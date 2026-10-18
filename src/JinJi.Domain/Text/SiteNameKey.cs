using System.Text;
using System.Text.RegularExpressions;

namespace JinJi.Domain.Text;

/// <summary>
/// 拠点名の照合用の正規化表記（取込の拠点名・勤務地を拠点マスタに当てるときに使う）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>組織名の規則（<see cref="OrgNameKey"/>: NFKC・異体字・空白の除去・「第一」と「1」）に加え、括弧・中黒・ハイフン・斜線を除く
/// （「東京 本社」「東京・本社」「ﾖｺﾊﾏ工場」を同じ表記にする）</item>
/// <item>「本社（東京）」のように括弧で場所を後ろに書いた表記は、括弧の中を前に出した「東京本社」も、「大阪支社（梅田）」のような補足は括弧の外の「大阪支社」も候補にする（<see cref="Candidates"/>）</item>
/// <item>「支社」と「支店」、「物流センター」と「倉庫」のように語が違うものは同一視しない（別の拠点のことがあるため。当たらなければ修正依頼）</item>
/// </list>
/// </remarks>
public static partial class SiteNameKey
{
    /// <summary>拠点名の照合用の正規化表記。空なら null。</summary>
    public static string? From(string? value)
    {
        if (OrgNameKey.From(value) is not { } key)
        {
            return null;
        }

        var builder = new StringBuilder(key.Length);
        foreach (var c in key)
        {
            if (!IsSeparator(c))
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>照合の候補（正規化表記、「本社（東京）」→「東京本社」の並べ替え、括弧の外だけ）。この順に試す。重複は除く。</summary>
    public static IReadOnlyList<string> Candidates(string? value)
    {
        var candidates = new List<string>();
        if (From(value) is { } key)
        {
            candidates.Add(key);
        }

        if (value is not null && Parenthesized().Match(value.Normalize(NormalizationForm.FormKC).Trim()) is { Success: true } m)
        {
            foreach (var alternative in new[] { m.Groups["inner"].Value + m.Groups["outer"].Value, m.Groups["outer"].Value })
            {
                if (From(alternative) is { } key2 && !candidates.Contains(key2))
                {
                    candidates.Add(key2);
                }
            }
        }

        return candidates;
    }

    // 長音（ー）は「センター」のように語の一部なので除かない
    private static bool IsSeparator(char c) => c is '(' or ')' or '[' or ']' or '「' or '」' or '【' or '】' or '・' or '･' or '-' or '‐' or '/' or '_' or '.';

    [GeneratedRegex(@"^(?<outer>[^()]+)\((?<inner>[^()]+)\)$")]
    private static partial Regex Parenthesized();
}
