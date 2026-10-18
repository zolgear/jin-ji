using JinJi.Application.Retention;
using JinJi.Policy;

namespace JinJi.Host;

/// <summary>
/// <c>JinJi.Host purge-expired</c>: 保持期限の消去を 1 回実行する（ワーカーの定期実行と同じ処理。設定 <c>JinJi:Retention</c>）。
/// </summary>
/// <remarks>
/// 閲覧者は jin-ji 自身（system、経路 cli）。表ごとの削除件数を出力し、retention_run・監査ログ（retention.purge）にも残す。
/// 定期実行を止めた構成（<c>JinJi:Retention:Enabled=false</c>）で、運用の手順から呼ぶことを想定する。
/// 接続文字列は <c>ConnectionStrings:App</c>。
/// </remarks>
internal static partial class RetentionCommand
{
    public const string Usage = "JinJi.Host purge-expired";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        if (args.Any(a => !a.StartsWith("--", StringComparison.Ordinal)))
        {
            await Console.Error.WriteLineAsync("使い方: " + Usage);
            return 2;
        }

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        try
        {
            builder.Services.AddJinJiSettings(builder.Configuration);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message);
            return 2;
        }

        builder.Services.AddJinJiDatabase(builder.Configuration);
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.Retention");
        try
        {
            var result = await host.Services.GetRequiredService<RetentionService>().RunAsync(Channel.Cli, CancellationToken.None);
            await WriteAsync(output, result);
            return 0;
        }
        catch (RetentionException ex)
        {
            if (ex.Result is { } partial)
            {
                await WriteAsync(output, partial);
            }

            LogFailed(logger, ex);
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    private static async Task WriteAsync(TextWriter output, RetentionResult result)
    {
        await output.WriteLineAsync($"run_id: {result.RunId}");
        await output.WriteLineAsync($"outcome: {(result.Succeeded ? "success" : "failed")}");
        foreach (var (target, count) in result.Deleted)
        {
            await output.WriteLineAsync($"{RetentionService.TableName(target)}: {count}");
        }

        await output.WriteLineAsync($"total: {result.Total}");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "保持期限の消去に失敗しました")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
