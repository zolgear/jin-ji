using JinJi.Connectors;
using JinJi.Ingest;
using JinJi.Ingest.Normalization;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>
/// サンプル（samples/messy-corp）のファイルを項目マッピングで読めることの軽い確認（DB を使わない）。
/// 取り込みから承認・正解データとの照合までの重い評価は <c>JinJi.Host evaluate-ingest</c> で行う（docs/reports/ingest-accuracy.md）。
/// </summary>
public sealed class SampleSmokeTests
{
    private static string Sample(string name) => Path.Combine(RepositoryPaths.Root, "samples", "messy-corp", name);

    public static TheoryData<string, string, int> Files => new()
    {
        { "org_chart.json", "org_chart_2026-04.csv", 71 },
        { "employees_hr_package.json", "employees_hr_package.csv", 350 },
        { "jirei_2026.json", "jirei_2026.csv", 108 },
        { "entra_users.json", "entra_users.json", 347 },
        { "sites.json", "sites.csv", 8 },
        { "floor_layouts.json", "floor_layouts.json", 380 },
        { "seat_assignments.json", "seat_assignments.csv", 233 },
    };

    [Theory(DisplayName = "サンプル: 項目マッピングで読め、行数が正解データと一致し、値は許可リストの取込項目だけ")]
    [MemberData(nameof(Files))]
    public async Task Sample_ReadsWithMapping(string mappingFile, string file, int rows)
    {
        var mapping = ConnectorMapping.Parse(await File.ReadAllBytesAsync(Sample(Path.Combine("mappings", mappingFile)), TestContext.Current.CancellationToken));

        var table = SourceReader.Read(await File.ReadAllBytesAsync(Sample(file), TestContext.Current.CancellationToken), file, mapping);

        Assert.Equal(rows, table.Rows.Count);
        Assert.All(table.Rows, r => Assert.All(r.Values.Keys, k => Assert.Contains(k, IngestFields.Mappable)));
        Assert.DoesNotContain(table.DroppedColumns, d => d.Kind == Application.Ingest.DroppedColumnKind.Unmapped);
    }

    [Fact(DisplayName = "サンプル: 人事パッケージの保持禁止列はすべて捨て、旧姓使用者は通称を氏名にして戸籍名の読みを捨てる")]
    public async Task Sample_HrPackageDropsForbiddenColumns()
    {
        var mapping = ConnectorMapping.Parse(await File.ReadAllBytesAsync(Sample("mappings/employees_hr_package.json"), TestContext.Current.CancellationToken));

        var table = SourceReader.Read(await File.ReadAllBytesAsync(Sample("employees_hr_package.csv"), TestContext.Current.CancellationToken), "employees_hr_package.csv", mapping);

        Assert.Equal(
            ["生年月日", "性別", "郵便番号", "住所", "私用電話番号", "扶養人数", "配偶者の有無", "等級", "号俸", "休職理由", "最新人事評価"],
            table.DroppedColumns.Where(d => d.Kind == Application.Ingest.DroppedColumnKind.Forbidden).Select(d => d.Name));
        var maiden = table.Rows.Single(r => r.Values[IngestFields.EmployeeNumber] == "L20");
        Assert.Equal("酒井　直子", maiden.Values[IngestFields.FullName]);
        Assert.False(maiden.Values.ContainsKey(IngestFields.FullNameKana));
    }

    [Fact(DisplayName = "サンプル: 組織図の階層を補って 71 組織にし、Entra の人ではないアカウントを 8 件判別する")]
    public async Task Sample_OrgChartAndDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = ConnectorMapping.Parse(await File.ReadAllBytesAsync(Sample("mappings/org_chart.json"), ct));
        var orgTable = SourceReader.Read(await File.ReadAllBytesAsync(Sample("org_chart_2026-04.csv"), ct), "org_chart_2026-04.csv", org);
        Assert.Equal(71, orgTable.Rows.Count(r => r.Values.ContainsKey(IngestFields.OrgName)));
        Assert.Equal(["P", "L"], orgTable.Rows.Select(r => org.Profile.ResolveLegalEntityCode(r.Values[IngestFields.LegalEntityCode])).Distinct());
        Assert.Contains(orgTable.Rows, r => r.Values[IngestFields.OrgName] == "神戸倉庫ｸﾞﾙｰﾌﾟ" && r.Values[IngestFields.ParentOrgName] == "西日本物流部");

        var entra = ConnectorMapping.Parse(await File.ReadAllBytesAsync(Sample("mappings/entra_users.json"), ct));
        var users = SourceReader.Read(await File.ReadAllBytesAsync(Sample("entra_users.json"), ct), "entra_users.json", entra);
        var normalizer = new RowNormalizer(entra.Profile);
        Assert.Equal(8, users.Rows.Count(r => ProposalPlanner.IsNonPerson(normalizer.Normalize(Guid.NewGuid(), r))));
    }

    [Fact(DisplayName = "サンプル: 拠点マスタは種別の空欄を拠点名から推定し、所在地の都道府県をコードに分ける。勤務地の表記ゆれ・リモートワークの揺れをそろえる")]
    public async Task Sample_SitesAndRemoteWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var sites = ConnectorMapping.Parse(await File.ReadAllBytesAsync(Sample("mappings/sites.json"), ct));
        var siteRows = SourceReader.Read(await File.ReadAllBytesAsync(Sample("sites.csv"), ct), "sites.csv", sites).Rows
            .Select(r => new RowNormalizer(sites.Profile).Normalize(Guid.NewGuid(), r))
            .ToList();
        var chiba = siteRows.Single(r => r[IngestFields.SiteCode] == "LS10");
        Assert.Equal(("logistics", "12", "千葉市美浜区新港9-9-6"), (chiba[IngestFields.SiteType], chiba[IngestFields.SitePrefecture], chiba[IngestFields.SiteLocation]));
        Assert.Equal("S030", siteRows.Single(r => r[IngestFields.SiteName] == "大阪支社")[IngestFields.SiteCode]);

        var hr = ConnectorMapping.Parse(await File.ReadAllBytesAsync(Sample("mappings/employees_hr_package.json"), ct));
        var normalizer = new RowNormalizer(hr.Profile);
        var people = SourceReader.Read(await File.ReadAllBytesAsync(Sample("employees_hr_package.csv"), ct), "employees_hr_package.csv", hr).Rows
            .Select(r => normalizer.Normalize(Guid.NewGuid(), r))
            .ToList();
        Assert.All(people.Where(r => r[IngestFields.RemoteWork] is not null), r => Assert.Contains(r[IngestFields.RemoteWork], new[] { "true", "false" }));
        Assert.DoesNotContain(people, r => r.HasIssue(IngestFields.RemoteWork, IssueCodes.InvalidValue));
        Assert.Contains(people, r => r[IngestFields.SiteName] is { } name && name.StartsWith("本社", StringComparison.Ordinal)
            && Domain.Text.SiteNameKey.Candidates(name).Contains(Domain.Text.SiteNameKey.From("東京本社")!));
    }

    [Fact(DisplayName = "サンプル: 座席表の列はすべて取り込む項目で、席番号の全角・ハイフンの揺れをそろえる")]
    public async Task Sample_SeatAssignments()
    {
        var ct = TestContext.Current.CancellationToken;
        var mapping = ConnectorMapping.Parse(await File.ReadAllBytesAsync(Sample("mappings/seat_assignments.json"), ct));
        var table = SourceReader.Read(await File.ReadAllBytesAsync(Sample("seat_assignments.csv"), ct), "seat_assignments.csv", mapping);
        Assert.Empty(table.DroppedColumns);
        var normalizer = new RowNormalizer(mapping.Profile);
        var codes = table.Rows.Select(r => normalizer.Normalize(Guid.NewGuid(), r)[IngestFields.SeatCode]).OfType<string>().ToList();
        Assert.All(codes, c => Assert.True(c.All(char.IsAscii), c));
        Assert.Contains("3F-Z99", codes);
    }
}
