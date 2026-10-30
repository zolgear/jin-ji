namespace JinJi.Domain.Organization;

/// <summary>グループの種別（DB の org_group_version.kind。ADR-0014）。</summary>
public enum GroupKind
{
    Team,
    Project,
    Committee,
    Other,
}

/// <summary>グループの中の役割（DB の org_group_member.role）。</summary>
public enum GroupRole
{
    Owner,
    Lead,
    Member,
}

/// <summary>グループの公開範囲（DB の org_group.visibility）。</summary>
public enum GroupVisibility
{
    /// <summary>全社公開（名前・種別・所管組織・メンバーと役割）。</summary>
    All,

    /// <summary>メンバー・組織管理者・人事管理者だけが存在を見られる。</summary>
    Members,
}

/// <summary>ある時点で有効なグループ（規程外のチーム・プロジェクトなど。組織のツリーには入らない）。</summary>
/// <param name="OwnerOrgUnitId">所管組織（任意）。</param>
/// <param name="Managed">アクセス権の管理の対象か（関係スコープ same_group に使える）。</param>
public sealed record OrgGroup(Guid Id, string Name, GroupKind Kind, Guid? OwnerOrgUnitId, GroupVisibility Visibility, bool Managed);

/// <summary>ある時点で有効なグループのメンバー。</summary>
/// <param name="Until">期間の終わり（この日を含まない）。null は終わりなし。</param>
public sealed record GroupMembership(Guid GroupId, Guid PersonId, GroupRole Role, DateOnly From, DateOnly? Until);

/// <summary>
/// ある時点のグループとメンバー（スナップショットの一部。ADR-0014）。閲覧者の区分で共有するスナップショットには、全社公開のグループだけが入る。
/// </summary>
public sealed class GroupMap
{
    private static readonly IReadOnlyList<GroupMembership> NoMemberships = [];

    private readonly Dictionary<Guid, OrgGroup> _groups;
    private readonly Dictionary<Guid, IReadOnlyList<GroupMembership>> _byGroup;
    private readonly Dictionary<Guid, IReadOnlyList<GroupMembership>> _byPerson;

    public GroupMap(IEnumerable<OrgGroup> groups, IEnumerable<GroupMembership> memberships)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(memberships);
        _groups = groups.ToDictionary(g => g.Id);
        var valid = memberships.Where(m => _groups.ContainsKey(m.GroupId)).ToList();
        _byGroup = valid.GroupBy(m => m.GroupId).ToDictionary(g => g.Key, g => (IReadOnlyList<GroupMembership>)[.. g.OrderBy(m => m.Role).ThenBy(m => m.PersonId)]);
        _byPerson = valid.GroupBy(m => m.PersonId).ToDictionary(g => g.Key, g => (IReadOnlyList<GroupMembership>)[.. g.OrderBy(m => m.GroupId)]);
    }

    public static GroupMap Empty { get; } = new([], []);

    public IReadOnlyCollection<OrgGroup> Groups => _groups.Values;

    public OrgGroup? Find(Guid id) => _groups.GetValueOrDefault(id);

    public IReadOnlyList<GroupMembership> MembersOf(Guid groupId) => _byGroup.GetValueOrDefault(groupId) ?? NoMemberships;

    public IReadOnlyList<GroupMembership> GroupsOf(Guid personId) => _byPerson.GetValueOrDefault(personId) ?? NoMemberships;

    /// <summary>2 人が同じ管理対象のグループのメンバーか（関係スコープ same_group）。</summary>
    public bool ShareManagedGroup(Guid a, Guid b)
    {
        var mine = GroupsOf(a).Where(m => _groups[m.GroupId].Managed).Select(m => m.GroupId).ToHashSet();
        return mine.Count > 0 && GroupsOf(b).Any(m => mine.Contains(m.GroupId));
    }
}

/// <summary>グループの種別・役割・公開範囲のコード値（DB・API の値）。</summary>
public static class GroupCodes
{
    public static string Of(GroupKind kind) => kind switch
    {
        GroupKind.Team => "team",
        GroupKind.Project => "project",
        GroupKind.Committee => "committee",
        GroupKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string Of(GroupRole role) => role switch
    {
        GroupRole.Owner => "owner",
        GroupRole.Lead => "lead",
        GroupRole.Member => "member",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public static string Of(GroupVisibility visibility) => visibility == GroupVisibility.Members ? "members" : "all";

    public static GroupKind ParseKind(string code) =>
        TryParseKind(code, out var kind) ? kind : throw new ArgumentException($"未知のグループの種別: {code}", nameof(code));

    public static GroupRole ParseRole(string code) =>
        TryParseRole(code, out var role) ? role : throw new ArgumentException($"未知のグループの役割: {code}", nameof(code));

    public static bool TryParseKind(string? code, out GroupKind kind)
    {
        foreach (var value in Enum.GetValues<GroupKind>())
        {
            if (Of(value) == code)
            {
                kind = value;
                return true;
            }
        }

        kind = default;
        return false;
    }

    public static bool TryParseRole(string? code, out GroupRole role)
    {
        foreach (var value in Enum.GetValues<GroupRole>())
        {
            if (Of(value) == code)
            {
                role = value;
                return true;
            }
        }

        role = default;
        return false;
    }

    public static bool TryParseVisibility(string? code, out GroupVisibility visibility)
    {
        visibility = code == "members" ? GroupVisibility.Members : GroupVisibility.All;
        return code is "all" or "members";
    }
}
