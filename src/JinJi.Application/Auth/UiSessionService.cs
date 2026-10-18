using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>検証を通った UI のセッション。ロールは要求ごとに DB から読んだもの。</summary>
public sealed record ActiveSession(
    Guid Id,
    SessionPrincipalKind Kind,
    Guid? PersonId,
    Guid? AccountId,
    Guid? BindingId,
    string Provider,
    IReadOnlyList<string> Methods,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// UI（BFF）のセッション。サーバー側に保存し、Cookie には乱数の鍵（256 ビット）だけを持たせる。DB には鍵の SHA-256 を持つ。
/// </summary>
/// <remarks>
/// 要求ごとの検証（<see cref="ValidateAsync"/>）で、次のどれかに当たればセッションを終えて拒否する（次の要求から効く）:
/// 終了済み、最長の期限（ログインから既定 12 時間）・無操作の期限（既定 8 時間）を過ぎた、アカウントが無効、紐付けが取り消された、
/// 人が退職済み・入社前・一時停止中。ロールはセッションに持たせず、要求ごとに role_grant から読む（取り消しが次の要求から効く）。
/// </remarks>
public sealed class UiSessionService
{
    private readonly IAuthStore _store;
    private readonly AuthAuditor _audit;
    private readonly SessionLifetimeOptions _options;
    private readonly TimeProvider _timeProvider;

    public UiSessionService(IAuthStore store, AuthAuditor audit, LoginOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _audit = audit;
        _options = options.Session;
        _timeProvider = timeProvider;
    }

    /// <summary>新しいセッションを作り、Cookie に入れる鍵を返す（ログインのたびに新しいセッション。セッション固定の対策）。</summary>
    public async Task<(string Key, Guid SessionId, DateTimeOffset ExpiresAt)> CreateAsync(NewSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var key = AuthSecrets.NewToken();
        var id = EntityId.New();
        var now = _timeProvider.GetUtcNow();
        var expiresAt = now + _options.AbsoluteTimeout;
        await _store.CreateSessionAsync(id, AuthSecrets.Sha256(key), session, now, expiresAt, cancellationToken);
        return (key, id, expiresAt);
    }

    /// <summary>鍵のセッションを検証する。使えなければ null（終えるべきものは終える）。</summary>
    public async Task<ActiveSession?> ValidateAsync(string key, string? requestId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key) || await _store.FindSessionAsync(AuthSecrets.Sha256(key), cancellationToken) is not { } session || session.Ended)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        SessionEndKind? end = null;
        if (now >= session.ExpiresAt)
        {
            end = SessionEndKind.Expired;
        }
        else if (now - session.LastSeenAt >= _options.IdleTimeout)
        {
            end = SessionEndKind.IdleTimeout;
        }
        else if (session.AccountDisabled)
        {
            end = SessionEndKind.AccountDisabled;
        }
        else if (session.BindingRevoked)
        {
            end = SessionEndKind.BindingRevoked;
        }
        else if (session.PersonId is { } person
                 && await _store.GetPersonStateAsync(person, BusinessDate.Today(_timeProvider), cancellationToken) != PersonLoginState.Active)
        {
            end = SessionEndKind.PersonInactive;
        }

        if (end is { } kind)
        {
            await _store.EndSessionAsync(session.Id, kind, cancellationToken);
            await _audit.RecordAsync(
                new AuthAuditEntry
                {
                    Action = AuthAuditActions.SessionEnd,
                    Outcome = AuditOutcome.Success,
                    ActorType = ActorType.System,
                    Channel = Channel.Ui,
                    TargetType = AuthAuditTargets.Session,
                    TargetId = session.Id,
                    SubjectPersonIds = session.PersonId is { } subject ? [subject] : [],
                    RequestId = requestId,
                },
                cancellationToken);
            return null;
        }

        var roles = session.PersonId is { } personId ? await _store.GetRolesAsync(personId, cancellationToken) : [];
        if (now - session.LastSeenAt >= _options.TouchInterval)
        {
            await _store.TouchSessionAsync(session.Id, now, cancellationToken);
        }

        return new ActiveSession(
            session.Id,
            session.Kind,
            session.PersonId,
            session.AccountId,
            session.BindingId,
            session.Provider,
            session.Methods,
            [.. roles.Where(Roles.Known.Contains)],
            session.CreatedAt,
            session.ExpiresAt);
    }

    /// <summary>鍵のセッションを終える（ログアウト・同じブラウザでの再ログイン）。</summary>
    public async Task<Guid?> EndAsync(string key, SessionEndKind kind, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key) || await _store.FindSessionAsync(AuthSecrets.Sha256(key), cancellationToken) is not { } session || session.Ended)
        {
            return null;
        }

        await _store.EndSessionAsync(session.Id, kind, cancellationToken);
        return session.Id;
    }
}
