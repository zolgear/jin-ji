using System.Globalization;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Workplace;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>建物・階・配置図の改訂と、人の席・持ち場の取込（0019。建物・フロアモードの仕様 §4）。</summary>
public sealed partial class ProposalPlanner
{
    /// <summary>配置図（JSON・席の CSV）の取込の提案を作る。</summary>
    public static PlanResult PlanFloorLayouts(PlanContext context, IReadOnlyList<NormalizedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var planner = new ProposalPlanner(context);
        planner.FloorLayouts(rows);
        return planner.Result();
    }

    /// <summary>割り当て（CSV）の取込の提案を作る。</summary>
    public static PlanResult PlanWorkplaceAssignments(PlanContext context, IReadOnlyList<NormalizedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var planner = new ProposalPlanner(context);
        planner.WorkplaceAssignments(rows);
        return planner.Result();
    }

    private WorkplaceMap Workplace => Snapshot.Workplace;

    // -----------------------------------------------------------------------
    // 建物・階・配置図の改訂
    // -----------------------------------------------------------------------

    /// <summary>
    /// 配置図の行（要素ごと）から、建物・階の新規と版の差分、配置図の改訂（改訂 1 つ分を 1 つの提案）を作る。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>建物は拠点（拠点コード）の中の建物コードで、階は建物の中の表示名で突合する。新しい建物・階は先に承認が要る（DependsOn）</item>
    /// <item>改訂は外形・固定の部分・ゾーン・席の全体を 1 つの起案にし、別の人事管理者の承認で有効日から切り替える。外形の行が無い（席の CSV）なら、
    /// 今の改訂の外形・固定の部分・ゾーンを引き継いで席だけを置き換える</item>
    /// <item>席番号が同じなら同じ席（改訂をまたいで割り当てが続く）。ゾーンは名前が同じなら同じゾーン</item>
    /// <item>形の検査（頂点数・自己交差・外形の内側・席の定員と種類・設備のタグの許可リスト）に通らない要素は修正依頼にして除く</item>
    /// <item>改訂で席が無くなって割り当てが外れる人の数を根拠に書く（承認の画面に出す）。今と同じ配置図なら提案を作らない</item>
    /// </list>
    /// </remarks>
    private void FloorLayouts(IReadOnlyList<NormalizedRow> rows)
    {
        foreach (var row in rows)
        {
            AddIssueCorrections(row);
        }

        foreach (var group in rows.GroupBy(r => (Site: r[IngestFields.SiteCode], Building: r[IngestFields.BuildingCode])))
        {
            var first = group.First();
            if (group.Key.Site is not { } siteCode || Snapshot.Sites.SingleOrDefault(s => string.Equals(s.Code, siteCode, StringComparison.OrdinalIgnoreCase)) is not { } site)
            {
                Correction(first, IngestFields.SiteCode, CorrectionIssue.UnknownSite, "建物の拠点（拠点コード）が jin-ji の拠点マスタに見つからないか、一意に決まりません。");
                continue;
            }

            if (group.Key.Building is not { } code)
            {
                Correction(first, IngestFields.BuildingCode, CorrectionIssue.MissingRequired, "建物コードがありません。");
                continue;
            }

            var floorRows = group.Where(r => r[IngestFields.FloorLabel] is not null).GroupBy(r => r[IngestFields.FloorLabel]!).ToList();
            foreach (var row in group.Where(r => r[IngestFields.FloorLabel] is null))
            {
                Correction(row, IngestFields.FloorLabel, CorrectionIssue.MissingRequired, "階の表示名がありません。");
            }

            var start = group.Select(r => Date(r[IngestFields.LayoutValidFrom])).OfType<DateOnly>().DefaultIfEmpty(_context.EffectiveDate ?? _context.Today).Min();
            var existing = Workplace.BuildingsOf(site.Id).FirstOrDefault(b => string.Equals(b.Code, code, StringComparison.OrdinalIgnoreCase));
            Guid buildingId;
            Guid? buildingProposal = null;
            if (existing is null)
            {
                if (group.Select(r => r[IngestFields.BuildingName]).FirstOrDefault(n => n is not null) is not { } name)
                {
                    Correction(first, IngestFields.BuildingName, CorrectionIssue.MissingRequired, "新しい建物の建物名がありません。");
                    continue;
                }

                buildingId = EntityId.New();
                buildingProposal = Add(
                    first,
                    ProposalTargetKind.Building,
                    ProposalOperation.Create,
                    buildingId,
                    start,
                    Payload(
                        (ProposalFields.SiteId, Id(site.Id)),
                        (ProposalFields.Code, code),
                        (ProposalFields.Name, name),
                        (ProposalFields.SortOrder, first[IngestFields.BuildingSortOrder] ?? "0"),
                        (ProposalFields.ValidFrom, Iso(start))),
                    new(),
                    new ProposalBasis { Note = "建物コードが拠点の中に無いので、新しい建物として登録します。" },
                    CreateConfidence);
            }
            else
            {
                buildingId = existing.Id;
                var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
                if (group.Select(r => r[IngestFields.BuildingName]).FirstOrDefault(n => n is not null) is { } name && name != existing.Name)
                {
                    diff[ProposalFields.Name] = new FieldChange(existing.Name, name);
                }

                var sort = existing.SortOrder.ToString(CultureInfo.InvariantCulture);
                if (first[IngestFields.BuildingSortOrder] is { } order && order != sort)
                {
                    diff[ProposalFields.SortOrder] = new FieldChange(sort, order);
                }

                if (diff.Count > 0)
                {
                    Add(first, ProposalTargetKind.BuildingVersion, ChangeOperation(), existing.Id, _context.EffectiveDate, new(), diff, new ProposalBasis(), CreateConfidence);
                }
            }

            foreach (var floorGroup in floorRows)
            {
                PlanFloor(floorGroup.Key, [.. floorGroup], existing, buildingId, buildingProposal);
            }
        }
    }

    private void PlanFloor(string label, IReadOnlyList<NormalizedRow> rows, Building? building, Guid buildingId, Guid? buildingProposal)
    {
        var first = rows[0];
        var floor = building is null ? null : Workplace.FloorsOf(building.Id).FirstOrDefault(f => f.Label == label);
        var start = rows.Select(r => Date(r[IngestFields.LayoutValidFrom])).OfType<DateOnly>().DefaultIfEmpty(_context.EffectiveDate ?? _context.Today).Min();
        Guid floorId;
        Guid? floorProposal = null;
        var kind = rows.Select(r => r[IngestFields.FloorKind]).FirstOrDefault(k => k is not null);
        var order = rows.Select(r => r[IngestFields.FloorLevelOrder]).FirstOrDefault(o => o is not null);
        if (floor is null)
        {
            if (order is null)
            {
                Correction(first, IngestFields.FloorLevelOrder, CorrectionIssue.MissingRequired, "新しい階の並び順（地上 n 階 = n × 10、地下は負）がありません。");
                return;
            }

            floorId = EntityId.New();
            floorProposal = Add(
                first,
                ProposalTargetKind.Floor,
                ProposalOperation.Create,
                floorId,
                start,
                Payload(
                    (ProposalFields.BuildingId, Id(buildingId)),
                    (ProposalFields.Label, label),
                    (ProposalFields.LevelOrder, order),
                    (ProposalFields.FloorKind, kind ?? WorkplaceCodes.ToCode(FloorKind.Office)),
                    (ProposalFields.ValidFrom, Iso(start))),
                new(),
                new ProposalBasis
                {
                    Note = "階の表示名が建物の中に無いので、新しい階として登録します。",
                    DependsOn = buildingProposal is { } b ? [b] : [],
                },
                CreateConfidence);
        }
        else
        {
            floorId = floor.Id;
            var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
            var currentOrder = floor.LevelOrder.ToString(CultureInfo.InvariantCulture);
            if (order is not null && order != currentOrder)
            {
                diff[ProposalFields.LevelOrder] = new FieldChange(currentOrder, order);
            }

            if (kind is not null && kind != WorkplaceCodes.ToCode(floor.Kind))
            {
                diff[ProposalFields.FloorKind] = new FieldChange(WorkplaceCodes.ToCode(floor.Kind), kind);
            }

            if (diff.Count > 0)
            {
                Add(first, ProposalTargetKind.FloorVersion, ChangeOperation(), floor.Id, _context.EffectiveDate, new(), diff, new ProposalBasis(), CreateConfidence);
            }
        }

        var elements = rows.Where(r => r[IngestFields.LayoutElement] is not (null or WorkplaceValueCodes.FloorOnly)).ToList();
        if (elements.Count > 0)
        {
            PlanLayout(elements, floorId, floor is null ? null : Workplace.LayoutOf(floor.Id), floorProposal);
        }
    }

    private void PlanLayout(IReadOnlyList<NormalizedRow> allRows, Guid floorId, FloorLayout? current, Guid? floorProposal)
    {
        // 値を解釈できない行（多角形の形・設備のタグ・座標など。修正依頼にした行）は改訂に入れない。外形が不正なら改訂を作らない
        var first = allRows[0];
        if (allRows.Any(r => r[IngestFields.LayoutElement] == WorkplaceValueCodes.Outline && r.Issues.Count > 0))
        {
            return;
        }

        var rows = allRows.Where(r => r.Issues.Count == 0).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var outlineRows = rows.Where(r => r[IngestFields.LayoutElement] == WorkplaceValueCodes.Outline).ToList();
        foreach (var extra in outlineRows.Skip(1))
        {
            Correction(extra, IngestFields.LayoutElement, CorrectionIssue.DuplicateRow, "同じ階の外形の行が複数あります（最初の行を使います）。");
        }

        IReadOnlyList<LayoutPoint> outline;
        int grid;
        var inherit = outlineRows.Count == 0;
        if (!inherit)
        {
            var outlineRow = outlineRows[0];
            if (outlineRow[IngestFields.LayoutPolygon] is not { } polygon)
            {
                Correction(outlineRow, IngestFields.LayoutPolygon, CorrectionIssue.MissingRequired, "階の外形の多角形がありません。");
                return;
            }

            outline = LayoutJson.ParsePolygon(polygon);
            grid = outlineRow[IngestFields.LayoutGridMm] is { } g ? int.Parse(g, CultureInfo.InvariantCulture) : current?.GridMm ?? 100;
        }
        else if (current is null)
        {
            Correction(first, IngestFields.LayoutElement, CorrectionIssue.MissingRequired, "階に配置図が無いので、外形（outline）の行が要ります（席の CSV だけでは配置図を作れません）。");
            return;
        }
        else
        {
            outline = current.Outline;
            grid = current.GridMm;
        }

        // 固定の部分
        var fixtureRows = rows.Where(r => r[IngestFields.LayoutElement] == WorkplaceValueCodes.Fixture).ToList();
        var fixtures = new List<LayoutFixture>();
        if (inherit && fixtureRows.Count == 0)
        {
            fixtures.AddRange(current!.Fixtures);
        }

        foreach (var row in fixtureRows)
        {
            if (!WorkplaceCodes.TryParse(row[IngestFields.LayoutKind], out FixtureKind fixtureKind))
            {
                Correction(row, IngestFields.LayoutKind, CorrectionIssue.InvalidValue, "固定の部分の種類は core（コア）/ room（部屋）/ void（吹き抜け）にしてください。");
                continue;
            }

            if (Polygon(row, outline) is { } polygon)
            {
                fixtures.Add(new LayoutFixture(fixtureKind, row[IngestFields.LayoutName], polygon));
            }
        }

        // ゾーン（名前が今の改訂と同じなら同じゾーン）
        var zoneRows = rows.Where(r => r[IngestFields.LayoutElement] == WorkplaceValueCodes.Zone).ToList();
        var zones = new List<LayoutZone>();
        if (inherit && zoneRows.Count == 0)
        {
            zones.AddRange(current!.Zones);
        }

        foreach (var row in ExcludeDuplicates(zoneRows, IngestFields.LayoutName, "ゾーンの名前"))
        {
            if (row[IngestFields.LayoutName] is not { } name)
            {
                Correction(row, IngestFields.LayoutName, CorrectionIssue.MissingRequired, "ゾーンの名前がありません。");
                continue;
            }

            if (!WorkplaceCodes.TryParse(row[IngestFields.LayoutKind] ?? "office", out ZoneKind zoneKind))
            {
                Correction(row, IngestFields.LayoutKind, CorrectionIssue.InvalidValue, $"ゾーンの種類は {string.Join(" / ", WorkplaceCodes.ZoneKinds)} にしてください。");
                continue;
            }

            if (Polygon(row, outline) is { } polygon)
            {
                var key = current?.Zones.FirstOrDefault(z => z.Name == name)?.ZoneKey ?? EntityId.New();
                zones.Add(new LayoutZone(key, name, zoneKind, polygon));
            }
        }

        // 席（席番号が今の改訂と同じなら同じ席）
        var seatRows = rows.Where(r => r[IngestFields.LayoutElement] == WorkplaceValueCodes.Seat).ToList();
        var seats = new List<DraftSeat>();
        foreach (var row in ExcludeDuplicates(seatRows, IngestFields.SeatCode, "席番号"))
        {
            if (DraftSeatOf(row, outline, zones, current) is { } seat)
            {
                seats.Add(seat);
            }
        }

        if (seats.Count > WorkplaceCodes.MaxSeatsPerFloor || zones.Count > WorkplaceCodes.MaxZonesPerFloor || fixtures.Count > WorkplaceCodes.MaxFixturesPerFloor)
        {
            Correction(first, IngestFields.LayoutElement, CorrectionIssue.InvalidValue,
                $"1 階の席は {WorkplaceCodes.MaxSeatsPerFloor} 席、ゾーン・固定の部分はそれぞれ {WorkplaceCodes.MaxZonesPerFloor} 個までです（配置図を登録しません）。");
            return;
        }

        var draft = new LayoutDraft(
            grid,
            outline,
            fixtures,
            [.. zones.OrderBy(z => z.Name, StringComparer.Ordinal)],
            [.. seats.OrderBy(s => s.Placement.Code, StringComparer.Ordinal)]);
        if (current is not null && LayoutJson.WriteDraft(draft) == LayoutJson.WriteDraft(DraftOf(current)))
        {
            return;
        }

        var validFrom = rows.Select(r => Date(r[IngestFields.LayoutValidFrom])).OfType<DateOnly>().FirstOrDefault(_context.EffectiveDate ?? _context.Today);
        var kept = draft.Seats.Select(s => s.SeatId).ToHashSet();
        var displaced = current is null ? 0 : Workplace.AssignmentsOnFloor(floorId).Where(a => a.SeatId is { } s && !kept.Contains(s)).Select(a => a.PersonId).Distinct().Count();
        var note = $"配置図の改訂（席 {draft.Seats.Count}・ゾーン {draft.Zones.Count}・固定の部分 {draft.Fixtures.Count}）。{validFrom:yyyy-MM-dd} から切り替えます。"
            + (inherit ? "外形・固定の部分・ゾーンは今の配置図を引き継ぎます。" : string.Empty)
            + (displaced > 0 ? $"改訂で席が無くなり、席の割り当てが外れる人が {displaced} 人います（割り当ての取込で付け替えてください）。" : string.Empty);
        var id = Add(
            first,
            ProposalTargetKind.FloorLayout,
            ProposalOperation.Create,
            floorId,
            validFrom,
            Payload((ProposalFields.FloorId, Id(floorId)), (ProposalFields.Layout, LayoutJson.WriteDraft(draft)), (ProposalFields.ValidFrom, Iso(validFrom))),
            new(),
            new ProposalBasis { Note = note, DependsOn = floorProposal is { } f ? [f] : [] },
            CreateConfidence);

        // 改訂は階の行のすべてから作るので、根拠の行もすべてにする
        var index = _proposals.FindIndex(p => p.Id == id);
        _proposals[index] = _proposals[index] with
        {
            StagingRowIds = [.. allRows.Select(r => r.StagingRowId)],
            Basis = _proposals[index].Basis with { RowNumbers = [.. allRows.Select(r => r.SourceRowNumber)] },
        };
    }

    /// <summary>行の多角形（外形の内側か）。不正なら修正依頼にして null。</summary>
    private IReadOnlyList<LayoutPoint>? Polygon(NormalizedRow row, IReadOnlyList<LayoutPoint> outline)
    {
        if (row[IngestFields.LayoutPolygon] is not { } text)
        {
            Correction(row, IngestFields.LayoutPolygon, CorrectionIssue.MissingRequired, "多角形がありません。");
            return null;
        }

        var polygon = LayoutJson.ParsePolygon(text);
        if (!FloorGeometry.ContainsAll(outline, polygon))
        {
            Correction(row, IngestFields.LayoutPolygon, CorrectionIssue.InvalidValue, "多角形が階の外形の内側にありません。");
            return null;
        }

        return polygon;
    }

    private DraftSeat? DraftSeatOf(NormalizedRow row, IReadOnlyList<LayoutPoint> outline, IReadOnlyList<LayoutZone> zones, FloorLayout? current)
    {
        if (row[IngestFields.SeatCode] is not { } code)
        {
            Correction(row, IngestFields.SeatCode, CorrectionIssue.MissingRequired, "席番号がありません。");
            return null;
        }

        if (row[IngestFields.SeatXMm] is not { } x || row[IngestFields.SeatYMm] is not { } y)
        {
            Correction(row, IngestFields.SeatXMm, CorrectionIssue.MissingRequired, "席の座標（x_mm・y_mm）がありません。");
            return null;
        }

        var point = new LayoutPoint(int.Parse(x, CultureInfo.InvariantCulture), int.Parse(y, CultureInfo.InvariantCulture));
        if (!FloorGeometry.Contains(outline, point))
        {
            Correction(row, IngestFields.SeatXMm, CorrectionIssue.InvalidValue, "席の座標が階の外形の内側にありません。");
            return null;
        }

        var rotation = row[IngestFields.SeatRotation] is { } r ? int.Parse(r, CultureInfo.InvariantCulture) : 0;
        if (rotation % 15 != 0)
        {
            Correction(row, IngestFields.SeatRotation, CorrectionIssue.InvalidValue, "席の向きは 15° 刻み（0〜345）にしてください。");
            return null;
        }

        var kind = WorkplaceCodes.TryParse(row[IngestFields.SeatKind] ?? "fixed", out SeatKind parsed) ? parsed : SeatKind.Fixed;
        var capacity = row[IngestFields.SeatCapacity] is { } c ? int.Parse(c, CultureInfo.InvariantCulture) : 1;
        if (capacity > 1 && kind != SeatKind.Station)
        {
            Correction(row, IngestFields.SeatCapacity, CorrectionIssue.InvalidValue, "定員 2 以上にできるのは持ち場（station）だけです。");
            return null;
        }

        Guid? zoneKey = null;
        if (row[IngestFields.SeatZone] is { } zoneName)
        {
            if (zones.FirstOrDefault(z => z.Name == zoneName) is { } zone)
            {
                zoneKey = zone.ZoneKey;
            }
            else
            {
                Correction(row, IngestFields.SeatZone, CorrectionIssue.UnknownZone, "席のゾーンがこの階の配置図にありません（ゾーンを付けずに登録します）。");
            }
        }

        var existing = current?.Seats.FirstOrDefault(s => s.Code == code);
        var seatId = existing?.SeatId ?? EntityId.New();
        var features = row[IngestFields.SeatFeatures] is { } f ? f.Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        return new DraftSeat(
            seatId,
            existing is null,
            new LayoutSeat(
                seatId,
                code,
                zoneKey,
                point.X,
                point.Y,
                rotation,
                row[IngestFields.SeatDeskType] ?? (kind == SeatKind.Station ? "station" : "desk_1200"),
                kind,
                capacity,
                features,
                row[IngestFields.SeatOutOfService] == "true"));
    }

    /// <summary>今の改訂を起案の形にする（同じ配置図かの比較に使う）。</summary>
    private static LayoutDraft DraftOf(FloorLayout layout) => new(
        layout.GridMm,
        layout.Outline,
        layout.Fixtures,
        [.. layout.Zones.OrderBy(z => z.Name, StringComparer.Ordinal)],
        [.. layout.Seats.OrderBy(s => s.Code, StringComparer.Ordinal).Select(s => new DraftSeat(s.SeatId, false, s with { Features = [.. s.Features.Order(StringComparer.Ordinal)] }))]);

    // -----------------------------------------------------------------------
    // 人の席・持ち場
    // -----------------------------------------------------------------------

    /// <summary>
    /// 割り当ての行から、人の席・持ち場（主・副）の新規・変更・終了を提案する。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>社員番号の行は人の席。席番号（階の中。階を省くと全階で一意なら）かゾーンの名前で、今日の配置図に照合する。照合は社員番号（名寄せ済みの人）だけ</item>
    /// <item>社員番号の無い行は修正依頼にする（席は人に割り当てる。組織単位のエリア・フリーアドレスは扱わない）</item>
    /// <item>最終日だけの行は割り当ての終了。持ち場の定員を超える割り当ては修正依頼にする</item>
    /// <item>席の拠点（建物の拠点）と人の拠点が食い違えば修正依頼（site_mismatch）にする（承認は止めない。どちらを直すかは人が判断する）</item>
    /// </list>
    /// </remarks>
    private void WorkplaceAssignments(IReadOnlyList<NormalizedRow> rows)
    {
        foreach (var row in rows)
        {
            AddIssueCorrections(row);
        }

        var planned = new Dictionary<Guid, int>();
        foreach (var row in rows)
        {
            if (row[IngestFields.EmployeeNumber] is null)
            {
                Correction(row, IngestFields.EmployeeNumber, CorrectionIssue.MissingEmployeeNumber, "社員番号がありません（席・持ち場は人に割り当てます）。");
                continue;
            }

            PlanPersonWorkplace(row, planned);
        }
    }

    private void PlanPersonWorkplace(NormalizedRow row, Dictionary<Guid, int> planned)
    {
        if (!TryResolveLegalEntity(row, out var entity))
        {
            return;
        }

        var number = row[IngestFields.EmployeeNumber]!;
        var people = Snapshot.People.Where(p => string.Equals(p.EmployeeNumber, number, StringComparison.OrdinalIgnoreCase) && (entity is null || p.LegalEntityId == entity)).ToList();
        if (people.Count != 1)
        {
            Correction(row, IngestFields.EmployeeNumber, CorrectionIssue.InvalidValue, "社員番号の人が jin-ji にいないか、一人に決まりません（人の取込を先に承認してください）。");
            return;
        }

        var person = people[0];
        var role = WorkplaceCodes.TryParse(row[IngestFields.WorkplaceRole] ?? "primary", out WorkplaceRole parsed) ? parsed : WorkplaceRole.Primary;
        var roleCode = WorkplaceCodes.ToCode(role);
        var current = Workplace.AssignmentsOf(person.Id).Where(a => a.Role == role).ToList();
        var start = Date(row[IngestFields.WorkplaceValidFrom]) ?? _context.EffectiveDate;

        if (row[IngestFields.SeatCode] is null && row[IngestFields.ZoneName] is null)
        {
            if (Date(row[IngestFields.WorkplaceEndDate]) is { } last)
            {
                if (current.Count == 0)
                {
                    Correction(row, IngestFields.WorkplaceEndDate, CorrectionIssue.InvalidValue, "終える割り当てが jin-ji にありません。");
                    return;
                }

                Add(row, ProposalTargetKind.WorkplaceAssignment, ProposalOperation.End, person.Id, last.AddDays(1), Payload((ProposalFields.Role, roleCode)), new(),
                    new ProposalBasis { Note = "席の割り当てを最終日の翌日で終えます。" }, CreateConfidence);
            }
            else
            {
                Correction(row, IngestFields.SeatCode, CorrectionIssue.MissingRequired, "席番号かゾーンの名前（または割り当ての最終日）がありません。");
            }

            return;
        }

        if (ResolveTarget(row) is not { } target)
        {
            return;
        }

        var (building, floor, seat, zone) = target;
        if (seat is { Capacity: > 1 } station)
        {
            var count = Workplace.AssignmentsOfSeat(station.SeatId).Count(a => a.PersonId != person.Id) + planned.GetValueOrDefault(station.SeatId);
            if (count + 1 > station.Capacity)
            {
                Correction(row, IngestFields.SeatCode, CorrectionIssue.InvalidValue, $"持ち場の定員（{station.Capacity} 人）を超えます。");
                return;
            }
        }

        if (seat is not null)
        {
            planned[seat.SeatId] = planned.GetValueOrDefault(seat.SeatId) + 1;
        }

        // 席の拠点と人の拠点の食い違い（承認は止めない）
        if (Snapshot.SiteOf(person.Id, person.Current) is { } personSite && personSite.Site.Id != building.SiteId)
        {
            Correction(row, IngestFields.SeatCode, CorrectionIssue.SiteMismatch, "席の建物の拠点と、人の拠点が違います。席と拠点のどちらが正しいか確かめてください（席の提案は作ります）。");
        }

        var exclusive = seat?.IsExclusive ?? false;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ProposalFields.FloorId] = Id(floor.Id),
            [ProposalFields.SeatId] = seat is null ? null : Id(seat.SeatId),
            [ProposalFields.ZoneKey] = seat is null ? Id(zone!.ZoneKey) : null,
            [ProposalFields.Exclusive] = exclusive ? "true" : "false",
        };
        var occupied = exclusive && Workplace.AssignmentsOfSeat(seat!.SeatId).Any(a => a.PersonId != person.Id);
        var basis = new ProposalBasis
        {
            Note = occupied ? "この席は今、別の人に割り当てられています。先にその人の席を変えるか終えてから承認してください。" : null,
        };

        if (role == WorkplaceRole.Secondary)
        {
            if (current.Any(a => a.SeatId == seat?.SeatId && a.ZoneKey == (seat is null ? zone!.ZoneKey : null)))
            {
                return;
            }

            Add(row, ProposalTargetKind.WorkplaceAssignment, ProposalOperation.Create, person.Id, start ?? _context.Today,
                Payload([.. values.Select(v => (v.Key, v.Value)), (ProposalFields.Role, roleCode), (ProposalFields.ValidFrom, Iso(start ?? _context.Today))]), new(), basis, CreateConfidence);
            return;
        }

        if (current.FirstOrDefault() is not { } primary)
        {
            Add(row, ProposalTargetKind.WorkplaceAssignment, ProposalOperation.Create, person.Id, start ?? _context.Today,
                Payload([.. values.Select(v => (v.Key, v.Value)), (ProposalFields.Role, roleCode), (ProposalFields.ValidFrom, Iso(start ?? _context.Today))]), new(), basis, CreateConfidence);
            return;
        }

        var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        void Compare(string key, string? old)
        {
            if (!string.Equals(old, values[key], StringComparison.Ordinal))
            {
                diff[key] = new FieldChange(old, values[key]);
            }
        }

        Compare(ProposalFields.FloorId, Id(primary.FloorId));
        Compare(ProposalFields.SeatId, primary.SeatId is { } s ? Id(s) : null);
        Compare(ProposalFields.ZoneKey, primary.ZoneKey is { } z ? Id(z) : null);
        Compare(ProposalFields.Exclusive, primary.Exclusive ? "true" : "false");
        if (diff.Count > 0)
        {
            Add(row, ProposalTargetKind.WorkplaceAssignment, start is null ? ProposalOperation.Correct : ProposalOperation.Change, person.Id, start, new(), diff, basis, CreateConfidence);
        }
    }

    /// <summary>
    /// 行の席・ゾーンを今日の配置図に照合する（建物は拠点・建物コード、階は表示名、席は席番号、ゾーンは名前。階を省くと全階で一意なとき）。
    /// 当たらなければ修正依頼にして null。
    /// </summary>
    private (Building Building, Floor Floor, LayoutSeat? Seat, LayoutZone? Zone)? ResolveTarget(NormalizedRow row)
    {
        var floors = Workplace.Floors.AsEnumerable();
        if (row[IngestFields.BuildingCode] is { } code)
        {
            var buildings = Workplace.Buildings.Where(b => string.Equals(b.Code, code, StringComparison.OrdinalIgnoreCase)
                && (row[IngestFields.SiteCode] is not { } site || string.Equals(Snapshot.FindSite(b.SiteId)?.Code, site, StringComparison.OrdinalIgnoreCase))).ToList();
            if (buildings.Count != 1)
            {
                Correction(row, IngestFields.BuildingCode, CorrectionIssue.UnknownBuilding, "建物が jin-ji に見つからないか、一意に決まりません（拠点コードも指定してください）。");
                return null;
            }

            floors = floors.Where(f => f.BuildingId == buildings[0].Id);
        }

        if (row[IngestFields.FloorLabel] is { } label)
        {
            floors = floors.Where(f => f.Label == label);
        }

        var candidates = floors.ToList();
        if (row[IngestFields.SeatCode] is { } seatCode)
        {
            var hits = candidates
                .Select(f => (Floor: f, Seat: Workplace.LayoutOf(f.Id)?.Seats.FirstOrDefault(s => s.Code == seatCode)))
                .Where(h => h.Seat is not null)
                .ToList();
            if (hits.Count != 1)
            {
                Correction(row, IngestFields.SeatCode, CorrectionIssue.UnknownSeat, "席番号がその階の配置図に見つからないか、一意に決まりません（建物・階も指定してください）。");
                return null;
            }

            var (floor, seat) = hits[0];
            return (Workplace.FindBuilding(floor.BuildingId)!, floor, seat, seat!.ZoneKey is { } z ? Workplace.FindZone(floor.Id, z) : null);
        }

        if (candidates.Count != 1)
        {
            Correction(row, IngestFields.FloorLabel, CorrectionIssue.UnknownFloor, "階が jin-ji に見つからないか、一意に決まりません（建物・階を指定してください）。");
            return null;
        }

        var only = candidates[0];
        if (Workplace.LayoutOf(only.Id)?.Zones.FirstOrDefault(z => z.Name == row[IngestFields.ZoneName]) is not { } zone)
        {
            Correction(row, IngestFields.ZoneName, CorrectionIssue.UnknownZone, "ゾーン（エリア）がその階の配置図にありません。");
            return null;
        }

        return (Workplace.FindBuilding(only.BuildingId)!, only, null, zone);
    }
}
