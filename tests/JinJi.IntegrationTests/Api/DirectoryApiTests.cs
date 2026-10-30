using System.Globalization;
using System.Net;
using System.Text.Json;
using JinJi.Application.Organization;
using JinJi.Domain.Organization;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>
/// UI 向けに足したエンドポイント: 組織ごとの集計・人の一括取得・組織の検索・メタ情報。
/// 集計が閲覧者に見える人だけを数えること（所属者の一覧と一致すること）を、閲覧者の立場ごとに確かめる。
/// </summary>
public sealed class DirectoryApiTests(DirectoryFixtureDatabase db) : IClassFixture<DirectoryFixtureDatabase>, IAsyncLifetime
{
    /// <summary>集計と所属者の一覧を突き合わせるため、回数の制限を緩め、不正検知を止める。一斉異動の下限は 2 件。</summary>
    private static readonly string[] RelaxedArgs =
    [
        "--JinJi:RateLimit:Api:PerClient=100000",
        "--JinJi:RateLimit:Api:PerUser=100000",
        "--JinJi:RateLimit:Api:PerEndpoint:person_list=100000",
        "--JinJi:RateLimit:Api:PerEndpoint:org_read=100000",
        "--JinJi:RateLimit:Api:PerEndpoint:org_stats=100000",
        "--JinJi:RateLimit:Api:ReturnedPeoplePerUser=1000000",
        "--JinJi:RateLimit:Api:ReturnedPeoplePerClient=1000000",
        "--JinJi:Abuse:Enabled=false",
        "--JinJi:Directory:MassActionThreshold=2",
        // 所属を渡さないクライアント（人）
        "--JinJi:Auth:Clients:5:ClientId=no-assign",
        "--JinJi:Auth:Clients:5:ActorTypes:0=user",
        "--JinJi:Auth:Clients:5:Fields:0=display_name",
        "--JinJi:Auth:Clients:5:Fields:1=employee_number",
    ];

    private readonly TestIssuer _issuer = new();
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer, RelaxedArgs);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _issuer.Dispose();
    }

    private string UserToken(string key) => _issuer.User("portal", F.Person(key));

    private string AdminToken() => _issuer.User("portal", F.Person("hr_admin"), "hr_admin");

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- 組織ごとの集計 ----------------------------------------------------------------------------------------------

    public static TheoryData<string, int?> StatsViewers => new()
    {
        { "user:sec1_head", null },
        { "user:sec1_head", -60 },
        { "user:sec1_head", -250 },
        { "user:r1_member", null },
        { "admin", null },
        { "admin", -250 },
        { "admin", 10 },
        { "admin", 20 },
        { "service:attendance", null },
        { "agent:u2_member", null },
        { "no-assign:u1_member", null },
    };

    [Theory(DisplayName = "集計: 閲覧者の立場・時点ごとに、所属者の一覧に現れる人だけを数える（人数・配下計・兼務・出向・組織間の集計が一覧から求めたものと一致）")]
    [MemberData(nameof(StatsViewers))]
    public async Task Stats_CountOnlyMembersVisibleToViewer(string viewer, int? offsetDays)
    {
        var token = Token(viewer);
        var asOf = offsetDays is { } days ? "?as_of=" + Date(F.Today.AddDays(days)) : string.Empty;
        var sep = asOf.Length == 0 ? "?" : "&";

        var (treeStatus, tree) = await _host.GetJsonAsync("/api/v1/orgs/tree" + asOf, token);
        Assert.Equal(HttpStatusCode.OK, treeStatus);
        var (statsStatus, stats) = await _host.GetJsonAsync("/api/v1/orgs/stats" + asOf, token);
        Assert.Equal(HttpStatusCode.OK, statsStatus);
        Assert.Equal(tree.GetProperty("as_of").GetString(), stats.GetProperty("as_of").GetString());

        var unitIds = tree.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("id").GetGuid()).ToList();
        var parents = tree.GetProperty("units").EnumerateArray().ToDictionary(
            u => u.GetProperty("id").GetGuid(),
            u => u.GetProperty("parent_id").ValueKind == JsonValueKind.Null ? (Guid?)null : u.GetProperty("parent_id").GetGuid());
        var counted = stats.GetProperty("units").EnumerateArray().ToDictionary(u => u.GetProperty("org_unit_id").GetGuid());
        Assert.Equal(unitIds, stats.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("org_unit_id").GetGuid()).ToList());

        // 所属者の一覧（見える人だけが現れる）から、組織ごとの区分と組織間の組を求める
        var people = new Dictionary<Guid, JsonElement>();
        foreach (var unit in unitIds)
        {
            var (status, members) = await _host.GetJsonAsync($"/api/v1/orgs/{unit}/members{asOf}{sep}limit=100", token);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(JsonValueKind.Null, members.GetProperty("next_cursor").ValueKind);
            foreach (var member in members.GetProperty("items").EnumerateArray())
            {
                people[member.GetProperty("id").GetGuid()] = member;
            }
        }

        var expected = unitIds.ToDictionary(u => u, _ => (Primary: new HashSet<Guid>(), Concurrent: new HashSet<Guid>(), Secondment: new HashSet<Guid>()));
        var links = new Dictionary<(Guid, Guid, string), int>();
        foreach (var (id, person) in people)
        {
            var assignments = person.GetProperty("assignments").EnumerateArray()
                .Select(a => (Unit: a.GetProperty("org_unit_id").GetGuid(), Type: a.GetProperty("type").GetString()!, Secondment: a.GetProperty("secondment").GetString()!))
                .ToList();
            foreach (var a in assignments)
            {
                var bucket = expected[a.Unit];
                (a.Secondment != "none" ? bucket.Secondment : a.Type == "concurrent" ? bucket.Concurrent : bucket.Primary).Add(id);
            }

            var home = assignments.Where(a => a.Type == "primary" && a.Secondment == "none").Select(a => a.Unit).ToHashSet();
            if (home.Count == 0)
            {
                home = assignments.Where(a => a.Type == "primary").Select(a => a.Unit).ToHashSet();
            }

            foreach (var pair in assignments
                         .Where(a => !home.Contains(a.Unit) && (a.Type == "concurrent" || a.Secondment != "none"))
                         .SelectMany(a => home.Select(h => (h, a.Unit, a.Secondment != "none" ? "secondment" : "concurrent")))
                         .Distinct())
            {
                links[pair] = links.GetValueOrDefault(pair) + 1;
            }
        }

        foreach (var unit in unitIds)
        {
            var actual = counted[unit];
            var subtree = unitIds.Where(u => IsSelfOrDescendant(u, unit, parents)).SelectMany(u => expected[u].Primary).Distinct().Count();
            Assert.Equal(
                (expected[unit].Primary.Count, subtree, expected[unit].Concurrent.Count, expected[unit].Secondment.Count),
                (actual.GetProperty("primary").GetInt32(), actual.GetProperty("primary_total").GetInt32(), actual.GetProperty("concurrent").GetInt32(), actual.GetProperty("secondment").GetInt32()));
        }

        var actualLinks = stats.GetProperty("links").EnumerateArray()
            .ToDictionary(l => (l.GetProperty("from_org_unit_id").GetGuid(), l.GetProperty("to_org_unit_id").GetGuid(), l.GetProperty("kind").GetString()!), l => l.GetProperty("count").GetInt32());
        Assert.Equal(links.OrderBy(l => l.Key).ToList(), actualLinks.OrderBy(l => l.Key).ToList());
    }

    [Fact(DisplayName = "集計: 内示・入社予定・退職者・一時停止・兼務・出向・ポリシーで隠れた人の数え方（一般・人事管理者）")]
    public async Task Stats_SpecificCases()
    {
        var user = await StatsAsync(UserToken("sec1_head"), null);

        // 一時停止中の人（suspended）と内示のある人（s1_member）は今日の所属で数える。入社予定の人（future_hire）は数えない
        Assert.Equal((4, 0, 0), Counts(user, "S1"));
        Assert.Equal((1, 0, 0), Counts(user, "U2"));
        // 兼務（concurrent: 主務 S2・兼務 U1）、退職予定（leaving）は今は在籍。退職者（retired・long_retired）は今日の所属が無い
        Assert.Equal((4, 0, 0), Counts(user, "S2"));
        Assert.Equal((2, 1, 0), Counts(user, "U1"));
        // 出向: seconded（主務が出向）と dual（兼務が出向）は出向だけに数える
        Assert.Equal((1, 0, 1), Counts(user, "B-DP"));
        Assert.Equal((1, 0, 1), Counts(user, "B-G1"));
        Assert.Equal((2, 1, 0), Counts(user, "DP2"));
        // ポリシーで所属が課内（R1 の中）だけの R1 の人は、外からは数えない
        Assert.Equal((0, 0, 0), Counts(user, "R1"));
        Assert.Equal(17, Total(user, "D1"));
        Assert.Equal(
            new[] { (F.Unit("S1"), F.Unit("DP2"), "concurrent", 1), (F.Unit("S1"), F.Unit("B-G1"), "secondment", 1), (F.Unit("S2"), F.Unit("U1"), "concurrent", 1) }.Order().ToList(),
            Links(user).Order().ToList());
        Assert.DoesNotContain(db.FutureUnit, Units(user));

        var r1 = await StatsAsync(UserToken("r1_member"), null);
        Assert.Equal((2, 0, 0), Counts(r1, "R1"));

        var admin = await StatsAsync(AdminToken(), null);
        Assert.Equal((2, 0, 0), Counts(admin, "R1"));
        Assert.Equal(19, Total(admin, "D1"));

        // 人事管理者は未来の時点で、異動・入社の予定を含めて数えられる
        var afterTransfer = await StatsAsync(AdminToken(), F.Today.AddDays(10));
        Assert.Equal((3, 0, 0), Counts(afterTransfer, "S1"));
        Assert.Equal((5, 0, 0), Counts(afterTransfer, "S2"));
        Assert.Contains(db.FutureUnit, Units(afterTransfer));
        var afterHire = await StatsAsync(AdminToken(), F.Today.AddDays(20));
        Assert.Equal((2, 0, 0), Counts(afterHire, "U2"));

        // 過去の時点: 退職後の公開期間内の人（retired）は数え、過ぎた人（long_retired）は一般には数えない
        Assert.Equal((5, 0, 0), Counts(await StatsAsync(UserToken("sec1_head"), F.Today.AddDays(-60)), "S2"));
        Assert.Equal((5, 0, 0), Counts(await StatsAsync(UserToken("sec1_head"), F.Today.AddDays(-250)), "S2"));
        Assert.Equal((6, 0, 0), Counts(await StatsAsync(AdminToken(), F.Today.AddDays(-250)), "S2"));

        // 所属を渡さないクライアントには誰も数えない
        var noAssign = await StatsAsync(_issuer.User("no-assign", F.Person("u1_member")), null);
        Assert.All(noAssign.GetProperty("units").EnumerateArray(), u => Assert.Equal(0, u.GetProperty("primary_total").GetInt32() + u.GetProperty("concurrent").GetInt32() + u.GetProperty("secondment").GetInt32()));
        Assert.Empty(noAssign.GetProperty("links").EnumerateArray());

        // 一般の閲覧者は未来の時点を指定できない
        using var denied = await _host.GetAsync($"/api/v1/orgs/stats?as_of={Date(F.Today.AddDays(1))}", UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/orgs/stats?as_of=2026/01/01", UserToken("sec1_head"))).Status);

        // 監査ログ: 集計の参照を記録する（人は返さないので対象者は空）
        var subjects = await db.QueryAsAppAsync<Guid[]>(F.HrAdmin(), "SELECT subject_person_ids FROM audit_event WHERE action = $1", Ct, OrgDirectoryService.StatsAction);
        Assert.NotEmpty(subjects);
        Assert.All(subjects, s => Assert.Empty(s));
    }

    [Fact(DisplayName = "集計: レート制限は集計の種別（org_stats）で数え、組織の参照・返却人数の制限とは別枠")]
    public async Task Stats_HaveTheirOwnRateLimit()
    {
        await using var host = await ApiTestHost.StartAsync(
            db.Database.AppConnectionString,
            _issuer,
            ["--JinJi:RateLimit:Api:PerEndpoint:org_stats=2", "--JinJi:RateLimit:Api:ReturnedPeoplePerUser=1"]);
        var token = UserToken("sec1_head");

        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/orgs/stats", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/orgs/stats", token)).Status);
        var (status, problem) = await host.GetJsonAsync("/api/v1/orgs/stats", token);
        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("urn:jinji:problem:rate-limited", problem.GetProperty("type").GetString());

        // 組織の参照は別枠。集計は人を返さないので返却人数（上限 1 人）にも数えない
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/orgs/tree", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", token)).Status);
    }

    // ---- 人の一括取得 ------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "一括取得: 指定の順で返し、存在しない・見えない・重複した ID は単に含めない")]
    public async Task Batch_ReturnsVisiblePeopleInRequestedOrder()
    {
        var ids = new[] { F.Person("s2_member"), Guid.NewGuid(), F.Person("long_retired"), F.Person("u1_member"), F.Person("future_hire"), F.Person("s2_member") };
        var query = "/api/v1/people?ids=" + string.Join(',', ids);

        var (status, user) = await _host.GetJsonAsync(query, UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([F.Person("s2_member"), F.Person("u1_member")], ApiEndpointTests.Ids(user.GetProperty("items")));
        Assert.Equal(JsonValueKind.Null, user.GetProperty("next_cursor").ValueKind);

        var (_, admin) = await _host.GetJsonAsync(query, AdminToken());
        Assert.Equal([F.Person("s2_member"), F.Person("long_retired"), F.Person("u1_member")], ApiEndpointTests.Ids(admin.GetProperty("items")));

        var (_, future) = await _host.GetJsonAsync($"{query}&as_of={Date(F.Today.AddDays(20))}", AdminToken());
        Assert.Contains(F.Person("future_hire"), ApiEndpointTests.Ids(future.GetProperty("items")));

        // 見えない人ばかりでも 200 の空（404 にしない。何件が見えなかったかも返さない）
        var (emptyStatus, empty) = await _host.GetJsonAsync($"/api/v1/people?ids={Guid.NewGuid()},{F.Person("long_retired")}", UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.OK, emptyStatus);
        Assert.Empty(empty.GetProperty("items").EnumerateArray());
    }

    [Fact(DisplayName = "一括取得: 上限（重複を除いて 100 件）・形の不正・他の条件との併用は 400")]
    public async Task Batch_ValidatesInput()
    {
        var token = UserToken("sec1_head");
        var hundred = string.Join(',', Enumerable.Range(0, 100).Select(_ => Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.OK, (await _host.GetJsonAsync($"/api/v1/people?ids={hundred}", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _host.GetJsonAsync($"/api/v1/people?ids={hundred},{hundred.Split(',')[0]}", token)).Status);

        foreach (var bad in new[]
                 {
                     $"ids={hundred},{Guid.NewGuid()}", "ids=", "ids=not-a-uuid", $"ids={F.Person("u1_member")}&query=member",
                     $"ids={F.Person("u1_member")}&limit=10", $"ids={F.Person("u1_member")}&cursor=x", $"ids={F.Person("u1_member")}&org={F.Unit("S1")}",
                     $"ids={F.Person("u1_member")}&as_of=2026/01/01",
                 })
        {
            using var response = await _host.GetAsync($"/api/v1/people?{bad}", token);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, bad);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact(DisplayName = "一括取得: 返した人数を返却人数の制限に数える（見えない ID は数えない）")]
    public async Task Batch_CountsTowardsReturnedPeopleLimit()
    {
        await using var host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer, ["--JinJi:RateLimit:Api:ReturnedPeoplePerUser=2"]);
        var token = UserToken("sec1_head");

        var (first, body) = await host.GetJsonAsync($"/api/v1/people?ids={F.Person("u1_member")},{Guid.NewGuid()},{F.Person("long_retired")}", token);
        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync($"/api/v1/people?ids={F.Person("u2_member")}", token)).Status);

        using var limited = await host.GetAsync($"/api/v1/people?ids={F.Person("s2_member")}", token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        using var json = JsonDocument.Parse(await limited.Content.ReadAsStringAsync(Ct));
        Assert.Equal("urn:jinji:problem:returned-people-limited", json.RootElement.GetProperty("type").GetString());
    }

    // ---- 組織の検索 --------------------------------------------------------------------------------------------------

    [Theory(DisplayName = "組織の検索: 組織名の正規化（全角半角・漢数字と算用数字・「第」の有無・異体字）と組織コードで当たる")]
    [InlineData("営業1課", "S1")]
    [InlineData("営業１課", "S1")]
    [InlineData("営業 一 課", "S1")]
    [InlineData("第１本部", "D1")]
    [InlineData("1本部", "D1")]
    [InlineData("第壱", null)]
    [InlineData("高崎", "PAST")]
    [InlineData("ｄｐ１", "DP1")]
    [InlineData("dp1", "DP1")]
    [InlineData("特命", "R1")]
    public async Task OrgSearch_Normalizes(string query, string? expectedFirst)
    {
        var (status, body) = await _host.GetJsonAsync("/api/v1/orgs?query=" + Uri.EscapeDataString(query), UserToken("sec1_head"));

        Assert.Equal(HttpStatusCode.OK, status);
        var codes = body.GetProperty("items").EnumerateArray().Select(u => u.GetProperty("code").GetString()).ToList();
        if (expectedFirst is null)
        {
            Assert.Empty(codes);
        }
        else
        {
            Assert.Equal(expectedFirst, codes.First());
        }
    }

    [Fact(DisplayName = "組織の検索: 完全一致・前方一致・部分一致の順、limit で打ち切ると has_more、入力の検査、時点の規則")]
    public async Task OrgSearch_RanksLimitsAndValidates()
    {
        var token = UserToken("sec1_head");

        var (_, sales) = await _host.GetJsonAsync("/api/v1/orgs?query=" + Uri.EscapeDataString("営業部"), token);
        var names = sales.GetProperty("items").EnumerateArray().Select(u => (u.GetProperty("code").GetString(), u.GetProperty("name").GetString())).ToList();
        Assert.Equal(("DP1", "営業部"), names[0]);
        Assert.Equal(("C-DP1", "営業部"), names[1]);
        Assert.False(sales.GetProperty("has_more").GetBoolean());

        var (_, limited) = await _host.GetJsonAsync("/api/v1/orgs?limit=1&query=" + Uri.EscapeDataString("営業"), token);
        Assert.Single(limited.GetProperty("items").EnumerateArray());
        Assert.True(limited.GetProperty("has_more").GetBoolean());

        // 組織長は見える人だけ
        var (_, s1) = await _host.GetJsonAsync("/api/v1/orgs?query=S1", token);
        Assert.Equal(F.Person("sec1_head"), s1.GetProperty("items")[0].GetProperty("head_person_id").GetGuid());

        // 新設の予定の組織は、一般には見えず、人事管理者は未来の時点で探せる
        var newUnit = "/api/v1/orgs?query=" + Uri.EscapeDataString("新設準備");
        Assert.Empty((await _host.GetJsonAsync(newUnit, token)).Body.GetProperty("items").EnumerateArray());
        Assert.Equal(db.FutureUnit, (await _host.GetJsonAsync($"{newUnit}&as_of={Date(F.Today.AddDays(5))}", AdminToken())).Body.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.GetJsonAsync($"{newUnit}&as_of={Date(F.Today.AddDays(5))}", token)).Status);

        foreach (var bad in new[] { string.Empty, "query=", "query=%E5%96%B6*", "query=a%25", "query=%20", "query=x&limit=101", "query=x&limit=0", "query=x&as_of=2026/01/01" })
        {
            using var response = await _host.GetAsync($"/api/v1/orgs?{bad}", token);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, bad);
        }
    }

    // ---- メタ情報 ----------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "メタ情報: 業務日付・段の定義を返し、組織改編・一斉異動の未来の日付は人事管理者にだけ返す")]
    public async Task Meta_HidesFutureMilestonesFromNonAdmin()
    {
        var (status, user) = await _host.GetJsonAsync("/api/v1/meta", UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Date(F.Today), user.GetProperty("business_date").GetString());
        Assert.Equal("Asia/Tokyo", user.GetProperty("time_zone").GetString());
        Assert.False(user.GetProperty("can_view_future").GetBoolean());

        var entities = user.GetProperty("legal_entities").EnumerateArray().ToDictionary(e => e.GetProperty("code").GetString()!);
        Assert.True(entities["A"].GetProperty("uses_default_levels").GetBoolean());
        Assert.Equal(["division", "department", "section", "unit"], entities["A"].GetProperty("levels").EnumerateArray().Select(l => l.GetProperty("key").GetString()).ToList());
        Assert.False(entities["C"].GetProperty("uses_default_levels").GetBoolean());
        Assert.Equal(["company", "business_unit", "department", "section", "team"], entities["C"].GetProperty("levels").EnumerateArray().Select(l => l.GetProperty("key").GetString()).ToList());
        Assert.Equal(4, user.GetProperty("default_levels").GetArrayLength());

        var userMilestones = Milestones(user);
        Assert.All(userMilestones.Keys, d => Assert.True(d <= F.Today, $"{d} は業務日付より後"));
        Assert.Contains("org_change", userMilestones[F.LongAgo]);
        Assert.Equal(["org_change"], userMilestones[F.Today.AddDays(-50)]);
        Assert.Equal(["mass_action"], userMilestones[F.Today.AddYears(-1)]);
        Assert.Equal(userMilestones.Keys.Order().ToList(), userMilestones.Keys.ToList());

        var (_, admin) = await _host.GetJsonAsync("/api/v1/meta", AdminToken());
        Assert.True(admin.GetProperty("can_view_future").GetBoolean());
        var adminMilestones = Milestones(admin);
        Assert.Equal(["org_change"], adminMilestones[F.Today.AddDays(5)]);
        Assert.Equal(["org_change"], adminMilestones[F.Today.AddDays(7)]);
        Assert.Equal(["mass_action"], adminMilestones[F.Today.AddDays(3)]);
        // 下限（2 件）に満たない日（10 日後の異動 1 件）は目盛りにしない
        Assert.False(adminMilestones.ContainsKey(F.Today.AddDays(10)));
        Assert.Equal(userMilestones, adminMilestones.Where(m => m.Key <= F.Today).ToDictionary());

        // 社内システム・AI エージェント（委任元が一般）も同じ
        Assert.All(Milestones((await _host.GetJsonAsync("/api/v1/meta", _issuer.Service("attendance"))).Body).Keys, d => Assert.True(d <= F.Today));
        Assert.All(Milestones((await _host.GetJsonAsync("/api/v1/meta", _issuer.Agent("agent-x", F.Person("u1_member")))).Body).Keys, d => Assert.True(d <= F.Today));
    }

    // ---- OpenAPI の文書と応答の一致 ----------------------------------------------------------------------------------

    [Fact(DisplayName = "OpenAPI: 主要なエンドポイントの応答が文書のスキーマ（required・null・enum・型）に一致する")]
    public async Task OpenApi_ResponsesMatchDocument()
    {
        var (_, document) = await _host.GetJsonAsync("/openapi/v1.json", null);
        var validator = new OpenApiResponseValidator(document);
        var user = UserToken("sec1_head");
        var admin = AdminToken();
        var future = Date(F.Today.AddDays(10));
        var cases = new (string Path, string Url, string Token)[]
        {
            ("/api/v1/people/{id}", $"/api/v1/people/{F.Person("s1_member")}", user),
            ("/api/v1/people/{id}", $"/api/v1/people/{F.Person("s1_member")}", admin),
            ("/api/v1/people/{id}", $"/api/v1/people/{F.Person("suspended")}", user),
            ("/api/v1/people/{id}", $"/api/v1/people/{F.Person("u1_member")}?as_of={future}", admin),
            ("/api/v1/people/{id}", $"/api/v1/people/{F.Person("retired")}", admin),
            ("/api/v1/people/{id}", $"/api/v1/people/{db.Dual}", user),
            ("/api/v1/people/{id}", $"/api/v1/people/{F.Person("u1_member")}", _issuer.Service("attendance")),
            ("/api/v1/people", "/api/v1/people?query=member&limit=5", user),
            ("/api/v1/people", $"/api/v1/people?ids={F.Person("seconded")},{F.Person("concurrent")},{db.Dual}", admin),
            ("/api/v1/people", "/api/v1/people?facets=true&sort=kana&limit=3", user),
            ("/api/v1/people", $"/api/v1/people?facets=true&org={F.Unit("D1")}&include_descendants=true&assignment_kind=concurrent&assignment_kind=secondment&level=section", admin),
            ("/api/v1/people", "/api/v1/people?facets=true&query=zzzz", user),
            ("/api/v1/people/{id}/manager", $"/api/v1/people/{F.Person("u1_member")}/manager", user),
            ("/api/v1/orgs/{id}", $"/api/v1/orgs/{F.Unit("A-ROOT")}", user),
            ("/api/v1/orgs/{id}", $"/api/v1/orgs/{F.Unit("U2")}", user),
            ("/api/v1/orgs/{id}/members", $"/api/v1/orgs/{F.Unit("DP1")}/members?include_descendants=true&limit=3", user),
            ("/api/v1/orgs/tree", "/api/v1/orgs/tree", user),
            ("/api/v1/orgs", "/api/v1/orgs?query=" + Uri.EscapeDataString("営業"), user),
            ("/api/v1/orgs/stats", "/api/v1/orgs/stats", user),
            ("/api/v1/orgs/stats", $"/api/v1/orgs/stats?as_of={future}", admin),
            ("/api/v1/meta", "/api/v1/meta", admin),
            ("/api/v1/changes", "/api/v1/changes", user),
            ("/api/v1/changes", "/api/v1/changes?since=" + Uri.EscapeDataString((await _host.GetJsonAsync("/api/v1/changes", admin)).Body.GetProperty("next_cursor").GetString()!), admin),
            ("/api/v1/admin/clients", "/api/v1/admin/clients", admin),
        };

        foreach (var (path, url, token) in cases)
        {
            var (status, body) = await _host.GetJsonAsync(url, token);
            Assert.True(status == HttpStatusCode.OK, $"{url}: {status}");
            var errors = validator.Validate(validator.ResponseSchema(path), body);
            Assert.True(errors.Count == 0, $"{url}\n{string.Join("\n", errors)}");
        }
    }

    [Fact(DisplayName = "OpenAPI: 応答の型は全項目が required、数値に文字列を許さず、コード値に enum があり、/metrics は載らない")]
    public async Task OpenApi_DocumentDeclarations()
    {
        var (_, document) = await _host.GetJsonAsync("/openapi/v1.json", null);
        var schemas = document.GetProperty("components").GetProperty("schemas");
        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();

        Assert.DoesNotContain("/metrics", paths);
        Assert.DoesNotContain("/healthz", paths);
        // REST API（/api/v1）と人のログインの JSON API（/auth）だけ
        Assert.DoesNotContain(paths, p => !p.StartsWith("/api/v1/", StringComparison.Ordinal) && !p.StartsWith("/auth/", StringComparison.Ordinal));
        foreach (var expected in new[] { "/api/v1/orgs", "/api/v1/orgs/stats", "/api/v1/meta" })
        {
            Assert.Contains(expected, paths);
        }

        foreach (var schema in schemas.EnumerateObject().Where(s => s.Name.EndsWith("Response", StringComparison.Ordinal)))
        {
            var properties = schema.Value.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
            var required = schema.Value.GetProperty("required").EnumerateArray().Select(r => r.GetString()!).Order(StringComparer.Ordinal).ToList();
            Assert.True(properties.SequenceEqual(required), $"{schema.Name}: required に無い項目 {string.Join(", ", properties.Except(required))}");
        }

        // integer | string（数値を文字列でも受け付ける既定）が残っていない
        var mixed = new List<string>();
        void Walk(JsonElement element, string at)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Array)
                {
                    var types = type.EnumerateArray().Select(t => t.GetString()).ToList();
                    if (types.Contains("string") && (types.Contains("integer") || types.Contains("number")))
                    {
                        mixed.Add(at);
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, $"{at}.{property.Name}");
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{at}[{i++}]");
                }
            }
        }

        Walk(document, "$");
        Assert.Empty(mixed);
        Assert.Equal("integer", schemas.GetProperty("OrgLevelResponse").GetProperty("properties").GetProperty("rank").GetProperty("type").GetString());

        string[] Enum(string schema, string property)
        {
            var p = schemas.GetProperty(schema).GetProperty("properties").GetProperty(property);
            var target = p.TryGetProperty("items", out var items) ? items : p;
            return [.. target.GetProperty("enum").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? "null" : v.GetString()!)];
        }

        Assert.Equal(["primary", "concurrent"], Enum("AssignmentResponse", "type"));
        Assert.Equal(["none", "outbound", "inbound"], Enum("AssignmentResponse", "secondment"));
        Assert.Equal(["active", "terminated", "null"], Enum("PersonResponse", "employment_status"));
        Assert.Equal(["active", "suspended", "null"], Enum("PersonResponse", "account_status"));
        Assert.Contains("position_appointment", Enum("PersonnelActionResponse", "action_type"));
        Assert.Equal(["primary", "concurrent", "null"], Enum("PersonnelActionResponse", "assignment_type"));
        Assert.Equal(["person", "org_unit", "site", "group"], Enum("ChangeResponse", "type"));
        Assert.Equal(SiteCodes.Types, Enum("SiteRefResponse", "type"));
        Assert.Equal(SiteCodes.Types, Enum("SiteResponse", "type"));
        Assert.Equal(["concurrent", "secondment"], Enum("OrgLinkResponse", "kind"));
        Assert.Equal(["org_change", "mass_action"], Enum("MilestoneResponse", "kinds"));
        Assert.Equal(["normal", "throttled", "suspended"], Enum("ClientStatusResponse", "state"));
        Assert.Contains("needs_review", Enum("ProposalResponse", "status"));
        Assert.Contains("critical", Enum("SyncPlanItemResponse", "priority"));
    }

    private string Token(string viewer) => viewer.Split(':') switch
    {
        ["admin"] => AdminToken(),
        ["user", var key] => UserToken(key),
        ["service", var client] => _issuer.Service(client),
        ["agent", var key] => _issuer.Agent("agent-x", F.Person(key)),
        ["no-assign", var key] => _issuer.User("no-assign", F.Person(key)),
        _ => throw new ArgumentException(viewer, nameof(viewer)),
    };

    private async Task<JsonElement> StatsAsync(string token, DateOnly? asOf)
    {
        var (status, body) = await _host.GetJsonAsync("/api/v1/orgs/stats" + (asOf is { } d ? "?as_of=" + Date(d) : string.Empty), token);
        Assert.Equal(HttpStatusCode.OK, status);
        return body;
    }

    private JsonElement Unit(JsonElement stats, string key) =>
        stats.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("org_unit_id").GetGuid() == F.Unit(key));

    private (int Primary, int Concurrent, int Secondment) Counts(JsonElement stats, string key)
    {
        var unit = Unit(stats, key);
        return (unit.GetProperty("primary").GetInt32(), unit.GetProperty("concurrent").GetInt32(), unit.GetProperty("secondment").GetInt32());
    }

    private int Total(JsonElement stats, string key) => Unit(stats, key).GetProperty("primary_total").GetInt32();

    private static List<Guid> Units(JsonElement stats) => [.. stats.GetProperty("units").EnumerateArray().Select(u => u.GetProperty("org_unit_id").GetGuid())];

    private static IEnumerable<(Guid, Guid, string, int)> Links(JsonElement stats) =>
        stats.GetProperty("links").EnumerateArray().Select(l => (
            l.GetProperty("from_org_unit_id").GetGuid(),
            l.GetProperty("to_org_unit_id").GetGuid(),
            l.GetProperty("kind").GetString()!,
            l.GetProperty("count").GetInt32()));

    private static Dictionary<DateOnly, List<string>> Milestones(JsonElement meta) =>
        meta.GetProperty("milestones").EnumerateArray().ToDictionary(
            m => DateOnly.ParseExact(m.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            m => m.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()!).ToList());

    private static bool IsSelfOrDescendant(Guid unit, Guid ancestor, Dictionary<Guid, Guid?> parents)
    {
        for (Guid? current = unit; current is { } id; current = parents.GetValueOrDefault(id))
        {
            if (id == ancestor)
            {
                return true;
            }
        }

        return false;
    }
}
