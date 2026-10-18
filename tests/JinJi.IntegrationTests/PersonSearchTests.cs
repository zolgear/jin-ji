using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Application.People;
using JinJi.Domain.Organization;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests;

/// <summary>
/// 人の多角的な検索（絞り込み・件数・並べ替え・読みの照合）。DB の代わりにメモリ上のスナップショットを使い、
/// 件数が閲覧者に見える人の見える値だけから数えられていること（全ページを読んで数え直したものと一致すること）を、閲覧者の立場ごとに確かめる。
/// </summary>
/// <remarks>
/// フィクスチャ（<see cref="OrgFixture"/>）に、拠点（組織 D1 に東京本社、人ごとに大阪支社・名古屋支店）・リモートワーク・職種・雇用区分・フリガナ・ローマ字・入社年月日を足して使う。
/// ポリシー（<see cref="PolicyEvaluationTests.Policies"/>）では、入社年月日は法人内だけ、営業部（DP1）の直属の人の拠点・リモートワークは本人だけに見える。
/// </remarks>
public sealed class PersonSearchTests
{
    private static readonly OrgFixture F = OrgFixture.Shared;
    private static readonly Site Tokyo = new(Guid.CreateVersion7(), F.EntityA, "S01", "東京本社", SiteType.HeadOffice, "13", "千代田区");
    private static readonly Site Osaka = new(Guid.CreateVersion7(), F.EntityA, "S02", "大阪支社", SiteType.Branch, "27", null);
    private static readonly Site Nagoya = new(Guid.CreateVersion7(), F.EntityC, "S03", "名古屋支店", SiteType.Branch, "23", null);
    private readonly EnrichedSnapshotProvider _snapshots = new(F);
    private readonly InMemoryPolicyRuleStore _policies = new(PolicyEvaluationTests.Policies.Rules);
    private readonly RecordingAuditSink _audit = new();
    private readonly FixedTimeProvider _time = new(F.Today);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PersonDirectoryService Directory => new(_snapshots, _policies, new AuditWriter(_audit, _time), _time);

    public static TheoryData<string> Viewers => new() { "user:sec1_head", "user:dept1_head", "user:r1_member", "user:c_t1_member", "admin", "agent:u2_member", "service" };

    private static ViewerContext Viewer(string key) => key switch
    {
        "admin" => F.HrAdmin(),
        "service" => ViewerContext.ForService("attendance", FieldLimit.Of([PersonFields.EmployeeNumber, PersonFields.Assignments, PersonFields.EmploymentType])),
        _ when key.StartsWith("agent:", StringComparison.Ordinal) =>
            ViewerContext.ForAgent("agent-x", F.Person(key[6..]), [], Channel.Mcp, FieldLimit.Of([PersonFields.DisplayName, PersonFields.Site, PersonFields.Assignments])),
        _ => F.User(key[5..]),
    };

    [Theory(DisplayName = "件数: 閲覧者ごとに、全ページを読んで見える値から数え直した件数・人数と一致する（見えない人・見えない値は数えない）")]
    [MemberData(nameof(Viewers))]
    public async Task Facets_MatchCountsRecomputedFromVisibleResults(string viewerKey)
    {
        var viewer = Viewer(viewerKey);
        foreach (var criteria in new[]
                 {
                     new PersonSearch(),
                     new PersonSearch { OrgUnitId = F.Unit("D1"), IncludeDescendants = true },
                     new PersonSearch { Query = "taro" },
                     new PersonSearch { AssignmentKinds = new HashSet<AssignmentKind> { AssignmentKind.Primary } },
                 })
        {
            var page = (await Directory.SearchPeopleAsync(viewer, criteria with { IncludeFacets = true, Limit = 1 }, null, Ct))!;
            var all = await ReadAllAsync(viewer, criteria);
            Assert.Equal(all.Count, page.Total);

            var facets = page.Facets!;
            Assert.Equal(Count(all, v => v.LegalEntity?.Id.ToString("D")), ToDictionary(facets.LegalEntities));
            Assert.Equal(Count(all, v => v.EmploymentType), ToDictionary(facets.EmploymentTypes));
            Assert.Equal(Count(all, v => v.JobType), ToDictionary(facets.JobTypes));
            Assert.Equal(Count(all, v => v.Site?.Id.ToString("D")), ToDictionary(facets.Sites));
            Assert.Equal(Count(all, v => v.Site is { } site ? SiteCodes.ToCode(site.Type) : null), ToDictionary(facets.SiteTypes));
            Assert.Equal(Count(all, v => v.Site?.PrefectureCode), ToDictionary(facets.Prefectures));
            Assert.Equal(Count(all, v => v.RemoteWork switch { true => "true", false => "false", null => null }), ToDictionary(facets.RemoteWork));
            Assert.Equal(Count(all, v => v.EmploymentStatus), ToDictionary(facets.EmploymentStatuses));
            Assert.Equal(Count(all, v => v.AccountStatus), ToDictionary(facets.AccountStatuses));
            Assert.Equal(CountAssignments(all, a => a.PositionTitle), ToDictionary(facets.Positions));
            Assert.Equal(
                CountAssignments(all, a => a.Secondment != SecondmentType.None ? "secondment" : a.Type == AssignmentType.Primary ? "primary" : "concurrent")
                    .Where(kv => criteria.AssignmentKinds.Count == 0 || kv.Key == "primary")
                    .ToDictionary(),
                ToDictionary(facets.AssignmentKinds).Where(kv => criteria.AssignmentKinds.Count == 0 || kv.Key == "primary").ToDictionary());
        }
    }

    [Fact(DisplayName = "件数: 見えない拠点・リモートワーク・入社年月日・人は数えず、絞り込みにも当たらない（本人・人事管理者には見える）")]
    public async Task Facets_DoNotRevealHiddenValues()
    {
        var withFacets = new PersonSearch { IncludeFacets = true, Limit = 100 };
        var osaka = Osaka.Id.ToString("D");

        // 営業部の直属（部長）の拠点・リモートワークは本人だけ（大阪支社の唯一の人。都道府県 27 も大阪支社だけ）
        var user = (await Directory.SearchPeopleAsync(F.User("sec1_head"), withFacets, null, Ct))!;
        Assert.DoesNotContain(user.Facets!.Sites, v => v.Value == osaka);
        Assert.DoesNotContain(user.Facets.Prefectures, v => v.Value == "27");
        Assert.Empty((await Directory.SearchPeopleAsync(F.User("sec1_head"), new PersonSearch { SiteIds = new HashSet<Guid> { Osaka.Id } }, null, Ct))!.Items);
        Assert.Empty((await Directory.SearchPeopleAsync(F.User("sec1_head"), new PersonSearch { Prefectures = new HashSet<string> { "27" } }, null, Ct))!.Items);
        var self = (await Directory.SearchPeopleAsync(F.User("dept1_head"), withFacets, null, Ct))!;
        Assert.Equal(1, self.Facets!.Sites.Single(v => v.Value == osaka).Count);
        Assert.Equal("大阪支社", self.Facets.Sites.Single(v => v.Value == osaka).Label);
        Assert.Equal("大阪府", self.Facets.Prefectures.Single(v => v.Value == "27").Label);
        var admin = (await Directory.SearchPeopleAsync(F.HrAdmin(), withFacets, null, Ct))!;
        Assert.Equal(1, admin.Facets!.Sites.Single(v => v.Value == osaka).Count);

        // リモートワーク: 部長（対象）は本人と人事管理者の件数にだけ現れる（一般の閲覧者の件数には、見えない値は数えない）
        var shown = user.Items.ToDictionary(v => v.PersonId);
        var hiddenRemote = admin.Items.Count(v => v.RemoteWork == true && shown.TryGetValue(v.PersonId, out var u) && !u.CanSee(PersonFields.RemoteWork));
        var adminOnlyRemote = admin.Items.Count(v => v.RemoteWork == true && !shown.ContainsKey(v.PersonId));
        Assert.True(hiddenRemote >= 1);
        Assert.Equal(
            user.Facets.RemoteWork.Single(v => v.Value == "true").Count,
            admin.Facets.RemoteWork.Single(v => v.Value == "true").Count - hiddenRemote - adminOnlyRemote);

        // 入社年月日は法人内だけ: 法人 A の閲覧者の範囲の検索には、他の法人の人が当たらない
        var hired = new PersonSearch { HiredFrom = F.LongAgo.AddYears(-50), Limit = 100 };
        var userHired = (await Directory.SearchPeopleAsync(F.User("sec1_head"), hired, null, Ct))!.Items;
        Assert.NotEmpty(userHired);
        Assert.All(userHired, v => Assert.Equal(F.EntityA, v.LegalEntity!.Id));
        Assert.Contains((await Directory.SearchPeopleAsync(F.HrAdmin(), hired, null, Ct))!.Items, v => v.LegalEntity!.Id == F.EntityC);

        // 入社予定・退職後の公開期間を過ぎた人は、人事管理者以外の人数に数えない
        Assert.Equal(user.Total, user.Items.Count);
        Assert.DoesNotContain(user.Items, v => v.PersonId == F.Person("future_hire") || v.PersonId == F.Person("long_retired"));
        Assert.Contains(admin.Items, v => v.PersonId == F.Person("long_retired"));
        Assert.True(admin.Total > user.Total);

        // 一時停止中・退職は、在籍状態・アカウント状態が見える人にだけ数える
        Assert.Equal(1, user.Facets.AccountStatuses.Single(v => v.Value == "suspended").Count);
        Assert.Equal(1, user.Facets.EmploymentStatuses.Single(v => v.Value == "terminated").Count);
        var service = (await Directory.SearchPeopleAsync(Viewer("service"), withFacets, null, Ct))!;
        Assert.Empty(service.Facets!.AccountStatuses);
        Assert.Empty(service.Facets.EmploymentStatuses);
        Assert.Empty(service.Facets.Sites);
        Assert.Empty(service.Facets.RemoteWork);
        Assert.Empty((await Directory.SearchPeopleAsync(Viewer("service"), new PersonSearch { AccountStatuses = new HashSet<string> { "suspended" } }, null, Ct))!.Items);
    }

    [Fact(DisplayName = "件数: ある条件の件数は、その条件以外に一致する人で数える（選んだ値以外の候補も出る）")]
    public async Task Facets_AreDisjunctivePerCriterion()
    {
        var viewer = F.User("sec1_head");
        var tokyo = new PersonSearch { SiteIds = new HashSet<Guid> { Tokyo.Id }, JobTypes = new HashSet<string> { "営業" }, IncludeFacets = true, Limit = 100 };
        var page = (await Directory.SearchPeopleAsync(viewer, tokyo, null, Ct))!;

        var sales = await ReadAllAsync(viewer, new PersonSearch { JobTypes = new HashSet<string> { "営業" } });
        Assert.Equal(Count(sales, v => v.Site?.Id.ToString("D")), ToDictionary(page.Facets!.Sites));
        Assert.True(page.Facets.Sites.Count > 1);

        var inTokyo = await ReadAllAsync(viewer, new PersonSearch { SiteIds = new HashSet<Guid> { Tokyo.Id } });
        Assert.Equal(Count(inTokyo, v => v.JobType), ToDictionary(page.Facets.JobTypes));
        Assert.Equal(sales.Count(v => v.Site?.Id == Tokyo.Id), page.Total);

        // 種別・都道府県・リモートワークも、その条件以外に一致する人で数える
        var branches = (await Directory.SearchPeopleAsync(viewer, new PersonSearch { SiteTypes = new HashSet<string> { "branch" }, RemoteWork = true, IncludeFacets = true, Limit = 100 }, null, Ct))!;
        var remote = await ReadAllAsync(viewer, new PersonSearch { RemoteWork = true });
        Assert.Equal(Count(remote, v => v.Site is { } site ? SiteCodes.ToCode(site.Type) : null), ToDictionary(branches.Facets!.SiteTypes));
        var branchPeople = await ReadAllAsync(viewer, new PersonSearch { SiteTypes = new HashSet<string> { "branch" } });
        Assert.Equal(Count(branchPeople, v => v.RemoteWork switch { true => "true", false => "false", null => null }), ToDictionary(branches.Facets.RemoteWork));
        Assert.All(branches.Items, v => Assert.True(v.RemoteWork == true && v.Site!.Type == SiteType.Branch));
    }

    [Fact(DisplayName = "絞り込み: 所属の条件（組織・所属の種類・段・役職）は同じ 1 つの所属で満たす")]
    public async Task AssignmentCriteria_AreSatisfiedByOneAssignment()
    {
        var viewer = F.User("sec1_head");
        async Task<HashSet<string>> Keys(PersonSearch search) =>
            [.. (await ReadAllAsync(viewer, search)).Select(v => F.PersonKey(v.PersonId))];

        var concurrentInU1 = await Keys(new PersonSearch { OrgUnitId = F.Unit("U1"), AssignmentKinds = new HashSet<AssignmentKind> { AssignmentKind.Concurrent } });
        Assert.Equal(["concurrent"], concurrentInU1);
        var primaryInU1 = await Keys(new PersonSearch { OrgUnitId = F.Unit("U1"), AssignmentKinds = new HashSet<AssignmentKind> { AssignmentKind.Primary } });
        Assert.DoesNotContain("concurrent", primaryInU1);
        Assert.Contains("u1_member", primaryInU1);

        Assert.Equal(["seconded"], await Keys(new PersonSearch { AssignmentKinds = new HashSet<AssignmentKind> { AssignmentKind.Secondment } }));

        // 課長は課（section）の組織にいる。係（unit）の課長はいない
        var heads = await Keys(new PersonSearch { Positions = new HashSet<string> { "課長" }, LevelKeys = new HashSet<string>(StringComparer.Ordinal) { "section" } });
        Assert.Equal(["c_s1_head", "sec1_head", "sec2_head"], heads.Order(StringComparer.Ordinal));
        Assert.Empty(await Keys(new PersonSearch { Positions = new HashSet<string> { "課長" }, LevelKeys = new HashSet<string>(StringComparer.Ordinal) { "unit" } }));

        // 段の件数は、役職の条件に一致する所属の段で数える
        var page = (await Directory.SearchPeopleAsync(viewer, new PersonSearch { Positions = new HashSet<string> { "課長" }, IncludeFacets = true }, null, Ct))!;
        Assert.Equal(3, page.Facets!.Levels.Single(v => v.Value == "section").Count);
        Assert.Equal("課", page.Facets.Levels.Single(v => v.Value == "section").Label);
        Assert.DoesNotContain(page.Facets.Levels, v => v.Value == "unit");
    }

    [Theory(DisplayName = "読み: ひらがな・カタカナ・ローマ字の綴りの揺れ・長音の有無を同一視して当たる")]
    [InlineData("さとう")]
    [InlineData("サトウ")]
    [InlineData("ｻﾄｳ")]
    [InlineData("sato")]
    [InlineData("Satoh")]
    [InlineData("satou hanako")]
    [InlineData("はなこさとう")]
    public async Task Query_MatchesReadings(string query)
    {
        var items = (await Directory.SearchPeopleAsync(F.User("sec1_head"), new PersonSearch { Query = query }, null, Ct))!.Items;
        Assert.Contains(items, v => v.PersonId == F.Person("u2_member"));
    }

    [Fact(DisplayName = "読み: 見えない読み（フリガナ・ローマ字）では当たらない")]
    public async Task Query_DoesNotMatchHiddenReadings()
    {
        var limited = ViewerContext.ForUser(F.Person("sec1_head"), [], Channel.Ui, FieldLimit.Of([PersonFields.DisplayName]));
        Assert.Empty((await Directory.SearchPeopleAsync(limited, new PersonSearch { Query = "satou" }, null, Ct))!.Items);
        Assert.NotEmpty((await Directory.SearchPeopleAsync(F.User("sec1_head"), new PersonSearch { Query = "satou" }, null, Ct))!.Items);
    }

    [Theory(DisplayName = "並べ替え: どの並びでも、ページを続けて読むと重複も抜けも無く、見えない値の人は最後になる")]
    [InlineData(PersonSort.Id)]
    [InlineData(PersonSort.Kana)]
    [InlineData(PersonSort.Org)]
    [InlineData(PersonSort.HireDate)]
    [InlineData(PersonSort.HireDateDesc)]
    public async Task Sort_PagesAreConsistent(PersonSort sort)
    {
        var viewer = F.User("sec1_head");
        var full = (await Directory.SearchPeopleAsync(viewer, new PersonSearch { Sort = sort, Limit = 100 }, null, Ct))!.Items.Select(v => v.PersonId).ToList();
        var paged = (await ReadAllAsync(viewer, new PersonSearch { Sort = sort }, pageSize: 3)).Select(v => v.PersonId).ToList();
        Assert.Equal(full, paged);
        Assert.Equal(full.Count, full.Distinct().Count());

        var views = (await Directory.SearchPeopleAsync(viewer, new PersonSearch { Sort = sort, Limit = 100 }, null, Ct))!.Items;
        switch (sort)
        {
            case PersonSort.Kana:
                var withKana = views.TakeWhile(v => v.FamilyNameKana is not null).ToList();
                Assert.True(withKana.Count > 1);
                Assert.All(views.Skip(withKana.Count), v => Assert.Null(v.FamilyNameKana));
                Assert.Equal(withKana.Select(v => v.FamilyNameKana + v.GivenNameKana).Order(StringComparer.Ordinal), withKana.Select(v => v.FamilyNameKana + v.GivenNameKana));
                break;
            case PersonSort.HireDate or PersonSort.HireDateDesc:
                var dated = views.TakeWhile(v => v.HireDate is not null).Select(v => v.HireDate!.Value).ToList();
                Assert.All(views.Skip(dated.Count), v => Assert.Null(v.HireDate));
                Assert.Equal(sort == PersonSort.HireDate ? dated.Order() : dated.OrderDescending(), dated);
                break;
            case PersonSort.Org:
                Assert.Equal(F.Person("president"), views[0].PersonId);
                break;
        }
    }

    [Fact(DisplayName = "検査: 件数は最初のページだけ・入社年月日の範囲の逆転・知らない状態・多すぎる値は拒否する。絞り込みがあれば全社横断の一覧ではない")]
    public async Task Validate_RejectsInvalidCriteria()
    {
        foreach (var bad in new[]
                 {
                     new PersonSearch { IncludeFacets = true, After = F.Person("u1_member") },
                     new PersonSearch { HiredFrom = F.Today, HiredTo = F.Today.AddDays(-1) },
                     new PersonSearch { EmploymentStatuses = new HashSet<string> { "retired" } },
                     new PersonSearch { AccountStatuses = new HashSet<string> { "Suspended" } },
                     new PersonSearch { Positions = new HashSet<string> { " " } },
                     new PersonSearch { JobTypes = new HashSet<string> { "a\nb" } },
                     new PersonSearch { SiteIds = Enumerable.Range(0, 21).Select(_ => Guid.NewGuid()).ToHashSet() },
                     new PersonSearch { SiteTypes = new HashSet<string> { "headquarters" } },
                     new PersonSearch { Prefectures = new HashSet<string> { "48" } },
                 })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => Directory.SearchPeopleAsync(F.User("sec1_head"), bad, null, Ct));
        }

        Assert.True(new PersonSearch().IsBroad);
        Assert.False(new PersonSearch { JobTypes = new HashSet<string> { "営業" } }.IsBroad);
        Assert.False(new PersonSearch { HiredTo = F.Today }.IsBroad);
        Assert.False(new PersonSearch { RemoteWork = false }.IsBroad);
        Assert.NotEqual(new PersonSearch { RemoteWork = false }.Fingerprint(), new PersonSearch { RemoteWork = true }.Fingerprint());
        Assert.NotEqual(new PersonSearch { Sort = PersonSort.Kana }.Fingerprint(), new PersonSearch().Fingerprint());
    }

    [Fact(DisplayName = "続き: 位置の人を参照できなくなったら、並べ替えたページの続きは作らない（最初からやり直してもらう）")]
    public async Task StaleCursor_IsRejected()
    {
        var search = new PersonSearch { Sort = PersonSort.Kana, After = F.Person("long_retired") };
        await Assert.ThrowsAsync<StaleCursorException>(() => Directory.SearchPeopleAsync(F.User("sec1_head"), search, null, Ct));
        Assert.NotNull(await Directory.SearchPeopleAsync(F.HrAdmin(), search, null, Ct));
    }

    [Fact(DisplayName = "監査: 件数を求めた検索も 1 件の person.search で、対象者は返した人だけ")]
    public async Task Facets_AuditOnlyReturnedPeople()
    {
        var page = (await Directory.SearchPeopleAsync(F.User("sec1_head"), new PersonSearch { IncludeFacets = true, Limit = 2 }, null, Ct))!;
        var record = Assert.Single(_audit.Records);
        Assert.Equal(PersonDirectoryService.SearchAction, record.Action);
        Assert.Equal(page.Items.Select(v => v.PersonId).Order(), record.SubjectPersonIds.Order());
        Assert.True(page.Total > 2);
    }

    private async Task<List<PersonView>> ReadAllAsync(ViewerContext viewer, PersonSearch criteria, int pageSize = 100)
    {
        var all = new List<PersonView>();
        Guid? after = null;
        do
        {
            var page = (await Directory.SearchPeopleAsync(viewer, criteria with { After = after, Limit = pageSize, IncludeFacets = false }, null, Ct))!;
            all.AddRange(page.Items);
            after = page.NextAfter;
        }
        while (after is not null);
        return all;
    }

    private static Dictionary<string, int> Count(IEnumerable<PersonView> views, Func<PersonView, string?> value) =>
        views.Select(value).OfType<string>().GroupBy(v => v, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> CountAssignments(IEnumerable<PersonView> views, Func<AssignmentView, string?> value) =>
        views.Where(v => v.Assignments is not null)
            .SelectMany(v => v.Assignments!.Select(value).OfType<string>().Distinct(StringComparer.Ordinal))
            .GroupBy(v => v, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> ToDictionary(IEnumerable<FacetValue> values) => values.ToDictionary(v => v.Value, v => v.Count, StringComparer.Ordinal);

    /// <summary>フィクスチャのスナップショットに、検索の条件に使う属性を足して返す。</summary>
    private sealed class EnrichedSnapshotProvider(OrgFixture fixture) : IOrgSnapshotProvider
    {
        public Task<OrgSnapshot> LoadAsync(ViewerContext viewer, DateOnly asOf, DateOnly attributesAsOf, CancellationToken cancellationToken)
        {
            var s = fixture.BuildSnapshot(asOf, attributesAsOf);
            var people = s.People.Select(p => p with { Current = Enrich(fixture.PersonKey(p.Id), p.Current), History = [.. p.History.Select(h => Enrich(fixture.PersonKey(p.Id), h))] });

            // 組織 D1（営業本部）に東京本社。配下の組織・所属者は引き継ぐ
            var units = s.Units.Select(u => u.Id == fixture.Unit("D1") ? u with { SiteId = Tokyo.Id } : u);
            return Task.FromResult(OrgSnapshot.Create(s.AsOf, s.LegalEntities, units, people, s.People.SelectMany(p => s.AssignmentsOf(p.Id)), s.LevelSchemes, [Tokyo, Osaka, Nagoya]));
        }

        private PersonVersion Enrich(string key, PersonVersion v)
        {
            var index = fixture.People.FindIndex(p => p.Id == fixture.Person(key));
            var kana = key switch
            {
                "u2_member" => ("サトウ", "ハナコ", "Satoh", "Hanako"),
                "u1_member" => ("イトウ", "ケン", "Ito", "Ken"),
                "sec1_head" => ("オオノ", "ジュン", "Ohno", "Jun"),
                "s2_member" => ("ヤマモト", "レン", "Yamamoto", "Ren"),
                "concurrent" => ("カトウ", "アイ", "Kato", "Ai"),
                _ => ((string?)null, (string?)null, (string?)null, (string?)null),
            };
            return v with
            {
                FamilyNameKana = kana.Item1,
                GivenNameKana = kana.Item2,
                FamilyNameLatin = kana.Item3,
                GivenNameLatin = kana.Item4 ?? "Taro",
                SiteId = key == "dept1_head" ? Osaka.Id : key.StartsWith("c_", StringComparison.Ordinal) || key.StartsWith("d_", StringComparison.Ordinal) ? Nagoya.Id : null,
                RemoteWork = key is "dept1_head" or "u2_member" or "s2_member" || key.StartsWith("c_", StringComparison.Ordinal),
                JobType = key is "hr_head" or "hr_admin" or "div2_head" ? "人事" : key.StartsWith("d_", StringComparison.Ordinal) ? "技術" : "営業",
                EmploymentType = key is "u2_member" or "s3_member" ? "contract" : key == "d_x_member" ? "part_time" : v.EmploymentType,
                HireDate = v.HireDate.AddDays(index),
            };
        }
    }
}
