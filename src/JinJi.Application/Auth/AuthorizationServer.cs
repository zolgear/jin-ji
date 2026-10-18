using System.Security.Cryptography;
using System.Text;
using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>/oauth/authorize の要求（クエリのまま。検査はこちらで行う）。</summary>
public sealed record AuthorizeRequest(
    string? ResponseType,
    string? ClientId,
    string? RedirectUri,
    string? Scope,
    string? State,
    string? CodeChallenge,
    string? CodeChallengeMethod,
    IReadOnlyList<string> Resources,
    bool HasDuplicateParameters);

/// <summary>/oauth/authorize の結果の種類。</summary>
public enum AuthorizeOutcome
{
    /// <summary>redirect_uri を確かめられないので転送せず、利用者に誤りを見せる。</summary>
    ShowError,

    /// <summary>redirect_uri に誤りを返す（error・state・iss）。</summary>
    RedirectError,

    /// <summary>要求を保存した。同意画面（未ログインならログイン）へ進む。</summary>
    Continue,
}

/// <param name="Error">OAuth の error コード（ShowError・RedirectError）。</param>
/// <param name="Description">利用者・クライアントに見せる説明（理由の詳細は含めない）。</param>
/// <param name="RedirectUrl">RedirectError の転送先。</param>
/// <param name="Handle">Continue の要求の ID（同意画面の URL に入れる）。</param>
public sealed record AuthorizeResult(AuthorizeOutcome Outcome, string? Error, string? Description, string? RedirectUrl, string? Handle);

/// <summary>同意画面に出す内容。</summary>
public sealed record ConsentView(
    OAuthClient Client,
    string RedirectUri,
    string RedirectHost,
    OAuthResource Resource,
    string ResourceUri,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt);

/// <summary>トークンエンドポイントでのクライアントの認証の材料（どれを使ったかはこちらで判定する）。</summary>
/// <param name="BasicClientId">Authorization: Basic の利用者名（URL デコード済み）。</param>
public sealed record ClientCredentialsInput(
    string? ClientId,
    string? ClientSecret,
    string? BasicClientId,
    string? BasicSecret,
    string? AssertionType,
    string? Assertion);

/// <summary>/oauth/token の要求。</summary>
public sealed record TokenRequest(
    string? GrantType,
    string? Code,
    string? RedirectUri,
    string? CodeVerifier,
    string? RefreshToken,
    string? Scope,
    IReadOnlyList<string> Resources,
    ClientCredentialsInput Client,
    bool HasDuplicateParameters);

/// <summary>トークンの応答（RFC 6749 §5.1）。</summary>
public sealed record IssuedTokens(string AccessToken, int ExpiresIn, string? RefreshToken, string Scope);

/// <summary>/oauth/token・/oauth/revoke の結果。</summary>
/// <param name="Status">HTTP の状態コード。</param>
/// <param name="Error">OAuth の error コード（失敗のとき）。</param>
/// <param name="BasicChallenge">Authorization: Basic での認証の失敗（401 に WWW-Authenticate: Basic を付ける）。</param>
/// <param name="ClientId">要求のクライアント（レート制限・不正検知の区分け）。</param>
/// <param name="Reuse">リフレッシュトークン・認可コードの再利用を検知した（不正検知に渡す）。</param>
public sealed record TokenResult(
    int Status,
    IssuedTokens? Tokens,
    string? Error,
    string? Description,
    bool BasicChallenge,
    OAuthClient? Client,
    (Guid ClientRef, Guid PersonId)? Reuse)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// 自前の最小の認可サーバー（ADR-0004 の段階 B）。認可コード + PKCE（S256 だけ）、リフレッシュトークン（MCP だけ。回転と再利用の検知）、
/// client credentials（private_key_jwt。移行用の client_secret）、CIMD と事前登録。DCR・implicit・ROPC・id_token は持たない。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>redirect_uri は登録したものと完全一致（ループバックの http だけポートを問わない）。client_id か redirect_uri を確かめられない誤りは転送しない</item>
/// <item>resource（RFC 8707）は認可の要求で必須。REST API と MCP の 2 つで、トークンを発行時の resource に束縛する</item>
/// <item>認可コードは 60 秒・単回。client・redirect_uri・resource・PKCE・スコープ・人に束縛する。使用済みのコードが再び使われたら、そのコードから発行した系列を失効する</item>
/// <item>認可の応答には iss（RFC 9207）を付ける</item>
/// <item>閲覧者の実効権限は「委任元ユーザーの権限 ∩ スコープ ∩ クライアントの上限」。ロールはトークンに持たせず、要求ごとに読む</item>
/// </list>
/// </remarks>
public sealed class AuthorizationServer
{
    /// <summary>client assertion の種類（RFC 7523）。</summary>
    public const string JwtBearerAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    private readonly IOAuthStore _store;
    private readonly IAuthStore _authStore;
    private readonly ClientResolver _clients;
    private readonly IClientAssertionValidator _assertions;
    private readonly AccessTokenValidator _validator;
    private readonly AuthAuditor _audit;
    private readonly AuthorizationServerOptions _options;
    private readonly TimeProvider _timeProvider;

    public AuthorizationServer(
        IOAuthStore store,
        IAuthStore authStore,
        ClientResolver clients,
        IClientAssertionValidator assertions,
        AccessTokenValidator validator,
        AuthAuditor audit,
        AuthorizationServerOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(authStore);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(assertions);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _authStore = authStore;
        _clients = clients;
        _assertions = assertions;
        _validator = validator;
        _audit = audit;
        _options = options;
        _timeProvider = timeProvider;
    }

    public AuthorizationServerOptions Options => _options;

    /// <summary>client assertion の iss（署名を確かめずに読む。レート制限の区分けだけに使う）。</summary>
    public string? AssertionClientId(string assertion) => _assertions.ReadClientId(assertion);

    // ---- 認可の要求 ----------------------------------------------------------------------------------------------

    /// <summary>/oauth/authorize の要求を検査し、通れば保存して同意画面へ進める。</summary>
    public async Task<AuthorizeResult> AuthorizeAsync(AuthorizeRequest request, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrEmpty(request.ClientId) || request.ClientId.Length > 2048)
        {
            return Show("invalid_request", "client_id がありません。");
        }

        var resolution = await _clients.ResolveAsync(request.ClientId, requestId, cancellationToken);
        if (resolution.Client is not { } client)
        {
            return resolution.Error switch
            {
                ClientResolutionError.PendingApproval => Show("unauthorized_client", "このクライアントは管理者の承認待ちです。承認されるまで利用できません。"),
                ClientResolutionError.MetadataInvalid => Show("invalid_client", "クライアントのメタデータを取得・確認できませんでした。"),
                _ => Show("invalid_client", "このクライアントは登録されていないか、利用できません。"),
            };
        }

        // redirect_uri を確かめる前の誤りは転送しない（オープンリダイレクトの対策）
        if (string.IsNullOrEmpty(request.RedirectUri) || !client.Allows(OAuthGrantTypes.AuthorizationCode)
            || !OAuthRedirectUris.IsAcceptable(request.RedirectUri) || !OAuthRedirectUris.Matches(client.RedirectUris, request.RedirectUri))
        {
            return Show("invalid_request", "redirect_uri がクライアントの登録と一致しません。");
        }

        var redirect = request.RedirectUri;
        if (request.HasDuplicateParameters)
        {
            return RedirectError(redirect, request.State, "invalid_request", "同じパラメーターが複数あります。");
        }

        if (request.ResponseType != "code")
        {
            return RedirectError(redirect, request.State, "unsupported_response_type", "response_type は code だけを受け付けます。");
        }

        if (string.IsNullOrEmpty(request.CodeChallenge) || request.CodeChallengeMethod != "S256" || !IsS256Challenge(request.CodeChallenge))
        {
            // plain・省略（OAuth 2.1 の既定は plain）は拒否する
            return RedirectError(redirect, request.State, "invalid_request", "PKCE（code_challenge_method=S256 と code_challenge）が必要です。");
        }

        if (request.State is { Length: > 1024 })
        {
            return RedirectError(redirect, null, "invalid_request", "state が長すぎます。");
        }

        if (request.Resources.Count != 1 || _options.ParseResourceUri(request.Resources[0]) is not { } resource || !client.Allows(resource))
        {
            return RedirectError(redirect, request.State, "invalid_target", "resource（REST API か MCP サーバーの URI）を 1 つ指定してください。");
        }

        if (GrantScopes(request.Scope, client.Scopes) is not { } scopes)
        {
            return RedirectError(redirect, request.State, "invalid_scope", "要求したスコープを与えられません。");
        }

        var handle = OAuthTokenPrefixes.New(OAuthTokenPrefixes.AuthorizationRequest);
        var now = _timeProvider.GetUtcNow();
        await _store.CreateAuthorizationRequestAsync(
            new StoredAuthorizationRequest(
                EntityId.New(), client.Id, redirect, resource, scopes, request.State, request.CodeChallenge, now, now + _options.AuthorizationRequestLifetime, false),
            AuthSecrets.Sha256(handle),
            cancellationToken);
        return new AuthorizeResult(AuthorizeOutcome.Continue, null, null, null, handle);
    }

    /// <summary>同意画面に出す内容。要求が無い・期限切れ・決定済み・クライアントが使えなければ null。</summary>
    public async Task<ConsentView?> GetConsentAsync(string? handle, CancellationToken cancellationToken)
    {
        if (await FindOpenRequestAsync(handle, cancellationToken) is not var (request, client))
        {
            return null;
        }

        return new ConsentView(
            client,
            request.RedirectUri,
            new Uri(request.RedirectUri).Host,
            request.Resource,
            _options.ResourceValue(request.Resource)!,
            request.Scopes,
            request.ExpiresAt);
    }

    /// <summary>
    /// 同意・拒否を記録し、redirect_uri への転送先（同意ならコード、拒否なら access_denied。どちらも state と iss を付ける）を返す。
    /// 要求が無い・期限切れ・決定済みなら null。
    /// </summary>
    public async Task<string?> DecideAsync(string? handle, Guid personId, bool approve, string? requestId, CancellationToken cancellationToken)
    {
        if (await FindOpenRequestAsync(handle, cancellationToken) is not var (request, client))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        var code = approve ? OAuthTokenPrefixes.New(OAuthTokenPrefixes.AuthorizationCode) : null;
        var decided = await _store.DecideAuthorizationRequestAsync(
            AuthSecrets.Sha256(handle!),
            personId,
            approve,
            now,
            code is null ? null : (EntityId.New(), AuthSecrets.Sha256(code), now + _options.AuthorizationCodeLifetime, EntityId.New()),
            cancellationToken);
        if (decided is null)
        {
            return null;
        }

        await _audit.RecordAsync(
            new AuthAuditEntry
            {
                Action = OAuthAuditActions.Consent,
                Outcome = approve ? AuditOutcome.Success : AuditOutcome.Denied,
                ActorType = ActorType.User,
                Channel = Channel.Ui,
                ActorPersonId = personId,
                ActorClientId = UiClientId,
                TargetType = OAuthAuditTargets.Client,
                TargetId = client.Id,
                SubjectPersonIds = [personId],
                RequestId = requestId,
            },
            cancellationToken);

        return approve
            ? AppendQuery(request.RedirectUri, [("code", code!), ("state", request.State), ("iss", _options.IssuerValue)])
            : AppendQuery(request.RedirectUri, [("error", "access_denied"), ("error_description", "利用者が許可しませんでした。"), ("state", request.State), ("iss", _options.IssuerValue)]);
    }

    // ---- トークン ------------------------------------------------------------------------------------------------

    /// <summary>/oauth/token。</summary>
    public async Task<TokenResult> TokenAsync(TokenRequest request, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.HasDuplicateParameters)
        {
            return Fail(400, "invalid_request", "同じパラメーターが複数あります。", null);
        }

        var (client, authFailure, basic) = await AuthenticateClientAsync(request.Client, cancellationToken);
        if (client is null)
        {
            await AuditTokenAsync(AuditOutcome.Denied, null, null, requestId, cancellationToken);
            return new TokenResult(401, null, "invalid_client", authFailure ?? "クライアントを認証できません。", basic, null, null);
        }

        TokenResult result = request.GrantType switch
        {
            OAuthGrantTypes.AuthorizationCode => await AuthorizationCodeAsync(client, request, requestId, cancellationToken),
            OAuthGrantTypes.RefreshToken => await RefreshAsync(client, request, requestId, cancellationToken),
            OAuthGrantTypes.ClientCredentials => await ClientCredentialsAsync(client, request, requestId, cancellationToken),
            null or "" => Fail(400, "invalid_request", "grant_type がありません。", client),
            _ => Fail(400, "unsupported_grant_type", "この grant_type は受け付けません。", client),
        };
        if (!result.Succeeded && result.Reuse is null)
        {
            await AuditTokenAsync(AuditOutcome.Denied, client, null, requestId, cancellationToken);
        }

        return result;
    }

    /// <summary>/oauth/revoke（RFC 7009）。知らないトークン・他のクライアントのトークンでも 200（有無を明かさない）。</summary>
    public async Task<TokenResult> RevokeAsync(ClientCredentialsInput credentials, string? token, string? requestId, CancellationToken cancellationToken)
    {
        var (client, authFailure, basic) = await AuthenticateClientAsync(credentials, cancellationToken);
        if (client is null)
        {
            return new TokenResult(401, null, "invalid_client", authFailure ?? "クライアントを認証できません。", basic, null, null);
        }

        if (string.IsNullOrEmpty(token))
        {
            return Fail(400, "invalid_request", "token がありません。", client);
        }

        var refresh = token.StartsWith(OAuthTokenPrefixes.RefreshToken, StringComparison.Ordinal);
        if ((refresh || token.StartsWith(OAuthTokenPrefixes.AccessToken, StringComparison.Ordinal))
            && await _store.RevokeTokenAsync(client.Id, AuthSecrets.Sha256(token), refresh, cancellationToken))
        {
            _validator.Invalidate();
            await _audit.RecordAsync(
                new AuthAuditEntry
                {
                    Action = OAuthAuditActions.TokenRevoke,
                    Outcome = AuditOutcome.Success,
                    ActorType = ActorType.System,
                    Channel = Channel.Api,
                    ActorClientId = client.ClientId,
                    TargetType = OAuthAuditTargets.Client,
                    TargetId = client.Id,
                    RequestId = requestId,
                },
                cancellationToken);
        }

        return new TokenResult(200, null, null, null, false, client, null);
    }

    private async Task<TokenResult> AuthorizationCodeAsync(OAuthClient client, TokenRequest request, string? requestId, CancellationToken cancellationToken)
    {
        if (!client.Allows(OAuthGrantTypes.AuthorizationCode))
        {
            return Fail(400, "unauthorized_client", "このクライアントは認可コードを使えません。", client);
        }

        if (string.IsNullOrEmpty(request.Code) || string.IsNullOrEmpty(request.RedirectUri) || string.IsNullOrEmpty(request.CodeVerifier))
        {
            return Fail(400, "invalid_request", "code・redirect_uri・code_verifier が必要です。", client);
        }

        if (!IsCodeVerifier(request.CodeVerifier))
        {
            return Fail(400, "invalid_grant", "code_verifier の形が不正です。", client);
        }

        OAuthResource? requestedResource = null;
        if (request.Resources.Count > 0)
        {
            if (request.Resources.Count != 1 || _options.ParseResourceUri(request.Resources[0]) is not { } parsed)
            {
                return Fail(400, "invalid_target", "resource が不正です。", client);
            }

            requestedResource = parsed;
        }

        var now = _timeProvider.GetUtcNow();
        var today = BusinessDate.Today(_timeProvider);
        var personActive = new Dictionary<Guid, bool>();
        string? accessToken = null;
        string? refreshToken = null;
        string? failure = null;

        // 束縛（client・redirect_uri・PKCE・resource）が合わなければコードを使用済みにしない（正しいクライアントの交換を妨げない）
        var exchanged = await _store.ExchangeAuthorizationCodeAsync(
            AuthSecrets.Sha256(request.Code),
            code =>
            {
                if (code.ClientRef != client.Id)
                {
                    failure = "認可コードが無効です。";
                    return null;
                }

                if (now >= code.ExpiresAt || !string.Equals(code.RedirectUri, request.RedirectUri, StringComparison.Ordinal)
                    || !string.Equals(code.CodeChallenge, S256(request.CodeVerifier), StringComparison.Ordinal)
                    || (requestedResource is { } r && r != code.Resource) || !client.Allows(code.Resource)
                    || !personActive.GetValueOrDefault(code.PersonId))
                {
                    failure = "認可コードが無効です。";
                    return null;
                }

                accessToken = OAuthTokenPrefixes.New(OAuthTokenPrefixes.AccessToken);
                var refreshes = code.Resource == OAuthResource.Mcp && client.Allows(OAuthGrantTypes.RefreshToken);
                refreshToken = refreshes ? OAuthTokenPrefixes.New(OAuthTokenPrefixes.RefreshToken) : null;
                var scopes = code.Scopes.Intersect(client.Scopes, StringComparer.Ordinal).ToList();
                if (scopes.Count == 0)
                {
                    failure = "認可コードが無効です。";
                    return null;
                }

                return new NewTokens(
                    client.Id,
                    code.PersonId,
                    EntityId.New(),
                    code.Resource,
                    scopes,
                    AuthSecrets.Sha256(accessToken),
                    now,
                    now + _options.AccessTokenLifetime,
                    refreshToken is null ? null : AuthSecrets.Sha256(refreshToken),
                    refreshToken is null ? null : Min(now + _options.RefreshTokenIdleLifetime, now + _options.RefreshTokenMaxLifetime),
                    now + _options.RefreshTokenMaxLifetime);
            },
            async code =>
            {
                personActive[code.PersonId] = await _authStore.GetPersonStateAsync(code.PersonId, today, cancellationToken) == PersonLoginState.Active;
            },
            cancellationToken);

        switch (exchanged.Status)
        {
            case CodeExchangeStatus.Reused:
                _validator.Invalidate();
                await AuditReuseAsync(client, exchanged.Code!.PersonId, exchanged.FamilyId, requestId, cancellationToken);
                return Fail(400, "invalid_grant", "認可コードは使用済みです。", client) with { Reuse = (client.Id, exchanged.Code.PersonId) };
            case CodeExchangeStatus.Invalid:
                return Fail(400, "invalid_grant", failure ?? "認可コードが無効です。", client);
        }

        var issued = exchanged.Code!;
        await AuditTokenAsync(AuditOutcome.Success, client, issued.PersonId, requestId, cancellationToken);
        return Issued(client, accessToken!, refreshToken, issued.Scopes.Intersect(client.Scopes, StringComparer.Ordinal));
    }

    private async Task<TokenResult> RefreshAsync(OAuthClient client, TokenRequest request, string? requestId, CancellationToken cancellationToken)
    {
        if (!client.Allows(OAuthGrantTypes.RefreshToken))
        {
            return Fail(400, "unauthorized_client", "このクライアントはリフレッシュトークンを使えません。", client);
        }

        if (string.IsNullOrEmpty(request.RefreshToken) || !request.RefreshToken.StartsWith(OAuthTokenPrefixes.RefreshToken, StringComparison.Ordinal))
        {
            return Fail(400, "invalid_grant", "リフレッシュトークンが無効です。", client);
        }

        OAuthResource? requestedResource = null;
        if (request.Resources.Count > 0)
        {
            if (request.Resources.Count != 1 || _options.ParseResourceUri(request.Resources[0]) is not { } parsed)
            {
                return Fail(400, "invalid_target", "resource が不正です。", client);
            }

            requestedResource = parsed;
        }

        var now = _timeProvider.GetUtcNow();
        var today = BusinessDate.Today(_timeProvider);
        var personActive = new Dictionary<Guid, bool>();
        string? accessToken = null;
        string? refreshToken = null;
        IReadOnlyList<string> grantedScopes = [];
        string? failure = null;
        var rotated = await _store.RotateRefreshTokenAsync(
            AuthSecrets.Sha256(request.RefreshToken),
            token =>
            {
                if (token.ClientRef != client.Id || token.FamilyEnded || now >= token.ExpiresAt || now >= token.FamilyExpiresAt
                    || (requestedResource is { } r && r != token.Resource) || !personActive.GetValueOrDefault(token.PersonId))
                {
                    failure = "リフレッシュトークンが無効です。";
                    return null;
                }

                // スコープは狭めることだけできる
                var available = token.Scopes.Intersect(client.Scopes, StringComparer.Ordinal).ToList();
                if (request.Scope is not null)
                {
                    if (ParseScopes(request.Scope) is not { } asked || asked.Any(s => !available.Contains(s, StringComparer.Ordinal)))
                    {
                        failure = "invalid_scope";
                        return null;
                    }

                    available = [.. asked];
                }

                if (available.Count == 0)
                {
                    failure = "リフレッシュトークンが無効です。";
                    return null;
                }

                grantedScopes = available;
                accessToken = OAuthTokenPrefixes.New(OAuthTokenPrefixes.AccessToken);
                refreshToken = OAuthTokenPrefixes.New(OAuthTokenPrefixes.RefreshToken);
                return new NewTokens(
                    client.Id,
                    token.PersonId,
                    token.FamilyId,
                    token.Resource,
                    available,
                    AuthSecrets.Sha256(accessToken),
                    now,
                    Min(now + _options.AccessTokenLifetime, token.FamilyExpiresAt),
                    AuthSecrets.Sha256(refreshToken),
                    Min(now + _options.RefreshTokenIdleLifetime, token.FamilyExpiresAt));
            },
            async token =>
            {
                personActive[token.PersonId] = await _authStore.GetPersonStateAsync(token.PersonId, today, cancellationToken) == PersonLoginState.Active;
            },
            cancellationToken);

        switch (rotated.Status)
        {
            case RefreshStatus.Reused:
                _validator.Invalidate();
                await AuditReuseAsync(client, rotated.Token!.PersonId, rotated.Token.FamilyId, requestId, cancellationToken);
                return Fail(400, "invalid_grant", "リフレッシュトークンは使用済みです。", client) with { Reuse = (client.Id, rotated.Token.PersonId) };
            case RefreshStatus.Invalid:
                return failure == "invalid_scope"
                    ? Fail(400, "invalid_scope", "要求したスコープを与えられません。", client)
                    : Fail(400, "invalid_grant", failure ?? "リフレッシュトークンが無効です。", client);
        }

        await AuditTokenAsync(AuditOutcome.Success, client, rotated.Token!.PersonId, requestId, cancellationToken);
        return Issued(client, accessToken!, refreshToken, grantedScopes);
    }

    private async Task<TokenResult> ClientCredentialsAsync(OAuthClient client, TokenRequest request, string? requestId, CancellationToken cancellationToken)
    {
        if (!client.Allows(OAuthGrantTypes.ClientCredentials) || client.AuthMethod == ClientAuthMethod.None)
        {
            return Fail(400, "unauthorized_client", "このクライアントは client credentials を使えません。", client);
        }

        // 社内システムは REST API だけ（MCP は人の委任だけを受け付ける）
        if (request.Resources.Count > 1
            || (request.Resources.Count == 1 && _options.ParseResourceUri(request.Resources[0]) != OAuthResource.Api)
            || !client.Allows(OAuthResource.Api))
        {
            return Fail(400, "invalid_target", "client credentials のトークンは REST API の resource だけに発行します。", client);
        }

        if (GrantScopes(request.Scope, client.Scopes) is not { } scopes)
        {
            return Fail(400, "invalid_scope", "要求したスコープを与えられません。", client);
        }

        var now = _timeProvider.GetUtcNow();
        var accessToken = OAuthTokenPrefixes.New(OAuthTokenPrefixes.AccessToken);
        await _store.CreateAccessTokenAsync(
            new NewTokens(client.Id, null, null, OAuthResource.Api, scopes, AuthSecrets.Sha256(accessToken), now, now + _options.AccessTokenLifetime, null, null),
            cancellationToken);
        await AuditTokenAsync(AuditOutcome.Success, client, null, requestId, cancellationToken);
        return Issued(client, accessToken, null, scopes);
    }

    // ---- クライアントの認証 --------------------------------------------------------------------------------------

    /// <summary>
    /// クライアントを認証する。方式は 1 つだけ（複数は拒否）。公開クライアントは client_id だけ、機密クライアントは private_key_jwt か移行用の client_secret。
    /// 登録の方式より弱い方式では通さない（機密クライアントが client_id だけで来ても拒否する）。
    /// </summary>
    private async Task<(OAuthClient? Client, string? Failure, bool Basic)> AuthenticateClientAsync(ClientCredentialsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var basic = input.BasicClientId is not null;
        var methods = (input.Assertion is not null || input.AssertionType is not null ? 1 : 0) + (basic ? 1 : 0) + (input.ClientSecret is not null ? 1 : 0);
        if (methods > 1)
        {
            return (null, "クライアントの認証の方式は 1 つだけにしてください。", basic);
        }

        if (input.Assertion is not null || input.AssertionType is not null)
        {
            if (input.AssertionType != JwtBearerAssertionType || string.IsNullOrEmpty(input.Assertion) || input.Assertion.Length > 8192)
            {
                return (null, null, false);
            }

            var assertedId = _assertions.ReadClientId(input.Assertion);
            if (assertedId is null || (input.ClientId is not null && !string.Equals(input.ClientId, assertedId, StringComparison.Ordinal)))
            {
                return (null, null, false);
            }

            if (await ActiveClientAsync(assertedId, cancellationToken) is not { AuthMethod: ClientAuthMethod.PrivateKeyJwt } client)
            {
                return (null, null, false);
            }

            var keys = await _store.GetClientKeysAsync(client.Id, cancellationToken);
            if (keys.Count == 0 || (await _assertions.ValidateAsync(input.Assertion, client, keys)).Assertion is not { } assertion)
            {
                return (null, null, false);
            }

            // jti の再利用を拒否する（assertion の期限まで覚える）
            return await _store.RecordClientAssertionAsync(client.Id, assertion.Jti, assertion.ExpiresAt, _timeProvider.GetUtcNow(), cancellationToken)
                ? (client, null, false)
                : (null, null, false);
        }

        var (clientId, secret) = basic ? (input.BasicClientId, input.BasicSecret) : (input.ClientId, input.ClientSecret);
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 2048)
        {
            return (null, null, basic);
        }

        var found = await ActiveClientAsync(clientId, cancellationToken);
        if (secret is not null)
        {
            if (found is not { AuthMethod: ClientAuthMethod.PrivateKeyJwt } confidential || secret.Length > 256)
            {
                return (null, null, basic);
            }

            var presented = Convert.FromHexString(AuthSecrets.Sha256(secret));
            var secrets = await _store.GetActiveClientSecretsAsync(confidential.Id, _timeProvider.GetUtcNow(), cancellationToken);
            var matched = false;
            foreach (var stored in secrets)
            {
                // 定数時間で比べる（全件を比べる）
                matched |= CryptographicOperations.FixedTimeEquals(presented, Convert.FromHexString(stored.SecretHash));
            }

            return matched ? (confidential, null, basic) : (null, null, basic);
        }

        return found is { AuthMethod: ClientAuthMethod.None } publicClient && !basic ? (publicClient, null, false) : (null, null, basic);
    }

    /// <summary>有効なクライアント（トークンエンドポイントでは CIMD の文書を取得しない。認可の要求で登録済みのものだけ）。</summary>
    private async Task<OAuthClient?> ActiveClientAsync(string clientId, CancellationToken cancellationToken) =>
        await _store.FindClientAsync(clientId, cancellationToken) is { Status: OAuthClientStatus.Active } client ? client : null;

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private const string UiClientId = Access.UiClient.Id;

    private async Task<(StoredAuthorizationRequest Request, OAuthClient Client)?> FindOpenRequestAsync(string? handle, CancellationToken cancellationToken)
    {
        if (handle is not { Length: 48 } || !handle.StartsWith(OAuthTokenPrefixes.AuthorizationRequest, StringComparison.Ordinal))
        {
            return null;
        }

        if (await _store.FindAuthorizationRequestAsync(AuthSecrets.Sha256(handle), cancellationToken) is not { Decided: false } request
            || _timeProvider.GetUtcNow() >= request.ExpiresAt
            || await _store.FindClientByRefAsync(request.ClientRef, cancellationToken) is not { Status: OAuthClientStatus.Active } client)
        {
            return null;
        }

        return (request, client);
    }

    /// <summary>
    /// 与えるスコープ。省略なら jinji.read（クライアントに許していれば）。知らないスコープがあれば null（invalid_scope）。
    /// 知っているがクライアントに許していないスコープは落とす（狭めて与える。空になれば null）。
    /// </summary>
    private static List<string>? GrantScopes(string? requested, IReadOnlyList<string> allowed)
    {
        var asked = requested is null ? [OAuthScopes.Read] : ParseScopes(requested);
        if (asked is null)
        {
            return null;
        }

        var granted = asked.Where(s => allowed.Contains(s, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
        return granted.Count == 0 ? null : granted;
    }

    /// <summary>スペース区切りのスコープ。知らないスコープ・空なら null。</summary>
    private static List<string>? ParseScopes(string value)
    {
        var scopes = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return scopes.Length == 0 || scopes.Length > 10 || scopes.Any(s => !OAuthScopes.All.Contains(s)) ? null : [.. scopes.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>S256 の code_challenge の形（base64url の 43 文字）。</summary>
    private static bool IsS256Challenge(string value) => value.Length == 43 && value.All(IsBase64UrlChar);

    /// <summary>code_verifier の形（RFC 7636: unreserved の 43〜128 文字）。</summary>
    private static bool IsCodeVerifier(string value) =>
        value.Length is >= 43 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~');

    private static bool IsBase64UrlChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    /// <summary>S256（BASE64URL(SHA256(ASCII(code_verifier)))）。</summary>
    public static string S256(string verifier) =>
        System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private TokenResult Issued(OAuthClient client, string accessToken, string? refreshToken, IEnumerable<string> scopes) =>
        new(200, new IssuedTokens(accessToken, (int)_options.AccessTokenLifetime.TotalSeconds, refreshToken, OAuthScopes.Join(scopes)), null, null, false, client, null);

    private static TokenResult Fail(int status, string error, string description, OAuthClient? client) =>
        new(status, null, error, description, false, client, null);

    private static AuthorizeResult Show(string error, string description) =>
        new(AuthorizeOutcome.ShowError, error, description, null, null);

    private AuthorizeResult RedirectError(string redirectUri, string? state, string error, string description) =>
        new(AuthorizeOutcome.RedirectError, error, description,
            AppendQuery(redirectUri, [("error", error), ("error_description", description), ("state", state), ("iss", _options.IssuerValue)]), null);

    /// <summary>redirect_uri にクエリを足す（既存のクエリは保つ）。値が null のものは付けない。</summary>
    public static string AppendQuery(string uri, IEnumerable<(string Name, string? Value)> parameters)
    {
        var builder = new StringBuilder(uri);
        var separator = uri.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        foreach (var (name, value) in parameters)
        {
            if (value is null)
            {
                continue;
            }

            builder.Append(separator).Append(name).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }

        return builder.ToString();
    }

    private Task AuditTokenAsync(AuditOutcome outcome, OAuthClient? client, Guid? personId, string? requestId, CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            new AuthAuditEntry
            {
                Action = OAuthAuditActions.TokenIssue,
                Outcome = outcome,
                ActorType = personId is null ? ActorType.Service : ActorType.Agent,
                Channel = Channel.Api,
                ActorClientId = client?.ClientId,
                TargetType = OAuthAuditTargets.Client,
                TargetId = client?.Id,
                SubjectPersonIds = personId is { } person ? [person] : [],
                RequestId = requestId,
            },
            cancellationToken);

    private Task AuditReuseAsync(OAuthClient client, Guid personId, Guid? familyId, string? requestId, CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            new AuthAuditEntry
            {
                Action = OAuthAuditActions.TokenReuseDetected,
                Outcome = AuditOutcome.Denied,
                ActorType = ActorType.Agent,
                Channel = Channel.Api,
                ActorClientId = client.ClientId,
                TargetType = familyId is null ? OAuthAuditTargets.Client : OAuthAuditTargets.TokenFamily,
                TargetId = familyId ?? client.Id,
                SubjectPersonIds = [personId],
                RequestId = requestId,
            },
            cancellationToken);
}
