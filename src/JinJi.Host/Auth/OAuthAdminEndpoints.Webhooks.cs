using JinJi.Api;
using JinJi.Application.Auth;
using JinJi.Application.Webhooks;
using Microsoft.AspNetCore.Mvc;

namespace JinJi.Host.Auth;

/// <summary>
/// 社内システムへの Webhook の管理（<c>/auth/admin/oauth/webhooks</c>。0018、アーキテクチャ概要 §9「Webhook の実装の規則」）。
/// 登録・変更（送信先・停止と再開・件数・間隔）・鍵のローテーション・送信の一覧・dead の再送・送信の確かめ。
/// </summary>
/// <remarks>
/// クライアントの管理と同じく、UI のセッションの人事管理者の人だけ（CSRF のトークンが要る。レート制限の種別は admin）。操作はすべて監査ログに残す。
/// 署名の鍵（<c>whsec_…</c>）は登録とローテーションの応答でだけ返る。
/// </remarks>
internal static partial class OAuthAdminEndpoints
{
    /// <summary>旧鍵で署名を続ける期間の上限（時間）。</summary>
    private const int MaxOverlapHours = 7 * 24;

    private static void MapWebhooks(RouteGroupBuilder admin)
    {
        admin.MapGet("/webhooks", ListWebhooksAsync)
            .WithSummary("社内システムへの Webhook の一覧（受け手のクライアント・送信待ち・dead・成功の件数・最終の成功と失敗・署名の鍵の期限）")
            .Produces<IReadOnlyList<WebhookResponse>>();
        admin.MapPost("/clients/{clientRef}/webhook", RegisterWebhookAsync)
            .WithSummary("社内システム（client credentials で REST API を読めるクライアント）に Webhook を登録する（クライアントごとに 1 つ）。署名の鍵はこの応答でだけ返る")
            .Produces<WebhookSecretResponse>();
        admin.MapPost("/webhooks/{webhookId}/update", UpdateWebhookAsync)
            .WithSummary("Webhook の送信先・状態（active / disabled）・1 回に送る件数・要約に切り替える件数・送信の間隔を変える（指定した項目だけ）")
            .Produces<WebhookResponse>();
        admin.MapPost("/webhooks/{webhookId}/rotate-secret", RotateWebhookSecretAsync)
            .WithSummary("署名の鍵を入れ替える。旧鍵でも overlap_hours（既定 24、0〜168）の間は署名を続ける。新しい鍵はこの応答でだけ返る")
            .Produces<WebhookSecretResponse>();
        admin.MapGet("/webhooks/{webhookId}/deliveries", ListWebhookDeliveriesAsync)
            .WithSummary("Webhook の送信の一覧（新しい順。status で絞れる。本文は種別と ID だけ）")
            .Produces<IReadOnlyList<WebhookDeliveryResponse>>();
        admin.MapPost("/webhooks/{webhookId}/redeliver", RedeliverWebhookAsync)
            .WithSummary("dead の送信を再送する（delivery_id を指定しなければ、その Webhook の dead をすべて。同じイベント ID・本文で送る）")
            .Produces<WebhookRedeliveredResponse>();
        admin.MapPost("/webhooks/{webhookId}/ping", PingWebhookAsync)
            .WithSummary("送信の確かめ（ping。人事データを持たない本文）を積む。受け手の署名の検証の確かめに使う")
            .Produces<WebhookPingResponse>();
    }

    private static Task<IResult> ListWebhooksAsync(HttpContext http, WebhookAdministrationService webhooks) =>
        RunAsync(http, async actor => Results.Ok((await webhooks.ListAsync(actor, http.RequestAborted)).Select(WebhookResponse.From).ToList()));

    private static Task<IResult> RegisterWebhookAsync(string clientRef, RegisterWebhookRequest request, HttpContext http, WebhookAdministrationService webhooks) =>
        WithClientAsync(http, clientRef, async (actor, id) =>
        {
            var issued = await webhooks.RegisterAsync(actor, id, request.Url, request.Settings(), http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(new WebhookSecretResponse(issued.Subscription.Id, issued.SecretId, issued.Secret));
        });

    private static Task<IResult> UpdateWebhookAsync(string webhookId, UpdateWebhookRequest request, HttpContext http, WebhookAdministrationService webhooks) =>
        WithWebhookAsync(http, webhookId, async (actor, id) =>
        {
            WebhookStatus? status;
            try
            {
                status = request.Status is null ? null : WebhookCodes.ParseStatus(request.Status);
            }
            catch (ArgumentException)
            {
                return Problems.BadRequest(http, "status は active / disabled のいずれかにしてください。");
            }

            await webhooks.UpdateAsync(actor, id, request.Url, status, request.Settings(), http.TraceIdentifier, http.RequestAborted);
            var view = (await webhooks.ListAsync(actor, http.RequestAborted)).Single(v => v.Subscription.Id == id);
            return Results.Ok(WebhookResponse.From(view));
        });

    private static Task<IResult> RotateWebhookSecretAsync(string webhookId, RotateWebhookSecretRequest request, HttpContext http, WebhookAdministrationService webhooks) =>
        WithWebhookAsync(http, webhookId, async (actor, id) =>
        {
            if (request.OverlapHours is < 0 or > MaxOverlapHours)
            {
                return Problems.BadRequest(http, $"overlap_hours は 0〜{MaxOverlapHours} で指定してください。");
            }

            TimeSpan? overlap = request.OverlapHours is { } hours ? TimeSpan.FromHours(hours) : null;
            var issued = await webhooks.RotateSecretAsync(actor, id, overlap, http.TraceIdentifier, http.RequestAborted);
            return Results.Ok(new WebhookSecretResponse(issued.Subscription.Id, issued.SecretId, issued.Secret));
        });

    private static Task<IResult> ListWebhookDeliveriesAsync(
        string webhookId,
        HttpContext http,
        WebhookAdministrationService webhooks,
        [FromQuery] string? status,
        [FromQuery] int? limit) =>
        WithWebhookAsync(http, webhookId, async (actor, id) =>
        {
            WebhookDeliveryStatus? filter;
            try
            {
                filter = status is null ? null : WebhookCodes.ParseDeliveryStatus(status);
            }
            catch (ArgumentException)
            {
                return Problems.BadRequest(http, "status は pending / sending / succeeded / dead のいずれかにしてください。");
            }

            var deliveries = await webhooks.ListDeliveriesAsync(actor, id, filter, limit ?? 50, http.RequestAborted);
            return Results.Ok(deliveries.Select(WebhookDeliveryResponse.From).ToList());
        });

    private static Task<IResult> RedeliverWebhookAsync(string webhookId, RedeliverWebhookRequest request, HttpContext http, WebhookAdministrationService webhooks) =>
        WithWebhookAsync(http, webhookId, async (actor, id) =>
            Results.Ok(new WebhookRedeliveredResponse(await webhooks.RedeliverAsync(actor, id, request.DeliveryId, http.TraceIdentifier, http.RequestAborted))));

    private static Task<IResult> PingWebhookAsync(string webhookId, HttpContext http, WebhookAdministrationService webhooks) =>
        WithWebhookAsync(http, webhookId, async (actor, id) =>
            Results.Ok(new WebhookPingResponse(await webhooks.PingAsync(actor, id, http.TraceIdentifier, http.RequestAborted))));

    private static Task<IResult> WithWebhookAsync(HttpContext http, string webhookId, Func<AuthActor, Guid, Task<IResult>> action) =>
        RunAsync(http, actor => Guid.TryParseExact(webhookId, "D", out var id) ? action(actor, id) : Task.FromResult(Problems.NotFound(http)));
}

/// <summary>Webhook の登録。</summary>
/// <param name="Url">送信先（https。公開のアドレスだけ。秘密を含めない）。</param>
/// <param name="MaxEventsPerDelivery">1 回の POST に載せる変更の件数の上限（1〜1000。既定 100）。</param>
/// <param name="SummaryThreshold">1 回の収集で通知する変更がこれを超えたら要約（件数と変更フィードの位置だけ）に切り替える（既定 1000）。</param>
/// <param name="MinIntervalSeconds">同じ受け手への POST の最小の間隔（秒。1〜3600。既定 10）。</param>
internal sealed record RegisterWebhookRequest(string? Url, int? MaxEventsPerDelivery, int? SummaryThreshold, int? MinIntervalSeconds)
{
    public WebhookSettings Settings() => new(MaxEventsPerDelivery, SummaryThreshold, MinIntervalSeconds);
}

/// <summary>Webhook の登録の変更（指定した項目だけ変える）。</summary>
internal sealed record UpdateWebhookRequest(
    string? Url,
    [property: OpenApiEnum("active", "disabled")] string? Status,
    int? MaxEventsPerDelivery,
    int? SummaryThreshold,
    int? MinIntervalSeconds)
{
    public WebhookSettings Settings() => new(MaxEventsPerDelivery, SummaryThreshold, MinIntervalSeconds);
}

/// <param name="OverlapHours">旧鍵でも署名を続ける時間（0〜168。既定 24）。</param>
internal sealed record RotateWebhookSecretRequest(int? OverlapHours);

/// <param name="DeliveryId">再送する送信（省略すると、その Webhook の dead をすべて）。</param>
internal sealed record RedeliverWebhookRequest(Guid? DeliveryId);

/// <summary>Webhook の登録（管理画面の 1 行）。</summary>
/// <param name="PendingCount">送信待ち（送信中を含む）の件数。</param>
/// <param name="DeadCount">上限回数まで失敗した送信の件数（再送するまで残る）。</param>
/// <param name="OldestPendingAt">最も古い送信待ちの作成日時（滞留の目安）。</param>
/// <param name="LastFailure">最後の失敗の理由（状態コード・接続できない等。URL・応答の本文は含めない）。</param>
/// <param name="Secrets">署名に使う鍵（今の鍵と、ローテーションで期限の来ていない旧鍵。値は含めない）。</param>
internal sealed record WebhookResponse(
    Guid Id,
    Guid ClientRef,
    string ClientId,
    string? ClientName,
    [property: OpenApiEnum("active", "pending_approval", "suspended", "rejected")] string ClientStatus,
    int DisclosureLeadDays,
    string Url,
    [property: OpenApiEnum("active", "disabled")] string Status,
    int MaxEventsPerDelivery,
    int SummaryThreshold,
    int MinIntervalSeconds,
    int PendingCount,
    int DeadCount,
    int SucceededCount,
    DateTimeOffset? OldestPendingAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? LastFailureAt,
    string? LastFailure,
    IReadOnlyList<WebhookSecretInfoResponse> Secrets,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static WebhookResponse From(WebhookSubscriptionView view)
    {
        var s = view.Subscription;
        return new(
            s.Id,
            s.ClientRef,
            view.ClientId,
            view.ClientName,
            OAuthCodes.ToCode(view.ClientStatus),
            view.DisclosureLeadDays,
            s.Url,
            WebhookCodes.ToCode(s.Status),
            s.MaxEventsPerDelivery,
            s.SummaryThreshold,
            s.MinIntervalSeconds,
            view.Counts.Pending,
            view.Counts.Dead,
            view.Counts.Succeeded,
            view.Counts.OldestPendingAt,
            s.LastSuccessAt,
            s.LastFailureAt,
            s.LastFailure,
            [.. view.Secrets.Select(k => new WebhookSecretInfoResponse(k.Id, k.CreatedAt, k.ExpiresAt))],
            s.CreatedAt,
            s.UpdatedAt);
    }
}

/// <summary>署名の鍵の情報（値は含めない）。</summary>
/// <param name="ExpiresAt">ローテーションで置き換えた旧鍵の期限（null は今の鍵）。</param>
internal sealed record WebhookSecretInfoResponse(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>登録・ローテーションで発行した署名の鍵。secret（<c>whsec_…</c>）はこの応答でだけ返る（DB には暗号化した値だけ）。</summary>
internal sealed record WebhookSecretResponse(Guid WebhookId, Guid SecretId, string Secret)
{
    public override string ToString() => $"WebhookSecretResponse {{ WebhookId = {WebhookId:D}, SecretId = {SecretId:D} }}";
}

/// <summary>Webhook の送信（1 回の POST。id がイベント ID = 冪等キー）。</summary>
/// <param name="Body">送る本文（種別と ID だけ。氏名などの値は持たない）。</param>
/// <param name="RedeliveredBy">最後に dead からの再送を指示した人事管理者。</param>
internal sealed record WebhookDeliveryResponse(
    Guid Id,
    [property: OpenApiEnum("changes", "summary", "ping")] string Kind,
    [property: OpenApiEnum("pending", "sending", "succeeded", "dead")] string Status,
    int EventCount,
    int Attempts,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    int? LastHttpStatus,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SucceededAt,
    DateTimeOffset? DeadAt,
    Guid? RedeliveredBy,
    DateTimeOffset? RedeliveredAt,
    string Body)
{
    public static WebhookDeliveryResponse From(WebhookDelivery d) => new(
        d.Id,
        WebhookCodes.ToCode(d.Kind),
        WebhookCodes.ToCode(d.Status),
        d.EventCount,
        d.Attempts,
        d.NextAttemptAt,
        d.LastAttemptAt,
        d.LastHttpStatus,
        d.LastError,
        d.CreatedAt,
        d.SucceededAt,
        d.DeadAt,
        d.RedeliveredBy,
        d.RedeliveredAt,
        d.Body);
}

internal sealed record WebhookRedeliveredResponse(int Redelivered);

/// <param name="DeliveryId">積んだ送信（イベント ID）。</param>
internal sealed record WebhookPingResponse(Guid DeliveryId);
