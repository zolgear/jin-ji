using JinJi.Domain.Text;

namespace JinJi.Domain.Organization;

/// <summary>拠点の種別・都道府県のコード値と表示名（DB・API・取込で共通）。</summary>
public static class SiteCodes
{
    /// <summary>種別のコード値（DB の site_version.site_type と同じ。API の値もこれ）。並びは表示の順。</summary>
    public static readonly IReadOnlyList<string> Types =
        ["head_office", "branch", "sales_office", "factory", "laboratory", "logistics", "store", "other"];

    /// <summary>都道府県（JIS X 0401 のコード順）。</summary>
    public static readonly IReadOnlyList<(string Code, string Name)> Prefectures =
    [
        ("01", "北海道"), ("02", "青森県"), ("03", "岩手県"), ("04", "宮城県"), ("05", "秋田県"), ("06", "山形県"), ("07", "福島県"),
        ("08", "茨城県"), ("09", "栃木県"), ("10", "群馬県"), ("11", "埼玉県"), ("12", "千葉県"), ("13", "東京都"), ("14", "神奈川県"),
        ("15", "新潟県"), ("16", "富山県"), ("17", "石川県"), ("18", "福井県"), ("19", "山梨県"), ("20", "長野県"), ("21", "岐阜県"),
        ("22", "静岡県"), ("23", "愛知県"), ("24", "三重県"), ("25", "滋賀県"), ("26", "京都府"), ("27", "大阪府"), ("28", "兵庫県"),
        ("29", "奈良県"), ("30", "和歌山県"), ("31", "鳥取県"), ("32", "島根県"), ("33", "岡山県"), ("34", "広島県"), ("35", "山口県"),
        ("36", "徳島県"), ("37", "香川県"), ("38", "愛媛県"), ("39", "高知県"), ("40", "福岡県"), ("41", "佐賀県"), ("42", "長崎県"),
        ("43", "熊本県"), ("44", "大分県"), ("45", "宮崎県"), ("46", "鹿児島県"), ("47", "沖縄県"),
    ];

    private static readonly Dictionary<string, string> PrefectureNames = Prefectures.ToDictionary(p => p.Code, p => p.Name, StringComparer.Ordinal);

    /// <summary>種別の表示名（取込の表記揺れの照合にも使う。最初が代表の表示名）。</summary>
    private static readonly IReadOnlyList<(SiteType Type, string[] Labels)> TypeLabels =
    [
        (SiteType.HeadOffice, ["本社", "本店", "本部事業所"]),
        (SiteType.Branch, ["支社", "支店", "事業所", "分室"]),
        (SiteType.SalesOffice, ["営業所", "出張所"]),
        (SiteType.Factory, ["工場", "製作所", "製造所"]),
        (SiteType.Laboratory, ["研究所", "研究センター", "開発センター", "ラボ"]),
        (SiteType.Logistics, ["物流拠点", "物流センター", "倉庫", "配送センター"]),
        (SiteType.Store, ["店舗", "店", "ショップ"]),
        (SiteType.Other, ["その他"]),
    ];

    public static string ToCode(SiteType type) => type switch
    {
        SiteType.HeadOffice => "head_office",
        SiteType.Branch => "branch",
        SiteType.SalesOffice => "sales_office",
        SiteType.Factory => "factory",
        SiteType.Laboratory => "laboratory",
        SiteType.Logistics => "logistics",
        SiteType.Store => "store",
        SiteType.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool TryParse(string? code, out SiteType type)
    {
        for (var i = 0; i < Types.Count; i++)
        {
            if (string.Equals(Types[i], code, StringComparison.Ordinal))
            {
                type = (SiteType)i;
                return true;
            }
        }

        type = SiteType.Other;
        return false;
    }

    /// <summary>種別の表示名（本社・支社/支店 など）。</summary>
    public static string Label(SiteType type) => type switch
    {
        SiteType.Branch => "支社・支店",
        SiteType.Logistics => "物流拠点",
        _ => TypeLabels.First(t => t.Type == type).Labels[0],
    };

    /// <summary>
    /// 取込の値（コード値・表示名・拠点名の末尾）から種別を推定する。コード値か表示名に一致すればその種別、
    /// 拠点名の末尾が表示名で終われば（「大阪支店」→ 支社・支店）その種別。当てはまらなければ false。
    /// </summary>
    public static bool TryInfer(string? value, out SiteType type)
    {
        type = SiteType.Other;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (TryParse(trimmed.ToLowerInvariant(), out type))
        {
            return true;
        }

        var key = SearchKey.From(trimmed);
        if (key is null)
        {
            return false;
        }

        // 完全一致を先に、次に末尾の一致（長い表示名を優先。「研究センター」を「センター」より先に）
        foreach (var (candidate, labels) in TypeLabels)
        {
            if (labels.Any(l => SearchKey.From(l) == key))
            {
                type = candidate;
                return true;
            }
        }

        var best = TypeLabels
            .SelectMany(t => t.Labels.Select(l => (t.Type, Key: SearchKey.From(l)!)))
            .Where(l => key.EndsWith(l.Key, StringComparison.Ordinal))
            .OrderByDescending(l => l.Key.Length)
            .FirstOrDefault();
        if (best.Key is null)
        {
            return false;
        }

        type = best.Type;
        return true;
    }

    /// <summary>都道府県のコードか。</summary>
    public static bool IsPrefectureCode(string? code) => code is not null && PrefectureNames.ContainsKey(code);

    /// <summary>都道府県の名前（コードが無効なら null）。</summary>
    public static string? PrefectureName(string? code) => code is not null && PrefectureNames.TryGetValue(code, out var name) ? name : null;

    /// <summary>
    /// 取込の値から都道府県のコードを求める（コード「13」・「東京都」・「東京」・所在地の先頭「東京都千代田区…」）。当てはまらなければ null。
    /// </summary>
    public static string? ParsePrefecture(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Normalize(System.Text.NormalizationForm.FormKC);
        if (trimmed.Length is 1 or 2 && int.TryParse(trimmed, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 47)
        {
            return n.ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var (code, name) in Prefectures)
        {
            if (trimmed.StartsWith(name, StringComparison.Ordinal))
            {
                return code;
            }
        }

        // 「都・府・県」を省いた表記（「東京」「大阪」「神奈川」）。北海道はそのまま
        foreach (var (code, name) in Prefectures)
        {
            var bare = name.Length > 2 && name[^1] is '都' or '府' or '県' ? name[..^1] : name;
            if (trimmed == bare)
            {
                return code;
            }
        }

        return null;
    }
}
