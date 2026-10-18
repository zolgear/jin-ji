using System.Text;

namespace JinJi.Ingest.Normalization;

/// <summary>社員番号の正規化の規則（項目マッピングの <c>employeeNumber</c>）。</summary>
public sealed record EmployeeNumberRules
{
    /// <summary>
    /// 数字だけの社員番号を、この桁数まで先頭を 0 で埋める（Excel で先頭の 0 が落ちた「123」を「000123」に戻す）。
    /// 先頭の 0 を一度取り除いてから埋めるので、「0000123」も同じ桁数にそろう。null なら埋めない。
    /// </summary>
    public int? PadLength { get; init; }

    /// <summary>
    /// 英字の接頭辞 + 数字の社員番号（子会社の L0045 など）の数字の部分を、この桁数まで 0 で埋める（L45 → L0045）。null なら埋めない。
    /// </summary>
    public int? PrefixedPadLength { get; init; }

    /// <summary>区切り（ハイフン・空白・下線・ドット）を取り除く（既定 true）。</summary>
    public bool RemoveSeparators { get; init; } = true;

    /// <summary>英字を大文字にそろえる（既定 true）。</summary>
    public bool UpperCase { get; init; } = true;

    public void Validate()
    {
        if (PadLength is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(PadLength), PadLength, "1〜32 で指定してください。");
        }

        if (PrefixedPadLength is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(PrefixedPadLength), PrefixedPadLength, "1〜32 で指定してください。");
        }
    }
}

/// <summary>
/// 社員番号の正規化（全角 → 半角、区切りの除去、英字の大文字化、ゼロ埋め）。取込元の値と jin-ji の値の両方に同じ規則をかけて突き合わせる。
/// </summary>
public sealed class EmployeeNumberNormalizer
{
    public EmployeeNumberNormalizer(EmployeeNumberRules? rules = null)
    {
        Rules = rules ?? new EmployeeNumberRules();
        Rules.Validate();
    }

    public EmployeeNumberRules Rules { get; }

    /// <summary>正規化した社員番号。空なら null。</summary>
    public string? Normalize(string? value)
    {
        var cleaned = TextNormalizer.CleanWhitespace(value);
        if (cleaned is null)
        {
            return null;
        }

        var text = TextNormalizer.Nfkc(cleaned);

        // Excel の数値セル（123.0）
        if (text.EndsWith(".0", StringComparison.Ordinal) && text.Length > 2 && text[..^2].All(char.IsAsciiDigit))
        {
            text = text[..^2];
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (Rules.RemoveSeparators && (char.IsWhiteSpace(c) || c is '-' or '_' or '.' or 'ー' or '‐' or '−'))
            {
                continue;
            }

            builder.Append(Rules.UpperCase ? char.ToUpperInvariant(c) : c);
        }

        var result = builder.ToString();
        if (result.Length == 0)
        {
            return null;
        }

        if (Rules.PadLength is { } length && result.All(char.IsAsciiDigit))
        {
            var trimmed = result.TrimStart('0');
            result = trimmed.PadLeft(Math.Max(length, 1), '0');
        }
        else if (Rules.PrefixedPadLength is { } prefixed)
        {
            var split = 0;
            while (split < result.Length && char.IsAsciiLetter(result[split]))
            {
                split++;
            }

            var digits = result[split..];
            if (split > 0 && digits.Length > 0 && digits.All(char.IsAsciiDigit))
            {
                var trimmed = digits.TrimStart('0');
                result = result[..split] + trimmed.PadLeft(prefixed, '0');
            }
        }

        return result;
    }
}
