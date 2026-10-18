using JinJi.Domain;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests;

/// <summary>
/// RLS（§5.4 の粗い防波堤）。アプリ用ロール（jinji_app）で接続し、実際に行が見える・見えないことを確かめる。
/// </summary>
public sealed class RowLevelSecurityTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public static TheoryData<string, string> TableViewerCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var table in new[]
                     {
                         "legal_entity", "org_unit", "person", "org_unit_version", "person_version", "personnel_action",
                         "assignment", "account_suspension", "policy_rule", "audit_event", "org_level_scheme", "org_level",
                     })
            {
                foreach (var viewer in new[] { "none", "invalid_kind", "user", "agent", "service", "hr" })
                {
                    data.Add(table, viewer);
                }
            }

            return data;
        }
    }

    [Theory(DisplayName = "テーブル × 閲覧者で見える行数")]
    [MemberData(nameof(TableViewerCases))]
    public async Task VisibleRowCount(string table, string viewer)
    {
        var count = await CountAsync(table, viewer);

        Assert.Equal(Expected(table, viewer), count);
    }

    [Fact(DisplayName = "内示（発令日前の発令）は hr_admin 以外には ID を指定しても見えない")]
    public async Task FutureActions_AreInvisibleToNonAdminsById()
    {
        var sql = "SELECT id FROM personnel_action WHERE id = ANY($1)";
        Guid[] ids = [F.FutureTransferActionId, F.FutureHireActionId];

        Assert.Empty(await db.QueryAsAppAsync<Guid>(F.User("sec1_head"), sql, Ct, ids));
        Assert.Empty(await db.QueryAsAppAsync<Guid>(F.User("s1_member"), sql, Ct, ids));
        Assert.Equal(ids.Order(), (await db.QueryAsAppAsync<Guid>(F.HrAdmin(), sql, Ct, ids)).Order());
    }

    [Fact(DisplayName = "氏名の過去の版は hr_admin 以外には見えず、現在の版は見える")]
    public async Task OldNameVersion_IsInvisibleToNonAdmins()
    {
        var sql = "SELECT family_name FROM person_version WHERE person_id = $1 ORDER BY lower(valid_period)";

        Assert.Equal(["新姓"], await db.QueryAsAppAsync<string>(F.User("u1_member"), sql, Ct, F.Person("u1_member")));
        Assert.Equal(["旧姓", "新姓"], await db.QueryAsAppAsync<string>(F.HrAdmin(), sql, Ct, F.Person("u1_member")));
    }

    [Fact(DisplayName = "閲覧者の設定はトランザクションの終了で消える（接続プールで次の利用者に引き継がれない）")]
    public async Task ViewerSetting_IsTransactionLocal()
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);

        Assert.Equal([(long)F.People.Count], await FixtureDatabase.QueryAsync<long>(connection, F.HrAdmin(), "SELECT count(*) FROM person", Ct));
        Assert.Equal([0L], await FixtureDatabase.QueryAsync<long>(connection, null, "SELECT count(*) FROM person", Ct));
    }

    [Fact(DisplayName = "FORCE ROW LEVEL SECURITY: テーブル所有者でも閲覧者が未設定なら何も見えない")]
    public async Task Owner_IsAlsoSubjectToRls()
    {
        await using var owner = await db.Database.OpenOwnerAsync(Ct);

        Assert.Equal([0L], await FixtureDatabase.QueryAsync<long>(owner, null, "SELECT count(*) FROM person", Ct));
        Assert.Equal([(long)F.Actions.Count(a => a.EffectiveDate > F.Today)], await FixtureDatabase.QueryAsync<long>(
            owner, F.HrAdmin(), "SELECT count(*) FROM personnel_action WHERE effective_date > jinji_business_date()", Ct));
    }

    [Fact(DisplayName = "全テーブルで RLS が有効かつ FORCE（schema_migration を除く）")]
    public async Task AllTables_HaveForcedRls()
    {
        await using var owner = await db.Database.OpenOwnerAsync(Ct);
        var missing = await FixtureDatabase.QueryAsync<string>(
            owner,
            null,
            """
            SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relname <> 'schema_migration'
              AND NOT (c.relrowsecurity AND c.relforcerowsecurity)
            """,
            Ct);

        Assert.Empty(missing);
    }

    [Theory(DisplayName = "閲覧者が未設定・一般の閲覧者は人事データを書き込めない")]
    [InlineData("none", "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'X', 'X')")]
    [InlineData("user", "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'X', 'X')")]
    [InlineData("service", "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'X', 'X')")]
    [InlineData("none", "INSERT INTO audit_event (id, actor_type, channel, action, outcome) VALUES ($1, 'system', 'cli', 'test', 'success')")]
    public async Task Writes_AreDenied(string viewer, string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(Viewer(viewer), sql, EntityId.New()));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "一般の閲覧者は監査ログを追記できるが参照できない")]
    public async Task AuditEvents_AreAppendOnlyForNonAdmins()
    {
        var id = EntityId.New();
        await ExecuteAsync(
            F.User("u1_member"),
            "INSERT INTO audit_event (id, actor_type, channel, action, outcome) VALUES ($1, 'user', 'ui', 'test', 'denied')",
            id);

        Assert.Empty(await db.QueryAsAppAsync<Guid>(F.User("u1_member"), "SELECT id FROM audit_event WHERE id = $1", Ct, id));
        Assert.Equal([id], await db.QueryAsAppAsync<Guid>(F.HrAdmin(), "SELECT id FROM audit_event WHERE id = $1", Ct, id));
    }

    [Fact(DisplayName = "ポリシーの変更系は hr_admin だけ。一般の閲覧者の起案は拒否、更新は 0 行")]
    public async Task PolicyRule_ChangesAreAdminOnly()
    {
        var insert = """
            INSERT INTO policy_rule (id, target_kind, person_id, fields, allow_scope, proposed_by_person_id)
            VALUES ($1, 'person', $3, '{work_email}', 'none', $2)
            """;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(F.User("u1_member"), insert, EntityId.New(), F.Person("u1_member"), F.Person("u2_member")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        var updated = await ExecuteAsync(
            F.User("u1_member"),
            "UPDATE policy_rule SET status = 'retired', retired_by_person_id = $1, retired_at = now() WHERE status = 'approved'",
            F.Person("u1_member"));
        Assert.Equal(0, updated);
        Assert.Equal(
            [(long)PolicyEvaluationTests.Policies.Rules.Count],
            await db.QueryAsAppAsync<long>(F.User("u1_member"), "SELECT count(*) FROM policy_rule WHERE status = 'approved'", Ct));
    }

    [Fact(DisplayName = "ポリシーの起案者・承認者は閲覧者本人としてしか記録できない")]
    public async Task PolicyRule_ActorsMustBeTheViewer()
    {
        var id = EntityId.New();
        var insertAsOther = """
            INSERT INTO policy_rule (id, target_kind, fields, allow_scope, proposed_by_person_id)
            VALUES ($1, 'all', '{job_type}', 'legal_entity', $2)
            """;
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(F.HrAdmin(), insertAsOther, id, F.Person("hr_head")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await ExecuteAsync(F.HrAdmin(), insertAsOther, id, F.Person("hr_admin"));
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            F.HrAdmin(),
            "UPDATE policy_rule SET status = 'approved', decided_by_person_id = $2, decided_at = now() WHERE id = $1",
            id,
            F.Person("hr_head")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "起案者＝承認者は DB の制約でも拒否される（アプリの検査をすり抜けた場合の防波堤）")]
    public async Task PolicyRule_ProposerCannotApprove_InDatabase()
    {
        var store = new NpgsqlPolicyRuleStore(db.AppDataSource, TimeProvider.System);
        var rule = PolicyEvaluationTests.Rule(PolicyTargetKind.All, null, [PersonFields.JobType], RelationScope.LegalEntity);
        await store.InsertProposalAsync(F.HrAdmin(), rule, Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => store.ApproveAsync(F.HrAdmin(), rule.Id, Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("policy_rule_segregation_of_duties", ex.ConstraintName);
    }

    [Fact(DisplayName = "閲覧者の person_id が UUID でなければ問い合わせ自体が失敗する（fail closed）")]
    public async Task InvalidPersonId_FailsClosed()
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.SetSessionViewerAsync(connection, "user", "hr_admin", null, Ct);
        await using (var set = new NpgsqlCommand("SELECT set_config('jinji.viewer_person_id', 'not-a-uuid', false)", connection))
        {
            await set.ExecuteNonQueryAsync(Ct);
        }

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            SampleData.ExecuteAsync(connection, "INSERT INTO policy_rule (id, target_kind, fields, allow_scope, proposed_by_person_id) VALUES ($1, 'all', '{job_type}', 'group', $1)", Ct, EntityId.New()));
        Assert.Equal(PostgresErrorCodes.InvalidTextRepresentation, ex.SqlState);
    }

    [Fact(DisplayName = "DB の業務日付とアプリの業務日付は一致する（日本時間）")]
    public async Task BusinessDate_MatchesApplication()
    {
        var dbToday = await db.QueryAsAppAsync<DateOnly>(F.User("u1_member"), "SELECT jinji_business_date()", Ct);

        Assert.Equal([BusinessDate.Today(TimeProvider.System)], dbToday);
    }

    private ViewerContext? Viewer(string spec) => spec switch
    {
        "none" => null,
        "user" => F.User("u1_member"),
        "agent" => ViewerContext.ForAgent("assistant", F.Person("u1_member"), [], Channel.Mcp, FieldLimit.Unrestricted),
        "service" => ViewerContext.ForService("attendance", FieldLimit.Unrestricted),
        "hr" => F.HrAdmin(),
        _ => throw new ArgumentException(spec, nameof(spec)),
    };

    private async Task<long> CountAsync(string table, string viewer)
    {
        // テーブル名はテストの表から来る固定値。他のテストが追記する行（起案中のポリシー・監査ログ）は数えない
        var filter = table switch
        {
            "policy_rule" => " WHERE status = 'approved'",
            "audit_event" => $" WHERE action = '{FixtureDatabase.FixtureAuditAction}'",
            _ => string.Empty,
        };
        var sql = $"SELECT count(*) FROM {table}{filter}";
        if (viewer == "invalid_kind")
        {
            // 未知の種別は、hr_admin のロールが付いていても「閲覧者なし」と同じ
            await using var connection = await db.Database.OpenAppAsync(Ct);
            await SampleData.SetSessionViewerAsync(connection, "admin", "hr_admin", F.Person("hr_admin"), Ct);
            return (await FixtureDatabase.QueryAsync<long>(connection, null, sql, Ct)).Single();
        }

        return (await db.QueryAsAppAsync<long>(Viewer(viewer), sql, Ct)).Single();
    }

    private long Expected(string table, string viewer)
    {
        if (viewer is "none" or "invalid_kind")
        {
            return 0;
        }

        var hr = viewer == "hr";
        return table switch
        {
            "legal_entity" => F.LegalEntities.Count,
            "org_unit" => F.Units.Count,
            "person" => F.People.Count,
            "org_unit_version" => F.Units.Count(u => hr || u.From <= F.Today),
            "person_version" => F.Versions.Count(v => hr || (v.From <= F.Today && (v.Until is null || F.Today < v.Until))),
            "personnel_action" => F.Actions.Count(a => hr || a.EffectiveDate <= F.Today),
            "assignment" => F.Assignments.Count(a => hr || a.From <= F.Today),
            "account_suspension" => F.Suspensions.Count(s => hr || s.From <= F.Today),
            "policy_rule" => PolicyEvaluationTests.Policies.Rules.Count,
            "audit_event" => hr ? 1 : 0,
            // 段の定義は判定に使うので、閲覧者が設定されていれば誰でも読める（既定 + 法人 C・D）
            "org_level_scheme" => F.LevelSchemes.Count,
            "org_level" => F.LevelSchemes.Sum(l => l.Levels.Count),
            _ => throw new ArgumentException(table, nameof(table)),
        };
    }

    private async Task<int> ExecuteAsync(ViewerContext? viewer, string sql, params object[] parameters)
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        if (viewer is not null)
        {
            await ViewerTransaction.ApplyAsync(connection, transaction, viewer, Ct);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var affected = await command.ExecuteNonQueryAsync(Ct);
        await transaction.CommitAsync(Ct);
        return affected;
    }
}
