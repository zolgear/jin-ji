using JinJi.Application.Access;
using JinJi.Application.Audit;
using JinJi.Application.Auth;
using JinJi.Policy;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;

namespace JinJi.Host.Auth;

/// <summary>ログインの結果（どの方式でも同じ形で SPA に返す）。</summary>
internal sealed record SignedInSession(ActiveSession Session, string? CsrfToken);

/// <summary>
/// ログインの成立（どの方式でも共通）: 同じブラウザの既存のセッションを終え、新しいセッションを作って Cookie を発行し、監査ログに残す。
/// </summary>
/// <remarks>
/// <para>ログインのたびに新しいセッション（セッション固定の対策）。応答には新しい閲覧者の CSRF の要求トークンを付ける。</para>
/// <para>
/// Cookie の認証ハンドラはセッションストアを使うとき、要求に既存の Cookie があるとその鍵のまま更新する（鍵を変えない）。
/// そのため、既存のセッションを終えた後に要求から既存の Cookie を取り除き、この要求でまだ Cookie を読んでいないハンドラでサインインする。
/// </para>
/// </remarks>
internal sealed class SessionSignIn(UiSessionService sessions, IAuthStore store, AuthAuditor audit, IAntiforgery antiforgery, IAuthenticationSchemeProvider schemes)
{
    /// <summary>
    /// セッションを作り、その鍵を返す（Cookie の発行は呼び出し側。外部 IdP は OIDC ハンドラが発行する）。
    /// 既存のセッションは終え、要求から既存の Cookie を取り除く。
    /// </summary>
    public async Task<string> BeginAsync(HttpContext http, NewSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (http.Request.Cookies.ContainsKey(SessionCookie.Name))
        {
            var existing = await (await FreshHandlerAsync(http)).AuthenticateAsync();
            if (existing.Principal?.FindFirst(SessionClaims.SessionId)?.Value is { } previous && Guid.TryParse(previous, out var previousId))
            {
                await store.EndSessionAsync(previousId, SessionEndKind.Replaced, cancellationToken);
            }

            RemoveRequestCookie(http);
        }

        var (key, _, _) = await sessions.CreateAsync(session, cancellationToken);
        await audit.RecordAsync(
            LoginAudit.Entry(http, AuditOutcome.Success, session.Kind == SessionPrincipalKind.BreakGlass, session.PersonId, session.AccountId, session.BindingId),
            cancellationToken);
        return key;
    }

    /// <summary>セッションを作って Cookie を発行し、この要求の閲覧者を新しいセッションにする。</summary>
    public async Task<SignedInSession> SignInAsync(HttpContext http, NewSession session, CancellationToken cancellationToken)
    {
        var key = await BeginAsync(http, session, cancellationToken);
        var handler = (IAuthenticationSignInHandler)await FreshHandlerAsync(http);
        await handler.SignInAsync(SessionCookie.PendingPrincipal(key), new AuthenticationProperties { IsPersistent = false });
        var active = await sessions.ValidateAsync(key, http.TraceIdentifier, cancellationToken)
            ?? throw new InvalidOperationException("作ったセッションを検証できません。");
        http.User = SessionCookie.Principal(active);
        return new SignedInSession(active, antiforgery.GetAndStoreTokens(http).RequestToken);
    }

    /// <summary>この要求でまだ Cookie を読んでいない、セッションの Cookie の認証ハンドラ。</summary>
    private async Task<IAuthenticationHandler> FreshHandlerAsync(HttpContext http) =>
        await new AuthenticationHandlerProvider(schemes).GetHandlerAsync(http, AuthSchemes.Session)
            ?? throw new InvalidOperationException("セッションの認証スキームがありません。");

    private static void RemoveRequestCookie(HttpContext http)
    {
        var kept = http.Request.Cookies.Where(c => c.Key != SessionCookie.Name).Select(c => $"{c.Key}={c.Value}");
        http.Request.Headers.Cookie = string.Join("; ", kept);
    }
}

/// <summary>ログインの監査ログ（auth.login）の材料。</summary>
internal static class LoginAudit
{
    /// <param name="breakGlass">緊急用アカウントか（操作者を break_glass にする）。</param>
    public static AuthAuditEntry Entry(
        HttpContext http,
        AuditOutcome outcome,
        bool breakGlass,
        Guid? personId,
        Guid? accountId,
        Guid? bindingId,
        Guid? unboundLoginId = null) => new()
        {
            Action = AuthAuditActions.Login,
            Outcome = outcome,
            ActorType = breakGlass && accountId is not null ? ActorType.BreakGlass : ActorType.User,
            Channel = Channel.Ui,
            ActorPersonId = personId,
            ActorAccountId = accountId,
            ActorClientId = UiClient.Id,
            TargetType = unboundLoginId is not null ? AuthAuditTargets.UnboundLogin
                : bindingId is not null ? AuthAuditTargets.LoginBinding
                : accountId is not null ? AuthAuditTargets.LocalAccount : null,
            TargetId = unboundLoginId ?? bindingId ?? accountId,
            RequestId = http.TraceIdentifier,
        };
}
