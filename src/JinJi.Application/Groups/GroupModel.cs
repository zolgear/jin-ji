using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Groups;

/// <summary>グループ（ある日に有効な版と今の設定）。</summary>
/// <param name="ValidUntil">終了日（この日を含まない）。null は終わりなし。</param>
public sealed record GroupRecord(
    Guid Id,
    string Name,
    GroupKind Kind,
    Guid? OwnerOrgUnitId,
    GroupVisibility Visibility,
    bool Managed,
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    Guid CreatedBy,
    DateTimeOffset CreatedAt,
    int MemberCount);

/// <summary>グループのメンバー（ある日に有効な行）。</summary>
/// <param name="Until">期間の終わり（この日を含まない）。null は終わりなし。</param>
public sealed record GroupMemberRecord(Guid PersonId, GroupRole Role, DateOnly From, DateOnly? Until, Guid AddedBy);

/// <summary>グループとメンバー。</summary>
public sealed record GroupDetail(GroupRecord Group, IReadOnlyList<GroupMemberRecord> Members);

/// <summary>グループの一覧の絞り込み。</summary>
/// <param name="PersonId">その人が今日のメンバーであるグループだけ。</param>
/// <param name="OwnerOrgUnitId">所管組織で絞る。</param>
public sealed record GroupQuery(GroupKind? Kind = null, Guid? PersonId = null, Guid? OwnerOrgUnitId = null, bool ManagedOnly = false, int Limit = 200);

/// <summary>グループの作成の入力。</summary>
public sealed record GroupCreateInput(string Name, GroupKind Kind, Guid? OwnerOrgUnitId, GroupVisibility Visibility, DateOnly? ValidFrom);

/// <summary>グループの属性の変更（null の項目は変えない。所管組織を外すなら <see cref="ClearOwnerOrgUnit"/>）。</summary>
public sealed record GroupUpdateInput(string? Name, GroupKind? Kind, Guid? OwnerOrgUnitId, bool ClearOwnerOrgUnit = false);

/// <summary>
/// グループの保存（Infrastructure が実装する。0022）。誰が書けるかの最終的な線は DB（RLS とトリガー）が引き、DB が拒否した書き込みは
/// <see cref="GroupException"/>（<see cref="GroupError.Forbidden"/>）にする。
/// </summary>
public interface IGroupStore
{
    /// <summary>その日に有効な、閲覧者に見えるグループ（名前の順）。</summary>
    Task<IReadOnlyList<GroupRecord>> ListAsync(ViewerContext viewer, GroupQuery query, DateOnly date, CancellationToken cancellationToken);

    /// <summary>グループとその日のメンバー。見えない・その日に無ければ null。</summary>
    Task<GroupDetail?> FindAsync(ViewerContext viewer, Guid id, DateOnly date, CancellationToken cancellationToken);

    /// <summary>グループを作る（設定・最初の版・作成者をオーナーにしたメンバーの行を 1 トランザクションで）。</summary>
    Task CreateAsync(ViewerContext viewer, Guid id, GroupCreateInput input, DateOnly from, CancellationToken cancellationToken);

    /// <summary>その日から属性を変える（その日に有効な版を分ける）。版が無ければ false。</summary>
    Task<bool> ReviseAsync(ViewerContext viewer, Guid id, GroupUpdateInput input, DateOnly date, CancellationToken cancellationToken);

    /// <summary>その日でグループを終える（版とメンバーの期間をその日の前日までにする）。版が無ければ false。</summary>
    Task<bool> CloseAsync(ViewerContext viewer, Guid id, DateOnly date, CancellationToken cancellationToken);

    /// <summary>公開範囲・管理対象を変える（null は変えない）。</summary>
    Task<bool> SetSettingsAsync(ViewerContext viewer, Guid id, GroupVisibility? visibility, bool? managed, CancellationToken cancellationToken);

    /// <summary>メンバーを加える。同じ人の期間が重なれば false。</summary>
    Task<bool> AddMemberAsync(ViewerContext viewer, Guid id, Guid personId, GroupRole role, DateOnly from, DateOnly? until, CancellationToken cancellationToken);

    /// <summary>その日からメンバーの役割を変える（その日に有効な行を分ける）。行が無ければ false。</summary>
    Task<bool> ChangeRoleAsync(ViewerContext viewer, Guid id, Guid personId, GroupRole role, DateOnly date, CancellationToken cancellationToken);

    /// <summary>その日でメンバーから外す（その日に有効な行の期間を前日までにする）。行が無ければ false。</summary>
    Task<bool> EndMemberAsync(ViewerContext viewer, Guid id, Guid personId, DateOnly date, CancellationToken cancellationToken);
}

/// <summary>グループの操作の誤りの種類。</summary>
public enum GroupError
{
    NotFound,
    Conflict,
    Invalid,
    Forbidden,
}

/// <summary>グループの操作を拒否した（理由は応答に出してよい範囲の文言）。</summary>
public sealed class GroupException : Exception
{
    public GroupException()
    {
    }

    public GroupException(string message)
        : base(message)
    {
    }

    public GroupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GroupException(GroupError error, string message)
        : base(message)
    {
        Error = error;
    }

    public GroupError Error { get; }
}
