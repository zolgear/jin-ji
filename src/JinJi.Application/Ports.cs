using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application;

/// <summary>
/// 組織ツリーのスナップショットの読み込み（Infrastructure が実装する）。
/// </summary>
/// <remarks>
/// 実装は閲覧者のコンテキストを RLS に渡して読み込む。人事管理者以外では、内示の行・過去の氏名の版がそもそも読み込まれない
/// （アプリ側の判定にバグがあっても漏れない）。
/// </remarks>
public interface IOrgSnapshotProvider
{
    /// <param name="viewer">閲覧者（RLS に渡す）。</param>
    /// <param name="asOf">組織・所属・一時停止の基準日。</param>
    /// <param name="attributesAsOf">人の属性（氏名・連絡先等）の版の基準日。人事管理者以外は常に今日。</param>
    Task<OrgSnapshot> LoadAsync(ViewerContext viewer, DateOnly asOf, DateOnly attributesAsOf, CancellationToken cancellationToken);
}

/// <summary>
/// 組織・人事データの世代（Infrastructure が実装する）。キャッシュしたスナップショットが古くなっていないかの確認に使う。
/// </summary>
/// <remarks>
/// 値そのものに意味は無く、人事データが変わったら（高い確率で）違う値になることだけを求める。
/// 閲覧者を RLS に渡して読むので、閲覧者に見えない行（内示など）の変化では値が変わらなくてよい。
/// </remarks>
public interface IOrgDataVersionSource
{
    Task<string> GetVersionAsync(ViewerContext viewer, CancellationToken cancellationToken);
}

/// <summary>
/// 同一プロセス内で人事データ（組織・人・所属・一時停止・発令）を書き込んだことの通知。
/// 書き込みを確定（コミット）した後に呼ぶ。スナップショットのキャッシュを捨てる。
/// </summary>
public interface IOrgDataChangeNotifier
{
    void NotifyChanged();
}

/// <summary>段の定義の状態（org_level_scheme.status）。</summary>
public enum OrgLevelSchemeStatus
{
    /// <summary>起案中。</summary>
    Proposed,

    /// <summary>承認済み（有効。範囲ごとに高々 1 つ）。</summary>
    Approved,

    /// <summary>却下。</summary>
    Rejected,

    /// <summary>新しい定義の承認で置き換えられた。</summary>
    Superseded,
}

/// <summary>保存された段の定義と、起案・承認の情報。</summary>
/// <param name="ProposedByPersonId">起案者。マイグレーションで作った初期の定義は null。</param>
public sealed record StoredOrgLevelScheme(
    Guid Id,
    OrgLevelScheme Scheme,
    OrgLevelSchemeStatus Status,
    Guid? ProposedByPersonId,
    Guid? DecidedByPersonId);

/// <summary>段の定義の保存先（Infrastructure が実装する）。有効な定義の読み込みはスナップショット（<see cref="IOrgSnapshotProvider"/>）が行う。</summary>
public interface IOrgLevelStore
{
    Task<StoredOrgLevelScheme?> FindAsync(ViewerContext viewer, Guid schemeId, CancellationToken cancellationToken);

    /// <summary>起案として保存する（起案者は閲覧者の人）。</summary>
    Task InsertProposalAsync(ViewerContext viewer, Guid schemeId, OrgLevelScheme scheme, CancellationToken cancellationToken);

    /// <summary>
    /// 起案中の定義を承認し、同じ範囲（法人、または既定）の有効な定義を置き換える（1 トランザクション。承認者は閲覧者の人）。起案中でなければ false。
    /// </summary>
    Task<bool> ApproveAsync(ViewerContext viewer, Guid schemeId, CancellationToken cancellationToken);

    /// <summary>組織の版（現在記録中）の有効期間の開始日のうち、<paramref name="after"/> より後のもの（昇順・重複なし）。</summary>
    Task<IReadOnlyList<DateOnly>> GetOrgChangeDatesAfterAsync(ViewerContext viewer, DateOnly after, CancellationToken cancellationToken);
}

/// <summary>時点の目盛りの種類。</summary>
public enum DirectoryMilestoneKind
{
    /// <summary>組織の版が変わった日（組織の新設・廃止・名称や上位組織・組織長の変更）。</summary>
    OrgChange,

    /// <summary>一斉異動の日（同じ発令日の異動・役職の任免・兼務・出向の発令が一定件数以上）。</summary>
    MassAction,
}

/// <summary>時点の目盛り（日付と種類だけ。件数や対象は持たない）。</summary>
public sealed record DirectoryMilestone(DateOnly Date, DirectoryMilestoneKind Kind);

/// <summary>組織改編・一斉異動の日付の読み込み（Infrastructure が実装する）。</summary>
/// <remarks>
/// 実装は閲覧者を RLS に渡して読む（人事管理者以外には、有効期間の開始が今日より後の組織の版・内示の発令の行がそもそも見えない）。
/// 見えている行の終了日（列の値）は RLS では隠せないので、引数 until で日付の上限を引く（呼び出し側も重ねて絞る）。
/// </remarks>
public interface IDirectoryCalendarStore
{
    /// <param name="until">この日以前の日付だけを返す（人事管理者以外は業務日付）。null なら上限なし。</param>
    /// <param name="massActionThreshold">一斉異動とみなす発令の件数の下限。</param>
    Task<IReadOnlyList<DirectoryMilestone>> GetMilestonesAsync(ViewerContext viewer, DateOnly? until, int massActionThreshold, CancellationToken cancellationToken);
}

/// <summary>ポリシーの状態（policy_rule.status）。</summary>
public enum PolicyRuleStatus
{
    Proposed,
    Approved,
    Rejected,
    Retired,
}

/// <summary>保存されたポリシーの規則と、起案・承認・廃止の起案の情報。</summary>
/// <param name="RetirementProposedByPersonId">廃止の起案者。廃止は起案 → 別人の承認で反映し、承認されるまで規則は有効のまま。</param>
public sealed record StoredPolicyRule(
    PolicyRule Rule,
    PolicyRuleStatus Status,
    Guid ProposedByPersonId,
    Guid? DecidedByPersonId,
    Guid? RetirementProposedByPersonId = null)
{
    /// <summary>廃止の承認待ち（規則はまだ有効）。</summary>
    public bool IsRetirementProposed => Status == PolicyRuleStatus.Approved && RetirementProposedByPersonId is not null;
}

/// <summary>ポリシーの保存先（Infrastructure が実装する）。</summary>
public interface IPolicyRuleStore
{
    /// <summary>承認済みの規則。</summary>
    Task<PolicySet> LoadActiveAsync(ViewerContext viewer, CancellationToken cancellationToken);

    /// <summary>起案中と承認済みの規則（段の定義の変更で、参照されている段を消さないための検査に使う）。</summary>
    Task<IReadOnlyList<PolicyRule>> ListOpenAsync(ViewerContext viewer, CancellationToken cancellationToken);

    Task<StoredPolicyRule?> FindAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken);

    /// <summary>起案として保存する（起案者は閲覧者の人）。</summary>
    Task InsertProposalAsync(ViewerContext viewer, PolicyRule rule, CancellationToken cancellationToken);

    /// <summary>起案中の規則を承認済みにする（承認者は閲覧者の人）。起案中でなければ false。</summary>
    Task<bool> ApproveAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken);

    /// <summary>承認済みの規則の廃止を起案する（起案者は閲覧者の人）。承認済みでないか、既に廃止の起案があれば false。</summary>
    Task<bool> ProposeRetirementAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken);

    /// <summary>廃止の起案を承認し、規則を廃止する（承認者は閲覧者の人）。廃止の承認待ちでなければ false。</summary>
    Task<bool> ApproveRetirementAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken);
}

/// <summary>認可で拒否された（監査ログには denied として記録済み）。</summary>
public sealed class AccessDeniedException : Exception
{
    public AccessDeniedException()
    {
    }

    public AccessDeniedException(string message)
        : base(message)
    {
    }

    public AccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
