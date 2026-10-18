using System.Net;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>レート制限・返却人数の制限・不正検知の段階遷移と解除（設定でしきい値を下げて確かめる）。</summary>
public sealed class RateLimitAndAbuseTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    [Fact(DisplayName = "レート制限: エンドポイント種別の上限を超えると 429 と Retry-After。別の利用者・別の種別は影響を受けない")]
    public async Task EndpointLimit_Returns429WithRetryAfter()
    {
        using var issuer = new TestIssuer();
        await using var host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, issuer, ["--JinJi:RateLimit:Api:PerEndpoint:person_read=3"]);
        var token = issuer.User("portal", F.Person("u1_member"));
        var path = $"/api/v1/people/{F.Person("u2_member")}";

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync(path, token)).Status);
        }

        using var limited = await host.GetAsync(path, token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(int.Parse(limited.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) >= 1);
        Assert.Equal("application/problem+json", limited.Content.Headers.ContentType?.MediaType);

        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync(path, issuer.User("portal", F.Person("u2_member")))).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync($"/api/v1/orgs/{F.Unit("S1")}", token)).Status);
    }

    [Fact(DisplayName = "レート制限: クライアント全体の上限は利用者をまたいで効く。未認証の要求は接続元ごとに制限する")]
    public async Task ClientAndAnonymousLimits()
    {
        using var issuer = new TestIssuer();
        await using var host = await ApiTestHost.StartAsync(
            db.Database.AppConnectionString, issuer, ["--JinJi:RateLimit:Api:PerClient=4", "--JinJi:RateLimit:Api:AnonymousPerAddress=2"]);
        var path = $"/api/v1/orgs/{F.Unit("S1")}";

        foreach (var key in new[] { "u1_member", "u2_member", "s2_member", "sec1_head" })
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync(path, issuer.User("portal", F.Person(key)))).Status);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.GetJsonAsync(path, issuer.User("portal", F.Person("dp2_member")))).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync(path, issuer.Service("sync"))).Status);

        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/healthz", null)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/healthz", null)).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.GetJsonAsync("/healthz", null)).Status);
    }

    [Fact(DisplayName = "返却人数の制限: 利用者ごとの返却人数の上限に達すると、人を返すエンドポイントは 429。人を返さないエンドポイントは使える")]
    public async Task ReturnedPeopleLimit()
    {
        using var issuer = new TestIssuer();
        await using var host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, issuer, ["--JinJi:RateLimit:Api:ReturnedPeoplePerUser=5"]);
        var token = issuer.User("portal", F.Person("sec1_head"));

        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/people?query=member&limit=3", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/people?query=member&limit=3", token)).Status);

        using var limited = await host.GetAsync($"/api/v1/people/{F.Person("u1_member")}", token);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        var body = await limited.Content.ReadAsStringAsync(Ct);
        Assert.Contains("urn:jinji:problem:returned-people-limited", body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/orgs/tree", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", issuer.User("portal", F.Person("u1_member")))).Status);
    }

    [Fact(DisplayName = "不正検知: 404 の急増で自動スロットル → 再発で一時停止 → 人事管理者の解除で戻る。監査ログ・メトリクスに残る")]
    public async Task DeniedBurst_ThrottleSuspendRelease()
    {
        using var issuer = new TestIssuer();
        await using var host = await ApiTestHost.StartAsync(
            db.Database.AppConnectionString,
            issuer,
            ["--JinJi:Abuse:DeniedThreshold=3", "--JinJi:RateLimit:Api:ThrottledPerClient=100"]);
        var agent = issuer.Agent("agent-x", F.Person("u1_member"));
        var admin = issuer.User("admin-console", F.Person("hr_admin"), "hr_admin");

        await ProbeAsync(host, agent, 3);
        var (_, statuses) = await host.GetJsonAsync("/api/v1/admin/clients", admin);
        var throttled = Assert.Single(statuses.EnumerateArray());
        Assert.Equal("agent-x", throttled.GetProperty("client_id").GetString());
        Assert.Equal("throttled", throttled.GetProperty("state").GetString());
        Assert.Equal("denied_burst", throttled.GetProperty("rule").GetString());
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}", agent)).Status);

        await ProbeAsync(host, agent, 3);
        var (suspendedStatus, problem) = await host.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}", agent);
        Assert.Equal(HttpStatusCode.Forbidden, suspendedStatus);
        Assert.Equal("urn:jinji:problem:client-suspended", problem.GetProperty("type").GetString());

        // 委任元が人事管理者でも、エージェントには解除させない
        var adminAgent = issuer.Agent("agent-x", F.Person("hr_admin"), "hr_admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetJsonAsync("/api/v1/admin/clients", adminAgent)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await host.GetJsonAsync("/api/v1/admin/clients", issuer.User("admin-console", F.Person("u1_member")))).Status);

        using (var released = await host.PostAsync("/api/v1/admin/clients/agent-x/release", admin))
        {
            Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}", agent)).Status);
        Assert.Empty((await host.GetJsonAsync("/api/v1/admin/clients", admin)).Body.EnumerateArray());
        using (var again = await host.PostAsync("/api/v1/admin/clients/agent-x/release", admin))
        {
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        }

        var actions = await db.QueryAsAppAsync<string>(
            F.HrAdmin(),
            "SELECT action FROM audit_event WHERE target_type = 'api_client:agent-x' ORDER BY occurred_at, action",
            Ct);
        Assert.Equal(["abuse.throttled", "abuse.suspended", "abuse.released"], actions);

        using var client = new HttpClient();
        var metrics = await client.GetStringAsync(new Uri($"http://127.0.0.1:{host.MetricsPort}/metrics"), Ct);
        Assert.Contains("jinji_abuse_detections_total{action=\"abuse.suspended\",client=\"agent-x\",rule=\"denied_burst\"} 1", metrics, StringComparison.Ordinal);
        Assert.Contains("reason=\"suspended\"", metrics, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "不正検知: 全社横断の連続取得（絞り込みの無い一覧）で自動スロットルになり、スロットル中はレート制限が絞られる")]
    public async Task BroadListing_Throttles()
    {
        using var issuer = new TestIssuer();
        await using var host = await ApiTestHost.StartAsync(
            db.Database.AppConnectionString,
            issuer,
            ["--JinJi:Abuse:BroadListingThreshold=2", "--JinJi:RateLimit:Api:ThrottledPerClient=2"]);
        var token = issuer.Service("sync");

        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/people?limit=5", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/people?limit=5", token)).Status);

        // スロットル中: クライアントの上限が 2 に絞られる
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync($"/api/v1/orgs/{F.Unit("S1")}", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync($"/api/v1/orgs/{F.Unit("S1")}", token)).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.GetJsonAsync($"/api/v1/orgs/{F.Unit("S1")}", token)).Status);
    }

    [Fact(DisplayName = "承認ルート: 起点の人と承認者を返却人数に数える。人を替えて総なめにすると、大量の個人参照の不正検知で自動スロットルになる")]
    public async Task ApprovalRoute_EnumerationIsDetected()
    {
        using var issuer = new TestIssuer();
        await using (var returned = await ApiTestHost.StartAsync(db.Database.AppConnectionString, issuer, ["--JinJi:RateLimit:Api:ReturnedPeoplePerUser=5"]))
        {
            var user = issuer.User("portal", F.Person("sec1_head"));
            // u2_member の経路は起点 + 承認者 4 人 = 5 人。次の要求は上限に達しているので 429
            Assert.Equal(HttpStatusCode.OK, (await returned.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}/approval-route", user)).Status);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await returned.GetJsonAsync($"/api/v1/people/{F.Person("u3_member")}/approval-route", user)).Status);
        }

        await using var host = await ApiTestHost.StartAsync(
            db.Database.AppConnectionString,
            issuer,
            ["--JinJi:Abuse:PeopleReturnedThreshold=12", "--JinJi:RateLimit:Api:ThrottledPerClient=1"]);
        var token = issuer.Service("sync");
        foreach (var key in new[] { "u2_member", "s2_member", "u3_member" })
        {
            Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync($"/api/v1/people/{F.Person(key)}/approval-route", token)).Status);
        }

        // スロットル中: クライアントの上限が 1 に絞られる
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 2; i++)
        {
            statuses.Add((await host.GetJsonAsync($"/api/v1/orgs/{F.Unit("S1")}", token)).Status);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact(DisplayName = "件数（facets）: 人の検索と同じ回数の制限・返却人数の確認を受け、絞り込みの無い件数の要求は全社横断の取得として数える")]
    public async Task Facets_AreLimitedLikeSearch()
    {
        using var issuer = new TestIssuer();

        // 返却人数: 件数を求めた要求でも、返した人（1 ページ分）は数える。上限に達したら件数も取れない
        await using (var returned = await ApiTestHost.StartAsync(db.Database.AppConnectionString, issuer, ["--JinJi:RateLimit:Api:ReturnedPeoplePerUser=2"]))
        {
            var user = issuer.User("portal", F.Person("sec1_head"));
            Assert.Equal(HttpStatusCode.OK, (await returned.GetJsonAsync("/api/v1/people?facets=true&limit=1&query=member", user)).Status);
            Assert.Equal(HttpStatusCode.OK, (await returned.GetJsonAsync("/api/v1/people?facets=true&limit=1&query=member", user)).Status);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await returned.GetJsonAsync("/api/v1/people?facets=true&limit=1&query=member", user)).Status);
        }

        await using var host = await ApiTestHost.StartAsync(
            db.Database.AppConnectionString,
            issuer,
            ["--JinJi:RateLimit:Api:PerEndpoint:person_list=3", "--JinJi:Abuse:BroadListingThreshold=2", "--JinJi:RateLimit:Api:ThrottledPerClient=1000"]);

        // 絞り込みの無い件数の要求が続くと自動スロットル（全社横断の連続取得）
        var service = issuer.Service("sync");
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/people?facets=true&limit=1", service)).Status);
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/people?facets=true&limit=1", service)).Status);
        var admin = issuer.User("admin-console", F.Person("hr_admin"), "hr_admin");
        var (_, statuses) = await host.GetJsonAsync("/api/v1/admin/clients", admin);
        Assert.Contains(statuses.EnumerateArray(), s => s.GetProperty("client_id").GetString() == "sync" && s.GetProperty("rule").GetString() == "broad_listing");

        // 回数: 人の検索の種別（person_list）の上限を共有する
        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/people?query=member&limit=1", service)).Status);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await host.GetJsonAsync("/api/v1/people?facets=true&query=member&limit=1", service)).Status);
    }

    private static async Task ProbeAsync(ApiTestHost host, string token, int count)
    {
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await host.GetJsonAsync($"/api/v1/people/{Guid.NewGuid()}", token)).Status);
        }
    }
}
