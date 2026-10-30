using JinJi.Application.Audit;
using JinJi.Application.Presence;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Policy;

namespace JinJi.Application.People;

/// <summary>
/// 人の参照（時点指定）。REST・MCP・UI はすべてこのサービスを通して人の情報を返す。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>結果は <see cref="PersonView"/>（見える項目だけを持つ投影）。認可エンジンを通らない値は返せない</item>
/// <item>as_of に今日より後の日付を指定できるのは人事管理者だけ（未来を見ると内示が見えるため）。それ以外は denied を記録して拒否する</item>
/// <item>人事管理者以外では、氏名などの属性は as_of に関わらず今日有効な版を返す（氏名の履歴の秘匿）</item>
/// <item>関係と組織単位の規則は今日時点の組織で評価し、値は as_of 時点のスナップショットから取る</item>
/// <item>人事管理者以外には、退職後の公開期間（<see cref="PersonDirectoryOptions.RetiredPublicDays"/>）を過ぎた人を返さない（一覧にも個別の参照にも）</item>
/// <item>参照はリクエスト単位で監査ログに 1 件記録する（返した人の ID の集合）</item>
/// </list>
/// スナップショットは <see cref="IOrgSnapshotProvider"/> から得る。今日の時点のものは <see cref="Organization.OrgSnapshotCache"/> がメモリ上にキャッシュする。
/// </remarks>
public sealed class PersonDirectoryService
{
    public const string ReadAction = "person.read";
    public const string ListAction = "person.list";
    public const string SearchAction = "person.search";
    public const string ManagerAction = "person.manager";
    public const string ApprovalRouteAction = "person.approval_route";

    private readonly IOrgSnapshotProvider _snapshots;
    private readonly IPolicyRuleStore _policies;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly PersonDirectoryOptions _options;
    private readonly PresenceService? _presence;

    /// <param name="presence">当日の所在の表示（外部の提供。1 人の参照の項目 presence）。</param>
    public PersonDirectoryService(
        IOrgSnapshotProvider snapshots,
        IPolicyRuleStore policies,
        AuditWriter audit,
        TimeProvider timeProvider,
        PersonDirectoryOptions? options = null,
        PresenceService? presence = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(policies);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options ?? new PersonDirectoryOptions();
        _options.Validate();
        _snapshots = snapshots;
        _policies = policies;
        _audit = audit;
        _timeProvider = timeProvider;
        _presence = presence;
    }

    /// <summary>指定した人を参照する。居ない人・何も見えない人は結果に含めない（存在の有無を区別させない）。</summary>
    /// <param name="includePresence">
    /// 当日の所在（項目 presence。外部の提供）を投影するか。1 人の参照（REST の <c>GET /people/{id}</c>・MCP の get_person）だけが true にする
    /// （一覧・検索・一括取得には出さない）。今日を表示しているときだけ効く。
    /// </param>
    public Task<IReadOnlyList<PersonView>> GetPeopleAsync(
        ViewerContext viewer,
        IReadOnlyCollection<Guid> personIds,
        DateOnly? asOf,
        CancellationToken cancellationToken,
        bool includePresence = false)
    {
        ArgumentNullException.ThrowIfNull(personIds);
        return RunAsync<IReadOnlyList<PersonView>>(
            viewer,
            new Request(ReadAction, personIds, asOf),
            (_, projector) => [.. personIds.Distinct().Select(id => projector.Project(viewer, id)).OfType<PersonView>()],
            views => views.Select(v => v.PersonId),
            cancellationToken,
            includePresence && personIds.Count == 1);
    }

    /// <summary>その時点の在籍者（スナップショットの全員。人事管理者以外には退職後の公開期間を過ぎた人を除く）を参照する。</summary>
    public Task<IReadOnlyList<PersonView>> ListPeopleAsync(ViewerContext viewer, DateOnly? asOf, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<PersonView>>(
            viewer,
            new Request(ListAction, [], asOf),
            (data, projector) => [.. data.People.Select(p => p.Id).Order().Select(id => projector.Project(viewer, id)).OfType<PersonView>()],
            views => views.Select(v => v.PersonId),
            cancellationToken);

    /// <summary>
    /// 人を検索する（<see cref="PersonSearch"/> の条件・並び順）。結果は並び順で <see cref="PersonSearch.After"/> の人より後の <see cref="PersonSearch.Limit"/> 件。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>照合は検索用の正規化表記（<see cref="SearchKey"/>。取込の search_name と同じ規則）と、読み（<see cref="ReadingKey"/>）で行う。ワイルドカードは受け付けない</item>
    /// <item>照合・絞り込み・件数・並べ替えに使うのは、閲覧者に見える項目（投影した後の値）だけ。見えない項目で当たるかどうか・数えられるかどうかから
    /// 値を推測させないため（所属で絞り込むときも、所属の項目が見えない人は当たらない）。件数の規則は <see cref="PersonFacets"/></item>
    /// <item>組織で絞り込むとき、その組織がその時点に無ければ null（呼び出し側は「見つからない」として扱う）</item>
    /// <item>続きの位置の人を参照できなくなったら <see cref="StaleCursorException"/>（ID の順以外の並びのとき）</item>
    /// </list>
    /// </remarks>
    public Task<PersonPage?> SearchPeopleAsync(ViewerContext viewer, PersonSearch search, DateOnly? asOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var key = search.Validate();
        return RunAsync(
            viewer,
            new Request(SearchAction, [], asOf, search.OrgUnitId is null ? "person" : "org_unit", search.OrgUnitId),
            (data, projector) => search.OrgUnitId is { } org && data.FindUnit(org) is null
                ? null
                : new PersonSearchEngine(data, projector, viewer, search, key).Run(),
            page => page?.Items.Select(v => v.PersonId) ?? [],
            cancellationToken);
    }

    /// <summary>
    /// 人の直属の上長を参照する。対象者が見えない・上長の項目が見えなければ null（存在の有無を区別させない）。上長のうち見えない人は結果に含めない。
    /// </summary>
    public Task<IReadOnlyList<PersonView>?> GetManagersAsync(ViewerContext viewer, Guid personId, DateOnly? asOf, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<PersonView>?>(
            viewer,
            new Request(ManagerAction, [personId], asOf, "person", personId),
            (_, projector) => projector.Project(viewer, personId) is { } target && target.CanSee(PersonFields.Managers)
                ? [.. target.Managers!.Select(id => projector.Project(viewer, id)).OfType<PersonView>()]
                : null,
            managers => managers is null ? [] : [personId, .. managers.Select(v => v.PersonId)],
            cancellationToken);

    /// <summary>
    /// 承認ルート（起点の人から上位の組織の組織長をたどった承認者の並び。規則は <see cref="ApprovalRouteBuilder"/>）を参照する。
    /// 起点の人が見えない・所属か上長の項目が見えない・指定した組織に所属していなければ null（存在の有無を区別させない）。
    /// </summary>
    /// <exception cref="ArgumentException">問い合わせの形が不正。</exception>
    public Task<ApprovalRoute?> GetApprovalRouteAsync(ViewerContext viewer, ApprovalRouteQuery query, DateOnly? asOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        query.Validate();
        return RunAsync(
            viewer,
            new Request(ApprovalRouteAction, [query.PersonId], asOf, "person", query.PersonId),
            (data, projector) => new ApprovalRouteBuilder(data, projector, viewer).Build(query),
            route => route is null ? [] : [query.PersonId, .. route.ApproverIds.Where(id => id != query.PersonId)],
            cancellationToken);
    }

    /// <summary>
    /// 指定した人のうち、閲覧者がその時点で参照できる人（投影できる人。<see cref="GetPeopleAsync"/> の結果に現れる人と同じ）。
    /// Webhook の宛先の判定に使う（受け手のクライアントを閲覧者にして、見えない人の変更を送らない）。値は返さず、監査ログにも残さない
    /// （呼び出し側が送信を webhook.deliver として記録する）。時点が閲覧者の上限より後なら空。
    /// </summary>
    public async Task<IReadOnlySet<Guid>> VisiblePeopleAsync(
        ViewerContext viewer,
        IReadOnlyCollection<Guid> personIds,
        DateOnly asOf,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(personIds);
        var today = BusinessDate.Today(_timeProvider);
        if (personIds.Count == 0 || asOf > viewer.DisclosureHorizon(today))
        {
            return new HashSet<Guid>();
        }

        var (data, relations, projectionDate) = await LoadAsync(_snapshots, viewer, asOf, today, cancellationToken);
        var policies = await _policies.LoadActiveAsync(viewer, cancellationToken);
        var projector = new PersonProjector(new PolicyEvaluator(policies, new RelationEvaluator(relations)), data, projectionDate, _options.RetiredPublicDays);
        return personIds.Where(id => projector.Project(viewer, id) is not null).ToHashSet();
    }

    /// <summary>
    /// 今日、閲覧者にその項目が見える人（グループのメンバーの一覧を項目 groups で狭めるのに使う。ADR-0014）。値は返さず、監査ログにも残さない
    /// （呼び出し側がグループの参照として記録する）。
    /// </summary>
    public async Task<IReadOnlySet<Guid>> WithVisibleFieldAsync(
        ViewerContext viewer,
        IReadOnlyCollection<Guid> personIds,
        string field,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(personIds);
        var today = BusinessDate.Today(_timeProvider);
        if (personIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var (data, relations, projectionDate) = await LoadAsync(_snapshots, viewer, today, today, cancellationToken);
        var policies = await _policies.LoadActiveAsync(viewer, cancellationToken);
        var projector = new PersonProjector(new PolicyEvaluator(policies, new RelationEvaluator(relations)), data, projectionDate, _options.RetiredPublicDays);
        return personIds.Where(id => projector.Project(viewer, id) is { } view && view.CanSee(field)).ToHashSet();
    }

    private async Task<T> RunAsync<T>(
        ViewerContext viewer,
        Request request,
        Func<OrgSnapshot, PersonProjector, T> run,
        Func<T, IEnumerable<Guid>> subjects,
        CancellationToken cancellationToken,
        bool includePresence = false)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var today = BusinessDate.Today(_timeProvider);
        var date = request.AsOf ?? today;

        if (date > viewer.DisclosureHorizon(today))
        {
            await _audit.RecordAsync(viewer, request.Action, AuditOutcome.Denied, request.Requested, cancellationToken, request.TargetType, request.TargetId);
            throw new AccessDeniedException(FutureDenied(viewer, today));
        }

        T result;
        try
        {
            var (data, relations, projectionDate) = await LoadAsync(_snapshots, viewer, date, today, cancellationToken);
            var policies = await _policies.LoadActiveAsync(viewer, cancellationToken);

            // 当日の所在（外部の提供。今日の 1 人の参照だけ）。組み込みの公開範囲の規則を足して判定する
            var presence = includePresence && _presence is not null
                ? await _presence.ForPeopleAsync(viewer, request.Requested, date, data, cancellationToken)
                : null;
            if (presence is not null)
            {
                policies = _presence!.WithPresenceRule(policies);
            }

            var projector = new PersonProjector(new PolicyEvaluator(policies, new RelationEvaluator(relations)), data, projectionDate, _options.RetiredPublicDays, presence);
            result = run(data, projector);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.RecordAsync(viewer, request.Action, AuditOutcome.Failed, request.Requested, CancellationToken.None, request.TargetType, request.TargetId);
            throw;
        }

        await _audit.RecordAsync(viewer, request.Action, AuditOutcome.Success, subjects(result), cancellationToken, request.TargetType, request.TargetId);
        return result;
    }

    /// <summary>未来の時点の拒否の理由（先行提供の社内システムには指定できる最後の日も示す）。</summary>
    internal static string FutureDenied(ViewerContext viewer, DateOnly today) =>
        viewer.DisclosureLeadDays > 0
            ? $"このクライアントが指定できる時点は {viewer.DisclosureHorizon(today):yyyy-MM-dd}（先行提供の {viewer.DisclosureLeadDays} 日先）までです。"
            : "未来の時点を指定できるのは人事管理者（と、先行提供を許された社内システム）だけです。";

    /// <summary>
    /// 時点のスナップショット・関係を評価する今日のスナップショット・投影の基準日を読む（人・組織の参照で共通）。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>関係（ポリシーの判定）は時点に関わらず今日の組織で評価する。今日のスナップショットは先行提供を外した閲覧者で読む（キャッシュを共有する）</item>
    /// <item>今日以前の時点も先行提供を外した閲覧者で読み、投影は今日を基準にする（先行提供の有無で今日以前の見え方を変えない）</item>
    /// <item>先行提供の社内システムが今日より後の時点（上限以内。呼び出し側が確かめる）を見るときは、その時点の人事管理者以外の閲覧者と同じに見せる:
    /// 属性はその時点の版、投影の基準日（内示・将来の終了日・退職年月日・退職後の公開期間の判定）もその時点。RLS は上限（業務日付 + 日数）で線を引く</item>
    /// <item>人事管理者は従来どおり（時点の属性・業務日付が基準）</item>
    /// </list>
    /// </remarks>
    internal static async Task<(OrgSnapshot Data, OrgSnapshot Relations, DateOnly ProjectionDate)> LoadAsync(
        IOrgSnapshotProvider snapshots,
        ViewerContext viewer,
        DateOnly date,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var relations = await snapshots.LoadAsync(viewer.WithoutDisclosureLead(), today, today, cancellationToken);
        if (date == today)
        {
            return (relations, relations, today);
        }

        if (viewer.IsHrAdmin)
        {
            return (await snapshots.LoadAsync(viewer, date, date, cancellationToken), relations, today);
        }

        return date < today
            ? (await snapshots.LoadAsync(viewer.WithoutDisclosureLead(), date, today, cancellationToken), relations, today)
            : (await snapshots.LoadAsync(viewer, date, date, cancellationToken), relations, date);
    }

    private sealed record Request(string Action, IReadOnlyCollection<Guid> Requested, DateOnly? AsOf, string TargetType = "person", Guid? TargetId = null);
}
