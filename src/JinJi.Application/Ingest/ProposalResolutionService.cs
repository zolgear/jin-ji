using System.Globalization;
using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Ingest;

/// <summary>要確認の提案に対する人の選択。</summary>
public enum ResolutionChoice
{
    /// <summary>候補（名寄せの候補・組織の候補・LLM が順位を付けた候補）から選ぶ。</summary>
    SelectCandidate,

    /// <summary>新規（新しい人）として登録する。</summary>
    RegisterNew,

    /// <summary>人事パッケージ側の修正依頼に回す。</summary>
    ReferToCorrection,

    /// <summary>却下する。</summary>
    Reject,
}

/// <summary>要確認の提案の解決の要求。</summary>
/// <param name="PersonId">候補から選ぶ人（人の候補がある提案）。</param>
/// <param name="OrgUnitId">候補から選ぶ組織（所属のあいまいな辞令・段飛ばしの可能性のある組織の上位組織）。</param>
public sealed record ResolutionRequest(ResolutionChoice Choice, Guid? PersonId = null, Guid? OrgUnitId = null);

/// <summary>要確認の提案の解決の結果。</summary>
/// <param name="DerivedProposalIds">解決の結果として起案した提案（通常の承認フローで、解決者・取込の実行者とは別の人が承認する）。</param>
public sealed record ResolutionResult(
    Guid ProposalId,
    ProposalStatus Status,
    ProposalResolution? Resolution,
    IReadOnlyList<Guid> DerivedProposalIds,
    Guid? CorrectionId);

/// <summary>提案が無いか、閲覧者に見えない。</summary>
public sealed class ProposalNotFoundException : Exception
{
    public ProposalNotFoundException()
    {
    }

    public ProposalNotFoundException(string message)
        : base(message)
    {
    }

    public ProposalNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 要確認の提案の解決（人の操作。アーキテクチャ概要 §7.1 の HITL・§8）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>解決できるのは人事管理者の人だけ（AI エージェント・ワーカーは不可）。取込の実行者（= 起案者）も解決してよい</item>
/// <item>解決の結果は通常の提案（起案中）として同じ取込単位に起案し直し、既存の承認フローに乗せる。解決者はその提案を承認できない
/// （承認者は起案者とも解決者とも別の人。2 人が揃わないと公開側に出ない。DB の CHECK 制約でも守る）</item>
/// <item>選べるのは候補（名寄せの候補・組織の候補・LLM が順位を付けた候補）だけ。候補に無い人・組織は選べない（直接の指定は修正依頼に回して取り込み直す）</item>
/// <item>決めた後（解決・却下）は、名寄せの要確認の根拠の行の氏名を作業領域から消す（戸籍名の可能性があるため。ADR-0002・<see cref="WorkspaceScrub"/>）</item>
/// <item>拒否・失敗も監査ログに残す（<see cref="ResolveAction"/>）</item>
/// </list>
/// </remarks>
public sealed class ProposalResolutionService
{
    public const string ResolveAction = "ingest.proposal.resolve";
    private const string TargetType = "change_proposal";
    private const decimal ResolvedConfidence = 1.0m;

    private readonly IIngestStore _store;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;

    public ProposalResolutionService(IIngestStore store, IOrgSnapshotProvider snapshots, AuditWriter audit, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _snapshots = snapshots;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    /// <summary>要確認の提案を参照する（人事管理者の人だけ。解決の画面・API 用）。</summary>
    public async Task<ChangeProposalRecord> GetAsync(ViewerContext viewer, Guid proposalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireHrAdminUserAsync(viewer, proposalId, "ingest.proposal.read", cancellationToken);
        var proposal = await _store.FindProposalAsync(viewer, proposalId, cancellationToken) ?? throw new ProposalNotFoundException($"提案 {proposalId} がありません。");
        await _audit.RecordAsync(viewer, "ingest.proposal.read", AuditOutcome.Success, ProposalReviewService.Subjects(proposal), cancellationToken, TargetType, proposalId);
        return proposal;
    }

    /// <summary>要確認の提案を解決する。</summary>
    /// <exception cref="AccessDeniedException">人事管理者の人でない。</exception>
    /// <exception cref="ProposalNotFoundException">提案が無い。</exception>
    /// <exception cref="InvalidOperationException">要確認の状態でない（解決済み・却下済みなど）。</exception>
    /// <exception cref="ArgumentException">その提案では選べない選択・候補に無い人や組織。</exception>
    public async Task<ResolutionResult> ResolveAsync(ViewerContext viewer, Guid proposalId, ResolutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(request);
        await RequireHrAdminUserAsync(viewer, proposalId, ResolveAction, cancellationToken);

        var proposal = await _store.FindProposalAsync(viewer, proposalId, cancellationToken);
        if (proposal is null)
        {
            await _audit.RecordAsync(viewer, ResolveAction, AuditOutcome.Failed, [], cancellationToken, TargetType, proposalId);
            throw new ProposalNotFoundException($"提案 {proposalId} がありません。");
        }

        var subjects = ProposalReviewService.Subjects(proposal);
        var batch = await _store.FindBatchAsync(viewer, proposal.BatchId, cancellationToken)
            ?? throw new ProposalNotFoundException($"提案 {proposalId} の取込単位がありません。");

        if (proposal.Status != ProposalStatus.NeedsReview)
        {
            await _audit.RecordAsync(viewer, ResolveAction, AuditOutcome.Failed, subjects, cancellationToken, TargetType, proposalId);
            throw new InvalidOperationException($"提案 {proposalId} は要確認の状態ではありません（解決・却下済みか、起案中の提案です）。");
        }

        var scrub = WorkspaceScrub.FieldsAfterDecision(proposal);
        ResolutionResult result;
        try
        {
            result = request.Choice switch
            {
                ResolutionChoice.Reject => await RejectAsync(viewer, proposal, scrub, cancellationToken),
                ResolutionChoice.ReferToCorrection => await ReferAsync(viewer, proposal, scrub, cancellationToken),
                ResolutionChoice.SelectCandidate => await WriteAsync(viewer, proposal, ProposalResolution.SelectedCandidate, await SelectAsync(viewer, proposal, batch, request, cancellationToken), scrub, cancellationToken),
                ResolutionChoice.RegisterNew => await WriteAsync(viewer, proposal, ProposalResolution.RegisteredNew, await RegisterNewAsync(viewer, proposal, cancellationToken), scrub, cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Choice, "未知の選択です。"),
            };
        }
        catch (ArgumentException)
        {
            await _audit.RecordAsync(viewer, ResolveAction, AuditOutcome.Denied, subjects, cancellationToken, TargetType, proposalId);
            throw;
        }
        catch (InvalidOperationException)
        {
            await _audit.RecordAsync(viewer, ResolveAction, AuditOutcome.Failed, subjects, cancellationToken, TargetType, proposalId);
            throw;
        }

        var chosen = request.PersonId is { } person ? subjects.Append(person) : subjects;
        await _audit.RecordAsync(viewer, ResolveAction, AuditOutcome.Success, chosen, cancellationToken, TargetType, proposalId);
        return result;
    }

    private async Task<ResolutionResult> RejectAsync(ViewerContext viewer, ChangeProposalRecord proposal, IReadOnlyCollection<string> scrub, CancellationToken cancellationToken)
    {
        if (!await _store.RejectProposalAsync(viewer, proposal.Id, scrub, cancellationToken))
        {
            throw new InvalidOperationException($"提案 {proposal.Id} を却下できませんでした（状態が変わった可能性があります）。");
        }

        return new ResolutionResult(proposal.Id, ProposalStatus.Rejected, null, [], null);
    }

    private async Task<ResolutionResult> ReferAsync(ViewerContext viewer, ChangeProposalRecord proposal, IReadOnlyCollection<string> scrub, CancellationToken cancellationToken)
    {
        // メッセージに取込元の値は書かない（種別と行番号だけ）
        var correction = new CorrectionRequestRecord(
            EntityId.New(),
            proposal.StagingRowIds.Count > 0 ? proposal.StagingRowIds[0] : null,
            proposal.Basis.RowNumbers.Count > 0 ? proposal.Basis.RowNumbers[0] : null,
            null,
            CorrectionIssue.ReferredFromReview,
            $"要確認の提案（種別 {proposal.Basis.Code ?? Code(proposal.TargetKind)}）を人事パッケージ側の修正に回しました。取込元の行を直して取り込み直してください。");
        if (!await _store.ResolveProposalAsync(viewer, new ProposalResolutionWrite(proposal.Id, ProposalResolution.ReferredToCorrection, [], correction, scrub), cancellationToken))
        {
            throw new InvalidOperationException($"提案 {proposal.Id} を解決できませんでした（状態が変わった可能性があります）。");
        }

        return new ResolutionResult(proposal.Id, ProposalStatus.Resolved, ProposalResolution.ReferredToCorrection, [], correction.Id);
    }

    private async Task<ResolutionResult> WriteAsync(
        ViewerContext viewer,
        ChangeProposalRecord proposal,
        ProposalResolution resolution,
        IReadOnlyList<ChangeProposalRecord> derived,
        IReadOnlyCollection<string> scrub,
        CancellationToken cancellationToken)
    {
        if (!await _store.ResolveProposalAsync(viewer, new ProposalResolutionWrite(proposal.Id, resolution, derived, null, scrub), cancellationToken))
        {
            throw new InvalidOperationException($"提案 {proposal.Id} を解決できませんでした（状態が変わった可能性があります）。");
        }

        return new ResolutionResult(proposal.Id, ProposalStatus.Resolved, resolution, [.. derived.Select(d => d.Id)], null);
    }

    // -----------------------------------------------------------------------
    // 候補から選ぶ
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<ChangeProposalRecord>> SelectAsync(
        ViewerContext viewer,
        ChangeProposalRecord proposal,
        ImportBatchRecord batch,
        ResolutionRequest request,
        CancellationToken cancellationToken)
    {
        var people = proposal.Basis.Candidates.Select(c => c.PersonId)
            .Concat(proposal.Basis.Llm?.People.Select(p => p.Id) ?? [])
            .ToHashSet();
        var orgs = proposal.Basis.OrgCandidates
            .Concat(proposal.Basis.Llm?.Orgs.Select(o => o.Id) ?? [])
            .ToHashSet();

        Guid ChoosePerson(Guid? fallback)
        {
            if (request.PersonId is { } chosen)
            {
                if (!people.Contains(chosen) && chosen != fallback)
                {
                    throw new ArgumentException("候補に無い人は選べません（修正依頼に回して取り込み直してください）。", nameof(request));
                }

                return chosen;
            }

            return fallback ?? throw new ArgumentException("候補から人を選んでください（person_id）。", nameof(request));
        }

        switch (proposal.TargetKind, proposal.Operation)
        {
            case (ProposalTargetKind.PersonnelAction, ProposalOperation.Review):
            {
                if (proposal.Payload.GetValueOrDefault(ProposalFields.ActionType) is not { } actionType)
                {
                    throw new ArgumentException("発令内容を解釈できなかった提案は候補から選べません（修正依頼に回すか却下してください）。", nameof(request));
                }

                var person = ChoosePerson(proposal.TargetId);
                var payload = proposal.Payload.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                payload[ProposalFields.PersonId] = Id(person);
                var currentOrg = payload.GetValueOrDefault(ProposalFields.OrgUnitId) is { } o ? Guid.Parse(o, CultureInfo.InvariantCulture) : (Guid?)null;
                if (request.OrgUnitId is { } org)
                {
                    if (!orgs.Contains(org) && org != currentOrg)
                    {
                        throw new ArgumentException("候補に無い組織は選べません。", nameof(request));
                    }

                    payload[ProposalFields.OrgUnitId] = Id(org);
                    payload[ProposalFields.AssignmentType] = actionType is "concurrent_start" or "secondment_start" ? "concurrent" : "primary";
                }
                else if (currentOrg is null && proposal.Basis.Code == SupplementCodes.AmbiguousOrg)
                {
                    throw new ArgumentException("所属のあいまいな辞令です。候補から組織を選んでください（org_unit_id）。", nameof(request));
                }

                return [Derived(proposal, ProposalTargetKind.PersonnelAction, ProposalOperation.Create, person, proposal.EffectiveDate, payload, new Dictionary<string, FieldChange>())];
            }

            case (ProposalTargetKind.ExternalIdentity, ProposalOperation.Review):
            {
                var person = ChoosePerson(null);
                var payload = proposal.Payload.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                payload[ProposalFields.PersonId] = Id(person);
                if (payload.GetValueOrDefault(ProposalFields.ExternalId) is null)
                {
                    throw new ArgumentException("外部 ID の無い行は対応付けられません。", nameof(request));
                }

                return [Derived(proposal, ProposalTargetKind.ExternalIdentity, ProposalOperation.Create, person, null, payload, new Dictionary<string, FieldChange>())];
            }

            case (ProposalTargetKind.Person, ProposalOperation.Review):
                return await SelectPersonAsync(viewer, proposal, batch, ChoosePerson(null), cancellationToken);

            case (ProposalTargetKind.OrgUnitVersion, ProposalOperation.Supplement) when proposal.Basis.Code == SupplementCodes.OrgParentAmbiguous:
            {
                var unit = proposal.TargetId ?? throw new InvalidOperationException($"提案 {proposal.Id} に対象の組織がありません。");
                var parent = request.OrgUnitId ?? throw new ArgumentException("上位組織を候補から選んでください（org_unit_id）。", nameof(request));
                if (!orgs.Contains(parent))
                {
                    throw new ArgumentException("候補に無い組織は選べません。", nameof(request));
                }

                // 起案中の上位組織（結合セルの補いで決めたもの）と同じなら、変える提案は要らない（解決だけを記録する）
                var proposed = proposal.Payload.GetValueOrDefault(ProposalFields.ParentOrgUnitId);
                if (proposed == Id(parent))
                {
                    return [];
                }

                var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal) { [ProposalFields.ParentOrgUnitId] = new FieldChange(proposed, Id(parent)) };
                var operation = batch.EffectiveDate is null ? ProposalOperation.Correct : ProposalOperation.Change;
                return [Derived(proposal, ProposalTargetKind.OrgUnitVersion, operation, unit, batch.EffectiveDate, new Dictionary<string, string?>(), diff)];
            }

            default:
                throw new ArgumentException("この提案は候補から選べません（修正依頼に回すか却下してください）。", nameof(request));
        }
    }

    /// <summary>人の取込の要確認で人を選んだ: その人の属性の版・主務の所属との差分を提案にする（差分が無ければ提案は作らない）。</summary>
    private async Task<IReadOnlyList<ChangeProposalRecord>> SelectPersonAsync(
        ViewerContext viewer,
        ChangeProposalRecord proposal,
        ImportBatchRecord batch,
        Guid personId,
        CancellationToken cancellationToken)
    {
        var today = BusinessDate.Today(_timeProvider);
        var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
        var person = snapshot.FindPerson(personId) ?? throw new InvalidOperationException($"人 {personId} が今日の時点にいません。");
        var current = person.Current;
        var currentValues = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ProposalFields.FamilyName] = current.FamilyName,
            [ProposalFields.GivenName] = current.GivenName,
            [ProposalFields.FamilyNameKana] = current.FamilyNameKana,
            [ProposalFields.GivenNameKana] = current.GivenNameKana,
            [ProposalFields.FamilyNameLatin] = current.FamilyNameLatin,
            [ProposalFields.GivenNameLatin] = current.GivenNameLatin,
            [ProposalFields.DisplayName] = current.DisplayName,
            [ProposalFields.WorkEmail] = current.WorkEmail,
            [ProposalFields.ExtensionNumber] = current.ExtensionNumber,
            [ProposalFields.WorkMobilePhone] = current.WorkMobilePhone,
            [ProposalFields.SiteId] = current.SiteId?.ToString("D"),
            [ProposalFields.RemoteWork] = current.RemoteWork ? "true" : "false",
            [ProposalFields.EmploymentType] = current.EmploymentType,
            [ProposalFields.JobType] = current.JobType,
            [ProposalFields.HireDate] = current.HireDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            [ProposalFields.TerminationDate] = current.TerminationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };

        var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        foreach (var (key, old) in currentValues)
        {
            if (proposal.Payload.GetValueOrDefault(key) is { } value && !string.Equals(value, old, StringComparison.Ordinal))
            {
                diff[key] = new FieldChange(old, value);
            }
        }

        if ((diff.ContainsKey(ProposalFields.FamilyName) || diff.ContainsKey(ProposalFields.GivenName)) && proposal.Payload.GetValueOrDefault(ProposalFields.SearchName) is { } searchName)
        {
            diff[ProposalFields.SearchName] = new FieldChange(null, searchName);
        }

        if ((diff.ContainsKey(ProposalFields.FamilyNameKana) || diff.ContainsKey(ProposalFields.GivenNameKana)) && proposal.Payload.GetValueOrDefault(ProposalFields.SearchNameKana) is { } searchKana)
        {
            diff[ProposalFields.SearchNameKana] = new FieldChange(null, searchKana);
        }

        var operation = batch.EffectiveDate is null ? ProposalOperation.Correct : ProposalOperation.Change;
        var derived = new List<ChangeProposalRecord>();
        if (diff.Count > 0)
        {
            derived.Add(Derived(proposal, ProposalTargetKind.PersonVersion, operation, personId, batch.EffectiveDate, new Dictionary<string, string?>(), diff));
        }

        if (proposal.Payload.GetValueOrDefault(ProposalFields.OrgUnitId) is { } orgText)
        {
            var primary = snapshot.AssignmentsOf(personId).FirstOrDefault(a => a.Type == AssignmentType.Primary);
            var title = proposal.Payload.GetValueOrDefault(ProposalFields.PositionTitle);
            if (primary is null)
            {
                var payload = new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    [ProposalFields.PersonId] = Id(personId),
                    [ProposalFields.OrgUnitId] = orgText,
                    [ProposalFields.AssignmentType] = "primary",
                };
                if (title is not null)
                {
                    payload[ProposalFields.PositionTitle] = title;
                }

                derived.Add(Derived(proposal, ProposalTargetKind.Assignment, ProposalOperation.Create, personId, batch.EffectiveDate ?? today, payload, new Dictionary<string, FieldChange>()));
            }
            else
            {
                var assignmentDiff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
                if (Id(primary.OrgUnitId) != orgText)
                {
                    assignmentDiff[ProposalFields.OrgUnitId] = new FieldChange(Id(primary.OrgUnitId), orgText);
                }

                if (title is not null && title != primary.PositionTitle)
                {
                    assignmentDiff[ProposalFields.PositionTitle] = new FieldChange(primary.PositionTitle, title);
                }

                if (assignmentDiff.Count > 0)
                {
                    derived.Add(Derived(proposal, ProposalTargetKind.Assignment, operation, personId, batch.EffectiveDate, new Dictionary<string, string?>(), assignmentDiff));
                }
            }
        }

        return derived;
    }

    // -----------------------------------------------------------------------
    // 新規として登録する
    // -----------------------------------------------------------------------

    private async Task<IReadOnlyList<ChangeProposalRecord>> RegisterNewAsync(ViewerContext viewer, ChangeProposalRecord proposal, CancellationToken cancellationToken)
    {
        if (proposal is not { TargetKind: ProposalTargetKind.Person, Operation: ProposalOperation.Review })
        {
            throw new ArgumentException("新規として登録できるのは、人の取込の名寄せの要確認だけです。", nameof(proposal));
        }

        var payload = proposal.Payload.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var missing = new[] { ProposalFields.LegalEntityId, ProposalFields.EmployeeNumber, ProposalFields.FamilyName, ProposalFields.HireDate, ProposalFields.EmploymentType }
            .Where(k => payload.GetValueOrDefault(k) is null)
            .ToList();
        if (missing.Count > 0)
        {
            throw new ArgumentException($"新しい人の登録に必要な項目がありません（{string.Join(", ", missing)}）。修正依頼に回してください。", nameof(proposal));
        }

        var today = BusinessDate.Today(_timeProvider);
        var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
        var entity = Guid.Parse(payload[ProposalFields.LegalEntityId]!, CultureInfo.InvariantCulture);
        if (snapshot.People.Any(p => p.LegalEntityId == entity && string.Equals(p.EmployeeNumber, payload[ProposalFields.EmployeeNumber], StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("同じ法人に同じ社員番号の人がいます。新規としては登録できません（候補から選ぶか、修正依頼に回してください）。", nameof(proposal));
        }

        var personId = EntityId.New();
        payload[ProposalFields.PersonId] = Id(personId);
        payload[ProposalFields.DisplayName] ??= payload.GetValueOrDefault(ProposalFields.GivenName) is { } given ? $"{payload[ProposalFields.FamilyName]} {given}" : payload[ProposalFields.FamilyName];
        if (payload.GetValueOrDefault(ProposalFields.OrgUnitId) is not null)
        {
            payload[ProposalFields.AssignmentType] = "primary";
        }

        var hire = DateOnly.ParseExact(payload[ProposalFields.HireDate]!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return [Derived(proposal, ProposalTargetKind.Person, ProposalOperation.Create, personId, hire, payload, new Dictionary<string, FieldChange>())];
    }

    // -----------------------------------------------------------------------
    // 共通
    // -----------------------------------------------------------------------

    private static ChangeProposalRecord Derived(
        ChangeProposalRecord source,
        ProposalTargetKind targetKind,
        ProposalOperation operation,
        Guid targetId,
        DateOnly? effectiveDate,
        IReadOnlyDictionary<string, string?> payload,
        IReadOnlyDictionary<string, FieldChange> diff) =>
        new()
        {
            Id = EntityId.New(),
            BatchId = source.BatchId,
            TargetKind = targetKind,
            Operation = operation,
            TargetId = targetId,
            EffectiveDate = effectiveDate,
            Payload = payload,
            Diff = diff,
            Basis = new ProposalBasis
            {
                RowNumbers = source.Basis.RowNumbers,
                Rule = source.Basis.Rule,
                Candidates = source.Basis.Candidates,
                OrgCandidates = source.Basis.OrgCandidates,
                DependsOn = source.Basis.DependsOn,
                Llm = source.Basis.Llm,
                Note = "要確認の提案を人が解決した結果です（解決した人とは別の人事管理者が承認してください）。",
            },
            StagingRowIds = source.StagingRowIds,
            Confidence = ResolvedConfidence,
            Status = ProposalStatus.Proposed,
            ProposedByPersonId = source.ProposedByPersonId,
            ProposedViaClientId = source.ProposedViaClientId,
            ResolvedFromProposalId = source.Id,
        };

    private async Task RequireHrAdminUserAsync(ViewerContext viewer, Guid proposalId, string action, CancellationToken cancellationToken)
    {
        if (viewer.Actor != ActorType.User || !viewer.IsHrAdmin || viewer.PersonId is null)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, proposalId);
            throw new AccessDeniedException("要確認の提案の解決は人事管理者（人）だけが行えます。");
        }
    }

    private static string Id(Guid id) => id.ToString("D");

    private static string Code(ProposalTargetKind kind) => System.Text.Json.JsonNamingPolicy.SnakeCaseLower.ConvertName(kind.ToString());
}
