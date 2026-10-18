using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Application.People;
using JinJi.Application.Policies;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests;

/// <summary>Application 層のサービス（時点指定・監査・ポリシーの起案と承認）。DB の代わりにメモリ上の実装を使う。</summary>
public sealed class ApplicationServiceTests
{
    private static readonly OrgFixture F = OrgFixture.Shared;
    private readonly FixtureSnapshotProvider _snapshots = new(F);
    private readonly InMemoryPolicyRuleStore _policies = new(PolicyEvaluationTests.Policies.Rules);
    private readonly RecordingAuditSink _audit = new();
    private readonly FixedTimeProvider _time = new(F.Today);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PersonDirectoryService Directory => new(_snapshots, _policies, new AuditWriter(_audit, _time), _time);

    private PolicyAdministrationService Administration => new(_policies, _snapshots, new AuditWriter(_audit, _time), _time);

    /// <summary>hr_admin とは別の人事管理者（承認者）。</summary>
    private static ViewerContext HrHead => ViewerContext.ForUser(F.Person("hr_head"), [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);

    /// <summary>3 人目の人事管理者（人事管理者が例外の対象者になる場合の承認者）。</summary>
    private static ViewerContext OtherHrAdmin => ViewerContext.ForUser(F.Person("div2_head"), [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);

    [Fact(DisplayName = "未来の時点は人事管理者以外には拒否し、denied を監査ログに残す")]
    public async Task FutureAsOf_IsDeniedForNonAdmin()
    {
        var requested = new[] { F.Person("future_hire"), F.Person("s1_member") };

        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            Directory.GetPeopleAsync(F.User("sec1_head"), requested, F.Today.AddDays(1), Ct));

        Assert.Empty(_snapshots.Calls);
        var record = Assert.Single(_audit.Records);
        Assert.Equal(AuditOutcome.Denied, record.Outcome);
        Assert.Equal(PersonDirectoryService.ReadAction, record.Action);
        Assert.Equal(requested.Order(), record.SubjectPersonIds);
        Assert.Equal(F.Person("sec1_head"), record.ActorPersonId);
    }

    [Fact(DisplayName = "人事管理者は未来の時点を見られる（入社予定者・将来の所属）")]
    public async Task FutureAsOf_IsAllowedForAdmin()
    {
        var views = await Directory.GetPeopleAsync(F.HrAdmin(), [F.Person("future_hire"), F.Person("s1_member")], F.Today.AddDays(20), Ct);

        Assert.Equal(2, views.Count);
        Assert.Equal([F.Unit("S2")], views.Single(v => v.PersonId == F.Person("s1_member")).Assignments!.Select(a => a.OrgUnitId));
        Assert.Equal(AuditOutcome.Success, Assert.Single(_audit.Records).Outcome);
    }

    [Fact(DisplayName = "過去の時点: 人事管理者以外は属性を今日の版で、人事管理者はその時点の版で読む")]
    public async Task PastAsOf_UsesTodaysAttributesForNonAdmin()
    {
        var past = F.Today.AddDays(-200);

        var byUser = await Directory.GetPeopleAsync(F.User("u2_member"), [F.Person("u1_member")], past, Ct);
        var byAdmin = await Directory.GetPeopleAsync(F.HrAdmin(), [F.Person("u1_member")], past, Ct);

        Assert.Equal("新姓", Assert.Single(byUser).FamilyName);
        Assert.Equal("旧姓", Assert.Single(byAdmin).FamilyName);
        Assert.Contains(_snapshots.Calls, c => !c.Viewer.IsHrAdmin && c.AsOf == past && c.AttributesAsOf == F.Today);
        Assert.Contains(_snapshots.Calls, c => c.Viewer.IsHrAdmin && c.AsOf == past && c.AttributesAsOf == past);
        // 関係は常に今日の組織で評価する
        Assert.All(_snapshots.Calls.Where(c => c.AsOf == F.Today), c => Assert.Equal(F.Today, c.AttributesAsOf));
    }

    [Fact(DisplayName = "参照はリクエスト単位で 1 件、返した人の ID の集合を記録する（居ない人は記録しない）")]
    public async Task Read_IsAuditedOncePerRequest()
    {
        var unknown = Guid.NewGuid();
        var views = await Directory.GetPeopleAsync(
            F.User("u1_member"),
            [F.Person("u2_member"), F.Person("r1_member"), unknown, F.Person("future_hire")],
            null,
            Ct);

        Assert.Equal([F.Person("u2_member"), F.Person("r1_member")], views.Select(v => v.PersonId));
        var record = Assert.Single(_audit.Records);
        Assert.Equal(views.Select(v => v.PersonId).Order(), record.SubjectPersonIds);
        Assert.Equal("person", record.TargetType);
        Assert.Equal(ActorType.User, record.ActorType);
        Assert.Equal(Channel.Ui, record.Channel);
    }

    [Fact(DisplayName = "一覧: 人事管理者以外には入社予定者が現れず、全員分が 1 件の監査ログになる")]
    public async Task List_HidesFutureHireAndAuditsOnce()
    {
        var views = await Directory.ListPeopleAsync(F.User("u1_member"), null, Ct);

        Assert.DoesNotContain(views, v => v.PersonId == F.Person("future_hire"));
        Assert.Contains(views, v => v.PersonId == F.Person("retired"));
        var record = Assert.Single(_audit.Records);
        Assert.Equal(PersonDirectoryService.ListAction, record.Action);
        Assert.Equal(views.Count, record.SubjectPersonIds.Count);
    }

    [Fact(DisplayName = "AI エージェントの参照は、クライアントと委任元で記録する")]
    public async Task AgentRead_IsAuditedWithDelegator()
    {
        var agent = ViewerContext.ForAgent("assistant", F.Person("u2_member"), [], Channel.Mcp, FieldLimit.Of([PersonFields.DisplayName]), "req-1");

        var views = await Directory.GetPeopleAsync(agent, [F.Person("u1_member")], null, Ct);

        Assert.Equal([PersonFields.DisplayName], Assert.Single(views).VisibleFields);
        var record = Assert.Single(_audit.Records);
        Assert.Equal(ActorType.Agent, record.ActorType);
        Assert.Null(record.ActorPersonId);
        Assert.Equal("assistant", record.ActorClientId);
        Assert.Equal(F.Person("u2_member"), record.OnBehalfOfPersonId);
        Assert.Equal(Channel.Mcp, record.Channel);
        Assert.Equal("req-1", record.RequestId);
    }

    [Fact(DisplayName = "読み込みに失敗したら failed を監査ログに残して例外を伝える")]
    public async Task Failure_IsAudited()
    {
        _snapshots.Failure = new InvalidOperationException("DB に接続できない");

        await Assert.ThrowsAsync<InvalidOperationException>(() => Directory.GetPeopleAsync(F.User("u1_member"), [F.Person("u2_member")], null, Ct));

        var record = Assert.Single(_audit.Records);
        Assert.Equal(AuditOutcome.Failed, record.Outcome);
        Assert.Equal([F.Person("u2_member")], record.SubjectPersonIds);
    }

    [Fact(DisplayName = "ポリシー: 起案者は自分の起案を承認できず、別の人事管理者なら承認できる")]
    public async Task Policy_ProposerCannotApprove()
    {
        var proposer = F.HrAdmin();
        var approver = HrHead;
        var rule = PolicyEvaluationTests.Rule(PolicyTargetKind.All, null, [PersonFields.Site], RelationScope.LegalEntity);

        await Administration.ProposeAsync(proposer, rule, Ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ApproveAsync(proposer, rule.Id, Ct));
        Assert.Equal(PolicyRuleStatus.Proposed, _policies.Rules[rule.Id].Status);

        await Administration.ApproveAsync(approver, rule.Id, Ct);
        Assert.Equal(PolicyRuleStatus.Approved, _policies.Rules[rule.Id].Status);

        Assert.Equal(
            [AuditOutcome.Success, AuditOutcome.Denied, AuditOutcome.Success],
            _audit.Records.Select(r => r.Outcome));
    }

    [Fact(DisplayName = "ポリシー: AI エージェントの起案は委任元の起案として扱い、委任元は承認できない")]
    public async Task Policy_AgentProposalCountsAsDelegators()
    {
        var agent = ViewerContext.ForAgent("assistant", F.Person("hr_admin"), [Roles.HrAdmin], Channel.Mcp, FieldLimit.Unrestricted);
        var rule = PolicyEvaluationTests.Rule(PolicyTargetKind.All, null, [PersonFields.JobType], RelationScope.LegalEntity);

        await Administration.ProposeAsync(agent, rule, Ct);

        Assert.Equal(F.Person("hr_admin"), _policies.Rules[rule.Id].ProposedByPersonId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ApproveAsync(F.HrAdmin(), rule.Id, Ct));
    }

    [Fact(DisplayName = "ポリシー: 人事管理者以外・人に紐付かない操作者は起案できない")]
    public async Task Policy_NonAdminCannotPropose()
    {
        var rule = PolicyEvaluationTests.Rule(PolicyTargetKind.Person, "u1_member", [PersonFields.WorkEmail], RelationScope.None);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ProposeAsync(F.User("u1_member"), rule, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ProposeAsync(ViewerContext.ForSystem(Channel.Worker), rule, Ct));

        Assert.DoesNotContain(rule.Id, _policies.Rules.Keys);
        Assert.All(_audit.Records, r => Assert.Equal(AuditOutcome.Denied, r.Outcome));
        Assert.Equal(2, _audit.Records.Count);
    }

    [Fact(DisplayName = "ポリシー: 個人単位の例外で広げる起案は拒否し、狭める起案は受け付ける")]
    public async Task Policy_WideningExceptionIsRejected()
    {
        var widening = PolicyEvaluationTests.Rule(PolicyTargetKind.Person, "u1_member", [PersonFields.WorkMobilePhone], RelationScope.Group);
        var narrowing = PolicyEvaluationTests.Rule(PolicyTargetKind.Person, "u1_member", [PersonFields.WorkMobilePhone], RelationScope.SameLevel("section"));

        await Assert.ThrowsAsync<PolicyValidationException>(() => Administration.ProposeAsync(F.HrAdmin(), widening, Ct));
        await Administration.ProposeAsync(F.HrAdmin(), narrowing, Ct);

        Assert.DoesNotContain(widening.Id, _policies.Rules.Keys);
        Assert.Contains(narrowing.Id, _policies.Rules.Keys);
        Assert.Equal([AuditOutcome.Denied, AuditOutcome.Success], _audit.Records.Select(r => r.Outcome));
        Assert.Equal([F.Person("u1_member")], _audit.Records[0].SubjectPersonIds);
    }

    [Fact(DisplayName = "ポリシー: 承認待ちの間に他の規則が狭まって広げる例外になったら、承認時に拒否する")]
    public async Task Policy_ExceptionIsRevalidatedOnApproval()
    {
        var exception = PolicyEvaluationTests.Rule(PolicyTargetKind.Person, "u1_member", [PersonFields.JobType], RelationScope.SameLevel("department"));
        await Administration.ProposeAsync(F.HrAdmin(), exception, Ct);

        var stricter = PolicyEvaluationTests.Rule(PolicyTargetKind.All, null, [PersonFields.JobType], RelationScope.SameLevel("section"));
        _policies.Rules.Add(stricter.Id, new StoredPolicyRule(stricter, PolicyRuleStatus.Approved, Guid.Empty, Guid.Empty));

        await Assert.ThrowsAsync<PolicyValidationException>(() => Administration.ApproveAsync(HrHead, exception.Id, Ct));
        Assert.Equal(PolicyRuleStatus.Proposed, _policies.Rules[exception.Id].Status);
    }

    [Fact(DisplayName = "ポリシー: 人事管理者でも自分を対象にした個人単位の例外は起案できない（本人の裁量にしない）")]
    public async Task Policy_SubjectCannotProposeOwnException()
    {
        var own = PolicyEvaluationTests.Rule(PolicyTargetKind.Person, "hr_admin", [PersonFields.WorkMobilePhone], RelationScope.SameLevel("section"));
        var agent = ViewerContext.ForAgent("assistant", F.Person("hr_admin"), [Roles.HrAdmin], Channel.Mcp, FieldLimit.Unrestricted);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ProposeAsync(F.HrAdmin(), own, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ProposeAsync(agent, own, Ct));

        Assert.DoesNotContain(own.Id, _policies.Rules.Keys);
        Assert.All(_audit.Records, r => Assert.Equal(AuditOutcome.Denied, r.Outcome));
        Assert.All(_audit.Records, r => Assert.Equal([F.Person("hr_admin")], r.SubjectPersonIds));
    }

    [Fact(DisplayName = "ポリシー: 個人単位の例外の対象者本人は、他人が起案した自分の例外を承認できない")]
    public async Task Policy_SubjectCannotApproveOwnException()
    {
        var forHrHead = PolicyEvaluationTests.Rule(PolicyTargetKind.Person, "hr_head", [PersonFields.WorkMobilePhone], RelationScope.SameLevel("section"));
        await Administration.ProposeAsync(F.HrAdmin(), forHrHead, Ct);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ApproveAsync(HrHead, forHrHead.Id, Ct));

        Assert.Equal(PolicyRuleStatus.Proposed, _policies.Rules[forHrHead.Id].Status);
        await Administration.ApproveAsync(OtherHrAdmin, forHrHead.Id, Ct);
        Assert.Equal(PolicyRuleStatus.Approved, _policies.Rules[forHrHead.Id].Status);
    }

    [Fact(DisplayName = "ポリシーの廃止: 起案 → 別人の承認で廃止され、承認までは有効のまま")]
    public async Task Retirement_RequiresApprovalByAnotherPerson()
    {
        var rule = PolicyEvaluationTests.Policies.Rules[0];

        await Administration.ProposeRetirementAsync(F.HrAdmin(), rule.Id, Ct);
        Assert.True(_policies.Rules[rule.Id].IsRetirementProposed);
        Assert.Contains(rule, (await _policies.LoadActiveAsync(F.HrAdmin(), Ct)).Rules);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ApproveRetirementAsync(F.HrAdmin(), rule.Id, Ct));
        Assert.Equal(PolicyRuleStatus.Approved, _policies.Rules[rule.Id].Status);

        await Administration.ApproveRetirementAsync(HrHead, rule.Id, Ct);
        Assert.Equal(PolicyRuleStatus.Retired, _policies.Rules[rule.Id].Status);
        Assert.DoesNotContain(rule, (await _policies.LoadActiveAsync(F.HrAdmin(), Ct)).Rules);

        Assert.Equal(
            [
                (PolicyAdministrationService.ProposeRetirementAction, AuditOutcome.Success),
                (PolicyAdministrationService.ApproveRetirementAction, AuditOutcome.Denied),
                (PolicyAdministrationService.ApproveRetirementAction, AuditOutcome.Success),
            ],
            _audit.Records.Select(r => (r.Action, r.Outcome)));
    }

    [Fact(DisplayName = "ポリシーの廃止: 人事管理者以外は起案できず、起案の無い規則・二重の起案・起案中の規則の廃止は失敗する")]
    public async Task Retirement_InvalidRequestsAreRejected()
    {
        var rule = PolicyEvaluationTests.Policies.Rules[0];
        var proposed = PolicyEvaluationTests.Rule(PolicyTargetKind.All, null, [PersonFields.JobType], RelationScope.LegalEntity);
        await Administration.ProposeAsync(F.HrAdmin(), proposed, Ct);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ProposeRetirementAsync(F.User("u1_member"), rule.Id, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ProposeRetirementAsync(ViewerContext.ForSystem(Channel.Worker), rule.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Administration.ApproveRetirementAsync(HrHead, rule.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Administration.ProposeRetirementAsync(F.HrAdmin(), proposed.Id, Ct));

        await Administration.ProposeRetirementAsync(F.HrAdmin(), rule.Id, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Administration.ProposeRetirementAsync(HrHead, rule.Id, Ct));
        Assert.Equal(F.Person("hr_admin"), _policies.Rules[rule.Id].RetirementProposedByPersonId);
    }

    [Fact(DisplayName = "ポリシーの廃止: 個人単位の例外の対象者本人は、自分の例外の廃止を起案・承認できない")]
    public async Task Retirement_SubjectCannotRetireOwnException()
    {
        var forHrHead = PolicyEvaluationTests.Rule(PolicyTargetKind.Person, "hr_head", [PersonFields.WorkMobilePhone], RelationScope.SameLevel("section"));
        await Administration.ProposeAsync(F.HrAdmin(), forHrHead, Ct);
        await Administration.ApproveAsync(OtherHrAdmin, forHrHead.Id, Ct);

        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ProposeRetirementAsync(HrHead, forHrHead.Id, Ct));
        await Administration.ProposeRetirementAsync(F.HrAdmin(), forHrHead.Id, Ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ApproveRetirementAsync(HrHead, forHrHead.Id, Ct));

        await Administration.ApproveRetirementAsync(OtherHrAdmin, forHrHead.Id, Ct);
        Assert.Equal(PolicyRuleStatus.Retired, _policies.Rules[forHrHead.Id].Status);
    }

    [Fact(DisplayName = "退職者: 公開期間を過ぎた人は人事管理者以外の一覧・参照に出ない（期間は設定で変えられる）")]
    public async Task RetiredPeople_DisappearAfterPublicPeriod()
    {
        var requested = new[] { F.Person("retired"), F.Person("long_retired") };

        Assert.DoesNotContain(await Directory.ListPeopleAsync(F.User("u1_member"), null, Ct), v => v.PersonId == F.Person("long_retired"));
        Assert.Equal([F.Person("retired")], (await Directory.GetPeopleAsync(F.User("u1_member"), requested, null, Ct)).Select(v => v.PersonId));
        Assert.Equal([F.Person("retired")], (await Directory.GetPeopleAsync(ViewerContext.ForService("svc", FieldLimit.Unrestricted), requested, null, Ct)).Select(v => v.PersonId));
        // 過去の時点を指定しても、判定は今日の退職年月日で行う
        Assert.Equal([F.Person("retired")], (await Directory.GetPeopleAsync(F.User("u1_member"), requested, F.Today.AddDays(-300), Ct)).Select(v => v.PersonId));
        Assert.Equal(requested.Order(), (await Directory.GetPeopleAsync(F.HrAdmin(), requested, null, Ct)).Select(v => v.PersonId).Order());

        // 期間を 30 日にすると先月退職した人も出なくなり、300 日にすると 200 日前に退職した人も出る
        var shorter = new PersonDirectoryService(_snapshots, _policies, new AuditWriter(_audit, _time), _time, new PersonDirectoryOptions { RetiredPublicDays = 30 });
        Assert.Empty(await shorter.GetPeopleAsync(F.User("u1_member"), requested, null, Ct));
        var longer = new PersonDirectoryService(_snapshots, _policies, new AuditWriter(_audit, _time), _time, new PersonDirectoryOptions { RetiredPublicDays = 300 });
        Assert.Equal(2, (await longer.GetPeopleAsync(F.User("u1_member"), requested, null, Ct)).Count);

        // 監査ログには返した人だけが残る
        Assert.Equal([F.Person("retired")], _audit.Records[1].SubjectPersonIds);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PersonDirectoryService(
            _snapshots, _policies, new AuditWriter(_audit, _time), _time, new PersonDirectoryOptions { RetiredPublicDays = -1 }));
    }

    [Fact(DisplayName = "閲覧者コンテキストの不変条件")]
    public void ViewerContext_Invariants()
    {
        Assert.Throws<ArgumentException>(() => ViewerContext.ForUser(Guid.NewGuid(), ["admin"], Channel.Ui, FieldLimit.Unrestricted));
        Assert.Throws<ArgumentException>(() => ViewerContext.ForUser(Guid.NewGuid(), [], Channel.Worker, FieldLimit.Unrestricted));
        Assert.Throws<ArgumentException>(() => ViewerContext.ForAgent(" ", Guid.NewGuid(), [], Channel.Mcp, FieldLimit.Unrestricted));
        Assert.Throws<ArgumentException>(() => ViewerContext.ForSystem(Channel.Mcp));

        var service = ViewerContext.ForService("attendance", FieldLimit.Of([PersonFields.EmployeeNumber]));
        Assert.Null(service.EffectivePersonId);
        Assert.False(service.IsHrAdmin);
        Assert.True(ViewerContext.ForSystem(Channel.Worker).IsHrAdmin);
    }
}
