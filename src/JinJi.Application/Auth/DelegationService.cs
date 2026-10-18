using JinJi.Application.Audit;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>
/// 本人による委任（同意画面で MCP クライアントなどに許可した、トークンの系列）の一覧と取り消し。UI でログインした本人だけが使う（アカウントの画面）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>取り消しは系列を終える（<c>end_kind = user</c>）。系列のリフレッシュトークン・アクセストークンは次の要求から使えない。
/// 同じプロセスの検証のキャッシュは捨て、他のプロセスはキャッシュの秒数以内に効く（§6.5）</item>
/// <item>他人の系列は取り消せない（人で絞って終える。無ければ 404 と同じ扱い）</item>
/// <item>取り消しは監査ログ（<c>auth.consent.revoke</c>。操作者は本人、経路 ui、対象は系列）に残す</item>
/// </list>
/// </remarks>
public sealed class DelegationService
{
    /// <summary>本人による委任の取り消し（監査ログの操作）。</summary>
    public const string RevokeAction = "auth.consent.revoke";

    private readonly IOAuthStore _store;
    private readonly AccessTokenValidator _validator;
    private readonly AuthAuditor _audit;
    private readonly TimeProvider _timeProvider;

    public DelegationService(IOAuthStore store, AccessTokenValidator validator, AuthAuditor audit, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _validator = validator;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    /// <summary>本人の生きている委任（新しい付与の順）。</summary>
    public Task<IReadOnlyList<Delegation>> ListAsync(Guid personId, CancellationToken cancellationToken) =>
        _store.ListDelegationsAsync(personId, _timeProvider.GetUtcNow(), cancellationToken);

    /// <summary>本人の委任を取り消す。本人の生きている系列でなければ false。</summary>
    public async Task<bool> RevokeAsync(Guid personId, Guid familyId, string? requestId, CancellationToken cancellationToken)
    {
        var ended = await _store.EndFamiliesAsync(null, personId, familyId, TokenFamilyEndKind.User, cancellationToken) > 0;
        if (ended)
        {
            _validator.Invalidate();
        }

        await _audit.RecordAsync(
            new AuthAuditEntry
            {
                Action = RevokeAction,
                Outcome = ended ? AuditOutcome.Success : AuditOutcome.Denied,
                ActorType = ActorType.User,
                Channel = Channel.Ui,
                ActorPersonId = personId,
                ActorClientId = Access.UiClient.Id,
                TargetType = OAuthAuditTargets.TokenFamily,
                TargetId = familyId,
                SubjectPersonIds = [personId],
                RequestId = requestId,
            },
            cancellationToken);
        return ended;
    }
}
