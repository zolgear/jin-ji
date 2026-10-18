using JinJi.Domain;
using JinJi.Infrastructure.Migrations;
using JinJi.IntegrationTests.Support;
using Npgsql;

namespace JinJi.IntegrationTests;

/// <summary>
/// 0004 の段の定義の守り（状態遷移・職務分掌・範囲ごとに 1 つ・段の追加の制限）と、既存データの移行。
/// データ操作はアプリ用ロール（jinji_app）に閲覧者を設定して行う。
/// </summary>
public sealed class OrgLevelSchemaTests : IAsyncLifetime
{
    private const string InsertScheme = "INSERT INTO org_level_scheme (id, legal_entity_id, proposed_by_person_id) VALUES ($1, $2, $3)";
    private const string InsertLevel = "INSERT INTO org_level (id, scheme_id, level_key, name, rank) VALUES ($1, $2, $3, $4, $5)";
    private const string Approve = "UPDATE org_level_scheme SET status = 'approved', decided_by_person_id = $2, decided_at = now() WHERE id = $1";

    private TestDatabase _database = null!;
    private NpgsqlConnection _app = null!;
    private SampleData.Ids _ids = null!;
    private Guid _other;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(Ct);
        await _database.MigrateAsync(Ct);
        _app = await _database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(_app, Ct);
        _ids = await SampleData.InsertBasicsAsync(_app, Ct);
        _other = EntityId.New();
        await SampleData.ExecuteAsync(_app, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E0002')", Ct, _other, _ids.LegalEntity);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact(DisplayName = "初期の定義: マイグレーションが既定の定義（本部・部・課・係）を承認済みで作る")]
    public async Task DefaultScheme_IsSeeded()
    {
        var levels = await ReadAsync<string>(
            "SELECT l.level_key || '/' || l.name || '/' || l.rank FROM org_level l JOIN org_level_scheme s ON s.id = l.scheme_id WHERE s.legal_entity_id IS NULL AND s.status = 'approved' ORDER BY l.rank");

        Assert.Equal(OrgFixture.DefaultLevelScheme.Levels.Select(l => $"{l.Key}/{l.Name}/{l.Rank}"), levels);
    }

    [Fact(DisplayName = "段の定義: 起案者は承認できず、別人の承認で有効になり、範囲の旧い定義は置き換え済みになる")]
    public async Task Scheme_ApprovalReplacesActive()
    {
        var first = await ProposeAsync(_ids.Person, ("company", "会社", 1), ("team", "チーム", 2));

        var self = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(_ids.Person, Approve, first, _ids.Person));
        Assert.Equal("org_level_scheme_segregation_of_duties", self.ConstraintName);

        await ExecuteAsAsync(_other, Approve, first, _other);

        var second = await ProposeAsync(_ids.Person, ("company", "会社", 1), ("department", "部", 2), ("team", "チーム", 3));

        // 置き換えずに 2 つ目を承認すると、コミット時に範囲ごとに 1 つの排他制約で失敗する
        var twoActive = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(_other, Approve, second, _other));
        Assert.Equal(PostgresErrorCodes.ExclusionViolation, twoActive.SqlState);
        Assert.Equal("org_level_scheme_one_active", twoActive.ConstraintName);

        // 承認と置き換えを 1 トランザクションで行えば通る
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", _other, Ct);
        await using (var tx = await _app.BeginTransactionAsync(Ct))
        {
            await SampleData.ExecuteAsync(_app, Approve, Ct, second, _other);
            await SampleData.ExecuteAsync(
                _app, "UPDATE org_level_scheme SET status = 'superseded', superseded_by_scheme_id = $2, superseded_at = now() WHERE id = $1", Ct, first, second);
            await tx.CommitAsync(Ct);
        }

        Assert.Equal(["superseded", "approved"], await ReadAsync<string>("SELECT status FROM org_level_scheme WHERE id = ANY($1) ORDER BY proposed_at, id", new[] { first, second }));
    }

    [Fact(DisplayName = "段の定義: 置き換え先は同じ範囲の承認済みの定義に限り、置き換え済み・却下からは戻せない")]
    public async Task Scheme_SupersedeIsGuarded()
    {
        var active = await ProposeAsync(_ids.Person, ("company", "会社", 1));
        await ExecuteAsAsync(_other, Approve, active, _other);
        var pending = await ProposeAsync(_ids.Person, ("company", "会社", 1), ("team", "チーム", 2));

        var toPending = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(
            _other, "UPDATE org_level_scheme SET status = 'superseded', superseded_by_scheme_id = $2, superseded_at = now() WHERE id = $1", active, pending));
        Assert.Equal("org_level_scheme_transition", toPending.ConstraintName);

        // 既定の定義（別の範囲）でも置き換えられない
        var defaultScheme = (await ReadAsync<Guid>("SELECT id FROM org_level_scheme WHERE legal_entity_id IS NULL")).Single();
        var otherScope = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(
            _other, "UPDATE org_level_scheme SET status = 'superseded', superseded_by_scheme_id = $2, superseded_at = now() WHERE id = $1", active, defaultScheme));
        Assert.Equal("org_level_scheme_transition", otherScope.ConstraintName);

        await ExecuteAsAsync(_other, "UPDATE org_level_scheme SET status = 'rejected', decided_by_person_id = $2, decided_at = now() WHERE id = $1", pending, _other);
        var back = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(_other, Approve, pending, _other));
        Assert.Equal("org_level_scheme_transition", back.ConstraintName);
    }

    [Fact(DisplayName = "段は起案中の定義に起案者だけが追加でき、追加後は変更できない")]
    public async Task Levels_AreAddedOnlyToOwnProposal()
    {
        var scheme = await ProposeAsync(_ids.Person, ("company", "会社", 1));

        var byOther = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(_other, InsertLevel, EntityId.New(), scheme, "team", "チーム", 2));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, byOther.SqlState);

        await ExecuteAsAsync(_other, Approve, scheme, _other);
        var afterApproval = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(_ids.Person, InsertLevel, EntityId.New(), scheme, "team", "チーム", 2));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, afterApproval.SqlState);

        var update = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(_ids.Person, "UPDATE org_level SET rank = 9 WHERE scheme_id = $1", scheme));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, update.SqlState);
    }

    [Theory(DisplayName = "段のキー・順位・表示名の形式と重複")]
    [InlineData("Team", "チーム", 1, "org_level_level_key_format")]
    [InlineData("1team", "チーム", 1, "org_level_level_key_format")]
    [InlineData("team", " ", 1, "org_level_name_not_blank")]
    [InlineData("team", "チーム", 0, "org_level_rank_positive")]
    [InlineData("company", "チーム", 2, "org_level_scheme_key")]
    [InlineData("team", "チーム", 1, "org_level_scheme_rank")]
    public async Task Levels_AreValidated(string key, string name, int rank, string constraint)
    {
        var scheme = await ProposeAsync(_ids.Person, ("company", "会社", 1));

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(_ids.Person, InsertLevel, EntityId.New(), scheme, key, name, rank));

        Assert.Equal(constraint, ex.ConstraintName);
    }

    [Fact(DisplayName = "アプリから初期の定義（seeded）や承認済みの定義を直接作れない")]
    public async Task SeededOrApprovedInsert_IsDenied()
    {
        var seeded = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(
            _ids.Person, "INSERT INTO org_level_scheme (id, legal_entity_id, status, seeded) VALUES ($1, $2, 'approved', true)", EntityId.New(), _ids.LegalEntity));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, seeded.SqlState);

        var approved = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsAsync(
            _ids.Person,
            "INSERT INTO org_level_scheme (id, legal_entity_id, status, proposed_by_person_id, decided_by_person_id, decided_at) VALUES ($1, $2, 'approved', $3, $4, now())",
            EntityId.New(),
            _ids.LegalEntity,
            _ids.Person,
            _other));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, approved.SqlState);
    }

    [Fact(DisplayName = "組織の版の段のキーは形式だけを DB で検査する（定義にあるかはアプリで検査する）")]
    public async Task OrgVersionLevelKey_FormatIsChecked()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app,
            """
            INSERT INTO org_unit_version (id, org_unit_id, legal_entity_id, code, name, level_key, path, valid_period)
            VALUES ($1, $2, $3, 'X', '組織', 'Bad Key', $4::ltree, '[2026-04-01,)'::daterange)
            """,
            Ct,
            EntityId.New(),
            _ids.OrgUnitA,
            _ids.LegalEntity,
            _ids.OrgUnitA.ToString("N")));

        Assert.Equal("org_unit_version_level_key_format", ex.ConstraintName);
    }

    private async Task<Guid> ProposeAsync(Guid proposer, params (string Key, string Name, int Rank)[] levels)
    {
        var id = EntityId.New();
        await ExecuteAsAsync(proposer, InsertScheme, id, _ids.LegalEntity, proposer);
        foreach (var (key, name, rank) in levels)
        {
            await ExecuteAsAsync(proposer, InsertLevel, EntityId.New(), id, key, name, rank);
        }

        return id;
    }

    private async Task<int> ExecuteAsAsync(Guid person, string sql, params object[] parameters)
    {
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", person, Ct);
        return await SampleData.ExecuteAsync(_app, sql, Ct, parameters);
    }

    private async Task<List<T>> ReadAsync<T>(string sql, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, _app);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add(reader.GetFieldValue<T>(0));
        }

        return rows;
    }
}

/// <summary>0004 の既存データの移行: unit_type を既定の定義の段に、same_team 等を same_level に写す。</summary>
public sealed class OrgLevelMigrationTests : IAsyncLifetime
{
    private TestDatabase _database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await TestDatabase.CreateAsync(Ct);

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact(DisplayName = "移行: 組織の種別は既定の段（group / other は段なし）に、課内・部内・本部内は same_level に写る")]
    public async Task ExistingData_IsMigrated()
    {
        var all = EmbeddedMigrations.Load();
        var runner = new MigrationRunner(_database.OwnerConnectionString);
        await runner.ApplyAsync([.. all.Where(m => string.CompareOrdinal(m.Version, "0004") < 0)], Ct);

        await using var app = await _database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(app, Ct);
        var ids = await SampleData.InsertBasicsAsync(app, Ct);
        var approver = EntityId.New();
        await SampleData.ExecuteAsync(app, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E0002')", Ct, approver, ids.LegalEntity);

        var types = new[] { "division", "department", "section", "unit", "group", "other" };
        var units = new Dictionary<string, Guid>();
        foreach (var type in types)
        {
            var unit = EntityId.New();
            units[type] = unit;
            await SampleData.ExecuteAsync(app, "INSERT INTO org_unit (id, legal_entity_id) VALUES ($1, $2)", Ct, unit, ids.LegalEntity);
            await SampleData.ExecuteAsync(
                app,
                """
                INSERT INTO org_unit_version (id, org_unit_id, legal_entity_id, code, name, unit_type, path, valid_period)
                VALUES ($1, $2, $3, $4, '組織', $4, $5::ltree, '[2026-04-01,)'::daterange)
                """,
                Ct,
                EntityId.New(),
                unit,
                ids.LegalEntity,
                type,
                unit.ToString("N"));
        }

        var rules = new Dictionary<string, Guid>();
        foreach (var scope in new[] { "same_team", "same_department", "same_division", "legal_entity" })
        {
            var rule = EntityId.New();
            rules[scope] = rule;
            await SampleData.SetSessionViewerAsync(app, "user", "hr_admin", ids.Person, Ct);
            await SampleData.ExecuteAsync(
                app,
                "INSERT INTO policy_rule (id, target_kind, fields, allow_scope, proposed_by_person_id) VALUES ($1, 'all', '{work_email}', $2, $3)",
                Ct,
                rule,
                scope,
                ids.Person);
            await SampleData.SetSessionViewerAsync(app, "user", "hr_admin", approver, Ct);
            await SampleData.ExecuteAsync(app, "UPDATE policy_rule SET status = 'approved', decided_by_person_id = $2, decided_at = now() WHERE id = $1", Ct, rule, approver);
        }

        await runner.ApplyAsync(all, Ct);

        await SampleData.ActAsHrAdminAsync(app, Ct);
        foreach (var type in types)
        {
            var expected = type is "group" or "other" ? null : type;
            Assert.Equal(expected, await ScalarAsync<string?>(app, "SELECT level_key FROM org_unit_version WHERE org_unit_id = $1", units[type]));
        }

        Assert.Equal("same_level/section", await ScalarAsync<string?>(app, "SELECT allow_scope || '/' || allow_level_key FROM policy_rule WHERE id = $1", rules["same_team"]));
        Assert.Equal("same_level/department", await ScalarAsync<string?>(app, "SELECT allow_scope || '/' || allow_level_key FROM policy_rule WHERE id = $1", rules["same_department"]));
        Assert.Equal("same_level/division", await ScalarAsync<string?>(app, "SELECT allow_scope || '/' || allow_level_key FROM policy_rule WHERE id = $1", rules["same_division"]));
        Assert.Equal("legal_entity", await ScalarAsync<string?>(app, "SELECT allow_scope || coalesce('/' || allow_level_key, '') FROM policy_rule WHERE id = $1", rules["legal_entity"]));

        // 移行後もトリガーと FORCE ROW LEVEL SECURITY は元どおり効く
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            app, "UPDATE org_unit_version SET recorded_period = tstzrange(lower(recorded_period), NULL, '(]') WHERE org_unit_id = $1", Ct, units["section"]));
        Assert.Equal("recorded_period_close_only", ex.ConstraintName);
        await using var owner = await _database.OpenOwnerAsync(Ct);
        Assert.Equal(0L, await ScalarAsync<long>(owner, "SELECT count(*) FROM policy_rule"));
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var value = await command.ExecuteScalarAsync(Ct);
        return value is DBNull or null ? default! : (T)value;
    }
}
