using System.Net;
using JinJi.Application.Access;
using JinJi.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.IntegrationTests.Mcp;

/// <summary>
/// MCP の横断の規則: 返却人数の上限・ツールの種別ごとのレート制限（REST と同じ仕組み）・不正検知・本文の上限・停止スイッチ。
/// </summary>
public sealed class McpLimitTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>
{
    private const string Loopback = "http://127.0.0.1:43125/callback";

    private OrgFixture F => db.Fixture;

    private async Task<McpTestClient> ClientAsync(OAuthTestEnvironment env, string person, string clientId)
    {
        await env.RegisterPublicClientAsync(clientId, Loopback, mcpTools: "*");
        return new McpTestClient(env.Host.McpPort, await env.AccessTokenAsync(F.Person(person), clientId, Loopback));
    }

    [Fact(DisplayName = "返却人数: 上限に達したら人を返すツールは 429 と Retry-After。人を返さないツールは使える")]
    public async Task ReturnedPeople_Limited()
    {
        await using var env = await OAuthTestEnvironment.StartAsync(db, ["--JinJi:RateLimit:Mcp:ReturnedPeoplePerUser=3"]);
        var mcp = await ClientAsync(env, "u1_member", "returned-agent");

        var first = await mcp.CallAsync("search_people", new { org_unit_id = F.Unit("DP1"), include_descendants = true, limit = 3 });
        Assert.Equal(3, first.Structured.GetProperty("items").GetArrayLength());

        var limited = await mcp.CallAsync("get_person", new { person_id = F.Person("u1_member") });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.NotNull(limited.Raw.Headers.RetryAfter);

        var orgs = await mcp.CallAsync("search_orgs", new { query = "営業" });
        Assert.Equal(HttpStatusCode.OK, orgs.Status);
        Assert.False(orgs.IsToolError);
    }

    [Fact(DisplayName = "レート制限: ツールの種別（person_read など）ごとに REST と同じ段で数える。tools/list は種別なし")]
    public async Task RateLimit_PerToolKind()
    {
        await using var env = await OAuthTestEnvironment.StartAsync(db, ["--JinJi:RateLimit:Mcp:PerEndpoint:person_read=2"]);
        var mcp = await ClientAsync(env, "u1_member", "kind-agent");

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await mcp.CallAsync("get_person", new { person_id = F.Person("u1_member") })).Status);
        }

        var limited = await mcp.CallAsync("get_person", new { person_id = F.Person("u1_member") });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal(HttpStatusCode.OK, (await mcp.CallAsync("search_orgs", new { query = "営業" })).Status);
        Assert.NotEmpty(await mcp.ListToolNamesAsync());
    }

    [Fact(DisplayName = "不正検知: ツールの not_found（HTTP は 200）も REST の 404 と同じく数え、しきい値で自動スロットルにする")]
    public async Task Abuse_CountsToolNotFound()
    {
        await using var env = await OAuthTestEnvironment.StartAsync(db, ["--JinJi:Abuse:DeniedThreshold=3", "--JinJi:RateLimit:Mcp:ThrottledPerClient=1000"]);
        var mcp = await ClientAsync(env, "u1_member", "enum-agent");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("not_found", (await mcp.CallAsync("get_person", new { person_id = Guid.NewGuid() })).ToolErrorCode);
        }

        var detector = env.Host.App.Services.GetRequiredService<AbuseDetector>();
        Assert.Contains(detector.List(), s => s.ClientId.StartsWith("oauth:", StringComparison.Ordinal) && s.State == AbuseState.Throttled);
    }

    [Fact(DisplayName = "本文の上限: MCP の要求の本文が上限を超えたら 413（ツールは動かない）")]
    public async Task Body_TooLarge()
    {
        await using var env = await OAuthTestEnvironment.StartAsync(db, ["--JinJi:Mcp:MaxRequestBodyBytes=1024"]);
        var mcp = await ClientAsync(env, "u1_member", "large-agent");
        var reply = await mcp.CallAsync("search_people", new { query = new string('あ', 2000) });
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reply.Status);
    }

    [Fact(DisplayName = "停止スイッチ: JinJi:Mcp:Enabled=false なら MCP のポートは 503（ヘルスチェックを除く）、REST API は動く")]
    public async Task StopSwitch()
    {
        await using var env = await OAuthTestEnvironment.StartAsync(db, ["--JinJi:Mcp:Enabled=false"]);
        await env.RegisterPublicClientAsync("stopped-agent", Loopback, resources: "api,mcp", mcpTools: "*");
        var token = await env.AccessTokenAsync(F.Person("u1_member"), "stopped-agent", Loopback);

        var reply = await new McpTestClient(env.Host.McpPort, token).CallAsync("get_directory_info");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        var (metadata, _) = await env.McpGetJsonAsync("/.well-known/oauth-protected-resource/mcp");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, metadata);
        var (health, _) = await env.McpGetJsonAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health);

        var apiToken = await env.AccessTokenAsync(F.Person("u1_member"), "stopped-agent", Loopback, resource: OAuthTestEnvironment.ApiResource);
        Assert.Equal(HttpStatusCode.OK, (await env.Host.GetJsonAsync("/api/v1/meta", apiToken)).Status);
    }
}
