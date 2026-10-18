using System.Text.Json;
using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>client_id を解決できなかった理由。</summary>
public enum ClientResolutionError
{
    /// <summary>登録されていない・形が不正・CIMD が無効。</summary>
    Unknown,

    /// <summary>許可外のドメインの CIMD で、管理者の承認待ち。</summary>
    PendingApproval,

    /// <summary>停止中・却下済み。</summary>
    Unavailable,

    /// <summary>CIMD の文書を取得できない・文書が不正。</summary>
    MetadataInvalid,
}

/// <summary>client_id の解決の結果。</summary>
/// <param name="Detail">失敗の理由（ログ用。応答には出さない）。</param>
public sealed record ClientResolution(OAuthClient? Client, ClientResolutionError? Error, string? Detail = null)
{
    public static ClientResolution Of(OAuthClient client) => new(client, null);

    public static ClientResolution Fail(ClientResolutionError error, string? detail = null) => new(null, error, detail);
}

/// <summary>
/// 認可の要求の client_id を、事前登録のクライアントか CIMD（Client ID Metadata Document）のクライアントに解決する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>事前登録: api_client の行。有効なものだけ</item>
/// <item>CIMD: client_id が https の URL。文書を取得するのは、ホストが許可ドメイン（設定の既定 ∪ 管理者が足したもの）にあるか、
/// 管理者が承認したクライアントだけ。許可外は文書を取得せずに「管理者の承認待ち」として記録し、認可を進めない</item>
/// <item>文書は HTTP キャッシュ（の上限まで）使い回し、期限を過ぎたら次の認可の要求で取り直す。文書の client_id は URL と完全に一致すること、
/// redirect_uris は https かループバックの http であること、クライアントの認証は none（公開クライアント）であることを確かめる</item>
/// <item>取得・検査の失敗は監査ログ（<c>auth.cimd.fetch_failed</c>）に残す</item>
/// </list>
/// </remarks>
public sealed class ClientResolver
{
    private readonly IOAuthStore _store;
    private readonly IClientMetadataFetcher _fetcher;
    private readonly AuthAuditor _audit;
    private readonly AuthorizationServerOptions _options;
    private readonly TimeProvider _timeProvider;

    public ClientResolver(IOAuthStore store, IClientMetadataFetcher fetcher, AuthAuditor audit, AuthorizationServerOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _fetcher = fetcher;
        _audit = audit;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>client_id が CIMD の URL の形か（https で始まる）。</summary>
    public static bool IsMetadataUrl(string clientId) => clientId.StartsWith("https://", StringComparison.Ordinal) || clientId.StartsWith("http://", StringComparison.Ordinal);

    /// <summary>
    /// CIMD の client_id として受け付ける URL か（https、パスを持つ、フラグメント・利用者情報・クエリ・ドット区間なし、2048 文字まで）。
    /// </summary>
    public static Uri? ParseMetadataUrl(string clientId)
    {
        if (clientId.Length > 2048 || !clientId.StartsWith("https://", StringComparison.Ordinal)
            || !Uri.TryCreate(clientId, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || clientId.Contains('#', StringComparison.Ordinal) || uri.AbsolutePath is "/" or ""
            || clientId.Contains("/./", StringComparison.Ordinal) || clientId.Contains("/../", StringComparison.Ordinal)
            || clientId.EndsWith("/.", StringComparison.Ordinal) || clientId.EndsWith("/..", StringComparison.Ordinal)
            || !string.Equals(uri.AbsoluteUri, clientId, StringComparison.Ordinal))
        {
            return null;
        }

        return uri;
    }

    /// <summary>認可の要求の client_id を解決する（CIMD なら必要に応じて文書を取得する）。</summary>
    public async Task<ClientResolution> ResolveAsync(string clientId, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        if (!IsMetadataUrl(clientId))
        {
            var registered = await _store.FindClientAsync(clientId, cancellationToken);
            return registered switch
            {
                null => ClientResolution.Fail(ClientResolutionError.Unknown),
                { Status: OAuthClientStatus.Active } => ClientResolution.Of(registered),
                _ => ClientResolution.Fail(ClientResolutionError.Unavailable),
            };
        }

        if (!_options.Cimd.Enabled || ParseMetadataUrl(clientId) is not { } url)
        {
            return ClientResolution.Fail(ClientResolutionError.Unknown);
        }

        var existing = await _store.FindClientAsync(clientId, cancellationToken);
        switch (existing?.Status)
        {
            case OAuthClientStatus.PendingApproval:
                return ClientResolution.Fail(ClientResolutionError.PendingApproval);
            case OAuthClientStatus.Suspended or OAuthClientStatus.Rejected:
                return ClientResolution.Fail(ClientResolutionError.Unavailable);
            case OAuthClientStatus.Active when existing.MetadataExpiresAt is { } expires && expires > _timeProvider.GetUtcNow():
                return ClientResolution.Of(existing);
        }

        if (existing is null && !await IsAllowedDomainAsync(url.Host, cancellationToken))
        {
            // 許可外のドメイン: 文書を取得せず、管理者の承認待ちとして記録する
            var pending = NewCimdClient(clientId, OAuthClientStatus.PendingApproval);
            if (await _store.CreateClientAsync(null, pending, cancellationToken))
            {
                await AuditAsync(OAuthAuditActions.CimdPending, AuditOutcome.Denied, pending.Id, requestId, cancellationToken);
                return ClientResolution.Fail(ClientResolutionError.PendingApproval);
            }

            // 同時の要求が先に記録した
            return await ResolveAsync(clientId, requestId, cancellationToken);
        }

        return await FetchAsync(url, existing, requestId, cancellationToken);
    }

    /// <summary>ホストが許可ドメイン（設定 ∪ DB）にあるか。</summary>
    public async Task<bool> IsAllowedDomainAsync(string host, CancellationToken cancellationToken)
    {
        var lower = host.ToLowerInvariant();
        if (_options.Cimd.AllowedDomains.Contains(lower, StringComparer.Ordinal))
        {
            return true;
        }

        var domains = await _store.ListAllowedDomainsAsync(cancellationToken);
        return domains.Any(d => string.Equals(d.Domain, lower, StringComparison.Ordinal));
    }

    private async Task<ClientResolution> FetchAsync(Uri url, OAuthClient? existing, string? requestId, CancellationToken cancellationToken)
    {
        var clientId = url.AbsoluteUri;
        var fetched = await _fetcher.FetchAsync(url, cancellationToken);
        var failure = fetched.Failure;
        CimdDocument? document = null;
        if (failure is null && !TryParse(fetched.Json!, clientId, out document, out failure))
        {
            document = null;
        }

        var now = _timeProvider.GetUtcNow();
        if (document is null)
        {
            await AuditAsync(OAuthAuditActions.CimdFetchFailed, AuditOutcome.Failed, existing?.Id, requestId, cancellationToken);
            return ClientResolution.Fail(ClientResolutionError.MetadataInvalid, failure);
        }

        var cacheFor = fetched.CacheFor ?? _options.Cimd.DefaultCacheDuration;
        if (cacheFor > _options.Cimd.MaxCacheDuration)
        {
            cacheFor = _options.Cimd.MaxCacheDuration;
        }

        if (existing is null)
        {
            var created = NewCimdClient(clientId, OAuthClientStatus.Active) with
            {
                DisplayName = document.ClientName,
                RedirectUris = document.RedirectUris,
                MetadataFetchedAt = now,
                MetadataExpiresAt = now + cacheFor,
            };
            if (await _store.CreateClientAsync(null, created, cancellationToken))
            {
                await AuditAsync(OAuthAuditActions.ClientRegister, AuditOutcome.Success, created.Id, requestId, cancellationToken);
                return ClientResolution.Of(created);
            }

            return await ResolveAsync(clientId, requestId, cancellationToken);
        }

        var updated = existing with
        {
            DisplayName = document.ClientName,
            RedirectUris = document.RedirectUris,
            MetadataFetchedAt = now,
            MetadataExpiresAt = now + cacheFor,
        };
        return await _store.UpdateClientAsync(null, updated, OAuthClientStatus.Active, cancellationToken)
            ? ClientResolution.Of(updated)
            : ClientResolution.Fail(ClientResolutionError.Unavailable);
    }

    private OAuthClient NewCimdClient(string clientId, OAuthClientStatus status) => new()
    {
        Id = EntityId.New(),
        ClientId = clientId,
        Registration = OAuthClientRegistration.Cimd,
        Status = status,
        AuthMethod = ClientAuthMethod.None,
        GrantTypes = [OAuthGrantTypes.AuthorizationCode, OAuthGrantTypes.RefreshToken],
        Resources = [OAuthResource.Mcp],
        Scopes = [.. _options.Cimd.DefaultScopes],
        Fields = [.. _options.Cimd.DefaultFields],
        McpTools = [.. _options.Cimd.DefaultMcpTools],
        CreatedAt = _timeProvider.GetUtcNow(),
        UpdatedAt = _timeProvider.GetUtcNow(),
    };

    private Task AuditAsync(string action, AuditOutcome outcome, Guid? clientRef, string? requestId, CancellationToken cancellationToken) =>
        _audit.RecordAsync(
            new AuthAuditEntry
            {
                Action = action,
                Outcome = outcome,
                ActorType = ActorType.System,
                Channel = Channel.Api,
                TargetType = OAuthAuditTargets.Client,
                TargetId = clientRef,
                RequestId = requestId,
            },
            cancellationToken);

    /// <summary>取得した CIMD の文書の必要な項目。</summary>
    private sealed record CimdDocument(string? ClientName, IReadOnlyList<string> RedirectUris);

    /// <summary>文書を検査して読む。</summary>
    private static bool TryParse(string json, string clientId, out CimdDocument? document, out string? failure)
    {
        document = null;
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                failure = "文書が JSON のオブジェクトではありません";
                return false;
            }

            if (!root.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String || !string.Equals(id.GetString(), clientId, StringComparison.Ordinal))
            {
                failure = "文書の client_id が URL と一致しません";
                return false;
            }

            if (!root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array
                || uris.GetArrayLength() is 0 or > 20
                || uris.EnumerateArray().Any(u => u.ValueKind != JsonValueKind.String || !OAuthRedirectUris.IsAcceptable(u.GetString())))
            {
                failure = "redirect_uris が無いか、https・ループバックの http 以外を含みます";
                return false;
            }

            if (root.TryGetProperty("token_endpoint_auth_method", out var method) && !(method.ValueKind == JsonValueKind.String && method.GetString() == "none"))
            {
                failure = "token_endpoint_auth_method は none（公開クライアント）だけを受け付けます";
                return false;
            }

            if (root.TryGetProperty("grant_types", out var grants)
                && (grants.ValueKind != JsonValueKind.Array
                    || !grants.EnumerateArray().Any(g => g.ValueKind == JsonValueKind.String && g.GetString() == OAuthGrantTypes.AuthorizationCode)))
            {
                failure = "grant_types に authorization_code がありません";
                return false;
            }

            if (root.TryGetProperty("response_types", out var responses)
                && (responses.ValueKind != JsonValueKind.Array || !responses.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == "code")))
            {
                failure = "response_types に code がありません";
                return false;
            }

            string? name = null;
            if (root.TryGetProperty("client_name", out var clientName) && clientName.ValueKind == JsonValueKind.String)
            {
                var value = new string([.. clientName.GetString()!.Where(c => !char.IsControl(c))]).Trim();
                name = value.Length == 0 ? null : value.Length > 200 ? value[..200] : value;
            }

            document = new CimdDocument(name, [.. uris.EnumerateArray().Select(u => u.GetString()!).Distinct(StringComparer.Ordinal)]);
            failure = null;
            return true;
        }
        catch (JsonException)
        {
            failure = "文書が JSON として読めません";
            return false;
        }
    }
}
