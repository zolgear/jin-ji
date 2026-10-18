using System.Text;
using JinJi.Application.Ingest;
using JinJi.Connectors;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Workplace;
using JinJi.Ingest;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>
/// 建物・フロアモードの取込（0019。仕様 §4）: 配置図の JSON・席の CSV（改訂の起案 → 別人の承認、有効日での切り替え、席の同一性）、
/// 割り当ての CSV（人の席・持ち場のゾーン・終了・拠点の食い違い）、形の検査と修正依頼、排他制約による承認時の衝突。
/// </summary>
public sealed class WorkplaceIngestTests : IAsyncLifetime
{
    private const string LayoutMapping = """
        {
          "name": "floor-layout-json",
          "target": "floor_layouts",
          "source": { "kind": "layout_json" },
          "fields": {
            "site_code": ["site_code"], "building_code": ["building_code"], "building_name": ["building_name"], "building_sort_order": ["building_sort_order"],
            "floor_label": ["floor_label"], "floor_level_order": ["floor_level_order"], "floor_kind": ["floor_kind"],
            "layout_valid_from": ["layout_valid_from"], "layout_grid_mm": ["layout_grid_mm"], "layout_element": ["layout_element"],
            "layout_name": ["layout_name"], "layout_kind": ["layout_kind"], "layout_polygon": ["layout_polygon"],
            "seat_code": ["seat_code"], "seat_x_mm": ["seat_x_mm"], "seat_y_mm": ["seat_y_mm"], "seat_rotation": ["seat_rotation"],
            "seat_desk_type": ["seat_desk_type"], "seat_kind": ["seat_kind"], "seat_capacity": ["seat_capacity"], "seat_features": ["seat_features"],
            "seat_zone": ["seat_zone"], "seat_out_of_service": ["seat_out_of_service"]
          }
        }
        """;

    private const string SeatCsvMapping = """
        {
          "name": "seats-csv",
          "target": "floor_layouts",
          "fields": {
            "site_code": ["拠点"], "building_code": ["建物"], "floor_label": ["階"], "layout_element": ["要素"], "layout_valid_from": ["有効日"],
            "seat_code": ["席番号"], "seat_x_mm": ["X"], "seat_y_mm": ["Y"], "seat_kind": ["種類"], "seat_zone": ["ゾーン"]
          },
          "defaults": { "layout_element": "seat" }
        }
        """;

    private const string AssignmentMapping = """
        {
          "name": "seat-assignments",
          "target": "workplace_assignments",
          "legalEntityCode": "A",
          "fields": {
            "employee_number": ["社員番号"], "building_code": ["建物"], "floor_label": ["階"], "seat_code": ["席番号"],
            "zone_name": ["エリア"], "workplace_role": ["主副"], "workplace_valid_from": ["開始日"], "workplace_end_date": ["最終日"]
          }
        }
        """;

    private IngestDatabase _db = null!;
    private Guid _site;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => IngestDatabase.Today;

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        _site = await InsertSiteAsync("S10", "東京本社");
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact(DisplayName = "配置図の JSON: 建物・階・改訂を提案し、別の人事管理者の承認で公開側に出る。席番号の全角・ハイフンの揺れをそろえ、形の不正・健康に関わるタグは修正依頼")]
    public async Task LayoutJson_CreatesBuildingFloorAndLayout()
    {
        var result = await ImportJsonAsync(LayoutJson());
        var proposals = await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        Assert.Equal(
            [ProposalTargetKind.Building, ProposalTargetKind.Floor, ProposalTargetKind.FloorLayout],
            proposals.Where(p => p.Status == ProposalStatus.Proposed).Select(p => p.TargetKind).Order());
        var corrections = await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        Assert.Contains(corrections, c => c.FieldKey == IngestFields.LayoutPolygon && c.Issue == CorrectionIssue.InvalidValue);
        Assert.Contains(corrections, c => c.FieldKey == IngestFields.SeatFeatures && c.Issue == CorrectionIssue.InvalidValue);
        Assert.Contains(corrections, c => c.FieldKey == IngestFields.SeatXMm && c.Issue == CorrectionIssue.InvalidValue);
        // フリーアドレスの席・ゾーンは持たない（席の種類は固定席と持ち場だけ）
        Assert.Contains(corrections, c => c.FieldKey == IngestFields.SeatKind && c.Issue == CorrectionIssue.InvalidValue);

        // 取込の実行者は承認できない。別の人事管理者が依存の順（建物 → 階 → 改訂）に承認する
        var layout = proposals.Single(p => p.TargetKind == ProposalTargetKind.FloorLayout);
        await Assert.ThrowsAsync<Application.AccessDeniedException>(() => _db.Review.ApproveAsync(_db.HrUser("hr1"), layout.Id, Ct));
        await Assert.ThrowsAsync<ProposalConflictException>(() => _db.Review.ApproveAsync(_db.HrUser("hr2"), layout.Id, Ct));
        await ApproveAllAsync(result);

        var map = (await SnapshotAsync()).Workplace;
        var building = Assert.Single(map.BuildingsOf(_site));
        Assert.Equal(("MAIN", "本館"), (building.Code, building.Name));
        var floor = Assert.Single(map.FloorsOf(building.Id));
        Assert.Equal(("3F", 30, FloorKind.Office), (floor.Label, floor.LevelOrder, floor.Kind));
        var current = map.LayoutOf(floor.Id)!;
        Assert.Equal(["3F-A01", "3F-A02", "3F-B01"], current.Seats.Select(s => s.Code));
        Assert.Equal(["3F 東", "3F 西"], current.Zones.Select(z => z.Name).Order(StringComparer.Ordinal));
        var west = current.Zones.Single(z => z.Name == "3F 西").ZoneKey;
        Assert.Equal(west, current.Seats.Single(s => s.Code == "3F-A01").ZoneKey);
        Assert.Equal(["monitor"], current.Seats.Single(s => s.Code == "3F-B01").Features);
        Assert.Single(current.Fixtures);

        // 起案者・承認者が改訂に残る
        Assert.Equal(
            [$"{_db.Id("hr1")}:{_db.Id("hr2")}"],
            await _db.QueryAsync<string>(null, "SELECT proposed_by_person_id || ':' || approved_by_person_id FROM floor_layout WHERE floor_id = $1", Ct, floor.Id));

        // 同じ配置図を取り込み直しても提案は作らない
        var again = await ImportJsonAsync(LayoutJson());
        Assert.DoesNotContain(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), again.BatchId, Ct), p => p.Status == ProposalStatus.Proposed);
    }

    [Fact(DisplayName = "席の CSV: 今の配置図の外形・ゾーンを引き継いで席だけを置き換える改訂。有効日で今の改訂を区切り、席番号が同じなら同じ席")]
    public async Task SeatCsv_RevisesFromEffectiveDate()
    {
        await ApproveAllAsync(await ImportJsonAsync(LayoutJson()));
        var before = (await SnapshotAsync()).Workplace;
        var floor = before.Floors.Single();
        var a01 = before.LayoutOf(floor.Id)!.Seats.Single(s => s.Code == "3F-A01").SeatId;
        var effective = Today.AddDays(20);

        var result = await ImportCsvAsync(
            $"""
            拠点,建物,階,有効日,席番号,X,Y,種類,ゾーン
            S10,MAIN,3F,{effective:yyyy-MM-dd},3F-A01,2500,2000,fixed,3F 西
            S10,MAIN,3F,{effective:yyyy-MM-dd},3f−a09,5000,2000,fixed,3F 西
            """,
            SeatCsvMapping);
        var proposal = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct), p => p.Status == ProposalStatus.Proposed);
        Assert.Equal((ProposalTargetKind.FloorLayout, effective), (proposal.TargetKind, proposal.EffectiveDate));
        Assert.Contains("外形・固定の部分・ゾーンは今の配置図を引き継ぎます", proposal.Basis.Note, StringComparison.Ordinal);
        await _db.Review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct);

        // 今日は元の改訂（区切った写し）、有効日からは新しい改訂。A01 は同じ席のまま動き、A02・B01 は無くなる
        var today = (await SnapshotAsync()).Workplace.LayoutOf(floor.Id)!;
        Assert.Equal(["3F-A01", "3F-A02", "3F-B01"], today.Seats.Select(s => s.Code));
        Assert.Equal(effective, today.ValidUntil);
        var future = (await _db.Snapshots.LoadAsync(_db.HrUser("hr2"), effective, effective, Ct)).Workplace.LayoutOf(floor.Id)!;
        Assert.Equal(["3F-A01", "3F-A09"], future.Seats.Select(s => s.Code));
        Assert.Equal((a01, 2500), (future.Seats[0].SeatId, future.Seats[0].XMm));
        Assert.Equal(today.Zones.Select(z => z.ZoneKey).Order(), future.Zones.Select(z => z.ZoneKey).Order());

        // 配置図の無い階に席だけの CSV は作れない
        var orphan = await ImportCsvAsync("拠点,建物,階,席番号,X,Y\nS10,MAIN,9F,9F-A01,100,100\n", SeatCsvMapping);
        Assert.Contains(await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), orphan.BatchId, Ct), c => c.Issue == CorrectionIssue.MissingRequired);
    }

    [Fact(DisplayName = "割り当ての CSV: 人の席・持ち場のゾーン・変更（有効日）・終了。社員番号の無い行・無い席は修正依頼、席の拠点と人の拠点の食い違いは承認を止めずに修正依頼")]
    public async Task Assignments_PlanAndApply()
    {
        await ApproveAllAsync(await ImportJsonAsync(LayoutJson()));
        var other = await InsertSiteAsync("S20", "大阪支社");
        await ImportAndApproveOrgSiteAsync("D1", "S20");

        var result = await ImportCsvAsync(
            """
            社員番号,建物,階,席番号,エリア,主副,開始日,最終日
            E0002,MAIN,3F,３Ｆ－Ａ０１,,,,
            E9001,,,,3F 東,,,
            ,MAIN,3F,,3F 東,,,
            E9002,MAIN,3F,3F-Z99,,,,
            """,
            AssignmentMapping);
        var corrections = await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        Assert.Contains(corrections, c => c.Issue == CorrectionIssue.UnknownSeat && c.SourceRowNumber == 5);
        // 社員番号の無い行（組織単位のエリア）は扱わない
        Assert.Contains(corrections, c => c.Issue == CorrectionIssue.MissingEmployeeNumber && c.SourceRowNumber == 4);
        // takahashi（E0002）は第一本部（大阪支社）の配下なので、東京本社の席と拠点が食い違う
        Assert.Contains(corrections, c => c.Issue == CorrectionIssue.SiteMismatch && c.SourceRowNumber == 2);
        Assert.DoesNotContain(corrections, c => c.Issue == CorrectionIssue.SiteMismatch && c.SourceRowNumber == 3);
        Assert.Equal(other, (await SnapshotAsync()).SiteOf(_db.Id("takahashi"), (await SnapshotAsync()).FindPerson(_db.Id("takahashi"))!.Current)!.Site.Id);

        await ApproveAllAsync(result);
        var snapshot = await SnapshotAsync();
        Assert.Equal("3F-A01", Assert.Single(snapshot.SeatsOf(_db.Id("takahashi"))).Seat.Code);
        Assert.Equal(("3F 東", WorkplaceSource.Zone), (snapshot.WorkplaceOf(_db.Id("hr1"))!.Zone!.Name, snapshot.WorkplaceOf(_db.Id("hr1"))!.Source));
        Assert.Null(snapshot.WorkplaceOf(_db.Id("hr2")));

        // 席替え（有効日つき）と終了
        var move = Today.AddDays(5);
        var changes = await ImportCsvAsync(
            $"""
            社員番号,建物,階,席番号,開始日,最終日
            E0002,MAIN,3F,3F-A02,{move:yyyy-MM-dd},
            E9001,,,,,{Today.AddDays(2):yyyy-MM-dd}
            """,
            AssignmentMapping);
        var planned = (await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), changes.BatchId, Ct)).Where(p => p.Status == ProposalStatus.Proposed).ToList();
        Assert.Equal([ProposalOperation.Change, ProposalOperation.End], planned.Select(p => p.Operation).Order());
        await ApproveAllAsync(changes);
        Assert.Equal("3F-A01", Assert.Single((await SnapshotAsync()).SeatsOf(_db.Id("takahashi"))).Seat.Code);
        var later = await _db.Snapshots.LoadAsync(_db.HrUser("hr2"), move, move, Ct);
        Assert.Equal("3F-A02", Assert.Single(later.SeatsOf(_db.Id("takahashi"))).Seat.Code);
        Assert.Null(later.WorkplaceOf(_db.Id("hr1")));

        // 変更フィード: 席の割り当ての変更は人の変更として届く（将来の席替えは見せてよい日まで人事管理者だけ）
        Assert.Contains(
            await _db.QueryAsync<DateOnly>(null, "SELECT effective_date FROM change_event WHERE entity_type = 'person' AND entity_id = $1", Ct, _db.Id("takahashi")),
            d => d == move);
        Assert.Empty(await _db.QueryWithAsync<Guid>(_db.User("member"), "SELECT entity_id FROM change_event WHERE entity_id = $1 AND effective_date = $2", Ct, _db.Id("takahashi"), move));
    }

    [Fact(DisplayName = "承認時の衝突: 同じ定員 1 の席に 2 人を割り当てる提案は、後の承認が排他制約で失敗する")]
    public async Task Assignments_ExclusiveSeatConflict()
    {
        await ApproveAllAsync(await ImportJsonAsync(LayoutJson()));
        var result = await ImportCsvAsync("社員番号,建物,階,席番号\nE0002,MAIN,3F,3F-A02\nE0005,MAIN,3F,3F-A02\n", AssignmentMapping);
        var proposals = (await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct)).Where(p => p.Status == ProposalStatus.Proposed).ToList();
        Assert.Equal(2, proposals.Count);
        await _db.Review.ApproveAsync(_db.HrUser("hr2"), proposals[0].Id, Ct);
        var conflict = await Assert.ThrowsAsync<ProposalConflictException>(() => _db.Review.ApproveAsync(_db.HrUser("hr2"), proposals[1].Id, Ct));
        Assert.Contains("workplace_assignment_seat_no_overlap", conflict.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "取込の入口: 配置図の取込に人の属性は書けず、人の取込に席は書けない。割り当ての取込に組織は書けない（組織単位のエリアは持たない）")]
    public void Mapping_TargetsAreSeparated()
    {
        Assert.Throws<FormatException>(() => ConnectorMapping.Parse(Encoding.UTF8.GetBytes(
            """{ "name": "x", "target": "floor_layouts", "fields": { "seat_code": ["席"], "入館時刻": ["入館"] } }""")));
        Assert.Throws<FormatException>(() => ConnectorMapping.Parse(Encoding.UTF8.GetBytes(
            """{ "name": "x", "target": "floor_layouts", "fields": { "employee_number": ["社員番号"] } }""")));
        Assert.Throws<FormatException>(() => ConnectorMapping.Parse(Encoding.UTF8.GetBytes(
            """{ "name": "x", "target": "people", "fields": { "seat_code": ["席"] } }""")));
        Assert.Throws<FormatException>(() => ConnectorMapping.Parse(Encoding.UTF8.GetBytes(
            """{ "name": "x", "target": "workplace_assignments", "fields": { "employee_number": ["社員番号"], "org_code": ["組織コード"] } }""")));
    }

    [Fact(DisplayName = "サンプル: messy-corp の配置図（3 拠点・4 棟・10 階）を取り込み、承認すると席が公開側に出る。健康に関わるタグは修正依頼")]
    public async Task Sample_FloorLayouts()
    {
        await InsertSiteAsync("S010", "東京本社");
        await InsertSiteAsync("S020", "横浜工場");
        await InsertSiteAsync("LS10", "千葉物流センター");
        var sample = await File.ReadAllTextAsync(Path.Combine(RepositoryPaths.Root, "samples", "messy-corp", "floor_layouts.json"), Ct);
        var result = await ImportJsonAsync(sample);
        var proposals = (await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct)).Where(p => p.Status == ProposalStatus.Proposed).ToList();
        Assert.Equal((4, 10, 8), (
            proposals.Count(p => p.TargetKind == ProposalTargetKind.Building),
            proposals.Count(p => p.TargetKind == ProposalTargetKind.Floor),
            proposals.Count(p => p.TargetKind == ProposalTargetKind.FloorLayout)));
        var corrections = await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        Assert.Equal([IngestFields.SeatFeatures], corrections.Select(c => c.FieldKey));

        await ApproveAllAsync(result);
        var map = (await SnapshotAsync()).Workplace;
        Assert.Equal(10, map.Floors.Count);
        var third = map.Floors.Single(f => f.Label == "3F");
        Assert.Contains(map.LayoutOf(third.Id)!.Seats, s => s.Code == "3F-A05");
        Assert.All(map.Floors.Select(f => map.LayoutOf(f.Id)).OfType<FloorLayout>().SelectMany(l => l.Seats), s => Assert.DoesNotContain("wheelchair", s.Features));
    }

    private static string LayoutJson() => """
        {
          "format": "jinji-floor-layout/v1",
          "site_code": "S10",
          "buildings": [{
            "code": "main", "name": "本館",
            "floors": [{
              "label": "３ｆ", "level_order": 30, "kind": "事務所",
              "layout": {
                "grid_mm": 100,
                "outline": [[0, 0], [40000, 0], [40000, 20000], [0, 20000]],
                "fixtures": [{ "kind": "core", "name": "コア", "polygon": [[18000, 9000], [22000, 9000], [22000, 11000], [18000, 11000]] }],
                "zones": [
                  { "name": "3F 西", "kind": "office", "polygon": [[0, 0], [20000, 0], [20000, 20000], [0, 20000]] },
                  { "name": "3F 東", "kind": "事務所", "polygon": [[20000, 0], [40000, 0], [40000, 20000], [20000, 20000]] },
                  { "name": "交差", "kind": "office", "polygon": [[0, 0], [1000, 1000], [1000, 0], [0, 1000]] }
                ],
                "seats": [
                  { "code": "３Ｆ－Ａ０１", "x_mm": 2000, "y_mm": 2000, "kind": "fixed", "zone": "3F 西" },
                  { "code": "3F-A02", "x_mm": 4000, "y_mm": 2000, "kind": "fixed", "zone": "3F 西" },
                  { "code": "3F-B01", "x_mm": 22000, "y_mm": 2000, "kind": "固定席", "zone": "3F 東", "features": ["monitor"] },
                  { "code": "3F-B02", "x_mm": 24000, "y_mm": 2000, "kind": "fixed", "zone": "3F 東", "features": ["wheelchair"] },
                  { "code": "3F-B03", "x_mm": 26000, "y_mm": 2000, "kind": "フリーアドレス", "zone": "3F 東" },
                  { "code": "3F-X01", "x_mm": 45000, "y_mm": 2000, "kind": "fixed" }
                ]
              }
            }]
          }]
        }
        """;

    private Task<ImportResult> ImportJsonAsync(string json)
    {
        var mapping = ConnectorMapping.Parse(Encoding.UTF8.GetBytes(LayoutMapping));
        var bytes = Encoding.UTF8.GetBytes(json);
        return _db.Importer.ImportAsync(
            IngestDatabase.Worker,
            new ImportRequest
            {
                Table = SourceReader.Read(bytes, "layout.json", mapping),
                Profile = mapping.Profile,
                FileName = "layout.json",
                FileSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                MappingName = mapping.Name,
                MappingSha256 = mapping.Sha256,
                ExecutedByPersonId = _db.Id("hr1"),
            },
            Ct);
    }

    private Task<ImportResult> ImportCsvAsync(string csv, string mapping) => _db.ImportCsvAsync(csv, mapping, Ct);

    /// <summary>起案中の提案を、依存（DependsOn）の順に別の人事管理者（hr2）が承認する。</summary>
    private async Task ApproveAllAsync(ImportResult result)
    {
        var pending = (await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct)).Where(p => p.Status == ProposalStatus.Proposed).ToList();
        var done = new HashSet<Guid>();
        while (pending.Count > 0)
        {
            var ready = pending.Where(p => p.Basis.DependsOn.All(done.Contains) || p.Basis.DependsOn.All(d => !pending.Any(x => x.Id == d))).ToList();
            Assert.NotEmpty(ready);
            foreach (var proposal in ready)
            {
                await _db.Review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct);
                done.Add(proposal.Id);
                pending.Remove(proposal);
            }
        }
    }

    private async Task ImportAndApproveOrgSiteAsync(string org, string site)
    {
        const string mapping = """{ "name": "org-sites", "target": "org_units", "legalEntityCode": "A", "fields": { "org_code": ["組織コード"], "site_code": ["拠点コード"] } }""";
        await ApproveAllAsync(await _db.ImportCsvAsync($"組織コード,拠点コード\n{org},{site}\n", mapping, Ct));
    }

    private Task<OrgSnapshot> SnapshotAsync() => _db.Snapshots.LoadAsync(_db.HrUser("hr2"), Today, Today, Ct);

    private async Task<Guid> InsertSiteAsync(string code, string name)
    {
        var id = EntityId.New();
        await _db.ExecuteAsync(_db.HrUser("hr2"), "INSERT INTO site (id, legal_entity_id) VALUES ($1, $2)", Ct, id, _db.EntityA);
        await _db.ExecuteAsync(
            _db.HrUser("hr2"),
            "INSERT INTO site_version (id, site_id, legal_entity_id, code, name, site_type, valid_period) VALUES ($1, $2, $3, $4, $5, 'head_office', daterange($6, NULL))",
            Ct,
            EntityId.New(),
            id,
            _db.EntityA,
            code,
            name,
            IngestDatabase.LongAgo);
        return id;
    }
}
