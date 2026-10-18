using JinJi.Connectors;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Ingest;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>辞令の解釈・保持しない値の検査・Shift_JIS の拡張文字・段の推定のテーブル駆動テスト（DB を使わない）。</summary>
public sealed class ActionParsingTests
{
    public static TheoryData<string, PersonnelActionType, string?, string?, string?> Actions => new()
    {
        { "株式会社サンプル商事（販売分析チーム　）へ出向を命ずる", PersonnelActionType.SecondmentStart, null, null, "株式会社サンプル商事(販売分析チーム )" },
        { "株式会社サンプル物流へ出向を命ずる", PersonnelActionType.SecondmentStart, null, null, "株式会社サンプル物流" },
        { "出向を命ずる（出向先: 株式会社サンプル商事）", PersonnelActionType.SecondmentStart, null, null, "株式会社サンプル商事" },
        { "出向を解く", PersonnelActionType.SecondmentEnd, null, null, null },
        { "株式会社サンプル物流 物流企画部 部長を兼務させる", PersonnelActionType.ConcurrentStart, "株式会社サンプル物流 物流企画部", "部長", null },
        { "ヘルプデスクﾁｰﾑ リーダーを兼務することを命ずる", PersonnelActionType.ConcurrentStart, "ヘルプデスクチーム", "リーダー", null },
        { "兼務を解く", PersonnelActionType.ConcurrentEnd, null, null, null },
        { "法務室の兼務を解く", PersonnelActionType.ConcurrentEnd, "法務室", null, null },
        { "休職を命ずる", PersonnelActionType.SuspensionStart, null, null, null },
        { "復職を命ずる", PersonnelActionType.SuspensionEnd, null, null, null },
        { "退職を命ずる", PersonnelActionType.Termination, null, null, null },
        { "新入社員 東京配送グループ配属を命ずる", PersonnelActionType.Hire, "東京配送グループ", null, null },
        { "総務経理ｸﾞﾙｰﾌﾟに配置換えを命ずる", PersonnelActionType.Transfer, "総務経理グループ", null, null },
        { "応用開発ﾁｰﾑへの異動を命ずる", PersonnelActionType.Transfer, "応用開発チーム", null, null },
        { "大阪配送ｸﾞﾙｰﾌﾟ勤務を命ずる", PersonnelActionType.Transfer, "大阪配送グループ", null, null },
        { "東京配送グループに配属を命ずる", PersonnelActionType.Transfer, "東京配送グループ", null, null },
        { "人事グループ　グループ長を命ずる", PersonnelActionType.Transfer, "人事グループ", "グループ長", null },
        { "西日本物流部部長に任ずる", PersonnelActionType.Transfer, "西日本物流部", "部長", null },
        { "東京配送グループグループ長に任ずる", PersonnelActionType.Transfer, "東京配送グループ", "グループ長", null },
        { "東日本チームリーダーに任ずる", PersonnelActionType.Transfer, "東日本チーム", "リーダー", null },
        { "製造第3課主任に任ずる", PersonnelActionType.Transfer, "製造第3課", "主任", null },
        { "製造二課 　主任を命ずる", PersonnelActionType.Transfer, "製造二課", "主任", null },
        { "管理本部本部長に任ずる", PersonnelActionType.Transfer, "管理本部", "本部長", null },
        { "課長を命ずる", PersonnelActionType.PositionAppointment, null, "課長", null },
    };

    [Theory(DisplayName = "辞令の発令内容をルールで解釈する（種類・組織・役職・出向先）")]
    [MemberData(nameof(Actions))]
    public void ParsesAction(string text, PersonnelActionType type, string? org, string? title, string? target)
    {
        var parsed = ActionTextParser.Parse(text);

        Assert.NotNull(parsed);
        Assert.Equal((type, org, title, target), (parsed.Type, parsed.OrgText, parsed.Title, parsed.TargetText));
    }

    [Theory(DisplayName = "解釈できない発令内容は null（要確認・修正依頼にする）")]
    [InlineData("")]
    [InlineData("よろしくお願いします")]
    [InlineData("営業部を命ずる")]
    [InlineData("表彰する")]
    public void RejectsUninterpretable(string text) => Assert.Null(ActionTextParser.Parse(text));

    [Fact(DisplayName = "「配属」は配属の印を付ける（入社日と同じ日なら入社として扱う）")]
    public void Placement_IsMarked()
    {
        Assert.True(ActionTextParser.Parse("東京配送グループに配属を命ずる")!.IsPlacement);
        Assert.False(ActionTextParser.Parse("東京配送グループに配置換えを命ずる")!.IsPlacement);
    }

    [Theory(DisplayName = "自由記述に保持しない情報（生年月日・住所・電話番号）らしき値があれば検出する")]
    [InlineData("生年月日 1980/1/2", "BirthDate")]
    [InlineData("1980年1月2日生", "BirthDate")]
    [InlineData("S55.1.2生まれ", "BirthDate")]
    [InlineData("42歳", "BirthDate")]
    [InlineData("東京都千代田区丸の内1-1-1", "Address")]
    [InlineData("大阪府吹田市6-22-16 に転居", "Address")]
    [InlineData("〒100-0001", "Address")]
    [InlineData("連絡先 090-1234-5678", "PhoneNumber")]
    [InlineData("03-1234-5678 まで", "PhoneNumber")]
    public void DetectsSensitiveValue(string text, string kind) =>
        Assert.Contains(Enum.Parse<SensitiveValueDetector.Kind>(kind), SensitiveValueDetector.Detect(text));

    [Theory(DisplayName = "通常の自由記述は検出しない（日付だけ・組織名・勤務地）")]
    [InlineData("2025/03/31 廃止")]
    [InlineData("2026/04新設")]
    [InlineData("H29統合")]
    [InlineData("東京本社")]
    [InlineData("千葉物流センター")]
    [InlineData("営業第一部 課長を命ずる")]
    [InlineData("製造第3課主任に任ずる")]
    [InlineData("兼務者が責任者")]
    public void AllowsOrdinaryText(string text) => Assert.Empty(SensitiveValueDetector.Detect(text));

    [Fact(DisplayName = "Shift_JIS: NEC 選定 IBM 拡張文字（﨑・髙 など）も IBM 拡張文字と同じ文字として読む")]
    public void ShiftJis_ReadsNecSelectedIbmExtensions()
    {
        // ED95 = 﨑（NEC 選定）、FAB1 = 﨑（IBM）、FBFC = 髙（IBM）、EEFC = ＂、EEEF = ⅰ
        byte[] bytes = [0xED, 0x95, 0xFA, 0xB1, 0xFB, 0xFC, 0xEE, 0xFC, 0xEE, 0xEF, 0x41];
        Assert.Equal("﨑﨑髙＂ⅰA", TextEncodingDetector.DecodeShiftJis(bytes));
        Assert.Equal(("﨑﨑髙＂ⅰA", "shift_jis"), TextEncodingDetector.Decode(bytes));
    }

    [Theory(DisplayName = "段の推定: 組織名の末尾を法人の段の表示名と照らす（長い表示名を優先、当てはまらなければ段なし）")]
    [InlineData("営業本部", "division")]
    [InlineData("営業1部", "department")]
    [InlineData("第一営業課", "section")]
    [InlineData("西日本ﾁｰﾑ", "team")]
    [InlineData("経営企画室", null)]
    [InlineData("プロジェクト", null)]
    public void InfersLevel(string name, string? expected)
    {
        var scheme = new OrgLevelScheme(null, [new("division", "本部", 1), new("department", "部", 2), new("section", "課", 3), new("team", "チーム", 4)]);
        Assert.Equal(expected, ProposalPlanner.InferLevel(scheme, name));
    }

    [Fact(DisplayName = "仮の組織コードは法人・上位組織名・組織名で決まり、取り込み直しても同じ")]
    public void ProvisionalCode_IsDeterministic()
    {
        var entity = Guid.CreateVersion7();
        var a = ProposalPlanner.ProvisionalCode(entity, "営業2部", "新規開拓チーム");

        Assert.Equal(a, ProposalPlanner.ProvisionalCode(entity, "営業2部", "新規開拓チーム"));
        Assert.NotEqual(a, ProposalPlanner.ProvisionalCode(entity, "営業1部", "新規開拓チーム"));
        Assert.StartsWith(ProposalPlanner.ProvisionalCodePrefix, a, StringComparison.Ordinal);
    }
}
