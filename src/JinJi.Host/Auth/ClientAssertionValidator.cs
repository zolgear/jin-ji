using System.Security.Cryptography;
using JinJi.Application.Auth;
using JinJi.Host.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace JinJi.Host.Auth;

/// <summary>
/// private_key_jwt の client assertion（RFC 7523）の検証。Microsoft.IdentityModel（JwtBearer が既に連れてくるもの）を使う。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>署名は登録した公開鍵（RSA・EC）で、非対称鍵のアルゴリズムだけ（<see cref="TokenAuthentication.Algorithms"/>。HS・none は拒否）</item>
/// <item><c>iss</c> = <c>sub</c> = client_id、<c>aud</c> は発行者（issuer）の 1 つだけ（トークンエンドポイントの URL などは受けない）</item>
/// <item><c>exp</c> 必須で、今から上限（既定 5 分）以内。<c>jti</c> 必須（再利用の拒否は呼び出し側が DB で行う）</item>
/// </list>
/// </remarks>
internal sealed class ClientAssertionValidator(AuthorizationServerOptions options, TimeProvider timeProvider) : IClientAssertionValidator
{
    private readonly JsonWebTokenHandler _handler = new() { MapInboundClaims = false };

    public async Task<ClientAssertionCheck> ValidateAsync(string assertion, OAuthClient client, IReadOnlyList<OAuthClientKey> keys)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(keys);
        var securityKeys = new List<SecurityKey>();
        try
        {
            foreach (var key in keys)
            {
                securityKeys.Add(LoadKey(key));
            }

            var now = timeProvider.GetUtcNow();
            var result = await _handler.ValidateTokenAsync(assertion, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = client.ClientId,
                ValidateAudience = true,
                ValidAudience = options.IssuerValue,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = securityKeys,
                TryAllIssuerSigningKeys = true,
                ValidAlgorithms = TokenAuthentication.Algorithms,
                ClockSkew = options.ClockSkew,
                LifetimeValidator = (notBefore, expires, _, _) =>
                    expires is { } exp
                    && exp > now.UtcDateTime - options.ClockSkew
                    && exp <= now.UtcDateTime + options.ClientAssertionMaxLifetime + options.ClockSkew
                    && (notBefore is null || notBefore <= now.UtcDateTime + options.ClockSkew),
            });
            if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
            {
                return new ClientAssertionCheck(null, result.Exception?.GetType().Name ?? "invalid");
            }

            if (!string.Equals(jwt.Subject, client.ClientId, StringComparison.Ordinal))
            {
                return new ClientAssertionCheck(null, "sub が client_id と一致しません");
            }

            if (jwt.Audiences.Count() != 1)
            {
                return new ClientAssertionCheck(null, "aud は発行者の 1 つだけにしてください");
            }

            if (!jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Jti, out var jti) || string.IsNullOrEmpty(jti) || jti.Length > 256)
            {
                return new ClientAssertionCheck(null, "jti がありません");
            }

            if (jwt.TryGetPayloadValue<long>(JwtRegisteredClaimNames.Iat, out var iat)
                && DateTimeOffset.FromUnixTimeSeconds(iat) > now + options.ClockSkew)
            {
                return new ClientAssertionCheck(null, "iat が未来です");
            }

            return new ClientAssertionCheck(new ClientAssertion(jti, new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero)), null);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException or SecurityTokenException)
        {
            return new ClientAssertionCheck(null, ex.GetType().Name);
        }
    }

    public string? ReadClientId(string assertion)
    {
        try
        {
            return _handler.CanReadToken(assertion) && new JsonWebToken(assertion).Issuer is { Length: > 0 and <= 2048 } issuer ? issuer : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static SecurityKey LoadKey(OAuthClientKey key)
    {
        try
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(key.PublicKeyPem);
            return new RsaSecurityKey(rsa) { KeyId = key.KeyId };
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(key.PublicKeyPem);
            return new ECDsaSecurityKey(ecdsa) { KeyId = key.KeyId };
        }
    }
}
