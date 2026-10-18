using System.Text.Json;
using JinJi.Application.Corrections;
using JinJi.Application.Ingest;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 修正依頼（correction_request・correction_endorsement・correction_notice_run。0005・0015）の読み書き。閲覧者を RLS に渡す。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>可視範囲・起票者・閉じた人・状態の遷移・中身の不変の最終的な線は DB が引く（0015）</item>
/// <item>jin-ji 自身（system、ロールなし）の閲覧者で読むのは 3 つだけ: 同じ人・項目の未対応の報告の ID と起票者（賛同の記録に要る。
/// 起票者以外には見えない行なので、ID と起票者のほかは返さない）、賛同の件数（件数は人事データの値ではない）、通知の実行の記録</item>
/// </list>
/// </remarks>
public sealed class NpgsqlCorrectionStore : ICorrectionStore
{
    private const string OpenReportKey = "correction_request_open_report_key";
    private const string EndorsementOnce = "correction_endorsement_once";

    private const string SelectColumns =
        """
        SELECT c.id, c.origin, c.issue, c.status, c.message, c.import_batch_id, b.file_name, c.source_row_number, c.field_key,
               c.person_id, c.person_field, c.explanation, c.suggested_value, c.value_fingerprint, c.reported_by_person_id, c.reported_via_client_id,
               c.created_at, c.resolved_at, c.closed_kind, c.closed_by_person_id, c.closed_note,
               EXISTS (SELECT 1 FROM correction_endorsement e
                       WHERE e.correction_request_id = c.id AND e.endorsed_by_person_id = jinji_viewer_person_id()) AS endorsed_by_viewer
        FROM correction_request c LEFT JOIN import_batch b ON b.id = c.import_batch_id
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlCorrectionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<bool> TryInsertReportAsync(ViewerContext viewer, CorrectionRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(record);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using (var command = tx.CreateCommand(
            """
            INSERT INTO correction_request (id, origin, issue, message, person_id, person_field, explanation, suggested_value, value_fingerprint,
                                            reported_by_person_id, reported_via_client_id, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            """))
        {
            command.Parameters.Add(P(record.Id));
            command.Parameters.Add(P(Code(record.Origin)));
            command.Parameters.Add(P(Code(record.Issue)));
            command.Parameters.Add(P(record.Message));
            command.Parameters.Add(P(record.PersonId, NpgsqlDbType.Uuid));
            command.Parameters.Add(P(record.PersonField, NpgsqlDbType.Text));
            command.Parameters.Add(P(record.Reason, NpgsqlDbType.Text));
            command.Parameters.Add(P(record.SuggestedValue, NpgsqlDbType.Text));
            command.Parameters.Add(P(record.ValueFingerprint, NpgsqlDbType.Text));
            command.Parameters.Add(P(record.ReportedByPersonId, NpgsqlDbType.Uuid));
            command.Parameters.Add(P(record.ReportedViaClientId, NpgsqlDbType.Text));
            command.Parameters.Add(P(record.CreatedAt));
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == OpenReportKey)
            {
                return false;
            }
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<OpenReportKey?> FindOpenReportAsync(Guid personId, string field, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(field);
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand(
            """
            SELECT id, reported_by_person_id FROM correction_request
            WHERE person_id = $1 AND person_field = $2 AND status = 'open' AND origin IN ('user', 'agent')
            """);
        command.Parameters.Add(P(personId));
        command.Parameters.Add(P(field));
        OpenReportKey? found = null;
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await r.ReadAsync(cancellationToken))
            {
                found = new OpenReportKey(r.GetGuid(0), r.GetGuid(1));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return found;
    }

    public async Task<bool> TryEndorseAsync(ViewerContext viewer, Guid requestId, Guid endorsementId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using (var command = tx.CreateCommand(
            """
            INSERT INTO correction_endorsement (id, correction_request_id, endorsed_by_person_id, endorsed_via_actor, endorsed_via_client_id)
            VALUES ($1, $2, jinji_viewer_person_id(), jinji_viewer_kind(), $3)
            """))
        {
            command.Parameters.Add(P(endorsementId));
            command.Parameters.Add(P(requestId));
            command.Parameters.Add(P(viewer.Actor == ActorType.Agent ? viewer.ClientId : null, NpgsqlDbType.Text));
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName == EndorsementOnce)
            {
                return false;
            }
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> CountRecentReportsAsync(ViewerContext viewer, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            """
            SELECT (SELECT count(*) FROM correction_request WHERE reported_by_person_id = jinji_viewer_person_id() AND created_at > $1)
                 + (SELECT count(*) FROM correction_endorsement WHERE endorsed_by_person_id = jinji_viewer_person_id() AND created_at > $1)
            """);
        command.Parameters.Add(P(since));
        var count = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        await tx.CommitAsync(cancellationToken);
        return (int)count;
    }

    public async Task<CorrectionRecord?> FindAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken) =>
        (await QueryAsync(viewer, "WHERE c.id = $1", [P(id)], cancellationToken)).SingleOrDefault();

    public Task<IReadOnlyList<CorrectionRecord>> ListAsync(ViewerContext viewer, CorrectionQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return QueryAsync(
            viewer,
            """
            WHERE ($1::text[] IS NULL OR c.status = ANY ($1))
              AND ($2::uuid IS NULL OR c.person_id = $2)
              AND ($3::uuid IS NULL OR c.id > $3)
            ORDER BY c.id
            LIMIT $4
            """,
            [
                P(Statuses(query.Status), NpgsqlDbType.Array | NpgsqlDbType.Text),
                P(query.PersonId, NpgsqlDbType.Uuid),
                P(query.After, NpgsqlDbType.Uuid),
                P(query.Limit + 1),
            ],
            cancellationToken);
    }

    public async Task<bool> CloseAsync(
        ViewerContext viewer,
        Guid id,
        CorrectionStatus status,
        CorrectionClosedKind kind,
        string? note,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE correction_request
            SET status = $2, resolved_at = now(), closed_kind = $3,
                closed_by_person_id = CASE WHEN $3 = 'person' THEN jinji_viewer_person_id() END,
                closed_note = $4
            WHERE id = $1 AND status = 'open'
            """);
        command.Parameters.Add(P(id));
        command.Parameters.Add(P(Code(status)));
        command.Parameters.Add(P(Code(kind)));
        command.Parameters.Add(P(note, NpgsqlDbType.Text));
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return updated == 1;
    }

    public async Task<CorrectionCounts> CountOpenAsync(ViewerContext viewer, DateTimeOffset newSince, DateTimeOffset staleBefore, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            """
            SELECT count(*), count(*) FILTER (WHERE created_at > $1), count(*) FILTER (WHERE created_at <= $2), min(created_at)
            FROM correction_request WHERE status = 'open'
            """);
        command.Parameters.Add(P(newSince));
        command.Parameters.Add(P(staleBefore));
        CorrectionCounts counts;
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            await r.ReadAsync(cancellationToken);
            counts = new CorrectionCounts(
                (int)r.GetInt64(0),
                (int)r.GetInt64(1),
                (int)r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetFieldValue<DateTimeOffset>(3));
        }

        await tx.CommitAsync(cancellationToken);
        return counts;
    }

    public Task<IReadOnlyList<CorrectionRecord>> ListOpenReportsAsync(ViewerContext viewer, IReadOnlyCollection<Guid>? personIds, CancellationToken cancellationToken) =>
        QueryAsync(
            viewer,
            """
            WHERE c.status = 'open' AND c.value_fingerprint IS NOT NULL AND ($1::uuid[] IS NULL OR c.person_id = ANY ($1))
            ORDER BY c.id
            """,
            [P(personIds?.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Uuid)],
            cancellationToken);

    public async Task<DateTimeOffset?> LastSentNoticeAsync(CancellationToken cancellationToken)
    {
        await using var tx = await SystemAsync(cancellationToken);
        await using var command = tx.CreateCommand("SELECT max(started_at) FROM correction_notice_run WHERE outcome = 'sent'");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return value is DateTime utc ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)) : value as DateTimeOffset?;
    }

    public async Task RecordNoticeAsync(CorrectionNoticeRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var tx = await SystemAsync(cancellationToken);
        await using (var command = tx.CreateCommand(
            """
            INSERT INTO correction_notice_run (id, trigger, outcome, started_at, finished_at, open_count, new_count, stale_count, auto_resolved_count,
                                               destination_host, http_status)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """))
        {
            command.Parameters.Add(P(run.Id));
            command.Parameters.Add(P(run.Trigger == Channel.Cli ? "cli" : "worker"));
            command.Parameters.Add(P(CorrectionNoticeService.OutcomeCode(run.Outcome)));
            command.Parameters.Add(P(run.StartedAt));
            command.Parameters.Add(P(run.FinishedAt));
            command.Parameters.Add(P(run.Counts.Open));
            command.Parameters.Add(P(run.Counts.New));
            command.Parameters.Add(P(run.Counts.Stale));
            command.Parameters.Add(P(run.AutoResolved));
            command.Parameters.Add(P(run.DestinationHost, NpgsqlDbType.Text));
            command.Parameters.Add(P(run.HttpStatus, NpgsqlDbType.Integer));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<CorrectionRecord>> QueryAsync(
        ViewerContext viewer,
        string condition,
        NpgsqlParameter[] parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var rows = new List<CorrectionRecord>();
        await using (var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true))
        {
            await using var command = tx.CreateCommand(SelectColumns + "\n" + condition);
            command.Parameters.AddRange(parameters);
            await using (var r = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await r.ReadAsync(cancellationToken))
                {
                    rows.Add(new CorrectionRecord
                    {
                        Id = r.GetGuid(0),
                        Origin = Parse<CorrectionOrigin>(r.GetString(1)),
                        Issue = Parse<CorrectionIssue>(r.GetString(2)),
                        Status = Parse<CorrectionStatus>(r.GetString(3)),
                        Message = r.GetString(4),
                        BatchId = r.IsDBNull(5) ? null : r.GetGuid(5),
                        FileName = r.IsDBNull(6) ? null : r.GetString(6),
                        SourceRowNumber = r.IsDBNull(7) ? null : r.GetInt32(7),
                        SourceField = r.IsDBNull(8) ? null : r.GetString(8),
                        PersonId = r.IsDBNull(9) ? null : r.GetGuid(9),
                        PersonField = r.IsDBNull(10) ? null : r.GetString(10),
                        Reason = r.IsDBNull(11) ? null : r.GetString(11),
                        SuggestedValue = r.IsDBNull(12) ? null : r.GetString(12),
                        ValueFingerprint = r.IsDBNull(13) ? null : r.GetString(13),
                        ReportedByPersonId = r.IsDBNull(14) ? null : r.GetGuid(14),
                        ReportedViaClientId = r.IsDBNull(15) ? null : r.GetString(15),
                        CreatedAt = r.GetFieldValue<DateTimeOffset>(16),
                        ClosedAt = r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17),
                        ClosedKind = r.IsDBNull(18) ? null : Parse<CorrectionClosedKind>(r.GetString(18)),
                        ClosedByPersonId = r.IsDBNull(19) ? null : r.GetGuid(19),
                        ClosedNote = r.IsDBNull(20) ? null : r.GetString(20),
                        EndorsedByViewer = r.GetBoolean(21),
                    });
                }
            }

            await tx.CommitAsync(cancellationToken);
        }

        if (rows.Count == 0)
        {
            return rows;
        }

        // 賛同の件数は jin-ji 自身の閲覧者で数える（賛同の行は修正担当者・人事管理者と賛同した本人にしか見えないが、件数は人事データの値ではない）
        var counts = new Dictionary<Guid, int>();
        await using (var tx = await SystemAsync(cancellationToken))
        {
            await using var command = tx.CreateCommand(
                "SELECT correction_request_id, count(*) FROM correction_endorsement WHERE correction_request_id = ANY ($1) GROUP BY correction_request_id");
            command.Parameters.Add(P(rows.Select(r => r.Id).ToArray()));
            await using (var r = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await r.ReadAsync(cancellationToken))
                {
                    counts[r.GetGuid(0)] = (int)r.GetInt64(1);
                }
            }

            await tx.CommitAsync(cancellationToken);
        }

        return [.. rows.Select(r => r with { Endorsements = counts.GetValueOrDefault(r.Id) })];
    }

    private Task<ViewerTransaction> SystemAsync(CancellationToken cancellationToken) =>
        ViewerTransaction.BeginRawAsync(_dataSource, "system", string.Empty, null, cancellationToken);

    private static string[]? Statuses(CorrectionStatusFilter filter) => filter switch
    {
        CorrectionStatusFilter.Open => ["open"],
        CorrectionStatusFilter.Closed => ["resolved", "dismissed", "auto_resolved", "superseded"],
        _ => null,
    };

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };

    private static string Code<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static T Parse<T>(string code)
        where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            if (Code(value) == code)
            {
                return value;
            }
        }

        throw new InvalidOperationException($"DB に未知の {typeof(T).Name} があります: {code}");
    }
}
