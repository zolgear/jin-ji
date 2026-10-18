using JinJi.Application.Access;
using JinJi.Host.Auth;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JinJi.Host.Web;

/// <summary>
/// API・MCP の入口の共通処理（認証・閲覧者・一時停止・返却人数・レート制限・メトリクス・不正検知）の登録と、パイプラインの組み立て。
/// </summary>
internal static partial class WebAccess
{
    /// <summary>サービスを登録する。設定は起動時に検査し、不正なら起動させない。</summary>
    public static IServiceCollection AddJinJiWebAccess(this IServiceCollection services, IConfiguration configuration)
    {
        var access = ApplicationSettings.Access(configuration);
        var rateLimit = ApplicationSettings.RateLimit(configuration);
        var abuse = ApplicationSettings.Abuse(configuration);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(access);
        services.AddSingleton(rateLimit);
        services.AddSingleton(abuse);
        services.AddSingleton<ViewerContextFactory>();
        services.AddSingleton<AbuseDetector>();
        services.AddSingleton<ReturnedPeopleLimiter>();
        services.AddSingleton<AccessMetrics>();
        services.AddSingleton<PrometheusMetrics>();
        services.AddSingleton<IAbuseNotifier, AbuseNotifier>();
        services.AddProblemDetails();
        // jin-ji が発行するアクセストークン（参照トークン）の検証。認可サーバーを有効にしたときだけ（API・MCP の共通）
        var server = Auth.OAuthRegistration.Server(configuration);
        services.AddSingleton(server);
        if (server.Enabled)
        {
            services.AddJinJiAccessTokens(server);
        }

        services.AddJinJiTokenAuthentication(access);
        services.AddJinJiRateLimiting(rateLimit);
        return services;
    }

    /// <summary>
    /// 入口の共通処理をパイプラインに置く。順序: 例外・状態コードの Problem Details →（<paramref name="beforeRouting"/>: SPA の静的ファイル）→ ルーティング
    /// → 認証（Cookie か Bearer の振り分け。外部 IdP のコールバックもここで受ける）→ 入口の共通処理 → レート制限 → CSRF（人のログインを有効にした役割だけ）→ 認可。
    /// </summary>
    public static WebApplication UseJinJiWebAccess(this WebApplication app, Action<IApplicationBuilder>? beforeRouting = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Services.GetRequiredService<AccessOptions>().Issuers.Count == 0)
        {
            LogNoIssuers(app.Logger);
        }

        // 出力器は起動時に作る（購読を始める前の計測は失われるため）
        _ = app.Services.GetRequiredService<PrometheusMetrics>();

        if (app.Services.GetService<Auth.HumanLoginMarker>() is not null)
        {
            // 公開 URL が https なら API のポートの要求を https として扱う（Secure の Cookie・Antiforgery のため。ヘッダーは信用しない）
            app.UseMiddleware<Auth.PublicOriginMiddleware>();
        }

        app.UseExceptionHandler();
        app.UseStatusCodePages();
        beforeRouting?.Invoke(app);
        app.UseRouting();
        app.UseAuthentication();
        app.UseMiddleware<AccessGuardMiddleware>();
        app.UseRateLimiter();
        if (app.Services.GetService<Auth.HumanLoginMarker>() is not null)
        {
            app.UseMiddleware<Auth.CsrfMiddleware>();
        }

        app.UseAuthorization();
        return app;
    }

    /// <summary>メトリクスのエンドポイント（Prometheus のテキスト形式）。</summary>
    public static IEndpointRouteBuilder MapJinJiMetrics(this IEndpointRouteBuilder endpoints)
    {
        // API の文書（OpenAPI）には載せない（別ポートの運用向けのエンドポイントで、API の利用者のものではない）
        endpoints.MapGet("/metrics", (PrometheusMetrics metrics) =>
            Results.Text(metrics.Render(), "text/plain; version=0.0.4; charset=utf-8"))
            .ExcludeFromDescription();
        return endpoints;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "JinJi:Auth:Issuers が空のため、どのトークンも受け付けません（認証が要る API はすべて 401 になります）")]
    private static partial void LogNoIssuers(ILogger logger);
}
