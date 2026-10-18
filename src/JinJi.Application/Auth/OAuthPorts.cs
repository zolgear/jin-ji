namespace JinJi.Application.Auth;

/// <summary>
/// トークン発行の表（0012）の保存先（Infrastructure が実装する）。コード・トークン・同意・jti は jin-ji 自身（system）の閲覧者で、
/// クライアントの管理は <see cref="AuthActor"/>（管理 CLI・人事管理者の人）の閲覧者で読み書きする。
/// </summary>
public interface IOAuthStore
{
    // ---- クライアント --------------------------------------------------------------------------------------------

    Task<OAuthClient?> FindClientAsync(string clientId, CancellationToken cancellationToken);

    Task<OAuthClient?> FindClientByRefAsync(Guid clientRef, CancellationToken cancellationToken);

    Task<IReadOnlyList<OAuthClient>> ListClientsAsync(AuthActor actor, OAuthClientStatus? status, CancellationToken cancellationToken);

    /// <summary>クライアントを作る。client_id が重複していれば false。<paramref name="actor"/> が null なら CIMD の取得（jin-ji 自身）として記録する。</summary>
    Task<bool> CreateClientAsync(AuthActor? actor, OAuthClient client, CancellationToken cancellationToken);

    /// <summary>
    /// 変えられる列（状態・表示名・付与・redirect_uri・resource・スコープ・項目・MCP ツール・レート制限・CIMD の取得日時）を保存する。
    /// <paramref name="expectedStatus"/> を指定すれば、今の状態がそれと同じときだけ更新する（同時の承認・却下の取り違えを防ぐ）。更新できなければ false。
    /// </summary>
    Task<bool> UpdateClientAsync(AuthActor? actor, OAuthClient client, OAuthClientStatus? expectedStatus, CancellationToken cancellationToken);

    Task<IReadOnlyList<OAuthClientKey>> GetClientKeysAsync(Guid clientRef, CancellationToken cancellationToken);

    /// <summary>公開鍵を登録する。同じ kid の有効な鍵があれば false。</summary>
    Task<bool> AddClientKeyAsync(AuthActor actor, Guid clientRef, OAuthClientKey key, CancellationToken cancellationToken);

    Task<bool> RevokeClientKeyAsync(AuthActor actor, Guid clientRef, string keyId, CancellationToken cancellationToken);

    /// <summary>有効（期限内・取り消されていない）な client_secret のハッシュ。</summary>
    Task<IReadOnlyList<OAuthClientSecret>> GetActiveClientSecretsAsync(Guid clientRef, DateTimeOffset now, CancellationToken cancellationToken);

    Task AddClientSecretAsync(AuthActor actor, Guid clientRef, OAuthClientSecret secret, CancellationToken cancellationToken);

    /// <summary>有効な client_secret をすべて取り消す。取り消した数。</summary>
    Task<int> RevokeClientSecretsAsync(AuthActor actor, Guid clientRef, CancellationToken cancellationToken);

    // ---- CIMD の許可ドメイン -------------------------------------------------------------------------------------

    Task<IReadOnlyList<CimdAllowedDomain>> ListAllowedDomainsAsync(CancellationToken cancellationToken);

    /// <summary>許可ドメインを足す。既に有効なら false。</summary>
    Task<bool> AddAllowedDomainAsync(AuthActor actor, Guid id, string domain, CancellationToken cancellationToken);

    Task<bool> RemoveAllowedDomainAsync(AuthActor actor, string domain, CancellationToken cancellationToken);

    // ---- 認可の要求・コード --------------------------------------------------------------------------------------

    Task CreateAuthorizationRequestAsync(StoredAuthorizationRequest request, string handleHash, CancellationToken cancellationToken);

    Task<StoredAuthorizationRequest?> FindAuthorizationRequestAsync(string handleHash, CancellationToken cancellationToken);

    /// <summary>
    /// 未決定・期限内の要求に同意（<paramref name="approved"/>）か拒否を記録する（1 回だけ成功する）。同意なら同じトランザクションで
    /// 同意の記録と認可コードを作る。記録できなければ null。
    /// </summary>
    Task<StoredAuthorizationRequest?> DecideAuthorizationRequestAsync(
        string handleHash,
        Guid personId,
        bool approved,
        DateTimeOffset now,
        (Guid CodeId, string CodeHash, DateTimeOffset ExpiresAt, Guid ConsentId)? code,
        CancellationToken cancellationToken);

    /// <summary>
    /// 認可コードを交換する（1 回だけ。コードの行をロックする）。<paramref name="prepare"/>（人の状態の読み込みなど）の後、<paramref name="exchange"/> がコードを見て、
    /// トークンを発行してよいなら材料を返す（null なら何も変えずに <c>Invalid</c>。コードは使用済みにしない）。
    /// 既に使われたコードなら <paramref name="exchange"/> を呼ばず、そのコードから発行した系列を終えて <c>Reused</c> を返す。
    /// </summary>
    Task<CodeExchangeResult> ExchangeAuthorizationCodeAsync(
        string codeHash,
        Func<StoredAuthorizationCode, NewTokens?> exchange,
        Func<StoredAuthorizationCode, Task> prepare,
        CancellationToken cancellationToken);

    // ---- トークン ------------------------------------------------------------------------------------------------

    /// <summary>client credentials のアクセストークンを保存する。</summary>
    Task CreateAccessTokenAsync(NewTokens tokens, CancellationToken cancellationToken);

    /// <summary>
    /// リフレッシュトークンを回転する（1 回だけ。トークンと系列の行をロックする）。<paramref name="prepare"/> の後、<paramref name="rotate"/> がトークンを見て、
    /// 発行してよいなら新しい材料を返す（null なら何も変えずに <c>Invalid</c>）。
    /// 使用済みのトークンなら <paramref name="rotate"/> を呼ばずに系列を終え（再利用の検知）、<c>Reused</c> を返す（系列が既に終わっていれば <c>Invalid</c>）。
    /// </summary>
    Task<RefreshResult> RotateRefreshTokenAsync(
        string tokenHash,
        Func<StoredRefreshToken, NewTokens?> rotate,
        Func<StoredRefreshToken, Task> prepare,
        CancellationToken cancellationToken);

    /// <summary>アクセストークンを引いて検証の材料を返す（1 回の問い合わせ。人の状態・ロール・クライアントの状態を含む）。</summary>
    Task<AccessTokenRecord?> FindAccessTokenAsync(string tokenHash, DateOnly today, CancellationToken cancellationToken);

    /// <summary>クライアントのトークンを失効する（RFC 7009）。アクセストークンはそれだけ、リフレッシュトークンは系列ごと。失効したら true。</summary>
    Task<bool> RevokeTokenAsync(Guid clientRef, string tokenHash, bool refreshToken, CancellationToken cancellationToken);

    /// <summary>系列を終える（管理者の失効など）。終えた系列の数。</summary>
    Task<int> EndFamiliesAsync(Guid? clientRef, Guid? personId, Guid? familyId, TokenFamilyEndKind kind, CancellationToken cancellationToken);

    /// <summary>
    /// 人の生きている委任（終わっておらず、期限内で、使えるリフレッシュトークンかアクセストークンが残っている系列）。新しい付与の順。
    /// </summary>
    Task<IReadOnlyList<Delegation>> ListDelegationsAsync(Guid personId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>client credentials のアクセストークンをすべて失効する（クライアントの停止・管理者の失効）。</summary>
    Task<int> RevokeClientAccessTokensAsync(Guid clientRef, CancellationToken cancellationToken);

    /// <summary>client assertion の jti を記録する。同じクライアントで同じ jti があれば false（再利用）。期限を過ぎた jti は消す。</summary>
    Task<bool> RecordClientAssertionAsync(Guid clientRef, string jti, DateTimeOffset expiresAt, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>認可コードの交換の結果。</summary>
public enum CodeExchangeStatus
{
    /// <summary>発行した。</summary>
    Issued,

    /// <summary>コードが無い・期限切れ・束縛が合わない。</summary>
    Invalid,

    /// <summary>使用済みのコードが再び使われた（発行した系列を終えた）。</summary>
    Reused,
}

public sealed record CodeExchangeResult(CodeExchangeStatus Status, StoredAuthorizationCode? Code, Guid? FamilyId);

/// <summary>リフレッシュトークンの回転の結果。</summary>
public enum RefreshStatus
{
    Issued,
    Invalid,

    /// <summary>使用済みのトークンが再び使われた（系列を終えた）。</summary>
    Reused,
}

public sealed record RefreshResult(RefreshStatus Status, StoredRefreshToken? Token);

/// <summary>アクセストークンの検証の材料（1 回の問い合わせで読む）。</summary>
public sealed record AccessTokenRecord(
    Guid TokenId,
    OAuthClient Client,
    Guid? PersonId,
    Guid? FamilyId,
    OAuthResource Resource,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt,
    bool Revoked,
    bool PersonActive,
    IReadOnlyList<string> Roles);

/// <summary>
/// private_key_jwt の client assertion の検証（Host が Microsoft.IdentityModel で実装する）。
/// 署名（登録した公開鍵・非対称鍵だけ）、iss = sub = client_id、aud = 発行者、exp（今から上限以内）を確かめ、jti と exp を返す。
/// </summary>
public interface IClientAssertionValidator
{
    /// <summary>検証する。通らなければ <see cref="ClientAssertionCheck.Assertion"/> が null（理由は <see cref="ClientAssertionCheck.Failure"/>。ログ用）。</summary>
    Task<ClientAssertionCheck> ValidateAsync(string assertion, OAuthClient client, IReadOnlyList<OAuthClientKey> keys);

    /// <summary>検証の前に、assertion の iss（= client_id）を読む（クライアントを引くため。署名は確かめない）。読めなければ null。</summary>
    string? ReadClientId(string assertion);
}

/// <summary>検証を通った client assertion。</summary>
public sealed record ClientAssertion(string Jti, DateTimeOffset ExpiresAt);

/// <summary>client assertion の検証の結果。</summary>
public sealed record ClientAssertionCheck(ClientAssertion? Assertion, string? Failure);

/// <summary>
/// CIMD の文書の取得（Host が HttpClient で実装する）。https だけ、私設・ループバック・リンクローカルのアドレス宛ては拒否（DNS の解決後のアドレスで判定）、
/// リダイレクトに従わない、大きさ・時間の上限を守る。
/// </summary>
public interface IClientMetadataFetcher
{
    Task<ClientMetadataFetch> FetchAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>取得の結果。失敗なら <see cref="Failure"/>（ログ・監査用の短い理由）。</summary>
/// <param name="CacheFor">HTTP キャッシュ（Cache-Control・Expires）から決めた使い回しの時間。指定が無ければ null。</param>
public sealed record ClientMetadataFetch(string? Json, TimeSpan? CacheFor, string? Failure)
{
    public static ClientMetadataFetch Failed(string failure) => new(null, null, failure);
}
