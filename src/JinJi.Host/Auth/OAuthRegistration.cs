using JinJi.Application.Auth;
using JinJi.Application.Webhooks;
using JinJi.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JinJi.Host.Auth;

/// <summary>
/// トークン発行（ADR-0004 の段階 B）の登録。参照トークンの検証は API・MCP の両方の役割、認可サーバー（<c>/oauth/*</c>）は API の役割だけ。
/// </summary>
internal static class OAuthRegistration
{
    /// <summary>トークン発行の設定（<c>JinJi:Auth:Server</c>）。</summary>
    public static AuthorizationServerOptions Server(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(AuthorizationServerOptions.SectionName).Get<AuthorizationServerOptions>() ?? new AuthorizationServerOptions();
        options.Validate();
        return options;
    }

    /// <summary>参照トークンの検証（API・MCP の共通。DB は使うときに初めて接続する）と、クライアントの管理（管理 CLI と共通）。</summary>
    public static IServiceCollection AddJinJiAccessTokens(this IServiceCollection services, AuthorizationServerOptions options)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(options);
        services.TryAddSingleton<NpgsqlOAuthStore>();
        services.TryAddSingleton<IOAuthStore>(sp => sp.GetRequiredService<NpgsqlOAuthStore>());
        services.TryAddSingleton<AccessTokenValidator>();
        return services;
    }

    /// <summary>クライアントの管理（Web の管理と管理 CLI。人のログインの監査の部品を使う）。</summary>
    public static IServiceCollection AddJinJiOAuthAdministration(this IServiceCollection services, AuthorizationServerOptions options)
    {
        services.AddJinJiAccessTokens(options);
        services.TryAddSingleton<OAuthClientAdministrationService>();
        services.TryAddSingleton<IDisclosureLeadStore>(sp => sp.GetRequiredService<NpgsqlOAuthStore>());
        services.TryAddSingleton<DisclosureLeadService>();
        // 社内システムへの Webhook の登録と管理（0018。署名の鍵の暗号化は Data Protection。人のログインの役割で使う）
        services.TryAddSingleton<WebhookAdministrationService>();
        return services;
    }

    /// <summary>
    /// 認可サーバー（API の役割。人のログインの登録の後に呼ぶ）。発行者は人のログインの公開 URL と同じにする（同意画面とログインが同じオリジンにあるため）。
    /// </summary>
    public static IServiceCollection AddJinJiAuthorizationServer(this IServiceCollection services, AuthorizationServerOptions options, LoginOptions login)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(login);
        if (!options.Enabled)
        {
            return services;
        }

        if (!string.Equals(login.OriginString(), options.IssuerValue, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"JinJi:Auth:Server:Issuer（{options.Issuer}）は JinJi:Auth:Login:PublicOrigin と同じオリジンにしてください（同意画面とログインに使うため）。",
                nameof(options));
        }

        services.AddJinJiOAuthAdministration(options);
        services.TryAddSingleton<IClientMetadataFetcher, CimdFetcher>();
        services.TryAddSingleton<IClientAssertionValidator, ClientAssertionValidator>();
        services.TryAddSingleton<ClientResolver>();
        services.TryAddSingleton<AuthorizationServer>();
        services.TryAddSingleton<DelegationService>();
        services.TryAddSingleton<TokenEndpointThrottle>();
        services.TryAddSingleton<TokenEndpointGuard>();
        return services;
    }
}
