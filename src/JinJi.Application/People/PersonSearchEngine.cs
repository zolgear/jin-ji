using System.Globalization;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Policy;

namespace JinJi.Application.People;

/// <summary>
/// 人の検索の本体（照合・絞り込み・件数・並べ替え・ページ）。<see cref="PersonDirectoryService.SearchPeopleAsync"/> だけが使う。
/// </summary>
/// <remarks>
/// 照合・絞り込み・件数・並べ替えに使うのは、すべて <see cref="PersonProjector"/> で投影した後の値（閲覧者に見える項目だけ）。
/// 投影できない人（公開範囲の外・退職後の公開期間を過ぎた人・内示）は、どの件数にも数えない。
/// </remarks>
internal sealed class PersonSearchEngine
{
    private readonly OrgSnapshot _data;
    private readonly PersonProjector _projector;
    private readonly ViewerContext _viewer;
    private readonly PersonSearch _search;
    private readonly string? _queryKey;
    private readonly string? _queryReading;
    private readonly HashSet<string> _levels;
    private readonly HashSet<string> _positions;
    private readonly HashSet<string> _employmentTypes;
    private readonly HashSet<string> _jobTypes;
    private Dictionary<Guid, int>? _orgOrder;

    public PersonSearchEngine(OrgSnapshot data, PersonProjector projector, ViewerContext viewer, PersonSearch search, string? queryKey)
    {
        _data = data;
        _projector = projector;
        _viewer = viewer;
        _search = search;
        _queryKey = queryKey;
        // 読みでの照合は、語が読み（かな・ローマ字）だけでできていて十分に長いときだけ（短い読みは多くの人に当たるため）
        _queryReading = search.Query is { } q && ReadingKey.From(q) is { Length: >= ReadingKey.MinMatchLength } reading ? reading : null;
        _levels = [.. search.LevelKeys];
        _positions = Keys(search.Positions);
        _employmentTypes = Keys(search.EmploymentTypes);
        _jobTypes = Keys(search.JobTypes);
    }

    /// <summary>人ごとに判定する条件（件数を求める条件の単位）。</summary>
    private enum Facet
    {
        LegalEntity,
        EmploymentType,
        JobType,
        Site,
        SiteType,
        Prefecture,
        RemoteWork,
        EmploymentStatus,
        AccountStatus,
    }

    public PersonPage Run()
    {
        // ID の順で件数も要らなければ、ページの分が揃った所で打ち切る（従来の検索と同じ動き）
        if (_search.Sort == PersonSort.Id && !_search.IncludeFacets)
        {
            var matches = _data.People
                .Select(p => p.Id)
                .Where(id => _search.After is not { } after || id.CompareTo(after) > 0)
                .Order()
                .Select(id => _projector.Project(_viewer, id))
                .OfType<PersonView>()
                .Where(v => Evaluate(v, null))
                .Take(_search.Limit + 1)
                .ToList();
            return Page(matches);
        }

        var counter = _search.IncludeFacets ? new FacetCounter(_data) : null;
        var matched = new List<(PersonView View, SortKey Key)>();
        foreach (var person in _data.People)
        {
            if (_projector.Project(_viewer, person.Id) is { } view && Evaluate(view, counter))
            {
                matched.Add((view, KeyOf(view)));
            }
        }

        matched.Sort((x, y) => x.Key.CompareTo(y.Key));
        IEnumerable<(PersonView View, SortKey Key)> rest = matched;
        if (_search.After is { } afterId)
        {
            // 続きの位置は、前のページの最後の人の今の値で決める（カーソルに値を入れないため）
            var anchor = _projector.Project(_viewer, afterId) ?? throw new StaleCursorException();
            var afterKey = KeyOf(anchor);
            rest = matched.Where(m => m.Key.CompareTo(afterKey) > 0);
        }

        var page = Page([.. rest.Take(_search.Limit + 1).Select(m => m.View)]);
        return counter is null ? page : page with { Total = matched.Count, Facets = counter.Build() };
    }

    private PersonPage Page(List<PersonView> matches)
    {
        var hasMore = matches.Count > _search.Limit;
        var items = matches.Take(_search.Limit).ToList();
        return new PersonPage(items, hasMore ? items[^1].PersonId : null);
    }

    /// <summary>
    /// 条件にすべて一致するかを返す。件数を求めるときは、件数の規則（<see cref="PersonFacets"/>）に従って <paramref name="counter"/> に数える。
    /// </summary>
    private bool Evaluate(PersonView view, FacetCounter? counter)
    {
        // 件数を持たない条件（検索語・組織・入社年月日）は、一致しなければどの件数にも数えない
        if (_queryKey is not null && !MatchesQuery(view))
        {
            return false;
        }

        if ((_search.HiredFrom is not null || _search.HiredTo is not null)
            && (view.HireDate is not { } hired || (_search.HiredFrom is { } from && hired < from) || (_search.HiredTo is { } to && hired > to)))
        {
            return false;
        }

        Facet? failed = null;
        var failures = 0;
        foreach (var facet in Enum.GetValues<Facet>())
        {
            if (!PassesPersonFacet(view, facet))
            {
                failed = facet;
                failures++;
            }
        }

        // 所属の条件は同じ 1 つの所属で満たす。所属の項目が見えない人は、所属の条件があれば一致しない
        var assignments = view.Assignments ?? [];
        var assignmentsMatch = !_search.HasAssignmentCriteria || assignments.Any(a => AssignmentMatches(a, null));
        var matches = failures == 0 && assignmentsMatch;
        if (counter is null)
        {
            return matches;
        }

        if (assignmentsMatch)
        {
            if (failures == 0)
            {
                foreach (var facet in Enum.GetValues<Facet>())
                {
                    counter.AddPerson(facet, view);
                }
            }
            else if (failures == 1)
            {
                // その条件だけが外れた人は、その条件の他の値の件数に数える（同じ条件の中は OR のため）
                counter.AddPerson(failed!.Value, view);
            }
        }

        if (failures == 0 && view.Assignments is not null)
        {
            counter.AddAssignments(assignments.Where(a => AssignmentMatches(a, AssignmentPart.Level)), a => LevelOf(a), AssignmentPart.Level);
            counter.AddAssignments(assignments.Where(a => AssignmentMatches(a, AssignmentPart.Kind)), a => PersonSearchCodes.ToCode(KindOf(a)), AssignmentPart.Kind);
            counter.AddAssignments(assignments.Where(a => AssignmentMatches(a, AssignmentPart.Position)), a => a.PositionTitle, AssignmentPart.Position);
        }

        return matches;
    }

    /// <summary>投影した値（見える項目だけ）が検索語に当たるか。</summary>
    private bool MatchesQuery(PersonView view)
    {
        foreach (var value in new[]
                 {
                     view.FamilyName + view.GivenName, view.DisplayName, view.FamilyNameKana + view.GivenNameKana,
                     view.FamilyNameLatin + view.GivenNameLatin, view.GivenNameLatin + view.FamilyNameLatin,
                     view.EmployeeNumber, view.WorkEmail,
                 })
        {
            if (SearchKey.From(value) is { } candidate && candidate.Contains(_queryKey!, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (_queryReading is null)
        {
            return false;
        }

        foreach (var value in new[] { view.FamilyNameKana + view.GivenNameKana, view.FamilyNameLatin + view.GivenNameLatin, view.GivenNameLatin + view.FamilyNameLatin })
        {
            if (ReadingKey.From(value) is { } candidate && candidate.Contains(_queryReading, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private bool PassesPersonFacet(PersonView view, Facet facet) => facet switch
    {
        Facet.LegalEntity => _search.LegalEntityIds.Count == 0 || (view.LegalEntity is { } e && _search.LegalEntityIds.Contains(e.Id)),
        Facet.EmploymentType => In(_employmentTypes, view.EmploymentType),
        Facet.JobType => In(_jobTypes, view.JobType),
        Facet.Site => _search.SiteIds.Count == 0 || (view.Site is { } site && _search.SiteIds.Contains(site.Id)),
        Facet.SiteType => _search.SiteTypes.Count == 0 || (view.Site is { } site && _search.SiteTypes.Contains(SiteCodes.ToCode(site.Type))),
        Facet.Prefecture => _search.Prefectures.Count == 0 || (view.Site?.PrefectureCode is { } prefecture && _search.Prefectures.Contains(prefecture)),
        Facet.RemoteWork => _search.RemoteWork is not { } remote || view.RemoteWork == remote,
        Facet.EmploymentStatus => _search.EmploymentStatuses.Count == 0 || (view.EmploymentStatus is { } s && _search.EmploymentStatuses.Contains(s)),
        Facet.AccountStatus => _search.AccountStatuses.Count == 0 || (view.AccountStatus is { } s && _search.AccountStatuses.Contains(s)),
        _ => throw new ArgumentOutOfRangeException(nameof(facet)),
    };

    private static bool In(HashSet<string> keys, string? value) => keys.Count == 0 || (Key(value) is { } key && keys.Contains(key));

    /// <summary>所属の条件の部分（件数を求めるときに、その部分を外して判定する）。</summary>
    private enum AssignmentPart
    {
        Level,
        Kind,
        Position,
    }

    /// <summary>所属が所属の条件に一致するか。<paramref name="ignore"/> の部分は判定しない（その部分の件数を求めるため）。</summary>
    private bool AssignmentMatches(AssignmentView a, AssignmentPart? ignore)
    {
        if (_search.OrgUnitId is { } unit && a.OrgUnitId != unit && !(_search.IncludeDescendants && _data.IsSelfOrDescendantOf(a.OrgUnitId, unit)))
        {
            return false;
        }

        if (ignore != AssignmentPart.Level && _levels.Count > 0 && (LevelOf(a) is not { } level || !_levels.Contains(level)))
        {
            return false;
        }

        if (ignore != AssignmentPart.Kind && _search.AssignmentKinds.Count > 0 && !_search.AssignmentKinds.Contains(KindOf(a)))
        {
            return false;
        }

        return ignore == AssignmentPart.Position || In(_positions, a.PositionTitle);
    }

    /// <summary>所属組織の段のキー（法人の定義にある段だけ。段を持たない組織は null）。</summary>
    private string? LevelOf(AssignmentView a) =>
        _data.FindUnit(a.OrgUnitId) is { LevelKey: { } key } unit && _data.LevelSchemeOf(unit.LegalEntityId).Find(key) is not null ? key : null;

    private static AssignmentKind KindOf(AssignmentView a) =>
        a.Secondment != SecondmentType.None ? AssignmentKind.Secondment
        : a.Type == AssignmentType.Primary ? AssignmentKind.Primary
        : AssignmentKind.Concurrent;

    /// <summary>並び順の鍵。見えない値は最後（<see cref="Last"/>）に置き、同じ値は jin-ji ID の順。</summary>
    private SortKey KeyOf(PersonView view)
    {
        var kana = SearchKey.From(view.FamilyNameKana + view.GivenNameKana) ?? Last;
        return _search.Sort switch
        {
            PersonSort.Kana => new SortKey(kana, string.Empty, view.PersonId),
            PersonSort.Org => new SortKey(OrgPosition(view), kana, view.PersonId),
            PersonSort.HireDate => new SortKey(view.HireDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? Last, kana, view.PersonId),
            PersonSort.Site => new SortKey(SitePosition(view), kana, view.PersonId),
            PersonSort.RemoteWork => new SortKey(view.RemoteWork switch { true => "0", false => "1", null => Last }, kana, view.PersonId),
            PersonSort.HireDateDesc => new SortKey(
                view.HireDate is { } d ? (99999999 - ((d.Year * 10000) + (d.Month * 100) + d.Day)).ToString("D8", CultureInfo.InvariantCulture) : Last,
                kana,
                view.PersonId),
            _ => new SortKey(string.Empty, string.Empty, view.PersonId),
        };
    }

    /// <summary>拠点の順（法人のコード・拠点コード）での位置。拠点が見えない・無い人は最後。</summary>
    private string SitePosition(PersonView view) =>
        view.Site is { } site
            ? string.Concat(_data.FindLegalEntity(site.LegalEntityId)?.Code ?? string.Empty, "\u001f", site.Code, "\u001f", site.Id.ToString("D"))
            : Last;

    /// <summary>主務（出向でない主務を優先）の組織の、組織図の順（法人のコード・上位から深さ優先・組織コード）での位置。</summary>
    private string OrgPosition(PersonView view)
    {
        if (view.Assignments is not { Count: > 0 } assignments)
        {
            return Last;
        }

        var home = assignments.FirstOrDefault(a => a.Type == AssignmentType.Primary && a.Secondment == SecondmentType.None)
            ?? assignments.FirstOrDefault(a => a.Type == AssignmentType.Primary)
            ?? assignments[0];
        _orgOrder ??= BuildOrgOrder();
        return _orgOrder.TryGetValue(home.OrgUnitId, out var index) ? index.ToString("D6", CultureInfo.InvariantCulture) : Last;
    }

    private Dictionary<Guid, int> BuildOrgOrder()
    {
        var children = _data.Units.ToLookup(u => u.ParentId is { } p && _data.FindUnit(p) is not null ? p : (Guid?)null);
        var order = new Dictionary<Guid, int>();
        void Visit(IEnumerable<OrgUnit> units)
        {
            foreach (var unit in units.OrderBy(u => u.Code, StringComparer.Ordinal))
            {
                if (order.TryAdd(unit.Id, order.Count))
                {
                    Visit(children[unit.Id]);
                }
            }
        }

        foreach (var entity in _data.LegalEntities.OrderBy(e => e.Code, StringComparer.Ordinal))
        {
            Visit(children[null].Where(u => u.LegalEntityId == entity.Id));
        }

        return order;
    }

    /// <summary>見えない値の並びの位置（どの値よりも後）。</summary>
    private const string Last = "￿";

    private static string? Key(string? value) => SearchKey.From(value);

    private static HashSet<string> Keys(IEnumerable<string> values) => [.. values.Select(Key).OfType<string>()];

    private readonly record struct SortKey(string Primary, string Secondary, Guid Id) : IComparable<SortKey>
    {
        public int CompareTo(SortKey other)
        {
            var c = string.CompareOrdinal(Primary, other.Primary);
            if (c != 0)
            {
                return c;
            }

            c = string.CompareOrdinal(Secondary, other.Secondary);
            return c != 0 ? c : Id.CompareTo(other.Id);
        }
    }

    /// <summary>条件ごとの件数を数える。値は投影した後の値（見える項目）だけ。</summary>
    private sealed class FacetCounter(OrgSnapshot data)
    {
        private readonly Dictionary<Facet, Dictionary<string, int>> _person = [];
        private readonly Dictionary<AssignmentPart, Dictionary<string, int>> _assignment = [];
        private readonly Dictionary<string, string> _entityNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _siteNames = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

        public void AddPerson(Facet facet, PersonView view)
        {
            string? value = facet switch
            {
                Facet.LegalEntity => view.LegalEntity?.Id.ToString("D"),
                Facet.EmploymentType => Text(view.EmploymentType),
                Facet.JobType => Text(view.JobType),
                Facet.Site => view.Site?.Id.ToString("D"),
                Facet.SiteType => view.Site is { } site ? SiteCodes.ToCode(site.Type) : null,
                Facet.Prefecture => view.Site?.PrefectureCode,
                Facet.RemoteWork => view.RemoteWork switch { true => "true", false => "false", null => null },
                Facet.EmploymentStatus => view.EmploymentStatus,
                Facet.AccountStatus => view.AccountStatus,
                _ => null,
            };
            if (value is null)
            {
                return;
            }

            if (facet == Facet.LegalEntity)
            {
                _entityNames[value] = view.LegalEntity!.Name;
            }
            else if (facet == Facet.Site)
            {
                _siteNames[value] = view.Site!.Name;
            }

            Increment(_person, facet, value);
        }

        /// <summary>所属の値を、人ごとに重複なく数える。</summary>
        public void AddAssignments(IEnumerable<AssignmentView> assignments, Func<AssignmentView, string?> valueOf, AssignmentPart part)
        {
            foreach (var value in assignments.Select(valueOf).Select(v => part == AssignmentPart.Position ? Text(v) : v).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                Increment(_assignment, part, value);
            }
        }

        public PersonFacets Build()
        {
            var levelNames = data.LevelSchemes
                .SelectMany(s => s.Levels)
                .GroupBy(l => l.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (Rank: g.Max(l => l.Rank), Name: string.Join("・", g.Select(l => l.Name).Distinct(StringComparer.Ordinal))), StringComparer.Ordinal);
            return new PersonFacets(
                [.. Counts(_person, Facet.LegalEntity)
                    .Select(c => new FacetValue(c.Key, _entityNames[c.Key], c.Value))
                    .OrderByDescending(v => v.Count)
                    .ThenBy(v => v.Label, StringComparer.Ordinal)],
                [.. Counts(_assignment, AssignmentPart.Level)
                    .Select(c => (Value: c, Level: levelNames.GetValueOrDefault(c.Key)))
                    .OrderBy(x => x.Level.Rank)
                    .ThenBy(x => x.Value.Key, StringComparer.Ordinal)
                    .Select(x => new FacetValue(x.Value.Key, x.Level.Name ?? x.Value.Key, x.Value.Value))],
                Fixed(Counts(_assignment, AssignmentPart.Kind), PersonSearchCodes.AssignmentKinds),
                Texts(Counts(_assignment, AssignmentPart.Position)),
                Texts(Counts(_person, Facet.EmploymentType)),
                Texts(Counts(_person, Facet.JobType)),
                [.. Counts(_person, Facet.Site)
                    .Select(c => new FacetValue(c.Key, _siteNames[c.Key], c.Value))
                    .OrderByDescending(v => v.Count)
                    .ThenBy(v => v.Label, StringComparer.Ordinal)
                    .Take(PersonFacets.TextLimit)],
                Fixed(Counts(_person, Facet.SiteType), PersonSearchCodes.SiteTypes),
                [.. PersonSearchCodes.Prefectures
                    .Where(Counts(_person, Facet.Prefecture).ContainsKey)
                    .Select(code => new FacetValue(code, SiteCodes.PrefectureName(code)!, Counts(_person, Facet.Prefecture)[code]))],
                Fixed(Counts(_person, Facet.RemoteWork), PersonSearchCodes.RemoteWorkValues),
                Fixed(Counts(_person, Facet.EmploymentStatus), PersonSearchCodes.EmploymentStatuses),
                Fixed(Counts(_person, Facet.AccountStatus), PersonSearchCodes.AccountStatuses));
        }

        /// <summary>文字列の値は正規化表記で同一視し、最初に見た表記を代表にする。</summary>
        private string? Text(string? value)
        {
            if (Key(value) is not { } key)
            {
                return null;
            }

            _texts.TryAdd(key, value!.Trim());
            return key;
        }

        private List<FacetValue> Texts(Dictionary<string, int> counts) =>
        [
            .. counts
                .Select(c => new FacetValue(_texts[c.Key], _texts[c.Key], c.Value))
                .OrderByDescending(v => v.Count)
                .ThenBy(v => v.Label, StringComparer.Ordinal)
                .Take(PersonFacets.TextLimit),
        ];

        private static List<FacetValue> Fixed(Dictionary<string, int> counts, IReadOnlyList<string> order) =>
            [.. order.Where(counts.ContainsKey).Select(code => new FacetValue(code, code, counts[code]))];

        private static Dictionary<string, int> Counts<TKey>(Dictionary<TKey, Dictionary<string, int>> all, TKey key)
            where TKey : notnull => all.GetValueOrDefault(key) ?? [];

        private static void Increment<TKey>(Dictionary<TKey, Dictionary<string, int>> all, TKey key, string value)
            where TKey : notnull
        {
            if (!all.TryGetValue(key, out var counts))
            {
                all[key] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
            }

            counts[value] = counts.GetValueOrDefault(value) + 1;
        }
    }
}

/// <summary>ページの続きの位置の人を参照できなくなった（その人が公開範囲の外になったなど）。最初から検索し直してもらう。</summary>
public sealed class StaleCursorException : Exception
{
    public StaleCursorException()
        : base("ページの続きの位置を決められなくなりました。最初から検索し直してください。")
    {
    }

    public StaleCursorException(string message)
        : base(message)
    {
    }

    public StaleCursorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
