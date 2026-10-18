using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using JinJi.Application.Auth;
using JinJi.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace JinJi.IntegrationTests.Support;

/// <summary>CIMD の文書の取得の代わり（URL ごとに決めた結果を返し、取得の回数を数える）。</summary>
internal sealed class FakeMetadataFetcher : IClientMetadataFetcher
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, ClientMetadataFetch> Documents { get; } = new(StringComparer.Ordinal);

    public int Count(string url) => _counts.GetValueOrDefault(url);

    public Task<ClientMetadataFetch> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        _counts.AddOrUpdate(url.AbsoluteUri, 1, (_, n) => n + 1);
        return Task.FromResult(Documents.TryGetValue(url.AbsoluteUri, out var document) ? document : ClientMetadataFetch.Failed("状態コード 404"));
    }

    /// <summary>CIMD の文書を置く。</summary>
    public void Put(string clientId, IEnumerable<string> redirectUris, string name = "テストの MCP クライアント", TimeSpan? cacheFor = null, string? documentClientId = null) =>
        Documents[clientId] = new ClientMetadataFetch(
            JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["client_id"] = documentClientId ?? clientId,
                ["client_name"] = name,
                ["redirect_uris"] = redirectUris.ToArray(),
                ["grant_types"] = new[] { "authorization_code", "refresh_token" },
                ["response_types"] = new[] { "code" },
                ["token_endpoint_auth_method"] = "none",
            }),
            cacheFor,
            null);
}

/// <summary>PKCE の組（S256）。</summary>
internal sealed record Pkce(string Verifier, string Challenge)
{
    public static Pkce New()
    {
        var verifier = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48));
        return new Pkce(verifier, AuthorizationServer.S256(verifier));
    }
}

/// <summary>トークンの応答。</summary>
internal sealed record TokenResponse(HttpStatusCode Status, JsonElement Body, HttpResponseMessage Raw)
{
    public string AccessToken => Body.GetProperty("access_token").GetString()!;

    public string? RefreshToken => Body.TryGetProperty("refresh_token", out var value) ? value.GetString() : null;

    public string? Error => Body.TryGetProperty("error", out var value) ? value.GetString() : null;
}

/// <summary>
/// トークン発行（ADR-0004 の段階 B）のテスト用のホスト。人のログインはスタブの OIDC（google）、CIMD の取得は <see cref="FakeMetadataFetcher"/>。
/// 発行者は https://jinji.test、REST API の resource は https://jinji.test/api/v1、MCP の resource は https://jinji.test/mcp。
/// CIMD の許可ドメインは cimd.example。
/// </summary>
internal sealed class OAuthTestEnvironment : IAsyncDisposable
{
    public const string IssuerUrl = AuthTestHost.Origin;
    public const string ApiResource = IssuerUrl + "/api/v1";
    public const string McpResource = IssuerUrl + "/mcp";
    public const string AllowedCimdDomain = "cimd.example";

    private readonly AuthFixtureDatabase _db;

    private OAuthTestEnvironment(AuthFixtureDatabase db, ApiTestHost host, StubOidcProvider stub, TestIssuer jwtIssuer, FakeMetadataFetcher fetcher, string[] serverArgs)
    {
        _db = db;
        Host = host;
        Stub = stub;
        JwtIssuer = jwtIssuer;
        Fetcher = fetcher;
        ServerArgs = serverArgs;
    }

    public ApiTestHost Host { get; }

    public StubOidcProvider Stub { get; }

    /// <summary>外部の発行者（JWT）。</summary>
    public TestIssuer JwtIssuer { get; }

    public FakeMetadataFetcher Fetcher { get; }

    private string[] ServerArgs { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<OAuthTestEnvironment> StartAsync(
        AuthFixtureDatabase db,
        IEnumerable<string>? extraArgs = null,
        TimeProvider? timeProvider = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var stub = await StubOidcProvider.StartAsync();
        var jwtIssuer = new TestIssuer();
        var fetcher = new FakeMetadataFetcher();
        string[] serverArgs =
        [
            $"--JinJi:Auth:Server:Issuer={IssuerUrl}",
            $"--JinJi:Auth:Server:McpResource={McpResource}",
            $"--JinJi:Auth:Server:Cimd:AllowedDomains:0={AllowedCimdDomain}",
        ];
        var host = await AuthTestHost.StartAsync(
            db,
            jwtIssuer,
            [
                .. stub.ConfigArgs(0, "google", "oidc"),
                .. serverArgs,
                // 1 つの接続元から多くの要求を送るので、テストの既定は接続元ごとの制限を広げる（制限と不正検知のテストは個別に絞る）
                "--JinJi:RateLimit:Api:AnonymousPerAddress=100000",
                "--JinJi:RateLimit:Mcp:AnonymousPerAddress=100000",
                "--JinJi:Auth:Server:RateLimit:PerAddress=100000",
                "--JinJi:Auth:Server:RateLimit:PerAddressAndClient=100000",
                "--JinJi:Abuse:TokenFailureThreshold=100000",
                .. extraArgs ?? [],
            ],
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IClientMetadataFetcher>(fetcher));
                if (timeProvider is not null)
                {
                    services.Replace(ServiceDescriptor.Singleton(timeProvider));
                }

                configureServices?.Invoke(services);
            });
        return new OAuthTestEnvironment(db, host, stub, jwtIssuer, fetcher, serverArgs);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        await Stub.DisposeAsync();
        JwtIssuer.Dispose();
    }

    // ---- 人のログイン・CLI ---------------------------------------------------------------------------------------

    /// <summary>人を google の新しい主体に紐付けてログインし、CSRF のトークンを持ったブラウザを返す。</summary>
    public async Task<TestBrowser> LoginAsync(Guid person)
    {
        var subject = Guid.NewGuid().ToString("N");
        var (exit, output) = await _db.CliAsync("bind-login", ["--person", person.ToString(), "--provider", "google", "--subject", subject, "--issuer", Stub.Issuer, .. Stub.ConfigArgs(0, "google", "oidc")]);
        Assert.True(exit == 0, output);
        var browser = new TestBrowser(Host.ApiPort);
        using (var callback = await AuthTestHost.LoginWithOidcAsync(browser, Stub, "google", new() { ["sub"] = subject }))
        {
            Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        }

        await browser.RefreshCsrfAsync();
        return browser;
    }

    /// <summary>トークン発行の管理 CLI。</summary>
    public async Task<(int Exit, string Output)> CliAsync(string command, params string[] args)
    {
        using var output = new StringWriter();
        var exit = await OAuthCommands.RunAsync(
            command,
            [
                .. args, .. ServerArgs, $"--ConnectionStrings:App={_db.Database.AppConnectionString}",
                $"--JinJi:Auth:Login:PublicOrigin={AuthTestHost.Origin}", "--Logging:LogLevel:Default=Warning",
            ],
            output);
        return (exit, output.ToString());
    }

    /// <summary>公開クライアント（MCP 向け、認可コード + リフレッシュ）を事前登録し、api_client.id を返す（<paramref name="mcpTools"/> は MCP ツールの許可リスト）。</summary>
    public async Task<Guid> RegisterPublicClientAsync(
        string clientId,
        string redirectUri,
        string resources = "mcp",
        string scopes = "jinji.read",
        string fields = "*",
        string grantTypes = "authorization_code,refresh_token",
        string? mcpTools = null)
    {
        string[] args =
        [
            "--client-id", clientId, "--name", $"{clientId} のアプリ", "--auth", "none", "--grant-types", grantTypes,
            "--redirect-uris", redirectUri, "--resources", resources, "--scopes", scopes, "--fields", fields,
            .. mcpTools is null ? Array.Empty<string>() : ["--mcp-tools", mcpTools],
        ];
        var (exit, output) = await CliAsync("register-client", args);
        Assert.True(exit == 0, output);
        return Guid.Parse(AuthFixtureDatabase.Field(output, "id"));
    }

    // ---- 認可の要求・トークン ------------------------------------------------------------------------------------

    /// <summary>/oauth/authorize を呼ぶ（転送はたどらない）。</summary>
    public Task<HttpResponseMessage> AuthorizeAsync(TestBrowser browser, params (string Name, string? Value)[] query) =>
        browser.GetAsync("/oauth/authorize?" + string.Join('&', query.Where(q => q.Value is not null).Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value!)}")));

    /// <summary>標準の認可の要求のパラメーター。</summary>
    public static (string, string?)[] AuthorizeQuery(string clientId, string redirectUri, Pkce pkce, string resource = McpResource, string? scope = "jinji.read", string? state = "st-123") =>
    [
        ("response_type", "code"), ("client_id", clientId), ("redirect_uri", redirectUri), ("scope", scope), ("state", state),
        ("code_challenge", pkce.Challenge), ("code_challenge_method", "S256"), ("resource", resource),
    ];

    /// <summary>同意画面への転送から要求 ID を読む。</summary>
    public static string RequestHandle(HttpResponseMessage authorize)
    {
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var location = authorize.Headers.Location!.OriginalString;
        Assert.StartsWith("/consent?request=", location, StringComparison.Ordinal);
        return Uri.UnescapeDataString(location["/consent?request=".Length..]);
    }

    /// <summary>同意・拒否して、redirect_uri への転送先を返す。</summary>
    public static async Task<Uri> DecideAsync(TestBrowser browser, string handle, bool approve)
    {
        using var response = await browser.PostAsync($"/oauth/requests/{handle}/decision", new { approve });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return new Uri((await TestBrowser.JsonAsync(response)).GetProperty("redirect_to").GetString()!);
    }

    /// <summary>ログイン済みのブラウザで認可コードを得る（同意まで）。</summary>
    public async Task<string> CodeAsync(TestBrowser browser, string clientId, string redirectUri, Pkce pkce, string resource = McpResource, string? scope = "jinji.read")
    {
        using var authorize = await AuthorizeAsync(browser, AuthorizeQuery(clientId, redirectUri, pkce, resource, scope));
        var redirect = await DecideAsync(browser, RequestHandle(authorize), approve: true);
        var query = Query(redirect);
        Assert.Equal(IssuerUrl, query["iss"]);
        Assert.Equal("st-123", query["state"]);
        return query["code"];
    }

    /// <summary>認可コードをトークンに交換する。</summary>
    public Task<TokenResponse> ExchangeAsync(string clientId, string code, string redirectUri, Pkce pkce, string? resource = McpResource) =>
        TokenAsync(new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = redirectUri,
            ["code_verifier"] = pkce.Verifier, ["resource"] = resource,
        });

    /// <summary>リフレッシュトークンを使う。</summary>
    public Task<TokenResponse> RefreshAsync(string clientId, string refreshToken, string? scope = null) =>
        TokenAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = clientId, ["refresh_token"] = refreshToken, ["scope"] = scope });

    /// <summary>/oauth/token に form で送る（値が null のものは送らない）。</summary>
    public async Task<TokenResponse> TokenAsync(Dictionary<string, string?> form, string? basic = null, string path = "/oauth/token", IEnumerable<string>? cookies = null)
    {
        using var http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{Host.ApiPort}{path}"))
        {
            Content = new FormUrlEncodedContent(form.Where(kv => kv.Value is not null).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value!))),
        };
        if (basic is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        }

        if (cookies is not null)
        {
            request.Headers.Add("Cookie", string.Join("; ", cookies));
        }

        var response = await http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "{}" : text);
        return new TokenResponse(response.StatusCode, json.RootElement.Clone(), response);
    }

    /// <summary>MCP のポートで JSON-RPC の ping を送る（応答は SSE。読むときは <see cref="McpTestClient.ReadMessageAsync"/>）。</summary>
    public async Task<HttpResponseMessage> McpPingAsync(string? token, string path = "/mcp")
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"http://127.0.0.1:{Host.McpPort}{path}"))
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", System.Text.Encoding.UTF8, "application/json"),
        };
        // Streamable HTTP の要求は JSON と SSE の両方を受け付けると示す（MCP 仕様。示さなければ 406）
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await http.SendAsync(request, Ct);
    }

    /// <summary>MCP のポートで GET する。</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> McpGetJsonAsync(string path)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{Host.McpPort}{path}"), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "null" : text);
        return (response.StatusCode, json.RootElement.Clone());
    }

    /// <summary>
    /// 人を新しくログインさせ、クライアントの認可コード + PKCE の流れで MCP（既定）か REST API のアクセストークンを得る。
    /// </summary>
    public async Task<string> AccessTokenAsync(Guid person, string clientId, string redirectUri, string resource = McpResource, string scope = "jinji.read")
    {
        using var browser = await LoginAsync(person);
        var pkce = Pkce.New();
        var code = await CodeAsync(browser, clientId, redirectUri, pkce, resource, scope);
        var tokens = await ExchangeAsync(clientId, code, redirectUri, pkce, resource);
        Assert.Equal(HttpStatusCode.OK, tokens.Status);
        return tokens.AccessToken;
    }

    /// <summary>URL のクエリ。</summary>
    public static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty, StringComparer.Ordinal);
}

/// <summary>private_key_jwt の鍵と client assertion。</summary>
internal sealed class ClientKey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string KeyId { get; } = "k1";

    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>client assertion（既定: aud = 発行者、exp = 2 分後、jti は乱数）。</summary>
    public string Assertion(string clientId, string? audience = null, DateTime? expires = null, string? jti = null, string? subject = null, string algorithm = SecurityAlgorithms.EcdsaSha256)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject ?? clientId, ["jti"] = jti ?? Guid.NewGuid().ToString("N") };
        var exp = expires ?? DateTime.UtcNow.AddMinutes(2);
        var notBefore = DateTime.UtcNow.AddSeconds(-5);
        SigningCredentials credentials = algorithm == SecurityAlgorithms.HmacSha256
            ? new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), algorithm)
            : new SigningCredentials(new ECDsaSecurityKey(_key) { KeyId = KeyId }, algorithm);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = clientId,
            Audience = audience ?? OAuthTestEnvironment.IssuerUrl,
            Claims = claims,
            IssuedAt = exp.AddMinutes(-1) < notBefore ? exp.AddMinutes(-1) : notBefore,
            NotBefore = exp.AddMinutes(-1) < notBefore ? exp.AddMinutes(-1) : notBefore,
            Expires = exp,
            SigningCredentials = credentials,
        });
    }

    public void Dispose() => _key.Dispose();
}
