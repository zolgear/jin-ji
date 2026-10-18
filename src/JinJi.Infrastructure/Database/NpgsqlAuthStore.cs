using JinJi.Application.Audit;
using JinJi.Application.Auth;
using JinJi.Domain;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 認証の表（0011）の読み書き。操作ごとに <see cref="AuthActor"/> の閲覧者（GUC）を設定したトランザクションで行う。
/// </summary>
/// <remarks>
/// <list type="table">
/// <item><term>jin-ji 自身・管理 CLI</term><description>jinji.viewer_kind = system、ロールなし（人事データは今日時点の行だけ見える）</description></item>
/// <item><term>人事管理者の人</term><description>user + hr_admin + 本人の person ID</description></item>
/// <item><term>緊急用アカウント</term><description>break_glass（人事データの表は 1 行も見えない）</description></item>
/// </list>
/// </remarks>
public sealed partial class NpgsqlAuthStore : IAuthStore, IDataProtectionKeyStore
{
    private const string AccountColumns =
        "id, kind, person_id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp, "
        + "lockout_end, access_failed_count, two_factor_enabled, authenticator_key_protected, disabled_at, totp_last_step";

    private const string BindingColumns = "id, provider, issuer, subject, person_id, basis, created_at";

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlAuthStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    // ---- ローカルアカウント --------------------------------------------------------------------------------------

    public async Task<LocalAccount?> FindAccountAsync(AuthActor actor, Guid accountId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        return await ReadAccountAsync(tx, $"SELECT {AccountColumns} FROM local_account WHERE id = $1", cancellationToken, accountId);
    }

    public async Task<LocalAccount?> FindAccountByNameAsync(AuthActor actor, string normalizedUserName, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        return await ReadAccountAsync(tx, $"SELECT {AccountColumns} FROM local_account WHERE normalized_user_name = $1", cancellationToken, normalizedUserName);
    }

    public async Task<LocalAccount?> FindAccountByPersonAsync(AuthActor actor, Guid personId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        return await ReadAccountAsync(tx, $"SELECT {AccountColumns} FROM local_account WHERE person_id = $1 AND disabled_at IS NULL", cancellationToken, personId);
    }

    public async Task<bool> CreateAccountAsync(LocalAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        await using var tx = await BeginAsync(AuthActor.Cli, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO local_account (id, kind, person_id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp,
                                       lockout_end, access_failed_count, two_factor_enabled, authenticator_key_protected, created_by_kind)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, 'cli')
            ON CONFLICT DO NOTHING
            """);
        AddAll(command,
        [
            P(account.Id), P(account.Kind == LocalAccountKind.BreakGlass ? "break_glass" : "person"), Nullable(account.PersonId, NpgsqlDbType.Uuid),
            P(account.UserName), P(account.NormalizedUserName), Nullable(account.PasswordHash, NpgsqlDbType.Text), P(account.SecurityStamp),
            P(account.ConcurrencyStamp), Nullable(account.LockoutEnd, NpgsqlDbType.TimestampTz), P(account.AccessFailedCount), P(account.TwoFactorEnabled),
            Nullable(account.AuthenticatorKeyProtected, NpgsqlDbType.Text),
        ]);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<bool> UpdateAccountAsync(AuthActor actor, LocalAccount account, string expectedConcurrencyStamp, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var next = Guid.NewGuid().ToString("D");
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE local_account
            SET password_hash = $2, security_stamp = $3, concurrency_stamp = $4, lockout_end = $5, access_failed_count = $6,
                two_factor_enabled = $7, authenticator_key_protected = $8, disabled_at = $9,
                -- 共有鍵を作り直したら、時間窓の記録も捨てる（新しい鍵の符号を、前の鍵の時間窓で拒否しない）
                totp_last_step = CASE WHEN authenticator_key_protected IS DISTINCT FROM $8 THEN NULL ELSE totp_last_step END
            WHERE id = $1 AND concurrency_stamp = $10
            """);
        AddAll(command,
        [
            P(account.Id), Nullable(account.PasswordHash, NpgsqlDbType.Text), P(account.SecurityStamp), P(next),
            Nullable(account.LockoutEnd, NpgsqlDbType.TimestampTz), P(account.AccessFailedCount), P(account.TwoFactorEnabled),
            Nullable(account.AuthenticatorKeyProtected, NpgsqlDbType.Text), Nullable(account.DisabledAt, NpgsqlDbType.TimestampTz), P(expectedConcurrencyStamp),
        ]);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        if (updated)
        {
            account.ConcurrencyStamp = next;
        }

        return updated;
    }

    public async Task<IReadOnlyList<LocalAccountSummary>> ListAccountsAsync(AuthActor actor, string? query, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var accounts = new List<LocalAccountSummary>();
        await using (var tx = await BeginAsync(actor, cancellationToken))
        {
            await using var command = tx.CreateCommand(
                """
                SELECT id, kind, person_id, user_name, password_hash IS NOT NULL, two_factor_enabled, lockout_end, disabled_at, created_at
                FROM local_account
                WHERE $1::text IS NULL OR strpos(normalized_user_name, $1) > 0
                ORDER BY normalized_user_name
                LIMIT $2
                """);
            AddAll(command, [Nullable(query is { Length: > 0 } q ? LocalAccountNames.Normalize(q) : null, NpgsqlDbType.Text), P(limit)]);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    accounts.Add(new LocalAccountSummary(
                        reader.GetGuid(0),
                        reader.GetString(1) == "break_glass" ? LocalAccountKind.BreakGlass : LocalAccountKind.Person,
                        reader.IsDBNull(2) ? null : reader.GetGuid(2),
                        reader.GetString(3),
                        reader.GetBoolean(4),
                        reader.GetBoolean(5),
                        0,
                        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                        reader.GetFieldValue<DateTimeOffset>(8)));
                }
            }

            await tx.CommitAsync(cancellationToken);
        }

        if (accounts.Count == 0)
        {
            return accounts;
        }

        // パスキーの表は jin-ji 自身だけが読める。一覧に見えたアカウントの数だけを数える
        var counts = new Dictionary<Guid, int>();
        await using (var tx = await BeginAsync(AuthActor.System, cancellationToken))
        {
            await using var command = tx.CreateCommand(
                "SELECT local_account_id, count(*) FROM local_account_passkey WHERE local_account_id = ANY ($1) GROUP BY local_account_id");
            command.Parameters.Add(P(accounts.Select(a => a.Id).ToArray()));
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    counts[reader.GetGuid(0)] = (int)reader.GetInt64(1);
                }
            }

            await tx.CommitAsync(cancellationToken);
        }

        return [.. accounts.Select(a => a with { PasskeyCount = counts.GetValueOrDefault(a.Id) })];
    }

    public async Task<bool> ConsumeTotpStepAsync(Guid accountId, long step, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand(
            "UPDATE local_account SET totp_last_step = $2 WHERE id = $1 AND (totp_last_step IS NULL OR totp_last_step < $2)");
        AddAll(command, [P(accountId), P(step)]);
        var consumed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return consumed;
    }

    public async Task<IReadOnlyList<StoredPasskey>> GetPasskeysAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            SELECT credential_id, public_key, created_at, sign_count, transports, is_user_verified, is_backup_eligible, is_backed_up,
                   attestation_object, client_data_json, name
            FROM local_account_passkey WHERE local_account_id = $1 ORDER BY created_at, credential_id
            """);
        command.Parameters.Add(P(accountId));
        var passkeys = new List<StoredPasskey>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                passkeys.Add(new StoredPasskey(
                    reader.GetFieldValue<byte[]>(0),
                    reader.GetFieldValue<byte[]>(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    (uint)reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetFieldValue<string[]>(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7),
                    reader.GetFieldValue<byte[]>(8),
                    reader.GetFieldValue<byte[]>(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return passkeys;
    }

    public async Task<Guid?> FindAccountByPasskeyAsync(byte[] credentialId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand("SELECT local_account_id FROM local_account_passkey WHERE credential_id = $1");
        command.Parameters.Add(P(credentialId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    public async Task AddOrUpdatePasskeyAsync(Guid accountId, StoredPasskey passkey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passkey);
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO local_account_passkey (credential_id, local_account_id, name, public_key, sign_count, transports, is_user_verified,
                                               is_backup_eligible, is_backed_up, attestation_object, client_data_json, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (credential_id) DO UPDATE
                SET name = EXCLUDED.name, sign_count = EXCLUDED.sign_count, is_user_verified = EXCLUDED.is_user_verified,
                    is_backup_eligible = EXCLUDED.is_backup_eligible, is_backed_up = EXCLUDED.is_backed_up
                WHERE local_account_passkey.local_account_id = EXCLUDED.local_account_id
            """);
        AddAll(command,
        [
            P(passkey.CredentialId), P(accountId), Nullable(passkey.Name, NpgsqlDbType.Text), P(passkey.PublicKey), P((long)passkey.SignCount),
            new NpgsqlParameter { Value = passkey.Transports?.ToArray() ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text },
            P(passkey.IsUserVerified), P(passkey.IsBackupEligible), P(passkey.IsBackedUp), P(passkey.AttestationObject), P(passkey.ClientDataJson),
            P(passkey.CreatedAt),
        ]);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("この資格情報 ID は別のアカウントのパスキーです。");
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task RemovePasskeyAsync(Guid accountId, byte[] credentialId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand("DELETE FROM local_account_passkey WHERE local_account_id = $1 AND credential_id = $2");
        AddAll(command, [P(accountId), P(credentialId)]);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task ReplaceRecoveryCodesAsync(Guid accountId, IReadOnlyCollection<string> codeHashes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codeHashes);
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using (var delete = tx.CreateCommand("DELETE FROM local_account_recovery_code WHERE local_account_id = $1"))
        {
            delete.Parameters.Add(P(accountId));
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var hash in codeHashes.Distinct(StringComparer.Ordinal))
        {
            await using var insert = tx.CreateCommand("INSERT INTO local_account_recovery_code (id, local_account_id, code_sha256) VALUES ($1, $2, $3)");
            AddAll(insert, [P(EntityId.New()), P(accountId), P(hash)]);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<bool> RedeemRecoveryCodeAsync(Guid accountId, string codeHash, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand(
            "UPDATE local_account_recovery_code SET redeemed_at = now() WHERE local_account_id = $1 AND code_sha256 = $2 AND redeemed_at IS NULL");
        AddAll(command, [P(accountId), P(codeHash)]);
        var redeemed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return redeemed;
    }

    public async Task<int> CountRecoveryCodesAsync(Guid accountId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand("SELECT count(*) FROM local_account_recovery_code WHERE local_account_id = $1 AND redeemed_at IS NULL");
        command.Parameters.Add(P(accountId));
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        await tx.CommitAsync(cancellationToken);
        return (int)count;
    }

    // ---- 人の状態・ロール --------------------------------------------------------------------------------------

    public async Task<PersonLoginState> GetPersonStateAsync(Guid personId, DateOnly today, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            SELECT EXISTS (
                       SELECT 1 FROM person_version v
                       WHERE v.person_id = $1 AND upper_inf(v.recorded_period) AND v.valid_period @> $2::date
                         AND v.hire_date <= $2 AND (v.termination_date IS NULL OR v.termination_date >= $2))
               AND NOT EXISTS (
                       SELECT 1 FROM account_suspension s
                       WHERE s.person_id = $1 AND upper_inf(s.recorded_period) AND s.valid_period @> $2::date)
            """);
        AddAll(command, [P(personId), P(today)]);
        var active = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        await tx.CommitAsync(cancellationToken);
        return active ? PersonLoginState.Active : PersonLoginState.Inactive;
    }

    public async Task<IReadOnlyList<string>> GetRolesAsync(Guid personId, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(AuthActor.System, cancellationToken);
        var roles = await ReadListAsync<string>(tx, "SELECT role FROM role_grant WHERE person_id = $1 AND revoked_at IS NULL ORDER BY role", cancellationToken, personId);
        await tx.CommitAsync(cancellationToken);
        return roles;
    }

    public async Task<bool> GrantRoleAsync(AuthActor actor, Guid grantId, Guid personId, string role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO role_grant (id, person_id, role, granted_by_kind, granted_by_person_id)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT DO NOTHING
            """);
        AddAll(command, [P(grantId), P(personId), P(role), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid)]);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<bool> RevokeRoleAsync(AuthActor actor, Guid personId, string role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE role_grant SET revoked_by_kind = $3, revoked_by_person_id = $4, revoked_at = now()
            WHERE person_id = $1 AND role = $2 AND revoked_at IS NULL
            """);
        AddAll(command, [P(personId), P(role), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid)]);
        var revoked = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return revoked;
    }

    // ---- 監査 ----------------------------------------------------------------------------------------------------

    public async Task WriteAuditAsync(AuthActor actor, AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(record);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(NpgsqlAuditSink.InsertSql);
        NpgsqlAuditSink.AddParameters(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // ---- Data Protection の鍵 ------------------------------------------------------------------------------------

    public IReadOnlyList<string> LoadAll()
    {
        using var tx = ViewerTransaction.BeginRaw(_dataSource, "system", string.Empty, null);
        using var command = tx.CreateCommand("SELECT xml FROM data_protection_key ORDER BY created_at, id");
        var keys = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                keys.Add(reader.GetString(0));
            }
        }

        tx.Transaction.Commit();
        return keys;
    }

    public void Store(string? friendlyName, string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var tx = ViewerTransaction.BeginRaw(_dataSource, "system", string.Empty, null);
        using var command = tx.CreateCommand("INSERT INTO data_protection_key (id, friendly_name, xml) VALUES ($1, $2, $3)");
        AddAll(command, [P(EntityId.New()), Nullable(friendlyName, NpgsqlDbType.Text), P(xml)]);
        command.ExecuteNonQuery();
        tx.Transaction.Commit();
    }

    // ---- 共通 ----------------------------------------------------------------------------------------------------

    private Task<ViewerTransaction> BeginAsync(AuthActor actor, CancellationToken cancellationToken) => actor.Kind switch
    {
        AuthActorKind.System or AuthActorKind.Cli => ViewerTransaction.BeginRawAsync(_dataSource, "system", string.Empty, null, cancellationToken),
        AuthActorKind.HrAdmin => ViewerTransaction.BeginRawAsync(_dataSource, "user", Policy.Roles.HrAdmin, actor.PersonId, cancellationToken),
        AuthActorKind.BreakGlass => ViewerTransaction.BeginRawAsync(_dataSource, "break_glass", string.Empty, null, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(actor)),
    };

    /// <summary>付与・取り消し・発行の主体のコード値（person / cli / break_glass）。</summary>
    private static string OperatorKind(AuthActor actor) => actor.Kind switch
    {
        AuthActorKind.Cli => "cli",
        AuthActorKind.HrAdmin => "person",
        AuthActorKind.BreakGlass => "break_glass",
        _ => throw new InvalidOperationException("管理の操作は管理 CLI・人事管理者の人・緊急用アカウントとして行います。"),
    };

    private static async Task<LocalAccount?> ReadAccountAsync(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        AddAll(command, [.. parameters.Select(P)]);
        LocalAccount? account = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                account = new LocalAccount
                {
                    Id = reader.GetGuid(0),
                    Kind = reader.GetString(1) == "break_glass" ? LocalAccountKind.BreakGlass : LocalAccountKind.Person,
                    PersonId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    UserName = reader.GetString(3),
                    NormalizedUserName = reader.GetString(4),
                    PasswordHash = reader.IsDBNull(5) ? null : reader.GetString(5),
                    SecurityStamp = reader.GetString(6),
                    ConcurrencyStamp = reader.GetString(7),
                    LockoutEnd = reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                    AccessFailedCount = reader.GetInt32(9),
                    TwoFactorEnabled = reader.GetBoolean(10),
                    AuthenticatorKeyProtected = reader.IsDBNull(11) ? null : reader.GetString(11),
                    DisabledAt = reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                    TotpLastStep = reader.IsDBNull(13) ? null : reader.GetInt64(13),
                };
            }
        }

        await tx.CommitAsync(cancellationToken);
        return account;
    }

    private static async Task<List<T>> ReadListAsync<T>(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        AddAll(command, [.. parameters.Select(P)]);
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetFieldValue<T>(0));
        }

        return rows;
    }

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static void AddAll(NpgsqlCommand command, NpgsqlParameter[] parameters) => command.Parameters.AddRange(parameters);

    private static NpgsqlParameter Nullable(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
