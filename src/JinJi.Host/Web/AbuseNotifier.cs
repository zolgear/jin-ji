using JinJi.Application.Access;
using JinJi.Application.Audit;
using JinJi.Policy;

namespace JinJi.Host.Web;

/// <summary>
/// 不正検知の遷移の管理者への通知。今はログ（警告）・監査ログ・メトリクスに出す（メール・チャットへの通知は後で足す）。
/// </summary>
/// <remarks>
/// 監査ログの action は <c>abuse.throttled</c> / <c>abuse.suspended</c> / <c>abuse.released</c>。検知は対象のクライアントの閲覧者で
/// （actor_client_id が対象）、解除は解除した人事管理者で記録し、対象のクライアントは target_type（<c>api_client:&lt;ID&gt;</c>）に書く。
/// 監査ログの書き込みに失敗しても要求の処理は止めない（ログに残す）。
/// </remarks>
internal sealed partial class AbuseNotifier(AuditWriter audit, AccessMetrics metrics, ILogger<AbuseNotifier> logger) : IAbuseNotifier
{
    public async Task NotifyAsync(AbuseTransition transition, ViewerContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(actor);

        var action = transition.To switch
        {
            AbuseState.Throttled => "abuse.throttled",
            AbuseState.Suspended => "abuse.suspended",
            _ => "abuse.released",
        };
        var rule = transition.Rule ?? "release";
        metrics.RecordAbuse(UiClient.LabelOf(transition.ClientId), rule, action);
        if (transition.To == AbuseState.Normal)
        {
            LogReleased(logger, transition.ClientId, transition.From, actor.EffectivePersonId);
        }
        else
        {
            LogDetected(logger, transition.ClientId, rule, transition.From, transition.To);
        }

        try
        {
            await audit.RecordAsync(
                actor,
                action,
                transition.To == AbuseState.Normal ? AuditOutcome.Success : AuditOutcome.Denied,
                [],
                CancellationToken.None,
                targetType: $"api_client:{transition.ClientId}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuditFailed(logger, ex, transition.ClientId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "不正検知: クライアント {ClientId} で規則 {Rule} が発火しました（{From} → {To}）。管理者は状態を確認し、必要なら POST /api/v1/admin/clients/<クライアント ID>/release で解除してください")]
    private static partial void LogDetected(ILogger logger, string clientId, string rule, AbuseState from, AbuseState to);

    [LoggerMessage(Level = LogLevel.Warning, Message = "不正検知: クライアント {ClientId} の制限（{From}）を人事管理者 {PersonId} が解除しました")]
    private static partial void LogReleased(ILogger logger, string clientId, AbuseState from, Guid? personId);

    [LoggerMessage(Level = LogLevel.Error, Message = "不正検知の監査ログを書き込めませんでした（クライアント {ClientId}）")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception, string clientId);
}
