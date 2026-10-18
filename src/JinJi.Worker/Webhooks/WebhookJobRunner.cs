using JinJi.Application.Webhooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JinJi.Worker.Webhooks;

/// <summary>
/// 社内システムへの Webhook の収集と送信の定期実行（--role worker / all。設定 <c>JinJi:Webhooks</c>）。本体は <see cref="WebhookDispatcher"/>。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>起動から <see cref="WebhookOptions.InitialDelay"/> 待ってから、<see cref="WebhookOptions.Interval"/> ごとに実行する（保持期限の消去と同じ流儀）</item>
/// <item>失敗してもワーカーは止めず、次の間隔でやり直す（変更フィードの位置は確定したところまでしか進まない。送信の結果は webhook_delivery に残る）</item>
/// <item>複数のワーカーが同時に動いても、取り出しは FOR UPDATE SKIP LOCKED と貸し出しの期限で行うので、同じ送信を二重に送らない</item>
/// </list>
/// </remarks>
internal sealed partial class WebhookJobRunner(
    IServiceProvider services,
    WebhookOptions options,
    TimeProvider timeProvider,
    ILogger<WebhookJobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        try
        {
            await Task.Delay(options.InitialDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(options.Interval, timeProvider);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 停止要求による終了
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            // DB・Data Protection は実行するときに初めて解決する（DB を使わない構成でもワーカーを起動できるように）
            var result = await services.GetRequiredService<WebhookDispatcher>().RunOnceAsync(cancellationToken);
            if (result.Queued + result.Succeeded + result.Retried + result.Dead > 0)
            {
                LogCompleted(logger, result.Collected, result.Queued, result.Succeeded, result.Retried, result.Dead);
            }

            foreach (var failure in result.Failures)
            {
                LogItemFailed(logger, failure);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook の定期実行は無効です（JinJi:Webhooks:Enabled = false）")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Webhook: 収集 {Collected} 件の受け手・積んだ送信 {Queued} 件、送信の成功 {Succeeded} 件・再試行 {Retried} 件・dead {Dead} 件")]
    private static partial void LogCompleted(ILogger logger, int collected, int queued, int succeeded, int retried, int dead);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook: {Failure}")]
    private static partial void LogItemFailed(ILogger logger, string failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "Webhook の定期実行に失敗しました")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
