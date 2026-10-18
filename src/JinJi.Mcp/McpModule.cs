using System.Security.Cryptography;
using JinJi.Application.Access;
using JinJi.Mcp.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace JinJi.Mcp;

/// <summary>
/// MCP サーバーの登録口。Host（--role mcp / all）から呼ばれ、API とは別のポートで待ち受ける。
/// MCP だけをネットワーク上で公開・遮断できるようにするため（アーキテクチャ概要 §2）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>トランスポート（Streamable HTTP・JSON-RPC・プロトコルの版の取り決め）は公式 C# SDK（<c>ModelContextProtocol.AspNetCore</c>。ADR-0006）。
/// セッションを持たない（stateless）。2026-07-28 版の仕様はセッションを持たず、jin-ji は 1 要求ごとに閲覧者を作るため</item>
/// <item>認証は SDK の認証ハンドラを使わず、既存の参照トークンの認証スキーム（<c>jinji.token</c>）と 401 の challenge を使う。
/// 閲覧者・一時停止・スコープ・返却人数・レート制限は入口の共通処理（Host）が REST API と同じ仕組みで行う</item>
/// <item>ツールは SDK のツールの属性ではなく、<see cref="McpToolCatalog"/> のツールを tools/list・tools/call の処理（<see cref="McpToolDispatcher"/>）で扱う</item>
/// </list>
/// </remarks>
public static partial class McpModule
{
    /// <summary>MCP の受け口のパス（設定 JinJi:Auth:Server:McpResource の URI のパスと合わせる）。</summary>
    public const string EndpointPath = "/mcp";

    /// <summary>MCP サーバーが使うサービスを登録する（DB を使うサービスは Host が登録する）。</summary>
    /// <param name="configuration">設定の節 <c>JinJi:Mcp</c>（<see cref="McpOptions"/>。再読み込みで停止スイッチが効く）。</param>
    /// <param name="cursorKey">カーソルの署名鍵（REST API と同じ <c>JinJi:Api:CursorKey</c>。null なら起動ごとに乱数）。</param>
    public static IServiceCollection AddJinJiMcp(this IServiceCollection services, IConfiguration configuration, byte[]? cursorKey = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<McpOptions>()
            .Bind(configuration)
            .Validate(o =>
            {
                o.Validate();
                return true;
            })
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp =>
        {
            if (cursorKey is not null)
            {
                return new CursorProtector(cursorKey);
            }

            Log.EphemeralCursorKey(sp.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.Mcp"));
            return new CursorProtector(RandomNumberGenerator.GetBytes(32));
        });
        services.TryAddSingleton<McpToolDispatcher>();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "jin-ji", Title = "jin-ji（人事情報基盤）", Version = "1.0.0" };
                options.ServerInstructions = McpTexts.ServerInstructions;
            })
            .WithHttpTransport(transport =>
            {
                transport.SessionMode = HttpServerSessionMode.Stateless;
                // stateless では要求ごとに呼ばれる。要求の HttpContext（入口の共通処理が作った閲覧者）を tools/list・tools/call の処理に渡す
                transport.ConfigureSessionOptions = (http, options, _) =>
                {
                    var dispatcher = http.RequestServices.GetRequiredService<McpToolDispatcher>();
                    options.Handlers.ListToolsHandler = (request, cancellationToken) => dispatcher.ListAsync(http, request, cancellationToken);
                    options.Handlers.CallToolHandler = (request, cancellationToken) => dispatcher.CallAsync(http, request, cancellationToken);
                    options.Capabilities ??= new ServerCapabilities();
                    options.Capabilities.Tools ??= new ToolsCapability();
                    return Task.CompletedTask;
                };
            });
        return services;
    }

    /// <summary>MCP サーバーのエンドポイントを登録する。</summary>
    public static IEndpointRouteBuilder MapJinJiMcp(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // REST API の文書（OpenAPI）には載せない（MCP のポートのエンドポイント）
        endpoints.MapGet("/healthz", () => Results.Ok(new McpHealthResponse("ok", "mcp")))
            .ExcludeFromDescription();

        // MCP の受け口（Streamable HTTP の POST。stateless なので GET・DELETE は持たない）。種別はツールごとに入口の共通処理が決める
        endpoints.MapMcp(EndpointPath)
            .RequireAuthorization()
            .ExcludeFromDescription()
            .WithMetadata(new RequestKindMetadata(RequestKind.Other, false));
        return endpoints;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "JinJi:Api:CursorKey が未設定のため、MCP のカーソルの署名鍵を起動ごとに作ります。再起動・別のプロセスでは以前のカーソルが使えません。")]
        public static partial void EphemeralCursorKey(ILogger logger);
    }
}

/// <summary>ヘルスチェックの応答。</summary>
public sealed record McpHealthResponse(string Status, string Surface);
