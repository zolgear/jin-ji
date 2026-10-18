using JinJi.Domain.Organization;

namespace JinJi.Policy;

/// <summary>
/// 人の投影（マスク済み DTO）。見える項目だけに値が入り、見えない項目は null。
/// </summary>
/// <remarks>
/// コンストラクタは internal で、生成できるのは <see cref="PersonProjector"/> だけ。
/// REST・MCP・UI は必ずこの型で人の情報を受け取るので、認可エンジンを通らない応答を型の上で作れない。
/// 値が null のとき「見えない」のか「値が無い」のかは <see cref="VisibleFields"/> で区別する。
/// </remarks>
public sealed class PersonView
{
    internal PersonView(Guid personId, IReadOnlySet<string> visibleFields)
    {
        PersonId = personId;
        VisibleFields = visibleFields;
    }

    /// <summary>jin-ji ID。ID は秘密ではないので常に返す（§7）。</summary>
    public Guid PersonId { get; }

    /// <summary>この閲覧者に見える項目（<see cref="PersonFields"/>）。</summary>
    public IReadOnlySet<string> VisibleFields { get; }

    public string? EmployeeNumber { get; internal init; }

    public LegalEntityView? LegalEntity { get; internal init; }

    public string? DisplayName { get; internal init; }

    public string? FamilyName { get; internal init; }

    public string? GivenName { get; internal init; }

    public string? FamilyNameKana { get; internal init; }

    public string? GivenNameKana { get; internal init; }

    public string? FamilyNameLatin { get; internal init; }

    public string? GivenNameLatin { get; internal init; }

    public string? WorkEmail { get; internal init; }

    public string? ExtensionNumber { get; internal init; }

    public string? WorkMobilePhone { get; internal init; }

    /// <summary>拠点（人の上書きか、主務の組織の拠点）。拠点が無い・閉鎖済みなら null（見えるかどうかは <see cref="VisibleFields"/> で区別する）。</summary>
    public SiteView? Site { get; internal init; }

    /// <summary>リモートワークの対象か（有無だけ）。</summary>
    public bool? RemoteWork { get; internal init; }

    public string? EmploymentType { get; internal init; }

    public string? JobType { get; internal init; }

    public DateOnly? HireDate { get; internal init; }

    /// <summary>退職年月日（最終在籍日）。人事管理者以外には、退職済みになるまで返さない（退職の予定は内示と同じく秘匿する）。</summary>
    public DateOnly? TerminationDate { get; internal init; }

    /// <summary>在籍状態（"active" / "terminated"）。</summary>
    public string? EmploymentStatus { get; internal init; }

    /// <summary>アカウント状態（"active" / "suspended"）。理由は持たない。</summary>
    public string? AccountStatus { get; internal init; }

    /// <summary>一時停止の期間。一時停止中でなければ null（見えるかどうかは <see cref="VisibleFields"/> で区別する）。</summary>
    public SuspensionView? AccountSuspension { get; internal init; }

    public IReadOnlyList<AssignmentView>? Assignments { get; internal init; }

    /// <summary>直属の上長の jin-ji ID。</summary>
    public IReadOnlyList<Guid>? Managers { get; internal init; }

    public IReadOnlyList<PersonnelActionView>? PersonnelActions { get; internal init; }

    public IReadOnlyList<NameVersionView>? NameHistory { get; internal init; }

    /// <summary>項目が見えるか。</summary>
    public bool CanSee(string field) => VisibleFields.Contains(field);
}

public sealed record LegalEntityView(Guid Id, string Code, string Name);

/// <summary>拠点（拠点の情報は組織と同じく全社公開。人の拠点として見せるかは項目 site の判定による）。</summary>
/// <param name="PrefectureCode">所在の都道府県のコード（JIS X 0401）。国外は null。</param>
/// <param name="Inherited">人の拠点なら主務の組織から、組織の拠点なら上位組織から引き継いだか（false は上書き・自分の拠点）。</param>
public sealed record SiteView(Guid Id, Guid LegalEntityId, string Code, string Name, SiteType Type, string? PrefectureCode, bool Inherited)
{
    /// <summary>解決した拠点から作る。</summary>
    public static SiteView From(ResolvedSite resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        var s = resolved.Site;
        return new SiteView(s.Id, s.LegalEntityId, s.Code, s.Name, s.Type, s.PrefectureCode, resolved.Inherited);
    }
}

/// <param name="ValidUntil">有効期間の終了日（この日を含まない）。人事管理者以外には、業務日付より後なら null（無期限と同じに見せる）。</param>
public sealed record AssignmentView(
    Guid OrgUnitId,
    string OrgUnitCode,
    string OrgUnitName,
    Guid LegalEntityId,
    AssignmentType Type,
    string? PositionTitle,
    SecondmentType Secondment,
    DateOnly ValidFrom,
    DateOnly? ValidUntil);

/// <summary>一時停止の期間（理由は持たない）。</summary>
/// <param name="Until">終了予定日（この日を含まない）。人事管理者以外には、業務日付より後なら null（本人にも返さない）。</param>
public sealed record SuspensionView(DateOnly From, DateOnly? Until);

public sealed record PersonnelActionView(
    Guid Id,
    PersonnelActionType ActionType,
    DateOnly EffectiveDate,
    Guid? OrgUnitId,
    AssignmentType? AssignmentType,
    string? PositionTitle,
    string? DocumentNumber);

public sealed record NameVersionView(
    Guid VersionId,
    DateOnly ValidFrom,
    DateOnly? ValidUntil,
    string FamilyName,
    string? GivenName,
    string? FamilyNameKana,
    string? GivenNameKana,
    string DisplayName);

/// <summary>
/// 見える項目だけを持つ投影を作る。REST・MCP・UI のすべての経路が、人の情報をこれを通して返す。
/// </summary>
/// <remarks>
/// 項目の判定（<see cref="PolicyEvaluator"/>）に加え、次をここで重ねて守る（RLS と同じ規則。片方のバグでは漏れない）:
/// <list type="bullet">
/// <item>内示の秘匿: 人事管理者以外には、発令日が業務日付より後の発令を返さない（本人にも）</item>
/// <item>氏名の履歴: 人事管理者以外には返さず、氏名・連絡先などの属性も今日有効な版だけを使う</item>
/// <item>将来の予定の秘匿: 人事管理者以外には、所属・一時停止の終了日が業務日付より後なら終了日を返さず（無期限と同じに見せる）、
/// 退職年月日も退職済みになるまで返さない。終了日から異動・復職・退職の予定を推測させないため（本人・上長にも）。
/// 人の属性の版の有効期間は氏名の履歴（人事管理者だけ）にしか現れない</item>
/// <item>退職者の一般公開の終了: 人事管理者以外には、退職年月日から公開期間（既定 90 日）を過ぎた人を投影しない（ADR-0002）</item>
/// </list>
/// </remarks>
public sealed class PersonProjector
{
    /// <summary>退職者を人事管理者以外に公開し続ける日数の既定値（退職年月日の翌日から数える）。</summary>
    public const int DefaultRetiredPublicDays = 90;

    private readonly PolicyEvaluator _evaluator;
    private readonly OrgSnapshot _data;
    private readonly DateOnly _today;
    private readonly int _retiredPublicDays;

    /// <param name="evaluator">今日時点の関係で評価する判定器。</param>
    /// <param name="data">返す値を取り出すスナップショット（時点指定）。</param>
    /// <param name="today">業務日付（内示・将来の予定・退職後の公開期間の判定に使う）。</param>
    /// <param name="retiredPublicDays">
    /// 退職者を人事管理者以外に公開する日数。退職年月日（最終在籍日）+ この日数の日までは公開し、その翌日から投影しない。0 なら退職の翌日から投影しない。
    /// </param>
    public PersonProjector(PolicyEvaluator evaluator, OrgSnapshot data, DateOnly today, int retiredPublicDays = DefaultRetiredPublicDays)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentOutOfRangeException.ThrowIfNegative(retiredPublicDays);
        _evaluator = evaluator;
        _data = data;
        _today = today;
        _retiredPublicDays = retiredPublicDays;
    }

    /// <summary>
    /// 投影を作る。対象者がスナップショットに居ないか、見える項目が一つも無いか、人事管理者以外から見て退職後の公開期間を過ぎていれば null。
    /// </summary>
    public PersonView? Project(ViewerContext viewer, Guid personId)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (_data.FindPerson(personId) is not { } person)
        {
            return null;
        }

        var visible = _evaluator.VisibleFields(viewer, personId);
        if (!viewer.IsHrAdmin)
        {
            visible = visible.Where(f => !PersonFields.HrAdminOnly.Contains(f)).ToHashSet(StringComparer.Ordinal);
        }

        if (visible.Count == 0)
        {
            return null;
        }

        // 氏名の履歴の秘匿: 人事管理者以外には、今日有効な版（RLS の person_version の規則と同じ）だけを使う
        var v = viewer.IsHrAdmin ? person.Current : CurrentVersion(person);
        if (v is null)
        {
            return null;
        }

        // 退職者の一般公開の終了: 判定は時点指定に関わらず今日の版の退職年月日で行う（過去の時点を指定しても見えるようにはならない）
        if (!viewer.IsHrAdmin && !IsPubliclyListed(v, _today, _retiredPublicDays))
        {
            return null;
        }

        bool See(string field) => visible.Contains(field);
        var hideFuture = !viewer.IsHrAdmin;

        return new PersonView(personId, visible)
        {
            EmployeeNumber = See(PersonFields.EmployeeNumber) ? person.EmployeeNumber : null,
            LegalEntity = See(PersonFields.LegalEntity) && _data.FindLegalEntity(person.LegalEntityId) is { } entity
                ? new LegalEntityView(entity.Id, entity.Code, entity.Name)
                : null,
            DisplayName = See(PersonFields.DisplayName) ? v.DisplayName : null,
            FamilyName = See(PersonFields.FamilyName) ? v.FamilyName : null,
            GivenName = See(PersonFields.GivenName) ? v.GivenName : null,
            FamilyNameKana = See(PersonFields.FamilyNameKana) ? v.FamilyNameKana : null,
            GivenNameKana = See(PersonFields.GivenNameKana) ? v.GivenNameKana : null,
            FamilyNameLatin = See(PersonFields.FamilyNameLatin) ? v.FamilyNameLatin : null,
            GivenNameLatin = See(PersonFields.GivenNameLatin) ? v.GivenNameLatin : null,
            WorkEmail = See(PersonFields.WorkEmail) ? v.WorkEmail : null,
            ExtensionNumber = See(PersonFields.ExtensionNumber) ? v.ExtensionNumber : null,
            WorkMobilePhone = See(PersonFields.WorkMobilePhone) ? v.WorkMobilePhone : null,
            Site = See(PersonFields.Site) && _data.SiteOf(personId, v) is { } site ? SiteView.From(site) : null,
            RemoteWork = See(PersonFields.RemoteWork) ? v.RemoteWork : null,
            EmploymentType = See(PersonFields.EmploymentType) ? v.EmploymentType : null,
            JobType = See(PersonFields.JobType) ? v.JobType : null,
            HireDate = See(PersonFields.HireDate) ? v.HireDate : null,
            TerminationDate = See(PersonFields.TerminationDate) ? DisclosedTermination(v.TerminationDate, hideFuture) : null,
            EmploymentStatus = See(PersonFields.EmploymentStatus) ? (person.IsTerminatedOn(_data.AsOf) ? "terminated" : "active") : null,
            AccountStatus = See(PersonFields.AccountStatus) ? (person.IsSuspended ? "suspended" : "active") : null,
            AccountSuspension = See(PersonFields.AccountSuspension) && person.Suspension is { } suspension
                ? new SuspensionView(suspension.From, DisclosedEnd(suspension.Until, hideFuture))
                : null,
            Assignments = See(PersonFields.Assignments) ? ProjectAssignments(personId, hideFuture) : null,
            Managers = See(PersonFields.Managers) ? [.. _data.DirectManagersOf(personId).Order()] : null,
            PersonnelActions = See(PersonFields.PersonnelActions) ? ProjectActions(person, viewer.IsHrAdmin) : null,
            NameHistory = See(PersonFields.NameHistory) && viewer.IsHrAdmin ? ProjectNameHistory(person) : null,
        };
    }

    /// <summary>閲覧者に発令の存在を見せてよいか（内示の秘匿。RLS の personnel_action の規則と同じ）。</summary>
    public static bool IsActionDisclosed(PersonnelActionRecord action, bool viewerIsHrAdmin, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(action);
        return viewerIsHrAdmin || action.EffectiveDate <= today;
    }

    /// <summary>
    /// 人事管理者以外の検索・参照に出してよいか（退職者の一般公開の終了。ADR-0002）。
    /// 退職年月日が無いか、退職年月日 + 公開日数が業務日付以後なら出す。
    /// </summary>
    public static bool IsPubliclyListed(PersonVersion version, DateOnly today, int retiredPublicDays)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.TerminationDate is not { } terminated || terminated.AddDays(retiredPublicDays) >= today;
    }

    /// <summary>
    /// 有効期間の終了日（この日を含まない）を見せてよい形にする。将来の終了日は人事管理者以外には null（無期限と同じ）にする。
    /// 終了日が業務日付以前（既に終わった期間。過去の時点を指定したとき）ならそのまま返す。
    /// </summary>
    private DateOnly? DisclosedEnd(DateOnly? until, bool hideFuture) =>
        hideFuture && until is { } end && end > _today ? null : until;

    /// <summary>退職年月日（最終在籍日）は、人事管理者以外には退職済み（最終在籍日 &lt; 業務日付）になるまで返さない。</summary>
    private DateOnly? DisclosedTermination(DateOnly? terminationDate, bool hideFuture) =>
        hideFuture && terminationDate is { } last && last >= _today ? null : terminationDate;

    private PersonVersion? CurrentVersion(PersonRecord person) =>
        person.Current.IsValidOn(_today) ? person.Current : person.History.FirstOrDefault(h => h.IsValidOn(_today));

    private List<AssignmentView> ProjectAssignments(Guid personId, bool hideFuture) =>
    [
        .. _data.AssignmentsOf(personId).Select(a =>
        {
            var unit = _data.FindUnit(a.OrgUnitId)!;
            return new AssignmentView(
                unit.Id, unit.Code, unit.Name, unit.LegalEntityId, a.Type, a.PositionTitle, a.Secondment, a.ValidFrom, DisclosedEnd(a.ValidUntil, hideFuture));
        }),
    ];

    private List<PersonnelActionView> ProjectActions(PersonRecord person, bool isHrAdmin) =>
    [
        .. person.Actions
            .Where(a => IsActionDisclosed(a, isHrAdmin, _today))
            .OrderBy(a => a.EffectiveDate)
            .Select(a => new PersonnelActionView(a.Id, a.ActionType, a.EffectiveDate, a.OrgUnitId, a.AssignmentType, a.PositionTitle, a.DocumentNumber)),
    ];

    private static List<NameVersionView> ProjectNameHistory(PersonRecord person) =>
    [
        .. person.History
            .OrderBy(h => h.ValidFrom)
            .Select(h => new NameVersionView(h.VersionId, h.ValidFrom, h.ValidUntil, h.FamilyName, h.GivenName, h.FamilyNameKana, h.GivenNameKana, h.DisplayName)),
    ];
}
