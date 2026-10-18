using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using JinJi.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// テスト用のトークンの発行者。鍵はテストのたびに生成し、公開鍵を設定（JinJi:Auth:Issuers）で差し込む。
/// 本番の設定で使える開発用の固定の鍵は作らない。
/// </summary>
internal sealed class TestIssuer : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestIssuer(string issuer = "https://issuer.jinji.test", string audience = "jinji-api", string keyId = "test-key")
    {
        Issuer = issuer;
        Audience = audience;
        KeyId = keyId;
    }

    public string Issuer { get; }

    public string Audience { get; }

    public string KeyId { get; }

    public string PublicKeyPem => _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>人のトークン。</summary>
    public string User(string clientId, Guid personId, params string[] roles) =>
        Create(new Dictionary<string, object> { ["sub"] = personId.ToString(), ["client_id"] = clientId, ["roles"] = roles });

    /// <summary>AI エージェントのトークン（委任元の人）。</summary>
    public string Agent(string clientId, Guid delegatorId, params string[] roles) =>
        Create(new Dictionary<string, object>
        {
            ["sub"] = delegatorId.ToString(),
            ["client_id"] = clientId,
            ["roles"] = roles,
            ["act"] = new Dictionary<string, object> { ["sub"] = clientId },
        });

    /// <summary>社内システムのトークン（Client Credentials）。</summary>
    public string Service(string clientId) =>
        Create(new Dictionary<string, object> { ["sub"] = clientId, ["client_id"] = clientId });

    /// <summary>任意のクレームのトークン。</summary>
    public string Create(Dictionary<string, object> claims, string? audience = null, DateTime? expires = null)
    {
        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience ?? Audience,
            Claims = claims,
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_key) { KeyId = KeyId }, SecurityAlgorithms.EcdsaSha256),
        });
    }

    /// <summary>発行者の設定（コマンドライン引数の形）。</summary>
    public IEnumerable<string> ConfigArgs(int index = 0, bool trustRoles = true) =>
    [
        $"--JinJi:Auth:Issuers:{index}:Issuer={Issuer}",
        $"--JinJi:Auth:Issuers:{index}:Audiences:0={Audience}",
        $"--JinJi:Auth:Issuers:{index}:SigningKeys:0:KeyId={KeyId}",
        $"--JinJi:Auth:Issuers:{index}:SigningKeys:0:PublicKeyPem={PublicKeyPem}",
        $"--JinJi:Auth:Issuers:{index}:ActorTypes:0=user",
        $"--JinJi:Auth:Issuers:{index}:ActorTypes:1=service",
        $"--JinJi:Auth:Issuers:{index}:ActorTypes:2=agent",
        $"--JinJi:Auth:Issuers:{index}:TrustRoles={trustRoles}",
    ];

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// テスト用に Kestrel で API を立てる（実 DB はフィクスチャの一時データベース）。ポートは OS に割り当てさせる。
/// </summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    /// <summary>登録するクライアント: portal（人・エージェント、上限なし）、attendance（社内システム、勤怠向けの項目だけ）、agent-x（エージェント）、admin-console（人）。</summary>
    public static readonly string[] DefaultClients =
    [
        "--JinJi:Auth:Clients:0:ClientId=portal",
        "--JinJi:Auth:Clients:0:ActorTypes:0=user",
        "--JinJi:Auth:Clients:0:Fields:0=*",
        "--JinJi:Auth:Clients:1:ClientId=attendance",
        "--JinJi:Auth:Clients:1:ActorTypes:0=service",
        "--JinJi:Auth:Clients:1:Fields:0=employee_number",
        "--JinJi:Auth:Clients:1:Fields:1=display_name",
        "--JinJi:Auth:Clients:1:Fields:2=assignments",
        "--JinJi:Auth:Clients:1:Fields:3=managers",
        "--JinJi:Auth:Clients:1:Fields:4=employment_status",
        "--JinJi:Auth:Clients:2:ClientId=agent-x",
        "--JinJi:Auth:Clients:2:ActorTypes:0=agent",
        "--JinJi:Auth:Clients:2:Fields:0=*",
        "--JinJi:Auth:Clients:3:ClientId=admin-console",
        "--JinJi:Auth:Clients:3:ActorTypes:0=user",
        "--JinJi:Auth:Clients:3:Fields:0=*",
        "--JinJi:Auth:Clients:4:ClientId=sync",
        "--JinJi:Auth:Clients:4:ActorTypes:0=service",
        "--JinJi:Auth:Clients:4:Fields:0=*",
    ];

    private ApiTestHost(WebApplication app, int apiPort, int mcpPort, int metricsPort)
    {
        App = app;
        ApiPort = apiPort;
        McpPort = mcpPort;
        MetricsPort = metricsPort;
        Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{apiPort}") };
    }

    public WebApplication App { get; }

    public int ApiPort { get; }

    public int McpPort { get; }

    public int MetricsPort { get; }

    public HttpClient Http { get; }

    /// <summary>
    /// API・MCP・メトリクスを別ポートで立てる（役割 all からワーカーを除いたもの）。
    /// ポートは 0 を指定して OS に割り当てさせ、待ち受けを始めた後に実際のポートを読む（空いているポートを先に選んで閉じると、
    /// 待ち受けるまでの間に他のテストや外向きの接続が同じポートを取り得る）。
    /// </summary>
    public static async Task<ApiTestHost> StartAsync(
        string appConnectionString,
        TestIssuer issuer,
        IEnumerable<string>? extraArgs = null,
        Action<IServiceCollection>? configureServices = null)
    {
        string[] args =
        [
            "--JinJi:Listen:Address=127.0.0.1",
            "--JinJi:Listen:ApiPort=0",
            "--JinJi:Listen:McpPort=0",
            "--JinJi:Listen:MetricsPort=0",
            $"--ConnectionStrings:App={appConnectionString}",
            "--Logging:LogLevel:Default=Warning",
            // テストは証明書なしの Data Protection の鍵で動かす（明示した設定でだけ許す）
            "--JinJi:DataProtection:AllowUnprotectedKeys=true",
            .. issuer.ConfigArgs(),
            .. DefaultClients,
            .. extraArgs ?? [],
        ];
        var app = ServerHost.BuildWeb(HostRole.Api | HostRole.Mcp, args, configureServices);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var ports = app.Services.GetRequiredService<SurfacePortMap>();
        return new ApiTestHost(app, BoundPort(ports, Surface.Api), BoundPort(ports, Surface.Mcp), BoundPort(ports, Surface.Metrics));
    }

    /// <summary>待ち受けを始めたホストの公開面のポート。</summary>
    public static int BoundPort(SurfacePortMap ports, Surface surface) =>
        ports.PortOf(surface) ?? throw new InvalidOperationException($"{surface} のポートで待ち受けていません。");

    /// <summary>トークンを付けて GET する。</summary>
    public async Task<HttpResponseMessage> GetAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>トークンを付けて POST する（本文なし）。</summary>
    public async Task<HttpResponseMessage> PostAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>GET して JSON を読む（状態コードも返す）。</summary>
    public async Task<(HttpStatusCode Status, JsonElement Body)> GetJsonAsync(string path, string? token)
    {
        using var response = await GetAsync(path, token);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "null" : text);
        return (response.StatusCode, json.RootElement.Clone());
    }

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
    }
}
