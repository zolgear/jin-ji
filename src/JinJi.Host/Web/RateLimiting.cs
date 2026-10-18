using System.Threading.RateLimiting;
using JinJi.Api;
using JinJi.Application.Access;
using JinJi.Policy;

namespace JinJi.Host.Web;

/// <summary>
/// レート制限（ASP.NET Core RateLimiter。アーキテクチャ概要 §7）。経路（API・MCP）ごとの設定（<see cref="RateLimitOptions"/>）で、
/// クライアント → クライアント × 利用者 → クライアント × 利用者 × エンドポイント種別 の 3 段を連ねる。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>どの段もスライディングウィンドウ（窓を 6 区間に分ける）。待たせずにすぐ 429 と Retry-After を返す</item>
/// <item>不正検知で自動スロットル中のクライアントは、クライアントの段を <see cref="RateLimitProfile.ThrottledPerClient"/> に絞る</item>
/// <item>未認証の要求（ヘルスチェック・OpenAPI 文書・401 になる要求）は接続元アドレスごとに制限する</item>
/// <item>返却人数の制限（大量抽出対策）は入口の共通処理（<see cref="AccessGuardMiddleware"/>）が別に行う</item>
/// <item>区分けの値は入口の共通処理が置いた <see cref="AccessContext"/> から読む（このミドルウェアは共通処理の後に置く）。
/// クライアントの段の単位は <see cref="AccessContext.ClientKey"/>（UI のセッションは人ごと）</item>
/// </list>
/// </remarks>
internal static class RateLimiting
{
    private const int SegmentsPerWindow = 6;

    public static IServiceCollection AddJinJiRateLimiting(this IServiceCollection services, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(http => ClientPartition(http, options)),
                PartitionedRateLimiter.Create<HttpContext, string>(http => UserPartition(http, options)),
                PartitionedRateLimiter.Create<HttpContext, string>(http => EndpointPartition(http, options)));
            limiter.OnRejected = async (context, _) =>
            {
                var http = context.HttpContext;
                var wait = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? retryAfter
                    : Profile(http, options)?.Window / SegmentsPerWindow ?? TimeSpan.FromSeconds(10);
                http.Response.Headers.RetryAfter = AccessGuardMiddleware.Seconds(wait);
                if (http.Features.Get<AccessContext>() is { } access)
                {
                    access.RejectReason = RejectReasons.RateLimited;
                }

                await AccessGuardMiddleware.WriteProblemAsync(
                    http,
                    StatusCodes.Status429TooManyRequests,
                    "リクエストが多すぎます",
                    "リクエスト数の上限に達しました。Retry-After の秒数の後に再試行してください。",
                    Problems.RateLimitedType);
            };
        });
        return services;
    }

    private static RateLimitPartition<string> ClientPartition(HttpContext http, RateLimitOptions options)
    {
        if (Request(http, options) is not var (channel, profile, access))
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        if (access.ClientKey is not { } client)
        {
            var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return Sliding($"{channel}|anonymous|{address}", profile.AnonymousPerAddress, profile.Window);
        }

        var throttled = http.RequestServices.GetRequiredService<AbuseDetector>().StateOf(client) == AbuseState.Throttled;
        if (throttled)
        {
            return Sliding($"{channel}|{client}|throttled", Math.Min(profile.ThrottledPerClient, access.ClientRateLimit ?? int.MaxValue), profile.Window);
        }

        // クライアントの登録でレート制限を決めていればそれを使う（区分けに値を含めて、変更の後は新しい制限で数え直す）
        return access.ClientRateLimit is { } limit
            ? Sliding($"{channel}|{client}|{limit}", limit, profile.Window)
            : Sliding($"{channel}|{client}", profile.PerClient, profile.Window);
    }

    private static RateLimitPartition<string> UserPartition(HttpContext http, RateLimitOptions options) =>
        Request(http, options) is var (channel, profile, access) && access.ClientKey is { } client
            ? Sliding($"{channel}|{client}|{access.Subject}", profile.PerUser, profile.Window)
            : RateLimitPartition.GetNoLimiter(string.Empty);

    private static RateLimitPartition<string> EndpointPartition(HttpContext http, RateLimitOptions options)
    {
        if (Request(http, options) is not var (channel, profile, access) || access.ClientKey is not { } client)
        {
            return RateLimitPartition.GetNoLimiter(string.Empty);
        }

        // MCP はツールごとの種別を入口の共通処理が置く（1 つのエンドポイントで全ツールを受けるため）
        var kind = access.Kind?.Kind ?? http.GetEndpoint()?.Metadata.GetMetadata<RequestKindMetadata>()?.Kind ?? RequestKind.Other;
        return profile.PerEndpointFor(kind) is { } limit
            ? Sliding($"{channel}|{client}|{access.Subject}|{RequestKinds.ToCode(kind)}", limit, profile.Window)
            : RateLimitPartition.GetNoLimiter(string.Empty);
    }

    /// <summary>API・MCP の要求なら、経路・設定・観測値。それ以外（メトリクス）なら null。</summary>
    private static (string Channel, RateLimitProfile Profile, AccessContext Access)? Request(HttpContext http, RateLimitOptions options)
    {
        if (http.Features.Get<AccessContext>() is not { } access)
        {
            return null;
        }

        var surface = http.RequestServices.GetRequiredService<SurfacePortMap>().Resolve(http.Connection.LocalPort);
        var channel = surface == Surface.Mcp ? Channel.Mcp : Channel.Api;
        return (AccessCodes.ToCode(channel), options.For(channel), access);
    }

    private static RateLimitProfile? Profile(HttpContext http, RateLimitOptions options) => Request(http, options)?.Profile;

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
