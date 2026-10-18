using System.Security.Cryptography;
using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>クライアントの登録の指定（事前登録）。</summary>
/// <param name="ClientId">jin-ji が払い出す client_id（英小文字・数字で始まる 3〜64 文字）。</param>
public sealed record ClientRegistrationRequest(
    string ClientId,
    string? DisplayName,
    ClientAuthMethod AuthMethod,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<OAuthResource> Resources,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> McpTools,
    int? RateLimitPerClient);

/// <summary>クライアントの登録の変更（null の項目は変えない）。</summary>
public sealed record ClientUpdateRequest(
    string? DisplayName = null,
    IReadOnlyList<string>? RedirectUris = null,
    IReadOnlyList<OAuthResource>? Resources = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? Fields = null,
    IReadOnlyList<string>? McpTools = null,
    int? RateLimitPerClient = null,
    bool ClearRateLimit = false);

/// <summary>発行した client_secret。<see cref="Secret"/> はこの場でだけ見える（DB には SHA-256 だけ）。</summary>
public sealed record IssuedClientSecret(Guid Id, string Secret, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"IssuedClientSecret {{ Id = {Id:D}, ExpiresAt = {ExpiresAt:O} }}";
}

/// <summary>
/// jin-ji が発行するトークンのクライアントの管理（登録・変更・停止と再開・承認待ちの CIMD の承認と却下・公開鍵・移行用の client_secret・
/// CIMD の許可ドメイン・トークンの系列の失効）。管理 CLI と、UI でログインした人事管理者の人が使う。すべて監査ログに残す。
/// </summary>
/// <remarks>
/// AI エージェント・社内システムのトークン、緊急用アカウントでは使えない（呼び出し側の Host が <see cref="AuthActor"/> を CLI か人事管理者に限る）。
/// 停止・項目の上限の変更・失効は、同じプロセスのトークンのキャッシュを捨てて次の要求から効かせる（他のプロセスはキャッシュの秒数以内）。
/// </remarks>
public sealed class OAuthClientAdministrationService
{
    private readonly IOAuthStore _store;
    private readonly AccessTokenValidator _validator;
    private readonly AuthAuditor _audit;
    private readonly AuthorizationServerOptions _options;
    private readonly TimeProvider _timeProvider;

    public OAuthClientAdministrationService(
        IOAuthStore store,
        AccessTokenValidator validator,
        AuthAuditor audit,
        AuthorizationServerOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _validator = validator;
        _audit = audit;
        _options = options;
        _timeProvider = timeProvider;
    }

    // ---- クライアント --------------------------------------------------------------------------------------------

    public Task<IReadOnlyList<OAuthClient>> ListAsync(AuthActor actor, OAuthClientStatus? status, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        return _store.ListClientsAsync(actor, status, cancellationToken);
    }

    /// <summary>client_id（または api_client.id の文字列）でクライアントを引く。無ければ NotFound。</summary>
    public async Task<OAuthClient> FindAsync(string clientIdOrRef, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientIdOrRef);
        var client = Guid.TryParseExact(clientIdOrRef, "D", out var id)
            ? await _store.FindClientByRefAsync(id, cancellationToken)
            : await _store.FindClientAsync(clientIdOrRef, cancellationToken);
        return client ?? throw new AuthAdminException(AuthAdminError.NotFound, "クライアントが見つかりません。");
    }

    /// <summary>事前登録のクライアントを作る。</summary>
    public async Task<OAuthClient> RegisterAsync(AuthActor actor, ClientRegistrationRequest request, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOperator(actor);
        if (ClientResolver.IsMetadataUrl(request.ClientId))
        {
            throw Invalid("URL の client_id は CIMD のクライアントです。事前登録には英小文字・数字の ID を使ってください。");
        }

        var now = _timeProvider.GetUtcNow();
        var client = new OAuthClient
        {
            Id = EntityId.New(),
            ClientId = request.ClientId,
            Registration = OAuthClientRegistration.Registered,
            Status = OAuthClientStatus.Active,
            DisplayName = Trimmed(request.DisplayName),
            AuthMethod = request.AuthMethod,
            GrantTypes = [.. request.GrantTypes.Distinct(StringComparer.Ordinal)],
            RedirectUris = [.. request.RedirectUris.Distinct(StringComparer.Ordinal)],
            Resources = [.. request.Resources.Distinct()],
            Scopes = [.. request.Scopes.Distinct(StringComparer.Ordinal)],
            Fields = [.. request.Fields.Distinct(StringComparer.Ordinal)],
            McpTools = [.. request.McpTools.Distinct(StringComparer.Ordinal)],
            RateLimitPerClient = request.RateLimitPerClient,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Validate(client);
        if (!await _store.CreateClientAsync(actor, client, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, $"client_id {request.ClientId} は既に使われています。");
        }

        await AuditAsync(actor, OAuthAuditActions.ClientRegister, client.Id, requestId, cancellationToken);
        return client;
    }

    /// <summary>登録を変える（スコープ・項目の上限などを狭めた場合も次の要求から効く）。</summary>
    public async Task<OAuthClient> UpdateAsync(AuthActor actor, Guid clientRef, ClientUpdateRequest request, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOperator(actor);
        var current = await FindByRefAsync(clientRef, cancellationToken);
        if (current.Registration == OAuthClientRegistration.Cimd && (request.RedirectUris is not null || request.DisplayName is not null))
        {
            throw Invalid("CIMD のクライアントの表示名・redirect_uri は文書から読みます（変えられません）。");
        }

        var updated = current with
        {
            DisplayName = request.DisplayName is null ? current.DisplayName : Trimmed(request.DisplayName),
            RedirectUris = request.RedirectUris is null ? current.RedirectUris : [.. request.RedirectUris.Distinct(StringComparer.Ordinal)],
            Resources = request.Resources is null ? current.Resources : [.. request.Resources.Distinct()],
            Scopes = request.Scopes is null ? current.Scopes : [.. request.Scopes.Distinct(StringComparer.Ordinal)],
            Fields = request.Fields is null ? current.Fields : [.. request.Fields.Distinct(StringComparer.Ordinal)],
            McpTools = request.McpTools is null ? current.McpTools : [.. request.McpTools.Distinct(StringComparer.Ordinal)],
            RateLimitPerClient = request.ClearRateLimit ? null : request.RateLimitPerClient ?? current.RateLimitPerClient,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        Validate(updated);
        if (!await _store.UpdateClientAsync(actor, updated, current.Status, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, "クライアントの状態が変わりました。やり直してください。");
        }

        _validator.Invalidate();
        await AuditAsync(actor, OAuthAuditActions.ClientUpdate, current.Id, requestId, cancellationToken);
        return updated;
    }

    /// <summary>
    /// 状態を変える: 停止（有効 → 停止）、再開（停止 → 有効）、承認（承認待ち・却下 → 有効。CIMD）、却下（承認待ち → 却下）。
    /// 停止したクライアントのトークンは次の要求から使えない（系列は終えないので、再開すれば使える）。
    /// </summary>
    public async Task<OAuthClient> SetStatusAsync(AuthActor actor, Guid clientRef, OAuthClientStatus status, string? requestId, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        var current = await FindByRefAsync(clientRef, cancellationToken);
        var (allowed, action) = (current.Status, status) switch
        {
            (OAuthClientStatus.Active, OAuthClientStatus.Suspended) => (true, OAuthAuditActions.ClientSuspend),
            (OAuthClientStatus.Suspended, OAuthClientStatus.Active) => (true, OAuthAuditActions.ClientActivate),
            (OAuthClientStatus.PendingApproval or OAuthClientStatus.Rejected, OAuthClientStatus.Active) => (true, OAuthAuditActions.ClientApprove),
            (OAuthClientStatus.PendingApproval, OAuthClientStatus.Rejected) => (true, OAuthAuditActions.ClientReject),
            _ => (false, string.Empty),
        };
        if (!allowed)
        {
            throw new AuthAdminException(
                AuthAdminError.Conflict,
                $"状態 {OAuthCodes.ToCode(current.Status)} のクライアントを {OAuthCodes.ToCode(status)} にはできません。");
        }

        var updated = current with { Status = status, UpdatedAt = _timeProvider.GetUtcNow() };
        if (!await _store.UpdateClientAsync(actor, updated, current.Status, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, "クライアントの状態が変わりました。やり直してください。");
        }

        _validator.Invalidate();
        await AuditAsync(actor, action, current.Id, requestId, cancellationToken);
        return updated;
    }

    // ---- 鍵・シークレット ----------------------------------------------------------------------------------------

    /// <summary>公開鍵（RSA 2048 ビット以上か EC の SubjectPublicKeyInfo の PEM）を登録する。</summary>
    public async Task AddKeyAsync(AuthActor actor, Guid clientRef, string keyId, string publicKeyPem, string? requestId, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        var client = await FindByRefAsync(clientRef, cancellationToken);
        if (client.AuthMethod != ClientAuthMethod.PrivateKeyJwt)
        {
            throw Invalid("公開鍵は private_key_jwt のクライアントにだけ登録できます。");
        }

        if (string.IsNullOrEmpty(keyId) || keyId.Length > 128 || !keyId.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw Invalid("鍵 ID（kid）は英数字と . _ - の 128 文字以内にしてください。");
        }

        var pem = NormalizePublicKey(publicKeyPem);
        if (!await _store.AddClientKeyAsync(actor, clientRef, new OAuthClientKey(EntityId.New(), keyId, pem, _timeProvider.GetUtcNow()), cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, $"鍵 ID {keyId} の有効な鍵が既にあります。先に取り消してください。");
        }

        await AuditAsync(actor, OAuthAuditActions.ClientKeyAdd, clientRef, requestId, cancellationToken);
    }

    public async Task RevokeKeyAsync(AuthActor actor, Guid clientRef, string keyId, string? requestId, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        _ = await FindByRefAsync(clientRef, cancellationToken);
        if (!await _store.RevokeClientKeyAsync(actor, clientRef, keyId, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.NotFound, "有効な鍵が見つかりません。");
        }

        await AuditAsync(actor, OAuthAuditActions.ClientKeyRevoke, clientRef, requestId, cancellationToken);
    }

    /// <summary>移行用の client_secret を発行する（private_key_jwt のクライアントだけ。期限必須）。</summary>
    public async Task<IssuedClientSecret> IssueSecretAsync(AuthActor actor, Guid clientRef, TimeSpan? lifetime, string? requestId, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        var client = await FindByRefAsync(clientRef, cancellationToken);
        if (client.AuthMethod != ClientAuthMethod.PrivateKeyJwt)
        {
            throw Invalid("client_secret は機密クライアント（private_key_jwt）の移行用にだけ発行できます。");
        }

        var effective = lifetime ?? _options.ClientSecretLifetime;
        if (effective <= TimeSpan.Zero || effective > TimeSpan.FromDays(366))
        {
            throw Invalid("client_secret の有効期間は 366 日以内で指定してください。");
        }

        var secret = OAuthTokenPrefixes.New(OAuthTokenPrefixes.ClientSecret);
        var now = _timeProvider.GetUtcNow();
        var stored = new OAuthClientSecret(EntityId.New(), AuthSecrets.Sha256(secret), now, now + effective);
        await _store.AddClientSecretAsync(actor, clientRef, stored, cancellationToken);
        await AuditAsync(actor, OAuthAuditActions.ClientSecretIssue, clientRef, requestId, cancellationToken);
        return new IssuedClientSecret(stored.Id, secret, stored.ExpiresAt);
    }

    public async Task<int> RevokeSecretsAsync(AuthActor actor, Guid clientRef, string? requestId, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        _ = await FindByRefAsync(clientRef, cancellationToken);
        var count = await _store.RevokeClientSecretsAsync(actor, clientRef, cancellationToken);
        await AuditAsync(actor, OAuthAuditActions.ClientSecretRevoke, clientRef, requestId, cancellationToken);
        return count;
    }

    // ---- CIMD の許可ドメイン -------------------------------------------------------------------------------------

    /// <summary>許可ドメイン（設定の既定と、管理者が足したもの）。</summary>
    public async Task<(IReadOnlyList<string> Configured, IReadOnlyList<CimdAllowedDomain> Added)> ListDomainsAsync(AuthActor actor, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        return (_options.Cimd.AllowedDomains, await _store.ListAllowedDomainsAsync(cancellationToken));
    }

    public async Task AddDomainAsync(AuthActor actor, string domain, string? requestId, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        var normalized = domain?.Trim().ToLowerInvariant();
        if (!CimdOptions.IsDomain(normalized))
        {
            throw Invalid("ドメインはホスト名（例 claude.ai）で指定してください。");
        }

        var id = EntityId.New();
        if (!await _store.AddAllowedDomainAsync(actor, id, normalized!, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, $"{normalized} は既に許可しています。");
        }

        await AuditAsync(actor, OAuthAuditActions.CimdDomainAdd, id, requestId, cancellationToken, OAuthAuditTargets.CimdDomain);
    }

    /// <summary>
    /// 許可ドメインを外す（設定の既定は外せない）。既にそのドメインから登録したクライアントは有効のまま（止めるならクライアントを停止する）。
    /// </summary>
    public async Task RemoveDomainAsync(AuthActor actor, string domain, string? requestId, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        var normalized = domain?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!await _store.RemoveAllowedDomainAsync(actor, normalized, cancellationToken))
        {
            throw new AuthAdminException(
                AuthAdminError.NotFound,
                _options.Cimd.AllowedDomains.Contains(normalized, StringComparer.Ordinal)
                    ? $"{normalized} は設定（JinJi:Auth:Server:Cimd:AllowedDomains）の既定です。設定から外してください。"
                    : $"{normalized} は許可していません。");
        }

        await AuditAsync(actor, OAuthAuditActions.CimdDomainRemove, null, requestId, cancellationToken, OAuthAuditTargets.CimdDomain);
    }

    // ---- トークンの失効 ------------------------------------------------------------------------------------------

    /// <summary>
    /// トークンを失効する: クライアントの系列（人を指定すればその人の分だけ）と、client credentials のアクセストークン。系列を指定すればその系列だけ。
    /// 失効した系列とアクセストークンの数を返す。
    /// </summary>
    public async Task<int> RevokeTokensAsync(AuthActor actor, Guid? clientRef, Guid? personId, Guid? familyId, string? requestId, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        if (clientRef is null && personId is null && familyId is null)
        {
            throw Invalid("クライアント・人・系列のどれかを指定してください。");
        }

        if (clientRef is { } client)
        {
            _ = await FindByRefAsync(client, cancellationToken);
        }

        var count = await _store.EndFamiliesAsync(clientRef, personId, familyId, TokenFamilyEndKind.Admin, cancellationToken);
        if (clientRef is { } byClient && personId is null && familyId is null)
        {
            count += await _store.RevokeClientAccessTokensAsync(byClient, cancellationToken);
        }

        _validator.Invalidate();
        await _audit.RecordAsync(
            AuthAuditor.ForOperator(
                actor,
                OAuthAuditActions.TokenRevoke,
                AuditOutcome.Success,
                familyId is null ? OAuthAuditTargets.Client : OAuthAuditTargets.TokenFamily,
                familyId ?? clientRef,
                personId is { } person ? [person] : [],
                requestId),
            cancellationToken);
        return count;
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    /// <summary>登録の値を検査する（DB の CHECK 制約より先に、分かる言葉で拒否する）。</summary>
    private void Validate(OAuthClient client)
    {
        if (client.Registration == OAuthClientRegistration.Registered
            && (client.ClientId.Length is < 3 or > 64 || !char.IsAsciiLetterLower(client.ClientId[0]) && !char.IsAsciiDigit(client.ClientId[0])
                || !client.ClientId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-')
                || client.ClientId == Access.UiClient.Id))
        {
            throw Invalid("client_id は英小文字・数字で始まる英小文字・数字・. _ - の 3〜64 文字にしてください（jinji-ui は予約語）。");
        }

        if (client.DisplayName is { Length: > 200 })
        {
            throw Invalid("表示名は 200 文字以内にしてください。");
        }

        if (client.GrantTypes.Count == 0 || client.GrantTypes.Any(g => !OAuthGrantTypes.All.Contains(g)))
        {
            throw Invalid("grant_types は authorization_code / refresh_token / client_credentials から指定してください。");
        }

        if (client.Allows(OAuthGrantTypes.RefreshToken) && !client.Allows(OAuthGrantTypes.AuthorizationCode))
        {
            throw Invalid("refresh_token は authorization_code と一緒にだけ許せます。");
        }

        if (client.Allows(OAuthGrantTypes.ClientCredentials) && client.AuthMethod == ClientAuthMethod.None)
        {
            throw Invalid("client_credentials は機密クライアント（private_key_jwt）だけが使えます。");
        }

        if (client.Allows(OAuthGrantTypes.AuthorizationCode) && client.Registration == OAuthClientRegistration.Registered && client.RedirectUris.Count == 0)
        {
            throw Invalid("authorization_code には redirect_uri を 1 つ以上登録してください。");
        }

        if (client.RedirectUris.Count > 20 || client.RedirectUris.Any(u => !OAuthRedirectUris.IsAcceptable(u)))
        {
            throw Invalid("redirect_uri は https か、ループバックの http（localhost・127.0.0.1・[::1]）の 20 個以内にしてください。");
        }

        if (client.Resources.Count == 0)
        {
            throw Invalid("resource（api / mcp）を 1 つ以上指定してください。");
        }

        if (client.Resources.Contains(OAuthResource.Mcp) && _options.McpResource is null)
        {
            throw Invalid("MCP の resource（JinJi:Auth:Server:McpResource）が設定されていません。");
        }

        if (client.Allows(OAuthGrantTypes.ClientCredentials) && !client.Resources.Contains(OAuthResource.Api))
        {
            throw Invalid("client_credentials のクライアントには resource api が要ります（MCP は人の委任だけ）。");
        }

        if (client.Scopes.Count == 0 || client.Scopes.Any(s => !OAuthScopes.All.Contains(s)))
        {
            throw Invalid("スコープは jinji.read / jinji.propose から 1 つ以上指定してください。");
        }

        if (client.Fields.Count == 0)
        {
            throw Invalid("項目の上限を指定してください（上限なしは *）。");
        }

        try
        {
            _ = client.FieldLimit();
        }
        catch (ArgumentException ex)
        {
            throw Invalid(ex.Message);
        }

        if (client.McpTools.Count > 200 || client.McpTools.Any(t => t.Length is 0 or > 128))
        {
            throw Invalid("MCP ツールの許可リストは 128 文字以内の名前を 200 個以内で指定してください。");
        }

        if (client.McpTools.FirstOrDefault(t => !Access.McpToolNames.IsKnown(t)) is { } unknownTool)
        {
            throw Invalid($"MCP ツールの名前ではありません: {unknownTool}（使える名前: {string.Join(", ", Access.McpToolNames.All.Order(StringComparer.Ordinal))}、すべては *）。");
        }

        if (client.RateLimitPerClient is < 1 or > 1_000_000)
        {
            throw Invalid("レート制限は 1〜1000000 で指定してください。");
        }
    }

    /// <summary>公開鍵の PEM を読めるか確かめ、SubjectPublicKeyInfo の PEM に揃える（秘密鍵・弱い RSA は拒否）。</summary>
    private static string NormalizePublicKey(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem) || pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw Invalid("公開鍵（-----BEGIN PUBLIC KEY-----）の PEM を指定してください。秘密鍵は渡さないでください。");
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (rsa.KeySize < 2048)
            {
                throw Invalid("RSA の鍵は 2048 ビット以上にしてください。");
            }

            return rsa.ExportSubjectPublicKeyInfoPem();
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(pem);
                return ecdsa.ExportSubjectPublicKeyInfoPem();
            }
            catch (Exception inner) when (inner is ArgumentException or CryptographicException)
            {
                throw Invalid("RSA・EC の公開鍵の PEM として読めません。");
            }
        }
    }

    private async Task<OAuthClient> FindByRefAsync(Guid clientRef, CancellationToken cancellationToken) =>
        await _store.FindClientByRefAsync(clientRef, cancellationToken)
        ?? throw new AuthAdminException(AuthAdminError.NotFound, "クライアントが見つかりません。");

    private static void RequireOperator(AuthActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind is not (AuthActorKind.Cli or AuthActorKind.HrAdmin))
        {
            throw new AuthAdminException(AuthAdminError.Forbidden, "クライアントの管理は管理 CLI か、UI でログインした人事管理者だけが行えます。");
        }
    }

    private static string? Trimmed(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static AuthAdminException Invalid(string message) => new(AuthAdminError.Invalid, message);

    private Task AuditAsync(AuthActor actor, string action, Guid? target, string? requestId, CancellationToken cancellationToken, string targetType = OAuthAuditTargets.Client) =>
        _audit.RecordAsync(AuthAuditor.ForOperator(actor, action, AuditOutcome.Success, targetType, target, [], requestId), cancellationToken);
}
