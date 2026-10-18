using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Corrections;

/// <summary>修正依頼の通知のメトリクス（Meter <c>JinJi.Corrections</c>。API の役割と同じプロセスならメトリクスのポートに出る）。</summary>
public sealed class CorrectionMetrics
{
    public const string MeterName = "JinJi.Corrections";

    private readonly Counter<long> _notices;
    private readonly Counter<long> _autoResolved;

    public CorrectionMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Meter = meterFactory.Create(MeterName);
        _notices = Meter.CreateCounter<long>("jinji.correction.notices", "{run}", "修正依頼の通知の実行回数（契機・結果別）");
        _autoResolved = Meter.CreateCounter<long>("jinji.correction.auto_resolved", "{request}", "値が変わったので自動で解消にした修正依頼の数");
    }

    public Meter Meter { get; }

    public void Record(CorrectionNoticeRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _notices.Add(
            1,
            new KeyValuePair<string, object?>("trigger", run.Trigger == Channel.Cli ? "cli" : "worker"),
            new KeyValuePair<string, object?>("outcome", CorrectionNoticeService.OutcomeCode(run.Outcome)));
        if (run.AutoResolved > 0)
        {
            _autoResolved.Add(run.AutoResolved);
        }
    }
}

/// <summary>
/// 修正依頼の通知（ワーカーの定期ジョブと管理 CLI の <c>notify-corrections</c>）。jin-ji はメールを送らない（ADR-0004）ので、
/// 画面内の通知（<see cref="CorrectionRequestService.SummaryAsync"/>）と、任意の Webhook（Teams のワークフロー・Slack の Incoming Webhook）だけ。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>1 回の実行で: 自動の解消の判定（値が変わった依頼を閉じる。将来の日付で効いた変更も拾う）→ 未対応の件数の集計 → Webhook（有効で、
/// 前回送ってからの新しい依頼か長く放置された依頼があるときだけ）</item>
/// <item>Webhook の本文は件数と jin-ji へのリンクだけ。人・項目・理由などの人事データの値は載せない（受け手のチャットは jin-ji の認可の外にあるため）</item>
/// <item>実行ごとに件数・結果・送信先のホストを correction_notice_run に残し、監査ログ（correction.notify。操作者 system）とメトリクスに出す。
/// URL は秘密を含むので残さない</item>
/// </list>
/// </remarks>
public sealed class CorrectionNoticeService
{
    public const string AuditAction = "correction.notify";
    public const string AuditTarget = "correction_notice_run";

    private readonly ICorrectionStore _store;
    private readonly CorrectionRequestService _corrections;
    private readonly ICorrectionWebhookSender? _sender;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly CorrectionMetrics? _metrics;

    public CorrectionNoticeService(
        ICorrectionStore store,
        CorrectionRequestService corrections,
        AuditWriter audit,
        TimeProvider timeProvider,
        ICorrectionWebhookSender? sender = null,
        CorrectionMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(corrections);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _corrections = corrections;
        _sender = sender;
        _audit = audit;
        _timeProvider = timeProvider;
        _metrics = metrics;
    }

    /// <summary>1 回実行する。<paramref name="trigger"/> はワーカー（定期実行）か管理 CLI。</summary>
    public async Task<CorrectionNoticeRun> RunAsync(Channel trigger, CancellationToken cancellationToken)
    {
        if (trigger is not (Channel.Worker or Channel.Cli))
        {
            throw new ArgumentException("通知はワーカーか管理 CLI から実行します。", nameof(trigger));
        }

        var viewer = ViewerContext.ForSystem(trigger);
        var webhook = _corrections.Options.Notice.Webhook;
        var startedAt = _timeProvider.GetUtcNow();
        var autoResolved = 0;
        var counts = new CorrectionCounts(0, 0, 0, null);
        var outcome = CorrectionNoticeOutcome.Failed;
        int? status = null;
        string? host = null;
        Exception? failure = null;
        try
        {
            autoResolved = await _corrections.ReconcileAsync(viewer, null, cancellationToken);
            var lastSent = await _store.LastSentNoticeAsync(cancellationToken);
            counts = await _corrections.CountAsync(viewer, lastSent, cancellationToken);
            if (webhook.Destination is not { } url || _sender is null)
            {
                outcome = CorrectionNoticeOutcome.Disabled;
            }
            else if (counts.New == 0 && counts.Stale == 0)
            {
                outcome = CorrectionNoticeOutcome.NothingToSend;
            }
            else
            {
                host = url.IdnHost;
                var result = await _sender.SendAsync(url, Body(webhook, counts, _corrections.Options.StaleAfterDays), cancellationToken);
                status = result.HttpStatus;
                outcome = result.Succeeded ? CorrectionNoticeOutcome.Sent : CorrectionNoticeOutcome.Failed;
                if (!result.Succeeded)
                {
                    failure = new CorrectionNoticeException($"Webhook（{host}）への送信に失敗しました: {result.Error}");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex;
            outcome = CorrectionNoticeOutcome.Failed;
        }

        var run = new CorrectionNoticeRun
        {
            Id = EntityId.New(),
            Trigger = trigger,
            Outcome = outcome,
            StartedAt = startedAt,
            FinishedAt = _timeProvider.GetUtcNow(),
            Counts = counts,
            AutoResolved = autoResolved,
            DestinationHost = host,
            HttpStatus = status,
        };

        // 記録は取り消しの要求に関わらず残す（送った・送れなかった事実を失わない）
        await _store.RecordNoticeAsync(run, CancellationToken.None);
        await _audit.RecordAsync(
            viewer,
            AuditAction,
            outcome == CorrectionNoticeOutcome.Failed ? AuditOutcome.Failed : AuditOutcome.Success,
            [],
            CancellationToken.None,
            AuditTarget,
            run.Id);
        _metrics?.Record(run);
        if (failure is not null)
        {
            throw failure as CorrectionNoticeException ?? new CorrectionNoticeException("修正依頼の通知に失敗しました（実行の記録は correction_notice_run に残しました）。", failure);
        }

        return run;
    }

    public static string OutcomeCode(CorrectionNoticeOutcome outcome) => outcome switch
    {
        CorrectionNoticeOutcome.Sent => "sent",
        CorrectionNoticeOutcome.NothingToSend => "nothing_to_send",
        CorrectionNoticeOutcome.Disabled => "disabled",
        CorrectionNoticeOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    /// <summary>
    /// Webhook の本文（件数と jin-ji へのリンクだけ。人事データの値は載せない）。Slack は <c>{"text"}</c>、Teams のワークフローは Adaptive Card の添付を持つメッセージ。
    /// </summary>
    public static string Body(CorrectionWebhookOptions webhook, CorrectionCounts counts, int staleAfterDays)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        ArgumentNullException.ThrowIfNull(counts);
        string N(int value) => value.ToString(CultureInfo.InvariantCulture);
        var summary = $"未対応の修正依頼が {N(counts.Open)} 件あります（新しい依頼 {N(counts.New)} 件、起票から {N(staleAfterDays)} 日以上の未対応 {N(counts.Stale)} 件）。"
            + "人事パッケージで確認し、jin-ji で対応済み・対応不要にしてください。";
        var link = webhook.LinkUrl;
        if (webhook.Format == CorrectionWebhookFormat.Teams)
        {
            var card = new Dictionary<string, object?>
            {
                ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
                ["type"] = "AdaptiveCard",
                ["version"] = "1.4",
                ["body"] = new object[]
                {
                    new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = "jin-ji: 修正依頼", ["weight"] = "Bolder", ["size"] = "Medium" },
                    new Dictionary<string, object?> { ["type"] = "TextBlock", ["text"] = summary, ["wrap"] = true },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "FactSet",
                        ["facts"] = new object[]
                        {
                            new Dictionary<string, string> { ["title"] = "未対応", ["value"] = N(counts.Open) + " 件" },
                            new Dictionary<string, string> { ["title"] = "新しい依頼", ["value"] = N(counts.New) + " 件" },
                            new Dictionary<string, string> { ["title"] = $"{N(staleAfterDays)} 日以上", ["value"] = N(counts.Stale) + " 件" },
                        },
                    },
                },
                ["actions"] = link is null
                    ? Array.Empty<object>()
                    : new object[] { new Dictionary<string, string> { ["type"] = "Action.OpenUrl", ["title"] = "jin-ji で開く", ["url"] = link } },
            };
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "message",
                ["attachments"] = new object[]
                {
                    new Dictionary<string, object?> { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["contentUrl"] = null, ["content"] = card },
                },
            });
        }

        return JsonSerializer.Serialize(new Dictionary<string, string> { ["text"] = "jin-ji: " + summary + (link is null ? string.Empty : " " + link) });
    }
}

/// <summary>通知が失敗した（実行の記録は残した）。</summary>
public sealed class CorrectionNoticeException : Exception
{
    public CorrectionNoticeException()
    {
    }

    public CorrectionNoticeException(string message)
        : base(message)
    {
    }

    public CorrectionNoticeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
