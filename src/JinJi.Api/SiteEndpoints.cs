using JinJi.Application.Access;
using JinJi.Application.Organization;
using JinJi.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>拠点の参照（<c>/api/v1/sites</c>）。拠点の情報は全社公開で、人数は閲覧者に見える人だけを数える。</summary>
internal static class SiteEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/sites", ListAsync)
            .WithName("ListSites")
            .WithSummary("拠点の一覧と、拠点ごとの人数")
            .WithDescription(
                "その時点で有効な拠点を、法人のコード・拠点コードの順に返す。query（拠点名の正規化表記・拠点コードの部分一致。ワイルドカード不可）・"
                + "legal_entity（法人の ID）・site_type（拠点の種別）・prefecture（都道府県のコード 01〜47）で絞り込める（同じ名前を繰り返すと OR、条件同士は AND）。"
                + "member_count・remote_work_count は閲覧者に結果として返し得る人のうち、拠点（とリモートワーク）の項目が見える人だけを数える（人の検索の件数と同じ規則）。"
                + "人は返さない（返却人数の制限の対象外で、集計の種別 org_stats で回数を制限する）。拠点の人の一覧は GET /people?site= で引く。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgStats, false))
            .Produces<SiteListResponse>();

        api.MapGet("/sites/{id}", GetAsync)
            .WithName("GetSite")
            .WithSummary("拠点を参照する")
            .WithDescription("その時点に無い拠点（閉鎖済み・人事管理者以外には開設の予定）は 404。人数の規則は一覧と同じ。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgStats, false))
            .Produces<SiteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(
        [FromQuery(Name = "query")] string? query,
        [FromQuery(Name = "legal_entity")] string[]? legalEntities,
        [FromQuery(Name = "site_type")] string[]? siteTypes,
        [FromQuery(Name = "prefecture")] string[]? prefectures,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        OrgDirectoryService orgs,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        var entityIds = new HashSet<Guid>();
        foreach (var value in legalEntities ?? [])
        {
            if (!ApiRequest.TryParseId(value, out var entity))
            {
                return Problems.BadRequest(http, "legal_entity は法人の ID（UUID）で指定してください。");
            }

            entityIds.Add(entity);
        }

        var search = new SiteSearch
        {
            Query = string.IsNullOrEmpty(query) ? null : query,
            LegalEntityIds = entityIds,
            Types = new HashSet<string>(siteTypes ?? [], StringComparer.Ordinal),
            Prefectures = new HashSet<string>(prefectures ?? [], StringComparer.Ordinal),
        };
        try
        {
            search.Validate();
        }
        catch (ArgumentException ex)
        {
            return Problems.BadRequest(http, ex.Message.Split(" (Parameter", 2)[0]);
        }

        var sites = await orgs.ListSitesAsync(http.Viewer(), search, date, cancellationToken);
        return Results.Ok(new SiteListResponse(date ?? BusinessDate.Today(timeProvider), [.. sites.Select(SiteResponse.From)]));
    }

    private static async Task<IResult> GetAsync(
        string id,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        OrgDirectoryService orgs,
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

        return await orgs.GetSiteAsync(http.Viewer(), siteId, date, cancellationToken) is { } site
            ? Results.Ok(SiteResponse.From(site))
            : Problems.NotFound(http);
    }
}
