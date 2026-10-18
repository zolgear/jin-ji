using System.Text.Json;
using JinJi.Domain.Workplace;

namespace JinJi.Ingest.Normalization;

/// <summary>建物・フロアモードの取込の表示値 → コード値（0019）。当てはまらなければ null（修正依頼にする）。</summary>
public static class WorkplaceValueCodes
{
    /// <summary>配置図の行の要素。</summary>
    public const string Outline = "outline";
    public const string Fixture = "fixture";
    public const string Zone = "zone";
    public const string Seat = "seat";

    /// <summary>階だけの行（配置図の無い階を登録する）。</summary>
    public const string FloorOnly = "floor";

    private static readonly Dictionary<string, string> FloorKinds = new(StringComparer.Ordinal)
    {
        ["事務所"] = "office", ["オフィス"] = "office", ["執務"] = "office",
        ["製造"] = "factory", ["製造フロア"] = "factory", ["工場"] = "factory",
        ["倉庫"] = "warehouse", ["物流"] = "warehouse",
        ["ロビー"] = "lobby", ["エントランス"] = "lobby", ["受付"] = "lobby",
        ["機械室"] = "service", ["駐車場"] = "service", ["設備"] = "service",
        ["その他"] = "other",
    };

    private static readonly Dictionary<string, string> Elements = new(StringComparer.Ordinal)
    {
        ["外形"] = Outline, ["固定"] = Fixture, ["固定の部分"] = Fixture, ["ゾーン"] = Zone, ["エリア"] = Zone, ["席"] = Seat, ["座席"] = Seat, ["階"] = FloorOnly,
    };

    private static readonly Dictionary<string, string> LayoutKinds = new(StringComparer.Ordinal)
    {
        ["事務所"] = "office", ["オフィス"] = "office", ["製造ライン"] = "production_line", ["ライン"] = "production_line",
        ["保管"] = "storage", ["共用"] = "common", ["立入制限"] = "restricted",
        ["コア"] = "core", ["部屋"] = "room", ["会議室"] = "room", ["吹き抜け"] = "void", ["吹抜け"] = "void",
    };

    private static readonly Dictionary<string, string> SeatKinds = new(StringComparer.Ordinal)
    {
        ["固定"] = "fixed", ["固定席"] = "fixed", ["持ち場"] = "station", ["ステーション"] = "station",
    };

    private static readonly Dictionary<string, string> Roles = new(StringComparer.Ordinal)
    {
        ["主"] = "primary", ["主務"] = "primary", ["副"] = "secondary", ["兼務"] = "secondary",
    };

    public static string? FloorKind(string value) => Lookup(value, WorkplaceCodes.FloorKinds, FloorKinds);

    public static string? LayoutElement(string value) =>
        Lookup(value, [Outline, Fixture, Zone, Seat, FloorOnly], Elements);

    /// <summary>ゾーンか固定の部分の種類（どちらの種類かは要素で決まる。照合は計画で行う）。</summary>
    public static string? LayoutKind(string value) =>
        Lookup(value, [.. WorkplaceCodes.ZoneKinds, .. WorkplaceCodes.FixtureKinds], LayoutKinds);

    public static string? SeatKind(string value) => Lookup(value, WorkplaceCodes.SeatKinds, SeatKinds);

    public static string? Role(string value) => Lookup(value, WorkplaceCodes.Roles, Roles);

    /// <summary>設備のタグ（カンマ・読点・セミコロン・空白区切り）。許可リストに無いタグがあれば null（健康・障害に関わるタグは作らない）。</summary>
    public static string? Features(string value)
    {
        var tags = value.Split([',', '、', ';', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return tags.Count > WorkplaceCodes.MaxFeaturesPerSeat || tags.Any(t => !WorkplaceCodes.Features.Contains(t, StringComparer.Ordinal))
            ? null
            : string.Join(',', tags.Order(StringComparer.Ordinal));
    }

    /// <summary>多角形（[[x, y], …] の JSON）。形が不正なら null。整えた JSON を返す。</summary>
    public static string? Polygon(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            var polygon = LayoutJson.ReadPolygon(document.RootElement);
            return FloorGeometry.Validate(polygon) is null ? LayoutJson.WritePolygon(polygon) : null;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }
    }

    private static string? Lookup(string value, IReadOnlyList<string> codes, Dictionary<string, string> labels)
    {
        var lower = value.ToLowerInvariant();
        return codes.Contains(lower, StringComparer.Ordinal) ? lower : labels.GetValueOrDefault(value);
    }
}
