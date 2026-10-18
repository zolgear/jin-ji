using System.Text.Json;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests;

/// <summary>
/// アプリ側の判定（PersonProjector）と RLS が、同じフィクスチャで矛盾しないこと（内示・氏名の履歴）。
/// </summary>
/// <remarks>
/// アプリ側は「全データ（hr_admin で読んだスナップショット）」に閲覧者の規則を当てた結果、RLS 側は「閲覧者で直接 SQL を投げた結果」。
/// 両者が一致すれば、どちらか一方にバグがあっても、もう一方が同じ線を引いている。
/// </remarks>
public sealed class AuthorizationConsistencyTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public static TheoryData<string> Viewers => ["u1_member", "sec1_head", "s1_member", "president", "service", "agent:sec1_head", "hr:hr_admin"];

    [Theory(DisplayName = "内示: アプリが見せる発令と RLS が見せる発令が一致する")]
    [MemberData(nameof(Viewers))]
    public async Task PersonnelActions_AppMatchesRls(string viewerSpec)
    {
        var viewer = PolicyEvaluationTests.Viewer(viewerSpec, FieldLimit.Unrestricted);
        var full = await LoadAsync(F.HrAdmin());
        var projector = new PersonProjector(new PolicyEvaluator(PolicySet.Empty, new RelationEvaluator(full)), full, F.Today);

        foreach (var person in F.People)
        {
            var rls = await db.QueryAsAppAsync<Guid>(viewer, "SELECT id FROM personnel_action WHERE person_id = $1 ORDER BY id", Ct, person.Id);
            var view = projector.Project(viewer, person.Id);
            if (view is null)
            {
                // スナップショットに居ない人（入社予定者）は、hr_admin 以外には発令も見えない
                // 退職後の公開期間を過ぎた人はアプリだけが隠す（RLS では線を引かない。§5.6）
                if (PersonKeyOf(person.Id) == "long_retired")
                {
                    Assert.False(viewer.IsHrAdmin);
                    continue;
                }

                Assert.Equal("future_hire", PersonKeyOf(person.Id));
                if (!viewer.IsHrAdmin)
                {
                    Assert.Empty(rls);
                }

                continue;
            }

            Assert.Equal(rls, view.PersonnelActions!.Select(a => a.Id).Order());
        }
    }

    [Theory(DisplayName = "氏名の履歴: アプリが使う版と RLS が見せる版が一致する")]
    [MemberData(nameof(Viewers))]
    public async Task NameVersions_AppMatchesRls(string viewerSpec)
    {
        var viewer = PolicyEvaluationTests.Viewer(viewerSpec, FieldLimit.Unrestricted);
        var full = await LoadAsync(F.HrAdmin());
        var projector = new PersonProjector(new PolicyEvaluator(PolicySet.Empty, new RelationEvaluator(full)), full, F.Today);

        foreach (var person in full.People)
        {
            var rls = await db.QueryAsAppAsync<string>(
                viewer,
                "SELECT family_name FROM person_version WHERE person_id = $1 ORDER BY lower(valid_period)",
                Ct,
                person.Id);
            var view = projector.Project(viewer, person.Id);
            if (view is null)
            {
                Assert.Equal("long_retired", PersonKeyOf(person.Id));
                Assert.False(viewer.IsHrAdmin);
                continue;
            }

            if (viewer.IsHrAdmin)
            {
                Assert.Equal(rls, view.NameHistory!.Select(h => h.FamilyName));
            }
            else
            {
                Assert.Null(view.NameHistory);
                Assert.Equal([view.FamilyName!], rls);
            }
        }
    }

    [Theory(DisplayName = "RLS で読んだスナップショットでも、全データのスナップショットでも、閲覧者への投影は同じ")]
    [MemberData(nameof(Viewers))]
    public async Task Projection_IsIndependentOfRlsFiltering(string viewerSpec)
    {
        var viewer = PolicyEvaluationTests.Viewer(viewerSpec, FieldLimit.Unrestricted);
        var full = await LoadAsync(F.HrAdmin());
        var filtered = await LoadAsync(viewer);
        var policies = await new NpgsqlPolicyRuleStore(db.AppDataSource, TimeProvider.System).LoadActiveAsync(viewer, Ct);

        var fromFull = new PersonProjector(new PolicyEvaluator(policies, new RelationEvaluator(full)), full, F.Today);
        var fromFiltered = new PersonProjector(new PolicyEvaluator(policies, new RelationEvaluator(filtered)), filtered, F.Today);

        Assert.Equal(full.People.Count, filtered.People.Count);
        foreach (var person in full.People)
        {
            Assert.Equal(Json(fromFull.Project(viewer, person.Id)), Json(fromFiltered.Project(viewer, person.Id)));
        }
    }

    [Fact(DisplayName = "DB から読んだスナップショットはメモリ上のフィクスチャと同じ組織・所属・上長を持つ")]
    public async Task DatabaseSnapshot_MatchesInMemoryFixture()
    {
        var fromDb = await LoadAsync(F.HrAdmin());
        var inMemory = F.BuildSnapshot();

        Assert.Equal(inMemory.People.Select(p => p.Id).Order(), fromDb.People.Select(p => p.Id).Order());
        Assert.Equal(inMemory.Units.Select(u => u.Id).Order(), fromDb.Units.Select(u => u.Id).Order());
        foreach (var person in inMemory.People)
        {
            Assert.Equal(inMemory.AssignmentsOf(person.Id), fromDb.AssignmentsOf(person.Id));
            Assert.Equal(inMemory.ManagersChainOf(person.Id).Order(), fromDb.ManagersChainOf(person.Id).Order());
            Assert.Equal(inMemory.FindPerson(person.Id)!.Suspension, fromDb.FindPerson(person.Id)!.Suspension);
            Assert.Equal(
                inMemory.FindPerson(person.Id)!.Actions.Select(a => a.Id).Order(),
                fromDb.FindPerson(person.Id)!.Actions.Select(a => a.Id).Order());
        }

    }

    [Theory(DisplayName = "段の定義と基準組織: DB（RLS を通して読んだもの）とメモリ上のフィクスチャで一致する（既定・5 段・3 段・段なし・段の飛ばし）")]
    [MemberData(nameof(Viewers))]
    public async Task LevelSchemesAndBaseUnits_MatchInMemoryFixture(string viewerSpec)
    {
        var fromDb = await LoadAsync(PolicyEvaluationTests.Viewer(viewerSpec, FieldLimit.Unrestricted));
        var inMemory = F.BuildSnapshot();

        Assert.Equal(Describe(inMemory.LevelSchemes), Describe(fromDb.LevelSchemes));
        var keys = inMemory.LevelSchemes.SelectMany(s => s.Levels.Select(l => l.Key)).Distinct().ToList();
        foreach (var unit in inMemory.Units)
        {
            Assert.Equal(inMemory.RankOf(unit.Id), fromDb.RankOf(unit.Id));
            foreach (var key in keys)
            {
                Assert.Equal(inMemory.BaseUnitOf(unit.Id, key), fromDb.BaseUnitOf(unit.Id, key));
            }
        }
    }

    private static List<string> Describe(IEnumerable<OrgLevelScheme> schemes) =>
    [
        .. schemes
            .Select(s => $"{s.LegalEntityId}: {string.Join(" > ", s.Levels.Select(l => $"{l.Key}/{l.Name}/{l.Rank}"))}")
            .Order(StringComparer.Ordinal),
    ];

    private string PersonKeyOf(Guid id) => F.PersonKey(id);

    private Task<OrgSnapshot> LoadAsync(ViewerContext viewer) =>
        new NpgsqlOrgSnapshotProvider(db.AppDataSource).LoadAsync(viewer, F.Today, F.Today, Ct);

    private static string Json(PersonView? view) => JsonSerializer.Serialize(view);
}
