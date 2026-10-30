using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Application.FieldManagement;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Ingest.Normalization;
using JinJi.Ingest.Suggestions;
using JinJi.Policy;

namespace JinJi.Ingest;

/// <summary>取込の要求（コネクタで読み、項目マッピングを通した後のもの）。</summary>
public sealed record ImportRequest
{
    public required MappedTable Table { get; init; }

    public required ImportProfile Profile { get; init; }

    public required string FileName { get; init; }

    /// <summary>取込元のファイルの SHA-256（小文字の 16 進）。</summary>
    public required string FileSha256 { get; init; }

    public required string MappingName { get; init; }

    /// <summary>項目マッピング（JSON）の SHA-256（小文字の 16 進）。</summary>
    public required string MappingSha256 { get; init; }

    /// <summary>取込の実行者。閲覧者が jin-ji 自身（ワーカー・CLI）のときは必須。人・AI エージェントのときは閲覧者（委任元）と同じでなければならない。</summary>
    public Guid? ExecutedByPersonId { get; init; }

    /// <summary>変更の有効日。null なら差分を訂正として提案する。</summary>
    public DateOnly? EffectiveDate { get; init; }

    /// <summary>
    /// 要確認の提案に LLM による候補提示を行うか（取込ごとのオプトイン。既定 false）。true なのに LLM が設定されていなければ取込は失敗する。
    /// </summary>
    public bool UseLlm { get; init; }
}

/// <summary>取込の結果（取込結果に記録したものの要約）。</summary>
public sealed record ImportResult(
    Guid BatchId,
    int RowCount,
    IReadOnlyList<DroppedColumn> DroppedColumns,
    IReadOnlyList<string> MissingFields,
    int ProposalCount,
    int NeedsReviewCount,
    int CorrectionCount,
    LlmUsageRecord? Llm = null);

/// <summary>
/// 取込の中核（アーキテクチャ概要 §8）: staging への保存 → 正規化 → 決定的な名寄せ → 変更提案・補完提案・修正依頼の作成。
/// 作業領域に書くだけで、公開側は変えない（反映は別人の承認。<see cref="ProposalReviewService"/>）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>取込を実行できるのは人事管理者（人、その委任を受けた AI エージェント、jin-ji 自身のワーカー・CLI）。実行者（人）は取込単位に記録し、
/// その取込の提案の起案者になる（承認はできない。§5.5）</item>
/// <item>staging に入るのは、コネクタが項目マッピングと許可リストを通した項目だけ（<see cref="MappedTable"/>）。DB の CHECK 制約でも検査する</item>
/// <item>作業領域の行は 1 トランザクションで保存する。失敗・拒否も監査ログに残す</item>
/// </list>
/// </remarks>
public sealed class ImportService
{
    public const string ImportAction = "ingest.import";

    /// <summary>LLM への送信（送信先・送った項目名と件数は llm_request_log）。</summary>
    public const string LlmSendAction = "ingest.llm.send";
    private const string TargetType = "import_batch";

    private readonly IIngestStore _store;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly ProposalSuggester? _suggester;
    private readonly FieldManagementService? _fieldManagement;
    private readonly IFieldRegistrationStore? _fieldRegistrations;

    /// <param name="suggester">LLM による候補提示（設定で有効にしたときだけ。取込ごとに <see cref="ImportRequest.UseLlm"/> でオプトインする）。</param>
    /// <param name="fieldManagement">領域の管理元（ADR-0013。無ければすべて人事パッケージとして扱う）。</param>
    /// <param name="fieldRegistrations">直接登録が書いた対象（管理元が両方の領域の印に使う）。</param>
    public ImportService(
        IIngestStore store,
        IOrgSnapshotProvider snapshots,
        AuditWriter audit,
        TimeProvider timeProvider,
        ProposalSuggester? suggester = null,
        FieldManagementService? fieldManagement = null,
        IFieldRegistrationStore? fieldRegistrations = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _snapshots = snapshots;
        _audit = audit;
        _timeProvider = timeProvider;
        _suggester = suggester;
        _fieldManagement = fieldManagement;
        _fieldRegistrations = fieldRegistrations;
    }

    public async Task<ImportResult> ImportAsync(ViewerContext viewer, ImportRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(request);
        request.Profile.Validate();

        var batchId = EntityId.New();
        var executor = await ResolveExecutorAsync(viewer, request, batchId, cancellationToken);
        if (request.UseLlm && _suggester is null)
        {
            await _audit.RecordAsync(viewer, ImportAction, AuditOutcome.Failed, [], cancellationToken, TargetType, batchId);
            throw new InvalidOperationException("LLM による候補提示を使う取込ですが、LLM が設定されていません（JinJi:Llm:Enabled）。");
        }

        try
        {
            var workspace = await BuildAsync(viewer, request, batchId, executor, cancellationToken);
            await _store.SaveImportAsync(viewer, workspace, cancellationToken);
            await _audit.RecordAsync(viewer, ImportAction, AuditOutcome.Success, [], cancellationToken, TargetType, batchId);
            if (workspace.Llm is not null)
            {
                // 送信先・送った項目名と件数は llm_request_log（値は記録しない）
                await _audit.RecordAsync(viewer, LlmSendAction, AuditOutcome.Success, [], cancellationToken, TargetType, batchId);
            }

            return new ImportResult(
                batchId,
                workspace.Batch.RowCount,
                workspace.Batch.DroppedColumns,
                request.Table.MissingFields,
                workspace.Proposals.Count(p => p.Status == ProposalStatus.Proposed),
                workspace.Proposals.Count(p => p.Status == ProposalStatus.NeedsReview),
                workspace.Corrections.Count,
                workspace.Llm);
        }
        catch (LlmEndpointNotAllowedException)
        {
            await _audit.RecordAsync(viewer, LlmSendAction, AuditOutcome.Denied, [], CancellationToken.None, TargetType, batchId);
            await _audit.RecordAsync(viewer, ImportAction, AuditOutcome.Failed, [], CancellationToken.None, TargetType, batchId);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.RecordAsync(viewer, ImportAction, AuditOutcome.Failed, [], CancellationToken.None, TargetType, batchId);
            throw;
        }
    }

    private async Task<ImportWorkspace> BuildAsync(ViewerContext viewer, ImportRequest request, Guid batchId, Guid executor, CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var today = BusinessDate.Today(_timeProvider);
        var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);

        Guid? legalEntity = null;
        if (request.Profile.LegalEntityCode is { } code)
        {
            legalEntity = snapshot.LegalEntities.SingleOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new InvalidOperationException($"項目マッピングの法人のコード {code} が jin-ji の法人にありません。");
        }
        else if (snapshot.LegalEntities.Count == 1)
        {
            legalEntity = snapshot.LegalEntities.Single().Id;
        }

        // 自由記述の項目の検査: 生年月日・住所・電話番号らしき値は staging に保存する前に除き、修正依頼に項目と種別だけを残す
        var expiresAt = startedAt.AddDays(request.Profile.StagingRetentionDays);
        var sensitive = new List<CorrectionRequestRecord>();
        var mapped = new List<MappedRow>();
        var staging = new List<StagingRowRecord>();
        foreach (var row in request.Table.Rows)
        {
            var id = EntityId.New();
            var values = new Dictionary<string, string>(row.Values, StringComparer.Ordinal);
            foreach (var field in IngestFields.FreeText.Where(values.ContainsKey).Order(StringComparer.Ordinal))
            {
                var kinds = SensitiveValueDetector.Detect(values[field]);
                if (kinds.Count > 0)
                {
                    values.Remove(field);
                    sensitive.Add(new CorrectionRequestRecord(
                        EntityId.New(),
                        id,
                        row.SourceRowNumber,
                        field,
                        CorrectionIssue.SuspectedSensitiveValue,
                        $"自由記述の項目 {field} に保持しない情報（{string.Join("・", kinds.Select(SensitiveValueDetector.Label))}）らしき値があるため、取り込みませんでした。"));
                }
            }

            mapped.Add(row with { Values = values });
            staging.Add(new StagingRowRecord(id, row.SourceRowNumber, row.RowSha256, values, expiresAt));
        }

        // 正規化
        var normalizer = new RowNormalizer(request.Profile);
        var normalized = staging
            .Zip(mapped, (s, r) => normalizer.Normalize(s.Id, r))
            .ToList();

        // 名寄せ・提案
        var context = new PlanContext(
            batchId,
            executor,
            viewer.Actor == ActorType.Agent ? viewer.ClientId : null,
            snapshot,
            legalEntity,
            request.EffectiveDate,
            today,
            normalizer.EmployeeNumbers)
        {
            Profile = request.Profile,
        };
        var plan = ProposalPlanner.Plan(context, normalized);
        var proposals = plan.Proposals;

        // 管理元が jin-ji・両方の領域の差分を振り分ける（ADR-0013 決定 2）
        if (_fieldManagement is not null)
        {
            var modes = await _fieldManagement.ModesAsync(viewer, cancellationToken);
            var written = _fieldRegistrations is not null && modes.Values.Contains(FieldManagementMode.Both)
                ? await _fieldRegistrations.FieldWrittenAsync(viewer, today, cancellationToken)
                : FieldWrittenTargets.None;
            proposals = FieldModeFilter.Apply(proposals, modes, written);
        }

        // LLM による候補提示（オプトイン）: 要確認の提案の根拠に推定を付けるだけ。提案の状態・値は変えない（§7.1）
        LlmUsageRecord? llm = null;
        if (request.UseLlm && _suggester is not null)
        {
            var suggested = await _suggester.SuggestAsync(context, normalized, proposals, cancellationToken);
            proposals = suggested.Proposals;
            llm = suggested.Usage;
        }

        // 名寄せで人に対応付けられなかった行（要確認で対象の人が無い）は、氏名が戸籍名の可能性がある。staging の保持期限を短くする（ADR-0002）
        var unidentified = proposals
            .Where(p => p.Operation == ProposalOperation.Review && p.TargetId is null)
            .SelectMany(p => p.StagingRowIds)
            .ToHashSet();
        if (unidentified.Count > 0)
        {
            var shortExpiry = startedAt.AddDays(Math.Min(request.Profile.UnidentifiedStagingRetentionDays, request.Profile.StagingRetentionDays));
            staging = [.. staging.Select(s => unidentified.Contains(s.Id) ? s with { ExpiresAt = shortExpiry } : s)];
        }

        // 名寄せで分かった保持しない値（戸籍名で書かれた氏名など）は、保存する前に作業領域から除く
        if (plan.Scrub is { Count: > 0 } scrub)
        {
            staging = [.. staging.Select(s => scrub.TryGetValue(s.Id, out var fields)
                ? s with { Data = s.Data.Where(kv => !fields.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) }
                : s)];
            normalized = [.. normalized.Select(n => scrub.TryGetValue(n.StagingRowId, out var fields)
                ? new NormalizedRow(n.StagingRowId, n.SourceRowNumber, n.Data.Where(kv => !fields.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), n.Issues)
                : n)];
        }

        var batch = new ImportBatchRecord
        {
            Id = batchId,
            SourceKind = request.Table.SourceKind,
            TargetKind = request.Profile.Target,
            FileName = request.FileName,
            FileSha256 = request.FileSha256,
            MappingName = request.MappingName,
            MappingSha256 = request.MappingSha256,
            LegalEntityId = legalEntity,
            EffectiveDate = request.EffectiveDate,
            ExecutedByPersonId = executor,
            ExecutedViaActor = viewer.Actor,
            ExecutedViaClientId = viewer.ClientId,
            RowCount = request.Table.Rows.Count,
            DroppedColumns = request.Table.DroppedColumns,
            StartedAt = startedAt,
            FinishedAt = _timeProvider.GetUtcNow(),
        };

        return new ImportWorkspace(
            batch,
            staging,
            [.. normalized.Select(n => new NormalizedRowRecord(n.StagingRowId, n.Data, n.Issues))],
            plan.Candidates,
            proposals,
            [.. sensitive, .. plan.Corrections],
            llm);
    }

    private async Task<Guid> ResolveExecutorAsync(ViewerContext viewer, ImportRequest request, Guid batchId, CancellationToken cancellationToken)
    {
        if (!viewer.IsHrAdmin)
        {
            await _audit.RecordAsync(viewer, ImportAction, AuditOutcome.Denied, [], cancellationToken, TargetType, batchId);
            throw new AccessDeniedException("取込は人事管理者（またはその委任を受けたエージェント・jin-ji のワーカー）だけが実行できます。");
        }

        if (viewer.Actor == ActorType.System)
        {
            return request.ExecutedByPersonId
                ?? throw new ArgumentException("jin-ji のワーカー・CLI で取り込むときは実行者（人）を指定してください。", nameof(request));
        }

        if (viewer.EffectivePersonId is not { } person
            || (request.ExecutedByPersonId is { } requested && requested != person))
        {
            await _audit.RecordAsync(viewer, ImportAction, AuditOutcome.Denied, [], cancellationToken, TargetType, batchId);
            throw new AccessDeniedException("取込の実行者は閲覧者本人（AI エージェントは委任元）に限ります。");
        }

        return person;
    }
}
