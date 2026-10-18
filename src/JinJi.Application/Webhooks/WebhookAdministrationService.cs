using JinJi.Application.Audit;
using JinJi.Application.Auth;
using JinJi.Application.Changes;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Webhooks;

/// <summary>Webhook の監査ログの操作（audit_event.action）。</summary>
public static class WebhookAuditActions
{
    public const string Register = "webhook.register";

    /// <summary>送信先・状態（停止・再開）・件数・間隔の変更。</summary>
    public const string Update = "webhook.update";

    public const string SecretRotate = "webhook.secret.rotate";

    /// <summary>dead の送信の再送（人事管理者の操作）。</summary>
    public const string Redeliver = "webhook.redeliver";

    /// <summary>送信の確かめ（ping）を積んだ。</summary>
    public const string Ping = "webhook.ping";

    /// <summary>送信した（成功。対象者は本文に載せた人）。ワーカー（jin-ji 自身）。</summary>
    public const string Deliver = "webhook.deliver";

    /// <summary>上限回数まで失敗して dead にした。ワーカー（jin-ji 自身）。</summary>
    public const string Dead = "webhook.dead";
}

/// <summary>Webhook の監査ログの対象の種類（audit_event.target_type）。</summary>
public static class WebhookAuditTargets
{
    public const string Subscription = "webhook_subscription";
    public const string Delivery = "webhook_delivery";
}

/// <summary>登録・ローテーションの結果。<see cref="Secret"/>（<c>whsec_…</c>）はこの場でだけ見える（DB には暗号化した値だけ）。</summary>
public sealed record WebhookSecretIssued(WebhookSubscription Subscription, Guid SecretId, string Secret)
{
    public override string ToString() => $"WebhookSecretIssued {{ Subscription = {Subscription.Id:D}, SecretId = {SecretId:D} }}";
}

/// <summary>
/// 社内システムへの Webhook の登録と管理（アーキテクチャ概要 §9「Webhook の実装の規則」）。UI でログインした人事管理者が使う（管理 CLI も登録・変更・鍵の
/// ローテーションはできる。dead の再送と ping は人を記録するので人事管理者の人だけ）。操作はすべて監査ログに残す（拒否も）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>受け手は jin-ji が発行するトークンの社内システム（client credentials・REST API の resource・<c>jinji.read</c> を持つ api_client）だけ。
/// 受け手は通知を受けて自分のトークンで取り直すので、取り直せないクライアントには登録させない</item>
/// <item>起案と別人の承認は持たない。本文は種別・ID・日付だけで値を持たず、受け手が読める範囲（項目の上限・スコープ・先行提供の日数。どれも
/// 承認・登録の手順を経る）は登録で広がらないため（クライアントの登録・項目の上限の変更と同じく人事管理者 1 人の操作。0018 の冒頭）</item>
/// <item>送信先は https だけ（登録の時に形と IP アドレスのホストを、送信の直前に DNS の解決の後のアドレスを確かめる）</item>
/// <item>署名の鍵は 32 バイトの乱数で、Data Protection で暗号化して保存し、平文は登録・ローテーションの応答で 1 回だけ返す。
/// ローテーションでは旧鍵でも既定 24 時間（0〜7 日）署名を続け、受け手が新しい鍵に切り替える間を作る</item>
/// <item>読み始めの位置は登録の時点の変更フィードの位置（それより前の変更は送らない。受け手は先に一覧で全件を取る）。
/// 先行提供の受け手には、最初の収集で上限までの既存の予定が届く（変更フィードと同じ）</item>
/// </list>
/// </remarks>
public sealed class WebhookAdministrationService
{
    /// <summary>送信の一覧の最大件数。</summary>
    public const int MaxDeliveryListLimit = 200;

    private readonly IWebhookStore _store;
    private readonly IOAuthStore _clients;
    private readonly ChangeFeedService _changes;
    private readonly IWebhookSecretProtector _protector;
    private readonly AuthAuditor _audit;
    private readonly WebhookOptions _options;
    private readonly TimeProvider _timeProvider;

    public WebhookAdministrationService(
        IWebhookStore store,
        IOAuthStore clients,
        ChangeFeedService changes,
        IWebhookSecretProtector protector,
        AuthAuditor audit,
        WebhookOptions options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _clients = clients;
        _changes = changes;
        _protector = protector;
        _audit = audit;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>登録の一覧（受け手のクライアント・送信待ち・dead・成功の件数・最終の成功と失敗・鍵の情報）。</summary>
    public Task<IReadOnlyList<WebhookSubscriptionView>> ListAsync(AuthActor actor, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        return _store.ListAsync(actor, _timeProvider.GetUtcNow(), cancellationToken);
    }

    /// <summary>登録する（受け手のクライアントごとに 1 つ）。署名の鍵（平文）はこの応答でだけ返る。</summary>
    public async Task<WebhookSecretIssued> RegisterAsync(
        AuthActor actor,
        Guid clientRef,
        string? url,
        WebhookSettings settings,
        string? requestId,
        CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        ArgumentNullException.ThrowIfNull(settings);
        var client = await _clients.FindClientByRefAsync(clientRef, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, "クライアントが見つかりません。");
        RequireReceiver(client);
        if (WebhookUrls.Validate(url, out _) is { } error)
        {
            throw Invalid(error);
        }

        var (maxEvents, threshold, interval) = Resolve(settings, 100, 1000, 10);
        var now = _timeProvider.GetUtcNow();
        var subscription = new WebhookSubscription
        {
            Id = EntityId.New(),
            ClientRef = client.Id,
            Url = url!,
            Status = WebhookStatus.Active,
            MaxEventsPerDelivery = maxEvents,
            SummaryThreshold = threshold,
            MinIntervalSeconds = interval,
            Position = await _changes.CurrentPositionAsync(Channel.Worker, cancellationToken),
            CreatedAt = now,
            UpdatedAt = now,
        };

        var secret = WebhookSignature.NewSecret();
        var secretId = EntityId.New();
        if (!await _store.CreateAsync(actor, subscription, secretId, _protector.Protect(secret), cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, "このクライアントには既に Webhook が登録されています（送信先の変更は登録の変更で行います）。");
        }

        await AuditAsync(actor, WebhookAuditActions.Register, subscription.Id, requestId, cancellationToken);
        return new WebhookSecretIssued(subscription, secretId, WebhookSignature.Format(secret));
    }

    /// <summary>送信先・状態（停止・再開）・件数・間隔を変える（指定した項目だけ）。</summary>
    public async Task<WebhookSubscription> UpdateAsync(
        AuthActor actor,
        Guid id,
        string? url,
        WebhookStatus? status,
        WebhookSettings settings,
        string? requestId,
        CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        ArgumentNullException.ThrowIfNull(settings);
        var current = await _store.FindAsync(actor, id, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, "Webhook の登録が見つかりません。");
        if (url is not null && WebhookUrls.Validate(url, out _) is { } error)
        {
            throw Invalid(error);
        }

        var (maxEvents, threshold, interval) = Resolve(settings, current.MaxEventsPerDelivery, current.SummaryThreshold, current.MinIntervalSeconds);
        var updated = current with
        {
            Url = url ?? current.Url,
            Status = status ?? current.Status,
            MaxEventsPerDelivery = maxEvents,
            SummaryThreshold = threshold,
            MinIntervalSeconds = interval,
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        if (!await _store.UpdateAsync(actor, updated, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.NotFound, "Webhook の登録が見つかりません。");
        }

        await AuditAsync(actor, WebhookAuditActions.Update, id, requestId, cancellationToken);
        return updated;
    }

    /// <summary>
    /// 署名の鍵を入れ替える。旧鍵でも <paramref name="overlap"/>（既定は設定。0〜7 日）の間は署名を続ける（受け手が新しい鍵に切り替える間）。
    /// 新しい鍵（平文）はこの応答でだけ返る。
    /// </summary>
    public async Task<WebhookSecretIssued> RotateSecretAsync(AuthActor actor, Guid id, TimeSpan? overlap, string? requestId, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        var keep = overlap ?? _options.DefaultRotationOverlap;
        if (keep < TimeSpan.Zero || keep > WebhookOptions.MaxRotationOverlap)
        {
            throw Invalid("旧鍵で署名を続ける期間は 0〜7 日で指定してください。");
        }

        var subscription = await _store.FindAsync(actor, id, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, "Webhook の登録が見つかりません。");
        var secret = WebhookSignature.NewSecret();
        var secretId = EntityId.New();
        var now = _timeProvider.GetUtcNow();
        if (!await _store.RotateSecretAsync(actor, id, secretId, _protector.Protect(secret), now + keep, now, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.NotFound, "Webhook の登録が見つかりません。");
        }

        await AuditAsync(actor, WebhookAuditActions.SecretRotate, id, requestId, cancellationToken);
        return new WebhookSecretIssued(subscription, secretId, WebhookSignature.Format(secret));
    }

    /// <summary>送信の一覧（新しい順。状態で絞れる）。本文（種別と ID）も返す。</summary>
    public async Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(
        AuthActor actor,
        Guid id,
        WebhookDeliveryStatus? status,
        int limit,
        CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        if (limit is < 1 or > MaxDeliveryListLimit)
        {
            throw Invalid($"limit は 1〜{MaxDeliveryListLimit} で指定してください。");
        }

        _ = await _store.FindAsync(actor, id, cancellationToken) ?? throw new AuthAdminException(AuthAdminError.NotFound, "Webhook の登録が見つかりません。");
        return await _store.ListDeliveriesAsync(actor, id, status, limit, cancellationToken);
    }

    /// <summary>
    /// dead の送信を再送する（試行回数を 0 に戻して送信待ちにする。同じイベント ID・同じ本文で送るので、受け手は重複を除ける）。
    /// <paramref name="deliveryId"/> を指定しなければ、その登録の dead をすべて。UI でログインした人事管理者だけ（再送した人を記録する）。戻した件数。
    /// </summary>
    public async Task<int> RedeliverAsync(AuthActor actor, Guid id, Guid? deliveryId, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != AuthActorKind.HrAdmin)
        {
            await AuditAsync(actor, WebhookAuditActions.Redeliver, id, requestId, cancellationToken, AuditOutcome.Denied);
            throw new AuthAdminException(AuthAdminError.Forbidden, "再送は UI でログインした人事管理者だけが指示できます。");
        }

        _ = await _store.FindAsync(actor, id, cancellationToken) ?? throw new AuthAdminException(AuthAdminError.NotFound, "Webhook の登録が見つかりません。");
        var count = await _store.RedeliverAsync(actor, id, deliveryId, _timeProvider.GetUtcNow(), cancellationToken);
        if (deliveryId is not null && count == 0)
        {
            throw new AuthAdminException(AuthAdminError.Conflict, "dead の送信だけを再送できます（送信待ち・成功した送信は再送しません）。");
        }

        await AuditAsync(actor, WebhookAuditActions.Redeliver, id, requestId, cancellationToken);
        return count;
    }

    /// <summary>送信の確かめ（ping。人事データを持たない本文）を積む。受け手の署名の検証の確かめに使う。UI でログインした人事管理者だけ。</summary>
    public async Task<Guid> PingAsync(AuthActor actor, Guid id, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind != AuthActorKind.HrAdmin)
        {
            throw new AuthAdminException(AuthAdminError.Forbidden, "送信の確かめは UI でログインした人事管理者だけが行えます。");
        }

        var subscription = await _store.FindAsync(actor, id, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, "Webhook の登録が見つかりません。");
        var client = await _clients.FindClientByRefAsync(subscription.ClientRef, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, "クライアントが見つかりません。");
        var now = _timeProvider.GetUtcNow();
        var deliveryId = EntityId.New();
        await _store.InsertPingAsync(actor, id, new NewWebhookDelivery(deliveryId, WebhookDeliveryKind.Ping, WebhookPayload.Ping(deliveryId, now, client.ClientId), 0), now, cancellationToken);
        await AuditAsync(actor, WebhookAuditActions.Ping, id, requestId, cancellationToken);
        return deliveryId;
    }

    /// <summary>
    /// 受け手になれるクライアントか（有効か停止中の、client credentials・REST API の resource・jinji.read を持つ社内システム）。
    /// 停止中のクライアントにも登録はできる（送信は再開まで止まる）。
    /// </summary>
    private static void RequireReceiver(OAuthClient client)
    {
        if (client.Status is not (OAuthClientStatus.Active or OAuthClientStatus.Suspended))
        {
            throw Invalid("承認待ち・却下したクライアントには登録できません。");
        }

        if (!client.Allows(OAuthGrantTypes.ClientCredentials) || !client.Allows(OAuthResource.Api) || !client.Scopes.Contains(OAuthScopes.Read, StringComparer.Ordinal))
        {
            throw Invalid("Webhook は、REST API を client credentials で jinji.read のスコープを使って読める社内システムにだけ登録できます（受け手は通知を受けて自分のトークンで取り直します）。");
        }
    }

    private static (int MaxEvents, int Threshold, int Interval) Resolve(WebhookSettings settings, int maxEvents, int threshold, int interval)
    {
        var resolvedMax = settings.MaxEventsPerDelivery ?? maxEvents;
        var resolvedThreshold = settings.SummaryThreshold ?? threshold;
        var resolvedInterval = settings.MinIntervalSeconds ?? interval;
        if (resolvedMax is < 1 or > 1000)
        {
            throw Invalid("1 回に送る件数は 1〜1000 で指定してください。");
        }

        if (resolvedThreshold < resolvedMax || resolvedThreshold > 100_000)
        {
            throw Invalid("要約に切り替える件数は、1 回に送る件数以上 100,000 以下で指定してください。");
        }

        if (resolvedInterval is < 1 or > 3600)
        {
            throw Invalid("送信の間隔は 1〜3600 秒で指定してください。");
        }

        return (resolvedMax, resolvedThreshold, resolvedInterval);
    }

    private static void RequireOperator(AuthActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind is not (AuthActorKind.Cli or AuthActorKind.HrAdmin))
        {
            throw new AuthAdminException(AuthAdminError.Forbidden, "Webhook の管理は管理 CLI か、UI でログインした人事管理者だけが扱えます。");
        }
    }

    private static AuthAdminException Invalid(string message) => new(AuthAdminError.Invalid, message);

    private Task AuditAsync(AuthActor actor, string action, Guid target, string? requestId, CancellationToken cancellationToken, AuditOutcome outcome = AuditOutcome.Success) =>
        _audit.RecordAsync(AuthAuditor.ForOperator(actor, action, outcome, WebhookAuditTargets.Subscription, target, [], requestId), cancellationToken);
}
