using JinJi.Application.Ingest;
using Npgsql;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 発令・一時停止・所属の終了・組織の廃止・外部 ID の対応の反映。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>発令は、発令の行を追加したうえで、<b>承認時の</b>所属・一時停止から版を導出する（所属や一時停止を直接は指定しない。overview §4.3）。
/// 既にその状態なら版は変えない（人事パッケージの現在値と辞令が重複しても二重にならない）</item>
/// <item>「終える」は、有効日の前日までに有効期間を縮める: 有効日を含む行は記録期間を閉じて [開始, 有効日) の行を追加し、有効日以後に始まる行は記録期間を閉じるだけ</item>
/// </list>
/// </remarks>
public sealed partial class NpgsqlIngestStore
{
    private static readonly Column[] AccountSuspensionColumns =
    [
        new("person_id", null),
        new("personnel_action_id", "uuid"),
        new("supersedes_id", "uuid"),
    ];

    private static DateOnly Required(ChangeProposalRecord proposal) =>
        proposal.EffectiveDate ?? throw new InvalidOperationException($"提案 {proposal.Id} に有効日がありません。");

    /// <summary>発令を反映する（発令の行の追加と、承認時の状態からの版の導出）。</summary>
    private static async Task ApplyActionAsync(ViewerTransaction tx, Guid personId, ChangeProposalRecord proposal, CancellationToken cancellationToken)
    {
        var date = Required(proposal);
        var payload = proposal.Payload;
        var type = Required(payload, ProposalFields.ActionType);
        var org = payload.GetValueOrDefault(ProposalFields.OrgUnitId) is { } o ? Uuid(o) : (Guid?)null;
        var title = payload.GetValueOrDefault(ProposalFields.PositionTitle);
        var actionId = await InsertActionAsync(tx, personId, type, date, org, cancellationToken, payload.GetValueOrDefault(ProposalFields.AssignmentType), title);

        switch (type)
        {
            case "transfer" or "hire" or "position_appointment":
                await MovePrimaryAsync(tx, personId, org, title, date, actionId, cancellationToken);
                break;
            case "concurrent_start" or "secondment_start":
                var secondment = type == "secondment_start" ? "outbound" : "none";
                if (org is { } unit && !await HasAssignmentAsync(tx, personId, unit, date, cancellationToken))
                {
                    await InsertAssignmentAsync(tx, personId, unit, "concurrent", title, secondment, actionId, date, null, cancellationToken);
                }

                break;
            case "concurrent_end":
                await EndRowsAsync(
                    tx,
                    "assignment",
                    AssignmentColumns,
                    "person_id = $1 AND assignment_type = 'concurrent' AND secondment_type = 'none'" + (org is null ? string.Empty : " AND org_unit_id = $2"),
                    org is { } endUnit ? [P(personId), P(endUnit)] : [P(personId)],
                    date,
                    cancellationToken);
                break;
            case "secondment_end":
                await EndRowsAsync(tx, "assignment", AssignmentColumns, "person_id = $1 AND secondment_type = 'outbound'", [P(personId)], date, cancellationToken);
                break;
            case "suspension_start":
                await StartSuspensionAsync(tx, personId, date, actionId, cancellationToken);
                break;
            case "suspension_end":
                await EndRowsAsync(tx, "account_suspension", AccountSuspensionColumns, "person_id = $1", [P(personId)], date, cancellationToken);
                break;
            case "termination":
                await EndRowsAsync(tx, "assignment", AssignmentColumns, "person_id = $1", [P(personId)], date, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"反映できない発令の種類です: {type}");
        }
    }

    /// <summary>主務の所属を、有効日から指定の組織・役職にする（既にそうなら何もしない）。</summary>
    private static async Task MovePrimaryAsync(ViewerTransaction tx, Guid personId, Guid? org, string? title, DateOnly date, Guid actionId, CancellationToken cancellationToken)
    {
        Guid? rowId = null;
        Guid currentOrg = default;
        string? currentTitle = null;
        DateOnly from = default;
        DateOnly? until = null;
        await using (var select = tx.CreateCommand(
            """
            SELECT id, org_unit_id, position_title, lower(valid_period), upper(valid_period) FROM assignment
            WHERE person_id = $1 AND assignment_type = 'primary' AND upper_inf(recorded_period) AND valid_period @> $2::date
            FOR UPDATE
            """))
        {
            select.Parameters.Add(P(personId));
            select.Parameters.Add(P(date));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                rowId = reader.GetGuid(0);
                currentOrg = reader.GetGuid(1);
                currentTitle = reader.IsDBNull(2) ? null : reader.GetString(2);
                from = reader.GetFieldValue<DateOnly>(3);
                until = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateOnly>(4);
            }
        }

        if (rowId is not { } id)
        {
            if (org is { } unit)
            {
                await InsertAssignmentAsync(tx, personId, unit, "primary", title, "none", actionId, date, null, cancellationToken);
            }

            return;
        }

        var newOrg = org ?? currentOrg;
        var newTitle = title ?? (newOrg == currentOrg ? currentTitle : null);
        if (newOrg == currentOrg && newTitle == currentTitle)
        {
            return;
        }

        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["org_unit_id"] = newOrg.ToString("D"),
            ["position_title"] = newTitle,
            ["personnel_action_id"] = actionId.ToString("D"),
            ["supersedes_id"] = id.ToString("D"),
        };
        await SplitAsync(tx, "assignment", AssignmentColumns, id, from, until, date, overrides, new Dictionary<string, string?> { ["supersedes_id"] = id.ToString("D") }, cancellationToken);
    }

    private static async Task<bool> HasAssignmentAsync(ViewerTransaction tx, Guid personId, Guid unit, DateOnly date, CancellationToken cancellationToken)
    {
        await using var command = tx.CreateCommand(
            "SELECT count(*) FROM assignment WHERE person_id = $1 AND org_unit_id = $2 AND upper_inf(recorded_period) AND valid_period @> $3::date");
        command.Parameters.Add(P(personId));
        command.Parameters.Add(P(unit));
        command.Parameters.Add(P(date));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))! > 0;
    }

    /// <summary>一時停止を有効日から始める（既に一時停止中なら何もしない）。</summary>
    private static async Task StartSuspensionAsync(ViewerTransaction tx, Guid personId, DateOnly date, Guid? actionId, CancellationToken cancellationToken)
    {
        await using (var command = tx.CreateCommand(
            "SELECT count(*) FROM account_suspension WHERE person_id = $1 AND upper_inf(recorded_period) AND valid_period @> $2::date"))
        {
            command.Parameters.Add(P(personId));
            command.Parameters.Add(P(date));
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! > 0)
            {
                return;
            }
        }

        actionId ??= await InsertActionAsync(tx, personId, "suspension_start", date, null, cancellationToken, null);
        await ExecuteAsync(
            tx,
            "INSERT INTO account_suspension (id, person_id, personnel_action_id, valid_period) VALUES ($1, $2, $3, daterange($4, NULL))",
            cancellationToken,
            P(Guid.CreateVersion7()),
            P(personId),
            P(actionId.Value),
            P(date));
    }

    /// <summary>所属を終える（退職は全所属、それ以外は指定の組織か全所属）。</summary>
    private static async Task EndAssignmentsAsync(ViewerTransaction tx, Guid personId, ChangeProposalRecord proposal, CancellationToken cancellationToken)
    {
        var date = Required(proposal);
        if (proposal.Payload.GetValueOrDefault(ProposalFields.ActionType) is { } action)
        {
            await InsertActionAsync(tx, personId, action, date, null, cancellationToken, null);
        }

        if (proposal.Payload.GetValueOrDefault(ProposalFields.OrgUnitId) is { } org)
        {
            await EndRowsAsync(tx, "assignment", AssignmentColumns, "person_id = $1 AND org_unit_id = $2", [P(personId), P(Uuid(org))], date, cancellationToken);
        }
        else
        {
            await EndRowsAsync(tx, "assignment", AssignmentColumns, "person_id = $1", [P(personId)], date, cancellationToken);
        }
    }

    /// <summary>
    /// 条件に当たる現在記録中の行を、有効日の前日までで終える（有効日を含む行は [開始, 有効日) に縮め、有効日以後に始まる行は取り消す）。
    /// </summary>
    private static async Task EndRowsAsync(
        ViewerTransaction tx,
        string table,
        Column[] columns,
        string where,
        NpgsqlParameter[] parameters,
        DateOnly date,
        CancellationToken cancellationToken)
    {
        var rows = new List<(Guid Id, DateOnly From)>();
        await using (var select = tx.CreateCommand(
            $"""
            SELECT id, lower(valid_period) FROM {table}
            WHERE {where} AND upper_inf(recorded_period) AND (upper_inf(valid_period) OR upper(valid_period) > ${parameters.Length + 1}::date)
            FOR UPDATE
            """))
        {
            select.Parameters.AddRange(parameters);
            select.Parameters.Add(P(date));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1)));
            }
        }

        var lineage = columns.Any(c => c.Name == "supersedes_id");
        foreach (var (id, from) in rows)
        {
            await ExecuteAsync(tx, $"UPDATE {table} SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE id = $1", cancellationToken, P(id));
            if (from < date)
            {
                var overrides = lineage ? new Dictionary<string, string?> { ["supersedes_id"] = id.ToString("D") } : new Dictionary<string, string?>();
                await CopyAsync(tx, table, columns, id, from, date, overrides, cancellationToken);
            }
        }
    }

    /// <summary>行を有効日で分ける: 記録期間を閉じ、[開始, 有効日) を before の上書きで、[有効日, 終了) を after の上書きで追加する。</summary>
    private static async Task SplitAsync(
        ViewerTransaction tx,
        string table,
        Column[] columns,
        Guid rowId,
        DateOnly from,
        DateOnly? until,
        DateOnly date,
        IReadOnlyDictionary<string, string?> after,
        IReadOnlyDictionary<string, string?> before,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(tx, $"UPDATE {table} SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE id = $1", cancellationToken, P(rowId));
        if (date > from)
        {
            await CopyAsync(tx, table, columns, rowId, from, date, before, cancellationToken);
            await CopyAsync(tx, table, columns, rowId, date, until, after, cancellationToken);
        }
        else
        {
            await CopyAsync(tx, table, columns, rowId, from, until, after, cancellationToken);
        }
    }

    /// <summary>外部 ID の対応を追加する。既に同じ人に対応していれば何もしない。別の人に対応していれば反映しない。</summary>
    private static async Task CreateExternalIdentityAsync(ViewerTransaction tx, Guid personId, IReadOnlyDictionary<string, string?> payload, CancellationToken cancellationToken)
    {
        var system = Required(payload, ProposalFields.SourceSystem);
        var externalId = Required(payload, ProposalFields.ExternalId);
        await ExecuteAsync(
            tx,
            "INSERT INTO external_identity (id, person_id, source_system, external_id) VALUES ($1, $2, $3, $4) ON CONFLICT (source_system, external_id) DO NOTHING",
            cancellationToken,
            P(Guid.CreateVersion7()),
            P(personId),
            P(system),
            P(externalId));

        await using var command = tx.CreateCommand("SELECT person_id FROM external_identity WHERE source_system = $1 AND external_id = $2");
        command.Parameters.Add(P(system));
        command.Parameters.Add(P(externalId));
        if (await command.ExecuteScalarAsync(cancellationToken) is Guid existing && existing != personId)
        {
            throw new ProposalConflictException($"外部 ID は既に別の人に対応しています（{system}）。");
        }
    }

    /// <summary>上位組織の変更で、配下の組織の版の ltree パスを作り直す（有効日から。訂正なら同じ有効期間で置き換える）。</summary>
    private static async Task RebaseDescendantsAsync(
        ViewerTransaction tx,
        Guid unitId,
        string oldPath,
        string newPath,
        DateOnly date,
        bool split,
        CancellationToken cancellationToken)
    {
        var rows = new List<(Guid Id, string Path, DateOnly From, DateOnly? Until)>();
        await using (var select = tx.CreateCommand(
            """
            SELECT id, path::text, lower(valid_period), upper(valid_period) FROM org_unit_version
            WHERE path <@ $1::ltree AND org_unit_id <> $2 AND upper_inf(recorded_period) AND valid_period @> $3::date
            FOR UPDATE
            """))
        {
            select.Parameters.Add(P(oldPath));
            select.Parameters.Add(P(unitId));
            select.Parameters.Add(P(date));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetGuid(0), reader.GetString(1), reader.GetFieldValue<DateOnly>(2), reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3)));
            }
        }

        foreach (var row in rows)
        {
            var rebased = newPath + row.Path[oldPath.Length..];
            var after = new Dictionary<string, string?> { ["path"] = rebased };
            await SplitAsync(tx, "org_unit_version", OrgUnitVersionColumns, row.Id, row.From, row.Until, split ? date : row.From, after, new Dictionary<string, string?>(), cancellationToken);
        }
    }

    /// <summary>組織の現在の版の ltree パス（基準日を含む行）。</summary>
    private static async Task<string?> CurrentPathAsync(ViewerTransaction tx, Guid unitId, DateOnly date, CancellationToken cancellationToken)
    {
        await using var command = tx.CreateCommand(
            "SELECT path::text FROM org_unit_version WHERE org_unit_id = $1 AND upper_inf(recorded_period) AND valid_period @> $2::date");
        command.Parameters.Add(P(unitId));
        command.Parameters.Add(P(date));
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }
}
