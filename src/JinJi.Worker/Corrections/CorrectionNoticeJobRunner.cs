using JinJi.Application.Corrections;
using JinJi.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JinJi.Worker.Corrections;

/// <summary>
/// 修正依頼の通知の定期実行（--role worker / all。設定 <c>JinJi:Corrections:Notice</c>）。閲覧者は jin-ji 自身（経路 worker）。
/// 自動の解消の判定 → 件数の集計 → 任意の Webhook（<see cref="CorrectionNoticeService"/>）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>起動から <see cref="CorrectionNoticeOptions.InitialDelay"/> 待ってから、<see cref="CorrectionNoticeOptions.Interval"/> ごとに実行する（保持期限の消去と同じ流儀）</item>
/// <item>失敗してもワーカーは止めず、次の間隔でやり直す（結果は correction_notice_run・監査ログに残る）</item>
/// <item>複数のワーカーが同時に動くと Webhook が重ねて送られ得る（件数だけなので害は小さい。単一インスタンス前提。§1）</item>
/// </list>
/// </remarks>
internal sealed partial class CorrectionNoticeJobRunner(
    IServiceProvider services,
    CorrectionOptions options,
    TimeProvider timeProvider,
    ILogger<CorrectionNoticeJobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Notice.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        try
        {
            await Task.Delay(options.Notice.InitialDelay, timeProvider, stoppingToken);
            using var timer = new PeriodicTimer(options.Notice.Interval, timeProvider);
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
            var run = await services.GetRequiredService<CorrectionNoticeService>().RunAsync(Channel.Worker, cancellationToken);
            LogCompleted(logger, run.Id, CorrectionNoticeService.OutcomeCode(run.Outcome), run.Counts.Open, run.Counts.New, run.Counts.Stale, run.AutoResolved);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "修正依頼の通知の定期実行は無効です（JinJi:Corrections:Notice:Enabled = false）")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "修正依頼の通知: 実行 {RunId}、結果 {Outcome}、未対応 {Open} 件（新しい依頼 {New} 件・長く放置 {Stale} 件）、自動で解消 {AutoResolved} 件")]
    private static partial void LogCompleted(ILogger logger, Guid runId, string outcome, int open, int @new, int stale, int autoResolved);

    [LoggerMessage(Level = LogLevel.Error, Message = "修正依頼の通知に失敗しました")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
