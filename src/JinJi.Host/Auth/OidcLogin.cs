using JinJi.Application.Audit;
using JinJi.Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace JinJi.Host.Auth;

/// <summary>
/// 外部 IdP のログイン（ASP.NET Core 標準の OpenID Connect ハンドラ。Entra ID と汎用 OIDC を同じコードで。ADR-0004）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>設定の IdP ごとに認証スキーム（名前 = IdP の名前）を登録し、複数を同時に有効にできる。Microsoft.Identity.Web は使わない</item>
/// <item>認可コード + PKCE（S256）。応答は query で受ける（form_post にすると相関の Cookie を SameSite=None にする必要があるため）。
/// redirect_uri は設定の公開 URL から作る（要求の Host ヘッダーから作らない）</item>
/// <item>トークンは保存しない（SaveTokens = false）。UserInfo も取らない。クレームの名前は変換しない</item>
/// <item>id_token の検証の後、<see cref="LoginBindingResolver"/> で person に紐付ける。紐付いて在籍中ならセッションを作り、そうでなければ
/// セッションを作らずに SPA のログイン画面（<c>/login?error=...</c>）へ戻す</item>
/// <item>受け口（コールバック）は API のポートだけ。MCP のポートに来たら 404。IdP からのサインアウト（front-channel）は受け付けない</item>
/// <item>相関・nonce の Cookie も <c>__Host-</c>・Secure・HttpOnly・SameSite=Lax</item>
/// </list>
/// </remarks>
internal static partial class OidcLogin
{
    /// <summary>失敗のときに戻す SPA の画面。</summary>
    public const string ErrorPath = "/login";

    public static AuthenticationBuilder AddJinJiOidcProviders(this AuthenticationBuilder builder, LoginOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var provider in options.Providers)
        {
            builder.AddOpenIdConnect(provider.Name, provider.DisplayName ?? provider.Name, oidc => Configure(oidc, provider, options));
        }

        return builder;
    }

    private static void Configure(OpenIdConnectOptions oidc, LoginProviderOptions provider, LoginOptions options)
    {
        var origin = options.OriginString()!;
        oidc.Authority = provider.EffectiveAuthority;
        oidc.ClientId = provider.ClientId;
        oidc.ClientSecret = provider.ClientSecret;
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;
        oidc.UsePkce = true;
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        foreach (var scope in provider.Scopes.Where(s => s != "openid"))
        {
            oidc.Scope.Add(scope);
        }

        oidc.CallbackPath = $"{AuthEndpoints.Prefix}/callback/{provider.Name}";
        oidc.SignedOutCallbackPath = $"{AuthEndpoints.Prefix}/signout-callback/{provider.Name}";
        oidc.RemoteSignOutPath = $"{AuthEndpoints.Prefix}/remote-signout/{provider.Name}";
        oidc.SignInScheme = AuthSchemes.Session;
        oidc.SaveTokens = false;
        oidc.GetClaimsFromUserInfoEndpoint = false;
        oidc.MapInboundClaims = false;
        oidc.DisableTelemetry = true;
        // メタデータは https だけ（設定の検査で、http はループバックの IdP〔開発・テスト〕だけを許している）
        oidc.RequireHttpsMetadata = !new Uri(oidc.Authority).IsLoopback;
        oidc.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
        oidc.TokenValidationParameters.NameClaimType = "name";
        foreach (var cookie in new[] { oidc.CorrelationCookie, oidc.NonceCookie })
        {
            cookie.Path = "/";
            cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.SameSite = SameSiteMode.Lax;
            cookie.HttpOnly = true;
            cookie.IsEssential = true;
        }

        oidc.CorrelationCookie.Name = "__Host-jinji.correlation.";
        oidc.NonceCookie.Name = "__Host-jinji.nonce.";

        oidc.Events = new OpenIdConnectEvents
        {
            OnRedirectToIdentityProvider = context =>
            {
                if (!IsApiSurface(context.HttpContext))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    context.HandleResponse();
                    return Task.CompletedTask;
                }

                context.ProtocolMessage.RedirectUri = origin + oidc.CallbackPath;
                return Task.CompletedTask;
            },
            OnMessageReceived = context =>
            {
                if (!IsApiSurface(context.HttpContext))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    context.HandleResponse();
                }

                return Task.CompletedTask;
            },
            OnTokenValidated = context => OnTokenValidatedAsync(context, provider),
            OnRemoteFailure = async context =>
            {
                var http = context.HttpContext;
                await http.RequestServices.GetRequiredService<AuthAuditor>().RecordAsync(
                    LoginAudit.Entry(http, AuditOutcome.Failed, false, null, null, null), http.RequestAborted);
                LogFailed(http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OidcLogin)), provider.Name, context.Failure?.Message);
                context.Response.Redirect(ErrorPath + "?error=failed");
                context.HandleResponse();
            },
            OnRemoteSignOut = context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.HandleResponse();
                return Task.CompletedTask;
            },
        };
    }

    private static async Task OnTokenValidatedAsync(TokenValidatedContext context, LoginProviderOptions provider)
    {
        var http = context.HttpContext;
        var services = http.RequestServices;
        var principal = context.Principal!;
        string? Claim(string type) => principal.FindFirst(type)?.Value is { Length: > 0 } value ? value : null;

        var issuer = Claim("iss") ?? context.SecurityToken?.Issuer;
        var subject = provider.ProviderType == LoginProviderType.Entra ? Claim("oid") : Claim("sub");
        var external = new ExternalPrincipal(
            provider,
            issuer ?? string.Empty,
            subject ?? string.Empty,
            Claim("tid"),
            provider.EmployeeNumberClaim is { } claim ? Claim(claim) : null,
            Claim("email"),
            Claim("name"));

        var result = await services.GetRequiredService<LoginBindingResolver>().ResolveAsync(external, http.TraceIdentifier, http.RequestAborted);
        if (result is { Outcome: ExternalLoginOutcome.Bound, PersonId: { } person, BindingId: { } binding })
        {
            var key = await services.GetRequiredService<SessionSignIn>().BeginAsync(
                http,
                new NewSession(SessionPrincipalKind.Person, person, null, binding, provider.Name, ["fed"]),
                http.RequestAborted);
            context.Principal = SessionCookie.PendingPrincipal(key);
            return;
        }

        await services.GetRequiredService<AuthAuditor>().RecordAsync(
            LoginAudit.Entry(http, AuditOutcome.Denied, false, result.PersonId, null, result.BindingId, result.UnboundLoginId),
            http.RequestAborted);
        LogDenied(services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(OidcLogin)), provider.Name, result.Outcome, result.Detail);
        var error = result.Outcome switch
        {
            ExternalLoginOutcome.Unbound => "unbound",
            ExternalLoginOutcome.Inactive => "inactive",
            _ => "rejected",
        };
        context.Response.Redirect(ErrorPath + "?error=" + error);
        context.HandleResponse();
    }

    private static bool IsApiSurface(HttpContext http) =>
        http.RequestServices.GetRequiredService<SurfacePortMap>().Resolve(http.Connection.LocalPort) == Surface.Api;

    [LoggerMessage(Level = LogLevel.Information, Message = "外部 IdP {Provider} のログインに失敗しました: {Message}")]
    private static partial void LogFailed(ILogger logger, string provider, string? message);

    [LoggerMessage(Level = LogLevel.Information, Message = "外部 IdP {Provider} のログインを拒否しました（{Outcome}: {Detail}）")]
    private static partial void LogDenied(ILogger logger, string provider, ExternalLoginOutcome outcome, string? detail);
}
