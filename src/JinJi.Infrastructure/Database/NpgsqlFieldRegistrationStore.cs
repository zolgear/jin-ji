using System.Text.Json;
using JinJi.Application.FieldManagement;
using JinJi.Application.Ingest;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>現場・IT 部門の直接登録（field_registration と、その変更提案）の読み書き（0021）。閲覧者を RLS に渡す。</summary>
/// <remarks>
/// <list type="bullet">
/// <item>反映は取込の承認と同じ <see cref="NpgsqlIngestStore.ApplyInTransactionAsync"/> を、登録の提案ごとに 1 トランザクションの中で呼ぶ</item>
/// <item>反映の間は GUC jinji.field_registration に登録を指し、DB のトリガーが書いた行を field_registration_row に記録する（取り消しと秘匿に使う）。
/// 同意で反映するときは、同意者を記録してから反映する（DB は同意者と登録者の範囲の和で書き込みを判定する）</item>
/// <item>範囲の外の書き込み（RLS の拒否・権限を生む変更・人の版の列）は <see cref="FieldManagementException"/>（Forbidden）、
/// 旧値の不一致・他の変更との衝突は <see cref="ProposalConflictException"/></item>
/// </list>
/// </remarks>
public sealed class NpgsqlFieldRegistrationStore : IFieldRegistrationStore
{
    private const string RegistrationSetting = "jinji.field_registration";

    private const string Columns =
        """
        id, status, effective_date, note, registered_by_person_id, registered_via, registered_via_client_id, created_at, expires_at,
        org_unit_ids, person_ids, consent_org_unit_ids, consented_by_person_id, consented_at, applied_by_person_id, applied_at,
        withdrawn_by_person_id, withdrawn_at, closed_by_person_id, closed_at
        """;

    /// <summary>取り消しで戻す公開側の表（記録期間を持つもの）。</summary>
    private static readonly HashSet<string> BitemporalTables = new(StringComparer.Ordinal)
    {
        "org_unit_version", "person_version", "assignment", "personnel_action",
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;

    public NpgsqlFieldRegistrationStore(NpgsqlDataSource dataSource, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dataSource = dataSource;
        _timeProvider = timeProvider;
    }

    public async Task SaveAsync(ViewerContext viewer, FieldRegistration registration, FieldRegistrationStatus next, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(registration);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using (var insert = tx.CreateCommand(
            """
            INSERT INTO field_registration (id, effective_date, note, registered_by_person_id, registered_via, registered_via_client_id,
                                            created_at, expires_at, org_unit_ids, person_ids, consent_org_unit_ids)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """))
        {
            insert.Parameters.Add(P(registration.Id));
            insert.Parameters.Add(P(registration.EffectiveDate));
            insert.Parameters.Add(P(registration.Note, NpgsqlDbType.Text));
            insert.Parameters.Add(P(registration.RegisteredBy));
            insert.Parameters.Add(P(registration.RegisteredVia == ActorType.Agent ? "agent" : "user"));
            insert.Parameters.Add(P(registration.RegisteredViaClientId, NpgsqlDbType.Text));
            insert.Parameters.Add(P(registration.CreatedAt));
            insert.Parameters.Add(P(registration.ExpiresAt));
            insert.Parameters.Add(P(registration.OrgUnitIds.ToArray()));
            insert.Parameters.Add(P(registration.PersonIds.ToArray()));
            insert.Parameters.Add(P(registration.ConsentOrgUnitIds.ToArray()));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await AddViewerAsync(tx, registration.Id, cancellationToken);
        foreach (var proposal in registration.Proposals)
        {
            await using var command = tx.CreateCommand(
                """
                INSERT INTO change_proposal (id, field_registration_id, target_kind, operation, target_id, effective_date, payload, diff, basis,
                                             confidence, status, proposed_by_person_id, proposed_via_client_id, proposed_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, 'proposed', $11, $12, $13)
                """);
            command.Parameters.Add(P(proposal.Id));
            command.Parameters.Add(P(registration.Id));
            command.Parameters.Add(P(NpgsqlIngestStore.Code(proposal.TargetKind)));
            command.Parameters.Add(P(NpgsqlIngestStore.Code(proposal.Operation)));
            command.Parameters.Add(P(proposal.TargetId, NpgsqlDbType.Uuid));
            command.Parameters.Add(P(proposal.EffectiveDate, NpgsqlDbType.Date));
            command.Parameters.Add(Jsonb(proposal.Payload));
            command.Parameters.Add(Jsonb(proposal.Diff));
            command.Parameters.Add(Jsonb(proposal.Basis));
            command.Parameters.Add(P(proposal.Confidence));
            command.Parameters.Add(P(proposal.ProposedByPersonId));
            command.Parameters.Add(P(proposal.ProposedViaClientId, NpgsqlDbType.Text));
            command.Parameters.Add(P(registration.CreatedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (next != FieldRegistrationStatus.Draft
            && !await AdvanceInTransactionAsync(tx, viewer, registration, FieldRegistrationStatus.Draft, next, consent: false, today, cancellationToken))
        {
            throw new InvalidOperationException($"保存した登録 {registration.Id} を進められませんでした。");
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<bool> AdvanceAsync(
        ViewerContext viewer,
        FieldRegistration registration,
        FieldRegistrationStatus next,
        bool consent,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(registration);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        if (!await AdvanceInTransactionAsync(tx, viewer, registration, registration.Status, next, consent, today, cancellationToken))
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>登録を <paramref name="from"/> から <paramref name="next"/>（同意待ちか反映済み）に進める。反映なら提案をすべて反映する。</summary>
    private async Task<bool> AdvanceInTransactionAsync(
        ViewerTransaction tx,
        ViewerContext viewer,
        FieldRegistration registration,
        FieldRegistrationStatus from,
        FieldRegistrationStatus next,
        bool consent,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        await using (var lockCommand = tx.CreateCommand("SELECT status FROM field_registration WHERE id = $1 FOR UPDATE"))
        {
            lockCommand.Parameters.Add(P(registration.Id));
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not string status || status != FieldRegistrationService.Code(from))
            {
                return false;
            }
        }

        var now = _timeProvider.GetUtcNow();
        if (next == FieldRegistrationStatus.PendingConsent)
        {
            return await ExecuteAsync(tx, "UPDATE field_registration SET status = 'pending_consent' WHERE id = $1", cancellationToken, P(registration.Id)) == 1;
        }

        if (next != FieldRegistrationStatus.Applied)
        {
            throw new ArgumentOutOfRangeException(nameof(next));
        }

        if (consent)
        {
            await ExecuteAsync(
                tx,
                "UPDATE field_registration SET consented_by_person_id = jinji_viewer_person_id(), consented_at = $2 WHERE id = $1",
                cancellationToken,
                P(registration.Id),
                P(now));
            await AddViewerAsync(tx, registration.Id, cancellationToken);
        }

        await ExecuteAsync(tx, $"SELECT set_config('{RegistrationSetting}', $1, true)", cancellationToken, P(registration.Id.ToString("D")));
        var proposals = await ReadProposalsAsync(tx, registration.Id, cancellationToken);
        try
        {
            foreach (var proposal in proposals)
            {
                if (!await NpgsqlIngestStore.ApplyInTransactionAsync(tx, viewer, proposal, today, now, cancellationToken))
                {
                    throw new ProposalConflictException($"登録の提案 {proposal.Id} が起案中ではありません。");
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            // RLS（範囲・管理元）・権限を生む変更・人の版の列の検査で DB が拒否した
            throw new FieldManagementException(FieldManagementError.Forbidden, "範囲の外の変更を含むため、登録できません: " + ex.MessageText, ex);
        }

        await ExecuteAsync(tx, $"SELECT set_config('{RegistrationSetting}', '', true)", cancellationToken);
        return await ExecuteAsync(
            tx,
            "UPDATE field_registration SET status = 'applied', applied_by_person_id = jinji_viewer_person_id(), applied_at = $2 WHERE id = $1",
            cancellationToken,
            P(registration.Id),
            P(now)) == 1;
    }

    public async Task<bool> WithdrawAsync(ViewerContext viewer, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using (var lockCommand = tx.CreateCommand("SELECT status FROM field_registration WHERE id = $1 FOR UPDATE"))
        {
            lockCommand.Parameters.Add(P(id));
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not "applied")
            {
                await tx.Transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        // 取り消す人も関係者にする（未来日の行が見え、記録期間を閉じられるように）
        await AddViewerAsync(tx, id, cancellationToken);
        var rows = new List<(string Table, Guid Row, string Operation)>();
        await using (var select = tx.CreateCommand("SELECT table_name, row_id, operation FROM field_registration_row WHERE registration_id = $1 ORDER BY row_id"))
        {
            select.Parameters.Add(P(id));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetString(2)));
            }
        }

        // 同じ登録で追加して閉じた行（途中の版）は、差し引きで何もしない
        var inserted = rows.Where(r => r.Operation == "insert").Select(r => (r.Table, r.Row)).ToHashSet();
        var closed = rows.Where(r => r.Operation == "close").Select(r => (r.Table, r.Row)).ToHashSet();
        var toClose = inserted.Except(closed).Where(r => BitemporalTables.Contains(r.Table)).ToList();
        var toRestore = closed.Except(inserted).Where(r => BitemporalTables.Contains(r.Table)).ToList();
        try
        {
            foreach (var (table, row) in toClose)
            {
                var updated = await ExecuteAsync(
                    tx,
                    $"UPDATE {table} SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE id = $1 AND upper_inf(recorded_period)",
                    cancellationToken,
                    P(row));
                if (updated != 1)
                {
                    throw new ProposalConflictException("登録の後に同じ人・組織の情報が変わっているので取り消せません。新しく登録し直してください。");
                }
            }

            foreach (var (table, row) in toRestore)
            {
                await ExecuteAsync(
                    tx,
                    $"""
                    INSERT INTO {table}
                    SELECT (jsonb_populate_record(NULL::{table}, to_jsonb(o) || jsonb_build_object('id', $2::uuid, 'recorded_period', tstzrange(now(), NULL)))).*
                    FROM {table} o WHERE o.id = $1
                    """,
                    cancellationToken,
                    P(row),
                    P(Guid.CreateVersion7()));
            }
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("23", StringComparison.Ordinal))
        {
            throw new ProposalConflictException($"取り消せません（{ex.ConstraintName ?? ex.SqlState}）: {ex.MessageText}", ex);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new FieldManagementException(FieldManagementError.Forbidden, "範囲の外の行を含むため、取り消せません: " + ex.MessageText, ex);
        }

        var withdrawn = await ExecuteAsync(
            tx,
            "UPDATE field_registration SET status = 'withdrawn', withdrawn_by_person_id = jinji_viewer_person_id(), withdrawn_at = $2 WHERE id = $1",
            cancellationToken,
            P(id),
            P(now)) == 1;
        await tx.CommitAsync(cancellationToken);
        return withdrawn;
    }

    public async Task<bool> CloseAsync(ViewerContext viewer, Guid id, bool expired, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        var updated = await ExecuteAsync(
            tx,
            expired
                ? "UPDATE field_registration SET status = 'expired', closed_at = $2 WHERE id = $1 AND status IN ('draft', 'pending_consent') AND expires_at <= $2"
                : "UPDATE field_registration SET status = 'discarded', closed_by_person_id = jinji_viewer_person_id(), closed_at = $2 WHERE id = $1 AND status IN ('draft', 'pending_consent')",
            cancellationToken,
            P(id),
            P(now));
        if (updated != 1)
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return false;
        }

        // 提案は却下にする（期限切れは人の決定ではないので、提案は起案中のまま残す）
        if (!expired)
        {
            await ExecuteAsync(
                tx,
                "UPDATE change_proposal SET status = 'rejected', decided_by_person_id = jinji_viewer_person_id(), decided_at = $2 WHERE field_registration_id = $1 AND status = 'proposed'",
                cancellationToken,
                P(id),
                P(now));
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<FieldRegistration?> FindAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        FieldRegistration? registration;
        await using (var command = tx.CreateCommand($"SELECT {Columns} FROM field_registration WHERE id = $1"))
        {
            command.Parameters.Add(P(id));
            registration = (await ReadAllAsync(command, cancellationToken)).SingleOrDefault();
        }

        if (registration is not null)
        {
            registration = registration with { Proposals = await ReadProposalsAsync(tx, id, cancellationToken) };
        }

        await tx.CommitAsync(cancellationToken);
        return registration;
    }

    public async Task<IReadOnlyList<FieldRegistration>> ListAsync(ViewerContext viewer, FieldRegistrationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(query);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            $"""
            SELECT {Columns} FROM field_registration
            WHERE (NOT $1 OR registered_by_person_id = jinji_viewer_person_id())
              AND (cardinality($2::text[]) = 0 OR status = ANY ($2::text[]))
            ORDER BY created_at DESC, id DESC
            LIMIT $3
            """);
        command.Parameters.Add(P(query.Mine));
        command.Parameters.Add(P(query.Statuses.Select(FieldRegistrationService.Code).ToArray()));
        command.Parameters.Add(P(Math.Clamp(query.Limit, 1, 500)));
        var registrations = await ReadAllAsync(command, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return registrations;
    }

    public async Task<FieldWrittenTargets> FieldWrittenAsync(ViewerContext viewer, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        var orgs = await TargetsAsync(tx, "org_unit_version", "org_unit_id", string.Empty, date, cancellationToken);
        var people = await TargetsAsync(tx, "person_version", "person_id", string.Empty, date, cancellationToken);
        var assignments = await TargetsAsync(tx, "assignment", "person_id", "AND t.assignment_type = 'primary'", date, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new FieldWrittenTargets(orgs, people, assignments);
    }

    private static async Task<IReadOnlySet<Guid>> TargetsAsync(ViewerTransaction tx, string table, string key, string filter, DateOnly date, CancellationToken cancellationToken)
    {
        await using var command = tx.CreateCommand(
            $"""
            SELECT DISTINCT t.{key} FROM {table} t
            JOIN field_registration_row r ON r.row_id = t.id AND r.operation = 'insert'
            JOIN field_registration g ON g.id = r.registration_id AND g.status = 'applied'
            WHERE upper_inf(t.recorded_period) AND t.valid_period @> $1::date {filter}
            """);
        command.Parameters.Add(P(date));
        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task AddViewerAsync(ViewerTransaction tx, Guid registrationId, CancellationToken cancellationToken) =>
        await ExecuteAsync(
            tx,
            "INSERT INTO field_registration_viewer (registration_id, person_id) VALUES ($1, jinji_viewer_person_id()) ON CONFLICT DO NOTHING",
            cancellationToken,
            P(registrationId));

    private static async Task<IReadOnlyList<ChangeProposalRecord>> ReadProposalsAsync(ViewerTransaction tx, Guid registrationId, CancellationToken cancellationToken)
    {
        await using var command = tx.CreateCommand(
            """
            SELECT id, target_kind, operation, target_id, effective_date, payload::text, diff::text, basis::text, confidence, status,
                   proposed_by_person_id, proposed_via_client_id, decided_by_person_id
            FROM change_proposal WHERE field_registration_id = $1
            """);
        command.Parameters.Add(P(registrationId));
        var rows = new List<ChangeProposalRecord>();
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                rows.Add(new ChangeProposalRecord
                {
                    Id = r.GetGuid(0),
                    BatchId = Guid.Empty,
                    TargetKind = NpgsqlIngestStore.Parse<ProposalTargetKind>(r.GetString(1)),
                    Operation = NpgsqlIngestStore.Parse<ProposalOperation>(r.GetString(2)),
                    TargetId = r.IsDBNull(3) ? null : r.GetGuid(3),
                    EffectiveDate = r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4),
                    Payload = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(5), NpgsqlIngestStore.Json) ?? [],
                    Diff = JsonSerializer.Deserialize<Dictionary<string, FieldChange>>(r.GetString(6), NpgsqlIngestStore.Json) ?? [],
                    Basis = JsonSerializer.Deserialize<ProposalBasis>(r.GetString(7), NpgsqlIngestStore.Json) ?? new ProposalBasis(),
                    Confidence = r.GetDecimal(8),
                    Status = NpgsqlIngestStore.Parse<ProposalStatus>(r.GetString(9)),
                    ProposedByPersonId = r.GetGuid(10),
                    ProposedViaClientId = r.IsDBNull(11) ? null : r.GetString(11),
                    DecidedByPersonId = r.IsDBNull(12) ? null : r.GetGuid(12),
                });
            }
        }

        // 反映の順（組織の新設 → 組織の変更 → 所属 → 人 → 組織の廃止）。提案の ID は UUIDv7 で、計画の順に採番している
        return [.. rows.OrderBy(p => Order(p)).ThenBy(p => p.Id)];
    }

    private static int Order(ChangeProposalRecord proposal) => (proposal.TargetKind, proposal.Operation) switch
    {
        (ProposalTargetKind.OrgUnit, _) => 0,
        (ProposalTargetKind.OrgUnitVersion, ProposalOperation.Abolish) => 4,
        (ProposalTargetKind.OrgUnitVersion, _) => 1,
        (ProposalTargetKind.Assignment, _) => 2,
        _ => 3,
    };

    private static async Task<List<FieldRegistration>> ReadAllAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<FieldRegistration>();
        await using var r = await command.ExecuteReaderAsync(cancellationToken);
        while (await r.ReadAsync(cancellationToken))
        {
            rows.Add(new FieldRegistration
            {
                Id = r.GetGuid(0),
                Status = FieldRegistrationService.TryParseStatus(r.GetString(1), out var status)
                    ? status
                    : throw new InvalidOperationException($"DB に未知の登録の状態があります: {r.GetString(1)}"),
                EffectiveDate = r.GetFieldValue<DateOnly>(2),
                Note = r.IsDBNull(3) ? null : r.GetString(3),
                RegisteredBy = r.GetGuid(4),
                RegisteredVia = r.GetString(5) == "agent" ? ActorType.Agent : ActorType.User,
                RegisteredViaClientId = r.IsDBNull(6) ? null : r.GetString(6),
                CreatedAt = r.GetFieldValue<DateTimeOffset>(7),
                ExpiresAt = r.GetFieldValue<DateTimeOffset>(8),
                OrgUnitIds = r.GetFieldValue<Guid[]>(9),
                PersonIds = r.GetFieldValue<Guid[]>(10),
                ConsentOrgUnitIds = r.GetFieldValue<Guid[]>(11),
                ConsentedBy = r.IsDBNull(12) ? null : r.GetGuid(12),
                ConsentedAt = r.IsDBNull(13) ? null : r.GetFieldValue<DateTimeOffset>(13),
                AppliedBy = r.IsDBNull(14) ? null : r.GetGuid(14),
                AppliedAt = r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
                WithdrawnBy = r.IsDBNull(16) ? null : r.GetGuid(16),
                WithdrawnAt = r.IsDBNull(17) ? null : r.GetFieldValue<DateTimeOffset>(17),
                ClosedBy = r.IsDBNull(18) ? null : r.GetGuid(18),
                ClosedAt = r.IsDBNull(19) ? null : r.GetFieldValue<DateTimeOffset>(19),
            });
        }

        return rows;
    }

    private static async Task<int> ExecuteAsync(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };

    private static NpgsqlParameter Jsonb(object value) =>
        new() { Value = JsonSerializer.Serialize(value, NpgsqlIngestStore.Json), NpgsqlDbType = NpgsqlDbType.Jsonb };
}
