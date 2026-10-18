using JinJi.Application.Auth;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// トークン発行の表（0012）の読み書き。コード・トークン・同意・jti とクライアントの参照は jin-ji 自身（system）、
/// クライアント・鍵・シークレット・許可ドメインの管理は <see cref="AuthActor"/>（管理 CLI = system、人事管理者の人 = user + hr_admin）の閲覧者で行う。
/// </summary>
public sealed partial class NpgsqlOAuthStore : IOAuthStore
{
    private const string ClientColumns =
        "c.id, c.client_id, c.registration, c.status, c.display_name, c.auth_method, c.grant_types, c.redirect_uris, c.resources, c.scopes, "
        + "c.fields, c.mcp_tools, c.rate_limit_per_client, c.metadata_fetched_at, c.metadata_expires_at, c.created_at, c.updated_at, "
        + "coalesce((SELECT l.lead_days FROM api_client_disclosure_lead l WHERE l.api_client_id = c.id AND l.status = 'approved'), 0)";

    private const int ClientColumnCount = 18;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlOAuthStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    // ---- クライアント --------------------------------------------------------------------------------------------

    public async Task<OAuthClient?> FindClientAsync(string clientId, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        var client = await ReadClientAsync(tx, $"SELECT {ClientColumns} FROM api_client c WHERE c.client_id = $1", cancellationToken, clientId);
        await tx.CommitAsync(cancellationToken);
        return client;
    }

    public async Task<OAuthClient?> FindClientByRefAsync(Guid clientRef, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        var client = await ReadClientAsync(tx, $"SELECT {ClientColumns} FROM api_client c WHERE c.id = $1", cancellationToken, clientRef);
        await tx.CommitAsync(cancellationToken);
        return client;
    }

    public async Task<IReadOnlyList<OAuthClient>> ListClientsAsync(AuthActor actor, OAuthClientStatus? status, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand($"SELECT {ClientColumns} FROM api_client c WHERE ($1::text IS NULL OR c.status = $1) ORDER BY c.created_at, c.id");
        command.Parameters.Add(Nullable(status is { } s ? OAuthCodes.ToCode(s) : null, NpgsqlDbType.Text));
        var clients = new List<OAuthClient>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                clients.Add(ReadClient(reader, 0));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return clients;
    }

    public async Task<bool> CreateClientAsync(AuthActor? actor, OAuthClient client, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        await using var tx = actor is null ? await SystemAsync(cancellationToken) : await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO api_client (id, client_id, registration, status, display_name, auth_method, grant_types, redirect_uris, resources, scopes,
                                    fields, mcp_tools, rate_limit_per_client, metadata_fetched_at, metadata_expires_at,
                                    created_by_kind, created_by_person_id, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $18)
            ON CONFLICT (client_id) DO NOTHING
            """);
        AddAll(command,
        [
            P(client.Id), P(client.ClientId), P(OAuthCodes.ToCode(client.Registration)), P(OAuthCodes.ToCode(client.Status)),
            Nullable(client.DisplayName, NpgsqlDbType.Text), P(OAuthCodes.ToCode(client.AuthMethod)), Texts(client.GrantTypes), Texts(client.RedirectUris),
            Texts(client.Resources.Select(OAuthCodes.ToCode)), Texts(client.Scopes), Texts(client.Fields), Texts(client.McpTools),
            Nullable(client.RateLimitPerClient, NpgsqlDbType.Integer), Nullable(client.MetadataFetchedAt, NpgsqlDbType.TimestampTz),
            Nullable(client.MetadataExpiresAt, NpgsqlDbType.TimestampTz), P(OperatorKind(actor)), Nullable(actor?.PersonId, NpgsqlDbType.Uuid),
            P(client.CreatedAt),
        ]);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<bool> UpdateClientAsync(AuthActor? actor, OAuthClient client, OAuthClientStatus? expectedStatus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        await using var tx = actor is null ? await SystemAsync(cancellationToken) : await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE api_client
            SET status = $2, display_name = $3, grant_types = $4, redirect_uris = $5, resources = $6, scopes = $7, fields = $8, mcp_tools = $9,
                rate_limit_per_client = $10, metadata_fetched_at = $11, metadata_expires_at = $12,
                updated_by_kind = $13, updated_by_person_id = $14, updated_at = now()
            WHERE id = $1 AND ($15::text IS NULL OR status = $15)
            """);
        AddAll(command,
        [
            P(client.Id), P(OAuthCodes.ToCode(client.Status)), Nullable(client.DisplayName, NpgsqlDbType.Text), Texts(client.GrantTypes),
            Texts(client.RedirectUris), Texts(client.Resources.Select(OAuthCodes.ToCode)), Texts(client.Scopes), Texts(client.Fields), Texts(client.McpTools),
            Nullable(client.RateLimitPerClient, NpgsqlDbType.Integer), Nullable(client.MetadataFetchedAt, NpgsqlDbType.TimestampTz),
            Nullable(client.MetadataExpiresAt, NpgsqlDbType.TimestampTz), P(OperatorKind(actor)), Nullable(actor?.PersonId, NpgsqlDbType.Uuid),
            Nullable(expectedStatus is { } s ? OAuthCodes.ToCode(s) : null, NpgsqlDbType.Text),
        ]);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<IReadOnlyList<OAuthClientKey>> GetClientKeysAsync(Guid clientRef, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            "SELECT id, key_id, public_key_pem, created_at FROM api_client_key WHERE client_ref = $1 AND revoked_at IS NULL ORDER BY created_at, id");
        command.Parameters.Add(P(clientRef));
        var keys = new List<OAuthClientKey>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                keys.Add(new OAuthClientKey(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return keys;
    }

    public async Task<bool> AddClientKeyAsync(AuthActor actor, Guid clientRef, OAuthClientKey key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO api_client_key (id, client_ref, key_id, public_key_pem, created_by_kind, created_by_person_id)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT DO NOTHING
            """);
        AddAll(command, [P(key.Id), P(clientRef), P(key.KeyId), P(key.PublicKeyPem), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid)]);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<bool> RevokeClientKeyAsync(AuthActor actor, Guid clientRef, string keyId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE api_client_key SET revoked_by_kind = $3, revoked_by_person_id = $4, revoked_at = now()
            WHERE client_ref = $1 AND key_id = $2 AND revoked_at IS NULL
            """);
        AddAll(command, [P(clientRef), P(keyId), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid)]);
        var revoked = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return revoked;
    }

    public async Task<IReadOnlyList<OAuthClientSecret>> GetActiveClientSecretsAsync(Guid clientRef, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            "SELECT id, secret_sha256, created_at, expires_at FROM api_client_secret WHERE client_ref = $1 AND revoked_at IS NULL AND expires_at > $2");
        AddAll(command, [P(clientRef), P(now)]);
        var secrets = new List<OAuthClientSecret>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                secrets.Add(new OAuthClientSecret(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return secrets;
    }

    public async Task AddClientSecretAsync(AuthActor actor, Guid clientRef, OAuthClientSecret secret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secret);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO api_client_secret (id, client_ref, secret_sha256, created_by_kind, created_by_person_id, created_at, expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """);
        AddAll(command,
        [
            P(secret.Id), P(clientRef), P(secret.SecretHash), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid), P(secret.CreatedAt), P(secret.ExpiresAt),
        ]);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<int> RevokeClientSecretsAsync(AuthActor actor, Guid clientRef, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            "UPDATE api_client_secret SET revoked_by_kind = $2, revoked_by_person_id = $3, revoked_at = now() WHERE client_ref = $1 AND revoked_at IS NULL");
        AddAll(command, [P(clientRef), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid)]);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return count;
    }

    // ---- CIMD の許可ドメイン -------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CimdAllowedDomain>> ListAllowedDomainsAsync(CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand("SELECT id, domain, created_at FROM cimd_allowed_domain WHERE revoked_at IS NULL ORDER BY domain");
        var domains = new List<CimdAllowedDomain>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                domains.Add(new CimdAllowedDomain(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return domains;
    }

    public async Task<bool> AddAllowedDomainAsync(AuthActor actor, Guid id, string domain, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO cimd_allowed_domain (id, domain, created_by_kind, created_by_person_id) VALUES ($1, $2, $3, $4)
            ON CONFLICT DO NOTHING
            """);
        AddAll(command, [P(id), P(domain), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid)]);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<bool> RemoveAllowedDomainAsync(AuthActor actor, string domain, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            "UPDATE cimd_allowed_domain SET revoked_by_kind = $2, revoked_by_person_id = $3, revoked_at = now() WHERE domain = $1 AND revoked_at IS NULL");
        AddAll(command, [P(domain), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid)]);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return removed;
    }

    // ---- 共通 ----------------------------------------------------------------------------------------------------

    private Task<ViewerTransaction> SystemAsync(CancellationToken cancellationToken) =>
        ViewerTransaction.BeginRawAsync(_dataSource, "system", string.Empty, null, cancellationToken);

    private Task<ViewerTransaction> BeginAsync(AuthActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Kind switch
        {
            AuthActorKind.System or AuthActorKind.Cli => SystemAsync(cancellationToken),
            AuthActorKind.HrAdmin => ViewerTransaction.BeginRawAsync(_dataSource, "user", Policy.Roles.HrAdmin, actor.PersonId, cancellationToken),
            _ => throw new InvalidOperationException("クライアントの管理は管理 CLI か人事管理者の人として行います。"),
        };
    }

    /// <summary>作成・変更・取り消しの主体のコード値（null は CIMD の取得 = jin-ji 自身）。</summary>
    private static string OperatorKind(AuthActor? actor) => actor?.Kind switch
    {
        null or AuthActorKind.System => "cimd",
        AuthActorKind.Cli => "cli",
        AuthActorKind.HrAdmin => "person",
        _ => throw new InvalidOperationException("クライアントの管理は管理 CLI か人事管理者の人として行います。"),
    };

    private static async Task<OAuthClient?> ReadClientAsync(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        AddAll(command, [.. parameters.Select(P)]);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadClient(reader, 0) : null;
    }

    /// <summary><see cref="ClientColumns"/> の並びの列を <paramref name="offset"/> から読む。</summary>
    private static OAuthClient ReadClient(NpgsqlDataReader reader, int offset) => new()
    {
        Id = reader.GetGuid(offset),
        ClientId = reader.GetString(offset + 1),
        Registration = OAuthCodes.ParseRegistration(reader.GetString(offset + 2)),
        Status = OAuthCodes.ParseStatus(reader.GetString(offset + 3)),
        DisplayName = reader.IsDBNull(offset + 4) ? null : reader.GetString(offset + 4),
        AuthMethod = OAuthCodes.ParseAuthMethod(reader.GetString(offset + 5)),
        GrantTypes = reader.GetFieldValue<string[]>(offset + 6),
        RedirectUris = reader.GetFieldValue<string[]>(offset + 7),
        Resources = [.. reader.GetFieldValue<string[]>(offset + 8).Select(OAuthCodes.ParseResource)],
        Scopes = reader.GetFieldValue<string[]>(offset + 9),
        Fields = reader.GetFieldValue<string[]>(offset + 10),
        McpTools = reader.GetFieldValue<string[]>(offset + 11),
        RateLimitPerClient = reader.IsDBNull(offset + 12) ? null : reader.GetInt32(offset + 12),
        MetadataFetchedAt = reader.IsDBNull(offset + 13) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 13),
        MetadataExpiresAt = reader.IsDBNull(offset + 14) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 14),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(offset + 15),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(offset + 16),
        DisclosureLeadDays = reader.GetInt32(offset + 17),
    };

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static NpgsqlParameter Texts(IEnumerable<string> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    private static void AddAll(NpgsqlCommand command, NpgsqlParameter[] parameters) => command.Parameters.AddRange(parameters);

    private static NpgsqlParameter Nullable(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
