using JinJi.Domain.Organization;
using JinJi.Domain.Workplace;
using JinJi.Policy;

namespace JinJi.Application.Workplace;

/// <summary>閲覧者に見せる席の状態（建物・フロアモードの仕様 §3.2。3 つだけで、増やさない）。</summary>
public enum SeatStatus
{
    /// <summary>空席（割り当ての無い席。予約の空きの意味は持たない）。</summary>
    Vacant,

    /// <summary>使用中（閲覧者に項目 seat が見える人の席。当日の所在では、見える人の所在の席）。</summary>
    Occupied,

    /// <summary>
    /// 使えない席（保守中・予備と、閲覧者に見えない人の席・所在）。個人単位の例外で席を隠した人の席を「割り当てあり（名前なし）」と
    /// 見せると例外があること自体が目立つので、保守中・予備の席と区別しない。空席には数えない。
    /// </summary>
    Unavailable,
}

/// <summary>階の要約（席数・使用中・空席・使えない席。閲覧者に見える人だけで数える）。</summary>
/// <param name="Layout">その時点の改訂（無ければ null。席数はすべて 0）。</param>
public sealed record FloorSummary(
    Floor Floor,
    FloorLayout? Layout,
    int SeatCount,
    int OccupiedCount,
    int VacantCount,
    int UnavailableCount);

/// <summary>建物と階の要約（下の階が先）。</summary>
public sealed record BuildingSummary(Building Building, Site? Site, IReadOnlyList<FloorSummary> Floors);

/// <summary>拠点の建物の一覧。</summary>
public sealed record SiteBuildings(DateOnly AsOf, Site Site, IReadOnlyList<BuildingSummary> Buildings);

/// <summary>建物の参照の結果。</summary>
public sealed record BuildingDetail(DateOnly AsOf, BuildingSummary Building);

/// <summary>席と、閲覧者に見せる状態。</summary>
public sealed record SeatState(LayoutSeat Seat, SeatStatus Status);

/// <summary>ゾーン（配置図上の区画。工場のライン・倉庫のエリアを含む）。</summary>
public sealed record ZoneState(LayoutZone Zone);

/// <summary>その日の配置図（外形・固定の部分・ゾーン・席の置き場所と状態）。人は返さない。</summary>
/// <param name="Layout">その時点の改訂（無ければ null。ゾーン・席は空）。</param>
public sealed record FloorLayoutResult(
    DateOnly AsOf,
    Building Building,
    Floor Floor,
    FloorLayout? Layout,
    IReadOnlyList<ZoneState> Zones,
    IReadOnlyList<SeatState> Seats);

/// <summary>階の在籍者（所属上の席だけ。当日の所在は含めない）。</summary>
/// <param name="Seat">割り当ての席（ゾーンの割り当ての人は null）。</param>
/// <param name="Zone">エリア（席のゾーンか、割り当てのゾーン）。</param>
/// <param name="Role">席の役割。</param>
public sealed record FloorOccupant(PersonView Person, LayoutSeat? Seat, LayoutZone? Zone, WorkplaceRole Role, WorkplaceSource Source);

/// <summary>階の在籍者の 1 ページ。</summary>
/// <param name="NextAfter">次のページの位置（無ければ null）。呼び出し側が署名付きのカーソルにする。</param>
public sealed record FloorOccupantPage(DateOnly AsOf, IReadOnlyList<FloorOccupant> Items, string? NextAfter);

/// <summary>階の在籍者の条件。</summary>
public sealed record FloorOccupantQuery
{
    public const int MaxLimit = 200;

    /// <summary>ゾーンで絞る（null なら階全体）。</summary>
    public Guid? ZoneKey { get; init; }

    /// <summary>この位置より後（前のページの <see cref="FloorOccupantPage.NextAfter"/>）。</summary>
    public string? After { get; init; }

    public int Limit { get; init; } = 50;

    public void Validate()
    {
        if (Limit is < 1 or > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), Limit, $"件数は 1〜{MaxLimit} で指定してください。");
        }
    }
}

/// <summary>席の検索の条件（人は返さない）。拠点・建物・階のいずれかで範囲を決める。</summary>
public sealed record SeatSearch
{
    /// <summary>1 回の最大件数（MCP の find_vacant_seats と同じ）。</summary>
    public const int MaxLimit = 100;

    public IReadOnlySet<Guid> FloorIds { get; init; } = new HashSet<Guid>();

    public Guid? BuildingId { get; init; }

    public Guid? SiteId { get; init; }

    public Guid? ZoneKey { get; init; }

    /// <summary>席の種類（<see cref="WorkplaceCodes.SeatKinds"/>）。</summary>
    public IReadOnlySet<string> Kinds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>設備のタグ（すべてを持つ席。<see cref="WorkplaceCodes.Features"/>）。</summary>
    public IReadOnlySet<string> Features { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>空席（割り当ての無い席）だけ（使えない席は空席として返さない）。</summary>
    public bool VacantOnly { get; init; }

    public int Limit { get; init; } = 50;

    public void Validate()
    {
        if (Limit is < 1 or > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), Limit, $"件数は 1〜{MaxLimit} で指定してください。");
        }

        if (FloorIds.Count == 0 && BuildingId is null && SiteId is null)
        {
            throw new ArgumentException("floor・building・site のいずれかで範囲を指定してください。", nameof(FloorIds));
        }

        if (FloorIds.Count > People.PersonSearch.MaxValuesPerFilter)
        {
            throw new ArgumentException($"floor は {People.PersonSearch.MaxValuesPerFilter} 件までです。", nameof(FloorIds));
        }

        if (Kinds.FirstOrDefault(k => !WorkplaceCodes.SeatKinds.Contains(k, StringComparer.Ordinal)) is { } kind)
        {
            throw new ArgumentException($"kind は {string.Join(" / ", WorkplaceCodes.SeatKinds)} で指定してください（{kind} は使えません）。", nameof(Kinds));
        }

        if (Features.FirstOrDefault(f => !WorkplaceCodes.Features.Contains(f, StringComparer.Ordinal)) is { } feature)
        {
            throw new ArgumentException($"feature は {string.Join(" / ", WorkplaceCodes.Features)} で指定してください（{feature} は使えません）。", nameof(Features));
        }
    }
}

/// <summary>席の検索の 1 件。</summary>
public sealed record SeatHit(Building Building, Floor Floor, LayoutSeat Seat, LayoutZone? Zone, SeatStatus Status);

/// <summary>席の検索の結果。</summary>
public sealed record SeatSearchResult(DateOnly AsOf, IReadOnlyList<SeatHit> Items, bool HasMore);
