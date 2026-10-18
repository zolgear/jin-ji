using System.Text.RegularExpressions;
using JinJi.Domain.Organization;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>辞令の発令内容（自由記述）をルールで解釈した結果。</summary>
/// <param name="Type">発令の種類。</param>
/// <param name="OrgText">組織の記述（法人名が前に付くことがある。解釈の側で組織に引き当てる）。無ければ null。</param>
/// <param name="Title">役職（「部長」「リーダー」など）。無ければ null。</param>
/// <param name="TargetText">出向先の記述（「株式会社サンプル物流」「株式会社サンプル商事(販売分析チーム)」）。出向以外は null。</param>
/// <param name="IsNewHire">「新入社員」の配属。</param>
/// <param name="IsPlacement">「配属」（入社と同じ日なら入社時の配属。解釈の側で入社年月日と照らす）。</param>
public sealed record ParsedAction(PersonnelActionType Type, string? OrgText, string? Title, string? TargetText = null, bool IsNewHire = false, bool IsPlacement = false);

/// <summary>
/// 辞令の発令内容（自由記述）のルールによる解釈（LLM は使わない。解釈できない記述は null を返し、要確認・修正依頼にする）。
/// </summary>
/// <remarks>
/// 扱う形（表示用の正規化の後。全角の括弧・コロンは半角になる）:
/// <list type="table">
/// <item><term>出向</term><description>「X へ出向を命ずる」「X(組織) へ出向を命ずる」「出向を命ずる(出向先: X)」/ 解除「出向を解く」「帰任を命ずる」</description></item>
/// <item><term>兼務</term><description>「組織 役職 を兼務させる / を兼務することを命ずる」/ 解除「(組織の)兼務を解く」</description></item>
/// <item><term>休職・復職</term><description>「休職を命ずる」「復職を命ずる」（一時停止の開始・終了。理由は持たない）</description></item>
/// <item><term>入社・退職</term><description>「新入社員 組織 配属を命ずる」/「退職を命ずる」など</description></item>
/// <item><term>異動</term><description>「組織 に配置換え / への異動 / 勤務 / に配属 を命ずる」（「配属」は入社と同じ日なら入社時の配属として扱う）</description></item>
/// <item><term>役職の任命</term><description>「組織 役職 を命ずる」「組織役職に任ずる」（役職は語尾の一覧から最長一致で切り出す。「チームリーダー」は組織名の「チーム」+「リーダー」とみなす）</description></item>
/// </list>
/// </remarks>
public static partial class ActionTextParser
{
    /// <summary>役職の一覧（語尾で切り出す。長いものを優先）。</summary>
    public static readonly IReadOnlyList<string> Titles =
    [
        "副本部長", "本部長", "副部長", "部長", "次長", "室長", "課長代理", "課長", "係長", "グループ長", "リーダー", "主任",
        "支店長", "所長", "工場長", "センター長",
    ];

    /// <summary>役職が表す段のキー（既定の段の定義・子会社の定義で使うキー）。当てはまらなければ段を問わない。</summary>
    public static readonly IReadOnlyDictionary<string, string> TitleLevels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["本部長"] = "division",
        ["副本部長"] = "division",
        ["部長"] = "department",
        ["副部長"] = "department",
        ["次長"] = "department",
        ["課長"] = "section",
        ["課長代理"] = "section",
        ["係長"] = "unit",
        ["グループ長"] = "group",
        ["リーダー"] = "team",
    };

    public static ParsedAction? Parse(string? text)
    {
        if (TextNormalizer.ToDisplay(text) is not { } display)
        {
            return null;
        }

        var t = display.TrimEnd('。', '.').Trim();

        if (SecondmentEnd().IsMatch(t))
        {
            return new ParsedAction(PersonnelActionType.SecondmentEnd, null, null);
        }

        if (SecondmentTo().Match(t) is { Success: true } to)
        {
            return new ParsedAction(PersonnelActionType.SecondmentStart, null, null, to.Groups["target"].Value.Trim());
        }

        if (Secondment().Match(t) is { Success: true } secondment)
        {
            return new ParsedAction(PersonnelActionType.SecondmentStart, null, null, secondment.Groups["target"].Value.Trim());
        }

        if (ConcurrentEnd().Match(t) is { Success: true } concurrentEnd)
        {
            var org = concurrentEnd.Groups["org"].Value.Trim();
            return new ParsedAction(PersonnelActionType.ConcurrentEnd, org.Length == 0 ? null : org, null);
        }

        if (Concurrent().Match(t) is { Success: true } concurrent)
        {
            var (org, title) = SplitTitle(concurrent.Groups["body"].Value);
            return new ParsedAction(PersonnelActionType.ConcurrentStart, org, title);
        }

        if (t is "休職を命ずる" or "休職とする")
        {
            return new ParsedAction(PersonnelActionType.SuspensionStart, null, null);
        }

        if (t is "復職を命ずる" or "復職とする")
        {
            return new ParsedAction(PersonnelActionType.SuspensionEnd, null, null);
        }

        if (Termination().IsMatch(t))
        {
            return new ParsedAction(PersonnelActionType.Termination, null, null);
        }

        if (Hire().Match(t) is { Success: true } hire)
        {
            return new ParsedAction(PersonnelActionType.Hire, hire.Groups["org"].Value.Trim(), null, IsNewHire: true);
        }

        if (Transfer().Match(t) is { Success: true } transfer)
        {
            return new ParsedAction(PersonnelActionType.Transfer, transfer.Groups["org"].Value.Trim(), null, IsPlacement: transfer.Groups["kind"].Value == "配属");
        }

        if (Appointment().Match(t) is { Success: true } appointment)
        {
            var (org, title) = SplitTitle(appointment.Groups["body"].Value);
            if (title is null)
            {
                return null;
            }

            return new ParsedAction(org is null ? PersonnelActionType.PositionAppointment : PersonnelActionType.Transfer, org, title);
        }

        return null;
    }

    /// <summary>「組織 役職」を組織と役職に分ける（役職は語尾の一覧から最長一致）。役職が無ければ全体を組織とする。</summary>
    public static (string? Org, string? Title) SplitTitle(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var text = body.Trim();
        foreach (var title in Titles.OrderByDescending(x => x.Length))
        {
            if (text.EndsWith(title, StringComparison.Ordinal))
            {
                var org = text[..^title.Length].Trim().TrimEnd('の').Trim();
                return (org.Length == 0 ? null : org, title);
            }
        }

        return (text.Length == 0 ? null : text, null);
    }

    [GeneratedRegex(@"出向を(解く|解除する|命ずる\s*\(帰任\))|帰任を命ずる", RegexOptions.CultureInvariant)]
    private static partial Regex SecondmentEnd();

    [GeneratedRegex(@"^出向を命ずる\s*\(\s*出向先\s*[:：]?\s*(?<target>[^)]+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex Secondment();

    [GeneratedRegex(@"^(?<target>.+?)\s*(へ|に)\s*出向を命ずる$", RegexOptions.CultureInvariant)]
    private static partial Regex SecondmentTo();

    [GeneratedRegex(@"^(?<org>.*?)\s*の?\s*兼務を(解く|解除する)$", RegexOptions.CultureInvariant)]
    private static partial Regex ConcurrentEnd();

    [GeneratedRegex(@"^(?<body>.+?)\s*(を兼務させる|を兼務することを命ずる|の兼務を命ずる|兼務を命ずる|を兼務とする)$", RegexOptions.CultureInvariant)]
    private static partial Regex Concurrent();

    [GeneratedRegex(@"退職を命ずる|退職とする|退職を承認する|依願退職|定年退職", RegexOptions.CultureInvariant)]
    private static partial Regex Termination();

    [GeneratedRegex(@"^新入社員\s*(?<org>.+?)\s*(に|へ)?\s*配属を命ずる$", RegexOptions.CultureInvariant)]
    private static partial Regex Hire();

    [GeneratedRegex(@"^(?<org>.+?)\s*(への|に|へ)?\s*(?<kind>配置換え|異動|配属|勤務|転勤)を命ずる$", RegexOptions.CultureInvariant)]
    private static partial Regex Transfer();

    [GeneratedRegex(@"^(?<body>.+?)\s*(を命ずる|に任ずる|を任ずる|に任命する)$", RegexOptions.CultureInvariant)]
    private static partial Regex Appointment();
}
