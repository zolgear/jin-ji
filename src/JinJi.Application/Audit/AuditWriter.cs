using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Audit;

/// <summary>監査の結果（audit_event.outcome）。</summary>
public enum AuditOutcome
{
    Success,
    Denied,
    Failed,
}

/// <summary>監査ログの 1 件（audit_event の 1 行）。</summary>
public sealed record AuditRecord
{
    public required Guid Id { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required ActorType ActorType { get; init; }

    public Guid? ActorPersonId { get; init; }

    public string? ActorClientId { get; init; }

    public Guid? OnBehalfOfPersonId { get; init; }

    /// <summary>操作者のローカルアカウント（緊急用の管理者アカウントの操作・ローカルアカウントのログイン）。</summary>
    public Guid? ActorAccountId { get; init; }

    public required Channel Channel { get; init; }

    /// <summary>操作の種類（例: person.read）。</summary>
    public required string Action { get; init; }

    public string? TargetType { get; init; }

    public Guid? TargetId { get; init; }

    /// <summary>参照・変更の対象になった人の ID の集合（重複なし・昇順）。</summary>
    public required IReadOnlyList<Guid> SubjectPersonIds { get; init; }

    public required AuditOutcome Outcome { get; init; }

    public string? RequestId { get; init; }
}

/// <summary>監査ログの保存先（Infrastructure が実装する）。</summary>
public interface IAuditSink
{
    /// <summary>
    /// 1 件を追記する。業務のトランザクションとは別に確定させる（業務の処理が失敗・拒否されても監査は残す）。
    /// RLS の都合で、閲覧者のコンテキストで書き込む。
    /// </summary>
    Task WriteAsync(ViewerContext viewer, AuditRecord record, CancellationToken cancellationToken);
}

/// <summary>
/// 監査ログの書き込み（アーキテクチャ概要 §4.6）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>参照はリクエスト単位で 1 件にまとめ、対象の person ID の集合を記録する（大量読み取りでもログが爆発しない粒度）</item>
/// <item>拒否（denied）と失敗（failed）も記録する。拒否の対象者は「要求された人」を記録する</item>
/// <item>AI エージェントは actor_client_id と委任元（on_behalf_of_person_id）で記録する</item>
/// </list>
/// </remarks>
public sealed class AuditWriter
{
    private readonly IAuditSink _sink;
    private readonly TimeProvider _timeProvider;

    public AuditWriter(IAuditSink sink, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _sink = sink;
        _timeProvider = timeProvider;
    }

    public Task RecordAsync(
        ViewerContext viewer,
        string action,
        AuditOutcome outcome,
        IEnumerable<Guid> subjectPersonIds,
        CancellationToken cancellationToken,
        string? targetType = null,
        Guid? targetId = null)
    {
        var record = Build(viewer, action, outcome, subjectPersonIds, targetType, targetId);
        return _sink.WriteAsync(viewer, record, cancellationToken);
    }

    /// <summary>閲覧者のコンテキストから監査ログの行を組み立てる。</summary>
    public AuditRecord Build(
        ViewerContext viewer,
        string action,
        AuditOutcome outcome,
        IEnumerable<Guid> subjectPersonIds,
        string? targetType = null,
        Guid? targetId = null)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(subjectPersonIds);

        return new AuditRecord
        {
            Id = EntityId.New(),
            OccurredAt = _timeProvider.GetUtcNow(),
            ActorType = viewer.Actor,
            ActorPersonId = viewer.PersonId,
            ActorClientId = viewer.ClientId,
            OnBehalfOfPersonId = viewer.DelegatorPersonId,
            Channel = viewer.Channel,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            SubjectPersonIds = [.. subjectPersonIds.Distinct().Order()],
            Outcome = outcome,
            RequestId = viewer.RequestId,
        };
    }
}
