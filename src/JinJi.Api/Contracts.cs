using JinJi.Application;
using JinJi.Application.Access;
using JinJi.Application.Changes;
using JinJi.Application.Organization;
using JinJi.Application.People;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Api;

/// <summary>
/// 人の応答。<see cref="PersonView"/>（見える項目だけに値を持つ投影）を写すだけで、値を足したり引き直したりしない。
/// 値が無い項目・見えない項目は null（省略しない）。見えないのか値が無いのかは <see cref="VisibleFields"/> で区別する。
/// </summary>
public sealed record PersonResponse
{
    public required Guid Id { get; init; }

    /// <summary>この閲覧者に見える項目の名前。</summary>
    public required IReadOnlyList<string> VisibleFields { get; init; }

    public string? EmployeeNumber { get; init; }

    public LegalEntityResponse? LegalEntity { get; init; }

    public string? DisplayName { get; init; }

    public string? FamilyName { get; init; }

    public string? GivenName { get; init; }

    public string? FamilyNameKana { get; init; }

    public string? GivenNameKana { get; init; }

    public string? FamilyNameLatin { get; init; }

    public string? GivenNameLatin { get; init; }

    public string? WorkEmail { get; init; }

    public string? ExtensionNumber { get; init; }

    public string? WorkMobilePhone { get; init; }

    /// <summary>拠点（人ごとの上書きか、主務の組織の拠点。inherited で区別する）。拠点が無ければ null。</summary>
    public SiteRefResponse? Site { get; init; }

    /// <summary>リモートワークの対象か（有無だけ。理由・頻度・シフトは持たない）。</summary>
    public bool? RemoteWork { get; init; }

    /// <summary>階・エリア（席・ゾーンの割り当てか、主務の組織のエリア。source・inherited で区別する）。無ければ null。</summary>
    public WorkplaceRefResponse? Workplace { get; init; }

    /// <summary>主の席（席番号・座標・階）。席が無い・その時点の配置図に無ければ null。</summary>
    public SeatRefResponse? Seat { get; init; }

    /// <summary>
    /// 当日の所在（外部の提供の今日の分。jin-ji は保持しない）。1 人の参照（GET /people/{id}）で、提供があり、今日を表示し、
    /// 項目の上限に presence を明示したクライアントと UI だけ。所在が無い・見えないときは null で、visible_fields にも入れない（「不在」の値を作らない）。
    /// </summary>
    public PresenceRefResponse? Presence { get; init; }

    public string? EmploymentType { get; init; }

    public string? JobType { get; init; }

    public DateOnly? HireDate { get; init; }

    /// <summary>退職年月日（最終在籍日）。人事管理者以外には退職済みになるまで返さない。</summary>
    public DateOnly? TerminationDate { get; init; }

    /// <summary>active / terminated。</summary>
    [OpenApiEnum("active", "terminated")]
    public string? EmploymentStatus { get; init; }

    /// <summary>active / suspended。</summary>
    [OpenApiEnum("active", "suspended")]
    public string? AccountStatus { get; init; }

    public SuspensionResponse? AccountSuspension { get; init; }

    public IReadOnlyList<AssignmentResponse>? Assignments { get; init; }

    /// <summary>直属の上長の jin-ji ID。</summary>
    public IReadOnlyList<Guid>? Managers { get; init; }

    public IReadOnlyList<PersonnelActionResponse>? PersonnelActions { get; init; }

    /// <summary>所属するグループと役割（全社公開のグループだけ）。</summary>
    public IReadOnlyList<GroupMembershipResponse>? Groups { get; init; }

    /// <summary>氏名の履歴（人事管理者だけ）。</summary>
    public IReadOnlyList<NameVersionResponse>? NameHistory { get; init; }

    public static PersonResponse From(PersonView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new PersonResponse
        {
            Id = view.PersonId,
            VisibleFields = [.. view.VisibleFields.Order(StringComparer.Ordinal)],
            EmployeeNumber = view.EmployeeNumber,
            LegalEntity = view.LegalEntity is { } e ? new LegalEntityResponse(e.Id, e.Code, e.Name) : null,
            DisplayName = view.DisplayName,
            FamilyName = view.FamilyName,
            GivenName = view.GivenName,
            FamilyNameKana = view.FamilyNameKana,
            GivenNameKana = view.GivenNameKana,
            FamilyNameLatin = view.FamilyNameLatin,
            GivenNameLatin = view.GivenNameLatin,
            WorkEmail = view.WorkEmail,
            ExtensionNumber = view.ExtensionNumber,
            WorkMobilePhone = view.WorkMobilePhone,
            Site = SiteRefResponse.From(view.Site),
            RemoteWork = view.RemoteWork,
            Workplace = WorkplaceRefResponse.From(view.Workplace),
            Seat = SeatRefResponse.From(view.Seat),
            Presence = PresenceRefResponse.From(view.Presence),
            Groups = view.Groups?.Select(GroupMembershipResponse.From).ToList(),
            EmploymentType = view.EmploymentType,
            JobType = view.JobType,
            HireDate = view.HireDate,
            TerminationDate = view.TerminationDate,
            EmploymentStatus = view.EmploymentStatus,
            AccountStatus = view.AccountStatus,
            AccountSuspension = view.AccountSuspension is { } s ? new SuspensionResponse(s.From, s.Until) : null,
            Assignments = view.Assignments?.Select(a => new AssignmentResponse(
                a.OrgUnitId,
                a.OrgUnitCode,
                a.OrgUnitName,
                a.LegalEntityId,
                a.Type == AssignmentType.Primary ? "primary" : "concurrent",
                a.PositionTitle,
                Secondment(a.Secondment),
                a.ValidFrom,
                a.ValidUntil)).ToList(),
            Managers = view.Managers,
            PersonnelActions = view.PersonnelActions?.Select(a => new PersonnelActionResponse(
                a.Id,
                ActionType(a.ActionType),
                a.EffectiveDate,
                a.OrgUnitId,
                a.AssignmentType switch
                {
                    AssignmentType.Primary => "primary",
                    AssignmentType.Concurrent => "concurrent",
                    _ => null,
                },
                a.PositionTitle,
                a.DocumentNumber)).ToList(),
            NameHistory = view.NameHistory?.Select(n => new NameVersionResponse(
                n.ValidFrom, n.ValidUntil, n.FamilyName, n.GivenName, n.FamilyNameKana, n.GivenNameKana, n.DisplayName)).ToList(),
        };
    }

    private static string Secondment(SecondmentType type) => type switch
    {
        SecondmentType.Outbound => "outbound",
        SecondmentType.Inbound => "inbound",
        _ => "none",
    };

    private static string ActionType(PersonnelActionType type) => type switch
    {
        PersonnelActionType.Hire => "hire",
        PersonnelActionType.Termination => "termination",
        PersonnelActionType.Transfer => "transfer",
        PersonnelActionType.PositionAppointment => "position_appointment",
        PersonnelActionType.PositionDismissal => "position_dismissal",
        PersonnelActionType.ConcurrentStart => "concurrent_start",
        PersonnelActionType.ConcurrentEnd => "concurrent_end",
        PersonnelActionType.SecondmentStart => "secondment_start",
        PersonnelActionType.SecondmentEnd => "secondment_end",
        PersonnelActionType.SuspensionStart => "suspension_start",
        PersonnelActionType.SuspensionEnd => "suspension_end",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

public sealed record LegalEntityResponse(Guid Id, string Code, string Name);

/// <summary>拠点の参照（人・組織の拠点）。拠点の情報（コード・名称・種別・都道府県）は組織と同じく全社公開。</summary>
/// <param name="Type">拠点の種別（head_office 本社 / branch 支社・支店 / sales_office 営業所 / factory 工場 / laboratory 研究所 / logistics 物流拠点 / store 店舗 / other その他）。</param>
/// <param name="PrefectureCode">所在の都道府県のコード（JIS X 0401。01〜47）。国外は null。</param>
/// <param name="PrefectureName">都道府県名（例: 東京都）。</param>
/// <param name="Inherited">人の拠点なら主務の組織の拠点を、組織の拠点なら上位組織の拠点を引き継いだか（false は人ごとの上書き・組織自身の拠点）。</param>
public sealed record SiteRefResponse(
    Guid Id,
    Guid LegalEntityId,
    string Code,
    string Name,
    [property: OpenApiEnum(typeof(SiteType))] string Type,
    string? PrefectureCode,
    string? PrefectureName,
    bool Inherited)
{
    public static SiteRefResponse? From(SiteView? view) =>
        view is null
            ? null
            : new SiteRefResponse(view.Id, view.LegalEntityId, view.Code, view.Name, SiteCodes.ToCode(view.Type), view.PrefectureCode, SiteCodes.PrefectureName(view.PrefectureCode), view.Inherited);
}

/// <param name="Until">終了予定日（この日を含まない）。人事管理者以外には、今日より後なら返さない。</param>
public sealed record SuspensionResponse(DateOnly From, DateOnly? Until);

/// <param name="Type">primary / concurrent。</param>
/// <param name="Secondment">none / outbound / inbound。</param>
/// <param name="ValidUntil">有効期間の終了日（この日を含まない）。人事管理者以外には、今日より後なら返さない（無期限と同じに見せる）。</param>
public sealed record AssignmentResponse(
    Guid OrgUnitId,
    string OrgUnitCode,
    string OrgUnitName,
    Guid LegalEntityId,
    [property: OpenApiEnum(typeof(AssignmentType))] string Type,
    string? PositionTitle,
    [property: OpenApiEnum(typeof(SecondmentType))] string Secondment,
    DateOnly ValidFrom,
    DateOnly? ValidUntil);

public sealed record PersonnelActionResponse(
    Guid Id,
    [property: OpenApiEnum(typeof(PersonnelActionType))] string ActionType,
    DateOnly EffectiveDate,
    Guid? OrgUnitId,
    [property: OpenApiEnum(typeof(AssignmentType))] string? AssignmentType,
    string? PositionTitle,
    string? DocumentNumber);

public sealed record NameVersionResponse(
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    string FamilyName,
    string? GivenName,
    string? FamilyNameKana,
    string? GivenNameKana,
    string DisplayName);

/// <summary>人の一覧の 1 ページ。</summary>
/// <param name="NextCursor">続きがあれば、次のページの cursor に渡す値（改ざんを検知する署名付き）。</param>
public sealed record PersonListResponse(IReadOnlyList<PersonResponse> Items, string? NextCursor);

/// <summary>人の検索の 1 ページ。</summary>
/// <param name="NextCursor">続きがあれば、次のページの cursor に渡す値（改ざんを検知する署名付き）。</param>
/// <param name="Total">条件に一致した人数（facets=true のときだけ。それ以外は null）。</param>
/// <param name="Facets">条件ごとの件数（facets=true のときだけ。それ以外は null）。</param>
public sealed record PersonSearchResponse(IReadOnlyList<PersonResponse> Items, string? NextCursor, int? Total, PersonFacetsResponse? Facets);

/// <summary>
/// 条件ごとの件数。数えるのは閲覧者に返し得る人の、見える項目の値だけ（見えない値・見えない人は数えない。「非公開」の区分も無い）。
/// ある条件の件数は、その条件以外のすべての条件に一致する人で数える。
/// </summary>
public sealed record PersonFacetsResponse(
    IReadOnlyList<FacetValueResponse> LegalEntities,
    IReadOnlyList<FacetValueResponse> Levels,
    IReadOnlyList<FacetValueResponse> AssignmentKinds,
    IReadOnlyList<FacetValueResponse> Positions,
    IReadOnlyList<FacetValueResponse> EmploymentTypes,
    IReadOnlyList<FacetValueResponse> JobTypes,
    IReadOnlyList<FacetValueResponse> Sites,
    IReadOnlyList<FacetValueResponse> SiteTypes,
    IReadOnlyList<FacetValueResponse> Prefectures,
    IReadOnlyList<FacetValueResponse> RemoteWork,
    IReadOnlyList<FacetValueResponse> EmploymentStatuses,
    IReadOnlyList<FacetValueResponse> AccountStatuses)
{
    public static PersonFacetsResponse From(PersonFacets f)
    {
        ArgumentNullException.ThrowIfNull(f);
        static List<FacetValueResponse> Map(IReadOnlyList<FacetValue> values) => [.. values.Select(v => new FacetValueResponse(v.Value, v.Label, v.Count))];
        return new PersonFacetsResponse(
            Map(f.LegalEntities), Map(f.Levels), Map(f.AssignmentKinds), Map(f.Positions), Map(f.EmploymentTypes), Map(f.JobTypes),
            Map(f.Sites), Map(f.SiteTypes), Map(f.Prefectures), Map(f.RemoteWork), Map(f.EmploymentStatuses), Map(f.AccountStatuses));
    }
}

/// <summary>条件の値と人数。</summary>
/// <param name="Value">条件に指定する値（法人・拠点は ID、段はキー、リモートワークは true / false、それ以外は値そのもの）。</param>
/// <param name="Label">表示名（法人名・拠点名・段の名前・都道府県名。それ以外は値と同じ）。</param>
public sealed record FacetValueResponse(string Value, string Label, int Count);

/// <summary>上長の応答。</summary>
public sealed record ManagerResponse(Guid PersonId, IReadOnlyList<PersonResponse> Managers);

/// <summary>承認ルートの応答（ワークフロー製品が「この人の申請を誰が承認するか」を 1 回で引く。§9「承認ルート」）。</summary>
/// <param name="PersonId">起点の人。</param>
/// <param name="AsOf">時点。</param>
/// <param name="Vacancy">空席の段の扱い（promote: 上位へ繰り上げ / vacant: 空席の段として返す）。</param>
/// <param name="Start">起点の所属（主務、または指定した兼務・出向の所属）。所属が無ければ null（steps は空）。</param>
/// <param name="Steps">下の段から順の承認者（level・position で絞り込んだら、当たった段だけ）。空なら承認者を決められない（自動で承認しないこと）。</param>
/// <param name="UnresolvedUnits">最上位まで繰り上げても承認者が見つからなかった組織（下の組織が先）。</param>
public sealed record ApprovalRouteResponse(
    Guid PersonId,
    DateOnly AsOf,
    [property: OpenApiEnum(typeof(ApprovalVacancyMode))] string Vacancy,
    ApprovalStartResponse? Start,
    IReadOnlyList<ApprovalStepResponse> Steps,
    IReadOnlyList<ApprovalSkippedUnitResponse> UnresolvedUnits)
{
    public static ApprovalRouteResponse From(ApprovalRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new ApprovalRouteResponse(
            route.PersonId,
            route.AsOf,
            Codes.Of(route.Vacancy),
            route.Start is { } s
                ? new ApprovalStartResponse(Org(s.OrgUnit), Codes.Of(s.Type), Codes.Of(s.Secondment), s.PositionTitle)
                : null,
            [
                .. route.Steps.Select(step => new ApprovalStepResponse(
                    step.Order,
                    Org(step.OrgUnit),
                    Codes.Of(step.Status),
                    step.Approver is { } approver ? PersonResponse.From(approver) : null,
                    step.ApproverPosition,
                    Skipped(step.SkippedUnits))),
            ],
            Skipped(route.UnresolvedUnits));
    }

    private static ApprovalOrgUnitResponse Org(ApprovalOrgUnit unit) =>
        new(unit.Id, unit.LegalEntityId, unit.Code, unit.Name, unit.LevelKey, unit.LevelName, unit.LevelRank);

    private static List<ApprovalSkippedUnitResponse> Skipped(IEnumerable<ApprovalSkippedUnit> units) =>
        [.. units.Select(u => new ApprovalSkippedUnitResponse(Org(u.OrgUnit), Codes.Of(u.Reason)))];
}

/// <summary>承認ルートに現れる組織（組織の情報は全社公開）。</summary>
/// <param name="LevelKey">段のキー（法人ごとの段の定義で解決。段を持たない組織は null）。</param>
/// <param name="LevelName">段の表示名（法人ごとの段の定義の名前。例: 部）。</param>
/// <param name="LevelRank">段の順位（上から 1, 2, ...）。</param>
public sealed record ApprovalOrgUnitResponse(Guid Id, Guid LegalEntityId, string Code, string Name, string? LevelKey, string? LevelName, int? LevelRank);

/// <summary>承認ルートの起点の所属。</summary>
/// <param name="AssignmentType">primary（主務）/ concurrent（兼務）。</param>
/// <param name="Secondment">none / outbound（出向）/ inbound（出向受入）。</param>
/// <param name="PositionTitle">起点の人のその所属の役職。</param>
public sealed record ApprovalStartResponse(
    ApprovalOrgUnitResponse OrgUnit,
    [property: OpenApiEnum(typeof(AssignmentType))] string AssignmentType,
    [property: OpenApiEnum(typeof(SecondmentType))] string Secondment,
    string? PositionTitle);

/// <summary>承認ルートの 1 段。</summary>
/// <param name="Order">絞り込む前の経路での順番（1 から。下の段が先）。</param>
/// <param name="Status">assigned（承認者がいる）/ vacant（組織長が空席。vacancy=vacant のときだけ）。</param>
/// <param name="Approver">承認者（組織長）。見える項目だけ。一時停止中かどうかは account_status（代わりの人は返さない）。空席なら null。</param>
/// <param name="ApproverPosition">承認者のその組織での役職（その組織への所属の役職。無い・所属の項目が見えなければ null）。</param>
/// <param name="SkippedUnits">この段の手前で飛ばした組織（reason: vacant = 空席を繰り上げた / self = 起点の人自身が組織長）。</param>
public sealed record ApprovalStepResponse(
    int Order,
    ApprovalOrgUnitResponse OrgUnit,
    [property: OpenApiEnum(typeof(ApprovalStepStatus))] string Status,
    PersonResponse? Approver,
    string? ApproverPosition,
    IReadOnlyList<ApprovalSkippedUnitResponse> SkippedUnits);

/// <summary>段として返さずに飛ばした組織。</summary>
/// <param name="Reason">vacant（組織長が空席・退職済み・見えない）/ self（起点の人自身が組織長）。</param>
public sealed record ApprovalSkippedUnitResponse(ApprovalOrgUnitResponse OrgUnit, [property: OpenApiEnum(typeof(ApprovalSkipReason))] string Reason);

/// <summary>組織の応答。</summary>
public sealed record OrgUnitResponse(
    Guid Id,
    LegalEntityRef LegalEntity,
    string Code,
    string Name,
    string? ShortName,
    OrgLevelResponse? Level,
    Guid? ParentId,
    Guid? HeadPersonId,
    SiteRefResponse? Site)
{
    public static OrgUnitResponse From(OrgUnitView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new OrgUnitResponse(
            view.Id,
            new LegalEntityRef(view.LegalEntityId, view.LegalEntityCode, view.LegalEntityName),
            view.Code,
            view.Name,
            view.ShortName,
            view.LevelKey is { } key ? new OrgLevelResponse(key, view.LevelName!, view.LevelRank!.Value) : null,
            view.ParentId,
            view.HeadPersonId,
            SiteRefResponse.From(view.Site));
    }
}

public sealed record LegalEntityRef(Guid Id, string? Code, string? Name);

/// <param name="Rank">段の順位（上から 1, 2, ...）。</param>
public sealed record OrgLevelResponse(string Key, string Name, int Rank);

/// <summary>拠点（拠点の一覧・詳細）。拠点の情報は全社公開。人数は閲覧者に見える人（人の拠点の項目が見える人）だけを数える。</summary>
/// <param name="Type">拠点の種別（<see cref="SiteRefResponse.Type"/> と同じ値）。</param>
/// <param name="PrefectureCode">所在の都道府県のコード（JIS X 0401）。国外は null。</param>
/// <param name="Location">所在地（都道府県より後の部分。事業所の住所 = 会社の情報）。</param>
/// <param name="MemberCount">この拠点の人の数（閲覧者に人と項目 site が見える人だけ。人ごとの上書きと、主務の組織から引き継いだ拠点の両方）。</param>
/// <param name="RemoteWorkCount">そのうちリモートワークの対象の人の数（項目 remote_work も見える人だけ）。</param>
/// <param name="OrgUnits">この拠点を自分の拠点として持つ組織（配下で引き継ぐ組織は含めない。組織図の順）。</param>
public sealed record SiteResponse(
    Guid Id,
    LegalEntityRef LegalEntity,
    string Code,
    string Name,
    [property: OpenApiEnum(typeof(SiteType))] string Type,
    string? PrefectureCode,
    string? PrefectureName,
    string? Location,
    int MemberCount,
    int RemoteWorkCount,
    IReadOnlyList<SiteOrgUnitResponse> OrgUnits)
{
    public static SiteResponse From(SiteSummary site)
    {
        ArgumentNullException.ThrowIfNull(site);
        var s = site.Site;
        return new SiteResponse(
            s.Id,
            new LegalEntityRef(s.LegalEntityId, site.LegalEntityCode, site.LegalEntityName),
            s.Code,
            s.Name,
            SiteCodes.ToCode(s.Type),
            s.PrefectureCode,
            SiteCodes.PrefectureName(s.PrefectureCode),
            s.Location,
            site.MemberCount,
            site.RemoteWorkCount,
            [.. site.OrgUnits.Select(u => new SiteOrgUnitResponse(u.Id, u.Code, u.Name))]);
    }
}

/// <summary>拠点を持つ組織。</summary>
public sealed record SiteOrgUnitResponse(Guid Id, string Code, string Name);

/// <summary>拠点の一覧（法人のコード・拠点コードの順）。</summary>
public sealed record SiteListResponse(DateOnly AsOf, IReadOnlyList<SiteResponse> Items);

/// <summary>組織ツリー（上位組織の ID で木を組める平らな一覧）。</summary>
public sealed record OrgTreeResponse(DateOnly AsOf, IReadOnlyList<OrgUnitResponse> Units);

/// <summary>変更フィードの 1 件（種別と ID だけ）。</summary>
/// <param name="Type">person / org_unit / site（拠点。拠点の変更は、その拠点を使う組織・人の変更としても届く）。</param>
public sealed record ChangeResponse([property: OpenApiEnum("person", "org_unit", "site", "group")] string Type, Guid Id)
{
    public static ChangeResponse From(ChangeEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new ChangeResponse(
            change.EntityType switch
            {
                ChangeEntityType.Person => "person",
                ChangeEntityType.OrgUnit => "org_unit",
                ChangeEntityType.Site => "site",
                ChangeEntityType.Group => "group",
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            },
            change.EntityId);
    }
}

/// <summary>変更フィードの 1 ページ。</summary>
/// <param name="NextCursor">次に since に渡す値（改ざんを検知する署名付き）。</param>
/// <param name="HasMore">すぐに続きを読めるか（false なら、しばらく待ってから next_cursor で読む）。</param>
public sealed record ChangeListResponse(IReadOnlyList<ChangeResponse> Items, string NextCursor, bool HasMore);

/// <summary>不正検知の状態（通常でないクライアント）。</summary>
/// <param name="State">throttled / suspended。</param>
public sealed record ClientStatusResponse(string ClientId, [property: OpenApiEnum(typeof(AbuseState))] string State, string? Rule, DateTimeOffset? Since, DateTimeOffset? Until)
{
    public static ClientStatusResponse From(AbuseStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new ClientStatusResponse(status.ClientId, AbuseStates.ToCode(status.State), status.Rule, status.Since, status.Until);
    }
}

/// <summary>コード値。</summary>
public static class AbuseStates
{
    public static string ToCode(AbuseState state) => state switch
    {
        AbuseState.Normal => "normal",
        AbuseState.Throttled => "throttled",
        AbuseState.Suspended => "suspended",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

/// <summary>人の一括取得の上限（ID の数）。</summary>
public static class PeopleBatch
{
    public const int MaxIds = 100;
}

/// <summary>組織の検索の結果。</summary>
/// <param name="HasMore">limit で打ち切ったか（続きは返さない。語を絞り込む）。</param>
public sealed record OrgSearchResponse(DateOnly AsOf, IReadOnlyList<OrgUnitResponse> Items, bool HasMore);

/// <summary>組織ごとの集計（閲覧者に見える人だけを数える）。</summary>
/// <param name="Units">その時点の全組織（組織ツリーと同じ順。0 人の組織も含む）。</param>
/// <param name="Links">主務の組織から兼務・出向の組織への所属の、組ごとの人数。</param>
public sealed record OrgStatsResponse(DateOnly AsOf, IReadOnlyList<OrgHeadcountResponse> Units, IReadOnlyList<OrgLinkResponse> Links)
{
    public static OrgStatsResponse From(OrgStats stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        return new OrgStatsResponse(
            stats.AsOf,
            [.. stats.Units.Select(u => new OrgHeadcountResponse(u.OrgUnitId, u.Primary, u.PrimaryTotal, u.Concurrent, u.Secondment))],
            [.. stats.Links.Select(l => new OrgLinkResponse(l.FromOrgUnitId, l.ToOrgUnitId, Codes.Of(l.Kind), l.Count))]);
    }
}

/// <summary>組織の人数（閲覧者に見える人だけ）。</summary>
/// <param name="Primary">出向でない主務の人数。</param>
/// <param name="PrimaryTotal">その組織か配下の組織に出向でない主務を持つ人数（重複なし）。</param>
/// <param name="Concurrent">出向でない兼務の人数。</param>
/// <param name="Secondment">出向（主務・兼務とも）の人数。</param>
public sealed record OrgHeadcountResponse(Guid OrgUnitId, int Primary, int PrimaryTotal, int Concurrent, int Secondment);

/// <summary>主務の組織から兼務・出向の組織への所属の人数。</summary>
/// <param name="Kind">concurrent（兼務）/ secondment（出向）。</param>
public sealed record OrgLinkResponse(Guid FromOrgUnitId, Guid ToOrgUnitId, [property: OpenApiEnum(typeof(OrgLinkKind))] string Kind, int Count);

/// <summary>メタ情報（業務日付・段の定義・時点の目盛り）。</summary>
/// <param name="BusinessDate">業務日付（日本時間の今日）。as_of の既定値で、これより後を指定できるのは can_view_future のときだけ。</param>
/// <param name="TimeZone">業務日付の時間帯（Asia/Tokyo）。</param>
/// <param name="CanViewFuture">業務日付より後の時点を指定できるか（人事管理者と、先行提供を許された社内システム）。</param>
/// <param name="MaxAsOf">as_of に指定できる最後の日（人事管理者は null = 上限なし。先行提供の社内システムは業務日付 + 日数、それ以外は業務日付）。</param>
/// <param name="DefaultLevels">グループ共通の既定の段。</param>
/// <param name="Milestones">時点の目盛り（組織改編・一斉異動の日。日付の昇順）。人事管理者以外には max_as_of 以前だけ。</param>
public sealed record MetaResponse(
    DateOnly BusinessDate,
    string TimeZone,
    bool CanViewFuture,
    DateOnly? MaxAsOf,
    IReadOnlyList<LegalEntityLevelsResponse> LegalEntities,
    IReadOnlyList<OrgLevelResponse> DefaultLevels,
    IReadOnlyList<MilestoneResponse> Milestones)
{
    public static MetaResponse From(DirectoryMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);
        return new MetaResponse(
            meta.BusinessDate,
            DirectoryMetaService.TimeZone,
            meta.CanViewFuture,
            meta.MaxAsOf,
            [.. meta.LegalEntities.Select(e => new LegalEntityLevelsResponse(e.LegalEntity.Id, e.LegalEntity.Code, e.LegalEntity.Name, e.UsesDefault, Levels(e.Levels)))],
            Levels(meta.DefaultLevels),
            [.. meta.Milestones.Select(m => new MilestoneResponse(m.Date, [.. m.Kinds.Select(Codes.Of)]))]);
    }

    private static List<OrgLevelResponse> Levels(IEnumerable<OrgLevel> levels) => [.. levels.Select(l => new OrgLevelResponse(l.Key, l.Name, l.Rank))];
}

/// <summary>法人と、その法人に効く段の定義。</summary>
/// <param name="UsesDefaultLevels">法人の定義が無く、グループ共通の既定を使っているか。</param>
/// <param name="Levels">段（順位の昇順）。</param>
public sealed record LegalEntityLevelsResponse(Guid Id, string Code, string Name, bool UsesDefaultLevels, IReadOnlyList<OrgLevelResponse> Levels);

/// <summary>時点の目盛り。</summary>
/// <param name="Kinds">org_change（組織改編）/ mass_action（一斉異動）。</param>
public sealed record MilestoneResponse(DateOnly Date, [property: OpenApiEnum(typeof(DirectoryMilestoneKind))] IReadOnlyList<string> Kinds);

/// <summary>ヘルスチェックの応答。</summary>
public sealed record HealthResponse(string Status, string Surface);

/// <summary>人の所属するグループ。</summary>
public sealed record GroupMembershipResponse(
    Guid GroupId,
    string Name,
    [property: OpenApiEnum(typeof(GroupKind))] string Kind,
    [property: OpenApiEnum(typeof(GroupRole))] string Role,
    bool Managed)
{
    public static GroupMembershipResponse From(GroupMembershipView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new(view.GroupId, view.Name, GroupCodes.Of(view.Kind), GroupCodes.Of(view.Role), view.Managed);
    }
}
