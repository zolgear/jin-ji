using JinJi.Application.Corrections;
using JinJi.Application.Retention;
using JinJi.Application.Webhooks;
using JinJi.Ingest;
using JinJi.Ingest.Suggestions;
using JinJi.Llm;
using JinJi.Worker.Corrections;
using JinJi.Worker.Ingest;
using JinJi.Worker.Retention;
using JinJi.Worker.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JinJi.Worker;

/// <summary>
/// ワーカーの登録口。Host（--role worker / all）から呼ばれ、ホスト型のバックグラウンドサービスとして動く。
/// </summary>
public static class WorkerModule
{
    /// <summary>ワーカーのサービスを登録する（取込のジョブの実行と、保持期限の消去・修正依頼の通知・社内システムへの Webhook の定期実行）。</summary>
    /// <param name="llm">LLM による候補提示の設定（<c>JinJi:Llm</c>。無効・null なら登録しない）。</param>
    /// <param name="retention">
    /// 保持期限の消去の設定（<c>JinJi:Retention</c>）。消去の本体（<see cref="RetentionService"/>）は Host が DB と合わせて登録する。
    /// </param>
    /// <param name="corrections">
    /// 修正依頼の設定（<c>JinJi:Corrections</c>。通知の定期実行）。通知の本体（<see cref="CorrectionNoticeService"/>）は Host が DB・Webhook の送信と合わせて登録する。
    /// </param>
    /// <param name="webhooks">
    /// 社内システムへの Webhook の設定（<c>JinJi:Webhooks</c>）。収集と送信の本体（<see cref="WebhookDispatcher"/>）は Host が DB・送信・鍵の暗号化と合わせて登録する。
    /// </param>
    public static IServiceCollection AddJinJiWorker(
        this IServiceCollection services,
        LlmOptions? llm = null,
        RetentionOptions? retention = null,
        CorrectionOptions? corrections = null,
        WebhookOptions? webhooks = null)
    {
        services.AddJinJiImport(llm);
        services.AddHostedService<ImportJobRunner>();
        services.TryAddSingleton(retention ?? new RetentionOptions());
        services.TryAddSingleton<RetentionMetrics>();
        services.AddHostedService<RetentionJobRunner>();
        services.TryAddSingleton(corrections ?? new CorrectionOptions());
        services.AddHostedService<CorrectionNoticeJobRunner>();
        services.TryAddSingleton(webhooks ?? new WebhookOptions());
        services.AddHostedService<WebhookJobRunner>();
        return services;
    }

    /// <summary>
    /// 取込（コネクタ・正規化・名寄せ・提案）のサービスを登録する。保存先（IIngestStore）・スナップショット・監査ログは Host が DB と合わせて登録する。
    /// </summary>
    /// <param name="llm">
    /// LLM による候補提示の設定。有効なら OpenAI 互換の呼び出し（<see cref="OpenAiLlmClient"/>。API キーは使うときに初めて環境変数から読む）と
    /// <see cref="ProposalSuggester"/> を登録する。登録しても、取込ごとにオプトインしなければ使わない。
    /// </param>
    public static IServiceCollection AddJinJiImport(this IServiceCollection services, LlmOptions? llm = null)
    {
        services.TryAddSingleton(TimeProvider.System);
        if (llm is { Enabled: true })
        {
            llm.Validate();
            services.TryAddSingleton(llm);
            services.TryAddSingleton<ILlmClient>(_ => new DeferredLlmClient(llm));
            services.TryAddSingleton<ProposalSuggester>();
        }

        services.TryAddSingleton<ImportService>();
        services.TryAddSingleton<WorkLocationReconciler>();
        services.TryAddSingleton<ImportJob>();
        services.TryAddSingleton<ImportJobQueue>();
        return services;
    }
}
