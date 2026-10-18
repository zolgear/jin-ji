using JinJi.Application.Auth;
using JinJi.Application.Changes;
using JinJi.Application.Webhooks;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// Webhook の表（0018）の読み書き。管理は <see cref="AuthActor"/>（人事管理者の人 = user + hr_admin、管理 CLI = system）、
/// 収集と送信は jin-ji 自身（system）の閲覧者で行う。状態の遷移・本文の不変はトリガー、見える範囲は RLS が守る。
/// </summary>
/// <remarks>
/// 複数のワーカーでも二重に収集・送信しないよう、取り出しは FOR UPDATE SKIP LOCKED で行に鍵を掛け、貸し出しの期限（lease_until）を記録してから確定する。
/// 結果の記録は、貸し出しの持ち主（収集）・試行回数（送信）が取り出したときのままのときだけ行う（期限切れで他のワーカーが引き継いだ後の古い結果で上書きしない）。
/// </remarks>
public sealed class NpgsqlWebhookStore : IWebhookStore
{
    private const string SubscriptionColumns =
        "s.id, s.api_client_id, s.url, s.status, s.max_events_per_delivery, s.summary_threshold, s.min_interval_seconds, "
        + "s.position_xid, s.position_id, s.activated_through, s.deferred_target, s.deferred_xid, s.deferred_id, "
        + "s.last_success_at, s.last_failure_at, s.last_failure, s.created_at, s.updated_at";

    private const int SubscriptionColumnCount = 18;

    private const string DeliveryColumns =
        "d.id, d.subscription_id, d.kind, d.body, d.event_count, d.status, d.attempts, d.next_attempt_at, d.last_attempt_at, d.last_http_status, "
        + "d.last_error, d.created_at, d.succeeded_at, d.dead_at, d.redelivered_by_person_id, d.redelivered_at";

    /// <summary>受け手として通知してよいクライアント（WebhookDispatcher.CanReceive と同じ条件）。</summary>
    private const string ReceiverCondition =
        "c.status = 'active' AND 'client_credentials' = ANY (c.grant_types) AND 'api' = ANY (c.resources) AND 'jinji.read' = ANY (c.scopes)";

    /// <summary>送る時期が来た送信（送信待ちか、送信中のまま貸し出しの期限が切れたもの）。</summary>
    private const string DueCondition = "((d.status = 'pending' AND d.next_attempt_at <= $1) OR (d.status = 'sending' AND d.lease_until < $1))";

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlWebhookStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    // ---- 管理 ----------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<WebhookSubscriptionView>> ListAsync(AuthActor actor, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        var secrets = new Dictionary<Guid, List<WebhookSecretInfo>>();
        await using (var command = tx.CreateCommand(
            "SELECT id, subscription_id, created_at, expires_at FROM webhook_secret WHERE expires_at IS NULL OR expires_at > $1 ORDER BY created_at DESC, id DESC"))
        {
            command.Parameters.Add(P(now));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var subscription = reader.GetGuid(1);
                if (!secrets.TryGetValue(subscription, out var list))
                {
                    secrets[subscription] = list = [];
                }

                list.Add(new WebhookSecretInfo(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(2), NullableTime(reader, 3)));
            }
        }

        var views = new List<WebhookSubscriptionView>();
        await using (var command = tx.CreateCommand(
            $"""
            SELECT {SubscriptionColumns}, c.client_id, c.display_name, c.status,
                   coalesce((SELECT l.lead_days FROM api_client_disclosure_lead l WHERE l.api_client_id = c.id AND l.status = 'approved'), 0),
                   (SELECT count(*) FROM webhook_delivery d WHERE d.subscription_id = s.id AND d.status IN ('pending', 'sending'))::int,
                   (SELECT count(*) FROM webhook_delivery d WHERE d.subscription_id = s.id AND d.status = 'dead')::int,
                   (SELECT count(*) FROM webhook_delivery d WHERE d.subscription_id = s.id AND d.status = 'succeeded')::int,
                   (SELECT min(d.created_at) FROM webhook_delivery d WHERE d.subscription_id = s.id AND d.status IN ('pending', 'sending'))
            FROM webhook_subscription s
            JOIN api_client c ON c.id = s.api_client_id
            ORDER BY s.created_at, s.id
            """))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var subscription = ReadSubscription(reader);
                var o = SubscriptionColumnCount;
                views.Add(new WebhookSubscriptionView(
                    subscription,
                    reader.GetString(o),
                    reader.IsDBNull(o + 1) ? null : reader.GetString(o + 1),
                    OAuthCodes.ParseStatus(reader.GetString(o + 2)),
                    reader.GetInt32(o + 3),
                    new WebhookDeliveryCounts(reader.GetInt32(o + 4), reader.GetInt32(o + 5), reader.GetInt32(o + 6), NullableTime(reader, o + 7)),
                    secrets.GetValueOrDefault(subscription.Id) ?? []));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return views;
    }

    public async Task<WebhookSubscription?> FindAsync(AuthActor actor, Guid id, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        WebhookSubscription? subscription = null;
        await using (var command = tx.CreateCommand($"SELECT {SubscriptionColumns} FROM webhook_subscription s WHERE s.id = $1"))
        {
            command.Parameters.Add(P(id));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                subscription = ReadSubscription(reader);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return subscription;
    }

    public async Task<bool> CreateAsync(AuthActor actor, WebhookSubscription subscription, Guid secretId, string protectedSecret, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentException.ThrowIfNullOrEmpty(protectedSecret);
        await using var tx = await BeginAsync(actor, cancellationToken);
        var position = subscription.Position;
        await using (var insert = tx.CreateCommand(
            """
            INSERT INTO webhook_subscription (id, api_client_id, url, status, max_events_per_delivery, summary_threshold, min_interval_seconds,
                                              position_xid, position_id, activated_through, deferred_target, deferred_xid, deferred_id,
                                              created_by_kind, created_by_person_id, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $16)
            ON CONFLICT (api_client_id) DO NOTHING
            """))
        {
            AddAll(insert,
            [
                P(subscription.Id), P(subscription.ClientRef), P(subscription.Url), P(WebhookCodes.ToCode(subscription.Status)),
                P(subscription.MaxEventsPerDelivery), P(subscription.SummaryThreshold), P(subscription.MinIntervalSeconds),
                P(position.Xid), P(position.Id), P(position.ActivatedThrough),
                Nullable(position.Deferred?.Target, NpgsqlDbType.Date), Nullable(position.Deferred?.Xid, NpgsqlDbType.Bigint), Nullable(position.Deferred?.Id, NpgsqlDbType.Bigint),
                P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid), P(subscription.CreatedAt),
            ]);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await tx.Transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await InsertSecretAsync(tx, actor, subscription.Id, secretId, protectedSecret, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateAsync(AuthActor actor, WebhookSubscription subscription, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE webhook_subscription
            SET url = $2, status = $3, max_events_per_delivery = $4, summary_threshold = $5, min_interval_seconds = $6,
                updated_by_kind = $7, updated_by_person_id = $8, updated_at = $9
            WHERE id = $1
            """);
        AddAll(command,
        [
            P(subscription.Id), P(subscription.Url), P(WebhookCodes.ToCode(subscription.Status)), P(subscription.MaxEventsPerDelivery),
            P(subscription.SummaryThreshold), P(subscription.MinIntervalSeconds), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid),
            P(subscription.UpdatedAt),
        ]);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<bool> RotateSecretAsync(
        AuthActor actor,
        Guid subscriptionId,
        Guid secretId,
        string protectedSecret,
        DateTimeOffset previousExpiresAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedSecret);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using (var exists = tx.CreateCommand("SELECT 1 FROM webhook_subscription WHERE id = $1 FOR UPDATE"))
        {
            exists.Parameters.Add(P(subscriptionId));
            if (await exists.ExecuteScalarAsync(cancellationToken) is null)
            {
                await tx.Transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        // 今の鍵に期限を記録する（1 回だけ。署名に使うのは今の鍵と、期限の来ていない直前の鍵の 2 つまで）
        await using (var expire = tx.CreateCommand("UPDATE webhook_secret SET expires_at = $2 WHERE subscription_id = $1 AND expires_at IS NULL"))
        {
            AddAll(expire, [P(subscriptionId), P(previousExpiresAt)]);
            await expire.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertSecretAsync(tx, actor, subscriptionId, secretId, protectedSecret, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(
        AuthActor actor,
        Guid subscriptionId,
        WebhookDeliveryStatus? status,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            $"""
            SELECT {DeliveryColumns} FROM webhook_delivery d
            WHERE d.subscription_id = $1 AND ($2::text IS NULL OR d.status = $2)
            ORDER BY d.created_at DESC, d.id DESC
            LIMIT $3
            """);
        AddAll(command, [P(subscriptionId), Nullable(status is { } s ? WebhookCodes.ToCode(s) : null, NpgsqlDbType.Text), P(limit)]);
        var deliveries = await ReadDeliveriesAsync(command, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return deliveries;
    }

    public async Task<int> RedeliverAsync(AuthActor actor, Guid subscriptionId, Guid? deliveryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE webhook_delivery
            SET status = 'pending', attempts = 0, next_attempt_at = $3, dead_at = NULL,
                redelivered_by_person_id = jinji_viewer_person_id(), redelivered_at = $3
            WHERE subscription_id = $1 AND status = 'dead' AND ($2::uuid IS NULL OR id = $2)
            """);
        AddAll(command, [P(subscriptionId), Nullable(deliveryId, NpgsqlDbType.Uuid), P(now)]);
        var count = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return count;
    }

    public async Task InsertPingAsync(AuthActor actor, Guid subscriptionId, NewWebhookDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await InsertDeliveryAsync(tx, subscriptionId, delivery, now, cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // ---- 収集（ワーカー） ----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<WebhookSubscription>> ClaimForCollectionAsync(
        Guid owner,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        int max,
        CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            $"""
            UPDATE webhook_subscription AS s SET lease_owner = $2, lease_until = $3
            WHERE s.id IN (
                SELECT s2.id FROM webhook_subscription s2
                JOIN api_client c ON c.id = s2.api_client_id
                WHERE s2.status = 'active' AND {ReceiverCondition} AND s2.next_collect_at <= $1
                  AND (s2.lease_until IS NULL OR s2.lease_until < $1)
                ORDER BY s2.next_collect_at, s2.id
                LIMIT $4
                FOR UPDATE OF s2 SKIP LOCKED)
            RETURNING {SubscriptionColumns}
            """);
        AddAll(command, [P(now), P(owner), P(leaseUntil), P(max)]);
        var claimed = new List<WebhookSubscription>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                claimed.Add(ReadSubscription(reader));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<IReadOnlySet<WebhookEntity>> NotifiedAsync(Guid subscriptionId, IReadOnlyCollection<WebhookEntity> entities, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entities);
        var found = new HashSet<WebhookEntity>();
        if (entities.Count == 0)
        {
            return found;
        }

        await using var tx = await SystemAsync(cancellationToken);
        await using (var command = tx.CreateCommand(
            """
            SELECT n.entity_type, n.entity_id FROM webhook_notified_entity n
            JOIN unnest($2::text[], $3::uuid[]) AS u (entity_type, entity_id) ON u.entity_type = n.entity_type AND u.entity_id = n.entity_id
            WHERE n.subscription_id = $1
            """))
        {
            AddAll(command, [P(subscriptionId), Texts(entities.Select(e => WebhookCodes.ToCode(e.Type))), Uuids(entities.Select(e => e.Id))]);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                found.Add(new WebhookEntity(WebhookCodes.ParseEntityType(reader.GetString(0)), reader.GetGuid(1)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return found;
    }

    public async Task<bool> CompleteCollectionAsync(
        Guid owner,
        Guid subscriptionId,
        ChangeFeedPosition next,
        IReadOnlyList<NewWebhookDelivery> deliveries,
        IReadOnlyCollection<WebhookEntity> notified,
        DateTimeOffset now,
        DateTimeOffset nextCollectAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(deliveries);
        ArgumentNullException.ThrowIfNull(notified);
        await using var tx = await SystemAsync(cancellationToken);

        // 貸し出しが自分のものであるときだけ位置を進める（先に行の鍵を取り、同じ受け手の収集の結果が重ならないようにする）
        await using (var advance = tx.CreateCommand(
            """
            UPDATE webhook_subscription
            SET position_xid = $3, position_id = $4, activated_through = $5, deferred_target = $6, deferred_xid = $7, deferred_id = $8,
                next_collect_at = $9, lease_owner = NULL, lease_until = NULL
            WHERE id = $1 AND lease_owner = $2
            """))
        {
            AddAll(advance,
            [
                P(subscriptionId), P(owner), P(next.Xid), P(next.Id), P(next.ActivatedThrough),
                Nullable(next.Deferred?.Target, NpgsqlDbType.Date), Nullable(next.Deferred?.Xid, NpgsqlDbType.Bigint), Nullable(next.Deferred?.Id, NpgsqlDbType.Bigint),
                P(nextCollectAt),
            ]);
            if (await advance.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await tx.Transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        foreach (var delivery in deliveries)
        {
            await InsertDeliveryAsync(tx, subscriptionId, delivery, now, cancellationToken);
        }

        if (notified.Count > 0)
        {
            await using var record = tx.CreateCommand(
                """
                INSERT INTO webhook_notified_entity (subscription_id, entity_type, entity_id, notified_at)
                SELECT $1, u.entity_type, u.entity_id, $4 FROM unnest($2::text[], $3::uuid[]) AS u (entity_type, entity_id)
                ON CONFLICT (subscription_id, entity_type, entity_id) DO UPDATE SET notified_at = EXCLUDED.notified_at
                """);
            AddAll(record, [P(subscriptionId), Texts(notified.Select(e => WebhookCodes.ToCode(e.Type))), Uuids(notified.Select(e => e.Id)), P(now)]);
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseCollectionAsync(Guid owner, Guid subscriptionId, DateTimeOffset nextCollectAt, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            "UPDATE webhook_subscription SET lease_owner = NULL, lease_until = NULL, next_collect_at = $3 WHERE id = $2 AND lease_owner = $1");
        AddAll(command, [P(owner), P(subscriptionId), P(nextCollectAt)]);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // ---- 送信（ワーカー） ----------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<ClaimedWebhookDelivery>> ClaimDeliveriesAsync(DateTimeOffset now, DateTimeOffset leaseUntil, int max, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);

        // 受け手（登録の行）に鍵を掛けて取り出す。他のワーカーが取り出し中の受け手は飛ばす
        var targets = new List<(Guid Subscription, string Url, int Interval, Guid ClientRef, string ClientId)>();
        await using (var select = tx.CreateCommand(
            $"""
            SELECT s.id, s.url, s.min_interval_seconds, c.id, c.client_id
            FROM webhook_subscription s
            JOIN api_client c ON c.id = s.api_client_id
            WHERE s.status = 'active' AND {ReceiverCondition} AND s.next_send_at <= $1
              AND EXISTS (SELECT 1 FROM webhook_delivery d WHERE d.subscription_id = s.id AND {DueCondition})
            ORDER BY s.next_send_at, s.id
            LIMIT $2
            FOR UPDATE OF s SKIP LOCKED
            """))
        {
            AddAll(select, [P(now), P(max)]);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                targets.Add((reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetGuid(3), reader.GetString(4)));
            }
        }

        var claimed = new List<ClaimedWebhookDelivery>();
        foreach (var target in targets)
        {
            await using (var take = tx.CreateCommand(
                $"""
                UPDATE webhook_delivery AS d SET status = 'sending', lease_until = $3, attempts = d.attempts + 1, last_attempt_at = $1
                WHERE d.id = (
                    SELECT d.id FROM webhook_delivery d
                    WHERE d.subscription_id = $2 AND {DueCondition}
                    ORDER BY d.created_at, d.id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED)
                RETURNING {DeliveryColumns}
                """))
            {
                AddAll(take, [P(now), P(target.Subscription), P(leaseUntil)]);
                foreach (var delivery in await ReadDeliveriesAsync(take, cancellationToken))
                {
                    claimed.Add(new ClaimedWebhookDelivery(delivery, target.Url, target.ClientRef, target.ClientId));
                }
            }

            await using var pace = tx.CreateCommand("UPDATE webhook_subscription SET next_send_at = $2 + make_interval(secs => min_interval_seconds) WHERE id = $1");
            AddAll(pace, [P(target.Subscription), P(now)]);
            await pace.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<IReadOnlyList<StoredWebhookSecret>> ActiveSecretsAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            """
            SELECT id, protected_secret, created_at, expires_at FROM webhook_secret
            WHERE subscription_id = $1 AND (expires_at IS NULL OR expires_at > $2)
            ORDER BY (expires_at IS NULL) DESC, created_at DESC, id DESC
            LIMIT 2
            """);
        AddAll(command, [P(subscriptionId), P(now)]);
        var secrets = new List<StoredWebhookSecret>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                secrets.Add(new StoredWebhookSecret(reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), NullableTime(reader, 3)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return secrets;
    }

    public async Task<bool> CompleteDeliveryAsync(
        Guid deliveryId,
        int attempts,
        WebhookAttemptOutcome outcome,
        int? httpStatus,
        string? error,
        DateTimeOffset now,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken)
    {
        var status = outcome switch
        {
            WebhookAttemptOutcome.Succeeded => "succeeded",
            WebhookAttemptOutcome.Dead => "dead",
            _ => "pending",
        };
        await using var tx = await SystemAsync(cancellationToken);
        Guid? subscription = null;
        await using (var command = tx.CreateCommand(
            """
            UPDATE webhook_delivery
            SET status = $3, lease_until = NULL, last_http_status = $4, last_error = $5,
                succeeded_at = CASE WHEN $3 = 'succeeded' THEN $6 END,
                dead_at = CASE WHEN $3 = 'dead' THEN $6 END,
                next_attempt_at = coalesce($7, next_attempt_at)
            WHERE id = $1 AND status = 'sending' AND attempts = $2
            RETURNING subscription_id
            """))
        {
            AddAll(command,
            [
                P(deliveryId), P(attempts), P(status), Nullable(httpStatus, NpgsqlDbType.Integer), Nullable(error, NpgsqlDbType.Text), P(now),
                Nullable(nextAttemptAt, NpgsqlDbType.TimestampTz),
            ]);
            if (await command.ExecuteScalarAsync(cancellationToken) is Guid id)
            {
                subscription = id;
            }
        }

        if (subscription is not { } subscriptionId)
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using (var result = tx.CreateCommand(
            outcome == WebhookAttemptOutcome.Succeeded
                ? "UPDATE webhook_subscription SET last_success_at = $2 WHERE id = $1"
                : "UPDATE webhook_subscription SET last_failure_at = $2, last_failure = $3 WHERE id = $1"))
        {
            AddAll(result, [P(subscriptionId), P(now)]);
            if (outcome != WebhookAttemptOutcome.Succeeded)
            {
                result.Parameters.Add(Nullable(error, NpgsqlDbType.Text));
            }

            await result.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private static async Task InsertSecretAsync(ViewerTransaction tx, AuthActor actor, Guid subscriptionId, Guid secretId, string protectedSecret, CancellationToken cancellationToken)
    {
        await using var command = tx.CreateCommand(
            "INSERT INTO webhook_secret (id, subscription_id, protected_secret, created_by_kind, created_by_person_id) VALUES ($1, $2, $3, $4, $5)");
        AddAll(command, [P(secretId), P(subscriptionId), P(protectedSecret), P(OperatorKind(actor)), Nullable(actor.PersonId, NpgsqlDbType.Uuid)]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertDeliveryAsync(ViewerTransaction tx, Guid subscriptionId, NewWebhookDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = tx.CreateCommand(
            """
            INSERT INTO webhook_delivery (id, subscription_id, kind, body, event_count, next_attempt_at, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $6)
            """);
        AddAll(command, [P(delivery.Id), P(subscriptionId), P(WebhookCodes.ToCode(delivery.Kind)), P(delivery.Body), P(delivery.EventCount), P(now)]);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static WebhookSubscription ReadSubscription(NpgsqlDataReader reader)
    {
        DeferredProgress? deferred = reader.IsDBNull(10)
            ? null
            : new DeferredProgress(reader.GetFieldValue<DateOnly>(10), reader.GetInt64(11), reader.GetInt64(12));
        return new WebhookSubscription
        {
            Id = reader.GetGuid(0),
            ClientRef = reader.GetGuid(1),
            Url = reader.GetString(2),
            Status = WebhookCodes.ParseStatus(reader.GetString(3)),
            MaxEventsPerDelivery = reader.GetInt32(4),
            SummaryThreshold = reader.GetInt32(5),
            MinIntervalSeconds = reader.GetInt32(6),
            Position = new ChangeFeedPosition(reader.GetInt64(7), reader.GetInt64(8), reader.GetFieldValue<DateOnly>(9), deferred),
            LastSuccessAt = NullableTime(reader, 13),
            LastFailureAt = NullableTime(reader, 14),
            LastFailure = reader.IsDBNull(15) ? null : reader.GetString(15),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(16),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(17),
        };
    }

    private static async Task<List<WebhookDelivery>> ReadDeliveriesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var deliveries = new List<WebhookDelivery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            deliveries.Add(new WebhookDelivery
            {
                Id = reader.GetGuid(0),
                SubscriptionId = reader.GetGuid(1),
                Kind = WebhookCodes.ParseKind(reader.GetString(2)),
                Body = reader.GetString(3),
                EventCount = reader.GetInt32(4),
                Status = WebhookCodes.ParseDeliveryStatus(reader.GetString(5)),
                Attempts = reader.GetInt32(6),
                NextAttemptAt = reader.GetFieldValue<DateTimeOffset>(7),
                LastAttemptAt = NullableTime(reader, 8),
                LastHttpStatus = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                LastError = reader.IsDBNull(10) ? null : reader.GetString(10),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(11),
                SucceededAt = NullableTime(reader, 12),
                DeadAt = NullableTime(reader, 13),
                RedeliveredBy = reader.IsDBNull(14) ? null : reader.GetGuid(14),
                RedeliveredAt = NullableTime(reader, 15),
            });
        }

        return deliveries;
    }

    private Task<ViewerTransaction> SystemAsync(CancellationToken cancellationToken) =>
        ViewerTransaction.BeginRawAsync(_dataSource, "system", string.Empty, null, cancellationToken);

    private Task<ViewerTransaction> BeginAsync(AuthActor actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Kind switch
        {
            AuthActorKind.System or AuthActorKind.Cli => SystemAsync(cancellationToken),
            AuthActorKind.HrAdmin => ViewerTransaction.BeginRawAsync(_dataSource, "user", Policy.Roles.HrAdmin, actor.PersonId, cancellationToken),
            _ => throw new InvalidOperationException("Webhook の管理は管理 CLI か人事管理者の人として行います。"),
        };
    }

    private static string OperatorKind(AuthActor actor) => actor.Kind == AuthActorKind.HrAdmin ? "person" : "cli";

    private static DateTimeOffset? NullableTime(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static NpgsqlParameter Texts(IEnumerable<string> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text };

    private static NpgsqlParameter Uuids(IEnumerable<Guid> values) =>
        new() { Value = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid };

    private static void AddAll(NpgsqlCommand command, NpgsqlParameter[] parameters) => command.Parameters.AddRange(parameters);

    private static NpgsqlParameter Nullable(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
