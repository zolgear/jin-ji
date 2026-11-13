using System.Security.Cryptography;
using System.Text.Json;
using JinJi.Application;
using JinJi.Application.Sync;
using JinJi.Domain;
using JinJi.Sync;
using Npgsql;

namespace JinJi.IntegrationTests.Sync;

/// <summary>組織に対応するグループの計画の保存・承認・置き換え・古い計画の拒否（実 PostgreSQL。ADR-0015）。</summary>
public sealed class GroupPlanServiceTests : IAsyncLifetime
{
    private static readonly SyncSettings Settings = SyncDatabase.Settings with { OrgGroups = new OrgGroupSettings { Prefix = "JJ-" } };

    private SyncDatabase _sync = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _sync = await SyncDatabase.CreateAsync(Ct);

    public async ValueTask DisposeAsync() => await _sync.DisposeAsync();

    private Task<SyncPlanCreated> CreateAsync(object[]? groups = null, SyncSettings? settings = null)
    {
        var content = JsonSerializer.SerializeToUtf8Bytes(new { value = groups ?? [] });
        return _sync.Service.CreateGroupPlanAsync(
            _sync.Hr("hr1"),
            new GroupSyncPlanRequest(GraphDirectoryGroups.Parse(content), settings ?? Settings, "entra_groups.json", Convert.ToHexStringLower(SHA256.HashData(content))),
            Ct);
    }

    [Fact(DisplayName = "グループの計画を保存して読み戻せる（対象の組織・グループの名前を項目に持つ）。ユーザーの計画とは互いに置き換えない")]
    public async Task Create_SavesGroupPlan_SeparatelyFromUserPlan()
    {
        var users = await _sync.CreatePlanAsync(Ct);
        var created = await CreateAsync();

        Assert.Empty(created.Superseded);
        var plan = (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), created.Plan.Id, Ct))!;
        Assert.Equal((SyncPlanService.TargetSystemEntraGroups, SyncPlanStatus.Proposed, 0), (plan.TargetSystem, plan.Status, plan.DirectoryUserCount));
        Assert.Equal("JJ-", plan.Settings.OrgGroups!.Prefix);
        var items = await _sync.Store.ListItemsAsync(_sync.Hr("hr2"), plan.Id, Ct);
        Assert.Equal(
            created.Items.Select(i => (i.Id, i.Sequence, i.Action, i.ObjectId, i.OrgUnitId, i.GroupObjectId, i.GroupMarkerOrgUnitId, i.GroupDisplayName, i.PersonId, i.InScript)),
            items.Select(i => (i.Id, i.Sequence, i.Action, i.ObjectId, i.OrgUnitId, i.GroupObjectId, i.GroupMarkerOrgUnitId, i.GroupDisplayName, i.PersonId, i.InScript)));
        var create = items.First(i => i.Action == SyncAction.CreateGroup);
        Assert.StartsWith("JJ-", create.GroupDisplayName, StringComparison.Ordinal);
        Assert.NotNull(create.OrgUnitId);
        Assert.Contains(items, i => i.Action == SyncAction.AddGroupMember && i.ObjectId == _sync.Object("takahashi") && i.GroupObjectId is null);
        Assert.Equal(items.Count(i => i.Action == SyncAction.CreateGroup), plan.Summary["action:create_group"]);

        // ユーザーの計画は起案中のまま。グループの計画を作り直すと、前のグループの計画だけが置き換え済みになる
        Assert.Equal(SyncPlanStatus.Proposed, (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), users.Plan.Id, Ct))!.Status);
        var next = await CreateAsync();
        Assert.Equal([plan.Id], next.Superseded);
        Assert.Equal(SyncPlanStatus.Proposed, (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), users.Plan.Id, Ct))!.Status);
    }

    [Fact(DisplayName = "承認は起案者以外の人事管理者。設定に orgGroups が無ければ作れない。所属が変わると古い計画として拒否する")]
    public async Task Approve_AndStale()
    {
        await Assert.ThrowsAsync<FormatException>(() => CreateAsync(settings: SyncDatabase.Settings));

        var created = await CreateAsync();
        await Assert.ThrowsAsync<AccessDeniedException>(() => _sync.Service.ApproveAsync(_sync.Hr("hr1"), created.Plan.Id, Ct));
        Assert.Equal(SyncPlanStatus.Approved, (await _sync.Service.ApproveAsync(_sync.Hr("hr2"), created.Plan.Id, Ct)).Status);

        // 外部 ID の対応が増える（メンバーにできる人が変わる）と、あるべき状態が変わる
        var again = await CreateAsync();
        await _sync.Db.ExecuteAsync(
            _sync.Hr("hr2"), "INSERT INTO external_identity (id, person_id, source_system, external_id) VALUES ($1, $2, 'entra_id', $3)", Ct, EntityId.New(), _sync.Id("hr1"), Guid.NewGuid().ToString("D"));
        await Assert.ThrowsAsync<SyncPlanStaleException>(() => _sync.Service.ApproveAsync(_sync.Hr("hr2"), again.Plan.Id, Ct));
        Assert.Equal(SyncPlanStatus.Stale, (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), again.Plan.Id, Ct))!.Status);
    }

    [Fact(DisplayName = "DB の制約: グループの列はグループの項目だけ。作成の項目は対象のオブジェクト ID を持てない。参考・衝突は実行できない")]
    public async Task DatabaseConstraints()
    {
        var created = await CreateAsync();
        await using var owner = await _sync.Db.Database.OpenOwnerAsync(Ct);
        await Support.SampleData.ActAsHrAdminAsync(owner, Ct);

        async Task<string?> InsertAsync(string action, string? objectId, bool inScript, string? groupName)
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => Support.SampleData.ExecuteAsync(
                owner,
                "INSERT INTO sync_plan_item (id, sync_plan_id, sequence, priority, action, directory_object_id, in_script, group_display_name) VALUES ($1, $2, $3, 'normal', $4, $5, $6, $7)",
                Ct,
                EntityId.New(),
                created.Plan.Id,
                9000 + Random.Shared.Next(1000),
                action,
                (object?)objectId ?? DBNull.Value,
                inScript,
                (object?)groupName ?? DBNull.Value));
            return ex.ConstraintName;
        }

        var id = Guid.NewGuid().ToString("D");
        Assert.Equal("sync_plan_item_group_columns_check", await InsertAsync("update_attributes", id, true, "JJ-x"));
        Assert.Equal("sync_plan_item_object_id_check", await InsertAsync("create_group", id, true, "JJ-x"));
        Assert.Equal("sync_plan_item_object_id_check", await InsertAsync("add_group_member", null, true, "JJ-x"));
        Assert.Equal("sync_plan_item_in_script_check", await InsertAsync("group_conflict", id, true, "JJ-x"));
        Assert.Equal("sync_plan_item_action_check", await InsertAsync("delete_group", id, true, "JJ-x"));
    }
}
