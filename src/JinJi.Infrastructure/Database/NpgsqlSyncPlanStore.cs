using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JinJi.Application.Sync;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 同期計画の作業領域（sync_plan / sync_plan_item / sync_run / sync_item_result）の読み書き（<see cref="ISyncPlanStore"/>）。
/// </summary>
/// <remarks>
/// 権限の最終的な線は DB が引く（0009）: 参照・追加は hr_admin だけ（RLS）、起案者 ≠ 承認者（CHECK 制約）、決定者は閲覧者本人（RLS）、
/// 状態の遷移と中身の不変（トリガー）、changes / observed のキーの許可リスト（CHECK 制約）。
/// </remarks>
public sealed class NpgsqlSyncPlanStore : ISyncPlanStore
{
    private const int BatchSize = 500;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;

    public NpgsqlSyncPlanStore(NpgsqlDataSource dataSource, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dataSource = dataSource;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<ExternalIdentityLink>> LoadIdentitiesAsync(ViewerContext viewer, string sourceSystem, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand("SELECT person_id, external_id FROM external_identity WHERE source_system = $1 ORDER BY person_id, external_id");
        command.Parameters.Add(P(sourceSystem));
        var links = new List<ExternalIdentityLink>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new ExternalIdentityLink(reader.GetGuid(0), reader.GetString(1)));
        }

        return links;
    }

    public async Task<IReadOnlyList<Guid>> InsertPlanAsync(ViewerContext viewer, SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(items);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);

        var superseded = new List<Guid>();
        await using (var command = tx.CreateCommand(
            "UPDATE sync_plan SET status = 'superseded', closed_at = $2 WHERE target_system = $1 AND status IN ('proposed', 'approved') RETURNING id"))
        {
            command.Parameters.Add(P(plan.TargetSystem));
            command.Parameters.Add(P(plan.ProposedAt));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                superseded.Add(reader.GetGuid(0));
            }
        }

        await using (var command = tx.CreateCommand(
            """
            INSERT INTO sync_plan (id, target_system, status, settings, settings_sha256, source_file_name, source_sha256, directory_user_count,
                                   jinji_fingerprint, business_date, summary, proposed_by_person_id, proposed_via_actor, proposed_via_client_id,
                                   proposed_at, expires_at)
            VALUES ($1, $2, 'proposed', $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
            """))
        {
            AddAll(command.Parameters, 
            [
                P(plan.Id),
                P(plan.TargetSystem),
                new NpgsqlParameter { Value = plan.Settings.ToCanonicalJson(), NpgsqlDbType = NpgsqlDbType.Jsonb },
                P(plan.SettingsSha256),
                P(plan.SourceFileName),
                P(plan.SourceSha256),
                P(plan.DirectoryUserCount),
                P(plan.JinjiFingerprint),
                P(plan.BusinessDate),
                Jsonb(plan.Summary),
                P(plan.ProposedByPersonId),
                P(DbCodes.ToCode(plan.ProposedViaActor)),
                P(plan.ProposedViaClientId, NpgsqlDbType.Text),
                P(plan.ProposedAt),
                P(plan.ExpiresAt),
            ]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var chunk in items.Chunk(BatchSize))
        {
            await using var batch = new NpgsqlBatch(tx.Connection, tx.Transaction);
            foreach (var item in chunk)
            {
                var command = new NpgsqlBatchCommand(
                    """
                    INSERT INTO sync_plan_item (id, sync_plan_id, sequence, priority, action, person_id, employee_number, directory_object_id,
                                                user_principal_name, in_script, changes, basis, note)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
                    """);
                AddAll(command.Parameters, 
                [
                    P(item.Id),
                    P(plan.Id),
                    P(item.Sequence),
                    P(SyncPlanService.Code(item.Priority)),
                    P(SyncPlanService.Code(item.Action)),
                    P(item.PersonId, NpgsqlDbType.Uuid),
                    P(item.EmployeeNumber, NpgsqlDbType.Text),
                    P(item.ObjectId, NpgsqlDbType.Text),
                    P(item.UserPrincipalName, NpgsqlDbType.Text),
                    P(item.InScript),
                    Jsonb(item.Changes.Select(c => new StoredChange(c.Field, c.Current, c.Desired, c.CurrentKnown))),
                    Jsonb(item.Basis),
                    P(item.Note, NpgsqlDbType.Text),
                ]);
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return superseded;
    }

    public async Task<IReadOnlyList<SyncPlanRecord>> ListPlansAsync(ViewerContext viewer, int limit, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(PlanSelect + " ORDER BY proposed_at DESC, id DESC LIMIT $1");
        command.Parameters.Add(P(limit));
        return await ReadPlansAsync(command, cancellationToken);
    }

    public async Task<SyncPlanRecord?> FindPlanAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(PlanSelect + " WHERE id = $1");
        command.Parameters.Add(P(planId));
        return (await ReadPlansAsync(command, cancellationToken)).SingleOrDefault();
    }

    public async Task<IReadOnlyList<SyncPlanItem>> ListItemsAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            """
            SELECT id, sequence, priority, action, person_id, employee_number, directory_object_id, user_principal_name, in_script,
                   changes::text, basis::text, note
            FROM sync_plan_item WHERE sync_plan_id = $1 ORDER BY sequence
            """);
        command.Parameters.Add(P(planId));
        var items = new List<SyncPlanItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var changes = JsonSerializer.Deserialize<List<StoredChange>>(reader.GetString(9), Json) ?? [];
            items.Add(new SyncPlanItem
            {
                Id = reader.GetGuid(0),
                Sequence = reader.GetInt32(1),
                Priority = Parse<SyncPriority>(reader.GetString(2)),
                Action = Parse<SyncAction>(reader.GetString(3)),
                PersonId = reader.IsDBNull(4) ? null : reader.GetGuid(4),
                EmployeeNumber = reader.IsDBNull(5) ? null : reader.GetString(5),
                ObjectId = reader.IsDBNull(6) ? null : reader.GetString(6),
                UserPrincipalName = reader.IsDBNull(7) ? null : reader.GetString(7),
                InScript = reader.GetBoolean(8),
                Changes = [.. changes.Select(c => new SyncChange(c.Field, c.Current, c.Desired, c.CurrentKnown))],
                Basis = JsonSerializer.Deserialize<SyncBasis>(reader.GetString(10), Json) ?? new SyncBasis(),
                Note = reader.IsDBNull(11) ? null : reader.GetString(11),
            });
        }

        return items;
    }

    public async Task<bool> DecideAsync(ViewerContext viewer, Guid planId, bool approve, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(
            "UPDATE sync_plan SET status = $2, decided_by_person_id = $3, decided_at = $4 WHERE id = $1 AND status = 'proposed'");
        AddAll(command.Parameters, [P(planId), P(approve ? "approved" : "rejected"), P(viewer.PersonId, NpgsqlDbType.Uuid), P(_timeProvider.GetUtcNow())]);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return updated == 1;
    }

    public async Task<bool> MarkStaleAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand("UPDATE sync_plan SET status = 'stale', closed_at = $2 WHERE id = $1 AND status IN ('proposed', 'approved')");
        AddAll(command.Parameters, [P(planId), P(_timeProvider.GetUtcNow())]);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return updated == 1;
    }

    public async Task InsertRunAsync(ViewerContext viewer, SyncRunRecord run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using (var command = tx.CreateCommand(
            """
            INSERT INTO sync_run (id, sync_plan_id, mode, what_if, log_sha256, data_sha256, started_at, finished_at, recorded_by_person_id,
                                  recorded_via_actor, recorded_at, entry_count, unknown_entry_count)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            """))
        {
            AddAll(command.Parameters, 
            [
                P(run.Id),
                P(run.PlanId),
                P(SyncPlanService.Code(run.Mode)),
                P(run.WhatIf),
                P(run.LogSha256, NpgsqlDbType.Text),
                P(run.DataSha256),
                P(run.StartedAt, NpgsqlDbType.TimestampTz),
                P(run.FinishedAt, NpgsqlDbType.TimestampTz),
                P(run.RecordedByPersonId, NpgsqlDbType.Uuid),
                P(DbCodes.ToCode(run.RecordedViaActor)),
                P(run.RecordedAt),
                P(run.Results.Count),
                P(run.UnknownEntryCount),
            ]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var chunk in run.Results.Chunk(BatchSize))
        {
            await using var batch = new NpgsqlBatch(tx.Connection, tx.Transaction);
            foreach (var result in chunk)
            {
                var command = new NpgsqlBatchCommand(
                    """
                    INSERT INTO sync_item_result (id, sync_run_id, sync_plan_id, sync_plan_item_id, outcome, observed, message, executed_at)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                    """);
                AddAll(command.Parameters, 
                [
                    P(Domain.EntityId.New()),
                    P(run.Id),
                    P(run.PlanId),
                    P(result.ItemId),
                    P(SyncPlanService.Code(result.Outcome)),
                    Jsonb(result.Observed),
                    P(result.Message, NpgsqlDbType.Text),
                    P(result.ExecutedAt, NpgsqlDbType.TimestampTz),
                ]);
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!run.WhatIf)
        {
            await using var command = tx.CreateCommand("UPDATE sync_plan SET status = 'applied', applied_at = $2 WHERE id = $1 AND status = 'approved'");
            AddAll(command.Parameters, [P(run.PlanId), P(_timeProvider.GetUtcNow())]);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SyncRunRecord>> ListRunsAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        var results = new Dictionary<Guid, List<SyncLogEntry>>();
        await using (var command = tx.CreateCommand(
            "SELECT sync_run_id, sync_plan_item_id, outcome, observed::text, message, executed_at FROM sync_item_result WHERE sync_plan_id = $1 ORDER BY id"))
        {
            command.Parameters.Add(P(planId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var runId = reader.GetGuid(0);
                if (!results.TryGetValue(runId, out var list))
                {
                    results[runId] = list = [];
                }

                list.Add(new SyncLogEntry(
                    reader.GetGuid(1),
                    Parse<SyncOutcome>(reader.GetString(2)),
                    JsonSerializer.Deserialize<Dictionary<string, string?>>(reader.GetString(3)) ?? [],
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        var runs = new List<SyncRunRecord>();
        await using (var command = tx.CreateCommand(
            """
            SELECT id, mode, what_if, log_sha256, data_sha256, started_at, finished_at, recorded_by_person_id, recorded_via_actor, recorded_at, unknown_entry_count
            FROM sync_run WHERE sync_plan_id = $1 ORDER BY recorded_at, id
            """))
        {
            command.Parameters.Add(P(planId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetGuid(0);
                runs.Add(new SyncRunRecord
                {
                    Id = id,
                    PlanId = planId,
                    Mode = Parse<SyncRunMode>(reader.GetString(1)),
                    WhatIf = reader.GetBoolean(2),
                    LogSha256 = reader.IsDBNull(3) ? null : reader.GetString(3),
                    DataSha256 = reader.GetString(4),
                    StartedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                    FinishedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    RecordedByPersonId = reader.IsDBNull(7) ? null : reader.GetGuid(7),
                    RecordedViaActor = ParseActor(reader.GetString(8)),
                    RecordedAt = reader.GetFieldValue<DateTimeOffset>(9),
                    UnknownEntryCount = reader.GetInt32(10),
                    Results = results.GetValueOrDefault(id) ?? [],
                });
            }
        }

        return runs;
    }

    private const string PlanSelect =
        """
        SELECT id, target_system, status, settings::text, settings_sha256, source_file_name, source_sha256, directory_user_count, jinji_fingerprint,
               business_date, summary::text, proposed_by_person_id, proposed_via_actor, proposed_via_client_id, proposed_at, expires_at,
               decided_by_person_id, decided_at, closed_at, applied_at
        FROM sync_plan
        """;

    private static async Task<IReadOnlyList<SyncPlanRecord>> ReadPlansAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var plans = new List<SyncPlanRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            plans.Add(new SyncPlanRecord
            {
                Id = reader.GetGuid(0),
                TargetSystem = reader.GetString(1),
                Status = Parse<SyncPlanStatus>(reader.GetString(2)),
                Settings = SyncSettings.Parse(Encoding.UTF8.GetBytes(reader.GetString(3))),
                SettingsSha256 = reader.GetString(4),
                SourceFileName = reader.GetString(5),
                SourceSha256 = reader.GetString(6),
                DirectoryUserCount = reader.GetInt32(7),
                JinjiFingerprint = reader.GetString(8),
                BusinessDate = reader.GetFieldValue<DateOnly>(9),
                Summary = JsonSerializer.Deserialize<SortedDictionary<string, int>>(reader.GetString(10)) ?? [],
                ProposedByPersonId = reader.GetGuid(11),
                ProposedViaActor = ParseActor(reader.GetString(12)),
                ProposedViaClientId = reader.IsDBNull(13) ? null : reader.GetString(13),
                ProposedAt = reader.GetFieldValue<DateTimeOffset>(14),
                ExpiresAt = reader.GetFieldValue<DateTimeOffset>(15),
                DecidedByPersonId = reader.IsDBNull(16) ? null : reader.GetGuid(16),
                DecidedAt = reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
                ClosedAt = reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18),
                AppliedAt = reader.IsDBNull(19) ? null : reader.GetFieldValue<DateTimeOffset>(19),
            });
        }

        return plans;
    }

    private static ActorType ParseActor(string code) => code switch
    {
        "user" => ActorType.User,
        "agent" => ActorType.Agent,
        "system" => ActorType.System,
        "service" => ActorType.Service,
        _ => throw new InvalidOperationException($"DB に未知の操作者の種別があります: {code}"),
    };

    private static void AddAll(NpgsqlParameterCollection collection, NpgsqlParameter[] parameters) => collection.AddRange(parameters);

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };

    private static NpgsqlParameter Jsonb(object value) =>
        new() { Value = JsonSerializer.Serialize(value, Json), NpgsqlDbType = NpgsqlDbType.Jsonb };

    private static T Parse<T>(string code)
        where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            if (SyncPlanService.Code(value) == code)
            {
                return value;
            }
        }

        throw new InvalidOperationException($"DB に未知の {typeof(T).Name} があります: {code}");
    }

    /// <summary>changes の 1 要素（DB の形。current / desired は null でも書く）。</summary>
    private sealed record StoredChange(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string Field,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Current,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Desired,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool CurrentKnown);
}
