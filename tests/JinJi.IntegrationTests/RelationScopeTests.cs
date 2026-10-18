using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests;

/// <summary>関係スコープの評価（§5.1）。閲覧者 × 対象者 × スコープのテーブル駆動（DB を使わない）。</summary>
public sealed class RelationScopeTests
{
    private static readonly OrgFixture F = OrgFixture.Shared;
    private static readonly RelationEvaluator Relations = new(F.BuildSnapshot());

    /// <summary>閲覧者（null は人に紐付かない社内システム）、対象者、スコープ、期待値。</summary>
    public static TheoryData<string?, string, string, bool> Cases => new()
    {
        // self
        { "u1_member", "u1_member", "self", true },
        { "u1_member", "u2_member", "self", false },
        { "retired", "retired", "same_level(section)", true },
        // 課内（既定の定義の section）: 同じ課の係同士・課の直属と係
        { "u1_member", "u2_member", "same_level(section)", true },
        { "u1_member", "sec1_head", "same_level(section)", true },
        { "u1_member", "s2_member", "same_level(section)", false },
        { "u1_member", "dept1_head", "same_level(section)", false },
        // 課の無い部: 部の直属同士は同じ課内、部の直下の係は別
        { "dept2_head", "dp2_member", "same_level(section)", true },
        { "dp2_member", "u3_member", "same_level(section)", false },
        { "dp2_member", "u3_member", "same_level(department)", true },
        // 部の無い本部直下の課: 部内は課の中だけ
        { "s3_member", "div1_head", "same_level(department)", false },
        { "s3_member", "u1_member", "same_level(department)", false },
        { "s3_member", "u1_member", "same_level(division)", true },
        // 本部直下の室（other）は部相当
        { "r1_member", "r1_head", "same_level(section)", true },
        { "r1_member", "u1_member", "same_level(department)", false },
        { "r1_member", "u1_member", "same_level(division)", true },
        // 部内・本部内
        { "u1_member", "s2_member", "same_level(department)", true },
        { "u1_member", "dp2_member", "same_level(department)", false },
        { "u1_member", "dp2_member", "same_level(division)", true },
        { "u1_member", "hr_admin", "same_level(division)", false },
        { "president", "u1_member", "same_level(division)", false },
        // 兼務は和集合: 主務 S2・兼務 U1
        { "concurrent", "u2_member", "same_level(section)", true },
        { "concurrent", "s2_member", "same_level(section)", true },
        { "u2_member", "concurrent", "same_level(section)", true },
        { "concurrent", "dp2_member", "same_level(section)", false },
        // 出向: 出向先の組織で判定し、法人は雇用元と出向先の両方
        { "seconded", "b_member", "same_level(department)", true },
        { "seconded", "b_head", "same_level(section)", true },
        { "seconded", "u1_member", "same_level(division)", false },
        { "seconded", "u1_member", "legal_entity", true },
        { "b_member", "seconded", "legal_entity", true },
        { "b_member", "u1_member", "legal_entity", false },
        { "b_member", "b_head", "same_level(division)", true },
        // 一時停止中でも所属は維持される
        { "u1_member", "suspended", "same_level(section)", true },
        { "suspended", "sec1_head", "direct_reports", false },
        { "sec1_head", "suspended", "direct_reports", true },
        // 退職者: 所属が無いので組織の関係は成り立たず、法人とグループだけ
        { "u1_member", "retired", "same_level(section)", false },
        { "u1_member", "retired", "same_level(department)", false },
        { "u1_member", "retired", "legal_entity", true },
        { "u1_member", "retired", "group", true },
        { "sec2_head", "retired", "direct_reports", false },
        // group: 社内システムを含む全員
        { null, "u1_member", "group", true },
        { "b_member", "u1_member", "group", true },
        // 人に紐付かない社内システムは group 以外成り立たない
        { null, "u1_member", "legal_entity", false },
        { null, "u1_member", "same_level(section)", false },
        { null, "u1_member", "self", false },
        { null, "u1_member", "managers_chain", false },
        // none: 誰も満たさない（本人・人事管理者の扱いは PolicyEvaluator 側）
        { "u1_member", "u1_member", "none", false },
        { null, "u1_member", "none", false },
        // direct_reports: 対象者が閲覧者の直属の部下
        { "unit1_head", "u1_member", "direct_reports", true },
        { "sec1_head", "u1_member", "direct_reports", false },
        { "sec1_head", "u2_member", "direct_reports", true },
        { "sec1_head", "unit1_head", "direct_reports", true },
        { "unit1_head", "concurrent", "direct_reports", true },
        { "sec2_head", "concurrent", "direct_reports", true },
        { "dept2_head", "u3_member", "direct_reports", true },
        { "div1_head", "s3_member", "direct_reports", true },
        { "b_head", "seconded", "direct_reports", true },
        { "u1_member", "unit1_head", "direct_reports", false },
        { "u1_member", "u1_member", "direct_reports", false },
        // subordinates: 対象者が閲覧者の配下
        { "sec1_head", "u1_member", "subordinates", true },
        { "president", "u1_member", "subordinates", true },
        { "president", "hr_admin", "subordinates", true },
        { "dept2_head", "u1_member", "subordinates", false },
        { "president", "seconded", "subordinates", false },
        { "u1_member", "sec1_head", "subordinates", false },
        // managers_chain: 対象者が閲覧者の上長系列
        { "u1_member", "unit1_head", "managers_chain", true },
        { "u1_member", "president", "managers_chain", true },
        { "u1_member", "sec2_head", "managers_chain", false },
        { "concurrent", "sec2_head", "managers_chain", true },
        { "concurrent", "unit1_head", "managers_chain", true },
        { "sec1_head", "u1_member", "managers_chain", false },
        { "seconded", "b_head", "managers_chain", true },
    };

    [Theory(DisplayName = "関係スコープ")]
    [MemberData(nameof(Cases))]
    public void Satisfies(string? viewer, string target, string scope, bool expected)
    {
        var viewerId = viewer is null ? (Guid?)null : F.Person(viewer);

        Assert.Equal(expected, Relations.Satisfies(viewerId, F.Person(target), RelationScopes.Parse(scope)));
    }

    /// <summary>段の定義が法人ごとに違うグループ（法人 C = 5 段、法人 D = 3 段、A・B = 既定）での same_level。</summary>
    public static TheoryData<string, string, string, bool> LevelCases => new()
    {
        // 5 段（会社 > 事業部 > 部 > 課 > チーム）
        { "c_t1_member", "c_t2_member", "same_level(team)", false },
        { "c_t1_member", "c_t2_member", "same_level(section)", true },
        { "c_t1_member", "c_s1_head", "same_level(team)", false },
        { "c_t1_member", "c_s1_head", "same_level(section)", true },
        { "c_t1_member", "c_dp1_head", "same_level(department)", true },
        { "c_t1_member", "c_bu1_head", "same_level(department)", false },
        { "c_t1_member", "c_president", "same_level(company)", true },
        // 段の飛ばし: 事業部直下の課の「部内」は、その課（事業部全体に広げない）
        { "c_s2_member", "c_t1_member", "same_level(department)", false },
        { "c_s2_member", "c_t1_member", "same_level(business_unit)", true },
        { "c_s2_member", "c_bu1_head", "same_level(department)", false },
        // 段を持たない組織（室）: 祖先の段で判定し、見つからなければ室の中に狭める。室の下のチームは室と同じ課・部に入る
        { "c_p1_member", "c_t3_member", "same_level(team)", false },
        { "c_p1_member", "c_t3_member", "same_level(section)", true },
        { "c_p1_member", "c_t3_member", "same_level(department)", true },
        { "c_p1_member", "c_s2_member", "same_level(department)", false },
        { "c_p1_member", "c_bu1_head", "same_level(business_unit)", true },
        { "c_p1_member", "c_bu1_head", "same_level(section)", false },
        // 3 段（会社 > 部 > チーム）: section は定義に無いので誰とも一致しない
        { "d_t1_member", "d_dp1_head", "same_level(department)", true },
        { "d_t1_member", "d_dp1_head", "same_level(team)", false },
        { "d_t1_member", "d_dp1_head", "same_level(section)", false },
        { "d_x_member", "d_t1_member", "same_level(department)", false },
        { "d_x_member", "d_president", "same_level(company)", true },
        { "d_x_member", "d_president", "same_level(department)", false },
        // 既定の定義の段は C・D の定義に無い
        { "c_t1_member", "c_t2_member", "same_level(unit)", false },
        { "c_t1_member", "c_t2_member", "same_level(division)", false },
        // 法人をまたぐと、同じキーの段でも一致しない（組織ツリーは法人ごと）
        { "c_dp1_head", "d_dp1_head", "same_level(department)", false },
        { "c_president", "d_president", "same_level(company)", false },
        { "u1_member", "c_t1_member", "same_level(department)", false },
        // 定義に無い段でも本人は本人
        { "d_t1_member", "d_t1_member", "same_level(section)", true },
    };

    [Theory(DisplayName = "same_level: 法人ごとに段の定義が違うグループ・段の飛ばし・段を持たない組織")]
    [MemberData(nameof(LevelCases))]
    public void SatisfiesLevel(string viewer, string target, string scope, bool expected)
    {
        Assert.Equal(expected, Relations.Satisfies(F.Person(viewer), F.Person(target), RelationScopes.Parse(scope)));
    }

    [Fact(DisplayName = "包含が言える組はすべての閲覧者・対象者で成り立つ（same_level ⊂ legal_entity ⊂ group、段同士、direct_reports ⊂ subordinates）")]
    public void ProvableInclusions_HoldForAllPairs()
    {
        var keys = F.LevelSchemes.SelectMany(s => s.Levels.Select(l => l.Key)).Distinct().Append("no_such_level").ToList();
        RelationScope[] fixedScopes =
        [
            RelationScope.None, RelationScope.Self, RelationScope.LegalEntity, RelationScope.Group,
            RelationScope.DirectReports, RelationScope.Subordinates, RelationScope.ManagersChain,
        ];
        var scopes = fixedScopes.Concat(keys.Select(RelationScope.SameLevel)).ToList();
        // self ⊂ 他のスコープは「本人は自分の項目を見られる」（PolicyEvaluator）で成り立たせているので、ここでは対象にしない
        var provable = scopes
            .Where(a => a != RelationScope.Self)
            .SelectMany(a => scopes.Where(b => a != b && RelationScopes.IsNarrowerOrEqual(a, b, F.LevelSchemes)).Select(b => (a, b)))
            .ToList();
        var people = Relations.Snapshot.People.Select(p => (Guid?)p.Id).Append(null).ToList();

        // 前提の確認: 段同士の包含が言える組と言えない組の両方がある
        Assert.Contains((RelationScope.SameLevel("section"), RelationScope.SameLevel("department")), provable);
        Assert.DoesNotContain((RelationScope.SameLevel("team"), RelationScope.SameLevel("section")), provable);

        foreach (var viewer in people)
        {
            foreach (var target in Relations.Snapshot.People.Select(p => p.Id))
            {
                foreach (var (a, b) in provable)
                {
                    if (Relations.Satisfies(viewer, target, a))
                    {
                        Assert.True(
                            Relations.Satisfies(viewer, target, b),
                            $"{(viewer is { } v ? F.PersonKey(v) : "service")} → {F.PersonKey(target)}: {a} が成り立つのに {b} が成り立たない");
                    }
                }
            }
        }
    }

    [Theory(DisplayName = "スコープの包含関係（個人単位の例外の検査に使う。法人ごとの定義のすべてで言えるときだけ）")]
    [InlineData("none", "self", true)]
    [InlineData("self", "none", false)]
    [InlineData("self", "direct_reports", true)]
    [InlineData("self", "same_level(team)", true)]
    [InlineData("same_level(section)", "same_level(department)", true)]
    [InlineData("same_level(department)", "same_level(section)", false)]
    [InlineData("same_level(section)", "same_level(section)", true)]
    // 既定には team が無く、C（5 ≥ 3）・D（3 ≥ 2）では部より下
    [InlineData("same_level(team)", "same_level(department)", true)]
    // D に section が無い（D のチーム内で見える人が、D では誰も満たさない section で見えるとは言えない）
    [InlineData("same_level(team)", "same_level(section)", false)]
    // 既定に business_unit が無い
    [InlineData("same_level(department)", "same_level(business_unit)", false)]
    // C・D に division が無い
    [InlineData("same_level(department)", "same_level(division)", false)]
    // C・D に unit が無いので unit はそこでは誰も満たさない
    [InlineData("same_level(unit)", "same_level(section)", true)]
    // どの定義にも無い段は誰も満たさない
    [InlineData("same_level(no_such_level)", "same_level(team)", true)]
    [InlineData("same_level(company)", "legal_entity", true)]
    [InlineData("same_level(section)", "group", true)]
    [InlineData("legal_entity", "same_level(company)", false)]
    [InlineData("legal_entity", "group", true)]
    [InlineData("group", "legal_entity", false)]
    [InlineData("direct_reports", "subordinates", true)]
    [InlineData("subordinates", "direct_reports", false)]
    [InlineData("direct_reports", "same_level(section)", false)]
    [InlineData("same_level(section)", "direct_reports", false)]
    [InlineData("managers_chain", "group", true)]
    [InlineData("managers_chain", "legal_entity", false)]
    [InlineData("subordinates", "subordinates", true)]
    public void IsNarrowerOrEqual(string a, string b, bool expected)
    {
        Assert.Equal(expected, RelationScopes.IsNarrowerOrEqual(RelationScopes.Parse(a), RelationScopes.Parse(b), F.LevelSchemes));
    }

    [Fact(DisplayName = "段同士の包含は定義の組み合わせで変わる（既定だけなら部 ⊂ 本部、C があれば言えない）")]
    public void LevelInclusion_DependsOnSchemes()
    {
        var department = RelationScope.SameLevel("department");
        var division = RelationScope.SameLevel("division");

        Assert.True(RelationScopes.IsNarrowerOrEqual(department, division, [OrgFixture.DefaultLevelScheme]));
        Assert.False(RelationScopes.IsNarrowerOrEqual(department, division, F.LevelSchemes));
        Assert.False(RelationScopes.IsNarrowerOrEqual(division, department, [OrgFixture.DefaultLevelScheme]));
    }

    [Theory(DisplayName = "スコープの表記は往復でき、不正な表記は拒否する")]
    [InlineData("none")]
    [InlineData("self")]
    [InlineData("same_level(section)")]
    [InlineData("same_level(business_unit)")]
    [InlineData("legal_entity")]
    [InlineData("group")]
    [InlineData("direct_reports")]
    [InlineData("subordinates")]
    [InlineData("managers_chain")]
    public void ScopeCodes_RoundTrip(string code)
    {
        var scope = RelationScopes.Parse(code);

        Assert.Equal(code, scope.ToCode());
        Assert.Equal(scope, RelationScope.Of(scope.Kind, scope.LevelKey));
    }

    [Theory(DisplayName = "不正なスコープの表記は拒否する")]
    [InlineData("same_team")]
    [InlineData("same_level()")]
    [InlineData("same_level(Section)")]
    [InlineData("same_level(課)")]
    [InlineData("same_level")]
    public void InvalidScopeCodes_AreRejected(string code)
    {
        Assert.Throws<ArgumentException>(() => RelationScopes.Parse(code));
    }
}
