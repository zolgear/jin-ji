using System.Data;
using JinJi.Application.Changes;
using JinJi.Policy;
using Npgsql;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 変更フィードの記録（change_event。0007）の読み出し。閲覧者を RLS に渡し、読み取り専用・REPEATABLE READ の 1 トランザクションで読む。
/// </summary>
/// <remarks>
/// 位置はトランザクション ID（xid8）を bigint にしたもの。xid8 はエポックを含む 64 ビットで単調に増え、周回しない。
/// 「実行中のどのトランザクションよりも古い」は <c>txid &lt; pg_snapshot_xmin(pg_current_snapshot())</c> で判定する（同じスナップショットで読む）。
/// 見せてよい日の上限・取り消しの記録の規則は RLS（0007・0016）が引き、ここでも同じ条件を重ねる。
/// </remarks>
public sealed class NpgsqlChangeFeedStore : IChangeFeedStore
{
    private const string Columns = "txid::text::bigint, id, entity_type, entity_id, effective_date, withdrawn";

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlChangeFeedStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<long> GetWatermarkAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, IsolationLevel.RepeatableRead, readOnly: true);
        var watermark = await WatermarkAsync(tx, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return watermark;
    }

    public async Task<NewChanges> ReadNewAsync(
        ViewerContext viewer,
        (long Xid, long Id) after,
        DateOnly? effectiveThrough,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, IsolationLevel.RepeatableRead, readOnly: true);
        var watermark = await WatermarkAsync(tx, cancellationToken);
        await using var command = tx.CreateCommand(
            $"""
            SELECT {Columns}
            FROM change_event
            WHERE (txid, id) > ($1::text::xid8, $2)
              AND txid <= $3::text::xid8
              AND ($4::date IS NULL OR effective_date <= $4::date)
              AND ($6 OR NOT withdrawn)
            ORDER BY txid, id
            LIMIT $5
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = Math.Max(after.Xid, 0).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        command.Parameters.Add(new NpgsqlParameter { Value = after.Xid < 0 ? long.MinValue : after.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = watermark.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        command.Parameters.Add(new NpgsqlParameter { Value = effectiveThrough is { } through ? (object)through : DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Date });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        command.Parameters.Add(new NpgsqlParameter { Value = SeesWithdrawals(viewer) });
        var rows = await ReadAsync(command, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new NewChanges(rows, watermark);
    }

    public async Task<IReadOnlyList<StoredChange>> ReadDeferredAsync(
        ViewerContext viewer,
        (long Xid, long Id) upTo,
        (long Xid, long Id) after,
        DateOnly effectiveAfter,
        DateOnly effectiveThrough,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, IsolationLevel.RepeatableRead, readOnly: true);
        await using var command = tx.CreateCommand(
            $"""
            SELECT {Columns}
            FROM change_event
            WHERE (txid, id) <= ($1::text::xid8, $2)
              AND (txid, id) > ($3::text::xid8, $4)
              AND effective_date > $5 AND effective_date <= $6
              AND ($8 OR NOT withdrawn)
            ORDER BY txid, id
            LIMIT $7
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = Math.Max(upTo.Xid, 0).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        command.Parameters.Add(new NpgsqlParameter { Value = upTo.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = Math.Max(after.Xid, 0).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        command.Parameters.Add(new NpgsqlParameter { Value = after.Xid < 0 ? long.MinValue : after.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = effectiveAfter });
        command.Parameters.Add(new NpgsqlParameter { Value = effectiveThrough });
        command.Parameters.Add(new NpgsqlParameter { Value = limit });
        command.Parameters.Add(new NpgsqlParameter { Value = SeesWithdrawals(viewer) });
        var rows = await ReadAsync(command, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    /// <summary>
    /// 将来の予定の取り消しの記録（withdrawn）を見せる閲覧者か（人事管理者と、先行提供の日数を持つ社内システム。RLS の change_event_select と同じ規則）。
    /// 日数 0 の閲覧者はその予定を一度も見ていないので、取り消しの記録は予定があったことの手がかりにしかならない。
    /// </summary>
    private static bool SeesWithdrawals(ViewerContext viewer) => viewer.IsHrAdmin || viewer.DisclosureLeadDays > 0;

    /// <summary>実行中のどのトランザクションよりも前の、最大のトランザクション ID。</summary>
    private static async Task<long> WatermarkAsync(ViewerTransaction tx, CancellationToken cancellationToken)
    {
        await using var command = tx.CreateCommand("SELECT pg_snapshot_xmin(pg_current_snapshot())::text::bigint - 1");
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<List<StoredChange>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<StoredChange>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new StoredChange(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2) switch
                {
                    "person" => ChangeEntityType.Person,
                    "org_unit" => ChangeEntityType.OrgUnit,
                    "site" => ChangeEntityType.Site,
                    "group" => ChangeEntityType.Group,
                    var other => throw new InvalidOperationException($"DB に未知の変更の種別があります: {other}"),
                },
                reader.GetGuid(3),
                reader.GetFieldValue<DateOnly>(4),
                reader.GetBoolean(5)));
        }

        return rows;
    }
}
