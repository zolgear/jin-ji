using System.Net;
using System.Text.Json;
using JinJi.Domain;
using JinJi.Host.Auth;
using JinJi.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.IntegrationTests.Auth;

/// <summary>
/// ローカルアカウント（パスワード・TOTP・リカバリコード・パスキー・ロックアウト・レート制限・リンク）と緊急用の管理者アカウント（ADR-0004 の段階 A）。
/// </summary>
public sealed class LocalLoginTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>, IAsyncLifetime
{
    private const string Password = "correct-Horse-battery-9";
    private readonly TestIssuer _issuer = new();
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _host = await AuthTestHost.StartAsync(db, _issuer);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _issuer.Dispose();
    }

    [Fact(DisplayName = "リンク: 管理者が発行したリンクでパスワードを設定してログインできる。リンクは 1 回だけ使え、要件を満たさないパスワードではリンクを消費しない")]
    public async Task ResetLink_SetsPasswordOnce()
    {
        var (_, token) = await CreateAccountAsync("alice", "E7101");
        using var browser = await BrowserAsync();

        using (var weak = await browser.PostAsync("/auth/reset/password", new { token, new_password = "short" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);
        using (var reuse = await browser.PostAsync("/auth/reset/password", new { token, new_password = Password + "x" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
            Assert.Equal("urn:jinji:problem:invalid-link", (await TestBrowser.JsonAsync(reuse)).GetProperty("type").GetString());
        }

        var login = await LoginAsync(browser, "alice", Password);
        Assert.Equal("signed_in", login.GetProperty("status").GetString());
        Assert.Equal(["pwd"], login.GetProperty("session").GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal("local", login.GetProperty("session").GetProperty("provider").GetString());
        Assert.False(string.IsNullOrEmpty(login.GetProperty("csrf_token").GetString()));
        Assert.True(await db.CountAuditAsync("auth.reset_link.use", "success", Ct) >= 1);
        Assert.True(await db.CountAuditAsync("auth.reset_link.issue", "success", Ct) >= 1);
        Assert.True(await db.CountAuditAsync("auth.account.create", "success", Ct) >= 1);

        // ログインの応答のトークンで状態変更ができる（閲覧者に束縛し直したトークン）
        browser.CsrfToken = login.GetProperty("csrf_token").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/logout")).StatusCode);
    }

    [Fact(DisplayName = "リンク: 期限切れ・取り消し済み（新しいリンクの発行）は使えない")]
    public async Task ResetLink_ExpiredOrSuperseded()
    {
        var (account, first) = await CreateAccountAsync("bella", "E7102");
        var (exit, output) = await db.CliAsync("issue-reset-link", "--user-name", "bella");
        Assert.Equal(0, exit);
        var second = AuthFixtureDatabase.LinkToken(output);
        using var browser = await BrowserAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/auth/reset/password", new { token = first, new_password = Password })).StatusCode);

        // 期限切れのリンク（DB に直接入れる）
        var expired = Application.Auth.AuthSecrets.NewToken();
        await using (var app = await db.Database.OpenAppAsync(Ct))
        {
            await SampleData.SetSessionViewerAsync(app, "system", string.Empty, null, Ct);
            await SampleData.ExecuteAsync(
                app,
                """
                INSERT INTO account_reset_link (id, local_account_id, purpose, token_sha256, issued_by_kind, issued_at, expires_at)
                VALUES ($1, $2, 'password', $3, 'cli', now() - interval '2 hours', now() - interval '1 hour')
                """,
                Ct,
                EntityId.New(),
                account,
                Application.Auth.AuthSecrets.Sha256(expired));
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/auth/reset/password", new { token = expired, new_password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token = second, new_password = Password })).StatusCode);
        Assert.True(await db.CountAuditAsync("auth.reset_link.use", "denied", Ct) >= 2);
    }

    [Fact(DisplayName = "ロックアウト: 5 回の失敗で 15 分ロックし、正しいパスワードも通らない。管理 CLI の解除で戻る。失敗の応答は理由を区別しない")]
    public async Task Lockout_AfterFiveFailures()
    {
        var (account, token) = await CreateAccountAsync("carol", "E7103");
        using var browser = await BrowserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRawAsync(browser, "carol", "wrong-password-" + i)).StatusCode);
        }

        using (var locked = await LoginRawAsync(browser, "carol", Password))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
            Assert.Equal("urn:jinji:problem:login-failed", (await TestBrowser.JsonAsync(locked)).GetProperty("type").GetString());
        }

        var lockoutEnd = (await db.QueryAsSystemAsync<DateTime>("SELECT lockout_end FROM local_account WHERE id = $1", Ct, account)).Single();
        Assert.InRange(lockoutEnd, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));

        // 知らないユーザー名も同じ応答
        using (var unknown = await LoginRawAsync(browser, "nobody", Password))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
            Assert.Equal("urn:jinji:problem:login-failed", (await TestBrowser.JsonAsync(unknown)).GetProperty("type").GetString());
        }

        Assert.Equal(0, (await db.CliAsync("unlock-account", "--user-name", "carol")).Exit);
        Assert.Equal("signed_in", (await LoginAsync(browser, "carol", Password)).GetProperty("status").GetString());
        Assert.True(await db.CountAuditAsync("auth.account.unlock", "success", Ct) >= 1);
        Assert.True(await db.CountAuditAsync("auth.login", "failed", Ct) >= 5);
    }

    [Fact(DisplayName = "レート制限: 接続元 × アカウントの上限で 429（ロックアウトの回数は増やさない）。接続元の上限はアカウントをまたいで効く")]
    public async Task RateLimit_AddressFirstThenAccount()
    {
        await using var host = await AuthTestHost.StartAsync(
            db,
            _issuer,
            ["--JinJi:Auth:Login:RateLimit:PerAddress=6", "--JinJi:Auth:Login:RateLimit:PerAddressAndAccount=3"]);
        var (account, token) = await CreateAccountAsync("dave", "E7104");
        using var browser = new TestBrowser(host.ApiPort);
        await browser.RefreshCsrfAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRawAsync(browser, "dave", "wrong")).StatusCode);
        }

        using (var throttled = await LoginRawAsync(browser, "dave", Password))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
            Assert.True(throttled.Headers.RetryAfter is not null);
        }

        Assert.Equal([3], await db.QueryAsSystemAsync<int>("SELECT access_failed_count FROM local_account WHERE id = $1", Ct, account));

        // 接続元の上限（6 回。リセットリンクの 1 回を含む）: 別のアカウントでも 429
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRawAsync(browser, "someone-else", "x")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginRawAsync(browser, "another", "x")).StatusCode);
    }

    [Fact(DisplayName = "TOTP: 有効にするとパスワードの後に符号が要る。同じ時間窓の符号は 2 度受け付けない（有効化に使った符号も）。リカバリコードは 1 回だけ使える")]
    public async Task TwoFactor_TotpAndRecoveryCode()
    {
        var (_, token) = await CreateAccountAsync("erin", "E7105");
        using var browser = await BrowserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);
        browser.CsrfToken = (await LoginAsync(browser, "erin", Password)).GetProperty("csrf_token").GetString();

        using var setup = await browser.PostAsync("/auth/account/two-factor/setup");
        Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await TestBrowser.JsonAsync(setup)).GetProperty("shared_key").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/auth/account/two-factor/enable", new { code = "000000" == Totp.Code(key) ? "111111" : "000000" })).StatusCode);
        var enabledCode = Totp.Code(key);
        using var enable = await browser.PostAsync("/auth/account/two-factor/enable", new { code = enabledCode });
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        var recovery = (await TestBrowser.JsonAsync(enable)).GetProperty("recovery_codes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, recovery.Count);
        Assert.Empty(await db.QueryAsSystemAsync<string>("SELECT code_sha256 FROM local_account_recovery_code WHERE code_sha256 = ANY($1)", Ct, [recovery.ToArray()]));

        // パスワード → TOTP。有効化に使った時間窓の符号は再利用として拒否し、次の時間窓の符号（Identity は前後 2 つの時間窓を受け付ける）で入る
        var nextCode = Totp.Code(key, DateTimeOffset.UtcNow.AddSeconds(30));
        using var second = await BrowserAsync();
        Assert.Equal("two_factor_required", (await LoginAsync(second, "erin", Password)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostAsync("/auth/login/two-factor", new { code = "12345x" })).StatusCode);
        if (enabledCode != nextCode)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await second.PostAsync("/auth/login/two-factor", new { code = enabledCode })).StatusCode);
        }

        using (var otp = await second.PostAsync("/auth/login/two-factor", new { code = nextCode }))
        {
            Assert.Equal(HttpStatusCode.OK, otp.StatusCode);
            Assert.Equal(["pwd", "otp"], (await TestBrowser.JsonAsync(otp)).GetProperty("session").GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
        }

        // 受け付けた符号は、別のブラウザ（盗んだ符号）でも 2 度目は通らない
        using (var replay = await BrowserAsync())
        {
            Assert.Equal("two_factor_required", (await LoginAsync(replay, "erin", Password)).GetProperty("status").GetString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await replay.PostAsync("/auth/login/two-factor", new { code = nextCode })).StatusCode);
        }

        Assert.NotEmpty(await db.QueryAsSystemAsync<long>("SELECT totp_last_step FROM local_account WHERE user_name = 'erin' AND totp_last_step IS NOT NULL", Ct));

        // パスワードを経ずに TOTP だけでは入れない
        using var direct = await BrowserAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await direct.PostAsync("/auth/login/two-factor", new { code = Totp.Code(key, DateTimeOffset.UtcNow.AddSeconds(60)) })).StatusCode);

        // リカバリコードは 1 回だけ
        foreach (var expected in new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized })
        {
            using var rc = await BrowserAsync();
            Assert.Equal("two_factor_required", (await LoginAsync(rc, "erin", Password)).GetProperty("status").GetString());
            Assert.Equal(expected, (await rc.PostAsync("/auth/login/two-factor", new { recovery_code = recovery[0] })).StatusCode);
        }
    }

    [Fact(DisplayName = "パスキー: ログイン中に登録し、パスキーだけでログインできる。origin が違えば通らない")]
    public async Task Passkey_RegisterAndLogin()
    {
        var (_, token) = await CreateAccountAsync("frank", "E7106");
        using var browser = await BrowserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);
        browser.CsrfToken = (await LoginAsync(browser, "frank", Password)).GetProperty("csrf_token").GetString();
        using var authenticator = new TestAuthenticator(AuthTestHost.RpId, AuthTestHost.Origin);

        await RegisterPasskeyAsync(browser, authenticator, "/auth/account/passkeys/options", "/auth/account/passkeys", new { });

        var session = await PasskeyLoginAsync(authenticator);
        Assert.Equal(["hwk"], session.GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(1L, (await db.QueryAsSystemAsync<long>("SELECT sign_count FROM local_account_passkey WHERE credential_id = $1", Ct, authenticator.CredentialId)).Single());

        // origin が違う（別のサイトで作られたアサーション・別のサイトからの要求）
        foreach (var (clientOrigin, headerOrigin) in new[] { ("https://evil.example", AuthTestHost.Origin), (AuthTestHost.Origin, "https://evil.example") })
        {
            using var other = await BrowserAsync();
            other.Origin = headerOrigin;
            using var evilOptions = await other.PostAsync("/auth/login/passkey/options");
            var evil = authenticator.Get(await TestBrowser.JsonAsync(evilOptions), origin: clientOrigin);
            Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsync("/auth/login/passkey", new { credential = evil })).StatusCode);
        }

        using var another = await BrowserAsync();
        using var options = await another.PostAsync("/auth/login/passkey/options");
        var credential = authenticator.Get(await TestBrowser.JsonAsync(options));

        // challenge を出していない（別のブラウザの）アサーションは通らない
        using var replay = await BrowserAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await replay.PostAsync("/auth/login/passkey", new { credential })).StatusCode);
    }

    [Fact(DisplayName = "管理系のロール: パスワードだけではログインできず、パスキー（管理者が発行したリンクで登録）なら入れる")]
    public async Task AdminRole_RequiresSecondFactor()
    {
        var person = await db.InsertPersonAsync("E7107", "e7107@example.test", Ct);
        var (_, token) = await CreateAccountForAsync(person, "grace");
        Assert.Equal(0, (await db.CliAsync("grant-role", "--person", person.ToString(), "--role", "hr_admin")).Exit);
        using var browser = await BrowserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);

        using (var passwordOnly = await LoginRawAsync(browser, "grace", Password))
        {
            Assert.Equal(HttpStatusCode.Forbidden, passwordOnly.StatusCode);
            Assert.Equal("urn:jinji:problem:mfa-required", (await TestBrowser.JsonAsync(passwordOnly)).GetProperty("type").GetString());
        }

        var (exit, output) = await db.CliAsync("issue-reset-link", "--user-name", "grace", "--purpose", "passkey");
        Assert.Equal(0, exit);
        using var authenticator = new TestAuthenticator(AuthTestHost.RpId, AuthTestHost.Origin);
        var linkToken = AuthFixtureDatabase.LinkToken(output);
        await RegisterPasskeyAsync(browser, authenticator, "/auth/reset/passkey/options", "/auth/reset/passkey", new { token = linkToken });

        var session = await PasskeyLoginAsync(authenticator);
        Assert.Equal(["hr_admin"], session.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));

        // リンクは使用済み
        using var again = await BrowserAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await again.PostAsync("/auth/reset/passkey/options", new { token = linkToken })).StatusCode);
    }

    [Fact(DisplayName = "緊急用アカウント: パスキーだけでログインでき、人事情報は読めない（API・RLS とも）。認証の復旧は使え、使用は監査ログに残る")]
    public async Task BreakGlass_CannotReadHrData()
    {
        var (exit, output) = await db.CliAsync("create-break-glass-account", "--user-name", "breakglass1");
        Assert.Equal(0, exit);
        Assert.Equal("passkey", AuthFixtureDatabase.Field(output, "purpose"));
        var accountId = Guid.Parse(AuthFixtureDatabase.Field(output, "account_id"));
        using var browser = await BrowserAsync();
        using var authenticator = new TestAuthenticator(AuthTestHost.RpId, AuthTestHost.Origin);
        await RegisterPasskeyAsync(browser, authenticator, "/auth/reset/passkey/options", "/auth/reset/passkey", new { token = AuthFixtureDatabase.LinkToken(output) });

        // パスワードは持たない
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRawAsync(browser, "breakglass1", Password)).StatusCode);
        var (passwordLink, _) = await db.CliAsync("issue-reset-link", "--user-name", "breakglass1", "--purpose", "password");
        Assert.Equal(1, passwordLink);

        using var signedIn = await BrowserAsync();
        var session = await PasskeyLoginAsync(authenticator, signedIn);
        Assert.Equal("break_glass", session.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, session.GetProperty("person_id").ValueKind);

        // 人事情報は API で読めない
        foreach (var url in new[] { $"/api/v1/people/{F.Person("u1_member")}", "/api/v1/meta", "/api/v1/orgs/tree", "/api/v1/admin/clients" })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.GetAsync(url)).StatusCode);
        }

        // 認証の復旧は使える（候補は見せない）
        using (var unbound = await signedIn.GetAsync("/auth/admin/unbound-logins?include_resolved=true"))
        {
            Assert.Equal(HttpStatusCode.OK, unbound.StatusCode);
            Assert.All((await TestBrowser.JsonAsync(unbound)).EnumerateArray(), row => Assert.Empty(row.GetProperty("candidate_person_ids").EnumerateArray()));
        }

        // 画面の読み込み直しで CSRF のトークンを取り直せる
        await signedIn.RefreshCsrfAsync();
        var (_, token) = await CreateAccountAsync("henry", "E7108");
        var henry = (await db.QueryAsSystemAsync<Guid>("SELECT id FROM local_account WHERE user_name = 'henry'", Ct)).Single();
        Assert.Equal(HttpStatusCode.NoContent, (await signedIn.PostAsync($"/auth/admin/local-accounts/{henry}/unlock")).StatusCode);
        using (var link = await signedIn.PostAsync($"/auth/admin/local-accounts/{henry}/reset-links", new { purpose = "password" }))
        {
            Assert.Equal(HttpStatusCode.OK, link.StatusCode);
            Assert.StartsWith(AuthTestHost.Origin + "/account-recovery?purpose=password#token=", (await TestBrowser.JsonAsync(link)).GetProperty("url").GetString(), StringComparison.Ordinal);
        }

        // 自分のアカウントには使えない
        Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.PostAsync($"/auth/admin/local-accounts/{accountId}/reset-links", new { purpose = "passkey" })).StatusCode);
        // 緊急用アカウントのセッションでは認証の復旧以外のエンドポイントは使えない
        Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);
        // 新しいリンクの発行で、henry の最初のリンクは取り消された
        using var anonymous = await BrowserAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);

        // 監査ログ: 操作者は緊急用アカウント
        Assert.True(await db.CountAuditAsync("auth.login", "success", Ct, "break_glass") >= 1);
        Assert.True(await db.CountAuditAsync("auth.account.unlock", "success", Ct, "break_glass") >= 1);
        Assert.True(await db.CountAuditAsync("auth.reset_link.issue", "success", Ct, "break_glass") >= 1);
        Assert.True(await db.CountAuditAsync("auth.reset_link.issue", "denied", Ct, "break_glass") >= 1);
        Assert.Equal(
            [accountId],
            (await db.QueryAsAppAsync<Guid>(F.HrAdmin(), "SELECT DISTINCT actor_account_id FROM audit_event WHERE actor_type = 'break_glass' AND action = 'auth.account.unlock'", Ct)));
    }

    [Fact(DisplayName = "ローカル: 同じブラウザで別の人がログインすると新しいセッションになり、前のセッションは終わる")]
    public async Task Login_RotatesSessionInSameBrowser()
    {
        var (_, kateToken) = await CreateAccountAsync("kate", "E7110");
        var (_, leoToken) = await CreateAccountAsync("leo", "E7111");
        using var browser = await BrowserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token = kateToken, new_password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token = leoToken, new_password = Password })).StatusCode);

        var kate = await LoginAsync(browser, "kate", Password);
        var kateSession = kate.GetProperty("session").GetProperty("session_id").GetGuid();
        var kateCookie = browser.Cookies[SessionCookie.Name];

        // ログイン済みのブラウザの次のログインには、新しい閲覧者のトークンが要る（ログインの応答で受け取る）
        browser.CsrfToken = kate.GetProperty("csrf_token").GetString();
        var leo = await LoginAsync(browser, "leo", Password);
        var leoSession = leo.GetProperty("session").GetProperty("session_id").GetGuid();

        Assert.NotEqual(kateSession, leoSession);
        Assert.NotEqual(kateCookie, browser.Cookies[SessionCookie.Name]);
        Assert.Equal(["replaced"], await db.QueryAsSystemAsync<string>("SELECT end_kind FROM ui_session WHERE id = $1", Ct, kateSession));
        using var me = await browser.GetAsync("/auth/me");
        Assert.Equal(leoSession, (await TestBrowser.JsonAsync(me)).GetProperty("session_id").GetGuid());

        // 古い Cookie はもう使えない
        using var stale = new TestBrowser(_host.ApiPort);
        stale.SetCookie(SessionCookie.Name, kateCookie);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stale.GetAsync("/auth/me")).StatusCode);
    }

    [Fact(DisplayName = "退職済みの人のローカルアカウントはログインできない")]
    public async Task RetiredPerson_CannotLogIn()
    {
        var (_, token) = await CreateAccountForAsync(F.Person("retired"), "ivan");
        using var browser = await BrowserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRawAsync(browser, "ivan", Password)).StatusCode);
    }

    [Fact(DisplayName = "セッション: 無操作の期限（8 時間）を過ぎたら無効、最長（12 時間）はログインから数える")]
    public async Task Session_IdleAndAbsoluteTimeouts()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await AuthTestHost.StartAsync(db, _issuer, configureServices: services => services.AddSingleton<TimeProvider>(time));
        var (_, token) = await CreateAccountAsync("judy", "E7109");
        using var browser = new TestBrowser(host.ApiPort);
        await browser.RefreshCsrfAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token, new_password = Password })).StatusCode);

        Assert.Equal("signed_in", (await LoginAsync(browser, "judy", Password)).GetProperty("status").GetString());
        time.Advance(TimeSpan.FromHours(7));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/auth/me")).StatusCode);
        time.Advance(TimeSpan.FromHours(4.5));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/auth/me")).StatusCode);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me")).StatusCode);

        using var idle = new TestBrowser(host.ApiPort);
        await idle.RefreshCsrfAsync();
        Assert.Equal("signed_in", (await LoginAsync(idle, "judy", Password)).GetProperty("status").GetString());
        time.Advance(TimeSpan.FromHours(8) + TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await idle.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(
            ["expired", "idle_timeout"],
            (await db.QueryAsSystemAsync<string>(
                "SELECT s.end_kind FROM ui_session s JOIN local_account a ON a.id = s.local_account_id WHERE a.user_name = 'judy' ORDER BY s.created_at",
                Ct)));
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private async Task<(Guid Account, string Token)> CreateAccountAsync(string userName, string employeeNumber) =>
        await CreateAccountForAsync(await db.InsertPersonAsync(employeeNumber, $"{userName}@example.test", Ct), userName);

    private async Task<(Guid Account, string Token)> CreateAccountForAsync(Guid person, string userName)
    {
        var (exit, output) = await db.CliAsync("create-local-account", "--person", person.ToString(), "--user-name", userName);
        Assert.True(exit == 0, output);
        return (Guid.Parse(AuthFixtureDatabase.Field(output, "account_id")), AuthFixtureDatabase.LinkToken(output));
    }

    private async Task<TestBrowser> BrowserAsync()
    {
        var browser = new TestBrowser(_host.ApiPort) { Origin = AuthTestHost.Origin };
        await browser.RefreshCsrfAsync();
        return browser;
    }

    private static Task<HttpResponseMessage> LoginRawAsync(TestBrowser browser, string userName, string password) =>
        browser.PostAsync("/auth/login/password", new { user_name = userName, password });

    private static async Task<JsonElement> LoginAsync(TestBrowser browser, string userName, string password)
    {
        using var response = await LoginRawAsync(browser, userName, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestBrowser.JsonAsync(response);
    }

    private static async Task RegisterPasskeyAsync(TestBrowser browser, TestAuthenticator authenticator, string optionsUrl, string completeUrl, object optionsBody)
    {
        using var options = await browser.PostAsync(optionsUrl, optionsBody);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var credential = authenticator.Create(await TestBrowser.JsonAsync(options));
        var body = JsonSerializer.SerializeToElement(optionsBody);
        var payload = body.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
        payload["credential"] = credential;
        payload["name"] = "テストの認証器";
        using var complete = await browser.PostAsync(completeUrl, payload);
        Assert.True(complete.StatusCode == HttpStatusCode.NoContent, await complete.Content.ReadAsStringAsync(Ct));
    }

    private async Task<JsonElement> PasskeyLoginAsync(TestAuthenticator authenticator, TestBrowser? browser = null)
    {
        var owned = browser is null;
        browser ??= await BrowserAsync();
        try
        {
            if (browser.CsrfToken is null)
            {
                await browser.RefreshCsrfAsync();
            }

            browser.Origin = AuthTestHost.Origin;
            using var options = await browser.PostAsync("/auth/login/passkey/options");
            Assert.Equal(HttpStatusCode.OK, options.StatusCode);
            var credential = authenticator.Get(await TestBrowser.JsonAsync(options));
            using var login = await browser.PostAsync("/auth/login/passkey", new { credential });
            Assert.True(login.StatusCode == HttpStatusCode.OK, await login.Content.ReadAsStringAsync(Ct));
            var body = await TestBrowser.JsonAsync(login);
            browser.CsrfToken = body.GetProperty("csrf_token").GetString();
            return body.GetProperty("session");
        }
        finally
        {
            if (owned)
            {
                browser.Dispose();
            }
        }
    }
}
