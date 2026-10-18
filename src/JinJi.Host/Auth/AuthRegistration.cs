using JinJi.Application.Auth;
using JinJi.Infrastructure.Database;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JinJi.Host.Auth;

/// <summary>
/// 人のログイン（ADR-0004 の段階 A）の登録。役割 api / all でだけ使う（MCP は Bearer だけを受け付け、Cookie を読まない）。
/// </summary>
internal static class AuthRegistration
{
    /// <summary>人のログインの設定（<c>JinJi:Auth:Login</c>）。</summary>
    public static LoginOptions Login(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(LoginOptions.SectionName).Get<LoginOptions>() ?? new LoginOptions();
        options.Validate();
        return options;
    }

    /// <summary>認証の表を使うサービス（Web と管理 CLI の共通）。</summary>
    public static IServiceCollection AddJinJiAuthStore(this IServiceCollection services, LoginOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<NpgsqlAuthStore>();
        services.TryAddSingleton<IAuthStore>(sp => sp.GetRequiredService<NpgsqlAuthStore>());
        services.TryAddSingleton<IDataProtectionKeyStore>(sp => sp.GetRequiredService<NpgsqlAuthStore>());
        // ローカルアカウントの無効化で、その人の委任（トークンの系列）も失効する（認可サーバーを使わない構成・管理 CLI でも、過去に発行した分を止める）
        services.TryAddSingleton<NpgsqlOAuthStore>();
        services.TryAddSingleton<IOAuthStore>(sp => sp.GetRequiredService<NpgsqlOAuthStore>());
        services.TryAddSingleton<AuthAuditor>();
        services.TryAddSingleton<AuthAdministrationService>();
        services.TryAddSingleton<LoginBindingResolver>();
        services.TryAddSingleton<UiSessionService>();
        return services;
    }

    public static IServiceCollection AddJinJiHumanLogin(this IServiceCollection services, IConfiguration configuration)
    {
        var options = Login(configuration);
        services.AddSingleton<HumanLoginMarker>();
        services.AddJinJiAuthStore(options);
        services.AddJinJiDataProtection(configuration);
        services.AddHttpContextAccessor();

        // ASP.NET Core Identity（共有フレームワーク）。ストアは Npgsql の自前実装、ロールは Identity のものを使わない
        services.AddIdentityCore<LocalAccount>(identity =>
            {
                identity.Lockout.AllowedForNewUsers = true;
                identity.Lockout.MaxFailedAccessAttempts = options.Lockout.MaxFailedAttempts;
                identity.Lockout.DefaultLockoutTimeSpan = options.Lockout.Duration;
                identity.Password.RequiredLength = options.PasswordMinLength;
                identity.User.RequireUniqueEmail = false;
                identity.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-_@";
                identity.Tokens.AuthenticatorTokenProvider = TokenOptions.DefaultAuthenticatorProvider;
            })
            .AddUserStore<LocalAccountUserStore>()
            .AddTokenProvider<AuthenticatorTokenProvider<LocalAccount>>(TokenOptions.DefaultAuthenticatorProvider);
        services.Replace(ServiceDescriptor.Scoped<ILookupNormalizer, LocalAccountNameNormalizer>());
        services.TryAddScoped<IPasskeyHandler<LocalAccount>, PasskeyHandler<LocalAccount>>();
        services.Configure<IdentityPasskeyOptions>(passkey =>
        {
            // RP ID は設定で明示する（既定は Host ヘッダーから推定されるため）。アテステーションは検証しない（Identity の既定）
            passkey.ServerDomain = options.PasskeyServerDomain;
            passkey.UserVerificationRequirement = "required";
            passkey.ResidentKeyRequirement = "required";
            var origin = options.OriginString();
            // origin は設定の公開 URL と完全に一致するものだけ（クライアントデータの origin と、要求の Origin ヘッダーの両方。iframe からの呼び出しは受けない）
            passkey.ValidateOrigin = context => ValueTask.FromResult(
                !context.CrossOrigin
                && origin is not null
                && string.Equals(context.Origin, origin, StringComparison.Ordinal)
                && context.HttpContext.Request.Headers.Origin is var header
                && (header.Count == 0 || (header.Count == 1 && string.Equals(header[0], origin, StringComparison.Ordinal))));
        });

        // セッションの Cookie と外部 IdP
        services.AddSingleton<SessionTicketStore>();
        services.AddOptions<CookieAuthenticationOptions>(AuthSchemes.Session)
            .Configure<SessionTicketStore>((cookie, store) =>
            {
                SessionCookie.Configure(cookie, options);
                cookie.SessionStore = store;
            });
        services.Configure<AuthenticationOptions>(authentication =>
        {
            authentication.DefaultSignInScheme = AuthSchemes.Session;
            authentication.DefaultSignOutScheme = AuthSchemes.Session;
        });
        services.AddAuthentication()
            .AddCookie(AuthSchemes.Session)
            .AddJinJiOidcProviders(options);

        services.AddAntiforgery(CsrfMiddleware.Configure);
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<PendingLoginCookie>();
        services.AddScoped<SessionSignIn>();
        services.AddScoped<LocalLoginService>();
        return services;
    }
}
