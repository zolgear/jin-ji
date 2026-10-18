using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace JinJi.Application.Auth;

/// <summary>ローカルアカウントの種類（local_account.kind）。</summary>
public enum LocalAccountKind
{
    /// <summary>人のアカウント（person に紐付く）。</summary>
    Person,

    /// <summary>緊急用の管理者アカウント（person に紐付かない。パスキーだけでログインし、権限は認証の復旧だけ）。</summary>
    BreakGlass,
}

/// <summary>
/// ローカルアカウント（ASP.NET Core Identity のユーザー）。Identity のストアが値を書き換え、更新でまとめて保存する。
/// </summary>
/// <remarks>TOTP の共有鍵は暗号化した値（<see cref="AuthenticatorKeyProtected"/>）だけを持つ。暗号化・復号は Host のストアが行う。</remarks>
public sealed class LocalAccount
{
    public Guid Id { get; set; }

    public LocalAccountKind Kind { get; set; }

    public Guid? PersonId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string NormalizedUserName { get; set; } = string.Empty;

    public string? PasswordHash { get; set; }

    public string SecurityStamp { get; set; } = string.Empty;

    public string ConcurrencyStamp { get; set; } = string.Empty;

    public DateTimeOffset? LockoutEnd { get; set; }

    public int AccessFailedCount { get; set; }

    public bool TwoFactorEnabled { get; set; }

    public string? AuthenticatorKeyProtected { get; set; }

    public DateTimeOffset? DisabledAt { get; set; }

    /// <summary>最後に受け付けた TOTP の時間窓（読み取り専用。受け付けは <see cref="IAuthStore.ConsumeTotpStepAsync"/> が 1 回だけ記録する）。</summary>
    public long? TotpLastStep { get; set; }

    public bool IsBreakGlass => Kind == LocalAccountKind.BreakGlass;
}

/// <summary>ローカルアカウントの一覧の 1 行（管理の画面。資格情報の値は持たない）。</summary>
/// <param name="PasskeyCount">登録したパスキーの数（パスキーの表は jin-ji 自身だけが読めるので、数だけを jin-ji 自身が数える）。</param>
public sealed record LocalAccountSummary(
    Guid Id,
    LocalAccountKind Kind,
    Guid? PersonId,
    string UserName,
    bool HasPassword,
    bool TwoFactorEnabled,
    int PasskeyCount,
    DateTimeOffset? LockoutEnd,
    DateTimeOffset? DisabledAt,
    DateTimeOffset CreatedAt);

/// <summary>ローカルアカウントの名前の扱い。</summary>
public static class LocalAccountNames
{
    /// <summary>照合用の正規化（NFKC + 大文字）。Identity の <c>ILookupNormalizer</c> もこれを使う。</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    }

    /// <summary>ユーザー名として使えるか（英数字と <c>. _ - @</c>、3〜64 文字）。</summary>
    public static bool IsValid(string? name) =>
        name is { Length: >= 3 and <= 64 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '@');
}

/// <summary>保存したパスキー（Identity の UserPasskeyInfo と同じ項目）。</summary>
public sealed record StoredPasskey(
    byte[] CredentialId,
    byte[] PublicKey,
    DateTimeOffset CreatedAt,
    uint SignCount,
    IReadOnlyList<string>? Transports,
    bool IsUserVerified,
    bool IsBackupEligible,
    bool IsBackedUp,
    byte[] AttestationObject,
    byte[] ClientDataJson,
    string? Name);

/// <summary>
/// 認証の表を読み書きする主体。DB の閲覧者（GUC）を決める（0011 の冒頭）。
/// </summary>
public enum AuthActorKind
{
    /// <summary>jin-ji 自身（ログインの処理・セッションの検証・鍵）。GUC は system（ロールなし）。</summary>
    System,

    /// <summary>管理 CLI（jin-ji の運用者）。GUC は system（ロールなし）。記録の主体は cli。</summary>
    Cli,

    /// <summary>人事管理者の人（UI のセッション）。GUC は user + hr_admin。</summary>
    HrAdmin,

    /// <summary>緊急用の管理者アカウント。GUC は break_glass（人事データは何も見えない）。</summary>
    BreakGlass,
}

/// <summary>認証の表を読み書きする主体と、その人・アカウント。</summary>
public sealed record AuthActor
{
    private AuthActor(AuthActorKind kind, Guid? personId, Guid? accountId)
    {
        Kind = kind;
        PersonId = personId;
        AccountId = accountId;
    }

    public AuthActorKind Kind { get; }

    public Guid? PersonId { get; }

    public Guid? AccountId { get; }

    public static AuthActor System { get; } = new(AuthActorKind.System, null, null);

    public static AuthActor Cli { get; } = new(AuthActorKind.Cli, null, null);

    public static AuthActor HrAdmin(Guid personId) => new(AuthActorKind.HrAdmin, personId, null);

    public static AuthActor BreakGlass(Guid accountId) => new(AuthActorKind.BreakGlass, null, accountId);

    /// <summary>記録の主体のコード値（created_by_kind など）。</summary>
    public string Code => Kind switch
    {
        AuthActorKind.System => "login",
        AuthActorKind.Cli => "cli",
        AuthActorKind.HrAdmin => "person",
        AuthActorKind.BreakGlass => "break_glass",
        _ => throw new ArgumentOutOfRangeException(nameof(Kind)),
    };
}

/// <summary>紐付けの根拠（login_binding.basis）。</summary>
public enum BindingBasis
{
    /// <summary>Entra の oid が取込の外部 ID の対応（external_identity）と一致し、tid が設定のテナントと一致。</summary>
    EntraObjectId,

    /// <summary>IdP ごとに信頼すると設定した社員番号のクレームが、person の社員番号と一致。</summary>
    EmployeeNumber,

    /// <summary>手動（人事管理者・緊急用アカウント・管理 CLI）。</summary>
    Manual,
}

/// <summary>外部 IdP の主体と person の紐付け。</summary>
public sealed record LoginBinding(
    Guid Id,
    string Provider,
    string Issuer,
    string Subject,
    Guid PersonId,
    BindingBasis Basis,
    DateTimeOffset CreatedAt);

/// <summary>紐付けできなかったログインの記録（の材料）。</summary>
public sealed record UnboundLoginAttempt(
    string Provider,
    string Issuer,
    string Subject,
    string? TenantId,
    string? EmployeeNumber,
    string? Email,
    string? DisplayName,
    IReadOnlyList<Guid> CandidatePersonIds);

/// <summary>紐付けできなかったログイン（unbound_login の 1 行）。</summary>
public sealed record UnboundLogin(
    Guid Id,
    string Provider,
    string Issuer,
    string Subject,
    string? TenantId,
    string? EmployeeNumber,
    string? Email,
    string? DisplayName,
    IReadOnlyList<Guid> CandidatePersonIds,
    int AttemptCount,
    DateTimeOffset FirstAttemptedAt,
    DateTimeOffset LastAttemptedAt,
    DateTimeOffset? ResolvedAt,
    Guid? ResolvedBindingId);

/// <summary>リンクの用途（account_reset_link.purpose）。</summary>
public enum ResetLinkPurpose
{
    /// <summary>パスワードの設定・再設定。</summary>
    Password,

    /// <summary>パスキーの登録（緊急用アカウントの初期設定・再登録、管理系ロールの人の多要素の登録）。</summary>
    Passkey,
}

/// <summary>使えるリンク（未使用・期限内・取り消されていない）。</summary>
public sealed record ResetLinkTarget(Guid LinkId, Guid AccountId, ResetLinkPurpose Purpose, DateTimeOffset ExpiresAt);

/// <summary>人のログインの可否。</summary>
public enum PersonLoginState
{
    /// <summary>在籍中（入社済み・退職前）で、一時停止中でない。</summary>
    Active,

    /// <summary>退職済み・入社前・一時停止中、または今日の属性の版が無い。</summary>
    Inactive,
}

/// <summary>UI のセッションの種類（ui_session.principal_kind）。</summary>
public enum SessionPrincipalKind
{
    Person,
    BreakGlass,
}

/// <summary>新しく作るセッション。</summary>
/// <param name="Provider">ローカルアカウントは <c>local</c>、外部 IdP は設定の名前。</param>
/// <param name="Methods">認証の方法（RFC 8176 の amr に倣う: pwd / otp / rc / hwk / fed）。</param>
public sealed record NewSession(
    SessionPrincipalKind Kind,
    Guid? PersonId,
    Guid? AccountId,
    Guid? BindingId,
    string Provider,
    IReadOnlyList<string> Methods);

/// <summary>セッションの終了の種類（ui_session.end_kind）。</summary>
public enum SessionEndKind
{
    Logout,
    IdleTimeout,
    Expired,
    PersonInactive,
    AccountDisabled,
    CredentialChanged,
    BindingRevoked,
    Replaced,
}

/// <summary>保存したセッションと、検証に要る関連の状態（アカウントの無効化・紐付けの取り消し）。</summary>
public sealed record StoredSession(
    Guid Id,
    SessionPrincipalKind Kind,
    Guid? PersonId,
    Guid? AccountId,
    Guid? BindingId,
    string Provider,
    IReadOnlyList<string> Methods,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    bool Ended,
    bool AccountDisabled,
    bool BindingRevoked);

/// <summary>秘密の値（リンク・セッションの鍵・リカバリコード）の扱い。</summary>
public static class AuthSecrets
{
    /// <summary>256 ビットの乱数（base64url、パディングなし）。</summary>
    public static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>保存用の SHA-256（小文字の 16 進）。</summary>
    public static string Sha256(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
