using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JinJi.Api;
using JinJi.Application.Access;
using JinJi.Application.Auth;
using JinJi.Host.Web;
using JinJi.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace JinJi.Host.Auth;

/// <summary>メタデータ（RFC 8414 の認可サーバー・RFC 9728 の保護リソース）の URL と中身。</summary>
internal static class OAuthMetadata
{
    public const string AuthorizationServerPath = "/.well-known/oauth-authorization-server";
    public const string ProtectedResourcePath = "/.well-known/oauth-protected-resource";

    /// <summary>
    /// 保護リソースメタデータの URL（RFC 9728 §3.1: resource のホストの直後に well-known を挟む。例 https://h/mcp → https://h/.well-known/oauth-protected-resource/mcp）。
    /// resource が未設定なら null。
    /// </summary>
    public static string? ProtectedResourceMetadataUrl(AuthorizationServerOptions server, OAuthResource resource)
    {
        if (server.ResourceValue(resource) is not { } value || !Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        return uri.GetLeftPart(UriPartial.Authority) + ProtectedResourcePath + path;
    }

    /// <summary>保護リソースメタデータを返すパス（resource のパスを足したものと、足さないもの）。</summary>
    public static IEnumerable<string> ProtectedResourcePaths(AuthorizationServerOptions server, OAuthResource resource)
    {
        yield return ProtectedResourcePath;
        if (server.ResourceValue(resource) is { } value && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.AbsolutePath.TrimEnd('/') is { Length: > 0 } path)
        {
            yield return ProtectedResourcePath + path;
        }
    }

    public static Dictionary<string, object> AuthorizationServer(AuthorizationServerOptions server)
    {
        var issuer = server.IssuerValue;
        return new Dictionary<string, object>
        {
            ["issuer"] = issuer,
            ["authorization_endpoint"] = issuer + OAuthEndpoints.AuthorizePath,
            ["token_endpoint"] = issuer + OAuthEndpoints.TokenPath,
            ["revocation_endpoint"] = issuer + OAuthEndpoints.RevokePath,
            ["response_types_supported"] = new[] { "code" },
            ["response_modes_supported"] = new[] { "query" },
            ["grant_types_supported"] = new[] { OAuthGrantTypes.AuthorizationCode, OAuthGrantTypes.RefreshToken, OAuthGrantTypes.ClientCredentials },
            ["code_challenge_methods_supported"] = new[] { "S256" },
            // client_secret は機密クライアントの移行用の例外なので広告しない（受け付けはする）
            ["token_endpoint_auth_methods_supported"] = new[] { "none", "private_key_jwt" },
            ["token_endpoint_auth_signing_alg_values_supported"] = TokenAuthentication.Algorithms,
            ["revocation_endpoint_auth_methods_supported"] = new[] { "none", "private_key_jwt" },
            ["scopes_supported"] = new[] { OAuthScopes.Read, OAuthScopes.Propose },
            ["client_id_metadata_document_supported"] = server.Cimd.Enabled,
            ["authorization_response_iss_parameter_supported"] = true,
        };
    }

    public static Dictionary<string, object> ProtectedResource(AuthorizationServerOptions server, OAuthResource resource) => new()
    {
        ["resource"] = server.ResourceValue(resource)!,
        ["authorization_servers"] = new[] { server.IssuerValue },
        ["scopes_supported"] = new[] { OAuthScopes.Read, OAuthScopes.Propose },
        ["bearer_methods_supported"] = new[] { "header" },
        ["resource_name"] = resource == OAuthResource.Mcp ? "jin-ji MCP" : "jin-ji API",
    };
}

/// <summary>
/// 認可サーバーのエンドポイント（API のポート。ADR-0004 の段階 B）と、保護リソースメタデータ（API・MCP のポート）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>GET /.well-known/oauth-authorization-server</c>（RFC 8414）、<c>GET /.well-known/oauth-protected-resource[/パス]</c>（RFC 9728）</item>
/// <item><c>GET /oauth/authorize</c>: 要求を検査して保存し、同意画面（SPA の <c>/consent?request=</c>）へ転送する。未ログインならログイン画面（<c>/login?return_url=</c>）へ。
/// 戻り先は短い要求 ID だけなので、authorize の URL が長くても戻り先の上限（2048 文字）に収まる</item>
/// <item><c>GET /oauth/requests/{id}</c>・<c>POST /oauth/requests/{id}/decision</c>: 同意画面の JSON API（UI のセッションの人だけ。POST は CSRF のトークンが要る）</item>
/// <item><c>POST /oauth/token</c>・<c>POST /oauth/revoke</c>: Cookie を読まない（<see cref="NoSessionMetadata"/>）。接続元 × client_id のレート制限と、失敗の急増の不正検知</item>
/// </list>
/// </remarks>
internal static partial class OAuthEndpoints
{
    public const string AuthorizePath = "/oauth/authorize";
    public const string TokenPath = "/oauth/token";
    public const string RevokePath = "/oauth/revoke";

    /// <summary>SPA の同意画面とログイン画面のパス。</summary>
    public const string ConsentPagePath = "/consent";
    public const string LoginPagePath = "/login";

    public const string InvalidAuthorizationRequestType = "urn:jinji:problem:invalid-authorization-request";

    private const int MaxFormBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapJinJiOAuth(this IEndpointRouteBuilder endpoints, AuthorizationServerOptions server)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(server);
        var kind = new RequestKindMetadata(RequestKind.Other, false);
        endpoints.MapGet(OAuthMetadata.AuthorizationServerPath, () => Results.Json(OAuthMetadata.AuthorizationServer(server)))
            .AllowAnonymous().ExcludeFromDescription().WithMetadata(kind, NoSessionMetadata.Instance);
        endpoints.MapJinJiProtectedResourceMetadata(server, OAuthResource.Api);

        var oauth = endpoints.MapGroup("/oauth").WithTags("oauth").WithMetadata(kind);
        oauth.MapGet("/authorize", AuthorizeAsync).AllowAnonymous().ExcludeFromDescription();
        oauth.MapGet("/requests/{requestId}", GetRequestAsync).RequireAuthorization()
            .WithSummary("同意画面に出す認可の要求（UI でログインした人だけ）").Produces<ConsentResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        oauth.MapPost("/requests/{requestId}/decision", DecideAsync).RequireAuthorization()
            .WithSummary("認可の要求に同意・拒否する（redirect_to へ遷移する）").Produces<ConsentDecisionResponse>().ProducesProblem(StatusCodes.Status404NotFound);
        oauth.MapGet("/delegations", ListDelegationsAsync).RequireAuthorization()
            .WithSummary("本人が AI エージェント（MCP クライアントなど）に許可している委任の一覧（UI でログインした本人だけ）").Produces<IReadOnlyList<DelegationResponse>>();
        oauth.MapPost("/delegations/{familyId}/revoke", RevokeDelegationAsync).RequireAuthorization()
            .WithSummary("本人の委任を取り消す（そのクライアントのトークンは次の要求から使えない）").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
        oauth.MapPost("/token", TokenAsync).AllowAnonymous().ExcludeFromDescription().WithMetadata(NoSessionMetadata.Instance).DisableAntiforgery();
        oauth.MapPost("/revoke", RevokeAsync).AllowAnonymous().ExcludeFromDescription().WithMetadata(NoSessionMetadata.Instance).DisableAntiforgery();
        return endpoints;
    }

    /// <summary>保護リソースメタデータ（RFC 9728）。MCP のポートは MCP の resource、API のポートは REST API の resource。</summary>
    public static IEndpointRouteBuilder MapJinJiProtectedResourceMetadata(this IEndpointRouteBuilder endpoints, AuthorizationServerOptions server, OAuthResource resource)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(server);
        if (!server.Enabled || server.ResourceValue(resource) is null)
        {
            return endpoints;
        }

        foreach (var path in OAuthMetadata.ProtectedResourcePaths(server, resource).Distinct(StringComparer.Ordinal))
        {
            endpoints.MapGet(path, () => Results.Json(OAuthMetadata.ProtectedResource(server, resource)))
                .AllowAnonymous().ExcludeFromDescription().WithMetadata(new RequestKindMetadata(RequestKind.Other, false), NoSessionMetadata.Instance);
        }

        return endpoints;
    }

    // ---- 認可の要求・同意 ----------------------------------------------------------------------------------------

    private static async Task<IResult> AuthorizeAsync(HttpContext http, AuthorizationServer server)
    {
        NoStore(http);
        var query = http.Request.Query;
        var request = new AuthorizeRequest(
            query["response_type"],
            query["client_id"],
            query["redirect_uri"],
            query["scope"],
            query["state"],
            query["code_challenge"],
            query["code_challenge_method"],
            [.. query["resource"].Where(r => !string.IsNullOrEmpty(r)).Select(r => r!)],
            query.Any(kv => kv.Value.Count > 1 && kv.Key != "resource"));
        var result = await server.AuthorizeAsync(request, http.TraceIdentifier, http.RequestAborted);
        switch (result.Outcome)
        {
            case AuthorizeOutcome.RedirectError:
                return Results.Redirect(result.RedirectUrl!);
            case AuthorizeOutcome.ShowError:
                Problems.Observe(http).RejectReason = RejectReasons.BadRequest;
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "認可の要求が不正です",
                    detail: result.Description,
                    type: InvalidAuthorizationRequestType,
                    extensions: new Dictionary<string, object?> { ["error"] = result.Error });
        }

        var consent = $"{ConsentPagePath}?request={Uri.EscapeDataString(result.Handle!)}";
        return SessionPerson(http) is null
            ? Results.Redirect($"{LoginPagePath}?return_url={Uri.EscapeDataString(consent)}")
            : Results.Redirect(consent);
    }

    private static async Task<IResult> GetRequestAsync(string requestId, HttpContext http, AuthorizationServer server)
    {
        NoStore(http);
        if (SessionPerson(http) is null)
        {
            return Problems.Forbidden(http, "同意は UI でログインした本人だけが行えます。");
        }

        if (await server.GetConsentAsync(requestId, http.RequestAborted) is not { } view)
        {
            return Problems.NotFound(http);
        }

        return Results.Ok(ConsentResponse.From(view));
    }

    private static async Task<IResult> DecideAsync(string requestId, ConsentDecisionRequest request, HttpContext http, AuthorizationServer server)
    {
        NoStore(http);
        if (SessionPerson(http) is not { } person)
        {
            return Problems.Forbidden(http, "同意は UI でログインした本人だけが行えます。");
        }

        if (request.Approve is not { } approve)
        {
            return Problems.BadRequest(http, "approve（true / false）を指定してください。");
        }

        return await server.DecideAsync(requestId, person, approve, http.TraceIdentifier, http.RequestAborted) is { } redirect
            ? Results.Ok(new ConsentDecisionResponse(redirect))
            : Problems.NotFound(http);
    }

    private static async Task<IResult> ListDelegationsAsync(HttpContext http, DelegationService delegations)
    {
        NoStore(http);
        if (SessionPerson(http) is not { } person)
        {
            return Problems.Forbidden(http, "委任の一覧は UI でログインした本人だけが見られます。");
        }

        var rows = await delegations.ListAsync(person, http.RequestAborted);
        return Results.Ok(rows.Select(DelegationResponse.From).ToList());
    }

    private static async Task<IResult> RevokeDelegationAsync(string familyId, HttpContext http, DelegationService delegations)
    {
        NoStore(http);
        if (SessionPerson(http) is not { } person)
        {
            return Problems.Forbidden(http, "委任の取り消しは UI でログインした本人だけが行えます。");
        }

        return Guid.TryParseExact(familyId, "D", out var id) && await delegations.RevokeAsync(person, id, http.TraceIdentifier, http.RequestAborted)
            ? Results.NoContent()
            : Problems.NotFound(http);
    }

    /// <summary>UI のセッションの人（緊急用アカウント・トークンは除く）。</summary>
    private static Guid? SessionPerson(HttpContext http) =>
        SessionClaims.IsSession(http.User) && !SessionClaims.IsBreakGlass(http.User)
        && http.Features.Get<AccessContext>()?.Viewer is { Channel: Channel.Ui, Actor: ActorType.User, PersonId: { } person }
            ? person
            : null;

    // ---- トークン ------------------------------------------------------------------------------------------------

    private static async Task<IResult> TokenAsync(HttpContext http, AuthorizationServer server, TokenEndpointGuard guard)
    {
        NoStore(http);
        if (await ReadFormAsync(http) is not { } form)
        {
            return OAuthError(http, StatusCodes.Status400BadRequest, "invalid_request", "application/x-www-form-urlencoded の本文（16 KiB 以内）で送ってください。");
        }

        if (!TryReadBasic(http, out var basicId, out var basicSecret))
        {
            return OAuthError(http, StatusCodes.Status401Unauthorized, "invalid_client", "Authorization ヘッダーを読めません。", basic: true);
        }

        var credentials = new ClientCredentialsInput(
            form["client_id"].FirstOrDefault(), form["client_secret"].FirstOrDefault(), basicId, basicSecret,
            form["client_assertion_type"].FirstOrDefault(), form["client_assertion"].FirstOrDefault());
        if (guard.Check(http, BucketClientId(credentials, server)) is { } rejected)
        {
            return rejected;
        }

        var request = new TokenRequest(
            form["grant_type"].FirstOrDefault(),
            form["code"].FirstOrDefault(),
            form["redirect_uri"].FirstOrDefault(),
            form["code_verifier"].FirstOrDefault(),
            form["refresh_token"].FirstOrDefault(),
            form["scope"].FirstOrDefault(),
            [.. form["resource"].Where(r => !string.IsNullOrEmpty(r)).Select(r => r!)],
            credentials,
            form.Any(kv => kv.Value.Count > 1 && kv.Key != "resource"));
        var result = await server.TokenAsync(request, http.TraceIdentifier, http.RequestAborted);
        await guard.ObserveAsync(http, result);
        if (!result.Succeeded)
        {
            return OAuthError(http, result.Status, result.Error!, result.Description, result.BasicChallenge);
        }

        var tokens = result.Tokens!;
        var body = new Dictionary<string, object>
        {
            ["access_token"] = tokens.AccessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = tokens.ExpiresIn,
            ["scope"] = tokens.Scope,
        };
        if (tokens.RefreshToken is { } refresh)
        {
            body["refresh_token"] = refresh;
        }

        return Results.Json(body);
    }

    private static async Task<IResult> RevokeAsync(HttpContext http, AuthorizationServer server, TokenEndpointGuard guard)
    {
        NoStore(http);
        if (await ReadFormAsync(http) is not { } form)
        {
            return OAuthError(http, StatusCodes.Status400BadRequest, "invalid_request", "application/x-www-form-urlencoded の本文（16 KiB 以内）で送ってください。");
        }

        if (!TryReadBasic(http, out var basicId, out var basicSecret))
        {
            return OAuthError(http, StatusCodes.Status401Unauthorized, "invalid_client", "Authorization ヘッダーを読めません。", basic: true);
        }

        var credentials = new ClientCredentialsInput(
            form["client_id"].FirstOrDefault(), form["client_secret"].FirstOrDefault(), basicId, basicSecret,
            form["client_assertion_type"].FirstOrDefault(), form["client_assertion"].FirstOrDefault());
        if (guard.Check(http, BucketClientId(credentials, server)) is { } rejected)
        {
            return rejected;
        }

        var result = await server.RevokeAsync(credentials, form["token"].FirstOrDefault(), http.TraceIdentifier, http.RequestAborted);
        await guard.ObserveAsync(http, result);
        return result.Succeeded ? Results.Ok() : OAuthError(http, result.Status, result.Error!, result.Description, result.BasicChallenge);
    }

    /// <summary>レート制限の区分けの client_id（認証の前なので、要求が名乗る値。assertion は署名を確かめずに iss を読む）。</summary>
    private static string? BucketClientId(ClientCredentialsInput credentials, AuthorizationServer server) =>
        credentials.ClientId ?? credentials.BasicClientId
        ?? (credentials.Assertion is { } assertion ? server.AssertionClientId(assertion) : null);

    private static async Task<IFormCollection?> ReadFormAsync(HttpContext http)
    {
        if (!http.Request.HasFormContentType
            || !Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(http.Request.ContentType, out var type)
            || !string.Equals(type.MediaType.Value, "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || http.Request.ContentLength is not (>= 0 and <= MaxFormBytes))
        {
            return null;
        }

        try
        {
            return await http.Request.ReadFormAsync(http.RequestAborted);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Authorization: Basic（client_secret_basic。RFC 6749 §2.3.1: 利用者名とパスワードは form-urlencoded してから Base64）。
    /// Basic でなければ値なしで true。形が不正なら false。
    /// </summary>
    private static bool TryReadBasic(HttpContext http, out string? clientId, out string? secret)
    {
        clientId = null;
        secret = null;
        if (!http.Request.Headers.TryGetValue(HeaderNames.Authorization, out var header) || header.Count == 0)
        {
            return true;
        }

        if (header.Count != 1 || !AuthenticationHeaderValue.TryParse(header[0], out var parsed) || !string.Equals(parsed.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
        {
            // Basic 以外の Authorization ヘッダー（Bearer など）はトークンエンドポイントでは使わない
            return header.Count == 1 && !header[0]!.StartsWith("Basic", StringComparison.OrdinalIgnoreCase);
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parsed.Parameter ?? string.Empty));
            var colon = decoded.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                return false;
            }

            clientId = Uri.UnescapeDataString(decoded[..colon].Replace('+', ' '));
            secret = Uri.UnescapeDataString(decoded[(colon + 1)..].Replace('+', ' '));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>RFC 6749 §5.2 の誤りの応答（JSON の error・error_description）。</summary>
    public static IResult OAuthError(HttpContext http, int status, string error, string? description, bool basic = false)
    {
        Problems.Observe(http).RejectReason = status switch
        {
            StatusCodes.Status401Unauthorized => RejectReasons.Unauthenticated,
            StatusCodes.Status403Forbidden => RejectReasons.Forbidden,
            StatusCodes.Status429TooManyRequests => RejectReasons.RateLimited,
            _ => RejectReasons.BadRequest,
        };
        if (status == StatusCodes.Status401Unauthorized && basic)
        {
            http.Response.Headers.Append(HeaderNames.WWWAuthenticate, "Basic realm=\"jin-ji\"");
        }

        var body = new Dictionary<string, string> { ["error"] = error };
        if (description is not null)
        {
            body["error_description"] = description;
        }

        return Results.Json(body, statusCode: status);
    }

    private static void NoStore(HttpContext http)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
    }
}

/// <summary>同意・拒否。</summary>
internal sealed record ConsentDecisionRequest(bool? Approve);

/// <summary>本人の委任（トークンの系列）。取り消しの単位は id。</summary>
internal sealed record DelegationResponse(
    Guid Id,
    string ClientId,
    string? ClientName,
    [property: OpenApiEnum(typeof(OAuthClientRegistration))] string Registration,
    [property: OpenApiEnum("api", "mcp")] string Resource,
    IReadOnlyList<string> Scopes,
    DateTimeOffset GrantedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset ExpiresAt)
{
    public static DelegationResponse From(Delegation row) => new(
        row.FamilyId, row.ClientId, row.ClientName, OAuthCodes.ToCode(row.Registration), OAuthCodes.ToCode(row.Resource), row.Scopes,
        row.GrantedAt, row.LastUsedAt, row.ExpiresAt);
}

/// <summary>同意・拒否の後に遷移する先（クライアントの redirect_uri。コードか access_denied と、state・iss を含む）。</summary>
internal sealed record ConsentDecisionResponse(string RedirectTo);

/// <summary>同意画面に出す内容。</summary>
/// <param name="LoopbackOnly">クライアントがループバックの redirect_uri しか持たない（ローカルで動くアプリ。画面で注意を促す）。</param>
internal sealed record ConsentResponse(
    ConsentClientResponse Client,
    string RedirectUri,
    string RedirectHost,
    [property: OpenApiEnum("api", "mcp")] string Resource,
    string ResourceUri,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt)
{
    public static ConsentResponse From(ConsentView view) => new(
        new ConsentClientResponse(
            view.Client.ClientId,
            view.Client.DisplayName ?? view.Client.ClientId,
            OAuthCodes.ToCode(view.Client.Registration),
            view.Client.HasOnlyLoopbackRedirects,
            view.Client.Fields,
            view.Client.McpTools),
        view.RedirectUri,
        view.RedirectHost,
        OAuthCodes.ToCode(view.Resource),
        view.ResourceUri,
        view.Scopes,
        view.ExpiresAt);
}

/// <summary>同意画面に出すクライアント。</summary>
/// <param name="Fields">クライアントに渡してよい項目の上限（<c>*</c> は全項目）。実際に渡るのは、同意した人に見える項目との重なりだけ。</param>
/// <param name="McpTools">MCP ツールの許可リスト（空は何も許さない、<c>*</c> はすべて）。</param>
internal sealed record ConsentClientResponse(
    string ClientId,
    string Name,
    [property: OpenApiEnum("registered", "cimd")] string Registration,
    bool LoopbackOnly,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> McpTools);
