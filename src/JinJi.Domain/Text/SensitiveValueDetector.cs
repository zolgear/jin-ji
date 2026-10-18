using System.Text;
using System.Text.RegularExpressions;

namespace JinJi.Domain.Text;

/// <summary>
/// 自由記述の項目に、保持しない情報（生年月日・住所・私用の電話番号）らしき値が混ざっていないかの検査（ADR-0002「混入を検知する」）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>取込では自由記述の項目（JinJi.Ingest の <c>IngestFields.FreeText</c>）だけを対象にする。内線・社用携帯の項目は電話番号が正しい値なので対象にしない。
/// 当てはまった値は staging に保存する前に除き、修正依頼（suspected_sensitive_value）に項目名と種別だけを記録する（値は記録しない）</item>
/// <item>修正依頼の理由・対応のメモ・値の提案（人・AI エージェントが書く自由記述）も同じ規則で検査し、当てはまれば保存しない（JinJi.Application の修正依頼）</item>
/// <item>規則は誤検知を避けて狭めにしている: 日付だけ（「2025/03/31 廃止」）は生年月日とみなさず、「生」「生年月日」「歳」などの文脈を要る。
/// 住所は都道府県 + 市区町村 + 番地、郵便番号は 〒 か 3 桁-4 桁、電話番号は市外局番の形（0 から始まる 3 つの数字の組）</item>
/// </list>
/// </remarks>
public static partial class SensitiveValueDetector
{
    /// <summary>疑いの種別（修正依頼のメッセージに使う）。</summary>
    public enum Kind
    {
        BirthDate,
        Address,
        PhoneNumber,
    }

    /// <summary>値に当てはまった疑いの種別（重複なし）。空なら問題なし。</summary>
    public static IReadOnlyList<Kind> Detect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var text = Clean(value).Normalize(NormalizationForm.FormKC);
        var kinds = new List<Kind>();
        if (Birth().IsMatch(text))
        {
            kinds.Add(Kind.BirthDate);
        }

        if (Address().IsMatch(text) || PostalCode().IsMatch(text))
        {
            kinds.Add(Kind.Address);
        }

        if (Phone().IsMatch(text))
        {
            kinds.Add(Kind.PhoneNumber);
        }

        return kinds;
    }

    /// <summary>ゼロ幅の文字を除く（区切りに挟んで規則をすり抜けさせない）。</summary>
    private static string Clean(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (!SearchKey.IsZeroWidth(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    public static string Label(Kind kind) => kind switch
    {
        Kind.BirthDate => "生年月日",
        Kind.Address => "住所",
        Kind.PhoneNumber => "電話番号",
        _ => kind.ToString(),
    };

    [GeneratedRegex(@"生年月日|誕生日|[0-9]{1,3}\s*歳|[0-9]{1,4}\s*[年./-]\s*[0-9]{1,2}\s*[月./-]\s*[0-9]{1,2}\s*日?\s*(生まれ|生)", RegexOptions.CultureInvariant)]
    private static partial Regex Birth();

    [GeneratedRegex(@"(北海道|東京都|京都府|大阪府|.{2,3}県)\s*\S{1,6}?[市区町村郡]\S{0,12}?[0-9]+\s*[-－丁目番]\s*[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Address();

    [GeneratedRegex(@"〒\s*[0-9]{3}|(^|[^0-9])[0-9]{3}-[0-9]{4}($|[^0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex PostalCode();

    [GeneratedRegex(@"(^|[^0-9])0[0-9]{1,4}[-(]\s*[0-9]{1,4}[-)]\s*[0-9]{3,4}($|[^0-9])|\+81[-\s]?[0-9]", RegexOptions.CultureInvariant)]
    private static partial Regex Phone();
}
