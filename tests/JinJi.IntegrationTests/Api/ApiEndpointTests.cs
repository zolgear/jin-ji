using System.Globalization;
using System.Net;
using System.Text.Json;
using JinJi.Application.Organization;
using JinJi.Application.People;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>
/// REST API の端から端まで（Kestrel・JWT の検証・入口の共通処理・Application 層・実 DB の RLS）。閲覧者の立場ごとの応答と列挙対策。
/// </summary>
public sealed class ApiEndpointTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>, IAsyncLifetime
{
    private readonly TestIssuer _issuer = new();
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _issuer.Dispose();
    }

    private string UserToken(string key) => _issuer.User("portal", F.Person(key));

    private string AdminToken() => _issuer.User("portal", F.Person("hr_admin"), "hr_admin");

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact(DisplayName = "人: 一般の閲覧者には内示の発令・将来の終了日が見えず、人事管理者には見える")]
    public async Task Person_HidesPlannedChangesFromNonAdmin()
    {
        var path = $"/api/v1/people/{F.Person("s1_member")}";

        var (status, body) = await _host.GetJsonAsync(path, UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.OK, status);
        var assignment = Assert.Single(body.GetProperty("assignments").EnumerateArray());
        Assert.Equal(F.Unit("S1"), assignment.GetProperty("org_unit_id").GetGuid());
        Assert.Equal(JsonValueKind.Null, assignment.GetProperty("valid_until").ValueKind);
        Assert.DoesNotContain(Ids(body.GetProperty("personnel_actions")), id => id == F.FutureTransferActionId);

        var (adminStatus, admin) = await _host.GetJsonAsync(path, AdminToken());
        Assert.Equal(HttpStatusCode.OK, adminStatus);
        Assert.Equal(Date(F.Today.AddDays(10)), Assert.Single(admin.GetProperty("assignments").EnumerateArray()).GetProperty("valid_until").GetString());
        Assert.Contains(Ids(admin.GetProperty("personnel_actions")), id => id == F.FutureTransferActionId);
    }

    [Fact(DisplayName = "人: 退職予定者の退職年月日は人事管理者にだけ返す")]
    public async Task Person_HidesFutureTerminationDate()
    {
        var path = $"/api/v1/people/{F.Person("leaving")}";

        var (_, user) = await _host.GetJsonAsync(path, UserToken("s2_member"));
        Assert.Contains("termination_date", Strings(user.GetProperty("visible_fields")));
        Assert.Equal(JsonValueKind.Null, user.GetProperty("termination_date").ValueKind);
        Assert.Equal("active", user.GetProperty("employment_status").GetString());

        var (_, admin) = await _host.GetJsonAsync(path, AdminToken());
        Assert.Equal(Date(F.Today.AddDays(15)), admin.GetProperty("termination_date").GetString());
    }

    [Fact(DisplayName = "人: 入社予定者は一般には見えず、人事管理者は未来の時点で見られる。一般が未来の時点を指定すると 403")]
    public async Task Person_FutureHireAndFutureAsOf()
    {
        var path = $"/api/v1/people/{F.Person("future_hire")}";
        var future = Date(F.Today.AddDays(20));

        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync(path, UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.OK, (await _host.GetJsonAsync($"{path}?as_of={future}", AdminToken())).Status);

        using var denied = await _host.GetAsync($"{path}?as_of={future}", UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType?.MediaType);
    }

    [Fact(DisplayName = "列挙対策: 存在しない ID・公開期間を過ぎた退職者・形の不正な ID は同じ 404 になる（403 と区別しない）")]
    public async Task Enumeration_NotFoundAndInvisibleAreIndistinguishable()
    {
        var token = UserToken("sec1_head");
        var responses = new List<(HttpStatusCode Status, string? ContentType, string Body)>();
        foreach (var id in new[] { Guid.NewGuid().ToString(), F.Person("long_retired").ToString(), "not-a-uuid" })
        {
            using var response = await _host.GetAsync($"/api/v1/people/{id}", token);
            var text = await response.Content.ReadAsStringAsync(Ct);
            using var json = JsonDocument.Parse(text);
            // traceId・instance（要求したパス）はリクエストごとに違うので除いて比べる
            var problem = string.Join("|", json.RootElement.EnumerateObject().Where(p => p.Name is not ("traceId" or "trace_id" or "instance" or "requestId")).Select(p => $"{p.Name}={p.Value}"));
            responses.Add((response.StatusCode, response.Content.Headers.ContentType?.ToString(), problem));
        }

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.NotFound, r.Status));
        var distinct = responses.Select(r => (r.ContentType, r.Body)).Distinct().ToList();
        Assert.True(distinct.Count == 1, string.Join("\n", distinct));

        // 人事管理者には退職者が見える（存在はする）
        Assert.Equal(HttpStatusCode.OK, (await _host.GetJsonAsync($"/api/v1/people/{F.Person("long_retired")}", AdminToken())).Status);

        // 上長・組織も同じ
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/people/{F.Person("long_retired")}/manager", token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/orgs/{Guid.NewGuid()}", token)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/orgs/{Guid.NewGuid()}/members", token)).Status);
    }

    [Fact(DisplayName = "クライアントの項目上限: 勤怠システムには上限の項目だけを返す")]
    public async Task ServiceClient_FieldLimitApplies()
    {
        var (status, body) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", _issuer.Service("attendance"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            ["assignments", "display_name", "employee_number", "employment_status", "managers"],
            Strings(body.GetProperty("visible_fields")).Order(StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("work_email").ValueKind);
        Assert.Equal("新姓 太郎", body.GetProperty("display_name").GetString());
    }

    [Fact(DisplayName = "AI エージェント: 委任元の人として判定される（本人の個人単位の例外の項目も見える）")]
    public async Task Agent_ActsAsDelegator()
    {
        var token = _issuer.Agent("agent-x", F.Person("u2_member"));

        var (_, self) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}", token);
        Assert.Contains("extension_number", Strings(self.GetProperty("visible_fields")));

        var (_, other) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}", _issuer.Agent("agent-x", F.Person("u1_member")));
        Assert.DoesNotContain("extension_number", Strings(other.GetProperty("visible_fields")));
    }

    [Fact(DisplayName = "検索: 正規化表記で照合し、ワイルドカード・短すぎる語・過大な limit を拒否する")]
    public async Task Search_NormalizesAndValidates()
    {
        var token = UserToken("sec1_head");

        var (status, body) = await _host.GetJsonAsync("/api/v1/people?query=" + Uri.EscapeDataString("ｓ２＿ＭＥＭＢＥＲ"), token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(F.Person("s2_member"), Ids(body.GetProperty("items")));

        foreach (var bad in new[] { "query=s2*", "query=s2%25", "query=s", "limit=101", "limit=0", "as_of=2026/01/01", "org=abc" })
        {
            using var response = await _host.GetAsync($"/api/v1/people?{bad}", token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact(DisplayName = "検索: 見えない項目では当たらない（他の本部の人の社用メール）")]
    public async Task Search_DoesNotMatchInvisibleFields()
    {
        var query = "/api/v1/people?query=" + Uri.EscapeDataString("hr_head@example");

        var (_, user) = await _host.GetJsonAsync(query, UserToken("sec1_head"));
        Assert.Empty(user.GetProperty("items").EnumerateArray());

        var (_, admin) = await _host.GetJsonAsync(query, AdminToken());
        Assert.Equal([F.Person("hr_head")], Ids(admin.GetProperty("items")));
    }

    [Fact(DisplayName = "検索: カーソルで続きを読める。改ざん・別の条件・別の利用者のカーソルは 400")]
    public async Task Search_CursorIsTamperProof()
    {
        var token = UserToken("sec1_head");
        var (_, first) = await _host.GetJsonAsync("/api/v1/people?query=member&limit=2", token);
        var cursor = first.GetProperty("next_cursor").GetString()!;
        var (_, second) = await _host.GetJsonAsync($"/api/v1/people?query=member&limit=2&cursor={Uri.EscapeDataString(cursor)}", token);

        Assert.Equal(2, Ids(first.GetProperty("items")).Count);
        Assert.Empty(Ids(first.GetProperty("items")).Intersect(Ids(second.GetProperty("items"))));
        Assert.True(Ids(first.GetProperty("items")).Max() < Ids(second.GetProperty("items")).Min());

        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? "B" : "A") + cursor[^1];
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/people?query=member&cursor={Uri.EscapeDataString(tampered)}", token)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/people?query=head&cursor={Uri.EscapeDataString(cursor)}", token)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/people?query=member&cursor={Uri.EscapeDataString(cursor)}", UserToken("u1_member"))).Status);
    }

    [Fact(DisplayName = "組織: 参照・所属者・組織ツリー。所属者に将来の異動者は一般には現れず、人事管理者は未来の時点で見られる")]
    public async Task Orgs_ReadMembersAndTree()
    {
        var token = UserToken("sec1_head");

        var (_, unit) = await _host.GetJsonAsync($"/api/v1/orgs/{F.Unit("S1")}", token);
        Assert.Equal("営業一課", unit.GetProperty("name").GetString());
        Assert.Equal("section", unit.GetProperty("level").GetProperty("key").GetString());
        Assert.Equal(F.Unit("DP1"), unit.GetProperty("parent_id").GetGuid());
        Assert.Equal(F.Person("sec1_head"), unit.GetProperty("head_person_id").GetGuid());

        var (_, members) = await _host.GetJsonAsync($"/api/v1/orgs/{F.Unit("S2")}/members?limit=100", token);
        var ids = Ids(members.GetProperty("items"));
        Assert.Contains(F.Person("s2_member"), ids);
        Assert.Contains(F.Person("concurrent"), ids);
        Assert.DoesNotContain(F.Person("s1_member"), ids);

        var (_, future) = await _host.GetJsonAsync($"/api/v1/orgs/{F.Unit("S2")}/members?limit=100&as_of={Date(F.Today.AddDays(10))}", AdminToken());
        Assert.Contains(F.Person("s1_member"), Ids(future.GetProperty("items")));

        var (_, descendants) = await _host.GetJsonAsync($"/api/v1/orgs/{F.Unit("DP1")}/members?include_descendants=true&limit=100", token);
        Assert.Contains(F.Person("u1_member"), Ids(descendants.GetProperty("items")));

        var (_, tree) = await _host.GetJsonAsync("/api/v1/orgs/tree", token);
        Assert.Equal(F.Units.Count, tree.GetProperty("units").GetArrayLength());
        Assert.Equal(Date(F.Today), tree.GetProperty("as_of").GetString());
    }

    [Fact(DisplayName = "上長: 直属の上長を返し、監査ログに対象者と上長の ID を 1 件で残す")]
    public async Task Manager_ReturnsDirectManagerAndIsAudited()
    {
        var (status, body) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}/manager", _issuer.Service("attendance"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([F.Person("unit1_head")], Ids(body.GetProperty("managers")));

        var subjects = await db.QueryAsAppAsync<Guid[]>(
            F.HrAdmin(),
            "SELECT subject_person_ids FROM audit_event WHERE action = $1 AND actor_client_id = 'attendance' AND actor_type = 'service' AND channel = 'api'",
            Ct,
            PersonDirectoryService.ManagerAction);
        Assert.Equal(new[] { F.Person("u1_member"), F.Person("unit1_head") }.Order(), Assert.Single(subjects).Order());
    }

    [Fact(DisplayName = "組織の参照も監査ログに残る（組織長の ID）")]
    public async Task OrgRead_IsAudited()
    {
        await _host.GetJsonAsync($"/api/v1/orgs/{F.Unit("U1")}", _issuer.Service("sync"));

        var subjects = await db.QueryAsAppAsync<Guid[]>(
            F.HrAdmin(),
            "SELECT subject_person_ids FROM audit_event WHERE action = $1 AND actor_client_id = 'sync'",
            Ct,
            OrgDirectoryService.ReadAction);
        Assert.Equal([F.Person("unit1_head")], Assert.Single(subjects));
    }

    [Fact(DisplayName = "認証: トークンなし・知らない鍵・違う audience・期限切れは 401、未登録のクライアント・受け付けない操作者の種別は 403")]
    public async Task Authentication_RejectsInvalidTokens()
    {
        var path = $"/api/v1/people/{F.Person("u1_member")}";
        using var stranger = new TestIssuer();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetJsonAsync(path, null)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetJsonAsync(path, stranger.User("portal", F.Person("u1_member")))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetJsonAsync(path, _issuer.Create(
            new() { ["sub"] = F.Person("u1_member").ToString(), ["client_id"] = "portal" }, audience: "other-api"))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.GetJsonAsync(path, _issuer.Create(
            new() { ["sub"] = F.Person("u1_member").ToString(), ["client_id"] = "portal" }, expires: DateTime.UtcNow.AddMinutes(-5)))).Status);

        var (unregistered, problem) = await _host.GetJsonAsync(path, _issuer.User("unknown-client", F.Person("u1_member")));
        Assert.Equal(HttpStatusCode.Forbidden, unregistered);
        Assert.Equal("urn:jinji:problem:client-rejected", problem.GetProperty("type").GetString());

        // portal は人のクライアント。社内システムのトークン（sub がクライアント自身）は受け付けない
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.GetJsonAsync(path, _issuer.Service("portal"))).Status);
    }

    [Fact(DisplayName = "ロールは発行者の設定で信用する場合だけ使う")]
    public async Task Roles_AreTrustedOnlyForConfiguredIssuers()
    {
        using var external = new TestIssuer("https://external-idp.test");
        await using var host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer, [.. external.ConfigArgs(1, trustRoles: false)]);
        var path = $"/api/v1/people/{F.Person("long_retired")}";

        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync(path, _issuer.User("portal", F.Person("hr_admin"), "hr_admin"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await host.GetJsonAsync(path, external.User("portal", F.Person("hr_admin"), "hr_admin"))).Status);
    }

    [Fact(DisplayName = "OpenAPI 3.1 の文書を生成する")]
    public async Task OpenApi_DocumentIsGenerated()
    {
        var (status, document) = await _host.GetJsonAsync("/openapi/v1.json", null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.StartsWith("3.1", document.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        var paths = document.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        foreach (var expected in new[]
                 {
                     "/api/v1/people/{id}", "/api/v1/people", "/api/v1/people/{id}/manager", "/api/v1/orgs/{id}", "/api/v1/orgs/{id}/members",
                     "/api/v1/orgs/tree", "/api/v1/changes",
                 })
        {
            Assert.Contains(expected, paths);
        }

        Assert.True(document.GetProperty("components").GetProperty("securitySchemes").TryGetProperty("bearer", out _));
        var parameters = document.GetProperty("paths").GetProperty("/api/v1/people").GetProperty("get").GetProperty("parameters")
            .EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("as_of", parameters);
        Assert.Contains("cursor", parameters);
    }

    [Fact(DisplayName = "メトリクス: 専用のポートでだけ公開し、API・MCP のポートからは見えない")]
    public async Task Metrics_AreOnlyOnTheirOwnPort()
    {
        await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", UserToken("u2_member"));
        await _host.GetJsonAsync($"/api/v1/people/{Guid.NewGuid()}", UserToken("u2_member"));

        using var client = new HttpClient();
        using var viaApi = await client.GetAsync(new Uri($"http://127.0.0.1:{_host.ApiPort}/metrics"), Ct);
        using var viaMcp = await client.GetAsync(new Uri($"http://127.0.0.1:{_host.McpPort}/metrics"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, viaApi.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, viaMcp.StatusCode);

        var text = await client.GetStringAsync(new Uri($"http://127.0.0.1:{_host.MetricsPort}/metrics"), Ct);
        Assert.Contains("# TYPE jinji_requests_total counter", text, StringComparison.Ordinal);
        Assert.Contains("client=\"portal\"", text, StringComparison.Ordinal);
        Assert.Contains("jinji_people_returned_total{channel=\"api\",client=\"portal\"}", text, StringComparison.Ordinal);
        Assert.Contains("jinji_requests_rejected_total{channel=\"api\",client=\"portal\",reason=\"not_found\"}", text, StringComparison.Ordinal);
        Assert.Contains("# TYPE jinji_request_duration_seconds histogram", text, StringComparison.Ordinal);

        // メトリクスのポートから API は見えない
        using var apiViaMetrics = await client.GetAsync(new Uri($"http://127.0.0.1:{_host.MetricsPort}/healthz"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, apiViaMetrics.StatusCode);
    }

    internal static List<Guid> Ids(JsonElement array) =>
        [.. array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetGuid() : e.GetProperty("id").GetGuid())];

    internal static List<string> Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];
}
