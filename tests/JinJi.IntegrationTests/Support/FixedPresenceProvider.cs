using System.Collections.Concurrent;
using JinJi.Application.Presence;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// テスト用の当日の所在の提供（固定の値を返す）。配備側が登録する <see cref="IPresenceProvider"/> の代わりに DI で差し替える。
/// 問い合わせ（日付・階・人）を記録し、時点指定で呼ばれないこと・今日だけ呼ばれることを確かめる。
/// </summary>
internal sealed class FixedPresenceProvider(IReadOnlyList<PresenceEntry> entries) : IPresenceProvider
{
    private readonly ConcurrentQueue<PresenceQuery> _queries = new();

    public bool IsAvailable => true;

    /// <summary>受けた問い合わせ（順に）。</summary>
    public IReadOnlyList<PresenceQuery> Queries => [.. _queries];

    public Task<IReadOnlyList<PresenceEntry>> GetAsync(PresenceQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        _queries.Enqueue(query);
        IReadOnlyList<PresenceEntry> hits = [.. entries.Where(e =>
            (query.FloorId is null || e.FloorId == query.FloorId) && (query.PersonIds.Count == 0 || query.PersonIds.Contains(e.PersonId)))];
        return Task.FromResult(hits);
    }
}
