using JinJi.Domain.Text;

namespace JinJi.Ingest.Normalization;

/// <summary>
/// 組織名の表記ゆれの吸収（検索用の正規化表記）。「第一営業部」「第１営業部」「第1営業部」、「営業一課」「営業1課」を同じ表記にそろえる。
/// </summary>
/// <remarks>
/// 規則は <see cref="OrgNameKey"/>（参照の組織の検索と共有する。規則を二重に持たない）。
/// 表示用の組織名は <see cref="TextNormalizer.ToDisplay"/> で別に作り、正式表記を保つ。
/// </remarks>
public static class OrgNameNormalizer
{
    /// <summary>組織名の検索用の正規化表記。空なら null。</summary>
    public static string? ToSearchKey(string? value) => OrgNameKey.From(value);

    /// <summary>漢数字（〇一二三四五六七八九十百千）を整数にする。</summary>
    public static int ParseKanjiNumber(string value) => OrgNameKey.ParseKanjiNumber(value);
}
