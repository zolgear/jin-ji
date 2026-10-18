using System.Globalization;
using System.Text;
using JinJi.Application;
using JinJi.Policy;
using Npgsql;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 組織・人事データの世代を、二つの時間軸のテーブルの「行数・記録時間の開始の最大値・記録時間の終了の最大値」から作る（<see cref="IOrgDataVersionSource"/>）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>人事データの変更は、行の追加（recorded_period の開始 = 記録時刻）か、記録期間を閉じる更新（終了 = 閉じた時刻）だけ（0002 のトリガー）。
/// どちらかがあれば、行数か最大値のどれかが変わる</item>
/// <item>行数も見るのは、記録時刻はトランザクションの開始時刻（now()）なので、長いトランザクションが後からコミットされると
/// 既に見た最大値より前の時刻の行が現れるため（行数で検知する）</item>
/// <item>段の定義（org_level_scheme）も見る。承認（decided_at）と置き換え（superseded_at）で値が変わる</item>
/// <item>拠点の版（site_version。0017）も二つの時間軸の表として見る</item>
/// <item>法人・組織・人・拠点の不変 ID の表（legal_entity / org_unit / person / site）は見ない。更新できず、版や所属の行が無ければスナップショットに現れないため</item>
/// <item>閲覧者を RLS に渡して読む。見えない行（内示など）の変化では世代が変わらないが、その閲覧者のスナップショットも変わらない</item>
/// <item>最大 1 万人規模（数十万行）なら、1 回の確認は全表の走査でも数十ミリ秒程度。確認の間隔は <c>OrgSnapshotCacheOptions</c> で絞る</item>
/// </list>
/// </remarks>
public sealed class NpgsqlOrgDataVersionSource : IOrgDataVersionSource
{
    private const string Sql = """
        SELECT 'org_unit_version', count(*), max(lower(recorded_period)), max(upper(recorded_period)) FROM org_unit_version
        UNION ALL
        SELECT 'person_version', count(*), max(lower(recorded_period)), max(upper(recorded_period)) FROM person_version
        UNION ALL
        SELECT 'personnel_action', count(*), max(lower(recorded_period)), max(upper(recorded_period)) FROM personnel_action
        UNION ALL
        SELECT 'assignment', count(*), max(lower(recorded_period)), max(upper(recorded_period)) FROM assignment
        UNION ALL
        SELECT 'account_suspension', count(*), max(lower(recorded_period)), max(upper(recorded_period)) FROM account_suspension
        UNION ALL
        SELECT 'site_version', count(*), max(lower(recorded_period)), max(upper(recorded_period)) FROM site_version
        UNION ALL
        SELECT 'org_level_scheme', count(*), max(decided_at), max(superseded_at) FROM org_level_scheme
        """;

    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlOrgDataVersionSource(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<string> GetVersionAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(Sql);
        var version = new StringBuilder();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                version.Append(reader.GetString(0)).Append(':').Append(reader.GetInt64(1).ToString(CultureInfo.InvariantCulture));
                for (var i = 2; i <= 3; i++)
                {
                    version.Append(':').Append(reader.IsDBNull(i) ? "-" : reader.GetFieldValue<DateTime>(i).Ticks.ToString(CultureInfo.InvariantCulture));
                }

                version.Append(';');
            }
        }

        await tx.CommitAsync(cancellationToken);
        return version.ToString();
    }
}
