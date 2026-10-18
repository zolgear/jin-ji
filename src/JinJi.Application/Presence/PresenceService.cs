using JinJi.Application.People;
using JinJi.Application.Workplace;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Workplace;
using JinJi.Policy;

namespace JinJi.Application.Presence;

/// <summary>その日の階の所在で、閲覧者に項目 presence が見える人（席番号は項目 seat も見えるときだけ）。</summary>
public sealed record PresenceItem(PersonView Person, LayoutSeat? Seat, LayoutZone? Zone);

/// <summary>所在のある席の状態（occupied = 閲覧者に見える人の所在 / unavailable = 見えない人の所在。予約・空きは持たない）。</summary>
public sealed record PresenceSeatState(LayoutSeat Seat, SeatStatus Status);

/// <summary>今日の階の所在（外部の提供に jin-ji の認可を適用したもの）。</summary>
public sealed record FloorPresence(DateOnly Date, Building Building, Floor Floor, IReadOnlyList<PresenceSeatState> Seats, IReadOnlyList<PresenceItem> Items);

/// <summary>
/// 当日の所在の表示（外部の提供 <see cref="IPresenceProvider"/> を注入したときだけ）。jin-ji は所在を保存せず、予約も受けない。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>今日（業務日付）だけ提供に問い合わせる。時点指定（過去・将来）では呼ばない</item>
/// <item>返った所在には jin-ji の認可を必ず適用する: 項目 presence の公開範囲（組み込みの規則。既定は本人と同じ部）・ポリシー・個人単位の例外・クライアントの項目の上限
/// （<c>*</c> は presence を含まない）。席番号は項目 seat も見えるときだけ。jin-ji の配置図に無い階・席・エリアの所在は捨てる</item>
/// <item>キャッシュ・永続化せず、変更フィード・Webhook・同期・監査ログに記録しない（応答は no-store）</item>
/// </list>
/// </remarks>
public sealed class PresenceService
{
    private readonly IPresenceProvider _provider;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly IPolicyRuleStore _policies;
    private readonly TimeProvider _timeProvider;
    private readonly PersonDirectoryOptions _options;

    public PresenceService(
        IPresenceProvider provider,
        IOrgSnapshotProvider snapshots,
        IPolicyRuleStore policies,
        TimeProvider timeProvider,
        PersonDirectoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options ?? new PersonDirectoryOptions();
        _options.Validate();
        _provider = provider;
        _snapshots = snapshots;
        _policies = policies;
        _timeProvider = timeProvider;
    }

    /// <summary>所在の提供があるか。</summary>
    public bool IsAvailable => _provider.IsAvailable;

    /// <summary>
    /// 今日の階の所在。提供が無ければ <see cref="PresenceUnavailableException"/>、階がその時点に無ければ null。
    /// </summary>
    public async Task<FloorPresence?> GetFloorAsync(ViewerContext viewer, Guid floorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (!_provider.IsAvailable)
        {
            throw new PresenceUnavailableException();
        }

        var today = BusinessDate.Today(_timeProvider);
        var current = viewer.WithoutDisclosureLead();
        var data = await _snapshots.LoadAsync(current, today, today, cancellationToken);
        var map = data.Workplace;
        if (map.FindFloor(floorId) is not { } floor || map.FindBuilding(floor.BuildingId) is not { } building)
        {
            return null;
        }

        var facts = Validated(map, await _provider.GetAsync(new PresenceQuery(today, floorId, []), cancellationToken), today)
            .Where(f => f.FloorId == floorId)
            .ToList();
        var policies = PresencePolicy.WithBuiltInRule(await _policies.LoadActiveAsync(current, cancellationToken), _options.PresenceVisibilityScope);
        var projector = new PersonProjector(
            new PolicyEvaluator(policies, new RelationEvaluator(data)), data, today, _options.RetiredPublicDays, facts.ToDictionary(f => f.PersonId));

        var items = new List<PresenceItem>();
        var seats = new Dictionary<Guid, SeatStatus>();
        foreach (var fact in facts.OrderBy(f => f.PersonId))
        {
            var view = projector.Project(current, fact.PersonId);
            var visible = view?.Presence is not null;
            if (fact.SeatId is { } seatId)
            {
                var status = visible && view!.Presence!.SeatId is not null ? SeatStatus.Occupied : SeatStatus.Unavailable;
                seats[seatId] = seats.GetValueOrDefault(seatId) == SeatStatus.Occupied ? SeatStatus.Occupied : status;
            }

            if (visible)
            {
                var presence = view!.Presence!;
                items.Add(new PresenceItem(
                    view,
                    presence.SeatId is { } s ? map.FindSeat(s)?.Seat : null,
                    presence.ZoneKey is { } z ? map.FindZone(floorId, z) : null));
            }
        }

        var layoutSeats = map.LayoutOf(floorId)?.Seats ?? [];
        return new FloorPresence(
            today,
            building,
            floor,
            [.. layoutSeats.Where(s => seats.ContainsKey(s.SeatId)).Select(s => new PresenceSeatState(s, seats[s.SeatId]))],
            items);
    }

    /// <summary>
    /// 人の今日の所在（1 人の参照の項目 presence に使う。<see cref="PersonDirectoryService"/>）。提供が無い・時点が今日でない・クライアントの項目の上限に
    /// presence が無ければ null（問い合わせない）。
    /// </summary>
    internal async Task<IReadOnlyDictionary<Guid, PresenceFact>?> ForPeopleAsync(
        ViewerContext viewer,
        IReadOnlyCollection<Guid> personIds,
        DateOnly date,
        OrgSnapshot data,
        CancellationToken cancellationToken)
    {
        var today = BusinessDate.Today(_timeProvider);
        if (!_provider.IsAvailable || date != today || personIds.Count == 0 || !viewer.FieldLimit.Allows(PersonFields.Presence))
        {
            return null;
        }

        var wanted = personIds.ToHashSet();
        var facts = Validated(data.Workplace, await _provider.GetAsync(new PresenceQuery(today, null, [.. wanted]), cancellationToken), today);
        return facts.Where(f => wanted.Contains(f.PersonId)).ToDictionary(f => f.PersonId);
    }

    /// <summary>組み込みの公開範囲の規則を足したポリシー（所在を投影するときに使う）。</summary>
    internal PolicySet WithPresenceRule(PolicySet policies) => PresencePolicy.WithBuiltInRule(policies, _options.PresenceVisibilityScope);

    /// <summary>jin-ji の配置図にある階・席・エリアの所在だけを、人ごとに 1 件にする。</summary>
    private static IEnumerable<PresenceFact> Validated(WorkplaceMap map, IReadOnlyList<PresenceEntry> entries, DateOnly today)
    {
        var seen = new HashSet<Guid>();
        foreach (var e in entries)
        {
            if (map.FindFloor(e.FloorId) is null || (e.SeatId is null && e.ZoneKey is null) || !seen.Add(e.PersonId))
            {
                continue;
            }

            if (e.SeatId is { } seat && map.FindSeat(seat)?.Floor.Id != e.FloorId)
            {
                continue;
            }

            if (e.ZoneKey is { } zone && map.FindZone(e.FloorId, zone) is null)
            {
                continue;
            }

            // 席だけの所在は、席のゾーンをエリアにする
            yield return new PresenceFact(e.PersonId, today, e.FloorId, e.SeatId, e.ZoneKey ?? (e.SeatId is { } s ? map.FindSeat(s)?.Seat.ZoneKey : null));
        }
    }
}
