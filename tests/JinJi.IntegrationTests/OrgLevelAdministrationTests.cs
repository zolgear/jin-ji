using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Application.Organization;
using JinJi.Application.Policies;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests;

/// <summary>段の定義の起案と承認（OrgLevelAdministrationService）。DB の代わりにメモリ上の実装を使う。</summary>
public sealed class OrgLevelAdministrationTests
{
    private static readonly OrgFixture F = OrgFixture.Shared;

    private readonly InMemoryOrgLevelStore _store = new();
    private readonly InMemoryPolicyRuleStore _policies = new(PolicyEvaluationTests.Policies.Rules);
    private readonly RecordingAuditSink _audit = new();
    private readonly FixedTimeProvider _time = new(F.Today);
    private IOrgSnapshotProvider _snapshots = new FixtureSnapshotProvider(F);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ViewerContext HrHead => ViewerContext.ForUser(F.Person("hr_head"), [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);

    private OrgLevelAdministrationService Administration => new(_store, _snapshots, _policies, new AuditWriter(_audit, _time), _time);

    /// <summary>D の 3 段に課を足した 4 段（会社 > 部 > 課 > チーム）。D の組織の親子の順位は崩れない。</summary>
    private static OrgLevelScheme FourLevelD => new(F.EntityD, [new("company", "会社", 1), new("department", "部", 2), new("section", "課", 3), new("team", "チーム", 4)]);

    [Fact(DisplayName = "起案 → 別人の承認で有効になり、起案者は承認できない")]
    public async Task ProposeAndApprove()
    {
        var id = EntityId.New();

        await Administration.ProposeAsync(F.HrAdmin(), id, FourLevelD, Ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ApproveAsync(F.HrAdmin(), id, Ct));
        Assert.Equal(OrgLevelSchemeStatus.Proposed, _store.Schemes[id].Status);

        await Administration.ApproveAsync(HrHead, id, Ct);
        Assert.Equal(OrgLevelSchemeStatus.Approved, _store.Schemes[id].Status);
        Assert.Equal(
            [AuditOutcome.Success, AuditOutcome.Denied, AuditOutcome.Success],
            _audit.Records.Select(r => r.Outcome));
        Assert.All(_audit.Records, r => Assert.Equal("org_level_scheme", r.TargetType));
    }

    [Fact(DisplayName = "人事管理者以外・人に紐付かない操作者は起案できない")]
    public async Task NonAdmin_CannotPropose()
    {
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ProposeAsync(F.User("u1_member"), EntityId.New(), FourLevelD, Ct));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Administration.ProposeAsync(ViewerContext.ForSystem(Channel.Worker), EntityId.New(), FourLevelD, Ct));

        Assert.Empty(_store.Schemes);
    }

    public static TheoryData<string, string[]> InvalidShapes => new()
    {
        { "段なし", [] },
        { "順位が 1 から始まらない", ["company:2", "team:3"] },
        { "順位の飛び", ["company:1", "team:3"] },
        { "順位の重複", ["company:1", "team:1"] },
        { "キーの重複", ["company:1", "company:2"] },
        { "キーの形式", ["Company:1"] },
    };

    [Theory(DisplayName = "定義の形が不正なら拒否する（denied を残す）")]
    [MemberData(nameof(InvalidShapes))]
    public async Task InvalidShape_IsRejected(string label, string[] levels)
    {
        var scheme = new OrgLevelScheme(F.EntityD, levels.Select(l => l.Split(':')).Select(p => new OrgLevel(p[0], p[0], int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture))));

        await Assert.ThrowsAsync<OrgLevelValidationException>(() => Administration.ProposeAsync(F.HrAdmin(), EntityId.New(), scheme, Ct));

        Assert.Empty(_store.Schemes);
        Assert.Equal(AuditOutcome.Denied, Assert.Single(_audit.Records).Outcome);
        Assert.NotEmpty(label);
    }

    [Fact(DisplayName = "ポリシーから参照されている段は削除できない（既定から section を消す。R1 の規則が same_level(section)）")]
    public async Task ReferencedLevel_CannotBeRemoved()
    {
        var withoutSection = new OrgLevelScheme(null, [new("division", "本部", 1), new("department", "部", 2), new("unit", "係", 3)]);

        var ex = await Assert.ThrowsAsync<OrgLevelValidationException>(() => Administration.ProposeAsync(F.HrAdmin(), EntityId.New(), withoutSection, Ct));

        Assert.Contains("section", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "起案中のポリシーから参照されている段も削除できない")]
    public async Task LevelReferencedByProposedRule_CannotBeRemoved()
    {
        var rule = PolicyEvaluationTests.Rule(PolicyTargetKind.All, null, [PersonFields.JobType], RelationScope.SameLevel("company"));
        await new PolicyAdministrationService(_policies, _snapshots, new AuditWriter(_audit, _time), _time).ProposeAsync(F.HrAdmin(), rule, Ct);
        var withoutCompany = new OrgLevelScheme(F.EntityD, [new("division", "本部", 1), new("department", "部", 2), new("team", "チーム", 3)]);

        var ex = await Assert.ThrowsAsync<OrgLevelValidationException>(() => Administration.ProposeAsync(F.HrAdmin(), EntityId.New(), withoutCompany, Ct));
        Assert.Contains(rule.Id.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "組織が使っている段は削除できない（D から team を消す）")]
    public async Task LevelUsedByOrgUnit_CannotBeRemoved()
    {
        var withoutTeam = new OrgLevelScheme(F.EntityD, [new("company", "会社", 1), new("department", "部", 2)]);

        var ex = await Assert.ThrowsAsync<OrgLevelValidationException>(() => Administration.ProposeAsync(F.HrAdmin(), EntityId.New(), withoutTeam, Ct));

        Assert.Contains("D-T1", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "順位の変更で組織の親子の順位が逆転する定義は拒否する（C の課とチームを入れ替える）")]
    public async Task RankChangeBreakingHierarchy_IsRejected()
    {
        var swapped = new OrgLevelScheme(F.EntityC, [new("company", "会社", 1), new("business_unit", "事業部", 2), new("department", "部", 3), new("team", "チーム", 4), new("section", "課", 5)]);

        var ex = await Assert.ThrowsAsync<OrgLevelValidationException>(() => Administration.ProposeAsync(F.HrAdmin(), EntityId.New(), swapped, Ct));

        Assert.Contains("C-T1", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "今日は問題なくても、将来の組織（予定の組織改編）で順位が逆転する定義は拒否する")]
    public async Task RankChangeBreakingFutureHierarchy_IsRejected()
    {
        // 30 日後に C-P1（今は段なし）が新しい段 office になる予定。office をチームより下に置く定義は、その日に C-T3（チーム）の親が下位になる
        var future = F.Today.AddDays(30);
        _store.OrgChangeDates.Add(future);
        _snapshots = new FutureOrgProvider(future, F.Unit("C-P1"), "office");
        var withOffice = new OrgLevelScheme(
            F.EntityC,
            [new("company", "会社", 1), new("business_unit", "事業部", 2), new("department", "部", 3), new("section", "課", 4), new("team", "チーム", 5), new("office", "室", 6)]);

        var ex = await Assert.ThrowsAsync<OrgLevelValidationException>(() => Administration.ProposeAsync(F.HrAdmin(), EntityId.New(), withOffice, Ct));
        Assert.Contains(future.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ex.Message, StringComparison.Ordinal);

        // 室をチームより上（事業部と部の間）に置く定義なら通る
        var officeAboveDepartment = new OrgLevelScheme(
            F.EntityC,
            [new("company", "会社", 1), new("business_unit", "事業部", 2), new("office", "室", 3), new("department", "部", 4), new("section", "課", 5), new("team", "チーム", 6)]);
        await Administration.ProposeAsync(F.HrAdmin(), EntityId.New(), officeAboveDepartment, Ct);
    }

    [Fact(DisplayName = "承認時にも検査する（起案後に、順位が逆転する組織改編の予定が登録された）")]
    public async Task Approval_IsRevalidated()
    {
        var id = EntityId.New();
        var withOffice = new OrgLevelScheme(
            F.EntityC,
            [new("company", "会社", 1), new("business_unit", "事業部", 2), new("department", "部", 3), new("section", "課", 4), new("team", "チーム", 5), new("office", "室", 6)]);
        await Administration.ProposeAsync(F.HrAdmin(), id, withOffice, Ct);

        var future = F.Today.AddDays(30);
        _store.OrgChangeDates.Add(future);
        _snapshots = new FutureOrgProvider(future, F.Unit("C-P1"), "office");

        await Assert.ThrowsAsync<OrgLevelValidationException>(() => Administration.ApproveAsync(HrHead, id, Ct));
        Assert.Equal(OrgLevelSchemeStatus.Proposed, _store.Schemes[id].Status);
        Assert.Equal(AuditOutcome.Denied, _audit.Records[^1].Outcome);
    }

    [Fact(DisplayName = "存在しない法人の定義は起案できない")]
    public async Task UnknownLegalEntity_IsRejected()
    {
        await Assert.ThrowsAsync<OrgLevelValidationException>(() => Administration.ProposeAsync(
            F.HrAdmin(), EntityId.New(), new OrgLevelScheme(Guid.NewGuid(), [new("company", "会社", 1)]), Ct));
    }

    [Fact(DisplayName = "ポリシー: どの段の定義にも無い段は指定できない")]
    public async Task Policy_UnknownLevel_IsRejected()
    {
        var policies = new PolicyAdministrationService(_policies, _snapshots, new AuditWriter(_audit, _time), _time);
        var unknown = PolicyEvaluationTests.Rule(PolicyTargetKind.All, null, [PersonFields.JobType], RelationScope.SameLevel("no_such_level"));
        var known = PolicyEvaluationTests.Rule(PolicyTargetKind.All, null, [PersonFields.JobType], RelationScope.SameLevel("business_unit"));

        await Assert.ThrowsAsync<PolicyValidationException>(() => policies.ProposeAsync(F.HrAdmin(), unknown, Ct));
        await policies.ProposeAsync(F.HrAdmin(), known, Ct);
    }

    /// <summary>今日はフィクスチャのまま、指定日だけ組織の段を変えたスナップショットを返す。</summary>
    private sealed class FutureOrgProvider(DateOnly date, Guid unitId, string levelKey) : IOrgSnapshotProvider
    {
        public Task<OrgSnapshot> LoadAsync(ViewerContext viewer, DateOnly asOf, DateOnly attributesAsOf, CancellationToken cancellationToken)
        {
            var snapshot = F.BuildSnapshot(asOf, attributesAsOf);
            if (asOf != date)
            {
                return Task.FromResult(snapshot);
            }

            return Task.FromResult(OrgSnapshot.Create(
                asOf,
                snapshot.LegalEntities,
                snapshot.Units.Select(u => u.Id == unitId ? u with { LevelKey = levelKey } : u),
                snapshot.People,
                snapshot.People.SelectMany(p => snapshot.AssignmentsOf(p.Id)),
                snapshot.LevelSchemes));
        }
    }
}

/// <summary>段の定義の変更を PostgreSQL の実装（RLS・制約・トリガー）とつないだ結合テスト。</summary>
public sealed class OrgLevelDatabaseTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    [Fact(DisplayName = "段の定義: 起案 → 別人の承認で旧い定義を置き換え、スナップショットとキャッシュに反映される")]
    public async Task Approval_ReplacesSchemeAndInvalidatesCache()
    {
        var provider = new NpgsqlOrgSnapshotProvider(db.AppDataSource);
        var versions = new NpgsqlOrgDataVersionSource(db.AppDataSource);
        using var cache = new OrgSnapshotCache(provider, versions, TimeProvider.System, new OrgSnapshotCacheOptions { VersionCheckInterval = TimeSpan.FromHours(1), MaxAge = TimeSpan.FromHours(2) });
        var store = new NpgsqlOrgLevelStore(db.AppDataSource, TimeProvider.System);
        var administration = new OrgLevelAdministrationService(
            store,
            provider,
            new NpgsqlPolicyRuleStore(db.AppDataSource, TimeProvider.System),
            new AuditWriter(new NpgsqlAuditSink(db.AppDataSource), TimeProvider.System),
            TimeProvider.System,
            cache);
        var viewer = F.User("d_t1_member");
        var before = await cache.LoadAsync(viewer, F.Today, F.Today, Ct);
        Assert.Null(before.BaseUnitOf(F.Unit("D-T1"), "section"));

        var id = EntityId.New();
        var fourLevels = new OrgLevelScheme(F.EntityD, [new("company", "会社", 1), new("department", "部", 2), new("section", "課", 3), new("team", "チーム", 4)]);
        await administration.ProposeAsync(F.HrAdmin(), id, fourLevels, Ct);
        await Assert.ThrowsAsync<AccessDeniedException>(() => administration.ApproveAsync(F.HrAdmin(), id, Ct));
        await administration.ApproveAsync(db.Approver, id, Ct);

        var stored = await store.FindAsync(F.HrAdmin(), id, Ct);
        Assert.Equal(OrgLevelSchemeStatus.Approved, stored!.Status);
        Assert.Equal(fourLevels.Levels, stored.Scheme.Levels);

        // 同一プロセスの承認なので、確認の間隔内でもキャッシュは捨てられている
        var after = await cache.LoadAsync(viewer, F.Today, F.Today, Ct);
        Assert.NotSame(before, after);
        Assert.Equal(4, after.LevelSchemeOf(F.EntityD).Levels.Count);
        Assert.Equal(F.Unit("D-T1"), after.BaseUnitOf(F.Unit("D-T1"), "section"));
        Assert.Single(after.LevelSchemes, s => s.LegalEntityId == F.EntityD);
    }
}
