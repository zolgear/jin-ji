namespace JinJi.Domain.Organization;

/// <summary>主務 / 兼務。</summary>
public enum AssignmentType
{
    Primary,
    Concurrent,
}

/// <summary>出向の区別。</summary>
public enum SecondmentType
{
    /// <summary>出向なし。</summary>
    None,

    /// <summary>出向（送り出し）。</summary>
    Outbound,

    /// <summary>出向受入。</summary>
    Inbound,
}

/// <summary>発令の種類（DB の personnel_action.action_type）。</summary>
public enum PersonnelActionType
{
    Hire,
    Termination,
    Transfer,
    PositionAppointment,
    PositionDismissal,
    ConcurrentStart,
    ConcurrentEnd,
    SecondmentStart,
    SecondmentEnd,
    SuspensionStart,
    SuspensionEnd,
}

/// <summary>法人。</summary>
public sealed record LegalEntity(Guid Id, string Code, string Name);

/// <summary>ある時点で有効な組織の版。</summary>
/// <param name="LevelKey">
/// 段のキー（<see cref="OrgLevel.Key"/>。法人の段の定義で解決する）。null は段を持たない組織（室・プロジェクト等）で、
/// その所属者は祖先の段で判定する（<see cref="OrgSnapshot"/>）。
/// </param>
public sealed record OrgUnit(
    Guid Id,
    Guid LegalEntityId,
    string Code,
    string Name,
    string? ShortName,
    string? LevelKey,
    Guid? ParentId,
    Guid? HeadPersonId)
{
    /// <summary>組織の拠点（<see cref="Site"/>）。null なら上位組織の拠点を使う（<see cref="OrgSnapshot.SiteOfUnit"/>）。</summary>
    public Guid? SiteId { get; init; }
}

/// <summary>拠点の種別（DB の site_version.site_type）。固定の少数で、表示とまとめ方にだけ使う。</summary>
public enum SiteType
{
    /// <summary>本社。</summary>
    HeadOffice,

    /// <summary>支社・支店。</summary>
    Branch,

    /// <summary>営業所。</summary>
    SalesOffice,

    /// <summary>工場。</summary>
    Factory,

    /// <summary>研究所。</summary>
    Laboratory,

    /// <summary>物流拠点・倉庫。</summary>
    Logistics,

    /// <summary>店舗。</summary>
    Store,

    /// <summary>その他。</summary>
    Other,
}

/// <summary>ある時点で有効な拠点の版。所在地は事業所の住所（会社の情報）で、個人の住所ではない。</summary>
/// <param name="PrefectureCode">所在の都道府県のコード（JIS X 0401。01〜47）。国外は null。</param>
/// <param name="Location">所在地（都道府県より後の部分）。</param>
public sealed record Site(
    Guid Id,
    Guid LegalEntityId,
    string Code,
    string Name,
    SiteType Type,
    string? PrefectureCode,
    string? Location);

/// <summary>ある時点で有効な所属。</summary>
/// <param name="ValidFrom">有効期間の開始日。</param>
/// <param name="ValidUntil">有効期間の終了日（この日を含まない）。null は終了なし。</param>
public sealed record Assignment(
    Guid PersonId,
    Guid OrgUnitId,
    AssignmentType Type,
    string? PositionTitle,
    SecondmentType Secondment,
    DateOnly ValidFrom,
    DateOnly? ValidUntil);

/// <summary>アカウントの一時停止の期間（理由は持たない。ADR-0002）。</summary>
/// <param name="From">開始日。</param>
/// <param name="Until">終了予定日（この日を含まない）。null は終了予定なし。</param>
public sealed record SuspensionPeriod(DateOnly From, DateOnly? Until);

/// <summary>発令。effective_date が業務日付より後のものは内示（人事管理者以外には存在を見せない）。</summary>
public sealed record PersonnelActionRecord(
    Guid Id,
    Guid PersonId,
    PersonnelActionType ActionType,
    DateOnly EffectiveDate,
    Guid? OrgUnitId,
    AssignmentType? AssignmentType,
    string? PositionTitle,
    string? DocumentNumber);

/// <summary>人の属性の版（person_version の現在記録中の 1 行）。</summary>
public sealed record PersonVersion
{
    /// <summary>版の行 ID（person_version.id）。</summary>
    public required Guid VersionId { get; init; }

    /// <summary>有効期間の開始日。</summary>
    public required DateOnly ValidFrom { get; init; }

    /// <summary>有効期間の終了日（この日を含まない）。null は終了なし。</summary>
    public DateOnly? ValidUntil { get; init; }

    public required string FamilyName { get; init; }

    public string? GivenName { get; init; }

    public string? FamilyNameKana { get; init; }

    public string? GivenNameKana { get; init; }

    public string? FamilyNameLatin { get; init; }

    public string? GivenNameLatin { get; init; }

    public required string DisplayName { get; init; }

    public string? WorkEmail { get; init; }

    public string? ExtensionNumber { get; init; }

    public string? WorkMobilePhone { get; init; }

    /// <summary>人の拠点の上書き（主務の組織の拠点と異なる場合だけ）。null なら主務の組織の拠点（<see cref="OrgSnapshot.SiteOf"/>）。</summary>
    public Guid? SiteId { get; init; }

    /// <summary>リモートワークの対象か（有無だけ。理由・頻度・シフトは持たない。ADR-0002）。</summary>
    public bool RemoteWork { get; init; }

    /// <summary>雇用区分（DB のコード値。regular, contract 等）。</summary>
    public required string EmploymentType { get; init; }

    public string? JobType { get; init; }

    public required DateOnly HireDate { get; init; }

    /// <summary>退職年月日（最終在籍日）。</summary>
    public DateOnly? TerminationDate { get; init; }

    /// <summary>指定日を有効期間に含むか。</summary>
    public bool IsValidOn(DateOnly date) => ValidFrom <= date && (ValidUntil is null || date < ValidUntil.Value);
}

/// <summary>スナップショット内の人。</summary>
public sealed record PersonRecord
{
    public required Guid Id { get; init; }

    /// <summary>社員番号を採番した（雇用元の）法人。</summary>
    public required Guid LegalEntityId { get; init; }

    public required string EmployeeNumber { get; init; }

    /// <summary>属性の基準日に有効な版。</summary>
    public required PersonVersion Current { get; init; }

    /// <summary>読み込めた版のすべて（有効期間の開始日順）。人事管理者以外は RLS により現在の版だけになる。</summary>
    public required IReadOnlyList<PersonVersion> History { get; init; }

    /// <summary>スナップショットの時点で有効な一時停止。一時停止中でなければ null。</summary>
    public SuspensionPeriod? Suspension { get; init; }

    /// <summary>スナップショットの時点でアカウントが一時停止中か。</summary>
    public bool IsSuspended => Suspension is not null;

    /// <summary>読み込めた発令（発令日順）。人事管理者以外は RLS により内示が含まれない。</summary>
    public required IReadOnlyList<PersonnelActionRecord> Actions { get; init; }

    /// <summary>指定日の時点で退職済みか（退職年月日は最終在籍日）。</summary>
    public bool IsTerminatedOn(DateOnly date) => Current.TerminationDate is { } terminated && terminated < date;
}
