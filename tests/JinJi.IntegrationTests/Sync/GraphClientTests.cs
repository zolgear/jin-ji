using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using JinJi.Sync.DirectApply;

namespace JinJi.IntegrationTests.Sync;

/// <summary>Graph REST の最小実装・トークンの取得・設定（実テナントには接続しない。HTTP はフェイク）。</summary>
public sealed class GraphClientTests
{
    private static readonly DirectApplyOptions Options = new()
    {
        Enabled = true,
        TenantId = "00000000-0000-0000-0000-0000000000aa",
        ClientId = "00000000-0000-0000-0000-0000000000bb",
        CertificatePath = "/nonexistent.pfx",
        MaxRetries = 2,
        MaxRetryDelay = TimeSpan.FromMilliseconds(10),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "Graph: GET は $select の項目だけ、PATCH は変える項目だけ、上長は manager/$ref に PUT する")]
    public async Task Requests()
    {
        var id = Guid.NewGuid().ToString("D");
        var manager = Guid.NewGuid().ToString("D");
        var handler = new FakeHandler(request => request.Method.Method switch
        {
            "GET" when request.RequestUri!.AbsolutePath.EndsWith("/manager", StringComparison.Ordinal) => Json(HttpStatusCode.OK, $$"""{"id":"{{manager.ToUpperInvariant()}}"}"""),
            "GET" => Json(HttpStatusCode.OK, $$"""{"id":"{{id}}","department":"営業一課","accountEnabled":true,"jobTitle":null}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        });
        var client = Client(handler);

        var values = await client.GetUserAsync(id, ["department", "accountEnabled", "jobTitle"], Ct);
        Assert.Equal(new Dictionary<string, string?> { ["department"] = "営業一課", ["accountEnabled"] = "true", ["jobTitle"] = null }, values);
        Assert.Equal(manager, await client.GetManagerIdAsync(id, Ct));
        await client.UpdateUserAsync(id, new Dictionary<string, object?> { ["accountEnabled"] = false, ["department"] = "\"; DROP" }, Ct);
        await client.SetManagerAsync(id, manager, Ct);

        Assert.Equal(
            [
                $"GET https://graph.microsoft.com/v1.0/users/{id}?$select=id,department,accountEnabled,jobTitle",
                $"GET https://graph.microsoft.com/v1.0/users/{id}/manager?$select=id",
                $"PATCH https://graph.microsoft.com/v1.0/users/{id}",
                $"PUT https://graph.microsoft.com/v1.0/users/{id}/manager/$ref",
            ],
            handler.Requests.Select(r => $"{r.Method} {Uri.UnescapeDataString(r.Uri)}"));
        Assert.All(handler.Requests, r => Assert.Equal("Bearer test-token", r.Authorization));
        using (var patch = JsonDocument.Parse(handler.Requests[2].Body!))
        {
            Assert.False(patch.RootElement.GetProperty("accountEnabled").GetBoolean());
            Assert.Equal("\"; DROP", patch.RootElement.GetProperty("department").GetString());
        }

        using var put = JsonDocument.Parse(handler.Requests[3].Body!);
        Assert.Equal($"https://graph.microsoft.com/v1.0/users/{manager}", put.RootElement.GetProperty("@odata.id").GetString());
    }

    [Fact(DisplayName = "Graph: 上長がいなければ null（404）。429 は Retry-After を待って再送し、他のエラーは状態コードとエラーコードで例外")]
    public async Task Errors()
    {
        var id = Guid.NewGuid().ToString("D");
        var attempts = 0;
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/manager", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.NotFound, """{"error":{"code":"Request_ResourceNotFound"}}""");
            }

            if (request.Method == HttpMethod.Patch)
            {
                return Json(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied","message":"secret detail"}}""");
            }

            attempts++;
            var throttled = Json(HttpStatusCode.TooManyRequests, "{}");
            throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(1));
            return attempts <= 2 ? throttled : Json(HttpStatusCode.OK, "{}");
        });
        var client = Client(handler);

        Assert.Null(await client.GetManagerIdAsync(id, Ct));
        Assert.Equal(new Dictionary<string, string?> { ["department"] = null }, await client.GetUserAsync(id, ["department"], Ct));
        Assert.Equal(3, attempts);
        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => client.UpdateUserAsync(id, new Dictionary<string, object?> { ["department"] = "x" }, Ct));
        Assert.Equal((403, "Authorization_RequestDenied"), (ex.StatusCode, ex.ErrorCode));
        Assert.DoesNotContain("secret", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Graph: UUID でない ID・許可リストに無いプロパティ・許可リストに無いホストには何も送らない")]
    public async Task Rejects()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = Client(handler);
        var id = Guid.NewGuid().ToString("D");

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetUserAsync("../me", ["department"], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.SetManagerAsync(id, "x?$ref", Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetUserAsync(id, ["birthday"], Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpdateUserAsync(id, new Dictionary<string, object?> { ["passwordProfile"] = "x" }, Ct));
        var evil = new GraphRestDirectoryClient(new HttpClient(handler), new FakeTokens(), new DirectApplyOptions { Enabled = true, GraphBaseUrl = "https://evil.example.com/v1.0" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => evil.GetManagerIdAsync(id, Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact(DisplayName = "トークン: 証明書で署名したクライアント アサーション（PS256・x5t#S256）で client_credentials を要求し、期限まで使い回す")]
    public async Task Token_WithCertificateAssertion()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=jinji-sync-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"issued","expires_in":3600,"token_type":"Bearer"}"""));
        var tokens = new ClientCredentialTokenSource(
            new HttpClient(handler), Options, new CertificateAssertionSource(certificate, Options.ClientId!, TimeProvider.System), TimeProvider.System);

        Assert.Equal("issued", await tokens.GetTokenAsync(Ct));
        Assert.Equal("issued", await tokens.GetTokenAsync(Ct));

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"POST https://login.microsoftonline.com/{Options.TenantId}/oauth2/v2.0/token", $"{request.Method} {request.Uri}");
        var form = request.Body!.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        Assert.Equal(("client_credentials", "https://graph.microsoft.com/.default", Options.ClientId), (form["grant_type"], form["scope"], form["client_id"]));
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
        Assert.DoesNotContain("client_secret", form.Keys);

        var parts = form["client_assertion"].Split('.');
        using var header = JsonDocument.Parse(Base64Url(parts[0]));
        using var payload = JsonDocument.Parse(Base64Url(parts[1]));
        Assert.Equal("PS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal(Convert.ToBase64String(SHA256.HashData(certificate.RawData)).TrimEnd('=').Replace('+', '-').Replace('/', '_'), header.RootElement.GetProperty("x5t#S256").GetString());
        Assert.Equal(request.Uri, payload.RootElement.GetProperty("aud").GetString());
        Assert.Equal(Options.ClientId, payload.RootElement.GetProperty("sub").GetString());
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    [Fact(DisplayName = "トークン: 失敗の応答では、エラーコードだけを例外に出す")]
    public async Task Token_Failure()
    {
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_client","error_description":"AADSTS700027 details"}"""));
        var tokens = new ClientCredentialTokenSource(new HttpClient(handler), Options, new FakeAssertion(), TimeProvider.System);

        var ex = await Assert.ThrowsAsync<GraphRequestException>(() => tokens.GetTokenAsync(Ct));
        Assert.Equal((400, "invalid_client"), (ex.StatusCode, ex.ErrorCode));
        Assert.DoesNotContain("AADSTS", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "設定: 既定は無効。有効にするとテナント・クライアント・証明書・https・許可リストのホストが要る")]
    public void Options_Validate()
    {
        Assert.False(new DirectApplyOptions().Enabled);
        new DirectApplyOptions().Validate();
        Options.Validate();
        Assert.Throws<InvalidOperationException>(() => new DirectApplyOptions { Enabled = true }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DirectApplyOptions { Enabled = true, TenantId = "t", ClientId = "c" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DirectApplyOptions { Enabled = true, TenantId = "t", ClientId = "c", CertificatePath = "x", GraphBaseUrl = "http://graph.microsoft.com/v1.0" }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DirectApplyOptions { Enabled = true, TenantId = "t", ClientId = "c", CertificatePath = "x", GraphBaseUrl = "https://graph.example.com/v1.0" }.Validate());
        new DirectApplyOptions { Enabled = true, TenantId = "t", ClientId = "c", Credential = GraphCredentialKind.WorkloadIdentity }.Validate();
    }

    private static GraphRestDirectoryClient Client(FakeHandler handler) => new(new HttpClient(handler), new FakeTokens(), Options);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static byte[] Base64Url(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '='));
    }

    private sealed class FakeTokens : IGraphTokenSource
    {
        public Task<string> GetTokenAsync(CancellationToken cancellationToken) => Task.FromResult("test-token");
    }

    private sealed class FakeAssertion : IClientAssertionSource
    {
        public Task<string> CreateAsync(Uri audience, CancellationToken cancellationToken) => Task.FromResult("assertion");
    }

    internal sealed record RecordedRequest(HttpMethod Method, string Uri, string? Authorization, string? Body);

    /// <summary>要求を記録し、決めた応答を返す HTTP のフェイク（外へは送らない）。</summary>
    internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }
}
