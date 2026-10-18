namespace JinJi.Policy;

/// <summary>
/// 当日の所在（項目 presence）の組み込みの公開範囲の規則。外部の提供（IPresenceProvider）の所在は、全社公開レベルを超える行動の情報に近いので、
/// 「全社公開を既定にポリシーで狭める」（ADR-0008）の上で最初から狭い規則を置く。既定は本人と同じ部（<c>same_level(department)</c>）で、
/// 設定 <c>JinJi:Directory:PresenceVisibilityScope</c> で変えられる。ポリシー（policy_rule）でさらに狭められる（合成は積）。
/// 人事管理者の全項目の閲覧はこの項目には効かない（<see cref="PolicyEvaluator.VisibleFields"/>）。
/// </summary>
public static class PresencePolicy
{
    /// <summary>組み込みの規則の ID（固定。ポリシーの表には無い）。</summary>
    public static readonly Guid BuiltInRuleId = new("0b9f6c1e-2d8a-4c57-9a1e-5f0c3d7a2b10");

    /// <summary>既定の公開範囲（本人と同じ部）。</summary>
    public const string DefaultScope = "same_level(department)";

    /// <summary>有効なポリシーに組み込みの規則を足す（所在を投影するときだけ使う）。</summary>
    public static PolicySet WithBuiltInRule(PolicySet policies, string scope)
    {
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(scope);
        return new PolicySet(
        [
            .. policies.Rules,
            new PolicyRule
            {
                Id = BuiltInRuleId,
                TargetKind = PolicyTargetKind.All,
                Fields = new HashSet<string>(StringComparer.Ordinal) { PersonFields.Presence },
                Allow = RelationScopes.Parse(scope),
            },
        ]);
    }
}
