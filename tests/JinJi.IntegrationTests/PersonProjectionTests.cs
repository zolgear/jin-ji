using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JinJi.Domain.Organization;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests;

/// <summary>投影（マスク済み DTO）・内示の秘匿・将来の予定の秘匿・氏名の履歴・退職者の公開期間（DB を使わない）。</summary>
public sealed partial class PersonProjectionTests
{
    private static readonly OrgFixture F = OrgFixture.Shared;
    private static readonly OrgSnapshot Full = F.BuildSnapshot();

    [Fact(DisplayName = "内示: 人事管理者以外には発令日前の発令を見せない（上長にも）")]
    public void FutureActions_AreHiddenFromNonAdmins()
    {
        var byManager = Project(F.User("sec1_head"), "s1_member")!;
        var byAdmin = Project(F.HrAdmin(), "s1_member")!;

        Assert.True(byManager.CanSee(PersonFields.PersonnelActions));
        Assert.DoesNotContain(byManager.PersonnelActions!, a => a.Id == F.FutureTransferActionId);
        Assert.All(byManager.PersonnelActions!, a => Assert.True(a.EffectiveDate <= F.Today));
        Assert.Contains(byAdmin.PersonnelActions!, a => a.Id == F.FutureTransferActionId);
    }

    [Fact(DisplayName = "内示: 本人にも発令日前の発令を見せない")]
    public void FutureActions_AreHiddenFromThePersonThemself()
    {
        var self = Project(F.User("s1_member"), "s1_member")!;

        Assert.DoesNotContain(self.PersonnelActions!, a => a.Id == F.FutureTransferActionId);
        Assert.Contains(self.PersonnelActions!, a => a.ActionType == PersonnelActionType.Hire);
    }

    [Fact(DisplayName = "内示: 将来の所属は今日の投影に現れない")]
    public void FutureAssignment_IsNotInTodaysProjection()
    {
        var view = Project(F.User("u1_member"), "s1_member")!;

        Assert.Equal([F.Unit("S1")], view.Assignments!.Select(a => a.OrgUnitId));
    }

    [Fact(DisplayName = "氏名の履歴: 人事管理者だけが過去の版を見られ、他は現在の氏名だけ")]
    public void NameHistory_IsOnlyForAdmins()
    {
        var byAdmin = Project(F.HrAdmin(), "u1_member")!;
        var bySelf = Project(F.User("u1_member"), "u1_member")!;
        var byOther = Project(F.User("u2_member"), "u1_member")!;

        Assert.Equal(["旧姓", "新姓"], byAdmin.NameHistory!.Select(h => h.FamilyName));
        Assert.Null(bySelf.NameHistory);
        Assert.False(bySelf.CanSee(PersonFields.NameHistory));
        Assert.Null(byOther.NameHistory);
        Assert.Equal("新姓", bySelf.FamilyName);
        Assert.Equal("新姓", byOther.FamilyName);
    }

    [Fact(DisplayName = "氏名の履歴: 過去の版を基準にしたスナップショットを渡されても、人事管理者以外には今日の版を返す")]
    public void NonAdmin_AlwaysGetsTodaysVersion_EvenFromPastSnapshot()
    {
        var past = F.BuildSnapshot(F.Today.AddDays(-200), F.Today.AddDays(-200));
        var projector = new PersonProjector(Evaluator(Full), past, F.Today);

        Assert.Equal("新姓", projector.Project(F.User("u2_member"), F.Person("u1_member"))!.FamilyName);
        Assert.Equal("旧姓", projector.Project(F.HrAdmin(), F.Person("u1_member"))!.FamilyName);
    }

    [Fact(DisplayName = "見えない項目は null で、見える項目だけに値が入る")]
    public void HiddenFields_AreNull()
    {
        var view = Project(F.User("u1_member"), "r1_member")!;

        Assert.False(view.CanSee(PersonFields.ExtensionNumber));
        Assert.Null(view.ExtensionNumber);
        Assert.Null(view.WorkMobilePhone);
        Assert.NotNull(view.DisplayName);
        Assert.NotNull(view.WorkEmail);
    }

    [Fact(DisplayName = "一時停止中・退職者・出向者の状態")]
    public void Statuses()
    {
        var viewer = F.HrAdmin();

        var suspended = Project(viewer, "suspended")!;
        Assert.Equal("suspended", suspended.AccountStatus);
        Assert.Equal(new SuspensionView(F.Today.AddDays(-30), F.Today.AddDays(60)), suspended.AccountSuspension);
        Assert.Null(Project(viewer, "u1_member")!.AccountSuspension);
        Assert.Equal("active", suspended.EmploymentStatus);
        Assert.Equal([F.Unit("S1")], suspended.Assignments!.Select(a => a.OrgUnitId));

        var retired = Project(viewer, "retired")!;
        Assert.Equal("terminated", retired.EmploymentStatus);
        Assert.Empty(retired.Assignments!);

        var seconded = Project(viewer, "seconded")!;
        var assignment = Assert.Single(seconded.Assignments!);
        Assert.Equal(SecondmentType.Outbound, assignment.Secondment);
        Assert.Equal(F.EntityB, assignment.LegalEntityId);
        Assert.Equal(F.EntityA, seconded.LegalEntity!.Id);
        Assert.Equal([F.Person("b_head")], seconded.Managers);

        var concurrent = Project(viewer, "concurrent")!;
        Assert.Equal([AssignmentType.Primary, AssignmentType.Concurrent], concurrent.Assignments!.Select(a => a.Type));
    }

    [Fact(DisplayName = "見える項目が一つも無い人・居ない人は投影しない")]
    public void NothingVisible_ReturnsNull()
    {
        Assert.Null(Project(F.User("u1_member", FieldLimit.Of([PersonFields.NameHistory])), "u2_member"));
        Assert.Null(new PersonProjector(Evaluator(Full), Full, F.Today).Project(F.HrAdmin(), Guid.NewGuid()));
        Assert.Null(Project(F.HrAdmin(), "future_hire"));
    }

    [Fact(DisplayName = "全項目に投影のプロパティがあり、項目を一つだけ許すとその値以外はすべて null")]
    public void EachField_MapsToExactlyOneProperty()
    {
        var properties = typeof(PersonView).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name is not nameof(PersonView.PersonId) and not nameof(PersonView.VisibleFields))
            .ToDictionary(p => SnakeCase(p.Name), StringComparer.Ordinal);

        Assert.Equal(PersonFields.All.Order(StringComparer.Ordinal), properties.Keys.Order(StringComparer.Ordinal));

        foreach (var field in PersonFields.All)
        {
            var view = Project(F.HrAdmin(FieldLimit.Of([field])), "u1_member")!;
            Assert.Equal([field], view.VisibleFields);
            foreach (var (name, property) in properties)
            {
                if (name != field)
                {
                    Assert.Null(property.GetValue(view));
                }
            }
        }
    }

    /// <summary>閲覧者の立場（PolicyEvaluationTests.Viewer の表記。"self" は対象者本人）。</summary>
    public static TheoryData<string> NonAdminViewers => ["sec1_head", "u1_member", "s2_member", "self", "agent:sec1_head", "agent:self", "service"];

    [Theory(DisplayName = "将来の予定: 人事管理者以外への応答には、業務日付より後の日付が一つも無い（上長・同僚・本人・エージェント・サービス）")]
    [MemberData(nameof(NonAdminViewers))]
    public void NonAdmin_NeverSeesFutureDates(string viewerSpec)
    {
        foreach (var snapshot in new[] { Full, F.BuildSnapshot(F.Today.AddDays(-200), F.Today) })
        {
            var projector = new PersonProjector(Evaluator(Full), snapshot, F.Today);
            foreach (var person in F.People)
            {
                var spec = viewerSpec.Replace("self", F.PersonKey(person.Id), StringComparison.Ordinal);
                var view = projector.Project(PolicyEvaluationTests.Viewer(spec, FieldLimit.Unrestricted), person.Id);
                Assert.DoesNotContain(DatesIn(view), d => d > F.Today);
            }
        }
    }

    [Fact(DisplayName = "将来の予定: 人事管理者には終了予定日をそのまま返す（上のテストの前提の確認）")]
    public void Admin_SeesFutureEndDates()
    {
        Assert.Contains(F.People, p => DatesIn(Project(F.HrAdmin(), F.PersonKey(p.Id))).Any(d => d > F.Today));
        Assert.Equal(F.Today.AddDays(10), Project(F.HrAdmin(), "s1_member")!.Assignments!.Single().ValidUntil);
        Assert.Equal(F.Today.AddDays(60), Project(F.HrAdmin(), "suspended")!.AccountSuspension!.Until);
        Assert.Equal(F.Today.AddDays(15), Project(F.HrAdmin(), "leaving")!.TerminationDate);
    }

    [Theory(DisplayName = "将来の予定: 異動前の所属の終了日は無期限として返す（上長・同僚・本人）")]
    [InlineData("sec1_head")]
    [InlineData("u1_member")]
    [InlineData("s1_member")]
    public void FutureAssignmentEnd_IsHidden(string viewer)
    {
        var assignment = Assert.Single(Project(F.User(viewer), "s1_member")!.Assignments!);

        Assert.Equal(F.Unit("S1"), assignment.OrgUnitId);
        Assert.Equal(F.LongAgo, assignment.ValidFrom);
        Assert.Null(assignment.ValidUntil);
    }

    [Theory(DisplayName = "将来の予定: 一時停止の終了予定日は本人にも返さない（一時停止中であることと開始日は返す）")]
    [InlineData("suspended")]
    [InlineData("sec1_head")]
    [InlineData("u1_member")]
    public void SuspensionEnd_IsHidden(string viewer)
    {
        var view = Project(F.User(viewer), "suspended")!;

        Assert.Equal("suspended", view.AccountStatus);
        Assert.Equal(new SuspensionView(F.Today.AddDays(-30), null), view.AccountSuspension);
    }

    [Fact(DisplayName = "将来の予定: 退職予定者の退職年月日は返さず、退職済みの人の退職年月日は返す")]
    public void FutureTermination_IsHidden()
    {
        var leaving = Project(F.User("s2_member"), "leaving")!;
        Assert.True(leaving.CanSee(PersonFields.TerminationDate));
        Assert.Null(leaving.TerminationDate);
        Assert.Equal("active", leaving.EmploymentStatus);
        Assert.Null(Assert.Single(leaving.Assignments!).ValidUntil);
        Assert.Null(Project(F.User("leaving"), "leaving")!.TerminationDate);

        Assert.Equal(F.Today.AddDays(-31), Project(F.User("s2_member"), "retired")!.TerminationDate);
    }

    [Fact(DisplayName = "過去の時点: 既に終わった所属の終了日は人事管理者以外にも返す")]
    public void PastEndDates_AreDisclosed()
    {
        var past = F.BuildSnapshot(F.Today.AddDays(-60), F.Today);
        var view = new PersonProjector(Evaluator(Full), past, F.Today).Project(F.User("u1_member"), F.Person("retired"))!;

        Assert.Equal(F.Today.AddDays(-30), Assert.Single(view.Assignments!).ValidUntil);
    }

    [Theory(DisplayName = "退職者の一般公開の終了: 公開期間を過ぎた人は人事管理者以外（本人を含む）には投影しない")]
    [InlineData("u1_member", "long_retired", 90, false)]
    [InlineData("self", "long_retired", 90, false)]
    [InlineData("service", "long_retired", 90, false)]
    [InlineData("agent:sec1_head", "long_retired", 90, false)]
    [InlineData("hr:hr_admin", "long_retired", 90, true)]
    [InlineData("u1_member", "retired", 90, true)]
    // retired の最終在籍日は 31 日前: 31 日までは公開、30 日なら公開しない
    [InlineData("u1_member", "retired", 31, true)]
    [InlineData("u1_member", "retired", 30, false)]
    [InlineData("hr:hr_admin", "retired", 0, true)]
    [InlineData("u1_member", "leaving", 0, true)]
    public void RetiredPeople_AreHiddenAfterPublicPeriod(string viewerSpec, string person, int days, bool visible)
    {
        var viewer = PolicyEvaluationTests.Viewer(viewerSpec == "self" ? person : viewerSpec, FieldLimit.Unrestricted);
        var view = new PersonProjector(Evaluator(Full), Full, F.Today, days).Project(viewer, F.Person(person));

        Assert.Equal(visible, view is not null);
    }

    private static List<DateOnly> DatesIn(PersonView? view) =>
    [
        .. DatePattern().Matches(JsonSerializer.Serialize(view))
            .Select(m => DateOnly.ParseExact(m.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
    ];

    [GeneratedRegex("\"(\\d{4}-\\d{2}-\\d{2})\"")]
    private static partial Regex DatePattern();

    private static PolicyEvaluator Evaluator(OrgSnapshot relations) => new(PolicyEvaluationTests.Policies, new RelationEvaluator(relations));

    private static PersonView? Project(ViewerContext viewer, string person) =>
        new PersonProjector(Evaluator(Full), Full, F.Today).Project(viewer, F.Person(person));

    private static string SnakeCase(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsUpper(c) && builder.Length > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }
}
