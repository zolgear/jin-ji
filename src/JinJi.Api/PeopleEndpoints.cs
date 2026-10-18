using JinJi.Application.Access;
using JinJi.Application.People;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>人の参照（<c>/api/v1/people</c>）。</summary>
internal static class PeopleEndpoints
{
    /// <summary>一覧の既定の件数。</summary>
    public const int DefaultLimit = 50;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/people/{id}", GetPersonAsync)
            .WithName("GetPerson")
            .WithSummary("人を参照する")
            .WithDescription("見える項目だけを返す（visible_fields）。存在しない人と参照できない人は区別せず 404。as_of（yyyy-MM-dd）に今日より後を指定できるのは人事管理者と、先行提供を許された社内システム（/meta の max_as_of まで）だけ。"
                + "workplace（階・エリア）・seat（主の席）はその時点の席の割り当て。presence（今日の所在）は配備側が所在の提供を登録していて、時点が今日で、"
                + "閲覧者に見えるときだけ返す（jin-ji は所在を保持しない。既定の公開範囲は本人と同じ部）。presence を返したときの応答は Cache-Control: no-store。")
            .WithMetadata(new RequestKindMetadata(RequestKind.PersonRead, true))
            .Produces<PersonResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/people/{id}/manager", GetManagerAsync)
            .WithName("GetPersonManager")
            .WithSummary("人の直属の上長を参照する")
            .WithDescription("対象者が見えない、または上長の項目が見えなければ 404。上長のうち見えない人は含めない。")
            .WithMetadata(new RequestKindMetadata(RequestKind.PersonRead, true))
            .Produces<ManagerResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/people/{id}/approval-route", GetApprovalRouteAsync)
            .WithName("GetApprovalRoute")
            .WithSummary("人の承認ルート（申請を誰が承認するか）を参照する")
            .WithDescription(
                "起点の人の所属組織（既定は主務。org_unit_id で兼務・出向の所属を起点にできる）から上位の組織へたどり、各組織の組織長を下の段から順に返す。"
                + "本人が組織長の組織は飛ばす（skipped_units の reason=self）。組織長が空席（いない・退職済み・見えない）の組織は、vacancy=promote（既定）なら上位へ繰り上げて"
                + "次の段の skipped_units（reason=vacant）に記録し、vacancy=vacant なら status=vacant の段として返す。一時停止中の承認者は承認者のまま返す（account_status。代わりの人は返さない）。"
                + "level（段のキー。法人ごとの段の定義。例: department）・position（承認者のその組織での役職。例: 部長）で当たる段だけに絞れる（同じ名前を繰り返すと OR、条件同士は AND。"
                + "level は繰り上げ・本人のために飛ばした組織の段にも当たる）。承認者は見える項目だけ（社内システムは項目の上限）。"
                + "起点の人が見えない・所属か上長の項目が見えない・org_unit_id がその人の所属でなければ 404。返した人数（起点の人と承認者）は返却人数の制限に数える。"
                + "as_of の規則は人の参照と同じ。")
            .WithMetadata(new RequestKindMetadata(RequestKind.PersonRead, true))
            .Produces<ApprovalRouteResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/people", ListPeopleAsync)
            .WithName("ListPeople")
            .WithSummary("人を検索する・ID で一括取得する")
            .WithDescription(
                $"query は氏名・フリガナ・ローマ字・社員番号・社用メールの部分一致（検索用の正規化表記と読みで照合。ワイルドカード不可、正規化して {PersonSearch.MinQueryKeyLength} 文字以上）。"
                + "絞り込み: org（所属組織。include_descendants で配下も）・legal_entity（雇用元の法人の ID）・level（所属組織の段のキー）・assignment_kind（primary / concurrent / secondment）・position（役職）・"
                + "employment_type・job_type・site（拠点の ID）・site_type（拠点の種別）・prefecture（拠点の都道府県のコード 01〜47）・remote_work（true / false）・"
                + "hired_from / hired_to（入社年月日。yyyy-MM-dd、両端を含む）・employment_status（active / terminated）・account_status（active / suspended）。"
                + $"複数の値は同じ名前を繰り返す（同じ条件の中は OR、条件同士は AND。1 つの条件に {PersonSearch.MaxValuesPerFilter} 件まで）。所属の条件（org・level・assignment_kind・position）は同じ 1 つの所属で満たす。"
                + "照合・絞り込み・件数・並べ替えに使うのは閲覧者に見える項目だけ。sort は id（既定）/ kana / org / hire_date / hire_date_desc / site（拠点ごと）/ remote_work（対象者が先）（見えない値の人は最後）。"
                + "facets=true で一致した人数（total）と条件ごとの件数（facets。見える人の見える値だけを数え、ある条件の件数はその条件以外に一致する人で数える）も返す（最初のページだけ。cursor と併用不可）。"
                + $"limit は 1〜{PersonSearch.MaxLimit}。"
                + $"ids（jin-ji ID のカンマ区切り、重複を除いて最大 {PeopleBatch.MaxIds} 件）を指定すると、その人たちを指定の順で返す（他の引数は as_of 以外と併用できない）。存在しない・見えない ID は単に含めない。next_cursor は常に null。")
            .WithMetadata(new RequestKindMetadata(RequestKind.PersonList, true))
            .Produces<PersonSearchResponse>();
    }

    private static async Task<IResult> GetPersonAsync(
        string id,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        PersonDirectoryService people,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(id, out var personId))
        {
            return Problems.NotFound(http);
        }

        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        var views = await people.GetPeopleAsync(http.Viewer(), [personId], date, cancellationToken, includePresence: true);
        if (views.Count == 0)
        {
            return Problems.NotFound(http);
        }

        if (views[0].Presence is not null)
        {
            // 所在は短命の情報なので、キャッシュに置かせない
            http.Response.Headers.CacheControl = "no-store";
        }

        Problems.Observe(http).PeopleReturned = 1;
        return Results.Ok(PersonResponse.From(views[0]));
    }

    private static async Task<IResult> GetManagerAsync(
        string id,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        PersonDirectoryService people,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(id, out var personId))
        {
            return Problems.NotFound(http);
        }

        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        var managers = await people.GetManagersAsync(http.Viewer(), personId, date, cancellationToken);
        if (managers is null)
        {
            return Problems.NotFound(http);
        }

        Problems.Observe(http).PeopleReturned = managers.Count;
        return Results.Ok(new ManagerResponse(personId, [.. managers.Select(PersonResponse.From)]));
    }

    private static async Task<IResult> GetApprovalRouteAsync(
        string id,
        [FromQuery(Name = "org_unit_id")] string? orgUnitId,
        [FromQuery(Name = "vacancy")] string? vacancy,
        [FromQuery(Name = "level")] string[]? levels,
        [FromQuery(Name = "position")] string[]? positions,
        [FromQuery(Name = "as_of")] string? asOf,
        HttpContext http,
        PersonDirectoryService people,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(id, out var personId))
        {
            return Problems.NotFound(http);
        }

        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        Guid? start = null;
        if (!string.IsNullOrEmpty(orgUnitId))
        {
            if (!ApiRequest.TryParseId(orgUnitId, out var parsed))
            {
                return Problems.BadRequest(http, "org_unit_id は組織の ID（UUID）で指定してください。");
            }

            start = parsed;
        }

        var mode = ApprovalVacancyMode.Promote;
        if (!string.IsNullOrEmpty(vacancy) && !ApprovalRouteCodes.TryParseVacancy(vacancy, out mode))
        {
            return Problems.BadRequest(http, "vacancy は promote / vacant で指定してください。");
        }

        var query = new ApprovalRouteQuery
        {
            PersonId = personId,
            StartOrgUnitId = start,
            Vacancy = mode,
            LevelKeys = Set(levels),
            Positions = Set(positions),
        };
        ApprovalRoute? route;
        try
        {
            route = await people.GetApprovalRouteAsync(http.Viewer(), query, date, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return Problems.BadRequest(http, ex.Message.Split(" (Parameter", 2)[0]);
        }

        if (route is null)
        {
            return Problems.NotFound(http);
        }

        Problems.Observe(http).PeopleReturned = 1 + route.ApproverIds.Count(a => a != personId);
        return Results.Ok(ApprovalRouteResponse.From(route));
    }

    private static async Task<IResult> ListPeopleAsync(
        [FromQuery(Name = "ids")] string? ids,
        [FromQuery(Name = "query")] string? query,
        [FromQuery(Name = "org")] string? org,
        [FromQuery(Name = "include_descendants")] string? includeDescendants,
        [FromQuery(Name = "legal_entity")] string[]? legalEntities,
        [FromQuery(Name = "level")] string[]? levels,
        [FromQuery(Name = "assignment_kind")] string[]? assignmentKinds,
        [FromQuery(Name = "position")] string[]? positions,
        [FromQuery(Name = "employment_type")] string[]? employmentTypes,
        [FromQuery(Name = "job_type")] string[]? jobTypes,
        [FromQuery(Name = "site")] string[]? sites,
        [FromQuery(Name = "site_type")] string[]? siteTypes,
        [FromQuery(Name = "prefecture")] string[]? prefectures,
        [FromQuery(Name = "remote_work")] string? remoteWork,
        [FromQuery(Name = "hired_from")] string? hiredFrom,
        [FromQuery(Name = "hired_to")] string? hiredTo,
        [FromQuery(Name = "employment_status")] string[]? employmentStatuses,
        [FromQuery(Name = "account_status")] string[]? accountStatuses,
        [FromQuery(Name = "sort")] string? sort,
        [FromQuery(Name = "facets")] string? facets,
        [FromQuery(Name = "as_of")] string? asOf,
        [FromQuery(Name = "cursor")] string? cursor,
        [FromQuery(Name = "limit")] string? limit,
        HttpContext http,
        PersonDirectoryService people,
        CursorProtector cursors,
        CancellationToken cancellationToken)
    {
        if (ids is not null)
        {
            var others = new[] { query, org, includeDescendants, hiredFrom, hiredTo, sort, facets, cursor, limit, remoteWork };
            var lists = new[] { legalEntities, levels, assignmentKinds, positions, employmentTypes, jobTypes, sites, siteTypes, prefectures, employmentStatuses, accountStatuses };
            if (others.Any(v => !string.IsNullOrEmpty(v)) || lists.Any(l => l is { Length: > 0 }))
            {
                return Problems.BadRequest(http, "ids は as_of 以外の引数と併用できません。");
            }

            return await GetBatchAsync(http, people, ids, asOf, cancellationToken);
        }

        Guid? orgId = null;
        if (!string.IsNullOrEmpty(org))
        {
            if (!ApiRequest.TryParseId(org, out var parsed))
            {
                return Problems.BadRequest(http, "org は組織の ID（UUID）で指定してください。");
            }

            orgId = parsed;
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

        var siteIds = new HashSet<Guid>();
        foreach (var value in sites ?? [])
        {
            if (!ApiRequest.TryParseId(value, out var site))
            {
                return Problems.BadRequest(http, "site は拠点の ID（UUID）で指定してください。");
            }

            siteIds.Add(site);
        }

        var kinds = new HashSet<AssignmentKind>();
        foreach (var value in assignmentKinds ?? [])
        {
            if (!PersonSearchCodes.TryParseAssignmentKind(value, out var kind))
            {
                return Problems.BadRequest(http, $"assignment_kind は {string.Join(" / ", PersonSearchCodes.AssignmentKinds)} で指定してください。");
            }

            kinds.Add(kind);
        }

        var order = PersonSort.Id;
        if (!string.IsNullOrEmpty(sort) && !PersonSearchCodes.TryParseSort(sort, out order))
        {
            return Problems.BadRequest(http, $"sort は {string.Join(" / ", PersonSearchCodes.Sorts)} で指定してください。");
        }

        if (!ApiRequest.TryParseDate(hiredFrom, out var from) || !ApiRequest.TryParseDate(hiredTo, out var to))
        {
            return Problems.BadRequest(http, "hired_from・hired_to は yyyy-MM-dd で指定してください。");
        }

        if (!ApiRequest.TryParseFlag(includeDescendants, out var descendants) || !ApiRequest.TryParseFlag(facets, out var withFacets)
            || !ApiRequest.TryParseFlag(remoteWork, out var remote))
        {
            return Problems.BadRequest(http, "include_descendants・facets・remote_work は true / false で指定してください。");
        }

        var criteria = new PersonSearch
        {
            Query = string.IsNullOrEmpty(query) ? null : query,
            OrgUnitId = orgId,
            IncludeDescendants = descendants,
            LegalEntityIds = entityIds,
            LevelKeys = Set(levels),
            AssignmentKinds = kinds,
            Positions = Set(positions),
            EmploymentTypes = Set(employmentTypes),
            JobTypes = Set(jobTypes),
            SiteIds = siteIds,
            SiteTypes = Set(siteTypes),
            Prefectures = Set(prefectures),
            RemoteWork = string.IsNullOrEmpty(remoteWork) ? null : remote,
            HiredFrom = from,
            HiredTo = to,
            EmploymentStatuses = Set(employmentStatuses),
            AccountStatuses = Set(accountStatuses),
            Sort = order,
            IncludeFacets = withFacets,
        };
        return await SearchAsync(http, people, cursors, criteria, asOf, cursor, limit, notFoundWhenOrgMissing: false, detailed: true, cancellationToken);
    }

    private static HashSet<string> Set(string[]? values) => new(values ?? [], StringComparer.Ordinal);

    /// <summary>
    /// ID で一括取得する。存在しない・見えない ID は単に含めない（何件が見えなかったかは返さない）。返した人数は返却人数の制限に数える。
    /// 形の不正な ID・上限を超える件数は 400（存在の有無とは関係しない拒否）。
    /// </summary>
    private static async Task<IResult> GetBatchAsync(HttpContext http, PersonDirectoryService people, string ids, string? asOf, CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        var parts = ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return Problems.BadRequest(http, "ids に jin-ji ID（UUID）をカンマ区切りで指定してください。");
        }

        var requested = new List<Guid>();
        foreach (var part in parts)
        {
            if (!ApiRequest.TryParseId(part, out var id))
            {
                return Problems.BadRequest(http, "ids は jin-ji ID（UUID）のカンマ区切りで指定してください。");
            }

            if (!requested.Contains(id))
            {
                requested.Add(id);
            }
        }

        if (requested.Count > PeopleBatch.MaxIds)
        {
            return Problems.BadRequest(http, $"ids は重複を除いて {PeopleBatch.MaxIds} 件までです。");
        }

        var views = await people.GetPeopleAsync(http.Viewer(), requested, date, cancellationToken);
        Problems.Observe(http).PeopleReturned = views.Count;
        return Results.Ok(new PersonSearchResponse([.. views.Select(PersonResponse.From)], null, null, null));
    }

    /// <summary>人の検索・組織の所属者の共通の処理。<paramref name="detailed"/> なら検索の応答（件数つき）、そうでなければ一覧の応答。</summary>
    internal static async Task<IResult> SearchAsync(
        HttpContext http,
        PersonDirectoryService people,
        CursorProtector cursors,
        PersonSearch criteria,
        string? asOf,
        string? cursor,
        string? limit,
        bool notFoundWhenOrgMissing,
        bool detailed,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseDate(asOf, out var date))
        {
            return Problems.BadRequest(http, "as_of は yyyy-MM-dd で指定してください。");
        }

        if (!ApiRequest.TryParseLimit(limit, DefaultLimit, PersonSearch.MaxLimit, out var pageSize))
        {
            return Problems.BadRequest(http, $"limit は 1〜{PersonSearch.MaxLimit} で指定してください。");
        }

        var viewer = http.Viewer();
        var purpose = "people:" + CursorProtector.Hash(criteria.Fingerprint(), asOf);
        Guid? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!cursors.TryUnprotect<PeopleCursor>(purpose, viewer, cursor, out var position))
            {
                return Problems.BadRequest(http, "cursor が不正です（改ざん・別の検索条件・別の利用者のカーソルは使えません）。", Problems.InvalidCursorType);
            }

            after = position!.After;
        }

        var search = criteria with { After = after, Limit = pageSize };
        try
        {
            search.Validate();
        }
        catch (ArgumentException ex)
        {
            return Problems.BadRequest(http, ex.Message.Split(" (Parameter", 2)[0]);
        }

        var observed = Problems.Observe(http);
        observed.IsBroadListing = search.IsBroad;
        PersonPage? page;
        try
        {
            page = await people.SearchPeopleAsync(viewer, search, date, cancellationToken);
        }
        catch (StaleCursorException ex)
        {
            return Problems.BadRequest(http, ex.Message, Problems.InvalidCursorType);
        }

        if (page is null)
        {
            return notFoundWhenOrgMissing ? Problems.NotFound(http)
                : detailed ? Results.Ok(new PersonSearchResponse([], null, search.IncludeFacets ? 0 : null, null))
                : Results.Ok(new PersonListResponse([], null));
        }

        observed.PeopleReturned = page.Items.Count;
        var next = page.NextAfter is { } nextAfter ? cursors.Protect(purpose, viewer, new PeopleCursor(nextAfter)) : null;
        var items = page.Items.Select(PersonResponse.From).ToList();
        return detailed
            ? Results.Ok(new PersonSearchResponse(items, next, page.Total, page.Facets is { } f ? PersonFacetsResponse.From(f) : null))
            : Results.Ok(new PersonListResponse(items, next));
    }

    /// <summary>人の一覧のカーソルの中身。</summary>
    internal sealed record PeopleCursor(Guid After);
}
