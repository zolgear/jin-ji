using System.Text;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>
/// 取込元の列名が、保持しない項目（ADR-0002）を表しているか。当てはまる列は、項目マッピングの指定に関わらず staging に保存する前に捨てる。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>人事パッケージのエクスポートには生年月日・性別・住所・給与などが混ざっていることが多い。マッピングの設定ミス（誤って取込項目に対応させる）
/// への二重の守りとして、列名でも判定する</item>
/// <item>日本語は部分文字列で、英語は語（英字以外で区切った単位）で判定する（age が manager や page に当たらないように）</item>
/// <item>誤検知した列は捨てられるだけで、取込結果に「保持禁止として捨てた」と残る。列名を変えるか、ここの規則を見直す</item>
/// </list>
/// </remarks>
public static class ForbiddenColumnNames
{
    private static readonly string[] JapaneseSubstrings =
    [
        // 生年月日・年齢・性別
        "生年月日", "誕生日", "年齢", "性別",
        // 住所・私用連絡先
        "住所", "郵便番号", "〒", "自宅", "私用", "個人携帯", "個人メール", "緊急連絡",
        // 家族・扶養
        "家族", "扶養", "配偶者", "続柄",
        // 給与・賞与・等級・口座・税・保険
        "給与", "給料", "基本給", "賃金", "賞与", "ボーナス", "手当", "等級", "号俸", "口座", "銀行", "税", "年金", "保険",
        // 評価
        "評価", "考課", "査定",
        // 健康・休職理由・障害
        "健康", "病", "休職理由", "休業理由", "障害", "障がい", "診断", "血液型",
        // 国籍・宗教
        "国籍", "本籍", "宗教", "在留",
        // 学歴・職歴・戸籍
        "学歴", "職歴", "出身校", "戸籍",
        // マイナンバー
        "マイナンバー", "個人番号",
    ];

    private static readonly HashSet<string> EnglishWords = new(StringComparer.Ordinal)
    {
        "birth", "birthday", "birthdate", "dob", "age", "gender", "sex",
        "address", "zip", "postal", "postcode", "home", "private", "personal",
        "spouse", "dependent", "dependents", "marital",
        "salary", "wage", "wages", "pay", "payroll", "bonus", "allowance", "grade", "bank", "tax", "pension", "insurance",
        "evaluation", "appraisal", "rating", "performance",
        "health", "medical", "illness", "disability", "diagnosis", "blood",
        "nationality", "citizenship", "religion", "passport",
        "education", "school", "university", "resume",
        "koseki", "maiden", "mynumber",
    };

    /// <summary>列名が保持しない項目を表していれば true。</summary>
    public static bool IsForbidden(string columnName)
    {
        ArgumentNullException.ThrowIfNull(columnName);
        var text = TextNormalizer.Nfkc(columnName).ToLowerInvariant();
        if (JapaneseSubstrings.Any(s => text.Contains(s, StringComparison.Ordinal)))
        {
            return true;
        }

        // 英字の語に分ける（camelCase も分ける: DateOfBirth → date, of, birth）
        var words = new List<string>();
        var current = new StringBuilder();
        var original = TextNormalizer.Nfkc(columnName);
        for (var i = 0; i < original.Length; i++)
        {
            var c = original[i];
            if (char.IsAsciiLetter(c))
            {
                if (char.IsUpper(c) && current.Length > 0 && i > 0 && char.IsLower(original[i - 1]))
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                current.Append(char.ToLowerInvariant(c));
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words.Any(EnglishWords.Contains) || text.Contains("my_number", StringComparison.Ordinal);
    }
}
