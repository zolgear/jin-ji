using JinJi.Domain.Workplace;

namespace JinJi.Domain.Organization;

/// <summary>
/// 時点指定の組織ツリーのスナップショット（組織・所属・上長系列）。DB に依存せず、メモリ上で関係を判定する。
/// </summary>
/// <remarks>
/// <para>
/// 想定規模（最大 1 万人、アーキテクチャ概要 §1）はメモリに載るので、生成時に祖先・段の順位・上長系列をすべて前計算する。
/// 生成後は不変で、複数スレッドから同時に参照してよい。
/// </para>
/// <para>
/// <b>段と順位</b>: 組織は法人の段の定義（<see cref="OrgLevelScheme"/>。法人に定義が無ければグループ共通の既定）の段を参照する。
/// 段の順位は上から 1, 2, ...。段の飛ばし（事業部直下の課）は許す。段を持たない組織（室・プロジェクト等）は、
/// 最上位（上位組織が無い）なら順位 0（法人全体相当）、それ以外は順位を持たず、判定では素通りする（所属者は祖先の段で判定する）。
/// 段のキーが法人の定義に無い組織も段を持たない組織として扱う。子の段の順位は親（段を持つ最も近い祖先）より必ず下
/// （<see cref="LevelOrderViolations"/> で検査する）。
/// </para>
/// <para>
/// <b>基準組織</b>（<see cref="BaseUnitOf"/>）: 「同じ〈段〉内」（same_level）の判定は、所属組織から上にたどって決めた基準組織が一致するかで行う。
/// 段 k の順位を r（所属組織の法人の定義で解決する。定義に無ければ基準組織なし = 誰とも一致しない）として:
/// <list type="number">
/// <item>所属組織から上にたどり、順位がちょうど r の組織があればそれ（例: チーム → 所属する課）。
/// 順位を持たない組織は素通りし、r より上位（順位が小さい）の組織に出たら探すのをやめる</item>
/// <item>見つからず、所属組織自体が r より上位なら所属組織自体（例: 部の直属の人の「課内」は、その部の直属の人だけ）</item>
/// <item>見つからなければ、やめた所までにたどった組織（r より下位か順位を持たない組織）の最上位
/// （例: 部を飛ばした事業部直下の課の「部内」は、その課。事業部直下の段を持たない室の「部内」は、その室）</item>
/// </list>
/// 段が欠けている場合は広げずに狭める方向へ倒すので、範囲が法人の外に広がることはない。
/// 同じ定義の中では、上位の段の範囲 ⊇ 下位の段の範囲（下位の段で一致すれば上位の段でも一致する）が成り立つ。
/// 組織ツリーは法人ごとなので、法人の違う組織の基準組織は一致しない（法人ごとに定義が違っても same_level は法人の外に広がらない）。
/// </para>
/// <para>
/// <b>上長</b>: 所属組織から上にたどり、本人以外の組織長が最初に見つかった組織の組織長。兼務・出向を含むすべての所属について求めた和集合。
/// 組織長がスナップショットに居ない（退職済みで版が無い等）場合はさらに上にたどる。
/// </para>
/// <para>
/// スナップショットに無い組織を指す所属・上位組織は無視する（上位組織が無ければ最上位として扱う）。上位組織の循環は打ち切る。
/// </para>
/// </remarks>
public sealed class OrgSnapshot
{
    private static readonly IReadOnlyList<Assignment> NoAssignments = [];
    private static readonly IReadOnlySet<Guid> NoIds = new HashSet<Guid>();

    private readonly Dictionary<Guid, LegalEntity> _legalEntities;
    private readonly Dictionary<Guid, OrgUnit> _units;
    private readonly Dictionary<Guid, PersonRecord> _people;
    private readonly Dictionary<Guid, IReadOnlyList<Assignment>> _assignmentsByPerson;
    private readonly Dictionary<Guid, IReadOnlyList<Guid>> _ancestors;
    private readonly Dictionary<Guid, int?> _ranks;
    private readonly Dictionary<Guid, OrgLevelScheme> _schemesByEntity;
    private readonly OrgLevelScheme _defaultScheme;
    private readonly Dictionary<Guid, IReadOnlySet<Guid>> _directManagers;
    private readonly Dictionary<Guid, IReadOnlySet<Guid>> _managersChain;
    private readonly Dictionary<Guid, IReadOnlySet<Guid>> _legalEntitiesOfPerson;
    private readonly Dictionary<Guid, Site> _sites;

    private OrgSnapshot(
        DateOnly asOf,
        Dictionary<Guid, LegalEntity> legalEntities,
        Dictionary<Guid, OrgUnit> units,
        Dictionary<Guid, PersonRecord> people,
        Dictionary<Guid, IReadOnlyList<Assignment>> assignmentsByPerson,
        IReadOnlyList<OrgLevelScheme> levelSchemes,
        Dictionary<Guid, Site> sites,
        WorkplaceMap workplace,
        GroupMap groups)
    {
        AsOf = asOf;
        _sites = sites;
        Workplace = workplace;
        Groups = groups;
        _legalEntities = legalEntities;
        _units = units;
        _people = people;
        _assignmentsByPerson = assignmentsByPerson;
        LevelSchemes = levelSchemes;
        _defaultScheme = levelSchemes.FirstOrDefault(s => s.LegalEntityId is null) ?? new OrgLevelScheme(null, []);
        _schemesByEntity = [];
        foreach (var scheme in levelSchemes.Where(s => s.LegalEntityId is not null))
        {
            _schemesByEntity.TryAdd(scheme.LegalEntityId!.Value, scheme);
        }

        _ancestors = units.Keys.ToDictionary(id => id, id => (IReadOnlyList<Guid>)BuildAncestors(id));
        _ranks = units.Values.ToDictionary(u => u.Id, ComputeRank);

        _directManagers = people.Keys.ToDictionary(id => id, id => (IReadOnlySet<Guid>)ComputeDirectManagers(id));
        _managersChain = people.Keys.ToDictionary(id => id, id => (IReadOnlySet<Guid>)ComputeManagersChain(id));
        _legalEntitiesOfPerson = people.Values.ToDictionary(p => p.Id, p => (IReadOnlySet<Guid>)ComputeLegalEntities(p));
    }

    /// <summary>組織・所属の基準日。</summary>
    public DateOnly AsOf { get; }

    public IReadOnlyCollection<LegalEntity> LegalEntities => _legalEntities.Values;

    public IReadOnlyCollection<OrgUnit> Units => _units.Values;

    public IReadOnlyCollection<PersonRecord> People => _people.Values;

    /// <summary>その時点で有効な拠点。</summary>
    public IReadOnlyCollection<Site> Sites => _sites.Values;

    /// <summary>有効な段の定義（グループ共通の既定と法人ごとの定義）。</summary>
    public IReadOnlyList<OrgLevelScheme> LevelSchemes { get; }

    /// <summary>その時点の建物・階・配置図・人の席（建物・フロアモード。0019）。</summary>
    public WorkplaceMap Workplace { get; }

    /// <summary>その時点のグループとメンバー（チーム・プロジェクト。ADR-0014）。</summary>
    public GroupMap Groups { get; }

    /// <summary>スナップショットを作る。</summary>
    /// <param name="levelSchemes">有効な段の定義。既定（法人 null）は高々 1 つ、法人ごとにも高々 1 つ（重複は先のものを使う）。</param>
    /// <param name="sites">その時点で有効な拠点（省略は拠点なし）。</param>
    /// <param name="workplace">その時点の建物・階・配置図・人の席（省略は建物なし）。</param>
    /// <param name="groups">その時点のグループとメンバー（省略はグループなし）。</param>
    public static OrgSnapshot Create(
        DateOnly asOf,
        IEnumerable<LegalEntity> legalEntities,
        IEnumerable<OrgUnit> units,
        IEnumerable<PersonRecord> people,
        IEnumerable<Assignment> assignments,
        IEnumerable<OrgLevelScheme> levelSchemes,
        IEnumerable<Site>? sites = null,
        WorkplaceMap? workplace = null,
        GroupMap? groups = null)
    {
        ArgumentNullException.ThrowIfNull(legalEntities);
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(levelSchemes);

        var unitMap = units.ToDictionary(u => u.Id);
        var peopleMap = people.ToDictionary(p => p.Id);
        var byPerson = assignments
            .Where(a => unitMap.ContainsKey(a.OrgUnitId) && peopleMap.ContainsKey(a.PersonId))
            .GroupBy(a => a.PersonId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Assignment>)[.. g.OrderBy(a => a.Type)]);

        return new OrgSnapshot(
            asOf, legalEntities.ToDictionary(e => e.Id), unitMap, peopleMap, byPerson, [.. levelSchemes], (sites ?? []).ToDictionary(s => s.Id), workplace ?? WorkplaceMap.Empty,
            groups ?? GroupMap.Empty);
    }

    /// <summary>同じ組織・人のデータで、段の定義だけを差し替えたスナップショットを作る（段の定義の変更の検査に使う）。</summary>
    public OrgSnapshot WithLevelSchemes(IEnumerable<OrgLevelScheme> levelSchemes)
    {
        ArgumentNullException.ThrowIfNull(levelSchemes);
        return new OrgSnapshot(AsOf, _legalEntities, _units, _people, _assignmentsByPerson, [.. levelSchemes], _sites, Workplace, Groups);
    }

    /// <summary>法人に効く段の定義（法人の定義、無ければグループ共通の既定）。</summary>
    public OrgLevelScheme LevelSchemeOf(Guid legalEntityId) => _schemesByEntity.GetValueOrDefault(legalEntityId) ?? _defaultScheme;

    public LegalEntity? FindLegalEntity(Guid id) => _legalEntities.GetValueOrDefault(id);

    public OrgUnit? FindUnit(Guid id) => _units.GetValueOrDefault(id);

    public PersonRecord? FindPerson(Guid id) => _people.GetValueOrDefault(id);

    public Site? FindSite(Guid id) => _sites.GetValueOrDefault(id);

    /// <summary>
    /// 組織の拠点。組織に拠点が無ければ上位組織へたどり、最初に見つかった拠点（<see cref="ResolvedSite.Inherited"/> = true）。
    /// どこにも無いか、指す拠点がその時点で有効でなければ（閉鎖など）null。
    /// </summary>
    public ResolvedSite? SiteOfUnit(Guid unitId)
    {
        if (!_ancestors.TryGetValue(unitId, out var chain))
        {
            return null;
        }

        foreach (var id in chain)
        {
            if (_units[id].SiteId is { } siteId)
            {
                return _sites.TryGetValue(siteId, out var site) ? new ResolvedSite(site, id != unitId) : null;
            }
        }

        return null;
    }

    /// <summary>
    /// 人の拠点（ADR-0002）。<paramref name="version"/> に上書き（<see cref="PersonVersion.SiteId"/>）があればその拠点、
    /// 無ければ主務（出向でない主務を優先）の組織の拠点（<see cref="SiteOfUnit"/>。<see cref="ResolvedSite.Inherited"/> = true）。
    /// 上書きの拠点がその時点で有効でなければ null（主務の組織の拠点には戻さない。上書きは組織と違う拠点を指す値なので）。
    /// </summary>
    /// <param name="version">拠点の上書きを読む版（投影に使う版。人事管理者以外は今日の版）。</param>
    public ResolvedSite? SiteOf(Guid personId, PersonVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.SiteId is { } own)
        {
            return _sites.TryGetValue(own, out var site) ? new ResolvedSite(site, false) : null;
        }

        var assignments = AssignmentsOf(personId);
        var home = assignments.FirstOrDefault(a => a.Type == AssignmentType.Primary && a.Secondment == SecondmentType.None)
            ?? assignments.FirstOrDefault(a => a.Type == AssignmentType.Primary);
        return home is not null && SiteOfUnit(home.OrgUnitId) is { } unitSite ? unitSite with { Inherited = true } : null;
    }

    /// <summary>
    /// 人の階・エリア（建物・フロアモードの仕様 §2.4）: 主の席の割り当てならその席の階とゾーン、主のゾーンの割り当て（工場のライン・倉庫のエリアなど
    /// 席の無い持ち場）ならその階とゾーン。割り当てが無ければ null。割り当ての席・ゾーンがその時点の改訂に無ければエリアは無し（階は割り当ての階）で、
    /// 別の席・エリアに読み替えない。
    /// </summary>
    public ResolvedWorkplace? WorkplaceOf(Guid personId)
    {
        if (Workplace.AssignmentsOf(personId).FirstOrDefault(a => a.Role == WorkplaceRole.Primary) is not { } primary
            || Workplace.FindFloor(primary.FloorId) is not { } floor
            || Workplace.FindBuilding(floor.BuildingId) is not { } building)
        {
            return null;
        }

        if (primary.SeatId is { } seatId)
        {
            var seatZone = Workplace.FindSeat(seatId)?.Seat.ZoneKey;
            return new ResolvedWorkplace(building, floor, seatZone is { } z ? Workplace.FindZone(floor.Id, z) : null, WorkplaceSource.Seat);
        }

        return new ResolvedWorkplace(building, floor, primary.ZoneKey is { } key ? Workplace.FindZone(floor.Id, key) : null, WorkplaceSource.Zone);
    }

    /// <summary>
    /// 人の席（主の席が先、副の席が後）。その時点の改訂に無い席・階・建物の席は含めない（「席なし」。別の席に読み替えない）。
    /// ゾーンの割り当て（席の無い持ち場・エリア）は含めない。
    /// </summary>
    public IReadOnlyList<ResolvedSeat> SeatsOf(Guid personId) =>
    [
        .. Workplace.AssignmentsOf(personId)
            .Where(a => a.SeatId is not null)
            .Select(a => Workplace.FindSeat(a.SeatId!.Value) is { } placement
                && placement.Floor.Id == a.FloorId
                && Workplace.FindBuilding(placement.Floor.BuildingId) is { } building
                ? new ResolvedSeat(building, placement.Floor, placement.Seat, placement.Seat.ZoneKey is { } z ? Workplace.FindZone(a.FloorId, z) : null, a.Role)
                : null)
            .OfType<ResolvedSeat>(),
    ];

    /// <summary>人の所属（主務が先）。スナップショットに居なければ空。</summary>
    public IReadOnlyList<Assignment> AssignmentsOf(Guid personId) => _assignmentsByPerson.GetValueOrDefault(personId) ?? NoAssignments;

    /// <summary>組織自身から最上位までの組織 ID（自組織が先頭）。</summary>
    public IReadOnlyList<Guid> AncestorsOf(Guid unitId) =>
        _ancestors.TryGetValue(unitId, out var chain) ? chain : throw UnknownUnit(unitId);

    /// <summary>
    /// 組織の段の順位。段を持たない組織は、最上位なら 0（法人全体相当）、それ以外は null（判定では素通りする）。
    /// </summary>
    public int? RankOf(Guid unitId) => _ranks.TryGetValue(unitId, out var rank) ? rank : throw UnknownUnit(unitId);

    /// <summary>
    /// 所属組織から見た、段 <paramref name="levelKey"/> の基準組織（クラスの説明の規則を参照）。
    /// 段が所属組織の法人の定義に無ければ null（その段では誰とも一致しない）。
    /// </summary>
    public Guid? BaseUnitOf(Guid unitId, string levelKey)
    {
        ArgumentNullException.ThrowIfNull(levelKey);
        var chain = AncestorsOf(unitId);
        if (LevelSchemeOf(_units[unitId].LegalEntityId).Find(levelKey) is not { } level)
        {
            return null;
        }

        // 1. 順位がちょうど r の組織。順位を持たない組織は素通りし、より上位の組織に出たら打ち切る。
        //    3. 見つからなければ、打ち切るまでにたどった組織の最上位（2. 所属組織自体が上位なら所属組織自体）
        var top = (Guid?)null;
        foreach (var id in chain)
        {
            if (_ranks[id] is { } rank)
            {
                if (rank == level.Rank)
                {
                    return id;
                }

                if (rank < level.Rank)
                {
                    break;
                }
            }

            top = id;
        }

        return top ?? unitId;
    }

    /// <summary>
    /// 段の順位の逆転（子の組織の順位が、段を持つ最も近い祖先の順位より上か等しい）を返す。空なら問題なし。
    /// 段の定義の変更を承認する前と、組織を書き込む前に検査する（DB では検査しない。overview §5.6）。
    /// </summary>
    public IReadOnlyList<(Guid Unit, Guid Ancestor)> LevelOrderViolations()
    {
        var violations = new List<(Guid, Guid)>();
        foreach (var (id, chain) in _ancestors)
        {
            if (_ranks[id] is not { } rank)
            {
                continue;
            }

            foreach (var ancestor in chain.Skip(1))
            {
                if (_ranks[ancestor] is { } ancestorRank)
                {
                    if (rank <= ancestorRank)
                    {
                        violations.Add((id, ancestor));
                    }

                    break;
                }
            }
        }

        return violations;
    }

    /// <summary>直属の上長（所属ごとに求めた和集合）。</summary>
    public IReadOnlySet<Guid> DirectManagersOf(Guid personId) => _directManagers.GetValueOrDefault(personId) ?? NoIds;

    /// <summary>上長系列（直属の上長、その上長、…）。本人は含まない。</summary>
    public IReadOnlySet<Guid> ManagersChainOf(Guid personId) => _managersChain.GetValueOrDefault(personId) ?? NoIds;

    /// <summary>人が属する法人（雇用元の法人と、所属組織の法人の和集合。出向先を含む）。</summary>
    public IReadOnlySet<Guid> LegalEntitiesOf(Guid personId) => _legalEntitiesOfPerson.GetValueOrDefault(personId) ?? NoIds;

    /// <summary>組織が、指定組織自身かその配下か。</summary>
    public bool IsSelfOrDescendantOf(Guid unitId, Guid ancestorId) =>
        _ancestors.TryGetValue(unitId, out var chain) && chain.Contains(ancestorId);

    private static KeyNotFoundException UnknownUnit(Guid unitId) =>
        new($"組織 {unitId} はスナップショット（{nameof(OrgSnapshot)}）にありません。");

    private Guid[] BuildAncestors(Guid unitId)
    {
        var chain = new List<Guid>();
        var visited = new HashSet<Guid>();
        for (Guid? current = unitId; current is { } id && _units.TryGetValue(id, out var unit) && visited.Add(id); current = unit.ParentId)
        {
            chain.Add(id);
        }

        return [.. chain];
    }

    private int? ComputeRank(OrgUnit unit)
    {
        if (unit.LevelKey is { } key && LevelSchemeOf(unit.LegalEntityId).Find(key) is { } level)
        {
            return level.Rank;
        }

        // 段を持たない最上位の組織（上位組織が無い・スナップショットに無い・循環の打ち切り）は法人全体相当
        return _ancestors[unit.Id].Count == 1 ? 0 : null;
    }

    private HashSet<Guid> ComputeDirectManagers(Guid personId)
    {
        var managers = new HashSet<Guid>();
        foreach (var assignment in AssignmentsOf(personId))
        {
            foreach (var unitId in _ancestors[assignment.OrgUnitId])
            {
                if (_units[unitId].HeadPersonId is { } head && head != personId && _people.ContainsKey(head))
                {
                    managers.Add(head);
                    break;
                }
            }
        }

        return managers;
    }

    private HashSet<Guid> ComputeManagersChain(Guid personId)
    {
        var chain = new HashSet<Guid>();
        var queue = new Queue<Guid>(_directManagers[personId]);
        while (queue.TryDequeue(out var manager))
        {
            if (manager == personId || !chain.Add(manager))
            {
                continue;
            }

            foreach (var next in _directManagers.GetValueOrDefault(manager) ?? NoIds)
            {
                queue.Enqueue(next);
            }
        }

        return chain;
    }

    private HashSet<Guid> ComputeLegalEntities(PersonRecord person)
    {
        var entities = new HashSet<Guid> { person.LegalEntityId };
        foreach (var assignment in AssignmentsOf(person.Id))
        {
            entities.Add(_units[assignment.OrgUnitId].LegalEntityId);
        }

        return entities;
    }
}

/// <summary>解決した拠点。</summary>
/// <param name="Inherited">組織の拠点なら上位組織から、人の拠点なら主務の組織から引き継いだか（false は自分で持つ拠点）。</param>
public sealed record ResolvedSite(Site Site, bool Inherited);
