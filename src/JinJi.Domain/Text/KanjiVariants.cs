using System.Text;

namespace JinJi.Domain.Text;

/// <summary>
/// 異体字の正規化テーブル（検索用の正規化表記にだけ使う。表示用の正式表記は変えない。アーキテクチャ概要 §4.4）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>CJK 互換漢字の多く（U+F900〜）は NFKC で統合漢字になるので、ここでは NFKC の後に残る異体字だけを扱う。
/// ただし U+FA0E〜U+FA29 の一部（﨑 U+FA11 など）は互換漢字の区画にあっても分解を持たず NFKC で残るので、ここに含める</item>
/// <item>氏名・組織名の突合に使う。別人を同一視する誤りより、同一人物を見落とす誤りのほうが名寄せでは安全なので、
/// 一般に別字として使い分けられるもの（龍/竜 など）は含めない</item>
/// <item>足すときは表の左を異体字、右を代表字にし、テーブル駆動のテストに例を足す</item>
/// </list>
/// </remarks>
public static class KanjiVariants
{
    /// <summary>異体字 → 代表字。</summary>
    public static readonly IReadOnlyDictionary<string, string> Table = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["髙"] = "高",
        ["﨑"] = "崎",
        ["嵜"] = "崎",
        ["碕"] = "崎",
        ["齋"] = "斉",
        ["齊"] = "斉",
        ["斎"] = "斉",
        ["邊"] = "辺",
        ["邉"] = "辺",
        ["澤"] = "沢",
        ["濱"] = "浜",
        ["濵"] = "浜",
        ["廣"] = "広",
        ["國"] = "国",
        ["嶋"] = "島",
        ["嶌"] = "島",
        ["德"] = "徳",
        ["槗"] = "橋",
        ["惠"] = "恵",
        ["眞"] = "真",
        ["冨"] = "富",
        ["曻"] = "昇",
        ["瀨"] = "瀬",
        ["槇"] = "槙",
        ["栁"] = "柳",
        ["藪"] = "薮",
        ["舘"] = "館",
        ["櫻"] = "桜",
        ["薗"] = "園",
        ["條"] = "条",
        ["實"] = "実",
        ["壽"] = "寿",
        ["藏"] = "蔵",
        ["淺"] = "浅",
        ["龜"] = "亀",
        ["靜"] = "静",
        ["彌"] = "弥",
        ["𠮷"] = "吉",
    };

    /// <summary>異体字を代表字に置き換える（文字単位。サロゲートペアの文字も扱う）。</summary>
    public static string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            var text = rune.ToString();
            builder.Append(Table.TryGetValue(text, out var replacement) ? replacement : text);
        }

        return builder.ToString();
    }
}
