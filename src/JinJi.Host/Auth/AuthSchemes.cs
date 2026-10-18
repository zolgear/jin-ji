using JinJi.Application.Access;
using JinJi.Application.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Net.Http.Headers;

namespace JinJi.Host.Auth;

/// <summary>
/// 認証スキームの名前と、Cookie（UI の BFF のセッション）と Bearer（トークン）の振り分け。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>MCP のポートは常に jin-ji のアクセストークン（参照トークン）だけ。Cookie も外部の発行者の JWT（Entra のアプリのトークンなど）も受け付けない（ADR-0004「構成」）</item>
/// <item>API のポートは <c>Authorization</c> ヘッダーがあれば Bearer（jin-ji のアクセストークンの接頭辞なら参照トークン、それ以外は JWT）、
/// 無ければセッションの Cookie（人のログインを有効にした役割 api / all だけ）</item>
/// <item>Bearer の要求に Cookie が付いていても Cookie は読まない（同じ要求で 2 つの主体を混ぜない）。
/// トークンエンドポイントなど Cookie を読まないエンドポイント（<see cref="NoSessionMetadata"/>）も Cookie を読まない</item>
/// </list>
/// </remarks>
internal static class AuthSchemes
{
    /// <summary>既定のスキーム（振り分け）。</summary>
    public const string Default = "jinji";

    /// <summary>UI のセッションの Cookie のスキーム（<see cref="SessionClaims.AuthenticationType"/> と同じ名前）。</summary>
    public const string Session = SessionClaims.AuthenticationType;

    /// <summary>401・403 の応答のスキーム（振り分け）。</summary>
    public const string Challenge = "jinji.challenge";

    public static string Select(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var surface = http.RequestServices.GetRequiredService<SurfacePortMap>().Resolve(http.Connection.LocalPort);
        if (surface == Surface.Mcp || HasAccessToken(http))
        {
            return AccessTokenAuthenticationHandler.SchemeName;
        }

        if (http.RequestServices.GetService<HumanLoginMarker>() is null
            || http.Request.Headers.ContainsKey(HeaderNames.Authorization)
            || surface != Surface.Api
            || http.GetEndpoint()?.Metadata.GetMetadata<NoSessionMetadata>() is not null)
        {
            return JwtBearerDefaults.AuthenticationScheme;
        }

        return Session;
    }

    /// <summary>401・403 を返すスキーム。MCP のポートと jin-ji のアクセストークンは保護リソースメタデータを示す形、それ以外は従来の Bearer（JWT）。</summary>
    public static string SelectChallenge(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.RequestServices.GetRequiredService<SurfacePortMap>().Resolve(http.Connection.LocalPort) == Surface.Mcp || HasAccessToken(http)
            ? AccessTokenAuthenticationHandler.SchemeName
            : JwtBearerDefaults.AuthenticationScheme;
    }

    /// <summary>Authorization ヘッダーが jin-ji のアクセストークン（接頭辞 jjat_）の Bearer か（形の検査はハンドラが行う）。</summary>
    private static bool HasAccessToken(HttpContext http) =>
        http.Request.Headers.TryGetValue(HeaderNames.Authorization, out var header)
        && header.Count == 1
        && header[0] is { } value
        && value.StartsWith("Bearer " + OAuthTokenPrefixes.AccessToken, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Cookie（UI のセッション）を読まないエンドポイントのメタデータ（トークンエンドポイント・メタデータ。ADR-0004）。</summary>
internal sealed class NoSessionMetadata
{
    public static NoSessionMetadata Instance { get; } = new();
}

/// <summary>人のログイン（Cookie のセッション・OIDC・ローカルアカウント）を登録したことの印（役割 api / all）。</summary>
internal sealed class HumanLoginMarker;

/// <summary>
/// 公開 URL（<c>JinJi:Auth:Login:PublicOrigin</c>）が https なら、API のポートの要求を https として扱う。
/// </summary>
/// <remarks>
/// jin-ji は HTTP で待ち受け、TLS はリバースプロキシで終端する（§7）。Secure の Cookie（<c>__Host-</c>）と Antiforgery は https の要求を前提にするため、
/// 要求のスキームを設定から決める。X-Forwarded-Proto などのヘッダーは利用者が偽装できるので信用しない（Host ヘッダーを信用しないのと同じ考え方）。
/// 公開 URL が未設定なら何もしない（https で直接受けた要求だけで UI のログインが動く）。
/// </remarks>
internal sealed class PublicOriginMiddleware(RequestDelegate next, SurfacePortMap ports, JinJi.Application.Auth.LoginOptions options)
{
    private readonly bool _https = options.PublicOriginUri()?.Scheme == Uri.UriSchemeHttps;

    public Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        if (_https && ports.Resolve(http.Connection.LocalPort) == Surface.Api)
        {
            http.Request.Scheme = Uri.UriSchemeHttps;
        }

        return next(http);
    }
}
