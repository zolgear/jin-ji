using System.Security.Cryptography;
using JinJi.Application.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace JinJi.Host.Web;

/// <summary>
/// Bearer トークンの検証。外部の発行者の JWT（発行者・audience・署名の公開鍵は設定 <c>JinJi:Auth</c>、<see cref="AccessOptions"/>）と、
/// jin-ji が発行したアクセストークン（参照トークン。<see cref="Auth.AccessTokenAuthenticationHandler"/>）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>複数の発行者を受け付ける。audience と署名鍵は発行者ごとに照合する（ある発行者の鍵で別の発行者を名乗るトークンを通さない）</item>
/// <item>署名は非対称鍵（RS / PS / ES）だけ。共有鍵（HS）・署名なしは受け付けない。有効期限は必須</item>
/// <item>鍵は設定の PEM から読む（メタデータの取得はしない。外向きの通信を増やさないため。発行の方式と鍵の配り方は ADR-0004 で決める）</item>
/// <item>クレームの名前は変換しない（<c>MapInboundClaims = false</c>）。クレームから閲覧者を作るのは <see cref="ViewerContextFactory"/> だけ</item>
/// <item>開発用の固定の署名鍵は持たない。テストは鍵を生成して設定で差し込む</item>
/// </list>
/// </remarks>
internal static partial class TokenAuthentication
{
    /// <summary>受け付ける署名のアルゴリズム（非対称鍵だけ）。</summary>
    public static readonly IReadOnlyList<string> Algorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
    ];

    public static IServiceCollection AddJinJiTokenAuthentication(this IServiceCollection services, AccessOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var keysByIssuer = options.Issuers.ToDictionary(
            i => i.Issuer,
            i => (IReadOnlyList<SecurityKey>)[.. i.SigningKeys.Select(k => LoadKey(i.Issuer, k))],
            StringComparer.Ordinal);
        var audiencesByIssuer = options.Issuers.ToDictionary(
            i => i.Issuer,
            i => (IReadOnlySet<string>)i.Audiences.ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        services
            .AddAuthentication(authentication =>
            {
                // 認証は振り分けのスキーム（Cookie か Bearer か）を通す。401・403 は Bearer の形（WWW-Authenticate: Bearer）で返し、ログイン画面へ転送しない。
                // MCP のポートと jin-ji のアクセストークンの 401 は保護リソースメタデータ（resource_metadata）を示す
                authentication.DefaultScheme = Auth.AuthSchemes.Default;
                authentication.DefaultChallengeScheme = Auth.AuthSchemes.Challenge;
                authentication.DefaultForbidScheme = Auth.AuthSchemes.Challenge;
            })
            .AddPolicyScheme(Auth.AuthSchemes.Default, displayName: null, policy => policy.ForwardDefaultSelector = Auth.AuthSchemes.Select)
            .AddPolicyScheme(Auth.AuthSchemes.Challenge, displayName: null, policy => policy.ForwardDefaultSelector = Auth.AuthSchemes.SelectChallenge)
            .AddScheme<AuthenticationSchemeOptions, Auth.AccessTokenAuthenticationHandler>(Auth.AccessTokenAuthenticationHandler.SchemeName, displayName: null, _ => { })
            .AddJwtBearer(jwt =>
            {
                jwt.MapInboundClaims = false;
                jwt.RequireHttpsMetadata = true;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuers = [.. keysByIssuer.Keys],
                    ValidateAudience = true,
                    AudienceValidator = (audiences, token, _) =>
                        token is JsonWebToken jwtToken
                        && audiencesByIssuer.TryGetValue(jwtToken.Issuer, out var allowed)
                        && audiences.Any(allowed.Contains),
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (_, token, kid, _) =>
                        token is JsonWebToken jwtToken && keysByIssuer.TryGetValue(jwtToken.Issuer, out var keys)
                            ? keys.Where(k => kid is null || k.KeyId is null || string.Equals(k.KeyId, kid, StringComparison.Ordinal))
                            : [],
                    ValidAlgorithms = Algorithms,
                    ClockSkew = options.ClockSkew,
                    NameClaimType = TokenClaims.Subject,
                    RoleClaimType = TokenClaims.Roles,
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>PEM（SubjectPublicKeyInfo）の公開鍵を読む。RSA か EC。</summary>
    private static SecurityKey LoadKey(string issuer, SigningKeyOptions key)
    {
        try
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(key.PublicKeyPem);
            return new RsaSecurityKey(rsa) { KeyId = key.KeyId };
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            try
            {
                var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(key.PublicKeyPem);
                return new ECDsaSecurityKey(ecdsa) { KeyId = key.KeyId };
            }
            catch (Exception inner) when (inner is ArgumentException or CryptographicException)
            {
                throw new ArgumentException($"発行者 {issuer} の署名鍵（{key.KeyId ?? "kid なし"}）を RSA・EC の公開鍵の PEM として読めません。", nameof(key), inner);
            }
        }
    }
}
