using System.Net;
using JinJi.Application.Access;
using JinJi.Application.Auth;
using JinJi.Application.Changes;

namespace JinJi.Application.Webhooks;

/// <summary>Webhook の登録の状態（webhook_subscription.status）。</summary>
public enum WebhookStatus
{
    Active,

    /// <summary>止めている（収集も送信もしない。変更フィードの位置は残し、再開すると止めていた間の変更も届く）。</summary>
    Disabled,
}

/// <summary>送信の状態（webhook_delivery.status）。</summary>
public enum WebhookDeliveryStatus
{
    /// <summary>送信待ち（初回・再試行）。</summary>
    Pending,

    /// <summary>ワーカーが送信中（貸し出しの期限を過ぎたら他のワーカーが送り直す）。</summary>
    Sending,

    Succeeded,

    /// <summary>上限回数まで失敗した。人事管理者が確かめて再送する。</summary>
    Dead,
}

/// <summary>送信の種類（webhook_delivery.kind）。</summary>
public enum WebhookDeliveryKind
{
    /// <summary>変更の通知（種別と ID）。</summary>
    Changes,

    /// <summary>大量の変更の要約（件数と変更フィードの位置だけ）。</summary>
    Summary,

    /// <summary>送信の確かめ（人事管理者の操作。署名の検証の確かめにも使う）。</summary>
    Ping,
}

/// <summary>コード値（DB・応答）。</summary>
public static class WebhookCodes
{
    public static string ToCode(WebhookStatus status) => status == WebhookStatus.Disabled ? "disabled" : "active";

    public static WebhookStatus ParseStatus(string code) => code switch
    {
        "active" => WebhookStatus.Active,
        "disabled" => WebhookStatus.Disabled,
        _ => throw new ArgumentException($"未知の Webhook の状態です: {code}（active / disabled）", nameof(code)),
    };

    public static string ToCode(WebhookDeliveryStatus status) => status switch
    {
        WebhookDeliveryStatus.Pending => "pending",
        WebhookDeliveryStatus.Sending => "sending",
        WebhookDeliveryStatus.Succeeded => "succeeded",
        WebhookDeliveryStatus.Dead => "dead",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static WebhookDeliveryStatus ParseDeliveryStatus(string code) => code switch
    {
        "pending" => WebhookDeliveryStatus.Pending,
        "sending" => WebhookDeliveryStatus.Sending,
        "succeeded" => WebhookDeliveryStatus.Succeeded,
        "dead" => WebhookDeliveryStatus.Dead,
        _ => throw new ArgumentException($"未知の送信の状態です: {code}（pending / sending / succeeded / dead）", nameof(code)),
    };

    public static string ToCode(WebhookDeliveryKind kind) => kind switch
    {
        WebhookDeliveryKind.Changes => "changes",
        WebhookDeliveryKind.Summary => "summary",
        WebhookDeliveryKind.Ping => "ping",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static WebhookDeliveryKind ParseKind(string code) => code switch
    {
        "changes" => WebhookDeliveryKind.Changes,
        "summary" => WebhookDeliveryKind.Summary,
        "ping" => WebhookDeliveryKind.Ping,
        _ => throw new ArgumentException($"未知の送信の種類です: {code}", nameof(code)),
    };

    public static string ToCode(ChangeEntityType type) => type switch
    {
        ChangeEntityType.Person => "person",
        ChangeEntityType.OrgUnit => "org_unit",
        ChangeEntityType.Site => "site",
        ChangeEntityType.Group => "group",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static ChangeEntityType ParseEntityType(string code) => code switch
    {
        "person" => ChangeEntityType.Person,
        "org_unit" => ChangeEntityType.OrgUnit,
        "site" => ChangeEntityType.Site,
        "group" => ChangeEntityType.Group,
        _ => throw new ArgumentException($"未知の変更の種別です: {code}", nameof(code)),
    };
}

/// <summary>Webhook の登録（webhook_subscription の 1 行）。</summary>
public sealed record WebhookSubscription
{
    public required Guid Id { get; init; }

    /// <summary>受け手のクライアント（api_client.id）。</summary>
    public required Guid ClientRef { get; init; }

    public required string Url { get; init; }

    public required WebhookStatus Status { get; init; }

    public required int MaxEventsPerDelivery { get; init; }

    public required int SummaryThreshold { get; init; }

    public required int MinIntervalSeconds { get; init; }

    /// <summary>受け手のクライアントを閲覧者として読み進めた変更フィードの位置。</summary>
    public required ChangeFeedPosition Position { get; init; }

    public DateTimeOffset? LastSuccessAt { get; init; }

    public DateTimeOffset? LastFailureAt { get; init; }

    /// <summary>最後の失敗の理由（URL・本文・応答の本文は含まない）。</summary>
    public string? LastFailure { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>署名の鍵の情報（値は含まない）。</summary>
/// <param name="ExpiresAt">ローテーションで置き換えた旧鍵の期限（null は今の鍵）。</param>
public sealed record WebhookSecretInfo(Guid Id, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>保存した署名の鍵（Data Protection で暗号化した値）。</summary>
public sealed record StoredWebhookSecret(Guid Id, string ProtectedSecret, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

/// <summary>送信の件数（登録ごと）。</summary>
/// <param name="OldestPendingAt">最も古い送信待ちの作成日時（滞留の目安）。</param>
public sealed record WebhookDeliveryCounts(int Pending, int Dead, int Succeeded, DateTimeOffset? OldestPendingAt);

/// <summary>管理画面の 1 行（登録・受け手のクライアント・件数・鍵）。</summary>
public sealed record WebhookSubscriptionView(
    WebhookSubscription Subscription,
    string ClientId,
    string? ClientName,
    OAuthClientStatus ClientStatus,
    int DisclosureLeadDays,
    WebhookDeliveryCounts Counts,
    IReadOnlyList<WebhookSecretInfo> Secrets);

/// <summary>送信（webhook_delivery の 1 行）。</summary>
public sealed record WebhookDelivery
{
    /// <summary>イベント ID（冪等キー・nonce。本文の id と webhook-id ヘッダーに同じ値を載せる）。</summary>
    public required Guid Id { get; init; }

    public required Guid SubscriptionId { get; init; }

    public required WebhookDeliveryKind Kind { get; init; }

    public required string Body { get; init; }

    public required int EventCount { get; init; }

    public required WebhookDeliveryStatus Status { get; init; }

    public required int Attempts { get; init; }

    public required DateTimeOffset NextAttemptAt { get; init; }

    public DateTimeOffset? LastAttemptAt { get; init; }

    public int? LastHttpStatus { get; init; }

    public string? LastError { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? SucceededAt { get; init; }

    public DateTimeOffset? DeadAt { get; init; }

    public Guid? RedeliveredBy { get; init; }

    public DateTimeOffset? RedeliveredAt { get; init; }
}

/// <summary>積む送信。</summary>
public sealed record NewWebhookDelivery(Guid Id, WebhookDeliveryKind Kind, string Body, int EventCount);

/// <summary>ワーカーが取り出した送信（宛先の URL と、受け手の登録の送信の間隔）。</summary>
public sealed record ClaimedWebhookDelivery(WebhookDelivery Delivery, string Url, Guid ClientRef, string ClientId);

/// <summary>通知した対象（取り消しの宛先の判定に使う）。</summary>
public sealed record WebhookEntity(ChangeEntityType Type, Guid Id);

/// <summary>1 回の試行の結果の記録。</summary>
public enum WebhookAttemptOutcome
{
    Succeeded,

    /// <summary>失敗。次の試行の日時に再送する。</summary>
    Retry,

    /// <summary>上限回数まで失敗した。</summary>
    Dead,
}

/// <summary>登録・変更の設定（指定しない項目は既定のまま・今のまま）。</summary>
public sealed record WebhookSettings(int? MaxEventsPerDelivery = null, int? SummaryThreshold = null, int? MinIntervalSeconds = null);

/// <summary>送信先の URL の検査（登録・変更のとき。送信の直前には DNS の解決の後のアドレスも確かめる）。</summary>
public static class WebhookUrls
{
    public const int MaxLength = 2048;

    /// <summary>
    /// https だけ・利用者情報とフラグメントなし・2048 文字まで・ホストがある。IP アドレスのホストは公開のアドレスだけ、localhost は不可。
    /// 不正なら誤りの理由（URL の値は含めない）。
    /// </summary>
    public static string? Validate(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength || url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
        {
            return $"送信先の URL は空白・制御文字を含まない {MaxLength} 文字までの https の URL で指定してください。";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
        {
            return "送信先の URL は https で指定してください。";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo) || url.Contains('#', StringComparison.Ordinal) || string.IsNullOrEmpty(parsed.Host))
        {
            return "送信先の URL に利用者情報・フラグメントは使えません。";
        }

        var host = parsed.IdnHost.Trim('[', ']');
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "送信先に localhost は使えません。";
        }

        if (IPAddress.TryParse(host, out var address) && !PublicAddresses.IsPublic(address))
        {
            return "送信先に公開でないアドレス（私設・ループバックなど）は使えません。";
        }

        uri = parsed;
        return null;
    }
}
