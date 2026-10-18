using JinJi.Application;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 組織改編・一斉異動の日付（時点の目盛り）の読み込み。閲覧者を RLS に渡して読む。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>組織改編: 現在記録中の組織の版の有効期間の開始日と終了日（終了日は版が終わる日 = 次の状態が始まる日）</item>
/// <item>一斉異動: 現在記録中の発令のうち、異動・役職の任免・兼務・出向の発令が同じ発令日に下限以上ある日（入退社・一時停止は数えない）</item>
/// <item>人事管理者以外には RLS が将来の行（開始が今日より後の組織の版・内示の発令）を見せない。見えている版の終了日は列の値なので
/// RLS では隠せず、上限の日付（until）で切る</item>
/// </list>
/// </remarks>
public sealed class NpgsqlDirectoryCalendarStore : IDirectoryCalendarStore
{
    private const string Sql = """
        SELECT d, 'org_change' FROM (
            SELECT lower(valid_period) AS d FROM org_unit_version WHERE upper_inf(recorded_period) AND NOT lower_inf(valid_period)
            UNION
            SELECT upper(valid_period) FROM org_unit_version WHERE upper_inf(recorded_period) AND NOT upper_inf(valid_period)
        ) AS org_dates
        WHERE $1::date IS NULL OR d <= $1::date
        UNION ALL
        SELECT effective_date, 'mass_action'
        FROM personnel_action
        WHERE upper_inf(recorded_period)
          AND action_type IN ('transfer', 'position_appointment', 'position_dismissal', 'concurrent_start', 'concurrent_end', 'secondment_start', 'secondment_end')
          AND ($1::date IS NULL OR effective_date <= $1::date)
        GROUP BY effective_date
        HAVING count(*) >= $2
        ORDER BY 1, 2
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlDirectoryCalendarStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<DirectoryMilestone>> GetMilestonesAsync(ViewerContext viewer, DateOnly? until, int massActionThreshold, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(massActionThreshold, 1);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        var milestones = new List<DirectoryMilestone>();
        await using (var command = tx.CreateCommand(Sql))
        {
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Date, Value = until is { } d ? d : DBNull.Value });
            command.Parameters.Add(new NpgsqlParameter { Value = (long)massActionThreshold });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = reader.GetString(1) == "org_change" ? DirectoryMilestoneKind.OrgChange : DirectoryMilestoneKind.MassAction;
                milestones.Add(new DirectoryMilestone(reader.GetFieldValue<DateOnly>(0), kind));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return milestones;
    }
}
