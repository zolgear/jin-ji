using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Organization;

/// <summary>
/// 組織の階層の定義（段）の起案と承認。段の定義は same_level の範囲を決めるので、変更は公開範囲の変更として承認を経て反映する（§5.5）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>起案・承認は人事管理者（人）だけ。起案者 ≠ 承認者（職務分掌。DB の CHECK 制約でも守る）</item>
/// <item>定義は範囲（法人、またはグループ共通の既定）ごとに丸ごと起案し、承認でその範囲の有効な定義を置き換える</item>
/// <item>起案時と承認時の両方で検査する（その間にポリシーや組織が変わり得るため）:
/// 形（キー・表示名・順位が 1 から連続）、法人の存在、ポリシー（起案中・承認済み）から参照されている段の削除の禁止、
/// 組織が使っている段の削除の禁止、順位の変更で組織の親子の順位が逆転しないこと（今日と、今日より後に組織が変わる日のすべてで）</item>
/// <item>拒否は監査ログに denied として残す。承認したら同一プロセスのスナップショットのキャッシュを捨てる</item>
/// </list>
/// </remarks>
public sealed class OrgLevelAdministrationService
{
    public const string ProposeAction = "org_level.propose";
    public const string ApproveAction = "org_level.approve";
    private const string TargetType = "org_level_scheme";

    private readonly IOrgLevelStore _store;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly IPolicyRuleStore _policies;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly IOrgDataChangeNotifier? _changes;

    public OrgLevelAdministrationService(
        IOrgLevelStore store,
        IOrgSnapshotProvider snapshots,
        IPolicyRuleStore policies,
        AuditWriter audit,
        TimeProvider timeProvider,
        IOrgDataChangeNotifier? changes = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _snapshots = snapshots;
        _policies = policies;
        _audit = audit;
        _timeProvider = timeProvider;
        _changes = changes;
    }

    /// <summary>段の定義を起案する。</summary>
    public async Task ProposeAsync(ViewerContext viewer, Guid schemeId, OrgLevelScheme scheme, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(scheme);
        await RequireHrAdminPersonAsync(viewer, ProposeAction, schemeId, cancellationToken);
        await ValidateAsync(viewer, ProposeAction, schemeId, scheme, cancellationToken);

        await _store.InsertProposalAsync(viewer, schemeId, scheme, cancellationToken);
        await _audit.RecordAsync(viewer, ProposeAction, AuditOutcome.Success, [], cancellationToken, TargetType, schemeId);
    }

    /// <summary>起案中の段の定義を承認し、同じ範囲の有効な定義を置き換える。</summary>
    public async Task ApproveAsync(ViewerContext viewer, Guid schemeId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireHrAdminPersonAsync(viewer, ApproveAction, schemeId, cancellationToken);

        var stored = await _store.FindAsync(viewer, schemeId, cancellationToken);
        if (stored is null || stored.Status != OrgLevelSchemeStatus.Proposed)
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Failed, [], cancellationToken, TargetType, schemeId);
            throw new InvalidOperationException($"起案中の段の定義 {schemeId} がありません。");
        }

        if (stored.ProposedByPersonId == viewer.EffectivePersonId)
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Denied, [], cancellationToken, TargetType, schemeId);
            throw new AccessDeniedException("起案者は自分の起案を承認できません（職務分掌）。");
        }

        await ValidateAsync(viewer, ApproveAction, schemeId, stored.Scheme, cancellationToken);

        if (!await _store.ApproveAsync(viewer, schemeId, cancellationToken))
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Failed, [], cancellationToken, TargetType, schemeId);
            throw new InvalidOperationException($"段の定義 {schemeId} を承認できませんでした（状態が変わった可能性があります）。");
        }

        _changes?.NotifyChanged();
        await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Success, [], cancellationToken, TargetType, schemeId);
    }

    /// <summary>
    /// 有効な定義のうち、範囲 <paramref name="legalEntityId"/>（null は既定）を <paramref name="replacement"/> で置き換えた定義の集合。
    /// </summary>
    public static IReadOnlyList<OrgLevelScheme> Replace(IEnumerable<OrgLevelScheme> current, Guid? legalEntityId, OrgLevelScheme replacement)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(replacement);
        return [.. current.Where(s => s.LegalEntityId != legalEntityId), replacement];
    }

    private async Task RequireHrAdminPersonAsync(ViewerContext viewer, string action, Guid schemeId, CancellationToken cancellationToken)
    {
        if (!viewer.IsHrAdmin || viewer.EffectivePersonId is null)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, schemeId);
            throw new AccessDeniedException("段の定義の起案・承認は人事管理者（人）だけが行えます。");
        }
    }

    private async Task ValidateAsync(ViewerContext viewer, string action, Guid schemeId, OrgLevelScheme scheme, CancellationToken cancellationToken)
    {
        try
        {
            scheme.Validate();
            var today = BusinessDate.Today(_timeProvider);
            var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
            if (scheme.LegalEntityId is { } entity && snapshot.FindLegalEntity(entity) is null)
            {
                throw new OrgLevelValidationException($"法人 {entity} がありません。");
            }

            // 範囲の現在の定義（法人の定義が無ければ、その法人に効いている既定）から消える段
            var current = scheme.LegalEntityId is { } id ? snapshot.LevelSchemeOf(id) : snapshot.LevelSchemes.FirstOrDefault(s => s.LegalEntityId is null);
            var removed = (current?.Levels.Select(l => l.Key) ?? []).Where(k => scheme.Find(k) is null).ToHashSet(StringComparer.Ordinal);

            var referenced = (await _policies.ListOpenAsync(viewer, cancellationToken))
                .Where(r => r.Allow.Kind == RelationScopeKind.SameLevel && removed.Contains(r.Allow.LevelKey!))
                .Select(r => $"{r.Allow.LevelKey}（規則 {r.Id}）")
                .ToList();
            if (referenced.Count > 0)
            {
                throw new OrgLevelValidationException("ポリシーから参照されている段は削除できません: " + string.Join(", ", referenced));
            }

            var replaced = Replace(snapshot.LevelSchemes, scheme.LegalEntityId, scheme);
            var dates = await _store.GetOrgChangeDatesAfterAsync(viewer, today, cancellationToken);
            foreach (var date in dates.Prepend(today))
            {
                var data = date == today ? snapshot : await _snapshots.LoadAsync(viewer, date, today, cancellationToken);
                EnsureCompatible(data, scheme, removed, replaced, date);
            }
        }
        catch (OrgLevelValidationException)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, schemeId);
            throw;
        }
    }

    /// <summary>その日の組織が、新しい定義でも段を解決でき、親子の順位が逆転しないこと。</summary>
    private static void EnsureCompatible(OrgSnapshot data, OrgLevelScheme scheme, IReadOnlySet<string> removed, IReadOnlyList<OrgLevelScheme> replaced, DateOnly date)
    {
        bool Affected(Guid entity) => scheme.LegalEntityId is { } id ? entity == id : data.LevelSchemeOf(entity).LegalEntityId is null;

        var inUse = data.Units.Where(u => u.LevelKey is { } k && removed.Contains(k) && Affected(u.LegalEntityId)).Select(u => $"{u.Code}（{u.LevelKey}）").ToList();
        if (inUse.Count > 0)
        {
            throw new OrgLevelValidationException($"{date:yyyy-MM-dd} 時点の組織が使っている段は削除できません: " + string.Join(", ", inUse));
        }

        var before = data.LevelOrderViolations().ToHashSet();
        var after = data.WithLevelSchemes(replaced);
        var broken = after.LevelOrderViolations().Where(v => !before.Contains(v))
            .Select(v => $"{after.FindUnit(v.Unit)!.Code} の上位 {after.FindUnit(v.Ancestor)!.Code}")
            .ToList();
        if (broken.Count > 0)
        {
            throw new OrgLevelValidationException($"{date:yyyy-MM-dd} 時点の組織で、子の段の順位が親より下でなくなります: " + string.Join(", ", broken));
        }
    }
}
