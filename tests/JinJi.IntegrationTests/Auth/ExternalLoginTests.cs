using System.Net;
using System.Text.Json;
using JinJi.Host.Auth;
using JinJi.IntegrationTests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.IntegrationTests.Auth;

/// <summary>
/// 外部 IdP のログイン（OIDC。スタブの発行者）と UI のセッション（ADR-0004 の段階 A）。
/// </summary>
/// <remarks>
/// IdP は 3 つ: entra（Entra ID。tid + oid）、corp（汎用 OIDC。社員番号のクレーム employee_id を信頼する・法人 A）、
/// google（汎用 OIDC。社員番号のクレームは信頼しない）。どれも同じスタブを使うので、テストごとに主体の識別子を変える。
/// </remarks>
public sealed class ExternalLoginTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>, IAsyncLifetime
{
    private readonly TestIssuer _issuer = new();
    private StubOidcProvider _stub = null!;
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync()
    {
        _stub = await StubOidcProvider.StartAsync();
        _host = await AuthTestHost.StartAsync(
            db,
            _issuer,
            [
                .. _stub.ConfigArgs(0, "entra", "entra", tenantId: AuthFixtureDatabase.TenantId),
                .. _stub.ConfigArgs(1, "corp", "oidc", employeeNumberClaim: "employee_id", trustEmployeeNumber: true),
                "--JinJi:Auth:Login:Providers:1:EmployeeNumberLegalEntity=A",
                .. _stub.ConfigArgs(2, "google", "oidc", employeeNumberClaim: "employee_id"),
            ]);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _stub.DisposeAsync();
        _issuer.Dispose();
    }

    [Fact(DisplayName = "Entra: oid が取込の外部 ID の対応と一致し tid が設定と一致すれば自動で紐付き、2 回目は既存の紐付けを使う")]
    public async Task Entra_BindsByObjectId()
    {
        using var browser = Browser();
        var claims = EntraClaims(db.EntraOid);

        using var callback = await AuthTestHost.LoginWithOidcAsync(browser, _stub, "entra", claims, "/people?tab=1");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/people?tab=1", callback.Headers.Location!.OriginalString);
        var me = await MeAsync(browser);
        Assert.Equal("person", me.GetProperty("kind").GetString());
        Assert.Equal(F.Person("u1_member"), me.GetProperty("person_id").GetGuid());
        Assert.Equal("entra", me.GetProperty("provider").GetString());
        Assert.Equal(["fed"], me.GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
        Assert.Equal(
            ["entra_object_id"],
            await db.QueryAsSystemAsync<string>("SELECT basis FROM login_binding WHERE subject = $1 AND created_by_kind = 'login'", Ct, db.EntraOid));

        // 2 回目は既存の紐付け（作り直さない）
        using var again = Browser();
        using var second = await AuthTestHost.LoginWithOidcAsync(again, _stub, "entra", claims);
        Assert.Equal("/", second.Headers.Location!.OriginalString);
        Assert.Single(await db.QueryAsSystemAsync<Guid>("SELECT id FROM login_binding WHERE subject = $1", Ct, db.EntraOid));
        Assert.True(await db.CountAuditAsync("auth.binding.create", "success", Ct) >= 1);
        Assert.True(await db.CountAuditAsync("auth.login", "success", Ct) >= 2);

        // セッションで REST API を使える（経路は UI、項目の上限なし）
        using var person = await browser.GetAsync($"/api/v1/people/{F.Person("s1_member")}");
        Assert.Equal(HttpStatusCode.OK, person.StatusCode);
    }

    [Fact(DisplayName = "Entra: tid が設定のテナントと違えば拒否する（セッションを作らない）")]
    public async Task Entra_RejectsOtherTenant()
    {
        using var browser = Browser();
        var claims = EntraClaims(db.EntraOid);
        claims["tid"] = Guid.NewGuid().ToString("D");

        using var callback = await AuthTestHost.LoginWithOidcAsync(browser, _stub, "entra", claims);

        Assert.Equal("/login?error=rejected", callback.Headers.Location!.OriginalString);
        Assert.DoesNotContain(SessionCookie.Name, browser.Cookies.Keys);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me")).StatusCode);
    }

    [Fact(DisplayName = "社員番号: 信頼すると設定した IdP のクレームが 1 人の社員番号と一致すれば紐付ける")]
    public async Task EmployeeNumber_TrustedClaimBinds()
    {
        using var browser = Browser();
        var subject = Guid.NewGuid().ToString("N");

        using var callback = await AuthTestHost.LoginWithOidcAsync(browser, _stub, "corp", new() { ["sub"] = subject, ["employee_id"] = EmployeeNumber("s2_member") });

        Assert.Equal("/", callback.Headers.Location!.OriginalString);
        Assert.Equal(F.Person("s2_member"), (await MeAsync(browser)).GetProperty("person_id").GetGuid());
        Assert.Equal(["employee_number"], await db.QueryAsSystemAsync<string>("SELECT basis FROM login_binding WHERE subject = $1", Ct, subject));
    }

    [Fact(DisplayName = "メール: 社用メールが一致しても自動で紐付けず、候補として未紐付けのログインに記録する。手動の紐付けの後はログインできる")]
    public async Task Email_IsNeverUsedForBinding()
    {
        using var browser = Browser();
        var subject = Guid.NewGuid().ToString("N");
        var claims = new Dictionary<string, object>
        {
            ["sub"] = subject,
            ["email"] = "S2_MEMBER@example.test",
            ["name"] = "s2 のアカウント",
            // 信頼しない IdP の社員番号のクレームでも紐付けない
            ["employee_id"] = EmployeeNumber("s2_member"),
        };

        using var callback = await AuthTestHost.LoginWithOidcAsync(browser, _stub, "google", claims);

        Assert.Equal("/login?error=unbound", callback.Headers.Location!.OriginalString);
        Assert.DoesNotContain(SessionCookie.Name, browser.Cookies.Keys);
        Assert.Empty(await db.QueryAsSystemAsync<Guid>("SELECT id FROM login_binding WHERE subject = $1", Ct, subject));
        var unboundId = Assert.Single(await db.QueryAsSystemAsync<Guid>("SELECT id FROM unbound_login WHERE subject = $1", Ct, subject));
        Assert.Equal(
            [F.Person("s2_member")],
            (await db.QueryAsSystemAsync<Guid[]>("SELECT candidate_person_ids FROM unbound_login WHERE id = $1", Ct, unboundId)).Single());
        Assert.Equal(
            [EmployeeNumber("s2_member")],
            await db.QueryAsSystemAsync<string>("SELECT claimed_employee_number FROM unbound_login WHERE id = $1", Ct, unboundId));
        Assert.True(await db.CountAuditAsync("auth.login", "denied", Ct) >= 1);

        // 再試行は同じ行で数える
        using (await AuthTestHost.LoginWithOidcAsync(Browser(), _stub, "google", claims))
        {
            Assert.Equal([2], await db.QueryAsSystemAsync<int>("SELECT attempt_count FROM unbound_login WHERE id = $1", Ct, unboundId));
        }

        // 人事管理者に相当する管理 CLI で手動の紐付け → ログインできる。未紐付けのログインは解決済み
        var (exit, output) = await CliAsync("bind-login", "--person", F.Person("s2_member").ToString(), "--unbound", unboundId.ToString());
        Assert.Equal(0, exit);
        Assert.Equal(subject, AuthFixtureDatabase.Field(output, "subject"));
        using var bound = await AuthTestHost.LoginWithOidcAsync(browser, _stub, "google", claims);
        Assert.Equal("/", bound.Headers.Location!.OriginalString);
        Assert.Equal(F.Person("s2_member"), (await MeAsync(browser)).GetProperty("person_id").GetGuid());
        Assert.Single(await db.QueryAsSystemAsync<DateTime>("SELECT resolved_at FROM unbound_login WHERE id = $1 AND resolved_at IS NOT NULL", Ct, unboundId));
        Assert.Equal(["manual"], await db.QueryAsSystemAsync<string>("SELECT basis FROM login_binding WHERE subject = $1 AND created_by_kind = 'cli'", Ct, subject));

        // 同じ主体に 2 つ目の紐付けは作れない（1 主体は 1 人）
        var (conflict, _) = await CliAsync("bind-login", "--person", F.Person("s1_member").ToString(), "--provider", "google", "--subject", subject, "--issuer", _stub.Issuer);
        Assert.Equal(1, conflict);
    }

    [Fact(DisplayName = "退職済みの人はログインできない（自動の紐付けも作らない）")]
    public async Task Retired_CannotLogIn()
    {
        using var browser = Browser();

        using var callback = await AuthTestHost.LoginWithOidcAsync(browser, _stub, "entra", EntraClaims(db.RetiredOid));

        Assert.Equal("/login?error=inactive", callback.Headers.Location!.OriginalString);
        Assert.Empty(await db.QueryAsSystemAsync<Guid>("SELECT id FROM login_binding WHERE subject = $1", Ct, db.RetiredOid));
    }

    [Fact(DisplayName = "一時停止: 一時停止になった人のセッションは次の要求から無効になり、以後ログインできない")]
    public async Task Suspension_EndsSessionImmediately()
    {
        var person = await db.InsertPersonAsync("E7001", "e7001@example.test", Ct);
        var subject = Guid.NewGuid().ToString("N");
        Assert.Equal(0, (await CliAsync("bind-login", "--person", person.ToString(), "--provider", "google", "--subject", subject, "--issuer", _stub.Issuer)).Exit);
        using var browser = Browser();
        using (await AuthTestHost.LoginWithOidcAsync(browser, _stub, "google", new() { ["sub"] = subject }))
        {
            Assert.Equal(person, (await MeAsync(browser)).GetProperty("person_id").GetGuid());
        }

        await db.SuspendAsync(person, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync($"/api/v1/people/{person}")).StatusCode);
        Assert.Equal(["person_inactive"], await db.QueryAsSystemAsync<string>("SELECT end_kind FROM ui_session WHERE person_id = $1", Ct, person));
        using var again = await AuthTestHost.LoginWithOidcAsync(Browser(), _stub, "google", new() { ["sub"] = subject });
        Assert.Equal("/login?error=inactive", again.Headers.Location!.OriginalString);
    }

    [Fact(DisplayName = "認証の管理: 人事管理者が UI で紐付けを取り消すと、その紐付けのセッションは次の要求から無効。CSRF のトークンが要る")]
    public async Task Admin_RevokesBindingWithCsrf()
    {
        var (admin, _) = await LoginBoundAsync("hr_admin");
        var (user, userSubject) = await LoginBoundAsync("dp2_member");
        var binding = (await db.QueryAsSystemAsync<Guid>("SELECT id FROM login_binding WHERE subject = $1", Ct, userSubject)).Single();

        // CSRF のトークンが無い・合わない要求は拒否
        using (var missing = await admin.PostAsync($"/auth/admin/login-bindings/{binding}/revoke", csrf: false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
            Assert.Equal("urn:jinji:problem:csrf", (await TestBrowser.JsonAsync(missing)).GetProperty("type").GetString());
        }

        admin.CsrfToken = "bogus";
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/auth/admin/login-bindings/{binding}/revoke")).StatusCode);

        // 別の利用者のトークンでも通らない（トークンは閲覧者に束縛される）
        admin.CsrfToken = await user.RefreshCsrfAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/auth/admin/login-bindings/{binding}/revoke")).StatusCode);

        await admin.RefreshCsrfAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/auth/admin/login-bindings/{binding}/revoke")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/auth/me")).StatusCode);
        Assert.True(await db.CountAuditAsync("auth.binding.revoke", "success", Ct) >= 1);

        // 一般の人は認証の管理を使えない
        var (general, _) = await LoginBoundAsync("u3_member");
        await general.RefreshCsrfAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await general.GetAsync("/auth/admin/unbound-logins")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await general.PostAsync($"/auth/admin/login-bindings/{binding}/revoke")).StatusCode);

        // 人事管理者は未紐付けのログインの一覧（候補つき）を見られる
        using var list = await admin.GetAsync("/auth/admin/unbound-logins?include_resolved=true");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact(DisplayName = "CSRF: Cookie のセッションでの REST API の状態変更にもトークンが要る。Bearer には要らない")]
    public async Task Csrf_RequiredForCookieStateChanges()
    {
        var (admin, _) = await LoginBoundAsync("hr_head");

        using (var noToken = await admin.PostAsync("/api/v1/admin/clients/portal/release", csrf: false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, noToken.StatusCode);
        }

        await admin.RefreshCsrfAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/admin/clients/portal/release")).StatusCode);

        // Bearer は Cookie を読まないので CSRF の対象外
        using var bearer = await _host.PostAsync("/api/v1/admin/clients/portal/release", _issuer.User("portal", F.Person("hr_head"), "hr_admin"));
        Assert.Equal(HttpStatusCode.NotFound, bearer.StatusCode);
    }

    [Fact(DisplayName = "Cookie: __Host- 接頭辞・Secure・HttpOnly・SameSite=Lax・Path=/、永続化しない。ログインのたびに新しいセッションになる")]
    public async Task SessionCookie_AttributesAndRotation()
    {
        var (browser, subject) = await LoginBoundAsync("s3_member");
        var header = Assert.Single(browser.LastSetCookies, c => c.StartsWith(SessionCookie.Name + "=", StringComparison.Ordinal)).ToLowerInvariant();
        Assert.Contains("secure", header, StringComparison.Ordinal);
        Assert.Contains("httponly", header, StringComparison.Ordinal);
        Assert.Contains("samesite=lax", header, StringComparison.Ordinal);
        Assert.Contains("path=/", header, StringComparison.Ordinal);
        Assert.DoesNotContain("domain=", header, StringComparison.Ordinal);
        Assert.DoesNotContain("expires=", header, StringComparison.Ordinal);
        Assert.DoesNotContain("max-age=", header, StringComparison.Ordinal);
        // 中身は鍵だけ（人の ID などを含まない）
        Assert.DoesNotContain(F.Person("s3_member").ToString(), browser.Cookies[SessionCookie.Name], StringComparison.OrdinalIgnoreCase);

        var first = (await MeAsync(browser)).GetProperty("session_id").GetGuid();
        using (await AuthTestHost.LoginWithOidcAsync(browser, _stub, "google", new() { ["sub"] = subject }))
        {
            var second = (await MeAsync(browser)).GetProperty("session_id").GetGuid();
            Assert.NotEqual(first, second);
        }

        Assert.Equal(["replaced"], await db.QueryAsSystemAsync<string>("SELECT end_kind FROM ui_session WHERE id = $1", Ct, first));

        // ログアウトでセッションを終える
        await browser.RefreshCsrfAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/logout")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me")).StatusCode);
        Assert.True(await db.CountAuditAsync("auth.logout", "success", Ct) >= 1);
    }

    [Fact(DisplayName = "振り分け: Authorization ヘッダーがあれば Bearer だけを見て Cookie は読まない。無ければ Cookie")]
    public async Task BearerAndCookie_AreSeparated()
    {
        var (browser, _) = await LoginBoundAsync("u2_member");

        // Cookie だけ: 一般の人（未来の時点は見られない）
        Assert.False((await JsonAsync(browser, "/api/v1/meta")).GetProperty("can_view_future").GetBoolean());
        // Cookie + 人事管理者のトークン: トークンの閲覧者
        using (var withBearer = await browser.GetAsync("/api/v1/meta", _issuer.User("portal", F.Person("hr_admin"), "hr_admin")))
        {
            Assert.True((await TestBrowser.JsonAsync(withBearer)).GetProperty("can_view_future").GetBoolean());
        }

        // Cookie + 不正なトークン: 401（Cookie に戻らない）
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/meta", "not-a-token")).StatusCode);
        // /auth/me は Cookie のセッションだけ
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/me", _issuer.User("portal", F.Person("u2_member")))).StatusCode);
    }

    [Fact(DisplayName = "MCP のポートは Cookie を読まず、OIDC のコールバックも受けない")]
    public async Task McpPort_IgnoresCookie()
    {
        var (browser, _) = await LoginBoundAsync("unit1_head");
        var cookie = $"{SessionCookie.Name}={browser.Cookies[SessionCookie.Name]}";

        Assert.False(await AuthenticateAsync(_host.McpPort, cookie));
        Assert.True(await AuthenticateAsync(_host.ApiPort, cookie));

        using var mcp = new TestBrowser(_host.McpPort);
        mcp.SetCookie(SessionCookie.Name, browser.Cookies[SessionCookie.Name]);
        Assert.Equal(HttpStatusCode.NotFound, (await mcp.GetAsync("/auth/callback/entra?code=x&state=y")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mcp.GetAsync("/auth/me")).StatusCode);
    }

    [Fact(DisplayName = "ログインの後の戻り先は同じオリジンのパスだけ（外部の URL は / にする）")]
    public async Task ReturnUrl_IsLocalOnly()
    {
        var subject = Guid.NewGuid().ToString("N");
        Assert.Equal(0, (await CliAsync("bind-login", "--person", F.Person("r1_member").ToString(), "--provider", "google", "--subject", subject, "--issuer", _stub.Issuer)).Exit);
        foreach (var url in new[] { "https://evil.example/", "//evil.example/", "/\\evil.example", "/auth/logout" })
        {
            using var callback = await AuthTestHost.LoginWithOidcAsync(Browser(), _stub, "google", new() { ["sub"] = subject }, url);
            Assert.Equal("/", callback.Headers.Location!.OriginalString);
        }

        Assert.Equal("/", AuthEndpoints.SafeReturnUrl(null));
        Assert.Equal("/orgs/1", AuthEndpoints.SafeReturnUrl("/orgs/1"));
    }

    [Fact(DisplayName = "ログイン方式の一覧と、知らない方式の拒否")]
    public async Task Providers_AreListed()
    {
        using var browser = Browser();
        var methods = await JsonAsync(browser, "/auth/providers");
        Assert.Equal(["entra", "corp", "google"], methods.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.True(methods.GetProperty("password").GetBoolean());
        Assert.True(methods.GetProperty("passkey").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/auth/login/unknown")).StatusCode);
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private TestBrowser Browser() => new(_host.ApiPort);

    /// <summary>管理 CLI（このホストと同じログイン方式の設定を渡す）。</summary>
    private Task<(int Exit, string Output)> CliAsync(string command, params string[] args) =>
        db.CliAsync(command, [.. args, .. _stub.ConfigArgs(0, "entra", "entra", tenantId: AuthFixtureDatabase.TenantId), .. _stub.ConfigArgs(1, "google", "oidc")]);

    private Dictionary<string, object> EntraClaims(string oid) => new()
    {
        ["sub"] = Guid.NewGuid().ToString("N"),
        ["oid"] = oid,
        ["tid"] = AuthFixtureDatabase.TenantId,
        ["name"] = "Entra の利用者",
    };

    private string EmployeeNumber(string key) => F.People.Single(p => p.Id == F.Person(key)).EmployeeNumber;

    /// <summary>人を google の新しい主体に手動で紐付け、ログインしたブラウザを返す。</summary>
    private async Task<(TestBrowser Browser, string Subject)> LoginBoundAsync(string key)
    {
        var subject = Guid.NewGuid().ToString("N");
        Assert.Equal(0, (await CliAsync("bind-login", "--person", F.Person(key).ToString(), "--provider", "google", "--subject", subject, "--issuer", _stub.Issuer)).Exit);
        var browser = Browser();
        using var callback = await AuthTestHost.LoginWithOidcAsync(browser, _stub, "google", new() { ["sub"] = subject });
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
        return (browser, subject);
    }

    private static async Task<JsonElement> MeAsync(TestBrowser browser) => await JsonAsync(browser, "/auth/me");

    private static async Task<JsonElement> JsonAsync(TestBrowser browser, string url)
    {
        using var response = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await TestBrowser.JsonAsync(response);
    }

    /// <summary>ホストの認証の振り分けで、指定のポートに来た Cookie だけの要求を認証する。</summary>
    private async Task<bool> AuthenticateAsync(int localPort, string cookie)
    {
        await using var scope = _host.App.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        http.Connection.LocalPort = localPort;
        http.Request.Headers.Cookie = cookie;
        var result = await http.AuthenticateAsync();
        return result.Succeeded;
    }
}
