using System.Globalization;
using System.Text;
using JinJi.Application.Ingest;

namespace JinJi.Application.Corrections;

/// <summary>
/// 修正依頼のレポート（人事パッケージの担当者の作業リスト）。列と行の文字列にしたもので、CSV（REST・管理 CLI）と xlsx（管理 CLI。JinJi.Connectors）が共有する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>列は作業の順: 状態・経過日数 → 誰の（社員番号・氏名・法人・主務の組織）→ 何を（項目・理由・正しい値の提案）→ 根拠（起票の経路・賛同数・取込のファイルと行番号）→ 識別子</item>
/// <item>CSV は UTF-8（BOM 付き。Excel で文字化けしない）・CRLF・RFC 4180 の引用。数式として解釈される先頭の文字（= + - @ タブ CR）には ' を前置する（同期の差分レポートと同じ）</item>
/// <item>載せるのは閲覧者（修正担当者・人事管理者）に見える値だけ（氏名などは投影を通す）。jin-ji の現在の値は載せない（正は人事パッケージにある）</item>
/// </list>
/// </remarks>
public static class CorrectionReport
{
    /// <summary>列の見出し。</summary>
    public static readonly IReadOnlyList<string> Headers =
    [
        "状態", "経過日数", "社員番号", "氏名", "法人", "主務の組織", "項目", "理由・内容", "正しい値の提案",
        "起票の経路", "賛同数", "取込のファイル", "取込の行番号", "起票日", "種別", "項目のコード", "依頼 ID", "閉じた日", "対応のメモ",
    ];

    /// <summary>行（見出しと同じ順の文字列）。</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Rows(CorrectionExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        return [.. export.Items.Select(item => Row(item, item.PersonId is { } id ? export.People.GetValueOrDefault(id) : null))];
    }

    /// <summary>CSV（UTF-8 BOM・CRLF）。</summary>
    public static byte[] ToCsv(CorrectionExport export)
    {
        ArgumentNullException.ThrowIfNull(export);
        var text = new StringBuilder();
        AppendLine(text, Headers);
        foreach (var row in Rows(export))
        {
            AppendLine(text, row);
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>ダウンロードのファイル名（業務日付と状態）。</summary>
    public static string FileName(CorrectionExport export, CorrectionStatusFilter status, string extension)
    {
        ArgumentNullException.ThrowIfNull(export);
        return $"jinji-corrections-{StatusFilterCode(status)}-{export.Today:yyyyMMdd}.{extension}";
    }

    public static string StatusFilterCode(CorrectionStatusFilter status) => status switch
    {
        CorrectionStatusFilter.Open => "open",
        CorrectionStatusFilter.Closed => "closed",
        _ => "all",
    };

    public static string StatusLabel(CorrectionStatus status) => status switch
    {
        CorrectionStatus.Open => "未対応",
        CorrectionStatus.Resolved => "対応済み",
        CorrectionStatus.Dismissed => "対応不要",
        CorrectionStatus.AutoResolved => "解消（取り込み直しで値が変わった）",
        CorrectionStatus.Superseded => "置き換え（次の取込で改めて起票）",
        _ => status.ToString(),
    };

    public static string OriginLabel(CorrectionOrigin origin) => origin switch
    {
        CorrectionOrigin.Ingest => "取込の自動検出",
        CorrectionOrigin.User => "人",
        CorrectionOrigin.Agent => "AI エージェント（委任元の名義）",
        _ => origin.ToString(),
    };

    private static List<string> Row(CorrectionView item, CorrectionPersonSummary? person)
    {
        var field = item.PersonField ?? item.SourceField;
        string Date(DateTimeOffset? at) => at?.ToOffset(Domain.BusinessDate.UtcOffset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
        return
        [
            StatusLabel(item.Status),
            item.Status == CorrectionStatus.Open ? item.AgeDays.ToString(CultureInfo.InvariantCulture) : string.Empty,
            person?.EmployeeNumber ?? string.Empty,
            person?.DisplayName ?? string.Empty,
            person?.LegalEntityName ?? string.Empty,
            person?.PrimaryOrgName ?? string.Empty,
            field is null ? string.Empty : CorrectionFields.Label(field),
            item.Reason ?? item.Message,
            item.SuggestedValue ?? string.Empty,
            OriginLabel(item.Origin),
            item.Endorsements.ToString(CultureInfo.InvariantCulture),
            item.FileName ?? string.Empty,
            item.SourceRowNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Date(item.CreatedAt),
            System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(item.Issue.ToString()),
            field ?? string.Empty,
            item.Id.ToString("D"),
            Date(item.ClosedAt),
            item.ClosedNote ?? string.Empty,
        ];
    }

    private static void AppendLine(StringBuilder text, IEnumerable<string> cells)
    {
        text.Append(string.Join(',', cells.Select(Quote)));
        text.Append("\r\n");
    }

    /// <summary>RFC 4180 の引用と、数式の注入の対策（先頭の = + - @ タブ CR に ' を前置）。</summary>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || value.StartsWith(' ') || value.EndsWith(' ')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
