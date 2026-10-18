using JinJi.Application.Auth;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>認可の要求・コード・トークン・jti（jin-ji 自身の閲覧者だけ）。</summary>
public sealed partial class NpgsqlOAuthStore
{
    // ---- 認可の要求・コード --------------------------------------------------------------------------------------

    public async Task CreateAuthorizationRequestAsync(StoredAuthorizationRequest request, string handleHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO oauth_authorization_request (id, handle_sha256, client_ref, redirect_uri, resource, scopes, state, code_challenge, created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """);
        AddAll(command,
        [
            P(request.Id), P(handleHash), P(request.ClientRef), P(request.RedirectUri), P(OAuthCodes.ToCode(request.Resource)), Texts(request.Scopes),
            Nullable(request.State, NpgsqlDbType.Text), P(request.CodeChallenge), P(request.CreatedAt), P(request.ExpiresAt),
        ]);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<StoredAuthorizationRequest?> FindAuthorizationRequestAsync(string handleHash, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        var request = await ReadRequestAsync(tx, "WHERE handle_sha256 = $1", cancellationToken, handleHash);
        await tx.CommitAsync(cancellationToken);
        return request;
    }

    public async Task<StoredAuthorizationRequest?> DecideAuthorizationRequestAsync(
        string handleHash,
        Guid personId,
        bool approved,
        DateTimeOffset now,
        (Guid CodeId, string CodeHash, DateTimeOffset ExpiresAt, Guid ConsentId)? code,
        CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        StoredAuthorizationRequest? request;
        await using (var command = tx.CreateCommand(
            $"""
            UPDATE oauth_authorization_request SET decided_at = $2, decision = $3, person_id = $4
            WHERE handle_sha256 = $1 AND decided_at IS NULL AND expires_at > $2
            RETURNING {RequestColumns}
            """))
        {
            AddAll(command, [P(handleHash), P(now), P(approved ? "approved" : "denied"), P(personId)]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            request = await reader.ReadAsync(cancellationToken) ? ReadRequest(reader) : null;
        }

        if (request is null)
        {
            return null;
        }

        if (approved && code is { } issued)
        {
            await using (var consent = tx.CreateCommand(
                """
                INSERT INTO oauth_consent (id, request_id, client_ref, person_id, redirect_uri, resource, scopes, granted_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                """))
            {
                AddAll(consent,
                [
                    P(issued.ConsentId), P(request.Id), P(request.ClientRef), P(personId), P(request.RedirectUri), P(OAuthCodes.ToCode(request.Resource)),
                    Texts(request.Scopes), P(now),
                ]);
                await consent.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var insert = tx.CreateCommand(
                """
                INSERT INTO oauth_authorization_code (id, code_sha256, request_id, client_ref, person_id, redirect_uri, resource, scopes, code_challenge, created_at, expires_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                """);
            AddAll(insert,
            [
                P(issued.CodeId), P(issued.CodeHash), P(request.Id), P(request.ClientRef), P(personId), P(request.RedirectUri), P(OAuthCodes.ToCode(request.Resource)),
                Texts(request.Scopes), P(request.CodeChallenge), P(now), P(issued.ExpiresAt),
            ]);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return request;
    }

    public async Task<CodeExchangeResult> ExchangeAuthorizationCodeAsync(
        string codeHash,
        Func<StoredAuthorizationCode, NewTokens?> exchange,
        Func<StoredAuthorizationCode, Task> prepare,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(prepare);
        await using var tx = await SystemAsync(cancellationToken);
        StoredAuthorizationCode? code;
        await using (var command = tx.CreateCommand(
            """
            SELECT id, client_ref, person_id, redirect_uri, resource, scopes, code_challenge, expires_at, used_at IS NOT NULL, family_id
            FROM oauth_authorization_code WHERE code_sha256 = $1
            FOR UPDATE
            """))
        {
            command.Parameters.Add(P(codeHash));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            code = await reader.ReadAsync(cancellationToken)
                ? new StoredAuthorizationCode(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetString(3),
                    OAuthCodes.ParseResource(reader.GetString(4)),
                    reader.GetFieldValue<string[]>(5),
                    reader.GetString(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetBoolean(8),
                    reader.IsDBNull(9) ? null : reader.GetGuid(9))
                : null;
        }

        if (code is null)
        {
            return new CodeExchangeResult(CodeExchangeStatus.Invalid, null, null);
        }

        if (code.Used)
        {
            // 使用済みのコードの再利用: そのコードから発行した系列を終える（RFC 9700 §4.2.4）
            if (code.FamilyId is { } family)
            {
                await EndFamilyAsync(tx, family, TokenFamilyEndKind.CodeReuse, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return new CodeExchangeResult(CodeExchangeStatus.Reused, code, code.FamilyId);
        }

        await prepare(code);
        if (exchange(code) is not { FamilyId: { } familyId } tokens)
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return new CodeExchangeResult(CodeExchangeStatus.Invalid, code, null);
        }

        await using (var family = tx.CreateCommand(
            """
            INSERT INTO oauth_token_family (id, client_ref, person_id, resource, scopes, created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """))
        {
            AddAll(family,
            [
                P(familyId), P(tokens.ClientRef), P(tokens.PersonId!.Value), P(OAuthCodes.ToCode(tokens.Resource)), Texts(tokens.Scopes),
                P(tokens.IssuedAt), P(tokens.FamilyExpiresAt ?? tokens.AccessTokenExpiresAt),
            ]);
            await family.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertTokensAsync(tx, tokens, cancellationToken);
        await using (var used = tx.CreateCommand("UPDATE oauth_authorization_code SET used_at = $2, family_id = $3 WHERE id = $1"))
        {
            AddAll(used, [P(code.Id), P(tokens.IssuedAt), P(familyId)]);
            await used.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return new CodeExchangeResult(CodeExchangeStatus.Issued, code, familyId);
    }

    // ---- トークン ------------------------------------------------------------------------------------------------

    public async Task CreateAccessTokenAsync(NewTokens tokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        await using var tx = await SystemAsync(cancellationToken);
        await InsertTokensAsync(tx, tokens, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<RefreshResult> RotateRefreshTokenAsync(
        string tokenHash,
        Func<StoredRefreshToken, NewTokens?> rotate,
        Func<StoredRefreshToken, Task> prepare,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rotate);
        ArgumentNullException.ThrowIfNull(prepare);
        await using var tx = await SystemAsync(cancellationToken);
        StoredRefreshToken? token;
        await using (var command = tx.CreateCommand(
            """
            SELECT r.id, r.family_id, f.client_ref, f.person_id, f.resource, f.scopes, r.expires_at, f.expires_at, r.used_at IS NOT NULL, f.ended_at IS NOT NULL
            FROM oauth_refresh_token r
            JOIN oauth_token_family f ON f.id = r.family_id
            WHERE r.token_sha256 = $1
            FOR UPDATE OF r, f
            """))
        {
            command.Parameters.Add(P(tokenHash));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            token = await reader.ReadAsync(cancellationToken)
                ? new StoredRefreshToken(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    OAuthCodes.ParseResource(reader.GetString(4)),
                    reader.GetFieldValue<string[]>(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetBoolean(8),
                    reader.GetBoolean(9))
                : null;
        }

        if (token is null)
        {
            return new RefreshResult(RefreshStatus.Invalid, null);
        }

        if (token.Used)
        {
            if (token.FamilyEnded)
            {
                return new RefreshResult(RefreshStatus.Invalid, token);
            }

            // 使用済みのリフレッシュトークンの再利用: 系列ごと失効する（盗まれたトークンと正しいクライアントのどちらが先かは分からないため）
            await EndFamilyAsync(tx, token.FamilyId, TokenFamilyEndKind.ReuseDetected, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new RefreshResult(RefreshStatus.Reused, token);
        }

        await prepare(token);
        if (rotate(token) is not { } tokens)
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return new RefreshResult(RefreshStatus.Invalid, token);
        }

        await using (var used = tx.CreateCommand("UPDATE oauth_refresh_token SET used_at = $2 WHERE id = $1"))
        {
            AddAll(used, [P(token.Id), P(tokens.IssuedAt)]);
            await used.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertTokensAsync(tx, tokens, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new RefreshResult(RefreshStatus.Issued, token);
    }

    public async Task<AccessTokenRecord?> FindAccessTokenAsync(string tokenHash, DateOnly today, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        // 1 回の問い合わせで、トークン・系列・クライアント・人の状態（在籍・一時停止）・ロールを読む
        await using var command = tx.CreateCommand(
            $"""
            SELECT t.id, t.person_id, t.family_id, t.resource, t.scopes, t.expires_at,
                   t.revoked_at IS NOT NULL OR f.ended_at IS NOT NULL,
                   t.person_id IS NOT NULL
                   AND EXISTS (
                       SELECT 1 FROM person_version v
                       WHERE v.person_id = t.person_id AND upper_inf(v.recorded_period) AND v.valid_period @> $2::date
                         AND v.hire_date <= $2 AND (v.termination_date IS NULL OR v.termination_date >= $2))
                   AND NOT EXISTS (
                       SELECT 1 FROM account_suspension s
                       WHERE s.person_id = t.person_id AND upper_inf(s.recorded_period) AND s.valid_period @> $2::date),
                   coalesce((SELECT array_agg(g.role ORDER BY g.role) FROM role_grant g WHERE g.person_id = t.person_id AND g.revoked_at IS NULL), ARRAY[]::text[]),
                   {ClientColumns}
            FROM oauth_access_token t
            JOIN api_client c ON c.id = t.client_ref
            LEFT JOIN oauth_token_family f ON f.id = t.family_id
            WHERE t.token_sha256 = $1
            """);
        AddAll(command, [P(tokenHash), P(today)]);
        AccessTokenRecord? record = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                record = new AccessTokenRecord(
                    reader.GetGuid(0),
                    ReadClient(reader, 9),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    OAuthCodes.ParseResource(reader.GetString(3)),
                    reader.GetFieldValue<string[]>(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7),
                    reader.GetFieldValue<string[]>(8));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<bool> RevokeTokenAsync(Guid clientRef, string tokenHash, bool refreshToken, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        int count;
        if (refreshToken)
        {
            await using var command = tx.CreateCommand(
                """
                UPDATE oauth_token_family f SET ended_at = now(), end_kind = 'revoked'
                FROM oauth_refresh_token r
                WHERE r.token_sha256 = $1 AND f.id = r.family_id AND f.client_ref = $2 AND f.ended_at IS NULL
                """);
            AddAll(command, [P(tokenHash), P(clientRef)]);
            count = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var command = tx.CreateCommand(
                "UPDATE oauth_access_token SET revoked_at = now() WHERE token_sha256 = $1 AND client_ref = $2 AND revoked_at IS NULL");
            AddAll(command, [P(tokenHash), P(clientRef)]);
            count = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return count > 0;
    }

    public async Task<int> EndFamiliesAsync(Guid? clientRef, Guid? personId, Guid? familyId, TokenFamilyEndKind kind, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE oauth_token_family SET ended_at = now(), end_kind = $4
            WHERE ended_at IS NULL AND ($1::uuid IS NULL OR client_ref = $1) AND ($2::uuid IS NULL OR person_id = $2) AND ($3::uuid IS NULL OR id = $3)
            """);
        AddAll(command,
        [
            Nullable(clientRef, NpgsqlDbType.Uuid), Nullable(personId, NpgsqlDbType.Uuid), Nullable(familyId, NpgsqlDbType.Uuid), P(EndCode(kind)),
        ]);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<IReadOnlyList<Delegation>> ListDelegationsAsync(Guid personId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            """
            SELECT f.id, c.client_id, c.display_name, c.registration, f.resource, f.scopes, f.created_at,
                   (SELECT max(a.issued_at) FROM oauth_access_token a WHERE a.family_id = f.id), f.expires_at
            FROM oauth_token_family f
            JOIN api_client c ON c.id = f.client_ref
            WHERE f.person_id = $1 AND f.ended_at IS NULL AND f.expires_at > $2
              AND (EXISTS (SELECT 1 FROM oauth_refresh_token r WHERE r.family_id = f.id AND r.used_at IS NULL AND r.expires_at > $2)
                   OR EXISTS (SELECT 1 FROM oauth_access_token a WHERE a.family_id = f.id AND a.revoked_at IS NULL AND a.expires_at > $2))
            ORDER BY f.created_at DESC, f.id
            LIMIT 200
            """);
        AddAll(command, [P(personId), P(now)]);
        var rows = new List<Delegation>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new Delegation(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3) == "cimd" ? OAuthClientRegistration.Cimd : OAuthClientRegistration.Registered,
                    OAuthCodes.ParseResource(reader.GetString(4)),
                    reader.GetFieldValue<string[]>(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetFieldValue<DateTimeOffset>(8)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<int> RevokeClientAccessTokensAsync(Guid clientRef, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            "UPDATE oauth_access_token SET revoked_at = now() WHERE client_ref = $1 AND family_id IS NULL AND revoked_at IS NULL AND expires_at > now()");
        command.Parameters.Add(P(clientRef));
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return count;
    }

    public async Task<bool> RecordClientAssertionAsync(Guid clientRef, string jti, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using (var purge = tx.CreateCommand("DELETE FROM oauth_client_assertion WHERE client_ref = $1 AND expires_at < $2"))
        {
            AddAll(purge, [P(clientRef), P(now)]);
            await purge.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var command = tx.CreateCommand(
            "INSERT INTO oauth_client_assertion (client_ref, jti, expires_at) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING");
        AddAll(command, [P(clientRef), P(jti), P(expiresAt)]);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private const string RequestColumns = "id, client_ref, redirect_uri, resource, scopes, state, code_challenge, created_at, expires_at, decided_at IS NOT NULL";

    private static async Task<StoredAuthorizationRequest?> ReadRequestAsync(ViewerTransaction tx, string where, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = tx.CreateCommand($"SELECT {RequestColumns} FROM oauth_authorization_request {where}");
        AddAll(command, [.. parameters.Select(P)]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadRequest(reader) : null;
    }

    private static StoredAuthorizationRequest ReadRequest(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetString(2),
        OAuthCodes.ParseResource(reader.GetString(3)),
        reader.GetFieldValue<string[]>(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetString(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetFieldValue<DateTimeOffset>(8),
        reader.GetBoolean(9));

    private static async Task InsertTokensAsync(ViewerTransaction tx, NewTokens tokens, CancellationToken cancellationToken)
    {
        await using (var access = tx.CreateCommand(
            """
            INSERT INTO oauth_access_token (id, token_sha256, family_id, client_ref, person_id, resource, scopes, issued_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """))
        {
            AddAll(access,
            [
                P(Domain.EntityId.New()), P(tokens.AccessTokenHash), Nullable(tokens.FamilyId, NpgsqlDbType.Uuid), P(tokens.ClientRef),
                Nullable(tokens.PersonId, NpgsqlDbType.Uuid), P(OAuthCodes.ToCode(tokens.Resource)), Texts(tokens.Scopes), P(tokens.IssuedAt),
                P(tokens.AccessTokenExpiresAt),
            ]);
            await access.ExecuteNonQueryAsync(cancellationToken);
        }

        if (tokens.RefreshTokenHash is { } refresh)
        {
            await using var command = tx.CreateCommand(
                "INSERT INTO oauth_refresh_token (id, token_sha256, family_id, issued_at, expires_at) VALUES ($1, $2, $3, $4, $5)");
            AddAll(command, [P(Domain.EntityId.New()), P(refresh), P(tokens.FamilyId!.Value), P(tokens.IssuedAt), P(tokens.RefreshTokenExpiresAt!.Value)]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task EndFamilyAsync(ViewerTransaction tx, Guid familyId, TokenFamilyEndKind kind, CancellationToken cancellationToken)
    {
        await using var command = tx.CreateCommand("UPDATE oauth_token_family SET ended_at = now(), end_kind = $2 WHERE id = $1 AND ended_at IS NULL");
        AddAll(command, [P(familyId), P(EndCode(kind))]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string EndCode(TokenFamilyEndKind kind) => kind switch
    {
        TokenFamilyEndKind.Revoked => "revoked",
        TokenFamilyEndKind.ReuseDetected => "reuse_detected",
        TokenFamilyEndKind.CodeReuse => "code_reuse",
        TokenFamilyEndKind.Admin => "admin",
        TokenFamilyEndKind.User => "user",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
