using JinJi.Application;
using JinJi.Domain.Organization;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 段の定義（org_level_scheme / org_level）の読み書き。権限の最終的な線は DB が引く:
/// RLS（変更系は hr_admin だけ・記録する人は閲覧者本人・段は起案中の定義に起案者だけが追加できる）、
/// CHECK 制約（起案者 ≠ 承認者）、排他制約（範囲ごとに有効な定義は 1 つ）、トリガー（状態の遷移・置き換え先の検査）。
/// </summary>
public sealed class NpgsqlOrgLevelStore : IOrgLevelStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;

    public NpgsqlOrgLevelStore(NpgsqlDataSource dataSource, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dataSource = dataSource;
        _timeProvider = timeProvider;
    }

    public async Task<StoredOrgLevelScheme?> FindAsync(ViewerContext viewer, Guid schemeId, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        StoredOrgLevelScheme? found = null;
        await using (var command = tx.CreateCommand(
            "SELECT legal_entity_id, status, proposed_by_person_id, decided_by_person_id FROM org_level_scheme WHERE id = $1"))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = schemeId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                found = new StoredOrgLevelScheme(
                    schemeId,
                    new OrgLevelScheme(reader.IsDBNull(0) ? null : reader.GetGuid(0), []),
                    DbCodes.ParseLevelSchemeStatus(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3));
            }
        }

        if (found is not null)
        {
            var levels = new List<OrgLevel>();
            await using var command = tx.CreateCommand("SELECT level_key, name, rank FROM org_level WHERE scheme_id = $1 ORDER BY rank");
            command.Parameters.Add(new NpgsqlParameter { Value = schemeId });
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    levels.Add(new OrgLevel(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
                }
            }

            found = found with { Scheme = new OrgLevelScheme(found.Scheme.LegalEntityId, levels) };
        }

        await tx.CommitAsync(cancellationToken);
        return found;
    }

    public async Task InsertProposalAsync(ViewerContext viewer, Guid schemeId, OrgLevelScheme scheme, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(scheme);
        scheme.Validate();

        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using (var command = tx.CreateCommand(
            """
            INSERT INTO org_level_scheme (id, legal_entity_id, proposed_by_person_id, proposed_via_client_id, proposed_at)
            VALUES ($1, $2, $3, $4, $5)
            """))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = schemeId });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)scheme.LegalEntityId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)viewer.EffectivePersonId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = (viewer.Actor == ActorType.Agent ? viewer.ClientId : null) ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            command.Parameters.Add(new NpgsqlParameter { Value = _timeProvider.GetUtcNow() });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var level in scheme.Levels)
        {
            await using var command = tx.CreateCommand("INSERT INTO org_level (id, scheme_id, level_key, name, rank) VALUES ($1, $2, $3, $4, $5)");
            command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
            command.Parameters.Add(new NpgsqlParameter { Value = schemeId });
            command.Parameters.Add(new NpgsqlParameter { Value = level.Key });
            command.Parameters.Add(new NpgsqlParameter { Value = level.Name });
            command.Parameters.Add(new NpgsqlParameter { Value = level.Rank });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<bool> ApproveAsync(ViewerContext viewer, Guid schemeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var now = _timeProvider.GetUtcNow();
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);

        // 1. 起案中の定義を承認する（範囲ごとに有効な定義は 1 つという排他制約はコミット時に検査する）
        await using (var approve = tx.CreateCommand(
            "UPDATE org_level_scheme SET status = 'approved', decided_by_person_id = $2, decided_at = $3 WHERE id = $1 AND status = 'proposed'"))
        {
            approve.Parameters.Add(new NpgsqlParameter { Value = schemeId });
            approve.Parameters.Add(new NpgsqlParameter { Value = (object?)viewer.EffectivePersonId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            approve.Parameters.Add(new NpgsqlParameter { Value = now });
            if (await approve.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await tx.Transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        // 2. 同じ範囲の、それまで有効だった定義を置き換え済みにする（置き換え先が承認済み・同じ範囲であることはトリガーが検査する）
        await using (var supersede = tx.CreateCommand(
            """
            UPDATE org_level_scheme old SET status = 'superseded', superseded_by_scheme_id = $1, superseded_at = $2
            FROM org_level_scheme new
            WHERE new.id = $1 AND old.id <> new.id AND old.status = 'approved'
              AND old.legal_entity_id IS NOT DISTINCT FROM new.legal_entity_id
            """))
        {
            supersede.Parameters.Add(new NpgsqlParameter { Value = schemeId });
            supersede.Parameters.Add(new NpgsqlParameter { Value = now });
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<DateOnly>> GetOrgChangeDatesAfterAsync(ViewerContext viewer, DateOnly after, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            "SELECT DISTINCT lower(valid_period) FROM org_unit_version WHERE upper_inf(recorded_period) AND lower(valid_period) > $1 ORDER BY 1");
        command.Parameters.Add(new NpgsqlParameter { Value = after });
        var dates = new List<DateOnly>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                dates.Add(reader.GetFieldValue<DateOnly>(0));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return dates;
    }
}
