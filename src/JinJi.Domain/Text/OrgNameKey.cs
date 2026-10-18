using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JinJi.Domain.Text;

/// <summary>
/// 組織名の検索用の正規化表記。「第一営業部」「第１営業部」「第1営業部」、「営業一課」「営業1課」を同じ表記にそろえる。
/// 取込（名寄せ。<c>JinJi.Ingest.Normalization.OrgNameNormalizer</c> はこれに委ねる）と参照の組織の検索（REST・MCP）が同じ規則を使う。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>NFKC（全角数字 → 半角）→ 異体字 → 空白・ゼロ幅の文字の除去 → 漢数字を算用数字に（「第」の後と、課・部・係などの単位の前だけ）→ <see cref="SearchKey"/></item>
/// <item>数字の前の「第」は除く（「営業第一部」「営業1部」「営業１部」を同じ表記にする）</item>
/// <item>漢数字の置き換えを文脈に限るのは、「一般」「十日町」のような固有名の中の漢数字を変えないため</item>
/// </list>
/// </remarks>
public static partial class OrgNameKey
{
    /// <summary>組織名の検索用の正規化表記。空（空白だけを含む）なら null。</summary>
    public static string? From(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = KanjiVariants.Normalize(value.Normalize(NormalizationForm.FormKC));
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (!char.IsWhiteSpace(c) && !SearchKey.IsZeroWidth(c))
            {
                builder.Append(c);
            }
        }

        if (builder.Length == 0)
        {
            return null;
        }

        var text = Ordinal().Replace(builder.ToString(), m => "第" + ToArabic(m.Groups["n"].Value));
        text = Counter().Replace(text, m => ToArabic(m.Groups["n"].Value) + m.Groups["u"].Value);

        // 「製造第2部」と「製造2部」のように、数字の前の「第」の有無は同じ組織の表記ゆれとして扱う
        text = OrdinalPrefix().Replace(text, "$1");
        return SearchKey.From(text);
    }

    /// <summary>漢数字（〇一二三四五六七八九十百千）を整数にする。位取り（一〇）と十・百・千の組み合わせ（二十三）の両方を扱う。</summary>
    public static int ParseKanjiNumber(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var total = 0;
        var current = 0;
        foreach (var c in value)
        {
            var digit = "〇一二三四五六七八九".IndexOf(c, StringComparison.Ordinal);
            if (digit >= 0)
            {
                current = (current * 10) + digit;
                continue;
            }

            var unit = c switch
            {
                '十' => 10,
                '百' => 100,
                '千' => 1000,
                _ => throw new FormatException($"漢数字ではありません: {c}"),
            };
            total += (current == 0 ? 1 : current) * unit;
            current = 0;
        }

        return total + current;
    }

    private static string ToArabic(string kanji) => ParseKanjiNumber(kanji).ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("第([0-9]+)", RegexOptions.CultureInvariant)]
    private static partial Regex OrdinalPrefix();

    [GeneratedRegex("第(?<n>[〇一二三四五六七八九十百千]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Ordinal();

    [GeneratedRegex("(?<n>[〇一二三四五六七八九十百千]+)(?<u>課|部|係|班|室|グループ|チーム|本部|事業部|営業所|支店|支社|工場|センター|研究所|ユニット)", RegexOptions.CultureInvariant)]
    private static partial Regex Counter();
}
