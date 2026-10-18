using JinJi.Domain.Organization;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests;

/// <summary>組織ツリーのスナップショット: 段の順位・基準組織・段の順位の逆転・上長系列の規則（DB を使わない）。</summary>
public sealed class OrgSnapshotTests
{
    private static readonly OrgFixture F = OrgFixture.Shared;
    private static readonly OrgSnapshot Snapshot = F.BuildSnapshot();

    [Theory(DisplayName = "段の順位: 法人の段の定義で決まり、段を持たない組織は最上位なら 0・それ以外は順位なし")]
    [InlineData("A-ROOT", 0)]
    [InlineData("D1", 1)]
    [InlineData("DP1", 2)]
    [InlineData("S1", 3)]
    [InlineData("U1", 4)]
    [InlineData("R1", null)]
    [InlineData("S3", 3)]
    [InlineData("U3", 4)]
    [InlineData("B-DP", 2)]
    [InlineData("B-G1", null)]
    [InlineData("C-ROOT", 1)]
    [InlineData("C-DP1", 3)]
    [InlineData("C-T1", 5)]
    [InlineData("C-P1", null)]
    [InlineData("D-DP1", 2)]
    [InlineData("D-T1", 3)]
    [InlineData("D-X", null)]
    public void RankOf(string unit, int? expected)
    {
        Assert.Equal(expected, Snapshot.RankOf(F.Unit(unit)));
    }

    [Theory(DisplayName = "基準組織: 既定の定義（本部・部・課・係）")]
    // 段が揃っている場合
    [InlineData("U1", "section", "S1")]
    [InlineData("U1", "department", "DP1")]
    [InlineData("U1", "division", "D1")]
    [InlineData("U1", "unit", "U1")]
    [InlineData("S1", "section", "S1")]
    [InlineData("S2", "department", "DP1")]
    // 上位の組織の直属: その組織自体（部の直属の人の「課内」は部の直属の人だけ）
    [InlineData("DP1", "section", "DP1")]
    [InlineData("D1", "section", "D1")]
    [InlineData("D1", "department", "D1")]
    [InlineData("A-ROOT", "division", "A-ROOT")]
    // 課を飛ばした部の直下の係: 課内は係自体（部全体に広げない）
    [InlineData("U3", "section", "U3")]
    [InlineData("U3", "department", "DP2")]
    [InlineData("DP2", "section", "DP2")]
    // 部を飛ばした本部直下の課: 部内は課自体（本部全体に広げない）
    [InlineData("S3", "department", "S3")]
    [InlineData("S3", "division", "D1")]
    // 段を持たない本部直下の室: 本部内は本部、部内・課内は室の中
    [InlineData("R1", "section", "R1")]
    [InlineData("R1", "department", "R1")]
    [InlineData("R1", "division", "D1")]
    // 本部の無い法人: 本部内は最上位の部（法人全体に広げない）
    [InlineData("B-DP", "division", "B-DP")]
    [InlineData("B-G1", "division", "B-DP")]
    [InlineData("B-G1", "section", "B-G1")]
    [InlineData("B-G1", "department", "B-DP")]
    public void BaseUnitOf_DefaultScheme(string unit, string level, string expected)
    {
        Assert.Equal(F.Unit(expected), Snapshot.BaseUnitOf(F.Unit(unit), level));
    }

    [Theory(DisplayName = "基準組織: 5 段（会社・事業部・部・課・チーム）と 3 段（会社・部・チーム）の定義")]
    [InlineData("C-T1", "team", "C-T1")]
    [InlineData("C-T1", "section", "C-S1")]
    [InlineData("C-T1", "department", "C-DP1")]
    [InlineData("C-T1", "business_unit", "C-BU1")]
    [InlineData("C-T1", "company", "C-ROOT")]
    [InlineData("C-S1", "team", "C-S1")]
    [InlineData("C-ROOT", "business_unit", "C-ROOT")]
    // 段の飛ばし: 事業部直下の課
    [InlineData("C-S2", "department", "C-S2")]
    [InlineData("C-S2", "business_unit", "C-BU1")]
    // 段を持たない組織と、その下のチーム
    [InlineData("C-P1", "team", "C-P1")]
    [InlineData("C-P1", "section", "C-P1")]
    [InlineData("C-P1", "department", "C-P1")]
    [InlineData("C-P1", "business_unit", "C-BU1")]
    [InlineData("C-T3", "team", "C-T3")]
    [InlineData("C-T3", "section", "C-P1")]
    [InlineData("C-T3", "department", "C-P1")]
    [InlineData("D-T1", "team", "D-T1")]
    [InlineData("D-T1", "department", "D-DP1")]
    [InlineData("D-DP1", "team", "D-DP1")]
    [InlineData("D-X", "department", "D-X")]
    [InlineData("D-X", "company", "D-ROOT")]
    public void BaseUnitOf_EntitySchemes(string unit, string level, string expected)
    {
        Assert.Equal(F.Unit(expected), Snapshot.BaseUnitOf(F.Unit(unit), level));
    }

    [Theory(DisplayName = "基準組織: 法人に効く定義に無い段は基準組織なし")]
    [InlineData("C-T1", "unit")]
    [InlineData("C-T1", "division")]
    [InlineData("D-T1", "section")]
    [InlineData("U1", "team")]
    [InlineData("B-G1", "company")]
    public void BaseUnitOf_UndefinedLevel_IsNull(string unit, string level)
    {
        Assert.Null(Snapshot.BaseUnitOf(F.Unit(unit), level));
    }

    [Fact(DisplayName = "基準組織は上位の段ほど広い（下位の段で一致すれば上位の段でも一致する。定義ごとに全組織の組で検査）")]
    public void BaseUnits_AreMonotonic()
    {
        foreach (var scheme in Snapshot.LevelSchemes)
        {
            var units = Snapshot.Units.Where(u => Snapshot.LevelSchemeOf(u.LegalEntityId) == scheme).Select(u => u.Id).ToList();
            Assert.NotEmpty(units);
            foreach (var a in units)
            {
                foreach (var b in units)
                {
                    foreach (var lower in scheme.Levels)
                    {
                        if (Snapshot.BaseUnitOf(a, lower.Key) != Snapshot.BaseUnitOf(b, lower.Key))
                        {
                            continue;
                        }

                        foreach (var upper in scheme.Levels.Where(l => l.Rank < lower.Rank))
                        {
                            Assert.Equal(Snapshot.BaseUnitOf(a, upper.Key), Snapshot.BaseUnitOf(b, upper.Key));
                        }
                    }
                }

                // 基準組織は所属組織自身かその祖先
                foreach (var level in scheme.Levels)
                {
                    Assert.Contains(Snapshot.BaseUnitOf(a, level.Key)!.Value, Snapshot.AncestorsOf(a));
                }
            }
        }
    }

    [Fact(DisplayName = "段の順位の逆転: フィクスチャには無く、順位を入れ替えた定義では検出される")]
    public void LevelOrderViolations_AreDetected()
    {
        Assert.Empty(Snapshot.LevelOrderViolations());

        // C の課とチームの順位を入れ替える: チーム（C-T1・C-T2）が課（C-S1）より上になる
        var swapped = new OrgLevelScheme(F.EntityC, [new("company", "会社", 1), new("business_unit", "事業部", 2), new("department", "部", 3), new("team", "チーム", 4), new("section", "課", 5)]);
        var broken = Snapshot.WithLevelSchemes([OrgFixture.DefaultLevelScheme, swapped, F.LevelSchemes.Single(s => s.LegalEntityId == F.EntityD)]);

        var violations = broken.LevelOrderViolations().Select(v => (F.UnitKey(v.Unit), F.UnitKey(v.Ancestor))).Order().ToList();
        Assert.Equal([("C-T1", "C-S1"), ("C-T2", "C-S1")], violations);

        // 段を持たない組織は素通りして、段を持つ最も近い祖先と比べる（C-T3 の上の C-P1 を飛ばして C-BU1）
        var flat = Snapshot.WithLevelSchemes([OrgFixture.DefaultLevelScheme, new OrgLevelScheme(F.EntityC, [new("team", "チーム", 1), new("company", "会社", 2)])]);
        Assert.Contains(flat.LevelOrderViolations(), v => v == (F.Unit("C-T1"), F.Unit("C-ROOT")));
    }

    [Theory(DisplayName = "直属の上長: 本人以外の組織長を上にたどる（兼務・出向を含む和集合）")]
    [InlineData("u1_member", new[] { "unit1_head" })]
    [InlineData("unit1_head", new[] { "sec1_head" })]
    [InlineData("u2_member", new[] { "sec1_head" })]
    [InlineData("sec1_head", new[] { "dept1_head" })]
    [InlineData("concurrent", new[] { "sec2_head", "unit1_head" })]
    [InlineData("u3_member", new[] { "dept2_head" })]
    [InlineData("s3_member", new[] { "div1_head" })]
    [InlineData("r1_member", new[] { "r1_head" })]
    [InlineData("r1_head", new[] { "div1_head" })]
    [InlineData("seconded", new[] { "b_head" })]
    [InlineData("b_member", new[] { "b_head" })]
    [InlineData("president", new string[0])]
    [InlineData("b_head", new string[0])]
    [InlineData("retired", new string[0])]
    public void DirectManagersOf(string person, string[] expected)
    {
        var actual = Snapshot.DirectManagersOf(F.Person(person)).Select(F.PersonKey).Order(StringComparer.Ordinal);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual);
    }

    [Fact(DisplayName = "上長系列は最上位までたどり本人を含まない")]
    public void ManagersChain_ReachesTop()
    {
        var chain = Snapshot.ManagersChainOf(F.Person("u1_member")).Select(F.PersonKey).Order(StringComparer.Ordinal);
        Assert.Equal(new[] { "dept1_head", "div1_head", "president", "sec1_head", "unit1_head" }, chain);
    }

    [Fact(DisplayName = "法人: 雇用元と所属組織の法人の和集合（出向先を含む）")]
    public void LegalEntities_IncludeSecondmentDestination()
    {
        Assert.Equal(new HashSet<Guid> { F.EntityA, F.EntityB }, Snapshot.LegalEntitiesOf(F.Person("seconded")).ToHashSet());
        Assert.Equal(new HashSet<Guid> { F.EntityA }, Snapshot.LegalEntitiesOf(F.Person("retired")).ToHashSet());
    }

    [Fact(DisplayName = "時点指定: 将来の所属・入社予定者は今日のスナップショットに入らず、その日のスナップショットに入る")]
    public void FutureRows_AppearOnlyInFutureSnapshot()
    {
        Assert.Null(Snapshot.FindPerson(F.Person("future_hire")));
        Assert.Equal([F.Unit("S1")], Snapshot.AssignmentsOf(F.Person("s1_member")).Select(a => a.OrgUnitId));

        var future = F.BuildSnapshot(F.Today.AddDays(20));
        Assert.NotNull(future.FindPerson(F.Person("future_hire")));
        Assert.Equal([F.Unit("S2")], future.AssignmentsOf(F.Person("s1_member")).Select(a => a.OrgUnitId));
    }

    [Fact(DisplayName = "上位組織の循環やスナップショットに無い上位組織でも破綻しない")]
    public void BrokenHierarchy_DoesNotThrow()
    {
        var entity = Guid.NewGuid();
        var (a, b, orphan, missing) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var snapshot = OrgSnapshot.Create(
            F.Today,
            [new LegalEntity(entity, "X", "X")],
            [
                new OrgUnit(a, entity, "A", "A", null, "section", b, null),
                new OrgUnit(b, entity, "B", "B", null, "section", a, null),
                new OrgUnit(orphan, entity, "O", "O", null, "unit", missing, null),
            ],
            [],
            [],
            [OrgFixture.DefaultLevelScheme]);

        Assert.Equal([a, b], snapshot.AncestorsOf(a));
        Assert.Equal(orphan, snapshot.BaseUnitOf(orphan, "section"));
        Assert.Equal(orphan, snapshot.BaseUnitOf(orphan, "division"));
    }

    [Fact(DisplayName = "段の定義が無い法人・定義に無いキーの組織は、段を持たない組織として扱う")]
    public void MissingSchemeOrKey_IsLevelless()
    {
        var entity = Guid.NewGuid();
        var (root, child) = (Guid.NewGuid(), Guid.NewGuid());
        var snapshot = OrgSnapshot.Create(
            F.Today,
            [new LegalEntity(entity, "X", "X")],
            [new OrgUnit(root, entity, "R", "R", null, "division", null, null), new OrgUnit(child, entity, "C", "C", null, "no_such_level", root, null)],
            [],
            [],
            []);

        Assert.Equal(0, snapshot.RankOf(root));
        Assert.Null(snapshot.RankOf(child));
        Assert.Null(snapshot.BaseUnitOf(child, "division"));
        Assert.Empty(snapshot.LevelSchemeOf(entity).Levels);
    }
}
