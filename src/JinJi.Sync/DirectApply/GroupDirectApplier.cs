using JinJi.Application.Sync;
using JinJi.Policy;

namespace JinJi.Sync.DirectApply;

/// <summary>
/// グループの計画の直接反映（ADR-0015 決定 8）。承認済みの計画の項目を Graph API で適用し、項目ごとの結果を記録する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>順序: グループの作成 → 更新・廃止の印 → オーナー → メンバーの追加 → メンバーの削除 → チーム（チームにはオーナーが要る）</item>
/// <item>どの項目も、変更の前に今の状態を読む。既にあるべき状態なら何もしない（冪等）。計画の後に Entra 側が変わっていればスキップして記録する</item>
/// <item>既にあるグループに書き込む前に、そのグループの説明の印が計画時の組織 ID のままかを確かめる（ADR-0015 決定 3）。
/// 印が外された・別の組織に書き換えられたグループには、更新・メンバー・オーナー・チームのどれも書き込まない。
/// 同じ計画で作った（または印で作成済みと確かめた）グループは確かめ直さない</item>
/// <item>グループの作成は、同じ mailNickname のグループが既にあれば作らない: jin-ji の印があれば作成済みとして続け、印が無ければ失敗にする</item>
/// <item>同じ計画で作るグループのメンバー・オーナー・チームは、作ったグループの ID を使う。ドライランでは作らないので「反映する予定」になる</item>
/// <item>エラーでも続行し、項目ごとに記録する。削除はしない</item>
/// </list>
/// </remarks>
public sealed class GroupDirectApplier
{
    private static readonly SyncAction[] Order =
    [
        SyncAction.CreateGroup, SyncAction.UpdateGroup, SyncAction.RetireGroup, SyncAction.AddGroupOwner, SyncAction.AddGroupMember, SyncAction.RemoveGroupMember, SyncAction.CreateTeam,
    ];

    private readonly SyncPlanService _service;
    private readonly IGroupDirectoryClient _client;
    private readonly TimeProvider _timeProvider;

    public GroupDirectApplier(SyncPlanService service, IGroupDirectoryClient client, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _service = service;
        _client = client;
        _timeProvider = timeProvider;
    }

    public async Task<SyncResultImport> ApplyAsync(ViewerContext viewer, Guid planId, bool whatIf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var target = await _service.PrepareApplyAsync(viewer, planId, cancellationToken);
        if (target.Plan.TargetSystem != SyncPlanService.TargetSystemEntraGroups)
        {
            throw new InvalidOperationException($"計画 {planId} はグループの計画ではありません。");
        }

        var startedAt = _timeProvider.GetUtcNow();
        var created = new Dictionary<Guid, string>();
        var markers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var results = new List<SyncLogEntry>();
        foreach (var item in target.Items.Where(i => Order.Contains(i.Action)).OrderBy(i => Array.IndexOf(Order, i.Action)).ThenBy(i => i.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ApplyItemAsync(item, whatIf, created, markers, cancellationToken));
        }

        return await _service.RecordDirectRunAsync(viewer, target, whatIf, results, startedAt, cancellationToken);
    }

    private async Task<SyncLogEntry> ApplyItemAsync(SyncPlanItem item, bool whatIf, Dictionary<Guid, string> created, Dictionary<string, string?> markers,
        CancellationToken cancellationToken)
    {
        var observed = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            if (item.Action == SyncAction.CreateGroup)
            {
                return await CreateAsync(item, whatIf, created, observed, cancellationToken);
            }

            var groupId = item.GroupObjectId ?? (item.OrgUnitId is { } org ? created.GetValueOrDefault(org) : null);
            if (groupId is null)
            {
                // 同じ計画で作るグループ。ドライランでは作っていない
                return whatIf
                    ? Entry(SyncOutcome.WouldApply, null)
                    : Entry(SyncOutcome.Failed, "対象のグループが作られていないので、反映できません。");
            }

            if (item.GroupObjectId is not null && await MarkerProblemAsync(item, groupId, markers, cancellationToken) is { } problem)
            {
                return Entry(SyncOutcome.SkippedPrecondition, problem);
            }

            switch (item.Action)
            {
                case SyncAction.UpdateGroup or SyncAction.RetireGroup:
                    return await UpdateAsync(item, groupId, whatIf, observed, cancellationToken);
                case SyncAction.AddGroupMember or SyncAction.RemoveGroupMember:
                {
                    var userId = item.ObjectId ?? throw new InvalidOperationException("ユーザーのオブジェクト ID がありません。");
                    var want = item.Action == SyncAction.AddGroupMember;
                    var isMember = await _client.IsMemberAsync(groupId, userId, cancellationToken);
                    observed[GroupFields.Member] = isMember ? "true" : "false";
                    if (isMember == want)
                    {
                        return Entry(SyncOutcome.AlreadyApplied, null);
                    }

                    if (whatIf)
                    {
                        return Entry(SyncOutcome.WouldApply, null);
                    }

                    if (want)
                    {
                        await _client.AddMemberAsync(groupId, userId, cancellationToken);
                    }
                    else
                    {
                        await _client.RemoveMemberAsync(groupId, userId, cancellationToken);
                    }

                    return Entry(SyncOutcome.Succeeded, null);
                }

                case SyncAction.AddGroupOwner:
                {
                    var userId = item.ObjectId ?? throw new InvalidOperationException("ユーザーのオブジェクト ID がありません。");
                    var isOwner = (await _client.ListOwnersAsync(groupId, cancellationToken)).Contains(userId, StringComparer.OrdinalIgnoreCase);
                    observed[GroupFields.Owner] = isOwner ? "true" : "false";
                    if (isOwner)
                    {
                        return Entry(SyncOutcome.AlreadyApplied, null);
                    }

                    if (whatIf)
                    {
                        return Entry(SyncOutcome.WouldApply, null);
                    }

                    await _client.AddOwnerAsync(groupId, userId, cancellationToken);
                    return Entry(SyncOutcome.Succeeded, null);
                }

                case SyncAction.CreateTeam:
                {
                    var group = await _client.GetGroupAsync(groupId, cancellationToken) ?? throw new InvalidOperationException("グループが見つかりません。");
                    observed[GroupFields.Team] = group.HasTeam == true ? "true" : "false";
                    if (group.HasTeam == true)
                    {
                        return Entry(SyncOutcome.AlreadyApplied, null);
                    }

                    if (!group.Unified)
                    {
                        return Entry(SyncOutcome.SkippedPrecondition, "Microsoft 365 グループではないので、チームを作れません。");
                    }

                    if (whatIf)
                    {
                        return Entry(SyncOutcome.WouldApply, null);
                    }

                    await _client.CreateTeamAsync(groupId, cancellationToken);
                    return Entry(SyncOutcome.Succeeded, null);
                }

                default:
                    return Entry(SyncOutcome.Failed, "直接反映できない操作です。");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return Entry(SyncOutcome.Failed, ex.Message);
        }

        SyncLogEntry Entry(SyncOutcome outcome, string? message) =>
            new(item.Id, outcome, new Dictionary<string, string?>(observed, StringComparer.Ordinal), message is null ? null : message[..Math.Min(message.Length, 1000)], _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// 既にあるグループの印が、計画時の組織 ID のままかを確かめる。問題があればその説明を、無ければ null を返す。
    /// グループごとに一度だけ読む（同じ実行の中の更新は印を変えない）。
    /// </summary>
    private async Task<string?> MarkerProblemAsync(SyncPlanItem item, string groupId, Dictionary<string, string?> markers, CancellationToken cancellationToken)
    {
        if (item.GroupMarkerOrgUnitId is not { } expected)
        {
            return "計画の項目に、グループの印の組織 ID がありません。jin-ji のグループと確かめられないので、書き込みません。";
        }

        // 印の組織 ID ごとに覚える（同じグループを指す項目でも、計画時の印が違えば別に確かめる）
        var key = $"{groupId}|{expected:D}";
        if (!markers.TryGetValue(key, out var problem))
        {
            var group = await _client.GetGroupAsync(groupId, cancellationToken) ?? throw new InvalidOperationException("グループが見つかりません。");
            var now = OrgGroupSettings.ReadMarker(group.Description);
            problem = now == expected
                ? null
                : now is null
                    ? "グループの説明に jin-ji の印がありません（計画の後に外されたか、jin-ji のグループではありません）。書き込みません。"
                    : "グループの説明の印が、計画のときと別の組織を指しています。書き込みません。";
            markers[key] = problem;
        }

        return problem;
    }

    private async Task<SyncLogEntry> CreateAsync(SyncPlanItem item, bool whatIf, Dictionary<Guid, string> created, Dictionary<string, string?> observed, CancellationToken cancellationToken)
    {
        SyncLogEntry Entry(SyncOutcome outcome, string? message) =>
            new(item.Id, outcome, new Dictionary<string, string?>(observed, StringComparer.Ordinal), message, _timeProvider.GetUtcNow());

        string Desired(string field) => item.Changes.FirstOrDefault(c => c.Field == field)?.Desired ?? throw new InvalidOperationException($"作成の項目に {field} がありません。");
        var orgUnitId = item.OrgUnitId ?? throw new InvalidOperationException("作成の項目に組織がありません。");
        var nickname = Desired(GroupFields.MailNickname);
        var existing = await _client.FindByMailNicknameAsync(nickname, cancellationToken);
        if (existing.FirstOrDefault(g => OrgGroupSettings.ReadMarker(g.Description) == orgUnitId) is { } mine)
        {
            observed[GroupFields.DisplayName] = mine.DisplayName;
            created[orgUnitId] = mine.ObjectId;
            return Entry(SyncOutcome.AlreadyApplied, null);
        }

        if (existing.Count > 0)
        {
            observed[GroupFields.MailNickname] = nickname;
            return Entry(SyncOutcome.SkippedPrecondition, "同じ mailNickname のグループが既にあります（jin-ji の印がありません）。作成しません。");
        }

        if (whatIf)
        {
            return Entry(SyncOutcome.WouldApply, null);
        }

        created[orgUnitId] = await _client.CreateGroupAsync(
            Desired(GroupFields.DisplayName), nickname, Desired(GroupFields.Description), Desired(GroupFields.Type) == EntraGroupPlanner.TypeCode(OrgGroupType.Microsoft365), cancellationToken);
        return Entry(SyncOutcome.Succeeded, null);
    }

    private async Task<SyncLogEntry> UpdateAsync(SyncPlanItem item, string groupId, bool whatIf, Dictionary<string, string?> observed, CancellationToken cancellationToken)
    {
        SyncLogEntry Entry(SyncOutcome outcome, string? message) =>
            new(item.Id, outcome, new Dictionary<string, string?>(observed, StringComparer.Ordinal), message, _timeProvider.GetUtcNow());

        var group = await _client.GetGroupAsync(groupId, cancellationToken) ?? throw new InvalidOperationException("グループが見つかりません。");
        observed[GroupFields.DisplayName] = group.DisplayName;
        observed[GroupFields.Description] = group.Description;
        var pending = new Dictionary<string, string?>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (var change in item.Changes)
        {
            var now = observed.GetValueOrDefault(change.Field);
            if (string.Equals(now ?? string.Empty, change.Desired ?? string.Empty, StringComparison.Ordinal))
            {
                continue;
            }

            if (change.CurrentKnown && !string.Equals(now ?? string.Empty, change.Current ?? string.Empty, StringComparison.Ordinal))
            {
                conflicts.Add(change.Field);
            }
            else
            {
                pending[change.Field == GroupFields.DisplayName ? "displayName" : "description"] = change.Desired;
            }
        }

        if (conflicts.Count > 0)
        {
            return Entry(SyncOutcome.SkippedPrecondition, "計画の後に Entra 側の値が変わったためスキップしました: " + string.Join(", ", conflicts));
        }

        if (pending.Count == 0)
        {
            return Entry(SyncOutcome.AlreadyApplied, null);
        }

        if (whatIf)
        {
            return Entry(SyncOutcome.WouldApply, null);
        }

        await _client.UpdateGroupAsync(groupId, pending, cancellationToken);
        return Entry(SyncOutcome.Succeeded, null);
    }
}
