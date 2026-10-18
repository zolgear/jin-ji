using JinJi.Application.Audit;

namespace JinJi.Application.Auth;

/// <summary>
/// 認証の表（0011）の保存先（Infrastructure が実装する）。どの操作も <see cref="AuthActor"/> の閲覧者（GUC）で行う。
/// </summary>
/// <remarks>
/// 人の状態（在籍・一時停止）・ロール・紐付けの材料（外部 ID の対応・社員番号・社用メール）は jin-ji 自身（system。hr_admin なし）で読む。
/// 人事データは今日時点の行だけが見え、内示・氏名の履歴は読まない。
/// </remarks>
public interface IAuthStore
{
    // ---- ローカルアカウント --------------------------------------------------------------------------------------

    Task<LocalAccount?> FindAccountAsync(AuthActor actor, Guid accountId, CancellationToken cancellationToken);

    Task<LocalAccount?> FindAccountByNameAsync(AuthActor actor, string normalizedUserName, CancellationToken cancellationToken);

    /// <summary>人の有効なローカルアカウント。無ければ null。</summary>
    Task<LocalAccount?> FindAccountByPersonAsync(AuthActor actor, Guid personId, CancellationToken cancellationToken);

    /// <summary>
    /// ローカルアカウントの一覧（ユーザー名の順）。<paramref name="query"/> はユーザー名の部分一致（正規化して比べる）。
    /// パスキーの数は jin-ji 自身が数える（数だけ。資格情報の中身は返さない）。
    /// </summary>
    Task<IReadOnlyList<LocalAccountSummary>> ListAccountsAsync(AuthActor actor, string? query, int limit, CancellationToken cancellationToken);

    /// <summary>作成する（管理 CLI だけ）。名前が重複していれば false。</summary>
    Task<bool> CreateAccountAsync(LocalAccount account, CancellationToken cancellationToken);

    /// <summary>
    /// 可変の列（パスワードのハッシュ・security stamp・ロックアウト・TOTP・無効化）を保存し、concurrency stamp を進める。
    /// 保存した値が <paramref name="expectedConcurrencyStamp"/> と違えば（他の要求が先に更新した）false。
    /// </summary>
    Task<bool> UpdateAccountAsync(AuthActor actor, LocalAccount account, string expectedConcurrencyStamp, CancellationToken cancellationToken);

    /// <summary>
    /// TOTP の時間窓を受け付けた記録にする。最後に受け付けた時間窓より後のときだけ成功する（同じ・前の時間窓の符号は再利用として拒否。RFC 6238 §5.2）。
    /// </summary>
    Task<bool> ConsumeTotpStepAsync(Guid accountId, long step, CancellationToken cancellationToken);

    Task<IReadOnlyList<StoredPasskey>> GetPasskeysAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>資格情報 ID からアカウントを引く。無ければ null。</summary>
    Task<Guid?> FindAccountByPasskeyAsync(byte[] credentialId, CancellationToken cancellationToken);

    Task AddOrUpdatePasskeyAsync(Guid accountId, StoredPasskey passkey, CancellationToken cancellationToken);

    Task RemovePasskeyAsync(Guid accountId, byte[] credentialId, CancellationToken cancellationToken);

    /// <summary>リカバリコード（SHA-256）を置き換える。</summary>
    Task ReplaceRecoveryCodesAsync(Guid accountId, IReadOnlyCollection<string> codeHashes, CancellationToken cancellationToken);

    /// <summary>未使用のリカバリコードを使用済みにする。無ければ false。</summary>
    Task<bool> RedeemRecoveryCodeAsync(Guid accountId, string codeHash, CancellationToken cancellationToken);

    Task<int> CountRecoveryCodesAsync(Guid accountId, CancellationToken cancellationToken);

    // ---- 人の状態・ロール --------------------------------------------------------------------------------------

    /// <summary>人がログインできる状態か（業務日付で判定）。</summary>
    Task<PersonLoginState> GetPersonStateAsync(Guid personId, DateOnly today, CancellationToken cancellationToken);

    /// <summary>人の有効なロール。</summary>
    Task<IReadOnlyList<string>> GetRolesAsync(Guid personId, CancellationToken cancellationToken);

    /// <summary>ロールを付与する（既に有効なら false）。</summary>
    Task<bool> GrantRoleAsync(AuthActor actor, Guid grantId, Guid personId, string role, CancellationToken cancellationToken);

    /// <summary>ロールを取り消す（有効な付与が無ければ false）。</summary>
    Task<bool> RevokeRoleAsync(AuthActor actor, Guid personId, string role, CancellationToken cancellationToken);

    // ---- 紐付け --------------------------------------------------------------------------------------------------

    Task<LoginBinding?> FindActiveBindingAsync(AuthActor actor, string issuer, string subject, CancellationToken cancellationToken);

    Task<LoginBinding?> FindBindingAsync(AuthActor actor, Guid bindingId, CancellationToken cancellationToken);

    /// <summary>取込の外部 ID の対応（external_identity）から人を引く。</summary>
    Task<IReadOnlyList<Guid>> FindPersonsByExternalIdAsync(string sourceSystem, string externalId, CancellationToken cancellationToken);

    /// <summary>社員番号から人を引く（法人のコードを指定すればその法人だけ）。</summary>
    Task<IReadOnlyList<Guid>> FindPersonsByEmployeeNumberAsync(string employeeNumber, string? legalEntityCode, CancellationToken cancellationToken);

    /// <summary>今日有効な版の社用メールが一致する人（大文字小文字を区別しない。候補の提示だけに使う）。</summary>
    Task<IReadOnlyList<Guid>> FindPersonsByWorkEmailAsync(string email, int limit, CancellationToken cancellationToken);

    /// <summary>人（の不変 ID）があるか。jin-ji 自身で確かめる（緊急用アカウントの閲覧者には person の行が見えないため。ID の有無だけを返す）。</summary>
    Task<bool> PersonExistsAsync(Guid personId, CancellationToken cancellationToken);

    /// <summary>
    /// 紐付けを作る。同じ主体の有効な紐付けがあれば作らずに null（1 つの主体は 1 人だけ）。
    /// <paramref name="resolvesUnboundId"/> を指定すれば、同じトランザクションでその未紐付けのログインを解決済みにする。
    /// </summary>
    Task<LoginBinding?> CreateBindingAsync(
        AuthActor actor,
        LoginBinding binding,
        Guid? resolvesUnboundId,
        CancellationToken cancellationToken);

    /// <summary>紐付けを取り消し、その紐付けのセッションを終える。有効な紐付けが無ければ false。</summary>
    Task<bool> RevokeBindingAsync(AuthActor actor, Guid bindingId, CancellationToken cancellationToken);

    /// <summary>未紐付けのログインを記録する（主体ごとに 1 行。再試行は回数を足し、解決済みなら未解決に戻す）。行の ID を返す。</summary>
    Task<Guid> RecordUnboundLoginAsync(UnboundLoginAttempt attempt, CancellationToken cancellationToken);

    Task<IReadOnlyList<UnboundLogin>> ListUnboundLoginsAsync(AuthActor actor, bool includeResolved, int limit, CancellationToken cancellationToken);

    Task<UnboundLogin?> FindUnboundLoginAsync(AuthActor actor, Guid unboundId, CancellationToken cancellationToken);

    // ---- リンク --------------------------------------------------------------------------------------------------

    /// <summary>リンクを発行する（同じアカウント・用途の未使用のリンクは取り消す）。</summary>
    Task IssueResetLinkAsync(
        AuthActor actor,
        Guid linkId,
        Guid accountId,
        ResetLinkPurpose purpose,
        string tokenHash,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken);

    /// <summary>使えるリンクを引く（使用済みにしない）。</summary>
    Task<ResetLinkTarget?> FindUsableResetLinkAsync(string tokenHash, ResetLinkPurpose purpose, CancellationToken cancellationToken);

    /// <summary>使えるリンクを使用済みにする（1 回だけ成功する）。使えなければ null。</summary>
    Task<ResetLinkTarget?> ConsumeResetLinkAsync(string tokenHash, ResetLinkPurpose purpose, CancellationToken cancellationToken);

    // ---- セッション ----------------------------------------------------------------------------------------------

    Task CreateSessionAsync(Guid sessionId, string keyHash, NewSession session, DateTimeOffset now, DateTimeOffset expiresAt, CancellationToken cancellationToken);

    Task<StoredSession?> FindSessionAsync(string keyHash, CancellationToken cancellationToken);

    Task TouchSessionAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>セッションを終える（終了済みなら何もしない）。</summary>
    Task EndSessionAsync(Guid sessionId, SessionEndKind kind, CancellationToken cancellationToken);

    /// <summary>アカウントの有効なセッションをすべて終える。</summary>
    Task<int> EndAccountSessionsAsync(Guid accountId, SessionEndKind kind, CancellationToken cancellationToken);

    // ---- 監査 ----------------------------------------------------------------------------------------------------

    /// <summary>監査ログを追記する（緊急用アカウントの操作は break_glass の閲覧者、それ以外は jin-ji 自身で書く）。</summary>
    Task WriteAuditAsync(AuthActor actor, AuditRecord record, CancellationToken cancellationToken);
}

/// <summary>Data Protection の鍵の保存先（IXmlRepository は同期の API なので同期で読み書きする）。</summary>
public interface IDataProtectionKeyStore
{
    IReadOnlyList<string> LoadAll();

    void Store(string? friendlyName, string xml);
}
