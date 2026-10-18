namespace JinJi.Domain.Workplace;

/// <summary>階の種類（DB の floor_version.floor_kind）。表示とまとめ方にだけ使う固定の少数。</summary>
public enum FloorKind
{
    /// <summary>事務所。</summary>
    Office,

    /// <summary>製造フロア。</summary>
    Factory,

    /// <summary>倉庫。</summary>
    Warehouse,

    /// <summary>ロビー。</summary>
    Lobby,

    /// <summary>機械室・駐車場など。</summary>
    Service,

    /// <summary>その他。</summary>
    Other,
}

/// <summary>ゾーンの種類（DB の layout_zone.zone_kind）。</summary>
public enum ZoneKind
{
    /// <summary>事務所。</summary>
    Office,

    /// <summary>製造ライン。</summary>
    ProductionLine,

    /// <summary>保管。</summary>
    Storage,

    /// <summary>共用（会議・休憩など）。</summary>
    Common,

    /// <summary>立入制限。</summary>
    Restricted,
}

/// <summary>配置図の固定の部分の種類（コア・壁で囲った部屋・吹き抜け）。</summary>
public enum FixtureKind
{
    Core,
    Room,
    Void,
}

/// <summary>席の種類（DB の layout_seat.seat_kind）。</summary>
public enum SeatKind
{
    /// <summary>固定席。</summary>
    Fixed,

    /// <summary>持ち場（工場のライン・倉庫など。定員 2 以上を許す）。</summary>
    Station,
}

/// <summary>人の席の役割（DB の workplace_assignment.role）。</summary>
public enum WorkplaceRole
{
    /// <summary>主の席（1 人に同時に 1 つ）。</summary>
    Primary,

    /// <summary>副の席（兼務先など）。</summary>
    Secondary,
}

/// <summary>配置図の点（階ごとのローカル座標。整数のミリメートル。原点は左下、x は右、y は奥）。</summary>
public readonly record struct LayoutPoint(int X, int Y);

/// <summary>ある時点で有効な建物の版。</summary>
public sealed record Building(Guid Id, Guid SiteId, string Code, string Name, int SortOrder);

/// <summary>ある時点で有効な階の版。</summary>
/// <param name="LevelOrder">並び順（地上 n 階 = n × 10、地下 n 階 = -n × 10、中二階は間の値）。</param>
public sealed record Floor(Guid Id, Guid BuildingId, string Label, int LevelOrder, FloorKind Kind);

/// <summary>配置図の固定の部分（コア・部屋・吹き抜け）。</summary>
public sealed record LayoutFixture(FixtureKind Kind, string? Name, IReadOnlyList<LayoutPoint> Polygon);

/// <summary>改訂のゾーン。<paramref name="ZoneKey"/> は改訂をまたいで同じゾーンを指す。</summary>
public sealed record LayoutZone(Guid ZoneKey, string Name, ZoneKind Kind, IReadOnlyList<LayoutPoint> Polygon);

/// <summary>改訂の席の置き場所と属性。</summary>
/// <param name="SeatId">席の不変 ID（改訂をまたいで同じ席）。</param>
/// <param name="Code">席番号（階の中で一意）。</param>
/// <param name="Capacity">定員（持ち場だけ 2 以上）。</param>
/// <param name="Features">設備のタグ（許可リスト <see cref="WorkplaceCodes.Features"/>）。</param>
/// <param name="OutOfService">使えない席（保守中・予備）。</param>
public sealed record LayoutSeat(
    Guid SeatId,
    string Code,
    Guid? ZoneKey,
    int XMm,
    int YMm,
    int RotationDeg,
    string DeskType,
    SeatKind Kind,
    int Capacity,
    IReadOnlyList<string> Features,
    bool OutOfService)
{
    /// <summary>定員 1 の席か（割り当ての排他に使う）。</summary>
    public bool IsExclusive => Capacity == 1;
}

/// <summary>ある時点で有効な配置図の改訂（承認済み）。</summary>
/// <param name="ValidUntil">有効期間の終了日（この日を含まない）。null は終了なし。</param>
public sealed record FloorLayout(
    Guid Id,
    Guid FloorId,
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    int GridMm,
    IReadOnlyList<LayoutPoint> Outline,
    IReadOnlyList<LayoutFixture> Fixtures,
    IReadOnlyList<LayoutZone> Zones,
    IReadOnlyList<LayoutSeat> Seats);

/// <summary>ある時点で有効な人の席・持ち場（座席かゾーンのどちらか一方）。</summary>
/// <param name="Exclusive">定員 1 の席か。</param>
public sealed record WorkplaceAssignment(
    Guid Id,
    Guid PersonId,
    Guid FloorId,
    Guid? SeatId,
    Guid? ZoneKey,
    WorkplaceRole Role,
    bool Exclusive,
    DateOnly ValidFrom,
    DateOnly? ValidUntil);

/// <summary>人の階・エリアの根拠。</summary>
public enum WorkplaceSource
{
    /// <summary>席の割り当て。</summary>
    Seat,

    /// <summary>ゾーンの割り当て（工場のライン・倉庫のエリアなど、席の無い持ち場）。</summary>
    Zone,
}

/// <summary>解決した人の階・エリア。</summary>
/// <param name="Zone">エリア（ゾーンが改訂に無い・席にゾーンが無ければ null）。</param>
public sealed record ResolvedWorkplace(Building Building, Floor Floor, LayoutZone? Zone, WorkplaceSource Source);

/// <summary>解決した人の席（その時点の改訂にある席だけ）。</summary>
public sealed record ResolvedSeat(Building Building, Floor Floor, LayoutSeat Seat, LayoutZone? Zone, WorkplaceRole Role);

/// <summary>席の置き場所（階と改訂の席）。</summary>
public sealed record SeatPlacement(Floor Floor, LayoutSeat Seat);
