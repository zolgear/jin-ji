namespace JinJi.Policy;

/// <summary>
/// 項目ごとの公開範囲の判定（アーキテクチャ概要 §5.2〜5.3）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>既定は全項目 group 公開。規則は狭める方向にだけ働く</item>
/// <item>合成は「最も狭いものが勝つ」: 項目に当てはまるすべての規則（全員・組織・個人単位の例外）を満たしたときだけ見える（積）。
/// 個人単位の例外が仮に広い範囲を指定していても、積なので他の規則より広がることはない</item>
/// <item>本人は公開範囲の制限を受けずに自分の項目を見られる（人事管理者だけの項目は除く）</item>
/// <item>人事管理者は公開範囲の制限を受けない</item>
/// <item>最終的な項目 = 上の結果 ∩ クライアントの項目上限（本人・人事管理者にも上限は効く）</item>
/// <item>関係と組織単位の規則は、渡されたスナップショット（呼び出し側は常に今日時点のものを渡す）で評価する。
/// 過去の時点を見るときも、過去の同僚関係で現在の連絡先が見えることはない</item>
/// </list>
/// </remarks>
public sealed class PolicyEvaluator
{
    private readonly PolicySet _policies;
    private readonly RelationEvaluator _relations;

    public PolicyEvaluator(PolicySet policies, RelationEvaluator relations)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(relations);
        _policies = policies;
        _relations = relations;
    }

    /// <summary>閲覧者が対象者について見られる項目。</summary>
    public IReadOnlySet<string> VisibleFields(ViewerContext viewer, Guid targetPersonId)
    {
        ArgumentNullException.ThrowIfNull(viewer);

        IEnumerable<string> candidates;
        if (viewer.IsHrAdmin)
        {
            candidates = PersonFields.All;
        }
        else if (viewer.EffectivePersonId == targetPersonId)
        {
            candidates = PersonFields.PolicyControllable;
        }
        else
        {
            var applicable = ApplicableRules(targetPersonId);
            candidates = PersonFields.PolicyControllable.Where(field =>
                applicable.Where(r => r.Covers(field)).All(r => _relations.Satisfies(viewer.EffectivePersonId, targetPersonId, r.Allow)));
        }

        return candidates.Where(viewer.FieldLimit.Allows).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>対象者に当てはまる規則（全員・所属組織・個人単位の例外）。</summary>
    public IReadOnlyList<PolicyRule> ApplicableRules(Guid targetPersonId) =>
        [.. _policies.Rules.Where(r => AppliesTo(r, targetPersonId))];

    private bool AppliesTo(PolicyRule rule, Guid targetPersonId)
    {
        var snapshot = _relations.Snapshot;
        return rule.TargetKind switch
        {
            PolicyTargetKind.All => true,
            PolicyTargetKind.Person => rule.PersonId == targetPersonId,
            PolicyTargetKind.OrgUnit => snapshot.AssignmentsOf(targetPersonId).Any(a =>
                a.OrgUnitId == rule.OrgUnitId
                || (rule.IncludeDescendants && rule.OrgUnitId is { } root && snapshot.IsSelfOrDescendantOf(a.OrgUnitId, root))),
            _ => false,
        };
    }
}

/// <summary>
/// 個人単位の例外の検査。例外は人事管理者が起案し承認を経て設定するもので、狭める方向だけ許す（§5.3）。
/// 公開範囲は組織のルールとして決め、本人が自分の公開範囲を変える機能は持たない。
/// </summary>
public static class PersonExceptionValidator
{
    /// <summary>
    /// 例外の候補が、その人に当てはまる他の規則（全員・組織）のどれよりも広くないかを調べ、違反を返す（空なら問題なし）。
    /// 包含関係が規則から言えない組み合わせ（例: 他の規則が direct_reports で例外が same_level(section)、
    /// 法人ごとに段の順位が違って same_level 同士の包含が言えない場合）も「広げる」とみなして拒否する。
    /// </summary>
    public static IReadOnlyList<string> FindWidening(PolicySet current, RelationEvaluator relations, PolicyRule candidate)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(candidate);
        candidate.Validate();
        if (candidate.TargetKind != PolicyTargetKind.Person || candidate.PersonId is not { } personId)
        {
            throw new PolicyValidationException("個人単位の例外ではありません。");
        }

        var evaluator = new PolicyEvaluator(current, relations);
        var baseRules = evaluator.ApplicableRules(personId).Where(r => r.TargetKind != PolicyTargetKind.Person).ToList();
        var fields = candidate.Fields.Contains(PersonFields.Wildcard) ? PersonFields.PolicyControllable : candidate.Fields;

        var violations = new List<string>();
        foreach (var field in fields.Order(StringComparer.Ordinal))
        {
            foreach (var rule in baseRules.Where(r => r.Covers(field)))
            {
                if (!RelationScopes.IsNarrowerOrEqual(candidate.Allow, rule.Allow, relations.Snapshot.LevelSchemes))
                {
                    violations.Add($"{field}: 例外 {candidate.Allow.ToCode()} は規則 {rule.Id} の {rule.Allow.ToCode()} より狭くありません");
                }
            }
        }

        return violations;
    }

    /// <summary>違反があれば例外にする。</summary>
    public static void EnsureNarrowing(PolicySet current, RelationEvaluator relations, PolicyRule candidate)
    {
        var violations = FindWidening(current, relations, candidate);
        if (violations.Count > 0)
        {
            throw new PolicyValidationException("個人単位の例外は公開範囲を狭める方向にしか設定できません: " + string.Join("; ", violations));
        }
    }
}
