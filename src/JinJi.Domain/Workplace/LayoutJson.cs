using System.Text.Json;

namespace JinJi.Domain.Workplace;

/// <summary>改訂の起案（変更提案の payload の <c>layout</c>。改訂 1 つ分）。承認で floor_layout・layout_zone・layout_seat になる。</summary>
/// <param name="Seats">席（<see cref="DraftSeat.IsNew"/> の席は承認時に seat の行も作る）。</param>
public sealed record LayoutDraft(
    int GridMm,
    IReadOnlyList<LayoutPoint> Outline,
    IReadOnlyList<LayoutFixture> Fixtures,
    IReadOnlyList<LayoutZone> Zones,
    IReadOnlyList<DraftSeat> Seats);

/// <summary>改訂の起案の席。</summary>
/// <param name="IsNew">席番号が階に無い（承認時に席の不変 ID を作る）。</param>
public sealed record DraftSeat(Guid SeatId, bool IsNew, LayoutSeat Placement);

/// <summary>
/// 配置図の JSON（DB の jsonb と変更提案の payload）。多角形は <c>[[x_mm, y_mm], …]</c>、固定の部分は <c>[{kind, name, polygon}]</c>。
/// </summary>
public static class LayoutJson
{
    public static IReadOnlyList<LayoutPoint> ParsePolygon(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return ReadPolygon(document.RootElement);
    }

    public static IReadOnlyList<LayoutFixture> ParseFixtures(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.EnumerateArray().Select(ReadFixture)];
    }

    public static string WritePolygon(IReadOnlyList<LayoutPoint> polygon) => Write(w => WritePolygon(w, polygon));

    public static string WriteFixtures(IReadOnlyList<LayoutFixture> fixtures) => Write(w => WriteFixtures(w, fixtures));

    /// <summary>改訂の起案を JSON にする。</summary>
    public static string WriteDraft(LayoutDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return Write(w =>
        {
            w.WriteStartObject();
            w.WriteNumber("grid_mm", draft.GridMm);
            w.WritePropertyName("outline");
            WritePolygon(w, draft.Outline);
            w.WritePropertyName("fixtures");
            WriteFixtures(w, draft.Fixtures);
            w.WriteStartArray("zones");
            foreach (var zone in draft.Zones)
            {
                w.WriteStartObject();
                w.WriteString("zone_key", zone.ZoneKey);
                w.WriteString("name", zone.Name);
                w.WriteString("kind", WorkplaceCodes.ToCode(zone.Kind));
                w.WritePropertyName("polygon");
                WritePolygon(w, zone.Polygon);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("seats");
            foreach (var seat in draft.Seats)
            {
                var p = seat.Placement;
                w.WriteStartObject();
                w.WriteString("seat_id", seat.SeatId);
                w.WriteBoolean("new", seat.IsNew);
                w.WriteString("code", p.Code);
                if (p.ZoneKey is { } zone)
                {
                    w.WriteString("zone_key", zone);
                }

                w.WriteNumber("x_mm", p.XMm);
                w.WriteNumber("y_mm", p.YMm);
                w.WriteNumber("rotation_deg", p.RotationDeg);
                w.WriteString("desk_type", p.DeskType);
                w.WriteString("kind", WorkplaceCodes.ToCode(p.Kind));
                w.WriteNumber("capacity", p.Capacity);
                w.WriteStartArray("features");
                foreach (var feature in p.Features)
                {
                    w.WriteStringValue(feature);
                }

                w.WriteEndArray();
                w.WriteBoolean("out_of_service", p.OutOfService);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>改訂の起案を読む。形が不正なら <see cref="FormatException"/>。</summary>
    public static LayoutDraft ParseDraft(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var zones = root.GetProperty("zones").EnumerateArray().Select(z => new LayoutZone(
                z.GetProperty("zone_key").GetGuid(),
                z.GetProperty("name").GetString()!,
                WorkplaceCodes.TryParse(z.GetProperty("kind").GetString(), out ZoneKind kind) ? kind : throw new FormatException("ゾーンの種類が不正です。"),
                ReadPolygon(z.GetProperty("polygon")))).ToList();
            var seats = root.GetProperty("seats").EnumerateArray().Select(s => new DraftSeat(
                s.GetProperty("seat_id").GetGuid(),
                s.GetProperty("new").GetBoolean(),
                new LayoutSeat(
                    s.GetProperty("seat_id").GetGuid(),
                    s.GetProperty("code").GetString()!,
                    s.TryGetProperty("zone_key", out var zone) ? zone.GetGuid() : null,
                    s.GetProperty("x_mm").GetInt32(),
                    s.GetProperty("y_mm").GetInt32(),
                    s.GetProperty("rotation_deg").GetInt32(),
                    s.GetProperty("desk_type").GetString()!,
                    WorkplaceCodes.TryParse(s.GetProperty("kind").GetString(), out SeatKind seatKind) ? seatKind : throw new FormatException("席の種類が不正です。"),
                    s.GetProperty("capacity").GetInt32(),
                    [.. s.GetProperty("features").EnumerateArray().Select(f => f.GetString()!)],
                    s.GetProperty("out_of_service").GetBoolean()))).ToList();
            return new LayoutDraft(
                root.GetProperty("grid_mm").GetInt32(),
                ReadPolygon(root.GetProperty("outline")),
                [.. root.GetProperty("fixtures").EnumerateArray().Select(ReadFixture)],
                zones,
                seats);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("配置図の改訂の形が不正です。", ex);
        }
    }

    /// <summary>多角形を読む（要素が [x, y] の整数の組でなければ <see cref="FormatException"/>）。</summary>
    public static IReadOnlyList<LayoutPoint> ReadPolygon(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("多角形は [[x, y], …] の配列にしてください。");
        }

        var points = new List<LayoutPoint>();
        foreach (var point in element.EnumerateArray())
        {
            if (point.ValueKind != JsonValueKind.Array || point.GetArrayLength() != 2
                || !point[0].TryGetInt32(out var x) || !point[1].TryGetInt32(out var y))
            {
                throw new FormatException("多角形の頂点は [x_mm, y_mm]（整数のミリメートル）にしてください。");
            }

            points.Add(new LayoutPoint(x, y));
        }

        return points;
    }

    private static LayoutFixture ReadFixture(JsonElement element) => new(
        WorkplaceCodes.TryParse(element.GetProperty("kind").GetString(), out FixtureKind kind) ? kind : throw new FormatException("固定の部分の種類が不正です。"),
        element.TryGetProperty("name", out var name) ? name.GetString() : null,
        ReadPolygon(element.GetProperty("polygon")));

    private static void WritePolygon(Utf8JsonWriter w, IReadOnlyList<LayoutPoint> polygon)
    {
        w.WriteStartArray();
        foreach (var point in polygon)
        {
            w.WriteStartArray();
            w.WriteNumberValue(point.X);
            w.WriteNumberValue(point.Y);
            w.WriteEndArray();
        }

        w.WriteEndArray();
    }

    private static void WriteFixtures(Utf8JsonWriter w, IReadOnlyList<LayoutFixture> fixtures)
    {
        w.WriteStartArray();
        foreach (var fixture in fixtures)
        {
            w.WriteStartObject();
            w.WriteString("kind", WorkplaceCodes.ToCode(fixture.Kind));
            if (fixture.Name is { } name)
            {
                w.WriteString("name", name);
            }

            w.WritePropertyName("polygon");
            WritePolygon(w, fixture.Polygon);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            write(writer);
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
