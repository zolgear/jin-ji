using System.Text;
using System.Text.RegularExpressions;

namespace JinJi.Domain.Text;

/// <summary>
/// 人名の「読み」の照合キー。ひらがな・カタカナ・半角カナ・全角英字・ローマ字の綴りの揺れ（ヘボン式・訓令式・ワープロ式）と長音・促音の有無を吸収する。
/// </summary>
/// <remarks>
/// NFKC → 英字の小文字化 → 区切り（空白・ゼロ幅の文字・<c>・-‐'’.,</c>）を除く → かな・英字以外を含むなら null →
/// かなを訓令式寄りのローマ字にする → 英字の綴りを正規化する（shi→si、chi→ti、tsu→tu、ji→zi、f→h、mb→nb など）→
/// 畳み込み（語中・語末の oh→o、長音の母音の重なり、同じ子音の重なり）。結果は英小文字 a-z だけ。
/// 規則は Web UI の <c>web/src/lib/readingKey.ts</c> と同じ。両者の一致は共有のテストベクタ（<c>web/src/lib/readingKey.vectors.json</c>）で確かめる。
/// </remarks>
public static partial class ReadingKey
{
    /// <summary>照合に使う最小の長さ（これより短いキーは部分一致に使わない）。</summary>
    public const int MinMatchLength = 3;

    /// <summary>ぁ（U+3041）〜ゖ（U+3096）のローマ字。小さい ぁぃぅぇぉ・ゃゅょ・っ は空（別に扱う）。</summary>
    private static readonly string[] Romaji =
    [
        "", "a", "", "i", "", "u", "", "e", "", "o",
        "ka", "ga", "ki", "gi", "ku", "gu", "ke", "ge", "ko", "go",
        "sa", "za", "si", "zi", "su", "zu", "se", "ze", "so", "zo",
        "ta", "da", "ti", "zi", "", "tu", "zu", "te", "de", "to", "do",
        "na", "ni", "nu", "ne", "no",
        "ha", "ba", "pa", "hi", "bi", "pi", "hu", "bu", "pu", "he", "be", "pe", "ho", "bo", "po",
        "ma", "mi", "mu", "me", "mo",
        "", "ya", "", "yu", "", "yo",
        "ra", "ri", "ru", "re", "ro",
        "wa", "wa", "i", "e", "o", "n", "vu", "ka", "ke",
    ];

    /// <summary>英字の綴りの正規化（この順に当てる）。</summary>
    private static readonly (string From, string To)[] Spellings =
    [
        ("tch", "ch"), ("shi", "si"), ("sh", "sy"), ("chi", "ti"), ("ch", "ty"), ("tsu", "tu"),
        ("ji", "zi"), ("j", "zy"), ("f", "h"), ("c", "k"), ("l", "r"),
    ];

    /// <summary>長音として畳む母音の重なり（変化が無くなるまで繰り返す）。</summary>
    private static readonly (string From, string To)[] LongVowels =
    [
        ("ou", "o"), ("oo", "o"), ("uu", "u"), ("aa", "a"), ("ii", "i"), ("ee", "e"),
    ];

    /// <summary>読みの照合キー。読みとして扱えない値（漢字・数字・記号を含む）や空なら null。</summary>
    public static string? From(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length * 2);

        // 直前の 1 文字ぶんの出力の開始位置（小さいかなが直前の音を書き換えるため）
        var chunkStart = 0;
        foreach (var raw in normalized)
        {
            // 英字の小文字化は ASCII だけ（ほかの文字はどのみち null にするため。TS 側と結果をそろえる）
            var c = raw is >= 'A' and <= 'Z' ? (char)(raw + 0x20) : raw;
            if (char.IsWhiteSpace(c) || SearchKey.IsZeroWidth(c) || IsSeparator(c))
            {
                continue;
            }

            if (c is >= 'a' and <= 'z')
            {
                chunkStart = builder.Length;
                builder.Append(c);
                continue;
            }

            if (c is >= 'ァ' and <= 'ヶ')
            {
                c = (char)(c - 0x60);
            }
            else if (c == 'ー')
            {
                chunkStart = builder.Length;
                continue;
            }
            else if (c is not (>= 'ぁ' and <= 'ゖ'))
            {
                return null;
            }

            AppendKana(builder, ref chunkStart, c);
        }

        if (builder.Length == 0)
        {
            return null;
        }

        // 綴りの正規化と畳み込みは、結果が変わらなくなるまで繰り返す（畳み込みで新しくできた綴りも正規化し、冪等にする）
        var key = builder.ToString();
        while (true)
        {
            var next = Fold(NormalizeSpelling(key));
            if (next == key)
            {
                break;
            }

            key = next;
        }

        return key.Length == 0 ? null : key;
    }

    /// <summary>読みの区切りとして除く記号。</summary>
    private static bool IsSeparator(char c) => c is '・' or '-' or '‐' or '\'' or '’' or '.' or ',';

    /// <summary>ひらがな 1 文字をローマ字にして足す。</summary>
    private static void AppendKana(StringBuilder builder, ref int chunkStart, char c)
    {
        var chunkLength = builder.Length - chunkStart;
        switch (c)
        {
            case 'っ':
                // 促音は何も出さない（後段の同じ子音の畳み込みで、促音の有無を同一視する）
                chunkStart = builder.Length;
                return;
            case 'ゃ' or 'ゅ' or 'ょ':
            {
                var vowel = c switch { 'ゃ' => 'a', 'ゅ' => 'u', _ => 'o' };
                if (chunkLength >= 2 && builder[^1] == 'i')
                {
                    // きゃ → kya、しゃ → sya
                    builder[^1] = 'y';
                    builder.Append(vowel);
                }
                else
                {
                    chunkStart = builder.Length;
                    builder.Append('y').Append(vowel);
                }

                return;
            }
            case 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ':
            {
                var vowel = c switch { 'ぁ' => 'a', 'ぃ' => 'i', 'ぅ' => 'u', 'ぇ' => 'e', _ => 'o' };
                if (chunkLength >= 2 && IsVowel(builder[^1]) && !IsVowel(builder[^2]))
                {
                    // ふぁ → ha、てぃ → ti、ゔぁ → va
                    builder[^1] = vowel;
                }
                else
                {
                    chunkStart = builder.Length;
                    builder.Append(vowel);
                }

                return;
            }
            default:
                chunkStart = builder.Length;
                builder.Append(Romaji[c - 'ぁ']);
                return;
        }
    }

    private static bool IsVowel(char c) => c is 'a' or 'i' or 'u' or 'e' or 'o';

    /// <summary>ヘボン式・ワープロ式などの綴りを訓令式寄りにそろえる。</summary>
    private static string NormalizeSpelling(string value)
    {
        foreach (var (from, to) in Spellings)
        {
            value = value.Replace(from, to, StringComparison.Ordinal);
        }

        // 撥音の m（Namba・Homma）を n にそろえる
        return MBeforeLabial().Replace(value, "n");
    }

    /// <summary>語中・語末の oh、長音の母音の重なり、同じ子音の重なりを畳む。</summary>
    private static string Fold(string value)
    {
        value = OhBeforeConsonant().Replace(value, "o");

        string previous;
        do
        {
            previous = value;
            foreach (var (from, to) in LongVowels)
            {
                value = value.Replace(from, to, StringComparison.Ordinal);
            }
        }
        while (value != previous);

        return DoubledConsonant().Replace(value, "$1");
    }

    [GeneratedRegex("m(?=[bpm])", RegexOptions.CultureInvariant)]
    private static partial Regex MBeforeLabial();

    [GeneratedRegex("oh(?![aiueoy])", RegexOptions.CultureInvariant)]
    private static partial Regex OhBeforeConsonant();

    [GeneratedRegex("([bcdfghjklmnpqrstvwxz])\\1+", RegexOptions.CultureInvariant)]
    private static partial Regex DoubledConsonant();
}
