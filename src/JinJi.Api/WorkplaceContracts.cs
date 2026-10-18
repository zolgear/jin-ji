using JinJi.Application.Presence;
using JinJi.Application.Workplace;
using JinJi.Domain.Organization;
using JinJi.Domain.Workplace;
using JinJi.Policy;

namespace JinJi.Api;

/// <summary>人の階・エリア（人の応答の workplace）。建物・階は全社公開の情報で、人の階として見せるかは項目 workplace の判定による。</summary>
/// <param name="ZoneKey">エリアのゾーン（改訂をまたぐ ID）。エリアが無ければ null。</param>
/// <param name="Source">根拠（seat 席の割り当て / zone ゾーンの割り当て）。</param>
public sealed record WorkplaceRefResponse(
    Guid SiteId,
    Guid BuildingId,
    string BuildingCode,
    string BuildingName,
    Guid FloorId,
    string FloorLabel,
    int LevelOrder,
    Guid? ZoneKey,
    string? ZoneName,
    [property: OpenApiEnum("seat", "zone")] string Source)
{
    public static WorkplaceRefResponse? From(WorkplaceView? view) =>
        view is null
            ? null
            : new WorkplaceRefResponse(
                view.SiteId, view.BuildingId, view.BuildingCode, view.BuildingName, view.FloorId, view.FloorLabel, view.LevelOrder, view.ZoneKey, view.ZoneName,
                WorkplaceCodesApi.Source(view.Source));
}

/// <summary>人の主の席（人の応答の seat。席番号・座標・階）。</summary>
public sealed record SeatRefResponse(
    Guid SeatId,
    string Code,
    Guid BuildingId,
    string BuildingName,
    Guid FloorId,
    string FloorLabel,
    Guid? ZoneKey,
    string? ZoneName,
    int XMm,
    int YMm,
    int RotationDeg,
    [property: OpenApiEnum(typeof(SeatKind))] string Kind)
{
    public static SeatRefResponse? From(SeatView? view) =>
        view is null
            ? null
            : new SeatRefResponse(
                view.SeatId, view.Code, view.BuildingId, view.BuildingName, view.FloorId, view.FloorLabel, view.ZoneKey, view.ZoneName, view.XMm, view.YMm,
                view.RotationDeg, WorkplaceCodes.ToCode(view.Kind));
}

/// <summary>
/// 当日の所在（人の応答の presence。外部の提供の今日の階・エリア・席）。所在が無い・見えないときは項目ごと省く（visible_fields にも無い）。
/// 所在が無いことは不在・在宅・休みを意味しない。
/// </summary>
/// <param name="SeatId">所在の席（エリアだけの所在か、項目 seat が見えなければ null）。</param>
public sealed record PresenceRefResponse(
    DateOnly Date,
    Guid BuildingId,
    string BuildingName,
    Guid FloorId,
    string FloorLabel,
    Guid? ZoneKey,
    string? ZoneName,
    Guid? SeatId,
    string? SeatCode)
{
    public static PresenceRefResponse? From(PresenceView? view) =>
        view is null
            ? null
            : new PresenceRefResponse(view.Date, view.BuildingId, view.BuildingName, view.FloorId, view.FloorLabel, view.ZoneKey, view.ZoneName, view.SeatId, view.SeatCode);
}

/// <summary>階の要約（席数・使用中・空席・使えない席。閲覧者に見える人だけで数える）。</summary>
/// <param name="LayoutId">その時点の配置図の改訂（無ければ null。席数はすべて 0）。</param>
/// <param name="LayoutValidFrom">改訂の有効日（最終改訂日。古い配置図の目安）。</param>
/// <param name="OccupiedCount">使用中の席（閲覧者に項目 seat が見える人の席）。</param>
/// <param name="VacantCount">空席（割り当ての無い席）。</param>
/// <param name="UnavailableCount">使えない席（保守中・予備と、閲覧者に見えない人の席）。</param>
public sealed record FloorSummaryResponse(
    Guid Id,
    Guid BuildingId,
    string Label,
    int LevelOrder,
    [property: OpenApiEnum(typeof(FloorKind))] string Kind,
    Guid? LayoutId,
    DateOnly? LayoutValidFrom,
    int SeatCount,
    int OccupiedCount,
    int VacantCount,
    int UnavailableCount)
{
    public static FloorSummaryResponse From(FloorSummary s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new FloorSummaryResponse(
            s.Floor.Id, s.Floor.BuildingId, s.Floor.Label, s.Floor.LevelOrder, WorkplaceCodes.ToCode(s.Floor.Kind), s.Layout?.Id, s.Layout?.ValidFrom,
            s.SeatCount, s.OccupiedCount, s.VacantCount, s.UnavailableCount);
    }
}

/// <summary>建物と階の一覧（下の階が先）。</summary>
public sealed record BuildingResponse(
    Guid Id,
    Guid SiteId,
    string? SiteCode,
    string? SiteName,
    string Code,
    string Name,
    int SortOrder,
    IReadOnlyList<FloorSummaryResponse> Floors)
{
    public static BuildingResponse From(BuildingSummary b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return new BuildingResponse(
            b.Building.Id, b.Building.SiteId, b.Site?.Code, b.Site?.Name, b.Building.Code, b.Building.Name, b.Building.SortOrder,
            [.. b.Floors.Select(FloorSummaryResponse.From)]);
    }
}

/// <summary>拠点の建物の一覧。</summary>
public sealed record BuildingListResponse(DateOnly AsOf, Guid SiteId, IReadOnlyList<BuildingResponse> Items);

/// <summary>建物の参照。</summary>
public sealed record BuildingDetailResponse(DateOnly AsOf, BuildingResponse Building);

/// <summary>階（建物・拠点を添える）。</summary>
public sealed record FloorRefResponse(
    Guid Id,
    Guid BuildingId,
    string BuildingCode,
    string BuildingName,
    Guid SiteId,
    string Label,
    int LevelOrder,
    [property: OpenApiEnum(typeof(FloorKind))] string Kind)
{
    public static FloorRefResponse From(Building building, Floor floor)
    {
        ArgumentNullException.ThrowIfNull(building);
        ArgumentNullException.ThrowIfNull(floor);
        return new FloorRefResponse(floor.Id, building.Id, building.Code, building.Name, building.SiteId, floor.Label, floor.LevelOrder, WorkplaceCodes.ToCode(floor.Kind));
    }
}

/// <summary>配置図の固定の部分（コア・部屋・吹き抜け）。多角形は [[x_mm, y_mm], …]。</summary>
public sealed record FixtureResponse([property: OpenApiEnum(typeof(FixtureKind))] string Kind, string? Name, IReadOnlyList<IReadOnlyList<int>> Polygon);

/// <summary>ゾーン（エリア。工場のライン・倉庫のエリアを含む）。</summary>
public sealed record ZoneResponse(
    Guid ZoneKey,
    string Name,
    [property: OpenApiEnum(typeof(ZoneKind))] string Kind,
    IReadOnlyList<IReadOnlyList<int>> Polygon);

/// <summary>席の置き場所と状態（人は返さない）。</summary>
/// <param name="Status">vacant 空席 / occupied 使用中（閲覧者に見える人の席）/ unavailable 使えない席（保守中・予備・見えない人の席）。</param>
public sealed record LayoutSeatResponse(
    Guid SeatId,
    string Code,
    Guid? ZoneKey,
    int XMm,
    int YMm,
    int RotationDeg,
    [property: OpenApiEnum("desk_1000", "desk_1200", "desk_1400", "desk_1600", "desk_1800", "station", "counter", "booth")] string DeskType,
    [property: OpenApiEnum(typeof(SeatKind))] string Kind,
    int Capacity,
    [property: OpenApiEnum("monitor", "dual_monitor", "standing", "phone_booth", "docking_station", "locker", "quiet")] IReadOnlyList<string> Features,
    [property: OpenApiEnum(typeof(SeatStatus))] string Status);

/// <summary>その日の配置図の改訂（承認済み）。有効期間の終了日は返さない（次の改訂の予定を推測させないため）。</summary>
public sealed record LayoutResponse(
    Guid Id,
    DateOnly ValidFrom,
    int GridMm,
    IReadOnlyList<IReadOnlyList<int>> Outline,
    IReadOnlyList<FixtureResponse> Fixtures,
    IReadOnlyList<ZoneResponse> Zones,
    IReadOnlyList<LayoutSeatResponse> Seats);

/// <summary>その日の配置図（配置図が無い階は layout が null）。</summary>
public sealed record FloorLayoutResponse(DateOnly AsOf, FloorRefResponse Floor, LayoutResponse? Layout)
{
    public static FloorLayoutResponse From(FloorLayoutResult r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var statuses = r.Seats.ToDictionary(s => s.Seat.SeatId, s => s.Status);
        return new FloorLayoutResponse(
            r.AsOf,
            FloorRefResponse.From(r.Building, r.Floor),
            r.Layout is not { } layout
                ? null
                : new LayoutResponse(
                    layout.Id,
                    layout.ValidFrom,
                    layout.GridMm,
                    WorkplaceCodesApi.Points(layout.Outline),
                    [.. layout.Fixtures.Select(f => new FixtureResponse(WorkplaceCodes.ToCode(f.Kind), f.Name, WorkplaceCodesApi.Points(f.Polygon)))],
                    [.. r.Zones.Select(z => new ZoneResponse(
                        z.Zone.ZoneKey, z.Zone.Name, WorkplaceCodes.ToCode(z.Zone.Kind), WorkplaceCodesApi.Points(z.Zone.Polygon)))],
                    [.. layout.Seats.Select(s => new LayoutSeatResponse(
                        s.SeatId, s.Code, s.ZoneKey, s.XMm, s.YMm, s.RotationDeg, s.DeskType, WorkplaceCodes.ToCode(s.Kind), s.Capacity, s.Features,
                        WorkplaceCodesApi.Status(statuses[s.SeatId])))]));
    }
}

/// <summary>階の在籍者（所属上の席だけ。当日の所在は含めない）。</summary>
/// <param name="SeatId">割り当ての席（ゾーンの割り当ての人は null）。</param>
/// <param name="Role">primary 主の席 / secondary 副の席。</param>
/// <param name="Source">seat 席の割り当て / zone ゾーンの割り当て。</param>
public sealed record FloorOccupantResponse(
    PersonResponse Person,
    Guid? SeatId,
    string? SeatCode,
    Guid? ZoneKey,
    string? ZoneName,
    [property: OpenApiEnum(typeof(WorkplaceRole))] string Role,
    [property: OpenApiEnum("seat", "zone")] string Source)
{
    public static FloorOccupantResponse From(FloorOccupant o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new FloorOccupantResponse(
            PersonResponse.From(o.Person), o.Seat?.SeatId, o.Seat?.Code, o.Zone?.ZoneKey, o.Zone?.Name, WorkplaceCodes.ToCode(o.Role), WorkplaceCodesApi.Source(o.Source));
    }
}

/// <summary>階の在籍者の 1 ページ。</summary>
public sealed record FloorOccupantListResponse(DateOnly AsOf, Guid FloorId, IReadOnlyList<FloorOccupantResponse> Items, string? NextCursor);

/// <summary>席の検索の 1 件（人は返さない）。</summary>
public sealed record SeatSearchItemResponse(
    Guid SeatId,
    string Code,
    Guid BuildingId,
    string BuildingName,
    Guid FloorId,
    string FloorLabel,
    Guid? ZoneKey,
    string? ZoneName,
    [property: OpenApiEnum(typeof(SeatKind))] string Kind,
    int Capacity,
    [property: OpenApiEnum("monitor", "dual_monitor", "standing", "phone_booth", "docking_station", "locker", "quiet")] IReadOnlyList<string> Features,
    [property: OpenApiEnum(typeof(SeatStatus))] string Status)
{
    public static SeatSearchItemResponse From(SeatHit h)
    {
        ArgumentNullException.ThrowIfNull(h);
        return new SeatSearchItemResponse(
            h.Seat.SeatId, h.Seat.Code, h.Building.Id, h.Building.Name, h.Floor.Id, h.Floor.Label, h.Zone?.ZoneKey, h.Zone?.Name,
            WorkplaceCodes.ToCode(h.Seat.Kind), h.Seat.Capacity, h.Seat.Features, WorkplaceCodesApi.Status(h.Status));
    }
}

/// <summary>席の検索の結果（空席は割り当ての無い席。予約の空きではない）。</summary>
public sealed record SeatSearchResponse(DateOnly AsOf, IReadOnlyList<SeatSearchItemResponse> Items, bool HasMore);

/// <summary>今日の所在のある席の状態（空きは返さない）。</summary>
/// <param name="Status">occupied 閲覧者に見える人の所在の席 / unavailable 見えない人の所在の席。</param>
public sealed record PresenceSeatResponse(Guid SeatId, string Code, [property: OpenApiEnum("occupied", "unavailable")] string Status);

/// <summary>閲覧者に項目 presence が見える人の今日の所在（席番号は項目 seat も見えるときだけ）。</summary>
public sealed record PresenceItemResponse(PersonResponse Person, Guid? SeatId, string? SeatCode, Guid? ZoneKey, string? ZoneName);

/// <summary>
/// 今日の階の所在（外部の提供を注入したときだけ。jin-ji は所在を保持しない）。所在が無いことは不在・在宅・休みを意味しない。
/// </summary>
public sealed record FloorPresenceResponse(
    DateOnly Date,
    FloorRefResponse Floor,
    IReadOnlyList<PresenceSeatResponse> Seats,
    IReadOnlyList<PresenceItemResponse> Items)
{
    public static FloorPresenceResponse From(FloorPresence p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new FloorPresenceResponse(
            p.Date,
            FloorRefResponse.From(p.Building, p.Floor),
            [.. p.Seats.Select(s => new PresenceSeatResponse(s.Seat.SeatId, s.Seat.Code, WorkplaceCodesApi.Status(s.Status)))],
            [.. p.Items.Select(i => new PresenceItemResponse(PersonResponse.From(i.Person), i.Seat?.SeatId, i.Seat?.Code, i.Zone?.ZoneKey, i.Zone?.Name))]);
    }
}

/// <summary>建物・フロアモードの API のコード値。</summary>
internal static class WorkplaceCodesApi
{
    public static string Source(WorkplaceSource source) => source switch
    {
        WorkplaceSource.Seat => "seat",
        WorkplaceSource.Zone => "zone",
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    public static string Status(SeatStatus status) => Codes.Of(status);

    public static IReadOnlyList<IReadOnlyList<int>> Points(IReadOnlyList<LayoutPoint> polygon) =>
        [.. polygon.Select(p => (IReadOnlyList<int>)[p.X, p.Y])];
}
