using System.Globalization;
using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;
using Npgsql;

namespace JinJi.Host.Evaluation;

/// <summary>
/// <c>JinJi.Host evaluate-sync &lt;サンプルのディレクトリ&gt; [--output docs/reports/sync-plan.md] [--script-output &lt;dir&gt;] [--keep-database true]</c>:
/// サンプルを取り込んだ一時 DB から Entra ID への同期計画を作り、正解データと比べる（重いので通常のテストからは外している。実テナントには接続しない）。
/// </summary>
internal static class EvaluateSyncCommand
{
    public const string Usage = "JinJi.Host evaluate-sync <サンプルのディレクトリ> [--output <Markdown のパス>] [--script-output <ディレクトリ>] [--keep-database true]";

    public static async Task<int> RunAsync(string[] args, TextWriter? output = null)
    {
        output ??= Console.Out;
        var positional = args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (positional.Count != 1)
        {
            await Console.Error.WriteLineAsync("使い方: " + Usage);
            return 2;
        }

        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args.Skip(1).ToArray()).Build();
        var directory = positional[0];
        using var truth = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "ground_truth.json")));
        var keep = configuration["keep-database"] == "true";

        SyncEvaluationResult result;
        await using (var database = await EvaluationDatabase.CreateAsync(configuration, keep, CancellationToken.None))
        {
            await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
            result = await new SyncEvaluator(directory, truth.RootElement).RunAsync(dataSource, configuration["script-output"], CancellationToken.None);
            if (keep)
            {
                await output.WriteLineAsync($"database: {database.Name}");
            }
        }

        var report = Render(result, DateTimeOffset.UtcNow);
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

        return result.Checks.All(c => c.Found == c.Expected) && result.PendingOrderViolations == 0 ? 0 : 1;
    }

    public static string Render(SyncEvaluationResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        var b = new StringBuilder();
        var plan = result.Plan;
        b.AppendLine("# 同期計画の評価（messy-corp サンプル）");
        b.AppendLine();
        b.AppendLine("`JinJi.Host evaluate-sync samples/messy-corp` の出力（手で編集しない）。サンプルの 5 ファイルを取り込み・承認した一時 DB から、`entra_users.json` に対する Entra ID の同期計画を作り、正解データと比べた。実テナント・Graph には接続しない（反映はメモリ上の Entra で確かめる）。");
        b.AppendLine();
        b.AppendLine("| 項目 | 値 |");
        b.AppendLine("| --- | --- |");
        b.AppendLine(CultureInfo.InvariantCulture, $"| 実行日時 | {now.ToOffset(TimeSpan.FromHours(9)):yyyy-MM-dd HH:mm} (JST) |");
        b.AppendLine(CultureInfo.InvariantCulture, $"| 基準日（業務日付） | {result.AsOf:yyyy-MM-dd} |");
        b.AppendLine(CultureInfo.InvariantCulture, $"| 入力のユーザー | {plan.DirectoryUserCount} |");
        b.AppendLine(CultureInfo.InvariantCulture, $"| 設定 | `{plan.Settings.Name}`（`samples/messy-corp/mappings/entra_sync.json` + 評価用の人の除外） |");
        b.AppendLine(CultureInfo.InvariantCulture, $"| 項目（スクリプトに含める） | {result.Items.Count}（{result.Items.Count(i => i.InScript)}） |");
        b.AppendLine();

        b.AppendLine("## 計画の項目");
        b.AppendLine();
        b.AppendLine("| 優先度 | 操作 | 件数 | スクリプトに含める |");
        b.AppendLine("| --- | --- | --- | --- |");
        foreach (var group in result.Items.GroupBy(i => (i.Priority, i.Action)).OrderBy(g => g.Key.Priority).ThenBy(g => g.Key.Action))
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"| {SyncPlanService.Code(group.Key.Priority)} | {SyncPlanService.Code(group.Key.Action)} | {group.Count()} | {group.Count(i => i.InScript)} |");
        }

        b.AppendLine();
        b.AppendLine("属性の更新の内訳（項目ごとの件数）:");
        b.AppendLine();
        b.AppendLine("| 項目 | 件数 |");
        b.AppendLine("| --- | --- |");
        foreach (var group in result.Items.Where(i => i.Action == SyncAction.UpdateAttributes).SelectMany(i => i.Changes).GroupBy(c => c.Field).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {group.Count()} |");
        }

        b.AppendLine();
        b.AppendLine("参考の項目の内訳: " + string.Join("、", result.Items.Where(i => i.Priority == SyncPriority.Info).GroupBy(i => i.Basis.Code ?? "-").OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")));
        b.AppendLine();
        if (result.Warnings.Count > 0)
        {
            b.AppendLine("警告:");
            b.AppendLine();
            foreach (var warning in result.Warnings)
            {
                b.AppendLine("- " + warning);
            }

            b.AppendLine();
        }

        b.AppendLine("## 正解データとの比較");
        b.AppendLine();
        b.AppendLine("| 検査 | 正解データ | 結果 | 誤検出 | 定義 |");
        b.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var check in result.Checks)
        {
            var rate = check.Expected == 0 ? "-" : $"{100.0 * check.Found / check.Expected:0.0}%（{check.Found}/{check.Expected}）";
            b.AppendLine(CultureInfo.InvariantCulture, $"| {check.Label} | {check.Category} | {rate} | {check.FalsePositives} | {check.Note} |");
        }

        b.AppendLine(CultureInfo.InvariantCulture,
            $"| 内示（未来日付の辞令）を反映しない | pending_orders | {(result.PendingOrderViolations == 0 ? "OK" : "NG")} | {result.PendingOrderViolations} | 内示の対象者の Entra のアカウント {result.PendingOrdersChecked} 件で、部署・役職のあるべき値が発令後の値になっていない |");
        b.AppendLine();

        b.AppendLine("## スクリプト一式");
        b.AppendLine();
        b.AppendLine("| ファイル | バイト |");
        b.AppendLine("| --- | --- |");
        foreach (var (name, bytes) in result.ScriptFiles)
        {
            b.AppendLine(CultureInfo.InvariantCulture, $"| `{name.Replace(plan.Id.ToString("D"), "<計画 ID>", StringComparison.Ordinal)}` | {bytes} |");
        }

        b.AppendLine();
        b.AppendLine(CultureInfo.InvariantCulture, $"必要な最小権限（本番の実行）: {string.Join(" + ", JinJi.Sync.EntraScriptRenderer.WriteScopes(result.Items))}。ドライランは User.Read.All。");
        b.AppendLine();

        b.AppendLine("## メモリ上の Entra への反映と収束");
        b.AppendLine();
        b.AppendLine("承認済みの計画をメモリ上の Entra（入力の一覧から作る）に直接反映の経路で適用し（作成の推奨は除く）、反映後の一覧から計画を作り直した。反映の前に 1 人の部署を Entra 側で変え、事前条件の不一致でスキップされることも確かめた。");
        b.AppendLine();
        b.AppendLine("| 実行 | succeeded | already_applied | skipped_precondition | failed | would_apply |");
        b.AppendLine("| --- | --- | --- | --- | --- | --- |");
        foreach (var (label, run) in new[] { ("ドライラン", result.DryRun), ("反映", result.Applied) })
        {
            if (run is null)
            {
                continue;
            }

            int C(SyncOutcome o) => run.Counts.GetValueOrDefault(o);
            b.AppendLine(CultureInfo.InvariantCulture,
                $"| {label} | {C(SyncOutcome.Succeeded)} | {C(SyncOutcome.AlreadyApplied)} | {C(SyncOutcome.SkippedPrecondition)} | {C(SyncOutcome.Failed)} | {C(SyncOutcome.WouldApply)} |");
        }

        b.AppendLine();
        var remaining = result.Replanned.Where(i => i.InScript).ToList();
        b.AppendLine(CultureInfo.InvariantCulture, $"- 書き込み（PATCH・PUT）: {result.Writes} 回");
        b.AppendLine(CultureInfo.InvariantCulture, $"- 作り直した計画のスクリプトに含める項目: {remaining.Count} 件（{string.Join("、", remaining.GroupBy(i => i.Action).Select(g => $"{SyncPlanService.Code(g.Key)} {g.Count()}"))}）。事前条件の不一致でスキップした 1 件だけが残れば収束");
        b.AppendLine();

        b.AppendLine("定義:");
        b.AppendLine();
        b.AppendLine("- 正解データの Entra の不整合（`anomalies` の `entra_users.json`）の行を、正解の対応（`mappings`）で Entra のオブジェクト ID にして、計画の項目と突き合わせる");
        b.AppendLine("- 誤検出: 計画に出たが正解に無いもの（表示名・部署は表記の違いの更新も出るので、部署は正解の組織名との一致だけを数える）");
        b.AppendLine("- 対応付けは取込の Entra ID の外部 ID の対応（承認済み）だけで行う。人ではないアカウント・対応付けの無いアカウントは触らない");
        return b.ToString();
    }
}
