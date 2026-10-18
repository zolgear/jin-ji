using JinJi.Application.Auth;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>紐付け・未紐付けのログイン・リンク・セッション。</summary>
public sealed partial class NpgsqlAuthStore
{
    // ---- 紐付け --------------------------------------------------------------------------------------------------

    public async Task<LoginBinding?> FindActiveBindingAsync(AuthActor actor, string issuer, string subject, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        return await ReadBindingAsync(tx, $"SELECT {BindingColumns} FROM login_binding WHERE issuer = $1 AND subject = $2 AND revoked_at IS NULL", cancellationToken, issuer, subject);
    }

    public async Task<LoginBinding?> FindBindingAsync(AuthActor actor, Guid bindingId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        return await ReadBindingAsync(tx, $"SELECT {BindingColumns} FROM login_binding WHERE id = $1", cancellationToken, bindingId);
    }

    public async Task<IReadOnlyList<Guid>> FindPersonsByExternalIdAsync(string sourceSystem, string externalId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        var rows = await ReadListAsync<Guid>(
            tx,
            "SELECT DISTINCT person_id FROM external_identity WHERE source_system = $1 AND lower(external_id) = lower($2) LIMIT 2",
            cancellationToken,
            sourceSystem,
            externalId);
        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<IReadOnlyList<Guid>> FindPersonsByEmployeeNumberAsync(string employeeNumber, string? legalEntityCode, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        var rows = legalEntityCode is null
            ? await ReadListAsync<Guid>(tx, "SELECT id FROM person WHERE employee_number = $1 LIMIT 2", cancellationToken, employeeNumber)
            : await ReadListAsync<Guid>(
                tx,
                "SELECT p.id FROM person p JOIN legal_entity e ON e.id = p.legal_entity_id WHERE p.employee_number = $1 AND e.code = $2 LIMIT 2",
                cancellationToken,
                employeeNumber,
                legalEntityCode);
        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<IReadOnlyList<Guid>> FindPersonsByWorkEmailAsync(string email, int limit, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        var rows = await ReadListAsync<Guid>(
            tx,
            """
            SELECT DISTINCT v.person_id FROM person_version v
            WHERE upper_inf(v.recorded_period) AND v.valid_period @> jinji_business_date() AND lower(v.work_email) = lower($1)
            ORDER BY v.person_id
            LIMIT $2
            """,
            cancellationToken,
            email,
            limit);
        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<bool> PersonExistsAsync(Guid personId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand("SELECT EXISTS (SELECT 1 FROM person WHERE id = $1)");
        command.Parameters.Add(P(personId));
        var exists = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        await tx.CommitAsync(cancellationToken);
        return exists;
    }

    public async Task<LoginBinding?> CreateBindingAsync(AuthActor actor, LoginBinding binding, Guid? resolvesUnboundId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(binding);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            $"""
            INSERT INTO login_binding (id, provider, issuer, subject, person_id, basis, created_by_kind, created_by_person_id, created_by_account_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT DO NOTHING
            RETURNING {BindingColumns}
            """);
        AddAll(command,
        [
            P(binding.Id), P(binding.Provider), P(binding.Issuer), P(binding.Subject), P(binding.PersonId), P(BasisCode(binding.Basis)),
            P(actor.Code), Nullable(actor.Kind == AuthActorKind.HrAdmin ? actor.PersonId : null, NpgsqlDbType.Uuid),
            Nullable(actor.Kind == AuthActorKind.BreakGlass ? actor.AccountId : null, NpgsqlDbType.Uuid),
        ]);
        LoginBinding? created = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                created = ReadBinding(reader);
            }
        }

        if (created is null)
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return null;
        }

        // 同じ主体の未紐付けのログインを解決済みにする（手動なら指定した行。主体が違う行を指定したら解決しない）
        await using (var resolve = tx.CreateCommand(
                         """
                         UPDATE unbound_login SET resolved_at = now(), resolved_binding_id = $1
                         WHERE issuer = $2 AND subject = $3 AND resolved_at IS NULL AND ($4::uuid IS NULL OR id = $4)
                         """))
        {
            AddAll(resolve, [P(created.Id), P(created.Issuer), P(created.Subject), Nullable(resolvesUnboundId, NpgsqlDbType.Uuid)]);
            await resolve.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return created;
    }

    public async Task<bool> RevokeBindingAsync(AuthActor actor, Guid bindingId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        bool revoked;
        await using (var tx = await BeginAsync(actor, cancellationToken))
        {
            await using var command = tx.CreateCommand(
                """
                UPDATE login_binding SET revoked_by_kind = $2, revoked_by_person_id = $3, revoked_by_account_id = $4, revoked_at = now()
                WHERE id = $1 AND revoked_at IS NULL
                """);
            AddAll(command,
            [
                P(bindingId), P(OperatorKind(actor)), Nullable(actor.Kind == AuthActorKind.HrAdmin ? actor.PersonId : null, NpgsqlDbType.Uuid),
                Nullable(actor.Kind == AuthActorKind.BreakGlass ? actor.AccountId : null, NpgsqlDbType.Uuid),
            ]);
            revoked = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            await tx.CommitAsync(cancellationToken);
        }

        if (revoked)
        {
            // セッションは jin-ji 自身だけが扱う（検証でも紐付けの取り消しを見るので、ここで終えなくても次の要求で無効になる）
            await using var sessions = await BeginAsync(AuthActor.System, cancellationToken);
            await using var end = sessions.CreateCommand("UPDATE ui_session SET ended_at = now(), end_kind = 'binding_revoked' WHERE login_binding_id = $1 AND ended_at IS NULL");
            end.Parameters.Add(P(bindingId));
            await end.ExecuteNonQueryAsync(cancellationToken);
            await sessions.CommitAsync(cancellationToken);
        }

        return revoked;
    }

    public async Task<Guid> RecordUnboundLoginAsync(UnboundLoginAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO unbound_login (id, provider, issuer, subject, tenant_id, claimed_employee_number, claimed_email, claimed_display_name, candidate_person_ids)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (issuer, subject) DO UPDATE
                SET provider = EXCLUDED.provider, tenant_id = EXCLUDED.tenant_id, claimed_employee_number = EXCLUDED.claimed_employee_number,
                    claimed_email = EXCLUDED.claimed_email, claimed_display_name = EXCLUDED.claimed_display_name,
                    candidate_person_ids = EXCLUDED.candidate_person_ids, attempt_count = unbound_login.attempt_count + 1,
                    last_attempted_at = greatest(now(), unbound_login.first_attempted_at), resolved_at = NULL, resolved_binding_id = NULL
            RETURNING id
            """);
        AddAll(command,
        [
            P(Domain.EntityId.New()), P(attempt.Provider), P(attempt.Issuer), P(attempt.Subject), Nullable(attempt.TenantId, NpgsqlDbType.Text),
            Nullable(attempt.EmployeeNumber, NpgsqlDbType.Text), Nullable(attempt.Email, NpgsqlDbType.Text), Nullable(attempt.DisplayName, NpgsqlDbType.Text),
            new NpgsqlParameter { Value = attempt.CandidatePersonIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid },
        ]);
        var id = (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
        await tx.CommitAsync(cancellationToken);
        return id;
    }

    public async Task<IReadOnlyList<UnboundLogin>> ListUnboundLoginsAsync(AuthActor actor, bool includeResolved, int limit, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        var rows = await ReadUnboundAsync(
            tx,
            $"SELECT {UnboundColumns} FROM unbound_login WHERE ($1 OR resolved_at IS NULL) ORDER BY last_attempted_at DESC, id LIMIT $2",
            cancellationToken,
            includeResolved,
            limit);
        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<UnboundLogin?> FindUnboundLoginAsync(AuthActor actor, Guid unboundId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        var rows = await ReadUnboundAsync(tx, $"SELECT {UnboundColumns} FROM unbound_login WHERE id = $1", cancellationToken, unboundId);
        await tx.CommitAsync(cancellationToken);
        return rows.SingleOrDefault();
    }

    // ---- リンク --------------------------------------------------------------------------------------------------

    public async Task IssueResetLinkAsync(
        AuthActor actor,
        Guid linkId,
        Guid accountId,
        ResetLinkPurpose purpose,
        string tokenHash,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using (var revoke = tx.CreateCommand(
                         "UPDATE account_reset_link SET revoked_at = now() WHERE local_account_id = $1 AND purpose = $2 AND used_at IS NULL AND revoked_at IS NULL"))
        {
            AddAll(revoke, [P(accountId), P(PurposeCode(purpose))]);
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = tx.CreateCommand(
                         """
                         INSERT INTO account_reset_link (id, local_account_id, purpose, token_sha256, issued_by_kind, issued_by_person_id, issued_by_account_id, issued_at, expires_at)
                         VALUES ($1, $2, $3, $4, $5, $6, $7, now(), $8)
                         """))
        {
            AddAll(insert,
            [
                P(linkId), P(accountId), P(PurposeCode(purpose)), P(tokenHash), P(OperatorKind(actor)),
                Nullable(actor.Kind == AuthActorKind.HrAdmin ? actor.PersonId : null, NpgsqlDbType.Uuid),
                Nullable(actor.Kind == AuthActorKind.BreakGlass ? actor.AccountId : null, NpgsqlDbType.Uuid),
                P(expiresAt),
            ]);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<ResetLinkTarget?> FindUsableResetLinkAsync(string tokenHash, ResetLinkPurpose purpose, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        var link = await ReadLinkAsync(
            tx,
            """
            SELECT id, local_account_id, purpose, expires_at FROM account_reset_link
            WHERE token_sha256 = $1 AND purpose = $2 AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now()
            """,
            cancellationToken,
            tokenHash,
            PurposeCode(purpose));
        await tx.CommitAsync(cancellationToken);
        return link;
    }

    public async Task<ResetLinkTarget?> ConsumeResetLinkAsync(string tokenHash, ResetLinkPurpose purpose, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        var link = await ReadLinkAsync(
            tx,
            """
            UPDATE account_reset_link SET used_at = now()
            WHERE token_sha256 = $1 AND purpose = $2 AND used_at IS NULL AND revoked_at IS NULL AND expires_at > now()
            RETURNING id, local_account_id, purpose, expires_at
            """,
            cancellationToken,
            tokenHash,
            PurposeCode(purpose));
        await tx.CommitAsync(cancellationToken);
        return link;
    }

    // ---- セッション ----------------------------------------------------------------------------------------------

    public async Task CreateSessionAsync(Guid sessionId, string keyHash, NewSession session, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO ui_session (id, key_sha256, principal_kind, person_id, local_account_id, login_binding_id, provider, methods, created_at, last_seen_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $9, $10)
            """);
        AddAll(command,
        [
            P(sessionId), P(keyHash), P(session.Kind == SessionPrincipalKind.BreakGlass ? "break_glass" : "person"),
            Nullable(session.PersonId, NpgsqlDbType.Uuid), Nullable(session.AccountId, NpgsqlDbType.Uuid), Nullable(session.BindingId, NpgsqlDbType.Uuid),
            P(session.Provider), new NpgsqlParameter { Value = session.Methods.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
            P(now), P(expiresAt),
        ]);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<StoredSession?> FindSessionAsync(string keyHash, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            SELECT s.id, s.principal_kind, s.person_id, s.local_account_id, s.login_binding_id, s.provider, s.methods,
                   s.created_at, s.last_seen_at, s.expires_at, s.ended_at IS NOT NULL,
                   s.local_account_id IS NOT NULL AND (a.id IS NULL OR a.disabled_at IS NOT NULL),
                   s.login_binding_id IS NOT NULL AND (b.id IS NULL OR b.revoked_at IS NOT NULL)
            FROM ui_session s
            LEFT JOIN local_account a ON a.id = s.local_account_id
            LEFT JOIN login_binding b ON b.id = s.login_binding_id
            WHERE s.key_sha256 = $1
            """);
        command.Parameters.Add(P(keyHash));
        StoredSession? session = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                session = new StoredSession(
                    reader.GetGuid(0),
                    reader.GetString(1) == "break_glass" ? SessionPrincipalKind.BreakGlass : SessionPrincipalKind.Person,
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    reader.IsDBNull(4) ? null : reader.GetGuid(4),
                    reader.GetString(5),
                    reader.GetFieldValue<string[]>(6),
                    reader.GetFieldValue<DateTimeOffset>(7),
                    reader.GetFieldValue<DateTimeOffset>(8),
                    reader.GetFieldValue<DateTimeOffset>(9),
                    reader.GetBoolean(10),
                    reader.GetBoolean(11),
                    reader.GetBoolean(12));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return session;
    }

    public async Task TouchSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand("UPDATE ui_session SET last_seen_at = $2 WHERE id = $1 AND ended_at IS NULL AND last_seen_at < $2");
        AddAll(command, [P(sessionId), P(now)]);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task EndSessionAsync(Guid sessionId, SessionEndKind kind, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand("UPDATE ui_session SET ended_at = now(), end_kind = $2 WHERE id = $1 AND ended_at IS NULL");
        AddAll(command, [P(sessionId), P(EndCode(kind))]);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<int> EndAccountSessionsAsync(Guid accountId, SessionEndKind kind, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand("UPDATE ui_session SET ended_at = now(), end_kind = $2 WHERE local_account_id = $1 AND ended_at IS NULL");
        AddAll(command, [P(accountId), P(EndCode(kind))]);
        var ended = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return ended;
    }

    // ---- 読み出し・コード値 --------------------------------------------------------------------------------------

    private const string UnboundColumns =
        "id, provider, issuer, subject, tenant_id, claimed_employee_number, claimed_email, claimed_display_name, candidate_person_ids, "
        + "attempt_count, first_attempted_at, last_attempted_at, resolved_at, resolved_binding_id";

    private static async Task<LoginBinding?> ReadBindingAsync(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        AddAll(command, [.. parameters.Select(P)]);
        LoginBinding? binding = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                binding = ReadBinding(reader);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return binding;
    }

    private static LoginBinding ReadBinding(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetGuid(4),
        reader.GetString(5) switch
        {
            "entra_object_id" => BindingBasis.EntraObjectId,
            "employee_number" => BindingBasis.EmployeeNumber,
            _ => BindingBasis.Manual,
        },
        reader.GetFieldValue<DateTimeOffset>(6));

    private static async Task<List<UnboundLogin>> ReadUnboundAsync(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        AddAll(command, [.. parameters.Select(P)]);
        var rows = new List<UnboundLogin>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new UnboundLogin(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetFieldValue<Guid[]>(8),
                reader.GetInt32(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                reader.IsDBNull(13) ? null : reader.GetGuid(13)));
        }

        return rows;
    }

    private static async Task<ResetLinkTarget?> ReadLinkAsync(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        AddAll(command, [.. parameters.Select(P)]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ResetLinkTarget(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2) == "passkey" ? ResetLinkPurpose.Passkey : ResetLinkPurpose.Password,
                reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    private static string BasisCode(BindingBasis basis) => basis switch
    {
        BindingBasis.EntraObjectId => "entra_object_id",
        BindingBasis.EmployeeNumber => "employee_number",
        BindingBasis.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(basis)),
    };

    private static string PurposeCode(ResetLinkPurpose purpose) => purpose switch
    {
        ResetLinkPurpose.Password => "password",
        ResetLinkPurpose.Passkey => "passkey",
        _ => throw new ArgumentOutOfRangeException(nameof(purpose)),
    };

    private static string EndCode(SessionEndKind kind) => kind switch
    {
        SessionEndKind.Logout => "logout",
        SessionEndKind.IdleTimeout => "idle_timeout",
        SessionEndKind.Expired => "expired",
        SessionEndKind.PersonInactive => "person_inactive",
        SessionEndKind.AccountDisabled => "account_disabled",
        SessionEndKind.CredentialChanged => "credential_changed",
        SessionEndKind.BindingRevoked => "binding_revoked",
        SessionEndKind.Replaced => "replaced",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
