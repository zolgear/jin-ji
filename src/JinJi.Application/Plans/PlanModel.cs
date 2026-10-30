using JinJi.Application.FieldManagement;
using JinJi.Policy;

namespace JinJi.Application.Plans;

/// <summary>素案の変更の種類（直接登録の 5 つと、グループの加入・脱退）。</summary>
public enum PlanChangeKind
{
    Assignment,
    Person,
    OrgUpdate,
    OrgCreate,
    OrgAbolish,

    /// <summary>グループに加える（target_id はグループ、values に person_id と role）。</summary>
    GroupJoin,

    /// <summary>グループから外す（target_id はグループ、values に person_id）。</summary>
    GroupLeave,
}

/// <summary>素案の 1 つの変更。値のキーは <see cref="PlanChanges.ValueKeys"/>。</summary>
public sealed record PlanChange(PlanChangeKind Kind, Guid? TargetId, IReadOnlyDictionary<string, string?> Values);

/// <summary>素案（ADR-0013 決定 8）。</summary>
public sealed record Plan
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Note { get; init; }

    /// <summary>基準日（この日の公開側の上に変更を重ねて見る）。</summary>
    public required DateOnly BaseDate { get; init; }

    /// <summary>予定の有効日（直接登録にするときの有効日）。</summary>
    public required DateOnly EffectiveDate { get; init; }

    public required IReadOnlyList<PlanChange> Changes { get; init; }

    public required Guid Owner { get; init; }

    public required ActorType CreatedVia { get; init; }

    public IReadOnlyList<Guid> SharedWith { get; init; } = [];

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>申請版（素案を固定したもの。ADR-0013 決定 9）。</summary>
public sealed record PlanVersion
{
    public required Guid Id { get; init; }

    public required Guid PlanId { get; init; }

    public required int VersionNo { get; init; }

    public required string Title { get; init; }

    public string? Note { get; init; }

    public required DateOnly BaseDate { get; init; }

    public required DateOnly EffectiveDate { get; init; }

    public required IReadOnlyList<PlanChange> Changes { get; init; }

    public required Guid CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateOnly? ApprovedOn { get; init; }

    /// <summary>ワークフロー側の申請番号。</summary>
    public string? ApprovalReference { get; init; }

    public Guid? ApprovalRecordedBy { get; init; }

    public string? ApprovalRecordedByClient { get; init; }

    public DateTimeOffset? ApprovalRecordedAt { get; init; }
}

/// <summary>素案・申請版のどの変更がどの直接登録になったか。</summary>
public sealed record PlanRegistrationLink(Guid PlanId, Guid? PlanVersionId, int ChangeIndex, Guid FieldRegistrationId);

/// <summary>素案の中身の入力。</summary>
public sealed record PlanInput(string Title, string? Note, DateOnly BaseDate, DateOnly EffectiveDate, IReadOnlyList<PlanChange> Changes);

/// <summary>素案の保存（Infrastructure が実装する。0023）。見える範囲・書ける人の最終的な線は DB（RLS）が引く。</summary>
public interface IPlanStore
{
    /// <summary>閲覧者が作成した素案と共有された素案（更新の新しい順）。</summary>
    Task<IReadOnlyList<Plan>> ListAsync(ViewerContext viewer, CancellationToken cancellationToken);

    Task<Plan?> FindAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken);

    Task InsertAsync(ViewerContext viewer, Plan plan, CancellationToken cancellationToken);

    /// <summary>中身を置き換える（作成者だけ）。無ければ false。</summary>
    Task<bool> UpdateAsync(ViewerContext viewer, Guid id, PlanInput input, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>共有する人を置き換える（作成者だけ）。</summary>
    Task<bool> ShareAsync(ViewerContext viewer, Guid id, IReadOnlyList<Guid> people, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>素案を消す（作成者だけ。申請版・登録の記録も消える）。</summary>
    Task<bool> DeleteAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken);

    /// <summary>今の中身を次の版番号の申請版にする（作成者だけ）。</summary>
    Task<PlanVersion?> FreezeAsync(ViewerContext viewer, Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlanVersion>> ListVersionsAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken);

    Task<PlanVersion?> FindVersionAsync(ViewerContext viewer, Guid planId, int versionNo, CancellationToken cancellationToken);

    /// <summary>承認の結果を記録する（人なら閲覧者本人、社内システムならクライアント）。記録済みなら false。</summary>
    Task<bool> RecordApprovalAsync(ViewerContext viewer, Guid versionId, DateOnly approvedOn, string? reference, DateTimeOffset now, CancellationToken cancellationToken);

    Task AddRegistrationLinksAsync(ViewerContext viewer, IReadOnlyList<PlanRegistrationLink> links, CancellationToken cancellationToken);

    Task<IReadOnlyList<PlanRegistrationLink>> ListRegistrationLinksAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken);
}

/// <summary>素案の変更のコード値と許可リスト。</summary>
public static class PlanChanges
{
    /// <summary>values に入れてよいキー（DB の <c>jinji_plan_value_keys()</c> と同じ集合）。</summary>
    public static readonly IReadOnlySet<string> ValueKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "org_unit_id", "position_title", "site_id", "remote_work", "extension_number", "work_mobile_phone",
        "code", "name", "short_name", "level_key", "parent_org_unit_id", "head_person_id",
        "person_id", "role",
    };

    /// <summary>1 つの素案の変更の上限（DB の CHECK 制約と同じ）。</summary>
    public const int MaxChanges = 500;

    public static string Code(PlanChangeKind kind) => kind switch
    {
        PlanChangeKind.Assignment => "assignment",
        PlanChangeKind.Person => "person",
        PlanChangeKind.OrgUpdate => "org_update",
        PlanChangeKind.OrgCreate => "org_create",
        PlanChangeKind.OrgAbolish => "org_abolish",
        PlanChangeKind.GroupJoin => "group_join",
        PlanChangeKind.GroupLeave => "group_leave",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static bool TryParse(string? code, out PlanChangeKind kind)
    {
        foreach (var value in Enum.GetValues<PlanChangeKind>())
        {
            if (Code(value) == code)
            {
                kind = value;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>直接登録の変更の種類（グループの変更なら null）。</summary>
    public static FieldChangeKind? FieldKind(PlanChangeKind kind) => kind switch
    {
        PlanChangeKind.Assignment => FieldChangeKind.Assignment,
        PlanChangeKind.Person => FieldChangeKind.Person,
        PlanChangeKind.OrgUpdate => FieldChangeKind.OrgUpdate,
        PlanChangeKind.OrgCreate => FieldChangeKind.OrgCreate,
        PlanChangeKind.OrgAbolish => FieldChangeKind.OrgAbolish,
        _ => null,
    };
}

/// <summary>素案の操作の誤りの種類。</summary>
public enum PlanError
{
    NotFound,
    Conflict,
    Invalid,
    Forbidden,
}

/// <summary>素案の操作を拒否した（理由は応答に出してよい範囲の文言）。</summary>
public sealed class PlanException : Exception
{
    public PlanException()
    {
    }

    public PlanException(string message)
        : base(message)
    {
    }

    public PlanException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PlanException(PlanError error, string message)
        : base(message)
    {
        Error = error;
    }

    public PlanError Error { get; }
}
