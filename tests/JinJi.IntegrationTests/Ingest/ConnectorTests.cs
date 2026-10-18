using System.Text;
using ClosedXML.Excel;
using JinJi.Application.Ingest;
using JinJi.Connectors;
using JinJi.Ingest;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>コネクタ（CSV の文字コード・引用符・改行、xlsx・結合セル）と項目マッピング（列の捨て方）のテスト（DB を使わない）。</summary>
public sealed class ConnectorTests
{
    private const string Header = "社員番号,氏名,生年月日,性別,社用メール,備考,所属";

    // 値に区切り・引用符・改行を含む行と、表題の無い CSV
    private const string Body =
        "E0001,\"山田, 太郎\",1980/01/02,男,taro@example.co.jp,\"メモ\"\"引用\"\"\",営業一課\r\n" +
        "E0002,髙橋 一郎,1990/03/04,男,ichiro@example.co.jp,\"複数行\r\nの備考\",営業一課\r\n" +
        "\r\n" +
        "E0003,ｻﾄｳ ﾊﾅｺ,2000/05/06,女,,,\r\n";

    internal static ConnectorMapping PeopleMapping(string extra = "") => ConnectorMapping.Parse(Encoding.UTF8.GetBytes($$"""
        {
          "name": "test-people",
          "target": "people",
          {{extra}}
          "fields": {
            "employee_number": ["社員番号", "社員No"],
            "full_name": ["氏名"],
            "work_email": ["社用メール", "メール"],
            "org_name": ["所属"]
          },
          "ignore": ["備考"]
        }
        """));

    public static TheoryData<string> Encodings => ["utf-8", "utf-8-bom", "shift_jis"];

    [Theory(DisplayName = "CSV: UTF-8 / UTF-8 BOM / Shift_JIS(CP932) を判定し、CRLF・引用符・値の中の改行を読む")]
    [MemberData(nameof(Encodings))]
    public void Csv_DetectsEncodingAndParses(string encoding)
    {
        var text = Header + "\r\n" + Body;
        var bytes = encoding switch
        {
            "utf-8" => new UTF8Encoding(false).GetBytes(text),
            "utf-8-bom" => [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(text)],
            _ => TextEncodingDetector.ShiftJis.GetBytes(text),
        };

        var (decoded, detected) = TextEncodingDetector.Decode(bytes);
        Assert.Equal(encoding, detected);
        Assert.Equal(text, decoded);

        var table = SourceReader.Read(bytes, "people.csv", PeopleMapping());

        Assert.Equal(ImportSourceKind.Csv, table.SourceKind);
        Assert.Equal([2, 3, 5], table.Rows.Select(r => r.SourceRowNumber));
        Assert.Equal("山田, 太郎", table.Rows[0].Values[IngestFields.FullName]);
        Assert.Equal("髙橋 一郎", table.Rows[1].Values[IngestFields.FullName]);
        Assert.Equal("ｻﾄｳ ﾊﾅｺ", table.Rows[2].Values[IngestFields.FullName]);
        Assert.False(table.Rows[2].Values.ContainsKey(IngestFields.WorkEmail));
    }

    [Fact(DisplayName = "CSV: 引用符の中の二重の引用符・区切り・改行、LF だけの改行、表題の行の読み飛ばし")]
    public void Csv_HandlesQuotesAndHeaderRow()
    {
        var table = CsvTableReader.Read("在籍者一覧\n\n社員番号,備考\nE1,\"a,\"\"b\"\"\nc\"\nE2,d", headerRow: 3);

        Assert.Equal(["社員番号", "備考"], table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal("a,\"b\"\nc", table.Rows[0].Cells[1]);
        Assert.Equal(4, table.Rows[0].SourceRowNumber);
        Assert.Equal("d", table.Rows[1].Cells[1]);
    }

    [Fact(DisplayName = "CSV: 引用符が閉じていなければ読み込みを失敗させる")]
    public void Csv_RejectsUnterminatedQuote() =>
        Assert.Throws<FormatException>(() => CsvTableReader.Read("a,b\n1,\"open"));

    [Fact(DisplayName = "CSV: 文字コードを指定して読める。指定と違うバイト列は失敗させる")]
    public void Csv_ExplicitEncoding()
    {
        var sjis = TextEncodingDetector.ShiftJis.GetBytes("社員番号\r\n");
        Assert.Equal("社員番号\r\n", TextEncodingDetector.Decode(sjis, "shift_jis").Text);
        Assert.Throws<FormatException>(() => TextEncodingDetector.Decode(sjis, "utf-8"));
    }

    [Fact(DisplayName = "項目マッピング: マッピングにない列・保持禁止の列・無視する列を捨て、列名と件数だけを残す（値は残さない）")]
    public void Mapping_DropsColumnsBeforeStaging()
    {
        var bytes = Encoding.UTF8.GetBytes(Header + "\r\n" + Body);

        var table = SourceReader.Read(bytes, "people.csv", PeopleMapping());

        Assert.All(table.Rows, r => Assert.All(r.Values.Keys, k => Assert.Contains(k, IngestFields.Mappable)));
        Assert.DoesNotContain(table.Rows.SelectMany(r => r.Values.Values), v => v.Contains("1980", StringComparison.Ordinal) || v is "男" or "女");
        Assert.Equal(
            [("生年月日", 3, DroppedColumnKind.Forbidden), ("性別", 3, DroppedColumnKind.Forbidden), ("備考", 2, DroppedColumnKind.Ignored)],
            table.DroppedColumns.Select(d => (d.Name, d.Count, d.Kind)));
        Assert.Empty(table.MissingFields);
    }

    [Fact(DisplayName = "元の行のハッシュは保持する列だけから作る（捨てた列の値が違っても同じ）")]
    public void RowHash_ExcludesDroppedColumns()
    {
        var a = SourceReader.Read(Encoding.UTF8.GetBytes("社員番号,氏名,生年月日\nE1,山田,1980/01/01\n"), "a.csv", PeopleMapping());
        var b = SourceReader.Read(Encoding.UTF8.GetBytes("社員番号,氏名,生年月日\nE1,山田,1999/12/31\n"), "b.csv", PeopleMapping());
        var c = SourceReader.Read(Encoding.UTF8.GetBytes("社員番号,氏名,生年月日\nE1,山本,1980/01/01\n"), "c.csv", PeopleMapping());

        Assert.Equal(a.Rows[0].RowSha256, b.Rows[0].RowSha256);
        Assert.NotEqual(a.Rows[0].RowSha256, c.Rows[0].RowSha256);
        Assert.Matches("^[0-9a-f]{64}$", a.Rows[0].RowSha256);
    }

    [Fact(DisplayName = "項目マッピング: 候補の列は行ごとに先に書いたものの値を優先し（空なら次の候補）、見出しのゆれ（全角・空白）を吸収する")]
    public void Mapping_UsesCandidateOrder()
    {
        var table = SourceReader.Read(
            Encoding.UTF8.GetBytes("メール,社員 Ｎｏ,社用メール\nold@example.com,E9,new@example.com\nfallback@example.com,E10,\n"),
            "x.csv",
            PeopleMapping());

        Assert.Equal("E9", table.Rows[0].Values[IngestFields.EmployeeNumber]);
        Assert.Equal("new@example.com", table.Rows[0].Values[IngestFields.WorkEmail]);
        Assert.Equal("fallback@example.com", table.Rows[1].Values[IngestFields.WorkEmail]);
        Assert.Empty(table.DroppedColumns);
        Assert.Equal([IngestFields.FullName, IngestFields.OrgName], table.MissingFields);
    }

    [Fact(DisplayName = "項目マッピング: discardWhenPresent の列に値がある行では、指定の項目を捨てる（旧姓使用者の戸籍名の読み）")]
    public void Mapping_DiscardsFieldsWhenColumnPresent()
    {
        var mapping = ConnectorMapping.Parse(Encoding.UTF8.GetBytes("""
            {"name":"x","target":"people",
             "fields":{"employee_number":["社員番号"],"full_name":["通称(旧姓使用)","氏名"],"full_name_kana":["氏名カナ"]},
             "discardWhenPresent":{"通称(旧姓使用)":["full_name_kana"]}}
            """));

        var table = SourceReader.Read(Encoding.UTF8.GetBytes("社員番号,氏名,氏名カナ,通称(旧姓使用)\nE1,上田 直子,ウエダ ナオコ,酒井 直子\nE2,山田 太郎,ヤマダ タロウ,\n"), "x.csv", mapping);

        Assert.Equal("酒井 直子", table.Rows[0].Values[IngestFields.FullName]);
        Assert.False(table.Rows[0].Values.ContainsKey(IngestFields.FullNameKana));
        Assert.Equal("山田 太郎", table.Rows[1].Values[IngestFields.FullName]);
        Assert.Equal("ヤマダ タロウ", table.Rows[1].Values[IngestFields.FullNameKana]);
        Assert.DoesNotContain(table.Rows.SelectMany(r => r.Values.Values), v => v.Contains("上田", StringComparison.Ordinal) || v.Contains("ウエダ", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "組織図: 列が階層の表を、上の行で空欄を補って組織名と上位組織名にする（室は補いでは上位組織にしない）")]
    public void Hierarchy_FillsDownBlanks()
    {
        var csv = string.Join("\n",
            "会社,本部,部・室,課・グループ,チーム,組織コード",
            "株式会社X,経営企画室,,,,A1",
            ",,内部監査部,,,A2",
            ",営業本部,,,,A3",
            ",,営業1部,,,A4",
            ",,,第一営業課,,A5",
            ",営業本部,営業１部,第一営業課,東日本チーム,A6",
            ",,,,西日本チーム,A7",
            ",,営業企画室,,,A8",
            ",営業本部 ,営業企画室 ,,販売分析チーム,A9",
            "株式会社Y,,物流企画部,,,B1",
            ",,,企画グループ,,B2");
        var mapping = ConnectorMapping.Parse(Encoding.UTF8.GetBytes("""
            {"name":"x","target":"org_units",
             "source":{"hierarchy":{"entityColumn":"会社","columns":["本部","部・室","課・グループ","チーム"],"noCarrySuffixes":["室"]}},
             "fields":{"legal_entity_code":["会社"],"org_code":["組織コード"]}}
            """));

        var table = SourceReader.Read(Encoding.UTF8.GetBytes(csv), "org.csv", mapping);

        var actual = table.Rows.Select(r => (
            r.Values[IngestFields.OrgCode],
            r.Values[IngestFields.OrgName],
            r.Values.GetValueOrDefault(IngestFields.ParentOrgName),
            r.Values[IngestFields.LegalEntityCode])).ToList();
        Assert.Equal(
            [
                ("A1", "経営企画室", null, "株式会社X"),
                ("A2", "内部監査部", null, "株式会社X"),
                ("A3", "営業本部", null, "株式会社X"),
                ("A4", "営業1部", "営業本部", "株式会社X"),
                ("A5", "第一営業課", "営業1部", "株式会社X"),
                ("A6", "東日本チーム", "第一営業課", "株式会社X"),
                ("A7", "西日本チーム", "第一営業課", "株式会社X"),
                ("A8", "営業企画室", "営業本部", "株式会社X"),
                ("A9", "販売分析チーム", "営業企画室", "株式会社X"),
                ("B1", "物流企画部", null, "株式会社Y"),
                ("B2", "企画グループ", "物流企画部", "株式会社Y"),
            ],
            actual);
    }

    [Fact(DisplayName = "Graph の GET /users 応答（JSON）を読み、真偽値・配列・null を文字列にする")]
    public async Task GraphJson_ReadsUsers()
    {
        var json = """
            {"@odata.context":"x","value":[
              {"id":"A-1","displayName":"山田 太郎","employeeId":"123","accountEnabled":true,"businessPhones":["+81 3"],"mobilePhone":null},
              {"id":"A-2","displayName":"会議室","employeeId":null,"accountEnabled":false,"businessPhones":[]}
            ]}
            """;
        var table = await new GraphUsersJsonFile(Encoding.UTF8.GetBytes(json)).ReadUsersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["id", "displayName", "employeeId", "accountEnabled", "businessPhones", "mobilePhone"], table.Headers);
        Assert.Equal(["A-1", "山田 太郎", "123", "true", "+81 3", ""], table.Rows[0].Cells);
        Assert.Equal(["A-2", "会議室", "", "false", "", ""], table.Rows[1].Cells);
        Assert.Equal([1, 2], table.Rows.Select(r => r.SourceRowNumber));
    }

    [Theory(DisplayName = "項目マッピング: 不正な設定は読み込みで拒否する")]
    [InlineData("""{"name":"x","target":"people","fields":{"birth_date":["生年月日"]}}""")]
    [InlineData("""{"name":"x","target":"people","fields":{"employee_number":["社員番号"],"hire_date":["生年月日"]}}""")]
    [InlineData("""{"name":"x","target":"people","fields":{"employee_number":["社員番号"],"full_name":["社員番号"]}}""")]
    [InlineData("""{"name":"x","target":"people","fields":{"employee_number":["社員番号"]},"ignore":["社員番号"]}""")]
    [InlineData("""{"name":"x","target":"people","fields":{"employee_number":["社員番号"]},"defaults":{"gender":"x"}}""")]
    [InlineData("""{"name":"x","target":"payroll","fields":{"employee_number":["社員番号"]}}""")]
    [InlineData("""{"name":"x","target":"people","fields":{"search_name":["氏名"]}}""")]
    [InlineData("""{"target":"people","fields":{"employee_number":["社員番号"]}}""")]
    public void Mapping_RejectsInvalidSettings(string json) =>
        Assert.Throws<FormatException>(() => ConnectorMapping.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact(DisplayName = "xlsx: 指定のシート・表題の行の読み飛ばし・日付と数値のセル・保持禁止の列を捨てる")]
    public void Xlsx_ReadsSheet()
    {
        var bytes = Workbook(workbook =>
        {
            workbook.AddWorksheet("表紙").Cell(1, 1).Value = "表紙";
            var sheet = workbook.AddWorksheet("名簿");
            sheet.Cell(1, 1).Value = "在籍者一覧（2024年4月）";
            sheet.Cell(2, 1).Value = "社員番号";
            sheet.Cell(2, 2).Value = "氏名";
            sheet.Cell(2, 3).Value = "生年月日";
            sheet.Cell(2, 4).Value = "入社日";
            sheet.Cell(3, 1).Value = 123;
            sheet.Cell(3, 2).Value = "山田 太郎";
            sheet.Cell(3, 3).Value = new DateTime(1980, 1, 2);
            sheet.Cell(3, 4).Value = new DateTime(2024, 4, 1);
        });
        var mapping = ConnectorMapping.Parse(Encoding.UTF8.GetBytes("""
            {"name":"x","target":"people","source":{"sheet":"名簿","headerRow":2},
             "fields":{"employee_number":["社員番号"],"full_name":["氏名"],"hire_date":["入社日"]}}
            """));

        var table = SourceReader.Read(bytes, "people.xlsx", mapping);

        Assert.Equal(ImportSourceKind.Xlsx, table.SourceKind);
        var row = Assert.Single(table.Rows);
        Assert.Equal(3, row.SourceRowNumber);
        Assert.Equal("123", row.Values[IngestFields.EmployeeNumber]);
        Assert.Equal("2024-04-01", row.Values[IngestFields.HireDate]);
        Assert.Equal([("生年月日", 1, DroppedColumnKind.Forbidden)], table.DroppedColumns.Select(d => (d.Name, d.Count, d.Kind)));
    }

    [Theory(DisplayName = "xlsx: 結合セルの値の補完（指定したときだけ）")]
    [InlineData(true)]
    [InlineData(false)]
    public void Xlsx_FillsMergedCells(bool fill)
    {
        var bytes = Workbook(workbook =>
        {
            var sheet = workbook.AddWorksheet("組織図");
            sheet.Cell(1, 1).Value = "部";
            sheet.Cell(1, 2).Value = "課コード";
            sheet.Cell(1, 3).Value = "課";
            sheet.Cell(2, 1).Value = "営業部";
            sheet.Range(2, 1, 4, 1).Merge();
            sheet.Cell(2, 2).Value = "S1";
            sheet.Cell(2, 3).Value = "営業一課";
            sheet.Cell(3, 2).Value = "S2";
            sheet.Cell(3, 3).Value = "営業二課";
            sheet.Cell(4, 2).Value = "S3";
            sheet.Cell(4, 3).Value = "営業三課";
        });
        var mapping = ConnectorMapping.Parse(Encoding.UTF8.GetBytes($$$"""
            {"name":"x","target":"org_units","source":{"fillMergedCells":{{{(fill ? "true" : "false")}}}},
             "fields":{"org_code":["課コード"],"org_name":["課"],"parent_org_name":["部"]}}
            """));

        var table = SourceReader.Read(bytes, "org.xlsx", mapping);

        var parents = table.Rows.Select(r => r.Values.GetValueOrDefault(IngestFields.ParentOrgName)).ToList();
        Assert.Equal(fill ? ["営業部", "営業部", "営業部"] : ["営業部", null, null], parents);
    }

    internal static byte[] Workbook(Action<XLWorkbook> build)
    {
        using var workbook = new XLWorkbook();
        build(workbook);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
