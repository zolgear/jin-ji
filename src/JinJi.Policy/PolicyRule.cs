namespace JinJi.Policy;

/// <summary>ポリシーの適用対象の種類（policy_rule.target_kind）。</summary>
public enum PolicyTargetKind
{
    /// <summary>全員（項目単位の制限）。</summary>
    All,

    /// <summary>組織に所属する人（組織単位の制限。兼務を含む）。</summary>
    OrgUnit,

    /// <summary>特定の人（個人単位の例外）。人事管理者が起案し承認を経て設定する。狭める方向だけ。本人は起案・承認に関われない。</summary>
    Person,
}

/// <summary>ポリシーの定義が不正（未知の項目、個人単位の例外で範囲を広げる等）。</summary>
public sealed class PolicyValidationException : Exception
{
    public PolicyValidationException()
    {
    }

    public PolicyValidationException(string message)
        : base(message)
    {
    }

    public PolicyValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 公開範囲のポリシーの 1 規則（アーキテクチャ概要 §5.2）。既定（全項目 group 公開）は規則として持たない。
/// </summary>
public sealed record PolicyRule
{
    public required Guid Id { get; init; }

    public required PolicyTargetKind TargetKind { get; init; }

    /// <summary>組織単位の規則の対象組織。</summary>
    public Guid? OrgUnitId { get; init; }

    /// <summary>組織単位の規則を配下の組織にも適用するか。</summary>
    public bool IncludeDescendants { get; init; } = true;

    /// <summary>個人単位の例外の対象者。</summary>
    public Guid? PersonId { get; init; }

    /// <summary>対象の項目名（<see cref="PersonFields"/>）。"*" は全項目。</summary>
    public required IReadOnlySet<string> Fields { get; init; }

    /// <summary>公開範囲。</summary>
    public required RelationScope Allow { get; init; }

    /// <summary>規則が項目を対象にするか。</summary>
    public bool Covers(string field) => Fields.Contains(PersonFields.Wildcard) || Fields.Contains(field);

    /// <summary>規則の形を検査する（対象の整合・項目名）。</summary>
    public void Validate()
    {
        var targetOk = TargetKind switch
        {
            PolicyTargetKind.All => OrgUnitId is null && PersonId is null,
            PolicyTargetKind.OrgUnit => OrgUnitId is not null && PersonId is null,
            PolicyTargetKind.Person => PersonId is not null && OrgUnitId is null,
            _ => false,
        };
        if (!targetOk)
        {
            throw new PolicyValidationException($"規則 {Id}: 対象の種類 {TargetKind} と対象の組織・人が整合しません。");
        }

        if (Fields.Count == 0)
        {
            throw new PolicyValidationException($"規則 {Id}: 項目がありません。");
        }

        var invalid = Fields.Where(f => f != PersonFields.Wildcard && !PersonFields.PolicyControllable.Contains(f)).ToList();
        if (invalid.Count > 0)
        {
            throw new PolicyValidationException($"規則 {Id}: ポリシーで指定できない項目です: {string.Join(", ", invalid)}");
        }
    }
}

/// <summary>有効な（承認済みの）規則の集合。</summary>
public sealed class PolicySet
{
    public PolicySet(IEnumerable<PolicyRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        Rules = [.. rules];
        foreach (var rule in Rules)
        {
            rule.Validate();
        }
    }

    /// <summary>規則なし（全項目 group 公開）。</summary>
    public static PolicySet Empty { get; } = new([]);

    public IReadOnlyList<PolicyRule> Rules { get; }
}
