using JinJi.Policy;

namespace JinJi.Application.Access;

/// <summary>
/// 「返却した人数 / 時間」の制限（大量抽出対策。アーキテクチャ概要 §7）。クライアント × 利用者と、クライアントの合計の 2 段で数える。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>状態はプロセス内に持つ（単一インスタンス前提。§1）。再起動で数え直しになる</item>
/// <item>人の情報を返すエンドポイントの前に <see cref="RetryAfter"/> で残りを確かめ、応答の後に返した人数を <see cref="Add"/> で足す。
/// 前もって件数は分からないので、上限の直前の 1 回は最大でページの大きさだけ超え得る（ページの上限で抑える）</item>
/// <item>窓はスライディングウィンドウ（記録の時刻と人数を持ち、窓の外の記録を捨てる）</item>
/// </list>
/// </remarks>
public sealed class ReturnedPeopleLimiter
{
    private readonly RateLimitOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private long _operations;

    public ReturnedPeopleLimiter(RateLimitOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>上限に達していれば、再試行までの時間。達していなければ null。</summary>
    public TimeSpan? RetryAfter(Channel channel, string clientId, string subject)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(subject);
        var profile = _options.For(channel);
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            var wait = Wait(ClientKey(channel, clientId), profile.ReturnedPeoplePerClient, profile.ReturnedPeopleWindow, now);
            var userWait = Wait(UserKey(channel, clientId, subject), profile.ReturnedPeoplePerUser, profile.ReturnedPeopleWindow, now);
            return (wait, userWait) switch
            {
                (null, null) => null,
                ({ } a, { } b) => a > b ? a : b,
                _ => wait ?? userWait,
            };
        }
    }

    /// <summary>返した人数を足す。</summary>
    public void Add(Channel channel, string clientId, string subject, int count)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            Get(ClientKey(channel, clientId)).Add(now, count);
            Get(UserKey(channel, clientId, subject)).Add(now, count);
            if (++_operations % 1024 == 0)
            {
                Sweep(now);
            }
        }
    }

    private static string ClientKey(Channel channel, string clientId) => $"{AccessCodes.ToCode(channel)}\n{clientId}";

    private static string UserKey(Channel channel, string clientId, string subject) => $"{AccessCodes.ToCode(channel)}\n{clientId}\n{subject}";

    private Counter Get(string key)
    {
        if (!_counters.TryGetValue(key, out var counter))
        {
            counter = new Counter();
            _counters.Add(key, counter);
        }

        return counter;
    }

    private TimeSpan? Wait(string key, int limit, TimeSpan window, DateTimeOffset now)
    {
        if (!_counters.TryGetValue(key, out var counter))
        {
            return null;
        }

        counter.Prune(now - window);
        if (counter.Sum < limit)
        {
            return null;
        }

        // 窓から外れていけば上限を下回る最初の時刻まで待たせる
        var remaining = counter.Sum;
        foreach (var (at, count) in counter.Entries)
        {
            remaining -= count;
            if (remaining < limit)
            {
                var wait = at + window - now;
                return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;
            }
        }

        return window;
    }

    private void Sweep(DateTimeOffset now)
    {
        var window = new[] { _options.Api.ReturnedPeopleWindow, _options.Mcp.ReturnedPeopleWindow }.Max();
        foreach (var key in _counters.Keys.ToList())
        {
            var counter = _counters[key];
            counter.Prune(now - window);
            if (counter.Sum == 0)
            {
                _counters.Remove(key);
            }
        }
    }

    private sealed class Counter
    {
        private readonly Queue<(DateTimeOffset At, int Count)> _entries = new();

        public long Sum { get; private set; }

        public IEnumerable<(DateTimeOffset At, int Count)> Entries => _entries;

        public void Add(DateTimeOffset at, int count)
        {
            _entries.Enqueue((at, count));
            Sum += count;
        }

        public void Prune(DateTimeOffset before)
        {
            while (_entries.TryPeek(out var entry) && entry.At <= before)
            {
                _entries.Dequeue();
                Sum -= entry.Count;
            }
        }
    }
}
