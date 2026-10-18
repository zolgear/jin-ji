using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using JinJi.Application.Organization;
using JinJi.Application.People;
using JinJi.Host;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.IntegrationTests;

/// <summary>
/// 1 プロセスで API と MCP を別ポートで待ち受け、互いのエンドポイントに到達できないことを確かめる。
/// </summary>
public sealed class HostSurfaceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "all では API と MCP がそれぞれのポートで応答する")]
    public async Task RoleAll_ServesApiAndMcpOnTheirOwnPorts()
    {
        var (apiPort, mcpPort) = (FreePort(), FreePort());
        await using var app = ServerHost.BuildWeb(HostRole.All, ListenArgs(apiPort, mcpPort));
        await app.StartAsync(Ct);
        using var client = new HttpClient();

        Assert.Equal("api", await GetSurfaceAsync(client, apiPort));
        Assert.Equal("mcp", await GetSurfaceAsync(client, mcpPort));

        await app.StopAsync(Ct);
    }

    [Fact(DisplayName = "公開面を持たないパスは 404 になる")]
    public async Task UnknownPath_Returns404()
    {
        var (apiPort, mcpPort) = (FreePort(), FreePort());
        await using var app = ServerHost.BuildWeb(HostRole.All, ListenArgs(apiPort, mcpPort));
        await app.StartAsync(Ct);
        using var client = new HttpClient();

        using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{apiPort}/no-such-path"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await app.StopAsync(Ct);
    }

    [Fact(DisplayName = "api の役割では MCP のポートを開かない")]
    public async Task RoleApi_DoesNotOpenMcpPort()
    {
        var (apiPort, mcpPort) = (FreePort(), FreePort());
        await using var app = ServerHost.BuildWeb(HostRole.Api, ListenArgs(apiPort, mcpPort));
        await app.StartAsync(Ct);
        using var client = new HttpClient();

        Assert.Equal("api", await GetSurfaceAsync(client, apiPort));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://127.0.0.1:{mcpPort}/healthz"), Ct));

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
        var (apiPort, mcpPort) = (FreePort(), FreePort());
        await using (var defaults = ServerHost.BuildWeb(HostRole.Api, ListenArgs(apiPort, mcpPort)))
        {
            Assert.Equal(90, defaults.Services.GetRequiredService<PersonDirectoryOptions>().RetiredPublicDays);
            Assert.Equal(TimeSpan.FromSeconds(5), defaults.Services.GetRequiredService<OrgSnapshotCacheOptions>().VersionCheckInterval);
        }

        await using var custom = ServerHost.BuildWeb(
            HostRole.Api,
            [.. ListenArgs(apiPort, mcpPort), "--JinJi:Directory:RetiredPublicDays=30", "--JinJi:OrgSnapshotCache:VersionCheckInterval=00:00:10"]);
        Assert.Equal(30, custom.Services.GetRequiredService<PersonDirectoryOptions>().RetiredPublicDays);
        Assert.Equal(TimeSpan.FromSeconds(10), custom.Services.GetRequiredService<OrgSnapshotCacheOptions>().VersionCheckInterval);
    }

    [Fact(DisplayName = "設定: 不正な値では起動しない")]
    public void Settings_RejectInvalidValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerHost.BuildWeb(
            HostRole.Api, [.. ListenArgs(FreePort(), FreePort()), "--JinJi:Directory:RetiredPublicDays=-1"]));
    }

    private static string[] ListenArgs(int apiPort, int mcpPort) =>
    [
        "--JinJi:Listen:Address=127.0.0.1",
        $"--JinJi:Listen:ApiPort={apiPort}",
        $"--JinJi:Listen:McpPort={mcpPort}",
        $"--JinJi:Listen:MetricsPort={FreePort()}",
        // テストは証明書なしの Data Protection の鍵で動かす（明示した設定でだけ許す）
        "--JinJi:DataProtection:AllowUnprotectedKeys=true",
        // 公開面の振り分けだけを見るので DB は使わない。Data Protection は起動時に鍵リングを読みに行くため、
        // 開発用の DB（環境変数から組み立てる接続文字列）に鍵を作らないよう、届かない接続先を明示する
        "--ConnectionStrings:App=Host=127.0.0.1;Port=9;Database=jinji_unused;Username=jinji_app;Password=unused;Timeout=1",
    ];

    private static async Task<string?> GetSurfaceAsync(HttpClient client, int port)
    {
        var body = await client.GetStringAsync(new Uri($"http://127.0.0.1:{port}/healthz"), Ct);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("surface").GetString();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
