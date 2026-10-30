namespace JinJi.Application.Access;

/// <summary>
/// MCP ツールの名前（クライアントの許可リスト <c>api_client.mcp_tools</c> に書く名前）。ツールの実装は JinJi.Mcp。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>許可リストが空なら何も許さない。<see cref="Wildcard"/>（<c>*</c>）はすべてのツール（項目の上限の <c>*</c> と同じ書き方）。
/// どちらでも、ツールごとのスコープ（参照は jinji.read、提案は jinji.propose）は別に要る</item>
/// <item>名前は管理 CLI・管理 API で登録するときに検査する（書き間違いで黙って何も許さない状態を作らない）</item>
/// </list>
/// </remarks>
public static class McpToolNames
{
    /// <summary>すべてのツール。</summary>
    public const string Wildcard = "*";

    public const string SearchPeople = "search_people";
    public const string GetPerson = "get_person";
    public const string GetPeople = "get_people";
    public const string GetApprovalRoute = "get_approval_route";
    public const string GetOrg = "get_org";
    public const string GetOrgTree = "get_org_tree";
    public const string SearchOrgs = "search_orgs";
    public const string ListSites = "list_sites";
    public const string ListFloors = "list_floors";
    public const string FindVacantSeats = "find_vacant_seats";
    public const string ListFloorOccupants = "list_floor_occupants";
    public const string GetChanges = "get_changes";
    public const string GetDirectoryInfo = "get_directory_info";
    public const string ListReviewProposals = "list_review_proposals";
    public const string SuggestReviewResolution = "suggest_review_resolution";
    public const string ListCorrections = "list_corrections";
    public const string ReportCorrection = "report_correction";
    public const string ListFieldRegistrations = "list_field_registrations";
    public const string ListGroups = "list_groups";
    public const string GetGroup = "get_group";
    public const string DraftFieldRegistration = "draft_field_registration";

    /// <summary>参照のツール（スコープ jinji.read）。</summary>
    public static readonly IReadOnlySet<string> Read = new HashSet<string>(StringComparer.Ordinal)
    {
        SearchPeople, GetPerson, GetPeople, GetApprovalRoute, GetOrg, GetOrgTree, SearchOrgs, ListSites, GetChanges, GetDirectoryInfo, ListReviewProposals, ListCorrections,
        ListFloors, FindVacantSeats, ListFloorOccupants, ListFieldRegistrations, ListGroups, GetGroup,
    };

    /// <summary>
    /// 提案のツール（スコープ jinji.propose。公開されない作業領域にだけ書く）。修正依頼の起票は人事パッケージで直せる人への情報提供で、
    /// jin-ji の公開データは変えない。直接登録は委任元の名義の下書きまでで、反映は委任元の人の確定だけ（ADR-0013 決定 4）。
    /// 公開側の値を直接変える提案ツールは持たない（§7.3）。
    /// </summary>
    public static readonly IReadOnlySet<string> Propose = new HashSet<string>(StringComparer.Ordinal) { SuggestReviewResolution, ReportCorrection, DraftFieldRegistration };

    /// <summary>すべてのツール。</summary>
    public static readonly IReadOnlySet<string> All = Read.Union(Propose).ToHashSet(StringComparer.Ordinal);

    /// <summary>許可リストがそのツールを許すか（空は何も許さない、<c>*</c> はすべて）。</summary>
    public static bool IsAllowed(IReadOnlyCollection<string> allowList, string tool)
    {
        ArgumentNullException.ThrowIfNull(allowList);
        ArgumentNullException.ThrowIfNull(tool);
        return All.Contains(tool) && (allowList.Contains(Wildcard) || allowList.Contains(tool));
    }

    /// <summary>許可リストに書ける名前か（ツールの名前か <c>*</c>）。</summary>
    public static bool IsKnown(string name) => name == Wildcard || All.Contains(name);
}
