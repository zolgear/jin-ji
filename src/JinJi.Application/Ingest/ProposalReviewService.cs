using System.Globalization;
using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Ingest;

/// <summary>
/// 変更提案の承認（公開側への反映）と却下。公開される情報への唯一の入口（アーキテクチャ概要 §7.1・§8）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>承認・却下できるのは人事管理者の人（<see cref="ActorType.User"/>）だけ。AI エージェント・ワーカーは承認できない（§7.1）</item>
/// <item>承認者 ≠ 取込の実行者（= 起案者）（§5.5 職務分掌。DB の CHECK 制約でも守る）。要確認を解決した結果の提案は、承認者 ≠ 解決者も</item>
/// <item>名寄せの要確認を却下したら、根拠の行の氏名を作業領域から消す（<see cref="WorkspaceScrub"/>）</item>
/// <item>反映は二つの時間軸の規則に従う（訂正は旧行の記録期間を閉じて新しい行を追加する。変更は有効日で版を分ける）</item>
/// <item>組織を書く提案は、反映後の組織で段の順位が逆転しないこと、段のキーが法人の段の定義にあることを検査する（<see cref="OrgSnapshot.LevelOrderViolations"/>。§5.6）</item>
/// <item>反映したら同一プロセスのスナップショットのキャッシュを捨てる（<see cref="IOrgDataChangeNotifier"/>）。拒否・失敗も監査ログに残す</item>
/// <item>反映したら、対象の人（組織の提案は上長が変わり得るのですべての人）の未対応の修正依頼のうち、値が変わったものを自動で解消にする
/// （<see cref="Corrections.CorrectionRequestService.ReconcileAsync"/>。承認は済んでいるので、失敗しても承認は取り消さず、ワーカーの定期ジョブが拾い直す）</item>
/// </list>
/// </remarks>
public sealed class ProposalReviewService
{
    public const string ApproveAction = "ingest.proposal.approve";
    public const string RejectAction = "ingest.proposal.reject";
    private const string TargetType = "change_proposal";

    private readonly IIngestStore _store;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly IOrgDataChangeNotifier? _changes;
    private readonly Corrections.CorrectionRequestService? _corrections;

    public ProposalReviewService(
        IIngestStore store,
        IOrgSnapshotProvider snapshots,
        AuditWriter audit,
        TimeProvider timeProvider,
        IOrgDataChangeNotifier? changes = null,
        Corrections.CorrectionRequestService? corrections = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _snapshots = snapshots;
        _audit = audit;
        _timeProvider = timeProvider;
        _changes = changes;
        _corrections = corrections;
    }

    /// <summary>起案中の提案を承認し、公開側へ反映する。</summary>
    public async Task ApproveAsync(ViewerContext viewer, Guid proposalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireHrAdminUserAsync(viewer, ApproveAction, proposalId, cancellationToken);

        var proposal = await _store.FindProposalAsync(viewer, proposalId, cancellationToken);
        if (proposal is null || proposal.Status != ProposalStatus.Proposed)
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Failed, [], cancellationToken, TargetType, proposalId);
            throw new InvalidOperationException($"承認できる状態の提案 {proposalId} がありません（要確認の提案はそのままでは承認できません）。");
        }

        var subjects = Subjects(proposal);
        await RequireNotExecutorAsync(viewer, ApproveAction, proposal, subjects, cancellationToken);
        if (proposal.ResolvedByPersonId is { } resolver && resolver == viewer.PersonId)
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Denied, subjects, cancellationToken, TargetType, proposalId);
            throw new AccessDeniedException("要確認の提案を解決した人は、その解決の結果の提案を承認できません（職務分掌）。");
        }

        var today = BusinessDate.Today(_timeProvider);
        try
        {
            if (proposal.TargetKind is ProposalTargetKind.OrgUnit or ProposalTargetKind.OrgUnitVersion && proposal.Operation != ProposalOperation.Abolish)
            {
                await ValidateOrgAsync(viewer, proposal, today, cancellationToken);
            }

            if (!await _store.ApplyProposalAsync(viewer, proposal, today, cancellationToken))
            {
                throw new InvalidOperationException($"提案 {proposalId} を反映できませんでした（状態が変わった可能性があります）。");
            }
        }
        catch (OrgLevelValidationException)
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Denied, subjects, cancellationToken, TargetType, proposalId);
            throw;
        }
        catch (Exception ex) when (ex is ProposalConflictException or InvalidOperationException)
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Failed, subjects, cancellationToken, TargetType, proposalId);
            throw;
        }

        _changes?.NotifyChanged();
        await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Success, subjects, cancellationToken, TargetType, proposalId);
        await ReconcileCorrectionsAsync(viewer, proposal, subjects, cancellationToken);
    }

    /// <summary>反映で値が変わった人の未対応の修正依頼を自動で解消にする（失敗しても承認は取り消さない。定期ジョブが拾い直す）。</summary>
    private async Task ReconcileCorrectionsAsync(ViewerContext viewer, ChangeProposalRecord proposal, IReadOnlyList<Guid> subjects, CancellationToken cancellationToken)
    {
        if (_corrections is null)
        {
            return;
        }

        // 組織・拠点・建物・階・配置図・組織のエリアの提案は、所属者の上長・拠点・階・席が変わり得るのですべての人を判定する
        var organizational = proposal.TargetKind is ProposalTargetKind.OrgUnit or ProposalTargetKind.OrgUnitVersion
            or ProposalTargetKind.Site or ProposalTargetKind.SiteVersion
            or ProposalTargetKind.Building or ProposalTargetKind.BuildingVersion or ProposalTargetKind.Floor or ProposalTargetKind.FloorVersion
            or ProposalTargetKind.FloorLayout;
        try
        {
            await _corrections.ReconcileAsync(viewer, organizational ? null : subjects, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.RecordAsync(viewer, Corrections.CorrectionRequestService.AutoResolveAction, AuditOutcome.Failed, subjects, CancellationToken.None, TargetType, proposal.Id);
        }
    }

    /// <summary>起案中・要確認の提案を却下する。</summary>
    public async Task RejectAsync(ViewerContext viewer, Guid proposalId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireHrAdminUserAsync(viewer, RejectAction, proposalId, cancellationToken);

        var proposal = await _store.FindProposalAsync(viewer, proposalId, cancellationToken);
        if (proposal is null || proposal.Status is not (ProposalStatus.Proposed or ProposalStatus.NeedsReview))
        {
            await _audit.RecordAsync(viewer, RejectAction, AuditOutcome.Failed, [], cancellationToken, TargetType, proposalId);
            throw new InvalidOperationException($"却下できる状態の提案 {proposalId} がありません。");
        }

        var subjects = Subjects(proposal);
        await RequireNotExecutorAsync(viewer, RejectAction, proposal, subjects, cancellationToken);
        if (!await _store.RejectProposalAsync(viewer, proposalId, WorkspaceScrub.FieldsAfterDecision(proposal), cancellationToken))
        {
            await _audit.RecordAsync(viewer, RejectAction, AuditOutcome.Failed, subjects, cancellationToken, TargetType, proposalId);
            throw new InvalidOperationException($"提案 {proposalId} を却下できませんでした（状態が変わった可能性があります）。");
        }

        await _audit.RecordAsync(viewer, RejectAction, AuditOutcome.Success, subjects, cancellationToken, TargetType, proposalId);
    }

    /// <summary>
    /// 組織の提案を反映した後の組織で、段のキーが法人の定義にあり、子の段の順位が親より下であること（新たな逆転を作らない）。
    /// </summary>
    private async Task ValidateOrgAsync(ViewerContext viewer, ChangeProposalRecord proposal, DateOnly today, CancellationToken cancellationToken)
    {
        var date = proposal.EffectiveDate ?? today;
        var snapshot = await _snapshots.LoadAsync(viewer, date, today, cancellationToken);
        var unitId = proposal.TargetId ?? throw new InvalidOperationException($"組織の提案 {proposal.Id} に対象の組織がありません。");

        OrgUnit unit;
        if (proposal.Operation == ProposalOperation.Create)
        {
            unit = new OrgUnit(
                unitId,
                Guid.Parse(Required(proposal.Payload, ProposalFields.LegalEntityId), CultureInfo.InvariantCulture),
                Required(proposal.Payload, ProposalFields.Code),
                Required(proposal.Payload, ProposalFields.Name),
                proposal.Payload.GetValueOrDefault(ProposalFields.ShortName),
                proposal.Payload.GetValueOrDefault(ProposalFields.LevelKey),
                ParseGuid(proposal.Payload.GetValueOrDefault(ProposalFields.ParentOrgUnitId)),
                ParseGuid(proposal.Payload.GetValueOrDefault(ProposalFields.HeadPersonId)));
        }
        else
        {
            var current = snapshot.FindUnit(unitId)
                ?? throw new ProposalConflictException($"{date:yyyy-MM-dd} に有効な組織 {unitId} がありません。");
            unit = current with
            {
                Name = New(proposal, ProposalFields.Name, current.Name) ?? current.Name,
                ShortName = New(proposal, ProposalFields.ShortName, current.ShortName),
                LevelKey = New(proposal, ProposalFields.LevelKey, current.LevelKey),
                ParentId = proposal.Diff.TryGetValue(ProposalFields.ParentOrgUnitId, out var parent) ? ParseGuid(parent.New) : current.ParentId,
                HeadPersonId = proposal.Diff.TryGetValue(ProposalFields.HeadPersonId, out var head) ? ParseGuid(head.New) : current.HeadPersonId,
            };
        }

        if (unit.LevelKey is { } key && snapshot.LevelSchemeOf(unit.LegalEntityId).Find(key) is null)
        {
            throw new OrgLevelValidationException($"段 {key} は組織 {unit.Code} の法人の段の定義にありません。");
        }

        var before = snapshot.LevelOrderViolations().ToHashSet();
        var units = snapshot.Units.Where(u => u.Id != unit.Id).Append(unit);
        var after = OrgSnapshot.Create(date, snapshot.LegalEntities, units, [], [], snapshot.LevelSchemes);
        var broken = after.LevelOrderViolations().Where(v => !before.Contains(v))
            .Select(v => $"{after.FindUnit(v.Unit)!.Code} の上位 {after.FindUnit(v.Ancestor)!.Code}")
            .ToList();
        if (broken.Count > 0)
        {
            throw new OrgLevelValidationException($"{date:yyyy-MM-dd} 時点の組織で、子の段の順位が親より下でなくなります: " + string.Join(", ", broken));
        }
    }

    private async Task RequireHrAdminUserAsync(ViewerContext viewer, string action, Guid proposalId, CancellationToken cancellationToken)
    {
        if (viewer.Actor != ActorType.User || !viewer.IsHrAdmin || viewer.PersonId is null)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, proposalId);
            throw new AccessDeniedException("変更提案の承認・却下は人事管理者（人）だけが行えます。");
        }
    }

    private async Task RequireNotExecutorAsync(
        ViewerContext viewer,
        string action,
        ChangeProposalRecord proposal,
        IReadOnlyList<Guid> subjects,
        CancellationToken cancellationToken)
    {
        var batch = await _store.FindBatchAsync(viewer, proposal.BatchId, cancellationToken);
        if (viewer.PersonId == proposal.ProposedByPersonId || batch is null || viewer.PersonId == batch.ExecutedByPersonId)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, subjects, cancellationToken, TargetType, proposal.Id);
            throw new AccessDeniedException("取込の実行者は、その取込の提案を承認・却下できません（職務分掌）。");
        }
    }

    /// <summary>監査ログの対象者（人の提案なら対象の人）。</summary>
    internal static IReadOnlyList<Guid> Subjects(ChangeProposalRecord proposal) =>
        proposal.TargetKind is ProposalTargetKind.Person or ProposalTargetKind.PersonVersion or ProposalTargetKind.Assignment
            or ProposalTargetKind.PersonnelAction or ProposalTargetKind.AccountSuspension or ProposalTargetKind.ExternalIdentity
            or ProposalTargetKind.WorkplaceAssignment
            && proposal.TargetId is { } id
            ? [id]
            : [];

    private static string? New(ChangeProposalRecord proposal, string key, string? current) =>
        proposal.Diff.TryGetValue(key, out var change) ? change.New : current;

    private static string Required(IReadOnlyDictionary<string, string?> payload, string key) =>
        payload.GetValueOrDefault(key) ?? throw new InvalidOperationException($"提案の {key} がありません。");

    private static Guid? ParseGuid(string? value) => value is null ? null : Guid.Parse(value, CultureInfo.InvariantCulture);
}
