using JinJi.Application.Ingest;
using JinJi.Domain.Organization;
using JinJi.Ingest;
using JinJi.Ingest.Matching;
using JinJi.Ingest.Normalization;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>名寄せ（社員番号 → 社用メール → 正規化氏名 + 所属）と提案の計画のテスト（DB を使わない）。</summary>
public sealed class MatchingTests
{
    private static readonly DateOnly Today = new(2026, 10, 12);
    private static readonly Guid Entity = Guid.CreateVersion7();
    private static readonly Guid S1 = Guid.CreateVersion7();
    private static readonly Guid S2 = Guid.CreateVersion7();
    private static readonly Guid Sato1 = Guid.CreateVersion7();
    private static readonly Guid Sato2 = Guid.CreateVersion7();
    private static readonly Guid Takahashi = Guid.CreateVersion7();
    private static readonly Guid Numeric = Guid.CreateVersion7();

    private static readonly OrgSnapshot Snapshot = OrgSnapshot.Create(
        Today,
        [new LegalEntity(Entity, "A", "テスト株式会社")],
        [
            new OrgUnit(S1, Entity, "S1", "営業一課", null, "section", null, Sato1),
            new OrgUnit(S2, Entity, "S2", "営業二課", null, "section", null, null),
        ],
        [
            Person(Sato1, "E0003", "佐藤", "花子", null),
            Person(Sato2, "E0004", "佐藤", "花子", null),
            Person(Takahashi, "E0002", "髙橋", "一郎", "ichiro.takahashi@example.co.jp"),
            Person(Numeric, "000123", "鈴木", "次郎", null),
        ],
        [
            new Assignment(Sato1, S1, AssignmentType.Primary, null, SecondmentType.None, Today.AddYears(-5), null),
            new Assignment(Sato2, S1, AssignmentType.Primary, null, SecondmentType.None, Today.AddYears(-5), null),
            new Assignment(Takahashi, S2, AssignmentType.Primary, null, SecondmentType.None, Today.AddYears(-5), null),
            new Assignment(Numeric, S2, AssignmentType.Primary, null, SecondmentType.None, Today.AddYears(-5), null),
        ],
        [new OrgLevelScheme(null, [new("division", "本部", 1), new("department", "部", 2), new("section", "課", 3)])]);

    private static readonly RowNormalizer Normalizer = new(new ImportProfile
    {
        Target = ImportTargetKind.People,
        EmployeeNumber = new EmployeeNumberRules { PadLength = 6 },
        Defaults = new Dictionary<string, string> { [IngestFields.EmploymentType] = "正社員" },
    });

    private static readonly PersonMatcher Matcher = new(Snapshot, Normalizer.EmployeeNumbers);

    [Theory(DisplayName = "社員番号のゆれ（全角・区切り・小文字・ゼロ埋め）を吸収して一意に突合する")]
    [InlineData("E0002")]
    [InlineData("e-0002")]
    [InlineData("Ｅ０００２")]
    [InlineData(" E 0002")]
    public void EmployeeNumber_Variants(string number)
    {
        var result = Matcher.Match(Row(("employee_number", number)), Entity, []);

        Assert.Equal(MatchOutcome.Unique, result.Outcome);
        Assert.Equal(MatchRule.EmployeeNumber, result.Rule);
        Assert.Equal(Takahashi, Assert.Single(result.Candidates).PersonId);
        Assert.Equal(1.0m, result.Candidates[0].Confidence);
    }

    [Theory(DisplayName = "数字だけの社員番号は、Excel で落ちた先頭の 0 を設定の桁数まで補って突合する")]
    [InlineData("123")]
    [InlineData("0123")]
    [InlineData("１２３")]
    [InlineData("123.0")]
    public void EmployeeNumber_ZeroPadding(string number) =>
        Assert.Equal(Numeric, Assert.Single(Matcher.Match(Row(("employee_number", number)), Entity, []).Candidates).PersonId);

    [Theory(DisplayName = "社用メールのゆれ（大文字小文字・全角・前後の空白）を吸収して突合する（社員番号が無い行）")]
    [InlineData("ichiro.takahashi@example.co.jp")]
    [InlineData("Ichiro.Takahashi@EXAMPLE.CO.JP")]
    [InlineData("ｉｃｈｉｒｏ.takahashi@example.co.jp ")]
    public void WorkEmail_Variants(string email)
    {
        var result = Matcher.Match(Row(("work_email", email)), Entity, []);

        Assert.Equal(MatchOutcome.Unique, result.Outcome);
        Assert.Equal(MatchRule.WorkEmail, result.Rule);
        Assert.False(result.Conflict);
        Assert.Equal(Takahashi, result.Candidates[0].PersonId);
    }

    [Fact(DisplayName = "同姓同名: 社員番号があれば、その人だけに突合する（同じ氏名・所属の別人と結合しない）")]
    public void SameName_ResolvedByEmployeeNumber()
    {
        var result = Matcher.Match(Row(("employee_number", "E0004"), ("full_name", "佐藤 花子")), Entity, [S1]);

        Assert.Equal(Sato2, Assert.Single(result.Candidates).PersonId);
        Assert.Equal(MatchRule.EmployeeNumber, result.Rule);
    }

    [Fact(DisplayName = "同姓同名: 社員番号が無く氏名と所属だけなら、複数候補として要確認にする")]
    public void SameName_WithoutEmployeeNumber_IsAmbiguous()
    {
        var result = Matcher.Match(Row(("full_name", "佐藤　花子")), Entity, [S1]);

        Assert.Equal(MatchOutcome.Ambiguous, result.Outcome);
        Assert.Equal(MatchRule.NameAndOrg, result.Rule);
        Assert.Equal(new[] { Sato1, Sato2 }.Order(), result.Candidates.Select(c => c.PersonId).Order());
    }

    [Fact(DisplayName = "氏名は異体字・空白のゆれを吸収するが、所属が違えば突合しない（氏名だけでは突合しない）")]
    public void NameAndOrg_RequiresOrg()
    {
        Assert.Equal(Takahashi, Assert.Single(Matcher.Match(Row(("full_name", "高橋一郎")), Entity, [S2]).Candidates).PersonId);
        Assert.Equal(MatchOutcome.None, Matcher.Match(Row(("full_name", "高橋一郎")), Entity, [S1]).Outcome);
        Assert.Equal(MatchOutcome.None, Matcher.Match(Row(("full_name", "高橋一郎")), Entity, []).Outcome);
    }

    [Fact(DisplayName = "社員番号が jin-ji に無いのに社用メールが既存の人のもの → 衝突（要確認）")]
    public void UnknownNumberWithKnownEmail_IsConflict()
    {
        var result = Matcher.Match(Row(("employee_number", "E0999"), ("work_email", "ichiro.takahashi@example.co.jp")), Entity, []);

        Assert.True(result.Conflict);
        Assert.True(result.EmployeeNumberUnknown);
    }

    [Fact(DisplayName = "提案: 同姓同名の行は、社員番号で突合した人の差分・新規の人・要確認に分かれ、誤って結合しない")]
    public void Planner_SameNameRows()
    {
        var rows = new[]
        {
            // 既存の佐藤 花子（E0004）の内線の変更
            Row(2, ("employee_number", "E0004"), ("full_name", "佐藤 花子"), ("extension_number", "1234"), ("org_name", "営業一課")),
            // 社員番号が新しい佐藤 花子（同姓同名の別人の新規入社）
            Row(3, ("employee_number", "E0100"), ("full_name", "佐藤 花子"), ("hire_date", "R8.10.1"), ("org_name", "営業一課")),
            // 社員番号の無い佐藤 花子（どちらか決められない）
            Row(4, ("full_name", "佐藤 花子"), ("org_name", "営業一課")),
        };

        var plan = ProposalPlanner.PlanPeople(Context(), rows);

        var update = Assert.Single(plan.Proposals, p => p.TargetKind == ProposalTargetKind.PersonVersion);
        Assert.Equal(Sato2, update.TargetId);
        Assert.Equal(ProposalOperation.Correct, update.Operation);
        Assert.Equal(new FieldChange(null, "1234"), update.Diff[ProposalFields.ExtensionNumber]);

        var create = Assert.Single(plan.Proposals, p => p.Operation == ProposalOperation.Create);
        Assert.Equal(ProposalStatus.Proposed, create.Status);
        Assert.Equal("E0100", create.Payload[ProposalFields.EmployeeNumber]);
        Assert.Equal(new[] { Sato1, Sato2 }.Order(), create.Basis.Candidates.Select(c => c.PersonId).Order());
        Assert.NotEqual(Sato1, create.TargetId);
        Assert.NotEqual(Sato2, create.TargetId);

        var review = Assert.Single(plan.Proposals, p => p.Operation == ProposalOperation.Review);
        Assert.Equal(ProposalStatus.NeedsReview, review.Status);
        Assert.Null(review.TargetId);
        Assert.Equal([4], review.Basis.RowNumbers);

        Assert.Contains(plan.Corrections, c => c.Issue == CorrectionIssue.MissingEmployeeNumber && c.SourceRowNumber == 4);
        Assert.DoesNotContain(plan.Proposals, p => p.Status == ProposalStatus.Proposed && p.TargetId == Sato1);
    }

    [Fact(DisplayName = "提案: 異体字だけの氏名の違いは確信度を下げて提案し、検索用の正規化表記も作り直す")]
    public void Planner_VariantOnlyNameChange()
    {
        var plan = ProposalPlanner.PlanPeople(Context(), [Row(2, ("employee_number", "E0002"), ("full_name", "高橋 一郎"))]);

        var proposal = Assert.Single(plan.Proposals);
        Assert.Equal(new FieldChange("髙橋", "高橋"), proposal.Diff[ProposalFields.FamilyName]);
        Assert.Equal(new FieldChange("髙橋 一郎", "高橋 一郎"), proposal.Diff[ProposalFields.DisplayName]);
        Assert.Equal("高橋一郎", proposal.Diff[ProposalFields.SearchName].New);
        Assert.True(proposal.Confidence < 1.0m);
        Assert.NotNull(proposal.Basis.Note);
    }

    [Fact(DisplayName = "提案: 異動（主務の所属の変更）・重複行・日付が解釈できない行・組織長の未設定")]
    public void Planner_TransferDuplicatesAndCorrections()
    {
        var rows = new[]
        {
            Row(2, ("employee_number", "E0002"), ("org_name", "営業１課"), ("position_title", "主任")),
            Row(3, ("employee_number", "E0003"), ("hire_date", "平成32年1月1日")),
            Row(4, ("employee_number", "000123"), ("full_name", "鈴木 次郎")),
            Row(5, ("employee_number", "123"), ("full_name", "鈴木 次郎")),
        };

        var plan = ProposalPlanner.PlanPeople(Context(effectiveDate: Today.AddDays(19)), rows);

        var transfer = Assert.Single(plan.Proposals, p => p.TargetKind == ProposalTargetKind.Assignment);
        Assert.Equal(ProposalOperation.Change, transfer.Operation);
        Assert.Equal(Today.AddDays(19), transfer.EffectiveDate);
        Assert.Equal(new FieldChange(S2.ToString("D"), S1.ToString("D")), transfer.Diff[ProposalFields.OrgUnitId]);
        Assert.Equal(new FieldChange(null, "主任"), transfer.Diff[ProposalFields.PositionTitle]);

        Assert.Contains(plan.Corrections, c => c.Issue == CorrectionIssue.InvalidDate && c.FieldKey == IngestFields.HireDate && c.SourceRowNumber == 3);
        Assert.Equal([4, 5], plan.Corrections.Where(c => c.Issue == CorrectionIssue.DuplicateRow).Select(c => c.SourceRowNumber!.Value).Order());
        Assert.DoesNotContain(plan.Proposals, p => p.TargetId == Numeric);

        // 補完提案: 行の所属組織（営業一課 = 長あり）は対象外、未設定の組織だけ
        Assert.DoesNotContain(plan.Proposals, p => p.Operation == ProposalOperation.Supplement && p.TargetId == S1);
        Assert.All(plan.Corrections, c => Assert.DoesNotContain("平成32", c.Message, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "提案: 所属する組織に組織長がいなければ補完提案（要確認）を作る")]
    public void Planner_SupplementsMissingHead()
    {
        var plan = ProposalPlanner.PlanPeople(Context(), [Row(2, ("employee_number", "E0002"), ("org_code", "S2"))]);

        var supplement = Assert.Single(plan.Proposals, p => p.Operation == ProposalOperation.Supplement);
        Assert.Equal(S2, supplement.TargetId);
        Assert.Equal(ProposalStatus.NeedsReview, supplement.Status);
    }

    private static PlanContext Context(DateOnly? effectiveDate = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), null, Snapshot, Entity, effectiveDate, Today, Normalizer.EmployeeNumbers);

    private static NormalizedRow Row(params (string Key, string Value)[] values) => Row(2, values);

    private static NormalizedRow Row(int number, params (string Key, string Value)[] values) =>
        Normalizer.Normalize(Guid.CreateVersion7(), new MappedRow(number, values.ToDictionary(v => v.Key, v => v.Value), new string('0', 64)));

    private static PersonRecord Person(Guid id, string number, string family, string given, string? email)
    {
        var version = new PersonVersion
        {
            VersionId = Guid.CreateVersion7(),
            ValidFrom = Today.AddYears(-5),
            FamilyName = family,
            GivenName = given,
            DisplayName = $"{family} {given}",
            WorkEmail = email,
            EmploymentType = "regular",
            HireDate = Today.AddYears(-5),
        };
        return new PersonRecord { Id = id, LegalEntityId = Entity, EmployeeNumber = number, Current = version, History = [version], Actions = [] };
    }
}
