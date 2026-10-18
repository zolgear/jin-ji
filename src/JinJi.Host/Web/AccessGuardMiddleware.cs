using System.Diagnostics;
using System.Globalization;
using JinJi.Api;
using JinJi.Application.Access;
using JinJi.Application.Audit;
using JinJi.Application.Auth;
using JinJi.Mcp;
using JinJi.Policy;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace JinJi.Host.Web;

/// <summary>
/// API・MCP の入口の共通処理。認証（トークンの検証）の後、レート制限の前に動く。
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>トークンから閲覧者を作る（<see cref="ViewerContextFactory"/>。経路は接続を受けたポートの公開面で決める）。作れなければ 403。
/// jin-ji が発行したトークンは「委任元ユーザーの権限 ∩ スコープ ∩ クライアントの上限」の閲覧者にし、スコープが足りなければ 403（insufficient_scope）。
/// UI のセッション（Cookie）は人の閲覧者（経路 UI）にする。緊急用アカウントのセッションは閲覧者を作らず、
/// 認証の復旧のエンドポイント（<see cref="BreakGlassAllowedMetadata"/>）以外は 403（人事情報を見せない）</item>
/// <item>一時停止中のクライアントを拒否する（403。人事管理者の人の管理操作だけは通す）</item>
/// <item>人の情報を返すエンドポイントで、返却人数の上限に達していれば 429 と Retry-After</item>
/// <item>MCP: 停止スイッチ（<see cref="McpOptions.Enabled"/>）が切れていれば、MCP のポートの要求（ヘルスチェックを除く）をすべて 503。
/// 受け口の要求は JSON-RPC の本文を先に読み（<see cref="McpRequestClassifier"/>）、ツールの種別でレート制限・返却人数を数え、
/// ツールのスコープ（提案のツールは jinji.propose）が足りなければ 403 と insufficient_scope</item>
/// <item>応答の後: 返却人数を数え、メトリクスを記録し、不正検知に観測値を渡す（遷移があれば管理者に通知）</item>
/// </list>
/// メトリクスの公開面（/metrics）はこの処理の対象外。
/// </remarks>
internal sealed partial class AccessGuardMiddleware(
    RequestDelegate next,
    SurfacePortMap ports,
    ViewerContextFactory viewers,
    AbuseDetector detector,
    ReturnedPeopleLimiter returnedPeople,
    AccessMetrics metrics,
    IAbuseNotifier notifier,
    AuthorizationServerOptions server,
    IOptionsMonitor<McpOptions> mcp,
    ILogger<AccessGuardMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var surface = ports.Resolve(http.Connection.LocalPort);
        if (surface is not (Surface.Api or Surface.Mcp))
        {
            await next(http);
            return;
        }

        var channel = surface == Surface.Mcp ? Channel.Mcp : Channel.Api;
        var kind = http.GetEndpoint()?.Metadata.GetMetadata<RequestKindMetadata>() ?? new RequestKindMetadata(RequestKind.Other, false);
        var access = new AccessContext();
        http.Features.Set(access);
        var started = Stopwatch.GetTimestamp();
        var clientLabel = AccessMetrics.AnonymousClient;

        try
        {
            if (channel == Channel.Mcp && !mcp.CurrentValue.Enabled && !IsHealthCheck(http))
            {
                // 停止スイッチ: 認証・ツールの前に止める（REST API・UI には影響しない）
                access.RejectReason = RejectReasons.McpDisabled;
                http.Response.Headers.RetryAfter = "300";
                await WriteProblemAsync(http, StatusCodes.Status503ServiceUnavailable, "MCP は停止中です", "管理者が MCP サーバーを停止しています。", McpDisabledType);
                return;
            }

            if (SessionClaims.IsBreakGlass(http.User))
            {
                // 緊急用アカウント: 閲覧者（人事情報を読む主体）は作らない。レート制限は未認証と同じ接続元アドレスごと
                access.ClientId = UiClient.Id;
                access.Subject = ViewerContextFactory.SubjectOf(http.User);
                clientLabel = UiClient.Id;
                if (http.GetEndpoint()?.Metadata.GetMetadata<BreakGlassAllowedMetadata>() is null)
                {
                    access.RejectReason = RejectReasons.Forbidden;
                    await WriteProblemAsync(http, StatusCodes.Status403Forbidden, "許可されていません", "緊急用の管理者アカウントは認証の復旧だけに使えます（人事情報は参照できません）。", Problems.ForbiddenType);
                    return;
                }
            }
            else if (http.User.Identity?.IsAuthenticated == true)
            {
                var session = SessionClaims.IsSession(http.User);
                var token = http.Features.Get<Auth.AccessTokenFeature>()?.Token;
                access.ClientId = session ? UiClient.Id : token?.ClientId ?? ViewerContextFactory.ClientIdOf(http.User);
                access.Subject = token is not null
                    ? token.PersonId?.ToString("D") ?? token.ClientKey
                    : ViewerContextFactory.SubjectOf(http.User) ?? access.ClientId;
                try
                {
                    access.Viewer = session ? ViewerContextFactory.CreateForSession(http.User, http.TraceIdentifier)
                        : token is not null ? ViewerContextFactory.CreateForAccessToken(token, channel, http.TraceIdentifier)
                        : viewers.Create(http.User, channel, http.TraceIdentifier);
                    access.ClientKey = session ? UiClient.KeyFor(access.Viewer.PersonId!.Value) : token?.ClientKey ?? access.Viewer.ClientId;
                    access.Token = token;
                    access.Scopes = token?.Scopes;
                    access.ClientRateLimit = token?.RateLimitPerClient;
                    clientLabel = access.Viewer.ClientId ?? AccessMetrics.UnregisteredClient;
                }
                catch (ViewerRejectedException ex)
                {
                    clientLabel = AccessMetrics.UnregisteredClient;
                    LogRejected(logger, ex.Message);
                    access.RejectReason = RejectReasons.ClientRejected;
                    await WriteProblemAsync(http, StatusCodes.Status403Forbidden, "許可されていません", "このトークンでは jin-ji を利用できません。", Problems.ClientRejectedType);
                    return;
                }

                var viewer = access.Viewer;
                var client = access.ClientKey!;
                if (detector.StateOf(client) == AbuseState.Suspended && !(kind.Kind == RequestKind.Admin && AdminAccess.IsHrAdminUser(viewer)))
                {
                    access.RejectReason = RejectReasons.Suspended;
                    await WriteProblemAsync(http, StatusCodes.Status403Forbidden, "一時停止中です", "このクライアントは不正検知により一時停止されています。人事管理者に解除を依頼してください。", Problems.SuspendedType);
                    return;
                }

                // MCP の受け口: JSON-RPC の本文を先に読み、ツールの種別（レート制限の段・返却人数）とスコープを決める
                McpRequestInfo? call = null;
                if (channel == Channel.Mcp && IsMcpEndpoint(http))
                {
                    if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
                    {
                        bodySize.MaxRequestBodySize = mcp.CurrentValue.MaxRequestBodyBytes;
                    }

                    try
                    {
                        call = await McpRequestClassifier.ClassifyAsync(http.Request, http.RequestAborted);
                    }
                    catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                    {
                        access.RejectReason = RejectReasons.TooLarge;
                        await WriteProblemAsync(http, StatusCodes.Status413PayloadTooLarge, "要求が大きすぎます", "MCP の要求の本文が上限を超えています。", Problems.BadRequestType);
                        return;
                    }

                    kind = call.Kind;
                    access.Kind = kind;
                }

                // jin-ji が発行したトークンのスコープ（RFC 6750 の insufficient_scope）: REST API は参照に jinji.read、状態を変える要求に jinji.propose。
                // MCP は JSON-RPC の要求がすべて POST なので、本文のツールで決める（jinji.read、提案のツールはさらに jinji.propose）
                var required = call?.RequiredScopes ?? [RequiredScope(http, channel)];
                if (access.Scopes is { } scopes && !required.All(scopes.Contains))
                {
                    access.RejectReason = RejectReasons.Forbidden;
                    var scope = string.Join(' ', required);
                    http.Response.Headers.Append(
                        Microsoft.Net.Http.Headers.HeaderNames.WWWAuthenticate,
                        Auth.AccessTokenAuthenticationHandler.ChallengeValue(server, channel == Channel.Mcp ? OAuthResource.Mcp : OAuthResource.Api, "insufficient_scope", scope));
                    if (call?.ToolName is not null && http.RequestServices.GetService<AuditWriter>() is { } audit)
                    {
                        await McpToolAudit.RecordAsync(audit, viewer, call.ToolName, AuditOutcome.Denied, [], http.RequestAborted);
                    }

                    await WriteProblemAsync(http, StatusCodes.Status403Forbidden, "スコープが足りません", $"この操作にはスコープ {scope} が必要です。", Problems.ForbiddenType);
                    return;
                }

                if (kind.ReturnsPeople && returnedPeople.RetryAfter(channel, client, access.Subject!) is { } wait)
                {
                    access.RejectReason = RejectReasons.ReturnedPeopleLimited;
                    http.Response.Headers.RetryAfter = Seconds(wait);
                    await WriteProblemAsync(http, StatusCodes.Status429TooManyRequests, "返却人数の上限に達しました", "一定時間に返せる人数の上限に達しました。Retry-After の秒数の後に再試行してください。", Problems.ReturnedPeopleLimitedType);
                    return;
                }
            }

            await next(http);
        }
        finally
        {
            await AfterAsync(http, access, channel, kind, clientLabel, Stopwatch.GetElapsedTime(started));
        }
    }

    /// <summary>MCP の停止中の応答の type。</summary>
    public const string McpDisabledType = "urn:jinji:problem:mcp-disabled";

    private static bool IsHealthCheck(HttpContext http) => http.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase);

    private static bool IsMcpEndpoint(HttpContext http) =>
        HttpMethods.IsPost(http.Request.Method) && http.Request.Path.Equals(McpModule.EndpointPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>入口で確かめるスコープ（REST API の参照と MCP は jinji.read、REST API の状態を変える要求は jinji.propose）。</summary>
    private static string RequiredScope(HttpContext http, Channel channel) =>
        channel == Channel.Mcp || HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method)
            ? OAuthScopes.Read
            : OAuthScopes.Propose;

    /// <summary>Retry-After の秒数（切り上げ、1 秒以上）。</summary>
    public static string Seconds(TimeSpan wait) =>
        Math.Max(1, (long)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Problem Details を書く（RFC 9457）。</summary>
    public static async Task WriteProblemAsync(HttpContext http, int status, string title, string detail, string type)
    {
        http.Response.StatusCode = status;
        var problems = http.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problems.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = { Status = status, Title = title, Detail = detail, Type = type },
        });
    }

    private async Task AfterAsync(HttpContext http, AccessContext access, Channel channel, RequestKindMetadata kind, string clientLabel, TimeSpan elapsed)
    {
        // MCP はツールの誤りも HTTP では 200 なので、ツールが読み替えた状態コードで数える
        var status = access.EffectiveStatus ?? http.Response.StatusCode;
        var reason = access.RejectReason ?? status switch
        {
            StatusCodes.Status400BadRequest => RejectReasons.BadRequest,
            StatusCodes.Status401Unauthorized => RejectReasons.Unauthenticated,
            StatusCodes.Status403Forbidden => RejectReasons.Forbidden,
            StatusCodes.Status404NotFound => RejectReasons.NotFound,
            StatusCodes.Status429TooManyRequests => RejectReasons.RateLimited,
            _ => null,
        };
        var people = status is >= 200 and < 300 ? access.PeopleReturned : 0;
        metrics.RecordRequest(clientLabel, AccessCodes.ToCode(channel), RequestKinds.ToCode(kind.Kind), status, elapsed, reason, people);

        if (access.Viewer is not { } viewer || access.ClientKey is not { } client || reason == RejectReasons.Suspended)
        {
            return;
        }

        if (people > 0)
        {
            returnedPeople.Add(channel, client, access.Subject ?? client, people);
        }

        if (detector.Observe(client, new AbuseObservation(status, people, access.IsBroadListing && people > 0)) is { } transition)
        {
            await notifier.NotifyAsync(transition, viewer, CancellationToken.None);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "トークン・セッションから閲覧者を作れないため拒否しました: {Reason}")]
    private static partial void LogRejected(ILogger logger, string reason);
}

/// <summary>緊急用の管理者アカウントのセッションでも使えるエンドポイント（認証の復旧）のメタデータ。無いエンドポイントは緊急用アカウントには 403。</summary>
internal sealed class BreakGlassAllowedMetadata
{
    public static BreakGlassAllowedMetadata Instance { get; } = new();
}
