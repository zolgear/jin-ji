using System.Globalization;
using JinJi.Domain;

namespace JinJi.Application.Access;

/// <summary>
/// 不正検知の設定（appsettings の <c>JinJi:Abuse</c>。アーキテクチャ概要 §7）。しきい値はクライアントごとに数える。
/// </summary>
public sealed class AbuseOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Abuse";

    /// <summary>検知を有効にするか（既定 true）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>数える窓（既定 5 分。スライディングウィンドウ）。</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>403 / 404 の数（列挙攻撃）。</summary>
    public int DeniedThreshold { get; init; } = 50;

    /// <summary>返した人数（短時間の大量の個人参照）。</summary>
    public int PeopleReturnedThreshold { get; init; } = 3000;

    /// <summary>絞り込みの無い一覧の取得の数（全社横断の連続取得）。</summary>
    public int BroadListingThreshold { get; init; } = 30;

    /// <summary>トークンエンドポイントの失敗（クライアントの認証・付与の拒否）の数。接続元アドレスごとに数える。</summary>
    public int TokenFailureThreshold { get; init; } = 30;

    /// <summary>業務時間外（日本時間）の判定と、そのときの返却人数のしきい値。</summary>
    public OffHoursOptions OffHours { get; init; } = new();

    /// <summary>自動スロットルを続ける時間（既定 30 分）。この間に再び検知すると一時停止にする。</summary>
    public TimeSpan ThrottleDuration { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        if (Window <= TimeSpan.Zero || ThrottleDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Window), "不正検知の窓とスロットルの時間は正の時間で指定してください。");
        }

        if (DeniedThreshold <= 0 || PeopleReturnedThreshold <= 0 || BroadListingThreshold <= 0 || OffHours.PeopleReturnedThreshold <= 0 || TokenFailureThreshold <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(DeniedThreshold), "不正検知のしきい値は 1 以上で指定してください。");
        }

        _ = OffHours.Parse();
    }
}

/// <summary>業務時間外（日本時間）。<see cref="Start"/> から翌日の <see cref="End"/> まで（Start &gt; End なら日をまたぐ）と、週末（任意）。祝日は見ない。</summary>
public sealed class OffHoursOptions
{
    /// <summary>業務時間外の開始（HH:mm、日本時間。既定 20:00）。</summary>
    public string Start { get; init; } = "20:00";

    /// <summary>業務時間外の終了（HH:mm、日本時間。既定 07:00）。</summary>
    public string End { get; init; } = "07:00";

    /// <summary>土日を終日業務時間外とするか（既定 true）。</summary>
    public bool IncludeWeekends { get; init; } = true;

    /// <summary>業務時間外の返却人数のしきい値（窓あたり）。</summary>
    public int PeopleReturnedThreshold { get; init; } = 500;

    /// <summary>時刻を解釈する。</summary>
    public (TimeOnly Start, TimeOnly End) Parse()
    {
        if (!TimeOnly.TryParseExact(Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(End, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            throw new ArgumentException("業務時間外の開始・終了は HH:mm で指定してください。", nameof(Start));
        }

        return (start, end);
    }

    /// <summary>日本時間の日時が業務時間外か。</summary>
    public bool Contains(DateTimeOffset utc)
    {
        var local = utc.ToOffset(BusinessDate.UtcOffset);
        if (IncludeWeekends && local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            return true;
        }

        var (start, end) = Parse();
        var time = TimeOnly.FromDateTime(local.DateTime);
        return start <= end ? time >= start && time < end : time >= start || time < end;
    }
}

/// <summary>クライアントの状態。</summary>
public enum AbuseState
{
    /// <summary>通常。</summary>
    Normal,

    /// <summary>自動スロットル中（レート制限を絞る）。期限が来たら通常に戻る。</summary>
    Throttled,

    /// <summary>一時停止（すべての要求を拒否する）。人事管理者が解除するまで続く。</summary>
    Suspended,
}

/// <summary>検知の規則のコード値。</summary>
public static class AbuseRules
{
    /// <summary>403 / 404 の急増（列挙攻撃）。</summary>
    public const string DeniedBurst = "denied_burst";

    /// <summary>短時間の大量の個人参照。</summary>
    public const string MassRead = "mass_read";

    /// <summary>全社横断の連続取得。</summary>
    public const string BroadListing = "broad_listing";

    /// <summary>業務時間外の大量参照。</summary>
    public const string OffHoursRead = "off_hours_read";

    /// <summary>トークンエンドポイントの失敗の急増（接続元アドレスごと。総当たり・盗んだ資格情報の試行）。</summary>
    public const string TokenFailures = "token_failures";

    /// <summary>使用済みのリフレッシュトークン・認可コードの再利用（トークンの盗用の兆候。1 回で段階を上げる）。</summary>
    public const string TokenReuse = "token_reuse";
}

/// <summary>1 リクエストの観測値。</summary>
/// <param name="TokenFailure">トークンエンドポイントの失敗（<see cref="AbuseRules.TokenFailures"/> で数える）。</param>
public sealed record AbuseObservation(int StatusCode, int PeopleReturned, bool IsBroadListing, bool TokenFailure = false);

/// <summary>トークンエンドポイントの不正検知の単位（接続元アドレス。管理 API で解除するときの ID）。</summary>
public static class TokenEndpointAbuseKeys
{
    public const string Prefix = "oauth-token:";

    public static string For(string address) => Prefix + address;
}

/// <summary>状態の遷移（検知・解除）。管理者への通知（ログ・監査ログ・メトリクス）に使う。</summary>
/// <param name="Rule">検知した規則（解除なら null）。</param>
public sealed record AbuseTransition(string ClientId, string? Rule, AbuseState From, AbuseState To, DateTimeOffset At);

/// <summary>クライアントの状態の一覧の 1 行。</summary>
/// <param name="Until">自動スロットルの期限（一時停止・通常では null）。</param>
/// <param name="Rule">直近に検知した規則。</param>
public sealed record AbuseStatus(string ClientId, AbuseState State, DateTimeOffset? Until, string? Rule, DateTimeOffset? Since);

/// <summary>
/// ルールベースの不正検知（アーキテクチャ概要 §7）。検知したら自動スロットル → 一時停止の順に段階を上げる（クライアント単位）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>規則: 403/404 の急増（列挙攻撃）、短時間の大量の個人参照、全社横断の連続取得（絞り込みの無い一覧）、業務時間外（日本時間）の大量参照</item>
/// <item>通常で検知 → 自動スロットル（<see cref="AbuseOptions.ThrottleDuration"/> の間、レート制限を絞る）。数え直しを始める</item>
/// <item>自動スロットル中に再び検知 → 一時停止（すべての要求を拒否）。一時停止は自動では解けず、人事管理者の操作（<see cref="Release"/>）で解除する</item>
/// <item>状態はプロセス内に持つ（単一インスタンス前提。§1）。再起動で通常に戻る</item>
/// <item>管理者への通知（ログ・監査ログ・メトリクス）は、返した遷移を見て呼び出し側が行う</item>
/// </list>
/// </remarks>
public sealed class AbuseDetector
{
    private readonly AbuseOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, ClientState> _clients = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public AbuseDetector(AbuseOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options.Validate();
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>クライアントの今の状態（自動スロットルの期限切れはここで通常に戻す）。</summary>
    public AbuseState StateOf(string clientId)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        lock (_lock)
        {
            return _clients.TryGetValue(clientId, out var state) ? Current(state, _timeProvider.GetUtcNow()) : AbuseState.Normal;
        }
    }

    /// <summary>通常でないクライアントの一覧。</summary>
    public IReadOnlyList<AbuseStatus> List()
    {
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            return
            [
                .. _clients
                    .Select(kv => (Id: kv.Key, State: kv.Value, Current: Current(kv.Value, now)))
                    .Where(c => c.Current != AbuseState.Normal)
                    .OrderBy(c => c.Id, StringComparer.Ordinal)
                    .Select(c => new AbuseStatus(c.Id, c.Current, c.Current == AbuseState.Throttled ? c.State.ThrottledUntil : null, c.State.Rule, c.State.Since)),
            ];
        }
    }

    /// <summary>応答を観測し、規則に当たれば段階を上げる。遷移が無ければ null。</summary>
    public AbuseTransition? Observe(string clientId, AbuseObservation observation)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(observation);
        if (!_options.Enabled)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_clients.TryGetValue(clientId, out var state))
            {
                state = new ClientState();
                _clients.Add(clientId, state);
            }

            var current = Current(state, now);
            if (current == AbuseState.Suspended)
            {
                return null;
            }

            var since = now - _options.Window;
            if (observation.StatusCode is 403 or 404)
            {
                state.Denied.Add(now, 1);
            }

            if (observation.PeopleReturned > 0)
            {
                state.Returned.Add(now, observation.PeopleReturned);
                if (_options.OffHours.Contains(now))
                {
                    state.OffHoursReturned.Add(now, observation.PeopleReturned);
                }
            }

            if (observation.IsBroadListing)
            {
                state.Broad.Add(now, 1);
            }

            if (observation.TokenFailure)
            {
                state.TokenFailures.Add(now, 1);
            }

            var rule = state.TokenFailures.SumSince(since) >= _options.TokenFailureThreshold ? AbuseRules.TokenFailures
                : state.Denied.SumSince(since) >= _options.DeniedThreshold ? AbuseRules.DeniedBurst
                : state.OffHoursReturned.SumSince(since) >= _options.OffHours.PeopleReturnedThreshold ? AbuseRules.OffHoursRead
                : state.Returned.SumSince(since) >= _options.PeopleReturnedThreshold ? AbuseRules.MassRead
                : state.Broad.SumSince(since) >= _options.BroadListingThreshold ? AbuseRules.BroadListing
                : null;
            return rule is null ? null : StepUp(clientId, state, current, rule, now);
        }
    }

    /// <summary>
    /// 1 回で段階を上げる規則（リフレッシュトークンの再利用など）を報告する。通常 → 自動スロットル、自動スロットル中 → 一時停止。遷移を返す（一時停止中・無効なら null）。
    /// </summary>
    public AbuseTransition? Escalate(string clientId, string rule)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        if (!_options.Enabled)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_clients.TryGetValue(clientId, out var state))
            {
                state = new ClientState();
                _clients.Add(clientId, state);
            }

            var current = Current(state, now);
            return current == AbuseState.Suspended ? null : StepUp(clientId, state, current, rule, now);
        }
    }

    private AbuseTransition StepUp(string clientId, ClientState state, AbuseState current, string rule, DateTimeOffset now)
    {
        var next = current == AbuseState.Normal ? AbuseState.Throttled : AbuseState.Suspended;
        state.State = next;
        state.ThrottledUntil = next == AbuseState.Throttled ? now + _options.ThrottleDuration : null;
        state.Rule = rule;
        state.Since = now;
        state.ResetCounts();
        return new AbuseTransition(clientId, rule, current, next, now);
    }

    /// <summary>一時停止・自動スロットルを解除して通常に戻す（人事管理者の操作）。通常だったなら null。</summary>
    public AbuseTransition? Release(string clientId)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            if (!_clients.TryGetValue(clientId, out var state))
            {
                return null;
            }

            var current = Current(state, now);
            if (current == AbuseState.Normal)
            {
                return null;
            }

            _clients.Remove(clientId);
            return new AbuseTransition(clientId, null, current, AbuseState.Normal, now);
        }
    }

    private static AbuseState Current(ClientState state, DateTimeOffset now)
    {
        if (state.State == AbuseState.Throttled && state.ThrottledUntil <= now)
        {
            state.State = AbuseState.Normal;
            state.ThrottledUntil = null;
        }

        return state.State;
    }

    private sealed class ClientState
    {
        public AbuseState State { get; set; }

        public DateTimeOffset? ThrottledUntil { get; set; }

        public string? Rule { get; set; }

        public DateTimeOffset? Since { get; set; }

        public Window Denied { get; } = new();

        public Window Returned { get; } = new();

        public Window OffHoursReturned { get; } = new();

        public Window Broad { get; } = new();

        public Window TokenFailures { get; } = new();

        public void ResetCounts()
        {
            TokenFailures.Clear();
            Denied.Clear();
            Returned.Clear();
            OffHoursReturned.Clear();
            Broad.Clear();
        }
    }

    private sealed class Window
    {
        private readonly Queue<(DateTimeOffset At, long Count)> _entries = new();
        private long _sum;

        public void Add(DateTimeOffset at, long count)
        {
            _entries.Enqueue((at, count));
            _sum += count;
        }

        public long SumSince(DateTimeOffset since)
        {
            while (_entries.TryPeek(out var entry) && entry.At <= since)
            {
                _entries.Dequeue();
                _sum -= entry.Count;
            }

            return _sum;
        }

        public void Clear()
        {
            _entries.Clear();
            _sum = 0;
        }
    }
}
