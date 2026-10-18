namespace JinJi.Application.Presence;

/// <summary>
/// 当日の所在の問い合わせ（<see cref="IPresenceProvider.GetAsync"/>）。jin-ji は今日（業務日付）についてだけ問い合わせる（時点指定の過去・将来では呼ばない）。
/// </summary>
/// <param name="Date">業務日付（日本時間の今日）。</param>
/// <param name="FloorId">階で引くとき、その階（<paramref name="PersonIds"/> は空）。</param>
/// <param name="PersonIds">人で引くとき、その人たち（<paramref name="FloorId"/> は null）。</param>
public sealed record PresenceQuery(DateOnly Date, Guid? FloorId, IReadOnlyCollection<Guid> PersonIds);

/// <summary>
/// 外部システムが提供する、人のその日の所在（建物・階・席またはエリア）。時刻・入退館・チェックイン・測位の生の記録は持たない型にしている。
/// </summary>
/// <param name="PersonId">人の jin-ji ID（外部 ID との対応は提供する側が解く）。</param>
/// <param name="FloorId">階（jin-ji の floor.id。建物は階から決まる）。</param>
/// <param name="SeatId">席（jin-ji の seat.id）。エリアだけなら null。</param>
/// <param name="ZoneKey">エリア（jin-ji の配置図のゾーンの zone_key）。席だけなら null。</param>
public sealed record PresenceEntry(Guid PersonId, Guid FloorId, Guid? SeatId, Guid? ZoneKey);

/// <summary>
/// 当日の所在の提供（拡張点）。jin-ji は所在を保存せず、予約も受けない。入館・座席予約などの連携先の所在を、導入先が自前の実装で注入する
/// （リアルタイムの所在は連携先の範疇）。既定の実装は <see cref="NoPresenceProvider"/>（提供なし = 機能は無効で、API は所在を返さない）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>返した所在には、jin-ji が必ず認可（項目 presence の公開範囲・ポリシー・個人単位の例外・クライアントの項目の上限）を適用してから返す。
/// 提供する側は閲覧者を知らない（閲覧者に合わせて絞らなくてよい）</item>
/// <item>jin-ji は返った所在をキャッシュ・永続化せず、変更フィード・Webhook・同期・監査ログに記録しない。応答は no-store</item>
/// <item>jin-ji の配置図に無い階・席・エリアの所在は捨てる</item>
/// </list>
/// </remarks>
public interface IPresenceProvider
{
    /// <summary>所在を提供できるか（false なら jin-ji は問い合わせず、所在の機能は無効）。</summary>
    bool IsAvailable { get; }

    /// <summary>その日の所在（人ごとに高々 1 件。同じ人が複数あれば jin-ji は最初のものを使う）。</summary>
    Task<IReadOnlyList<PresenceEntry>> GetAsync(PresenceQuery query, CancellationToken cancellationToken);
}

/// <summary>提供なし（既定）。所在の機能は無効になる。</summary>
public sealed class NoPresenceProvider : IPresenceProvider
{
    public bool IsAvailable => false;

    public Task<IReadOnlyList<PresenceEntry>> GetAsync(PresenceQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PresenceEntry>>([]);
}

/// <summary>所在の提供が無い（API は 404。機能が無いものとして扱う）。</summary>
public sealed class PresenceUnavailableException : Exception
{
    public PresenceUnavailableException()
        : base("当日の所在の提供（IPresenceProvider）が登録されていません。")
    {
    }

    public PresenceUnavailableException(string message)
        : base(message)
    {
    }

    public PresenceUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
