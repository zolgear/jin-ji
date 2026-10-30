using JinJi.Application.FieldManagement;
using JinJi.Application.Groups;
using JinJi.Application.People;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Ingest;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Groups;

/// <summary>
/// 規程外の単位のグループ（0022・ADR-0014）: 作成と公開範囲、編集できる人（アプリと RLS）、管理対象、メンバーの二つの時間軸、
/// 人の項目 groups・関係スコープ same_group・変更フィードを実 PostgreSQL で確かめる。
/// </summary>
/// <remarks>
/// 組織（<see cref="IngestDatabase"/>）: 第一本部 D1（長 head）> 第一営業部 DP1（長 head）> 営業一課 S1・営業二課 S2（長 head）、
/// 管理本部 D2（長 hr2）> 人事部 HR。人: head（DP1）・takahashi（S1）・member（S2）・hr1・hr2（HR）。
/// </remarks>
public sealed class GroupTests : IAsyncLifetime
{
    private IngestDatabase _db = null!;
    private GroupService _service = null!;
    private PersonDirectoryService _people = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => IngestDatabase.Today;

    private ViewerContext OrgAdmin => ViewerContext.ForUser(_db.Id("hr1"), [Roles.OrgAdmin], Channel.Ui, FieldLimit.Unrestricted);

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        var management = new FieldManagementService(new NpgsqlFieldManagementStore(_db.DataSource), _db.Snapshots, _db.Audit, TimeProvider.System);
        _people = new PersonDirectoryService(_db.Snapshots, new NpgsqlPolicyRuleStore(_db.DataSource, TimeProvider.System), _db.Audit, TimeProvider.System);
        _service = new GroupService(new NpgsqlGroupStore(_db.DataSource), management, _people, _db.Snapshots, _db.Audit, TimeProvider.System, _db.Notifier);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private Task<GroupDetail> CreateAsync(string by, string name, GroupVisibility visibility = GroupVisibility.All, string? owner = null) =>
        _service.CreateAsync(_db.User(by), new GroupCreateInput(name, GroupKind.Project, owner is null ? null : _db.Id(owner), visibility, null), Ct);

    private async Task<GroupError> ErrorOfAsync(Func<Task> action) => (await Assert.ThrowsAsync<GroupException>(action)).Error;

    [Fact(DisplayName = "作成: 誰でも作れて作成者がオーナーになる。全社公開は誰にでも、メンバーだけは メンバー・組織管理者にだけ見える")]
    public async Task Create_AndVisibility()
    {
        var open = await CreateAsync("member", "新製品プロジェクト");
        Assert.Equal((_db.Id("member"), GroupRole.Owner), (Assert.Single(open.Members).PersonId, open.Members[0].Role));
        Assert.Equal(1, _db.Notifier.Count);

        var secret = await CreateAsync("member", "社外秘プロジェクト", GroupVisibility.Members);
        Assert.Equal(2, (await _service.ListAsync(_db.User("member"), new GroupQuery(), Ct)).Count);
        Assert.Equal([open.Group.Id], (await _service.ListAsync(_db.User("takahashi"), new GroupQuery(), Ct)).Select(g => g.Id));
        Assert.Equal(GroupError.NotFound, await ErrorOfAsync(() => _service.GetAsync(_db.User("takahashi"), secret.Group.Id, Ct)));
        Assert.Equal(2, (await _service.ListAsync(OrgAdmin, new GroupQuery(), Ct)).Count);

        // メンバーに加えると見えるようになる
        await _service.AddMemberAsync(_db.User("member"), secret.Group.Id, _db.Id("takahashi"), GroupRole.Member, null, null, Ct);
        Assert.Equal(2, (await _service.GetAsync(_db.User("takahashi"), secret.Group.Id, Ct)).Members.Count);

        // AI エージェントは作れない
        var agent = ViewerContext.ForAgent("assistant", _db.Id("member"), [], Channel.Mcp, FieldLimit.Unrestricted);
        Assert.Equal(GroupError.Forbidden, await ErrorOfAsync(() =>
            _service.CreateAsync(agent, new GroupCreateInput("x", GroupKind.Team, null, GroupVisibility.All, null), Ct)));
    }

    [Fact(DisplayName = "編集: オーナー・所管組織の範囲を持つ人は変えられ、ただのメンバーは変えられない（アプリと RLS）。本人は自分を外せる")]
    public async Task Edit_RequiresOwnerOrScope()
    {
        var group = await CreateAsync("takahashi", "営業改善チーム", owner: "S1");
        var id = group.Group.Id;
        await _service.AddMemberAsync(_db.User("takahashi"), id, _db.Id("member"), GroupRole.Member, null, null, Ct);

        // ただのメンバーは他の人を加えられない（DB も拒否する）
        Assert.Equal(GroupError.Forbidden, await ErrorOfAsync(() => _service.AddMemberAsync(_db.User("member"), id, _db.Id("hr1"), GroupRole.Member, null, null, Ct)));
        await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.User("member"),
            "INSERT INTO org_group_member (id, group_id, person_id, role, added_by_person_id, valid_period) VALUES ($1, $2, $3, 'member', jinji_viewer_person_id(), daterange($4, NULL))",
            Ct,
            EntityId.New(),
            id,
            _db.Id("hr1"),
            Today));

        // 所管組織（S1）の範囲を持つ部長（head）は、メンバーでなくても変えられる
        var renamed = await _service.UpdateAsync(_db.User("head"), id, new GroupUpdateInput("営業改善チーム（第 2 期）", null, null), Ct);
        Assert.Equal("営業改善チーム（第 2 期）", renamed.Group.Name);
        await _service.AddMemberAsync(_db.User("head"), id, _db.Id("hr1"), GroupRole.Lead, null, null, Ct);

        // 本人は自分を外せる
        var left = await _service.RemoveMemberAsync(_db.User("member"), id, _db.Id("member"), Ct);
        Assert.DoesNotContain(left.Members, m => m.PersonId == _db.Id("member"));
        Assert.Equal(GroupError.Forbidden, await ErrorOfAsync(() => _service.RemoveMemberAsync(_db.User("member"), id, _db.Id("hr1"), Ct)));
    }

    [Fact(DisplayName = "管理対象: 組織管理者だけが設定し、オーナーは別の人をオーナーにできず、メンバーは自分を外せない")]
    public async Task Managed_RestrictsOwnersAndSelfRemoval()
    {
        var group = await CreateAsync("member", "共有フォルダの管理");
        var id = group.Group.Id;
        await _service.AddMemberAsync(_db.User("member"), id, _db.Id("takahashi"), GroupRole.Member, null, null, Ct);

        Assert.Equal(GroupError.Forbidden, await ErrorOfAsync(() => _service.SetSettingsAsync(_db.User("member"), id, null, true, Ct)));
        Assert.True((await _service.SetSettingsAsync(OrgAdmin, id, null, true, Ct)).Group.Managed);

        Assert.Equal(GroupError.Forbidden, await ErrorOfAsync(() => _service.ChangeRoleAsync(_db.User("member"), id, _db.Id("takahashi"), GroupRole.Owner, Ct)));
        Assert.Equal(GroupError.Forbidden, await ErrorOfAsync(() => _service.RemoveMemberAsync(_db.User("takahashi"), id, _db.Id("takahashi"), Ct)));
        Assert.Equal(GroupRole.Owner, (await _service.ChangeRoleAsync(OrgAdmin, id, _db.Id("takahashi"), GroupRole.Owner, Ct)).Members.Single(m => m.PersonId == _db.Id("takahashi")).Role);

        // 関係スコープ same_group: 管理対象のグループの同じメンバー（共有のスナップショットで判定する）
        var snapshot = await _db.Snapshots.LoadAsync(ViewerContext.ForService("x", FieldLimit.Unrestricted), Today, Today, Ct);
        var relations = new RelationEvaluator(snapshot);
        Assert.True(relations.Satisfies(_db.Id("member"), _db.Id("takahashi"), RelationScope.SameGroup));
        Assert.False(relations.Satisfies(_db.Id("member"), _db.Id("hr1"), RelationScope.SameGroup));
        Assert.Equal(RelationScope.SameGroup, RelationScopes.Parse("same_group"));
    }

    [Fact(DisplayName = "メンバー: 役割の変更は前の行を残し、外した人は記録に残る（二つの時間軸）。期間の重なりは衝突")]
    public async Task Members_AreBitemporal()
    {
        var group = await CreateAsync("member", "委員会");
        var id = group.Group.Id;
        await _service.AddMemberAsync(_db.User("member"), id, _db.Id("takahashi"), GroupRole.Member, null, null, Ct);
        Assert.Equal(GroupError.Conflict, await ErrorOfAsync(() => _service.AddMemberAsync(_db.User("member"), id, _db.Id("takahashi"), GroupRole.Lead, null, null, Ct)));

        await _service.ChangeRoleAsync(_db.User("member"), id, _db.Id("takahashi"), GroupRole.Lead, Ct);
        await _service.RemoveMemberAsync(_db.User("member"), id, _db.Id("takahashi"), Ct);

        var rows = await _db.QueryAsync<string>(
            null,
            "SELECT role || ':' || upper_inf(recorded_period) FROM org_group_member WHERE group_id = $1 AND person_id = $2 ORDER BY lower(recorded_period), role",
            Ct,
            id,
            _db.Id("takahashi"));
        Assert.Equal(["member:false", "lead:false"], rows);
        Assert.DoesNotContain((await _service.GetAsync(_db.User("member"), id, Ct)).Members, m => m.PersonId == _db.Id("takahashi"));
    }

    [Fact(DisplayName = "人の項目 groups: 全社公開のグループだけを返し、変更フィードにも全社公開のグループだけが載る")]
    public async Task PersonField_AndChangeFeed()
    {
        var open = await CreateAsync("member", "勉強会");
        var secret = await CreateAsync("member", "極秘", GroupVisibility.Members);

        var view = Assert.Single(await _people.GetPeopleAsync(_db.User("takahashi"), [_db.Id("member")], null, Ct));
        Assert.True(view.CanSee(PersonFields.Groups));
        var membership = Assert.Single(view.Groups!);
        Assert.Equal((open.Group.Id, GroupRole.Owner), (membership.GroupId, membership.Role));

        var events = await _db.QueryAsync<Guid>(null, "SELECT entity_id FROM change_event WHERE entity_type = 'group'", Ct);
        Assert.Contains(open.Group.Id, events);
        Assert.DoesNotContain(secret.Group.Id, events);
    }

    [Fact(DisplayName = "終了: 終了日からグループもメンバーも無くなる")]
    public async Task Close_EndsGroupAndMembers()
    {
        var group = await CreateAsync("member", "期間限定チーム");
        var id = group.Group.Id;
        await _service.AddMemberAsync(_db.User("member"), id, _db.Id("takahashi"), GroupRole.Member, null, null, Ct);

        var closed = await _service.CloseAsync(_db.User("member"), id, Today.AddDays(30), Ct);
        Assert.Equal(Today.AddDays(30), closed.Group.ValidUntil);
        Assert.All(closed.Members, m => Assert.Equal(Today.AddDays(30), m.Until));
    }
}
