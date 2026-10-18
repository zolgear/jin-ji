using System.Text;
using JinJi.Domain.Text;

namespace JinJi.Ingest.Normalization;

/// <summary>
/// 文字列の正規化（Unicode NFKC・全角/半角・カナ・空白）。表示用と検索用で規則を分ける（アーキテクチャ概要 §4.4）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>表示用（<see cref="ToDisplay"/>）: 空白の正規化、半角カナ → 全角カナ、全角英数記号 → 半角。漢字は変えない
/// （NFKC は CJK 互換漢字を統合漢字に置き換えるので、正式表記を壊さないよう表示用には使わない）</item>
/// <item>検索用（<see cref="ToSearchKey"/>）: NFKC → 異体字の正規化 → ひらがな → カタカナ → 小文字 → 空白をすべて除く</item>
/// <item>フリガナ（<see cref="ToKana"/>）: NFKC（半角カナの濁点も合成する）→ ひらがな → カタカナ → 空白の正規化</item>
/// </list>
/// </remarks>
public static class TextNormalizer
{
    /// <summary>Unicode NFKC。</summary>
    public static string Nfkc(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Normalize(NormalizationForm.FormKC);
    }

    /// <summary>
    /// 空白の正規化: 全角空白・ノーブレークスペース・タブ・改行などを半角空白 1 つにまとめ、前後を除く。ゼロ幅の文字は除く。空なら null。
    /// </summary>
    public static string? CleanWhitespace(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (IsZeroWidth(c))
            {
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>表示用の正規化（空白・半角カナ → 全角・全角英数記号 → 半角）。漢字（異体字・互換漢字を含む）は変えない。空なら null。</summary>
    public static string? ToDisplay(string? value)
    {
        var cleaned = CleanWhitespace(value);
        if (cleaned is null)
        {
            return null;
        }

        var builder = new StringBuilder(cleaned.Length);
        var i = 0;
        while (i < cleaned.Length)
        {
            var c = cleaned[i];
            if (IsHalfwidthKana(c))
            {
                // 半角カナの連なりを NFKC で全角にする（ｶﾞ → ガ のように濁点・半濁点も合成される）
                var start = i;
                while (i < cleaned.Length && IsHalfwidthKana(cleaned[i]))
                {
                    i++;
                }

                builder.Append(cleaned[start..i].Normalize(NormalizationForm.FormKC));
                continue;
            }

            builder.Append(c is >= '！' and <= '～' ? (char)(c - 0xFEE0) : c);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>フリガナの正規化（NFKC・ひらがな → カタカナ・空白の正規化）。空なら null。</summary>
    public static string? ToKana(string? value)
    {
        var cleaned = CleanWhitespace(value);
        return cleaned is null ? null : CleanWhitespace(HiraganaToKatakana(Nfkc(cleaned)));
    }

    /// <summary>検索用の正規化表記（NFKC・異体字・ひらがな → カタカナ・小文字・空白を除く）。空なら null。規則は <see cref="SearchKey"/>（参照の検索と共有）。</summary>
    public static string? ToSearchKey(string? value) => SearchKey.From(value);

    /// <summary>ひらがな（ぁ〜ゖ、ゝゞ）をカタカナにする。</summary>
    public static string HiraganaToKatakana(string value) => SearchKey.HiraganaToKatakana(value);

    private static bool IsHalfwidthKana(char c) => c is >= '｡' and <= 'ﾟ';

    private static bool IsZeroWidth(char c) => SearchKey.IsZeroWidth(c);
}
