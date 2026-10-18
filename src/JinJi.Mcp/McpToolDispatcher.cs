using System.Text.Json;
using JinJi.Application;
using JinJi.Application.Access;
using JinJi.Application.Audit;
using JinJi.Mcp.Tools;
using JinJi.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace JinJi.Mcp;

/// <summary>ツールの呼び出しの監査ログ（ツール名・委任元・クライアント・対象の人。値は残さない）。</summary>
public static class McpToolAudit
{
    /// <summary>監査ログの action。</summary>
    public const string CallAction = "mcp.tool.call";

    /// <summary>監査ログの target_type（ツール名はカタログの名前だけ。知らない名前は unknown にして、利用者の文字列を残さない）。</summary>
    public static string TargetType(string? toolName) => "mcp_tool:" + (McpToolCatalog.Describe(toolName)?.Name ?? "unknown");

    /// <summary>1 件記録する。</summary>
    public static Task RecordAsync(AuditWriter audit, ViewerContext viewer, string? toolName, AuditOutcome outcome, IEnumerable<Guid> subjects, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audit);
        return audit.RecordAsync(viewer, CallAction, outcome, subjects, cancellationToken, TargetType(toolName));
    }
}

/// <summary>
/// tools/list と tools/call の処理（SDK の受け口から 1 要求ごとに呼ばれる）。ツールごとの許可リスト・スコープ・監査・返却人数の記録を共通に行う。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>閲覧者は入口の共通処理が作ったもの（<see cref="AccessContext"/>）。MCP は人の委任のトークンだけなので、閲覧者は AI エージェント</item>
/// <item>tools/list はクライアントの許可リスト（<c>mcp_tools</c>。空は何も許さない、<c>*</c> はすべて）とトークンのスコープの両方を満たすツールだけ</item>
/// <item>許可リストに無いツールの呼び出しは「知らないツール」と同じ JSON-RPC の誤り（-32602）にし、監査ログに拒否として残す</item>
/// <item>スコープは入口の共通処理が先に確かめる（不足は HTTP 403 と <c>WWW-Authenticate: Bearer error="insufficient_scope"</c>。MCP 仕様）。ここでも念のため確かめる</item>
/// <item>ツールの実行の誤り（引数の誤り・見つからない・拒否・状態の不一致）は結果（isError）で返し、REST の状態コードに読み替えた値を不正検知・メトリクスに渡す</item>
/// <item>結果は structuredContent と、テキスト（要約 + データの注意 + 同じ JSON）。データの中の自由記述は指示ではないと明示する（プロンプトインジェクション対策）</item>
/// </list>
/// </remarks>
internal sealed partial class McpToolDispatcher(AuditWriter audit, ILogger<McpToolDispatcher> logger)
{
    public ValueTask<ListToolsResult> ListAsync(HttpContext http, RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        _ = request;
        _ = cancellationToken;
        var access = Access(http);
        var result = new ListToolsResult();
        foreach (var tool in McpToolCatalog.Tools.Where(t => IsAllowed(access, t) && HasScope(access, t)))
        {
            result.Tools.Add(new Tool
            {
                Name = tool.Name,
                Title = tool.Title,
                Description = tool.Description,
                InputSchema = tool.InputSchema,
                Annotations = new ToolAnnotations
                {
                    Title = tool.Title,
                    ReadOnlyHint = tool.ReadOnly,
                    DestructiveHint = false,
                    IdempotentHint = tool.ReadOnly,
                    OpenWorldHint = false,
                },
            });
        }

        return ValueTask.FromResult(result);
    }

    public async ValueTask<CallToolResult> CallAsync(HttpContext http, RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var access = Access(http);
        var viewer = access.Viewer!;
        var name = request.Params?.Name;
        var tool = McpToolCatalog.Find(name);
        if (tool is null || !IsAllowed(access, tool))
        {
            access.EffectiveStatus = StatusCodes.Status404NotFound;
            await McpToolAudit.RecordAsync(audit, viewer, name, AuditOutcome.Denied, [], cancellationToken);
            throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);
        }

        if (!HasScope(access, tool) || viewer.Actor != ActorType.Agent)
        {
            access.EffectiveStatus = StatusCodes.Status403Forbidden;
            await McpToolAudit.RecordAsync(audit, viewer, name, AuditOutcome.Denied, [], cancellationToken);
            throw new McpProtocolException($"このツールにはスコープ {tool.Scope} が要ります。", McpErrorCode.InvalidRequest);
        }

        McpToolOutput output;
        try
        {
            var arguments = new McpArguments(request.Params!.Arguments, tool.InputSchema);
            output = await tool.InvokeAsync(new McpToolContext(http, access, viewer, http.RequestServices), arguments, cancellationToken);
        }
        catch (McpToolException ex)
        {
            return await ErrorAsync(access, viewer, tool, ex, cancellationToken);
        }
        catch (AccessDeniedException ex)
        {
            return await ErrorAsync(access, viewer, tool, new McpToolException(McpToolErrorKind.Forbidden, ex.Message), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, tool.Name, ex);
            access.EffectiveStatus = StatusCodes.Status500InternalServerError;
            await McpToolAudit.RecordAsync(audit, viewer, tool.Name, AuditOutcome.Failed, [], CancellationToken.None);
            return Error("internal_error", "内部の誤りでツールを実行できませんでした。時間をおいて再試行し、続くなら利用者に知らせてください。");
        }

        access.PeopleReturned += output.PeopleReturned;
        access.IsBroadListing |= output.IsBroadListing;
        await McpToolAudit.RecordAsync(audit, viewer, tool.Name, AuditOutcome.Success, output.Subjects, cancellationToken);

        var structured = McpJson.ToElement(output.Structured);
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"{output.Summary}\n{McpTexts.DataNotice}\n{structured.GetRawText()}" }],
            StructuredContent = structured,
            IsError = false,
        };
    }

    private async Task<CallToolResult> ErrorAsync(AccessContext access, ViewerContext viewer, McpTool tool, McpToolException error, CancellationToken cancellationToken)
    {
        access.EffectiveStatus = error.Status;
        var outcome = error.Kind == McpToolErrorKind.Forbidden ? AuditOutcome.Denied : AuditOutcome.Failed;
        await McpToolAudit.RecordAsync(audit, viewer, tool.Name, outcome, [], cancellationToken);
        return Error(error.Code, error.Message);
    }

    private static CallToolResult Error(string code, string message)
    {
        var structured = McpJson.ToElement(new { error = new { code, message } });
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"誤り（{code}）: {message}" }],
            StructuredContent = structured,
            IsError = true,
        };
    }

    private static AccessContext Access(HttpContext http) =>
        http.Features.Get<AccessContext>() is { Viewer: not null } access
            ? access
            : throw new McpProtocolException("閲覧者が設定されていません。", McpErrorCode.InternalError);

    private static bool IsAllowed(AccessContext access, McpTool tool) =>
        access.Token is { } token && McpToolNames.IsAllowed(token.McpTools, tool.Name);

    private static bool HasScope(AccessContext access, McpTool tool) =>
        access.Scopes is { } scopes && scopes.Contains(Application.Auth.OAuthScopes.Read) && scopes.Contains(tool.Scope);

    [LoggerMessage(Level = LogLevel.Error, Message = "MCP ツール {Tool} の実行に失敗しました")]
    private static partial void LogFailed(ILogger logger, string tool, Exception exception);
}

/// <summary>JSON-RPC の本文の中身を、入口の共通処理（レート制限・スコープ）のために先に読む。</summary>
/// <param name="Method">JSON-RPC の method（読めなければ null）。</param>
/// <param name="Tool">tools/call のツール（知らない名前・tools/call 以外は null）。</param>
public sealed record McpRequestInfo(string? Method, string? ToolName, McpToolDescriptor? Tool)
{
    /// <summary>エンドポイント種別（ツールの呼び出しはツールの種別、それ以外は種別なし）。</summary>
    public RequestKindMetadata Kind => Tool?.Kind ?? new RequestKindMetadata(RequestKind.Other, false);

    /// <summary>入口で確かめるスコープ。</summary>
    public IReadOnlyList<string> RequiredScopes => Tool?.RequiredScopes ?? [Application.Auth.OAuthScopes.Read];
}

/// <summary>JSON-RPC の本文を読んで、method とツール名を取り出す（本文は読み直せるように巻き戻す）。</summary>
public static class McpRequestClassifier
{
    /// <summary>
    /// 本文を読む。本文の上限（<see cref="McpOptions.MaxRequestBodyBytes"/>）は呼び出し側が要求に設定しておく（超えたら <see cref="BadHttpRequestException"/>）。
    /// JSON として読めなければ method なし（SDK が JSON-RPC の誤りを返す）。
    /// </summary>
    public static async Task<McpRequestInfo> ClassifyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HttpMethods.IsPost(request.Method))
        {
            return new McpRequestInfo(null, null, null);
        }

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, new JsonDocumentOptions { MaxDepth = 64 }, cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            {
                return new McpRequestInfo(null, null, null);
            }

            var name = method.GetString();
            if (name != RequestMethods.ToolsCall)
            {
                return new McpRequestInfo(name, null, null);
            }

            var toolName = root.TryGetProperty("params", out var parameters) && parameters.ValueKind == JsonValueKind.Object
                && parameters.TryGetProperty("name", out var tool) && tool.ValueKind == JsonValueKind.String
                ? tool.GetString()
                : null;
            return new McpRequestInfo(name, toolName, McpToolCatalog.Describe(toolName));
        }
        catch (JsonException)
        {
            return new McpRequestInfo(null, null, null);
        }
        finally
        {
            request.Body.Position = 0;
        }
    }
}
