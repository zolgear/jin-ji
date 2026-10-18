namespace JinJi.Domain.Workplace;

/// <summary>建物・フロアモードのコード値（DB の CHECK 制約・API・取込で共通）と配置図の上限。</summary>
public static class WorkplaceCodes
{
    /// <summary>階の種類（<see cref="FloorKind"/> の順）。</summary>
    public static readonly IReadOnlyList<string> FloorKinds = ["office", "factory", "warehouse", "lobby", "service", "other"];

    /// <summary>ゾーンの種類（<see cref="ZoneKind"/> の順）。</summary>
    public static readonly IReadOnlyList<string> ZoneKinds = ["office", "production_line", "storage", "common", "restricted"];

    /// <summary>固定の部分の種類（<see cref="FixtureKind"/> の順）。</summary>
    public static readonly IReadOnlyList<string> FixtureKinds = ["core", "room", "void"];

    /// <summary>席の種類（<see cref="SeatKind"/> の順）。</summary>
    public static readonly IReadOnlyList<string> SeatKinds = ["fixed", "station"];

    /// <summary>席の役割（<see cref="WorkplaceRole"/> の順）。</summary>
    public static readonly IReadOnlyList<string> Roles = ["primary", "secondary"];

    /// <summary>机の型（既定の型の許可リスト。DB の layout_seat_desk_type_check と同じ）。</summary>
    public static readonly IReadOnlyList<string> DeskTypes = ["desk_1000", "desk_1200", "desk_1400", "desk_1600", "desk_1800", "station", "counter", "booth"];

    /// <summary>
    /// 設備のタグ（許可リスト。DB の layout_seat_features_check と同じ）。健康・障害に関わるタグ（車椅子対応など）は作らない
    /// （人の割り当てと組み合わさると、ADR-0002 で保持しない障害の情報を推測させるため）。
    /// </summary>
    public static readonly IReadOnlyList<string> Features = ["monitor", "dual_monitor", "standing", "phone_booth", "docking_station", "locker", "quiet"];

    /// <summary>座標の上限（ミリメートル。外接矩形の 1 辺 500 m）。</summary>
    public const int MaxCoordinateMm = 500_000;

    /// <summary>多角形の頂点の数の下限・上限。</summary>
    public const int MinPolygonPoints = 3;

    public const int MaxPolygonPoints = 64;

    /// <summary>1 階のゾーン・固定の部分の上限。</summary>
    public const int MaxZonesPerFloor = 200;

    public const int MaxFixturesPerFloor = 200;

    /// <summary>1 階の席の上限。</summary>
    public const int MaxSeatsPerFloor = 3000;

    /// <summary>持ち場の定員の上限。</summary>
    public const int MaxCapacity = 20;

    /// <summary>1 席の設備のタグの上限。</summary>
    public const int MaxFeaturesPerSeat = 8;

    /// <summary>
    /// 席番号・建物コードの正規化（全角・ハイフンの揺れ・空白・大文字小文字。<c>３F－Ａ０１</c> → <c>3F-A01</c>）。空なら null。
    /// </summary>
    public static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Normalize(System.Text.NormalizationForm.FormKC);
        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            builder.Append(c switch
            {
                '‐' or '‑' or '‒' or '–' or '—' or '―' or '−' or 'ｰ' or 'ー' or '﹣' => '-',
                _ => char.ToUpperInvariant(c),
            });
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>階の表示名の正規化（全角・空白・英字の大文字。<c>３ｆ</c> → <c>3F</c>、<c>b1</c> → <c>B1</c>）。日本語の表記（屋上・ロビー）はそのまま。空なら null。</summary>
    public static string? NormalizeFloorLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = new string([.. value.Normalize(System.Text.NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)).Select(c => char.IsAsciiLetter(c) ? char.ToUpperInvariant(c) : c)]);
        return text.Length == 0 ? null : text;
    }

    public static string ToCode(FloorKind kind) => FloorKinds[(int)kind];

    public static string ToCode(ZoneKind kind) => ZoneKinds[(int)kind];

    public static string ToCode(FixtureKind kind) => FixtureKinds[(int)kind];

    public static string ToCode(SeatKind kind) => SeatKinds[(int)kind];

    public static string ToCode(WorkplaceRole role) => Roles[(int)role];

    public static bool TryParse(string? code, out FloorKind kind) => TryParse(FloorKinds, code, out kind);

    public static bool TryParse(string? code, out ZoneKind kind) => TryParse(ZoneKinds, code, out kind);

    public static bool TryParse(string? code, out FixtureKind kind) => TryParse(FixtureKinds, code, out kind);

    public static bool TryParse(string? code, out SeatKind kind) => TryParse(SeatKinds, code, out kind);

    public static bool TryParse(string? code, out WorkplaceRole role) => TryParse(Roles, code, out role);

    /// <summary>DB の値を読む（未知の値は DB の CHECK 制約で入らないので、来たら例外）。</summary>
    public static T Parse<T>(string code)
        where T : struct, Enum
    {
        var values = Values<T>();
        var index = values.IndexOf(code);
        return index >= 0 ? (T)(object)index : throw new InvalidOperationException($"DB に未知の {typeof(T).Name} の値があります: {code}");
    }

    private static List<string> Values<T>()
        where T : struct, Enum => typeof(T).Name switch
        {
            nameof(FloorKind) => [.. FloorKinds],
            nameof(ZoneKind) => [.. ZoneKinds],
            nameof(FixtureKind) => [.. FixtureKinds],
            nameof(SeatKind) => [.. SeatKinds],
            nameof(WorkplaceRole) => [.. Roles],
            _ => throw new ArgumentException($"コード値の無い型です: {typeof(T).Name}"),
        };

    private static bool TryParse<T>(IReadOnlyList<string> values, string? code, out T value)
        where T : struct, Enum
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (string.Equals(values[i], code, StringComparison.Ordinal))
            {
                value = (T)(object)i;
                return true;
            }
        }

        value = default;
        return false;
    }
}
