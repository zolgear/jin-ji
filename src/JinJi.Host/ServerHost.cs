using System.Net;
using JinJi.Api;
using JinJi.Host.Auth;
using JinJi.Host.Web;
using JinJi.Mcp;
using JinJi.Worker;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JinJi.Host;

/// <summary>役割に応じてサーバー（Web / ワーカーのみ）を組み立てる。</summary>
internal static class ServerHost
{
    public const int DefaultApiPort = 28080;
    public const int DefaultMcpPort = 28090;
    public const int DefaultMetricsPort = 28100;

    /// <summary>API・MCP の少なくとも一方を含む役割のホストを組み立てる。</summary>
    /// <param name="configureServices">登録の後にサービスを差し替える（テストの時刻など、配備側の当日の所在の提供。<see cref="JinJiHost"/>）。</param>
    public static WebApplication BuildWeb(HostRole role, string[] args, Action<IServiceCollection>? configureServices = null)
    {
        if ((role & (HostRole.Api | HostRole.Mcp)) == HostRole.None)
        {
            throw new ArgumentException("API か MCP を含む役割を指定してください。", nameof(role));
        }

        var builder = WebApplication.CreateBuilder(args);
        var listen = builder.Configuration.GetSection("JinJi:Listen");
        var address = IPAddress.Parse(listen["Address"] ?? "127.0.0.1");
        // メトリクスは既定でループバックだけ（API・MCP を 0.0.0.0 で公開しても、メトリクスは明示しない限り外に出さない）
        var metricsAddress = IPAddress.Parse(listen["MetricsAddress"] ?? "127.0.0.1");

        if (role.HasFlag(HostRole.Api) && DatabaseRegistration.AppConnectionString(builder.Configuration) is null)
        {
            // API は DB が無いと何も返せない。起動してから最初の要求で失敗させず、起動時に止める
            DatabaseRegistration.RequireAppConnectionString(builder.Configuration);
        }

        var ports = new Dictionary<int, Surface>();
        if (role.HasFlag(HostRole.Api))
        {
            ports.Add(listen.GetValue("ApiPort", DefaultApiPort), Surface.Api);
        }

        if (role.HasFlag(HostRole.Mcp))
        {
            var mcpPort = listen.GetValue("McpPort", DefaultMcpPort);
            if (!ports.TryAdd(mcpPort, Surface.Mcp))
            {
                throw new ArgumentException($"API と MCP に同じポート {mcpPort} は指定できません。", nameof(args));
            }
        }

        var metricsPort = listen.GetValue("MetricsPort", DefaultMetricsPort);
        if (!ports.TryAdd(metricsPort, Surface.Metrics))
        {
            throw new ArgumentException($"メトリクスに API・MCP と同じポート {metricsPort} は指定できません。", nameof(args));
        }

        var portMap = new SurfacePortMap(ports);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            foreach (var (port, surface) in portMap.Ports)
            {
                kestrel.Listen(surface == Surface.Metrics ? metricsAddress : address, port);
            }
        });

        builder.Services.AddSingleton(portMap);
        builder.Services.AddJinJiSettings(builder.Configuration);
        builder.Services.AddJinJiDatabase(builder.Configuration);
        builder.Services.AddJinJiWebAccess(builder.Configuration);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<MatcherPolicy, SurfaceMatcherPolicy>());

        if (role.HasFlag(HostRole.Api))
        {
            builder.Services.AddJinJiApi(ApplicationSettings.Api(builder.Configuration));
            // 人のログイン（UI の Cookie のセッション・外部 IdP・ローカルアカウント）は API の役割だけ。MCP は Bearer だけを受け付ける
            builder.Services.AddJinJiHumanLogin(builder.Configuration);
            // トークン発行（認可サーバー。ADR-0004 の段階 B）。JinJi:Auth:Server:Issuer を設定したときだけ
            builder.Services.AddJinJiAuthorizationServer(OAuthRegistration.Server(builder.Configuration), AuthRegistration.Login(builder.Configuration));
        }

        if (role.HasFlag(HostRole.Mcp))
        {
            // 停止スイッチ（JinJi:Mcp:Enabled）は設定の再読み込みでも効く。カーソルの署名鍵は REST API と同じ（JinJi:Api:CursorKey）
            builder.Services.AddJinJiMcp(builder.Configuration.GetSection(McpOptions.SectionName), ApplicationSettings.Api(builder.Configuration).CursorKeyBytes());
        }

        if (role.HasFlag(HostRole.Worker))
        {
            builder.Services.AddJinJiWorker(
                ApplicationSettings.Llm(builder.Configuration),
                ApplicationSettings.Retention(builder.Configuration),
                ApplicationSettings.Corrections(builder.Configuration),
                ApplicationSettings.Webhooks(builder.Configuration));
            // Webhook の署名の鍵の復号（人のログインの役割では登録済み）
            builder.Services.AddJinJiDataProtectionIfConfigured(builder.Configuration);
        }

        configureServices?.Invoke(builder.Services);

        // Web UI（SPA）は API の役割でだけ配信する。成果物が無ければ API だけで動く
        var (webUi, spaRoot) = WebUi.Resolve(builder.Configuration, builder.Environment.ContentRootPath);

        var app = builder.Build();
        app.UseJinJiSecurityHeaders(portMap, webUi);
        app.UseJinJiWebAccess(role.HasFlag(HostRole.Api) ? pipeline => pipeline.UseJinJiSpa(portMap, spaRoot, app.Logger) : null);

        var server = app.Services.GetRequiredService<Application.Auth.AuthorizationServerOptions>();
        if (role.HasFlag(HostRole.Api))
        {
            app.MapGroup(string.Empty).WithMetadata(new SurfaceMetadata(Surface.Api)).MapJinJiApi();
            app.MapGroup(string.Empty).WithMetadata(new SurfaceMetadata(Surface.Api)).MapJinJiAuth();
            if (server.Enabled)
            {
                app.MapGroup(string.Empty).WithMetadata(new SurfaceMetadata(Surface.Api)).MapJinJiOAuth(server).MapJinJiOAuthAdmin();
            }
        }

        if (role.HasFlag(HostRole.Mcp))
        {
            app.MapGroup(string.Empty).WithMetadata(new SurfaceMetadata(Surface.Mcp)).MapJinJiMcp()
                .MapJinJiProtectedResourceMetadata(server, Application.Auth.OAuthResource.Mcp);
        }

        app.MapGroup(string.Empty).WithMetadata(new SurfaceMetadata(Surface.Metrics)).AllowAnonymous().MapJinJiMetrics();
        return app;
    }

    /// <summary>ワーカーだけの役割。HTTP は待ち受けない。</summary>
    public static IHost BuildWorkerOnly(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Services.AddJinJiSettings(builder.Configuration);
        builder.Services.AddJinJiDatabase(builder.Configuration);
        builder.Services.AddJinJiWorker(
            ApplicationSettings.Llm(builder.Configuration),
            ApplicationSettings.Retention(builder.Configuration),
            ApplicationSettings.Corrections(builder.Configuration),
            ApplicationSettings.Webhooks(builder.Configuration));
        // Webhook の署名の鍵の復号（API の役割と同じ Data Protection の設定。無ければ Webhook の送信だけが止まる）と、
        // 変更フィードのカーソル（Webhook の本文の changes_since。API と同じ JinJi:Api:CursorKey を設定したときだけ）
        builder.Services.AddJinJiDataProtectionIfConfigured(builder.Configuration);
        if (ApplicationSettings.Api(builder.Configuration).CursorKeyBytes() is { } cursorKey)
        {
            builder.Services.TryAddSingleton(new Application.Access.CursorProtector(cursorKey));
        }

        return builder.Build();
    }
}
