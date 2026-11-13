using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;
using JinJi.Sync;

namespace JinJi.IntegrationTests.Sync;

/// <summary>組織に対応するグループの計画の差分計算（<see cref="EntraGroupPlanner"/>。DB を使わない。ADR-0015）。</summary>
public sealed class GroupPlannerTests
{
    private const string Base = """{ "legalEntities": ["A"], "orgGroups": { "prefix": "JJ-" } }""";

    private readonly SyncFixture _f = new();
    private readonly List<Dictionary<string, object?>> _groups = [];

    public GroupPlannerTests()
    {
        foreach (var key in new[] { "boss", "chief", "member", "solo", "hostboss" })
        {
            _f.Account(key);
        }
    }

    private static SyncSettings Settings(string json = Base) => SyncFixture.Settings(json);

    private SyncPlanDraft Plan(SyncSettings? settings = null) =>
        new EntraGroupPlanner().Plan(new GroupSyncPlanInput(
            _f.Snapshot(), _f.Links, GraphDirectoryGroups.Parse(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["value"] = _groups })), settings ?? Settings(), SyncFixture.Today));

    /// <summary>jin-ji の印の付いたグループ（組織 <paramref name="unit"/> のもの）を Entra 側に置く。</summary>
    private string Owned(string unit, string name, string[]? members, bool unified = false, string[]? owners = null, bool? team = null, string? description = null)
    {
        var id = _f.Object("group:" + unit);
        var group = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id,
            ["displayName"] = name,
            ["mailNickname"] = "jinji-a-" + unit.ToLowerInvariant(),
            ["description"] = description ?? $"x {OrgGroupSettings.Marker(_f.Id(unit))}",
            ["groupTypes"] = unified ? new[] { "Unified" } : [],
        };
        if (members is not null)
        {
            group["members"] = members.Select(m => new Dictionary<string, string> { ["id"] = m }).ToList();
        }

        if (owners is not null)
        {
            group["owners"] = owners.Select(m => new Dictionary<string, string> { ["id"] = m }).ToList();
        }

        if (team is not null)
        {
            group["resourceProvisioningOptions"] = team == true ? new[] { "Team" } : [];
        }

        _groups.Add(group);
        return id;
    }

    [Fact(DisplayName = "グループが無ければ、対象の法人の組織ごとに接頭辞つきで作り、配下を含めてメンバーを加える")]
    public void CreatesGroups_WithPrefix_AndSubtreeMembers()
    {
        var plan = Plan();

        var creates = plan.Items.Where(i => i.Action == SyncAction.CreateGroup).ToList();
        Assert.Equal(["JJ-営業一課", "JJ-営業二課", "JJ-第一営業部", "JJ-第一本部"], creates.Select(i => i.GroupDisplayName).Order(StringComparer.Ordinal));
        var section = creates.Single(i => i.GroupDisplayName == "JJ-営業一課");
        Assert.Equal((null, null, _f.Id("S1"), true), (section.ObjectId, section.GroupObjectId, section.OrgUnitId!.Value, section.InScript));
        Assert.Contains(new SyncChange(GroupFields.MailNickname, null, "jinji-a-s1"), section.Changes);
        Assert.Contains(new SyncChange(GroupFields.Type, null, "security"), section.Changes);
        Assert.Equal(_f.Id("S1"), OrgGroupSettings.ReadMarker(section.Changes.Single(c => c.Field == GroupFields.Description).Desired));

        // 部のグループには、部の直属（boss）と配下の課の人（chief・member・solo）が入る。法人 B の人は入らない
        var department = plan.Items.Where(i => i.Action == SyncAction.AddGroupMember && i.GroupDisplayName == "JJ-第一営業部").ToList();
        Assert.Equal(new[] { "boss", "chief", "member", "solo" }.Select(_f.Object).Order(), department.Select(i => i.ObjectId).Order());
        Assert.All(department, i => Assert.Equal((null, new SyncChange(GroupFields.Member, "false", "true")), (i.GroupObjectId, Assert.Single(i.Changes))));
        Assert.Equal(["E0002", "E0003"], plan.Items.Where(i => i.Action == SyncAction.AddGroupMember && i.GroupDisplayName == "JJ-営業一課").Select(i => i.EmployeeNumber).Order());
        Assert.DoesNotContain(plan.Items, i => i.GroupDisplayName is { } n && n.Contains("出向先", StringComparison.Ordinal));
        Assert.Empty(plan.Warnings);
    }

    [Fact(DisplayName = "メンバーの範囲を直属だけにできる。兼務・出向は設定で入れる・入れないを選べる")]
    public void MemberScope_AndConcurrent()
    {
        var direct = Plan(Settings("""{ "legalEntities": ["A"], "orgGroups": { "prefix": "JJ-", "members": "direct" } }"""));
        Assert.Equal([_f.Object("boss")], direct.Items.Where(i => i.Action == SyncAction.AddGroupMember && i.GroupDisplayName == "JJ-第一営業部").Select(i => i.ObjectId));

        // boss を出向先（法人 B）に出向させる。法人 B のグループに入る（主務の法人 A にも残る）
        _f.Secondment("boss", "B1", "部長");
        var all = Settings("""{ "orgGroups": { "prefix": "JJ-" } }""");
        Assert.Contains(_f.Object("boss"), Plan(all).Items.Where(i => i.Action == SyncAction.AddGroupMember && i.GroupDisplayName == "JJ-出向先部").Select(i => i.ObjectId));
        var without = Settings("""{ "orgGroups": { "prefix": "JJ-", "includeSecondment": false } }""");
        Assert.DoesNotContain(_f.Object("boss"), Plan(without).Items.Where(i => i.Action == SyncAction.AddGroupMember && i.GroupDisplayName == "JJ-出向先部").Select(i => i.ObjectId));
        Assert.Contains(_f.Object("boss"), Plan(without).Items.Where(i => i.Action == SyncAction.AddGroupMember && i.GroupDisplayName == "JJ-第一営業部").Select(i => i.ObjectId));
    }

    [Fact(DisplayName = "印の付いたグループは作り直さず、メンバーの差分だけを出す（外すのは jin-ji の人に対応付いたメンバーだけ）")]
    public void OwnedGroup_MemberDiff()
    {
        var stranger = _f.Object("service-account");
        var groupId = Owned("S1", "JJ-営業一課", [_f.Object("chief"), _f.Object("solo"), stranger], description: $"営業一課（jin-ji が管理。手で変更しないでください） {OrgGroupSettings.Marker(_f.Id("S1"))}");

        var items = Plan().Items.Where(i => i.GroupDisplayName == "JJ-営業一課").ToList();

        Assert.DoesNotContain(items, i => i.Action is SyncAction.CreateGroup or SyncAction.UpdateGroup);
        var add = Assert.Single(items, i => i.Action == SyncAction.AddGroupMember);
        Assert.Equal((_f.Object("member"), groupId, SyncPriority.Normal), (add.ObjectId, add.GroupObjectId, add.Priority));
        var remove = Assert.Single(items, i => i.Action == SyncAction.RemoveGroupMember);
        Assert.Equal((_f.Object("solo"), "E0004", groupId, SyncPriority.High, true), (remove.ObjectId, remove.EmployeeNumber, remove.GroupObjectId, remove.Priority, remove.InScript));
        Assert.Equal(new SyncChange(GroupFields.Member, "true", "false"), Assert.Single(remove.Changes));
        var note = Assert.Single(items, i => i.Action == SyncAction.GroupNote);
        Assert.Equal(("unknown_members", false), (note.Basis.Code, note.InScript));

        // 設定で、対応付かないメンバーも外せる
        var strict = Plan(Settings("""{ "legalEntities": ["A"], "orgGroups": { "prefix": "JJ-", "removeUnknownMembers": true } }""")).Items;
        Assert.Contains(strict, i => i.Action == SyncAction.RemoveGroupMember && i.ObjectId == stranger && i.PersonId is null);
    }

    [Fact(DisplayName = "設定で除外したユーザーは、加えも外しもしない。退職者はメンバーから外す")]
    public void Excluded_AndRetired()
    {
        Owned("S1", "JJ-営業一課", [_f.Object("chief"), _f.Object("solo")]);
        var settings = Settings("""{ "legalEntities": ["A"], "orgGroups": { "prefix": "JJ-" }, "exclude": [{ "employeeNumber": "E0004" }, { "employeeNumber": "E0003" }] }""");

        var items = Plan(settings).Items.Where(i => i.GroupDisplayName == "JJ-営業一課").ToList();
        Assert.DoesNotContain(items, i => i.Action is SyncAction.AddGroupMember or SyncAction.RemoveGroupMember);

        _f.Person("chief", "E0002", "髙橋", "一郎", "S1", "課長", terminated: SyncFixture.Today.AddDays(-1));
        var remove = Assert.Single(Plan().Items, i => i.Action == SyncAction.RemoveGroupMember && i.GroupDisplayName == "JJ-営業一課" && i.ObjectId == _f.Object("chief"));
        Assert.Equal(SyncPriority.High, remove.Priority);
    }

    [Fact(DisplayName = "同じ名前の印の無いグループがあれば、作らずに衝突として出す（そのグループは変更しない）")]
    public void NameConflict_IsNotTouched()
    {
        var existing = _f.Object("existing-group");
        _groups.Add(new() { ["id"] = existing, ["displayName"] = "jj-営業一課", ["mailNickname"] = "sales1", ["description"] = "情シスが作ったグループ", ["members"] = Array.Empty<object>() });

        var items = Plan().Items.Where(i => i.GroupDisplayName == "JJ-営業一課").ToList();

        var conflict = Assert.Single(items);
        Assert.Equal((SyncAction.GroupConflict, SyncPriority.High, false, existing, "name_conflict"), (conflict.Action, conflict.Priority, conflict.InScript, conflict.ObjectId, conflict.Basis.Code));

        // 接頭辞を変えれば作れる
        Assert.Contains(Plan(Settings("""{ "legalEntities": ["A"], "orgGroups": { "prefix": "組織-" } }""")).Items, i => i.Action == SyncAction.CreateGroup && i.GroupDisplayName == "組織-営業一課");
    }

    [Fact(DisplayName = "組織の改称は表示名の更新にする。廃止された組織のグループは削除せず、廃止の印を付ける。メンバーは残し、退職した人だけを外す")]
    public void Rename_AndRetire()
    {
        var renamed = Owned("S2", "JJ-旧営業二課", [_f.Object("solo")]);
        var goneOrg = Guid.CreateVersion7();
        var gone = _f.Object("group:gone");
        _groups.Add(new()
        {
            ["id"] = gone,
            ["displayName"] = "JJ-企画課",
            ["description"] = $"企画課 {OrgGroupSettings.Marker(goneOrg)}",
            ["members"] = new[] { new Dictionary<string, string> { ["id"] = _f.Object("member") }, new Dictionary<string, string> { ["id"] = _f.Object("unknown") } },
        });

        var plan = Plan();

        var update = Assert.Single(plan.Items, i => i.Action == SyncAction.UpdateGroup);
        Assert.Equal((renamed, renamed, true), (update.ObjectId, update.GroupObjectId, update.InScript));
        Assert.Contains(new SyncChange(GroupFields.DisplayName, "JJ-旧営業二課", "JJ-営業二課"), update.Changes);
        var retire = Assert.Single(plan.Items, i => i.Action == SyncAction.RetireGroup);
        Assert.Equal((gone, true, "org_abolished"), (retire.ObjectId, retire.InScript, retire.Basis.Code));
        // 既にあるグループの項目は、計画時の印の組織 ID を持つ（廃止された組織では org_unit への参照が無いので、これで照合する）
        Assert.Equal((null, goneOrg, _f.Id("S2")), (retire.OrgUnitId, retire.GroupMarkerOrgUnitId, update.GroupMarkerOrgUnitId));
        Assert.All(plan.Items, i => Assert.Equal(i.InScript && i.GroupObjectId is not null, i.GroupMarkerOrgUnitId is not null));
        Assert.Contains(new SyncChange(GroupFields.DisplayName, "JJ-企画課", "【廃止】JJ-企画課"), retire.Changes);
        // 在籍している人（member）と、jin-ji の人に対応付かないメンバーは残す（引き継ぎ待ち）
        Assert.DoesNotContain(plan.Items, i => i.Action == SyncAction.RemoveGroupMember && i.GroupObjectId == gone);

        // 退職した人だけは外す
        _f.Person("member", "E0003", "鈴木", "次郎", "S1", null, terminated: SyncFixture.Today.AddDays(-1));
        var removed = Assert.Single(Plan().Items, i => i.Action == SyncAction.RemoveGroupMember && i.GroupObjectId == gone);
        Assert.Equal((_f.Object("member"), "member_retired", goneOrg), (removed.ObjectId, removed.Basis.Code, removed.GroupMarkerOrgUnitId));

        // 既に廃止の印が付いていれば、もう更新しない
        _groups[^1]["displayName"] = "【廃止】JJ-企画課";
        _groups[^1]["description"] = $"（廃止された組織） 企画課 {OrgGroupSettings.Marker(goneOrg)}";
        Assert.DoesNotContain(Plan().Items, i => i.Action == SyncAction.RetireGroup);
    }

    [Fact(DisplayName = "Microsoft 365 グループでは組織長をオーナーにし、設定があれば Teams のチームを作る（オーナーがいなければ作らない）")]
    public void Microsoft365_OwnerAndTeam()
    {
        var settings = Settings("""{ "legalEntities": ["A"], "orgGroups": { "prefix": "JJ-", "groupType": "microsoft365", "createTeam": true } }""");

        var plan = Plan(settings);

        Assert.Contains(new SyncChange(GroupFields.Type, null, "microsoft365"), plan.Items.First(i => i.Action == SyncAction.CreateGroup).Changes);
        var owner = Assert.Single(plan.Items, i => i.Action == SyncAction.AddGroupOwner && i.GroupDisplayName == "JJ-営業一課");
        Assert.Equal(_f.Object("chief"), owner.ObjectId);
        Assert.Contains(plan.Items, i => i.Action == SyncAction.CreateTeam && i.GroupDisplayName == "JJ-営業一課" && i.InScript);
        // 営業二課は組織長がいないので、チームは作らず参考に出す
        Assert.DoesNotContain(plan.Items, i => i.Action == SyncAction.CreateTeam && i.GroupDisplayName == "JJ-営業二課");
        Assert.Contains(plan.Items, i => i.Action == SyncAction.GroupNote && i.Basis.Code == "team_without_owner" && i.GroupDisplayName == "JJ-営業二課");

        // 既にオーナーとチームがあれば何もしない。種類が違うグループは衝突として出す
        Owned("S1", "JJ-営業一課", [_f.Object("chief"), _f.Object("member")], unified: true, owners: [_f.Object("chief")], team: true,
            description: $"営業一課（jin-ji が管理。手で変更しないでください） {OrgGroupSettings.Marker(_f.Id("S1"))}");
        Owned("S2", "JJ-営業二課", [_f.Object("solo")], unified: false);
        var second = Plan(settings).Items;
        Assert.DoesNotContain(second, i => i.GroupDisplayName == "JJ-営業一課");
        Assert.Contains(second, i => i.Action == SyncAction.GroupConflict && i.Basis.Code == "type_mismatch" && i.GroupDisplayName == "JJ-営業二課");
    }

    [Fact(DisplayName = "Entra のアカウントの対応が無い人は加えられないので、参考として 1 人 1 件で出す")]
    public void MemberWithoutAccount()
    {
        _f.Person("newcomer", "E0009", "新人", "太郎", "S1", null);

        var plan = Plan();

        var note = Assert.Single(plan.Items, i => i.Basis.Code == "member_without_account");
        Assert.Equal((SyncAction.GroupNote, "E0009", false, null), (note.Action, note.EmployeeNumber, note.InScript, note.ObjectId));
        Assert.DoesNotContain(plan.Items, i => i.Action == SyncAction.AddGroupMember && i.EmployeeNumber == "E0009");
    }

    [Fact(DisplayName = "設定: 接頭辞は必須。チームは Microsoft 365 グループだけ。あるべき状態のハッシュは所属が変わると変わる")]
    public void Settings_AndFingerprint()
    {
        Assert.Throws<FormatException>(() => Settings("""{ "orgGroups": { } }"""));
        Assert.Throws<FormatException>(() => Settings("""{ "orgGroups": { "prefix": "JJ-", "createTeam": true } }"""));
        Assert.Throws<FormatException>(() => Settings("""{ "orgGroups": { "prefix": "JJ-", "mailNicknamePrefix": "日本語" } }"""));
        Assert.Throws<FormatException>(() => new EntraGroupPlanner().Plan(new GroupSyncPlanInput(_f.Snapshot(), _f.Links, [], SyncSettings.Default, SyncFixture.Today)));
        // orgGroups を足しても、ユーザーの同期の設定のハッシュ（orgGroups なし）は変わらない
        Assert.Equal(SyncSettings.Default.Sha256(), SyncSettings.Parse(Encoding.UTF8.GetBytes("{}")).Sha256());

        var planner = new EntraGroupPlanner();
        var before = planner.Fingerprint(_f.Snapshot(), _f.Links, Settings(), SyncFixture.Today);
        Assert.Equal(before, Plan().JinjiFingerprint);
        _f.Person("solo", "E0004", "佐藤", "花子", "S1", null);
        Assert.NotEqual(before, planner.Fingerprint(_f.Snapshot(), _f.Links, Settings(), SyncFixture.Today));
    }
}
