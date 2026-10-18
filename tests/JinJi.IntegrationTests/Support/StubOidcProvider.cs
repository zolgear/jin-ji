using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// テスト用の小さな OpenID Connect の発行者（認可コード + PKCE）。外部 IdP（Entra ID・汎用 OIDC）の代わりに使う。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>メタデータ・JWKS・認可・トークンのエンドポイントだけを持つ。署名鍵（ES256）は起動ごとに生成する</item>
/// <item>認可のエンドポイントは利用者の操作を待たず、<see cref="NextClaims"/> の主体でただちに redirect_uri へ戻す</item>
/// <item>トークンのエンドポイントはコードの単回使用・redirect_uri の一致・PKCE（S256）・クライアントシークレットを確かめる</item>
/// </list>
/// </remarks>
internal sealed class StubOidcProvider : IAsyncDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);
    private readonly WebApplication _app;

    private StubOidcProvider(WebApplication app, int port, string clientId, string clientSecret)
    {
        _app = app;
        Issuer = $"http://127.0.0.1:{port}";
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    public string Issuer { get; }

    public string ClientId { get; }

    public string ClientSecret { get; }

    /// <summary>次の認可で発行する id_token のクレーム（sub・oid・tid・email など）。</summary>
    public Dictionary<string, object> NextClaims { get; set; } = [];

    /// <summary>最後に受けた認可の要求のクエリ。</summary>
    public IReadOnlyDictionary<string, string> LastAuthorizeRequest { get; private set; } = new Dictionary<string, string>();

    public static async Task<StubOidcProvider> StartAsync(string clientId = "jinji-test", string clientSecret = "stub-secret")
    {
        for (var attempt = 1; ; attempt++)
        {
            var port = ApiTestHost.FreePort();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
            var app = builder.Build();
            var stub = new StubOidcProvider(app, port, clientId, clientSecret);
            stub.Map(app);
            try
            {
                await app.StartAsync(TestContext.Current.CancellationToken);
                return stub;
            }
            catch (IOException ex) when (attempt < 5 && ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
            {
                await stub.DisposeAsync();
            }
        }
    }

    /// <summary>OIDC の設定（コマンドライン引数の形）。</summary>
    public IEnumerable<string> ConfigArgs(int index, string name, string type, string? tenantId = null, string? employeeNumberClaim = null, bool trustEmployeeNumber = false) =>
    [
        $"--JinJi:Auth:Login:Providers:{index}:Name={name}",
        $"--JinJi:Auth:Login:Providers:{index}:Type={type}",
        $"--JinJi:Auth:Login:Providers:{index}:Authority={Issuer}",
        $"--JinJi:Auth:Login:Providers:{index}:ClientId={ClientId}",
        $"--JinJi:Auth:Login:Providers:{index}:ClientSecret={ClientSecret}",
        .. tenantId is null ? Array.Empty<string>() : [$"--JinJi:Auth:Login:Providers:{index}:TenantId={tenantId}"],
        .. employeeNumberClaim is null ? Array.Empty<string>() : [$"--JinJi:Auth:Login:Providers:{index}:EmployeeNumberClaim={employeeNumberClaim}"],
        $"--JinJi:Auth:Login:Providers:{index}:TrustEmployeeNumberClaim={trustEmployeeNumber}",
    ];

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        _key.Dispose();
    }

    private void Map(WebApplication app)
    {
        app.MapGet("/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Issuer,
            ["authorization_endpoint"] = $"{Issuer}/authorize",
            ["token_endpoint"] = $"{Issuer}/token",
            ["jwks_uri"] = $"{Issuer}/jwks",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "ES256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            ["token_endpoint_auth_methods_supported"] = new[] { "client_secret_post", "client_secret_basic" },
        }));

        app.MapGet("/jwks", () =>
        {
            var parameters = _key.ExportParameters(false);
            return Results.Json(new
            {
                keys = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["kty"] = "EC",
                        ["crv"] = "P-256",
                        ["x"] = Base64UrlEncoder.Encode(parameters.Q.X),
                        ["y"] = Base64UrlEncoder.Encode(parameters.Q.Y),
                        ["kid"] = "stub-key",
                        ["use"] = "sig",
                        ["alg"] = "ES256",
                    },
                },
            });
        });

        app.MapGet("/authorize", (HttpContext http) =>
        {
            var query = http.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
            LastAuthorizeRequest = query;
            if (query.GetValueOrDefault("client_id") != ClientId || query.GetValueOrDefault("response_type") != "code"
                || query.GetValueOrDefault("code_challenge_method") != "S256" || string.IsNullOrEmpty(query.GetValueOrDefault("code_challenge")))
            {
                return Results.BadRequest("invalid_request");
            }

            var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            _codes[code] = new PendingCode(query["redirect_uri"], query["code_challenge"], query.GetValueOrDefault("nonce"), new Dictionary<string, object>(NextClaims));
            return Results.Redirect($"{query["redirect_uri"]}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(query["state"])}");
        });

        app.MapPost("/token", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            var (clientId, secret) = ClientCredentials(http, form);
            if (clientId != ClientId || secret != ClientSecret)
            {
                return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            }

            if (form["grant_type"] != "authorization_code" || !_codes.TryRemove(form["code"].ToString(), out var pending)
                || pending.RedirectUri != form["redirect_uri"]
                || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString()))) != pending.CodeChallenge)
            {
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            }

            var claims = new Dictionary<string, object>(pending.Claims);
            if (pending.Nonce is { } nonce)
            {
                claims["nonce"] = nonce;
            }

            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = Issuer,
                Audience = ClientId,
                Claims = claims,
                IssuedAt = DateTime.UtcNow,
                NotBefore = DateTime.UtcNow.AddMinutes(-1),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(_key) { KeyId = "stub-key" }, SecurityAlgorithms.EcdsaSha256),
            });
            return Results.Json(new Dictionary<string, object>
            {
                ["access_token"] = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16)),
                ["token_type"] = "Bearer",
                ["expires_in"] = 300,
                ["id_token"] = idToken,
            });
        });
    }

    private static (string? ClientId, string? Secret) ClientCredentials(HttpContext http, IFormCollection form)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Basic ", StringComparison.Ordinal))
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..])).Split(':', 2);
            return (Uri.UnescapeDataString(decoded[0]), Uri.UnescapeDataString(decoded[1]));
        }

        return (form["client_id"], form["client_secret"]);
    }

    private sealed record PendingCode(string RedirectUri, string CodeChallenge, string? Nonce, Dictionary<string, object> Claims);
}

/// <summary>テスト用の認証器（パスキー。ES256 の鍵と、アテステーション none の登録・アサーションの応答を作る）。</summary>
internal sealed class TestAuthenticator(string rpId, string origin) : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private uint _signCount;

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(16);

    public byte[]? UserHandle { get; private set; }

    /// <summary>navigator.credentials.create の結果（RegistrationResponseJSON）。</summary>
    public object Create(JsonElement options)
    {
        var publicKey = options.TryGetProperty("publicKey", out var inner) ? inner : options;
        Assert.Equal(rpId, publicKey.GetProperty("rp").GetProperty("id").GetString());
        UserHandle = Base64UrlEncoder.DecodeBytes(publicKey.GetProperty("user").GetProperty("id").GetString());
        var clientData = ClientData("webauthn.create", publicKey.GetProperty("challenge").GetString()!, null);
        var parameters = _key.ExportParameters(false);
        var coseKey = Cbor.Map(
            (Cbor.Int(1), Cbor.Int(2)),
            (Cbor.Int(3), Cbor.Int(-7)),
            (Cbor.Int(-1), Cbor.Int(1)),
            (Cbor.Int(-2), Cbor.Bytes(parameters.Q.X!)),
            (Cbor.Int(-3), Cbor.Bytes(parameters.Q.Y!)));
        var authData = Concat(
            SHA256.HashData(Encoding.UTF8.GetBytes(rpId)),
            [0x45], // UP | UV | AT
            BigEndian(_signCount),
            new byte[16],
            [(byte)(CredentialId.Length >> 8), (byte)CredentialId.Length],
            CredentialId,
            coseKey);
        var attestation = Cbor.Map(
            (Cbor.Text("fmt"), Cbor.Text("none")),
            (Cbor.Text("attStmt"), Cbor.Map()),
            (Cbor.Text("authData"), Cbor.Bytes(authData)));
        return new
        {
            id = Base64UrlEncoder.Encode(CredentialId),
            rawId = Base64UrlEncoder.Encode(CredentialId),
            type = "public-key",
            response = new
            {
                clientDataJSON = Base64UrlEncoder.Encode(clientData),
                attestationObject = Base64UrlEncoder.Encode(attestation),
                transports = new[] { "internal" },
            },
            clientExtensionResults = new { },
            authenticatorAttachment = "platform",
        };
    }

    /// <summary>navigator.credentials.get の結果（AuthenticationResponseJSON）。</summary>
    public object Get(JsonElement options, string? origin = null)
    {
        var publicKey = options.TryGetProperty("publicKey", out var inner) ? inner : options;
        var clientData = ClientData("webauthn.get", publicKey.GetProperty("challenge").GetString()!, origin);
        _signCount++;
        var authData = Concat(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), [0x05], BigEndian(_signCount));
        var signature = _key.SignData(Concat(authData, SHA256.HashData(clientData)), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new
        {
            id = Base64UrlEncoder.Encode(CredentialId),
            rawId = Base64UrlEncoder.Encode(CredentialId),
            type = "public-key",
            response = new
            {
                clientDataJSON = Base64UrlEncoder.Encode(clientData),
                authenticatorData = Base64UrlEncoder.Encode(authData),
                signature = Base64UrlEncoder.Encode(signature),
                userHandle = Base64UrlEncoder.Encode(UserHandle!),
            },
            clientExtensionResults = new { },
            authenticatorAttachment = "platform",
        };
    }

    public void Dispose() => _key.Dispose();

    private byte[] ClientData(string type, string challenge, string? originOverride) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["type"] = type, ["challenge"] = challenge, ["origin"] = originOverride ?? origin, ["crossOrigin"] = false });

    private static byte[] BigEndian(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    /// <summary>最小の CBOR の符号化（整数・バイト列・文字列・マップだけ）。</summary>
    private static class Cbor
    {
        public static byte[] Int(int value) => value >= 0 ? Head(0, (ulong)value) : Head(1, (ulong)(-1 - value));

        public static byte[] Bytes(byte[] value) => [.. Head(2, (ulong)value.Length), .. value];

        public static byte[] Text(string value) => [.. Head(3, (ulong)Encoding.UTF8.GetByteCount(value)), .. Encoding.UTF8.GetBytes(value)];

        public static byte[] Map(params (byte[] Key, byte[] Value)[] entries) =>
            [.. Head(5, (ulong)entries.Length), .. entries.SelectMany(e => e.Key.Concat(e.Value))];

        private static byte[] Head(int major, ulong value) => value switch
        {
            < 24 => [(byte)((major << 5) | (int)value)],
            <= byte.MaxValue => [(byte)((major << 5) | 24), (byte)value],
            <= ushort.MaxValue => [(byte)((major << 5) | 25), (byte)(value >> 8), (byte)value],
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }
}

/// <summary>TOTP（RFC 6238。HMAC-SHA1・30 秒・6 桁）の符号を作る（Identity の AuthenticatorTokenProvider と同じ）。</summary>
internal static class Totp
{
    public static string Code(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32(base32Key);
        var step = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(counter);
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = new List<byte>();
        int buffer = 0, count = 0;
        foreach (var c in input.TrimEnd('=').ToUpperInvariant())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c, StringComparison.Ordinal);
            count += 5;
            if (count >= 8)
            {
                bits.Add((byte)(buffer >> (count - 8)));
                count -= 8;
            }
        }

        return [.. bits];
    }
}
