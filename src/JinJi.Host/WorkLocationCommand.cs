using System.Globalization;
using JinJi.Ingest;
using JinJi.Policy;
using JinJi.Worker;

namespace JinJi.Host;

/// <summary>
/// <c>JinJi.Host reconcile-work-locations --executed-by &lt;person-id&gt;</c>: 取込済みの自由記述の勤務地（0017 で廃止）を拠点マスタに照合し、
/// 人ごとの拠点の変更提案と、拠点に当たらない値の修正依頼を作る（<see cref="WorkLocationReconciler"/>）。
/// </summary>
/// <remarks>
/// 閲覧者は jin-ji 自身（system、経路 cli。RLS 上は hr_admin）で、実行者は <c>--executed-by</c> の人（提案の起案者になり、承認できない）。
/// 拠点マスタと組織の拠点を先に取り込んで承認しておくこと。結果は作業領域に保存するだけで、公開側は別の人事管理者の承認で変わる。
/// </remarks>
internal static partial class WorkLocationCommand
{
    public const string Usage = "JinJi.Host reconcile-work-locations --executed-by <person-id>";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        if (!Guid.TryParse(builder.Configuration["executed-by"], CultureInfo.InvariantCulture, out var executor))
        {
            await Console.Error.WriteLineAsync("使い方: " + Usage);
            return 2;
        }

        builder.Services.AddJinJiSettings(builder.Configuration);
        builder.Services.AddJinJiDatabase(builder.Configuration);
        builder.Services.AddJinJiImport();
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.WorkLocation");
        try
        {
            var result = await host.Services.GetRequiredService<WorkLocationReconciler>()
                .RunAsync(ViewerContext.ForSystem(Channel.Cli), executor, CancellationToken.None);
            if (result is null)
            {
                await output.WriteLineAsync("勤務地の値を持つ人はいません（照合するものがありません）。");
                return 0;
            }

            await output.WriteLineAsync($"batch_id: {result.BatchId}");
            await output.WriteLineAsync($"rows: {result.RowCount}");
            await output.WriteLineAsync($"proposals: {result.ProposalCount}");
            await output.WriteLineAsync($"corrections: {result.CorrectionCount}");
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "勤務地の照合に失敗しました")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
