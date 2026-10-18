using JinJi.Application.Audit;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 監査ログ（audit_event）への追記。業務のトランザクションとは別の接続・トランザクションで確定させるので、
/// 業務の処理が失敗・拒否されても監査ログは残る。
/// </summary>
public sealed class NpgsqlAuditSink : IAuditSink
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlAuditSink(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task WriteAsync(ViewerContext viewer, AuditRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(record);

        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using var command = tx.CreateCommand(InsertSql);
        AddParameters(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>audit_event の 1 行の INSERT（認証の監査は NpgsqlAuthStore が自分の閲覧者で同じ文を使う）。</summary>
    internal const string InsertSql =
        """
        INSERT INTO audit_event (id, occurred_at, actor_type, actor_person_id, actor_client_id, on_behalf_of_person_id,
                                 channel, action, target_type, target_id, subject_person_ids, outcome, request_id, actor_account_id)
        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
        """;

    internal static void AddParameters(NpgsqlCommand command, AuditRecord record)
    {
        command.Parameters.Add(new NpgsqlParameter { Value = record.Id });
        command.Parameters.Add(new NpgsqlParameter { Value = record.OccurredAt });
        command.Parameters.Add(new NpgsqlParameter { Value = DbCodes.ToCode(record.ActorType) });
        command.Parameters.Add(Nullable(record.ActorPersonId, NpgsqlDbType.Uuid));
        command.Parameters.Add(Nullable(record.ActorClientId, NpgsqlDbType.Text));
        command.Parameters.Add(Nullable(record.OnBehalfOfPersonId, NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter { Value = DbCodes.ToCode(record.Channel) });
        command.Parameters.Add(new NpgsqlParameter { Value = record.Action });
        command.Parameters.Add(Nullable(record.TargetType, NpgsqlDbType.Text));
        command.Parameters.Add(Nullable(record.TargetId, NpgsqlDbType.Uuid));
        command.Parameters.Add(new NpgsqlParameter { Value = record.SubjectPersonIds.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid });
        command.Parameters.Add(new NpgsqlParameter { Value = DbCodes.ToCode(record.Outcome) });
        command.Parameters.Add(Nullable(record.RequestId, NpgsqlDbType.Text));
        command.Parameters.Add(Nullable(record.ActorAccountId, NpgsqlDbType.Uuid));
    }

    private static NpgsqlParameter Nullable(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };
}
