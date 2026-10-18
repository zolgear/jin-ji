using JinJi.Policy;

namespace JinJi.Application.Access;

/// <summary>
/// 不正検知の状態の遷移（自動スロットル・一時停止・解除）の管理者への通知。今はログ・監査ログ・メトリクスに出す（Host が実装する）。
/// </summary>
public interface IAbuseNotifier
{
    /// <param name="transition">遷移。</param>
    /// <param name="actor">遷移を起こした閲覧者（検知なら対象のクライアントの要求の閲覧者、解除なら解除した人事管理者）。</param>
    Task NotifyAsync(AbuseTransition transition, ViewerContext actor, CancellationToken cancellationToken);
}

/// <summary>管理操作（不正検知の制限の一覧・解除）の権限。</summary>
public static class AdminAccess
{
    /// <summary>
    /// 人事管理者の人か。AI エージェント（委任元が人事管理者でも）・社内システムは不可。一時停止はエージェント自身の暴走を止める手段なので、エージェントに解除させない。
    /// </summary>
    public static bool IsHrAdminUser(ViewerContext viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return viewer.Actor == ActorType.User && viewer.IsHrAdmin;
    }
}
