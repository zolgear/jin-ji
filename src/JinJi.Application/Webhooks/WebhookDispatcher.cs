using System.Diagnostics.Metrics;
using System.Globalization;
using JinJi.Application.Access;
using JinJi.Application.Audit;
using JinJi.Application.Auth;
using JinJi.Application.Changes;
using JinJi.Application.People;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Webhooks;

/// <summary>Webhook のメトリクス（Meter <c>JinJi.Webhooks</c>。API の役割と同じプロセスならメトリクスのポートに出る）。</summary>
public sealed class WebhookMetrics
{
    public const string MeterName = "JinJi.Webhooks";

    private readonly Counter<long> _deliveries;
    private readonly Counter<long> _events;
    private readonly Counter<long> _collections;

    public WebhookMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Meter = meterFactory.Create(MeterName);
        _deliveries = Meter.CreateCounter<long>("jinji.webhook.deliveries", "{attempt}", "Webhook の送信の試行（クライアント・結果〔succeeded / retry / dead〕別）");
        _events = Meter.CreateCounter<long>("jinji.webhook.events", "{event}", "Webhook で通知した変更の件数（クライアント・送り方〔changes / summary〕別）");
        _collections = Meter.CreateCounter<long>("jinji.webhook.collections", "{run}", "Webhook の変更の収集（結果〔success / skipped / failed〕別）");
    }

    public Meter Meter { get; }

    public void Attempt(string clientId, WebhookAttemptOutcome outcome) =>
        _deliveries.Add(1, new KeyValuePair<string, object?>("client", clientId), new KeyValuePair<string, object?>("outcome", Code(outcome)));

    public void Events(string clientId, WebhookDeliveryKind kind, int count)
    {
        if (count > 0)
        {
            _events.Add(count, new KeyValuePair<string, object?>("client", clientId), new KeyValuePair<string, object?>("kind", WebhookCodes.ToCode(kind)));
        }
    }

    public void Collection(string outcome) => _collections.Add(1, new KeyValuePair<string, object?>("outcome", outcome));

    private static string Code(WebhookAttemptOutcome outcome) => outcome switch
    {
        WebhookAttemptOutcome.Succeeded => "succeeded",
        WebhookAttemptOutcome.Retry => "retry",
        _ => "dead",
    };
}

/// <summary>1 回の実行の結果（ワーカーのログ用）。</summary>
public sealed record WebhookRunResult(int Collected, int Queued, int Succeeded, int Retried, int Dead, IReadOnlyList<string> Failures);

/// <summary>
/// Webhook の収集と送信（ワーカーの定期ジョブ。アーキテクチャ概要 §9「Webhook の実装の規則」）。
/// </summary>
/// <remarks>
/// <para><b>収集</b>（受け手ごと。貸し出し〔FOR UPDATE SKIP LOCKED と期限〕で、複数のワーカーでも同じ受け手を同時に収集しない）</para>
/// <list type="bullet">
/// <item>受け手のクライアントを閲覧者（社内システム。項目の上限・先行提供の日数はクライアントの今の登録）にして、登録が持つ位置から変更フィードを読む。
/// 見せてよい日の上限（業務日付 + その受け手の先行提供の日数）・日が来た行の拾い直し・取り消しの記録の宛先は変更フィードの規則（RLS とアプリ）がそのまま決める。
/// 日付が変わって上限が進んだら、その日に入った未来の変更がその時点で届く</item>
/// <item>見えない人の変更は送らない: 人は、変更の見せてよい日（今日以前なら今日）の時点で受け手が参照できる人（<see cref="PersonDirectoryService.VisiblePeopleAsync"/>）だけ。
/// 組織・拠点の情報は全社公開なので、変更の記録の RLS（上限）の内側ならすべて</item>
/// <item>取り消し（withdrawn）は RLS で先行提供の受け手にだけ現れる。そのうえで、受け手に今見える対象か、以前に通知した対象（webhook_notified_entity）にだけ送る
/// （取り消された入社予定の人は今は見えないが、先行提供で通知済みなら届ける）</item>
/// <item>1 回の収集で通知する変更が登録の要約の件数を超えたら（拠点の改称 1 件で数千人分の変更が出るなど）、1 件ずつ送らずに要約（件数と変更フィードの位置）を 1 つ送る。
/// それ以下なら登録の件数ずつまとめて送る</item>
/// <item>送信を積み、通知した対象を記録し、位置を進めるのは 1 トランザクション（貸し出しが自分のものであるときだけ）。失敗したら位置は進めず、次の収集で読み直す</item>
/// </list>
/// <para><b>送信</b></para>
/// <list type="bullet">
/// <item>受け手ごとに古い順で 1 つずつ取り出し（FOR UPDATE SKIP LOCKED。送信中にして貸し出しの期限を付ける）、受け手の次の送信を最小の間隔の後にする。
/// 貸し出しの期限は取り出した全部を送り終えるまでの時間の上限で、期限までに送り終えられない送信は送らずに残す。
/// 送信中のままワーカーが落ちたら、期限の後に他のワーカーが送り直す（少なくとも 1 回は届ける。受け手はイベント ID で重複を除く）</item>
/// <item>試行ごとに時刻を新しくして署名する（今の鍵と、ローテーションで期限の来ていない旧鍵の両方）。2xx だけが成功</item>
/// <item>失敗したら指数バックオフ（<see cref="WebhookOptions.RetryDelay"/> + 0〜10% の揺らぎ）で再試行し、上限回数で dead にする（人事管理者が画面で確かめて再送する）</item>
/// <item>送信の成功は監査ログ <c>webhook.deliver</c>（対象者は本文に載せた人）、dead は <c>webhook.dead</c>（操作者は jin-ji 自身・経路 worker）。メトリクスにも出す</item>
/// </list>
/// </remarks>
public sealed class WebhookDispatcher
{
    /// <summary>変更フィードを 1 回に読む行数。</summary>
    private const int ReadPageSize = ChangeFeedService.MaxLimit;

    private readonly IWebhookStore _store;
    private readonly IOAuthStore _clients;
    private readonly ChangeFeedService _changes;
    private readonly PersonDirectoryService _people;
    private readonly IWebhookSender _sender;
    private readonly IWebhookSecretProtector _protector;
    private readonly AuditWriter _audit;
    private readonly WebhookOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly WebhookMetrics? _metrics;
    private readonly CursorProtector? _cursors;

    public WebhookDispatcher(
        IWebhookStore store,
        IOAuthStore clients,
        ChangeFeedService changes,
        PersonDirectoryService people,
        IWebhookSender sender,
        IWebhookSecretProtector protector,
        AuditWriter audit,
        WebhookOptions options,
        TimeProvider timeProvider,
        WebhookMetrics? metrics = null,
        CursorProtector? cursors = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _clients = clients;
        _changes = changes;
        _people = people;
        _sender = sender;
        _protector = protector;
        _audit = audit;
        _options = options;
        _timeProvider = timeProvider;
        _metrics = metrics;
        _cursors = cursors;
    }

    /// <summary>1 回実行する（収集 → 送信）。</summary>
    public async Task<WebhookRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var (collected, queued) = await CollectAsync(failures, cancellationToken);
        var (succeeded, retried, dead) = await SendDueAsync(failures, cancellationToken);
        return new WebhookRunResult(collected, queued, succeeded, retried, dead, failures);
    }

    // ---- 収集 ----------------------------------------------------------------------------------------------------

    /// <summary>収集の時期が来た受け手の変更を集めて送信を積む。収集した受け手の数と積んだ送信の数。</summary>
    public async Task<(int Collected, int Queued)> CollectAsync(List<string> failures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var owner = EntityId.New();
        var now = _timeProvider.GetUtcNow();
        var claimed = await _store.ClaimForCollectionAsync(owner, now, now + _options.CollectLease, _options.SubscriptionsPerRun, cancellationToken);
        var (collected, queued) = (0, 0);
        foreach (var subscription in claimed)
        {
            try
            {
                var count = await CollectOneAsync(owner, subscription, cancellationToken);
                if (count is { } n)
                {
                    collected++;
                    queued += n;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add($"収集（登録 {subscription.Id:D}）: {ex.GetType().Name}: {ex.Message}");
                _metrics?.Collection("failed");
                await _store.ReleaseCollectionAsync(owner, subscription.Id, _timeProvider.GetUtcNow() + _options.Interval, CancellationToken.None);
            }
        }

        return (collected, queued);
    }

    /// <returns>積んだ送信の数。受け手のクライアントが使えないので収集しなかった・貸し出しを失ったなら null。</returns>
    private async Task<int?> CollectOneAsync(Guid owner, WebhookSubscription subscription, CancellationToken cancellationToken)
    {
        var client = await _clients.FindClientByRefAsync(subscription.ClientRef, cancellationToken);
        if (client is null || !CanReceive(client))
        {
            // 停止中・スコープを外したクライアントには集めない（位置は進めない。再開したら止めていた間の変更も届く）
            _metrics?.Collection("skipped");
            await _store.ReleaseCollectionAsync(owner, subscription.Id, _timeProvider.GetUtcNow() + _options.Interval, cancellationToken);
            return null;
        }

        var viewer = Receiver(client);
        var start = subscription.Position;
        var position = start;
        var rows = new List<StoredChange>();
        while (true)
        {
            var batch = await _changes.ReadBatchAsync(viewer, position, ReadPageSize, cancellationToken);
            rows.AddRange(batch.Rows);
            position = batch.Next;
            if (!batch.HasMore || rows.Count >= _options.MaxScanRows)
            {
                break;
            }
        }

        var events = await FilterAsync(viewer, subscription.Id, rows, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var cursor = _cursors?.Protect(ChangeCursorPurpose, viewer, start);
        var deliveries = new List<NewWebhookDelivery>();
        if (events.Count > subscription.SummaryThreshold)
        {
            var id = EntityId.New();
            deliveries.Add(new NewWebhookDelivery(id, WebhookDeliveryKind.Summary, WebhookPayload.Summary(id, now, client.ClientId, events, cursor), events.Count));
        }
        else
        {
            foreach (var chunk in events.Chunk(subscription.MaxEventsPerDelivery))
            {
                var id = EntityId.New();
                deliveries.Add(new NewWebhookDelivery(id, WebhookDeliveryKind.Changes, WebhookPayload.Changes(id, now, client.ClientId, chunk, cursor), chunk.Length));
            }
        }

        var notified = events.Where(e => !e.Withdrawn).Select(e => new WebhookEntity(e.Type, e.Id)).Distinct().ToList();
        if (!await _store.CompleteCollectionAsync(owner, subscription.Id, position, deliveries, notified, now, now + _options.Interval, cancellationToken))
        {
            // 貸し出しの期限が切れて他のワーカーが引き継いだ（その収集が同じ位置から読み直す）
            _metrics?.Collection("skipped");
            return null;
        }

        _metrics?.Collection("success");
        _metrics?.Events(client.ClientId, deliveries.Count == 1 && deliveries[0].Kind == WebhookDeliveryKind.Summary ? WebhookDeliveryKind.Summary : WebhookDeliveryKind.Changes, events.Count);
        return deliveries.Count;
    }

    /// <summary>変更フィードのカーソルの用途（REST の <c>/changes</c> と同じ。受け手のトークンでそのまま使える）。</summary>
    public const string ChangeCursorPurpose = "changes";

    /// <summary>
    /// 受け手に送る変更に絞る（重複を除き、見えない人の変更・見えなくなった対象の取り消しを除く）。順は変更フィードの順。
    /// </summary>
    private async Task<IReadOnlyList<WebhookEvent>> FilterAsync(ViewerContext viewer, Guid subscriptionId, List<StoredChange> rows, CancellationToken cancellationToken)
    {
        var today = BusinessDate.Today(_timeProvider);
        var candidates = rows
            .Select(r => new WebhookEvent(r.EntityType, r.EntityId, r.EffectiveDate, r.Withdrawn))
            .Distinct()
            .ToList();

        // 人の変更: 見せてよい日（今日以前なら今日）の時点で参照できる人だけ
        var visible = new HashSet<(Guid, DateOnly)>();
        foreach (var group in candidates.Where(e => e.Type == ChangeEntityType.Person && !e.Withdrawn).GroupBy(e => Max(e.EffectiveDate, today)))
        {
            var ids = group.Select(e => e.Id).Distinct().ToList();
            foreach (var id in await _people.VisiblePeopleAsync(viewer, ids, group.Key, cancellationToken))
            {
                visible.Add((id, group.Key));
            }
        }

        // 取り消し: 今見える人か、以前に通知した対象だけ（組織・拠点は全社公開なので、以前に通知していなくても送る）
        var withdrawn = candidates.Where(e => e.Withdrawn).ToList();
        var withdrawnPeople = withdrawn.Where(e => e.Type == ChangeEntityType.Person).Select(e => e.Id).Distinct().ToList();
        var visibleToday = await _people.VisiblePeopleAsync(viewer, withdrawnPeople, today, cancellationToken);
        var notified = withdrawn.Count == 0
            ? new HashSet<WebhookEntity>()
            : await _store.NotifiedAsync(subscriptionId, [.. withdrawn.Select(e => new WebhookEntity(e.Type, e.Id)).Distinct()], cancellationToken);

        return [.. candidates.Where(e => e switch
        {
            { Type: ChangeEntityType.Person, Withdrawn: false } => visible.Contains((e.Id, Max(e.EffectiveDate, today))),
            { Type: ChangeEntityType.Person, Withdrawn: true } => visibleToday.Contains(e.Id) || notified.Contains(new WebhookEntity(e.Type, e.Id)),
            _ => true,
        })];
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    /// <summary>
    /// 受け手として通知してよいクライアントか（有効で、REST API を client credentials・jinji.read で読める）。取り直せないクライアントには送らない。
    /// </summary>
    public static bool CanReceive(OAuthClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.Status == OAuthClientStatus.Active
            && client.Allows(OAuthGrantTypes.ClientCredentials)
            && client.Allows(OAuthResource.Api)
            && client.Scopes.Contains(OAuthScopes.Read, StringComparer.Ordinal);
    }

    /// <summary>受け手のクライアントの閲覧者（client credentials のトークンの閲覧者と同じ。§6.5）。</summary>
    public static ViewerContext Receiver(OAuthClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return ViewerContext.ForService(client.ClientId, client.FieldLimit(), null, client.Id, client.DisclosureLeadDays);
    }

    // ---- 送信 ----------------------------------------------------------------------------------------------------

    /// <summary>送る時期が来た送信を送る。成功・再試行・dead の数。</summary>
    public async Task<(int Succeeded, int Retried, int Dead)> SendDueAsync(List<string> failures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failures);
        var now = _timeProvider.GetUtcNow();
        var leaseUntil = now + _options.SendLease;
        var claimed = await _store.ClaimDeliveriesAsync(now, leaseUntil, _options.DeliveriesPerRun, cancellationToken);
        var outcomes = new List<WebhookAttemptOutcome>();
        var gate = new object();
        await Parallel.ForEachAsync(
            claimed,
            new ParallelOptions { MaxDegreeOfParallelism = _options.Concurrency, CancellationToken = cancellationToken },
            async (delivery, ct) =>
            {
                try
                {
                    // 送り終える前に貸し出しの期限が来るなら送らない（期限の後に他のワーカーが送り直すと二重になるため。期限の後に改めて取り出す）
                    if (_timeProvider.GetUtcNow() + _options.Timeout + TimeSpan.FromSeconds(5) > leaseUntil)
                    {
                        return;
                    }

                    var outcome = await SendOneAsync(delivery, ct);
                    lock (gate)
                    {
                        outcomes.Add(outcome);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lock (gate)
                    {
                        failures.Add($"送信（{delivery.Delivery.Id:D}）: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            });

        return (outcomes.Count(o => o == WebhookAttemptOutcome.Succeeded), outcomes.Count(o => o == WebhookAttemptOutcome.Retry), outcomes.Count(o => o == WebhookAttemptOutcome.Dead));
    }

    private async Task<WebhookAttemptOutcome> SendOneAsync(ClaimedWebhookDelivery claimed, CancellationToken cancellationToken)
    {
        var delivery = claimed.Delivery;
        WebhookSendResult result;
        var secrets = await _store.ActiveSecretsAsync(delivery.SubscriptionId, _timeProvider.GetUtcNow(), cancellationToken);
        if (secrets.Count == 0)
        {
            result = new WebhookSendResult(false, null, "署名の鍵がありません");
        }
        else if (WebhookUrls.Validate(claimed.Url, out var url) is { } invalid)
        {
            result = new WebhookSendResult(false, null, invalid);
        }
        else
        {
            var timestamp = _timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var id = delivery.Id.ToString("D");
            var signature = WebhookSignature.Sign(secrets.Select(s => _protector.Unprotect(s.ProtectedSecret)), id, timestamp, delivery.Body);
            result = await _sender.SendAsync(
                url!,
                delivery.Body,
                [
                    new(WebhookSignature.IdHeader, id),
                    new(WebhookSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture)),
                    new(WebhookSignature.SignatureHeader, signature),
                ],
                cancellationToken);
        }

        var now = _timeProvider.GetUtcNow();
        var outcome = result.Succeeded
            ? WebhookAttemptOutcome.Succeeded
            : delivery.Attempts >= _options.MaxAttempts ? WebhookAttemptOutcome.Dead : WebhookAttemptOutcome.Retry;
        DateTimeOffset? next = outcome == WebhookAttemptOutcome.Retry ? now + Jitter(_options.RetryDelay(delivery.Attempts)) : null;
        var error = result.Succeeded ? null : Truncate(result.Error ?? "失敗しました");
        if (!await _store.CompleteDeliveryAsync(delivery.Id, delivery.Attempts, outcome, result.HttpStatus, error, now, next, CancellationToken.None))
        {
            // 貸し出しの期限が切れて他のワーカーが引き継いだ（その試行の結果が記録される）
            return outcome;
        }

        _metrics?.Attempt(claimed.ClientId, outcome);
        if (outcome == WebhookAttemptOutcome.Succeeded)
        {
            await _audit.RecordAsync(
                ViewerContext.ForSystem(Channel.Worker),
                WebhookAuditActions.Deliver,
                AuditOutcome.Success,
                WebhookPayload.PersonIds(delivery.Body),
                CancellationToken.None,
                WebhookAuditTargets.Delivery,
                delivery.Id);
        }
        else if (outcome == WebhookAttemptOutcome.Dead)
        {
            await _audit.RecordAsync(
                ViewerContext.ForSystem(Channel.Worker),
                WebhookAuditActions.Dead,
                AuditOutcome.Failed,
                WebhookPayload.PersonIds(delivery.Body),
                CancellationToken.None,
                WebhookAuditTargets.Delivery,
                delivery.Id);
        }

        return outcome;
    }

    /// <summary>待ちに 0〜10% の揺らぎを足す（多くの受け手が同時に落ちたときに再試行が一斉に重ならないように）。</summary>
    private static TimeSpan Jitter(TimeSpan delay) =>
        delay + TimeSpan.FromTicks((long)(delay.Ticks * 0.1 * Random.Shared.NextDouble()));

    private static string Truncate(string text)
    {
        var clean = new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)]);
        return clean.Length <= 500 ? clean : clean[..500];
    }
}
