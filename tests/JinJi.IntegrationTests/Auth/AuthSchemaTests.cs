using JinJi.Domain;
using JinJi.IntegrationTests.Support;
using Npgsql;

namespace JinJi.IntegrationTests.Auth;

/// <summary>
/// 認証の表（0011）の RLS・制約・アプリ用ロールの権限。アプリ用ロールで接続し、閲覧者（GUC）ごとに見える行・書ける行を確かめる。
/// </summary>
public sealed class AuthSchemaTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public static TheoryData<string> HrTables =>
    [
        "legal_entity", "org_unit", "person", "org_unit_version", "person_version", "personnel_action", "assignment",
        "account_suspension", "external_identity", "policy_rule", "audit_event", "org_level_scheme", "org_level",
    ];

    [Theory(DisplayName = "緊急用アカウントの閲覧者（break_glass）には人事データの表が 1 行も見えない")]
    [MemberData(nameof(HrTables))]
    public async Task BreakGlass_SeesNoHrData(string table)
    {
        Assert.Equal([0L], await CountAsync("break_glass", string.Empty, null, $"SELECT count(*) FROM {table}"));
        Assert.True((await CountAsync("system", "hr_admin", null, $"SELECT count(*) FROM {table}")).Single() > 0);
    }

    [Fact(DisplayName = "緊急用アカウントの閲覧者は人事データに書き込めず、監査ログは自分の操作（break_glass）だけ追記できる")]
    public async Task BreakGlass_CannotWriteHrData()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "break_glass", string.Empty, null, "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'BG', 'BG')", EntityId.New()));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "break_glass", string.Empty, null, "INSERT INTO audit_event (id, actor_type, channel, action, outcome) VALUES ($1, 'system', 'ui', 'x', 'success')", EntityId.New()));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await ExecuteAsync(
            "break_glass", string.Empty, null,
            "INSERT INTO audit_event (id, actor_type, actor_account_id, channel, action, outcome) VALUES ($1, 'break_glass', $1, 'ui', 'x', 'success')",
            EntityId.New());

        // 緊急用アカウントの操作は、どのアカウントかを必ず持つ
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, "INSERT INTO audit_event (id, actor_type, channel, action, outcome) VALUES ($1, 'break_glass', 'ui', 'x', 'success')", EntityId.New()));
        Assert.Equal("audit_event_break_glass_account", ex.ConstraintName);
    }

    [Theory(DisplayName = "セッション・Data Protection の鍵・パスキー・リカバリコードは jin-ji 自身（system）だけが読める")]
    [InlineData("ui_session")]
    [InlineData("data_protection_key")]
    [InlineData("local_account_passkey")]
    [InlineData("local_account_recovery_code")]
    public async Task Secrets_AreSystemOnly(string table)
    {
        await ExecuteAsync("system", string.Empty, null, "INSERT INTO data_protection_key (id, xml) VALUES ($1, '<key/>')", EntityId.New());
        await InsertSessionAsync();
        Assert.Equal([0L], await CountAsync("user", "hr_admin", F.Person("hr_admin"), $"SELECT count(*) FROM {table}"));
        Assert.Equal([0L], await CountAsync("break_glass", string.Empty, null, $"SELECT count(*) FROM {table}"));
        Assert.Equal([0L], await CountAsync("agent", "hr_admin", F.Person("hr_admin"), $"SELECT count(*) FROM {table}"));
        Assert.Equal([0L], await CountAsync(string.Empty, string.Empty, null, $"SELECT count(*) FROM {table}"));
    }

    [Fact(DisplayName = "紐付け: 1 つの主体には有効な紐付けが 1 つだけ。取り消せば作り直せ、取り消しは 1 回だけで中身は変えられない")]
    public async Task LoginBinding_OneActivePerSubject()
    {
        var subject = Guid.NewGuid().ToString("N");
        var insert = """
            INSERT INTO login_binding (id, provider, issuer, subject, person_id, basis, created_by_kind)
            VALUES ($1, 'google', 'https://idp.test', $2, $3, 'manual', 'cli')
            """;
        var first = EntityId.New();
        await ExecuteAsync("system", string.Empty, null, insert, first, subject, F.Person("u1_member"));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("system", string.Empty, null, insert, EntityId.New(), subject, F.Person("u2_member")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);

        await ExecuteAsync("system", string.Empty, null, "UPDATE login_binding SET revoked_by_kind = 'cli', revoked_at = now() WHERE id = $1", first);
        await ExecuteAsync("system", string.Empty, null, insert, EntityId.New(), subject, F.Person("u2_member"));

        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("system", string.Empty, null, "UPDATE login_binding SET revoked_at = now() WHERE id = $1", first));
        Assert.Equal("revocation_only", ex.ConstraintName);

        // 自動の紐付けは手動の根拠を持てない・人事管理者は本人としてしか記録できない
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null,
            "INSERT INTO login_binding (id, provider, issuer, subject, person_id, basis, created_by_kind) VALUES ($1, 'google', 'https://idp.test', 'x', $2, 'manual', 'login')",
            EntityId.New(), F.Person("u1_member")));
        Assert.Equal("login_binding_created_by_check", ex.ConstraintName);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "user", "hr_admin", F.Person("hr_admin"),
            "INSERT INTO login_binding (id, provider, issuer, subject, person_id, basis, created_by_kind, created_by_person_id) VALUES ($1, 'google', 'https://idp.test', 'y', $2, 'manual', 'person', $3)",
            EntityId.New(), F.Person("u1_member"), F.Person("hr_head")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        // AI エージェントは委任元が人事管理者でも紐付けを扱えない
        Assert.Equal([0L], await CountAsync("agent", "hr_admin", F.Person("hr_admin"), "SELECT count(*) FROM login_binding"));
    }

    [Fact(DisplayName = "ロールの付与: 自分には付与できない（職務分掌）。有効な付与は人 × ロールで 1 つ")]
    public async Task RoleGrant_NotSelf()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "user", "hr_admin", F.Person("hr_admin"),
            "INSERT INTO role_grant (id, person_id, role, granted_by_kind, granted_by_person_id) VALUES ($1, $2, 'hr_admin', 'person', $2)",
            EntityId.New(), F.Person("hr_admin")));
        Assert.Equal("role_grant_not_self", ex.ConstraintName);

        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null,
            "INSERT INTO role_grant (id, person_id, role, granted_by_kind) VALUES ($1, $2, 'hr_admin', 'cli')",
            EntityId.New(), F.Person("hr_head")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);

        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null,
            "INSERT INTO role_grant (id, person_id, role, granted_by_kind) VALUES ($1, $2, 'superuser', 'cli')",
            EntityId.New(), F.Person("u1_member")));
        Assert.Equal("role_grant_role_check", ex.ConstraintName);
    }

    [Fact(DisplayName = "ローカルアカウント: 緊急用アカウントはパスワードを持てず、ロックアウトは外せない")]
    public async Task LocalAccount_Constraints()
    {
        var insert = """
            INSERT INTO local_account (id, kind, person_id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp, lockout_enabled, created_by_kind)
            VALUES ($1, $2, $3, $4, upper($4), $5, 's', 'c', $6, 'cli')
            """;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, insert, EntityId.New(), "break_glass", DBNull.Value, "bg-pw", "hash", true));
        Assert.Equal("local_account_break_glass_passkey_only", ex.ConstraintName);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, insert, EntityId.New(), "person", F.Person("u3_member"), "no-lockout", "hash", false));
        Assert.Equal("local_account_lockout_enabled_check", ex.ConstraintName);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, insert, EntityId.New(), "person", DBNull.Value, "no-person", "hash", true));
        Assert.Equal("local_account_person_check", ex.ConstraintName);

        // 作成は jin-ji 自身（管理 CLI）だけ
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "user", "hr_admin", F.Person("hr_admin"), insert, EntityId.New(), "person", F.Person("u3_member"), "by-admin", "hash", true));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "セッション: 終えたセッションは生き返らず、最終利用日時と終了以外は変えられない")]
    public async Task UiSession_CannotBeRevived()
    {
        var id = await InsertSessionAsync();
        await ExecuteAsync("system", string.Empty, null, "UPDATE ui_session SET ended_at = now(), end_kind = 'logout' WHERE id = $1", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("system", string.Empty, null, "UPDATE ui_session SET ended_at = NULL, end_kind = NULL WHERE id = $1", id));
        Assert.Equal("ui_session_ended", ex.ConstraintName);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("system", string.Empty, null, "UPDATE ui_session SET expires_at = expires_at + interval '1 hour' WHERE id = $1", id));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "アプリ用ロールは監査ログ・Data Protection の鍵・紐付け・ロールの付与・ローカルアカウントを削除できない")]
    public async Task AppRole_CannotDelete()
    {
        foreach (var table in new[] { "data_protection_key", "login_binding", "role_grant", "local_account", "audit_event", "retention_run" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("system", string.Empty, null, $"DELETE FROM {table}"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }
    }

    [Fact(DisplayName = "消去の下限（0014）: セッション・リンク・未紐付けのログインは、jin-ji 自身が終わった行だけを消せる（人事管理者・緊急用アカウントは消せない）")]
    public async Task AppRole_DeletesOnlyFinishedRows()
    {
        var live = await InsertSessionAsync();
        var ended = await InsertSessionAsync();
        await ExecuteAsync("system", string.Empty, null, "UPDATE ui_session SET ended_at = now(), end_kind = 'logout' WHERE id = $1", ended);
        Assert.Equal(0, await ExecuteAsync("system", string.Empty, null, "DELETE FROM ui_session WHERE id = $1", live));
        Assert.Equal(0, await ExecuteAsync("user", "hr_admin", F.Person("hr_admin"), "DELETE FROM ui_session WHERE id = $1", ended));
        Assert.Equal(1, await ExecuteAsync("system", string.Empty, null, "DELETE FROM ui_session WHERE id = $1", ended));

        // 未紐付けのログイン: 最後の試行から 1 日を過ぎたものだけ、jin-ji 自身だけ
        var (fresh, old) = (EntityId.New(), EntityId.New());
        foreach (var (id, ago) in new[] { (fresh, "1 hour"), (old, "2 days") })
        {
            await ExecuteAsync(
                "system", string.Empty, null,
                $"INSERT INTO unbound_login (id, provider, issuer, subject, first_attempted_at, last_attempted_at) VALUES ($1, 'google', 'https://idp.test', $2, now() - interval '{ago}', now() - interval '{ago}')",
                id, Guid.NewGuid().ToString("N"));
        }

        Assert.Equal(0, await ExecuteAsync("system", string.Empty, null, "DELETE FROM unbound_login WHERE id = $1", fresh));
        Assert.Equal(0, await ExecuteAsync("user", "hr_admin", F.Person("hr_admin"), "DELETE FROM unbound_login WHERE id = $1", old));
        Assert.Equal(0, await ExecuteAsync("break_glass", string.Empty, null, "DELETE FROM unbound_login WHERE id = $1", old));
        Assert.Equal(1, await ExecuteAsync("system", string.Empty, null, "DELETE FROM unbound_login WHERE id = $1", old));
    }

    private async Task<Guid> InsertSessionAsync()
    {
        var id = EntityId.New();
        await ExecuteAsync(
            "system", string.Empty, null,
            """
            INSERT INTO ui_session (id, key_sha256, principal_kind, person_id, login_binding_id, provider, methods, expires_at)
            SELECT $1, encode(sha256(convert_to($1::text, 'UTF8')), 'hex'), 'person', b.person_id, b.id, 'google', '{fed}', now() + interval '1 hour'
            FROM login_binding b LIMIT 1
            """,
            id);
        if ((await CountAsync("system", string.Empty, null, $"SELECT count(*) FROM ui_session WHERE id = '{id}'")).Single() == 0)
        {
            var binding = EntityId.New();
            await ExecuteAsync(
                "system", string.Empty, null,
                "INSERT INTO login_binding (id, provider, issuer, subject, person_id, basis, created_by_kind) VALUES ($1, 'google', 'https://idp.test', $2, $3, 'manual', 'cli')",
                binding, Guid.NewGuid().ToString("N"), F.Person("u1_member"));
            return await InsertSessionAsync();
        }

        return id;
    }

    private async Task<List<long>> CountAsync(string kind, string roles, Guid? person, string sql)
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.SetSessionViewerAsync(connection, kind, roles, person, Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return [(long)(await command.ExecuteScalarAsync(Ct))!];
    }

    private async Task<int> ExecuteAsync(string kind, string roles, Guid? person, string sql, params object[] parameters)
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.SetSessionViewerAsync(connection, kind, roles, person, Ct);
        return await SampleData.ExecuteAsync(connection, sql, Ct, parameters);
    }
}
