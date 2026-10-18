using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using JinJi.Application.Sync;

namespace JinJi.Sync;

/// <summary>
/// 同期計画の差分レポート（Markdown・CSV）。IT 全般統制の項目（退職者の無効化・一時停止者のサインイン停止）を先頭に、件数と一覧を出す。
/// </summary>
/// <remarks>
/// 値（氏名・組織名など）は信頼しない入力として扱う: Markdown では HTML と Markdown の記号をエスケープし、CSV では数式として解釈される先頭の文字を無害化する。
/// </remarks>
internal static class SyncReport
{
    private static readonly JsonSerializerOptions SettingsJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<SyncAction, string> ActionNames = new()
    {
        [SyncAction.DisableAccount] = "退職者のアカウントの無効化",
        [SyncAction.BlockSignIn] = "一時停止者のサインイン停止",
        [SyncAction.EnableAccount] = "アカウントの再有効化",
        [SyncAction.UpdateAttributes] = "属性の更新",
        [SyncAction.SetManager] = "上長の設定",
        [SyncAction.CreateUser] = "アカウントの作成",
        [SyncAction.UnmatchedAccount] = "対応付けの無いアカウント",
        [SyncAction.ExcludedAccount] = "除外・対象外",
    };

    public static string Markdown(SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items, string dataSha256)
    {
        var b = new StringBuilder();
        b.AppendLine(CultureInfo.InvariantCulture, $"# Entra ID 同期計画 `{plan.Id:D}`");
        b.AppendLine();
        b.AppendLine("jin-ji が生成した差分レポート（手で編集しない）。値は jin-ji の公開データ（業務日付時点。内示は含まない）と、計画を作ったときの Entra のユーザー一覧から決めた。");
        b.AppendLine();
        b.AppendLine("| 項目 | 値 |");
        b.AppendLine("| --- | --- |");
        Row(b, "状態", SyncPlanService.Code(plan.Status));
        Row(b, "業務日付", plan.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Row(b, "起案", $"{plan.ProposedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC（{plan.ProposedByPersonId:D}）");
        Row(b, "承認", plan.DecidedAt is { } decided ? $"{decided.ToUniversalTime():yyyy-MM-dd HH:mm} UTC（{plan.DecidedByPersonId:D}）" : "-");
        Row(b, "期限", $"{plan.ExpiresAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC");
        Row(b, "入力（Entra のユーザー一覧）", $"{plan.SourceFileName}（{plan.DirectoryUserCount} 件、SHA-256 {plan.SourceSha256}）");
        Row(b, "設定", $"{plan.Settings.Name}（SHA-256 {plan.SettingsSha256}）");
        Row(b, "データの SHA-256", dataSha256);
        Row(b, "スクリプトに含める項目", $"{items.Count(i => i.InScript)} 件（全 {items.Count} 件）");
        b.AppendLine();

        // IT 全般統制（最優先）
        var critical = items.Where(i => i.Priority == SyncPriority.Critical).ToList();
        b.AppendLine("## IT 全般統制（最優先）");
        b.AppendLine();
        b.AppendLine("| 区分 | 件数 |");
        b.AppendLine("| --- | --- |");
        Row(b, "**退職者のアカウントの無効化**", Count(items, SyncAction.DisableAccount));
        Row(b, "**一時停止者のサインイン停止**", Count(items, SyncAction.BlockSignIn));
        Row(b, "**除外の設定で無効化されない退職者・一時停止者（警告）**", critical.Count(i => i.Action == SyncAction.ExcludedAccount).ToString(CultureInfo.InvariantCulture));
        b.AppendLine();
        ItemTable(b, critical);

        Section(b, "要対応", items.Where(i => i.Priority == SyncPriority.High).ToList(), withChanges: true);
        Section(b, "属性・上長の更新", items.Where(i => i.Action is SyncAction.UpdateAttributes or SyncAction.SetManager).ToList(), withChanges: true);
        Section(b, "アカウントの作成（推奨）", items.Where(i => i.Action == SyncAction.CreateUser).ToList(), withChanges: false);

        b.AppendLine("## 参考（スクリプトに含めない）");
        b.AppendLine();
        b.AppendLine("| 種別 | 件数 |");
        b.AppendLine("| --- | --- |");
        foreach (var group in items.Where(i => i.Priority == SyncPriority.Info).GroupBy(i => i.Basis.Code ?? "-").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Row(b, group.Key, group.Count().ToString(CultureInfo.InvariantCulture));
        }

        b.AppendLine();
        b.AppendLine("## 設定（全文。計画の承認がこの設定の承認を兼ねる）");
        b.AppendLine();
        b.AppendLine("```json");
        using (var document = JsonDocument.Parse(plan.Settings.ToCanonicalJson()))
        {
            // コードブロックを閉じさせないよう、バッククォートはエスケープする
            b.AppendLine(JsonSerializer.Serialize(document.RootElement, SettingsJson).Replace("`", "\\u0060", StringComparison.Ordinal));
        }

        b.AppendLine("```");
        return b.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>CSV（1 行 1 変更。変更の無い項目は 1 行）。</summary>
    public static string Csv(IReadOnlyList<SyncPlanItem> items)
    {
        var b = new StringBuilder();
        CsvRow(b, ["sequence", "priority", "action", "in_script", "employee_number", "user_principal_name", "object_id", "field", "current", "desired", "current_known", "basis_code", "note"]);
        foreach (var item in items.OrderBy(i => i.Sequence))
        {
            var common = new[]
            {
                item.Sequence.ToString(CultureInfo.InvariantCulture), SyncPlanService.Code(item.Priority), SyncPlanService.Code(item.Action),
                item.InScript ? "true" : "false", item.EmployeeNumber, item.UserPrincipalName, item.ObjectId,
            };
            if (item.Changes.Count == 0)
            {
                CsvRow(b, [.. common, null, null, null, null, item.Basis.Code, item.Note]);
                continue;
            }

            foreach (var change in item.Changes)
            {
                CsvRow(b, [.. common, change.Field, change.Current, change.Desired, change.CurrentKnown ? "true" : "false", item.Basis.Code, item.Note]);
            }
        }

        return b.ToString();
    }

    /// <summary>Markdown の表のセルに入れる値（HTML・Markdown の記号をエスケープし、改行を詰める）。</summary>
    public static string Md(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "-";
        }

        var b = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '&': b.Append("&amp;"); break;
                case '<': b.Append("&lt;"); break;
                case '>': b.Append("&gt;"); break;
                case '\r' or '\n' or '\t': b.Append(' '); break;
                case '\\' or '|' or '`' or '*' or '_' or '[' or ']' or '(' or ')' or '#' or '!' or '~' or '$':
                    b.Append('\\').Append(c);
                    break;
                default:
                    if (!char.IsControl(c))
                    {
                        b.Append(c);
                    }

                    break;
            }
        }

        return b.ToString();
    }

    /// <summary>CSV のセル（RFC 4180 の引用。数式として解釈される先頭の文字は ' を前置して無害化する）。</summary>
    public static string CsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static void Section(StringBuilder b, string title, IReadOnlyList<SyncPlanItem> items, bool withChanges)
    {
        b.AppendLine(CultureInfo.InvariantCulture, $"## {title}（{items.Count} 件）");
        b.AppendLine();
        if (items.Count == 0)
        {
            b.AppendLine("なし");
            b.AppendLine();
            return;
        }

        if (withChanges)
        {
            ChangeTable(b, items);
        }
        else
        {
            ItemTable(b, items);
        }
    }

    private static void ItemTable(StringBuilder b, IReadOnlyList<SyncPlanItem> items)
    {
        if (items.Count == 0)
        {
            b.AppendLine("なし");
            b.AppendLine();
            return;
        }

        b.AppendLine("| 順 | 操作 | スクリプト | 社員番号 | UPN | 根拠 | 説明 |");
        b.AppendLine("| --- | --- | --- | --- | --- | --- | --- |");
        foreach (var item in items.OrderBy(i => i.Sequence))
        {
            b.AppendLine(CultureInfo.InvariantCulture,
                $"| {item.Sequence} | {ActionNames[item.Action]} | {(item.InScript ? "含める" : "含めない")} | {Md(item.EmployeeNumber)} | {Md(item.UserPrincipalName)} | {Md(Evidence(item))} | {Md(item.Note)} |");
        }

        b.AppendLine();
    }

    private static void ChangeTable(StringBuilder b, IReadOnlyList<SyncPlanItem> items)
    {
        b.AppendLine("| 順 | 操作 | スクリプト | 社員番号 | UPN | 項目 | Entra の現在値 | あるべき値（jin-ji） | 説明 |");
        b.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var item in items.OrderBy(i => i.Sequence))
        {
            var changes = item.Changes.Count == 0 ? [new SyncChange("-", null, null)] : item.Changes;
            foreach (var change in changes)
            {
                var current = change.CurrentKnown ? Md(change.Current) : "（入力に無し）";
                b.AppendLine(CultureInfo.InvariantCulture,
                    $"| {item.Sequence} | {ActionNames[item.Action]} | {(item.InScript ? "含める" : "含めない")} | {Md(item.EmployeeNumber)} | {Md(item.UserPrincipalName)} | {Md(change.Field)} | {current} | {Md(change.Desired)} | {Md(item.Note)} |");
            }
        }

        b.AppendLine();
    }

    private static string Evidence(SyncPlanItem item) =>
        string.Join("; ", item.Basis.Jinji.Where(e => e.Field != "id").Select(e => $"{e.Source}: {e.Value}"));

    private static string Count(IReadOnlyList<SyncPlanItem> items, SyncAction action) =>
        items.Count(i => i.Action == action).ToString(CultureInfo.InvariantCulture);

    private static void Row(StringBuilder b, string label, string value) => b.AppendLine(CultureInfo.InvariantCulture, $"| {label} | {Md(value)} |");

    private static void CsvRow(StringBuilder b, IEnumerable<string?> cells)
    {
        b.Append(string.Join(",", cells.Select(CsvCell)));
        b.Append("\r\n");
    }
}
