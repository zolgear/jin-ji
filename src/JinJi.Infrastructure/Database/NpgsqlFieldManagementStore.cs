using JinJi.Application.FieldManagement;
using JinJi.Domain.Organization;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>管理元の起案と承認（field_domain_setting）・担当の指名（org_editor_grant）の読み書き（0020）。閲覧者を RLS に渡す。</summary>
/// <remarks>
/// 起案・承認は人事管理者か組織管理者の人（本人として記録）、指名・取り消しは範囲を持つ人（本人として記録）であることを RLS が守る。
/// 起案者 ≠ 承認者・自分の指名の禁止は CHECK 制約、状態の遷移と中身の不変はトリガー、領域ごとに有効な行・起案中の行が高々 1 つは部分一意索引、
/// 同じ組織・同じ人の指名の期間の重なりは排他制約で守る。
/// </remarks>
public sealed class NpgsqlFieldManagementStore : IFieldManagementStore
{
    private const string GrantOverlap = "org_editor_grant_no_overlap";

    private const string SettingColumns =
        "id, domain, mode, status, note, proposed_by_person_id, proposed_at, decided_by_person_id, decided_at, superseded_at";

    private const string GrantColumns =
        "id, org_unit_id, person_id, lower(valid_period), upper(valid_period), granted_by_person_id, granted_at, revoked_by_person_id, revoked_at";

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlFieldManagementStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<FieldDomainSetting>> ListSettingsAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand($"SELECT {SettingColumns} FROM field_domain_setting ORDER BY proposed_at DESC, id DESC");
        var settings = await ReadAllAsync(command, ReadSetting, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return settings;
    }

    public async Task<FieldDomainSetting?> FindSettingAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand($"SELECT {SettingColumns} FROM field_domain_setting WHERE id = $1");
        command.Parameters.Add(P(id));
        var settings = await ReadAllAsync(command, ReadSetting, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return settings.SingleOrDefault();
    }

    public async Task<bool> InsertSettingAsync(ViewerContext viewer, FieldDomainSetting setting, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(setting);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO field_domain_setting (id, domain, mode, status, note, proposed_by_person_id, proposed_at)
            VALUES ($1, $2, $3, 'proposed', $4, $5, $6)
            ON CONFLICT (domain) WHERE status = 'proposed' DO NOTHING
            """);
        command.Parameters.Add(P(setting.Id));
        command.Parameters.Add(P(FieldDomains.Key(setting.Domain)));
        command.Parameters.Add(P(FieldDomains.Key(setting.Mode)));
        command.Parameters.Add(P(setting.Note, NpgsqlDbType.Text));
        command.Parameters.Add(P(setting.ProposedBy));
        command.Parameters.Add(P(setting.ProposedAt));
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<bool> DecideSettingAsync(ViewerContext viewer, Guid id, bool approve, DateTimeOffset decidedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        if (approve)
        {
            // 置き換え → 承認の順（領域ごとに承認済みは高々 1 つ。部分一意索引は文ごとに検査する）
            await using var supersede = tx.CreateCommand(
                """
                UPDATE field_domain_setting SET status = 'superseded', superseded_at = $2
                WHERE status = 'approved' AND domain = (SELECT domain FROM field_domain_setting WHERE id = $1 AND status = 'proposed')
                """);
            supersede.Parameters.Add(P(id));
            supersede.Parameters.Add(P(decidedAt));
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var decide = tx.CreateCommand(
            """
            UPDATE field_domain_setting SET status = $2, decided_by_person_id = jinji_viewer_person_id(), decided_at = $3
            WHERE id = $1 AND status = 'proposed'
            """);
        decide.Parameters.Add(P(id));
        decide.Parameters.Add(P(approve ? "approved" : "rejected"));
        decide.Parameters.Add(P(decidedAt));
        if (await decide.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<OrgEditorGrant>> ListGrantsAsync(ViewerContext viewer, Guid? orgUnitId, Guid? personId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(
            $"""
            SELECT {GrantColumns} FROM org_editor_grant
            WHERE ($1::uuid IS NULL OR org_unit_id = $1) AND ($2::uuid IS NULL OR person_id = $2)
            ORDER BY granted_at DESC, id DESC
            """);
        command.Parameters.Add(P(orgUnitId, NpgsqlDbType.Uuid));
        command.Parameters.Add(P(personId, NpgsqlDbType.Uuid));
        var grants = await ReadAllAsync(command, ReadGrant, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return grants;
    }

    public async Task<OrgEditorGrant?> FindGrantAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand($"SELECT {GrantColumns} FROM org_editor_grant WHERE id = $1");
        command.Parameters.Add(P(id));
        var grants = await ReadAllAsync(command, ReadGrant, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return grants.SingleOrDefault();
    }

    public async Task<bool> InsertGrantAsync(ViewerContext viewer, OrgEditorGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(grant);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using (var command = tx.CreateCommand(
            """
            INSERT INTO org_editor_grant (id, org_unit_id, person_id, valid_period, granted_by_person_id, granted_at)
            VALUES ($1, $2, $3, daterange($4, $5), $6, $7)
            """))
        {
            command.Parameters.Add(P(grant.Id));
            command.Parameters.Add(P(grant.OrgUnitId));
            command.Parameters.Add(P(grant.PersonId));
            command.Parameters.Add(P(grant.From));
            command.Parameters.Add(P(grant.Until));
            command.Parameters.Add(P(grant.GrantedBy));
            command.Parameters.Add(P(grant.GrantedAt));
            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation && ex.ConstraintName == GrantOverlap)
            {
                return false;
            }
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeGrantAsync(ViewerContext viewer, Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(
            "UPDATE org_editor_grant SET revoked_by_person_id = jinji_viewer_person_id(), revoked_at = $2 WHERE id = $1 AND revoked_at IS NULL");
        command.Parameters.Add(P(id));
        command.Parameters.Add(P(revokedAt));
        var revoked = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return revoked;
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

    private static FieldDomainSetting ReadSetting(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        FieldDomains.TryParseDomain(reader.GetString(1), out var domain) ? domain : throw new InvalidOperationException($"DB に未知の領域があります: {reader.GetString(1)}"),
        FieldDomains.TryParseMode(reader.GetString(2), out var mode) ? mode : throw new InvalidOperationException($"DB に未知の管理元があります: {reader.GetString(2)}"),
        reader.GetString(3) switch
        {
            "proposed" => FieldDomainSettingStatus.Proposed,
            "approved" => FieldDomainSettingStatus.Approved,
            "rejected" => FieldDomainSettingStatus.Rejected,
            "superseded" => FieldDomainSettingStatus.Superseded,
            var other => throw new InvalidOperationException($"DB に未知の管理元の起案の状態があります: {other}"),
        },
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetGuid(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9));

    private static OrgEditorGrant ReadGrant(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetFieldValue<DateOnly>(3),
        reader.GetFieldValue<DateOnly>(4),
        reader.GetGuid(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.IsDBNull(7) ? null : reader.GetGuid(7),
        reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8));

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
