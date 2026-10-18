using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Policies;

/// <summary>
/// ポリシーの起案・承認と、廃止の起案・承認（アーキテクチャ概要 §5.3、§5.5）。UI・API は後の手順で付ける。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>起案・承認は人事管理者だけ（RLS の policy_rule の規則と同じ）</item>
/// <item>起案者と承認者は別人（職務分掌）。AI エージェントの起案は委任元ユーザーの起案として扱う（§7.1）</item>
/// <item>廃止も起案 → 別人の承認で反映する。廃止は公開範囲を広げ得る変更なので、追加と同じく一人では行えない。
/// 承認されるまで規則は有効のまま</item>
/// <item>個人単位の例外は人事管理者が起案するもので、狭める方向だけ。起案時と承認時の両方で検査する（その間に他の規則が変わり得るため）</item>
/// <item>公開範囲は組織のルールとして決め、本人の裁量にしない。個人単位の例外の対象者本人は、その例外の起案・承認・廃止の起案・廃止の承認のどれにも関われない</item>
/// <item>拒否は監査ログに denied として残す</item>
/// </list>
/// </remarks>
public sealed class PolicyAdministrationService
{
    public const string ProposeAction = "policy.propose";
    public const string ApproveAction = "policy.approve";
    public const string ProposeRetirementAction = "policy.retire.propose";
    public const string ApproveRetirementAction = "policy.retire.approve";

    private readonly IPolicyRuleStore _store;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;

    public PolicyAdministrationService(IPolicyRuleStore store, IOrgSnapshotProvider snapshots, AuditWriter audit, TimeProvider timeProvider)
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

    /// <summary>規則を起案する。</summary>
    public async Task ProposeAsync(ViewerContext viewer, PolicyRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(rule);
        var subjects = SubjectsOf(rule);

        await RequireHrAdminPersonAsync(viewer, ProposeAction, rule.Id, subjects, cancellationToken);
        await RequireNotSubjectAsync(viewer, ProposeAction, rule, subjects, cancellationToken);
        await ValidateAsync(viewer, ProposeAction, rule, subjects, cancellationToken);

        await _store.InsertProposalAsync(viewer, rule, cancellationToken);
        await _audit.RecordAsync(viewer, ProposeAction, AuditOutcome.Success, subjects, cancellationToken, "policy_rule", rule.Id);
    }

    /// <summary>起案中の規則を承認する。</summary>
    public async Task ApproveAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireHrAdminPersonAsync(viewer, ApproveAction, ruleId, [], cancellationToken);

        var stored = await _store.FindAsync(viewer, ruleId, cancellationToken);
        if (stored is null || stored.Status != PolicyRuleStatus.Proposed)
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Failed, [], cancellationToken, "policy_rule", ruleId);
            throw new InvalidOperationException($"起案中の規則 {ruleId} がありません。");
        }

        var subjects = SubjectsOf(stored.Rule);
        await RequireDifferentPersonAsync(viewer, ApproveAction, stored.ProposedByPersonId, stored.Rule, subjects, cancellationToken);
        await RequireNotSubjectAsync(viewer, ApproveAction, stored.Rule, subjects, cancellationToken);
        await ValidateAsync(viewer, ApproveAction, stored.Rule, subjects, cancellationToken);

        if (!await _store.ApproveAsync(viewer, ruleId, cancellationToken))
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Failed, subjects, cancellationToken, "policy_rule", ruleId);
            throw new InvalidOperationException($"規則 {ruleId} を承認できませんでした（状態が変わった可能性があります）。");
        }

        await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Success, subjects, cancellationToken, "policy_rule", ruleId);
    }

    /// <summary>承認済みの規則の廃止を起案する。承認されるまで規則は有効のまま。</summary>
    public async Task ProposeRetirementAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireHrAdminPersonAsync(viewer, ProposeRetirementAction, ruleId, [], cancellationToken);

        var stored = await _store.FindAsync(viewer, ruleId, cancellationToken);
        if (stored is null || stored.Status != PolicyRuleStatus.Approved || stored.IsRetirementProposed)
        {
            await _audit.RecordAsync(viewer, ProposeRetirementAction, AuditOutcome.Failed, [], cancellationToken, "policy_rule", ruleId);
            throw new InvalidOperationException($"廃止を起案できる（承認済みで、廃止の起案が無い）規則 {ruleId} がありません。");
        }

        var subjects = SubjectsOf(stored.Rule);
        await RequireNotSubjectAsync(viewer, ProposeRetirementAction, stored.Rule, subjects, cancellationToken);

        if (!await _store.ProposeRetirementAsync(viewer, ruleId, cancellationToken))
        {
            await _audit.RecordAsync(viewer, ProposeRetirementAction, AuditOutcome.Failed, subjects, cancellationToken, "policy_rule", ruleId);
            throw new InvalidOperationException($"規則 {ruleId} の廃止を起案できませんでした（状態が変わった可能性があります）。");
        }

        await _audit.RecordAsync(viewer, ProposeRetirementAction, AuditOutcome.Success, subjects, cancellationToken, "policy_rule", ruleId);
    }

    /// <summary>廃止の起案を承認し、規則を廃止する。</summary>
    public async Task ApproveRetirementAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireHrAdminPersonAsync(viewer, ApproveRetirementAction, ruleId, [], cancellationToken);

        var stored = await _store.FindAsync(viewer, ruleId, cancellationToken);
        if (stored is null || !stored.IsRetirementProposed)
        {
            await _audit.RecordAsync(viewer, ApproveRetirementAction, AuditOutcome.Failed, [], cancellationToken, "policy_rule", ruleId);
            throw new InvalidOperationException($"廃止の承認待ちの規則 {ruleId} がありません。");
        }

        var subjects = SubjectsOf(stored.Rule);
        await RequireDifferentPersonAsync(viewer, ApproveRetirementAction, stored.RetirementProposedByPersonId!.Value, stored.Rule, subjects, cancellationToken);
        await RequireNotSubjectAsync(viewer, ApproveRetirementAction, stored.Rule, subjects, cancellationToken);

        if (!await _store.ApproveRetirementAsync(viewer, ruleId, cancellationToken))
        {
            await _audit.RecordAsync(viewer, ApproveRetirementAction, AuditOutcome.Failed, subjects, cancellationToken, "policy_rule", ruleId);
            throw new InvalidOperationException($"規則 {ruleId} の廃止を承認できませんでした（状態が変わった可能性があります）。");
        }

        await _audit.RecordAsync(viewer, ApproveRetirementAction, AuditOutcome.Success, subjects, cancellationToken, "policy_rule", ruleId);
    }

    private static Guid[] SubjectsOf(PolicyRule rule) => rule.PersonId is { } person ? [person] : [];

    private async Task RequireHrAdminPersonAsync(
        ViewerContext viewer,
        string action,
        Guid ruleId,
        IReadOnlyCollection<Guid> subjects,
        CancellationToken cancellationToken)
    {
        // 起案者・承認者を人として記録するため、人に紐付かない操作者（社内システム・jin-ji 自身）は扱えない
        if (!viewer.IsHrAdmin || viewer.EffectivePersonId is null)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, subjects, cancellationToken, "policy_rule", ruleId);
            throw new AccessDeniedException("ポリシーの起案・承認・廃止は人事管理者（人）だけが行えます。");
        }
    }

    /// <summary>職務分掌: 承認者は起案者（廃止なら廃止の起案者）と別人。</summary>
    private async Task RequireDifferentPersonAsync(
        ViewerContext viewer,
        string action,
        Guid proposerPersonId,
        PolicyRule rule,
        IReadOnlyCollection<Guid> subjects,
        CancellationToken cancellationToken)
    {
        if (proposerPersonId == viewer.EffectivePersonId)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, subjects, cancellationToken, "policy_rule", rule.Id);
            throw new AccessDeniedException("起案者は自分の起案を承認できません（職務分掌）。");
        }
    }

    /// <summary>個人単位の例外の対象者本人は、その例外の起案・承認・廃止に関われない（公開範囲を本人の裁量にしない）。</summary>
    private async Task RequireNotSubjectAsync(
        ViewerContext viewer,
        string action,
        PolicyRule rule,
        IReadOnlyCollection<Guid> subjects,
        CancellationToken cancellationToken)
    {
        if (rule.TargetKind == PolicyTargetKind.Person && rule.PersonId == viewer.EffectivePersonId)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, subjects, cancellationToken, "policy_rule", rule.Id);
            throw new AccessDeniedException("個人単位の例外の対象者本人は、その例外の起案・承認・廃止を行えません。");
        }
    }

    private async Task ValidateAsync(
        ViewerContext viewer,
        string action,
        PolicyRule rule,
        IReadOnlyCollection<Guid> subjects,
        CancellationToken cancellationToken)
    {
        try
        {
            rule.Validate();
            if (rule.TargetKind == PolicyTargetKind.Person || rule.Allow.Kind == RelationScopeKind.SameLevel)
            {
                var today = BusinessDate.Today(_timeProvider);
                var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);

                // 段のキーの書き間違いを防ぐ: どの段の定義にも無い段は指定できない（指定しても誰も満たさないので広がりはしない）
                if (rule.Allow.Kind == RelationScopeKind.SameLevel && snapshot.LevelSchemes.All(s => s.Find(rule.Allow.LevelKey!) is null))
                {
                    throw new PolicyValidationException($"規則 {rule.Id}: 段 {rule.Allow.LevelKey} はどの段の定義にもありません。");
                }

                if (rule.TargetKind == PolicyTargetKind.Person)
                {
                    var current = await _store.LoadActiveAsync(viewer, cancellationToken);
                    PersonExceptionValidator.EnsureNarrowing(current, new RelationEvaluator(snapshot), rule);
                }
            }
        }
        catch (PolicyValidationException)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, subjects, cancellationToken, "policy_rule", rule.Id);
            throw;
        }
    }
}
