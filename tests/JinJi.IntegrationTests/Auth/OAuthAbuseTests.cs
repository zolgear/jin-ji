using System.Net;
using JinJi.Application.Access;
using JinJi.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.IntegrationTests.Auth;

/// <summary>
/// トークンエンドポイントのレート制限（接続元 × client_id）と、失敗の急増の不正検知（接続元ごと。自動スロットル → 一時停止 → 人事管理者の解除）。
/// </summary>
public sealed class OAuthAbuseTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>, IAsyncLifetime
{
    private OAuthTestEnvironment _env = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _env = await OAuthTestEnvironment.StartAsync(
        db,
        [
            "--JinJi:Auth:Server:RateLimit:PerAddressAndClient=3",
            "--JinJi:Auth:Server:RateLimit:ThrottledPerAddress=1000",
            "--JinJi:Abuse:TokenFailureThreshold=4",
        ]);

    public async ValueTask DisposeAsync() => await _env.DisposeAsync();

    [Fact(DisplayName = "トークンエンドポイント: 接続元 × client_id で制限し（429 と Retry-After）、失敗の急増で接続元を自動スロットル → 一時停止にする。解除は人事管理者")]
    public async Task TokenEndpoint_RateLimitAndFailureBurst()
    {
        // 接続元 × client_id: 3 回まで（失敗の数には入らない誤り unsupported_grant_type で数える）
        await _env.RegisterPublicClientAsync("rate-a", "http://127.0.0.1/cb");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("unsupported_grant_type", (await _env.TokenAsync(new() { ["grant_type"] = "x", ["client_id"] = "rate-a" })).Error);
        }

        var limited = await _env.TokenAsync(new() { ["grant_type"] = "x", ["client_id"] = "rate-a" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.Status);
        Assert.Equal("slow_down", limited.Error);
        Assert.NotNull(limited.Raw.Headers.RetryAfter);

        // 失敗（invalid_client）の急増: 4 回で自動スロットル、さらに 4 回で一時停止
        var detector = _env.Host.App.Services.GetRequiredService<AbuseDetector>();
        var key = TokenEndpointAbuseKeys.For("127.0.0.1");
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal("invalid_client", (await _env.TokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = $"burst-{i}" })).Error);
        }

        Assert.Equal(AbuseState.Throttled, detector.StateOf(key));
        for (var i = 0; i < 4; i++)
        {
            _ = await _env.TokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = $"burst2-{i}" });
        }

        Assert.Equal(AbuseState.Suspended, detector.StateOf(key));
        var blocked = await _env.TokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = "anyone" });
        Assert.Equal(HttpStatusCode.Forbidden, blocked.Status);
        Assert.Equal("access_denied", blocked.Error);
        Assert.True(await db.CountAuditAsync("abuse.suspended", "denied", Ct) >= 1);

        // 人事管理者が解除する（既存の不正検知の解除の API）
        var admin = await _env.LoginAsync(F.Person("hr_head"));
        using (var release = await admin.PostAsync($"/api/v1/admin/clients/{Uri.EscapeDataString(key)}/release"))
        {
            Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        }

        Assert.Equal(AbuseState.Normal, detector.StateOf(key));
        Assert.Equal("invalid_client", (await _env.TokenAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = "after-release" })).Error);
    }
}
