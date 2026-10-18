using JinJi.Ingest;
using JinJi.Domain.Text;
using JinJi.Ingest.Normalization;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>正規化（NFKC・全角/半角・カナ・空白・異体字・社員番号・日付・組織名）のテーブル駆動テスト（DB を使わない）。</summary>
public sealed class NormalizationTests
{
    [Theory(DisplayName = "表示用: 空白の正規化・半角カナ → 全角・全角英数 → 半角。漢字（異体字）は変えない")]
    [InlineData("  山田　太郎  ", "山田 太郎")]
    [InlineData("山田\t\t太郎", "山田 太郎")]
    [InlineData("ﾔﾏﾀﾞ ﾀﾛｳ", "ヤマダ タロウ")]
    [InlineData("ﾊﾟｰﾄ", "パート")]
    [InlineData("ＡＢＣ１２３", "ABC123")]
    [InlineData("髙橋　﨑子", "髙橋 﨑子")]
    [InlineData("齋藤​一郎", "齋藤一郎")]
    [InlineData("営業部（東京）", "営業部(東京)")]
    public void ToDisplay(string input, string expected) => Assert.Equal(expected, TextNormalizer.ToDisplay(input));

    [Theory(DisplayName = "表示用・検索用: 空白だけの値は null")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("　　")]
    [InlineData("​")]
    public void Blank_IsNull(string input)
    {
        Assert.Null(TextNormalizer.ToDisplay(input));
        Assert.Null(TextNormalizer.ToSearchKey(input));
        Assert.Null(TextNormalizer.ToKana(input));
    }

    [Theory(DisplayName = "フリガナ: ひらがな → カタカナ、半角カナ → 全角（濁点の合成）")]
    [InlineData("やまだ たろう", "ヤマダ タロウ")]
    [InlineData("ﾔﾏﾀﾞ　ﾀﾛｳ", "ヤマダ タロウ")]
    [InlineData("ゔぁいおりん", "ヴァイオリン")]
    [InlineData("サトウ", "サトウ")]
    public void ToKana(string input, string expected) => Assert.Equal(expected, TextNormalizer.ToKana(input));

    [Theory(DisplayName = "検索用: 異体字・空白・全角/半角・かなのゆれを吸収する")]
    [InlineData("髙橋 一郎", "高橋一郎")]
    [InlineData("高橋　一郎", "高橋一郎")]
    [InlineData("山﨑 花子", "山崎花子")]
    [InlineData("山嵜花子", "山崎花子")]
    [InlineData("齋藤", "斉藤")]
    [InlineData("齊藤", "斉藤")]
    [InlineData("斎藤", "斉藤")]
    [InlineData("渡邊", "渡辺")]
    [InlineData("渡邉", "渡辺")]
    [InlineData("澤田", "沢田")]
    [InlineData("濱田", "浜田")]
    [InlineData("𠮷田", "吉田")]
    [InlineData("やまだ", "ヤマダ")]
    [InlineData("ﾔﾏﾀﾞ", "ヤマダ")]
    [InlineData("Ｔａｒｏ Ｙａｍａｄａ", "taroyamada")]
    public void ToSearchKey(string input, string expected) => Assert.Equal(expected, TextNormalizer.ToSearchKey(input));

    [Fact(DisplayName = "異体字の表の左側は NFKC で変わらない（NFKC の後に当てる表として成り立つ）")]
    public void VariantTable_IsStableUnderNfkc()
    {
        Assert.All(KanjiVariants.Table.Keys, k => Assert.Equal(k, TextNormalizer.Nfkc(k)));
        Assert.All(KanjiVariants.Table, kv => Assert.NotEqual(kv.Key, kv.Value));
    }

    [Theory(DisplayName = "社員番号: 全角・区切り・大文字小文字・ゼロ埋めのゆれ")]
    [InlineData("１２３", 6, "000123")]
    [InlineData("123", 6, "000123")]
    [InlineData("0000123", 6, "000123")]
    [InlineData("123.0", 6, "000123")]
    [InlineData("12-34", 6, "001234")]
    [InlineData("e-0001", null, "E0001")]
    [InlineData("Ｅ０００１", null, "E0001")]
    [InlineData(" E 0001 ", null, "E0001")]
    [InlineData("1234567", 6, "1234567")]
    [InlineData("0", 6, "000000")]
    [InlineData("A12", 6, "A0012")]
    [InlineData("E-0001X", 6, "E0001X")]
    [InlineData("L45", 5, "L0045")]
    [InlineData("l-0045", 5, "L0045")]
    [InlineData("L00045", 5, "L0045")]
    public void EmployeeNumber(string input, int? pad, string expected) =>
        Assert.Equal(expected, new EmployeeNumberNormalizer(new EmployeeNumberRules { PadLength = pad, PrefixedPadLength = pad is null ? null : 4 }).Normalize(input));

    [Theory(DisplayName = "日付: 西暦の各形式・和暦（令和・平成・昭和）・R6.4.1 形式・Excel のシリアル値")]
    [InlineData("2024-04-01", "2024-04-01")]
    [InlineData("2024/4/1", "2024-04-01")]
    [InlineData("2024.04.01", "2024-04-01")]
    [InlineData("2024年4月1日", "2024-04-01")]
    [InlineData("２０２４年４月１日", "2024-04-01")]
    [InlineData("20240401", "2024-04-01")]
    [InlineData("2024-04-01T00:00:00", "2024-04-01")]
    [InlineData("2024/04/01 0:00", "2024-04-01")]
    [InlineData("令和6年4月1日", "2024-04-01")]
    [InlineData("令和６年４月１日", "2024-04-01")]
    [InlineData("令和元年5月1日", "2019-05-01")]
    [InlineData("R6.4.1", "2024-04-01")]
    [InlineData("r06/04/01", "2024-04-01")]
    [InlineData("R6年4月1日", "2024-04-01")]
    [InlineData("H31.4.30", "2019-04-30")]
    [InlineData("平成31年4月30日", "2019-04-30")]
    [InlineData("平成元年1月8日", "1989-01-08")]
    [InlineData("S64.1.7", "1989-01-07")]
    [InlineData("昭和50年3月31日", "1975-03-31")]
    [InlineData("㋿6.4.1", "2024-04-01")]
    [InlineData("45383", "2024-04-01")]
    public void ParsesDate(string input, string expected) => Assert.Equal(expected, JapaneseDateParser.ToIsoOrNull(input));

    [Theory(DisplayName = "日付: 解釈できないもの（元号の期間外・存在しない日・2 桁の年・文字）")]
    [InlineData("平成32年1月1日")]
    [InlineData("令和元年4月30日")]
    [InlineData("昭和64年1月8日")]
    [InlineData("2024-02-30")]
    [InlineData("2023/2/29")]
    [InlineData("24/4/1")]
    [InlineData("2024-13-01")]
    [InlineData("未定")]
    [InlineData("R6.4")]
    [InlineData("")]
    public void RejectsDate(string input) => Assert.Null(JapaneseDateParser.ToIsoOrNull(input));

    [Fact(DisplayName = "日付: Excel のシリアル値は指定したときだけ解釈する")]
    public void ExcelSerial_IsOptional() => Assert.Null(JapaneseDateParser.ToIsoOrNull("45383", allowExcelSerial: false));

    [Theory(DisplayName = "組織名: 「第一」「第１」「第1」と「第」の有無、「一課」「1課」をそろえ、固有名の漢数字は変えない")]
    [InlineData("第一営業部", "1営業部")]
    [InlineData("第１営業部", "1営業部")]
    [InlineData("第1営業部", "1営業部")]
    [InlineData("第 一 営業部", "1営業部")]
    [InlineData("営業第一部", "営業1部")]
    [InlineData("営業１部", "営業1部")]
    [InlineData("製造第２部", "製造2部")]
    [InlineData("企画第２課", "企画2課")]
    [InlineData("営業一課", "営業1課")]
    [InlineData("営業１課", "営業1課")]
    [InlineData("第十二営業所", "12営業所")]
    [InlineData("営業二十三課", "営業23課")]
    [InlineData("一般事務部", "一般事務部")]
    [InlineData("十日町支店", "十日町支店")]
    [InlineData("髙崎営業所", "高崎営業所")]
    public void OrgName(string input, string expected) => Assert.Equal(expected, OrgNameNormalizer.ToSearchKey(input));

    [Theory(DisplayName = "漢数字")]
    [InlineData("一", 1)]
    [InlineData("十", 10)]
    [InlineData("十一", 11)]
    [InlineData("二十", 20)]
    [InlineData("二十三", 23)]
    [InlineData("一〇", 10)]
    [InlineData("百二", 102)]
    public void KanjiNumber(string input, int expected) => Assert.Equal(expected, OrgNameNormalizer.ParseKanjiNumber(input));

    [Theory(DisplayName = "雇用区分・主務/兼務の表示値 → コード値。表に無い値は null")]
    [InlineData("正社員", "regular")]
    [InlineData("正 社 員", "regular")]
    [InlineData("契約社員", "contract")]
    [InlineData("嘱託", "entrusted")]
    [InlineData("派遣", "dispatched")]
    [InlineData("出向受入", "seconded_in")]
    [InlineData("ﾊﾟｰﾄ", "part_time")]
    [InlineData("アルバイト", "part_time")]
    [InlineData("役員", null)]
    public void EmploymentType(string input, string? expected) => Assert.Equal(expected, ValueCodes.EmploymentType(input));

    [Theory(DisplayName = "保持しない項目を表す列名を検出する")]
    [InlineData("生年月日")]
    [InlineData("年齢")]
    [InlineData("性別")]
    [InlineData("現住所")]
    [InlineData("郵便番号")]
    [InlineData("自宅電話")]
    [InlineData("私用メール")]
    [InlineData("扶養家族数")]
    [InlineData("基本給")]
    [InlineData("等級")]
    [InlineData("人事評価")]
    [InlineData("休職理由")]
    [InlineData("国籍")]
    [InlineData("最終学歴")]
    [InlineData("マイナンバー")]
    [InlineData("BirthDate")]
    [InlineData("date_of_birth")]
    [InlineData("DOB")]
    [InlineData("Gender")]
    [InlineData("Home Address")]
    [InlineData("salary")]
    [InlineData("Age")]
    public void DetectsForbiddenColumn(string name) => Assert.True(ForbiddenColumnNames.IsForbidden(name), name);

    [Theory(DisplayName = "保持してよい項目の列名は検出しない")]
    [InlineData("社員番号")]
    [InlineData("氏名")]
    [InlineData("フリガナ")]
    [InlineData("社用メール")]
    [InlineData("内線")]
    [InlineData("社用携帯")]
    [InlineData("所属")]
    [InlineData("役職")]
    [InlineData("入社日")]
    [InlineData("雇用区分")]
    [InlineData("Family Name")]
    [InlineData("Manager")]
    [InlineData("Page")]
    [InlineData("Usage")]
    [InlineData("work_email")]
    public void AllowsPermittedColumn(string name) => Assert.False(ForbiddenColumnNames.IsForbidden(name), name);

    [Fact(DisplayName = "行の正規化: 氏名の分割・表示用と検索用・フリガナ・日付・コード値・問題の記録（値は持たない）")]
    public void RowNormalizer_NormalizesRow()
    {
        var normalizer = new RowNormalizer(new ImportProfile
        {
            Target = Application.Ingest.ImportTargetKind.People,
            EmployeeNumber = new EmployeeNumberRules { PadLength = 6 },
            Defaults = new Dictionary<string, string> { [IngestFields.EmploymentType] = "正社員" },
        });
        var row = new MappedRow(
            2,
            new Dictionary<string, string>
            {
                [IngestFields.EmployeeNumber] = "１２３",
                [IngestFields.FullName] = "髙橋　一郎",
                [IngestFields.FullNameKana] = "たかはし いちろう",
                [IngestFields.WorkEmail] = "Ichiro.Takahashi@Example.CO.JP ",
                [IngestFields.HireDate] = "H31.4.1",
                [IngestFields.TerminationDate] = "未定",
                [IngestFields.OrgName] = "営業一課",
            },
            new string('0', 64));

        var normalized = normalizer.Normalize(Guid.NewGuid(), row);

        Assert.Equal("000123", normalized[IngestFields.EmployeeNumber]);
        Assert.Equal("髙橋", normalized[IngestFields.FamilyName]);
        Assert.Equal("一郎", normalized[IngestFields.GivenName]);
        Assert.Equal("高橋一郎", normalized[IngestFields.SearchName]);
        Assert.Equal("タカハシ", normalized[IngestFields.FamilyNameKana]);
        Assert.Equal("イチロウ", normalized[IngestFields.GivenNameKana]);
        Assert.Equal("ichiro.takahashi@example.co.jp", normalized[IngestFields.WorkEmail]);
        Assert.Equal("2019-04-01", normalized[IngestFields.HireDate]);
        Assert.Null(normalized[IngestFields.TerminationDate]);
        Assert.Equal("regular", normalized[IngestFields.EmploymentType]);
        Assert.Equal("営業1課", normalized[IngestFields.OrgSearchName]);
        Assert.True(normalized.HasIssue(IngestFields.TerminationDate, IssueCodes.InvalidDate));
        Assert.All(normalized.Data.Keys, k => Assert.Contains(k, IngestFields.All));
    }

    [Theory(DisplayName = "拠点名: 異体字・全角半角・空白・括弧・中黒をそろえ、「本社（東京）」は「東京本社」・括弧の外も候補にする")]
    [InlineData("東京 本社", "東京本社")]
    [InlineData("ﾁﾊﾞ物流ｾﾝﾀｰ", "チバ物流センター")]
    [InlineData("本社（東京）", "東京本社")]
    [InlineData("大阪支社(梅田)", "大阪支社")]
    [InlineData("横浜・工場", "横浜工場")]
    [InlineData("第一工場", "1工場")]
    public void SiteName_Candidates(string input, string expected) =>
        Assert.Contains(SiteNameKey.From(expected)!, SiteNameKey.Candidates(input));

    [Fact(DisplayName = "拠点名: 支社と支店のように語が違うものは同じにしない")]
    public void SiteName_DoesNotMergeDifferentWords()
    {
        Assert.NotEqual(SiteNameKey.From("大阪支社"), SiteNameKey.From("大阪支店"));
        Assert.Empty(SiteNameKey.Candidates(" "));
    }

    [Theory(DisplayName = "拠点の種別: コード値・表示名・拠点名の末尾から推定する（長い表示名を優先）")]
    [InlineData("head_office", "head_office")]
    [InlineData("本社", "head_office")]
    [InlineData("大阪支店", "branch")]
    [InlineData("横浜工場", "factory")]
    [InlineData("千葉物流センター", "logistics")]
    [InlineData("つくば研究センター", "laboratory")]
    [InlineData("札幌営業所", "sales_office")]
    [InlineData("倉庫", "logistics")]
    public void SiteType_Infers(string input, string expected)
    {
        Assert.True(Domain.Organization.SiteCodes.TryInfer(input, out var type));
        Assert.Equal(expected, Domain.Organization.SiteCodes.ToCode(type));
    }

    [Theory(DisplayName = "都道府県: コード・名前・都道府県を省いた名前・所在地の先頭から求める")]
    [InlineData("13", "13")]
    [InlineData("1", "01")]
    [InlineData("東京都", "13")]
    [InlineData("東京", "13")]
    [InlineData("京都", "26")]
    [InlineData("北海道札幌市中央区", "01")]
    [InlineData("大阪府大阪市北区", "27")]
    [InlineData("４７", "47")]
    [InlineData("48", null)]
    [InlineData("東京市", null)]
    public void Prefecture_Parses(string input, string? expected) => Assert.Equal(expected, Domain.Organization.SiteCodes.ParsePrefecture(input));

    [Theory(DisplayName = "リモートワークの有無: 表示値の揺れを true / false にそろえ、解釈できない値は null")]
    [InlineData("○", "true")]
    [InlineData("〇", "true")]
    [InlineData("はい", "true")]
    [InlineData("有", "true")]
    [InlineData("可", "true")]
    [InlineData("対象", "true")]
    [InlineData("Ｙ", "true")]
    [InlineData("×", "false")]
    [InlineData("いいえ", "false")]
    [InlineData("無", "false")]
    [InlineData("対象外", "false")]
    [InlineData("－", "false")]
    [InlineData("たまに", null)]
    [InlineData("", null)]
    public void RemoteWork_Flags(string input, string? expected) => Assert.Equal(expected, RowNormalizer.ParseFlag(input));
}
