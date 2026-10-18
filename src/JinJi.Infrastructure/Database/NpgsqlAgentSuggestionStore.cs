using JinJi.Application.Ingest;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// AI エージェントによる要確認の提案への推定（proposal_agent_suggestion。0013）の読み書き。閲覧者を RLS に渡す。
/// </summary>
/// <remarks>
/// 追加できるのはエージェントの閲覧者だけで、委任元は閲覧者本人、委任元は hr_admin かバッチの実行者本人（RLS）。追記専用（UPDATE・DELETE の権限が無い）。
/// </remarks>
public sealed class NpgsqlAgentSuggestionStore : IAgentSuggestionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlAgentSuggestionStore(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task InsertAsync(ViewerContext viewer, AgentSuggestionRecord suggestion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(suggestion);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await using (var command = tx.CreateCommand(
            """
            INSERT INTO proposal_agent_suggestion (id, change_proposal_id, import_batch_id, resolution, person_id, org_unit_id, confidence, rationale,
                                                   suggested_by_person_id, suggested_via_client_id, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = suggestion.Id });
            command.Parameters.Add(new NpgsqlParameter { Value = suggestion.ProposalId });
            command.Parameters.Add(new NpgsqlParameter { Value = suggestion.BatchId });
            command.Parameters.Add(new NpgsqlParameter { Value = Code(suggestion.Resolution) });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)suggestion.PersonId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = (object?)suggestion.OrgUnitId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
            command.Parameters.Add(new NpgsqlParameter { Value = suggestion.Confidence });
            command.Parameters.Add(new NpgsqlParameter { Value = suggestion.Rationale });
            command.Parameters.Add(new NpgsqlParameter { Value = suggestion.SuggestedByPersonId });
            command.Parameters.Add(new NpgsqlParameter { Value = suggestion.SuggestedViaClientId });
            command.Parameters.Add(new NpgsqlParameter { Value = suggestion.CreatedAt });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AgentSuggestionRecord>> ListAsync(ViewerContext viewer, IReadOnlyCollection<Guid> proposalIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(proposalIds);
        if (proposalIds.Count == 0)
        {
            return [];
        }

        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            """
            SELECT id, change_proposal_id, import_batch_id, resolution, person_id, org_unit_id, confidence, rationale,
                   suggested_by_person_id, suggested_via_client_id, created_at
            FROM proposal_agent_suggestion
            WHERE change_proposal_id = ANY ($1)
            ORDER BY created_at, id
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = proposalIds.ToArray() });
        var rows = new List<AgentSuggestionRecord>();
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                rows.Add(new AgentSuggestionRecord(
                    r.GetGuid(0),
                    r.GetGuid(1),
                    r.GetGuid(2),
                    Parse(r.GetString(3)),
                    r.IsDBNull(4) ? null : r.GetGuid(4),
                    r.IsDBNull(5) ? null : r.GetGuid(5),
                    r.GetDecimal(6),
                    r.GetString(7),
                    r.GetGuid(8),
                    r.GetString(9),
                    r.GetFieldValue<DateTimeOffset>(10)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    /// <summary>解決の種類の DB のコード（0013 の CHECK 制約と同じ）。</summary>
    public static string Code(ResolutionChoice choice) => choice switch
    {
        ResolutionChoice.SelectCandidate => "select_candidate",
        ResolutionChoice.RegisterNew => "register_new",
        ResolutionChoice.ReferToCorrection => "refer_to_correction",
        ResolutionChoice.Reject => "reject",
        _ => throw new ArgumentOutOfRangeException(nameof(choice)),
    };

    private static ResolutionChoice Parse(string code) => code switch
    {
        "select_candidate" => ResolutionChoice.SelectCandidate,
        "register_new" => ResolutionChoice.RegisterNew,
        "refer_to_correction" => ResolutionChoice.ReferToCorrection,
        "reject" => ResolutionChoice.Reject,
        _ => throw new InvalidOperationException($"DB に未知の解決の種類があります: {code}"),
    };
}
