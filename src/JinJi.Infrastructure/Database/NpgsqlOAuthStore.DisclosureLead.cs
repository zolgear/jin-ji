using JinJi.Application.Auth;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>先行提供の日数の起案と承認（api_client_disclosure_lead。0016）。</summary>
/// <remarks>
/// 閲覧者は管理 CLI（jin-ji 自身。起案だけ）か人事管理者の人（起案・承認・却下。本人として記録）。起案者 ≠ 承認者は CHECK 制約、
/// 状態の遷移と中身の不変はトリガー、クライアントごとに有効な行・起案中の行が高々 1 つは部分一意索引で守る。
/// </remarks>
public sealed partial class NpgsqlOAuthStore : IDisclosureLeadStore
{
    private const string LeadColumns =
        "id, api_client_id, lead_days, status, note, proposed_by_kind, proposed_by_person_id, proposed_at, decided_by_person_id, decided_at, superseded_at";

    public async Task<IReadOnlyList<DisclosureLead>> ListDisclosureLeadsAsync(AuthActor actor, Guid? clientRef, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            $"SELECT {LeadColumns} FROM api_client_disclosure_lead WHERE ($1::uuid IS NULL OR api_client_id = $1) ORDER BY proposed_at DESC, id DESC");
        command.Parameters.Add(Nullable(clientRef, NpgsqlDbType.Uuid));
        var leads = new List<DisclosureLead>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                leads.Add(ReadLead(reader));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return leads;
    }

    public async Task<DisclosureLead?> FindDisclosureLeadAsync(AuthActor actor, Guid id, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand($"SELECT {LeadColumns} FROM api_client_disclosure_lead WHERE id = $1");
        command.Parameters.Add(P(id));
        DisclosureLead? lead = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                lead = ReadLead(reader);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return lead;
    }

    public async Task<bool> InsertDisclosureLeadAsync(AuthActor actor, DisclosureLead proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        await using var tx = await BeginAsync(actor, cancellationToken);
        await using var command = tx.CreateCommand(
            """
            INSERT INTO api_client_disclosure_lead (id, api_client_id, lead_days, status, note, proposed_by_kind, proposed_by_person_id, proposed_at)
            VALUES ($1, $2, $3, 'proposed', $4, $5, $6, $7)
            ON CONFLICT (api_client_id) WHERE status = 'proposed' DO NOTHING
            """);
        AddAll(command,
        [
            P(proposal.Id), P(proposal.ClientRef), P(proposal.LeadDays), Nullable(proposal.Note, NpgsqlDbType.Text),
            P(proposal.ProposedByKind), P(proposal.ProposedBy), P(proposal.ProposedAt),
        ]);
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        await tx.CommitAsync(cancellationToken);
        return inserted;
    }

    public async Task<bool> DecideDisclosureLeadAsync(AuthActor actor, Guid id, bool approve, DateTimeOffset decidedAt, CancellationToken cancellationToken)
    {
        await using var tx = await BeginAsync(actor, cancellationToken);
        if (approve)
        {
            // 置き換え → 承認の順（クライアントごとに承認済みは高々 1 つ。部分一意索引は文ごとに検査する）
            await using var supersede = tx.CreateCommand(
                """
                UPDATE api_client_disclosure_lead SET status = 'superseded', superseded_at = $2
                WHERE status = 'approved' AND api_client_id = (SELECT api_client_id FROM api_client_disclosure_lead WHERE id = $1 AND status = 'proposed')
                """);
            AddAll(supersede, [P(id), P(decidedAt)]);
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var decide = tx.CreateCommand(
            """
            UPDATE api_client_disclosure_lead SET status = $2, decided_by_person_id = jinji_viewer_person_id(), decided_at = $3
            WHERE id = $1 AND status = 'proposed'
            """);
        AddAll(decide, [P(id), P(approve ? "approved" : "rejected"), P(decidedAt)]);
        if (await decide.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    private static DisclosureLead ReadLead(NpgsqlDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetInt32(2),
        reader.GetString(3) switch
        {
            "proposed" => DisclosureLeadStatus.Proposed,
            "approved" => DisclosureLeadStatus.Approved,
            "rejected" => DisclosureLeadStatus.Rejected,
            "superseded" => DisclosureLeadStatus.Superseded,
            var other => throw new InvalidOperationException($"DB に未知の先行提供の状態があります: {other}"),
        },
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.GetString(5),
        reader.GetGuid(6),
        reader.GetFieldValue<DateTimeOffset>(7),
        reader.IsDBNull(8) ? null : reader.GetGuid(8),
        reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
        reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10));
}
