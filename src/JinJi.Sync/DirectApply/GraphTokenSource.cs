using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace JinJi.Sync.DirectApply;

/// <summary>Graph のアクセストークンの取得口（テストではフェイクに差し替える）。</summary>
public interface IGraphTokenSource
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

/// <summary>クライアント アサーション（JWT）の作成口。</summary>
public interface IClientAssertionSource
{
    Task<string> CreateAsync(Uri audience, CancellationToken cancellationToken);
}

/// <summary>
/// 証明書で署名したクライアント アサーション（PS256。ヘッダーに証明書の SHA-256 の拇印 <c>x5t#S256</c>）。
/// MSAL・Azure.Identity を入れずに、標準の暗号 API で作る（新しい依存を足さない）。
/// </summary>
public sealed class CertificateAssertionSource : IClientAssertionSource
{
    private readonly X509Certificate2 _certificate;
    private readonly string _clientId;
    private readonly TimeProvider _timeProvider;

    public CertificateAssertionSource(X509Certificate2 certificate, string clientId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (!certificate.HasPrivateKey || certificate.GetRSAPrivateKey() is null)
        {
            throw new InvalidOperationException("直接反映の証明書に RSA の秘密鍵がありません。");
        }

        _certificate = certificate;
        _clientId = clientId;
        _timeProvider = timeProvider;
    }

    /// <summary>設定の PFX を読む（パスワードは設定の名前の環境変数から）。</summary>
    public static CertificateAssertionSource FromOptions(DirectApplyOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        var password = options.CertificatePasswordEnvironmentVariable is { Length: > 0 } name ? Environment.GetEnvironmentVariable(name) : null;
        var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath!, password, X509KeyStorageFlags.EphemeralKeySet);
        return new CertificateAssertionSource(certificate, options.ClientId!, timeProvider);
    }

    public Task<string> CreateAsync(Uri audience, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audience);
        var now = _timeProvider.GetUtcNow();
        var header = new Dictionary<string, string>
        {
            ["alg"] = "PS256",
            ["typ"] = "JWT",
            ["x5t#S256"] = Base64Url(SHA256.HashData(_certificate.RawData)),
        };
        var payload = new Dictionary<string, object>
        {
            ["aud"] = audience.ToString(),
            ["iss"] = _clientId,
            ["sub"] = _clientId,
            ["jti"] = Guid.NewGuid().ToString("D"),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
        };
        var signingInput = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        using var key = _certificate.GetRSAPrivateKey()!;
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        return Task.FromResult(signingInput + "." + Base64Url(signature));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>ワークロード ID: フェデレーションのトークン（Kubernetes・Azure のワークロード ID が置くファイル）をそのままクライアント アサーションにする。</summary>
public sealed class FederatedTokenFileAssertionSource(string path) : IClientAssertionSource
{
    public static FederatedTokenFileAssertionSource FromOptions(DirectApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = options.FederatedTokenFile ?? Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE")
            ?? throw new InvalidOperationException("フェデレーションのトークンのファイル（FederatedTokenFile / AZURE_FEDERATED_TOKEN_FILE）がありません。");
        return new FederatedTokenFileAssertionSource(path);
    }

    public async Task<string> CreateAsync(Uri audience, CancellationToken cancellationToken) =>
        (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
}

/// <summary>
/// クライアント資格情報フロー（client_credentials + client_assertion）で Graph のトークンを取る。期限の 5 分前まで使い回す。
/// </summary>
public sealed class ClientCredentialTokenSource : IGraphTokenSource
{
    private readonly HttpClient _http;
    private readonly DirectApplyOptions _options;
    private readonly IClientAssertionSource _assertions;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private (string Token, DateTimeOffset ExpiresAt)? _cached;

    public ClientCredentialTokenSource(HttpClient http, DirectApplyOptions options, IClientAssertionSource assertions, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(assertions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _http = http;
        _options = options;
        _assertions = assertions;
        _timeProvider = timeProvider;
    }

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } cached && cached.ExpiresAt > _timeProvider.GetUtcNow().AddMinutes(5))
            {
                return cached.Token;
            }

            var endpoint = _options.TokenEndpoint;
            GraphRestDirectoryClient.RequireAllowed(_options, endpoint);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.ClientId!,
                ["scope"] = $"{_options.GraphBaseUri.GetLeftPart(UriPartial.Authority)}/.default",
                ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = await _assertions.CreateAsync(endpoint, cancellationToken),
            });
            using var response = await _http.PostAsync(endpoint, content, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!response.IsSuccessStatusCode || !json.RootElement.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String)
            {
                // エラーの本文（error・error_codes）だけを出す。アサーション・トークンは出さない
                var error = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                throw new GraphRequestException((int)response.StatusCode, error, "Graph のトークンを取得できませんでした。");
            }

            var lifetime = json.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) ? seconds : 3600;
            _cached = (token.GetString()!, _timeProvider.GetUtcNow().AddSeconds(lifetime));
            return _cached.Value.Token;
        }
        finally
        {
            _lock.Release();
        }
    }
}
