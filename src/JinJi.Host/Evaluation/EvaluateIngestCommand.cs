using System.Globalization;
using System.Text.Json;
using JinJi.Ingest.Suggestions;
using JinJi.Llm;
using Npgsql;

namespace JinJi.Host.Evaluation;

/// <summary>
/// <c>JinJi.Host evaluate-ingest &lt;サンプルのディレクトリ&gt; [--output docs/reports/ingest-accuracy.md] [--keep-database true] [--llm true --llm-model &lt;モデル名&gt;]</c>:
/// 空の一時 DB にサンプルを取り込み、正解データと比べて精度を出す（重いので通常のテストからは外している）。
/// <c>--llm true</c> で要確認の提案に LLM の候補提示を行い、その精度も出す（実 LLM を呼ぶ。API キーは環境変数。通常のテストでは呼ばない）。
/// </summary>
/// <remarks>
/// サンプルのディレクトリには ground_truth.json と mappings/（項目マッピング）が要る。一時 DB の作成にはテストと同じ環境変数を使う
/// （JINJI_PG_ADMIN_PASSWORD など。<c>scripts/dotnet.sh</c> が deploy/compose/.env を渡す）。
/// </remarks>
internal static class EvaluateIngestCommand
{
    public const string Usage = "JinJi.Host evaluate-ingest <サンプルのディレクトリ> [--output <Markdown のパス>] [--keep-database true] [--llm true --llm-model <モデル名> [--llm-input-price <USD/100 万トークン> --llm-output-price <USD/100 万トークン>]]";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var positional = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (positional.Count != 1)
        {
            await Console.Error.WriteLineAsync("使い方: " + Usage);
            return 2;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args.Skip(1).ToArray())
            .Build();
        var directory = positional[0];
        using var truth = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "ground_truth.json")));

        ProposalSuggester? suggester = null;
        if (configuration["llm"] == "true")
        {
            var options = LlmOptionsFor(configuration);
            suggester = new ProposalSuggester(OpenAiLlmClient.Create(options), options);
            await output.WriteLineAsync($"llm: {options.EndpointUri.Host} {options.Model}");
        }

        var evaluator = new IngestEvaluator(directory, truth.RootElement, suggester);

        EvaluationResult result;
        await using (var database = await EvaluationDatabase.CreateAsync(configuration, configuration["keep-database"] == "true", CancellationToken.None))
        {
            await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
            result = await evaluator.RunAsync(dataSource, CancellationToken.None);
            if (configuration["keep-database"] == "true")
            {
                await output.WriteLineAsync($"database: {database.Name}");
            }
        }

        var report = EvaluationReport.Render(result, truth.RootElement, GitCommit(directory), DateTimeOffset.UtcNow);
        if (configuration["output"] is { Length: > 0 } path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await File.WriteAllTextAsync(path, report);
            await output.WriteLineAsync($"report: {path}");
        }
        else
        {
            await output.WriteAsync(report);
        }

        return 0;
    }

    /// <summary>
    /// 評価で使う LLM の設定: JinJi:Llm（appsettings・環境変数）に、--llm-model・--llm-input-price・--llm-output-price を重ねる。
    /// 評価では 1 回の取込の上限を広げる（サンプルの要確認をすべて回す）。
    /// </summary>
    private static LlmOptions LlmOptionsFor(IConfiguration configuration)
    {
        var bound = configuration.GetSection(LlmOptions.SectionName).Get<LlmOptions>() ?? new LlmOptions();
        static decimal? Price(string? value) => value is { Length: > 0 } ? decimal.Parse(value, CultureInfo.InvariantCulture) : null;
        var options = new LlmOptions
        {
            Enabled = true,
            Provider = bound.Provider,
            BaseUrl = bound.BaseUrl,
            Model = configuration["llm-model"] ?? bound.Model,
            ApiKeyEnvironmentVariable = bound.ApiKeyEnvironmentVariable,
            AllowedEndpoints = bound.AllowedEndpoints,
            Timeout = bound.Timeout,
            MaxRetries = bound.MaxRetries,
            RetryDelay = bound.RetryDelay,
            MaxConcurrency = bound.MaxConcurrency,
            MaxCallsPerBatch = Math.Max(bound.MaxCallsPerBatch, 200),
            MaxCandidates = bound.MaxCandidates,
            MaxOutputTokens = bound.MaxOutputTokens,
            Temperature = bound.Temperature,
            InputPricePerMillionTokens = Price(configuration["llm-input-price"]) ?? bound.InputPricePerMillionTokens,
            OutputPricePerMillionTokens = Price(configuration["llm-output-price"]) ?? bound.OutputPricePerMillionTokens,
        };
        options.Validate();
        return options;
    }

    /// <summary>リポジトリの HEAD のコミット（.git を直接読む。git コマンドに依存しない）。</summary>
    private static string GitCommit(string start)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(start)); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (!Directory.Exists(git))
            {
                continue;
            }

            var head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
            if (!head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                return head[..Math.Min(7, head.Length)];
            }

            var reference = head[5..];
            var loose = Path.Combine(git, reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(loose))
            {
                return File.ReadAllText(loose).Trim()[..7];
            }

            var packed = Path.Combine(git, "packed-refs");
            if (File.Exists(packed) && File.ReadLines(packed).FirstOrDefault(l => l.EndsWith(" " + reference, StringComparison.Ordinal)) is { } line)
            {
                return line[..7];
            }
        }

        return "unknown";
    }
}
