using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JinJi.Application.Ingest;
using JinJi.Policy;
using Npgsql;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 取り込みの作業領域の読み書きと、変更提案の反映（<see cref="IIngestStore"/>）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>権限の最終的な線は DB が引く（0005）: 作業領域の参照・追加は hr_admin かバッチの実行者本人（RLS）、jsonb のキーの許可リスト（CHECK 制約）、
/// 承認者 ≠ 起案者（CHECK 制約）、提案の状態遷移と中身の不変（トリガー）、公開側の書き込みは hr_admin だけ（RLS）</item>
/// <item>反映は二つの時間軸の規則に従う: 対象の行（現在記録中で、基準日を有効期間に含む行）の記録期間を閉じ、
/// 訂正なら同じ有効期間で新しい値の行を、変更なら有効日の前を旧値・以後を新値の 2 行を追加する。行の内容は上書きしない</item>
/// <item>差分の旧値（検索用の正規化表記を除く）を対象の行と照合し、一致しなければ反映しない（提案の作成後に変わったデータを上書きしない）</item>
/// <item>反映と提案の状態の更新（applied・承認者・日時）は 1 トランザクション</item>
/// </list>
/// </remarks>
public sealed partial class NpgsqlIngestStore : IIngestStore
{
    private const int BatchSize = 500;

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly Column[] PersonVersionColumns =
    [
        new("person_id", null),
        .. new[]
        {
            "family_name", "given_name", "family_name_kana", "given_name_kana", "family_name_latin", "given_name_latin",
            "display_name", "search_name", "search_name_kana", "work_email", "extension_number", "work_mobile_phone",
            "employment_type", "job_type",
        }.Select(c => new Column(c, "text")),
        new("hire_date", "date"),
        new("termination_date", "date"),
        new("site_id", "uuid"),
        new("remote_work", "boolean"),
    ];

    private static readonly Column[] SiteVersionColumns =
    [
        new("site_id", null),
        new("legal_entity_id", null),
        new("code", "text"),
        new("name", "text"),
        new("site_type", "text"),
        new("prefecture_code", "text"),
        new("location", "text"),
    ];

    private static readonly Column[] OrgUnitVersionColumns =
    [
        new("org_unit_id", null),
        new("legal_entity_id", null),
        new("code", "text"),
        new("name", "text"),
        new("short_name", "text"),
        new("level_key", "text"),
        new("parent_org_unit_id", "uuid"),
        new("head_person_id", "uuid"),
        new("path", "ltree"),
        new("site_id", "uuid"),
    ];

    private static readonly Column[] AssignmentColumns =
    [
        new("person_id", null),
        new("org_unit_id", "uuid"),
        new("assignment_type", null),
        new("position_title", "text"),
        new("secondment_type", null),
        new("personnel_action_id", "uuid"),
        new("supersedes_id", "uuid"),
    ];

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _timeProvider;

    public NpgsqlIngestStore(NpgsqlDataSource dataSource, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dataSource = dataSource;
        _timeProvider = timeProvider;
    }

    // -----------------------------------------------------------------------
    // 作業領域の保存・参照
    // -----------------------------------------------------------------------

    public async Task SaveImportAsync(ViewerContext viewer, ImportWorkspace workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(workspace);
        var b = workspace.Batch;

        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        await ExecuteAsync(
            tx,
            """
            INSERT INTO import_batch (id, source_kind, target_kind, file_name, file_sha256, mapping_name, mapping_sha256, legal_entity_id,
                                      effective_date, executed_by_person_id, executed_via_actor, executed_via_client_id, status, row_count,
                                      dropped_columns, started_at, finished_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, 'completed', $13, $14, $15, $16)
            """,
            cancellationToken,
            P(b.Id),
            P(Code(b.SourceKind)),
            P(Code(b.TargetKind)),
            P(b.FileName),
            P(b.FileSha256),
            P(b.MappingName),
            P(b.MappingSha256),
            P(b.LegalEntityId, NpgsqlDbType.Uuid),
            P(b.EffectiveDate, NpgsqlDbType.Date),
            P(b.ExecutedByPersonId),
            P(DbCodes.ToCode(b.ExecutedViaActor)),
            P(b.ExecutedViaClientId, NpgsqlDbType.Text),
            P(b.RowCount),
            Jsonb(b.DroppedColumns),
            P(b.StartedAt),
            P(b.FinishedAt, NpgsqlDbType.TimestampTz));

        await InsertManyAsync(
            tx,
            "INSERT INTO staging_row (id, import_batch_id, source_row_number, row_sha256, data, expires_at) VALUES ($1, $2, $3, $4, $5, $6)",
            workspace.StagingRows,
            r => [P(r.Id), P(b.Id), P(r.SourceRowNumber), P(r.RowSha256), Jsonb(r.Data), P(r.ExpiresAt)],
            cancellationToken);

        await InsertManyAsync(
            tx,
            "INSERT INTO normalized_row (staging_row_id, import_batch_id, data, issues) VALUES ($1, $2, $3, $4)",
            workspace.NormalizedRows,
            r => [P(r.StagingRowId), P(b.Id), Jsonb(r.Data), Jsonb(r.Issues)],
            cancellationToken);

        await InsertManyAsync(
            tx,
            """
            INSERT INTO match_candidate (id, import_batch_id, staging_row_id, outcome, match_rule, candidate_person_id, confidence)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """,
            workspace.Candidates,
            c => [P(c.Id), P(b.Id), P(c.StagingRowId), P(Code(c.Outcome)), P(c.Rule is { } rule ? Code(rule) : null, NpgsqlDbType.Text), P(c.CandidatePersonId, NpgsqlDbType.Uuid), P(c.Confidence)],
            cancellationToken);

        await InsertManyAsync(
            tx,
            """
            INSERT INTO change_proposal (id, import_batch_id, target_kind, operation, target_id, effective_date, payload, diff, basis,
                                         staging_row_ids, confidence, status, proposed_by_person_id, proposed_via_client_id, proposed_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15)
            """,
            workspace.Proposals,
            p =>
            [
                P(p.Id), P(b.Id), P(Code(p.TargetKind)), P(Code(p.Operation)), P(p.TargetId, NpgsqlDbType.Uuid), P(p.EffectiveDate, NpgsqlDbType.Date),
                Jsonb(p.Payload), Jsonb(p.Diff), Jsonb(p.Basis), P(p.StagingRowIds.ToArray()), P(p.Confidence), P(Code(p.Status)),
                P(p.ProposedByPersonId), P(p.ProposedViaClientId, NpgsqlDbType.Text), P(b.FinishedAt ?? b.StartedAt),
            ],
            cancellationToken);

        await InsertManyAsync(
            tx,
            """
            INSERT INTO correction_request (id, import_batch_id, staging_row_id, source_row_number, field_key, issue, message)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """,
            workspace.Corrections,
            c => [P(c.Id), P(b.Id), P(c.StagingRowId, NpgsqlDbType.Uuid), P(c.SourceRowNumber, NpgsqlDbType.Integer), P(c.FieldKey, NpgsqlDbType.Text), P(Code(c.Issue)), P(c.Message)],
            cancellationToken);

        // 同じ項目マッピング（同じ取込元）の前の取込の未対応の修正依頼は「置き換え」にする（0015）。まだ直っていなければ、この取込が改めて起票している。
        // 閉じられる閲覧者（jin-ji 自身・人事管理者の人）でなければ RLS で 0 行になる（エージェントの取込では置き換えない）
        await ExecuteAsync(
            tx,
            """
            UPDATE correction_request c
            SET status = 'superseded', resolved_at = now(), closed_kind = 'auto'
            FROM import_batch p
            WHERE c.import_batch_id = p.id AND c.status = 'open' AND c.origin = 'ingest'
              AND p.id <> $1 AND p.mapping_name = $2 AND p.target_kind = $3 AND p.legal_entity_id IS NOT DISTINCT FROM $4 AND p.started_at <= $5
            """,
            cancellationToken,
            P(b.Id),
            P(b.MappingName),
            P(Code(b.TargetKind)),
            P(b.LegalEntityId, NpgsqlDbType.Uuid),
            P(b.StartedAt));

        if (workspace.Llm is { } llm)
        {
            // 送った値は記録しない（項目名と件数・回数・トークン数・料金の目安だけ）
            await ExecuteAsync(
                tx,
                """
                INSERT INTO llm_request_log (id, import_batch_id, endpoint_host, model, sent_fields, question_count, call_count, discarded_id_count,
                                             schema_violation_count, failed_count, skipped_count, input_tokens, output_tokens, estimated_cost_usd)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
                """,
                cancellationToken,
                P(Domain.EntityId.New()),
                P(b.Id),
                P(llm.EndpointHost),
                P(llm.Model),
                new NpgsqlParameter { Value = JsonSerializer.Serialize(llm.SentFields), NpgsqlDbType = NpgsqlDbType.Jsonb },
                P(llm.Questions),
                P(llm.Calls),
                P(llm.DiscardedIds),
                P(llm.SchemaViolations),
                P(llm.Failed),
                P(llm.Skipped),
                P(llm.InputTokens),
                P(llm.OutputTokens),
                P(llm.EstimatedCostUsd, NpgsqlDbType.Numeric));
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<ImportBatchRecord?> FindBatchAsync(ViewerContext viewer, Guid batchId, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            """
            SELECT id, source_kind, target_kind, file_name, file_sha256, mapping_name, mapping_sha256, legal_entity_id, effective_date,
                   executed_by_person_id, executed_via_actor, executed_via_client_id, row_count, dropped_columns::text, started_at, finished_at
            FROM import_batch WHERE id = $1
            """);
        command.Parameters.Add(P(batchId));
        ImportBatchRecord? found = null;
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await r.ReadAsync(cancellationToken))
            {
                found = new ImportBatchRecord
                {
                    Id = r.GetGuid(0),
                    SourceKind = Parse<ImportSourceKind>(r.GetString(1)),
                    TargetKind = Parse<ImportTargetKind>(r.GetString(2)),
                    FileName = r.GetString(3),
                    FileSha256 = r.GetString(4),
                    MappingName = r.GetString(5),
                    MappingSha256 = r.GetString(6),
                    LegalEntityId = r.IsDBNull(7) ? null : r.GetGuid(7),
                    EffectiveDate = r.IsDBNull(8) ? null : r.GetFieldValue<DateOnly>(8),
                    ExecutedByPersonId = r.GetGuid(9),
                    ExecutedViaActor = Parse<ActorType>(r.GetString(10)),
                    ExecutedViaClientId = r.IsDBNull(11) ? null : r.GetString(11),
                    RowCount = r.GetInt32(12),
                    DroppedColumns = JsonSerializer.Deserialize<List<DroppedColumn>>(r.GetString(13), Json) ?? [],
                    StartedAt = r.GetFieldValue<DateTimeOffset>(14),
                    FinishedAt = r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
                };
            }
        }

        await tx.CommitAsync(cancellationToken);
        return found;
    }

    public async Task<ChangeProposalRecord?> FindProposalAsync(ViewerContext viewer, Guid proposalId, CancellationToken cancellationToken) =>
        (await QueryProposalsAsync(viewer, "id = $1 AND import_batch_id IS NOT NULL", proposalId, cancellationToken)).SingleOrDefault();

    public Task<IReadOnlyList<ChangeProposalRecord>> ListProposalsAsync(ViewerContext viewer, Guid batchId, CancellationToken cancellationToken) =>
        QueryProposalsAsync(viewer, "import_batch_id = $1", batchId, cancellationToken);

    public async Task<IReadOnlyList<CorrectionRequestRecord>> ListCorrectionsAsync(ViewerContext viewer, Guid batchId, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            """
            SELECT id, staging_row_id, source_row_number, field_key, issue, message
            FROM correction_request WHERE import_batch_id = $1 ORDER BY source_row_number NULLS LAST, id
            """);
        command.Parameters.Add(P(batchId));
        var rows = new List<CorrectionRequestRecord>();
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                rows.Add(new CorrectionRequestRecord(
                    r.GetGuid(0),
                    r.IsDBNull(1) ? null : r.GetGuid(1),
                    r.IsDBNull(2) ? null : r.GetInt32(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    Parse<CorrectionIssue>(r.GetString(4)),
                    r.GetString(5)));
            }
        }

        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<bool> RejectProposalAsync(ViewerContext viewer, Guid proposalId, IReadOnlyCollection<string> scrubFields, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(scrubFields);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        var updated = await ExecuteAsync(
            tx,
            "UPDATE change_proposal SET status = 'rejected', decided_by_person_id = $2, decided_at = $3 WHERE id = $1 AND status IN ('proposed', 'needs_review')",
            cancellationToken,
            P(proposalId),
            P(viewer.EffectivePersonId, NpgsqlDbType.Uuid),
            P(_timeProvider.GetUtcNow()));
        if (updated == 1)
        {
            await ScrubAsync(tx, proposalId, scrubFields, cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return updated == 1;
    }

    public async Task<bool> ResolveProposalAsync(ViewerContext viewer, ProposalResolutionWrite resolution, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(resolution);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        var updated = await ExecuteAsync(
            tx,
            "UPDATE change_proposal SET status = 'resolved', resolution = $2, decided_by_person_id = $3, decided_at = $4 WHERE id = $1 AND status = 'needs_review'",
            cancellationToken,
            P(resolution.ProposalId),
            P(Code(resolution.Resolution)),
            P(viewer.EffectivePersonId, NpgsqlDbType.Uuid),
            P(_timeProvider.GetUtcNow()));
        if (updated != 1)
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        await InsertManyAsync(
            tx,
            """
            INSERT INTO change_proposal (id, import_batch_id, target_kind, operation, target_id, effective_date, payload, diff, basis,
                                         staging_row_ids, confidence, status, proposed_by_person_id, proposed_via_client_id, proposed_at,
                                         resolved_from_proposal_id, resolved_by_person_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, 'proposed', $12, $13, $14, $15, $16)
            """,
            resolution.Derived,
            p =>
            [
                P(p.Id), P(p.BatchId), P(Code(p.TargetKind)), P(Code(p.Operation)), P(p.TargetId, NpgsqlDbType.Uuid), P(p.EffectiveDate, NpgsqlDbType.Date),
                Jsonb(p.Payload), Jsonb(p.Diff), Jsonb(p.Basis), P(p.StagingRowIds.ToArray()), P(p.Confidence),
                P(p.ProposedByPersonId), P(p.ProposedViaClientId, NpgsqlDbType.Text), P(now),
                P(resolution.ProposalId), P(viewer.EffectivePersonId, NpgsqlDbType.Uuid),
            ],
            cancellationToken);

        if (resolution.Correction is { } c)
        {
            await ExecuteAsync(
                tx,
                """
                INSERT INTO correction_request (id, import_batch_id, staging_row_id, source_row_number, field_key, issue, message)
                SELECT $1, import_batch_id, $2, $3, $4, $5, $6 FROM change_proposal WHERE id = $7
                """,
                cancellationToken,
                P(c.Id),
                P(c.StagingRowId, NpgsqlDbType.Uuid),
                P(c.SourceRowNumber, NpgsqlDbType.Integer),
                P(c.FieldKey, NpgsqlDbType.Text),
                P(Code(c.Issue)),
                P(c.Message),
                P(resolution.ProposalId));
        }

        await ScrubAsync(tx, resolution.ProposalId, resolution.ScrubFields, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>提案の根拠の staging の行・正規化結果から項目を消す（キーを減らすだけ。0008 のトリガーが守る）。</summary>
    private static async Task ScrubAsync(ViewerTransaction tx, Guid proposalId, IReadOnlyCollection<string> fields, CancellationToken cancellationToken)
    {
        if (fields.Count == 0)
        {
            return;
        }

        var keys = fields.ToArray();
        await ExecuteAsync(
            tx,
            """
            UPDATE staging_row s SET data = s.data - $2::text[]
            FROM change_proposal p
            WHERE p.id = $1 AND s.id = ANY (p.staging_row_ids) AND s.data ?| $2::text[]
            """,
            cancellationToken,
            P(proposalId),
            P(keys));
        await ExecuteAsync(
            tx,
            """
            UPDATE normalized_row n SET data = n.data - $2::text[]
            FROM change_proposal p
            WHERE p.id = $1 AND n.staging_row_id = ANY (p.staging_row_ids) AND n.data ?| $2::text[]
            """,
            cancellationToken,
            P(proposalId),
            P(keys));
    }

    public async Task<int> PurgeExpiredStagingAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        var deleted = await ExecuteAsync(tx, "DELETE FROM staging_row WHERE expires_at <= now()", cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return deleted;
    }

    // -----------------------------------------------------------------------
    // 反映
    // -----------------------------------------------------------------------

    public async Task<bool> ApplyProposalAsync(ViewerContext viewer, ChangeProposalRecord proposal, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(proposal);

        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken);
        if (!await ApplyInTransactionAsync(tx, viewer, proposal, today, _timeProvider.GetUtcNow(), cancellationToken))
        {
            await tx.Transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// 起案中の提案を、始めたトランザクションの中で公開側へ反映し、反映済み（決めた人は閲覧者の人）にする。起案中でなければ false（何も書かない）。
    /// 取込の承認と直接登録（<see cref="NpgsqlFieldRegistrationStore"/>）が共有する反映の経路（ADR-0013 決定 5）。
    /// </summary>
    internal static async Task<bool> ApplyInTransactionAsync(
        ViewerTransaction tx,
        ViewerContext viewer,
        ChangeProposalRecord proposal,
        DateOnly today,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using (var lockCommand = tx.CreateCommand("SELECT status FROM change_proposal WHERE id = $1 FOR UPDATE"))
        {
            lockCommand.Parameters.Add(P(proposal.Id));
            if (await lockCommand.ExecuteScalarAsync(cancellationToken) is not "proposed")
            {
                return false;
            }
        }

        try
        {
            var target = proposal.TargetId ?? throw new InvalidOperationException($"提案 {proposal.Id} に対象がありません。");
            switch (proposal.TargetKind, proposal.Operation)
            {
                case (ProposalTargetKind.Person, ProposalOperation.Create):
                    await CreatePersonAsync(tx, target, proposal.Payload, cancellationToken);
                    break;
                case (ProposalTargetKind.PersonVersion, ProposalOperation.Correct or ProposalOperation.Change):
                    await ReviseAsync(tx, "person_version", PersonVersionColumns, "person_id = $1", target, proposal, today, new Dictionary<string, string?>(), cancellationToken);
                    break;
                case (ProposalTargetKind.Assignment, ProposalOperation.Create):
                    await CreateAssignmentAsync(tx, target, proposal, today, cancellationToken);
                    break;
                case (ProposalTargetKind.Assignment, ProposalOperation.Correct or ProposalOperation.Change):
                    await ReviseAssignmentAsync(tx, target, proposal, today, cancellationToken);
                    break;
                case (ProposalTargetKind.OrgUnit, ProposalOperation.Create):
                    await CreateOrgUnitAsync(tx, target, proposal.Payload, cancellationToken);
                    break;
                case (ProposalTargetKind.OrgUnitVersion, ProposalOperation.Correct or ProposalOperation.Change):
                    await ReviseOrgUnitAsync(tx, target, proposal, today, cancellationToken);
                    break;
                case (ProposalTargetKind.OrgUnitVersion, ProposalOperation.Abolish):
                    await EndRowsAsync(tx, "org_unit_version", OrgUnitVersionColumns, "org_unit_id = $1", [P(target)], Required(proposal), cancellationToken);
                    break;
                case (ProposalTargetKind.Assignment, ProposalOperation.End):
                    await EndAssignmentsAsync(tx, target, proposal, cancellationToken);
                    break;
                case (ProposalTargetKind.AccountSuspension, ProposalOperation.Create):
                    await StartSuspensionAsync(tx, target, Required(proposal), null, cancellationToken);
                    break;
                case (ProposalTargetKind.PersonnelAction, ProposalOperation.Create):
                    await ApplyActionAsync(tx, target, proposal, cancellationToken);
                    break;
                case (ProposalTargetKind.ExternalIdentity, ProposalOperation.Create):
                    await CreateExternalIdentityAsync(tx, target, proposal.Payload, cancellationToken);
                    break;
                case (ProposalTargetKind.Site, ProposalOperation.Create):
                    await CreateSiteAsync(tx, target, proposal.Payload, cancellationToken);
                    break;
                case (ProposalTargetKind.SiteVersion, ProposalOperation.Correct or ProposalOperation.Change):
                    await ReviseAsync(tx, "site_version", SiteVersionColumns, "site_id = $1", target, proposal, today, new Dictionary<string, string?>(), cancellationToken);
                    break;
                case (ProposalTargetKind.SiteVersion, ProposalOperation.Abolish):
                    await EndRowsAsync(tx, "site_version", SiteVersionColumns, "site_id = $1", [P(target)], Required(proposal), cancellationToken);
                    break;
                default:
                    if (!await ApplyWorkplaceAsync(tx, target, proposal, viewer.EffectivePersonId ?? throw new InvalidOperationException("承認者（人）がいません。"), today, cancellationToken))
                    {
                        throw new InvalidOperationException($"この提案は反映できません: {proposal.TargetKind} / {proposal.Operation}");
                    }

                    break;
            }
        }
        catch (PostgresException ex) when (ex.SqlState.StartsWith("23", StringComparison.Ordinal))
        {
            // 一意・排他・外部キーなどの制約違反（他の変更と衝突した）
            throw new ProposalConflictException($"提案 {proposal.Id} を反映できません（{ex.ConstraintName ?? ex.SqlState}）: {ex.MessageText}", ex);
        }

        await ExecuteAsync(
            tx,
            "UPDATE change_proposal SET status = 'applied', decided_by_person_id = $2, decided_at = $3, applied_at = $3 WHERE id = $1",
            cancellationToken,
            P(proposal.Id),
            P(viewer.EffectivePersonId, NpgsqlDbType.Uuid),
            P(now));
        return true;
    }

    private static async Task CreatePersonAsync(ViewerTransaction tx, Guid personId, IReadOnlyDictionary<string, string?> payload, CancellationToken cancellationToken)
    {
        var hire = Date(Required(payload, ProposalFields.HireDate));
        await ExecuteAsync(
            tx,
            "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, $3)",
            cancellationToken,
            P(personId),
            P(Uuid(Required(payload, ProposalFields.LegalEntityId))),
            P(Required(payload, ProposalFields.EmployeeNumber)));

        var columns = PersonVersionColumns.Skip(1).Select(c => c.Name).ToList();
        var values = PersonVersionColumns.Skip(1)
            .Select((c, i) => c.Name == "remote_work" ? $"coalesce(${i + 3}::boolean, false)" : $"${i + 3}::{c.Type}")
            .ToList();
        var parameters = new List<NpgsqlParameter> { P(Guid.CreateVersion7()), P(personId) };
        parameters.AddRange(columns.Select(c => P(payload.GetValueOrDefault(c), NpgsqlDbType.Text)));
        parameters.Add(P(hire));
        await ExecuteAsync(
            tx,
            $"INSERT INTO person_version (id, person_id, {string.Join(", ", columns)}, valid_period) VALUES ($1, $2, {string.Join(", ", values)}, daterange(${columns.Count + 3}, NULL))",
            cancellationToken,
            [.. parameters]);

        // 退職者は、すべての所属を退職日の翌日で終える
        DateOnly? until = payload.GetValueOrDefault(ProposalFields.TerminationDate) is { } terminated ? Date(terminated).AddDays(1) : null;
        var orgUnit = payload.GetValueOrDefault(ProposalFields.OrgUnitId) is { } org ? Uuid(org) : (Guid?)null;
        var actionId = await InsertActionAsync(tx, personId, "hire", hire, orgUnit, cancellationToken, orgUnit is null ? null : "primary");
        if (orgUnit is { } unit)
        {
            await InsertAssignmentAsync(tx, personId, unit, "primary", payload.GetValueOrDefault(ProposalFields.PositionTitle), "none", actionId, hire, until, cancellationToken);
        }

        if (payload.GetValueOrDefault(ProposalFields.ConcurrentOrgUnitId) is { } concurrent)
        {
            await InsertAssignmentAsync(tx, personId, Uuid(concurrent), "concurrent", payload.GetValueOrDefault(ProposalFields.ConcurrentPositionTitle), "none", null, hire, until, cancellationToken);
        }

        if (payload.GetValueOrDefault(ProposalFields.SecondmentOrgUnitId) is { } secondment)
        {
            var from = payload.GetValueOrDefault(ProposalFields.SecondmentStartDate) is { } start ? Date(start) : hire;
            var secondmentAction = await InsertActionAsync(tx, personId, "secondment_start", from, Uuid(secondment), cancellationToken, "concurrent");
            await InsertAssignmentAsync(tx, personId, Uuid(secondment), "concurrent", null, "outbound", secondmentAction, from, until, cancellationToken);
        }

        if (payload.GetValueOrDefault(ProposalFields.SuspensionStartDate) is { } suspended)
        {
            await StartSuspensionAsync(tx, personId, Date(suspended), null, cancellationToken);
        }
    }

    private static async Task CreateSiteAsync(ViewerTransaction tx, Guid siteId, IReadOnlyDictionary<string, string?> payload, CancellationToken cancellationToken)
    {
        var entity = Uuid(Required(payload, ProposalFields.LegalEntityId));
        await ExecuteAsync(tx, "INSERT INTO site (id, legal_entity_id) VALUES ($1, $2)", cancellationToken, P(siteId), P(entity));
        await ExecuteAsync(
            tx,
            """
            INSERT INTO site_version (id, site_id, legal_entity_id, code, name, site_type, prefecture_code, location, valid_period)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, daterange($9, NULL))
            """,
            cancellationToken,
            P(Guid.CreateVersion7()),
            P(siteId),
            P(entity),
            P(Required(payload, ProposalFields.Code)),
            P(Required(payload, ProposalFields.Name)),
            P(Required(payload, ProposalFields.SiteType)),
            P(payload.GetValueOrDefault(ProposalFields.PrefectureCode), NpgsqlDbType.Text),
            P(payload.GetValueOrDefault(ProposalFields.Location), NpgsqlDbType.Text),
            P(Date(Required(payload, ProposalFields.ValidFrom))));
    }

    private static async Task CreateAssignmentAsync(ViewerTransaction tx, Guid personId, ChangeProposalRecord proposal, DateOnly today, CancellationToken cancellationToken)
    {
        var from = proposal.EffectiveDate ?? today;
        var unit = Uuid(Required(proposal.Payload, ProposalFields.OrgUnitId));
        var type = proposal.Payload.GetValueOrDefault(ProposalFields.AssignmentType) ?? "primary";
        var secondment = proposal.Payload.GetValueOrDefault(ProposalFields.SecondmentType) ?? "none";
        var action = secondment == "outbound" ? "secondment_start" : type == "concurrent" ? "concurrent_start" : "transfer";
        var actionId = await InsertActionAsync(tx, personId, action, from, unit, cancellationToken, type);
        await InsertAssignmentAsync(tx, personId, unit, type, proposal.Payload.GetValueOrDefault(ProposalFields.PositionTitle), secondment, actionId, from, null, cancellationToken);
    }

    private static Task InsertAssignmentAsync(
        ViewerTransaction tx,
        Guid personId,
        Guid unit,
        string type,
        string? title,
        string secondment,
        Guid? actionId,
        DateOnly from,
        DateOnly? until,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            tx,
            """
            INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, position_title, secondment_type, personnel_action_id, valid_period)
            VALUES ($1, $2, $3, $4, $5, $6, $7, daterange($8, $9))
            """,
            cancellationToken,
            P(Guid.CreateVersion7()),
            P(personId),
            P(unit),
            P(type),
            P(title, NpgsqlDbType.Text),
            P(secondment),
            P(actionId, NpgsqlDbType.Uuid),
            P(from),
            P(until is { } u && u > from ? u : until is null ? null : from.AddDays(1), NpgsqlDbType.Date));

    private static async Task ReviseAssignmentAsync(ViewerTransaction tx, Guid personId, ChangeProposalRecord proposal, DateOnly today, CancellationToken cancellationToken)
    {
        var extra = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (proposal.Operation == ProposalOperation.Change)
        {
            // 変更（有効日のある異動・役職の変更）は発令として記録し、新しい所属の行を発令に結び付ける
            var orgChanged = proposal.Diff.TryGetValue(ProposalFields.OrgUnitId, out var org);
            var date = proposal.EffectiveDate!.Value;
            var unit = orgChanged ? Uuid(org!.New!) : (Guid?)null;
            var actionId = await InsertActionAsync(tx, personId, orgChanged ? "transfer" : "position_appointment", date, unit, cancellationToken);
            extra["personnel_action_id"] = actionId.ToString("D");
        }

        await ReviseAsync(tx, "assignment", AssignmentColumns, "person_id = $1 AND assignment_type = 'primary'", personId, proposal, today, extra, cancellationToken, supersedes: true);
    }

    private static async Task CreateOrgUnitAsync(ViewerTransaction tx, Guid unitId, IReadOnlyDictionary<string, string?> payload, CancellationToken cancellationToken)
    {
        var entity = Uuid(Required(payload, ProposalFields.LegalEntityId));
        var from = Date(Required(payload, ProposalFields.ValidFrom));
        var parent = payload.GetValueOrDefault(ProposalFields.ParentOrgUnitId) is { } p ? Uuid(p) : (Guid?)null;
        var path = await PathAsync(tx, unitId, parent, from, cancellationToken);

        await ExecuteAsync(tx, "INSERT INTO org_unit (id, legal_entity_id) VALUES ($1, $2)", cancellationToken, P(unitId), P(entity));
        await ExecuteAsync(
            tx,
            """
            INSERT INTO org_unit_version (id, org_unit_id, legal_entity_id, code, name, short_name, level_key, parent_org_unit_id, head_person_id, path, valid_period, site_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10::ltree, daterange($11, NULL), $12)
            """,
            cancellationToken,
            P(Guid.CreateVersion7()),
            P(unitId),
            P(entity),
            P(Required(payload, ProposalFields.Code)),
            P(Required(payload, ProposalFields.Name)),
            P(payload.GetValueOrDefault(ProposalFields.ShortName), NpgsqlDbType.Text),
            P(payload.GetValueOrDefault(ProposalFields.LevelKey), NpgsqlDbType.Text),
            P(parent, NpgsqlDbType.Uuid),
            P(payload.GetValueOrDefault(ProposalFields.HeadPersonId) is { } h ? Uuid(h) : null, NpgsqlDbType.Uuid),
            P(path),
            P(from),
            P(payload.GetValueOrDefault(ProposalFields.SiteId) is { } site ? Uuid(site) : null, NpgsqlDbType.Uuid));
    }

    private static async Task ReviseOrgUnitAsync(ViewerTransaction tx, Guid unitId, ChangeProposalRecord proposal, DateOnly today, CancellationToken cancellationToken)
    {
        var extra = new Dictionary<string, string?>(StringComparer.Ordinal);
        var date = proposal.Operation == ProposalOperation.Change ? proposal.EffectiveDate!.Value : today;
        string? oldPath = null;
        if (proposal.Diff.TryGetValue(ProposalFields.ParentOrgUnitId, out var parent))
        {
            // 上位組織が変わったら、新しい版の ltree パスを作り直し、配下の組織の版のパスも同じ日から作り直す
            oldPath = await CurrentPathAsync(tx, unitId, date, cancellationToken);
            extra["path"] = await PathAsync(tx, unitId, parent.New is { } p ? Uuid(p) : null, date, cancellationToken);
        }

        await ReviseAsync(tx, "org_unit_version", OrgUnitVersionColumns, "org_unit_id = $1", unitId, proposal, today, extra, cancellationToken);
        if (oldPath is not null && extra["path"] is { } newPath && newPath != oldPath)
        {
            await RebaseDescendantsAsync(tx, unitId, oldPath, newPath, date, proposal.Operation == ProposalOperation.Change, cancellationToken);
        }
    }

    /// <summary>
    /// 二つの時間軸の行の訂正・変更。対象の行（現在記録中で基準日を有効期間に含む行）の旧値を照合し、記録期間を閉じて新しい行を追加する。
    /// </summary>
    private static async Task ReviseAsync(
        ViewerTransaction tx,
        string table,
        Column[] columns,
        string where,
        Guid targetId,
        ChangeProposalRecord proposal,
        DateOnly today,
        Dictionary<string, string?> extra,
        CancellationToken cancellationToken,
        bool supersedes = false)
    {
        var date = proposal.Operation == ProposalOperation.Change ? proposal.EffectiveDate ?? throw new InvalidOperationException("変更の提案に有効日がありません。") : today;

        Guid rowId;
        JsonElement row;
        DateOnly from;
        DateOnly? until;
        await using (var select = tx.CreateCommand(
            $"""
            SELECT id, to_jsonb(t)::text, lower(valid_period), upper(valid_period) FROM {table} t
            WHERE {where} AND upper_inf(recorded_period) AND valid_period @> $2::date
            FOR UPDATE
            """))
        {
            select.Parameters.Add(P(targetId));
            select.Parameters.Add(P(date));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new ProposalConflictException($"{date:yyyy-MM-dd} に有効な {table} の行がありません（対象 {targetId}）。");
            }

            rowId = reader.GetGuid(0);
            row = JsonDocument.Parse(reader.GetString(1)).RootElement.Clone();
            from = reader.GetFieldValue<DateOnly>(2);
            until = reader.IsDBNull(3) ? null : reader.GetFieldValue<DateOnly>(3);
            if (await reader.ReadAsync(cancellationToken))
            {
                throw new ProposalConflictException($"{date:yyyy-MM-dd} に有効な {table} の行が一意に決まりません（対象 {targetId}）。");
            }
        }

        foreach (var (key, change) in proposal.Diff)
        {
            if (ProposalFields.Derived.Contains(key))
            {
                continue;
            }

            var current = row.TryGetProperty(key, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.Null => null,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Number => value.GetRawText(),
                    _ => value.GetString(),
                }
                : null;
            if (!string.Equals(current, change.Old, StringComparison.Ordinal))
            {
                throw new ProposalConflictException($"提案の作成後に {table}.{key} が変わっています（対象 {targetId}）。取り込み直してください。");
            }
        }

        var overrides = proposal.Diff.ToDictionary(d => d.Key, d => d.Value.New, StringComparer.Ordinal);
        foreach (var (key, value) in extra)
        {
            overrides[key] = value;
        }

        if (supersedes)
        {
            overrides["supersedes_id"] = rowId.ToString("D");
        }

        await ExecuteAsync(tx, $"UPDATE {table} SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE id = $1", cancellationToken, P(rowId));

        if (proposal.Operation == ProposalOperation.Change && date > from)
        {
            // 有効日の前は旧値のまま（系譜は旧行）、有効日以後は新値
            var before = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (supersedes)
            {
                before["supersedes_id"] = rowId.ToString("D");
            }

            await CopyAsync(tx, table, columns, rowId, from, date, before, cancellationToken);
            await CopyAsync(tx, table, columns, rowId, date, until, overrides, cancellationToken);
        }
        else
        {
            await CopyAsync(tx, table, columns, rowId, from, until, overrides, cancellationToken);
        }
    }

    /// <summary>行を写して新しい行を追加する（有効期間を指定し、列の値を上書きする）。</summary>
    private static Task CopyAsync(
        ViewerTransaction tx,
        string table,
        Column[] columns,
        Guid sourceId,
        DateOnly from,
        DateOnly? until,
        IReadOnlyDictionary<string, string?> overrides,
        CancellationToken cancellationToken)
    {
        var unknown = overrides.Keys.Where(k => !columns.Any(c => c.Name == k && c.Type is not null)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException($"{table} で変えられない列です: {string.Join(", ", unknown)}");
        }

        var expressions = columns.Select(c => c.Type is null
            ? c.Name
            : $"CASE WHEN $5::jsonb ? '{c.Name}' THEN ($5::jsonb ->> '{c.Name}')::{c.Type} ELSE {c.Name} END");
        var sql = $"""
            INSERT INTO {table} (id, {string.Join(", ", columns.Select(c => c.Name))}, valid_period)
            SELECT $1, {string.Join(", ", expressions)}, daterange($3, $4) FROM {table} WHERE id = $2
            """;
        return ExecuteAsync(tx, sql, cancellationToken, P(Guid.CreateVersion7()), P(sourceId), P(from), P(until, NpgsqlDbType.Date), Jsonb(overrides));
    }

    private static async Task<Guid> InsertActionAsync(
        ViewerTransaction tx,
        Guid personId,
        string type,
        DateOnly date,
        Guid? unit,
        CancellationToken cancellationToken,
        string? assignmentType = "primary",
        string? positionTitle = null)
    {
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            tx,
            "INSERT INTO personnel_action (id, person_id, action_type, effective_date, org_unit_id, assignment_type, position_title) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            cancellationToken,
            P(id),
            P(personId),
            P(type),
            P(date),
            P(unit, NpgsqlDbType.Uuid),
            P(assignmentType, NpgsqlDbType.Text),
            P(positionTitle, NpgsqlDbType.Text));
        return id;
    }

    /// <summary>組織の ltree パス（上位組織のその日のパス + 自組織）。上位組織がその日に無ければ反映しない。</summary>
    private static async Task<string> PathAsync(ViewerTransaction tx, Guid unitId, Guid? parent, DateOnly date, CancellationToken cancellationToken)
    {
        var self = unitId.ToString("N");
        if (parent is not { } parentId)
        {
            return self;
        }

        await using var command = tx.CreateCommand(
            "SELECT path::text FROM org_unit_version WHERE org_unit_id = $1 AND upper_inf(recorded_period) AND valid_period @> $2::date");
        command.Parameters.Add(P(parentId));
        command.Parameters.Add(P(date));
        return await command.ExecuteScalarAsync(cancellationToken) is string parentPath
            ? $"{parentPath}.{self}"
            : throw new ProposalConflictException($"{date:yyyy-MM-dd} に有効な上位組織 {parentId} がありません（新しい上位組織なら、その提案を先に承認してください）。");
    }

    // -----------------------------------------------------------------------
    // 共通
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<ChangeProposalRecord>> QueryProposalsAsync(ViewerContext viewer, string where, Guid id, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, readOnly: true);
        await using var command = tx.CreateCommand(
            $"""
            SELECT id, import_batch_id, target_kind, operation, target_id, effective_date, payload::text, diff::text, basis::text,
                   staging_row_ids, confidence, status, proposed_by_person_id, proposed_via_client_id, decided_by_person_id,
                   resolved_from_proposal_id, resolved_by_person_id, resolution
            FROM change_proposal WHERE {where} ORDER BY id
            """);
        command.Parameters.Add(P(id));
        var rows = new List<ChangeProposalRecord>();
        await using (var r = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await r.ReadAsync(cancellationToken))
            {
                rows.Add(new ChangeProposalRecord
                {
                    Id = r.GetGuid(0),
                    BatchId = r.GetGuid(1),
                    TargetKind = Parse<ProposalTargetKind>(r.GetString(2)),
                    Operation = Parse<ProposalOperation>(r.GetString(3)),
                    TargetId = r.IsDBNull(4) ? null : r.GetGuid(4),
                    EffectiveDate = r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5),
                    Payload = JsonSerializer.Deserialize<Dictionary<string, string?>>(r.GetString(6), Json) ?? new Dictionary<string, string?>(),
                    Diff = JsonSerializer.Deserialize<Dictionary<string, FieldChange>>(r.GetString(7), Json) ?? new Dictionary<string, FieldChange>(),
                    Basis = JsonSerializer.Deserialize<ProposalBasis>(r.GetString(8), Json) ?? new ProposalBasis(),
                    StagingRowIds = r.GetFieldValue<Guid[]>(9),
                    Confidence = r.GetDecimal(10),
                    Status = Parse<ProposalStatus>(r.GetString(11)),
                    ProposedByPersonId = r.GetGuid(12),
                    ProposedViaClientId = r.IsDBNull(13) ? null : r.GetString(13),
                    DecidedByPersonId = r.IsDBNull(14) ? null : r.GetGuid(14),
                    ResolvedFromProposalId = r.IsDBNull(15) ? null : r.GetGuid(15),
                    ResolvedByPersonId = r.IsDBNull(16) ? null : r.GetGuid(16),
                    Resolution = r.IsDBNull(17) ? null : Parse<ProposalResolution>(r.GetString(17)),
                });
            }
        }

        await tx.CommitAsync(cancellationToken);
        return rows;
    }

    private static async Task InsertManyAsync<T>(
        ViewerTransaction tx,
        string sql,
        IReadOnlyList<T> items,
        Func<T, NpgsqlParameter[]> parameters,
        CancellationToken cancellationToken)
    {
        foreach (var chunk in items.Chunk(BatchSize))
        {
            await using var batch = new NpgsqlBatch(tx.Connection, tx.Transaction);
            foreach (var item in chunk)
            {
                var command = new NpgsqlBatchCommand(sql);
                command.Parameters.AddRange(parameters(item));
                batch.BatchCommands.Add(command);
            }

            await batch.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<int> ExecuteAsync(ViewerTransaction tx, string sql, CancellationToken cancellationToken, params NpgsqlParameter[] parameters)
    {
        await using var command = tx.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlParameter P(object value) => new() { Value = value };

    private static NpgsqlParameter P(object? value, NpgsqlDbType type) => new() { Value = value ?? DBNull.Value, NpgsqlDbType = type };

    private static NpgsqlParameter Jsonb(object value) =>
        new() { Value = JsonSerializer.Serialize(value, Json), NpgsqlDbType = NpgsqlDbType.Jsonb };

    private static string Required(IReadOnlyDictionary<string, string?> payload, string key) =>
        payload.GetValueOrDefault(key) ?? throw new InvalidOperationException($"提案に {key} がありません。");

    private static Guid Uuid(string value) => Guid.Parse(value, CultureInfo.InvariantCulture);

    private static DateOnly Date(string value) => DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>列挙値の DB のコード（snake_case）。</summary>
    internal static string Code<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    internal static T Parse<T>(string code)
        where T : struct, Enum
    {
        foreach (var value in Enum.GetValues<T>())
        {
            if (Code(value) == code)
            {
                return value;
            }
        }

        throw new InvalidOperationException($"DB に未知の {typeof(T).Name} があります: {code}");
    }

    /// <summary>行を写すときの列（Type が null の列は旧行の値をそのまま使い、上書きできない）。</summary>
    private sealed record Column(string Name, string? Type);
}
