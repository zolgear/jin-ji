using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>クライアントの登録の種類（api_client.registration）。</summary>
public enum OAuthClientRegistration
{
    /// <summary>事前登録（jin-ji が払い出す client_id。社内システム・CIMD に対応しない MCP クライアント）。</summary>
    Registered,

    /// <summary>Client ID Metadata Document（client_id が https の URL。管理者が許可したドメイン・承認したクライアントだけ）。</summary>
    Cimd,
}

/// <summary>クライアントの状態（api_client.status）。</summary>
public enum OAuthClientStatus
{
    Active,

    /// <summary>許可外のドメインの CIMD。管理者が承認するまで文書を取得せず、認可も進めない。</summary>
    PendingApproval,

    /// <summary>停止中。発行済みのトークンも次の要求から使えない。</summary>
    Suspended,

    /// <summary>承認待ちを却下した。</summary>
    Rejected,
}

/// <summary>トークンエンドポイントでのクライアントの認証の方式（api_client.auth_method）。</summary>
public enum ClientAuthMethod
{
    /// <summary>公開クライアント（PKCE だけ）。</summary>
    None,

    /// <summary>private_key_jwt（RFC 7523。登録した公開鍵で client assertion を検証する）。移行用の client_secret も併用できる。</summary>
    PrivateKeyJwt,
}

/// <summary>トークンの宛先（resource。RFC 8707）。REST API と MCP は別の URI で、発行時の resource に束縛する。</summary>
public enum OAuthResource
{
    Api,
    Mcp,
}

/// <summary>スコープ（ADR-0004「スコープは最小限から」）。承認や管理の操作のスコープは作らない（エージェントにさせない）。</summary>
public static class OAuthScopes
{
    /// <summary>参照。</summary>
    public const string Read = "jinji.read";

    /// <summary>作業領域への書き込みと変更提案。</summary>
    public const string Propose = "jinji.propose";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Read, Propose };

    /// <summary>スペース区切りの文字列にする（並びは Read → Propose）。</summary>
    public static string Join(IEnumerable<string> scopes) =>
        string.Join(' ', scopes.Distinct(StringComparer.Ordinal).OrderBy(s => s == Read ? 0 : 1).ThenBy(s => s, StringComparer.Ordinal));
}

/// <summary>付与の種類（grant_type）。</summary>
public static class OAuthGrantTypes
{
    public const string AuthorizationCode = "authorization_code";
    public const string RefreshToken = "refresh_token";
    public const string ClientCredentials = "client_credentials";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { AuthorizationCode, RefreshToken, ClientCredentials };
}

/// <summary>コード値（DB・設定・応答）。</summary>
public static class OAuthCodes
{
    public static string ToCode(OAuthClientRegistration registration) => registration == OAuthClientRegistration.Cimd ? "cimd" : "registered";

    public static OAuthClientRegistration ParseRegistration(string code) => code switch
    {
        "registered" => OAuthClientRegistration.Registered,
        "cimd" => OAuthClientRegistration.Cimd,
        _ => throw new ArgumentException($"未知のクライアントの種類です: {code}", nameof(code)),
    };

    public static string ToCode(OAuthClientStatus status) => status switch
    {
        OAuthClientStatus.Active => "active",
        OAuthClientStatus.PendingApproval => "pending_approval",
        OAuthClientStatus.Suspended => "suspended",
        OAuthClientStatus.Rejected => "rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static OAuthClientStatus ParseStatus(string code) => code switch
    {
        "active" => OAuthClientStatus.Active,
        "pending_approval" => OAuthClientStatus.PendingApproval,
        "suspended" => OAuthClientStatus.Suspended,
        "rejected" => OAuthClientStatus.Rejected,
        _ => throw new ArgumentException($"未知のクライアントの状態です: {code}", nameof(code)),
    };

    public static string ToCode(ClientAuthMethod method) => method == ClientAuthMethod.PrivateKeyJwt ? "private_key_jwt" : "none";

    public static ClientAuthMethod ParseAuthMethod(string code) => code switch
    {
        "none" => ClientAuthMethod.None,
        "private_key_jwt" => ClientAuthMethod.PrivateKeyJwt,
        _ => throw new ArgumentException($"未知のクライアントの認証の方式です: {code}（none / private_key_jwt）", nameof(code)),
    };

    public static string ToCode(OAuthResource resource) => resource == OAuthResource.Mcp ? "mcp" : "api";

    public static OAuthResource ParseResource(string code) => code switch
    {
        "api" => OAuthResource.Api,
        "mcp" => OAuthResource.Mcp,
        _ => throw new ArgumentException($"未知の resource の種類です: {code}（api / mcp）", nameof(code)),
    };
}

/// <summary>jin-ji が発行するトークンのクライアント（api_client の 1 行）。</summary>
public sealed record OAuthClient
{
    public required Guid Id { get; init; }

    public required string ClientId { get; init; }

    public required OAuthClientRegistration Registration { get; init; }

    public required OAuthClientStatus Status { get; init; }

    public string? DisplayName { get; init; }

    public required ClientAuthMethod AuthMethod { get; init; }

    public required IReadOnlyList<string> GrantTypes { get; init; }

    public IReadOnlyList<string> RedirectUris { get; init; } = [];

    public required IReadOnlyList<OAuthResource> Resources { get; init; }

    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>渡してよい項目の上限（PersonFields の名前。["*"] は上限なし）。</summary>
    public required IReadOnlyList<string> Fields { get; init; }

    /// <summary>MCP ツールの許可リスト（<see cref="Access.McpToolNames"/>。空なら何も許さない、<c>*</c> はすべて）。</summary>
    public IReadOnlyList<string> McpTools { get; init; } = [];

    /// <summary>クライアントの段のレート制限（経路の設定の窓あたり）。null なら設定の既定。</summary>
    public int? RateLimitPerClient { get; init; }

    /// <summary>
    /// 先行提供の日数（承認済みの api_client_disclosure_lead。無ければ 0）。client credentials のトークン（社内システム）にだけ効く。
    /// 変更は起案と別人の承認で行う（<see cref="DisclosureLeadService"/>）ので、登録の変更（update-client）では変えられない。
    /// </summary>
    public int DisclosureLeadDays { get; init; }

    public DateTimeOffset? MetadataFetchedAt { get; init; }

    public DateTimeOffset? MetadataExpiresAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public bool Allows(string grantType) => GrantTypes.Contains(grantType, StringComparer.Ordinal);

    public bool Allows(OAuthResource resource) => Resources.Contains(resource);

    /// <summary>項目の上限。</summary>
    public FieldLimit FieldLimit() =>
        Policy.FieldLimit.ForClient(Fields);

    /// <summary>ループバックの redirect_uri しか持たないか（同意画面の注意書き。MCP 仕様の要求）。</summary>
    public bool HasOnlyLoopbackRedirects => RedirectUris.Count > 0 && RedirectUris.All(OAuthRedirectUris.IsLoopback);
}

/// <summary>登録した公開鍵。</summary>
public sealed record OAuthClientKey(Guid Id, string KeyId, string PublicKeyPem, DateTimeOffset CreatedAt);

/// <summary>移行用の client_secret（ハッシュと期限）。</summary>
public sealed record OAuthClientSecret(Guid Id, string SecretHash, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>CIMD の許可ドメイン（管理者が DB に足したもの）。</summary>
public sealed record CimdAllowedDomain(Guid Id, string Domain, DateTimeOffset CreatedAt);

/// <summary>保存した認可の要求。</summary>
public sealed record StoredAuthorizationRequest(
    Guid Id,
    Guid ClientRef,
    string RedirectUri,
    OAuthResource Resource,
    IReadOnlyList<string> Scopes,
    string? State,
    string CodeChallenge,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    bool Decided);

/// <summary>保存した認可コード（交換の材料）。</summary>
public sealed record StoredAuthorizationCode(
    Guid Id,
    Guid ClientRef,
    Guid PersonId,
    string RedirectUri,
    OAuthResource Resource,
    IReadOnlyList<string> Scopes,
    string CodeChallenge,
    DateTimeOffset ExpiresAt,
    bool Used,
    Guid? FamilyId);

/// <summary>保存したリフレッシュトークンと系列。</summary>
public sealed record StoredRefreshToken(
    Guid Id,
    Guid FamilyId,
    Guid ClientRef,
    Guid PersonId,
    OAuthResource Resource,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt,
    DateTimeOffset FamilyExpiresAt,
    bool Used,
    bool FamilyEnded);

/// <summary>新しく発行するアクセストークン（とリフレッシュトークン）の材料。</summary>
/// <param name="FamilyExpiresAt">新しい系列の最長の期限（認可コードの交換で系列を作るときだけ）。</param>
public sealed record NewTokens(
    Guid ClientRef,
    Guid? PersonId,
    Guid? FamilyId,
    OAuthResource Resource,
    IReadOnlyList<string> Scopes,
    string AccessTokenHash,
    DateTimeOffset IssuedAt,
    DateTimeOffset AccessTokenExpiresAt,
    string? RefreshTokenHash,
    DateTimeOffset? RefreshTokenExpiresAt,
    DateTimeOffset? FamilyExpiresAt = null);

/// <summary>系列の終了の種類（oauth_token_family.end_kind）。</summary>
public enum TokenFamilyEndKind
{
    /// <summary>クライアントの失効の要求（RFC 7009）。</summary>
    Revoked,

    /// <summary>使用済みのリフレッシュトークンの再利用を検知した。</summary>
    ReuseDetected,

    /// <summary>使用済みの認可コードが再び使われた。</summary>
    CodeReuse,

    /// <summary>管理者の失効（管理 CLI・人事管理者。ローカルアカウントの無効化を含む）。</summary>
    Admin,

    /// <summary>委任元の本人の取り消し（アカウントの画面）。</summary>
    User,
}

/// <summary>
/// 人の生きている委任（トークンの系列）。同意画面で許可したクライアントごとの 1 回の付与で、本人がアカウントの画面で一覧・取り消しする。
/// </summary>
/// <param name="FamilyId">oauth_token_family.id（取り消しの単位）。</param>
/// <param name="LastUsedAt">最後にトークンを発行した日時（アクセストークンの発行。リフレッシュを含む）。</param>
public sealed record Delegation(
    Guid FamilyId,
    string ClientId,
    string? ClientName,
    OAuthClientRegistration Registration,
    OAuthResource Resource,
    IReadOnlyList<string> Scopes,
    DateTimeOffset GrantedAt,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// 検証を通ったアクセストークン（参照トークン）。閲覧者は「委任元ユーザーの権限 ∩ スコープ ∩ クライアントの上限」で作る（<see cref="Access.ViewerContextFactory"/>）。
/// </summary>
/// <param name="TokenId">oauth_access_token.id。</param>
/// <param name="ClientRef">api_client.id。</param>
/// <param name="PersonId">委任元の人（client credentials は null）。</param>
/// <param name="Roles">委任元の今の有効なロール（要求ごとに role_grant から読む）。</param>
/// <param name="Scopes">トークンのスコープとクライアントの今のスコープの積。</param>
/// <param name="ExpiresAt">トークンの期限（キャッシュから使うときも確かめる）。</param>
public sealed record ValidatedAccessToken(
    Guid TokenId,
    Guid ClientRef,
    string ClientId,
    Guid? PersonId,
    Guid? FamilyId,
    OAuthResource Resource,
    IReadOnlySet<string> Scopes,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> McpTools,
    int? RateLimitPerClient,
    DateTimeOffset ExpiresAt,
    int DisclosureLeadDays = 0)
{
    /// <summary>ユーザー委任（認可コード）のトークンか。</summary>
    public bool IsDelegated => PersonId is not null;

    /// <summary>
    /// クライアントの段のレート制限・不正検知の一時停止の単位。委任は「クライアント × 委任元」（CIMD のクライアントは全員が同じ client_id なので、
    /// 1 人の振る舞いでクライアント全体を止めない。UI と同じ考え方）、client credentials はクライアント。
    /// client_id は URL のこともあるので、管理 API のパスに入れやすい api_client.id を使う。
    /// </summary>
    public string ClientKey => PersonId is { } person ? ClientKeys.For(ClientRef, person) : ClientKeys.For(ClientRef);
}

/// <summary>jin-ji が発行するトークンのクライアントの段の単位（<see cref="Access.AccessContext.ClientKey"/>）。</summary>
public static class ClientKeys
{
    /// <summary>委任元ごとの単位の区切り（メトリクスのラベルにはこれより前だけを使う）。</summary>
    public const char PersonSeparator = '#';

    public static string For(Guid clientRef) => "oauth:" + clientRef.ToString("D");

    public static string For(Guid clientRef, Guid personId) => For(clientRef) + PersonSeparator + personId.ToString("D");
}

/// <summary>redirect_uri の照合（完全一致。ループバックの http だけはポートを問わない。RFC 8252 §7.3）。</summary>
public static class OAuthRedirectUris
{
    /// <summary>ループバックの http の URI か（<c>localhost</c>・<c>127.0.0.1</c>・<c>[::1]</c>）。</summary>
    public static bool IsLoopback(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(parsed.Host);

    /// <summary>要求の redirect_uri が登録したもののどれかと一致するか。</summary>
    public static bool Matches(IEnumerable<string> registered, string requested)
    {
        ArgumentNullException.ThrowIfNull(registered);
        ArgumentNullException.ThrowIfNull(requested);
        return registered.Any(r => Matches(r, requested));
    }

    public static bool Matches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal))
        {
            return true;
        }

        // ループバックはポートだけを任意にする（スキーム・ホスト・パス・クエリは文字列として一致させる）
        if (!IsLoopback(registered) || !IsLoopback(requested))
        {
            return false;
        }

        return string.Equals(WithoutPort(registered), WithoutPort(requested), StringComparison.Ordinal);
    }

    /// <summary>redirect_uri として登録できる形か（https、ループバックの http。フラグメント・利用者情報なし）。</summary>
    public static bool IsAcceptable(string? uri) =>
        uri is { Length: > 0 and <= 2048 }
        && Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
        && string.IsNullOrEmpty(parsed.Fragment) && !uri.Contains('#', StringComparison.Ordinal)
        && string.IsNullOrEmpty(parsed.UserInfo)
        && (parsed.Scheme == Uri.UriSchemeHttps || (parsed.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(parsed.Host)));

    private static bool IsLoopbackHost(string host) =>
        host is "localhost" or "127.0.0.1" or "[::1]" or "::1";

    /// <summary>オリジンのポートを除いた文字列（http://host:port/path → http://host/path）。</summary>
    private static string WithoutPort(string uri)
    {
        var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal) + 3;
        var pathStart = uri.IndexOfAny(['/', '?'], schemeEnd);
        var authority = pathStart < 0 ? uri[schemeEnd..] : uri[schemeEnd..pathStart];
        var rest = pathStart < 0 ? string.Empty : uri[pathStart..];
        var host = authority.StartsWith('[') ? authority[..(authority.IndexOf(']', StringComparison.Ordinal) + 1)] : authority.Split(':')[0];
        return uri[..schemeEnd] + host.ToLowerInvariant() + rest;
    }
}

/// <summary>トークン・コードの接頭辞（種類を見分けるため。JWT とは「.」の有無でも見分けられる）。</summary>
public static class OAuthTokenPrefixes
{
    public const string AccessToken = "jjat_";
    public const string RefreshToken = "jjrt_";
    public const string AuthorizationCode = "jjac_";
    public const string AuthorizationRequest = "jjar_";
    public const string ClientSecret = "jjcs_";

    /// <summary>接頭辞 + 256 ビットの乱数。</summary>
    public static string New(string prefix) => prefix + AuthSecrets.NewToken();

    /// <summary>jin-ji のアクセストークンの形か（接頭辞 + base64url の 43 文字）。</summary>
    public static bool IsAccessToken(string? token) =>
        token is { Length: 48 } && token.StartsWith(AccessToken, StringComparison.Ordinal)
        && token.AsSpan(AccessToken.Length).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0;
}
