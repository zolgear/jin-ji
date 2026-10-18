namespace JinJi.Application.Corrections;

/// <summary>
/// 修正依頼の設定（appsettings の <c>JinJi:Corrections</c>。アーキテクチャ概要 §8「修正依頼の実装の規則」）。
/// </summary>
public sealed record CorrectionOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Corrections";

    /// <summary>
    /// 1 人（AI エージェントは委任元）が 24 時間に起票・賛同できる件数（既定 20）。経路の段のレート制限（1 分あたり）とは別に、
    /// エージェントの暴走で修正担当者の作業リストが埋まらないように抑える。
    /// </summary>
    public int MaxReportsPerDay { get; init; } = 20;

    /// <summary>「新しい依頼」とみなす日数（起票から。既定 7 日）。画面内の通知に使う。</summary>
    public int NewWithinDays { get; init; } = 7;

    /// <summary>「長く放置された依頼」とみなす日数（起票から。既定 14 日）。</summary>
    public int StaleAfterDays { get; init; } = 14;

    /// <summary>レポート（CSV・xlsx）の最大の行数（既定 5,000）。超えたら古い順に打ち切り、打ち切ったことを返す。</summary>
    public int MaxExportRows { get; init; } = 5000;

    /// <summary>通知（ワーカーの定期ジョブ）。</summary>
    public CorrectionNoticeOptions Notice { get; init; } = new();

    /// <summary>値を検査する。不正なら起動させない。</summary>
    public void Validate()
    {
        if (MaxReportsPerDay is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxReportsPerDay), MaxReportsPerDay, "MaxReportsPerDay は 1〜1000 で指定してください。");
        }

        if (NewWithinDays is < 1 or > 90 || StaleAfterDays is < 1 or > 365)
        {
            throw new ArgumentOutOfRangeException(nameof(StaleAfterDays), "NewWithinDays は 1〜90、StaleAfterDays は 1〜365 で指定してください。");
        }

        if (MaxExportRows is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxExportRows), MaxExportRows, "MaxExportRows は 1〜100,000 で指定してください。");
        }

        Notice.Validate();
    }
}

/// <summary>通知の定期ジョブ（自動の解消の判定 → 件数の集計 → 任意の Webhook）。</summary>
public sealed record CorrectionNoticeOptions
{
    /// <summary>ワーカーの定期実行を行うか（既定 true。false でも管理 CLI の <c>notify-corrections</c> は使える）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>実行の間隔（既定 1 日。5 分〜7 日）。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromDays(1);

    /// <summary>ワーカーの起動から最初の実行までの待ち（既定 10 分）。</summary>
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Webhook（Teams のワークフロー・Slack の Incoming Webhook。既定は無効）。</summary>
    public CorrectionWebhookOptions Webhook { get; init; } = new();

    public void Validate()
    {
        if (Interval < TimeSpan.FromMinutes(5) || Interval > TimeSpan.FromDays(7))
        {
            throw new ArgumentOutOfRangeException(nameof(Interval), Interval, "通知の間隔は 5 分〜7 日で指定してください。");
        }

        if (InitialDelay < TimeSpan.Zero || InitialDelay > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(InitialDelay), InitialDelay, "最初の実行までの待ちは 0〜1 日で指定してください。");
        }

        Webhook.Validate();
    }
}

/// <summary>Webhook の本文の形。</summary>
public enum CorrectionWebhookFormat
{
    /// <summary>Slack の Incoming Webhook（<c>{"text": ...}</c>）。</summary>
    Slack,

    /// <summary>Teams のワークフロー（「Teams Webhook 要求を受信したとき」。Adaptive Card の添付を持つメッセージ）。</summary>
    Teams,
}

/// <summary>
/// 任意の Webhook。jin-ji はメールを送らない（ADR-0004）ので、チャットへの通知はこれだけ。本文は件数と jin-ji へのリンクだけで、人事データの値は載せない。
/// </summary>
/// <remarks>
/// 送信先は https だけで、ホストは許可リスト（<see cref="AllowedHosts"/>。完全一致）にあるものだけ。DNS の解決の後のアドレスが公開でなければ接続しない（CIMD の取得と同じ守り。§6.5）。
/// Webhook の URL は秘密を含む（知っていれば誰でも投稿できる）ので、設定ファイルではなく環境変数（<c>JinJi__Corrections__Notice__Webhook__Url</c>）で渡し、ログ・記録にはホストだけを残す。
/// </remarks>
public sealed record CorrectionWebhookOptions
{
    /// <summary>Webhook に送るか（既定 false）。</summary>
    public bool Enabled { get; init; }

    /// <summary>送信先（https）。</summary>
    public string? Url { get; init; }

    /// <summary>本文の形（slack / teams）。</summary>
    public CorrectionWebhookFormat Format { get; init; } = CorrectionWebhookFormat.Slack;

    /// <summary>送信先のホストの許可リスト（完全一致。例: hooks.slack.com、prod-00.japaneast.logic.azure.com）。</summary>
    public IReadOnlyList<string> AllowedHosts { get; init; } = [];

    /// <summary>
    /// 通知に載せる jin-ji の修正依頼の画面の URL（https）。未設定なら人のログインの公開 URL（<c>JinJi:Auth:Login:PublicOrigin</c>）+ <c>/#/corrections</c>（Host が補う）。
    /// </summary>
    public string? LinkUrl { get; init; }

    /// <summary>送信の時間の上限（既定 10 秒。1〜60 秒）。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>送信先の URL（検査済み）。無効なら null。</summary>
    public Uri? Destination => Enabled && Url is { } url ? new Uri(url, UriKind.Absolute) : null;

    public void Validate()
    {
        if (Timeout < TimeSpan.FromSeconds(1) || Timeout > TimeSpan.FromSeconds(60))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "Webhook の時間の上限は 1〜60 秒で指定してください。");
        }

        if (LinkUrl is { } link && (!Uri.TryCreate(link, UriKind.Absolute, out var linkUri) || !IsHttpsOrLoopback(linkUri)))
        {
            throw new ArgumentException("JinJi:Corrections:Notice:Webhook:LinkUrl は https の URL で指定してください。", nameof(LinkUrl));
        }

        if (!Enabled)
        {
            return;
        }

        // 値（URL）は誤りの文に出さない（秘密を含むため）
        if (Url is null || !Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("JinJi:Corrections:Notice:Webhook:Url は https の URL（利用者情報・フラグメントなし）で指定してください。", nameof(Url));
        }

        var hosts = AllowedHosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (!hosts.Contains(uri.IdnHost.ToLowerInvariant()))
        {
            throw new ArgumentException($"Webhook の送信先のホスト {uri.IdnHost} が JinJi:Corrections:Notice:Webhook:AllowedHosts にありません。", nameof(AllowedHosts));
        }
    }

    private static bool IsHttpsOrLoopback(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
}
