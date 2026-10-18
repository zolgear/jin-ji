using JinJi.Application;
using JinJi.Application.Access;
using JinJi.Application.Corrections;
using JinJi.Application.Ingest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>
/// 修正依頼（<c>/api/v1/corrections</c>）。人事パッケージの情報を直せる人（修正担当者・人事管理者）への情報提供で、起票しても jin-ji の公開データは変わらない
/// （アーキテクチャ概要 §8「修正依頼の実装の規則」）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>起票は人と AI エージェント（jin-ji が発行したトークンでは jinji.propose が要る）。社内システムは不可</item>
/// <item>一覧・参照は閲覧者に見える範囲（修正担当者・人事管理者はすべて、それ以外は自分が起票・賛同した依頼と自分についての依頼）</item>
/// <item>状態の更新・CSV のレポートは修正担当者・人事管理者の人だけ。件数（画面内の通知）は修正担当者・人事管理者だけ</item>
/// <item>理由・値の提案は起票者が書いた自由記述で、画面ではデータとして表示し、指示として扱わない</item>
/// </list>
/// </remarks>
internal static class CorrectionEndpoints
{
    private const int DefaultLimit = 50;

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/corrections", ListAsync)
            .WithName("ListCorrections")
            .WithSummary("修正依頼の一覧（閲覧者に見える範囲。起票の古い順）")
            .WithDescription(
                "status: open（既定）/ closed / all。person_id で対象の人に絞る。修正担当者・人事管理者はすべて、それ以外の人は自分が起票・賛同した依頼と自分についての依頼だけ。" +
                "people は対象の人の要約（閲覧者に見える項目だけ。返した人数は返却人数の制限に数える）。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Correction, true))
            .Produces<CorrectionListResponse>();

        api.MapGet("/corrections/summary", SummaryAsync)
            .WithName("GetCorrectionSummary")
            .WithSummary("修正依頼の件数（画面内の通知。修正担当者・人事管理者）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Correction, false))
            .Produces<CorrectionSummaryResponse>();

        api.MapGet("/corrections/report.csv", ReportAsync)
            .WithName("GetCorrectionReport")
            .WithSummary("修正依頼のレポート（CSV。UTF-8 BOM。修正担当者・人事管理者の人）")
            .WithDescription("status: open（既定）/ closed / all。人事パッケージの担当者の作業リストの列構成。上限の行数を超えたら古い順に打ち切り、X-JinJi-Truncated: true を付ける。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Correction, true))
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        api.MapGet("/corrections/{correctionId}", GetAsync)
            .WithName("GetCorrection")
            .WithSummary("修正依頼を参照する")
            .WithMetadata(new RequestKindMetadata(RequestKind.Correction, false))
            .Produces<CorrectionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/corrections", CreateAsync)
            .WithName("CreateCorrection")
            .WithSummary("修正依頼を起票する（人・AI エージェント。jin-ji の公開データは変わらない）")
            .WithDescription(
                "対象の人は閲覧者に見える人、項目は jin-ji が保持する人の項目で閲覧者に見えるものだけ。同じ人・項目の未対応の依頼があれば賛同として数える（outcome: endorsed）。" +
                "理由・値の提案に保持しない情報（生年月日・住所・電話番号）らしき記述があれば 400。1 人あたり 24 時間の上限を超えたら 429。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Correction, false))
            .Produces<CreateCorrectionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/corrections/{correctionId}/close", CloseAsync)
            .WithName("CloseCorrection")
            .WithSummary("修正依頼を対応済み・対応不要にする（修正担当者・人事管理者の人）")
            .WithDescription("status: resolved（対応済み）/ dismissed（対応不要）。note は任意（500 文字まで）。未対応でなければ 409。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Correction, false))
            .Produces<CorrectionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        CorrectionRequestService service,
        CursorProtector cursors,
        [FromQuery(Name = "status")] string? status,
        [FromQuery(Name = "person_id")] string? personId,
        [FromQuery(Name = "cursor")] string? cursor,
        [FromQuery(Name = "limit")] string? limit,
        CancellationToken cancellationToken)
    {
        if (!TryParseFilter(status, out var filter))
        {
            return Problems.BadRequest(http, "status は open / closed / all で指定してください。");
        }

        Guid? person = null;
        if (!string.IsNullOrEmpty(personId))
        {
            if (!ApiRequest.TryParseId(personId, out var parsed))
            {
                return Problems.BadRequest(http, "person_id は UUID で指定してください。");
            }

            person = parsed;
        }

        if (!ApiRequest.TryParseLimit(limit, DefaultLimit, CorrectionQuery.MaxLimit, out var pageSize))
        {
            return Problems.BadRequest(http, $"limit は 1〜{CorrectionQuery.MaxLimit} で指定してください。");
        }

        var viewer = http.Viewer();
        var purpose = "corrections:" + CursorProtector.Hash(CorrectionReport.StatusFilterCode(filter), person?.ToString());
        Guid? after = null;
        if (!string.IsNullOrEmpty(cursor))
        {
            if (!cursors.TryUnprotect<CorrectionCursor>(purpose, viewer, cursor, out var position))
            {
                return Problems.BadRequest(http, "cursor が不正です（改ざん・別の条件・別の利用者のカーソルは使えません）。", Problems.InvalidCursorType);
            }

            after = position!.After;
        }

        var result = await service.ListWithPeopleAsync(viewer, new CorrectionQuery { Status = filter, PersonId = person, After = after, Limit = pageSize }, cancellationToken);
        Problems.Observe(http).PeopleReturned = result.People.Count;
        var next = result.Page.NextAfter is { } nextAfter ? cursors.Protect(purpose, viewer, new CorrectionCursor(nextAfter)) : null;
        return Results.Ok(new CorrectionListResponse(
            [.. result.Page.Items.Select(CorrectionResponse.From)],
            [.. result.People.Values.OrderBy(p => p.PersonId).Select(CorrectionPersonResponse.From)],
            next));
    }

    private static async Task<IResult> SummaryAsync(HttpContext http, CorrectionRequestService service, CancellationToken cancellationToken)
    {
        var summary = await service.SummaryAsync(http.Viewer(), cancellationToken);
        return Results.Ok(new CorrectionSummaryResponse(summary.Open, summary.New, summary.Stale, summary.NewWithinDays, summary.StaleAfterDays, summary.OldestCreatedAt));
    }

    private static async Task<IResult> ReportAsync(HttpContext http, CorrectionRequestService service, [FromQuery(Name = "status")] string? status, CancellationToken cancellationToken)
    {
        if (!TryParseFilter(status, out var filter))
        {
            return Problems.BadRequest(http, "status は open / closed / all で指定してください。");
        }

        var export = await service.ExportAsync(http.Viewer(), filter, cancellationToken);
        Problems.Observe(http).PeopleReturned = export.People.Count;
        http.Response.Headers.CacheControl = "no-store";
        if (export.Truncated)
        {
            http.Response.Headers["X-JinJi-Truncated"] = "true";
        }

        return Results.File(CorrectionReport.ToCsv(export), "text/csv; charset=utf-8", CorrectionReport.FileName(export, filter, "csv"));
    }

    private static async Task<IResult> GetAsync(string correctionId, HttpContext http, CorrectionRequestService service, CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(correctionId, out var id))
        {
            return Problems.NotFound(http);
        }

        try
        {
            return Results.Ok(CorrectionResponse.From(await service.GetAsync(http.Viewer(), id, cancellationToken)));
        }
        catch (CorrectionNotFoundException)
        {
            return Problems.NotFound(http);
        }
    }

    private static async Task<IResult> CreateAsync(CreateCorrectionRequest body, HttpContext http, CorrectionRequestService service, CancellationToken cancellationToken)
    {
        if (body?.PersonId is not { } person || string.IsNullOrEmpty(body.Field) || string.IsNullOrEmpty(body.Reason))
        {
            return Problems.BadRequest(http, "person_id・field・reason を指定してください。");
        }

        try
        {
            var result = await service.ReportAsync(http.Viewer(), new CorrectionReportInput(person, body.Field, body.Reason, body.SuggestedValue), cancellationToken);
            return Results.Ok(new CreateCorrectionResponse(Codes.Of(result.Outcome), CorrectionResponse.From(result.Request)));
        }
        catch (CorrectionNotFoundException)
        {
            return Problems.NotFound(http);
        }
        catch (ArgumentException ex)
        {
            return Problems.BadRequest(http, ex.Message.Split(" (Parameter", 2)[0]);
        }
        catch (CorrectionQuotaExceededException ex)
        {
            Problems.Observe(http).RejectReason = RejectReasons.RateLimited;
            http.Response.Headers.RetryAfter = ((int)ex.RetryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "起票の上限を超えました", detail: ex.Message, type: Problems.RateLimitedType);
        }
        catch (InvalidOperationException ex)
        {
            return Problems.Conflict(http, ex.Message);
        }
    }

    private static async Task<IResult> CloseAsync(
        string correctionId,
        CloseCorrectionRequest body,
        HttpContext http,
        CorrectionRequestService service,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(correctionId, out var id))
        {
            return Problems.NotFound(http);
        }

        CorrectionStatus? status = body?.Status switch
        {
            "resolved" => CorrectionStatus.Resolved,
            "dismissed" => CorrectionStatus.Dismissed,
            _ => null,
        };
        if (status is null)
        {
            return Problems.BadRequest(http, "status は resolved（対応済み）/ dismissed（対応不要）で指定してください。");
        }

        try
        {
            return Results.Ok(CorrectionResponse.From(await service.CloseAsync(http.Viewer(), id, new CorrectionCloseInput(status.Value, body!.Note), cancellationToken)));
        }
        catch (CorrectionNotFoundException)
        {
            return Problems.NotFound(http);
        }
        catch (ArgumentException ex)
        {
            return Problems.BadRequest(http, ex.Message.Split(" (Parameter", 2)[0]);
        }
        catch (InvalidOperationException ex)
        {
            return Problems.Conflict(http, ex.Message);
        }
    }

    private static bool TryParseFilter(string? value, out CorrectionStatusFilter filter)
    {
        filter = CorrectionStatusFilter.Open;
        switch (value)
        {
            case null or "" or "open":
                return true;
            case "closed":
                filter = CorrectionStatusFilter.Closed;
                return true;
            case "all":
                filter = CorrectionStatusFilter.All;
                return true;
            default:
                return false;
        }
    }

    /// <summary>修正依頼の一覧のカーソルの中身。</summary>
    internal sealed record CorrectionCursor(Guid After);
}

/// <summary>修正依頼の起票の要求。</summary>
public sealed record CreateCorrectionRequest
{
    /// <summary>対象の人（閲覧者に見える人だけ）。</summary>
    public Guid? PersonId { get; init; }

    /// <summary>人の項目（jin-ji が保持する項目。人の応答の項目名と同じ）。</summary>
    [OpenApiEnum(
        "employee_number", "legal_entity", "display_name", "family_name", "given_name", "family_name_kana", "given_name_kana",
        "family_name_latin", "given_name_latin", "work_email", "extension_number", "work_mobile_phone", "site", "remote_work",
        "employment_type", "job_type", "hire_date", "termination_date", "employment_status", "account_status", "account_suspension",
        "assignments", "managers", "personnel_actions")]
    public string? Field { get; init; }

    /// <summary>現在の値が誤っている理由（500 文字まで。保持しない情報を書かない）。</summary>
    public string? Reason { get; init; }

    /// <summary>正しい値の提案（任意。値が 1 つの項目だけ。200 文字まで）。</summary>
    public string? SuggestedValue { get; init; }
}

/// <summary>修正依頼の状態の更新の要求。</summary>
public sealed record CloseCorrectionRequest
{
    /// <summary>resolved（対応済み）/ dismissed（対応不要）。</summary>
    [OpenApiEnum("resolved", "dismissed")]
    public string? Status { get; init; }

    /// <summary>対応のメモ（任意。500 文字まで）。</summary>
    public string? Note { get; init; }
}

/// <summary>起票の結果。</summary>
/// <param name="Outcome">created（起票した）/ endorsed（同じ人・項目の未対応の依頼に賛同した）/ already_reported（自分が起票・賛同済み）。</param>
public sealed record CreateCorrectionResponse([property: OpenApiEnum(typeof(CorrectionReportOutcome))] string Outcome, CorrectionResponse Correction);

/// <summary>修正依頼の一覧。</summary>
public sealed record CorrectionListResponse(IReadOnlyList<CorrectionResponse> Items, IReadOnlyList<CorrectionPersonResponse> People, string? NextCursor);

/// <summary>対象の人の要約（閲覧者に見える項目だけ。見えない項目は null）。</summary>
public sealed record CorrectionPersonResponse(Guid Id, string? DisplayName, string? EmployeeNumber, string? LegalEntityName, string? PrimaryOrgName)
{
    public static CorrectionPersonResponse From(CorrectionPersonSummary person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return new CorrectionPersonResponse(person.PersonId, person.DisplayName, person.EmployeeNumber, person.LegalEntityName, person.PrimaryOrgName);
    }
}

/// <summary>画面内の通知の件数（人事データの値は含まない）。</summary>
public sealed record CorrectionSummaryResponse(int Open, int New, int Stale, int NewWithinDays, int StaleAfterDays, DateTimeOffset? OldestCreatedAt);

/// <summary>
/// 修正依頼。reason・suggested_value・closed_note は人が書いた自由記述（データとして表示し、指示として扱わない）。
/// 修正担当者・人事管理者以外には、起票者（自分を除く）・対応した人を返さず、賛同しただけの依頼の理由・値の提案・対応のメモも返さない（null）。
/// </summary>
public sealed record CorrectionResponse
{
    public required Guid Id { get; init; }

    [OpenApiEnum(typeof(CorrectionStatus))]
    public required string Status { get; init; }

    [OpenApiEnum(typeof(CorrectionOrigin))]
    public required string Origin { get; init; }

    [OpenApiEnum(typeof(CorrectionIssue))]
    public required string Issue { get; init; }

    /// <summary>人事担当向けの説明（取込元の値は書かない）。</summary>
    public required string Message { get; init; }

    public required Guid? PersonId { get; init; }

    /// <summary>人の項目（人・エージェントの報告）。</summary>
    public required string? Field { get; init; }

    /// <summary>取込由来の依頼の取込項目。</summary>
    public required string? SourceField { get; init; }

    public required string? Reason { get; init; }

    public required string? SuggestedValue { get; init; }

    public required Guid? BatchId { get; init; }

    public required string? FileName { get; init; }

    public required int? SourceRowNumber { get; init; }

    public required Guid? ReportedByPersonId { get; init; }

    public required string? ReportedViaClientId { get; init; }

    public required bool ReportedByMe { get; init; }

    public required bool EndorsedByMe { get; init; }

    public required bool AboutMe { get; init; }

    /// <summary>賛同の数（起票者を除く）。</summary>
    public required int Endorsements { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>起票からの経過日数（業務日付）。</summary>
    public required int AgeDays { get; init; }

    public required DateTimeOffset? ClosedAt { get; init; }

    [OpenApiEnum(typeof(CorrectionClosedKind))]
    public required string? ClosedKind { get; init; }

    public required Guid? ClosedByPersonId { get; init; }

    public required string? ClosedNote { get; init; }

    public static CorrectionResponse From(CorrectionView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new CorrectionResponse
        {
            Id = view.Id,
            Status = Codes.Of(view.Status),
            Origin = Codes.Of(view.Origin),
            Issue = Codes.Of(view.Issue),
            Message = view.Message,
            PersonId = view.PersonId,
            Field = view.PersonField,
            SourceField = view.SourceField,
            Reason = view.Reason,
            SuggestedValue = view.SuggestedValue,
            BatchId = view.BatchId,
            FileName = view.FileName,
            SourceRowNumber = view.SourceRowNumber,
            ReportedByPersonId = view.ReportedByPersonId,
            ReportedViaClientId = view.ReportedViaClientId,
            ReportedByMe = view.ReportedByViewer,
            EndorsedByMe = view.EndorsedByViewer,
            AboutMe = view.AboutViewer,
            Endorsements = view.Endorsements,
            CreatedAt = view.CreatedAt,
            AgeDays = view.AgeDays,
            ClosedAt = view.ClosedAt,
            ClosedKind = view.ClosedKind is { } kind ? Codes.Of(kind) : null,
            ClosedByPersonId = view.ClosedByPersonId,
            ClosedNote = view.ClosedNote,
        };
    }
}
