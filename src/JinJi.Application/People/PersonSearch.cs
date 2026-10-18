using System.Globalization;
using JinJi.Domain.Text;

namespace JinJi.Application.People;

/// <summary>所属の種類（検索の条件・件数の区分）。組織ごとの集計（<c>GET /orgs/stats</c>）と同じく、所属ごとに 1 つに決める。</summary>
public enum AssignmentKind
{
    /// <summary>出向でない主務。</summary>
    Primary,

    /// <summary>出向でない兼務。</summary>
    Concurrent,

    /// <summary>出向（出向・出向受入の区別がある所属。主務・兼務とも）。</summary>
    Secondment,
}

/// <summary>検索の結果の並び順。</summary>
public enum PersonSort
{
    /// <summary>jin-ji ID の順（既定。条件が変わらなければ順序が安定する）。</summary>
    Id,

    /// <summary>フリガナの順（見えない人は最後）。</summary>
    Kana,

    /// <summary>主務の組織の順（組織図の順。所属が見えない人は最後）、同じ組織の中はフリガナの順。</summary>
    Org,

    /// <summary>入社年月日の古い順（見えない人は最後）。</summary>
    HireDate,

    /// <summary>入社年月日の新しい順（見えない人は最後）。</summary>
    HireDateDesc,

    /// <summary>拠点の順（法人のコード・拠点コード。拠点が見えない・無い人は最後）、同じ拠点の中はフリガナの順。拠点ごとのまとまりで見せるときに使う。</summary>
    Site,

    /// <summary>リモートワークの対象者が先（見えない人は最後）、同じ区分の中はフリガナの順。</summary>
    RemoteWork,
}

/// <summary>検索の条件・並び順のコード値（REST・MCP の引数と応答の値）。</summary>
public static class PersonSearchCodes
{
    public static readonly IReadOnlyList<string> AssignmentKinds = ["primary", "concurrent", "secondment"];

    public static readonly IReadOnlyList<string> Sorts = ["id", "kana", "org", "hire_date", "hire_date_desc", "site", "remote_work"];

    /// <summary>拠点の種別（<see cref="Domain.Organization.SiteCodes.Types"/>）。</summary>
    public static IReadOnlyList<string> SiteTypes => Domain.Organization.SiteCodes.Types;

    /// <summary>都道府県のコード（JIS X 0401）。</summary>
    public static readonly IReadOnlyList<string> Prefectures = [.. Domain.Organization.SiteCodes.Prefectures.Select(p => p.Code)];

    /// <summary>リモートワークの有無（件数の値）。</summary>
    public static readonly IReadOnlyList<string> RemoteWorkValues = ["true", "false"];

    /// <summary>在籍状態（<see cref="JinJi.Policy.PersonView.EmploymentStatus"/> の値）。</summary>
    public static readonly IReadOnlyList<string> EmploymentStatuses = ["active", "terminated"];

    /// <summary>アカウント状態（<see cref="JinJi.Policy.PersonView.AccountStatus"/> の値）。</summary>
    public static readonly IReadOnlyList<string> AccountStatuses = ["active", "suspended"];

    public static string ToCode(AssignmentKind kind) => kind switch
    {
        AssignmentKind.Primary => "primary",
        AssignmentKind.Concurrent => "concurrent",
        AssignmentKind.Secondment => "secondment",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryParseAssignmentKind(string? code, out AssignmentKind kind)
    {
        (var ok, kind) = code switch
        {
            "primary" => (true, AssignmentKind.Primary),
            "concurrent" => (true, AssignmentKind.Concurrent),
            "secondment" => (true, AssignmentKind.Secondment),
            _ => (false, AssignmentKind.Primary),
        };
        return ok;
    }

    public static string ToCode(PersonSort sort) => sort switch
    {
        PersonSort.Id => "id",
        PersonSort.Kana => "kana",
        PersonSort.Org => "org",
        PersonSort.HireDate => "hire_date",
        PersonSort.HireDateDesc => "hire_date_desc",
        PersonSort.Site => "site",
        PersonSort.RemoteWork => "remote_work",
        _ => throw new ArgumentOutOfRangeException(nameof(sort)),
    };

    public static bool TryParseSort(string? code, out PersonSort sort)
    {
        (var ok, sort) = code switch
        {
            "id" => (true, PersonSort.Id),
            "kana" => (true, PersonSort.Kana),
            "org" => (true, PersonSort.Org),
            "hire_date" => (true, PersonSort.HireDate),
            "hire_date_desc" => (true, PersonSort.HireDateDesc),
            "site" => (true, PersonSort.Site),
            "remote_work" => (true, PersonSort.RemoteWork),
            _ => (false, PersonSort.Id),
        };
        return ok;
    }
}

/// <summary>人の検索の条件。</summary>
/// <remarks>
/// 条件はすべて AND。同じ条件の中の複数の値は OR。所属の条件（組織・段・所属の種類・役職）は同じ 1 つの所属で満たす必要がある
/// （「営業部に兼務」は営業部への兼務の所属がある人で、主務が営業部で別の組織に兼務の人ではない）。
/// 照合・絞り込みに使うのは閲覧者に見える項目（投影した後の値）だけ（<see cref="PersonDirectoryService.SearchPeopleAsync"/>）。
/// </remarks>
public sealed record PersonSearch
{
    /// <summary>1 ページの最大件数。</summary>
    public const int MaxLimit = 100;

    /// <summary>検索語の最大の長さ（文字数）。</summary>
    public const int MaxQueryLength = 100;

    /// <summary>検索語の正規化表記の最小の長さ（短すぎる語で全員に当てさせない）。</summary>
    public const int MinQueryKeyLength = 2;

    /// <summary>1 つの条件に指定できる値の数。</summary>
    public const int MaxValuesPerFilter = 20;

    /// <summary>文字列の条件の値の最大の長さ（文字数）。</summary>
    public const int MaxValueLength = 100;

    /// <summary>受け付けないワイルドカードの文字（検索は部分一致で、ワイルドカードの構文を持たない）。</summary>
    public static readonly IReadOnlySet<char> WildcardCharacters = new HashSet<char> { '*', '%', '?', '＊', '％', '？' };

    /// <summary>
    /// 検索語（氏名・フリガナ・ローマ字・社員番号・社用メールの部分一致）。null なら絞り込まない。
    /// 読み（フリガナ・ローマ字）は <see cref="ReadingKey"/> でも照合する（ひらがな・カタカナ・ローマ字の綴りの揺れ・長音の有無を同一視）。
    /// </summary>
    public string? Query { get; init; }

    /// <summary>所属組織での絞り込み。</summary>
    public Guid? OrgUnitId { get; init; }

    /// <summary>組織の配下の組織の所属者も含めるか。</summary>
    public bool IncludeDescendants { get; init; }

    /// <summary>雇用元の法人（項目 legal_entity）。</summary>
    public IReadOnlySet<Guid> LegalEntityIds { get; init; } = new HashSet<Guid>();

    /// <summary>所属組織の段のキー（その段の組織への所属がある人）。</summary>
    public IReadOnlySet<string> LevelKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>所属の種類。</summary>
    public IReadOnlySet<AssignmentKind> AssignmentKinds { get; init; } = new HashSet<AssignmentKind>();

    /// <summary>役職（所属の役職名。正規化表記の完全一致）。</summary>
    public IReadOnlySet<string> Positions { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>雇用区分（コード値。正規化表記の完全一致）。</summary>
    public IReadOnlySet<string> EmploymentTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>職種（正規化表記の完全一致）。</summary>
    public IReadOnlySet<string> JobTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>拠点（拠点の ID。項目 site）。</summary>
    public IReadOnlySet<Guid> SiteIds { get; init; } = new HashSet<Guid>();

    /// <summary>拠点の種別（コード値。項目 site の拠点の種別）。</summary>
    public IReadOnlySet<string> SiteTypes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>拠点の所在の都道府県（コード値。項目 site の拠点の都道府県）。</summary>
    public IReadOnlySet<string> Prefectures { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>リモートワークの対象か（null は絞り込まない。項目 remote_work）。</summary>
    public bool? RemoteWork { get; init; }

    /// <summary>入社年月日の範囲の開始（この日を含む）。</summary>
    public DateOnly? HiredFrom { get; init; }

    /// <summary>入社年月日の範囲の終了（この日を含む）。</summary>
    public DateOnly? HiredTo { get; init; }

    /// <summary>在籍状態（active / terminated）。</summary>
    public IReadOnlySet<string> EmploymentStatuses { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>アカウント状態（active / suspended）。</summary>
    public IReadOnlySet<string> AccountStatuses { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>並び順。</summary>
    public PersonSort Sort { get; init; } = PersonSort.Id;

    /// <summary>
    /// 一致した人数と、条件ごとの件数（<see cref="PersonFacets"/>）も求めるか。最初のページ（<see cref="After"/> なし）でだけ指定できる
    /// （一致した人がいれば必ず 1 人以上を返すので、返却人数の制限を受けずに件数だけを繰り返し取ることはできない）。
    /// </summary>
    public bool IncludeFacets { get; init; }

    /// <summary>この jin-ji ID の人より後から返す（ページの続き。並び順の位置はこの人の今の値で決める）。</summary>
    public Guid? After { get; init; }

    /// <summary>1 ページの件数（1〜<see cref="MaxLimit"/>）。</summary>
    public int Limit { get; init; } = 50;

    /// <summary>所属の条件（同じ 1 つの所属で満たす条件）があるか。</summary>
    public bool HasAssignmentCriteria => OrgUnitId is not null || LevelKeys.Count > 0 || AssignmentKinds.Count > 0 || Positions.Count > 0;

    /// <summary>絞り込みの無い一覧（全社横断の取得）か。不正検知の「絞り込みの無い一覧」に数える。</summary>
    public bool IsBroad =>
        Query is null && !HasAssignmentCriteria && LegalEntityIds.Count == 0 && EmploymentTypes.Count == 0 && JobTypes.Count == 0
        && SiteIds.Count == 0 && SiteTypes.Count == 0 && Prefectures.Count == 0 && RemoteWork is null && HiredFrom is null && HiredTo is null && EmploymentStatuses.Count == 0 && AccountStatuses.Count == 0;

    /// <summary>条件を検査し、検索語の正規化表記を返す（検索語が無ければ null）。不正なら <see cref="ArgumentException"/>。</summary>
    public string? Validate()
    {
        if (Limit is < 1 or > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), Limit, $"件数は 1〜{MaxLimit} で指定してください。");
        }

        if (IncludeFacets && After is not null)
        {
            throw new ArgumentException("件数（facets）は最初のページ（cursor なし）でだけ求められます。", nameof(IncludeFacets));
        }

        if (HiredFrom is { } from && HiredTo is { } to && from > to)
        {
            throw new ArgumentException("入社年月日の範囲は、開始を終了以前にしてください。", nameof(HiredFrom));
        }

        CheckCount(nameof(LegalEntityIds), LegalEntityIds.Count);
        CheckCount(nameof(AssignmentKinds), AssignmentKinds.Count);
        CheckTexts("level", LevelKeys, 63);
        CheckTexts("position", Positions, MaxValueLength);
        CheckTexts("employment_type", EmploymentTypes, MaxValueLength);
        CheckTexts("job_type", JobTypes, MaxValueLength);
        CheckCount("site", SiteIds.Count);
        CheckChoices("site_type", SiteTypes, PersonSearchCodes.SiteTypes);
        CheckChoices("prefecture", Prefectures, PersonSearchCodes.Prefectures);
        CheckChoices("employment_status", EmploymentStatuses, PersonSearchCodes.EmploymentStatuses);
        CheckChoices("account_status", AccountStatuses, PersonSearchCodes.AccountStatuses);

        if (Query is null)
        {
            return null;
        }

        if (Query.Length > MaxQueryLength)
        {
            throw new ArgumentException($"検索語は {MaxQueryLength} 文字以内で指定してください。", nameof(Query));
        }

        if (Query.Any(WildcardCharacters.Contains))
        {
            throw new ArgumentException("検索語にワイルドカード（* % ?）は使えません（検索は部分一致です）。", nameof(Query));
        }

        var key = SearchKey.From(Query);
        if (key is null || key.Length < MinQueryKeyLength)
        {
            throw new ArgumentException($"検索語は正規化した表記で {MinQueryKeyLength} 文字以上にしてください。", nameof(Query));
        }

        return key;
    }

    /// <summary>
    /// 条件と並び順を表す文字列（カーソルの用途に含め、別の条件のカーソルを流用させない）。ページの位置・件数・facets は含めない。
    /// </summary>
    public string Fingerprint() =>
        string.Join(
            '\n',
            Query ?? "\u0000",
            OrgUnitId?.ToString("D") ?? "\u0000",
            IncludeDescendants ? "1" : "0",
            Join(LegalEntityIds.Select(id => id.ToString("D"))),
            Join(LevelKeys),
            Join(AssignmentKinds.Select(PersonSearchCodes.ToCode)),
            Join(Positions),
            Join(EmploymentTypes),
            Join(JobTypes),
            Join(SiteIds.Select(id => id.ToString("D"))),
            Join(SiteTypes),
            Join(Prefectures),
            RemoteWork switch { true => "1", false => "0", null => "\u0000" },
            HiredFrom?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "\u0000",
            HiredTo?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "\u0000",
            Join(EmploymentStatuses),
            Join(AccountStatuses),
            PersonSearchCodes.ToCode(Sort));

    private static string Join(IEnumerable<string> values) => string.Join('\u001f', values.Order(StringComparer.Ordinal));

    private static void CheckCount(string name, int count)
    {
        if (count > MaxValuesPerFilter)
        {
            throw new ArgumentException($"{name} は {MaxValuesPerFilter} 件までです。", name);
        }
    }

    private static void CheckTexts(string name, IReadOnlySet<string> values, int maxLength)
    {
        CheckCount(name, values.Count);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            {
                throw new ArgumentException($"{name} の値は空でない {maxLength} 文字以内の文字列で指定してください（制御文字は使えません）。", name);
            }
        }
    }

    private static void CheckChoices(string name, IReadOnlySet<string> values, IReadOnlyList<string> choices)
    {
        CheckCount(name, values.Count);
        if (values.FirstOrDefault(v => !choices.Contains(v, StringComparer.Ordinal)) is { } bad)
        {
            throw new ArgumentException($"{name} は {string.Join(" / ", choices)} で指定してください（{bad} は使えません）。", name);
        }
    }
}

/// <summary>人の一覧の 1 ページ。</summary>
/// <param name="NextAfter">続きがあれば、次のページの <see cref="PersonSearch.After"/> に渡す値。</param>
/// <param name="Total">条件に一致した人数（<see cref="PersonSearch.IncludeFacets"/> のときだけ）。</param>
/// <param name="Facets">条件ごとの件数（<see cref="PersonSearch.IncludeFacets"/> のときだけ）。</param>
public sealed record PersonPage(IReadOnlyList<Policy.PersonView> Items, Guid? NextAfter, int? Total = null, PersonFacets? Facets = null);

/// <summary>
/// 条件ごとの件数（絞り込みの候補と、その値を選んだときの人数）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>数えるのは、閲覧者に結果として返し得る人（投影でき、検索の条件に一致する人）だけ。値は投影した後の値で、
/// その項目が見えない人はその条件の件数に数えない（「非公開」の区分も作らない）。件数から見えない値・見えない人を推測させないため</item>
/// <item>ある条件の件数は、その条件以外のすべての条件に一致する人で数える（同じ条件の中の値は OR なので、選んだ値以外の候補の件数も出す）。
/// 所属の条件（段・所属の種類・役職）は同じ 1 つの所属で満たすものとして数える</item>
/// <item>件数の多い順（同数は名前の順）。役職・雇用区分・職種・拠点は上位 <see cref="TextLimit"/> 件まで。段は上位の段から（法人によって順位が違う段は、最も下の順位で並べる）。
/// 所属の種類・状態・拠点の種別・都道府県・リモートワークは決まった順。拠点の種別・都道府県は項目 site の拠点の値（拠点が見えない人は数えない）</item>
/// </list>
/// </remarks>
public sealed record PersonFacets(
    IReadOnlyList<FacetValue> LegalEntities,
    IReadOnlyList<FacetValue> Levels,
    IReadOnlyList<FacetValue> AssignmentKinds,
    IReadOnlyList<FacetValue> Positions,
    IReadOnlyList<FacetValue> EmploymentTypes,
    IReadOnlyList<FacetValue> JobTypes,
    IReadOnlyList<FacetValue> Sites,
    IReadOnlyList<FacetValue> SiteTypes,
    IReadOnlyList<FacetValue> Prefectures,
    IReadOnlyList<FacetValue> RemoteWork,
    IReadOnlyList<FacetValue> EmploymentStatuses,
    IReadOnlyList<FacetValue> AccountStatuses)
{
    /// <summary>文字列の値の条件（役職・雇用区分・職種）と拠点で返す候補の最大数。</summary>
    public const int TextLimit = 50;
}

/// <summary>条件の値と人数。</summary>
/// <param name="Value">条件に指定する値（法人・拠点は ID、段はキー、それ以外は値そのもの）。</param>
/// <param name="Label">表示名（法人名・拠点名・段の名前・都道府県名。それ以外は値と同じ）。</param>
public sealed record FacetValue(string Value, string Label, int Count);
