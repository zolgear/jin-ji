namespace JinJi.Application.Webhooks;

/// <summary>
/// 社内システムへの Webhook の設定（appsettings の <c>JinJi:Webhooks</c>。アーキテクチャ概要 §9「Webhook の実装の規則」）。
/// 登録ごとの件数・間隔は登録（webhook_subscription）に持ち、ここはワーカーの動かし方と再送の規則だけを持つ。
/// </summary>
public sealed record WebhookOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Webhooks";

    /// <summary>ワーカー（--role worker / all）で収集と送信を動かすか（既定 true）。false でも登録・管理はできる。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>収集と送信の間隔（既定 10 秒。1 秒〜10 分）。登録ごとの送信の最小の間隔はこれより短くしても、この間隔より細かくは送らない。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>ワーカーの起動から最初の実行までの待ち（既定 30 秒）。</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>1 回の送信の時間の上限（既定 10 秒。1〜60 秒）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>試行の上限回数（既定 8。1〜20）。超えたら dead にして人事管理者が確かめる。</summary>
    public int MaxAttempts { get; init; } = 8;

    /// <summary>再試行の待ちの初めの長さ（既定 30 秒）。n 回目の失敗の後は 初め × 2^(n-1)（上限 <see cref="RetryMaxDelay"/>）に 0〜10% の揺らぎを足す。</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>再試行の待ちの上限（既定 6 時間）。既定の 8 回では最初の失敗から約 1 時間で dead になる。</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromHours(6);

    /// <summary>1 回の実行で収集する登録の数（既定 20）。</summary>
    public int SubscriptionsPerRun { get; init; } = 20;

    /// <summary>1 回の実行で送る数（既定 50。同じ受け手には 1 回の実行で 1 つだけ）。</summary>
    public int DeliveriesPerRun { get; init; } = 50;

    /// <summary>同時に送る数（既定 4）。</summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>1 回の収集で変更フィードから読む行の上限（既定 100,000）。超えた分は次の収集で読む。</summary>
    public int MaxScanRows { get; init; } = 100_000;

    /// <summary>収集の貸し出しの長さ（既定 2 分。ワーカーが落ちたら、この後に他のワーカーが引き継ぐ）。</summary>
    public TimeSpan CollectLease { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>鍵のローテーションで旧鍵でも署名を続ける既定の期間（既定 24 時間。0〜7 日）。</summary>
    public TimeSpan DefaultRotationOverlap { get; init; } = TimeSpan.FromHours(24);

    /// <summary>旧鍵でも署名を続ける期間の上限。</summary>
    public static readonly TimeSpan MaxRotationOverlap = TimeSpan.FromDays(7);

    /// <summary>
    /// 1 回の実行で取り出した送信の貸し出しの長さ（同時に送る数ずつ順に送り終えるまでの時間の上限 + 時間の上限 1 回分 + 30 秒）。
    /// これより後まで送れなかった送信は送らずに残し、期限の後に（このワーカーか他のワーカーが）送る。
    /// </summary>
    public TimeSpan SendLease => (Timeout * ((DeliveriesPerRun + Concurrency - 1) / Concurrency + 1)) + TimeSpan.FromSeconds(30);

    /// <summary>n 回目の失敗の後の待ち（揺らぎなし）。</summary>
    public TimeSpan RetryDelay(int attempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        var factor = Math.Pow(2, Math.Min(attempts - 1, 30));
        var delay = TimeSpan.FromTicks((long)Math.Min(RetryBaseDelay.Ticks * factor, RetryMaxDelay.Ticks));
        return delay;
    }

    /// <summary>値を検査する。不正なら起動させない。</summary>
    public void Validate()
    {
        if (Interval < TimeSpan.FromSeconds(1) || Interval > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(Interval), Interval, "JinJi:Webhooks:Interval は 1 秒〜10 分で指定してください。");
        }

        if (InitialDelay < TimeSpan.Zero || InitialDelay > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(InitialDelay), InitialDelay, "JinJi:Webhooks:InitialDelay は 0〜1 日で指定してください。");
        }

        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "JinJi:Webhooks:Timeout は 1〜60 秒で指定してください。");
        }

        if (MaxAttempts is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts), MaxAttempts, "JinJi:Webhooks:MaxAttempts は 1〜20 で指定してください。");
        }

        if (RetryBaseDelay < TimeSpan.FromSeconds(1) || RetryMaxDelay < RetryBaseDelay || RetryMaxDelay > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(RetryBaseDelay), "JinJi:Webhooks の RetryBaseDelay は 1 秒以上、RetryMaxDelay はそれ以上 1 日以下で指定してください。");
        }

        if (SubscriptionsPerRun is < 1 or > 1000 || DeliveriesPerRun is < 1 or > 1000 || Concurrency is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(SubscriptionsPerRun), "JinJi:Webhooks の SubscriptionsPerRun・DeliveriesPerRun は 1〜1000、Concurrency は 1〜32 で指定してください。");
        }

        if (MaxScanRows is < 1000 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxScanRows), MaxScanRows, "JinJi:Webhooks:MaxScanRows は 1,000〜1,000,000 で指定してください。");
        }

        if (CollectLease < TimeSpan.FromSeconds(30) || CollectLease > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(CollectLease), CollectLease, "JinJi:Webhooks:CollectLease は 30 秒〜1 時間で指定してください。");
        }

        if (DefaultRotationOverlap < TimeSpan.Zero || DefaultRotationOverlap > MaxRotationOverlap)
        {
            throw new ArgumentOutOfRangeException(nameof(DefaultRotationOverlap), DefaultRotationOverlap, "JinJi:Webhooks:DefaultRotationOverlap は 0〜7 日で指定してください。");
        }
    }
}
