using System.Net;
using JinJi.Application.Auth;
using JinJi.Host;
using JinJi.Host.Auth;
using JinJi.IntegrationTests.Support;
using Microsoft.Extensions.Configuration;

namespace JinJi.IntegrationTests.Auth;

/// <summary>トークン発行の設定の検査と、無効にしたときの振る舞い。</summary>
public sealed class OAuthHostTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory(DisplayName = "設定: トークン発行の不正な設定では起動しない")]
    [InlineData("Issuer=https://jinji.example/app")]
    [InlineData("Issuer=https://jinji.example/")]
    [InlineData("Issuer=http://jinji.example")]
    [InlineData("McpResource=https://jinji.example/api/v1")]
    [InlineData("McpResource=https://jinji.example/mcp?x=1")]
    [InlineData("AuthorizationCodeLifetime=00:11:00")]
    [InlineData("AccessTokenLifetime=02:00:00")]
    [InlineData("RefreshTokenIdleLifetime=40.00:00:00")]
    [InlineData("TokenCacheDuration=00:02:00")]
    [InlineData("Cimd:AllowedDomains:0=Claude.AI")]
    [InlineData("Cimd:DefaultScopes:0=jinji.admin")]
    [InlineData("Cimd:DefaultFields:0=salary")]
    [InlineData("RateLimit:PerAddressAndClient=1000000")]
    public void ServerOptions_RejectInvalidValues(string setting)
    {
        var values = new Dictionary<string, string?> { ["JinJi:Auth:Server:Issuer"] = "https://jinji.example" };
        var (key, value) = (setting.Split('=', 2)[0], setting.Split('=', 2)[1]);
        values["JinJi:Auth:Server:" + key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.ThrowsAny<ArgumentException>(() => { _ = OAuthRegistration.Server(configuration); });
    }

    [Fact(DisplayName = "設定: 既定値（アクセストークン 10 分、リフレッシュ 無操作 7 日・最長 30 日、認可コード 60 秒、キャッシュ 5 秒、CIMD 5 KiB・5 秒）")]
    public void ServerOptions_Defaults()
    {
        var options = new AuthorizationServerOptions { Issuer = "https://jinji.example" };
        options.Validate();

        Assert.Equal(TimeSpan.FromMinutes(10), options.AccessTokenLifetime);
        Assert.Equal(TimeSpan.FromDays(7), options.RefreshTokenIdleLifetime);
        Assert.Equal(TimeSpan.FromDays(30), options.RefreshTokenMaxLifetime);
        Assert.Equal(TimeSpan.FromSeconds(60), options.AuthorizationCodeLifetime);
        Assert.Equal(TimeSpan.FromSeconds(5), options.TokenCacheDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ClientAssertionMaxLifetime);
        Assert.Equal(5 * 1024, options.Cimd.MaxDocumentBytes);
        Assert.Equal(TimeSpan.FromSeconds(5), options.Cimd.FetchTimeout);
        Assert.Equal("https://jinji.example/api/v1", options.ApiResourceValue);
        Assert.Null(options.ResourceValue(OAuthResource.Mcp));
        Assert.Equal(OAuthResource.Api, options.ParseResourceUri("https://jinji.example/api/v1/"));
        Assert.Null(options.ParseResourceUri("https://jinji.example/api"));
    }

    [Fact(DisplayName = "設定: 発行者は人のログインの公開 URL と同じオリジンにする（同意画面とログインのため）")]
    public void Issuer_MustMatchPublicOrigin()
    {
        string[] args =
        [
            $"--ConnectionStrings:App={db.Database.AppConnectionString}",
            "--JinJi:Listen:ApiPort=0",
            "--JinJi:Listen:MetricsPort=0",
            "--JinJi:DataProtection:AllowUnprotectedKeys=true",
            $"--JinJi:Auth:Login:PublicOrigin={AuthTestHost.Origin}",
            "--JinJi:Auth:Server:Issuer=https://other.jinji.test",
        ];

        var ex = Assert.Throws<ArgumentException>(() => ServerHost.BuildWeb(HostRole.Api, args));
        Assert.Contains("PublicOrigin", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "無効（Issuer なし）: /oauth と AS メタデータは 404、MCP はどのトークンも受け付けず resource_metadata を示さない。外部の JWT の REST API は従来どおり")]
    public async Task Disabled_ByDefault()
    {
        using var issuer = new TestIssuer();
        await using var host = await AuthTestHost.StartAsync(db, issuer);

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetJsonAsync("/.well-known/oauth-authorization-server", null)).Status);
        using (var token = await host.PostAsync("/oauth/token", null))
        {
            Assert.Equal(HttpStatusCode.NotFound, token.StatusCode);
        }

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{host.McpPort}/mcp"))
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", issuer.Agent("agent-x", db.Fixture.Person("u1_member")));
        using var mcp = await http.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, mcp.StatusCode);
        Assert.DoesNotContain("resource_metadata", Assert.Single(mcp.Headers.WwwAuthenticate).ToString(), StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await host.GetJsonAsync("/api/v1/meta", issuer.User("portal", db.Fixture.Person("u1_member")))).Status);
    }
}
