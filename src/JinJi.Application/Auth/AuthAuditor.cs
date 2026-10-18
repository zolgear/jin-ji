using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>認証の監査ログの操作（audit_event.action）。</summary>
public static class AuthAuditActions
{
    /// <summary>ログイン（成功・拒否・失敗。外部 IdP・パスワード・TOTP・リカバリコード・パスキー）。</summary>
    public const string Login = "auth.login";

    public const string Logout = "auth.logout";

    /// <summary>セッションを要求の検証で終えた（退職・一時停止・紐付けの取り消しなど）。</summary>
    public const string SessionEnd = "auth.session.end";

    public const string BindingCreate = "auth.binding.create";

    public const string BindingRevoke = "auth.binding.revoke";

    public const string ResetLinkIssue = "auth.reset_link.issue";

    public const string ResetLinkUse = "auth.reset_link.use";

    public const string AccountCreate = "auth.account.create";

    public const string AccountUnlock = "auth.account.unlock";

    /// <summary>ローカルアカウントの無効化（セッションの終了・委任の失効を伴う）。</summary>
    public const string AccountDisable = "auth.account.disable";

    public const string AccountEnable = "auth.account.enable";

    public const string PasskeyRegister = "auth.passkey.register";

    /// <summary>本人によるパスキーの削除（拒否も）。</summary>
    public const string PasskeyDelete = "auth.passkey.delete";

    public const string TwoFactorEnable = "auth.two_factor.enable";

    public const string RoleGrant = "auth.role.grant";

    public const string RoleRevoke = "auth.role.revoke";

    /// <summary>緊急用アカウント・人事管理者の認証の管理の拒否（権限なし）。</summary>
    public const string AdminDenied = "auth.admin.denied";
}

/// <summary>監査ログの対象の種類（audit_event.target_type）。</summary>
public static class AuthAuditTargets
{
    public const string LocalAccount = "local_account";
    public const string LoginBinding = "login_binding";
    public const string UnboundLogin = "unbound_login";
    public const string ResetLink = "account_reset_link";
    public const string Session = "ui_session";
    public const string RoleGrant = "role_grant";
}

/// <summary>認証の監査ログの 1 件の材料。</summary>
public sealed record AuthAuditEntry
{
    public required string Action { get; init; }

    public required AuditOutcome Outcome { get; init; }

    public required ActorType ActorType { get; init; }

    public required Channel Channel { get; init; }

    public Guid? ActorPersonId { get; init; }

    public Guid? ActorAccountId { get; init; }

    public string? ActorClientId { get; init; }

    public string? TargetType { get; init; }

    public Guid? TargetId { get; init; }

    public IReadOnlyList<Guid> SubjectPersonIds { get; init; } = [];

    public string? RequestId { get; init; }
}

/// <summary>
/// 認証の監査ログの書き込み。緊急用アカウントの操作は break_glass の閲覧者で、それ以外は jin-ji 自身の閲覧者で書く
/// （ログインの前は人の閲覧者が無いため）。業務の処理とは別のトランザクションで確定させる。
/// </summary>
public sealed class AuthAuditor
{
    private readonly IAuthStore _store;
    private readonly TimeProvider _timeProvider;

    public AuthAuditor(IAuthStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _timeProvider = timeProvider;
    }

    public Task RecordAsync(AuthAuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var record = new AuditRecord
        {
            Id = EntityId.New(),
            OccurredAt = _timeProvider.GetUtcNow(),
            ActorType = entry.ActorType,
            ActorPersonId = entry.ActorPersonId,
            ActorAccountId = entry.ActorAccountId,
            ActorClientId = entry.ActorClientId,
            Channel = entry.Channel,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            SubjectPersonIds = [.. entry.SubjectPersonIds.Distinct().Order()],
            Outcome = entry.Outcome,
            RequestId = entry.RequestId,
        };
        var writer = entry.ActorType == ActorType.BreakGlass && entry.ActorAccountId is { } account
            ? AuthActor.BreakGlass(account)
            : AuthActor.System;
        return _store.WriteAuditAsync(writer, record, cancellationToken);
    }

    /// <summary>管理の操作者（CLI・人事管理者の人・緊急用アカウント）を監査ログの操作者にした材料。</summary>
    public static AuthAuditEntry ForOperator(
        AuthActor actor,
        string action,
        AuditOutcome outcome,
        string? targetType,
        Guid? targetId,
        IReadOnlyList<Guid> subjects,
        string? requestId)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Kind switch
        {
            AuthActorKind.Cli or AuthActorKind.System => new AuthAuditEntry
            {
                Action = action, Outcome = outcome, ActorType = ActorType.System, Channel = Channel.Cli,
                TargetType = targetType, TargetId = targetId, SubjectPersonIds = subjects, RequestId = requestId,
            },
            AuthActorKind.HrAdmin => new AuthAuditEntry
            {
                Action = action, Outcome = outcome, ActorType = ActorType.User, Channel = Channel.Ui, ActorPersonId = actor.PersonId,
                TargetType = targetType, TargetId = targetId, SubjectPersonIds = subjects, RequestId = requestId,
            },
            AuthActorKind.BreakGlass => new AuthAuditEntry
            {
                Action = action, Outcome = outcome, ActorType = ActorType.BreakGlass, Channel = Channel.Ui, ActorAccountId = actor.AccountId,
                TargetType = targetType, TargetId = targetId, SubjectPersonIds = subjects, RequestId = requestId,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(actor)),
        };
    }
}
