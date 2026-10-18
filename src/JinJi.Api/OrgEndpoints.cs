using JinJi.Application.Access;
using JinJi.Application.Organization;
using JinJi.Application.People;
using JinJi.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>組織の参照（<c>/api/v1/orgs</c>）。</summary>
internal static class OrgEndpoints
{
    /// <summary>組織の検索の既定の件数。</summary>
    public const int DefaultSearchLimit = 50;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/orgs/tree", GetTreeAsync)
            .WithName("GetOrgTree")
            .WithSummary("組織ツリーを参照する")
            .WithDescription("その時点の全組織を、上位組織の ID（parent_id）で木を組める平らな一覧で返す。組織長（head_person_id）は閲覧者に見える人だけ。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgRead, false))
            .Produces<OrgTreeResponse>();

        api.MapGet("/orgs", SearchAsync)
            .WithName("SearchOrgs")
            .WithSummary("組織を検索する")
            .WithDescription($"query は組織名・略称（組織名の正規化表記: 異体字・全角半角・「第一」と「1」をそろえる）と組織コードの部分一致。ワイルドカード不可。完全一致・前方一致・部分一致の順。limit は 1〜{OrgSearch.MaxLimit}（超えた分は返さず has_more を true にする）。組織長（head_person_id）は閲覧者に見える人だけ。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgRead, false))
            .Produces<OrgSearchResponse>();

        api.MapGet("/orgs/stats", GetStatsAsync)
            .WithName("GetOrgStats")
            .WithSummary("組織ごとの人数と、兼務・出向の組織間の集計")
            .WithDescription("その時点の全組織の人数（主務・配下を含む主務・兼務・出向）と、主務の組織から兼務・出向の組織への所属の組ごとの人数。数えるのは閲覧者に見える人（所属者の一覧に現れる人）だけ。人は返さない（返却人数の制限の対象外で、集計の種別 org_stats で回数を制限する）。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgStats, false))
            .Produces<OrgStatsResponse>();

        api.MapGet("/orgs/{id}", GetUnitAsync)
            .WithName("GetOrg")
            .WithSummary("組織を参照する")
            .WithDescription("その時点に無い組織は 404（人事管理者以外には組織改編の予定は見えない）。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgRead, false))
            .Produces<OrgUnitResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/orgs/{id}/members", GetMembersAsync)
            .WithName("ListOrgMembers")
            .WithSummary("組織の所属者を参照する")
            .WithDescription($"主務・兼務・出向で所属する人（include_descendants で配下の組織も）。所属の項目が見えない人は含めない。limit は 1〜{PersonSearch.MaxLimit}。")
            .WithMetadata(new RequestKindMetadata(RequestKind.PersonList, true))
            .Produces<PersonListResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> GetTreeAsync(
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

        var units = await orgs.GetTreeAsync(http.Viewer(), date, cancellationToken);
        return Results.Ok(new OrgTreeResponse(date ?? BusinessDate.Today(timeProvider), [.. units.Select(OrgUnitResponse.From)]));
    }

    private static async Task<IResult> SearchAsync(
        [FromQuery(Name = "query")] string? query,
        [FromQuery(Name = "as_of")] string? asOf,
        [FromQuery(Name = "limit")] string? limit,
        HttpContext http,
        OrgDirectoryService orgs,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        if (!ApiRequest.TryParseLimit(limit, DefaultSearchLimit, OrgSearch.MaxLimit, out var pageSize))
        {
            return Problems.BadRequest(http, $"limit は 1〜{OrgSearch.MaxLimit} で指定してください。");
        }

        if (string.IsNullOrEmpty(query))
        {
            return Problems.BadRequest(http, "query を指定してください。");
        }

        var search = new OrgSearch { Query = query, Limit = pageSize };
        try
        {
            search.Validate();
        }
        catch (ArgumentException ex)
        {
            return Problems.BadRequest(http, ex.Message.Split(" (Parameter", 2)[0]);
        }

        var result = await orgs.SearchAsync(http.Viewer(), search, date, cancellationToken);
        return Results.Ok(new OrgSearchResponse(date ?? BusinessDate.Today(timeProvider), [.. result.Items.Select(OrgUnitResponse.From)], result.HasMore));
    }

    private static async Task<IResult> GetStatsAsync(
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        OrgDirectoryService orgs,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        return Results.Ok(OrgStatsResponse.From(await orgs.GetStatsAsync(http.Viewer(), date, cancellationToken)));
    }

    private static async Task<IResult> GetUnitAsync(
        string id,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        OrgDirectoryService orgs,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(id, out var unitId))
        {
            return Problems.NotFound(http);
        }

        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        return await orgs.GetUnitAsync(http.Viewer(), unitId, date, cancellationToken) is { } unit
            ? Results.Ok(OrgUnitResponse.From(unit))
            : Problems.NotFound(http);
    }

    private static Task<IResult> GetMembersAsync(
        string id,
        [FromQuery(Name = "include_descendants")] string? includeDescendants,
        [FromQuery(Name = "as_of")] string? asOf,
        [FromQuery(Name = "cursor")] string? cursor,
        [FromQuery(Name = "limit")] string? limit,
        HttpContext http,
        PersonDirectoryService people,
        CursorProtector cursors,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(id, out var unitId))
        {
            return Task.FromResult(Problems.NotFound(http));
        }

        if (!ApiRequest.TryParseFlag(includeDescendants, out var descendants))
        {
            return Task.FromResult(Problems.BadRequest(http, "include_descendants は true / false で指定してください。"));
        }

        var criteria = new PersonSearch { OrgUnitId = unitId, IncludeDescendants = descendants };
        return PeopleEndpoints.SearchAsync(http, people, cursors, criteria, asOf, cursor, limit, notFoundWhenOrgMissing: true, detailed: false, cancellationToken);
    }
}
