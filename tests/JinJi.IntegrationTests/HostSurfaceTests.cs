using System.Net;
using System.Text.Json;
using JinJi.Application.Organization;
using JinJi.Application.People;
using JinJi.Host;
using JinJi.IntegrationTests.Support;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.IntegrationTests;

/// <summary>
/// 1 プロセスで API と MCP を別ポートで待ち受け、互いのエンドポイントに到達できないことを確かめる。
/// </summary>
public sealed class HostSurfaceTests
{
    /// <summary>待ち受けの設定。ポートは 0 にして OS に割り当てさせ、待ち受けを始めた後に実際のポートを読む。</summary>
    private static readonly string[] ListenArgs =
    [
        "--JinJi:Listen:Address=127.0.0.1",
        "--JinJi:Listen:ApiPort=0",
        "--JinJi:Listen:McpPort=0",
        "--JinJi:Listen:MetricsPort=0",
        // テストは証明書なしの Data Protection の鍵で動かす（明示した設定でだけ許す）
        "--JinJi:DataProtection:AllowUnprotectedKeys=true",
        // 公開面の振り分けだけを見るので DB は使わない。Data Protection は起動時に鍵リングを読みに行くため、
        // 開発用の DB（環境変数から組み立てる接続文字列）に鍵を作らないよう、届かない接続先を明示する
        "--ConnectionStrings:App=Host=127.0.0.1;Port=9;Database=jinji_unused;Username=jinji_app;Password=unused;Timeout=1",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "all では API と MCP がそれぞれのポートで応答する")]
    public async Task RoleAll_ServesApiAndMcpOnTheirOwnPorts()
    {
        await using var app = ServerHost.BuildWeb(HostRole.All, ListenArgs);
        await app.StartAsync(Ct);
        var ports = app.Services.GetRequiredService<SurfacePortMap>();
        using var client = new HttpClient();

        Assert.Equal("api", await GetSurfaceAsync(client, ApiTestHost.BoundPort(ports, Surface.Api)));
        Assert.Equal("mcp", await GetSurfaceAsync(client, ApiTestHost.BoundPort(ports, Surface.Mcp)));

        await app.StopAsync(Ct);
    }

    [Fact(DisplayName = "公開面を持たないパスは 404 になる")]
    public async Task UnknownPath_Returns404()
    {
        await using var app = ServerHost.BuildWeb(HostRole.All, ListenArgs);
        await app.StartAsync(Ct);
        var apiPort = ApiTestHost.BoundPort(app.Services.GetRequiredService<SurfacePortMap>(), Surface.Api);
        using var client = new HttpClient();

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{apiPort}/no-such-path"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await app.StopAsync(Ct);
    }

    [Fact(DisplayName = "api の役割では MCP のポートを開かない")]
    public async Task RoleApi_DoesNotOpenMcpPort()
    {
        await using var app = ServerHost.BuildWeb(HostRole.Api, ListenArgs);
        await app.StartAsync(Ct);
        var ports = app.Services.GetRequiredService<SurfacePortMap>();
        using var client = new HttpClient();

        Assert.Equal("api", await GetSurfaceAsync(client, ApiTestHost.BoundPort(ports, Surface.Api)));
        // 待ち受けは API とメトリクスの 2 つだけ（MCP の待ち受けを作らない）
        Assert.Null(ports.PortOf(Surface.Mcp));
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        Assert.Equal(2, addresses.Count);

        await app.StopAsync(Ct);
    }

    [Theory(DisplayName = "役割を解釈できる")]
    [InlineData(null, "All")]
    [InlineData("all", "All")]
    [InlineData("API", "Api")]
    [InlineData("mcp", "Mcp")]
    [InlineData("worker", "Worker")]
    public void ParsesRole(string? value, string expected)
    {
        Assert.Equal(Enum.Parse<HostRole>(expected), HostRoles.Parse(value));
    }

    [Fact(DisplayName = "不明な役割は拒否する")]
    public void RejectsUnknownRole()
    {
        Assert.Throws<ArgumentException>(() => HostRoles.Parse("admin"));
    }

    [Fact(DisplayName = "設定: 退職者の公開日数とキャッシュの設定は既定値を持ち、設定で変えられる")]
    public async Task Settings_HaveDefaultsAndCanBeOverridden()
    {
        await using (var defaults = ServerHost.BuildWeb(HostRole.Api, ListenArgs))
        {
            Assert.Equal(90, defaults.Services.GetRequiredService<PersonDirectoryOptions>().RetiredPublicDays);
            Assert.Equal(TimeSpan.FromSeconds(5), defaults.Services.GetRequiredService<OrgSnapshotCacheOptions>().VersionCheckInterval);
        }

        await using var custom = ServerHost.BuildWeb(
            HostRole.Api,
            [.. ListenArgs, "--JinJi:Directory:RetiredPublicDays=30", "--JinJi:OrgSnapshotCache:VersionCheckInterval=00:00:10"]);
        Assert.Equal(30, custom.Services.GetRequiredService<PersonDirectoryOptions>().RetiredPublicDays);
        Assert.Equal(TimeSpan.FromSeconds(10), custom.Services.GetRequiredService<OrgSnapshotCacheOptions>().VersionCheckInterval);
    }

    [Fact(DisplayName = "設定: 不正な値では起動しない")]
    public void Settings_RejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerHost.BuildWeb(
            HostRole.Api, [.. ListenArgs, "--JinJi:Directory:RetiredPublicDays=-1"]));
    }

    private static async Task<string?> GetSurfaceAsync(HttpClient client, int port)
    {
        var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{port}/healthz"), Ct);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("surface").GetString();
    }
}
