using JinJi.Application.Audit;
using JinJi.Application.People;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Policy;

namespace JinJi.Application.Organization;

/// <summary>
/// 組織の参照（時点指定）。REST・MCP はこのサービスを通して組織の情報を返す。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>組織の情報（名称・コード・段・上位組織・拠点）は全社公開。スナップショットは閲覧者を RLS に渡して読むので、
/// 人事管理者以外には組織改編の予定（有効期間の開始が今日より後の版）が現れない</item>
/// <item>as_of に今日より後の日付を指定できるのは人事管理者だけ（<see cref="PersonDirectoryService"/> と同じ）</item>
/// <item>組織長は人の ID なので、閲覧者にその人が見える（<see cref="PersonProjector"/> が投影を返す）ときだけ返す（退職後の公開期間を過ぎた人など）</item>
/// <item>参照はリクエスト単位で監査ログに 1 件記録する（返した組織長の ID の集合）</item>
/// </list>
/// </remarks>
public sealed class OrgDirectoryService
{
    public const string ReadAction = "org.read";
    public const string TreeAction = "org.tree";
    public const string SearchAction = "org.search";
    public const string StatsAction = "org.stats";
    public const string SiteListAction = "site.list";
    public const string SiteReadAction = "site.read";

    private readonly IOrgSnapshotProvider _snapshots;
    private readonly IPolicyRuleStore _policies;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly PersonDirectoryOptions _options;

    public OrgDirectoryService(
        IOrgSnapshotProvider snapshots,
        IPolicyRuleStore policies,
        AuditWriter audit,
        TimeProvider timeProvider,
        PersonDirectoryOptions? options = null)
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
    }

    /// <summary>組織を参照する。その時点に無ければ null。</summary>
    public async Task<OrgUnitView?> GetUnitAsync(ViewerContext viewer, Guid unitId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var units = await RunAsync<IReadOnlyList<OrgUnitView>>(viewer, ReadAction, unitId, asOf, (data, _, visible) =>
            data.FindUnit(unitId) is { } unit ? [ToView(data, unit, visible)] : [], HeadsOf, cancellationToken);
        return units.Count == 0 ? null : units[0];
    }

    /// <summary>
    /// 組織と、その上位組織（直近の上位から最上位まで）・直下の組織（ツリーの順）を参照する。その時点に無ければ null。
    /// </summary>
    /// <remarks>上位・直下の組織も全社公開の情報。組織長は閲覧者に見える人だけ（<see cref="GetUnitAsync"/> と同じ）。監査は <see cref="ReadAction"/>。</remarks>
    public Task<OrgUnitContext?> GetUnitContextAsync(ViewerContext viewer, Guid unitId, DateOnly? asOf, CancellationToken cancellationToken) =>
        RunAsync<OrgUnitContext?>(viewer, ReadAction, unitId, asOf, (data, _, visible) =>
            {
                if (data.FindUnit(unitId) is not { } unit)
                {
                    return null;
                }

                var ancestors = data.AncestorsOf(unitId).Skip(1).Select(data.FindUnit).OfType<OrgUnit>().Select(u => ToView(data, u, visible)).ToList();
                var children = TreeOrder(data).Where(u => u.ParentId == unitId).Select(u => ToView(data, u, visible)).ToList();
                return new OrgUnitContext(ToView(data, unit, visible), ancestors, children);
            },
            context => context is null ? [] : HeadsOf([context.Unit, .. context.Ancestors, .. context.Children]),
            cancellationToken);

    /// <summary>
    /// 組織図の部分木（<paramref name="rootId"/> とその配下を <paramref name="depth"/> 段まで。省略なら最上位の組織から）。ツリーの順の平らな一覧で、
    /// <paramref name="maxUnits"/> を超えた分は返さずに打ち切る。根の組織がその時点に無ければ null。
    /// </summary>
    /// <remarks>人は返さない（組織長は ID だけで、閲覧者に見える人だけ）。監査は <see cref="TreeAction"/>。</remarks>
    public Task<OrgSubtree?> GetSubtreeAsync(ViewerContext viewer, Guid? rootId, int depth, int maxUnits, DateOnly? asOf, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxUnits, 1);
        return RunAsync<OrgSubtree?>(viewer, TreeAction, rootId, asOf, (data, _, visible) =>
            {
                if (rootId is { } root && data.FindUnit(root) is null)
                {
                    return null;
                }

                // 根からの深さ（根 = 0）。根を省略したら最上位の組織（上位組織がスナップショットに無い組織）が深さ 0
                var selected = new List<(OrgUnit Unit, int Depth)>();
                foreach (var unit in TreeOrder(data))
                {
                    var chain = data.AncestorsOf(unit.Id);
                    var level = rootId is { } r ? IndexOf(chain, r) : chain.Count - 1;
                    if (level is >= 0 && level < depth)
                    {
                        selected.Add((unit, level));
                    }
                }

                return new OrgSubtree(
                    data.AsOf,
                    [.. selected.Take(maxUnits).Select(s => new OrgTreeNode(ToView(data, s.Unit, visible), s.Depth))],
                    selected.Count > maxUnits);
            },
            subtree => subtree is null ? [] : HeadsOf([.. subtree.Units.Select(u => u.Unit)]),
            cancellationToken);
    }

    private static int IndexOf(IReadOnlyList<Guid> chain, Guid id)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            if (chain[i] == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>その時点の全組織（上位組織の ID で木を組める平らな一覧。法人・段の順位・コードの順）。</summary>
    public Task<IReadOnlyList<OrgUnitView>> GetTreeAsync(ViewerContext viewer, DateOnly? asOf, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<OrgUnitView>>(viewer, TreeAction, null, asOf, (data, _, visible) =>
            [.. TreeOrder(data).Select(u => ToView(data, u, visible))], HeadsOf, cancellationToken);

    /// <summary>
    /// 組織を検索する（組織名・略称は組織名の正規化表記 <see cref="OrgNameKey"/>、組織コードは <see cref="SearchKey"/> の部分一致）。
    /// 完全一致・前方一致・部分一致の順、同順位はツリーの順。
    /// </summary>
    /// <remarks>組織の情報は全社公開なので、照合に使う項目の制限は無い（組織長だけは見える人に限る）。</remarks>
    public Task<OrgSearchResult> SearchAsync(ViewerContext viewer, OrgSearch search, DateOnly? asOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var (nameKey, codeKey) = search.Validate();
        return RunAsync(viewer, SearchAction, null, asOf, (data, _, visible) =>
            {
                var matches = TreeOrder(data)
                    .Select((unit, order) => (Unit: unit, Order: order, Rank: MatchRank(unit, nameKey, codeKey)))
                    .Where(m => m.Rank is not null)
                    .OrderBy(m => m.Rank)
                    .ThenBy(m => m.Order)
                    .Take(search.Limit + 1)
                    .ToList();
                return new OrgSearchResult(
                    [.. matches.Take(search.Limit).Select(m => ToView(data, m.Unit, visible))],
                    matches.Count > search.Limit);
            },
            result => HeadsOf(result.Items),
            cancellationToken);
    }

    /// <summary>
    /// 組織ごとの人数と、兼務・出向の組織間の集計。数えるのは閲覧者に見える人（投影でき、所属の項目が見える人）だけ。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>数える人は所属者の一覧（<see cref="People.PersonDirectoryService.SearchPeopleAsync"/> の組織での絞り込み）に現れる人と同じ。
    /// 内示（入社予定・将来の異動）・退職後の公開期間を過ぎた人・ポリシーで所属の項目が見えない人・クライアントの項目上限で所属を渡せない人は数えない。
    /// 人数の差から非公開の人の存在を推測させないため</item>
    /// <item>所属ごとに 1 つの区分に数える: 出向（出向の区別がある所属。主務・兼務とも）、主務（出向でない主務）、兼務（出向でない兼務）</item>
    /// <item>配下を含む主務は、その組織か配下の組織に出向でない主務を持つ人の数（重複なし）</item>
    /// <item>組織間の集計は、人ごとに「主務の組織 → 兼務・出向の組織」の組を作り、組ごとに人数を数える。
    /// 出向でない主務が無い人は主務（出向）の組織を起点にし、起点と同じ組織への組は作らない</item>
    /// <item>監査ログには集計の参照を 1 件記録する（人は返さないので対象者は空）</item>
    /// </list>
    /// </remarks>
    public Task<OrgStats> GetStatsAsync(ViewerContext viewer, DateOnly? asOf, CancellationToken cancellationToken) =>
        RunAsync(viewer, StatsAction, null, asOf, (data, projector, _) => ComputeStats(viewer, data, projector), _ => [], cancellationToken);

    private static OrgStats ComputeStats(ViewerContext viewer, OrgSnapshot data, PersonProjector projector)
    {
        var primary = new Dictionary<Guid, int>();
        var primaryTotal = new Dictionary<Guid, int>();
        var concurrent = new Dictionary<Guid, int>();
        var secondment = new Dictionary<Guid, int>();
        var links = new Dictionary<(Guid From, Guid To, OrgLinkKind Kind), int>();

        foreach (var person in data.People)
        {
            if (projector.Project(viewer, person.Id) is not { Assignments: { } assignments } view || !view.CanSee(PersonFields.Assignments))
            {
                continue;
            }

            var home = new HashSet<Guid>();
            var subtree = new HashSet<Guid>();
            var seen = new HashSet<(Guid, OrgLinkKind?)>();
            foreach (var a in assignments)
            {
                OrgLinkKind? kind = a.Secondment != SecondmentType.None ? OrgLinkKind.Secondment
                    : a.Type == AssignmentType.Concurrent ? OrgLinkKind.Concurrent
                    : null;
                if (!seen.Add((a.OrgUnitId, kind)))
                {
                    continue;
                }

                switch (kind)
                {
                    case OrgLinkKind.Secondment:
                        Increment(secondment, a.OrgUnitId);
                        break;
                    case OrgLinkKind.Concurrent:
                        Increment(concurrent, a.OrgUnitId);
                        break;
                    default:
                        Increment(primary, a.OrgUnitId);
                        home.Add(a.OrgUnitId);
                        subtree.UnionWith(data.AncestorsOf(a.OrgUnitId));
                        break;
                }
            }

            foreach (var unit in subtree)
            {
                Increment(primaryTotal, unit);
            }

            if (home.Count == 0)
            {
                home.UnionWith(assignments.Where(a => a.Type == AssignmentType.Primary).Select(a => a.OrgUnitId));
            }

            var pairs = new HashSet<(Guid, Guid, OrgLinkKind)>();
            foreach (var a in assignments.Where(a => !home.Contains(a.OrgUnitId) && (a.Type == AssignmentType.Concurrent || a.Secondment != SecondmentType.None)))
            {
                var kind = a.Secondment != SecondmentType.None ? OrgLinkKind.Secondment : OrgLinkKind.Concurrent;
                foreach (var from in home)
                {
                    if (pairs.Add((from, a.OrgUnitId, kind)))
                    {
                        links[(from, a.OrgUnitId, kind)] = links.GetValueOrDefault((from, a.OrgUnitId, kind)) + 1;
                    }
                }
            }
        }

        var units = TreeOrder(data)
            .Select(u => new OrgHeadcount(
                u.Id,
                primary.GetValueOrDefault(u.Id),
                primaryTotal.GetValueOrDefault(u.Id),
                concurrent.GetValueOrDefault(u.Id),
                secondment.GetValueOrDefault(u.Id)))
            .ToList();
        var order = units.Select((u, i) => (u.OrgUnitId, i)).ToDictionary(x => x.OrgUnitId, x => x.i);
        var linkList = links
            .Select(l => new OrgLink(l.Key.From, l.Key.To, l.Key.Kind, l.Value))
            .OrderBy(l => order.GetValueOrDefault(l.FromOrgUnitId))
            .ThenBy(l => order.GetValueOrDefault(l.ToOrgUnitId))
            .ThenBy(l => l.Kind)
            .ToList();
        return new OrgStats(data.AsOf, units, linkList);
    }

    private static void Increment(Dictionary<Guid, int> counts, Guid key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    /// <summary>
    /// 拠点の一覧（その時点で有効な拠点。法人のコード・拠点コードの順）と、拠点ごとの人数。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>拠点の情報（コード・名称・種別・所在地・拠点を持つ組織）は組織と同じく全社公開。人事管理者以外には開設・変更の予定（将来の版）は現れない（RLS）</item>
    /// <item>人数は件数の安全性の規則（§7.2）と同じく、閲覧者に結果として返し得る人（投影できる人）のうち、項目 site が見える人だけを数える。
    /// リモートワークの人数は、さらに項目 remote_work が見えて対象の人だけ。「非公開」の区分は作らない</item>
    /// <item>人は返さないので、集計（組織ごとの人数）と同じく返却人数の制限の対象外。監査ログは <see cref="SiteListAction"/>（対象者は空）</item>
    /// </list>
    /// </remarks>
    public Task<IReadOnlyList<SiteSummary>> ListSitesAsync(ViewerContext viewer, SiteSearch search, DateOnly? asOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(search);
        var (nameKey, codeKey) = search.Validate();
        return RunAsync<IReadOnlyList<SiteSummary>>(viewer, SiteListAction, null, asOf, (data, projector, _) =>
            {
                var counts = CountSiteMembers(viewer, data, projector);
                return [.. SiteOrder(data)
                    .Where(s => search.LegalEntityIds.Count == 0 || search.LegalEntityIds.Contains(s.LegalEntityId))
                    .Where(s => search.Types.Count == 0 || search.Types.Contains(SiteCodes.ToCode(s.Type)))
                    .Where(s => search.Prefectures.Count == 0 || (s.PrefectureCode is { } p && search.Prefectures.Contains(p)))
                    .Where(s => nameKey is null || MatchesSite(s, nameKey, codeKey))
                    .Select(s => Summarize(data, s, counts))];
            },
            _ => [],
            cancellationToken,
            "site");
    }

    /// <summary>拠点の詳細（人数は <see cref="ListSitesAsync"/> と同じ規則）。その時点に無ければ null。監査は <see cref="SiteReadAction"/>。</summary>
    public Task<SiteSummary?> GetSiteAsync(ViewerContext viewer, Guid siteId, DateOnly? asOf, CancellationToken cancellationToken) =>
        RunAsync<SiteSummary?>(viewer, SiteReadAction, siteId, asOf, (data, projector, _) =>
            data.FindSite(siteId) is { } site ? Summarize(data, site, CountSiteMembers(viewer, data, projector)) : null,
            _ => [],
            cancellationToken,
            "site");

    private static Dictionary<Guid, (int Members, int Remote)> CountSiteMembers(ViewerContext viewer, OrgSnapshot data, PersonProjector projector)
    {
        var counts = new Dictionary<Guid, (int Members, int Remote)>();
        foreach (var person in data.People)
        {
            if (projector.Project(viewer, person.Id) is not { Site: { } site } view || !view.CanSee(PersonFields.Site))
            {
                continue;
            }

            var (members, remote) = counts.GetValueOrDefault(site.Id);
            counts[site.Id] = (members + 1, remote + (view.CanSee(PersonFields.RemoteWork) && view.RemoteWork == true ? 1 : 0));
        }

        return counts;
    }

    private static SiteSummary Summarize(OrgSnapshot data, Site site, Dictionary<Guid, (int Members, int Remote)> counts)
    {
        var entity = data.FindLegalEntity(site.LegalEntityId);
        var (members, remote) = counts.GetValueOrDefault(site.Id);
        return new SiteSummary(
            site,
            entity?.Code,
            entity?.Name,
            members,
            remote,
            [.. TreeOrder(data).Where(u => u.SiteId == site.Id).Select(u => new SiteOrgUnit(u.Id, u.Code, u.Name))]);
    }

    private static IEnumerable<Site> SiteOrder(OrgSnapshot data) =>
        data.Sites
            .OrderBy(s => data.FindLegalEntity(s.LegalEntityId)?.Code, StringComparer.Ordinal)
            .ThenBy(s => s.Code, StringComparer.Ordinal);

    private static bool MatchesSite(Site site, string nameKey, string? codeKey) =>
        (OrgNameKey.From(site.Name) is { } name && name.Contains(nameKey, StringComparison.Ordinal))
        || (codeKey is not null && SearchKey.From(site.Code) is { } code && code.Contains(codeKey, StringComparison.Ordinal));

    /// <summary>照合の順位（0: 完全一致、1: 前方一致、2: 部分一致）。当たらなければ null。</summary>
    private static int? MatchRank(OrgUnit unit, string nameKey, string? codeKey)
    {
        int? best = null;
        void Consider(string? candidate, string? key)
        {
            if (candidate is null || key is null)
            {
                return;
            }

            int? rank = candidate == key ? 0 : candidate.StartsWith(key, StringComparison.Ordinal) ? 1 : candidate.Contains(key, StringComparison.Ordinal) ? 2 : null;
            if (rank is { } r && (best is null || r < best))
            {
                best = r;
            }
        }

        Consider(OrgNameKey.From(unit.Name), nameKey);
        Consider(OrgNameKey.From(unit.ShortName), nameKey);
        Consider(SearchKey.From(unit.Code), codeKey);
        return best;
    }

    /// <summary>ツリーの順（法人のコード・深さ・組織コード）。</summary>
    private static IEnumerable<OrgUnit> TreeOrder(OrgSnapshot data) =>
        data.Units
            .OrderBy(u => data.FindLegalEntity(u.LegalEntityId)?.Code, StringComparer.Ordinal)
            .ThenBy(u => data.AncestorsOf(u.Id).Count)
            .ThenBy(u => u.Code, StringComparer.Ordinal);

    private static IEnumerable<Guid> HeadsOf(IReadOnlyList<OrgUnitView> views) => views.Select(v => v.HeadPersonId).OfType<Guid>();

    private static OrgUnitView ToView(OrgSnapshot data, OrgUnit unit, Func<Guid, bool> visible)
    {
        var entity = data.FindLegalEntity(unit.LegalEntityId);
        var level = unit.LevelKey is { } key ? data.LevelSchemeOf(unit.LegalEntityId).Find(key) : null;
        return new OrgUnitView(
            unit.Id,
            unit.LegalEntityId,
            entity?.Code,
            entity?.Name,
            unit.Code,
            unit.Name,
            unit.ShortName,
            level?.Key,
            level?.Name,
            level?.Rank,
            unit.ParentId is { } parent && data.FindUnit(parent) is not null ? parent : null,
            unit.HeadPersonId is { } head && visible(head) ? head : null,
            data.SiteOfUnit(unit.Id) is { } site ? SiteView.From(site) : null);
    }

    private async Task<T> RunAsync<T>(
        ViewerContext viewer,
        string action,
        Guid? targetId,
        DateOnly? asOf,
        Func<OrgSnapshot, PersonProjector, Func<Guid, bool>, T> run,
        Func<T, IEnumerable<Guid>> subjects,
        CancellationToken cancellationToken,
        string targetType = "org_unit")
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var today = BusinessDate.Today(_timeProvider);
        var date = asOf ?? today;
        if (date > viewer.DisclosureHorizon(today))
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, targetType, targetId);
            throw new AccessDeniedException(PersonDirectoryService.FutureDenied(viewer, today));
        }

        T result;
        try
        {
            var (data, relations, projectionDate) = await PersonDirectoryService.LoadAsync(_snapshots, viewer, date, today, cancellationToken);
            var policies = await _policies.LoadActiveAsync(viewer, cancellationToken);
            var projector = new PersonProjector(new PolicyEvaluator(policies, new RelationEvaluator(relations)), data, projectionDate, _options.RetiredPublicDays);
            var cache = new Dictionary<Guid, bool>();
            bool Visible(Guid personId)
            {
                if (!cache.TryGetValue(personId, out var seen))
                {
                    seen = projector.Project(viewer, personId) is not null;
                    cache.Add(personId, seen);
                }

                return seen;
            }

            result = run(data, projector, Visible);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, [], CancellationToken.None, targetType, targetId);
            throw;
        }

        await _audit.RecordAsync(viewer, action, AuditOutcome.Success, subjects(result), cancellationToken, targetType, targetId);
        return result;
    }
}

/// <summary>組織の投影。</summary>
/// <param name="LevelKey">段のキー（法人の段の定義に無い・段を持たない組織は null）。</param>
/// <param name="ParentId">上位組織（その時点のスナップショットに無ければ null）。</param>
/// <param name="HeadPersonId">組織長（閲覧者に見える人だけ）。</param>
/// <param name="Site">組織の拠点（自分の拠点か、上位組織から引き継いだ拠点。拠点の情報は組織と同じく全社公開）。</param>
public sealed record OrgUnitView(
    Guid Id,
    Guid LegalEntityId,
    string? LegalEntityCode,
    string? LegalEntityName,
    string Code,
    string Name,
    string? ShortName,
    string? LevelKey,
    string? LevelName,
    int? LevelRank,
    Guid? ParentId,
    Guid? HeadPersonId,
    SiteView? Site);

/// <summary>組織と、その上位組織（直近の上位が先頭）・直下の組織。</summary>
public sealed record OrgUnitContext(OrgUnitView Unit, IReadOnlyList<OrgUnitView> Ancestors, IReadOnlyList<OrgUnitView> Children);

/// <summary>組織図の部分木の 1 組織。</summary>
/// <param name="Depth">根からの深さ（根 = 0）。</param>
public sealed record OrgTreeNode(OrgUnitView Unit, int Depth);

/// <summary>組織図の部分木（ツリーの順）。</summary>
/// <param name="Truncated">上限で打ち切ったか（根を下に移すか、段を浅くして取り直す）。</param>
public sealed record OrgSubtree(DateOnly AsOf, IReadOnlyList<OrgTreeNode> Units, bool Truncated);

/// <summary>組織の検索の条件。</summary>
public sealed record OrgSearch
{
    /// <summary>1 回の最大件数。</summary>
    public const int MaxLimit = 100;

    /// <summary>検索語の最大の長さ（文字数）。</summary>
    public const int MaxQueryLength = 100;

    /// <summary>検索語（組織名・略称・組織コードの部分一致）。</summary>
    public required string Query { get; init; }

    /// <summary>件数（1〜<see cref="MaxLimit"/>）。</summary>
    public int Limit { get; init; } = 50;

    /// <summary>
    /// 条件を検査し、組織名の正規化表記と組織コードの正規化表記を返す。不正なら <see cref="ArgumentException"/>。
    /// 組織の情報は全社公開なので、人の検索と違い 1 文字の語も受け付ける（ワイルドカードは受け付けない）。
    /// </summary>
    public (string NameKey, string? CodeKey) Validate()
    {
        if (Limit is < 1 or > MaxLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(Limit), Limit, $"件数は 1〜{MaxLimit} で指定してください。");
        }

        if (Query.Length > MaxQueryLength)
        {
            throw new ArgumentException($"検索語は {MaxQueryLength} 文字以内で指定してください。", nameof(Query));
        }

        if (Query.Any(People.PersonSearch.WildcardCharacters.Contains))
        {
            throw new ArgumentException("検索語にワイルドカード（* % ?）は使えません（検索は部分一致です）。", nameof(Query));
        }

        return OrgNameKey.From(Query) is { } nameKey
            ? (nameKey, SearchKey.From(Query))
            : throw new ArgumentException("検索語を指定してください。", nameof(Query));
    }
}

/// <summary>組織の検索の結果。</summary>
/// <param name="HasMore">件数の上限で打ち切ったか。</param>
public sealed record OrgSearchResult(IReadOnlyList<OrgUnitView> Items, bool HasMore);

/// <summary>組織間の所属の種類。</summary>
public enum OrgLinkKind
{
    /// <summary>兼務（出向でない）。</summary>
    Concurrent,

    /// <summary>出向。</summary>
    Secondment,
}

/// <summary>組織の人数（閲覧者に見える人だけ）。</summary>
/// <param name="Primary">出向でない主務の人数。</param>
/// <param name="PrimaryTotal">その組織か配下の組織に出向でない主務を持つ人数（重複なし）。</param>
/// <param name="Concurrent">出向でない兼務の人数。</param>
/// <param name="Secondment">出向（主務・兼務とも）の人数。</param>
public sealed record OrgHeadcount(Guid OrgUnitId, int Primary, int PrimaryTotal, int Concurrent, int Secondment);

/// <summary>主務の組織から兼務・出向の組織への所属の、組ごとの人数。</summary>
public sealed record OrgLink(Guid FromOrgUnitId, Guid ToOrgUnitId, OrgLinkKind Kind, int Count);

/// <summary>組織ごとの集計。</summary>
/// <param name="Units">その時点の全組織（ツリーの順。0 人の組織も含む）。</param>
public sealed record OrgStats(DateOnly AsOf, IReadOnlyList<OrgHeadcount> Units, IReadOnlyList<OrgLink> Links);

/// <summary>拠点と、閲覧者に見える人の数（<see cref="OrgDirectoryService.ListSitesAsync"/>）。</summary>
/// <param name="MemberCount">この拠点の人の数（閲覧者に人と項目 site が見える人だけ）。</param>
/// <param name="RemoteWorkCount">そのうちリモートワークの対象の人の数（項目 remote_work も見える人だけ）。</param>
/// <param name="OrgUnits">この拠点を自分の拠点として持つ組織（組織図の順）。</param>
public sealed record SiteSummary(
    Site Site,
    string? LegalEntityCode,
    string? LegalEntityName,
    int MemberCount,
    int RemoteWorkCount,
    IReadOnlyList<SiteOrgUnit> OrgUnits);

/// <summary>拠点を持つ組織。</summary>
public sealed record SiteOrgUnit(Guid Id, string Code, string Name);

/// <summary>拠点の一覧の条件（すべて AND、同じ条件の中は OR）。</summary>
public sealed record SiteSearch
{
    /// <summary>検索語（拠点名の正規化表記・拠点コードの部分一致）。null なら絞り込まない。</summary>
    public string? Query { get; init; }

    public IReadOnlySet<Guid> LegalEntityIds { get; init; } = new HashSet<Guid>();

    /// <summary>拠点の種別（<see cref="SiteCodes.Types"/>）。</summary>
    public IReadOnlySet<string> Types { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>都道府県のコード。</summary>
    public IReadOnlySet<string> Prefectures { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>条件を検査し、検索語の正規化表記（拠点名・拠点コード）を返す。不正なら <see cref="ArgumentException"/>。</summary>
    public (string? NameKey, string? CodeKey) Validate()
    {
        if (LegalEntityIds.Count > People.PersonSearch.MaxValuesPerFilter || Types.Count > People.PersonSearch.MaxValuesPerFilter
            || Prefectures.Count > People.PersonSearch.MaxValuesPerFilter)
        {
            throw new ArgumentException($"1 つの条件に指定できる値は {People.PersonSearch.MaxValuesPerFilter} 件までです。", nameof(Types));
        }

        if (Types.FirstOrDefault(t => !SiteCodes.Types.Contains(t, StringComparer.Ordinal)) is { } badType)
        {
            throw new ArgumentException($"site_type は {string.Join(" / ", SiteCodes.Types)} で指定してください（{badType} は使えません）。", nameof(Types));
        }

        if (Prefectures.FirstOrDefault(p => !SiteCodes.IsPrefectureCode(p)) is { } badPrefecture)
        {
            throw new ArgumentException($"prefecture は都道府県のコード（01〜47）で指定してください（{badPrefecture} は使えません）。", nameof(Prefectures));
        }

        if (Query is null)
        {
            return (null, null);
        }

        if (Query.Length > OrgSearch.MaxQueryLength)
        {
            throw new ArgumentException($"検索語は {OrgSearch.MaxQueryLength} 文字以内で指定してください。", nameof(Query));
        }

        if (Query.Any(People.PersonSearch.WildcardCharacters.Contains))
        {
            throw new ArgumentException("検索語にワイルドカード（* % ?）は使えません（検索は部分一致です）。", nameof(Query));
        }

        return OrgNameKey.From(Query) is { } nameKey
            ? (nameKey, SearchKey.From(Query))
            : throw new ArgumentException("検索語を指定してください。", nameof(Query));
    }
}
