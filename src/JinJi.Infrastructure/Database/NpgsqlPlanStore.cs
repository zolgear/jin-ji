using System.Text.Json;
using JinJi.Application.Plans;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>素案・申請版・登録の記録（plan・plan_version・plan_registration。0023）の読み書き。閲覧者を RLS に渡す。</summary>
/// <remarks>見えるのは作成者と共有した人だけ、書けるのは作成者だけ（RLS）。中身の形は CHECK 制約で検査する。</remarks>
public sealed class NpgsqlPlanStore : IPlanStore
{
    private const string PlanColumns =
        "id, title, note, base_date, effective_date, changes::text, owner_person_id, created_via, shared_with, created_at, updated_at";

    private const string VersionColumns =
        """
        id, plan_id, version_no, title, note, base_date, effective_date, changes::text, created_by_person_id, created_at,
        approved_on, approval_reference, approval_recorded_by_person_id, approval_recorded_by_client, approval_recorded_at
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlPlanStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public Task<IReadOnlyList<Plan>> ListAsync(ViewerContext viewer, CancellationToken cancellationToken) =>
        ReadAsync(viewer, $"SELECT {PlanColumns} FROM plan ORDER BY updated_at DESC, id DESC LIMIT 500", [], ReadPlan, cancellationToken);

    public async Task<Plan?> FindAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken) =>
        (await ReadAsync(viewer, $"SELECT {PlanColumns} FROM plan WHERE id = $1", [P(id)], ReadPlan, cancellationToken)).SingleOrDefault();

    public Task InsertAsync(ViewerContext viewer, Plan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ExecuteAsync(
            viewer,
            """
            INSERT INTO plan (id, title, note, base_date, effective_date, changes, owner_person_id, created_via, created_at, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $9)
            """,
            [
                P(plan.Id), P(plan.Title), P(plan.Note, NpgsqlDbType.Text), P(plan.BaseDate), P(plan.EffectiveDate), Changes(plan.Changes),
                P(plan.Owner), P(plan.CreatedVia == ActorType.Agent ? "agent" : "user"), P(plan.CreatedAt),
            ],
            cancellationToken);
    }

    public async Task<bool> UpdateAsync(ViewerContext viewer, Guid id, PlanInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return await ExecuteAsync(
            viewer,
            "UPDATE plan SET title = $2, note = $3, base_date = $4, effective_date = $5, changes = $6, updated_at = $7 WHERE id = $1",
            [P(id), P(input.Title), P(input.Note, NpgsqlDbType.Text), P(input.BaseDate), P(input.EffectiveDate), Changes(input.Changes), P(now)],
            cancellationToken) == 1;
    }

    public async Task<bool> ShareAsync(ViewerContext viewer, Guid id, IReadOnlyList<Guid> people, DateTimeOffset now, CancellationToken cancellationToken) =>
        await ExecuteAsync(viewer, "UPDATE plan SET shared_with = $2, updated_at = $3 WHERE id = $1", [P(id), P(people.ToArray()), P(now)], cancellationToken) == 1;

    public async Task<bool> DeleteAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken) =>
        await ExecuteAsync(viewer, "DELETE FROM plan WHERE id = $1", [P(id)], cancellationToken) == 1;

    public async Task<PlanVersion?> FreezeAsync(ViewerContext viewer, Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using (var command = tx.CreateCommand(
            """
            INSERT INTO plan_version (id, plan_id, version_no, title, note, base_date, effective_date, changes, created_by_person_id, created_at)
            SELECT $2, p.id, coalesce((SELECT max(v.version_no) FROM plan_version v WHERE v.plan_id = p.id), 0) + 1,
                   p.title, p.note, p.base_date, p.effective_date, p.changes, jinji_viewer_person_id(), $3
            FROM plan p WHERE p.id = $1 FOR UPDATE OF p
            """))
        {
            command.Parameters.Add(P(id));
            command.Parameters.Add(P(Guid.CreateVersion7()));
            command.Parameters.Add(P(now));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await tx.Transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await using var select = tx.CreateCommand($"SELECT {VersionColumns} FROM plan_version WHERE plan_id = $1 ORDER BY version_no DESC LIMIT 1");
        select.Parameters.Add(P(id));
        var version = (await ReadAllAsync(select, ReadVersion, cancellationToken)).Single();
        await tx.CommitAsync(cancellationToken);
        return version;
    }

    public Task<IReadOnlyList<PlanVersion>> ListVersionsAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken) =>
        ReadAsync(viewer, $"SELECT {VersionColumns} FROM plan_version WHERE plan_id = $1 ORDER BY version_no", [P(planId)], ReadVersion, cancellationToken);

    public async Task<PlanVersion?> FindVersionAsync(ViewerContext viewer, Guid planId, int versionNo, CancellationToken cancellationToken) =>
        (await ReadAsync(viewer, $"SELECT {VersionColumns} FROM plan_version WHERE plan_id = $1 AND version_no = $2", [P(planId), P(versionNo)], ReadVersion, cancellationToken))
        .SingleOrDefault();

    public async Task<bool> RecordApprovalAsync(ViewerContext viewer, Guid versionId, DateOnly approvedOn, string? reference, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var service = viewer.Actor == ActorType.Service;
        return await ExecuteAsync(
            viewer,
            """
            UPDATE plan_version SET approved_on = $2, approval_reference = $3, approval_recorded_at = $4,
                   approval_recorded_by_person_id = CASE WHEN $5 THEN NULL ELSE jinji_viewer_person_id() END,
                   approval_recorded_by_client = CASE WHEN $5 THEN $6 END
            WHERE id = $1 AND approved_on IS NULL
            """,
            [P(versionId), P(approvedOn), P(reference, NpgsqlDbType.Text), P(now), P(service), P(viewer.ClientId, NpgsqlDbType.Text)],
            cancellationToken) == 1;
    }

    public async Task AddRegistrationLinksAsync(ViewerContext viewer, IReadOnlyList<PlanRegistrationLink> links, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(links);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        foreach (var link in links)
        {
            await using var command = tx.CreateCommand(
                "INSERT INTO plan_registration (plan_id, plan_version_id, change_index, field_registration_id) VALUES ($1, $2, $3, $4)");
            command.Parameters.Add(P(link.PlanId));
            command.Parameters.Add(P(link.PlanVersionId, NpgsqlDbType.Uuid));
            command.Parameters.Add(P(link.ChangeIndex));
            command.Parameters.Add(P(link.FieldRegistrationId));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public Task<IReadOnlyList<PlanRegistrationLink>> ListRegistrationLinksAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken) =>
        ReadAsync(
            viewer,
            "SELECT plan_id, plan_version_id, change_index, field_registration_id FROM plan_registration WHERE plan_id = $1 ORDER BY change_index",
            [P(planId)],
            r => new PlanRegistrationLink(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetInt32(2), r.GetGuid(3)),
            cancellationToken);

    // -----------------------------------------------------------------------
    // 共通
    // -----------------------------------------------------------------------

    private static Plan ReadPlan(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        Title = r.GetString(1),
        Note = r.IsDBNull(2) ? null : r.GetString(2),
        BaseDate = r.GetFieldValue<DateOnly>(3),
        EffectiveDate = r.GetFieldValue<DateOnly>(4),
        Changes = ParseChanges(r.GetString(5)),
        Owner = r.GetGuid(6),
        CreatedVia = r.GetString(7) == "agent" ? ActorType.Agent : ActorType.User,
        SharedWith = r.GetFieldValue<Guid[]>(8),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(9),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(10),
    };

    private static PlanVersion ReadVersion(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0),
        PlanId = r.GetGuid(1),
        VersionNo = r.GetInt32(2),
        Title = r.GetString(3),
        Note = r.IsDBNull(4) ? null : r.GetString(4),
        BaseDate = r.GetFieldValue<DateOnly>(5),
        EffectiveDate = r.GetFieldValue<DateOnly>(6),
        Changes = ParseChanges(r.GetString(7)),
        CreatedBy = r.GetGuid(8),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(9),
        ApprovedOn = r.IsDBNull(10) ? null : r.GetFieldValue<DateOnly>(10),
        ApprovalReference = r.IsDBNull(11) ? null : r.GetString(11),
        ApprovalRecordedBy = r.IsDBNull(12) ? null : r.GetGuid(12),
        ApprovalRecordedByClient = r.IsDBNull(13) ? null : r.GetString(13),
        ApprovalRecordedAt = r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14),
    };

    /// <summary>変更の配列を JSON にする（キーは kind・target_id・values）。</summary>
    private static NpgsqlParameter Changes(IReadOnlyList<PlanChange> changes)
    {
        var items = changes.Select(c => new Dictionary<string, object?>
        {
            ["kind"] = PlanChanges.Code(c.Kind),
            ["target_id"] = c.TargetId?.ToString("D"),
            ["values"] = c.Values,
        });
        return new NpgsqlParameter { Value = JsonSerializer.Serialize(items), NpgsqlDbType = NpgsqlDbType.Jsonb };
    }

    private static IReadOnlyList<PlanChange> ParseChanges(string json)
    {
        using var document = JsonDocument.Parse(json);
        var changes = new List<PlanChange>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var kind = PlanChanges.TryParse(item.GetProperty("kind").GetString(), out var k) ? k : throw new InvalidOperationException("DB に未知の素案の変更があります。");
            Guid? target = item.TryGetProperty("target_id", out var t) && t.ValueKind == JsonValueKind.String ? Guid.Parse(t.GetString()!) : null;
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (item.TryGetProperty("values", out var v) && v.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in v.EnumerateObject())
                {
                    values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                }
            }

            changes.Add(new PlanChange(kind, target, values));
        }

        return changes;
    }

    private async Task<IReadOnlyList<T>> ReadAsync<T>(ViewerContext viewer, string sql, NpgsqlParameter[] parameters, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var rows = await ReadAllAsync(command, read, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    private async Task<int> ExecuteAsync(ViewerContext viewer, string sql, NpgsqlParameter[] parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        int affected;
        try
        {
            affected = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new PlanException(PlanError.Forbidden, "この操作は許されていません。");
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("23", StringComparison.Ordinal))
        {
            throw new PlanException(PlanError.Invalid, $"素案の中身が正しくありません（{ex.ConstraintName ?? ex.SqlState}）。");
        }

        await tx.CommitAsync(cancellationToken);
        return affected;
    }

    private static async Task<List<T>> ReadAllAsync<T>(NpgsqlCommand command, Func<NpgsqlDataReader, T> read, CancellationToken cancellationToken)
    {
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(read(reader));
        }

        return rows;
    }

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
