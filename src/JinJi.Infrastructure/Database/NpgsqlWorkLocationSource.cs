using JinJi.Application.Ingest;
using JinJi.Policy;
using Npgsql;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 取込済みの勤務地（0017 で廃止した person_version.work_location）を読む（<see cref="IWorkLocationSource"/>）。
/// 列は照合の入力と過去の記録としてだけ残っており、アプリが読むのはここだけ。
/// </summary>
public sealed class NpgsqlWorkLocationSource : IWorkLocationSource
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlWorkLocationSource(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<LegacyWorkLocation>> ListAsync(ViewerContext viewer, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            """
            SELECT p.employee_number, e.code, v.work_location
            FROM person_version v
            JOIN person p ON p.id = v.person_id
            JOIN legal_entity e ON e.id = p.legal_entity_id
            WHERE upper_inf(v.recorded_period) AND v.valid_period @> $1 AND btrim(coalesce(v.work_location, '')) <> ''
            ORDER BY e.code, p.employee_number
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = today });
        var rows = new List<LegacyWorkLocation>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new LegacyWorkLocation(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return rows;
    }
}
