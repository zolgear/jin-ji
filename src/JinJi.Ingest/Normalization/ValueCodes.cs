namespace JinJi.Ingest.Normalization;

/// <summary>
/// 取込元の表示値を DB のコード値にする（雇用区分・主務/兼務）。表に無い値は解釈できないとして修正依頼にする（推測で決めない）。
/// </summary>
public static class ValueCodes
{
    private static readonly Dictionary<string, string> EmploymentTypes = Build(new()
    {
        ["regular"] = ["正社員", "正規", "正規社員", "正規雇用", "社員", "regular", "fulltime", "full-time"],
        ["contract"] = ["契約", "契約社員", "有期", "有期雇用", "有期契約", "contract"],
        ["entrusted"] = ["嘱託", "嘱託社員", "嘱託職員", "entrusted"],
        ["dispatched"] = ["派遣", "派遣社員", "派遣スタッフ", "dispatched"],
        ["seconded_in"] = ["出向受入", "出向受け入れ", "出向受", "受入出向", "seconded_in"],
        ["part_time"] = ["パート", "アルバイト", "パートタイマー", "パート・アルバイト", "パート/アルバイト", "part_time", "parttime"],
        ["other"] = ["その他", "other"],
    });

    private static readonly Dictionary<string, string> AssignmentTypes = Build(new()
    {
        ["primary"] = ["主務", "本務", "主", "primary"],
        ["concurrent"] = ["兼務", "兼", "兼任", "concurrent"],
    });

    /// <summary>雇用区分のコード（regular / contract / ...）。解釈できなければ null。</summary>
    public static string? EmploymentType(string? value) => Lookup(EmploymentTypes, value);

    /// <summary>主務/兼務のコード（primary / concurrent）。解釈できなければ null。</summary>
    public static string? AssignmentType(string? value) => Lookup(AssignmentTypes, value);

    private static string? Lookup(Dictionary<string, string> table, string? value)
    {
        var key = Key(value);
        return key is null ? null : table.GetValueOrDefault(key);
    }

    private static string? Key(string? value) =>
        TextNormalizer.CleanWhitespace(value) is { } cleaned ? TextNormalizer.Nfkc(cleaned).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant() : null;

    private static Dictionary<string, string> Build(Dictionary<string, string[]> source)
    {
        var table = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, labels) in source)
        {
            foreach (var label in labels)
            {
                table.Add(Key(label)!, code);
            }
        }

        return table;
    }
}
