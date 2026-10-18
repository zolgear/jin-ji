using System.Net;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>
/// Web UI（SPA）の配信: 静的ファイル・クライアント側のルーティングのフォールバック・キャッシュ・セキュリティヘッダー。
/// 成果物は一時ディレクトリに作る（web/dist のビルドには依存しない）。
/// </summary>
public sealed class WebUiTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>, IAsyncLifetime
{
    private const string IndexHtml = "<!doctype html><html><head><title>jin-ji</title></head><body><div id=\"root\"></div></body></html>";
    private const string AssetPath = "/assets/index-AbC123xy.js";

    private readonly TestIssuer _issuer = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jinji-webui-" + Guid.NewGuid().ToString("N"));
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "assets"));
        await File.WriteAllTextAsync(Path.Combine(_root, "index.html"), IndexHtml, Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "assets", "index-AbC123xy.js"), "console.log('jin-ji');", Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>", Ct);
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "SECRET=1", Ct);
        _host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer, [$"--JinJi:Web:RootPath={_root}"]);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _issuer.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact(DisplayName = "SPA: / と画面のパスは index.html（no-cache）、ハッシュ付きのファイルは長期キャッシュ")]
    public async Task Spa_ServesIndexAndAssets()
    {
        foreach (var path in new[] { "/", "/org-chart", "/people/0198d3a2-0000-7000-8000-000000000000", "/overview-3d/deep/link" })
        {
            using var response = await _host.GetAsync(path, null);
            Assert.True(response.StatusCode == HttpStatusCode.OK, path);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(IndexHtml, await response.Content.ReadAsStringAsync(Ct));
            Assert.Equal("no-cache", response.Headers.CacheControl?.ToString());
        }

        using var asset = await _host.GetAsync(AssetPath, null);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("text/javascript", asset.Content.Headers.ContentType?.MediaType);
        Assert.Equal("public, max-age=31536000, immutable", asset.Headers.CacheControl?.ToString());
        Assert.NotNull(asset.Headers.ETag);

        using var favicon = await _host.GetAsync("/favicon.svg", null);
        Assert.Equal(HttpStatusCode.OK, favicon.StatusCode);
        Assert.Equal("no-cache", favicon.Headers.CacheControl?.ToString());

        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/org-chart");
        using var head = await _host.Http.SendAsync(headRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Fact(DisplayName = "SPA: 拡張子のある無いファイル・ドットファイルは 404、/api・/openapi はフォールバックしない、POST は配信しない")]
    public async Task Spa_DoesNotFallBackForFilesOrApi()
    {
        foreach (var path in new[] { "/assets/missing-0000.js", "/missing.css", "/.env" })
        {
            using var response = await _host.GetAsync(path, null);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, path);
            Assert.NotEqual(IndexHtml, await response.Content.ReadAsStringAsync(Ct));
        }

        using var api = await _host.GetAsync("/api/v1/no-such-endpoint", _issuer.User("portal", Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.Equal("application/problem+json", api.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", api.Headers.CacheControl?.ToString());

        using var apiRoot = await _host.GetAsync("/api", null);
        Assert.Equal(HttpStatusCode.NotFound, apiRoot.StatusCode);

        using var openApi = await _host.GetAsync("/openapi/v1.json", null);
        Assert.Equal("application/json", openApi.Content.Headers.ContentType?.MediaType);

        using var openApiMissing = await _host.GetAsync("/openapi/v2", null);
        Assert.Equal(HttpStatusCode.NotFound, openApiMissing.StatusCode);

        using var post = await _host.PostAsync("/org-chart", null);
        Assert.NotEqual(HttpStatusCode.OK, post.StatusCode);
        Assert.NotEqual(IndexHtml, await post.Content.ReadAsStringAsync(Ct));
    }

    [Fact(DisplayName = "SPA: API のポートだけで配信し、MCP・メトリクスのポートからは見えない")]
    public async Task Spa_IsOnlyOnApiPort()
    {
        using var client = new HttpClient();
        foreach (var port in new[] { _host.McpPort, _host.MetricsPort })
        {
            using var root = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/"), Ct);
            using var asset = await client.GetAsync(new Uri($"http://127.0.0.1:{port}{AssetPath}"), Ct);
            Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, asset.StatusCode);
        }
    }

    [Fact(DisplayName = "セキュリティヘッダー: SPA・API・エラーの応答に CSP・nosniff・Referrer-Policy・Permissions-Policy などを付ける。HSTS は既定で付けない")]
    public async Task SecurityHeaders_AreOnEveryResponse()
    {
        foreach (var (path, token) in new[]
                 {
                     ("/", (string?)null), (AssetPath, null), ("/openapi/v1.json", null), ("/healthz", null),
                     ("/api/v1/orgs/tree", null), ("/assets/missing.js", null),
                 })
        {
            using var response = await _host.GetAsync(path, token);
            var csp = Header(response, "Content-Security-Policy");
            foreach (var directive in new[] { "default-src 'self'", "connect-src 'self'", "frame-ancestors 'none'", "base-uri 'none'", "object-src 'none'", "style-src 'self'", "script-src 'self'" })
            {
                Assert.Contains(directive, csp, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("unsafe", csp, StringComparison.Ordinal);
            Assert.DoesNotContain("http", csp, StringComparison.Ordinal);
            Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
            Assert.Equal("no-referrer", Header(response, "Referrer-Policy"));
            Assert.Equal("DENY", Header(response, "X-Frame-Options"));
            Assert.Contains("camera=()", Header(response, "Permissions-Policy"), StringComparison.Ordinal);
            Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
            Assert.False(response.Headers.Contains("Strict-Transport-Security"), path);
        }

        using var unauthorized = await _host.GetAsync("/api/v1/orgs/tree", null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal("no-store", unauthorized.Headers.CacheControl?.ToString());

        using var client = new HttpClient();
        using var mcp = await client.GetAsync(new Uri($"http://127.0.0.1:{_host.McpPort}/healthz"), Ct);
        Assert.Equal("nosniff", Header(mcp, "X-Content-Type-Options"));
    }

    [Fact(DisplayName = "セキュリティヘッダー: TLS を終端するリバースプロキシの後ろでは、設定で HSTS を付ける")]
    public async Task Hsts_IsAddedWhenConfigured()
    {
        await using var host = await ApiTestHost.StartAsync(
            db.Database.AppConnectionString,
            _issuer,
            [$"--JinJi:Web:RootPath={_root}", "--JinJi:Web:Hsts:Enabled=true", "--JinJi:Web:Hsts:MaxAge=180.00:00:00", "--JinJi:Web:Hsts:IncludeSubDomains=true"]);

        using var response = await host.GetAsync("/", null);
        Assert.Equal("max-age=15552000; includeSubDomains", Header(response, "Strict-Transport-Security"));
    }

    [Fact(DisplayName = "SPA: 成果物が無ければ API だけで起動する（/ は 404、API は動く）")]
    public async Task Spa_IsOptional()
    {
        await using var host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer, [$"--JinJi:Web:RootPath={Path.Combine(_root, "missing")}"]);

        using var root = await host.GetAsync("/", null);
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal("nosniff", Header(root, "X-Content-Type-Options"));
        var (status, _) = await host.GetJsonAsync("/api/v1/orgs/tree", _issuer.User("portal", db.Fixture.Person("u1_member")));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : throw new Xunit.Sdk.XunitException($"ヘッダー {name} がありません（{response.RequestMessage?.RequestUri}）");
}
