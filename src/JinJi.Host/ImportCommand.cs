using System.Globalization;
using JinJi.Application.Ingest;
using JinJi.Policy;
using JinJi.Worker;
using JinJi.Worker.Ingest;

namespace JinJi.Host;

/// <summary>
/// <c>JinJi.Host import &lt;mapping.json&gt; &lt;file&gt; --executed-by &lt;person-id&gt; [--effective-date yyyy-MM-dd] [--llm true]</c>: 開発用の取込。
/// <c>--llm true</c> で要確認の提案に LLM による候補提示を行う（設定 JinJi:Llm を有効にし、API キーの環境変数を渡したときだけ）。
/// </summary>
/// <remarks>
/// 閲覧者は jin-ji 自身（system、経路 cli。RLS 上は hr_admin）で、実行者は <c>--executed-by</c> の人。結果は作業領域（staging・提案・修正依頼）に
/// 保存するだけで、公開側は変えない（実行者とは別の人事管理者が提案を承認したときだけ反映する）。
/// 接続文字列は <c>ConnectionStrings:App</c>（未設定なら開発用に JINJI_PG_APP_PASSWORD などから組み立てる）。
/// </remarks>
internal static partial class ImportCommand
{
    public const string Usage = "JinJi.Host import <mapping.json> <file> --executed-by <person-id> [--effective-date yyyy-MM-dd] [--llm true]";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var positional = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var options = args.Skip(positional.Count).ToArray();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(options);
        var executedBy = builder.Configuration["executed-by"];
        var effective = builder.Configuration["effective-date"];
        if (positional.Count != 2 || !Guid.TryParse(executedBy, CultureInfo.InvariantCulture, out var executor))
        {
            await Console.Error.WriteLineAsync("使い方: " + Usage);
            return 2;
        }

        DateOnly? effectiveDate = null;
        if (effective is not null)
        {
            if (!DateOnly.TryParseExact(effective, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                await Console.Error.WriteLineAsync("--effective-date は yyyy-MM-dd で指定してください。");
                return 2;
            }

            effectiveDate = parsed;
        }

        builder.Services.AddJinJiSettings(builder.Configuration);
        builder.Services.AddJinJiDatabase(builder.Configuration);
        var useLlm = builder.Configuration["llm"] == "true";
        builder.Services.AddJinJiImport(ApplicationSettings.Llm(builder.Configuration));
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.Import");

        try
        {
            var job = host.Services.GetRequiredService<ImportJob>();
            var result = await job.RunAsync(
                ViewerContext.ForSystem(Channel.Cli),
                new ImportJobRequest(positional[0], positional[1], executor, effectiveDate, useLlm),
                CancellationToken.None);

            await output.WriteLineAsync($"batch_id: {result.BatchId}");
            await output.WriteLineAsync($"rows: {result.RowCount}");
            await output.WriteLineAsync($"proposals: {result.ProposalCount}");
            await output.WriteLineAsync($"needs_review: {result.NeedsReviewCount}");
            await output.WriteLineAsync($"corrections: {result.CorrectionCount}");
            foreach (var column in result.DroppedColumns)
            {
                // 捨てた列は名前と件数だけ（値は出さない）
                await output.WriteLineAsync($"dropped: {column.Name} ({Kind(column.Kind)}, {column.Count})");
            }

            foreach (var field in result.MissingFields)
            {
                await output.WriteLineAsync($"missing_field: {field}");
            }

            if (result.Llm is { } llm)
            {
                // 送った値は出さない（送信先・項目名と件数・回数・トークン数・料金の目安だけ）
                await output.WriteLineAsync($"llm: {llm.EndpointHost} {llm.Model} questions={llm.Questions} calls={llm.Calls} input_tokens={llm.InputTokens} output_tokens={llm.OutputTokens} cost_usd={llm.EstimatedCostUsd?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
                foreach (var (field, count) in llm.SentFields.OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    await output.WriteLineAsync($"llm_sent: {field} ({count})");
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    private static string Kind(DroppedColumnKind kind) => kind switch
    {
        DroppedColumnKind.Unmapped => "unmapped",
        DroppedColumnKind.Ignored => "ignored",
        DroppedColumnKind.Forbidden => "forbidden",
        _ => kind.ToString(),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "取込に失敗しました")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
