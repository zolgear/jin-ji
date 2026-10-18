using System.Net;
using System.Text;
using JinJi.Application.Auth;
using JinJi.Host.Auth;
using JinJi.IntegrationTests.Support;
using Microsoft.IdentityModel.Tokens;

namespace JinJi.IntegrationTests.Auth;

/// <summary>
/// クライアントの認証（private_key_jwt・移行用の client_secret・公開クライアント）、client credentials、CIMD（許可ドメイン・承認待ち・文書の検査・キャッシュ）。
/// </summary>
public sealed class OAuthClientTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>, IAsyncLifetime
{
    private const string Loopback = "http://127.0.0.1:43123/callback";
    private OAuthTestEnvironment _env = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _env = await OAuthTestEnvironment.StartAsync(db);

    public async ValueTask DisposeAsync() => await _env.DisposeAsync();

    // ---- private_key_jwt・client credentials ---------------------------------------------------------------------

    [Fact(DisplayName = "private_key_jwt: aud は発行者だけ、exp は 5 分以内、jti の再利用は拒否、共有鍵（HS256）・別の鍵・sub の不一致も拒否")]
    public async Task PrivateKeyJwt_IsValidatedStrictly()
    {
        using var key = await RegisterServiceAsync("svc-jwt");
        Task<TokenResponse> TokenAsync(string assertion) => _env.TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = AuthorizationServer.JwtBearerAssertionType,
            ["client_assertion"] = assertion,
        });

        var ok = await TokenAsync(key.Assertion("svc-jwt", jti: "jti-1"));
        Assert.Equal(HttpStatusCode.OK, ok.Status);
        Assert.Null(ok.RefreshToken);

        Assert.Equal("invalid_client", (await TokenAsync(key.Assertion("svc-jwt", jti: "jti-1"))).Error);
        Assert.Equal("invalid_client", (await TokenAsync(key.Assertion("svc-jwt", audience: OAuthTestEnvironment.IssuerUrl + "/oauth/token"))).Error);
        Assert.Equal("invalid_client", (await TokenAsync(key.Assertion("svc-jwt", audience: "https://other.example"))).Error);
        Assert.Equal("invalid_client", (await TokenAsync(key.Assertion("svc-jwt", expires: DateTime.UtcNow.AddMinutes(10)))).Error);
        Assert.Equal("invalid_client", (await TokenAsync(key.Assertion("svc-jwt", expires: DateTime.UtcNow.AddMinutes(-2)))).Error);
        Assert.Equal("invalid_client", (await TokenAsync(key.Assertion("svc-jwt", subject: "someone-else"))).Error);
        Assert.Equal("invalid_client", (await TokenAsync(key.Assertion("svc-jwt", algorithm: SecurityAlgorithms.HmacSha256))).Error);
        using (var other = new ClientKey())
        {
            Assert.Equal("invalid_client", (await TokenAsync(other.Assertion("svc-jwt"))).Error);
        }

        // client_id を名乗るだけ（公開クライアントの方式）では機密クライアントとして通さない
        Assert.Equal("invalid_client", (await _env.TokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = "svc-jwt" })).Error);

        // 社内システムのトークンは REST API だけ（社内システムの閲覧者。MCP の resource は拒否）
        var (status, _) = await _env.Host.GetJsonAsync("/api/v1/meta", ok.AccessToken);
        Assert.Equal(HttpStatusCode.OK, status);
        var mcp = await _env.TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = AuthorizationServer.JwtBearerAssertionType,
            ["client_assertion"] = key.Assertion("svc-jwt"),
            ["resource"] = OAuthTestEnvironment.McpResource,
        });
        Assert.Equal("invalid_target", mcp.Error);
        using (var ping = await _env.McpPingAsync(ok.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ping.StatusCode);
        }

        Assert.True(await db.CountAuditAsync("auth.token.issue", "success", Ct, "service") >= 1);
        Assert.True(await db.CountAuditAsync("auth.token.issue", "denied", Ct) >= 1);
    }

    [Fact(DisplayName = "client_secret（移行用）: 期限付きで発行し、Basic・本文のどちらでも使える。違う値は 401 と WWW-Authenticate: Basic。取り消せば使えない")]
    public async Task ClientSecret_ForMigration()
    {
        using var key = await RegisterServiceAsync("svc-secret");
        var (exit, output) = await _env.CliAsync("issue-client-secret", "--client-id", "svc-secret", "--lifetime", "30.00:00:00");
        Assert.True(exit == 0, output);
        var secret = AuthFixtureDatabase.Field(output, "client_secret");
        Assert.StartsWith("jjcs_", secret, StringComparison.Ordinal);
        Assert.Empty(await db.QueryAsSystemAsync<string>("SELECT secret_sha256 FROM api_client_secret WHERE secret_sha256 = $1", Ct, secret));

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"svc-secret:{secret}"));
        Assert.Equal(HttpStatusCode.OK, (await _env.TokenAsync(new() { ["grant_type"] = "client_credentials" }, basic)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _env.TokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = "svc-secret", ["client_secret"] = secret })).Status);

        var wrong = await _env.TokenAsync(new() { ["grant_type"] = "client_credentials" }, Convert.ToBase64String(Encoding.UTF8.GetBytes("svc-secret:jjcs_wrong")));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal("invalid_client", wrong.Error);
        Assert.StartsWith("Basic", Assert.Single(wrong.Raw.Headers.WwwAuthenticate).ToString(), StringComparison.Ordinal);

        // 2 つの方式を同時に使うのは拒否
        Assert.Equal("invalid_client", (await _env.TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = AuthorizationServer.JwtBearerAssertionType,
            ["client_assertion"] = key.Assertion("svc-secret"),
        }, basic)).Error);

        Assert.Equal(0, (await _env.CliAsync("revoke-client-secrets", "--client-id", "svc-secret")).Exit);
        Assert.Equal("invalid_client", (await _env.TokenAsync(new() { ["grant_type"] = "client_credentials" }, basic)).Error);

        // 公開クライアントには発行できない
        await _env.RegisterPublicClientAsync("public-nosecret", Loopback);
        Assert.Equal(1, (await _env.CliAsync("issue-client-secret", "--client-id", "public-nosecret")).Exit);
    }

    [Fact(DisplayName = "付与の種類: 公開クライアントは client credentials を使えず、client credentials だけのクライアントは認可コードを使えない。知らない grant_type は拒否")]
    public async Task GrantTypes_AreEnforced()
    {
        await _env.RegisterPublicClientAsync("public-only", Loopback);
        using var key = await RegisterServiceAsync("svc-only");

        Assert.Equal("unauthorized_client", (await _env.TokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = "public-only" })).Error);
        Assert.Equal("unsupported_grant_type", (await _env.TokenAsync(new() { ["grant_type"] = "password", ["client_id"] = "public-only" })).Error);
        Assert.Equal("unauthorized_client", (await _env.TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["client_assertion_type"] = AuthorizationServer.JwtBearerAssertionType,
            ["client_assertion"] = key.Assertion("svc-only"),
            ["code"] = "jjac_x",
        })).Error);

        var browser = await _env.LoginAsync(F.Person("u2_member"));
        using var authorize = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery("svc-only", Loopback, Pkce.New(), OAuthTestEnvironment.ApiResource));
        Assert.Equal(HttpStatusCode.BadRequest, authorize.StatusCode);

        // 登録の検査: 公開クライアントの client credentials・redirect_uri の無い認可コード・http の redirect_uri・知らない項目は登録できない
        Assert.Equal(1, (await _env.CliAsync("register-client", "--client-id", "bad-1", "--auth", "none", "--grant-types", "client_credentials", "--resources", "api", "--fields", "*")).Exit);
        Assert.Equal(1, (await _env.CliAsync("register-client", "--client-id", "bad-2", "--auth", "none", "--grant-types", "authorization_code", "--resources", "mcp", "--fields", "*")).Exit);
        Assert.Equal(1, (await _env.CliAsync("register-client", "--client-id", "bad-3", "--auth", "none", "--grant-types", "authorization_code", "--redirect-uris", "http://app.example/cb", "--resources", "mcp", "--fields", "*")).Exit);
        Assert.Equal(1, (await _env.CliAsync("register-client", "--client-id", "bad-4", "--auth", "none", "--grant-types", "authorization_code", "--redirect-uris", Loopback, "--resources", "mcp", "--fields", "salary")).Exit);
        Assert.Equal(1, (await _env.CliAsync("register-client", "--client-id", "jinji-ui", "--auth", "none", "--grant-types", "authorization_code", "--redirect-uris", Loopback, "--resources", "mcp", "--fields", "*")).Exit);
    }

    // ---- CIMD ----------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "CIMD: 許可ドメインの文書を取得してクライアントにし、HTTP キャッシュの間は取り直さない。文書の redirect_uris だけを受け付ける")]
    public async Task Cimd_AllowedDomain()
    {
        const string clientId = "https://cimd.example/clients/app.json";
        _env.Fetcher.Put(clientId, ["http://127.0.0.1/callback", "https://cimd.example/cb"], "CIMD のアプリ", TimeSpan.FromHours(1));
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();

        using (var authorize = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery(clientId, "http://127.0.0.1:61000/callback", pkce)))
        {
            var handle = OAuthTestEnvironment.RequestHandle(authorize);
            var view = await TestBrowser.JsonAsync(await browser.GetAsync($"/oauth/requests/{handle}"));
            Assert.Equal("CIMD のアプリ", view.GetProperty("client").GetProperty("name").GetString());
            Assert.Equal("cimd", view.GetProperty("client").GetProperty("registration").GetString());
            var code = OAuthTestEnvironment.Query(await OAuthTestEnvironment.DecideAsync(browser, handle, true))["code"];
            var tokens = await _env.ExchangeAsync(clientId, code, "http://127.0.0.1:61000/callback", pkce);
            Assert.Equal(HttpStatusCode.OK, tokens.Status);
            Assert.NotNull(tokens.RefreshToken);
        }

        using (var other = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery(clientId, "https://evil.example/cb", pkce)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        }

        Assert.Equal(1, _env.Fetcher.Count(clientId));
        Assert.True(await db.CountAuditAsync("auth.client.register", "success", Ct, "system") >= 1);
    }

    [Fact(DisplayName = "CIMD: 許可外のドメインは文書を取得せず「管理者の承認待ち」として記録し、認可を進めない。承認すれば取得して進む。却下は拒否のまま")]
    public async Task Cimd_PendingApproval()
    {
        const string clientId = "https://unlisted.example/oauth/client.json";
        const string rejectedId = "https://unlisted.example/oauth/rejected.json";
        _env.Fetcher.Put(clientId, ["http://localhost/cb"]);
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var pending = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery(clientId, "http://localhost:4000/cb", pkce));
            Assert.Equal(HttpStatusCode.BadRequest, pending.StatusCode);
            Assert.Equal("unauthorized_client", (await TestBrowser.JsonAsync(pending)).GetProperty("error").GetString());
        }

        Assert.Equal(0, _env.Fetcher.Count(clientId));
        var (_, list) = await _env.CliAsync("list-clients", "--status", "pending_approval");
        Assert.Contains(clientId, list, StringComparison.Ordinal);
        Assert.True(await db.CountAuditAsync("auth.cimd.pending", "denied", Ct) >= 1);

        Assert.Equal(0, (await _env.CliAsync("approve-client", "--client-id", clientId)).Exit);
        using (var approved = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery(clientId, "http://localhost:4000/cb", pkce)))
        {
            _ = OAuthTestEnvironment.RequestHandle(approved);
        }

        Assert.Equal(1, _env.Fetcher.Count(clientId));

        using (var first = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery(rejectedId, "http://localhost:4000/cb", pkce)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        }

        Assert.Equal(0, (await _env.CliAsync("reject-client", "--client-id", rejectedId)).Exit);
        using (var rejected = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery(rejectedId, "http://localhost:4000/cb", pkce)))
        {
            Assert.Equal("invalid_client", (await TestBrowser.JsonAsync(rejected)).GetProperty("error").GetString());
        }

        Assert.Equal(0, _env.Fetcher.Count(rejectedId));
        Assert.True(await db.CountAuditAsync("auth.client.approve", "success", Ct) >= 1);
    }

    [Fact(DisplayName = "CIMD: 文書の client_id の不一致・https でないリダイレクト・取得の失敗は拒否し、監査に残す。許可ドメインを足せば取得する。no-store は毎回取り直す")]
    public async Task Cimd_DocumentIsValidated()
    {
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        const string mismatch = "https://cimd.example/mismatch.json";
        const string httpRedirect = "https://cimd.example/http.json";
        const string missing = "https://cimd.example/missing.json";
        _env.Fetcher.Put(mismatch, ["http://localhost/cb"], documentClientId: "https://cimd.example/other.json");
        _env.Fetcher.Put(httpRedirect, ["http://app.example/cb"]);

        foreach (var clientId in new[] { mismatch, httpRedirect, missing, "https://cimd.example/", "https://cimd.example/a.json#x", "https://cimd.example/a.json?x=1", "https://cimd.example/./a.json" })
        {
            using var response = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery(clientId, "http://localhost:4000/cb", pkce));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.True(await db.CountAuditAsync("auth.cimd.fetch_failed", "failed", Ct) >= 3);
        Assert.Equal(0, _env.Fetcher.Count("https://cimd.example/"));

        // 許可ドメインを足す（DB）。no-store の文書は毎回取り直す
        const string added = "https://tools.example/client.json";
        _env.Fetcher.Put(added, ["http://localhost/cb"], cacheFor: TimeSpan.Zero);
        Assert.Equal(0, (await _env.CliAsync("allow-cimd-domain", "--domain", "tools.example")).Exit);
        Assert.Equal(1, (await _env.CliAsync("allow-cimd-domain", "--domain", "tools.example")).Exit);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery(added, "http://localhost:4000/cb", pkce));
            _ = OAuthTestEnvironment.RequestHandle(response);
        }

        Assert.Equal(2, _env.Fetcher.Count(added));
        var (_, domains) = await _env.CliAsync("list-cimd-domains");
        Assert.Contains("cimd.example\tconfigured", domains, StringComparison.Ordinal);
        Assert.Contains("tools.example\tadded", domains, StringComparison.Ordinal);
    }

    // ---- CIMD の取得（SSRF の対策） ------------------------------------------------------------------------------

    [Theory(DisplayName = "CIMD の取得: 公開でないアドレス（私設・ループバック・リンクローカル・CGNAT・文書用・ULA など）は宛先にしない")]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.0.2.10", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("2002:0a00:0001::1", false)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("172.32.0.1", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("::ffff:8.8.8.8", true)]
    public void Cimd_RejectsNonPublicAddresses(string address, bool isPublic)
    {
        Assert.Equal(isPublic, CimdFetcher.IsPublic(IPAddress.Parse(address)));
    }

    [Fact(DisplayName = "CIMD の取得: IP アドレス・DNS で解決したループバック宛ては接続せず、https 以外は取得しない")]
    public async Task CimdFetcher_RefusesUnsafeTargets()
    {
        var options = new AuthorizationServerOptions { Issuer = OAuthTestEnvironment.IssuerUrl };
        using var fetcher = new CimdFetcher(options, TimeProvider.System);

        foreach (var url in new[] { "https://127.0.0.1/client.json", "https://[::1]/client.json", "https://10.0.0.5/client.json", "https://localhost:1/client.json" })
        {
            var result = await fetcher.FetchAsync(new Uri(url), Ct);
            Assert.Null(result.Json);
            Assert.Equal("公開でないアドレスです", result.Failure);
        }

        Assert.NotNull((await fetcher.FetchAsync(new Uri("http://cimd.example/client.json"), Ct)).Failure);
        Assert.False(CimdFetcher.CreateHandler(options.Cimd).AllowAutoRedirect);
    }

    [Fact(DisplayName = "CIMD の取得: リダイレクトに従わず、JSON 以外・大きすぎる文書は拒否し、Cache-Control に従う")]
    public async Task CimdFetcher_RespectsResponseRules()
    {
        var options = new AuthorizationServerOptions { Issuer = OAuthTestEnvironment.IssuerUrl };
        var url = new Uri("https://cimd.example/client.json");

        async Task<ClientMetadataFetch> FetchAsync(Func<HttpResponseMessage> respond)
        {
            using var fetcher = new CimdFetcher(options, TimeProvider.System, new StubHandler(respond));
            return await fetcher.FetchAsync(url, Ct);
        }

        var redirect = await FetchAsync(() => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://169.254.169.254/") } });
        Assert.Equal("リダイレクトには従いません", redirect.Failure);

        var html = await FetchAsync(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html/>", Encoding.UTF8, "text/html") });
        Assert.Equal("application/json ではありません", html.Failure);

        var large = await FetchAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"x\":\"" + new string('a', 6000) + "\"}", Encoding.UTF8, "application/json"),
        });
        Assert.Equal("文書が大きすぎます", large.Failure);

        var cached = await FetchAsync(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(7) };
            return response;
        });
        Assert.Null(cached.Failure);
        Assert.Equal(TimeSpan.FromMinutes(7), cached.CacheFor);

        var noStore = await FetchAsync(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            return response;
        });
        Assert.Equal(TimeSpan.Zero, noStore.CacheFor);
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    /// <summary>社内システム（client credentials、private_key_jwt）を登録し、鍵を返す。</summary>
    private async Task<ClientKey> RegisterServiceAsync(string clientId)
    {
        var (exit, output) = await _env.CliAsync(
            "register-client", "--client-id", clientId, "--name", "社内システム", "--auth", "private_key_jwt", "--grant-types", "client_credentials",
            "--resources", "api", "--scopes", "jinji.read", "--fields", "employee_number,display_name");
        Assert.True(exit == 0, output);
        var key = new ClientKey();
        var file = Path.Combine(Path.GetTempPath(), $"jinji-test-{Guid.NewGuid():N}.pem");
        try
        {
            await File.WriteAllTextAsync(file, key.PublicKeyPem, Ct);
            (exit, output) = await _env.CliAsync("add-client-key", "--client-id", clientId, "--kid", key.KeyId, "--key-file", file);
            Assert.True(exit == 0, output);
        }
        finally
        {
            File.Delete(file);
        }

        return key;
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond());
    }
}
