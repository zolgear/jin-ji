using JinJi.Api;
using JinJi.Application.Access;
using JinJi.Host.Web;
using Microsoft.AspNetCore.Antiforgery;

namespace JinJi.Host.Auth;

/// <summary>
/// Cookie 認証の状態変更の要求の CSRF 対策（ADR-0004「CSRF」）。独自ヘッダー <see cref="HeaderName"/> に Antiforgery の要求トークンを載せることを必須にする。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>対象: API のポートで、GET・HEAD・OPTIONS・TRACE 以外の要求のうち、UI のセッション（Cookie）で認証されたもの、または <c>/auth</c> の下のもの（未認証のログインの要求を含む）</item>
/// <item>独自ヘッダーは CORS のプリフライトを要するので、他のオリジンのページからは付けられない（第一の防御）。そのうえで Antiforgery が
/// Cookie のトークン（<c>__Host-jinji.csrf</c>）と要求トークンの組と、要求トークンの利用者が今の閲覧者であることを確かめる（第二の防御）</item>
/// <item>Bearer（<c>Authorization</c> ヘッダーのある要求）と MCP のポートは Cookie を読まないので対象外（<c>/auth</c> の下を除く）</item>
/// <item>要求トークンは <c>GET /auth/csrf</c> とログインの応答で渡す。ログインで閲覧者が変わったら取り直す</item>
/// </list>
/// </remarks>
internal sealed partial class CsrfMiddleware(RequestDelegate next, SurfacePortMap ports, IAntiforgery antiforgery, ILogger<CsrfMiddleware> logger)
{
    public const string HeaderName = "X-JinJi-CSRF";
    public const string CookieName = "__Host-jinji.csrf";
    public const string ProblemType = "urn:jinji:problem:csrf";

    public async Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (RequiresToken(http, ports) && !await IsValidAsync(http))
        {
            LogRejected(logger, http.Request.Method, http.Request.Path);
            if (http.Features.Get<AccessContext>() is { } access)
            {
                access.RejectReason = RejectReasons.Forbidden;
            }

            await AccessGuardMiddleware.WriteProblemAsync(
                http,
                StatusCodes.Status403Forbidden,
                "要求を確認できません",
                $"状態を変える要求には {HeaderName} ヘッダーに GET /auth/csrf で得たトークンを付けてください。",
                ProblemType);
            return;
        }

        await next(http);
    }

    public static bool RequiresToken(HttpContext http, SurfacePortMap ports)
    {
        var method = http.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)
            || ports.Resolve(http.Connection.LocalPort) != Surface.Api)
        {
            return false;
        }

        return SessionClaims.IsSession(http.User) || http.Request.Path.StartsWithSegments(AuthEndpoints.Prefix, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> IsValidAsync(HttpContext http)
    {
        // Secure の Cookie を使うので https の要求だけ（公開 URL が https なら PublicOriginMiddleware が https として扱う）
        if (!http.Request.IsHttps || !http.Request.Headers.TryGetValue(HeaderName, out var header) || string.IsNullOrEmpty(header))
        {
            return false;
        }

        try
        {
            return await antiforgery.IsRequestValidAsync(http);
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    public static void Configure(AntiforgeryOptions options)
    {
        options.HeaderName = HeaderName;
        options.Cookie.Name = CookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.Path = "/";
        options.Cookie.IsEssential = true;
        // X-Frame-Options はセキュリティヘッダー（WebUi）で付ける
        options.SuppressXFrameOptionsHeader = true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "CSRF のトークンが無い・合わない要求を拒否しました: {Method} {Path}")]
    private static partial void LogRejected(ILogger logger, string method, PathString path);
}
