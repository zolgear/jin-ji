using JinJi.Domain;
using JinJi.IntegrationTests.Support;
using Npgsql;

namespace JinJi.IntegrationTests;

/// <summary>
/// 0002・0003 で入れたスキーマの守り: 記録期間の保護、組織コードの時点での一意性、ポリシーの状態遷移と職務分掌（廃止を含む）、
/// 個人単位の例外への本人の関与の禁止。
/// データ操作はアプリ用ロール（jinji_app）に hr_admin の閲覧者を設定して行う。
/// </summary>
public sealed class SchemaGuardTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private NpgsqlConnection _app = null!;
    private SampleData.Ids _ids = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(Ct);
        await _database.MigrateAsync(Ct);
        _app = await _database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(_app, Ct);
        _ids = await SampleData.InsertBasicsAsync(_app, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact(DisplayName = "記録期間: 開いている期間を閉じる更新はできる")]
    public async Task RecordedPeriod_CanBeClosed()
    {
        var id = await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "[2026-04-01,)", Ct);

        Assert.Equal(1, await CloseAsync("assignment", id));
    }

    [Fact(DisplayName = "記録期間: 閉じた期間を開き直せない")]
    public async Task RecordedPeriod_CannotBeReopened()
    {
        var id = await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "[2026-04-01,)", Ct);
        await CloseAsync("assignment", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE assignment SET recorded_period = tstzrange(lower(recorded_period), NULL) WHERE id = $1", Ct, id));

        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, ex.SqlState);
        Assert.Equal("recorded_period_closed", ex.ConstraintName);
    }

    [Fact(DisplayName = "記録期間: 閉じた期間の終わりを動かせない")]
    public async Task RecordedPeriod_ClosedEndCannotMove()
    {
        var id = await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "[2026-04-01,)", Ct);
        await CloseAsync("assignment", id);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => CloseAsync("assignment", id));

        Assert.Equal("recorded_period_closed", ex.ConstraintName);
    }

    [Theory(DisplayName = "記録期間: 開始を動かす・閉じずに書き換える更新はできない")]
    [InlineData("tstzrange(lower(recorded_period) - interval '1 day', clock_timestamp())")]
    [InlineData("tstzrange(lower(recorded_period) + interval '1 microsecond', NULL)")]
    [InlineData("tstzrange(lower(recorded_period), NULL, '(]')")]
    public async Task RecordedPeriod_OnlyCloseIsAllowed(string newValue)
    {
        var id = await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "[2026-04-01,)", Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, $"UPDATE assignment SET recorded_period = {newValue} WHERE id = $1", Ct, id));

        Assert.Equal(PostgresErrorCodes.IntegrityConstraintViolation, ex.SqlState);
        Assert.Equal("recorded_period_close_only", ex.ConstraintName);
    }

    [Fact(DisplayName = "記録期間: 所有者でも記録期間以外の列は書き換えられない")]
    public async Task RecordedPeriod_OwnerCannotRewriteOtherColumns()
    {
        var id = await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "[2026-04-01,)", Ct);
        await using var owner = await _database.OpenOwnerAsync(Ct);
        await SampleData.ActAsHrAdminAsync(owner, Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            owner,
            "UPDATE assignment SET org_unit_id = $1, recorded_period = tstzrange(lower(recorded_period), clock_timestamp()) WHERE id = $2",
            Ct,
            _ids.OrgUnitB,
            id));

        Assert.Equal("recorded_period_close_only", ex.ConstraintName);
    }

    [Theory(DisplayName = "記録期間の保護は二つの時間軸のすべてのテーブルに付いている")]
    [InlineData("org_unit_version")]
    [InlineData("person_version")]
    [InlineData("personnel_action")]
    [InlineData("assignment")]
    [InlineData("account_suspension")]
    public async Task RecordedPeriodGuard_IsOnAllBitemporalTables(string table)
    {
        await using var owner = await _database.OpenOwnerAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_trigger WHERE tgrelid = $1::regclass AND tgfoid = 'jinji_guard_recorded_period'::regproc",
            owner);
        command.Parameters.Add(new NpgsqlParameter { Value = table });

        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact(DisplayName = "組織コード: 同一法人・現在記録中・有効期間が重なる版で同じコードは使えない")]
    public async Task OrgCode_MustBeUniqueAtAPointInTime()
    {
        await InsertOrgVersionAsync(_ids.OrgUnitA, "SALES", "[2026-04-01,)");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertOrgVersionAsync(_ids.OrgUnitB, "SALES", "[2026-10-01,2027-04-01)"));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, ex.SqlState);
        Assert.Equal("org_unit_version_code_no_overlap", ex.ConstraintName);
    }

    [Fact(DisplayName = "組織コード: 期間が重ならなければ・別の法人なら・旧い記録なら同じコードを使える")]
    public async Task OrgCode_MayBeReusedOutsideConflicts()
    {
        var first = await InsertOrgVersionAsync(_ids.OrgUnitA, "SALES", "[2026-04-01,2026-10-01)");
        await InsertOrgVersionAsync(_ids.OrgUnitB, "SALES", "[2026-10-01,)");

        // 別の法人
        var otherEntity = EntityId.New();
        var otherUnit = EntityId.New();
        await SampleData.ExecuteAsync(_app, "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'C002', '別法人')", Ct, otherEntity);
        await SampleData.ExecuteAsync(_app, "INSERT INTO org_unit (id, legal_entity_id) VALUES ($1, $2)", Ct, otherUnit, otherEntity);
        await InsertOrgVersionAsync(otherUnit, "SALES", "[2026-04-01,)", otherEntity);

        // 記録期間を閉じた旧い版とは衝突しない（訂正で同じ期間に同じコードを付け直せる）
        await CloseAsync("org_unit_version", first);
        await InsertOrgVersionAsync(_ids.OrgUnitA, "SALES", "[2026-04-01,2026-10-01)");
    }

    [Fact(DisplayName = "組織の版の法人は不変 ID の法人と一致しなければならない")]
    public async Task OrgVersion_LegalEntityMustMatchOrgUnit()
    {
        var otherEntity = EntityId.New();
        await SampleData.ExecuteAsync(_app, "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'C002', '別法人')", Ct, otherEntity);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertOrgVersionAsync(_ids.OrgUnitA, "X", "[2026-04-01,)", otherEntity));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        Assert.Equal("org_unit_version_org_unit_legal_entity_fkey", ex.ConstraintName);
    }

    [Fact(DisplayName = "上位組織は同じ法人の組織に限る")]
    public async Task OrgVersion_ParentMustBeInSameLegalEntity()
    {
        var otherEntity = EntityId.New();
        var otherUnit = EntityId.New();
        await SampleData.ExecuteAsync(_app, "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'C002', '別法人')", Ct, otherEntity);
        await SampleData.ExecuteAsync(_app, "INSERT INTO org_unit (id, legal_entity_id) VALUES ($1, $2)", Ct, otherUnit, otherEntity);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertOrgVersionAsync(otherUnit, "X", "[2026-04-01,)", otherEntity, parent: _ids.OrgUnitA));

        Assert.Equal("org_unit_version_parent_legal_entity_fkey", ex.ConstraintName);
    }

    [Fact(DisplayName = "ポリシー: 承認済みの規則は起案中に戻せず、中身も変えられない")]
    public async Task PolicyRule_TransitionsAreGuarded()
    {
        var (proposer, approver) = (_ids.Person, EntityId.New());
        await SampleData.ExecuteAsync(_app, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E0002')", Ct, approver, _ids.LegalEntity);
        var rule = EntityId.New();
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", proposer, Ct);
        await SampleData.ExecuteAsync(
            _app,
            "INSERT INTO policy_rule (id, target_kind, fields, allow_scope, proposed_by_person_id) VALUES ($1, 'all', '{job_type}', 'legal_entity', $2)",
            Ct,
            rule,
            proposer);
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", approver, Ct);
        await SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET status = 'approved', decided_by_person_id = $2, decided_at = now() WHERE id = $1", Ct, rule, approver);

        var back = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET status = 'rejected' WHERE id = $1", Ct, rule));
        Assert.Equal("policy_rule_transition", back.ConstraintName);

        // アプリ用ロールは中身の列を更新する権限を持たない
        var content = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET allow_scope = 'group' WHERE id = $1", Ct, rule));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, content.SqlState);

        // 所有者でもトリガーが止める
        await using var owner = await _database.OpenOwnerAsync(Ct);
        await SampleData.SetSessionViewerAsync(owner, "system", "hr_admin", approver, Ct);
        var byOwner = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            owner, "UPDATE policy_rule SET allow_scope = 'group' WHERE id = $1", Ct, rule));
        Assert.Equal("policy_rule_immutable", byOwner.ConstraintName);

        // 廃止の起案なしに廃止することはできない（0003）
        var direct = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET status = 'retired', retired_by_person_id = $2, retired_at = now() WHERE id = $1", Ct, rule, approver));
        Assert.Equal("policy_rule_transition", direct.ConstraintName);
    }

    [Fact(DisplayName = "ポリシーの廃止: 起案 → 別人の承認で廃止できる。廃止の起案者は承認できない")]
    public async Task PolicyRule_RetirementRequiresAnotherApprover()
    {
        var (proposer, approver) = await ApprovedRuleAsync();
        var rule = _rule;

        // approver が廃止を起案（承認済みのまま）
        await SampleData.ExecuteAsync(
            _app,
            "UPDATE policy_rule SET retirement_proposed_by_person_id = $2, retirement_proposed_at = now() WHERE id = $1",
            Ct,
            rule,
            approver);
        Assert.Equal(["approved"], await ReadAsync<string>("SELECT status FROM policy_rule WHERE id = $1", rule));

        // 廃止の起案者自身による廃止は CHECK 制約で拒否
        var self = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET status = 'retired', retired_by_person_id = $2, retired_at = now() WHERE id = $1", Ct, rule, approver));
        Assert.Equal(PostgresErrorCodes.CheckViolation, self.SqlState);
        Assert.Equal("policy_rule_retirement_segregation_of_duties", self.ConstraintName);

        // 廃止の承認と同時に廃止の起案者を書き換えて、職務分掌をすり抜けることはできない
        var rewrite = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app,
            "UPDATE policy_rule SET status = 'retired', retired_by_person_id = $2, retired_at = now(), retirement_proposed_by_person_id = $3 WHERE id = $1",
            Ct,
            rule,
            approver,
            proposer));
        Assert.Equal("policy_rule_transition", rewrite.ConstraintName);

        // 別の人事管理者（起案者）が承認すれば廃止される。廃止後は何も変えられない
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", proposer, Ct);
        await SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET status = 'retired', retired_by_person_id = $2, retired_at = now() WHERE id = $1", Ct, rule, proposer);
        Assert.Equal(["retired"], await ReadAsync<string>("SELECT status FROM policy_rule WHERE id = $1", rule));

        var after = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET retired_at = now() WHERE id = $1", Ct, rule));
        Assert.Equal("policy_rule_transition", after.ConstraintName);
    }

    [Fact(DisplayName = "ポリシーの廃止: 廃止の起案は閲覧者本人としてしか記録できず、承認と同時には記録できない")]
    public async Task PolicyRule_RetirementProposalIsGuarded()
    {
        var (proposer, approver) = await ApprovedRuleAsync();

        // 他人の名前で廃止を起案できない（RLS）
        var other = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET retirement_proposed_by_person_id = $2, retirement_proposed_at = now() WHERE id = $1", Ct, _rule, proposer));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, other.SqlState);

        // 起案中の規則で「他人の名前での承認 + 自分の廃止の起案」を一度に書けない（トリガー）
        var second = EntityId.New();
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", proposer, Ct);
        await SampleData.ExecuteAsync(
            _app,
            "INSERT INTO policy_rule (id, target_kind, fields, allow_scope, proposed_by_person_id) VALUES ($1, 'all', '{work_email}', 'legal_entity', $2)",
            Ct,
            second,
            proposer);
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", approver, Ct);
        var combined = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app,
            """
            UPDATE policy_rule SET status = 'approved', decided_by_person_id = $2, decided_at = now(),
                                   retirement_proposed_by_person_id = $3, retirement_proposed_at = now()
            WHERE id = $1
            """,
            Ct,
            second,
            proposer,
            approver));
        Assert.Equal("policy_rule_transition", combined.ConstraintName);

        // 却下した規則は廃止の起案ができない
        await SampleData.ExecuteAsync(_app, "UPDATE policy_rule SET status = 'rejected', decided_by_person_id = $2, decided_at = now() WHERE id = $1", Ct, second, approver);
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET retirement_proposed_by_person_id = $2, retirement_proposed_at = now() WHERE id = $1", Ct, second, approver));
        Assert.Equal("policy_rule_transition", rejected.ConstraintName);
    }

    [Fact(DisplayName = "個人単位の例外: 対象者本人は起案者・承認者・廃止の起案者・廃止の承認者になれない（CHECK 制約）")]
    public async Task PersonException_SubjectCannotTakePart()
    {
        var (admin, other) = (_ids.Person, EntityId.New());
        var third = EntityId.New();
        await SampleData.ExecuteAsync(_app, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E0002'), ($3, $2, 'E0003')", Ct, other, _ids.LegalEntity, third);
        const string Insert = "INSERT INTO policy_rule (id, target_kind, person_id, fields, allow_scope, proposed_by_person_id) VALUES ($1, 'person', $2, '{work_email}', 'none', $3)";

        // 自分を対象にした例外の起案
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", admin, Ct);
        var ownProposal = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(_app, Insert, Ct, EntityId.New(), admin, admin));
        Assert.Equal("policy_rule_person_exception_not_self", ownProposal.ConstraintName);

        // 他人が起案した自分の例外の承認
        var rule = EntityId.New();
        await SampleData.ExecuteAsync(_app, Insert, Ct, rule, other, admin);
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", other, Ct);
        var ownApproval = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET status = 'approved', decided_by_person_id = $2, decided_at = now() WHERE id = $1", Ct, rule, other));
        Assert.Equal("policy_rule_person_exception_not_self", ownApproval.ConstraintName);

        // 第三者の承認は通る。対象者本人による廃止の起案は拒否
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", third, Ct);
        await SampleData.ExecuteAsync(_app, "UPDATE policy_rule SET status = 'approved', decided_by_person_id = $2, decided_at = now() WHERE id = $1", Ct, rule, third);
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", other, Ct);
        var ownRetirement = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET retirement_proposed_by_person_id = $2, retirement_proposed_at = now() WHERE id = $1", Ct, rule, other));
        Assert.Equal("policy_rule_person_exception_not_self", ownRetirement.ConstraintName);
    }

    private Guid _rule;

    /// <summary>_ids.Person が起案し、別の人が承認した規則を作る。閲覧者は承認者のまま返す。</summary>
    private async Task<(Guid Proposer, Guid Approver)> ApprovedRuleAsync()
    {
        var (proposer, approver) = (_ids.Person, EntityId.New());
        await SampleData.ExecuteAsync(_app, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E0002')", Ct, approver, _ids.LegalEntity);
        _rule = EntityId.New();
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", proposer, Ct);
        await SampleData.ExecuteAsync(
            _app,
            "INSERT INTO policy_rule (id, target_kind, fields, allow_scope, proposed_by_person_id) VALUES ($1, 'all', '{job_type}', 'legal_entity', $2)",
            Ct,
            _rule,
            proposer);
        await SampleData.SetSessionViewerAsync(_app, "user", "hr_admin", approver, Ct);
        await SampleData.ExecuteAsync(
            _app, "UPDATE policy_rule SET status = 'approved', decided_by_person_id = $2, decided_at = now() WHERE id = $1", Ct, _rule, approver);
        return (proposer, approver);
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

    private async Task<int> CloseAsync(string table, Guid id) =>
        await SampleData.ExecuteAsync(
            _app,
            $"UPDATE {table} SET recorded_period = tstzrange(lower(recorded_period), clock_timestamp()) WHERE id = $1",
            Ct,
            id);

    private async Task<Guid> InsertOrgVersionAsync(Guid orgUnit, string code, string validPeriod, Guid? legalEntity = null, Guid? parent = null)
    {
        var id = EntityId.New();
        await SampleData.ExecuteAsync(
            _app,
            """
            INSERT INTO org_unit_version (id, org_unit_id, legal_entity_id, code, name, level_key, parent_org_unit_id, path, valid_period)
            VALUES ($1, $2, $3, $4, '組織', 'department', $5, $6::ltree, $7::daterange)
            """,
            Ct,
            id,
            orgUnit,
            legalEntity ?? _ids.LegalEntity,
            code,
            (object?)parent ?? DBNull.Value,
            orgUnit.ToString("N"),
            validPeriod);
        return id;
    }
}
