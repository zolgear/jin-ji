using JinJi.Domain.Organization;

namespace JinJi.Policy;

/// <summary>関係スコープの種類（policy_rule.allow_scope の値）。</summary>
public enum RelationScopeKind
{
    /// <summary>誰にも公開しない（本人と人事管理者は除く）。</summary>
    None,

    /// <summary>本人だけ。</summary>
    Self,

    /// <summary>同じ〈段〉内（段は <see cref="RelationScope.LevelKey"/>。例: 課内・部内・事業部内）。</summary>
    SameLevel,

    /// <summary>法人内（出向先を含む）。</summary>
    LegalEntity,

    /// <summary>グループ全体（jin-ji の利用者すべて）。既定の公開範囲。</summary>
    Group,

    /// <summary>対象者が閲覧者の直属の部下。</summary>
    DirectReports,

    /// <summary>対象者が閲覧者の配下（部下・その部下…）。</summary>
    Subordinates,

    /// <summary>対象者が閲覧者の上長系列。</summary>
    ManagersChain,
}

/// <summary>
/// 関係スコープ（閲覧者から見た対象者との関係。アーキテクチャ概要 §5.1）。ポリシーの allow に指定する。
/// </summary>
/// <remarks>
/// 課内・部内のような組織の段による範囲は固定せず、「同じ〈段〉内」= <c>same_level(段のキー)</c> で表す。段は法人ごとの段の定義で解決する。
/// 表記（<see cref="RelationScopes.ToCode"/>）は <c>none</c>・<c>self</c>・<c>same_level(section)</c>・<c>legal_entity</c>・<c>group</c>・
/// <c>direct_reports</c>・<c>subordinates</c>・<c>managers_chain</c>。
/// </remarks>
public readonly record struct RelationScope
{
    private RelationScope(RelationScopeKind kind, string? levelKey)
    {
        Kind = kind;
        LevelKey = levelKey;
    }

    public RelationScopeKind Kind { get; }

    /// <summary><see cref="RelationScopeKind.SameLevel"/> の段のキー。それ以外は null。</summary>
    public string? LevelKey { get; }

    public static RelationScope None { get; } = new(RelationScopeKind.None, null);

    public static RelationScope Self { get; } = new(RelationScopeKind.Self, null);

    public static RelationScope LegalEntity { get; } = new(RelationScopeKind.LegalEntity, null);

    public static RelationScope Group { get; } = new(RelationScopeKind.Group, null);

    public static RelationScope DirectReports { get; } = new(RelationScopeKind.DirectReports, null);

    public static RelationScope Subordinates { get; } = new(RelationScopeKind.Subordinates, null);

    public static RelationScope ManagersChain { get; } = new(RelationScopeKind.ManagersChain, null);

    /// <summary>同じ〈段〉内。</summary>
    public static RelationScope SameLevel(string levelKey)
    {
        if (!OrgLevelScheme.IsValidKey(levelKey))
        {
            throw new ArgumentException($"段のキーの形式が不正です: {levelKey}", nameof(levelKey));
        }

        return new RelationScope(RelationScopeKind.SameLevel, levelKey);
    }

    /// <summary>種類と段のキーから作る（DB の allow_scope・allow_level_key）。</summary>
    public static RelationScope Of(RelationScopeKind kind, string? levelKey)
    {
        if (kind == RelationScopeKind.SameLevel)
        {
            return SameLevel(levelKey ?? throw new ArgumentException("same_level には段のキーが要ります。", nameof(levelKey)));
        }

        if (levelKey is not null)
        {
            throw new ArgumentException($"{kind} に段のキーは指定できません。", nameof(levelKey));
        }

        return new RelationScope(kind, null);
    }

    public override string ToString() => this.ToCode();
}

/// <summary>関係スコープの表記（DB・設定での表記）と包含関係。</summary>
public static class RelationScopes
{
    private static readonly Dictionary<RelationScopeKind, string> KindCodes = new()
    {
        [RelationScopeKind.None] = "none",
        [RelationScopeKind.Self] = "self",
        [RelationScopeKind.SameLevel] = "same_level",
        [RelationScopeKind.LegalEntity] = "legal_entity",
        [RelationScopeKind.Group] = "group",
        [RelationScopeKind.DirectReports] = "direct_reports",
        [RelationScopeKind.Subordinates] = "subordinates",
        [RelationScopeKind.ManagersChain] = "managers_chain",
    };

    private static readonly Dictionary<string, RelationScopeKind> KindsByCode = KindCodes.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    /// <summary>種類のコード値（policy_rule.allow_scope）。</summary>
    public static string KindCode(this RelationScope scope) => KindCodes[scope.Kind];

    /// <summary>種類のコード値を解釈する。</summary>
    public static RelationScopeKind ParseKind(string code) =>
        KindsByCode.TryGetValue(code, out var kind) ? kind : throw new ArgumentException($"未知の関係スコープ: {code}", nameof(code));

    /// <summary>表記（例: <c>same_level(section)</c>、<c>group</c>）。</summary>
    public static string ToCode(this RelationScope scope) =>
        scope.Kind == RelationScopeKind.SameLevel ? $"same_level({scope.LevelKey})" : KindCodes[scope.Kind];

    /// <summary>表記を解釈する。</summary>
    public static RelationScope Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (code.StartsWith("same_level(", StringComparison.Ordinal) && code.EndsWith(')'))
        {
            return RelationScope.SameLevel(code["same_level(".Length..^1]);
        }

        return RelationScope.Of(ParseKind(code), null);
    }

    /// <summary>
    /// a が b より狭いか等しい（a で見える閲覧者は必ず b でも見える）ことが規則から言えるか。言えない組み合わせ（例: direct_reports と same_level）は false。
    /// </summary>
    /// <param name="levelSchemes">
    /// 有効な段の定義のすべて（既定と法人ごと）。<c>same_level(x) ⊆ same_level(y)</c> は、x を定義するすべての定義で
    /// y も定義され、y の順位が x の順位以上（上位か等しい）のときだけ言える。法人ごとに順位が違う・y が片方にしか無い場合は言えない（広がらない側に倒す）。
    /// x がどの定義にも無ければ same_level(x) は誰も満たさないので、どの same_level(y) にも含まれる。
    /// </param>
    public static bool IsNarrowerOrEqual(RelationScope a, RelationScope b, IReadOnlyCollection<OrgLevelScheme> levelSchemes)
    {
        ArgumentNullException.ThrowIfNull(levelSchemes);
        if (a == b || a.Kind == RelationScopeKind.None || b.Kind == RelationScopeKind.Group)
        {
            return true;
        }

        if (b.Kind == RelationScopeKind.None)
        {
            return false;
        }

        // 本人はどの公開範囲でも自分の項目を見られる（PolicyEvaluator）ので、self は none 以外のすべてに含まれる
        if (a.Kind == RelationScopeKind.Self)
        {
            return true;
        }

        // 同じ〈段〉内の基準組織は所属組織の法人の組織ツリーの中にあるので、法人内に含まれる
        if (a.Kind == RelationScopeKind.SameLevel && b.Kind == RelationScopeKind.LegalEntity)
        {
            return true;
        }

        if (a.Kind == RelationScopeKind.SameLevel && b.Kind == RelationScopeKind.SameLevel)
        {
            return levelSchemes.All(s =>
                s.Find(a.LevelKey!) is not { } x || (s.Find(b.LevelKey!) is { } y && y.Rank <= x.Rank));
        }

        return a.Kind == RelationScopeKind.DirectReports && b.Kind == RelationScopeKind.Subordinates;
    }
}

/// <summary>
/// 閲覧者と対象者の関係の評価（§5.1）。兼務・出向を含むすべての所属で判定し、いずれかで成り立てば関係ありとする（和集合）。
/// </summary>
public sealed class RelationEvaluator
{
    private readonly OrgSnapshot _snapshot;

    public RelationEvaluator(OrgSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
    }

    public OrgSnapshot Snapshot => _snapshot;

    /// <summary>閲覧者（人が無い場合は null）と対象者の間に関係が成り立つか。</summary>
    public bool Satisfies(Guid? viewerPersonId, Guid targetPersonId, RelationScope scope)
    {
        if (scope.Kind == RelationScopeKind.None)
        {
            return false;
        }

        // jin-ji の利用者はすべてグループ内（人に紐付かない社内システムを含む）
        if (scope.Kind == RelationScopeKind.Group)
        {
            return true;
        }

        if (viewerPersonId is not { } viewer)
        {
            return false;
        }

        if (viewer == targetPersonId && scope.Kind is RelationScopeKind.Self or RelationScopeKind.SameLevel or RelationScopeKind.LegalEntity)
        {
            return true;
        }

        return scope.Kind switch
        {
            RelationScopeKind.Self => false,
            RelationScopeKind.SameLevel => ShareBaseUnit(viewer, targetPersonId, scope.LevelKey!),
            RelationScopeKind.LegalEntity => _snapshot.LegalEntitiesOf(viewer).Overlaps(_snapshot.LegalEntitiesOf(targetPersonId)),
            RelationScopeKind.DirectReports => _snapshot.DirectManagersOf(targetPersonId).Contains(viewer),
            RelationScopeKind.Subordinates => _snapshot.ManagersChainOf(targetPersonId).Contains(viewer),
            RelationScopeKind.ManagersChain => _snapshot.ManagersChainOf(viewer).Contains(targetPersonId),
            _ => false,
        };
    }

    /// <summary>
    /// 閲覧者と対象者の所属のいずれかの組で、段の基準組織が一致するか。段が所属組織の法人の定義に無い所属は一致しない。
    /// 組織ツリーは法人ごとなので、法人の違う所属同士は一致しない。
    /// </summary>
    private bool ShareBaseUnit(Guid viewer, Guid target, string levelKey)
    {
        var targetBases = _snapshot.AssignmentsOf(target)
            .Select(a => _snapshot.BaseUnitOf(a.OrgUnitId, levelKey))
            .OfType<Guid>()
            .ToHashSet();
        return _snapshot.AssignmentsOf(viewer).Any(a => _snapshot.BaseUnitOf(a.OrgUnitId, levelKey) is { } b && targetBases.Contains(b));
    }
}
