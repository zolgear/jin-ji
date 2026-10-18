using System.Security.Claims;
using System.Text.Encodings.Web;
using JinJi.Application.Access;
using JinJi.Application.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace JinJi.Host.Auth;

/// <summary>検証を通った jin-ji のアクセストークン（入口の共通処理が閲覧者を作るのに使う）。</summary>
internal sealed record AccessTokenFeature(ValidatedAccessToken Token);

/// <summary>
/// jin-ji が発行したアクセストークン（参照トークン）の認証ハンドラ。API・MCP の共通（<see cref="AccessTokenValidator"/>）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>トークンは発行時の resource に束縛する: API のポートでは REST API 用、MCP のポートでは MCP 用だけを通す（取り違えは 401 invalid_token）</item>
/// <item>401 の <c>WWW-Authenticate</c> には保護リソースメタデータの URL（<c>resource_metadata</c>。RFC 9728）と <c>scope</c> を載せる（MCP 仕様）</item>
/// <item>主体のクレームは最小限（sub・client_id・scope）。閲覧者は入口の共通処理が <see cref="AccessTokenFeature"/> から作る</item>
/// </list>
/// </remarks>
internal sealed class AccessTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AuthorizationServerOptions server,
    SurfacePortMap ports,
    IServiceProvider services)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "jinji.token";

    /// <summary>主体の認証の種類（ClaimsIdentity.AuthenticationType）。</summary>
    public const string AuthenticationType = "jinji.token";

    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderNames.Authorization, out var header) || header.Count != 1
            || header[0] is not { } value || !value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = value[BearerPrefix.Length..].Trim();
        if (!server.Enabled || services.GetService<AccessTokenValidator>() is not { } validator)
        {
            return AuthenticateResult.Fail("jin-ji のトークン発行は無効です。");
        }

        if (await validator.ValidateAsync(token, Context.RequestAborted) is not { } validated)
        {
            return AuthenticateResult.Fail("トークンが無効です。");
        }

        // 発行時の resource（REST API / MCP）と、要求を受けたポートの公開面が一致するものだけ
        var expected = ports.Resolve(Context.Connection.LocalPort) == Surface.Mcp ? OAuthResource.Mcp : OAuthResource.Api;
        if (validated.Resource != expected)
        {
            return AuthenticateResult.Fail("このトークンはこのリソース宛てではありません。");
        }

        Context.Features.Set(new AccessTokenFeature(validated));
        var claims = new List<Claim>
        {
            new(TokenClaims.Subject, validated.PersonId?.ToString("D") ?? validated.ClientId),
            new(TokenClaims.ClientId, validated.ClientId),
            new("scope", OAuthScopes.Join(validated.Scopes)),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, TokenClaims.Subject, TokenClaims.Roles));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        var failed = Request.Headers.ContainsKey(HeaderNames.Authorization);
        Response.Headers.Append(HeaderNames.WWWAuthenticate, Challenge(failed ? "invalid_token" : null, OAuthScopes.Read));
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    /// <summary><c>WWW-Authenticate: Bearer</c> の値（resource_metadata・scope・error）。</summary>
    private string Challenge(string? error, string scope)
    {
        var surface = ports.Resolve(Context.Connection.LocalPort) == Surface.Mcp ? OAuthResource.Mcp : OAuthResource.Api;
        return ChallengeValue(server, surface, error, scope);
    }

    /// <summary><c>WWW-Authenticate: Bearer</c> の値を組み立てる（スコープ不足の 403 でも使う）。</summary>
    public static string ChallengeValue(AuthorizationServerOptions server, OAuthResource resource, string? error, string scope)
    {
        var parts = new List<string>();
        if (error is not null)
        {
            parts.Add($"error=\"{error}\"");
        }

        if (server.Enabled && OAuthMetadata.ProtectedResourceMetadataUrl(server, resource) is { } metadata)
        {
            parts.Add($"resource_metadata=\"{metadata}\"");
        }

        parts.Add($"scope=\"{scope}\"");
        return "Bearer " + string.Join(", ", parts);
    }
}
