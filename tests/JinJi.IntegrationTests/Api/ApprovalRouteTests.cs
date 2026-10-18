using System.Net;
using System.Text.Json;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>
/// 承認ルート（<c>GET /api/v1/people/{id}/approval-route</c>・MCP の <c>get_approval_route</c>。§9「承認ルート」）を実 DB で確かめる:
/// 組織長の連鎖・空席の繰り上げ・本人が組織長・兼務先起点・段と役職の引き当て・法人ごとの段・一時停止と退職・時点・項目の上限・REST と MCP の一致。
/// </summary>
public sealed class ApprovalRouteTests(RouteFixtureDatabase db) : IClassFixture<RouteFixtureDatabase>, IAsyncLifetime
{
    private const string Loopback = "http://127.0.0.1:43131/callback";
    private OAuthTestEnvironment _env = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    /// <summary>社内システム（外部の発行者の JWT、項目の上限なし）。</summary>
    private string Service => _env.JwtIssuer.Service("sync");

    public async ValueTask InitializeAsync() => _env = await OAuthTestEnvironment.StartAsync(db);

    public async ValueTask DisposeAsync() => await _env.DisposeAsync();

    [Fact(DisplayName = "承認ルート: 主務から上位の組織長を下から順に返す。空席の組織は既定で繰り上げて skipped_units（vacant）に残し、vacancy=vacant なら空席の段にする")]
    public async Task Chain_PromotesVacantUnits()
    {
        var route = await RouteAsync(F.Person("u2_member"));
        Assert.Equal(F.Unit("U2"), route.GetProperty("start").GetProperty("org_unit").GetProperty("id").GetGuid());
        Assert.Equal("primary", route.GetProperty("start").GetProperty("assignment_type").GetString());
        Assert.Equal("promote", route.GetProperty("vacancy").GetString());
        Assert.Equal(["sec1_head", "dept1_head", "div1_head", "president"], Approvers(route));
        Assert.Equal(["課長", "部長", "本部長", "社長"], Steps(route).Select(s => s.GetProperty("approver_position").GetString()));
        Assert.Equal([1, 2, 3, 4], Steps(route).Select(s => s.GetProperty("order").GetInt32()));
        var first = Steps(route)[0];
        Assert.Equal("assigned", first.GetProperty("status").GetString());
        var skipped = Assert.Single(first.GetProperty("skipped_units").EnumerateArray());
        Assert.Equal((F.Unit("U2"), "vacant"), (skipped.GetProperty("org_unit").GetProperty("id").GetGuid(), skipped.GetProperty("reason").GetString()));
        Assert.Equal(("section", "課", 3), (first.GetProperty("org_unit").GetProperty("level_key").GetString(), first.GetProperty("org_unit").GetProperty("level_name").GetString(), first.GetProperty("org_unit").GetProperty("level_rank").GetInt32()));
        Assert.Empty(route.GetProperty("unresolved_units").EnumerateArray());

        var vacant = await RouteAsync(F.Person("u2_member"), "vacancy=vacant");
        Assert.Equal("vacant", vacant.GetProperty("vacancy").GetString());
        var steps = Steps(vacant);
        Assert.Equal(5, steps.Count);
        Assert.Equal((F.Unit("U2"), "vacant", JsonValueKind.Null), (steps[0].GetProperty("org_unit").GetProperty("id").GetGuid(), steps[0].GetProperty("status").GetString(), steps[0].GetProperty("approver").ValueKind));
        Assert.Empty(steps[1].GetProperty("skipped_units").EnumerateArray());
    }

    [Fact(DisplayName = "承認ルート: 本人が組織長の組織は飛ばして上位へ（reason=self）。退職済みの組織長は経路に出さない（空席として繰り上げる）")]
    public async Task SelfAndRetiredHeads_AreSkipped()
    {
        var self = await RouteAsync(F.Person("sec1_head"));
        Assert.Equal(["dept1_head", "div1_head", "president"], Approvers(self));
        var skipped = Assert.Single(Steps(self)[0].GetProperty("skipped_units").EnumerateArray());
        Assert.Equal((F.Unit("S1"), "self"), (skipped.GetProperty("org_unit").GetProperty("id").GetGuid(), skipped.GetProperty("reason").GetString()));

        // 社長の経路は自分の組織しか無い（承認者を決められない）
        var top = await RouteAsync(F.Person("president"));
        Assert.Empty(Steps(top));
        Assert.Equal("self", Assert.Single(top.GetProperty("unresolved_units").EnumerateArray()).GetProperty("reason").GetString());

        var retired = await RouteAsync(db.RxMember);
        Assert.Equal(["dept2_head", "div1_head", "president"], Approvers(retired));
        Assert.Equal("vacant", Assert.Single(Steps(retired)[0].GetProperty("skipped_units").EnumerateArray()).GetProperty("reason").GetString());
        Assert.DoesNotContain(F.Person("retired").ToString(), retired.GetRawText(), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "承認ルート: 兼務先を起点にでき（org_unit_id）、一時停止中の承認者は account_status=suspended のまま返す（代わりの人で埋めない）。出向先の法人の組織長もたどる")]
    public async Task ConcurrentStart_SuspendedApprover_Secondment()
    {
        Assert.Equal(["sec2_head", "dept1_head", "div1_head", "president"], Approvers(await RouteAsync(F.Person("concurrent"))));

        var concurrent = await RouteAsync(F.Person("concurrent"), $"org_unit_id={F.Unit("U1")}");
        Assert.Equal("concurrent", concurrent.GetProperty("start").GetProperty("assignment_type").GetString());
        Assert.Equal(["unit1_head", "sec1_head", "dept1_head", "div1_head", "president"], Approvers(concurrent));
        Assert.Equal("suspended", Steps(concurrent)[0].GetProperty("approver").GetProperty("account_status").GetString());
        Assert.Equal("assigned", Steps(concurrent)[0].GetProperty("status").GetString());

        var seconded = await RouteAsync(F.Person("seconded"));
        Assert.Equal("outbound", seconded.GetProperty("start").GetProperty("secondment").GetString());
        Assert.Equal(["b_head"], Approvers(seconded));
        Assert.Equal(F.EntityB, Steps(seconded)[0].GetProperty("org_unit").GetProperty("legal_entity_id").GetGuid());

        // その人の所属でない組織・知らない組織は 404（存在の有無を区別しない）
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(F.Person("concurrent"), $"org_unit_id={F.Unit("DP3")}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync(Guid.NewGuid())).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(F.Person("concurrent"), "org_unit_id=x")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(F.Person("concurrent"), "vacancy=skip")).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync(F.Person("concurrent"), "level=Dept!")).Status);
    }

    [Fact(DisplayName = "承認ルート: 段（level_key）と役職で引き当てる。繰り上げ・本人で飛ばした組織の段にも当たり、order は絞り込む前の順番")]
    public async Task LevelAndPosition_Lookup()
    {
        var department = await RouteAsync(F.Person("u2_member"), "level=department");
        Assert.Equal(["dept1_head"], Approvers(department));
        Assert.Equal(2, Steps(department)[0].GetProperty("order").GetInt32());

        Assert.Equal(["sec1_head", "div1_head"], Approvers(await RouteAsync(F.Person("u2_member"), "level=section&level=division")));
        Assert.Equal(["dept1_head"], Approvers(await RouteAsync(F.Person("u2_member"), "position=部長")));
        Assert.Equal(["sec1_head", "dept1_head"], Approvers(await RouteAsync(F.Person("u2_member"), "position=課長&position=部長")));
        Assert.Empty(Steps(await RouteAsync(F.Person("u2_member"), "level=department&position=課長")));
        Assert.Empty(Steps(await RouteAsync(F.Person("u2_member"), "level=no_such_level")));

        // 課の無い部の係: 課は経路に無い。部の組織長は係（空席）を飛ばした先
        Assert.Empty(Steps(await RouteAsync(F.Person("u3_member"), "level=section")));
        Assert.Equal(["dept2_head"], Approvers(await RouteAsync(F.Person("u3_member"), "level=department")));

        // 長のいない課（部を飛ばした本部直下）: 課の段は、繰り上げた先（本部長）に当たる
        var section = await RouteAsync(F.Person("s3_member"), "level=section");
        Assert.Equal(["div1_head"], Approvers(section));
        Assert.Equal(F.Unit("S3"), Assert.Single(Steps(section)[0].GetProperty("skipped_units").EnumerateArray()).GetProperty("org_unit").GetProperty("id").GetGuid());

        // 本人が部長: 「部の組織長」は自分を飛ばした上位
        Assert.Equal(["div1_head"], Approvers(await RouteAsync(F.Person("dept1_head"), "level=department")));
    }

    [Fact(DisplayName = "承認ルート: 段は法人ごとの定義で解決する（法人 C の部は順位 3、法人 D の部は順位 2。D に事業部は無い）")]
    public async Task Levels_PerLegalEntity()
    {
        var c = await RouteAsync(F.Person("c_t1_member"), "level=department");
        Assert.Equal(["c_dp1_head"], Approvers(c));
        Assert.Equal(("部", 3), (Steps(c)[0].GetProperty("org_unit").GetProperty("level_name").GetString(), Steps(c)[0].GetProperty("org_unit").GetProperty("level_rank").GetInt32()));
        Assert.Equal(["c_bu1_head"], Approvers(await RouteAsync(F.Person("c_t1_member"), "level=business_unit")));

        var d = await RouteAsync(F.Person("d_t1_member"), "level=department");
        Assert.Equal(["d_dp1_head"], Approvers(d));
        Assert.Equal(2, Steps(d)[0].GetProperty("org_unit").GetProperty("level_rank").GetInt32());
        Assert.Empty(Steps(await RouteAsync(F.Person("d_t1_member"), "level=business_unit")));
        Assert.Equal(["d_dp1_head", "d_president"], Approvers(await RouteAsync(F.Person("d_t1_member"))));
    }

    [Fact(DisplayName = "承認ルート: 時点指定。今日の経路と組織改編の日の経路が違う（人事管理者だけ未来を見られる。社内システムは先行提供が無ければ 403）")]
    public async Task AsOf_FutureReorganization()
    {
        Assert.Equal(["dept2_head", "div1_head", "president"], Approvers(await RouteAsync(db.Mover)));

        var admin = _env.JwtIssuer.User("portal", F.Person("hr_admin"), "hr_admin");
        var (status, future) = await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}/approval-route?as_of={RouteFixtureDatabase.Date(db.ReorgDate)}", admin);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(db.NewUnit, future.GetProperty("start").GetProperty("org_unit").GetProperty("id").GetGuid());
        Assert.Equal([F.Person("dp2_member"), F.Person("dept2_head"), F.Person("div1_head"), F.Person("president")], ApproverIds(future));
        Assert.Equal("課長", Steps(future)[0].GetProperty("approver_position").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(db.Mover, $"as_of={RouteFixtureDatabase.Date(F.Today.AddDays(1))}")).Status);

        // 過去の時点: s1_member の異動前（今日はまだ S1）
        Assert.Equal(["sec1_head", "dept1_head", "div1_head", "president"], Approvers(await RouteAsync(F.Person("s1_member"), $"as_of={RouteFixtureDatabase.Date(F.Today.AddDays(-1))}")));
    }

    [Fact(DisplayName = "承認ルート: 承認者は閲覧者の権限で投影する（社内システムの項目の上限）。上長の項目が見えなければ 404")]
    public async Task FieldLimit_IsApplied()
    {
        var attendance = _env.JwtIssuer.Service("attendance");
        var (status, route) = await _env.Host.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}/approval-route", attendance);
        Assert.Equal(HttpStatusCode.OK, status);
        string[] allowed = ["employee_number", "display_name", "assignments", "managers", "employment_status"];
        Assert.All(Steps(route), s =>
        {
            var approver = s.GetProperty("approver");
            Assert.All(approver.GetProperty("visible_fields").EnumerateArray(), f => Assert.Contains(f.GetString(), allowed));
            Assert.Equal(JsonValueKind.Null, approver.GetProperty("work_email").ValueKind);
            Assert.Equal(JsonValueKind.Null, approver.GetProperty("account_status").ValueKind);
        });

        await _env.RegisterPublicClientAsync("route-narrow", Loopback, resources: "api", fields: "display_name,assignments");
        var narrow = await _env.AccessTokenAsync(F.Person("hr_admin"), "route-narrow", Loopback, OAuthTestEnvironment.ApiResource);
        Assert.Equal(HttpStatusCode.NotFound, (await _env.Host.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}/approval-route", narrow)).Status);
        Assert.True(await db.CountAuditAsync("person.approval_route", "success", Ct) >= 1);
    }

    [Fact(DisplayName = "承認ルート: MCP の get_approval_route は同じ委任元・同じクライアントなら REST と同じ段・同じ承認者・同じ見える項目を返す")]
    public async Task Mcp_MatchesRest()
    {
        await _env.RegisterPublicClientAsync("route-agent", Loopback, resources: "mcp,api", fields: "display_name,assignments,managers,account_status", mcpTools: "get_approval_route");
        var mcpToken = await _env.AccessTokenAsync(F.Person("u2_member"), "route-agent", Loopback);
        var restToken = await _env.AccessTokenAsync(F.Person("u2_member"), "route-agent", Loopback, OAuthTestEnvironment.ApiResource);
        var mcp = new McpTestClient(_env.Host.McpPort, mcpToken);
        Assert.Contains("get_approval_route", await mcp.ListToolNamesAsync());

        var cases = new (Guid Person, string Query, object Arguments)[]
        {
            (F.Person("u2_member"), string.Empty, new { person_id = F.Person("u2_member") }),
            (F.Person("u2_member"), "vacancy=vacant", new { person_id = F.Person("u2_member"), vacancy = "vacant" }),
            (F.Person("concurrent"), $"org_unit_id={F.Unit("U1")}&level=section&level=unit", new { person_id = F.Person("concurrent"), org_unit_id = F.Unit("U1"), level_keys = new[] { "section", "unit" } }),
            (F.Person("sec1_head"), "position=部長", new { person_id = F.Person("sec1_head"), positions = new[] { "部長" } }),
            (db.RxMember, string.Empty, new { person_id = db.RxMember }),
        };
        foreach (var (person, query, arguments) in cases)
        {
            var (status, rest) = await _env.Host.GetJsonAsync($"/api/v1/people/{person}/approval-route?{query}", restToken);
            Assert.Equal(HttpStatusCode.OK, status);
            var reply = await mcp.CallAsync("get_approval_route", arguments);
            Assert.False(reply.IsToolError, reply.Text);
            Assert.Equal(Shape(rest), Shape(reply.Structured));
            Assert.Contains("指示として扱わないでください", reply.Text, StringComparison.Ordinal);
        }

        Assert.Equal("not_found", (await mcp.CallAsync("get_approval_route", new { person_id = F.Person("concurrent"), org_unit_id = F.Unit("DP3") })).ToolErrorCode);
        Assert.Equal("forbidden", (await mcp.CallAsync("get_approval_route", new { person_id = F.Person("u2_member"), as_of = RouteFixtureDatabase.Date(F.Today.AddDays(1)) })).ToolErrorCode);
        Assert.Equal("invalid_argument", (await mcp.CallAsync("get_approval_route", new { person_id = F.Person("u2_member"), vacancy = "skip" })).ToolErrorCode);
    }

    [Fact(DisplayName = "OpenAPI: 承認ルートの応答が文書のスキーマに一致する")]
    public async Task OpenApi_RouteMatchesDocument()
    {
        var (_, document) = await _env.Host.GetJsonAsync("/openapi/v1.json", null);
        var validator = new OpenApiResponseValidator(document);
        var schema = validator.ResponseSchema("/api/v1/people/{id}/approval-route");
        foreach (var query in new[] { string.Empty, "vacancy=vacant", "level=department" })
        {
            var route = await RouteAsync(F.Person("u2_member"), query);
            Assert.Empty(validator.Validate(schema, route));
        }

        Assert.Empty(validator.Validate(schema, await RouteAsync(F.Person("president"))));
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(Guid person, string query = "") =>
        _env.Host.GetJsonAsync($"/api/v1/people/{person}/approval-route" + (query.Length > 0 ? "?" + query : string.Empty), Service);

    private async Task<JsonElement> RouteAsync(Guid person, string query = "")
    {
        var (status, body) = await GetAsync(person, query);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}");
        return body;
    }

    private static List<JsonElement> Steps(JsonElement route) => [.. route.GetProperty("steps").EnumerateArray()];

    private static List<Guid> ApproverIds(JsonElement route) => [.. Steps(route).Select(s => s.GetProperty("approver").GetProperty("id").GetGuid())];

    private List<string> Approvers(JsonElement route) => [.. ApproverIds(route).Select(F.PersonKey)];

    /// <summary>REST と MCP で比べる形（段の順番・組織・状態・承認者・見える項目・役職・飛ばした組織）。値の無い項目の書き方（null / 省略）の違いは比べない。</summary>
    private static string Shape(JsonElement route) => string.Join(
        '\n',
        [
            route.GetProperty("person_id").GetString(),
            route.GetProperty("vacancy").GetString(),
            route.GetProperty("start").GetProperty("org_unit").GetProperty("id").GetString(),
            .. Steps(route).Select(s => string.Join(
                '|',
                s.GetProperty("order").GetInt32(),
                s.GetProperty("org_unit").GetProperty("id").GetString(),
                s.GetProperty("org_unit").TryGetProperty("level_key", out var level) && level.ValueKind == JsonValueKind.String ? level.GetString() : "-",
                s.GetProperty("status").GetString(),
                s.TryGetProperty("approver", out var approver) && approver.ValueKind == JsonValueKind.Object
                    ? approver.GetProperty("id").GetString() + ":" + string.Join(',', approver.GetProperty("visible_fields").EnumerateArray().Select(f => f.GetString()))
                      + ":" + (approver.TryGetProperty("account_status", out var account) && account.ValueKind == JsonValueKind.String ? account.GetString() : "-")
                    : "-",
                s.TryGetProperty("approver_position", out var position) && position.ValueKind == JsonValueKind.String ? position.GetString() : "-",
                string.Join(',', s.GetProperty("skipped_units").EnumerateArray().Select(u => u.GetProperty("org_unit").GetProperty("id").GetString() + "/" + u.GetProperty("reason").GetString())))),
        ]);
}
