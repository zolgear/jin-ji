using System.Globalization;
using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Application.Changes;
using JinJi.Application.Corrections;
using JinJi.Application.Ingest;
using JinJi.Application.Organization;
using JinJi.Application.People;
using JinJi.Application.Retention;
using JinJi.Application.Sync;
using JinJi.Application.Webhooks;
using JinJi.Infrastructure.Database;
using JinJi.Sync;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace JinJi.Host;

/// <summary>
/// DB（アプリ用ロール jinji_app）を使うサービスの登録。接続文字列は使うときに初めて解決するので、DB を設定していない構成でも起動はできる。
/// </summary>
/// <remarks>
/// 接続文字列は <c>ConnectionStrings:App</c>。未設定なら、開発用に JINJI_PG_HOST / JINJI_PG_PORT / JINJI_PG_DATABASE / JINJI_PG_APP_PASSWORD から組み立てる。
/// テーブル所有者（jinji_owner）の接続文字列は使わない（所有者は RLS を外せるため。0002 の冒頭）。
/// </remarks>
internal static class DatabaseRegistration
{
    public static IServiceCollection AddJinJiDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(_ => NpgsqlDataSource.Create(RequireAppConnectionString(configuration)));

        services.TryAddSingleton<IAuditSink, NpgsqlAuditSink>();
        services.TryAddSingleton<AuditWriter>();
        services.TryAddSingleton<NpgsqlOrgSnapshotProvider>();
        services.TryAddSingleton<IOrgDataVersionSource, NpgsqlOrgDataVersionSource>();
        services.TryAddSingleton(sp => new OrgSnapshotCache(
            sp.GetRequiredService<NpgsqlOrgSnapshotProvider>(),
            sp.GetRequiredService<IOrgDataVersionSource>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<OrgSnapshotCacheOptions>()));
        services.TryAddSingleton<IOrgSnapshotProvider>(sp => sp.GetRequiredService<OrgSnapshotCache>());
        services.TryAddSingleton<IOrgDataChangeNotifier>(sp => sp.GetRequiredService<OrgSnapshotCache>());
        services.TryAddSingleton<IIngestStore, NpgsqlIngestStore>();
        services.TryAddSingleton<IWorkLocationSource, NpgsqlWorkLocationSource>();
        services.TryAddSingleton<ProposalReviewService>();
        services.TryAddSingleton<ProposalResolutionService>();
        // AI エージェント（MCP）による要確認の提案への推定（作業領域。0013）
        services.TryAddSingleton<IAgentSuggestionStore, NpgsqlAgentSuggestionStore>();
        services.TryAddSingleton<ProposalSuggestionService>();

        // 同期計画（差分計算・スクリプト生成は JinJi.Sync。直接反映は既定で無効なので、ここでは登録しない。CLI sync-apply が設定を見て作る）
        services.TryAddSingleton<ISyncPlanStore, NpgsqlSyncPlanStore>();
        services.TryAddSingleton<ISyncPlanner, EntraSyncPlanner>();
        services.TryAddSingleton<ISyncScriptRenderer, EntraScriptRenderer>();
        services.TryAddSingleton<ISyncLogParser, SyncLogParser>();
        services.TryAddSingleton<SyncPlanService>();

        // 参照（REST・MCP が共有する）
        services.TryAddSingleton<IPolicyRuleStore, NpgsqlPolicyRuleStore>();
        services.TryAddSingleton<PersonDirectoryService>();
        services.TryAddSingleton<OrgDirectoryService>();
        services.TryAddSingleton<Application.Workplace.WorkplaceDirectoryService>();
        // 当日の所在の提供（拡張点）。導入先が自前の実装を先に登録すればそれを使う。既定は提供なし（機能は無効）
        services.TryAddSingleton<Application.Presence.IPresenceProvider, Application.Presence.NoPresenceProvider>();
        services.TryAddSingleton<Application.Presence.PresenceService>();
        services.TryAddSingleton<IDirectoryCalendarStore, NpgsqlDirectoryCalendarStore>();
        services.TryAddSingleton<DirectoryMetaService>();
        services.TryAddSingleton<IChangeFeedStore, NpgsqlChangeFeedStore>();
        services.TryAddSingleton<ChangeFeedService>();

        // 修正依頼（起票・一覧・状態の更新・レポート・自動の解消・通知。設定 JinJi:Corrections は AddJinJiSettings が登録する。0015）
        services.TryAddSingleton<ICorrectionStore, NpgsqlCorrectionStore>();
        services.TryAddSingleton<CorrectionRequestService>();
        services.TryAddSingleton<CorrectionMetrics>();
        // Webhook の送信（宛先の検査は CIMD の取得と同じ。既定は無効で、無効なら送らない）
        services.TryAddSingleton<ICorrectionWebhookSender, Web.CorrectionWebhookSender>();
        services.TryAddSingleton<CorrectionNoticeService>();

        // 現場・IT 部門の直接登録（管理元・担当の指名・範囲の判定は 0020、登録は 0021。ADR-0013）
        services.TryAddSingleton<Application.FieldManagement.IFieldManagementStore, NpgsqlFieldManagementStore>();
        services.TryAddSingleton<Application.FieldManagement.FieldManagementService>();
        // 直接登録（登録・同意・取り消し。0021）
        services.TryAddSingleton<Application.FieldManagement.IFieldRegistrationStore, NpgsqlFieldRegistrationStore>();
        services.TryAddSingleton<Application.FieldManagement.FieldRegistrationService>();

        // グループ（チーム・プロジェクト。0022。ADR-0014）
        services.TryAddSingleton<Application.Groups.IGroupStore, NpgsqlGroupStore>();
        services.TryAddSingleton<Application.Groups.GroupService>();

        // 社内システムへの Webhook（0018。設定 JinJi:Webhooks は AddJinJiSettings が登録する）。収集と送信はワーカー、登録の管理は API の役割（OAuthRegistration）。
        // 受け手のクライアントは api_client から読む（認可サーバーを使わない構成でも読めるように、クライアントの表の読み取りだけを登録する）。
        // 署名の鍵の復号は Data Protection（人のログインの役割、またはワーカーに Data Protection の設定があるとき。WebhookRegistration）
        services.TryAddSingleton<IWebhookStore, NpgsqlWebhookStore>();
        services.TryAddSingleton<NpgsqlOAuthStore>();
        services.TryAddSingleton<Application.Auth.IOAuthStore>(sp => sp.GetRequiredService<NpgsqlOAuthStore>());
        services.TryAddSingleton<IWebhookSender, Web.WebhookSender>();
        services.TryAddSingleton<WebhookMetrics>();
        services.TryAddSingleton<WebhookDispatcher>();
        Auth.DataProtectionSetup.AddJinJiWebhookSecrets(services);

        // 保持期限の消去（ワーカーの定期実行と管理 CLI の purge-expired。設定 JinJi:Retention は AddJinJiSettings が登録する）
        services.TryAddSingleton<IRetentionStore, NpgsqlRetentionStore>();
        services.TryAddSingleton<RetentionService>();
        return services;
    }

    /// <summary>アプリ用ロールの接続文字列。未設定なら、何を設定すればよいかを示す例外。</summary>
    public static string RequireAppConnectionString(IConfiguration configuration) =>
        AppConnectionString(configuration) ?? throw MissingConnectionString();

    /// <summary>アプリ用ロールの接続文字列。未設定なら null。</summary>
    public static string? AppConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.GetConnectionString("App") is { Length: > 0 } configured)
        {
            return configured;
        }

        var password = configuration["JINJI_PG_APP_PASSWORD"];
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        var port = configuration["JINJI_PG_PORT"];
        return new NpgsqlConnectionStringBuilder
        {
            Host = configuration["JINJI_PG_HOST"] ?? "127.0.0.1",
            Port = string.IsNullOrEmpty(port) ? 25432 : int.Parse(port, CultureInfo.InvariantCulture),
            Database = configuration["JINJI_PG_DATABASE"] ?? "jinji",
            Username = "jinji_app",
            Password = password,
        }.ConnectionString;
    }

    private static InvalidOperationException MissingConnectionString() =>
        new("接続文字列 ConnectionStrings:App が設定されていません（環境変数 ConnectionStrings__App、または開発用の JINJI_PG_APP_PASSWORD）。");
}
