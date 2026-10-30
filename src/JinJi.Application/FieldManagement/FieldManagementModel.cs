using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.FieldManagement;

/// <summary>管理元の起案の状態（field_domain_setting.status）。</summary>
public enum FieldDomainSettingStatus
{
    Proposed,
    Approved,
    Rejected,
    Superseded,
}

/// <summary>管理元の起案（1 行）。</summary>
public sealed record FieldDomainSetting(
    Guid Id,
    FieldDomain Domain,
    FieldManagementMode Mode,
    FieldDomainSettingStatus Status,
    string? Note,
    Guid ProposedBy,
    DateTimeOffset ProposedAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? SupersededAt);

/// <summary>管理元・担当の指名の保存（Infrastructure が実装する。0020）。最終的な線（誰が書けるか・状態の遷移・職務分掌）は DB が引く。</summary>
public interface IFieldManagementStore
{
    /// <summary>管理元の起案の一覧（新しい順）。</summary>
    Task<IReadOnlyList<FieldDomainSetting>> ListSettingsAsync(ViewerContext viewer, CancellationToken cancellationToken);

    Task<FieldDomainSetting?> FindSettingAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken);

    /// <summary>起案を追加する。同じ領域の起案中の行が既にあれば false。</summary>
    Task<bool> InsertSettingAsync(ViewerContext viewer, FieldDomainSetting setting, CancellationToken cancellationToken);

    /// <summary>
    /// 起案を承認・却下する（決めた人は閲覧者本人）。承認なら同じ領域の承認済みの行を置き換え済みにしてから承認する（1 トランザクション）。起案中でなくなっていれば false。
    /// </summary>
    Task<bool> DecideSettingAsync(ViewerContext viewer, Guid id, bool approve, DateTimeOffset decidedAt, CancellationToken cancellationToken);

    /// <summary>担当の指名の一覧（取り消し済みを含む。新しい順）。組織・人で絞れる。</summary>
    Task<IReadOnlyList<OrgEditorGrant>> ListGrantsAsync(ViewerContext viewer, Guid? orgUnitId, Guid? personId, CancellationToken cancellationToken);

    Task<OrgEditorGrant?> FindGrantAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken);

    /// <summary>指名を追加する。同じ組織・同じ人の有効な指名と期間が重なれば false。</summary>
    Task<bool> InsertGrantAsync(ViewerContext viewer, OrgEditorGrant grant, CancellationToken cancellationToken);

    /// <summary>指名を取り消す（取り消した人は閲覧者本人）。取り消し済みなら false。</summary>
    Task<bool> RevokeGrantAsync(ViewerContext viewer, Guid id, DateTimeOffset revokedAt, CancellationToken cancellationToken);
}

/// <summary>管理元・担当の指名の操作の誤りの種類。</summary>
public enum FieldManagementError
{
    NotFound,
    Conflict,
    Invalid,
    Forbidden,
}

/// <summary>管理元・担当の指名の操作を拒否した（理由は応答に出してよい範囲の文言）。</summary>
public sealed class FieldManagementException : Exception
{
    public FieldManagementException()
    {
    }

    public FieldManagementException(string message)
        : base(message)
    {
    }

    public FieldManagementException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public FieldManagementException(FieldManagementError error, string message)
        : base(message)
    {
        Error = error;
    }

    public FieldManagementException(FieldManagementError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public FieldManagementError Error { get; }
}
