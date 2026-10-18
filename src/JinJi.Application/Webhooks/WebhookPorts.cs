using JinJi.Application.Auth;
using JinJi.Application.Changes;

namespace JinJi.Application.Webhooks;

/// <summary>
/// Webhook の表（0018）の保存先（Infrastructure が実装する）。管理は <see cref="AuthActor"/>（人事管理者の人・管理 CLI）、
/// 収集と送信は jin-ji 自身（system）の閲覧者で読み書きする。状態の遷移・中身の不変は DB のトリガーが守る。
/// </summary>
public interface IWebhookStore
{
    // ---- 管理 ----------------------------------------------------------------------------------------------------

    /// <summary>登録の一覧（受け手のクライアント・件数・鍵の情報つき）。</summary>
    Task<IReadOnlyList<WebhookSubscriptionView>> ListAsync(AuthActor actor, DateTimeOffset now, CancellationToken cancellationToken);

    Task<WebhookSubscription?> FindAsync(AuthActor actor, Guid id, CancellationToken cancellationToken);

    /// <summary>登録と最初の鍵を作る（1 トランザクション）。同じクライアントの登録が既にあれば false。</summary>
    Task<bool> CreateAsync(AuthActor actor, WebhookSubscription subscription, Guid secretId, string protectedSecret, CancellationToken cancellationToken);

    /// <summary>送信先・状態・件数・間隔を保存する。無ければ false。</summary>
    Task<bool> UpdateAsync(AuthActor actor, WebhookSubscription subscription, CancellationToken cancellationToken);

    /// <summary>
    /// 鍵を入れ替える（1 トランザクション）。今の鍵に期限 <paramref name="previousExpiresAt"/> を記録し、新しい鍵を今の鍵にする。
    /// 期限の前に置き換えた旧鍵（まだ期限の来ていないもの）は、今すぐ期限切れにする（重ねて署名するのは直前の鍵だけ）。登録が無ければ false。
    /// </summary>
    Task<bool> RotateSecretAsync(
        AuthActor actor,
        Guid subscriptionId,
        Guid secretId,
        string protectedSecret,
        DateTimeOffset previousExpiresAt,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>送信の一覧（新しい順に最大 <paramref name="limit"/> 件）。</summary>
    Task<IReadOnlyList<WebhookDelivery>> ListDeliveriesAsync(
        AuthActor actor,
        Guid subscriptionId,
        WebhookDeliveryStatus? status,
        int limit,
        CancellationToken cancellationToken);

    /// <summary>
    /// dead の送信を送信待ちに戻す（試行回数を 0 に、再送した人を記録）。<paramref name="deliveryId"/> を指定しなければ、その登録の dead をすべて。戻した件数。
    /// </summary>
    Task<int> RedeliverAsync(AuthActor actor, Guid subscriptionId, Guid? deliveryId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>送信の確かめ（ping）を積む。</summary>
    Task InsertPingAsync(AuthActor actor, Guid subscriptionId, NewWebhookDelivery delivery, DateTimeOffset now, CancellationToken cancellationToken);

    // ---- 収集（ワーカー） ----------------------------------------------------------------------------------------

    /// <summary>
    /// 収集の時期が来た有効な登録（受け手のクライアントが有効なもの）を最大 <paramref name="max"/> 件、貸し出す（FOR UPDATE SKIP LOCKED。
    /// 他のワーカーの貸し出し中のものは飛ばす）。
    /// </summary>
    Task<IReadOnlyList<WebhookSubscription>> ClaimForCollectionAsync(
        Guid owner,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        int max,
        CancellationToken cancellationToken);

    /// <summary>指定した対象のうち、この登録で以前に通知したもの。</summary>
    Task<IReadOnlySet<WebhookEntity>> NotifiedAsync(Guid subscriptionId, IReadOnlyCollection<WebhookEntity> entities, CancellationToken cancellationToken);

    /// <summary>
    /// 収集の結果を保存する（1 トランザクション）: 送信を積み、通知した対象を記録し、変更フィードの位置を進め、貸し出しを返す。
    /// 貸し出しが自分のものでなくなっていれば（期限切れで他のワーカーが引き継いだ）何もせずに false。
    /// </summary>
    Task<bool> CompleteCollectionAsync(
        Guid owner,
        Guid subscriptionId,
        ChangeFeedPosition next,
        IReadOnlyList<NewWebhookDelivery> deliveries,
        IReadOnlyCollection<WebhookEntity> notified,
        DateTimeOffset now,
        DateTimeOffset nextCollectAt,
        CancellationToken cancellationToken);

    /// <summary>収集せずに貸し出しを返す（受け手のクライアントが使えない・失敗したとき）。</summary>
    Task ReleaseCollectionAsync(Guid owner, Guid subscriptionId, DateTimeOffset nextCollectAt, CancellationToken cancellationToken);

    // ---- 送信（ワーカー） ----------------------------------------------------------------------------------------

    /// <summary>
    /// 送る時期が来た送信を、受け手ごとに古い順で 1 つずつ（最大 <paramref name="max"/> 受け手）取り出す。送信中にし（試行回数を 1 足し、
    /// 貸し出しの期限を <paramref name="leaseUntil"/> に）、受け手の次の送信を最小の間隔の後にする。登録・受け手のクライアントが有効なものだけ。
    /// </summary>
    Task<IReadOnlyList<ClaimedWebhookDelivery>> ClaimDeliveriesAsync(DateTimeOffset now, DateTimeOffset leaseUntil, int max, CancellationToken cancellationToken);

    /// <summary>署名に使う鍵（今の鍵と、期限の来ていない旧鍵）。</summary>
    Task<IReadOnlyList<StoredWebhookSecret>> ActiveSecretsAsync(Guid subscriptionId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// 試行の結果を記録する。送信中で試行回数が <paramref name="attempts"/> のままのときだけ（他のワーカーが引き継いでいれば false）。
    /// 登録の最終の成功・失敗も更新する。
    /// </summary>
    Task<bool> CompleteDeliveryAsync(
        Guid deliveryId,
        int attempts,
        WebhookAttemptOutcome outcome,
        int? httpStatus,
        string? error,
        DateTimeOffset now,
        DateTimeOffset? nextAttemptAt,
        CancellationToken cancellationToken);
}

/// <summary>Webhook の送信（Host が実装する。https・公開のアドレスだけ・リダイレクトに従わない・時間の上限は実装が守る）。</summary>
public interface IWebhookSender
{
    /// <summary>JSON の本文をヘッダーとともに POST する。2xx だけが成功。</summary>
    Task<WebhookSendResult> SendAsync(Uri url, string body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken);
}

/// <summary>送信の結果。</summary>
/// <param name="Error">失敗の理由（URL・本文・応答の本文は含めない）。</param>
public sealed record WebhookSendResult(bool Succeeded, int? HttpStatus, string? Error);

/// <summary>署名の鍵の暗号化（Host が Data Protection で実装する。DB には暗号化した値だけを持つ）。</summary>
public interface IWebhookSecretProtector
{
    string Protect(byte[] secret);

    byte[] Unprotect(string protectedSecret);
}
