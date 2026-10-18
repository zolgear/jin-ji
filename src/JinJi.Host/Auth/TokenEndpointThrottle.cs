using System.Threading.RateLimiting;
using JinJi.Application.Auth;

namespace JinJi.Host.Auth;

/// <summary>
/// トークンエンドポイント（<c>/oauth/token</c>・<c>/oauth/revoke</c>）のレート制限（ADR-0004「ブルートフォース」: IP × client_id）。
/// </summary>
/// <remarks>
/// 接続元アドレスを先に数え、超えていれば client_id の段には数えない（1 つの接続元から多くのクライアントを試させない）。次に接続元 × client_id。
/// 不正検知で自動スロットル中の接続元は、アドレスの段を <see cref="TokenEndpointRateLimitOptions.ThrottledPerAddress"/> に絞る。状態はプロセス内（§1）。
/// </remarks>
internal sealed class TokenEndpointThrottle : IDisposable
{
    private const int SegmentsPerWindow = 6;
    private readonly PartitionedRateLimiter<string> _perAddress;
    private readonly PartitionedRateLimiter<string> _throttled;
    private readonly PartitionedRateLimiter<string> _perClient;
    private readonly TimeSpan _window;

    public TokenEndpointThrottle(AuthorizationServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var limits = options.RateLimit;
        _window = limits.Window;
        _perAddress = PartitionedRateLimiter.Create<string, string>(key => Sliding(key, limits.PerAddress, limits.Window));
        _throttled = PartitionedRateLimiter.Create<string, string>(key => Sliding(key, limits.ThrottledPerAddress, limits.Window));
        _perClient = PartitionedRateLimiter.Create<string, string>(key => Sliding(key, limits.PerAddressAndClient, limits.Window));
    }

    /// <summary>1 回数える。超えていれば待つべき時間（Retry-After）。</summary>
    /// <param name="clientId">要求の client_id（分からなければ null）。</param>
    /// <param name="throttled">接続元が不正検知で自動スロットル中か。</param>
    public TimeSpan? TryAcquire(string address, string? clientId, bool throttled)
    {
        ArgumentNullException.ThrowIfNull(address);
        using (var lease = (throttled ? _throttled : _perAddress).AttemptAcquire(address))
        {
            if (!lease.IsAcquired)
            {
                return RetryAfter(lease);
            }
        }

        if (clientId is null)
        {
            return null;
        }

        using var clientLease = _perClient.AttemptAcquire($"{address}|{clientId}");
        return clientLease.IsAcquired ? null : RetryAfter(clientLease);
    }

    public void Dispose()
    {
        _perAddress.Dispose();
        _throttled.Dispose();
        _perClient.Dispose();
    }

    private TimeSpan RetryAfter(RateLimitLease lease) =>
        lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : _window / SegmentsPerWindow;

    private static RateLimitPartition<string> Sliding(string key, int permits, TimeSpan window) =>
        RateLimitPartition.GetSlidingWindowLimiter(key, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = permits,
            Window = window,
            SegmentsPerWindow = SegmentsPerWindow,
            QueueLimit = 0,
            AutoReplenishment = true,
        });
}

/// <summary>
/// トークンエンドポイントの入口の守り: 不正検知で一時停止中の接続元の拒否、レート制限（<see cref="TokenEndpointThrottle"/>）、
/// 失敗の急増（接続元アドレスごと）とリフレッシュトークン・認可コードの再利用（クライアント × 委任元）の不正検知への報告。
/// </summary>
/// <remarks>
/// 検知の遷移はログ（警告）・監査ログ（<c>abuse.throttled</c> / <c>abuse.suspended</c>。対象は target_type <c>api_client:&lt;単位&gt;</c>）・メトリクスに出す。
/// 解除は既存の <c>POST /api/v1/admin/clients/{単位}/release</c>（単位は <c>oauth-token:&lt;アドレス&gt;</c> か <c>oauth:&lt;api_client.id&gt;#&lt;person ID&gt;</c>）。
/// </remarks>
internal sealed partial class TokenEndpointGuard(
    TokenEndpointThrottle throttle,
    JinJi.Application.Access.AbuseDetector detector,
    JinJi.Application.Access.AccessMetrics metrics,
    AuthAuditor audit,
    ILogger<TokenEndpointGuard> logger)
{
    /// <summary>処理の前の検査。拒否するなら応答、通すなら null。</summary>
    public IResult? Check(HttpContext http, string? clientId)
    {
        ArgumentNullException.ThrowIfNull(http);
        var address = Address(http);
        var state = detector.StateOf(JinJi.Application.Access.TokenEndpointAbuseKeys.For(address));
        if (state == JinJi.Application.Access.AbuseState.Suspended)
        {
            return OAuthEndpoints.OAuthError(http, StatusCodes.Status403Forbidden, "access_denied", "この接続元は不正検知により一時停止されています。");
        }

        if (throttle.TryAcquire(address, clientId, state == JinJi.Application.Access.AbuseState.Throttled) is { } wait)
        {
            http.Response.Headers.RetryAfter = Web.AccessGuardMiddleware.Seconds(wait);
            return OAuthEndpoints.OAuthError(http, StatusCodes.Status429TooManyRequests, "slow_down", "要求が多すぎます。Retry-After の秒数の後に再試行してください。");
        }

        return null;
    }

    /// <summary>結果を不正検知に渡す。</summary>
    public async Task ObserveAsync(HttpContext http, TokenResult result)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Succeeded && result.Error is "invalid_client" or "invalid_grant")
        {
            var key = JinJi.Application.Access.TokenEndpointAbuseKeys.For(Address(http));
            if (detector.Observe(key, new JinJi.Application.Access.AbuseObservation(result.Status, 0, false, TokenFailure: true)) is { } transition)
            {
                await NotifyAsync(transition, http.TraceIdentifier);
            }
        }

        if (result.Reuse is var (clientRef, person)
            && detector.Escalate(ClientKeys.For(clientRef, person), JinJi.Application.Access.AbuseRules.TokenReuse) is { } escalated)
        {
            await NotifyAsync(escalated, http.TraceIdentifier);
        }
    }

    private async Task NotifyAsync(JinJi.Application.Access.AbuseTransition transition, string requestId)
    {
        var action = transition.To == JinJi.Application.Access.AbuseState.Suspended ? "abuse.suspended" : "abuse.throttled";
        metrics.RecordAbuse(JinJi.Application.Access.UiClient.LabelOf(transition.ClientId), transition.Rule ?? "unknown", action);
        LogDetected(logger, transition.ClientId, transition.Rule ?? "unknown", transition.From, transition.To);
        try
        {
            await audit.RecordAsync(
                new AuthAuditEntry
                {
                    Action = action,
                    Outcome = JinJi.Application.Audit.AuditOutcome.Denied,
                    ActorType = Policy.ActorType.System,
                    Channel = Policy.Channel.Api,
                    TargetType = $"api_client:{transition.ClientId}",
                    RequestId = requestId,
                },
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogAuditFailed(logger, ex, transition.ClientId);
        }
    }

    private static string Address(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    [LoggerMessage(Level = LogLevel.Warning, Message = "不正検知（トークンエンドポイント）: {Key} で規則 {Rule} が発火しました（{From} → {To}）。必要なら POST /api/v1/admin/clients/<単位>/release で解除してください")]
    private static partial void LogDetected(ILogger logger, string key, string rule, JinJi.Application.Access.AbuseState from, JinJi.Application.Access.AbuseState to);

    [LoggerMessage(Level = LogLevel.Error, Message = "不正検知の監査ログを書き込めませんでした（{Key}）")]
    private static partial void LogAuditFailed(ILogger logger, Exception exception, string key);
}
