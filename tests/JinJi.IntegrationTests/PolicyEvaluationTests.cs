using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests;

/// <summary>
/// ポリシーの合成と項目の判定（§5.2〜5.3）。閲覧者の立場 × 対象者 × 項目 × ポリシーのテーブル駆動（DB を使わない）。
/// </summary>
public sealed class PolicyEvaluationTests
{
    private static readonly OrgFixture F = OrgFixture.Shared;
    private static readonly RelationEvaluator Relations = new(F.BuildSnapshot());

    /// <summary>
    /// テスト用のポリシー:
    /// <list type="bullet">
    /// <item>社用携帯は部内（全員）</item>
    /// <item>特命プロジェクト室（R1）の人の内線・社用携帯は課内（組織単位）</item>
    /// <item>入社年月日は法人内（全員）</item>
    /// <item>発令は配下だけ（全員）</item>
    /// <item>管理本部（D2）配下の人の社用メールは本部内（組織単位・配下を含む）</item>
    /// <item>営業部（DP1）直属の人の拠点・リモートワークは本人だけ（組織単位・配下を含まない）</item>
    /// <item>u2_member の内線は誰にも見せない（個人単位の例外。人事管理者が起案し、別の人事管理者が承認したもの）</item>
    /// </list>
    /// </summary>
    internal static readonly PolicySet Policies = new(
    [
        Rule(PolicyTargetKind.All, null, [PersonFields.WorkMobilePhone], RelationScope.SameLevel("department")),
        Rule(PolicyTargetKind.OrgUnit, "R1", [PersonFields.ExtensionNumber, PersonFields.WorkMobilePhone], RelationScope.SameLevel("section")),
        Rule(PolicyTargetKind.All, null, [PersonFields.HireDate], RelationScope.LegalEntity),
        Rule(PolicyTargetKind.All, null, [PersonFields.PersonnelActions], RelationScope.Subordinates),
        Rule(PolicyTargetKind.OrgUnit, "D2", [PersonFields.WorkEmail], RelationScope.SameLevel("division")),
        Rule(PolicyTargetKind.OrgUnit, "DP1", [PersonFields.Site, PersonFields.RemoteWork], RelationScope.Self) with { IncludeDescendants = false },
        Rule(PolicyTargetKind.Person, "u2_member", [PersonFields.ExtensionNumber], RelationScope.None),
    ]);

    private static readonly PolicyEvaluator Evaluator = new(Policies, Relations);

    /// <summary>閲覧者の立場、対象者、項目、期待値。閲覧者は "hr:" 前置で人事管理者、"service" で社内システム、"agent:" 前置で AI エージェント（委任元）。</summary>
    public static TheoryData<string, string, string, bool> Cases => new()
    {
        // 既定は全項目 group 公開
        { "u1_member", "u2_member", PersonFields.DisplayName, true },
        { "b_member", "u1_member", PersonFields.DisplayName, true },
        { "service", "u1_member", PersonFields.DisplayName, true },
        { "service", "u1_member", PersonFields.EmployeeNumber, true },
        // 項目単位: 社用携帯は部内
        { "u2_member", "u1_member", PersonFields.WorkMobilePhone, true },
        { "s2_member", "u1_member", PersonFields.WorkMobilePhone, true },
        { "dept2_head", "u1_member", PersonFields.WorkMobilePhone, false },
        { "dp2_member", "u3_member", PersonFields.WorkMobilePhone, true },
        { "service", "u1_member", PersonFields.WorkMobilePhone, false },
        { "div1_head", "u1_member", PersonFields.WorkMobilePhone, false },
        // 兼務は和集合: concurrent（主務 S2・兼務 U1）は営業部内
        { "concurrent", "u2_member", PersonFields.WorkMobilePhone, true },
        // 組織単位と項目単位の積: R1 の社用携帯は部内（R1 = 部相当）かつ課内（R1）
        { "r1_member", "r1_head", PersonFields.WorkMobilePhone, true },
        { "r1_member", "r1_head", PersonFields.ExtensionNumber, true },
        { "s3_member", "r1_member", PersonFields.ExtensionNumber, false },
        { "div1_head", "r1_member", PersonFields.ExtensionNumber, false },
        { "u1_member", "r1_member", PersonFields.ExtensionNumber, false },
        { "u1_member", "r1_member", PersonFields.DisplayName, true },
        // 組織単位の規則は対象組織の人だけに効く
        { "s3_member", "u1_member", PersonFields.ExtensionNumber, true },
        // 個人単位の例外: u2_member の内線は誰にも見せない（本人と人事管理者は除く）
        { "u1_member", "u2_member", PersonFields.ExtensionNumber, false },
        { "sec1_head", "u2_member", PersonFields.ExtensionNumber, false },
        { "u2_member", "u2_member", PersonFields.ExtensionNumber, true },
        { "hr:hr_admin", "u2_member", PersonFields.ExtensionNumber, true },
        { "u1_member", "u2_member", PersonFields.WorkMobilePhone, true },
        // 法人内: 出向者は雇用元と出向先の両方の法人内
        { "u1_member", "seconded", PersonFields.HireDate, true },
        { "b_member", "seconded", PersonFields.HireDate, true },
        { "b_member", "u1_member", PersonFields.HireDate, false },
        { "seconded", "u1_member", PersonFields.HireDate, true },
        { "service", "u1_member", PersonFields.HireDate, false },
        // 関係スコープ（配下）: 発令は上長系列の人だけ
        { "sec1_head", "u1_member", PersonFields.PersonnelActions, true },
        { "president", "u1_member", PersonFields.PersonnelActions, true },
        { "u2_member", "u1_member", PersonFields.PersonnelActions, false },
        { "u1_member", "sec1_head", PersonFields.PersonnelActions, false },
        { "u1_member", "u1_member", PersonFields.PersonnelActions, true },
        // 組織単位（配下を含む）: 管理本部の人の社用メールは本部内
        { "div2_head", "hr_admin", PersonFields.WorkEmail, true },
        { "u1_member", "hr_admin", PersonFields.WorkEmail, false },
        { "u1_member", "u2_member", PersonFields.WorkEmail, true },
        // 組織単位（配下を含まない）: 営業部直属（dept1_head）の勤務地は本人だけ。配下の課の人には効かない
        { "u1_member", "dept1_head", PersonFields.Site, false },
        { "dept1_head", "dept1_head", PersonFields.Site, true },
        { "u1_member", "sec1_head", PersonFields.Site, true },
        // 一時停止中・退職者
        { "u1_member", "suspended", PersonFields.WorkMobilePhone, true },
        { "u1_member", "suspended", PersonFields.AccountStatus, true },
        { "s2_member", "retired", PersonFields.WorkMobilePhone, false },
        { "u1_member", "retired", PersonFields.HireDate, true },
        { "u1_member", "retired", PersonFields.EmploymentStatus, true },
        // 人事管理者は公開範囲の制限を受けない
        { "hr:hr_admin", "u1_member", PersonFields.WorkMobilePhone, true },
        { "hr:hr_admin", "r1_member", PersonFields.ExtensionNumber, true },
        { "hr:u1_member", "b_member", PersonFields.HireDate, true },
        // 氏名の履歴は人事管理者だけ（本人にも見せない）
        { "hr:hr_admin", "u1_member", PersonFields.NameHistory, true },
        { "u1_member", "u1_member", PersonFields.NameHistory, false },
        { "president", "u1_member", PersonFields.NameHistory, false },
        { "service", "u1_member", PersonFields.NameHistory, false },
        // AI エージェントは委任元ユーザーの権限で判定する
        { "agent:u2_member", "u1_member", PersonFields.WorkMobilePhone, true },
        { "agent:dept2_head", "u1_member", PersonFields.WorkMobilePhone, false },
        { "agent:u2_member", "u2_member", PersonFields.ExtensionNumber, true },
        { "agent:hr:hr_admin", "u2_member", PersonFields.ExtensionNumber, true },
    };

    [Theory(DisplayName = "項目の判定")]
    [MemberData(nameof(Cases))]
    public void VisibleFields(string viewer, string target, string field, bool expected)
    {
        var visible = Evaluator.VisibleFields(Viewer(viewer, FieldLimit.Unrestricted), F.Person(target));

        Assert.Equal(expected, visible.Contains(field));
    }

    /// <summary>閲覧者、対象者、クライアントの項目上限、期待する項目集合。</summary>
    public static TheoryData<string, string, string[], string[]> LimitCases => new()
    {
        // 上限 ∩ ポリシー
        { "u2_member", "u1_member", [PersonFields.DisplayName, PersonFields.WorkMobilePhone], [PersonFields.DisplayName, PersonFields.WorkMobilePhone] },
        { "dept2_head", "u1_member", [PersonFields.DisplayName, PersonFields.WorkMobilePhone], [PersonFields.DisplayName] },
        // 人事管理者・本人にも上限は効く
        { "hr:hr_admin", "u1_member", [PersonFields.EmployeeNumber], [PersonFields.EmployeeNumber] },
        { "u1_member", "u1_member", [PersonFields.EmployeeNumber, PersonFields.NameHistory], [PersonFields.EmployeeNumber] },
        // 勤怠システムの例: 社員番号・所属・上長・在籍状態だけ
        {
            "service", "suspended",
            [PersonFields.EmployeeNumber, PersonFields.Assignments, PersonFields.Managers, PersonFields.EmploymentStatus, PersonFields.AccountStatus],
            [PersonFields.EmployeeNumber, PersonFields.Assignments, PersonFields.Managers, PersonFields.EmploymentStatus, PersonFields.AccountStatus]
        },
        { "service", "u1_member", [PersonFields.WorkMobilePhone, PersonFields.HireDate], [] },
        { "agent:hr:hr_admin", "u1_member", [PersonFields.DisplayName], [PersonFields.DisplayName] },
        { "u1_member", "u2_member", [], [] },
    };

    [Theory(DisplayName = "最終的な項目 = ポリシーで見える項目 ∩ クライアントの項目上限")]
    [MemberData(nameof(LimitCases))]
    public void VisibleFields_AreLimitedByClient(string viewer, string target, string[] limit, string[] expected)
    {
        var visible = Evaluator.VisibleFields(Viewer(viewer, FieldLimit.Of(limit)), F.Person(target));

        Assert.Equal(expected.Order(StringComparer.Ordinal), visible.Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "規則なしなら人事管理者だけの項目を除く全項目が見える")]
    public void NoRules_ExposesEverythingExceptHrOnly()
    {
        var evaluator = new PolicyEvaluator(PolicySet.Empty, Relations);

        var visible = evaluator.VisibleFields(F.User("b_member"), F.Person("u1_member"));

        Assert.Equal(PersonFields.PolicyControllable.Order(StringComparer.Ordinal), visible.Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "最も狭いものが勝つ: 規則を足しても見える範囲は広がらない（全組で検査）")]
    public void AddingRules_NeverWidens()
    {
        var baseline = new PolicyEvaluator(PolicySet.Empty, Relations);
        var viewers = Relations.Snapshot.People.Select(p => F.User(F.PersonKey(p.Id))).Append(ViewerContext.ForService("svc", FieldLimit.Unrestricted));

        foreach (var viewer in viewers)
        {
            foreach (var target in Relations.Snapshot.People.Select(p => p.Id))
            {
                var restricted = Evaluator.VisibleFields(viewer, target);
                Assert.Subset(baseline.VisibleFields(viewer, target).ToHashSet(), restricted.ToHashSet());
            }
        }
    }

    [Fact(DisplayName = "個人単位の例外が広い範囲を指定していても、積なので他の規則より広がらない")]
    public void WideningException_HasNoEffectAtEvaluation()
    {
        var policies = new PolicySet(
        [
            .. Policies.Rules,
            Rule(PolicyTargetKind.Person, "u1_member", [PersonFields.WorkMobilePhone], RelationScope.Group),
        ]);
        var evaluator = new PolicyEvaluator(policies, Relations);

        Assert.DoesNotContain(PersonFields.WorkMobilePhone, evaluator.VisibleFields(F.User("dept2_head"), F.Person("u1_member")));
    }

    /// <summary>例外の対象者、項目、公開範囲、許されるか。</summary>
    public static TheoryData<string, string[], string, bool> ExceptionCases => new()
    {
        { "u2_member", [PersonFields.ExtensionNumber], "none", true },
        { "u1_member", [PersonFields.WorkMobilePhone], "same_level(section)", true },
        { "u1_member", [PersonFields.WorkMobilePhone], "same_level(department)", true },
        { "u1_member", [PersonFields.WorkMobilePhone], "same_level(division)", false },
        { "u1_member", [PersonFields.WorkMobilePhone], "group", false },
        { "u1_member", [PersonFields.DisplayName], "group", true },
        { "u1_member", [PersonFields.DisplayName], "same_level(section)", true },
        // 組織単位の規則より広げられない（R1 の内線は課内）
        { "r1_member", [PersonFields.ExtensionNumber], "same_level(department)", false },
        { "r1_member", [PersonFields.ExtensionNumber], "self", true },
        { "u1_member", [PersonFields.ExtensionNumber], "same_level(department)", true },
        // 関係スコープ同士: 包含が言えないものは拒否
        { "u1_member", [PersonFields.PersonnelActions], "direct_reports", true },
        { "u1_member", [PersonFields.PersonnelActions], "same_level(section)", false },
        { "u1_member", [PersonFields.PersonnelActions], "group", false },
        // 全項目の例外
        { "u1_member", [PersonFields.Wildcard], "self", true },
        { "u1_member", [PersonFields.Wildcard], "none", true },
        { "u1_member", [PersonFields.Wildcard], "same_level(section)", false },
        // 配下を含まない組織単位の規則は、配下の人の例外を縛らない
        { "u1_member", [PersonFields.Site], "group", true },
        { "dept1_head", [PersonFields.Site], "same_level(section)", false },
    };

    [Theory(DisplayName = "個人単位の例外は狭める方向だけ")]
    [MemberData(nameof(ExceptionCases))]
    public void PersonException_MustNarrow(string person, string[] fields, string allow, bool allowed)
    {
        var candidate = Rule(PolicyTargetKind.Person, person, fields, RelationScopes.Parse(allow));

        var violations = PersonExceptionValidator.FindWidening(Policies, Relations, candidate);

        Assert.Equal(allowed, violations.Count == 0);
        if (!allowed)
        {
            Assert.Throws<PolicyValidationException>(() => PersonExceptionValidator.EnsureNarrowing(Policies, Relations, candidate));
        }
    }

    [Theory(DisplayName = "規則の形の検査")]
    [InlineData(PolicyTargetKind.All, true, false, new[] { PersonFields.DisplayName }, false)]
    [InlineData(PolicyTargetKind.OrgUnit, false, false, new[] { PersonFields.DisplayName }, false)]
    [InlineData(PolicyTargetKind.Person, false, true, new[] { PersonFields.NameHistory }, false)]
    [InlineData(PolicyTargetKind.All, false, false, new[] { "birth_date" }, false)]
    [InlineData(PolicyTargetKind.All, false, false, new string[0], false)]
    [InlineData(PolicyTargetKind.All, false, false, new[] { PersonFields.Wildcard }, true)]
    [InlineData(PolicyTargetKind.Person, false, true, new[] { PersonFields.WorkEmail }, true)]
    public void RuleShape_IsValidated(PolicyTargetKind kind, bool withOrg, bool withPerson, string[] fields, bool valid)
    {
        var rule = new PolicyRule
        {
            Id = Guid.NewGuid(),
            TargetKind = kind,
            OrgUnitId = withOrg ? F.Unit("S1") : null,
            PersonId = withPerson ? F.Person("u1_member") : null,
            Fields = fields.ToHashSet(StringComparer.Ordinal),
            Allow = RelationScope.SameLevel("section"),
        };

        if (valid)
        {
            rule.Validate();
        }
        else
        {
            Assert.Throws<PolicyValidationException>(rule.Validate);
        }
    }

    [Fact(DisplayName = "クライアントの項目上限に未知の項目名は使えない")]
    public void FieldLimit_RejectsUnknownField()
    {
        Assert.Throws<ArgumentException>(() => FieldLimit.Of(["display_name", "birth_date"]));
    }

    internal static PolicyRule Rule(PolicyTargetKind kind, string? target, string[] fields, RelationScope allow) => new()
    {
        Id = Guid.NewGuid(),
        TargetKind = kind,
        OrgUnitId = kind == PolicyTargetKind.OrgUnit ? F.Unit(target!) : null,
        PersonId = kind == PolicyTargetKind.Person ? F.Person(target!) : null,
        Fields = fields.ToHashSet(StringComparer.Ordinal),
        Allow = allow,
    };

    internal static ViewerContext Viewer(string spec, FieldLimit limit)
    {
        if (spec == "service")
        {
            return ViewerContext.ForService("attendance-system", limit);
        }

        var agent = spec.StartsWith("agent:", StringComparison.Ordinal);
        var rest = agent ? spec["agent:".Length..] : spec;
        var hr = rest.StartsWith("hr:", StringComparison.Ordinal);
        var key = hr ? rest["hr:".Length..] : rest;
        string[] roles = hr ? [Roles.HrAdmin] : [];

        return agent
            ? ViewerContext.ForAgent("assistant", F.Person(key), roles, Channel.Mcp, limit)
            : ViewerContext.ForUser(F.Person(key), roles, Channel.Ui, limit);
    }
}
