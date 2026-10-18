using JinJi.Application;
using JinJi.Policy;
using Npgsql;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// ポリシー（policy_rule）の読み書き。権限の最終的な線は DB が引く: RLS（変更系は hr_admin だけ・記録する人は閲覧者本人）、
/// CHECK 制約（起案者 ≠ 承認者、廃止の起案者 ≠ 廃止の承認者、個人単位の例外の対象者は関われない）、トリガー（状態の遷移）。
/// </summary>
public sealed class NpgsqlPolicyRuleStore : IPolicyRuleStore
{
    private const string Columns = """
        id, target_kind, org_unit_id, include_descendants, person_id, fields, allow_scope,
        status, proposed_by_person_id, decided_by_person_id, retirement_proposed_by_person_id, allow_level_key
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;

    public NpgsqlPolicyRuleStore(NpgsqlDataSource dataSource, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dataSource = dataSource;
        _timeProvider = timeProvider;
    }

    public async Task<PolicySet> LoadActiveAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand($"SELECT {Columns} FROM policy_rule WHERE status = 'approved' ORDER BY id");
        var rules = new List<PolicyRule>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rules.Add(Read(reader).Rule);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return new PolicySet(rules);
    }

    public async Task<IReadOnlyList<PolicyRule>> ListOpenAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand($"SELECT {Columns} FROM policy_rule WHERE status IN ('proposed', 'approved') ORDER BY id");
        var rules = new List<PolicyRule>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rules.Add(Read(reader).Rule);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return rules;
    }

    public async Task<StoredPolicyRule?> FindAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand($"SELECT {Columns} FROM policy_rule WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = ruleId });
        StoredPolicyRule? found = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                found = Read(reader);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return found;
    }

    public async Task InsertProposalAsync(ViewerContext viewer, PolicyRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(rule);
        rule.Validate();

        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO policy_rule (id, target_kind, org_unit_id, include_descendants, person_id, fields, allow_scope,
                                     proposed_by_person_id, proposed_via_client_id, proposed_at, allow_level_key)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = rule.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = DbCodes.ToCode(rule.TargetKind) });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)rule.OrgUnitId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = rule.IncludeDescendants });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)rule.PersonId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = rule.Fields.Order(StringComparer.Ordinal).ToArray() });
        command.Parameters.Add(new NpgsqlParameter { Value = rule.Allow.KindCode() });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)viewer.EffectivePersonId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = (viewer.Actor == ActorType.Agent ? viewer.ClientId : null) ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = _timeProvider.GetUtcNow() });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)rule.Allow.LevelKey ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<bool> ApproveAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE policy_rule
            SET status = 'approved', decided_by_person_id = $2, decided_at = $3
            WHERE id = $1 AND status = 'proposed'
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = ruleId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)viewer.EffectivePersonId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = _timeProvider.GetUtcNow() });
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return updated == 1;
    }

    public async Task<bool> ProposeRetirementAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE policy_rule
            SET retirement_proposed_by_person_id = $2, retirement_proposed_via_client_id = $3, retirement_proposed_at = $4
            WHERE id = $1 AND status = 'approved' AND retirement_proposed_by_person_id IS NULL
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = ruleId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)viewer.EffectivePersonId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = (viewer.Actor == ActorType.Agent ? viewer.ClientId : null) ?? (object)DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = _timeProvider.GetUtcNow() });
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return updated == 1;
    }

    public async Task<bool> ApproveRetirementAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            UPDATE policy_rule
            SET status = 'retired', retired_by_person_id = $2, retired_at = $3
            WHERE id = $1 AND status = 'approved' AND retirement_proposed_by_person_id IS NOT NULL
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = ruleId });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)viewer.EffectivePersonId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = _timeProvider.GetUtcNow() });
        var updated = await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return updated == 1;
    }

    private static StoredPolicyRule Read(NpgsqlDataReader r)
    {
        var rule = new PolicyRule
        {
            Id = r.GetGuid(0),
            TargetKind = DbCodes.ParseTargetKind(r.GetString(1)),
            OrgUnitId = r.IsDBNull(2) ? null : r.GetGuid(2),
            IncludeDescendants = r.GetBoolean(3),
            PersonId = r.IsDBNull(4) ? null : r.GetGuid(4),
            Fields = r.GetFieldValue<string[]>(5).ToHashSet(StringComparer.Ordinal),
            Allow = RelationScope.Of(RelationScopes.ParseKind(r.GetString(6)), r.IsDBNull(11) ? null : r.GetString(11)),
        };
        return new StoredPolicyRule(
            rule,
            DbCodes.ParseRuleStatus(r.GetString(7)),
            r.GetGuid(8),
            r.IsDBNull(9) ? null : r.GetGuid(9),
            r.IsDBNull(10) ? null : r.GetGuid(10));
    }
}
