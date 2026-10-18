namespace JinJi.Domain.Workplace;

/// <summary>
/// 時点の建物・階・配置図・人の席（<see cref="Organization.OrgSnapshot.Workplace"/>）。生成後は不変。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>中身はその時点に有効な行だけ（読み込みは閲覧者を RLS に渡すので、人事管理者以外には将来の版・改訂・割り当てが現れない）</item>
/// <item>建物・階のどちらかがその時点に無い席・割り当ては、解決では「無い」として扱う（閉じた席を黙って別の席に読み替えない）</item>
/// </list>
/// </remarks>
public sealed class WorkplaceMap
{
    private static readonly IReadOnlyList<WorkplaceAssignment> NoAssignments = [];

    private readonly Dictionary<Guid, Building> _buildings;
    private readonly Dictionary<Guid, Floor> _floors;
    private readonly Dictionary<Guid, FloorLayout> _layoutsByFloor;
    private readonly Dictionary<Guid, SeatPlacement> _seats;
    private readonly Dictionary<(Guid Floor, Guid Zone), LayoutZone> _zones;
    private readonly Dictionary<Guid, IReadOnlyList<WorkplaceAssignment>> _byPerson;
    private readonly Dictionary<Guid, IReadOnlyList<WorkplaceAssignment>> _byFloor;
    private readonly Dictionary<Guid, IReadOnlyList<WorkplaceAssignment>> _bySeat;

    private WorkplaceMap(
        IEnumerable<Building> buildings,
        IEnumerable<Floor> floors,
        IEnumerable<FloorLayout> layouts,
        IEnumerable<WorkplaceAssignment> assignments)
    {
        _buildings = buildings.ToDictionary(b => b.Id);
        _floors = floors.Where(f => _buildings.ContainsKey(f.BuildingId)).ToDictionary(f => f.Id);
        _layoutsByFloor = [];
        foreach (var layout in layouts.Where(l => _floors.ContainsKey(l.FloorId)))
        {
            _layoutsByFloor.TryAdd(layout.FloorId, layout);
        }

        _seats = [];
        _zones = [];
        foreach (var layout in _layoutsByFloor.Values)
        {
            var floor = _floors[layout.FloorId];
            foreach (var zone in layout.Zones)
            {
                _zones.TryAdd((layout.FloorId, zone.ZoneKey), zone);
            }

            foreach (var seat in layout.Seats)
            {
                _seats.TryAdd(seat.SeatId, new SeatPlacement(floor, seat));
            }
        }

        var list = assignments.Where(a => _floors.ContainsKey(a.FloorId)).ToList();
        _byPerson = list.GroupBy(a => a.PersonId).ToDictionary(g => g.Key, g => (IReadOnlyList<WorkplaceAssignment>)[.. g.OrderBy(a => a.Role)]);
        _byFloor = list.GroupBy(a => a.FloorId).ToDictionary(g => g.Key, g => (IReadOnlyList<WorkplaceAssignment>)[.. g]);
        _bySeat = list.Where(a => a.SeatId is not null).GroupBy(a => a.SeatId!.Value).ToDictionary(g => g.Key, g => (IReadOnlyList<WorkplaceAssignment>)[.. g]);
    }

    /// <summary>建物・階の無い（建物・フロアモードを使っていない）地図。</summary>
    public static WorkplaceMap Empty { get; } = new([], [], [], []);

    /// <summary>地図を作る（同じ階の改訂が複数あれば先のものを使う。DB の排他制約で起きない）。</summary>
    public static WorkplaceMap Create(
        IEnumerable<Building> buildings,
        IEnumerable<Floor> floors,
        IEnumerable<FloorLayout> layouts,
        IEnumerable<WorkplaceAssignment> assignments)
    {
        ArgumentNullException.ThrowIfNull(buildings);
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(layouts);
        ArgumentNullException.ThrowIfNull(assignments);
        return new WorkplaceMap(buildings, floors, layouts, assignments);
    }

    public IReadOnlyCollection<Building> Buildings => _buildings.Values;

    public IReadOnlyCollection<Floor> Floors => _floors.Values;

    /// <summary>その時点の人の席・持ち場のすべて。</summary>
    public IEnumerable<WorkplaceAssignment> Assignments => _byPerson.Values.SelectMany(a => a);

    public Building? FindBuilding(Guid id) => _buildings.GetValueOrDefault(id);

    public Floor? FindFloor(Guid id) => _floors.GetValueOrDefault(id);

    /// <summary>拠点の建物（並び順・建物コードの順）。</summary>
    public IReadOnlyList<Building> BuildingsOf(Guid siteId) =>
        [.. _buildings.Values.Where(b => b.SiteId == siteId).OrderBy(b => b.SortOrder).ThenBy(b => b.Code, StringComparer.Ordinal)];

    /// <summary>建物の階（並び順。下の階が先）。</summary>
    public IReadOnlyList<Floor> FloorsOf(Guid buildingId) =>
        [.. _floors.Values.Where(f => f.BuildingId == buildingId).OrderBy(f => f.LevelOrder)];

    /// <summary>階のその時点の改訂。無ければ null。</summary>
    public FloorLayout? LayoutOf(Guid floorId) => _layoutsByFloor.GetValueOrDefault(floorId);

    /// <summary>席の置き場所（その時点の改訂にある席だけ）。</summary>
    public SeatPlacement? FindSeat(Guid seatId) => _seats.GetValueOrDefault(seatId);

    /// <summary>階のその時点の改訂のゾーン。無ければ null。</summary>
    public LayoutZone? FindZone(Guid floorId, Guid zoneKey) => _zones.GetValueOrDefault((floorId, zoneKey));

    /// <summary>人の席・持ち場（主の席が先）。</summary>
    public IReadOnlyList<WorkplaceAssignment> AssignmentsOf(Guid personId) => _byPerson.GetValueOrDefault(personId) ?? NoAssignments;

    /// <summary>階の席・持ち場の割り当て。</summary>
    public IReadOnlyList<WorkplaceAssignment> AssignmentsOnFloor(Guid floorId) => _byFloor.GetValueOrDefault(floorId) ?? NoAssignments;

    /// <summary>席の割り当て（持ち場は複数人）。</summary>
    public IReadOnlyList<WorkplaceAssignment> AssignmentsOfSeat(Guid seatId) => _bySeat.GetValueOrDefault(seatId) ?? NoAssignments;
}
