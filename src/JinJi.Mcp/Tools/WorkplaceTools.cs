using System.Globalization;
using System.Text.Json;
using JinJi.Application.Access;
using JinJi.Application.People;
using JinJi.Application.Workplace;
using JinJi.Domain;
using JinJi.Domain.Workplace;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.Mcp.Tools;

/// <summary>拠点・建物の階の一覧と席数（REST の <c>GET /api/v1/sites/{id}/buildings</c>・<c>/buildings/{id}</c> と同じサービス・同じ規則）。人は返さない。</summary>
internal sealed class ListFloorsTool : McpTool
{
    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "site_id": { "type": "string", "format": "uuid", "description": "拠点の ID（list_sites で探す）。building_id とどちらか一方" },
            "building_id": { "type": "string", "format": "uuid", "description": "建物の ID。site_id とどちらか一方" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" }
          },
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.ListFloors;

    public override string Title => "建物と階の一覧";

    public override string Description =>
        "拠点（site_id）か建物（building_id）の階の一覧を返す。建物は code・name、階は id・label（1F・B1 など）・level_order（下の階が小さい）・kind と、seat_count（席数）・"
        + "occupied_count（委任元に見える人の席）・vacant_count（空席。割り当ての無い席）・unavailable_count（保守中・予備と、見えない人の席）。"
        + "人は返さない。建物・階は全社公開。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.OrgStats, false);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var siteId = arguments.Id("site_id");
        var buildingId = arguments.Id("building_id");
        if ((siteId is null) == (buildingId is null))
        {
            throw McpToolException.Invalid("site_id か building_id のどちらか一方を指定してください。");
        }

        var asOf = arguments.Date("as_of");
        var workplace = context.Services.GetRequiredService<WorkplaceDirectoryService>();
        if (siteId is { } site)
        {
            var result = await workplace.ListBuildingsAsync(context.Viewer, site, asOf, cancellationToken) ?? throw McpToolException.NotFound("その拠点");
            return new McpToolOutput(
                new { as_of = result.AsOf, site_id = result.Site.Id, buildings = result.Buildings.Select(McpBuilding.From).ToList() },
                $"建物 {result.Buildings.Count} 棟（階 {result.Buildings.Sum(b => b.Floors.Count)} 階）を返しました。",
                []);
        }

        var detail = await workplace.GetBuildingAsync(context.Viewer, buildingId!.Value, asOf, cancellationToken) ?? throw McpToolException.NotFound("その建物");
        return new McpToolOutput(
            new { as_of = detail.AsOf, buildings = new[] { McpBuilding.From(detail.Building) } },
            $"建物 1 棟（階 {detail.Building.Floors.Count} 階）を返しました。",
            []);
    }
}

/// <summary>空席を探す（REST の <c>GET /api/v1/seats?vacant=true</c> と同じサービス・同じ規則）。人は返さない。</summary>
internal sealed class FindVacantSeatsTool : McpTool
{
    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "floor_ids": { "type": "array", "items": { "type": "string", "format": "uuid" }, "minItems": 1, "maxItems": {{PersonSearch.MaxValuesPerFilter}}, "uniqueItems": true, "description": "階の ID（list_floors で探す）" },
            "building_id": { "type": "string", "format": "uuid", "description": "建物の ID（建物のすべての階）" },
            "site_id": { "type": "string", "format": "uuid", "description": "拠点の ID（拠点のすべての建物の階）" },
            "zone_key": { "type": "string", "format": "uuid", "description": "ゾーン（エリア）の zone_key" },
            "kinds": { "type": "array", "items": { "type": "string", "enum": ["fixed", "station"] }, "maxItems": 2, "uniqueItems": true, "description": "席の種類（fixed 固定席 / station 持ち場）" },
            "features": { "type": "array", "items": { "type": "string", "enum": [{{string.Join(", ", WorkplaceCodes.Features.Select(f => $"\"{f}\""))}}] }, "maxItems": {{WorkplaceCodes.MaxFeaturesPerSeat}}, "uniqueItems": true, "description": "設備のタグ（すべてを持つ席）" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" },
            "limit": { "type": "integer", "minimum": 1, "maximum": {{SeatSearch.MaxLimit}}, "default": 50 }
          },
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.FindVacantSeats;

    public override string Title => "空席を探す";

    public override string Description =>
        "階（floor_ids）・建物（building_id）・拠点（site_id）のいずれかの範囲で、空席（割り当ての無い席。予約の空きではない。座席予約は扱わない）を探す。"
        + $"zone_key・kinds・features で絞れる。結果は席番号・階・ゾーン・種類・定員・設備のタグで、人は返さない。保守中・見えない人の席は空席として返さない。最大 {SeatSearch.MaxLimit} 件。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.OrgStats, false);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var search = new SeatSearch
        {
            FloorIds = arguments.Has("floor_ids") ? arguments.Ids("floor_ids", 1, PersonSearch.MaxValuesPerFilter).ToHashSet() : new HashSet<Guid>(),
            BuildingId = arguments.Id("building_id"),
            SiteId = arguments.Id("site_id"),
            ZoneKey = arguments.Id("zone_key"),
            Kinds = arguments.ChoiceList("kinds", 2, WorkplaceCodes.SeatKinds).ToHashSet(StringComparer.Ordinal),
            Features = arguments.ChoiceList("features", WorkplaceCodes.MaxFeaturesPerSeat, WorkplaceCodes.Features).ToHashSet(StringComparer.Ordinal),
            VacantOnly = true,
            Limit = arguments.Integer("limit", 50, 1, SeatSearch.MaxLimit),
        };

        SeatSearchResult result;
        try
        {
            result = await context.Services.GetRequiredService<WorkplaceDirectoryService>().SearchSeatsAsync(context.Viewer, search, arguments.Date("as_of"), cancellationToken);
        }
        catch (ArgumentException ex)
        {
            throw McpToolException.Invalid(ex.Message.Split(" (Parameter", 2)[0]);
        }

        return new McpToolOutput(
            new
            {
                as_of = result.AsOf,
                items = result.Items.Select(h => new
                {
                    seat_id = h.Seat.SeatId,
                    code = h.Seat.Code,
                    building_id = h.Building.Id,
                    building_name = h.Building.Name,
                    floor_id = h.Floor.Id,
                    floor_label = h.Floor.Label,
                    zone_key = h.Zone?.ZoneKey,
                    zone_name = h.Zone?.Name,
                    kind = WorkplaceCodes.ToCode(h.Seat.Kind),
                    capacity = h.Seat.Capacity,
                    features = h.Seat.Features,
                }).ToList(),
                has_more = result.HasMore,
            },
            $"空席 {result.Items.Count} 席を返しました。" + (result.HasMore ? "さらにあります（範囲を絞ってください）。" : string.Empty),
            []);
    }
}

/// <summary>階の在籍者と席（REST の <c>GET /api/v1/floors/{id}/occupants</c> と同じサービス・同じ規則）。所属上の席だけで、当日の所在は含めない。</summary>
internal sealed class ListFloorOccupantsTool : McpTool
{
    private const int DefaultLimit = 50;

    private static readonly JsonElement Schema = McpTexts.Schema(
        $$"""
        {
          "type": "object",
          "properties": {
            "floor_id": { "type": "string", "format": "uuid", "description": "階の ID（list_floors で探す）" },
            "zone_key": { "type": "string", "format": "uuid", "description": "ゾーン（エリア）の zone_key で絞る" },
            "limit": { "type": "integer", "minimum": 1, "maximum": {{FloorOccupantQuery.MaxLimit}}, "default": {{DefaultLimit}} },
            "cursor": { "type": "string", "maxLength": 2048, "description": "前の結果の next_cursor（続きを取るときだけ。他の引数は前と同じにする）" },
            "as_of": { "type": "string", "pattern": "^\\d{4}-\\d{2}-\\d{2}$", "description": "{{McpTexts.AsOf}}" }
          },
          "required": ["floor_id"],
          "additionalProperties": false
        }
        """);

    public override string Name => McpToolNames.ListFloorOccupants;

    public override string Title => "階の在籍者と席";

    public override string Description =>
        "階の在籍者（所属上の席・持ち場）を席番号の順に返す。items は person（見える項目だけ）・seat_id・seat_code・zone_key・zone_name・role（primary / secondary）・"
        + "source（seat 席 / zone ゾーンの割り当て）。席の人は委任元に席が見える人だけ、席の無い持ち場の人は階・エリアが見える人だけ。"
        + "当日の所在は含めない（今日その階にいる人の一覧は引けない）。"
        + $"1 ページ最大 {FloorOccupantQuery.MaxLimit} 人（既定 {DefaultLimit}）。next_cursor があれば cursor に渡して続きを取る。{McpTexts.PersonShape}。";

    public override JsonElement InputSchema => Schema;

    public override RequestKindMetadata Kind { get; } = new(RequestKind.PersonList, true);

    public override async Task<McpToolOutput> InvokeAsync(McpToolContext context, McpArguments arguments, CancellationToken cancellationToken)
    {
        var floorId = arguments.RequiredId("floor_id");
        var zoneKey = arguments.Id("zone_key");
        var limit = arguments.Integer("limit", DefaultLimit, 1, FloorOccupantQuery.MaxLimit);
        var cursor = arguments.String("cursor", 2048);
        var asOf = arguments.Date("as_of") ?? BusinessDate.Today(context.Services.GetRequiredService<TimeProvider>());
        var cursors = context.Services.GetRequiredService<CursorProtector>();
        var purpose = "mcp:floor-occupants:" + CursorProtector.Hash(floorId.ToString("D"), zoneKey?.ToString("D"), asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        string? after = null;
        if (cursor is not null)
        {
            if (!cursors.TryUnprotect<OccupantCursor>(purpose, context.Viewer, cursor, out var position) || position is null)
            {
                throw McpToolException.Invalid("cursor が不正です（改ざん・別の条件・別の利用者のカーソルは使えません）。cursor を省略して最初から取り直してください。");
            }

            after = position.After;
        }

        var page = await context.Services.GetRequiredService<WorkplaceDirectoryService>()
            .ListOccupantsAsync(context.Viewer, floorId, new FloorOccupantQuery { ZoneKey = zoneKey, After = after, Limit = limit }, asOf, cancellationToken)
            ?? throw McpToolException.NotFound("その階");
        var next = page.NextAfter is { } nextAfter ? cursors.Protect(purpose, context.Viewer, new OccupantCursor(nextAfter)) : null;
        var people = page.Items.Select(o => o.Person.PersonId).Distinct().ToList();
        return new McpToolOutput(
            new
            {
                as_of = page.AsOf,
                floor_id = floorId,
                items = page.Items.Select(o => new
                {
                    person = McpPerson.From(o.Person),
                    seat_id = o.Seat?.SeatId,
                    seat_code = o.Seat?.Code,
                    zone_key = o.Zone?.ZoneKey,
                    zone_name = o.Zone?.Name,
                    role = WorkplaceCodes.ToCode(o.Role),
                    source = o.Source == WorkplaceSource.Seat ? "seat" : "zone",
                }).ToList(),
                next_cursor = next,
            },
            $"{people.Count} 人を返しました。" + (next is null ? "続きはありません。" : "続きがあります（next_cursor を cursor に渡す）。"),
            people,
            people.Count);
    }

    internal sealed record OccupantCursor(string After);
}

/// <summary>建物（REST API の <c>BuildingResponse</c> と同じ項目）。</summary>
internal sealed record McpBuilding(Guid Id, Guid SiteId, string Code, string Name, int SortOrder, IReadOnlyList<McpFloor> Floors)
{
    public static McpBuilding From(BuildingSummary b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return new McpBuilding(b.Building.Id, b.Building.SiteId, b.Building.Code, b.Building.Name, b.Building.SortOrder, [.. b.Floors.Select(McpFloor.From)]);
    }
}

/// <summary>階の要約（REST API の <c>FloorSummaryResponse</c> と同じ項目）。</summary>
internal sealed record McpFloor(
    Guid Id,
    string Label,
    int LevelOrder,
    string Kind,
    DateOnly? LayoutValidFrom,
    int SeatCount,
    int OccupiedCount,
    int VacantCount,
    int UnavailableCount)
{
    public static McpFloor From(FloorSummary s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new McpFloor(
            s.Floor.Id, s.Floor.Label, s.Floor.LevelOrder, WorkplaceCodes.ToCode(s.Floor.Kind), s.Layout?.ValidFrom, s.SeatCount, s.OccupiedCount, s.VacantCount,
            s.UnavailableCount);
    }
}
