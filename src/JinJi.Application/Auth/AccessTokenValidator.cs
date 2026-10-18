using System.Collections.Concurrent;
using JinJi.Domain;

namespace JinJi.Application.Auth;

/// <summary>トークン発行の監査ログの操作（audit_event.action）。</summary>
public static class OAuthAuditActions
{
    /// <summary>利用者の同意・拒否（同意画面）。</summary>
    public const string Consent = "auth.consent";

    /// <summary>トークンの発行（成功・拒否。認可コード・リフレッシュ・client credentials）。</summary>
    public const string TokenIssue = "auth.token.issue";

    /// <summary>トークンの失効（RFC 7009 の要求・管理者の失効）。</summary>
    public const string TokenRevoke = "auth.token.revoke";

    /// <summary>使用済みのリフレッシュトークン・認可コードの再利用を検知した（系列を失効した）。</summary>
    public const string TokenReuseDetected = "auth.token.reuse_detected";

    public const string ClientRegister = "auth.client.register";

    /// <summary>クライアントの登録の変更（スコープ・項目の上限・redirect_uri など）。</summary>
    public const string ClientUpdate = "auth.client.update";

    public const string ClientSuspend = "auth.client.suspend";

    public const string ClientActivate = "auth.client.activate";

    /// <summary>承認待ちの CIMD のクライアントの承認・却下。</summary>
    public const string ClientApprove = "auth.client.approve";

    public const string ClientReject = "auth.client.reject";

    public const string ClientKeyAdd = "auth.client.key.add";

    public const string ClientKeyRevoke = "auth.client.key.revoke";

    public const string ClientSecretIssue = "auth.client.secret.issue";

    public const string ClientSecretRevoke = "auth.client.secret.revoke";

    public const string CimdDomainAdd = "auth.cimd.domain.add";

    public const string CimdDomainRemove = "auth.cimd.domain.remove";

    /// <summary>許可外のドメインの CIMD を承認待ちとして記録した。</summary>
    public const string CimdPending = "auth.cimd.pending";

    /// <summary>CIMD の文書の取得・検査に失敗した。</summary>
    public const string CimdFetchFailed = "auth.cimd.fetch_failed";

    /// <summary>先行提供の日数の起案（0016）。</summary>
    public const string DisclosureLeadPropose = "auth.client.disclosure_lead.propose";

    /// <summary>先行提供の日数の承認（拒否の記録も。起案者本人・人事管理者の人以外の試み）。</summary>
    public const string DisclosureLeadApprove = "auth.client.disclosure_lead.approve";

    public const string DisclosureLeadReject = "auth.client.disclosure_lead.reject";
}

/// <summary>監査ログの対象の種類（audit_event.target_type）。</summary>
public static class OAuthAuditTargets
{
    public const string Client = "api_client";
    public const string TokenFamily = "oauth_token_family";
    public const string AccessToken = "oauth_access_token";
    public const string CimdDomain = "cimd_allowed_domain";
    public const string DisclosureLead = "api_client_disclosure_lead";
}

/// <summary>
/// 参照トークン（jin-ji が発行するアクセストークン）の検証。API・MCP の共通。1 要求 1 回の索引検索に、数秒のメモリキャッシュを足す。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>キャッシュするのは通ったものだけ（<see cref="AuthorizationServerOptions.TokenCacheDuration"/>、既定 5 秒）。期限はキャッシュから使うときも確かめる。
/// 失効・退職・一時停止・クライアントの停止・ロールの取り消しはこの秒数以内に効く。同じプロセスの失効（RFC 7009・管理者）はキャッシュを捨てて即時に効かせる</item>
/// <item>知らないトークンは覚えない（乱数を送り続けてメモリを埋めさせない）。キャッシュは 1 万件を超えたら捨てる</item>
/// <item>スコープは「トークンのスコープ ∩ クライアントの今のスコープ」、項目の上限・MCP ツールはクライアントの今の登録（管理者が狭めたら次の要求から効く）</item>
/// </list>
/// </remarks>
public sealed class AccessTokenValidator
{
    private const int MaxCacheEntries = 10_000;
    private readonly IOAuthStore _store;
    private readonly AuthorizationServerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, (ValidatedAccessToken Token, DateTimeOffset CachedAt)> _cache = new(StringComparer.Ordinal);

    public AccessTokenValidator(IOAuthStore store, AuthorizationServerOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>検証する。使えなければ null。</summary>
    public async Task<ValidatedAccessToken?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        if (!OAuthTokenPrefixes.IsAccessToken(token))
        {
            return null;
        }

        var hash = AuthSecrets.Sha256(token);
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(hash, out var cached))
        {
            if (now - cached.CachedAt < _options.TokenCacheDuration && now < cached.Token.ExpiresAt)
            {
                return cached.Token;
            }

            _cache.TryRemove(hash, out _);
        }

        var record = await _store.FindAccessTokenAsync(hash, BusinessDate.Today(_timeProvider), cancellationToken);
        if (record is null || record.Revoked || now >= record.ExpiresAt || record.Client.Status != OAuthClientStatus.Active
            || !record.Client.Allows(record.Resource) || (record.PersonId is not null && !record.PersonActive))
        {
            return null;
        }

        var scopes = record.Scopes.Intersect(record.Client.Scopes, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (scopes.Count == 0)
        {
            return null;
        }

        var validated = new ValidatedAccessToken(
            record.TokenId,
            record.Client.Id,
            record.Client.ClientId,
            record.PersonId,
            record.FamilyId,
            record.Resource,
            scopes,
            record.Roles,
            record.Client.Fields,
            record.Client.McpTools,
            record.Client.RateLimitPerClient,
            record.ExpiresAt,
            record.PersonId is null ? record.Client.DisclosureLeadDays : 0);
        if (_options.TokenCacheDuration > TimeSpan.Zero)
        {
            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }

            _cache[hash] = (validated, now);
        }

        return validated;
    }

    /// <summary>キャッシュを捨てる（同じプロセスでの失効・クライアントの停止・項目の上限の変更の後）。</summary>
    public void Invalidate() => _cache.Clear();
}
