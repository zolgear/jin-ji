using JinJi.Application.Audit;
using JinJi.Application.FieldManagement;
using JinJi.Application.People;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Groups;

/// <summary>
/// 規程外の単位（チーム・プロジェクト・委員会）のグループ（ADR-0014）: 一覧・参照・作成・属性の変更・終了・公開範囲と管理対象・メンバー。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>作成と変更は承認なしで、すぐに反映する。作成は「人」なら誰でもでき、作成者が最初のオーナーになる</item>
/// <item>変更できるのは、オーナー・所管組織の範囲を持つ人（ADR-0013 決定 3）・組織管理者・人事管理者。メンバーは自分を外せる（管理対象のグループを除く）</item>
/// <item>管理対象（関係スコープ same_group に使える）にするのは組織管理者・人事管理者だけ。管理対象のグループのオーナーの指名は、
/// 組織管理者・人事管理者・所管組織の範囲を持つ人だけ</item>
/// <item>AI エージェント・社内システムは参照だけ（変更はアクセス権に効く公開情報なので、エージェントは素案の下書きまで。決定 7）</item>
/// <item>メンバーの一覧は、項目 groups が見える人だけを返す（ポリシー・クライアントの項目上限で狭められる。決定 5）</item>
/// <item>操作は監査ログに残す（拒否・失敗も）。最終的な線は DB（RLS とトリガー。0022）も引く</item>
/// </list>
/// </remarks>
public sealed class GroupService
{
    public const string ReadAction = "group.read";
    public const string CreateAction = "group.create";
    public const string UpdateAction = "group.update";
    public const string CloseAction = "group.close";
    public const string SettingsAction = "group.settings";
    public const string MemberAddAction = "group.member.add";
    public const string MemberChangeAction = "group.member.change";
    public const string MemberRemoveAction = "group.member.remove";

    private const string TargetType = "org_group";

    private readonly IGroupStore _store;
    private readonly FieldManagementService _scopes;
    private readonly PersonDirectoryService _people;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly IOrgDataChangeNotifier? _changes;

    public GroupService(
        IGroupStore store,
        FieldManagementService scopes,
        PersonDirectoryService people,
        IOrgSnapshotProvider snapshots,
        AuditWriter audit,
        TimeProvider timeProvider,
        IOrgDataChangeNotifier? changes = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _scopes = scopes;
        _people = people;
        _snapshots = snapshots;
        _audit = audit;
        _timeProvider = timeProvider;
        _changes = changes;
    }

    /// <summary>今日有効な、閲覧者に見えるグループ（名前の順）。人で絞るときは、その人の項目 groups が見えなければ空。</summary>
    public async Task<IReadOnlyList<GroupRecord>> ListAsync(ViewerContext viewer, GroupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(query);
        if (query.PersonId is { } person && !(await _people.WithVisibleFieldAsync(viewer, [person], PersonFields.Groups, cancellationToken)).Contains(person))
        {
            await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, [], cancellationToken, TargetType);
            return [];
        }

        var groups = await _store.ListAsync(viewer, query, Today, cancellationToken);
        await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, query.PersonId is { } p ? [p] : [], cancellationToken, TargetType);
        return groups;
    }

    /// <summary>グループと今日のメンバー（項目 groups が見える人だけ）。</summary>
    public async Task<GroupDetail> GetAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var detail = await _store.FindAsync(viewer, id, Today, cancellationToken);
        if (detail is null)
        {
            await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw NotFound();
        }

        var visible = await _people.WithVisibleFieldAsync(viewer, [.. detail.Members.Select(m => m.PersonId)], PersonFields.Groups, cancellationToken);
        var members = detail.Members.Where(m => visible.Contains(m.PersonId)).ToList();
        await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, [.. members.Select(m => m.PersonId)], cancellationToken, TargetType, id);
        return detail with { Members = members };
    }

    /// <summary>グループを作る（作成者が最初のオーナー）。</summary>
    public async Task<GroupDetail> CreateAsync(ViewerContext viewer, GroupCreateInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(input);
        var person = await RequireUserAsync(viewer, CreateAction, null, cancellationToken);
        var from = input.ValidFrom ?? Today;
        try
        {
            if (from < Today)
            {
                throw Invalid("開始日は今日以降にしてください。");
            }

            var normalized = input with { Name = Name(input.Name) };
            if (normalized.OwnerOrgUnitId is { } unit && (await TodayAsync(viewer, cancellationToken)).FindUnit(unit) is null)
            {
                throw new GroupException(GroupError.NotFound, "所管組織が見つかりません。");
            }

            var id = Guid.CreateVersion7();
            await _store.CreateAsync(viewer, id, normalized, from, cancellationToken);
            _changes?.NotifyChanged();
            await _audit.RecordAsync(viewer, CreateAction, AuditOutcome.Success, [person], cancellationToken, TargetType, id);
            return await _store.FindAsync(viewer, id, from, cancellationToken) ?? throw new InvalidOperationException("作ったグループが見つかりません。");
        }
        catch (GroupException ex)
        {
            await _audit.RecordAsync(viewer, CreateAction, Outcome(ex), [], cancellationToken, TargetType);
            throw;
        }
    }

    /// <summary>今日から属性（名称・種別・所管組織）を変える。所管組織を変えるのは、今の所管組織の管理者か組織管理者・人事管理者だけ。</summary>
    public Task<GroupDetail> UpdateAsync(ViewerContext viewer, Guid id, GroupUpdateInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return EditAsync(viewer, UpdateAction, id, [], cancellationToken, async (detail, access) =>
        {
            if (input.OwnerOrgUnitId is not null || input.ClearOwnerOrgUnit)
            {
                if (!access.Admin)
                {
                    throw Forbidden("所管組織を変えられるのは、所管組織の範囲を持つ人・組織管理者・人事管理者だけです。");
                }

                if (input.OwnerOrgUnitId is { } unit && !access.Scope.Covers(unit))
                {
                    throw Forbidden("新しい所管組織の範囲を持ちません。");
                }
            }

            var normalized = input with { Name = input.Name is null ? null : Name(input.Name) };
            return await _store.ReviseAsync(viewer, id, normalized, Today, cancellationToken);
        });
    }

    /// <summary>その日（既定は今日）でグループを終える。</summary>
    public Task<GroupDetail> CloseAsync(ViewerContext viewer, Guid id, DateOnly? date, CancellationToken cancellationToken) =>
        EditAsync(viewer, CloseAction, id, [], cancellationToken, async (_, _) =>
        {
            var end = date ?? Today;
            return end < Today ? throw Invalid("終了日は今日以降にしてください。") : await _store.CloseAsync(viewer, id, end, cancellationToken);
        });

    /// <summary>公開範囲（編集できる人）・管理対象（組織管理者・人事管理者）を変える。</summary>
    public Task<GroupDetail> SetSettingsAsync(ViewerContext viewer, Guid id, GroupVisibility? visibility, bool? managed, CancellationToken cancellationToken) =>
        EditAsync(viewer, SettingsAction, id, [], cancellationToken, async (_, _) =>
        {
            if (managed is not null && !viewer.ManagesAllOrgs)
            {
                throw Forbidden("管理対象にする・外すのは、組織管理者・人事管理者だけです。");
            }

            return await _store.SetSettingsAsync(viewer, id, visibility, managed, cancellationToken);
        });

    /// <summary>メンバーを加える（期間は今日以降に始まる）。</summary>
    public Task<GroupDetail> AddMemberAsync(
        ViewerContext viewer,
        Guid id,
        Guid personId,
        GroupRole role,
        DateOnly? from,
        DateOnly? until,
        CancellationToken cancellationToken) =>
        EditAsync(viewer, MemberAddAction, id, [personId], cancellationToken, async (detail, access) =>
        {
            var start = from ?? Today;
            if (start < Today || (until is { } end && end <= start))
            {
                throw Invalid("期間は今日以降に始まり、終わりは始まりより後にしてください。");
            }

            if ((await TodayAsync(viewer, cancellationToken)).FindPerson(personId) is null)
            {
                throw new GroupException(GroupError.NotFound, "人が見つかりません。");
            }

            RequireOwnerAppointment(detail, access, role);
            return await _store.AddMemberAsync(viewer, id, personId, role, start, until, cancellationToken)
                ? true
                : throw new GroupException(GroupError.Conflict, "この人は、その期間に既にメンバーです。");
        });

    /// <summary>今日からメンバーの役割を変える。</summary>
    public Task<GroupDetail> ChangeRoleAsync(ViewerContext viewer, Guid id, Guid personId, GroupRole role, CancellationToken cancellationToken) =>
        EditAsync(viewer, MemberChangeAction, id, [personId], cancellationToken, async (detail, access) =>
        {
            RequireOwnerAppointment(detail, access, role);
            return await _store.ChangeRoleAsync(viewer, id, personId, role, Today, cancellationToken);
        });

    /// <summary>今日でメンバーから外す。編集できる人のほか、本人は自分を外せる（管理対象のグループを除く）。</summary>
    public Task<GroupDetail> RemoveMemberAsync(ViewerContext viewer, Guid id, Guid personId, CancellationToken cancellationToken) =>
        EditAsync(
            viewer,
            MemberRemoveAction,
            id,
            [personId],
            cancellationToken,
            (_, _) => _store.EndMemberAsync(viewer, id, personId, Today, cancellationToken),
            allowSelf: personId);

    private async Task<GroupDetail> EditAsync(
        ViewerContext viewer,
        string action,
        Guid id,
        IReadOnlyList<Guid> subjects,
        CancellationToken cancellationToken,
        Func<GroupDetail, Access, Task<bool>> edit,
        Guid? allowSelf = null)
    {
        var person = await RequireUserAsync(viewer, action, id, cancellationToken);
        try
        {
            var detail = await _store.FindAsync(viewer, id, Today, cancellationToken) ?? throw NotFound();
            var access = await AccessAsync(viewer, person, detail, cancellationToken);
            var self = allowSelf == person && !detail.Group.Managed;
            if (!access.Edit && !self)
            {
                throw Forbidden("このグループを変えられるのは、オーナー・所管組織の範囲を持つ人・組織管理者・人事管理者です。");
            }

            if (!await edit(detail, access))
            {
                throw new GroupException(GroupError.Conflict, "対象の行が見つからないか、状態が変わりました。");
            }
        }
        catch (GroupException ex)
        {
            await _audit.RecordAsync(viewer, action, Outcome(ex), subjects, cancellationToken, TargetType, id);
            throw;
        }

        _changes?.NotifyChanged();
        await _audit.RecordAsync(viewer, action, AuditOutcome.Success, subjects, cancellationToken, TargetType, id);
        return await _store.FindAsync(viewer, id, Today, cancellationToken) ?? throw NotFound();
    }

    /// <summary>管理対象のグループのオーナーの指名は、管理者（組織管理者・人事管理者・所管組織の範囲を持つ人）だけ。</summary>
    private static void RequireOwnerAppointment(GroupDetail detail, Access access, GroupRole role)
    {
        if (role == GroupRole.Owner && detail.Group.Managed && !access.Admin)
        {
            throw Forbidden("管理対象のグループのオーナーを指名できるのは、所管組織の範囲を持つ人・組織管理者・人事管理者だけです。");
        }
    }

    private async Task<Access> AccessAsync(ViewerContext viewer, Guid person, GroupDetail detail, CancellationToken cancellationToken)
    {
        var scope = await _scopes.ScopeAsync(viewer, Today, cancellationToken);
        var admin = scope.ManagesAll || (detail.Group.OwnerOrgUnitId is { } unit && scope.Covers(unit));
        var owner = detail.Members.Any(m => m.PersonId == person && m.Role == GroupRole.Owner && m.From <= Today);
        return new Access(admin || owner, admin, scope);
    }

    private async Task<Guid> RequireUserAsync(ViewerContext viewer, string action, Guid? id, CancellationToken cancellationToken)
    {
        if (viewer.Actor != ActorType.User || viewer.PersonId is not { } person)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, id);
            throw Forbidden("グループを変えられるのは、ログインした人だけです（AI エージェントは素案の下書きで扱います）。");
        }

        return person;
    }

    private Task<OrgSnapshot> TodayAsync(ViewerContext viewer, CancellationToken cancellationToken) =>
        _snapshots.LoadAsync(viewer, Today, Today, cancellationToken);

    private DateOnly Today => BusinessDate.Today(_timeProvider);

    private static string Name(string name)
    {
        var value = name?.Trim();
        return string.IsNullOrEmpty(value) || value.Length > 200 || value.Any(char.IsControl)
            ? throw Invalid("名称は 1〜200 文字で、改行などの制御文字を含めずに指定してください。")
            : value;
    }

    private static AuditOutcome Outcome(GroupException ex) => ex.Error == GroupError.Forbidden ? AuditOutcome.Denied : AuditOutcome.Failed;

    private static GroupException NotFound() => new(GroupError.NotFound, "グループが見つかりません。");

    private static GroupException Forbidden(string message) => new(GroupError.Forbidden, message);

    private static GroupException Invalid(string message) => new(GroupError.Invalid, message);

    /// <param name="Edit">変更できる（管理者かオーナー）。</param>
    /// <param name="Admin">管理者（組織管理者・人事管理者・所管組織の範囲を持つ人）。</param>
    private sealed record Access(bool Edit, bool Admin, RegistrationScope Scope);
}
