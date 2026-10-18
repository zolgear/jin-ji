using System.Net;
using System.Text.Json;
using JinJi.Application.Access;
using JinJi.Application.Auth;
using JinJi.Host.Auth;
using JinJi.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.IntegrationTests.Auth;

/// <summary>
/// 認可サーバー（ADR-0004 の段階 B）の認可コード + PKCE・リフレッシュ・resource の束縛・同意・失効・スコープと項目の上限。
/// RFC 9700（OAuth 2.0 Security BCP）と MCP 仕様（2026-07-28 版）の Security Considerations の項目ごとに確かめる。
/// </summary>
public sealed class OAuthFlowTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>, IAsyncLifetime
{
    private const string Loopback = "http://127.0.0.1:43123/callback";
    private OAuthTestEnvironment _env = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _env = await OAuthTestEnvironment.StartAsync(db);

    public async ValueTask DisposeAsync() => await _env.DisposeAsync();

    // ---- メタデータ ----------------------------------------------------------------------------------------------

    [Fact(DisplayName = "メタデータ: 認可サーバー（RFC 8414）は S256 だけ・CIMD 対応・none と private_key_jwt・iss の付与を示す")]
    public async Task Metadata_AuthorizationServer()
    {
        var (status, body) = await _env.Host.GetJsonAsync("/.well-known/oauth-authorization-server", null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(OAuthTestEnvironment.IssuerUrl, body.GetProperty("issuer").GetString());
        Assert.Equal(OAuthTestEnvironment.IssuerUrl + "/oauth/authorize", body.GetProperty("authorization_endpoint").GetString());
        Assert.Equal(OAuthTestEnvironment.IssuerUrl + "/oauth/token", body.GetProperty("token_endpoint").GetString());
        Assert.Equal(["S256"], Strings(body, "code_challenge_methods_supported"));
        Assert.Equal(["code"], Strings(body, "response_types_supported"));
        Assert.Equal(["none", "private_key_jwt"], Strings(body, "token_endpoint_auth_methods_supported"));
        Assert.True(body.GetProperty("client_id_metadata_document_supported").GetBoolean());
        Assert.True(body.GetProperty("authorization_response_iss_parameter_supported").GetBoolean());
        Assert.Equal(["jinji.read", "jinji.propose"], Strings(body, "scopes_supported"));
        Assert.DoesNotContain("registration_endpoint", body.EnumerateObject().Select(p => p.Name));
        Assert.DoesNotContain("HS256", Strings(body, "token_endpoint_auth_signing_alg_values_supported"));
    }

    [Fact(DisplayName = "メタデータ: MCP の保護リソースメタデータ（RFC 9728）は jin-ji の issuer 1 件を示し、401 の WWW-Authenticate に resource_metadata と scope を載せる")]
    public async Task Metadata_ProtectedResourceAndChallenge()
    {
        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-protected-resource/mcp" })
        {
            var (status, body) = await _env.McpGetJsonAsync(path);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(OAuthTestEnvironment.McpResource, body.GetProperty("resource").GetString());
            Assert.Equal([OAuthTestEnvironment.IssuerUrl], Strings(body, "authorization_servers"));
        }

        // REST API の保護リソースメタデータは API のポート
        var (apiStatus, api) = await _env.Host.GetJsonAsync("/.well-known/oauth-protected-resource/api/v1", null);
        Assert.Equal(HttpStatusCode.OK, apiStatus);
        Assert.Equal(OAuthTestEnvironment.ApiResource, api.GetProperty("resource").GetString());

        using var unauthenticated = await _env.McpPingAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        var challenge = Assert.Single(unauthenticated.Headers.WwwAuthenticate).ToString();
        Assert.StartsWith("Bearer ", challenge, StringComparison.Ordinal);
        Assert.Contains("resource_metadata=\"https://jinji.test/.well-known/oauth-protected-resource/mcp\"", challenge, StringComparison.Ordinal);
        Assert.Contains("scope=\"jinji.read\"", challenge, StringComparison.Ordinal);
        Assert.DoesNotContain("error=", challenge, StringComparison.Ordinal);

        using var invalid = await _env.McpPingAsync("jjat_" + new string('A', 43));
        Assert.Equal(HttpStatusCode.Unauthorized, invalid.StatusCode);
        Assert.Contains("error=\"invalid_token\"", Assert.Single(invalid.Headers.WwwAuthenticate).ToString(), StringComparison.Ordinal);
    }

    // ---- MCP クライアントの流れ ----------------------------------------------------------------------------------

    [Fact(DisplayName = "MCP クライアントの流れ: 保護リソースメタデータ → AS メタデータ → authorize（未ログインならログインへ）→ 同意 → token → MCP の疎通 → リフレッシュ")]
    public async Task McpClientFlow_EndToEnd()
    {
        await _env.RegisterPublicClientAsync("flow-app", Loopback);

        // 1. 401 から保護リソースメタデータ、そこから認可サーバーのメタデータへ
        using var first = await _env.McpPingAsync(null);
        var metadataUrl = new Uri(Assert.Single(first.Headers.WwwAuthenticate).Parameter!.Split("resource_metadata=\"")[1].Split('"')[0]);
        var (_, resource) = await _env.McpGetJsonAsync(metadataUrl.AbsolutePath);
        var issuer = Strings(resource, "authorization_servers").Single();
        var (_, server) = await _env.Host.GetJsonAsync(new Uri(issuer + "/.well-known/oauth-authorization-server").AbsolutePath, null);
        Assert.Equal(issuer, server.GetProperty("issuer").GetString());

        // 2. 未ログインの authorize はログイン画面へ（戻り先は短い要求 ID の同意画面）
        var pkce = Pkce.New();
        using var anonymous = new TestBrowser(_env.Host.ApiPort);
        using (var toLogin = await _env.AuthorizeAsync(anonymous, OAuthTestEnvironment.AuthorizeQuery("flow-app", "http://127.0.0.1:50000/callback", pkce)))
        {
            Assert.Equal(HttpStatusCode.Redirect, toLogin.StatusCode);
            var location = toLogin.Headers.Location!.OriginalString;
            Assert.StartsWith("/login?return_url=", location, StringComparison.Ordinal);
            var returnUrl = Uri.UnescapeDataString(location["/login?return_url=".Length..]);
            Assert.StartsWith("/consent?request=jjar_", returnUrl, StringComparison.Ordinal);
            Assert.Equal(returnUrl, AuthEndpoints.SafeReturnUrl(returnUrl));
        }

        // 3. ログインして同意画面の内容を見て同意する（ループバックはポートを問わない）
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        using var authorize = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery("flow-app", "http://127.0.0.1:50000/callback", pkce));
        var handle = OAuthTestEnvironment.RequestHandle(authorize);
        using (var consent = await browser.GetAsync($"/oauth/requests/{handle}"))
        {
            Assert.Equal(HttpStatusCode.OK, consent.StatusCode);
            var view = await TestBrowser.JsonAsync(consent);
            Assert.Equal("flow-app のアプリ", view.GetProperty("client").GetProperty("name").GetString());
            Assert.True(view.GetProperty("client").GetProperty("loopback_only").GetBoolean());
            Assert.Equal(["*"], Strings(view.GetProperty("client"), "fields"));
            Assert.Empty(Strings(view.GetProperty("client"), "mcp_tools"));
            Assert.Equal("127.0.0.1", view.GetProperty("redirect_host").GetString());
            Assert.Equal("mcp", view.GetProperty("resource").GetString());
            Assert.Equal(["jinji.read"], Strings(view, "scopes"));
        }

        var redirect = await OAuthTestEnvironment.DecideAsync(browser, handle, approve: true);
        Assert.Equal("http://127.0.0.1:50000/callback", redirect.GetLeftPart(UriPartial.Path));
        var query = OAuthTestEnvironment.Query(redirect);
        Assert.Equal(OAuthTestEnvironment.IssuerUrl, query["iss"]);
        Assert.Equal("st-123", query["state"]);

        // 4. コードをトークンに（MCP はリフレッシュトークンも）
        var tokens = await _env.ExchangeAsync("flow-app", query["code"], "http://127.0.0.1:50000/callback", pkce);
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        Assert.Equal("Bearer", tokens.Body.GetProperty("token_type").GetString());
        Assert.Equal(600, tokens.Body.GetProperty("expires_in").GetInt32());
        Assert.Equal("jinji.read", tokens.Body.GetProperty("scope").GetString());
        Assert.StartsWith("jjat_", tokens.AccessToken, StringComparison.Ordinal);
        Assert.StartsWith("jjrt_", tokens.RefreshToken, StringComparison.Ordinal);
        Assert.Equal("no-store", tokens.Raw.Headers.CacheControl!.ToString());

        // 5. MCP の疎通（委任元の人とクライアントを監査できる閲覧者になる）
        using (var ping = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
            var reply = (await McpTestClient.ReadMessageAsync(ping))!.Value;
            Assert.Equal(1, reply.GetProperty("id").GetInt32());
            Assert.Equal(JsonValueKind.Object, reply.GetProperty("result").ValueKind);
        }

        // 6. リフレッシュ（回転）して、新しいトークンでも疎通する
        var refreshed = await _env.RefreshAsync("flow-app", tokens.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.NotEqual(tokens.RefreshToken, refreshed.RefreshToken);
        using (var ping = await _env.McpPingAsync(refreshed.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        Assert.True(await db.CountAuditAsync("auth.consent", "success", Ct) >= 1);
        Assert.True(await db.CountAuditAsync("auth.token.issue", "success", Ct, "agent") >= 2);
    }

    // ---- 認可の要求の検査 ----------------------------------------------------------------------------------------

    [Fact(DisplayName = "redirect_uri: 完全一致だけ（パス・クエリ・ホストの違いは転送せずに 400）。ループバックの http だけポートを問わない")]
    public async Task RedirectUri_ExactMatch()
    {
        await _env.RegisterPublicClientAsync("redirect-app", "https://app.example/cb");
        await _env.RegisterPublicClientAsync("loopback-app", "http://localhost:8080/cb");
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();

        foreach (var (client, uri) in new[]
                 {
                     ("redirect-app", "https://app.example/cb/"),
                     ("redirect-app", "https://app.example/cb?x=1"),
                     ("redirect-app", "https://APP.example/cb"),
                     ("redirect-app", "https://evil.example/cb"),
                     ("redirect-app", "http://app.example/cb"),
                     ("loopback-app", "http://127.0.0.1:8080/cb"),
                     ("loopback-app", "http://localhost:9090/other"),
                     ("loopback-app", "https://localhost:8080/cb"),
                     ("unknown-app", "https://app.example/cb"),
                 })
        {
            using var response = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery(client, uri, pkce));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Equal(OAuthEndpoints.InvalidAuthorizationRequestType, (await TestBrowser.JsonAsync(response)).GetProperty("type").GetString());
        }

        using (var exact = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery("redirect-app", "https://app.example/cb", pkce)))
        {
            _ = OAuthTestEnvironment.RequestHandle(exact);
        }

        using var anyPort = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery("loopback-app", "http://localhost:51234/cb", pkce));
        _ = OAuthTestEnvironment.RequestHandle(anyPort);
    }

    [Fact(DisplayName = "PKCE: S256 だけ。plain・方式の省略・code_challenge の欠落は redirect_uri に invalid_request（iss と state を付ける）")]
    public async Task Pkce_RequiresS256()
    {
        await _env.RegisterPublicClientAsync("pkce-app", "https://pkce.example/cb");
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        var basic = OAuthTestEnvironment.AuthorizeQuery("pkce-app", "https://pkce.example/cb", pkce);

        foreach (var variant in new[]
                 {
                     Replace(basic, "code_challenge_method", "plain"),
                     Replace(basic, "code_challenge_method", null),
                     Replace(basic, "code_challenge", null),
                     Replace(basic, "code_challenge", pkce.Verifier),
                 })
        {
            using var response = await _env.AuthorizeAsync(browser, variant);
            var query = ErrorRedirect(response, "https://pkce.example/cb");
            Assert.Equal("invalid_request", query["error"]);
            Assert.Equal(OAuthTestEnvironment.IssuerUrl, query["iss"]);
            Assert.Equal("st-123", query["state"]);
        }

        using var implicitFlow = await _env.AuthorizeAsync(browser, Replace(basic, "response_type", "token"));
        Assert.Equal("unsupported_response_type", ErrorRedirect(implicitFlow, "https://pkce.example/cb")["error"]);

        using var duplicate = await _env.AuthorizeAsync(browser, [.. basic, ("state", "second")]);
        Assert.Equal("invalid_request", ErrorRedirect(duplicate, "https://pkce.example/cb")["error"]);
    }

    [Fact(DisplayName = "resource（RFC 8707）: 必須で 1 つだけ。知らない URI・クライアントに許していない resource は invalid_target。知らないスコープは invalid_scope")]
    public async Task Resource_IsRequired()
    {
        await _env.RegisterPublicClientAsync("resource-app", "https://res.example/cb", resources: "api");
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        var basic = OAuthTestEnvironment.AuthorizeQuery("resource-app", "https://res.example/cb", pkce, OAuthTestEnvironment.ApiResource);

        foreach (var variant in new[]
                 {
                     Replace(basic, "resource", null),
                     Replace(basic, "resource", "https://other.example/api"),
                     Replace(basic, "resource", OAuthTestEnvironment.McpResource),
                     [.. basic, ("resource", OAuthTestEnvironment.McpResource)],
                 })
        {
            using var response = await _env.AuthorizeAsync(browser, variant);
            Assert.Equal("invalid_target", ErrorRedirect(response, "https://res.example/cb")["error"]);
        }

        using var unknownScope = await _env.AuthorizeAsync(browser, Replace(basic, "scope", "jinji.read jinji.admin"));
        Assert.Equal("invalid_scope", ErrorRedirect(unknownScope, "https://res.example/cb")["error"]);

        // 許していないスコープは落として与える（jinji.propose は許していない）
        var code = await _env.CodeAsync(browser, "resource-app", "https://res.example/cb", pkce, OAuthTestEnvironment.ApiResource, "jinji.read jinji.propose");
        var tokens = await _env.ExchangeAsync("resource-app", code, "https://res.example/cb", pkce, OAuthTestEnvironment.ApiResource);
        Assert.Equal("jinji.read", tokens.Body.GetProperty("scope").GetString());
        // REST API の resource にはリフレッシュトークンを出さない（MCP だけ）
        Assert.Null(tokens.RefreshToken);
    }

    // ---- 認可コード ----------------------------------------------------------------------------------------------

    [Fact(DisplayName = "認可コード: 単回使用。使用済みのコードが再び使われたら、そのコードから発行したトークンを失効する")]
    public async Task Code_IsSingleUse()
    {
        await _env.RegisterPublicClientAsync("single-app", Loopback);
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        var code = await _env.CodeAsync(browser, "single-app", Loopback, pkce);

        var first = await _env.ExchangeAsync("single-app", code, Loopback, pkce);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        using (var ping = await _env.McpPingAsync(first.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        var second = await _env.ExchangeAsync("single-app", code, Loopback, pkce);
        Assert.Equal(HttpStatusCode.BadRequest, second.Status);
        Assert.Equal("invalid_grant", second.Error);

        using (var revoked = await _env.McpPingAsync(first.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }

        Assert.Equal("invalid_grant", (await _env.RefreshAsync("single-app", first.RefreshToken!)).Error);
        Assert.Equal(["code_reuse"], await db.QueryAsSystemAsync<string>(
            "SELECT f.end_kind FROM oauth_token_family f JOIN api_client c ON c.id = f.client_ref WHERE c.client_id = 'single-app'", Ct));
        Assert.True(await db.CountAuditAsync("auth.token.reuse_detected", "denied", Ct) >= 1);
    }

    [Fact(DisplayName = "認可コード: client・redirect_uri・PKCE・resource に束縛する（合わなければ invalid_grant。正しいクライアントの交換は妨げない）")]
    public async Task Code_IsBound()
    {
        await _env.RegisterPublicClientAsync("bound-app", Loopback, resources: "api,mcp");
        await _env.RegisterPublicClientAsync("other-app", Loopback, resources: "api,mcp");
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        var code = await _env.CodeAsync(browser, "bound-app", Loopback, pkce);

        Assert.Equal("invalid_grant", (await _env.ExchangeAsync("other-app", code, Loopback, pkce)).Error);
        Assert.Equal("invalid_grant", (await _env.ExchangeAsync("bound-app", code, "http://127.0.0.1:43123/other", pkce)).Error);
        Assert.Equal("invalid_grant", (await _env.ExchangeAsync("bound-app", code, Loopback, Pkce.New())).Error);
        Assert.Equal("invalid_grant", (await _env.ExchangeAsync("bound-app", code, Loopback, pkce, OAuthTestEnvironment.ApiResource)).Error);
        Assert.Equal("invalid_target", (await _env.ExchangeAsync("bound-app", code, Loopback, pkce, "https://other.example/")).Error);
        var noVerifier = await _env.TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = "bound-app", ["code"] = code, ["redirect_uri"] = Loopback,
        });
        Assert.Equal("invalid_request", noVerifier.Error);

        var tokens = await _env.ExchangeAsync("bound-app", code, Loopback, pkce);
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
    }

    // ---- リフレッシュトークン ------------------------------------------------------------------------------------

    [Fact(DisplayName = "リフレッシュトークン: 毎回回転し、使用済みの再利用を検知したら系列ごと失効する（監査と不正検知に残す）。スコープは狭めることだけできる")]
    public async Task Refresh_RotatesAndDetectsReuse()
    {
        var clientRef = await _env.RegisterPublicClientAsync("refresh-app", Loopback, scopes: "jinji.read,jinji.propose");
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        var code = await _env.CodeAsync(browser, "refresh-app", Loopback, pkce, scope: "jinji.read jinji.propose");
        var first = await _env.ExchangeAsync("refresh-app", code, Loopback, pkce);
        Assert.Equal("jinji.read jinji.propose", first.Body.GetProperty("scope").GetString());

        Assert.Equal("invalid_scope", (await _env.RefreshAsync("refresh-app", first.RefreshToken!, "jinji.read jinji.admin")).Error);
        var second = await _env.RefreshAsync("refresh-app", first.RefreshToken!, "jinji.read");
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Equal("jinji.read", second.Body.GetProperty("scope").GetString());
        var third = await _env.RefreshAsync("refresh-app", second.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, third.Status);

        // 他のクライアントは使えない
        await _env.RegisterPublicClientAsync("thief-app", Loopback);
        Assert.Equal("invalid_grant", (await _env.RefreshAsync("thief-app", third.RefreshToken!)).Error);

        // 使用済みのトークン（second）の再利用 → 系列ごと失効（third の後継も使えない）
        var reuse = await _env.RefreshAsync("refresh-app", second.RefreshToken!);
        Assert.Equal("invalid_grant", reuse.Error);
        Assert.Equal("invalid_grant", (await _env.RefreshAsync("refresh-app", third.RefreshToken!)).Error);
        using (var ping = await _env.McpPingAsync(third.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ping.StatusCode);
        }

        Assert.Equal(["reuse_detected"], await db.QueryAsSystemAsync<string>("SELECT end_kind FROM oauth_token_family WHERE client_ref = $1", Ct, clientRef));
        Assert.True(await db.CountAuditAsync("auth.token.reuse_detected", "denied", Ct) >= 1);
        var detector = _env.Host.App.Services.GetRequiredService<AbuseDetector>();
        Assert.Equal(AbuseState.Throttled, detector.StateOf(ClientKeys.For(clientRef, F.Person("u2_member"))));
    }

    // ---- resource の束縛 -----------------------------------------------------------------------------------------

    [Fact(DisplayName = "resource の取り違え: MCP 用のトークンは REST API で、REST API 用のトークンは MCP で使えない。外部の JWT・Entra のアプリのトークンは MCP で受け付けない")]
    public async Task Tokens_AreBoundToResource()
    {
        await _env.RegisterPublicClientAsync("both-app", Loopback, resources: "api,mcp");
        var browser = await _env.LoginAsync(F.Person("u2_member"));

        var mcpPkce = Pkce.New();
        var mcp = await _env.ExchangeAsync("both-app", await _env.CodeAsync(browser, "both-app", Loopback, mcpPkce), Loopback, mcpPkce);
        var apiPkce = Pkce.New();
        var api = await _env.ExchangeAsync(
            "both-app", await _env.CodeAsync(browser, "both-app", Loopback, apiPkce, OAuthTestEnvironment.ApiResource), Loopback, apiPkce, OAuthTestEnvironment.ApiResource);

        Assert.Equal(HttpStatusCode.OK, (await _env.Host.GetJsonAsync("/api/v1/meta", api.AccessToken)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _env.Host.GetJsonAsync("/api/v1/meta", mcp.AccessToken)).Status);
        using (var ping = await _env.McpPingAsync(mcp.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        using (var ping = await _env.McpPingAsync(api.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ping.StatusCode);
        }

        // 外部の発行者の JWT（エージェント・Entra のアプリ）は MCP では受け付けない（REST API では従来どおり）
        var jwt = _env.JwtIssuer.Agent("agent-x", F.Person("u2_member"));
        var entraApp = _env.JwtIssuer.Create(new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["azp"] = "attendance", ["idtyp"] = "app" });
        foreach (var token in new[] { jwt, entraApp })
        {
            using var ping = await _env.McpPingAsync(token);
            Assert.Equal(HttpStatusCode.Unauthorized, ping.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await _env.Host.GetJsonAsync("/api/v1/meta", jwt)).Status);
    }

    // ---- 同意 ----------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "同意: 拒否は access_denied（iss・state 付き）。同意の POST は CSRF のトークンが要り、1 回だけ。同意画面の API はログインした人だけ")]
    public async Task Consent_DenyCsrfAndSingleDecision()
    {
        await _env.RegisterPublicClientAsync("consent-app", Loopback);
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();

        using (var authorize = await _env.AuthorizeAsync(browser, OAuthTestEnvironment.AuthorizeQuery("consent-app", Loopback, pkce)))
        {
            var handle = OAuthTestEnvironment.RequestHandle(authorize);
            using (var anonymous = new TestBrowser(_env.Host.ApiPort))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/oauth/requests/{handle}")).StatusCode);
            }

            using (var noCsrf = await browser.PostAsync($"/oauth/requests/{handle}/decision", new { approve = true }, csrf: false))
            {
                Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode);
                Assert.Equal(CsrfMiddleware.ProblemType, (await TestBrowser.JsonAsync(noCsrf)).GetProperty("type").GetString());
            }

            var denied = OAuthTestEnvironment.Query(await OAuthTestEnvironment.DecideAsync(browser, handle, approve: false));
            Assert.Equal("access_denied", denied["error"]);
            Assert.Equal(OAuthTestEnvironment.IssuerUrl, denied["iss"]);
            Assert.Equal("st-123", denied["state"]);
            Assert.False(denied.ContainsKey("code"));

            using var again = await browser.PostAsync($"/oauth/requests/{handle}/decision", new { approve = true });
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/oauth/requests/{handle}")).StatusCode);
        }

        // 外部の JWT・jin-ji のトークンでは同意できない（UI のセッションだけ）
        using var bearer = await _env.Host.GetAsync("/oauth/requests/jjar_" + new string('A', 43), _env.JwtIssuer.User("portal", F.Person("u2_member")));
        Assert.Equal(HttpStatusCode.Forbidden, bearer.StatusCode);
        Assert.True(await db.CountAuditAsync("auth.consent", "denied", Ct) >= 1);
    }

    [Fact(DisplayName = "トークンエンドポイントは Cookie を読まない（UI のセッションの Cookie を付けても CSRF の対象にならず、クライアントの認証で判定する）")]
    public async Task TokenEndpoint_DoesNotReadCookie()
    {
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var cookies = browser.Cookies.Select(c => $"{c.Key}={c.Value}").ToList();

        var response = await _env.TokenAsync(new() { ["grant_type"] = "client_credentials" }, cookies: cookies);

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
        Assert.Equal("invalid_client", response.Error);
        Assert.Equal("no-store", response.Raw.Headers.CacheControl!.ToString());
        var revoke = await _env.TokenAsync(new() { ["token"] = "x" }, path: "/oauth/revoke", cookies: cookies);
        Assert.Equal("invalid_client", revoke.Error);
        // JSON でない本文は受け付けない
        using var http = new HttpClient();
        using var json = await http.PostAsync(
            new Uri($"http://127.0.0.1:{_env.Host.ApiPort}/oauth/token"), new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, json.StatusCode);
    }

    [Fact(DisplayName = "失効（RFC 7009）: アクセストークンはそれだけ、リフレッシュトークンは系列ごと。知らないトークン・他のクライアントのトークンも 200")]
    public async Task Revoke_AccessAndRefresh()
    {
        await _env.RegisterPublicClientAsync("revoke-app", Loopback);
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        var tokens = await _env.ExchangeAsync("revoke-app", await _env.CodeAsync(browser, "revoke-app", Loopback, pkce), Loopback, pkce);

        Assert.Equal(HttpStatusCode.OK, (await _env.TokenAsync(new() { ["client_id"] = "revoke-app", ["token"] = "jjat_unknown" }, path: "/oauth/revoke")).Status);
        await _env.RegisterPublicClientAsync("revoke-other", Loopback);
        Assert.Equal(HttpStatusCode.OK, (await _env.TokenAsync(new() { ["client_id"] = "revoke-other", ["token"] = tokens.AccessToken }, path: "/oauth/revoke")).Status);
        using (var still = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, still.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await _env.TokenAsync(new() { ["client_id"] = "revoke-app", ["token"] = tokens.AccessToken }, path: "/oauth/revoke")).Status);
        using (var revoked = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }

        // アクセストークンの失効だけでは系列は続く。リフレッシュトークンの失効で系列ごと終わる
        var refreshed = await _env.RefreshAsync("revoke-app", tokens.RefreshToken!);
        Assert.Equal(HttpStatusCode.OK, refreshed.Status);
        Assert.Equal(HttpStatusCode.OK, (await _env.TokenAsync(new() { ["client_id"] = "revoke-app", ["token"] = refreshed.RefreshToken }, path: "/oauth/revoke")).Status);
        Assert.Equal("invalid_grant", (await _env.RefreshAsync("revoke-app", refreshed.RefreshToken!)).Error);
        using (var family = await _env.McpPingAsync(refreshed.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, family.StatusCode);
        }

        Assert.True(await db.CountAuditAsync("auth.token.revoke", "success", Ct) >= 2);
    }

    // ---- 閲覧者 --------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "閲覧者: 委任元ユーザーの権限 ∩ スコープ ∩ クライアントの項目上限。jinji.read だけのトークンは状態を変えられない（insufficient_scope）")]
    public async Task Viewer_IntersectsDelegatorScopeAndClientLimit()
    {
        await _env.RegisterPublicClientAsync("limited-app", Loopback, resources: "api", fields: "display_name,employee_number");

        async Task<string> TokenForAsync(string person)
        {
            var browser = await _env.LoginAsync(F.Person(person));
            var pkce = Pkce.New();
            var code = await _env.CodeAsync(browser, "limited-app", Loopback, pkce, OAuthTestEnvironment.ApiResource);
            return (await _env.ExchangeAsync("limited-app", code, Loopback, pkce, OAuthTestEnvironment.ApiResource)).AccessToken;
        }

        var admin = await TokenForAsync("hr_admin");
        var member = await TokenForAsync("u2_member");

        // 委任元の権限: 人事管理者の委任は未来の時点を見られる。一般の人の委任は見られない
        Assert.True((await _env.Host.GetJsonAsync("/api/v1/meta", admin)).Body.GetProperty("can_view_future").GetBoolean());
        Assert.False((await _env.Host.GetJsonAsync("/api/v1/meta", member)).Body.GetProperty("can_view_future").GetBoolean());

        // クライアントの上限: 2 項目だけ
        var (status, person) = await _env.Host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", member);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(["display_name", "employee_number"], Strings(person, "visible_fields"));

        // スコープ: 状態を変える要求は jinji.propose が要る
        using var write = await _env.Host.PostAsync($"/api/v1/admin/clients/x/release", admin);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        var challenge = Assert.Single(write.Headers.WwwAuthenticate).ToString();
        Assert.Contains("error=\"insufficient_scope\"", challenge, StringComparison.Ordinal);
        Assert.Contains("scope=\"jinji.propose\"", challenge, StringComparison.Ordinal);

        // AI エージェントは委任元が人事管理者でも管理操作（不正検知の一覧）を使えない
        Assert.Equal(HttpStatusCode.Forbidden, (await _env.Host.GetJsonAsync("/api/v1/admin/clients", admin)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _env.Host.GetJsonAsync("/auth/me", admin)).Status);
    }

    // ---- 管理 ----------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "管理: 人事管理者が UI でクライアントを登録・停止すると、そのクライアントのトークンは次の要求から使えない。一般の人・トークンは管理できない")]
    public async Task Admin_RegisterAndSuspend()
    {
        var admin = await _env.LoginAsync(F.Person("hr_head"));
        using (var registered = await admin.PostAsync("/auth/admin/oauth/clients", new
        {
            client_id = "admin-app",
            display_name = "管理から登録",
            auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            redirect_uris = new[] { Loopback },
            resources = new[] { "mcp" },
            scopes = new[] { "jinji.read" },
            fields = new[] { "*" },
        }))
        {
            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        }

        var list = await TestBrowser.JsonAsync(await admin.GetAsync("/auth/admin/oauth/clients"));
        var clientRef = list.EnumerateArray().Single(c => c.GetProperty("client_id").GetString() == "admin-app").GetProperty("id").GetGuid();

        var user = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        var tokens = await _env.ExchangeAsync("admin-app", await _env.CodeAsync(user, "admin-app", Loopback, pkce), Loopback, pkce);
        using (var ping = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        // 一般の人・トークンでは管理できない
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync($"/auth/admin/oauth/clients/{clientRef}/suspend")).StatusCode);
        using (var byToken = await _env.Host.PostAsync($"/auth/admin/oauth/clients/{clientRef}/suspend", _env.JwtIssuer.User("portal", F.Person("hr_head"), "hr_admin")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, byToken.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/auth/admin/oauth/clients/{clientRef}/suspend")).StatusCode);
        using (var ping = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ping.StatusCode);
        }

        Assert.Equal("invalid_client", (await _env.RefreshAsync("admin-app", tokens.RefreshToken!)).Error);

        // 再開すれば同じ系列が使える（停止は失効ではない）。管理者の失効で系列を終える
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/auth/admin/oauth/clients/{clientRef}/activate")).StatusCode);
        using (var ping = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        using (var revoke = await admin.PostAsync($"/auth/admin/oauth/clients/{clientRef}/revoke-tokens", new { person_id = F.Person("u2_member") }))
        {
            Assert.Equal(1, (await TestBrowser.JsonAsync(revoke)).GetProperty("revoked").GetInt32());
        }

        using (var ping = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ping.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/auth/admin/oauth/clients/{clientRef}/approve")).StatusCode);
        Assert.True(await db.CountAuditAsync("auth.client.suspend", "success", Ct, "user") >= 1);
        Assert.True(await db.CountAuditAsync("auth.client.register", "success", Ct, "user") >= 1);
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private static string[] Strings(JsonElement element, string property) =>
        [.. element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];

    private static (string, string?)[] Replace((string Name, string? Value)[] query, string name, string? value) =>
        [.. query.Select(q => q.Name == name ? (q.Name, value) : q)];

    /// <summary>redirect_uri への誤りの転送のクエリ。</summary>
    private static Dictionary<string, string> ErrorRedirect(HttpResponseMessage response, string redirectUri)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(redirectUri, location.GetLeftPart(UriPartial.Path));
        return OAuthTestEnvironment.Query(location);
    }
}
