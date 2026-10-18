using System.Globalization;
using System.Text.Json;
using JinJi.Ingest;

namespace JinJi.Connectors;

/// <summary>
/// jin-ji の配置図の形式 v1（JSON）を、要素ごとの行（外形・固定の部分・ゾーン・席。配置図の無い階は階だけの行）の表にする（0019）。
/// 見出しは取込項目の名前で、項目マッピングは同じ名前の列を対応させる（samples の floor-layouts マッピング）。
/// </summary>
/// <remarks>
/// <code>
/// {
///   "format": "jinji-floor-layout/v1",
///   "site_code": "TKY-HQ",
///   "buildings": [{
///     "code": "MAIN", "name": "本館", "sort_order": 0,
///     "floors": [{
///       "label": "3F", "level_order": 30, "kind": "office",
///       "layout": {
///         "valid_from": "2026-04-01", "grid_mm": 100,
///         "outline": [[0, 0], [40000, 0], [40000, 20000], [0, 20000]],
///         "fixtures": [{ "kind": "core", "name": "コア", "polygon": [[…]] }],
///         "zones": [{ "name": "3F 西", "kind": "office", "polygon": [[…]] }],
///         "seats": [{ "code": "3F-A01", "x_mm": 1200, "y_mm": 1500, "rotation_deg": 0, "desk_type": "desk_1200",
///                     "kind": "fixed", "capacity": 1, "features": ["monitor"], "zone": "3F 西", "out_of_service": false }]
///       }
///     }]
///   }]
/// }
/// </code>
/// <list type="bullet">
/// <item>CAD・BIM・図面画像・SVG は取り込まない（SVG は表示と書き出しにだけ使う。取り込むと保存型の XSS の入口になるため）</item>
/// <item>知らないプロパティは捨てる（取込項目の許可リストに無い値を staging に入れない）。値は文字列にし、形の検査は正規化と計画で行う</item>
/// </list>
/// </remarks>
public static class FloorLayoutJson
{
    /// <summary>形式の名前。</summary>
    public const string Format = "jinji-floor-layout/v1";

    private static readonly string[] Headers =
    [
        IngestFields.SiteCode, IngestFields.BuildingCode, IngestFields.BuildingName, IngestFields.BuildingSortOrder,
        IngestFields.FloorLabel, IngestFields.FloorLevelOrder, IngestFields.FloorKind, IngestFields.LayoutValidFrom, IngestFields.LayoutGridMm,
        IngestFields.LayoutElement, IngestFields.LayoutName, IngestFields.LayoutKind, IngestFields.LayoutPolygon,
        IngestFields.SeatCode, IngestFields.SeatXMm, IngestFields.SeatYMm, IngestFields.SeatRotation, IngestFields.SeatDeskType, IngestFields.SeatKind,
        IngestFields.SeatCapacity, IngestFields.SeatFeatures, IngestFields.SeatZone, IngestFields.SeatOutOfService,
    ];

    /// <summary>読む。形式が違う・構造が不正なら <see cref="FormatException"/>。</summary>
    public static RawTable Read(ReadOnlySpan<byte> content)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content.ToArray());
        }
        catch (JsonException ex)
        {
            throw new FormatException("配置図の JSON を読めません。", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Text(root, "format") != Format)
            {
                throw new FormatException($"配置図の JSON の format は {Format} にしてください。");
            }

            var rows = new List<RawRow>();
            var site = Text(root, "site_code");
            foreach (var building in Array(root, "buildings"))
            {
                foreach (var floor in Array(building, "floors"))
                {
                    var common = new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        [IngestFields.SiteCode] = Text(building, "site_code") ?? site,
                        [IngestFields.BuildingCode] = Text(building, "code"),
                        [IngestFields.BuildingName] = Text(building, "name"),
                        [IngestFields.BuildingSortOrder] = Text(building, "sort_order"),
                        [IngestFields.FloorLabel] = Text(floor, "label"),
                        [IngestFields.FloorLevelOrder] = Text(floor, "level_order"),
                        [IngestFields.FloorKind] = Text(floor, "kind"),
                    };

                    if (!floor.TryGetProperty("layout", out var layout) || layout.ValueKind != JsonValueKind.Object)
                    {
                        rows.Add(Row(rows.Count + 1, common, new() { [IngestFields.LayoutElement] = "floor" }));
                        continue;
                    }

                    common[IngestFields.LayoutValidFrom] = Text(layout, "valid_from");
                    common[IngestFields.LayoutGridMm] = Text(layout, "grid_mm");
                    rows.Add(Row(rows.Count + 1, common, new()
                    {
                        [IngestFields.LayoutElement] = "outline",
                        [IngestFields.LayoutPolygon] = Raw(layout, "outline"),
                    }));
                    foreach (var fixture in Array(layout, "fixtures"))
                    {
                        rows.Add(Row(rows.Count + 1, common, new()
                        {
                            [IngestFields.LayoutElement] = "fixture",
                            [IngestFields.LayoutName] = Text(fixture, "name"),
                            [IngestFields.LayoutKind] = Text(fixture, "kind"),
                            [IngestFields.LayoutPolygon] = Raw(fixture, "polygon"),
                        }));
                    }

                    foreach (var zone in Array(layout, "zones"))
                    {
                        rows.Add(Row(rows.Count + 1, common, new()
                        {
                            [IngestFields.LayoutElement] = "zone",
                            [IngestFields.LayoutName] = Text(zone, "name"),
                            [IngestFields.LayoutKind] = Text(zone, "kind"),
                            [IngestFields.LayoutPolygon] = Raw(zone, "polygon"),
                        }));
                    }

                    foreach (var seat in Array(layout, "seats"))
                    {
                        rows.Add(Row(rows.Count + 1, common, new()
                        {
                            [IngestFields.LayoutElement] = "seat",
                            [IngestFields.SeatCode] = Text(seat, "code"),
                            [IngestFields.SeatXMm] = Text(seat, "x_mm"),
                            [IngestFields.SeatYMm] = Text(seat, "y_mm"),
                            [IngestFields.SeatRotation] = Text(seat, "rotation_deg"),
                            [IngestFields.SeatDeskType] = Text(seat, "desk_type"),
                            [IngestFields.SeatKind] = Text(seat, "kind"),
                            [IngestFields.SeatCapacity] = Text(seat, "capacity"),
                            [IngestFields.SeatFeatures] = seat.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array
                                ? string.Join(',', features.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()))
                                : Text(seat, "features"),
                            [IngestFields.SeatZone] = Text(seat, "zone"),
                            [IngestFields.SeatOutOfService] = Text(seat, "out_of_service"),
                        }));
                    }
                }
            }

            return new RawTable(Headers, rows);
        }
    }

    private static RawRow Row(int number, Dictionary<string, string?> common, Dictionary<string, string?> values)
    {
        var all = new Dictionary<string, string?>(common, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            all[key] = value;
        }

        return new RawRow(number, [.. Headers.Select(h => all.GetValueOrDefault(h) ?? string.Empty)]);
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                : throw new FormatException($"{name} は配列にしてください。")
            : [];

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            }
            : null;

    private static string? Raw(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.GetRawText()
            : null;
}
