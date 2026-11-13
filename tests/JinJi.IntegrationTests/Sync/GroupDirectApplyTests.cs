using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;
using JinJi.Policy;
using JinJi.Sync;
using JinJi.Sync.DirectApply;

namespace JinJi.IntegrationTests.Sync;

/// <summary>グループの計画の直接反映（実 PostgreSQL。Graph はメモリ上の代役と HTTP のフェイク。実テナントには接続しない。ADR-0015）。</summary>
public sealed class GroupDirectApplyTests : IAsyncLifetime
{
    private static readonly DirectApplyOptions Options = new()
    {
        Enabled = true,
        TenantId = "00000000-0000-0000-0000-0000000000aa",
        ClientId = "00000000-0000-0000-0000-0000000000bb",
        CertificatePath = "/nonexistent.pfx",
        MaxRetries = 0,
    };

    private SyncDatabase _sync = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _sync = await SyncDatabase.CreateAsync(Ct);

    public async ValueTask DisposeAsync() => await _sync.DisposeAsync();

    private static SyncSettings Settings(string groups = """ "prefix": "JJ-" """) =>
        SyncSettings.Parse(Encoding.UTF8.GetBytes(SyncDatabase.Settings.ToCanonicalJson().TrimEnd('}') + $$""", "orgGroups": { {{groups}} } }"""));

    private async Task<SyncPlanCreated> PlanAsync(
        MemoryGroups directory, SyncSettings settings, bool approve = true, Func<DirectoryGroup, DirectoryGroup>? tamper = null)
    {
        var groups = await directory.ReadGroupsAsync(Ct);
        if (tamper is not null)
        {
            groups = [.. groups.Select(tamper)];
        }

        var created = await _sync.Service.CreateGroupPlanAsync(_sync.Hr("hr1"), new GroupSyncPlanRequest(groups, settings, "graph", new string('0', 64)), Ct);
        if (approve)
        {
            await _sync.Service.ApproveAsync(_sync.Hr("hr2"), created.Plan.Id, Ct);
        }

        return created;
    }

    private Task<SyncResultImport> ApplyAsync(IGroupDirectoryClient directory, Guid planId, bool whatIf = false) =>
        new GroupDirectApplier(_sync.Service, directory, _sync.Time).ApplyAsync(ViewerContext.ForSystem(Channel.Cli), planId, whatIf, Ct);

    [Fact(DisplayName = "直接反映: グループを作ってメンバーを加える。ドライランでは書かない。もう一度計画すると差分が無い（冪等）")]
    public async Task Apply_CreatesGroupsAndMembers()
    {
        var directory = new MemoryGroups();
        var plan = await PlanAsync(directory, Settings());
        var scripted = plan.Items.Count(i => i.InScript);

        var dry = await ApplyAsync(directory, plan.Plan.Id, whatIf: true);
        Assert.Equal((scripted, 0), (dry.Counts[SyncOutcome.WouldApply], directory.Writes));

        var applied = await ApplyAsync(directory, plan.Plan.Id);

        Assert.Equal(scripted, applied.Counts[SyncOutcome.Succeeded]);
        Assert.Equal(plan.Items.Count(i => i.Action == SyncAction.CreateGroup), directory.Groups.Count);
        Assert.All(directory.Groups.Values, g => Assert.StartsWith("JJ-", g.DisplayName, StringComparison.Ordinal));
        var section = directory.Groups.Values.Single(g => g.Members.Contains(_sync.Object("takahashi")) && g.DisplayName == "JJ-営業一課");
        Assert.NotNull(OrgGroupSettings.ReadMarker(section.Description));
        Assert.False(section.Unified);
        Assert.Equal(SyncPlanStatus.Applied, (await _sync.Store.FindPlanAsync(_sync.Hr("hr2"), plan.Plan.Id, Ct))!.Status);

        // 反映後の状態から計画し直すと、実行する項目が無い
        var again = await PlanAsync(directory, Settings(), approve: false);
        Assert.DoesNotContain(again.Items, i => i.InScript);
    }

    [Fact(DisplayName = "直接反映: 既にメンバーなら何もしない。外す・表示名の更新。計画の後に名前が変わっていればスキップする。失敗しても続ける")]
    public async Task Apply_Idempotent_Precondition_AndFailure()
    {
        var directory = new MemoryGroups();
        await ApplyAsync(directory, (await PlanAsync(directory, Settings())).Plan.Id);
        var section = directory.Groups.Values.Single(g => g.DisplayName == "JJ-営業一課");
        var department = directory.Groups.Values.Single(g => g.DisplayName == "JJ-第一営業部");

        // Entra 側で: 営業一課から takahashi を外す・member（別の課の人）を加える。部のグループを改名する
        section.Members.Remove(_sync.Object("takahashi"));
        section.Members.Add(_sync.Object("member"));
        department.DisplayName = "JJ-旧営業部";
        var plan = await PlanAsync(directory, Settings());
        var add = plan.Items.Single(i => i.Action == SyncAction.AddGroupMember && i.GroupObjectId == section.Id);
        var remove = plan.Items.Single(i => i.Action == SyncAction.RemoveGroupMember && i.GroupObjectId == section.Id);
        var update = plan.Items.Single(i => i.Action == SyncAction.UpdateGroup);

        // 計画の後に: 誰かが takahashi を戻した（既にあるべき状態）。部のグループの名前がさらに変わった（事前条件に合わない）。外す操作は失敗させる
        section.Members.Add(_sync.Object("takahashi"));
        department.DisplayName = "JJ-誰かが変えた名前";
        directory.FailRemove = true;

        var applied = await ApplyAsync(directory, plan.Plan.Id);

        Assert.Equal(SyncOutcome.AlreadyApplied, applied.Items.Single(r => r.ItemId == add.Id).Outcome);
        Assert.Equal(SyncOutcome.Failed, applied.Items.Single(r => r.ItemId == remove.Id).Outcome);
        Assert.Equal(SyncOutcome.SkippedPrecondition, applied.Items.Single(r => r.ItemId == update.Id).Outcome);
        Assert.Equal("JJ-誰かが変えた名前", department.DisplayName);
        Assert.Equal(
            "JJ-誰かが変えた名前",
            (await _sync.Db.QueryAsync<string>(null, "SELECT observed ->> 'group.displayName' FROM sync_item_result WHERE outcome = 'skipped_precondition'", Ct)).Single());
    }

    [Fact(DisplayName = "直接反映: Microsoft 365 グループでは組織長をオーナーにしてからチームを作る。同じ mailNickname の印の無いグループがあれば作らない")]
    public async Task Apply_Microsoft365_Team_AndNicknameClash()
    {
        var directory = new MemoryGroups();
        var settings = Settings(""" "prefix": "JJ-", "groupType": "microsoft365", "createTeam": true """);
        var plan = await PlanAsync(directory, settings);
        var team = plan.Items.First(i => i.Action == SyncAction.CreateTeam);
        var clashed = plan.Items.First(i => i.Action == SyncAction.CreateGroup && i.OrgUnitId != team.OrgUnitId
            && plan.Items.Any(m => m.Action == SyncAction.AddGroupMember && m.OrgUnitId == i.OrgUnitId));

        // 計画の後に、同じ mailNickname のグループを誰かが作った
        directory.AddForeign("別のグループ", clashed.Changes.Single(c => c.Field == GroupFields.MailNickname).Desired!);

        var applied = await ApplyAsync(directory, plan.Plan.Id);

        Assert.Equal(SyncOutcome.SkippedPrecondition, applied.Items.Single(r => r.ItemId == clashed.Id).Outcome);
        Assert.Equal(SyncOutcome.Succeeded, applied.Items.Single(r => r.ItemId == team.Id).Outcome);
        var group = directory.Groups.Values.Single(g => OrgGroupSettings.ReadMarker(g.Description) == team.OrgUnitId);
        Assert.True(group.Unified && group.HasTeam && group.Owners.Count == 1);
        // 作れなかったグループのメンバーは「グループが作られていない」で失敗として残る
        Assert.Contains(applied.Items, r => r.Outcome == SyncOutcome.Failed && plan.Items.Single(i => i.Id == r.ItemId).OrgUnitId == clashed.OrgUnitId);
    }

    [Fact(DisplayName = "直接反映: 計画の後に印を外した・別の組織の印に書き換えたグループには、更新・メンバー・オーナー・チームのどれも書き込まない")]
    public async Task Apply_MarkerRemovedOrRewritten_WritesNothing()
    {
        var directory = new MemoryGroups();
        var settings = Settings(""" "prefix": "JJ-", "groupType": "microsoft365", "createTeam": true """);
        await ApplyAsync(directory, (await PlanAsync(directory, settings)).Plan.Id);
        var department = directory.Groups.Values.Single(g => g.DisplayName == "JJ-第一営業部");
        var section = directory.Groups.Values.Single(g => g.DisplayName == "JJ-営業一課");
        Assert.True(department.HasTeam && department.Owners.Count == 1);

        // Entra 側でずれを作り、すべての種類の操作が計画に出るようにする
        department.DisplayName = "JJ-旧営業部";
        department.Members.Remove(_sync.Object("takahashi"));
        department.Members.Add(_sync.Object("retiree"));
        department.Owners.Clear();
        department.HasTeam = false;
        section.Members.Remove(_sync.Object("takahashi"));
        // 廃止された組織のグループ 2 つ（印の組織が jin-ji に無い）
        var kept = directory.AddOwned("JJ-企画課", "jinji-kikaku", Guid.CreateVersion7());
        var cut = directory.AddOwned("JJ-広報課", "jinji-koho", Guid.CreateVersion7());

        var plan = await PlanAsync(directory, settings);
        var onDepartment = plan.Items.Where(i => i.InScript && i.GroupObjectId == department.Id).ToList();
        Assert.Equal(
            [SyncAction.UpdateGroup, SyncAction.AddGroupMember, SyncAction.RemoveGroupMember, SyncAction.AddGroupOwner, SyncAction.CreateTeam],
            onDepartment.Select(i => i.Action).Distinct().Order());
        var onSection = plan.Items.Where(i => i.InScript && i.GroupObjectId == section.Id).ToList();
        Assert.Contains(onSection, i => i.Action == SyncAction.AddGroupMember);
        var retireKept = plan.Items.Single(i => i.Action == SyncAction.RetireGroup && i.GroupObjectId == kept.Id);
        var retireCut = plan.Items.Single(i => i.Action == SyncAction.RetireGroup && i.GroupObjectId == cut.Id);

        // 計画の後に: 部のグループの印を外す。課のグループの印を別の組織に書き換える。廃止のグループの片方の印を外す
        department.Description = "管理者が jin-ji の管理から外した";
        section.Description = $"営業一課 {OrgGroupSettings.Marker(Guid.CreateVersion7())}";
        cut.Description = "印を外した";
        var before = (department.Snapshot(), section.Snapshot(), cut.Snapshot());

        var applied = await ApplyAsync(directory, plan.Plan.Id);

        var skipped = onDepartment.Concat(onSection).Append(retireCut).Select(i => i.Id).ToHashSet();
        Assert.All(applied.Items.Where(r => skipped.Contains(r.ItemId)), r => Assert.Equal(SyncOutcome.SkippedPrecondition, r.Outcome));
        Assert.Equal(skipped.Count, applied.Items.Count(r => skipped.Contains(r.ItemId)));
        Assert.Equal(before, (department.Snapshot(), section.Snapshot(), cut.Snapshot()));
        // 印がそのままの廃止のグループには、廃止の印が付く（廃止された組織でも照合できている）
        Assert.Equal(SyncOutcome.Succeeded, applied.Items.Single(r => r.ItemId == retireKept.Id).Outcome);
        Assert.StartsWith("【廃止】", kept.DisplayName, StringComparison.Ordinal);

        // ドライランでも同じ判定になる（書き込む予定に数えない）
        var replan = await PlanAsync(directory, settings, tamper: g => g.ObjectId == department.Id ? g with { Description = $"x {OrgGroupSettings.Marker(onDepartment[0].OrgUnitId!.Value)}" } : g);
        var dry = await ApplyAsync(directory, replan.Plan.Id, whatIf: true);
        var dryTargets = replan.Items.Where(i => i.InScript && i.GroupObjectId == department.Id).Select(i => i.Id).ToHashSet();
        Assert.NotEmpty(dryTargets);
        Assert.All(dry.Items.Where(r => dryTargets.Contains(r.ItemId)), r => Assert.Equal(SyncOutcome.SkippedPrecondition, r.Outcome));
    }

    [Fact(DisplayName = "直接反映: 入力のファイルに印を書き足しても、Entra 側のグループに印が無ければ書き込まない")]
    public async Task Apply_ForgedMarkerInInput_WritesNothing()
    {
        var directory = new MemoryGroups();
        var target = (await PlanAsync(directory, Settings(), approve: false)).Items
            .First(i => i.Action == SyncAction.CreateGroup && i.GroupDisplayName == "JJ-営業一課").OrgUnitId!.Value;
        var foreign = directory.AddForeign("アプリの権限を与えるグループ", "app-admins");

        // 計画の入力だけ、印の無いグループに印を書き足す（グループの一覧をファイルで渡す場合に起こりうる）
        var plan = await PlanAsync(directory, Settings(), tamper: g => g.ObjectId == foreign.Id ? g with { Description = OrgGroupSettings.Marker(target) } : g);
        var forged = plan.Items.Where(i => i.InScript && i.GroupObjectId == foreign.Id).ToList();
        Assert.Contains(forged, i => i.Action == SyncAction.AddGroupMember);
        var before = foreign.Snapshot();
        var writes = directory.Writes;

        var applied = await ApplyAsync(directory, plan.Plan.Id);

        Assert.All(applied.Items.Where(r => forged.Any(i => i.Id == r.ItemId)), r => Assert.Equal(SyncOutcome.SkippedPrecondition, r.Outcome));
        Assert.Equal(before, foreign.Snapshot());
        Assert.Empty(foreign.Members);
        // 印の無いグループ以外への書き込み（他の組織のグループの作成など）は続ける
        Assert.True(directory.Writes > writes);
    }

    [Fact(DisplayName = "Graph: グループの作成・更新・メンバー・オーナー・チームの要求の形。一覧は次のページをたどり、印の付いたグループだけメンバーを読む")]
    public async Task GraphRequests()
    {
        var group = Guid.NewGuid().ToString("D");
        var other = Guid.NewGuid().ToString("D");
        var user = Guid.NewGuid().ToString("D");
        var marker = OrgGroupSettings.Marker(Guid.NewGuid());
        var handler = new GraphClientTests.FakeHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            return (request.Method.Method, path) switch
            {
                ("POST", "/v1.0/groups") => Json(HttpStatusCode.Created, $$"""{"id":"{{group.ToUpperInvariant()}}"}"""),
                ("POST", _) when path.EndsWith("/checkMemberGroups", StringComparison.Ordinal) => Json(HttpStatusCode.OK, $$"""{"value":["{{group}}"]}"""),
                ("GET", "/v1.0/groups") when query.Contains("$filter", StringComparison.Ordinal) => Json(HttpStatusCode.OK, """{"value":[]}"""),
                ("GET", "/v1.0/groups") when query.Contains("$skiptoken", StringComparison.Ordinal) =>
                    Json(HttpStatusCode.OK, $$"""{"value":[{"id":"{{other}}","displayName":"別のグループ","description":null,"groupTypes":[]}]}"""),
                ("GET", "/v1.0/groups") => Json(HttpStatusCode.OK,
                    $$"""{"value":[{"id":"{{group}}","displayName":"JJ-営業一課","description":"x {{marker}}","groupTypes":["Unified"],"resourceProvisioningOptions":["Team"]}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/groups?$skiptoken=abc"}"""),
                ("GET", _) when path.EndsWith("/members", StringComparison.Ordinal) || path.EndsWith("/owners", StringComparison.Ordinal) => Json(HttpStatusCode.OK, $$"""{"value":[{"id":"{{user}}"}]}"""),
                ("GET", _) => Json(HttpStatusCode.OK, $$"""{"id":"{{group}}","displayName":"JJ-営業一課","description":"x","groupTypes":[],"resourceProvisioningOptions":[]}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NoContent),
            };
        });
        var client = new GraphRestGroupClient(new HttpClient(handler), new Tokens(), Options);

        Assert.Equal(group, await client.CreateGroupAsync("JJ-営業一課", "jinji-a-s1", "説明", unified: true, Ct));
        await client.UpdateGroupAsync(group, new Dictionary<string, string?> { ["displayName"] = "JJ-新" }, Ct);
        Assert.True(await client.IsMemberAsync(group, user, Ct));
        await client.AddMemberAsync(group, user, Ct);
        await client.RemoveMemberAsync(group, user, Ct);
        await client.AddOwnerAsync(group, user, Ct);
        await client.CreateTeamAsync(group, Ct);
        Assert.Empty(await client.FindByMailNicknameAsync("o'brien", Ct));
        Assert.Equal((false, false), ((await client.GetGroupAsync(group, Ct))!.Unified, (await client.GetGroupAsync(group, Ct))!.HasTeam));

        Assert.Equal(
            [
                "POST https://graph.microsoft.com/v1.0/groups",
                $"PATCH https://graph.microsoft.com/v1.0/groups/{group}",
                $"POST https://graph.microsoft.com/v1.0/users/{user}/checkMemberGroups",
                $"POST https://graph.microsoft.com/v1.0/groups/{group}/members/$ref",
                $"DELETE https://graph.microsoft.com/v1.0/groups/{group}/members/{user}/$ref",
                $"POST https://graph.microsoft.com/v1.0/groups/{group}/owners/$ref",
                $"PUT https://graph.microsoft.com/v1.0/groups/{group}/team",
                "GET https://graph.microsoft.com/v1.0/groups?$filter=mailNickname eq 'o''brien'&$select=id,displayName,description,mailNickname,groupTypes,resourceProvisioningOptions",
            ],
            handler.Requests.Take(8).Select(r => $"{r.Method} {Uri.UnescapeDataString(r.Uri)}"));
        using (var create = JsonDocument.Parse(handler.Requests[0].Body!))
        {
            Assert.Equal(("JJ-営業一課", "jinji-a-s1", true, true, "Unified"),
                (create.RootElement.GetProperty("displayName").GetString(), create.RootElement.GetProperty("mailNickname").GetString(),
                    create.RootElement.GetProperty("mailEnabled").GetBoolean(), create.RootElement.GetProperty("securityEnabled").GetBoolean(),
                    create.RootElement.GetProperty("groupTypes")[0].GetString()));
        }

        using (var member = JsonDocument.Parse(handler.Requests[3].Body!))
        {
            Assert.Equal($"https://graph.microsoft.com/v1.0/directoryObjects/{user}", member.RootElement.GetProperty("@odata.id").GetString());
        }

        handler.Requests.Clear();
        var groups = await client.ReadGroupsAsync(Ct);
        Assert.Equal([(group, true, 1), (other, false, -1)], groups.Select(g => (g.ObjectId, g.HasTeam == true, g.MemberIds?.Count ?? -1)));
        Assert.Equal(4, handler.Requests.Count);

        // 次のページの送信先が許可リストに無ければ読まない
        var evil = new GraphClientTests.FakeHandler(_ => Json(HttpStatusCode.OK, """{"value":[],"@odata.nextLink":"https://evil.example.com/v1.0/groups"}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new GraphRestGroupClient(new HttpClient(evil), new Tokens(), Options).ReadGroupsAsync(Ct));
        Assert.Single(evil.Requests);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Tokens : IGraphTokenSource
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult("test-token");
    }

    /// <summary>メモリ上の Entra のグループ（Graph の代役）。</summary>
    private sealed class MemoryGroups : IGroupDirectoryClient
    {
        public Dictionary<string, Entry> Groups { get; } = new(StringComparer.Ordinal);

        public int Writes { get; private set; }

        public bool FailRemove { get; set; }

        public Entry AddForeign(string name, string nickname)
        {
            var id = Guid.NewGuid().ToString("D");
            return Groups[id] = new Entry { Id = id, DisplayName = name, MailNickname = nickname, Description = "手で作ったグループ" };
        }

        /// <summary>jin-ji の印の付いたグループを直接置く。</summary>
        public Entry AddOwned(string name, string nickname, Guid orgUnitId)
        {
            var id = Guid.NewGuid().ToString("D");
            return Groups[id] = new Entry { Id = id, DisplayName = name, MailNickname = nickname, Description = $"{name} {OrgGroupSettings.Marker(orgUnitId)}", Unified = true };
        }

        public Task<DirectoryGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken) =>
            Task.FromResult(Groups.TryGetValue(groupId, out var g) ? g.ToGroup(0, withMembers: false) : null);

        public Task<IReadOnlyList<DirectoryGroup>> FindByMailNicknameAsync(string mailNickname, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectoryGroup>>([.. Groups.Values.Where(g => g.MailNickname == mailNickname).Select(g => g.ToGroup(0, withMembers: false))]);

        public Task<string> CreateGroupAsync(string displayName, string mailNickname, string description, bool unified, CancellationToken cancellationToken)
        {
            Writes++;
            var id = Guid.NewGuid().ToString("D");
            Groups[id] = new Entry { Id = id, DisplayName = displayName, MailNickname = mailNickname, Description = description, Unified = unified };
            return Task.FromResult(id);
        }

        public Task UpdateGroupAsync(string groupId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken)
        {
            Writes++;
            if (changes.TryGetValue("displayName", out var name))
            {
                Groups[groupId].DisplayName = name!;
            }

            if (changes.TryGetValue("description", out var description))
            {
                Groups[groupId].Description = description!;
            }

            return Task.CompletedTask;
        }

        public Task<bool> IsMemberAsync(string groupId, string userId, CancellationToken cancellationToken) => Task.FromResult(Groups[groupId].Members.Contains(userId));

        public Task AddMemberAsync(string groupId, string userId, CancellationToken cancellationToken)
        {
            Writes++;
            Groups[groupId].Members.Add(userId);
            return Task.CompletedTask;
        }

        public Task RemoveMemberAsync(string groupId, string userId, CancellationToken cancellationToken)
        {
            if (FailRemove)
            {
                throw new GraphRequestException(403, "Authorization_RequestDenied", "Graph の要求が失敗しました。");
            }

            Writes++;
            Groups[groupId].Members.Remove(userId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListOwnersAsync(string groupId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([.. Groups[groupId].Owners]);

        public Task AddOwnerAsync(string groupId, string userId, CancellationToken cancellationToken)
        {
            Writes++;
            Groups[groupId].Owners.Add(userId);
            return Task.CompletedTask;
        }

        public Task CreateTeamAsync(string groupId, CancellationToken cancellationToken)
        {
            if (Groups[groupId].Owners.Count == 0)
            {
                throw new GraphRequestException(400, "BadRequest", "オーナーのいないグループにはチームを作れません。");
            }

            Writes++;
            Groups[groupId].HasTeam = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DirectoryGroup>> ReadGroupsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DirectoryGroup>>([.. Groups.Values.Select((g, i) => g.ToGroup(i, withMembers: true))]);

        internal sealed class Entry
        {
            public required string Id { get; init; }

            public required string DisplayName { get; set; }

            public required string MailNickname { get; init; }

            public required string Description { get; set; }

            public bool Unified { get; init; }

            public bool HasTeam { get; set; }

            public HashSet<string> Members { get; } = new(StringComparer.Ordinal);

            public HashSet<string> Owners { get; } = new(StringComparer.Ordinal);

            /// <summary>書き込みで変わりうる状態（比較用）。</summary>
            public string Snapshot() =>
                $"{DisplayName}|{Description}|{HasTeam}|{string.Join(",", Members.Order(StringComparer.Ordinal))}|{string.Join(",", Owners.Order(StringComparer.Ordinal))}";

            public DirectoryGroup ToGroup(int index, bool withMembers) =>
                new(Id, DisplayName, Description, MailNickname, Unified, HasTeam, withMembers ? [.. Members] : null, withMembers ? [.. Owners] : null, index);
        }
    }
}
