using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Organization;

/// <summary>
/// 組織スナップショットのキャッシュの設定（appsettings の <c>JinJi:OrgSnapshotCache</c>）。
/// </summary>
public sealed record OrgSnapshotCacheOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:OrgSnapshotCache";

    /// <summary>
    /// DB の世代を確かめる間隔（既定 5 秒）。他のプロセスが書き込んだ変更は、最長でこの時間だけ遅れて反映される。
    /// 0 なら参照のたびに確かめる。
    /// </summary>
    public TimeSpan VersionCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 世代が変わらなくても読み直すまでの最長時間（既定 10 分）。世代で検知できない変更（下記）への保険。
    /// </summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>値を検査する。</summary>
    public void Validate()
    {
        if (VersionCheckInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(VersionCheckInterval), VersionCheckInterval, "0 以上で指定してください。");
        }

        if (MaxAge <= TimeSpan.Zero || MaxAge < VersionCheckInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxAge), MaxAge, "正の値で、世代を確かめる間隔以上を指定してください。");
        }
    }
}

/// <summary>
/// 今日の業務日付の組織スナップショットをメモリ上にキャッシュする（アーキテクチャ概要 §1「組織ツリーと関係スコープはメモリ上にキャッシュ」）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>キャッシュするのは「組織・属性の基準日がどちらも今日の業務日付」の読み込みだけ。時点指定（過去・未来）は毎回読み込む。
/// 業務日付が変わると前日の分は使わない（内示の発令日の到来で見える行が変わるため）</item>
/// <item>閲覧者の区分（人事管理者かどうか）ごとに 1 つ持つ。RLS（0002）が見せる行は「hr_admin かどうか」だけで決まり、
/// 閲覧者個人には依存しないので、区分内の誰が読み込んでも同じ内容になる。RLS の規則を閲覧者個人に依存させる変更をしたら、ここのキーを見直すこと。
/// 0016 の先行提供（社内システムごとの日数）は RLS を閲覧者のクライアントに依存させるが、今日の読み込みは先行提供を外した閲覧者で行う
/// （<see cref="ViewerContext.WithoutDisclosureLead"/>）ので、区分は変わらない</item>
/// <item>同一プロセス内の人事データの書き込みは <see cref="NotifyChanged"/> で即座に捨てる</item>
/// <item>他のプロセスの書き込みは、<see cref="OrgSnapshotCacheOptions.VersionCheckInterval"/> ごとに DB の世代（<see cref="IOrgDataVersionSource"/>）を
/// 確かめて検知する。世代は読み込みの前に読むので、読み込みと書き込みが競合しても「古い世代 + 新しい内容」になり、次の確認で読み直すだけで済む</item>
/// <item>世代で検知できない変更（記録時間が既に見た最大値より前で、行数も変わらない更新など）に備え、<see cref="OrgSnapshotCacheOptions.MaxAge"/> で必ず読み直す</item>
/// <item>スナップショットは不変なので、複数のスレッドから同時に参照してよい。読み込みは区分ごとに 1 つに絞る</item>
/// </list>
/// </remarks>
public sealed class OrgSnapshotCache : IOrgSnapshotProvider, IOrgDataChangeNotifier, IDisposable
{
    private const int GeneralSlot = 0;
    private const int HrAdminSlot = 1;

    private readonly IOrgSnapshotProvider _inner;
    private readonly IOrgDataVersionSource _versions;
    private readonly TimeProvider _timeProvider;
    private readonly OrgSnapshotCacheOptions _options;
    private readonly SemaphoreSlim[] _locks = [new(1, 1), new(1, 1)];
    private readonly Entry?[] _entries = new Entry?[2];
    private long _epoch;

    public OrgSnapshotCache(
        IOrgSnapshotProvider inner,
        IOrgDataVersionSource versions,
        TimeProvider timeProvider,
        OrgSnapshotCacheOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(versions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options ?? new OrgSnapshotCacheOptions();
        _options.Validate();
        _inner = inner;
        _versions = versions;
        _timeProvider = timeProvider;
    }

    public async Task<OrgSnapshot> LoadAsync(ViewerContext viewer, DateOnly asOf, DateOnly attributesAsOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var today = BusinessDate.Today(_timeProvider);
        if (asOf != today || attributesAsOf != today)
        {
            return await _inner.LoadAsync(viewer, asOf, attributesAsOf, cancellationToken);
        }

        // 今日のスナップショットは先行提供の有無で変えない（先行提供が効くのは今日より後の時点だけ。RLS も従来の線で読む）
        viewer = viewer.WithoutDisclosureLead();
        var slot = viewer.IsHrAdmin ? HrAdminSlot : GeneralSlot;
        if (TryGetWithoutCheck(slot, today) is { } cached)
        {
            return cached;
        }

        await _locks[slot].WaitAsync(cancellationToken);
        try
        {
            // 待っている間に他の要求が読み込んだかもしれない
            if (TryGetWithoutCheck(slot, today) is { } loaded)
            {
                return loaded;
            }

            var epoch = Interlocked.Read(ref _epoch);
            var now = _timeProvider.GetUtcNow();
            var version = await _versions.GetVersionAsync(viewer, cancellationToken);

            var entry = Volatile.Read(ref _entries[slot]);
            if (entry is not null && IsUsable(entry, today, epoch, now) && entry.Version == version)
            {
                Volatile.Write(ref _entries[slot], entry with { CheckedAt = now });
                return entry.Snapshot;
            }

            var snapshot = await _inner.LoadAsync(viewer, today, today, cancellationToken);

            // 読み込み中に同一プロセスの書き込みがあったら、この結果はキャッシュしない（この要求には返してよい）
            if (Interlocked.Read(ref _epoch) == epoch)
            {
                Volatile.Write(ref _entries[slot], new Entry(today, snapshot, version, epoch, now, now));
            }

            return snapshot;
        }
        finally
        {
            _locks[slot].Release();
        }
    }

    /// <summary>同一プロセス内で人事データを書き込んだ（コミット後に呼ぶ）。キャッシュを捨てる。</summary>
    public void NotifyChanged()
    {
        Interlocked.Increment(ref _epoch);
        for (var i = 0; i < _entries.Length; i++)
        {
            Volatile.Write(ref _entries[i], null);
        }
    }

    public void Dispose()
    {
        foreach (var semaphore in _locks)
        {
            semaphore.Dispose();
        }
    }

    private OrgSnapshot? TryGetWithoutCheck(int slot, DateOnly today)
    {
        var entry = Volatile.Read(ref _entries[slot]);
        var now = _timeProvider.GetUtcNow();
        return entry is not null
               && IsUsable(entry, today, Interlocked.Read(ref _epoch), now)
               && now - entry.CheckedAt < _options.VersionCheckInterval
            ? entry.Snapshot
            : null;
    }

    private bool IsUsable(Entry entry, DateOnly today, long epoch, DateTimeOffset now) =>
        entry.BusinessDate == today && entry.Epoch == epoch && now - entry.LoadedAt < _options.MaxAge;

    private sealed record Entry(DateOnly BusinessDate, OrgSnapshot Snapshot, string Version, long Epoch, DateTimeOffset LoadedAt, DateTimeOffset CheckedAt);
}
