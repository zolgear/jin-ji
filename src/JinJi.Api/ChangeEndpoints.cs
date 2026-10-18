using JinJi.Application.Access;
using JinJi.Application.Changes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>変更フィード（<c>/api/v1/changes</c>。アーキテクチャ概要 §9）。</summary>
internal static class ChangeEndpoints
{
    /// <summary>既定の件数。</summary>
    public const int DefaultLimit = 100;

    private const string Purpose = "changes";

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/changes", GetChangesAsync)
            .WithName("GetChanges")
            .WithSummary("変更フィード")
            .WithDescription(
                "since（前回の next_cursor）より後に変わった人・組織の種別と ID を返す（値は API で取り直す）。since を省略すると今の位置だけを返す。"
                + "同じ ID が何度も現れ得る（少なくとも 1 回は届ける）。人事管理者以外には、内示・将来の予定は効力を持つ日まで現れない。"
                + $"has_more が false なら、しばらく待ってから next_cursor で読む。limit は 1〜{ChangeFeedService.MaxLimit}。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Changes, false))
            .Produces<ChangeListResponse>();
    }

    private static async Task<IResult> GetChangesAsync(
        [FromQuery(Name = "since")] string? since,
        [FromQuery(Name = "limit")] string? limit,
        HttpContext http,
        ChangeFeedService changes,
        CursorProtector cursors,
        CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseLimit(limit, DefaultLimit, ChangeFeedService.MaxLimit, out var pageSize))
        {
            return Problems.BadRequest(http, $"limit は 1〜{ChangeFeedService.MaxLimit} で指定してください。");
        }

        var viewer = http.Viewer();
        ChangeFeedPosition? position = null;
        if (!string.IsNullOrEmpty(since) && !cursors.TryUnprotect(Purpose, viewer, since, out position))
        {
            return Problems.BadRequest(http, "since が不正です（改ざん・別の利用者のカーソルは使えません）。since を省略して今の位置から読み直してください。", Problems.InvalidCursorType);
        }

        var page = await changes.ReadAsync(viewer, position, pageSize, cancellationToken);
        return Results.Ok(new ChangeListResponse(
            [.. page.Items.Select(ChangeResponse.From)],
            cursors.Protect(Purpose, viewer, page.Next),
            page.HasMore));
    }
}
