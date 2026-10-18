using JinJi.Api;
using JinJi.Application.Access;
using JinJi.Application.Corrections;
using JinJi.Application.Organization;
using JinJi.Application.People;
using JinJi.Application.Retention;
using JinJi.Application.Webhooks;
using JinJi.Ingest.Suggestions;

namespace JinJi.Host;

/// <summary>
/// appsettings（と環境変数・コマンドライン）からアプリの設定を読む。値は起動時に検査し、不正なら起動させない。
/// </summary>
/// <remarks>
/// 例（環境変数）: <c>JinJi__Directory__RetiredPublicDays=60</c>、<c>JinJi__OrgSnapshotCache__VersionCheckInterval=00:00:10</c>
/// </remarks>
internal static class ApplicationSettings
{
    /// <summary>人の参照の設定（<c>JinJi:Directory</c>）。</summary>
    public static PersonDirectoryOptions Directory(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(PersonDirectoryOptions.SectionName).Get<PersonDirectoryOptions>() ?? new PersonDirectoryOptions();
        options.Validate();
        return options;
    }

    /// <summary>組織スナップショットのキャッシュの設定（<c>JinJi:OrgSnapshotCache</c>）。</summary>
    public static OrgSnapshotCacheOptions OrgSnapshotCache(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(OrgSnapshotCacheOptions.SectionName).Get<OrgSnapshotCacheOptions>() ?? new OrgSnapshotCacheOptions();
        options.Validate();
        return options;
    }

    /// <summary>トークンの検証と閲覧者の対応付けの設定（<c>JinJi:Auth</c>）。</summary>
    public static AccessOptions Access(IConfiguration configuration) =>
        Bind<AccessOptions>(configuration, AccessOptions.SectionName, o => o.Validate());

    /// <summary>レート制限の設定（<c>JinJi:RateLimit</c>）。</summary>
    public static RateLimitOptions RateLimit(IConfiguration configuration) =>
        Bind<RateLimitOptions>(configuration, RateLimitOptions.SectionName, o => o.Validate());

    /// <summary>不正検知の設定（<c>JinJi:Abuse</c>）。</summary>
    public static AbuseOptions Abuse(IConfiguration configuration) =>
        Bind<AbuseOptions>(configuration, AbuseOptions.SectionName, o => o.Validate());

    /// <summary>REST API の設定（<c>JinJi:Api</c>）。</summary>
    public static ApiOptions Api(IConfiguration configuration) =>
        Bind<ApiOptions>(configuration, ApiOptions.SectionName, o => o.CursorKeyBytes());

    /// <summary>LLM による候補提示の設定（<c>JinJi:Llm</c>。既定は無効）。</summary>
    public static LlmOptions Llm(IConfiguration configuration) =>
        Bind<LlmOptions>(configuration, LlmOptions.SectionName, o => o.Validate());

    /// <summary>保持期限の消去の設定（<c>JinJi:Retention</c>）。</summary>
    public static RetentionOptions Retention(IConfiguration configuration) =>
        Bind<RetentionOptions>(configuration, RetentionOptions.SectionName, o => o.Validate());

    /// <summary>修正依頼の設定（<c>JinJi:Corrections</c>）。Webhook のリンクが未設定なら、人のログインの公開 URL + <c>/#/corrections</c> を使う。</summary>
    public static CorrectionOptions Corrections(IConfiguration configuration)
    {
        var options = Bind<CorrectionOptions>(configuration, CorrectionOptions.SectionName, o => o.Validate());
        if (options.Notice.Webhook.LinkUrl is null && configuration["JinJi:Auth:Login:PublicOrigin"] is { Length: > 0 } origin
            && Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
        {
            options = options with
            {
                Notice = options.Notice with
                {
                    Webhook = options.Notice.Webhook with { LinkUrl = new Uri(originUri, "/#/corrections").ToString() },
                },
            };
        }

        return options;
    }

    /// <summary>社内システムへの Webhook の設定（<c>JinJi:Webhooks</c>）。</summary>
    public static WebhookOptions Webhooks(IConfiguration configuration) =>
        Bind<WebhookOptions>(configuration, WebhookOptions.SectionName, o => o.Validate());

    /// <summary>設定を DI に登録する（DB を使うサービスは DatabaseRegistration.AddJinJiDatabase で登録する）。</summary>
    public static IServiceCollection AddJinJiSettings(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(Directory(configuration));
        services.AddSingleton(OrgSnapshotCache(configuration));
        services.AddSingleton(Retention(configuration));
        services.AddSingleton(Corrections(configuration));
        services.AddSingleton(Webhooks(configuration));
        return services;
    }

    private static T Bind<T>(IConfiguration configuration, string section, Action<T> validate)
        where T : new()
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(section).Get<T>() ?? new T();
        validate(options);
        return options;
    }
}
