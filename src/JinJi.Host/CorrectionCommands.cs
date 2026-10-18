using JinJi.Application.Corrections;
using JinJi.Connectors;
using JinJi.Policy;

namespace JinJi.Host;

/// <summary>
/// 修正依頼の管理 CLI。
/// <list type="bullet">
/// <item><c>correction-report --output &lt;path&gt; [--status open|closed|all] [--format csv|xlsx]</c>: レポート（人事パッケージの担当者の作業リスト）を書き出す</item>
/// <item><c>notify-corrections</c>: 通知を 1 回実行する（自動の解消の判定 → 件数の集計 → 任意の Webhook。ワーカーの定期実行と同じ）</item>
/// </list>
/// </summary>
/// <remarks>
/// 閲覧者は jin-ji 自身（system、経路 cli）。レポートの氏名などは jin-ji 自身の投影（人事管理者と同じ範囲）で、出力は運用者の責任で扱う（REST の CSV は修正担当者・人事管理者の人だけ）。
/// xlsx は ClosedXML（JinJi.Connectors）で書く。API のプロセスには ClosedXML を入れないので、REST は CSV だけ（ADR-0003）。
/// 接続文字列は <c>ConnectionStrings:App</c>。
/// </remarks>
internal static partial class CorrectionCommands
{
    public const string Usage =
        """
          JinJi.Host correction-report --output <path> [--status open|closed|all] [--format csv|xlsx]
                                                    修正依頼のレポート（人事パッケージの担当者の作業リスト。CSV は UTF-8 BOM）を書き出す
          JinJi.Host notify-corrections             修正依頼の通知を 1 回実行する（自動の解消・件数の集計・任意の Webhook。設定 JinJi:Corrections:Notice）
        """;

    public static async Task<int> ReportAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var options = Options(args);
        if (options is null || !options.TryGetValue("output", out var path))
        {
            await Console.Error.WriteLineAsync("使い方:\n" + Usage);
            return 2;
        }

        var status = options.GetValueOrDefault("status", "open") switch
        {
            "open" => CorrectionStatusFilter.Open,
            "closed" => CorrectionStatusFilter.Closed,
            "all" => CorrectionStatusFilter.All,
            _ => (CorrectionStatusFilter?)null,
        };
        var format = options.GetValueOrDefault("format", Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ? "xlsx" : "csv");
        if (status is null || format is not ("csv" or "xlsx"))
        {
            await Console.Error.WriteLineAsync("--status は open / closed / all、--format は csv / xlsx で指定してください。");
            return 2;
        }

        using var host = Build(args, out var error);
        if (host is null)
        {
            await Console.Error.WriteLineAsync(error);
            return 2;
        }

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.Corrections");
        try
        {
            var export = await host.Services.GetRequiredService<CorrectionRequestService>().ExportAsync(ViewerContext.ForSystem(Channel.Cli), status.Value, CancellationToken.None);
            var bytes = format == "xlsx"
                ? XlsxTableWriter.Write("修正依頼", CorrectionReport.Headers, CorrectionReport.Rows(export))
                : CorrectionReport.ToCsv(export);
            await File.WriteAllBytesAsync(path, bytes);
            await output.WriteLineAsync($"rows: {export.Items.Count}");
            await output.WriteLineAsync($"truncated: {(export.Truncated ? "true" : "false")}");
            await output.WriteLineAsync($"output: {path}");
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    public static async Task<int> NotifyAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        if (args.Any(a => !a.StartsWith("--", StringComparison.Ordinal)))
        {
            await Console.Error.WriteLineAsync("使い方:\n" + Usage);
            return 2;
        }

        using var host = Build(args, out var error);
        if (host is null)
        {
            await Console.Error.WriteLineAsync(error);
            return 2;
        }

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.Corrections");
        try
        {
            var run = await host.Services.GetRequiredService<CorrectionNoticeService>().RunAsync(Channel.Cli, CancellationToken.None);
            await output.WriteLineAsync($"run_id: {run.Id}");
            await output.WriteLineAsync($"outcome: {CorrectionNoticeService.OutcomeCode(run.Outcome)}");
            await output.WriteLineAsync($"open: {run.Counts.Open}");
            await output.WriteLineAsync($"new: {run.Counts.New}");
            await output.WriteLineAsync($"stale: {run.Counts.Stale}");
            await output.WriteLineAsync($"auto_resolved: {run.AutoResolved}");
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    private static IHost? Build(string[] args, out string? error)
    {
        error = null;
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(ConfigurationArgs(args));
        try
        {
            builder.Services.AddJinJiSettings(builder.Configuration);
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return null;
        }

        builder.Services.AddJinJiDatabase(builder.Configuration);
        return builder.Build();
    }

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal) { "output", "status", "format" };

    /// <summary>このコマンドの引数（--output など）。形の誤り・重複は null。設定の引数（--ConnectionStrings:App=... など）は数えない。</summary>
    private static Dictionary<string, string>? Options(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                return null;
            }

            var name = args[i][2..];
            if (!Known.Contains(name))
            {
                // 設定の引数: --Key=value はそれだけ、--Key value は値も読み飛ばす
                i += name.Contains('=', StringComparison.Ordinal) ? 0 : 1;
                continue;
            }

            if (i + 1 >= args.Length || !options.TryAdd(name, args[++i]))
            {
                return null;
            }
        }

        return options;
    }

    /// <summary>設定（ホストの構成）に渡す引数（このコマンドの引数を除く）。</summary>
    private static string[] ConfigurationArgs(string[] args)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && Known.Contains(args[i][2..]))
            {
                i++;
                continue;
            }

            rest.Add(args[i]);
        }

        return [.. rest];
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "修正依頼の管理 CLI が失敗しました")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
