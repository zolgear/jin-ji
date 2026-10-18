using JinJi.Application.Audit;
using JinJi.Application.People;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Workplace;
using JinJi.Policy;

namespace JinJi.Application.Workplace;

/// <summary>
/// 建物・階・配置図・席の参照（時点指定。建物・フロアモードの仕様 §6）。REST・MCP はこのサービスを通して返す。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>建物・階・配置図（外形・ゾーン・席の置き場所）は組織・拠点と同じく全社公開。スナップショットは閲覧者を RLS に渡して読むので、
/// 人事管理者以外には将来の版・改訂・割り当て（席替えの予定）が現れない。as_of の規則は人・組織の参照と同じ（今日より後は人事管理者と先行提供の社内システムだけ）</item>
/// <item>席の状態は 3 つ（<see cref="SeatStatus"/>）。閲覧者に項目 seat が見えない人の席は「使えない席」（保守中・予備と区別しない）。
/// 件数は閲覧者に見える人だけで数え、使えない席は空席に数えない</item>
/// <item>人を返すのは階の在籍者（所属上の席だけ。当日の所在は含めない）だけで、返却人数の制限の対象。配置図・席の検索・建物の一覧は人を返さない（監査の対象者は空）</item>
/// <item>「空席」は割り当ての無い席の意味だけ（予約の空きではない。予約・フリーアドレス・会議室は人事の範疇を超える施設管理で、連携先の範疇）</item>
/// </list>
/// </remarks>
public sealed class WorkplaceDirectoryService
{
    public const string BuildingListAction = "workplace.buildings";
    public const string BuildingReadAction = "workplace.building";
    public const string LayoutAction = "workplace.layout";
    public const string OccupantsAction = "workplace.occupants";
    public const string SeatSearchAction = "workplace.seats";

    private readonly IOrgSnapshotProvider _snapshots;
    private readonly IPolicyRuleStore _policies;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly PersonDirectoryOptions _options;

    public WorkplaceDirectoryService(
        IOrgSnapshotProvider snapshots,
        IPolicyRuleStore policies,
        AuditWriter audit,
        TimeProvider timeProvider,
        PersonDirectoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options ?? new PersonDirectoryOptions();
        _options.Validate();
        _snapshots = snapshots;
        _policies = policies;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    /// <summary>拠点の建物と階の一覧（階ごとの席数・使用中・空席・使えない席）。拠点がその時点に無ければ null。</summary>
    public Task<SiteBuildings?> ListBuildingsAsync(ViewerContext viewer, Guid siteId, DateOnly? asOf, CancellationToken cancellationToken) =>
        RunAsync<SiteBuildings?>(viewer, BuildingListAction, "site", siteId, asOf, (context, _) =>
                Task.FromResult(context.Data.FindSite(siteId) is { } site
                    ? new SiteBuildings(context.Data.AsOf, site, [.. context.Data.Workplace.BuildingsOf(siteId).Select(b => Summarize(context, b))])
                    : null),
            _ => [],
            cancellationToken);

    /// <summary>建物と階の一覧。建物がその時点に無ければ null。</summary>
    public Task<BuildingDetail?> GetBuildingAsync(ViewerContext viewer, Guid buildingId, DateOnly? asOf, CancellationToken cancellationToken) =>
        RunAsync<BuildingDetail?>(viewer, BuildingReadAction, "building", buildingId, asOf, (context, _) =>
                Task.FromResult(context.Data.Workplace.FindBuilding(buildingId) is { } building
                    ? new BuildingDetail(context.Data.AsOf, Summarize(context, building))
                    : null),
            _ => [],
            cancellationToken);

    /// <summary>その日の配置図と席の状態（人は返さない）。階がその時点に無ければ null。</summary>
    public Task<FloorLayoutResult?> GetLayoutAsync(ViewerContext viewer, Guid floorId, DateOnly? asOf, CancellationToken cancellationToken) =>
        RunAsync<FloorLayoutResult?>(viewer, LayoutAction, "floor", floorId, asOf, (context, _) =>
            {
                var map = context.Data.Workplace;
                if (map.FindFloor(floorId) is not { } floor || map.FindBuilding(floor.BuildingId) is not { } building)
                {
                    return Task.FromResult<FloorLayoutResult?>(null);
                }

                var layout = map.LayoutOf(floorId);
                var zones = layout?.Zones.Select(z => new ZoneState(z)).ToList() ?? [];
                var seats = layout?.Seats.Select(s => new SeatState(s, context.StatusOf(s))).ToList() ?? [];
                return Task.FromResult<FloorLayoutResult?>(new FloorLayoutResult(context.Data.AsOf, building, floor, layout, zones, seats));
            },
            _ => [],
            cancellationToken);

    /// <summary>
    /// 階の在籍者（所属上の席・持ち場）。席の人は閲覧者に項目 seat が見える人だけ、席の無い持ち場（ゾーンの割り当て）の人は項目 workplace が見える人だけ。
    /// 並びは席番号（席の無い人は後）・人の ID の順。当日の所在は含めない（固定。今日オフィスにいる人の一覧を引けないようにする）。階がその時点に無ければ null。
    /// </summary>
    public Task<FloorOccupantPage?> ListOccupantsAsync(ViewerContext viewer, Guid floorId, FloorOccupantQuery query, DateOnly? asOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return RunAsync<FloorOccupantPage?>(viewer, OccupantsAction, "floor", floorId, asOf, (context, _) =>
            {
                var map = context.Data.Workplace;
                if (map.FindFloor(floorId) is null)
                {
                    return Task.FromResult<FloorOccupantPage?>(null);
                }

                var occupants = new List<(string Key, FloorOccupant Occupant)>();
                foreach (var a in map.AssignmentsOnFloor(floorId))
                {
                    if (context.Projector.Project(viewer, a.PersonId) is not { } view)
                    {
                        continue;
                    }

                    if (a.SeatId is { } seatId)
                    {
                        if (!view.CanSee(PersonFields.Seat) || map.FindSeat(seatId) is not { } placement || placement.Floor.Id != floorId)
                        {
                            continue;
                        }

                        var zone = placement.Seat.ZoneKey is { } z ? map.FindZone(floorId, z) : null;
                        if (query.ZoneKey is { } wanted && zone?.ZoneKey != wanted)
                        {
                            continue;
                        }

                        occupants.Add((Key(placement.Seat.Code, a.PersonId, a.Role), new FloorOccupant(view, placement.Seat, zone, a.Role, WorkplaceSource.Seat)));
                    }
                    else if (a.ZoneKey is { } zoneKey && view.CanSee(PersonFields.Workplace) && (query.ZoneKey is null || query.ZoneKey == zoneKey))
                    {
                        occupants.Add((Key(null, a.PersonId, a.Role), new FloorOccupant(view, null, map.FindZone(floorId, zoneKey), a.Role, WorkplaceSource.Zone)));
                    }
                }

                var ordered = occupants.OrderBy(o => o.Key, StringComparer.Ordinal).Where(o => query.After is null || string.CompareOrdinal(o.Key, query.After) > 0).ToList();
                var page = ordered.Take(query.Limit).ToList();
                return Task.FromResult<FloorOccupantPage?>(new FloorOccupantPage(
                    context.Data.AsOf,
                    [.. page.Select(o => o.Occupant)],
                    ordered.Count > query.Limit ? page[^1].Key : null));
            },
            page => page?.Items.Select(o => o.Person.PersonId) ?? [],
            cancellationToken);
    }

    /// <summary>
    /// 席を探す（空席など。人は返さない）。使えない席は空席として返さない。並びは建物・階・席番号の順。
    /// </summary>
    /// <exception cref="ArgumentException">条件が不正（範囲が無い・未知の種類やタグ）。</exception>
    public async Task<SeatSearchResult> SearchSeatsAsync(ViewerContext viewer, SeatSearch search, DateOnly? asOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(search);
        search.Validate();
        return await RunAsync(viewer, SeatSearchAction, null, null, asOf, (context, _) =>
            {
                var map = context.Data.Workplace;
                var floors = search.FloorIds.Count > 0
                    ? search.FloorIds.Select(map.FindFloor).OfType<Floor>()
                    : search.BuildingId is { } building
                        ? map.FloorsOf(building)
                        : map.BuildingsOf(search.SiteId!.Value).SelectMany(b => map.FloorsOf(b.Id));
                if (search.SiteId is { } site)
                {
                    floors = floors.Where(f => map.FindBuilding(f.BuildingId)?.SiteId == site);
                }

                if (search.BuildingId is { } inBuilding)
                {
                    floors = floors.Where(f => f.BuildingId == inBuilding);
                }

                var hits = new List<SeatHit>();
                foreach (var floor in floors.Distinct().OrderBy(f => map.FindBuilding(f.BuildingId)!.SortOrder).ThenBy(f => map.FindBuilding(f.BuildingId)!.Code, StringComparer.Ordinal).ThenBy(f => f.LevelOrder))
                {
                    if (map.LayoutOf(floor.Id) is not { } layout)
                    {
                        continue;
                    }

                    foreach (var seat in layout.Seats.OrderBy(s => s.Code, StringComparer.Ordinal))
                    {
                        if ((search.ZoneKey is { } zoneKey && seat.ZoneKey != zoneKey)
                            || (search.Kinds.Count > 0 && !search.Kinds.Contains(WorkplaceCodes.ToCode(seat.Kind)))
                            || !search.Features.All(seat.Features.Contains))
                        {
                            continue;
                        }

                        var status = context.StatusOf(seat);

                        if (search.VacantOnly && status != SeatStatus.Vacant)
                        {
                            continue;
                        }

                        hits.Add(new SeatHit(map.FindBuilding(floor.BuildingId)!, floor, seat, seat.ZoneKey is { } z ? map.FindZone(floor.Id, z) : null, status));
                        if (hits.Count > search.Limit)
                        {
                            break;
                        }
                    }

                    if (hits.Count > search.Limit)
                    {
                        break;
                    }
                }

                return Task.FromResult(new SeatSearchResult(context.Data.AsOf, [.. hits.Take(search.Limit)], hits.Count > search.Limit));
            },
            _ => [],
            cancellationToken);
    }

    /// <summary>位置のキー（席番号の順、席の無い人は後、同じ席は人の ID の順）。</summary>
    private static string Key(string? seatCode, Guid personId, WorkplaceRole role) =>
        $"{(seatCode is null ? "￿" : seatCode)}\u0001{(role == WorkplaceRole.Primary ? '0' : '1')}{personId:N}";

    private static BuildingSummary Summarize(Context context, Building building)
    {
        var map = context.Data.Workplace;
        var floors = map.FloorsOf(building.Id).Select(floor =>
        {
            var layout = map.LayoutOf(floor.Id);
            var statuses = layout?.Seats.Select(s => (Seat: s, Status: context.StatusOf(s))).ToList() ?? [];
            return new FloorSummary(
                floor,
                layout,
                statuses.Count,
                statuses.Count(s => s.Status == SeatStatus.Occupied),
                statuses.Count(s => s.Status == SeatStatus.Vacant),
                statuses.Count(s => s.Status == SeatStatus.Unavailable));
        }).ToList();
        return new BuildingSummary(building, context.Data.FindSite(building.SiteId), floors);
    }

    private async Task<T> RunAsync<T>(
        ViewerContext viewer,
        string action,
        string? targetType,
        Guid? targetId,
        DateOnly? asOf,
        Func<Context, CancellationToken, Task<T>> run,
        Func<T, IEnumerable<Guid>> subjects,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var today = BusinessDate.Today(_timeProvider);
        var date = asOf ?? today;
        if (date > viewer.DisclosureHorizon(today))
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, targetType, targetId);
            throw new AccessDeniedException(PersonDirectoryService.FutureDenied(viewer, today));
        }

        T result;
        try
        {
            var (data, relations, projectionDate) = await PersonDirectoryService.LoadAsync(_snapshots, viewer, date, today, cancellationToken);
            var policies = await _policies.LoadActiveAsync(viewer, cancellationToken);
            var projector = new PersonProjector(new PolicyEvaluator(policies, new RelationEvaluator(relations)), data, projectionDate, _options.RetiredPublicDays);
            result = await run(new Context(viewer, data, projector, today), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, [], CancellationToken.None, targetType, targetId);
            throw;
        }

        await _audit.RecordAsync(viewer, action, AuditOutcome.Success, subjects(result), cancellationToken, targetType, targetId);
        return result;
    }

    /// <summary>1 回の参照の文脈（時点のスナップショットと投影。席の見え方をキャッシュする）。</summary>
    private sealed class Context(ViewerContext viewer, OrgSnapshot data, PersonProjector projector, DateOnly today)
    {
        private readonly Dictionary<Guid, bool> _seatVisible = [];

        public ViewerContext Viewer { get; } = viewer;

        public OrgSnapshot Data { get; } = data;

        public PersonProjector Projector { get; } = projector;

        public DateOnly Today { get; } = today;

        /// <summary>閲覧者に見せる席の状態（§3.2）。</summary>
        public SeatStatus StatusOf(LayoutSeat seat) => SeatStates.StatusOf(Data.Workplace, seat, SeatVisible);

        private bool SeatVisible(Guid personId)
        {
            if (!_seatVisible.TryGetValue(personId, out var visible))
            {
                visible = Projector.Project(Viewer, personId) is { } view && view.CanSee(PersonFields.Seat);
                _seatVisible.Add(personId, visible);
            }

            return visible;
        }
    }
}

/// <summary>席の状態の規則（配置図・階の要約・席の検索・当日の所在で共通）。</summary>
public static class SeatStates
{
    /// <summary>
    /// 所属上の席の状態: 保守中・予備は使えない席、割り当てが無ければ空席、閲覧者に項目 seat が見える人の割り当てがあれば使用中、
    /// 見えない人の割り当てだけなら使えない席（隠した人の席を目立たせない）。
    /// </summary>
    public static SeatStatus StatusOf(WorkplaceMap map, LayoutSeat seat, Func<Guid, bool> seatVisible)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(seat);
        ArgumentNullException.ThrowIfNull(seatVisible);
        if (seat.OutOfService)
        {
            return SeatStatus.Unavailable;
        }

        var assignments = map.AssignmentsOfSeat(seat.SeatId);
        if (assignments.Count == 0)
        {
            return SeatStatus.Vacant;
        }

        return assignments.Any(a => seatVisible(a.PersonId)) ? SeatStatus.Occupied : SeatStatus.Unavailable;
    }
}
