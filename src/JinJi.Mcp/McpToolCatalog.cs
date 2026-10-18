using JinJi.Application.Access;
using JinJi.Application.Auth;
using JinJi.Mcp.Tools;

namespace JinJi.Mcp;

/// <summary>ツールの宣言（入口の共通処理がレート制限の種別・スコープを決めるのに使う）。</summary>
/// <param name="Scope">要るスコープ（jinji.read / jinji.propose）。</param>
/// <param name="Kind">エンドポイント種別（REST API の同じ操作と同じ）。</param>
public sealed record McpToolDescriptor(string Name, string Scope, RequestKindMetadata Kind)
{
    /// <summary>入口で確かめるスコープ（MCP は常に jinji.read、提案のツールはさらに jinji.propose）。</summary>
    public IReadOnlyList<string> RequiredScopes => Scope == OAuthScopes.Read ? [OAuthScopes.Read] : [OAuthScopes.Read, Scope];
}

/// <summary>jin-ji の MCP ツールの一覧（<see cref="McpToolNames"/> と同じ集合。テストで突き合わせる）。</summary>
public static class McpToolCatalog
{
    internal static readonly IReadOnlyList<McpTool> Tools =
    [
        new SearchPeopleTool(),
        new GetPersonTool(),
        new GetPeopleTool(),
        new GetApprovalRouteTool(),
        new GetOrgTool(),
        new GetOrgTreeTool(),
        new SearchOrgsTool(),
        new ListSitesTool(),
        new ListFloorsTool(),
        new FindVacantSeatsTool(),
        new ListFloorOccupantsTool(),
        new GetChangesTool(),
        new GetDirectoryInfoTool(),
        new ListReviewProposalsTool(),
        new SuggestReviewResolutionTool(),
        new ListCorrectionsTool(),
        new ReportCorrectionTool(),
    ];

    private static readonly Dictionary<string, McpTool> ByName = Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);

    /// <summary>すべてのツールの宣言。</summary>
    public static IReadOnlyList<McpToolDescriptor> Descriptors { get; } = [.. Tools.Select(t => new McpToolDescriptor(t.Name, t.Scope, t.Kind))];

    /// <summary>名前からツールの宣言を引く。知らない名前なら null。</summary>
    public static McpToolDescriptor? Describe(string? name) =>
        name is not null && ByName.TryGetValue(name, out var tool) ? new McpToolDescriptor(tool.Name, tool.Scope, tool.Kind) : null;

    internal static McpTool? Find(string? name) => name is not null && ByName.TryGetValue(name, out var tool) ? tool : null;
}
