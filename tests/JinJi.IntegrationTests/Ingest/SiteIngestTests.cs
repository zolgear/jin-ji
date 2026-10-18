using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Ingest;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>
/// 拠点（0017）: 拠点マスタの取込・組織と人の拠点の照合・リモートワークの揺れ・勤務地の照合（移行）・変更フィード・権限を、実 PostgreSQL で確かめる。
/// </summary>
public sealed class SiteIngestTests : IAsyncLifetime
{
    private const string SiteMapping = """
        {
          "name": "sites-csv",
          "target": "sites",
          "legalEntityCode": "A",
          "fields": {
            "site_code": ["拠点コード"],
            "site_name": ["拠点名"],
            "site_type": ["種別"],
            "site_location": ["所在地"],
            "site_valid_from": ["開設日"],
            "site_closed_date": ["閉鎖日"]
          }
        }
        """;

    private const string PeopleMapping = """
        {
          "name": "people-sites",
          "target": "people",
          "legalEntityCode": "A",
          "fields": {
            "employee_number": ["社員番号"],
            "site_name": ["勤務地"],
            "remote_work": ["リモート"]
          }
        }
        """;

    private const string OrgMapping = """
        {
          "name": "org-sites",
          "target": "org_units",
          "legalEntityCode": "A",
          "fields": { "org_code": ["組織コード"], "site_code": ["拠点コード"] }
        }
        """;

    private IngestDatabase _db = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _db = await IngestDatabase.CreateAsync(Ct);

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact(DisplayName = "拠点マスタ: 種別の空欄は拠点名から推定し、所在地の都道府県をコードに分ける。変更・閉鎖は承認で版を作り、jin-ji に無い閉鎖は修正依頼")]
    public async Task SiteMaster_CreateChangeClose()
    {
        await ImportAndApproveAsync(
            """
            拠点コード,拠点名,種別,所在地,開設日
            S10,東京本社,本社,東京都千代田区丸の内9-9-1,2001/4/1
            Ｓ２０,大阪支店,,大阪府大阪市北区梅田9-9-3,R3.4.1
            """,
            SiteMapping);
        var sites = (await SnapshotAsync()).Sites.OrderBy(s => s.Code, StringComparer.Ordinal).ToList();
        Assert.Equal(
            [("S10", "東京本社", SiteType.HeadOffice, "13", "千代田区丸の内9-9-1"), ("S20", "大阪支店", SiteType.Branch, "27", "大阪市北区梅田9-9-3")],
            sites.Select(s => (s.Code, s.Name, s.Type, s.PrefectureCode, s.Location)));

        // 名称の変更は訂正（版の置き換え）、閉鎖は閉鎖日の翌日で有効期間を終える。jin-ji に無い拠点の閉鎖は修正依頼
        var result = await ImportAndApproveAsync(
            $"""
            拠点コード,拠点名,種別,所在地,開設日,閉鎖日
            S10,東京本社ビル,本社,東京都千代田区丸の内9-9-1,,
            S20,大阪支店,,,,{IngestDatabase.Today.AddDays(-1):yyyy-MM-dd}
            S99,川崎倉庫,倉庫,,,2025/3/31
            """,
            SiteMapping);
        var after = await SnapshotAsync();
        Assert.Equal(["東京本社ビル"], after.Sites.Select(s => s.Name));
        var corrections = await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        Assert.Contains(corrections, c => c.FieldKey == IngestFields.SiteClosedDate && c.SourceRowNumber == 4);

        // 閉鎖済みの拠点は、閉鎖前の時点には残る
        var before = await _db.Snapshots.LoadAsync(_db.HrUser("hr2"), IngestDatabase.Today.AddDays(-1), IngestDatabase.Today, Ct);
        Assert.Contains(before.Sites, s => s.Code == "S20");
    }

    [Fact(DisplayName = "人と組織の拠点: 組織の拠点は配下が引き継ぎ、人ごとの拠点は組織の拠点と違う人だけが持つ。表記ゆれは照合し、拠点マスタに無い勤務地は修正依頼")]
    public async Task PeopleAndOrgSites_AreReconciled()
    {
        await ImportAndApproveAsync("拠点コード,拠点名,所在地\nS10,東京本社,東京都千代田区\nS20,大阪支社,大阪府大阪市\n", SiteMapping);
        await ImportAndApproveAsync("組織コード,拠点コード\nD1,S10\n", OrgMapping);

        var result = await ImportAndApproveAsync(
            """
            社員番号,勤務地,リモート
            E0001,本社（東京）,○
            E0002,大阪支社(梅田),×
            E0005,札幌出張所,はい
            E9001,,
            """,
            PeopleMapping);

        var snapshot = await SnapshotAsync();
        string? SiteCode(string key) => snapshot.SiteOf(_db.Id(key), snapshot.FindPerson(_db.Id(key))!.Current)?.Site.Code;
        var tokyo = snapshot.Sites.Single(s => s.Code == "S10");
        Assert.Equal(tokyo.Id, snapshot.FindUnit(_db.Id("D1"))!.SiteId);
        Assert.Equal(new ResolvedSite(tokyo, true), snapshot.SiteOfUnit(_db.Id("S1")));

        // head は組織（第一本部配下）と同じ拠点なので人ごとの拠点を持たない。takahashi は大阪支社を人ごとに持つ
        Assert.Null(snapshot.FindPerson(_db.Id("head"))!.Current.SiteId);
        Assert.Equal("S10", SiteCode("head"));
        Assert.Equal("S20", SiteCode("takahashi"));
        Assert.False(snapshot.SiteOf(_db.Id("takahashi"), snapshot.FindPerson(_db.Id("takahashi"))!.Current)!.Inherited);

        // 拠点マスタに無い勤務地は修正依頼にして、拠点は組織のまま。リモートワークの揺れはそろえる
        Assert.Equal("S10", SiteCode("member"));
        var corrections = await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        Assert.Contains(corrections, c => c.Issue == CorrectionIssue.UnknownSite && c.SourceRowNumber == 4);
        Assert.Equal(
            [true, false, true, false],
            new[] { "head", "takahashi", "member", "hr1" }.Select(k => snapshot.FindPerson(_db.Id(k))!.Current.RemoteWork));
        Assert.Null(SiteCode("hr1"));

        // 人ごとの拠点が組織の拠点と同じになったら外す（取り込み直し）
        await ImportAndApproveAsync("社員番号,勤務地\nE0002,東京本社\n", PeopleMapping);
        Assert.Null((await SnapshotAsync()).FindPerson(_db.Id("takahashi"))!.Current.SiteId);
    }

    [Fact(DisplayName = "勤務地の照合（移行）: 取込済みの勤務地を拠点マスタに照合し、組織と違う拠点だけ提案し、当たらない値は修正依頼にする")]
    public async Task WorkLocationReconciliation()
    {
        await ImportAndApproveAsync("拠点コード,拠点名\nS10,東京本社\nS20,大阪支社\n", SiteMapping);
        await ImportAndApproveAsync("組織コード,拠点コード\nD1,S10\n", OrgMapping);
        await InsertLegacyPersonAsync("legacy1", "E0101", "S1", "大阪支社");
        await InsertLegacyPersonAsync("legacy2", "E0102", "S1", "東京本社");
        await InsertLegacyPersonAsync("legacy3", "E0103", "S1", "自宅（東京都世田谷区9-9-9）");
        await InsertLegacyPersonAsync("legacy4", "E0104", "S1", "旧本社");

        var reconciler = new WorkLocationReconciler(new Infrastructure.Database.NpgsqlWorkLocationSource(_db.DataSource), _db.Importer, TimeProvider.System);
        await Assert.ThrowsAsync<Application.AccessDeniedException>(() => reconciler.RunAsync(_db.User("member"), null, Ct));
        var result = (await reconciler.RunAsync(IngestDatabase.Worker, _db.Id("hr1"), Ct))!;

        Assert.Equal(4, result.RowCount);
        var proposal = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct));
        Assert.Equal((ProposalTargetKind.PersonVersion, _db.Id("legacy1")), (proposal.TargetKind, proposal.TargetId));
        Assert.Equal([ProposalFields.SiteId], proposal.Diff.Keys);
        var corrections = await _db.Store.ListCorrectionsAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        Assert.Contains(corrections, c => c.Issue == CorrectionIssue.SuspectedSensitiveValue);
        Assert.Contains(corrections, c => c.Issue == CorrectionIssue.UnknownSite);
        Assert.Equal(
            ["work_location"],
            await _db.QueryAsync<string>(null, "SELECT source_kind FROM import_batch WHERE id = $1", Ct, result.BatchId));

        await _db.Review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct);
        var snapshot = await SnapshotAsync();
        Assert.Equal("S20", snapshot.SiteOf(_db.Id("legacy1"), snapshot.FindPerson(_db.Id("legacy1"))!.Current)!.Site.Code);

        // 新しい版には勤務地を書かない（写した版でも NULL）
        Assert.Equal(
            [0L],
            await _db.QueryAsync<long>(null, "SELECT count(*) FROM person_version WHERE person_id = $1 AND upper_inf(recorded_period) AND work_location IS NOT NULL", Ct, _db.Id("legacy1")));
    }

    [Fact(DisplayName = "変更フィード: 拠点の版は拠点・その拠点を使う組織・引き継ぐ主務の所属者の変更として記録し、組織の拠点の変更は所属者にも届く。将来の拠点の取り消しは withdrawn")]
    public async Task ChangeFeed_RecordsSiteDependents()
    {
        await ImportAndApproveAsync("拠点コード,拠点名\nS10,東京本社\n", SiteMapping);
        var mark = await LastChangeAsync();
        await ImportAndApproveAsync("組織コード,拠点コード\nDP1,S10\n", OrgMapping);

        // 組織（第一営業部）の拠点が変わったので、第一営業部と配下（営業一課・二課）の主務の所属者（head・takahashi・member）に人の変更が届く
        var afterOrg = await ChangesAsync(mark);
        Assert.Contains(("org_unit", _db.Id("DP1"), false), afterOrg);
        Assert.Subset(afterOrg.ToHashSet(), new[] { "head", "takahashi", "member" }.Select(k => ("person", _db.Id(k), false)).ToHashSet());
        Assert.DoesNotContain(("person", _db.Id("hr1"), false), afterOrg);

        // 拠点の名称の変更は、拠点・拠点を使う組織（引き継ぐ配下も）・その所属者の変更
        mark = await LastChangeAsync();
        await ImportAndApproveAsync("拠点コード,拠点名\nS10,東京本社ビル\n", SiteMapping);
        var site = (await SnapshotAsync()).Sites.Single().Id;
        var afterSite = await ChangesAsync(mark);
        Assert.Contains(("site", site, false), afterSite);
        Assert.Subset(afterSite.ToHashSet(), new[] { "DP1", "S1", "S2" }.Select(k => ("org_unit", _db.Id(k), false)).ToHashSet());
        Assert.Subset(afterSite.ToHashSet(), new[] { "head", "takahashi", "member" }.Select(k => ("person", _db.Id(k), false)).ToHashSet());
        Assert.DoesNotContain(("org_unit", _db.Id("D1"), false), afterSite);

        // 将来の拠点の版（開設の予定）の記録期間を閉じたら取り消し（withdrawn）が届く
        mark = await LastChangeAsync();
        await ImportAndApproveAsync($"拠点コード,拠点名,開設日\nS30,名古屋支店,{IngestDatabase.Today.AddDays(30):yyyy-MM-dd}\n", SiteMapping);
        await _db.ExecuteAsync(
            _db.HrUser("hr2"),
            "UPDATE site_version SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE code = 'S30' AND upper_inf(recorded_period)",
            Ct);
        var withdrawn = await ChangesAsync(mark);
        Assert.Contains(withdrawn, c => c.Type == "site" && c.Withdrawn);
    }

    [Fact(DisplayName = "権限と RLS: アプリ用ロールは勤務地を新しい行に書けない。将来の拠点の版は人事管理者以外に見えない")]
    public async Task Privileges_AndRowLevelSecurity()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            _db.HrUser("hr2"),
            """
            INSERT INTO person_version (id, person_id, family_name, display_name, search_name, employment_type, hire_date, work_location, valid_period)
            VALUES ($1, $2, '試験', '試験', '試験', 'regular', $3, '東京', daterange($3, NULL))
            """,
            Ct,
            EntityId.New(),
            _db.Id("member"),
            IngestDatabase.Today.AddYears(1)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await ImportAndApproveAsync($"拠点コード,拠点名,開設日\nS10,東京本社,\nS30,名古屋支店,{IngestDatabase.Today.AddDays(30):yyyy-MM-dd}\n", SiteMapping);
        const string count = "SELECT count(*) FROM site_version WHERE upper_inf(recorded_period)";
        Assert.Equal([2L], await _db.QueryAsync<long>(_db.HrUser("hr2"), count, Ct));
        Assert.Equal([1L], await _db.QueryAsync<long>(_db.User("member"), count, Ct));
    }

    private async Task<ImportResult> ImportAndApproveAsync(string csv, string mapping)
    {
        var result = await _db.ImportCsvAsync(csv, mapping, Ct);
        var proposals = (await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct)).Where(p => p.Status == ProposalStatus.Proposed).ToList();
        foreach (var proposal in proposals.OrderBy(p => p.TargetKind is ProposalTargetKind.Site or ProposalTargetKind.OrgUnit ? 0 : 1))
        {
            await _db.Review.ApproveAsync(_db.HrUser("hr2"), proposal.Id, Ct);
        }

        return result;
    }

    private Task<OrgSnapshot> SnapshotAsync() => _db.Snapshots.LoadAsync(_db.HrUser("hr2"), IngestDatabase.Today, IngestDatabase.Today, Ct);

    private async Task<long> LastChangeAsync() =>
        (await _db.QueryAsync<long>(null, "SELECT coalesce(max(id), 0) FROM change_event", Ct)).Single();

    /// <summary>位置 <paramref name="after"/> より後の変更の記録（人事管理者の閲覧者で読む）。</summary>
    private async Task<List<(string Type, Guid Id, bool Withdrawn)>> ChangesAsync(long after)
    {
        await using var connection = await _db.Database.OpenAppAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await Infrastructure.Database.ViewerTransaction.ApplyAsync(connection, transaction, _db.HrUser("hr2"), Ct);
        await using var command = new NpgsqlCommand("SELECT entity_type, entity_id, withdrawn FROM change_event WHERE id > $1 ORDER BY id", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = after });
        var rows = new List<(string Type, Guid Id, bool Withdrawn)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetBoolean(2)));
        }

        return rows;
    }

    /// <summary>0017 より前に取り込んだ人（勤務地の値を持つ版）を所有者で登録する（アプリ用ロールは勤務地を書けないため）。</summary>
    private async Task InsertLegacyPersonAsync(string key, string number, string unit, string workLocation)
    {
        var id = _db.Register(key);
        await using var owner = await _db.Database.OpenOwnerAsync(Ct);
        await SampleData.ActAsHrAdminAsync(owner, Ct);
        await SampleData.ExecuteAsync(owner, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, $3)", Ct, id, _db.EntityA, number);
        await SampleData.ExecuteAsync(
            owner,
            """
            INSERT INTO person_version (id, person_id, family_name, display_name, search_name, employment_type, hire_date, work_location, valid_period)
            VALUES ($1, $2, '移行', '移行', '移行', 'regular', $3, $4, daterange($3, NULL))
            """,
            Ct,
            EntityId.New(),
            id,
            IngestDatabase.LongAgo,
            workLocation);
        await SampleData.ExecuteAsync(
            owner,
            "INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, valid_period) VALUES ($1, $2, $3, 'primary', daterange($4, NULL))",
            Ct,
            EntityId.New(),
            id,
            _db.Id(unit),
            IngestDatabase.LongAgo);
    }
}
