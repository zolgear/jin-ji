using JinJi.Application.FieldManagement;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Ingest;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.FieldManagement;

/// <summary>
/// 現場・IT 部門の直接登録の土台（0020・ADR-0013 決定 2・3）: 範囲の判定（アプリと RLS が同じ線を引く）、担当の指名と取り消し、
/// 管理元の起案と承認（職務分掌）を実 PostgreSQL で確かめる。テストごとに一時データベースを作る。
/// </summary>
/// <remarks>
/// 組織（<see cref="IngestDatabase"/>）: 第一本部 D1（長 head）> 第一営業部 DP1（長 head）> 営業一課 S1（長なし）・営業二課 S2（長 head）、
/// 管理本部 D2（長 hr2）> 人事部 HR（長 hr2）。
/// </remarks>
public sealed class FieldManagementTests : IAsyncLifetime
{
    private IngestDatabase _db = null!;
    private FieldManagementService _service = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => IngestDatabase.Today;

    /// <summary>組織管理者（IT 部門。人事管理者ではない）。</summary>
    private ViewerContext OrgAdmin => ViewerContext.ForUser(_db.Id("takahashi"), [Roles.OrgAdmin], Channel.Ui, FieldLimit.Unrestricted);

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        _service = new FieldManagementService(new NpgsqlFieldManagementStore(_db.DataSource), _db.Snapshots, _db.Audit, TimeProvider.System);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private async Task<bool> DbCoversAsync(ViewerContext viewer, string unit, bool withGrants = true) =>
        (await _db.QueryWithAsync<bool>(viewer, "SELECT jinji_viewer_scope_covers($1, $2, $3)", Ct, _db.Id(unit), Today, withGrants)).Single();

    private async Task AssertScopeAsync(ViewerContext viewer, string[] covered, string[] notCovered)
    {
        var scope = await _service.ScopeAsync(viewer, Today, Ct);
        foreach (var unit in covered)
        {
            Assert.True(scope.Covers(_db.Id(unit)), $"アプリ: {unit} を範囲に持つはず");
            Assert.True(await DbCoversAsync(viewer, unit), $"RLS: {unit} を範囲に持つはず");
        }

        foreach (var unit in notCovered)
        {
            Assert.False(scope.Covers(_db.Id(unit)), $"アプリ: {unit} を範囲に持たないはず");
            Assert.False(await DbCoversAsync(viewer, unit), $"RLS: {unit} を範囲に持たないはず");
        }
    }

    private async Task<long> CountAuditAsync(string action, string outcome) =>
        (await _db.QueryAsync<long>(null, "SELECT count(*) FROM audit_event WHERE action = $1 AND outcome = $2", Ct, action, outcome)).Single();

    // ---- 範囲の判定 ---------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "範囲: 組織長は自分の組織と配下を持ち、他の本部は持たない。アプリと RLS が同じ判定をする")]
    public async Task Scope_HeadCoversOwnSubtree()
    {
        await AssertScopeAsync(_db.User("head"), ["D1", "DP1", "S1", "S2"], ["D2", "HR"]);
        await AssertScopeAsync(_db.User("member"), [], ["D1", "DP1", "S1", "S2", "D2", "HR"]);
    }

    [Fact(DisplayName = "範囲: 人事管理者・組織管理者は全社。AI エージェントは委任元の範囲")]
    public async Task Scope_AdminsCoverAll_AgentUsesDelegator()
    {
        await AssertScopeAsync(OrgAdmin, ["D1", "S1", "D2", "HR"], []);
        await AssertScopeAsync(_db.HrUser("hr1"), ["D1", "S1", "D2", "HR"], []);
        await AssertScopeAsync(ViewerContext.ForAgent("assistant", _db.Id("hr2"), [], Channel.Mcp, FieldLimit.Unrestricted), ["D2", "HR"], ["D1", "S1"]);
    }

    [Fact(DisplayName = "範囲: 権限を生む変更は一つ上の組織の範囲を要し、最上位の組織は全社の範囲だけ")]
    public async Task Scope_ParentRequiredForPrivilegeChanges()
    {
        var head = await _service.ScopeAsync(_db.User("head"), Today, Ct);
        Assert.True(head.CoversParentOf(_db.Id("S2")));
        Assert.True(head.CoversParentOf(_db.Id("DP1")));
        Assert.False(head.CoversParentOf(_db.Id("D1")));
        Assert.False(head.CoversParentOf(_db.Id("HR")));

        var admin = await _service.ScopeAsync(OrgAdmin, Today, Ct);
        Assert.True(admin.CoversParentOf(_db.Id("D1")));
    }

    // ---- 担当の指名 ---------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "指名: 組織長が指名した担当は、その組織と配下の範囲を持ち、取り消すと失う")]
    public async Task Grant_GivesScopeUntilRevoked()
    {
        var grant = await _service.GrantEditorAsync(_db.User("head"), _db.Id("S1"), _db.Id("member"), Today, Today.AddDays(90), Ct);

        await AssertScopeAsync(_db.User("member"), ["S1"], ["DP1", "S2", "HR"]);
        Assert.False(await DbCoversAsync(_db.User("member"), "S1", withGrants: false));
        Assert.Equal(1, await CountAuditAsync(FieldManagementService.GrantEditorAction, "success"));

        var revoked = await _service.RevokeEditorAsync(_db.User("head"), grant.Id, Ct);
        Assert.NotNull(revoked.RevokedAt);
        await AssertScopeAsync(_db.User("member"), [], ["S1"]);
        await Assert.ThrowsAsync<FieldManagementException>(() => _service.RevokeEditorAsync(_db.User("head"), grant.Id, Ct));
    }

    [Fact(DisplayName = "指名: 担当は再指名できず、範囲の外・自分・AI エージェントの指名は拒否する（アプリと RLS）")]
    public async Task Grant_RejectsWithoutHeadScope()
    {
        await _service.GrantEditorAsync(_db.User("head"), _db.Id("S1"), _db.Id("member"), Today, Today.AddDays(30), Ct);

        // 担当（member）は担当としての範囲しか持たないので、さらに指名できない
        var redelegate = await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.GrantEditorAsync(_db.User("member"), _db.Id("S1"), _db.Id("takahashi"), Today, Today.AddDays(30), Ct));
        Assert.Equal(FieldManagementError.Forbidden, redelegate.Error);
        await Assert.ThrowsAsync<PostgresException>(() => InsertGrantDirectlyAsync(_db.User("member"), "S1", "takahashi", "member"));

        // 範囲の外（他の本部）
        var outside = await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.GrantEditorAsync(_db.User("head"), _db.Id("HR"), _db.Id("member"), Today, Today.AddDays(30), Ct));
        Assert.Equal(FieldManagementError.Forbidden, outside.Error);
        await Assert.ThrowsAsync<PostgresException>(() => InsertGrantDirectlyAsync(_db.User("head"), "HR", "member", "head"));

        // 自分は指名できない（CHECK 制約でも）
        var self = await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.GrantEditorAsync(_db.User("head"), _db.Id("S1"), _db.Id("head"), Today, Today.AddDays(30), Ct));
        Assert.Equal(FieldManagementError.Forbidden, self.Error);
        var check = await Assert.ThrowsAsync<PostgresException>(() => InsertGrantDirectlyAsync(_db.User("head"), "S1", "head", "head"));
        Assert.Equal("org_editor_grant_not_self", check.ConstraintName);

        // AI エージェントは委任元が組織長でも指名できない
        var agent = ViewerContext.ForAgent("assistant", _db.Id("head"), [], Channel.Mcp, FieldLimit.Unrestricted);
        var byAgent = await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.GrantEditorAsync(agent, _db.Id("S1"), _db.Id("takahashi"), Today, Today.AddDays(30), Ct));
        Assert.Equal(FieldManagementError.Forbidden, byAgent.Error);
        await Assert.ThrowsAsync<PostgresException>(() => InsertGrantDirectlyAsync(agent, "S1", "takahashi", "head"));

        Assert.True(await CountAuditAsync(FieldManagementService.GrantEditorAction, "denied") >= 4);
    }

    [Fact(DisplayName = "指名: 期間は今日以降に始まり上限の日数まで。同じ組織・同じ人の期間の重なりは拒否する")]
    public async Task Grant_ValidatesPeriod()
    {
        var head = _db.User("head");
        Assert.Equal(FieldManagementError.Invalid, (await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.GrantEditorAsync(head, _db.Id("S1"), _db.Id("member"), Today.AddDays(-1), Today.AddDays(10), Ct))).Error);
        Assert.Equal(FieldManagementError.Invalid, (await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.GrantEditorAsync(head, _db.Id("S1"), _db.Id("member"), Today, Today.AddDays(FieldManagementService.MaxGrantDays + 1), Ct))).Error);

        await _service.GrantEditorAsync(head, _db.Id("S1"), _db.Id("member"), Today, Today.AddDays(30), Ct);
        Assert.Equal(FieldManagementError.Conflict, (await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.GrantEditorAsync(head, _db.Id("S1"), _db.Id("member"), Today.AddDays(10), Today.AddDays(40), Ct))).Error);

        // 期間が重ならなければ続けて指名できる。別の組織も
        await _service.GrantEditorAsync(head, _db.Id("S1"), _db.Id("member"), Today.AddDays(30), Today.AddDays(60), Ct);
        await _service.GrantEditorAsync(head, _db.Id("S2"), _db.Id("member"), Today, Today.AddDays(30), Ct);
        Assert.Equal(3, (await _service.ListGrantsAsync(head, null, _db.Id("member"), Ct)).Count);
    }

    // ---- 管理元 -------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "管理元: 既定は全領域 hr。起案者と別の人が承認すると有効になり、次の承認で置き換わる")]
    public async Task Mode_ProposeAndApprove()
    {
        Assert.All((await _service.ModesAsync(_db.User("member"), Ct)).Values, m => Assert.Equal(FieldManagementMode.Hr, m));

        var proposal = await _service.ProposeModeAsync(OrgAdmin, FieldDomain.Assignment, FieldManagementMode.Field, "異動は現場で登録する", Ct);

        // 起案者は承認できない（アプリと CHECK 制約）
        var self = await Assert.ThrowsAsync<FieldManagementException>(() => _service.DecideModeAsync(OrgAdmin, proposal.Id, true, Ct));
        Assert.Equal(FieldManagementError.Forbidden, self.Error);
        var check = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            OrgAdmin, "UPDATE field_domain_setting SET status = 'approved', decided_by_person_id = jinji_viewer_person_id(), decided_at = now() WHERE id = $1", Ct, proposal.Id));
        Assert.Equal("field_domain_setting_segregation_of_duties", check.ConstraintName);

        // 同じ領域の起案中は 1 つだけ
        Assert.Equal(FieldManagementError.Conflict, (await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.ProposeModeAsync(_db.HrUser("hr1"), FieldDomain.Assignment, FieldManagementMode.Both, null, Ct))).Error);

        await _service.DecideModeAsync(_db.HrUser("hr2"), proposal.Id, true, Ct);
        Assert.Equal(FieldManagementMode.Field, (await _service.ModesAsync(_db.User("member"), Ct))[FieldDomain.Assignment]);
        Assert.Equal("field", (await _db.QueryWithAsync<string>(_db.User("member"), "SELECT jinji_field_domain_mode('assignment')", Ct)).Single());
        Assert.Equal("hr", (await _db.QueryWithAsync<string>(_db.User("member"), "SELECT jinji_field_domain_mode('org_site')", Ct)).Single());

        // 次の承認で置き換わる（有効な行は 1 つ）
        var next = await _service.ProposeModeAsync(_db.HrUser("hr1"), FieldDomain.Assignment, FieldManagementMode.Both, null, Ct);
        await _service.DecideModeAsync(OrgAdmin, next.Id, true, Ct);
        Assert.Equal(FieldManagementMode.Both, (await _service.ModesAsync(_db.User("member"), Ct))[FieldDomain.Assignment]);
        var settings = await _service.ListSettingsAsync(_db.User("member"), Ct);
        Assert.Equal(
            [FieldDomainSettingStatus.Approved, FieldDomainSettingStatus.Superseded],
            settings.Select(s => s.Status).ToArray());
    }

    [Fact(DisplayName = "管理元: 起案・承認は人事管理者か組織管理者の人だけ（一般の人・AI エージェント・RLS）。今と同じ管理元は起案できない")]
    public async Task Mode_RequiresAdminUser()
    {
        Assert.Equal(FieldManagementError.Forbidden, (await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.ProposeModeAsync(_db.User("head"), FieldDomain.OrgSite, FieldManagementMode.Field, null, Ct))).Error);
        var agent = ViewerContext.ForAgent("assistant", _db.Id("hr1"), [Roles.HrAdmin], Channel.Mcp, FieldLimit.Unrestricted);
        Assert.Equal(FieldManagementError.Forbidden, (await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.ProposeModeAsync(agent, FieldDomain.OrgSite, FieldManagementMode.Field, null, Ct))).Error);
        Assert.Equal(FieldManagementError.Invalid, (await Assert.ThrowsAsync<FieldManagementException>(() =>
            _service.ProposeModeAsync(OrgAdmin, FieldDomain.OrgSite, FieldManagementMode.Hr, null, Ct))).Error);

        await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.User("head"),
            "INSERT INTO field_domain_setting (id, domain, mode, proposed_by_person_id) VALUES ($1, 'org_site', 'field', jinji_viewer_person_id())",
            Ct,
            EntityId.New()));
        Assert.Equal(2, await CountAuditAsync(FieldManagementService.ProposeModeAction, "denied"));
    }

    [Fact(DisplayName = "ロール: org_admin を付与できる")]
    public async Task Role_OrgAdminIsKnown()
    {
        Assert.Contains(Roles.OrgAdmin, Roles.Known);
        await _db.ExecuteAsync(
            ViewerContext.ForSystem(Channel.Cli),
            "INSERT INTO role_grant (id, person_id, role, granted_by_kind) VALUES ($1, $2, 'org_admin', 'cli')",
            Ct,
            EntityId.New(),
            _db.Id("takahashi"));
    }

    private Task<int> InsertGrantDirectlyAsync(ViewerContext viewer, string unit, string person, string grantedBy) =>
        _db.ExecuteAsync(
            viewer,
            "INSERT INTO org_editor_grant (id, org_unit_id, person_id, valid_period, granted_by_person_id) VALUES ($1, $2, $3, daterange($4, $5), $6)",
            Ct,
            EntityId.New(),
            _db.Id(unit),
            _db.Id(person),
            Today,
            Today.AddDays(30),
            _db.Id(grantedBy));
}
