using System.Net;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Auth;

/// <summary>
/// 寿命と即時の無効化（時刻を手で進めるホスト）: 認可コード 60 秒、アクセストークン 10 分、リフレッシュトークンの無操作 7 日・最長 30 日、
/// 検証のキャッシュ（既定 5 秒）の間に起きた一時停止・クライアントの停止がキャッシュの秒数以内に効くこと。
/// </summary>
public sealed class OAuthLifetimeTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>, IAsyncLifetime
{
    private const string Loopback = "http://127.0.0.1:43123/callback";
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UtcNow);
    private OAuthTestEnvironment _env = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _env = await OAuthTestEnvironment.StartAsync(db, timeProvider: _time);

    public async ValueTask DisposeAsync() => await _env.DisposeAsync();

    [Fact(DisplayName = "認可コード: 60 秒で失効する（59 秒なら交換できる）")]
    public async Task Code_ExpiresAfter60Seconds()
    {
        await _env.RegisterPublicClientAsync("code-ttl", Loopback);
        var browser = await _env.LoginAsync(F.Person("u2_member"));

        var early = Pkce.New();
        var earlyCode = await _env.CodeAsync(browser, "code-ttl", Loopback, early);
        _time.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(HttpStatusCode.OK, (await _env.ExchangeAsync("code-ttl", earlyCode, Loopback, early)).Status);

        var late = Pkce.New();
        var lateCode = await _env.CodeAsync(browser, "code-ttl", Loopback, late);
        _time.Advance(TimeSpan.FromSeconds(61));
        var expired = await _env.ExchangeAsync("code-ttl", lateCode, Loopback, late);
        Assert.Equal("invalid_grant", expired.Error);
    }

    [Fact(DisplayName = "アクセストークンは 10 分、リフレッシュトークンは無操作 7 日・系列の最長 30 日で使えなくなる")]
    public async Task Tokens_Expire()
    {
        await _env.RegisterPublicClientAsync("token-ttl", Loopback);
        var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        var tokens = await _env.ExchangeAsync("token-ttl", await _env.CodeAsync(browser, "token-ttl", Loopback, pkce), Loopback, pkce);

        _time.Advance(TimeSpan.FromMinutes(9));
        using (var alive = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, alive.StatusCode);
        }

        _time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        using (var expired = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
        }

        // 6 日ごとに回転すれば続くが、系列は付与から 30 日で終わる
        var refresh = tokens.RefreshToken!;
        for (var day = 6; day < 30; day += 6)
        {
            _time.Advance(TimeSpan.FromDays(6));
            var rotated = await _env.RefreshAsync("token-ttl", refresh);
            Assert.True(rotated.Status == HttpStatusCode.OK, $"{day} 日目: {rotated.Error}");
            refresh = rotated.RefreshToken!;
        }

        _time.Advance(TimeSpan.FromDays(6));
        Assert.Equal("invalid_grant", (await _env.RefreshAsync("token-ttl", refresh)).Error);

        // 無操作 7 日
        var browser2 = await _env.LoginAsync(F.Person("u2_member"));
        var pkce2 = Pkce.New();
        var idle = await _env.ExchangeAsync("token-ttl", await _env.CodeAsync(browser2, "token-ttl", Loopback, pkce2), Loopback, pkce2);
        _time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromSeconds(1));
        Assert.Equal("invalid_grant", (await _env.RefreshAsync("token-ttl", idle.RefreshToken!)).Error);
    }

    [Fact(DisplayName = "一時停止・クライアントの停止（他のプロセスの操作）は、検証のキャッシュの秒数（既定 5 秒）以内に効く。一時停止中の人のリフレッシュも拒否")]
    public async Task Suspension_TakesEffectWithinCacheDuration()
    {
        await _env.RegisterPublicClientAsync("cache-app", Loopback);
        var person = await db.InsertPersonAsync("E8801", "cache@example.test", Ct);
        var browser = await _env.LoginAsync(person);
        var pkce = Pkce.New();
        var tokens = await _env.ExchangeAsync("cache-app", await _env.CodeAsync(browser, "cache-app", Loopback, pkce), Loopback, pkce);
        using (var ping = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        await db.SuspendAsync(person, Ct);
        using (var cached = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        }

        _time.Advance(TimeSpan.FromSeconds(6));
        using (var suspended = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, suspended.StatusCode);
        }

        Assert.Equal("invalid_grant", (await _env.RefreshAsync("cache-app", tokens.RefreshToken!)).Error);

        // クライアントの停止（管理 CLI = 別のプロセス）
        var other = await _env.LoginAsync(F.Person("u2_member"));
        var pkce2 = Pkce.New();
        var second = await _env.ExchangeAsync("cache-app", await _env.CodeAsync(other, "cache-app", Loopback, pkce2), Loopback, pkce2);
        using (var ping = await _env.McpPingAsync(second.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        Assert.Equal(0, (await _env.CliAsync("suspend-client", "--client-id", "cache-app")).Exit);
        _time.Advance(TimeSpan.FromSeconds(6));
        using (var stopped = await _env.McpPingAsync(second.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, stopped.StatusCode);
        }
    }

    [Fact(DisplayName = "同意の後に一時停止になった人の認可コードはトークンにできない（交換の時点で人の状態を確かめる）")]
    public async Task SuspendedPerson_CannotExchange()
    {
        await _env.RegisterPublicClientAsync("retire-app", Loopback);
        var person = await db.InsertPersonAsync("E8802", "retire@example.test", Ct);
        var browser = await _env.LoginAsync(person);
        var pkce = Pkce.New();
        var code = await _env.CodeAsync(browser, "retire-app", Loopback, pkce);

        await db.SuspendAsync(person, Ct);

        Assert.Equal("invalid_grant", (await _env.ExchangeAsync("retire-app", code, Loopback, pkce)).Error);
    }
}
