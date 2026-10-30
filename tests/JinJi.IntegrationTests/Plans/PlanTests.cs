using System.Text;
using JinJi.Application.FieldManagement;
using JinJi.Application.Groups;
using JinJi.Application.People;
using JinJi.Application.Plans;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Ingest;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Plans;

/// <summary>
/// 素案・申請版・提案資料（0023・ADR-0013 決定 8・9）: 作成者と共有した人だけに見えること（アプリと RLS）、公開側を変えないシミュレーション、
/// 申請版の固定と承認の記録、承認済みの申請版からの直接登録、CSV を実 PostgreSQL で確かめる。
/// </summary>
/// <remarks>
/// 組織（<see cref="IngestDatabase"/>）: 第一本部 D1（長 head）> 第一営業部 DP1（長 head）> 営業一課 S1・営業二課 S2（長 head）、
/// 管理本部 D2（長 hr2）> 人事部 HR。人: head（DP1）・takahashi（S1）・member（S2）・hr1・hr2（HR）。
/// </remarks>
public sealed class PlanTests : IAsyncLifetime
{
    private IngestDatabase _db = null!;
    private FieldManagementService _management = null!;
    private PlanService _service = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => IngestDatabase.Today;

    private ViewerContext OrgAdmin => ViewerContext.ForUser(_db.Id("hr1"), [Roles.OrgAdmin], Channel.Ui, FieldLimit.Unrestricted);

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        _management = new FieldManagementService(new NpgsqlFieldManagementStore(_db.DataSource), _db.Snapshots, _db.Audit, TimeProvider.System);
        var people = new PersonDirectoryService(_db.Snapshots, new NpgsqlPolicyRuleStore(_db.DataSource, TimeProvider.System), _db.Audit, TimeProvider.System);
        var registrations = new FieldRegistrationService(
            new NpgsqlFieldRegistrationStore(_db.DataSource, TimeProvider.System), _management, _db.Snapshots, _db.Audit, TimeProvider.System, _db.Notifier);
        var groups = new GroupService(new NpgsqlGroupStore(_db.DataSource), _management, people, _db.Snapshots, _db.Audit, TimeProvider.System);
        _service = new PlanService(
            new NpgsqlPlanStore(_db.DataSource), _db.Snapshots, people, _management, registrations, groups, _db.Audit, TimeProvider.System,
            new PlanOptions { ApprovalClients = ["workflow"] });
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private PlanInput Input(params PlanChange[] changes) => new("来期の体制案", "部内の再編", Today, Today.AddDays(30), changes);

    private PlanChange Transfer(string person, string to) =>
        new(PlanChangeKind.Assignment, _db.Id(person), new Dictionary<string, string?> { ["org_unit_id"] = _db.Id(to).ToString("D") });

    private async Task<PlanError> ErrorOfAsync(Func<Task> action) => (await Assert.ThrowsAsync<PlanException>(action)).Error;

    [Fact(DisplayName = "素案: 誰でも作れて、見えるのは作成者と共有した人だけ（人事管理者にも見えない。アプリと RLS）。共有された人は変えられない")]
    public async Task Plan_IsPrivateUntilShared()
    {
        var plan = await _service.CreateAsync(_db.User("member"), Input(Transfer("takahashi", "S2")), Ct);

        Assert.Equal(PlanError.NotFound, await ErrorOfAsync(() => _service.GetAsync(_db.User("head"), plan.Id, Ct)));
        Assert.Equal(PlanError.NotFound, await ErrorOfAsync(() => _service.GetAsync(_db.HrUser("hr1"), plan.Id, Ct)));
        Assert.Equal(0L, (await _db.QueryWithAsync<long>(_db.HrUser("hr1"), "SELECT count(*) FROM plan", Ct)).Single());

        await _service.ShareAsync(_db.User("member"), plan.Id, [_db.Id("head")], Ct);
        Assert.Equal(plan.Id, (await _service.GetAsync(_db.User("head"), plan.Id, Ct)).Id);
        Assert.Equal(PlanError.Forbidden, await ErrorOfAsync(() => _service.UpdateAsync(_db.User("head"), plan.Id, Input(), Ct)));

        // AI エージェントは委任元の名義で作れる
        var agent = ViewerContext.ForAgent("assistant", _db.Id("member"), [], Channel.Mcp, FieldLimit.Unrestricted);
        var drafted = await _service.CreateAsync(agent, Input(), Ct);
        Assert.Equal((_db.Id("member"), ActorType.Agent), (drafted.Owner, drafted.CreatedVia));

        // 中身の形は DB も検査する（許可リストに無いキー）
        await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.User("member"),
            "UPDATE plan SET changes = '[{\"kind\":\"person\",\"values\":{\"family_name\":\"x\"}}]' WHERE id = $1",
            Ct,
            plan.Id));
    }

    [Fact(DisplayName = "シミュレーション: 公開側を変えずに、異動・新設・人数の増減・警告を返す")]
    public async Task Simulate_DoesNotTouchPublicSide()
    {
        var unit = EntityId.New();
        var plan = await _service.CreateAsync(_db.User("member"), Input(
            new PlanChange(PlanChangeKind.OrgCreate, unit, new Dictionary<string, string?>
            {
                ["code"] = "S3", ["name"] = "営業三課", ["parent_org_unit_id"] = _db.Id("DP1").ToString("D"),
            }),
            Transfer("takahashi", "S2"),
            new PlanChange(PlanChangeKind.Assignment, _db.Id("member"), new Dictionary<string, string?> { ["org_unit_id"] = unit.ToString("D") }),
            new PlanChange(PlanChangeKind.OrgAbolish, _db.Id("S1"), new Dictionary<string, string?>()),
            new PlanChange(PlanChangeKind.Assignment, Guid.NewGuid(), new Dictionary<string, string?> { ["org_unit_id"] = unit.ToString("D") })), Ct);

        var simulation = await _service.SimulateAsync(_db.User("member"), plan.Id, null, Ct);

        Assert.Equal(2, simulation.Moves.Count);
        var created = simulation.Units.Single(u => u.Id == unit);
        Assert.Equal((PlanUnitState.Created, 0, 1), (created.State, created.Before, created.After));
        var s1 = simulation.Units.Single(u => u.Id == _db.Id("S1"));
        Assert.Equal((PlanUnitState.Abolished, 1, 0), (s1.State, s1.Before, s1.After));
        Assert.Contains(simulation.Warnings, w => w.Index == 4);
        Assert.Equal(_db.Id("S1"), (await _db.QueryAsync<Guid>(
            null, "SELECT org_unit_id FROM assignment WHERE person_id = $1 AND assignment_type = 'primary' AND upper_inf(recorded_period)", Ct, _db.Id("takahashi"))).Single());
    }

    [Fact(DisplayName = "申請版: 固定した中身は変わらず、承認の記録は範囲を持つ人か指定の社内システムが 1 回だけ。承認済みの版から範囲で登録する")]
    public async Task Version_ApprovalAndRegistration()
    {
        var plan = await _service.CreateAsync(_db.User("member"), Input(Transfer("takahashi", "S2")), Ct);
        await _service.ShareAsync(_db.User("member"), plan.Id, [_db.Id("head"), _db.Id("hr2")], Ct);
        var version = await _service.FreezeAsync(_db.User("member"), plan.Id, Ct);
        Assert.Equal(1, version.VersionNo);
        await _service.UpdateAsync(_db.User("member"), plan.Id, Input(), Ct);
        Assert.Single((await _service.GetVersionAsync(_db.User("member"), plan.Id, 1, Ct)).Changes);

        // 未承認の版からは登録できない
        Assert.Equal(PlanError.Conflict, await ErrorOfAsync(() => _service.RegisterAsync(_db.User("head"), plan.Id, 1, null, Ct)));

        // 範囲の外の人・指定外の社内システムは記録できない。範囲を持つ部長は記録できる（1 回だけ）
        Assert.Equal(PlanError.Forbidden, await ErrorOfAsync(() => _service.RecordApprovalAsync(_db.User("hr2"), plan.Id, 1, Today, "WF-1", Ct)));
        Assert.Equal(PlanError.Forbidden, await ErrorOfAsync(() =>
            _service.RecordApprovalAsync(ViewerContext.ForService("other", FieldLimit.Unrestricted), plan.Id, 1, Today, "WF-1", Ct)));
        var approved = await _service.RecordApprovalAsync(_db.User("head"), plan.Id, 1, Today, "WF-0001", Ct);
        Assert.Equal("WF-0001", approved.ApprovalReference);
        Assert.Equal(PlanError.Conflict, await ErrorOfAsync(() =>
            _service.RecordApprovalAsync(ViewerContext.ForService("workflow", FieldLimit.Unrestricted), plan.Id, 1, Today, "WF-2", Ct)));

        // 承認済みの版から、操作した人の範囲で直接登録にする（管理元が hr のままなら拒否）
        Assert.Equal(PlanError.Forbidden, await ErrorOfAsync(() => _service.RegisterAsync(_db.User("head"), plan.Id, 1, null, Ct)));
        var setting = await _management.ProposeModeAsync(OrgAdmin, FieldDomain.Assignment, FieldManagementMode.Field, null, Ct);
        await _management.DecideModeAsync(_db.HrUser("hr2"), setting.Id, true, Ct);
        var result = await _service.RegisterAsync(_db.User("head"), plan.Id, 1, null, Ct);
        Assert.Equal(FieldRegistrationStatus.Applied, result.Registration!.Status);
        var link = Assert.Single(await new NpgsqlPlanStore(_db.DataSource).ListRegistrationLinksAsync(_db.User("member"), plan.Id, Ct));
        Assert.Equal((version.Id, 0, result.Registration.Id), (link.PlanVersionId!.Value, link.ChangeIndex, link.FieldRegistrationId));
    }

    [Fact(DisplayName = "社内システム: 指定したワークフロー製品は承認を記録できる")]
    public async Task Version_ServiceRecordsApproval()
    {
        var plan = await _service.CreateAsync(_db.User("member"), Input(Transfer("takahashi", "S2")), Ct);
        await _service.FreezeAsync(_db.User("member"), plan.Id, Ct);

        var approved = await _service.RecordApprovalAsync(ViewerContext.ForService("workflow", FieldLimit.Unrestricted), plan.Id, 1, Today, "WF-9", Ct);

        Assert.Equal("workflow", approved.ApprovalRecordedByClient);
        Assert.Equal(Today, (await _service.GetVersionAsync(_db.User("member"), plan.Id, 1, Ct)).ApprovedOn);
    }

    [Fact(DisplayName = "CSV: 異動を UTF-8 BOM の CSV で出し、表計算の式になる値は無害化する")]
    public async Task Csv_ListsChanges()
    {
        var plan = await _service.CreateAsync(_db.User("member"), Input(
            new PlanChange(PlanChangeKind.Assignment, _db.Id("takahashi"), new Dictionary<string, string?>
            {
                ["org_unit_id"] = _db.Id("S2").ToString("D"), ["position_title"] = "=HYPERLINK()",
            })), Ct);

        var bytes = await _service.ExportCsvAsync(_db.User("member"), plan.Id, null, Ct);

        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("番号,種類,対象,項目,現在の値,素案の値,有効日", text, StringComparison.Ordinal);
        Assert.Contains("E0002", text, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK()\"", text, StringComparison.Ordinal);
    }
}
