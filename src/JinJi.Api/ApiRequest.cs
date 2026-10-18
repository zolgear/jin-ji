using System.Globalization;
using JinJi.Application;
using JinJi.Application.Access;
using JinJi.Policy;
using Microsoft.AspNetCore.Http;

namespace JinJi.Api;

/// <summary>RFC 9457 Problem Details の応答。type は jin-ji の問題の種類を表す URN。</summary>
public static class Problems
{
    public const string NotFoundType = "urn:jinji:problem:not-found";
    public const string BadRequestType = "urn:jinji:problem:invalid-request";
    public const string InvalidCursorType = "urn:jinji:problem:invalid-cursor";
    public const string ForbiddenType = "urn:jinji:problem:forbidden";
    public const string ClientRejectedType = "urn:jinji:problem:client-rejected";
    public const string SuspendedType = "urn:jinji:problem:client-suspended";
    public const string RateLimitedType = "urn:jinji:problem:rate-limited";
    public const string ReturnedPeopleLimitedType = "urn:jinji:problem:returned-people-limited";
    public const string ConflictType = "urn:jinji:problem:conflict";
    public const string PresenceUnavailableType = "urn:jinji:problem:presence-unavailable";

    /// <summary>
    /// 見つからない。存在しない ID・参照できない ID・形の不正な ID のどれも同じ応答にする（列挙対策。本文・ヘッダーで区別させない）。
    /// </summary>
    public static IResult NotFound(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        Observe(http).RejectReason = RejectReasons.NotFound;
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "見つかりません",
            detail: "指定したものは存在しないか、参照できません。",
            type: NotFoundType);
    }

    /// <summary>当日の所在の提供が無い（配備側が IPresenceProvider を登録していない）。機能が無いことは秘密ではないので type で区別する。</summary>
    public static IResult PresenceUnavailable(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        Observe(http).RejectReason = RejectReasons.NotFound;
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "当日の所在は提供されていません",
            detail: "この環境には当日の所在の提供がありません（jin-ji は所在を保持しません）。",
            type: PresenceUnavailableType);
    }

    /// <summary>要求の形が不正。</summary>
    public static IResult BadRequest(HttpContext http, string detail, string type = BadRequestType)
    {
        ArgumentNullException.ThrowIfNull(http);
        Observe(http).RejectReason = RejectReasons.BadRequest;
        return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "要求が不正です", detail: detail, type: type);
    }

    /// <summary>権限が無い操作（ID の有無には関わらない拒否。未来の時点の指定・管理操作など）。</summary>
    public static IResult Forbidden(HttpContext http, string detail)
    {
        ArgumentNullException.ThrowIfNull(http);
        Observe(http).RejectReason = RejectReasons.Forbidden;
        return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "許可されていません", detail: detail, type: ForbiddenType);
    }

    /// <summary>対象の状態と合わない操作（解決済みの提案の解決など）。</summary>
    public static IResult Conflict(HttpContext http, string detail)
    {
        ArgumentNullException.ThrowIfNull(http);
        Observe(http).RejectReason = RejectReasons.Conflict;
        return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "状態が合いません", detail: detail, type: ConflictType);
    }

    /// <summary>リクエストの観測値（入口の共通処理が置く。無ければ作る）。</summary>
    public static AccessContext Observe(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Features.Get<AccessContext>() is not { } access)
        {
            access = new AccessContext();
            http.Features.Set(access);
        }

        return access;
    }
}

/// <summary>エンドポイントの共通の処理（閲覧者・入力の解釈）。</summary>
internal static class ApiRequest
{
    /// <summary>閲覧者。入口の共通処理が作っていなければ例外（認証を通らずにここへ来ることはない。来たら閉じる側に倒す）。</summary>
    public static ViewerContext Viewer(this HttpContext http) =>
        http.Features.Get<AccessContext>()?.Viewer ?? throw new InvalidOperationException("閲覧者が設定されていません（入口の共通処理を通っていません）。");

    /// <summary>時点（yyyy-MM-dd）。省略なら null。形が不正なら false。</summary>
    public static bool TryParseDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = parsed;
            return true;
        }

        return false;
    }

    /// <summary>件数。省略なら既定値。1〜上限でなければ false（過大な件数は切り詰めずに拒否する）。</summary>
    public static bool TryParseLimit(string? value, int defaultValue, int max, out int limit)
    {
        limit = defaultValue;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out limit) && limit >= 1 && limit <= max;
    }

    /// <summary>真偽値（true / false）。省略なら false。</summary>
    public static bool TryParseFlag(string? value, out bool flag)
    {
        flag = false;
        return string.IsNullOrEmpty(value) || bool.TryParse(value, out flag);
    }

    /// <summary>ID（UUID）。形が不正なら false（呼び出し側は「見つからない」と同じ応答にする）。</summary>
    public static bool TryParseId(string? value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id);
}

/// <summary>Application 層の認可の拒否（未来の時点の指定など）を 403 にする。</summary>
internal sealed class AccessDeniedFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            return await next(context);
        }
        catch (AccessDeniedException ex)
        {
            return Problems.Forbidden(context.HttpContext, ex.Message);
        }
    }
}
