using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Changes;

/// <summary>変わったものの種別（change_event.entity_type）。</summary>
public enum ChangeEntityType
{
    Person,
    OrgUnit,

    /// <summary>拠点（0017。拠点の変更は、その拠点を使う組織・人の変更としても記録される）。</summary>
    Site,

    /// <summary>グループ（チーム・プロジェクト。0022。全社公開のグループだけ記録する）。</summary>
    Group,
}

/// <summary>変更フィードの 1 件。本文は種別と ID だけ（受け手は API で取り直す。受け手ごとの項目上限が効くように。§9）。</summary>
public sealed record ChangeEvent(ChangeEntityType EntityType, Guid EntityId);

/// <summary>保存された変更の 1 行。</summary>
/// <param name="TransactionId">書き込んだトランザクションの ID（xid8）。</param>
/// <param name="Sequence">行 ID（同じトランザクションの中の順序）。</param>
/// <param name="EffectiveDate">人事管理者以外に変更として見せてよい日。</param>
/// <param name="Withdrawn">将来の予定の取り消し・訂正の記録か（0016。先行提供の日数を持つ閲覧者と人事管理者にだけ現れる）。</param>
public sealed record StoredChange(long TransactionId, long Sequence, ChangeEntityType EntityType, Guid EntityId, DateOnly EffectiveDate, bool Withdrawn = false);

/// <summary>新しい変更の読み出しの結果。</summary>
/// <param name="Watermark">この読み出しで「これより前のトランザクションはすべて終わっている」と言える最大のトランザクション ID。</param>
public sealed record NewChanges(IReadOnlyList<StoredChange> Rows, long Watermark);

/// <summary>変更の記録の読み出し（Infrastructure が実装する）。閲覧者を RLS に渡す。</summary>
public interface IChangeFeedStore
{
    /// <summary>今の位置（実行中のどのトランザクションよりも前）。</summary>
    Task<long> GetWatermarkAsync(ViewerContext viewer, CancellationToken cancellationToken);

    /// <summary>
    /// (txid, id) が <paramref name="after"/> より後で、実行中のどのトランザクションよりも古い行を、(txid, id) の順に最大 <paramref name="limit"/> 件。
    /// <paramref name="effectiveThrough"/> を指定したら、見せてよい日がその日以前の行だけ。取り消しの記録（withdrawn）は、
    /// 閲覧者が人事管理者か先行提供の日数を持つときだけ含める（RLS と同じ規則）。
    /// </summary>
    Task<NewChanges> ReadNewAsync(ViewerContext viewer, (long Xid, long Id) after, DateOnly? effectiveThrough, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// (txid, id) が <paramref name="upTo"/> 以前かつ <paramref name="after"/> より後で、見せてよい日が (<paramref name="effectiveAfter"/>, <paramref name="effectiveThrough"/>] の行を、
    /// (txid, id) の順に最大 <paramref name="limit"/> 件（既に読み進めた位置より前の、見せてよい日が来た行の拾い直し）。
    /// </summary>
    Task<IReadOnlyList<StoredChange>> ReadDeferredAsync(
        ViewerContext viewer,
        (long Xid, long Id) upTo,
        (long Xid, long Id) after,
        DateOnly effectiveAfter,
        DateOnly effectiveThrough,
        int limit,
        CancellationToken cancellationToken);
}

/// <summary>
/// 変更フィードの位置。カーソルとして利用者に渡す（改ざんの検知は API が署名で行う）。
/// </summary>
/// <param name="Xid">読み進めた位置（トランザクション ID）。</param>
/// <param name="Id">読み進めた位置（同じトランザクションの中の行 ID）。</param>
/// <param name="ActivatedThrough">見せてよい日が来た行をこの日まで配った（人事管理者以外）。</param>
/// <param name="Deferred">拾い直しの途中なら、その目標の日と進んだ位置。</param>
public sealed record ChangeFeedPosition(long Xid, long Id, DateOnly ActivatedThrough, DeferredProgress? Deferred = null);

/// <summary>拾い直しの途中の位置。</summary>
public sealed record DeferredProgress(DateOnly Target, long Xid, long Id);

/// <summary>変更フィードの記録の 1 回分（Webhook の収集が使う。見せてよい日・取り消しかを含む）。</summary>
/// <param name="HasMore">すぐに続きを読めるか。</param>
public sealed record ChangeFeedBatch(IReadOnlyList<StoredChange> Rows, ChangeFeedPosition Next, bool HasMore);

/// <summary>変更フィードの 1 ページ。</summary>
/// <param name="HasMore">すぐに続きを読めるか（false なら、しばらく待ってから <paramref name="Next"/> で読む）。</param>
public sealed record ChangeFeedPage(IReadOnlyList<ChangeEvent> Items, ChangeFeedPosition Next, bool HasMore);

/// <summary>
/// 変更フィード（アーキテクチャ概要 §9。<c>GET /changes?since=...</c>）。社内システムはポーリングで差分を取得する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>位置は (書き込んだトランザクションの ID, 行 ID)。実行中のどのトランザクションよりも古い行だけを返すので、後からコミットされた行を取りこぼさない（0007 の冒頭）</item>
/// <item>人事管理者以外には、行の「見せてよい日」が来るまで返さない（内示・将来の終了日・退職の予定の秘匿）。
/// 日が来たら、既に読み進めた位置より前の行でも拾い直して返す（<see cref="ChangeFeedPosition.ActivatedThrough"/>）</item>
/// <item>先行提供の社内システム（<see cref="ViewerContext.DisclosureLeadDays"/>）には、見せてよい日の「日数前」から返す（上限 = 業務日付 + 日数。RLS も同じ）。
/// 将来の予定の取り消し・訂正の記録（0016。予定の行の記録期間を閉じたこと）も届く。日数が減ったら、拾い直しの位置を新しい上限まで戻す
/// （その間の行は改めて届く。重複は害にならない）</item>
/// <item>同じ人・組織が何度も現れ得る（少なくとも 1 回は届ける）。受け手は ID で取り直すので、重複は害にならない。1 ページの中では重複を除く</item>
/// <item>位置を指定しなければ今の位置を返す（中身は空）。受け手は先に一覧で全件を取り、その後このフィードで差分を追う</item>
/// <item>参照はリクエスト単位で監査ログに記録する（返した人の ID の集合）</item>
/// </list>
/// </remarks>
public sealed class ChangeFeedService
{
    public const string ReadAction = "change.read";

    /// <summary>1 ページの最大件数。</summary>
    public const int MaxLimit = 1000;

    private readonly IChangeFeedStore _store;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;

    public ChangeFeedService(IChangeFeedStore store, AuditWriter audit, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    /// <summary>位置 <paramref name="since"/> の後の変更を読む。</summary>
    public async Task<ChangeFeedPage> ReadAsync(ViewerContext viewer, ChangeFeedPosition? since, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);
        var today = BusinessDate.Today(_timeProvider);

        ChangeFeedPage page;
        try
        {
            page = since is null
                ? new ChangeFeedPage([], new ChangeFeedPosition(await _store.GetWatermarkAsync(viewer, cancellationToken), long.MaxValue, today), false)
                : ToPage(await ReadFromAsync(viewer, since, limit, viewer.DisclosureHorizon(today), cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Failed, [], CancellationToken.None, "change_event");
            throw;
        }

        await _audit.RecordAsync(
            viewer,
            ReadAction,
            AuditOutcome.Success,
            page.Items.Where(e => e.EntityType == ChangeEntityType.Person).Select(e => e.EntityId),
            cancellationToken,
            "change_event");
        return page;
    }

    /// <summary>
    /// 今の位置（<see cref="ReadAsync"/> の since の省略と同じ。中身は読まない）。Webhook の登録のときに受け手の読み始めの位置にする。
    /// 位置はトランザクション ID だけで決まり、閲覧者によらない（jin-ji 自身の閲覧者で読む）。
    /// </summary>
    public async Task<ChangeFeedPosition> CurrentPositionAsync(Channel channel, CancellationToken cancellationToken) =>
        new(await _store.GetWatermarkAsync(ViewerContext.ForSystem(channel), cancellationToken), long.MaxValue, BusinessDate.Today(_timeProvider));

    /// <summary>
    /// 位置 <paramref name="since"/> の後の変更の記録を読む（監査ログに残さない。Webhook の収集が受け手のクライアントを閲覧者として使い、
    /// 送信を webhook.deliver として自分で記録する）。規則は <see cref="ReadAsync"/> と同じで、行の見せてよい日・取り消しかも返す。
    /// </summary>
    public Task<ChangeFeedBatch> ReadBatchAsync(ViewerContext viewer, ChangeFeedPosition since, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(since);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, MaxLimit);
        return ReadFromAsync(viewer, since, limit, viewer.DisclosureHorizon(BusinessDate.Today(_timeProvider)), cancellationToken);
    }

    private static ChangeFeedPage ToPage(ChangeFeedBatch batch) => new(
        [.. batch.Rows.Select(r => new ChangeEvent(r.EntityType, r.EntityId)).Distinct()],
        batch.Next,
        batch.HasMore);

    /// <param name="horizon">見せてよい日の上限（業務日付 + 先行提供の日数。人事管理者は上限なし）。</param>
    private async Task<ChangeFeedBatch> ReadFromAsync(ViewerContext viewer, ChangeFeedPosition since, int limit, DateOnly horizon, CancellationToken cancellationToken)
    {
        var rows = new List<StoredChange>();
        var position = since;
        var hasMore = false;
        var today = BusinessDate.Today(_timeProvider);

        // 0. 先行提供の日数が減った（上限が前に戻った）: 拾い直しの位置を上限に戻す。戻した間の行は、上限がまた進んだときに改めて届く
        if (!viewer.IsHrAdmin && position.ActivatedThrough > horizon)
        {
            position = position with { ActivatedThrough = horizon, Deferred = null };
        }

        if (!viewer.IsHrAdmin && position.Deferred is { } stale && stale.Target > horizon)
        {
            position = position with { Deferred = null };
        }

        // 1. 人事管理者以外: 見せてよい日が来た行のうち、既に読み進めた位置より前のものを拾い直す
        if (!viewer.IsHrAdmin && (position.Deferred is not null || position.ActivatedThrough < horizon))
        {
            var target = position.Deferred?.Target ?? horizon;
            var after = position.Deferred is { } progress ? (progress.Xid, progress.Id) : (long.MinValue, long.MinValue);
            var deferred = await _store.ReadDeferredAsync(
                viewer, (position.Xid, position.Id), after, position.ActivatedThrough, target, limit, cancellationToken);
            rows.AddRange(deferred);
            if (deferred.Count == limit)
            {
                position = position with { Deferred = new DeferredProgress(target, deferred[^1].TransactionId, deferred[^1].Sequence) };
                hasMore = true;
            }
            else
            {
                position = position with { ActivatedThrough = target, Deferred = null };
            }
        }

        // 2. 位置より後の新しい行（人事管理者以外は見せてよい日が来たものだけ。残りは 1. で後から拾う）
        var remaining = limit - rows.Count;
        if (remaining > 0)
        {
            var fresh = await _store.ReadNewAsync(
                viewer, (position.Xid, position.Id), viewer.IsHrAdmin ? null : position.ActivatedThrough, remaining, cancellationToken);
            rows.AddRange(fresh.Rows);
            if (fresh.Rows.Count == remaining)
            {
                position = position with { Xid = fresh.Rows[^1].TransactionId, Id = fresh.Rows[^1].Sequence };
                hasMore = true;
            }
            else if (fresh.Watermark > position.Xid || (fresh.Watermark == position.Xid && position.Id != long.MaxValue))
            {
                // 実行中のトランザクションより前はすべて読んだ。見せてよい日が来ていない行は 1. で拾い直す
                position = position with { Xid = fresh.Watermark, Id = long.MaxValue };
            }
        }

        if (viewer.IsHrAdmin)
        {
            position = position with { ActivatedThrough = today, Deferred = null };
        }

        return new ChangeFeedBatch(rows, position, hasMore);
    }
}
