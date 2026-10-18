using JinJi.Policy;

namespace JinJi.Application.Corrections;

/// <summary>同じ人・項目の未対応の報告（jin-ji 自身の閲覧者で引く。ID と起票者だけ）。</summary>
public sealed record OpenReportKey(Guid Id, Guid ReportedByPersonId);

/// <summary>未対応の件数（人事データの値は含まない）。</summary>
public sealed record CorrectionCounts(int Open, int New, int Stale, DateTimeOffset? OldestCreatedAt);

/// <summary>通知の実行の記録（correction_notice_run の 1 行）。</summary>
public sealed record CorrectionNoticeRun
{
    public required Guid Id { get; init; }

    public required Channel Trigger { get; init; }

    public required CorrectionNoticeOutcome Outcome { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required DateTimeOffset FinishedAt { get; init; }

    public required CorrectionCounts Counts { get; init; }

    public int AutoResolved { get; init; }

    /// <summary>送信先のホスト（URL の全体は秘密を含み得るので残さない）。</summary>
    public string? DestinationHost { get; init; }

    public int? HttpStatus { get; init; }
}

/// <summary>通知の結果。</summary>
public enum CorrectionNoticeOutcome
{
    /// <summary>Webhook に送った。</summary>
    Sent,

    /// <summary>新しい依頼も長く放置された依頼も無いので送らなかった。</summary>
    NothingToSend,

    /// <summary>Webhook が無効（画面内の通知だけ）。</summary>
    Disabled,

    /// <summary>送信に失敗した。</summary>
    Failed,
}

/// <summary>
/// 修正依頼の保存先（Infrastructure が実装する）。可視範囲・起票者・状態の遷移・中身の不変の最終的な線は DB が引く（0015 の RLS・制約・トリガー）。
/// </summary>
public interface ICorrectionStore
{
    /// <summary>
    /// 人・エージェントの報告を追加する。同じ人・項目の未対応の報告があれば（部分一意索引）追加せずに false。
    /// </summary>
    Task<bool> TryInsertReportAsync(ViewerContext viewer, CorrectionRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// 同じ人・項目の未対応の報告を引く（jin-ji 自身の閲覧者で読む。起票者以外には見えない行なので、賛同の記録に要る ID と起票者だけを返す）。
    /// </summary>
    Task<OpenReportKey?> FindOpenReportAsync(Guid personId, string field, CancellationToken cancellationToken);

    /// <summary>賛同を記録する（閲覧者本人として）。既に賛同していれば false。</summary>
    Task<bool> TryEndorseAsync(ViewerContext viewer, Guid requestId, Guid endorsementId, CancellationToken cancellationToken);

    /// <summary>閲覧者本人が指定の日時より後に起票・賛同した件数（1 日の上限の判定）。</summary>
    Task<int> CountRecentReportsAsync(ViewerContext viewer, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>閲覧者に見える依頼（RLS）。見えなければ null。</summary>
    Task<CorrectionRecord?> FindAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken);

    /// <summary>閲覧者に見える依頼を ID の順に、条件の件数 + 1 件まで（続きの有無の判定に使う）。</summary>
    Task<IReadOnlyList<CorrectionRecord>> ListAsync(ViewerContext viewer, CorrectionQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// 未対応の依頼を閉じる（1 文の UPDATE。未対応でなければ false）。閉じた人は閲覧者本人としてしか記録できない（RLS）。
    /// </summary>
    Task<bool> CloseAsync(
        ViewerContext viewer,
        Guid id,
        CorrectionStatus status,
        CorrectionClosedKind kind,
        string? note,
        CancellationToken cancellationToken);

    /// <summary>未対応の件数（閲覧者に見える範囲）。</summary>
    Task<CorrectionCounts> CountOpenAsync(ViewerContext viewer, DateTimeOffset newSince, DateTimeOffset staleBefore, CancellationToken cancellationToken);

    /// <summary>値の指紋を持つ未対応の報告（自動の解消の判定）。<paramref name="personIds"/> が null ならすべて。</summary>
    Task<IReadOnlyList<CorrectionRecord>> ListOpenReportsAsync(ViewerContext viewer, IReadOnlyCollection<Guid>? personIds, CancellationToken cancellationToken);

    /// <summary>最後に Webhook に送った実行の開始日時（無ければ null。jin-ji 自身の閲覧者で読む）。</summary>
    Task<DateTimeOffset?> LastSentNoticeAsync(CancellationToken cancellationToken);

    /// <summary>通知の実行の記録を追記する（jin-ji 自身の閲覧者で書く）。</summary>
    Task RecordNoticeAsync(CorrectionNoticeRun run, CancellationToken cancellationToken);
}

/// <summary>Webhook の送信（Host が実装する。宛先の検査・時間の上限・リダイレクトに従わないことは実装が守る）。</summary>
public interface ICorrectionWebhookSender
{
    /// <summary>JSON の本文を POST する。結果は状態コード（接続できなければ null）と成否。</summary>
    Task<CorrectionWebhookResult> SendAsync(Uri url, string json, CancellationToken cancellationToken);
}

/// <summary>Webhook の送信の結果。</summary>
/// <param name="Error">失敗の理由（URL・本文は含めない）。</param>
public sealed record CorrectionWebhookResult(bool Succeeded, int? HttpStatus, string? Error);
