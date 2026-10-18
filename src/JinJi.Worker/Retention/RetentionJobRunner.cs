using JinJi.Application.Retention;
using JinJi.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JinJi.Worker.Retention;

/// <summary>
/// 保持期限の消去の定期実行（--role worker / all。設定 <c>JinJi:Retention</c>）。閲覧者は jin-ji 自身（経路 worker）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>起動から <see cref="RetentionOptions.InitialDelay"/> 待ってから、<see cref="RetentionOptions.Interval"/> ごとに実行する</item>
/// <item>失敗してもワーカーは止めず、次の間隔でやり直す（消した件数は retention_run・監査ログに残る）</item>
/// <item>複数のワーカーが同時に動いても、消す行は終わった行だけなので結果は変わらない（件数が分かれて記録されるだけ）</item>
/// </list>
/// </remarks>
internal sealed partial class RetentionJobRunner(
    IServiceProvider services,
    RetentionOptions options,
    TimeProvider timeProvider,
    ILogger<RetentionJobRunner> logger) : BackgroundService
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
            // DB の設定は実行するときに初めて解決する（DB を使わない構成でもワーカーを起動できるように）
            var service = services.GetRequiredService<RetentionService>();
            var result = await service.RunAsync(Channel.Worker, cancellationToken);
            LogCompleted(logger, result.RunId, result.Total, Summary(result));
        }
        catch (RetentionException ex)
        {
            LogFailed(logger, ex, ex.Result is { } partial ? Summary(partial) : "-");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex, "-");
        }
    }

    private static string Summary(RetentionResult result) =>
        string.Join(", ", result.Deleted.Where(kv => kv.Value > 0).Select(kv => $"{RetentionService.TableName(kv.Key)}={kv.Value}")) is { Length: > 0 } text ? text : "なし";

    [LoggerMessage(Level = LogLevel.Information, Message = "保持期限の消去の定期実行は無効です（JinJi:Retention:Enabled = false）")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "保持期限の消去が完了しました: 実行 {RunId}、計 {Total} 行（{Summary}）")]
    private static partial void LogCompleted(ILogger logger, Guid runId, int total, string summary);

    [LoggerMessage(Level = LogLevel.Error, Message = "保持期限の消去に失敗しました（消した行: {Summary}）")]
    private static partial void LogFailed(ILogger logger, Exception exception, string summary);
}
