using System.Text;

namespace JinJi.Domain.Text;

/// <summary>
/// 検索用の正規化表記（アーキテクチャ概要 §4.4）。取込（名寄せ・<c>person_version.search_name</c>）と参照の検索（REST・MCP）が同じ規則を使う。
/// </summary>
/// <remarks>
/// NFKC → 異体字の正規化（<see cref="KanjiVariants"/>）→ ひらがな → カタカナ → 小文字 → 空白・ゼロ幅の文字をすべて除く。
/// 取込の <c>TextNormalizer.ToSearchKey</c> はこれに委ねる（規則を二重に持たない）。
/// </remarks>
public static class SearchKey
{
    /// <summary>検索用の正規化表記。空（空白だけを含む）なら null。</summary>
    public static string? From(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = HiraganaToKatakana(KanjiVariants.Normalize(value.Normalize(NormalizationForm.FormKC))).ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (!char.IsWhiteSpace(c) && !IsZeroWidth(c))
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>ひらがな（ぁ〜ゖ、ゝゞ）をカタカナにする。</summary>
    public static string HiraganaToKatakana(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var chars = value.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is (>= 'ぁ' and <= 'ゖ') or 'ゝ' or 'ゞ')
            {
                chars[i] = (char)(chars[i] + 0x60);
            }
        }

        return new string(chars);
    }

    /// <summary>ゼロ幅の文字（ZWSP・ZWNJ・ZWJ・WJ・BOM）。</summary>
    public static bool IsZeroWidth(char c) => c is '​' or '‌' or '‍' or '⁠' or '﻿';
}
