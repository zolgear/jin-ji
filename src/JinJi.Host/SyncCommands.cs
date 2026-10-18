using System.Globalization;
using System.Security.Cryptography;
using JinJi.Application.Sync;
using JinJi.Policy;
using JinJi.Sync;
using JinJi.Sync.DirectApply;

namespace JinJi.Host;

/// <summary>
/// Entra ID への同期の CLI（閲覧者は jin-ji 自身。system・経路 cli、RLS 上は hr_admin）。接続文字列は <c>ConnectionStrings:App</c>。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>sync-plan &lt;entra_users.json&gt; --executed-by &lt;person-id&gt; [--settings &lt;sync.json&gt;]</c>: 計画を作る（起案者は --executed-by の人で、承認できない）</item>
/// <item><c>sync-script &lt;plan-id&gt; &lt;出力先ディレクトリ&gt;</c>: 承認済みの計画のスクリプト一式を書き出す</item>
/// <item><c>sync-result &lt;plan-id&gt; &lt;ログの JSON&gt;</c>: スクリプトの実行ログを取り込み、照合する</item>
/// <item><c>sync-apply &lt;plan-id&gt; [--what-if true]</c>: Graph への直接反映（<c>JinJi:Sync:DirectApply:Enabled</c> が true のときだけ）</item>
/// </list>
/// 承認は人事管理者の人が REST API（<c>POST /api/v1/sync/plans/{id}/approve</c>）で行う（jin-ji 自身は承認できない）。
/// 出力に人の値（氏名など）は出さない（件数・ID・ファイルのパスだけ）。
/// </remarks>
internal static partial class SyncCommands
{
    public const string PlanUsage = "JinJi.Host sync-plan <entra_users.json> --executed-by <person-id> [--settings <sync.json>]";
    public const string ScriptUsage = "JinJi.Host sync-script <plan-id> <出力先ディレクトリ>";
    public const string ResultUsage = "JinJi.Host sync-result <plan-id> <実行ログの JSON>";
    public const string ApplyUsage = "JinJi.Host sync-apply <plan-id> [--what-if true]";

    public static async Task<int> PlanAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (positional, builder) = Parse(args);
        if (positional.Count != 1 || !Guid.TryParse(builder.Configuration["executed-by"], CultureInfo.InvariantCulture, out var executor))
        {
            await Console.Error.WriteLineAsync("使い方: " + PlanUsage);
            return 2;
        }

        using var host = Build(builder);
        return await RunAsync(host, "sync-plan", async (service, viewer) =>
        {
            var content = await File.ReadAllBytesAsync(positional[0]);
            var settings = builder.Configuration["settings"] is { Length: > 0 } path ? SyncSettings.Parse(await File.ReadAllBytesAsync(path)) : SyncSettings.Default;
            var users = await new GraphUsersFile(content).ReadUsersAsync(CancellationToken.None);
            var created = await service.CreateAsync(
                viewer,
                new SyncPlanRequest(users, settings, Path.GetFileName(positional[0]), Convert.ToHexStringLower(SHA256.HashData(content)), executor),
                CancellationToken.None);

            var summary = created.Plan.Summary;
            await output.WriteLineAsync($"plan_id: {created.Plan.Id}");
            await output.WriteLineAsync($"status: {SyncPlanService.Code(created.Plan.Status)}");
            await output.WriteLineAsync($"business_date: {created.Plan.BusinessDate:yyyy-MM-dd}");
            await output.WriteLineAsync($"directory_users: {created.Plan.DirectoryUserCount}");
            await output.WriteLineAsync($"items: {summary.GetValueOrDefault("total")} (in_script {summary.GetValueOrDefault("in_script")})");
            await output.WriteLineAsync($"critical: {summary.GetValueOrDefault("priority:critical")} (disable_account {summary.GetValueOrDefault("action:disable_account")}, block_sign_in {summary.GetValueOrDefault("action:block_sign_in")})");
            foreach (var (key, count) in summary.Where(s => s.Key.StartsWith("action:", StringComparison.Ordinal)))
            {
                await output.WriteLineAsync($"{key}: {count}");
            }

            foreach (var id in created.Superseded)
            {
                await output.WriteLineAsync($"superseded: {id}");
            }

            foreach (var warning in created.Warnings)
            {
                await output.WriteLineAsync($"warning: {warning}");
            }
        });
    }

    public static async Task<int> ScriptAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (positional, builder) = Parse(args);
        if (positional.Count != 2 || !Guid.TryParseExact(positional[0], "D", out var planId))
        {
            await Console.Error.WriteLineAsync("使い方: " + ScriptUsage);
            return 2;
        }

        using var host = Build(builder);
        return await RunAsync(host, "sync-script", async (service, viewer) =>
        {
            var bundle = await service.ExportScriptAsync(viewer, planId, CancellationToken.None);
            Directory.CreateDirectory(positional[1]);
            foreach (var (name, content) in bundle.Files)
            {
                var path = Path.Combine(positional[1], name);
                await File.WriteAllBytesAsync(path, content);
                await output.WriteLineAsync($"file: {path}");
            }

            await output.WriteLineAsync($"data_sha256: {bundle.DataSha256}");
        });
    }

    public static async Task<int> ResultAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (positional, builder) = Parse(args);
        if (positional.Count != 2 || !Guid.TryParseExact(positional[0], "D", out var planId))
        {
            await Console.Error.WriteLineAsync("使い方: " + ResultUsage);
            return 2;
        }

        using var host = Build(builder);
        return await RunAsync(host, "sync-result", async (service, viewer) =>
        {
            var result = await service.ImportResultsAsync(viewer, planId, await File.ReadAllBytesAsync(positional[1]), CancellationToken.None);
            await WriteResultAsync(output, result);
        });
    }

    public static async Task<int> ApplyAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var (positional, builder) = Parse(args);
        if (positional.Count != 1 || !Guid.TryParseExact(positional[0], "D", out var planId))
        {
            await Console.Error.WriteLineAsync("使い方: " + ApplyUsage);
            return 2;
        }

        var options = builder.Configuration.GetSection(DirectApplyOptions.SectionName).Get<DirectApplyOptions>() ?? new DirectApplyOptions();
        if (!options.Enabled)
        {
            await Console.Error.WriteLineAsync($"直接反映は無効です（{DirectApplyOptions.SectionName}:Enabled）。スクリプト出力（sync-script）を使ってください。");
            return 3;
        }

        options.Validate();
        var whatIf = builder.Configuration["what-if"] == "true";
        using var host = Build(builder);
        return await RunAsync(host, "sync-apply", async (service, viewer) =>
        {
            var time = host.Services.GetRequiredService<TimeProvider>();
            IClientAssertionSource assertions = options.Credential == GraphCredentialKind.Certificate
                ? CertificateAssertionSource.FromOptions(options, time)
                : FederatedTokenFileAssertionSource.FromOptions(options);
            using var http = new HttpClient { Timeout = options.Timeout + options.Timeout };
            var client = new GraphRestDirectoryClient(http, new ClientCredentialTokenSource(http, options, assertions, time), options);
            var result = await new DirectApplier(service, client, time).ApplyAsync(viewer, planId, whatIf, CancellationToken.None);
            await WriteResultAsync(output, result);
        });
    }

    private static async Task WriteResultAsync(TextWriter output, SyncResultImport result)
    {
        await output.WriteLineAsync($"run_id: {result.RunId}");
        await output.WriteLineAsync($"what_if: {(result.WhatIf ? "true" : "false")}");
        foreach (var outcome in Enum.GetValues<SyncOutcome>())
        {
            await output.WriteLineAsync($"{SyncPlanService.Code(outcome)}: {result.Counts.GetValueOrDefault(outcome)}");
        }

        await output.WriteLineAsync($"missing: {result.MissingCount}");
        await output.WriteLineAsync($"unknown_entries: {result.UnknownEntryCount}");
    }

    private static (List<string> Positional, HostApplicationBuilder Builder) Parse(string[] args)
    {
        var positional = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args.Skip(positional.Count).ToArray());
        return (positional, builder);
    }

    private static IHost Build(HostApplicationBuilder builder)
    {
        builder.Services.AddJinJiSettings(builder.Configuration);
        builder.Services.AddJinJiDatabase(builder.Configuration);
        return builder.Build();
    }

    private static async Task<int> RunAsync(IHost host, string command, Func<SyncPlanService, ViewerContext, Task> action)
    {
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.Sync");
        try
        {
            await action(host.Services.GetRequiredService<SyncPlanService>(), ViewerContext.ForSystem(Channel.Cli));
            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(logger, command, ex);
            return 1;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Command} に失敗しました")]
    private static partial void LogFailed(ILogger logger, string command, Exception exception);
}
