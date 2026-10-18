using System.Security.Claims;
using JinJi.Application.Access;
using JinJi.Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace JinJi.Host.Auth;

/// <summary>
/// UI（BFF）のセッションの Cookie（ADR-0004「セキュリティの既定値」）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Cookie は <c>__Host-jinji.session</c>、Secure・HttpOnly・SameSite=Lax・Path=/・ブラウザを閉じれば消える（永続化しない）</item>
/// <item>中身は乱数の鍵（256 ビット）を Data Protection で保護したものだけ。セッションは DB（ui_session）にあり、閲覧者は要求ごとに DB から作る（<see cref="SessionTicketStore"/>）</item>
/// <item>未認証・権限なしでもログイン画面へ転送しない（401・403。SPA が画面を決める）</item>
/// </list>
/// </remarks>
internal static class SessionCookie
{
    public const string Name = "__Host-jinji.session";

    /// <summary>セッションを作る前の主体のクレーム（作ったセッションの鍵。チケットストアがこれを鍵として返す）。</summary>
    public const string PendingKeyClaim = "jinji_session_key";

    public static void Configure(CookieAuthenticationOptions options, LoginOptions login)
    {
        options.Cookie.Name = Name;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.Path = "/";
        options.Cookie.IsEssential = true;
        options.Cookie.MaxAge = null;
        options.ExpireTimeSpan = login.Session.AbsoluteTimeout;
        options.SlidingExpiration = false;
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            },
            OnRedirectToLogout = context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            OnRedirectToReturnUrl = context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
        };
    }

    /// <summary>作ったセッションの鍵だけを持つ主体（サインインに渡す。Cookie には鍵だけが入る）。</summary>
    public static ClaimsPrincipal PendingPrincipal(string key) =>
        new(new ClaimsIdentity([new Claim(PendingKeyClaim, key)], AuthSchemes.Session));

    /// <summary>検証を通ったセッションの閲覧者（要求ごとに DB から作る）。</summary>
    public static ClaimsPrincipal Principal(ActiveSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var breakGlass = session.Kind == SessionPrincipalKind.BreakGlass;
        var claims = new List<Claim>
        {
            new(SessionClaims.Subject, breakGlass ? $"break_glass:{session.AccountId:D}" : session.PersonId!.Value.ToString("D")),
            new(SessionClaims.Kind, breakGlass ? SessionClaims.BreakGlassKind : SessionClaims.PersonKind),
            new(SessionClaims.SessionId, session.Id.ToString("D")),
            new(SessionClaims.Provider, session.Provider),
        };
        if (session.AccountId is { } account)
        {
            claims.Add(new Claim(SessionClaims.AccountId, account.ToString("D")));
        }

        claims.AddRange(session.Methods.Select(m => new Claim(SessionClaims.Methods, m)));
        claims.AddRange(session.Roles.Select(r => new Claim(SessionClaims.Roles, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SessionClaims.AuthenticationType, SessionClaims.Subject, SessionClaims.Roles));
    }
}

/// <summary>
/// Cookie の認証ハンドラのセッションストア（<see cref="ITicketStore"/>）。Cookie の鍵から DB のセッションを引き、検証して閲覧者を作る。
/// </summary>
/// <remarks>
/// セッションはログインの処理が先に作り（<see cref="UiSessionService.CreateAsync"/>）、その鍵を <see cref="SessionCookie.PendingKeyClaim"/> で渡す。
/// ここでの保存は鍵を返すだけ。ロール・退職・一時停止・紐付けの取り消しは取り出し（要求ごと）で確かめる。
/// </remarks>
internal sealed class SessionTicketStore(UiSessionService sessions, IHttpContextAccessor accessor) : ITicketStore
{
    public Task<string> StoreAsync(AuthenticationTicket ticket) => StoreAsync(ticket, CancellationToken.None);

    public Task<string> StoreAsync(AuthenticationTicket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var key = ticket.Principal.FindFirst(SessionCookie.PendingKeyClaim)?.Value
            ?? throw new InvalidOperationException("セッションはログインの処理で作ってからサインインしてください（UiSessionService）。");
        return Task.FromResult(key);
    }

    public Task<string> StoreAsync(AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken) =>
        StoreAsync(ticket, cancellationToken);

    public Task RenewAsync(string key, AuthenticationTicket ticket) => Task.CompletedTask;

    public Task RenewAsync(string key, AuthenticationTicket ticket, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RenewAsync(string key, AuthenticationTicket ticket, HttpContext httpContext, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<AuthenticationTicket?> RetrieveAsync(string key) => RetrieveCoreAsync(key, accessor.HttpContext, CancellationToken.None);

    public Task<AuthenticationTicket?> RetrieveAsync(string key, CancellationToken cancellationToken) => RetrieveCoreAsync(key, accessor.HttpContext, cancellationToken);

    public Task<AuthenticationTicket?> RetrieveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken) =>
        RetrieveCoreAsync(key, httpContext, cancellationToken);

    private async Task<AuthenticationTicket?> RetrieveCoreAsync(string key, HttpContext? httpContext, CancellationToken cancellationToken)
    {
        if (await sessions.ValidateAsync(key, httpContext?.TraceIdentifier, cancellationToken) is not { } session)
        {
            return null;
        }

        var properties = new AuthenticationProperties
        {
            IssuedUtc = session.CreatedAt,
            ExpiresUtc = session.ExpiresAt,
            AllowRefresh = false,
            IsPersistent = false,
        };
        return new AuthenticationTicket(SessionCookie.Principal(session), properties, AuthSchemes.Session);
    }

    public Task RemoveAsync(string key) => RemoveAsync(key, CancellationToken.None);

    public async Task RemoveAsync(string key, CancellationToken cancellationToken) =>
        await sessions.EndAsync(key, SessionEndKind.Logout, cancellationToken);

    public Task RemoveAsync(string key, HttpContext httpContext, CancellationToken cancellationToken) => RemoveAsync(key, cancellationToken);
}
