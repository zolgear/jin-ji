using System.Security.Cryptography;
using System.Text.Json;
using JinJi.Application.Access;
using JinJi.Application.Presence;
using JinJi.Application.Workplace;
using JinJi.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace JinJi.Api;

/// <summary>
/// 建物・階・配置図・席の参照（建物・フロアモードの仕様 §6.1）。建物・階・配置図は全社公開で、件数は閲覧者に見える人だけで数える。
/// </summary>
internal static class WorkplaceEndpoints
{
    private const string OccupantsPurpose = "floor-occupants";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/sites/{id}/buildings", ListBuildingsAsync)
            .WithName("ListSiteBuildings")
            .WithSummary("拠点の建物と階の一覧（階ごとの席数・使用中・空席・使えない席）")
            .WithDescription(
                "その時点で有効な建物（並び順・建物コードの順）と階（下の階が先）。件数は閲覧者に見える人だけで数え、閲覧者に席が見えない人の席・保守中の席は"
                + "使えない席（unavailable_count）に数える（空席に数えない）。空席は割り当ての無い席の意味だけ（予約の空きではない）。"
                + "人は返さない（集計の種別 org_stats）。拠点がその時点に無ければ 404。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgStats, false))
            .Produces<BuildingListResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/buildings/{id}", GetBuildingAsync)
            .WithName("GetBuilding")
            .WithSummary("建物と階の一覧を参照する")
            .WithDescription("件数の規則は拠点の建物の一覧と同じ。その時点に無い建物（人事管理者以外には新設の予定）は 404。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgStats, false))
            .Produces<BuildingDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/floors/{id}/layout", GetLayoutAsync)
            .WithName("GetFloorLayout")
            .WithSummary("その日の配置図（外形・固定の部分・ゾーン・席の置き場所と状態）")
            .WithDescription(
                "座標は階ごとのローカル座標（整数のミリメートル。原点は左下、x は右、y は奥）。席の状態は vacant（空席）/ occupied（閲覧者に席が見える人の席）/ "
                + "unavailable（保守中・予備と、閲覧者に見えない人の席。区別しない）。人は返さない（人と席の対応は /floors/{id}/occupants）。"
                + "改訂の有効期間の終了日は返さない。配置図の無い階は layout が null。ETag を返し、If-None-Match が一致すれば 304。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgRead, false))
            .Produces<FloorLayoutResponse>()
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/floors/{id}/occupants", ListOccupantsAsync)
            .WithName("ListFloorOccupants")
            .WithSummary("階の在籍者と席（所属上の席だけ。当日の所在は含めない）")
            .WithDescription(
                "席の割り当ての人は閲覧者に席（seat）が見える人だけ、席の無い持ち場の人（ゾーンの割り当て）は階・エリア（workplace）が見える人だけ。"
                + $"zone（ゾーンの zone_key）で絞れる。並びは席番号の順（席の無い人は後）。limit は 1〜{FloorOccupantQuery.MaxLimit}（既定 50）、続きは next_cursor。"
                + "返した人数は返却人数の制限に数える。")
            .WithMetadata(new RequestKindMetadata(RequestKind.PersonList, true))
            .Produces<FloorOccupantListResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/floors/{id}/presence", GetPresenceAsync)
            .WithName("GetFloorPresence")
            .WithSummary("今日の階の所在（外部の提供を注入したときだけ）")
            .WithDescription(
                "jin-ji は所在を保持せず、配備側が登録した提供（IPresenceProvider）の今日の所在に jin-ji の認可を適用して返す。提供が無ければ 404（type は presence-unavailable）。"
                + "今日だけで、時点（as_of）は受け付けない。人は閲覧者に当日の所在（presence）が見える人だけ（既定の公開範囲は本人と同じ部。ポリシーと個人単位の例外でさらに狭まる）、"
                + "席番号は席（seat）も見えるときだけ。seats は所在のある席の状態だけ（occupied 見える人 / unavailable 見えない人）。所在が無いことは不在・在宅・休みを意味しない。"
                + "応答は Cache-Control: no-store。変更フィード・Webhook・監査ログには記録しない。返した人数は返却人数の制限に数える。")
            .WithMetadata(new RequestKindMetadata(RequestKind.PersonList, true))
            .Produces<FloorPresenceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/seats", SearchSeatsAsync)
            .WithName("SearchSeats")
            .WithSummary("席の検索（空席など。人は返さない）")
            .WithDescription(
                "floor（階の ID。繰り返し可）・building・site のいずれかで範囲を決める。zone（zone_key）・kind（fixed / station）・feature（設備のタグ。すべてを持つ席）・"
                + "vacant=true（空席だけ。空席は割り当ての無い席で、予約の空きではない。使えない席は空席として返さない）で絞る。"
                + $"limit は 1〜{SeatSearch.MaxLimit}（既定 50）。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgStats, false))
            .Produces<SeatSearchResponse>();
    }

    private static async Task<IResult> ListBuildingsAsync(
        string id,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        WorkplaceDirectoryService workplace,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(id, out var siteId))
        {
            return Problems.NotFound(http);
        }

        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        return await workplace.ListBuildingsAsync(http.Viewer(), siteId, date, cancellationToken) is { } result
            ? Results.Ok(new BuildingListResponse(result.AsOf, result.Site.Id, [.. result.Buildings.Select(BuildingResponse.From)]))
            : Problems.NotFound(http);
    }

    private static async Task<IResult> GetBuildingAsync(
        string id,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        WorkplaceDirectoryService workplace,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(id, out var buildingId))
        {
            return Problems.NotFound(http);
        }

        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        return await workplace.GetBuildingAsync(http.Viewer(), buildingId, date, cancellationToken) is { } result
            ? Results.Ok(new BuildingDetailResponse(result.AsOf, BuildingResponse.From(result.Building)))
            : Problems.NotFound(http);
    }

    private static async Task<IResult> GetLayoutAsync(
        string id,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        WorkplaceDirectoryService workplace,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(id, out var floorId))
        {
            return Problems.NotFound(http);
        }

        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        if (await workplace.GetLayoutAsync(http.Viewer(), floorId, date, cancellationToken) is not { } result)
        {
            return Problems.NotFound(http);
        }

        // 配置図は大きい（3,000 席で約 150 KB）ので、ETag で使い回す。席の状態は閲覧者ごとに違うので private（共有のキャッシュに置かない）
        var response = FloorLayoutResponse.From(result);
        var body = JsonSerializer.SerializeToUtf8Bytes(response, json.Value.SerializerOptions);
        var etag = $"\"{Convert.ToHexStringLower(SHA256.HashData(body))[..32]}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, no-cache";
        if (http.Request.Headers.IfNoneMatch.Any(v => string.Equals(v, etag, StringComparison.Ordinal)))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Bytes(body, "application/json");
    }

    private static async Task<IResult> ListOccupantsAsync(
        string id,
        [FromQuery(Name = "zone")] string? zone,
        [FromQuery(Name = "limit")] string? limit,
        [FromQuery(Name = "cursor")] string? cursor,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        WorkplaceDirectoryService workplace,
        CursorProtector cursors,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(id, out var floorId))
        {
            return Problems.NotFound(http);
        }

        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        Guid? zoneKey = null;
        if (!string.IsNullOrEmpty(zone))
        {
            if (!ApiRequest.TryParseId(zone, out var parsed))
            {
                return Problems.BadRequest(http, "zone はゾーンの zone_key（UUID）で指定してください。");
            }

            zoneKey = parsed;
        }

        if (!ApiRequest.TryParseLimit(limit, PeopleEndpoints.DefaultLimit, FloorOccupantQuery.MaxLimit, out var count))
        {
            return Problems.BadRequest(http, $"limit は 1〜{FloorOccupantQuery.MaxLimit} で指定してください。");
        }

        var viewer = http.Viewer();
        var day = date ?? BusinessDate.Today(timeProvider);
        var purpose = $"{OccupantsPurpose}:{CursorProtector.Hash(floorId.ToString("D"), zoneKey?.ToString("D"), day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))}";
        string? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!cursors.TryUnprotect<OccupantCursor>(purpose, viewer, cursor, out var position) || position is null)
            {
                return Problems.BadRequest(http, "cursor が不正です（条件・時点を変えたら最初から取り直してください）。", Problems.InvalidCursorType);
            }

            after = position.After;
        }

        var page = await workplace.ListOccupantsAsync(viewer, floorId, new FloorOccupantQuery { ZoneKey = zoneKey, After = after, Limit = count }, day, cancellationToken);
        if (page is null)
        {
            return Problems.NotFound(http);
        }

        Problems.Observe(http).PeopleReturned = page.Items.Select(o => o.Person.PersonId).Distinct().Count();
        return Results.Ok(new FloorOccupantListResponse(
            page.AsOf,
            floorId,
            [.. page.Items.Select(FloorOccupantResponse.From)],
            page.NextAfter is { } next ? cursors.Protect(purpose, viewer, new OccupantCursor(next)) : null));
    }

    private static async Task<IResult> SearchSeatsAsync(
        [FromQuery(Name = "floor")] string[]? floors,
        [FromQuery(Name = "building")] string? building,
        [FromQuery(Name = "site")] string? site,
        [FromQuery(Name = "zone")] string? zone,
        [FromQuery(Name = "kind")] string[]? kinds,
        [FromQuery(Name = "feature")] string[]? features,
        [FromQuery(Name = "vacant")] string? vacant,
        [FromQuery(Name = "limit")] string? limit,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        WorkplaceDirectoryService workplace,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        var floorIds = new HashSet<Guid>();
        foreach (var value in floors ?? [])
        {
            if (!ApiRequest.TryParseId(value, out var floorId))
            {
                return Problems.BadRequest(http, "floor は階の ID（UUID）で指定してください。");
            }

            floorIds.Add(floorId);
        }

        Guid? buildingId = null;
        Guid? siteId = null;
        Guid? zoneKey = null;
        foreach (var (name, value, set) in new (string, string?, Action<Guid>)[]
                 {
                     ("building", building, g => buildingId = g), ("site", site, g => siteId = g), ("zone", zone, g => zoneKey = g),
                 })
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!ApiRequest.TryParseId(value, out var parsed))
            {
                return Problems.BadRequest(http, $"{name} は ID（UUID）で指定してください。");
            }

            set(parsed);
        }

        if (!ApiRequest.TryParseFlag(vacant, out var vacantOnly))
        {
            return Problems.BadRequest(http, "vacant は true / false で指定してください。");
        }

        if (!ApiRequest.TryParseLimit(limit, PeopleEndpoints.DefaultLimit, SeatSearch.MaxLimit, out var count))
        {
            return Problems.BadRequest(http, $"limit は 1〜{SeatSearch.MaxLimit} で指定してください。");
        }

        try
        {
            var result = await workplace.SearchSeatsAsync(
                http.Viewer(),
                new SeatSearch
                {
                    FloorIds = floorIds,
                    BuildingId = buildingId,
                    SiteId = siteId,
                    ZoneKey = zoneKey,
                    Kinds = new HashSet<string>(kinds ?? [], StringComparer.Ordinal),
                    Features = new HashSet<string>(features ?? [], StringComparer.Ordinal),
                    VacantOnly = vacantOnly,
                    Limit = count,
                },
                date,
                cancellationToken);
            return Results.Ok(new SeatSearchResponse(result.AsOf, [.. result.Items.Select(SeatSearchItemResponse.From)], result.HasMore));
        }
        catch (ArgumentException ex)
        {
            return Problems.BadRequest(http, ex.Message.Split(" (Parameter", 2)[0]);
        }
    }

    private static async Task<IResult> GetPresenceAsync(
        string id,
        HttpContext http,
        PresenceService presence,
        CancellationToken cancellationToken)
    {
        // 所在は短命の情報なので、どの応答（404 を含む）も共有・私的なキャッシュに置かせない
        http.Response.Headers.CacheControl = "no-store";
        if (!ApiRequest.TryParseId(id, out var floorId))
        {
            return Problems.NotFound(http);
        }

        try
        {
            if (await presence.GetFloorAsync(http.Viewer(), floorId, cancellationToken) is not { } result)
            {
                return Problems.NotFound(http);
            }

            Problems.Observe(http).PeopleReturned = result.Items.Count;
            return Results.Ok(FloorPresenceResponse.From(result));
        }
        catch (PresenceUnavailableException)
        {
            return Problems.PresenceUnavailable(http);
        }
    }

    private sealed record OccupantCursor(string After);
}
