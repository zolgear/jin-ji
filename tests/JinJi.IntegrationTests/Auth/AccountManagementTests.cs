using System.Net;
using System.Text.Json;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Auth;

/// <summary>
/// 認証の管理の不足分（0014）: ローカルアカウントの一覧・無効化と再有効化、本人のパスキーの一覧・削除、本人による委任の一覧・取り消し。
/// </summary>
public sealed class AccountManagementTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>, IAsyncLifetime
{
    private const string Password = "correct-Horse-battery-9";
    private const string Loopback = "http://127.0.0.1:43124/callback";
    private OAuthTestEnvironment _env = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _env = await OAuthTestEnvironment.StartAsync(db);

    public async ValueTask DisposeAsync() => await _env.DisposeAsync();

    // ---- 委任 ----------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "委任: 本人は自分の委任を一覧・取り消しでき、取り消すとそのクライアントのトークンは次の要求から使えない。他人の委任は取り消せない")]
    public async Task Delegations_ListAndRevokeBySelf()
    {
        await _env.RegisterPublicClientAsync("deleg-app", Loopback);
        var person = await db.InsertPersonAsync("E8101", "e8101@example.test", Ct);
        var browser = await _env.LoginAsync(person);
        var pkce = Pkce.New();
        var tokens = await _env.ExchangeAsync("deleg-app", await _env.CodeAsync(browser, "deleg-app", Loopback, pkce), Loopback, pkce);
        Assert.Equal(HttpStatusCode.OK, tokens.Status);

        var list = await TestBrowser.JsonAsync(await browser.GetAsync("/oauth/delegations"));
        var delegation = Assert.Single(list.EnumerateArray());
        Assert.Equal("deleg-app", delegation.GetProperty("client_id").GetString());
        Assert.Equal("deleg-app のアプリ", delegation.GetProperty("client_name").GetString());
        Assert.Equal("mcp", delegation.GetProperty("resource").GetString());
        Assert.Equal(["jinji.read"], delegation.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()));
        Assert.NotEqual(JsonValueKind.Null, delegation.GetProperty("last_used_at").ValueKind);
        var id = delegation.GetProperty("id").GetGuid();

        // 他人には見えず、取り消せない
        var other = await _env.LoginAsync(F.Person("u1_member"));
        Assert.Empty((await TestBrowser.JsonAsync(await other.GetAsync("/oauth/delegations"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/oauth/delegations/{id}/revoke")).StatusCode);
        using (var ping = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        // トークン（Bearer）では使えない
        using (var byToken = await _env.Host.GetAsync("/oauth/delegations", _env.JwtIssuer.User("portal", person)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, byToken.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync($"/oauth/delegations/{id}/revoke")).StatusCode);
        using (var ping = await _env.McpPingAsync(tokens.AccessToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ping.StatusCode);
        }

        Assert.Equal("invalid_grant", (await _env.RefreshAsync("deleg-app", tokens.RefreshToken!)).Error);
        Assert.Empty((await TestBrowser.JsonAsync(await browser.GetAsync("/oauth/delegations"))).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync($"/oauth/delegations/{id}/revoke")).StatusCode);
        Assert.Equal(["user"], await db.QueryAsSystemAsync<string>("SELECT end_kind FROM oauth_token_family WHERE id = $1", Ct, id));
        Assert.True(await db.CountAuditAsync("auth.consent.revoke", "success", Ct, "user") >= 1);
        Assert.True(await db.CountAuditAsync("auth.consent.revoke", "denied", Ct, "user") >= 1);
    }

    // ---- ローカルアカウントの管理 ----------------------------------------------------------------------------------

    [Fact(DisplayName = "ローカルアカウント: 人事管理者は一覧を見られ、無効化でセッションを終え委任を失効する。再有効化でログインできるが、終えたセッションは戻らない")]
    public async Task LocalAccounts_ListDisableEnable()
    {
        var person = await db.InsertPersonAsync("E8102", "e8102@example.test", Ct);
        var accountId = await CreatePasswordAccountAsync(person, "mgmt-oscar");
        using var local = await LocalBrowserAsync();
        Assert.Equal(HttpStatusCode.OK, (await LoginRawAsync(local, "mgmt-oscar", Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await local.GetAsync("/auth/me")).StatusCode);

        // 同じ人の委任（外部 IdP のログインで同意したもの）
        await _env.RegisterPublicClientAsync("mgmt-app", Loopback);
        var token = await _env.AccessTokenAsync(person, "mgmt-app", Loopback);

        var admin = await _env.LoginAsync(F.Person("hr_head"));
        using (var list = await admin.GetAsync("/auth/admin/local-accounts?query=MGMT-osc"))
        {
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var row = Assert.Single((await TestBrowser.JsonAsync(list)).EnumerateArray());
            Assert.Equal(accountId, row.GetProperty("id").GetGuid());
            Assert.Equal("person", row.GetProperty("kind").GetString());
            Assert.Equal(person, row.GetProperty("person_id").GetGuid());
            Assert.True(row.GetProperty("has_password").GetBoolean());
            Assert.Equal(0, row.GetProperty("passkey_count").GetInt32());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("disabled_at").ValueKind);
        }

        // 一般の人・トークンは使えない
        var member = await _env.LoginAsync(F.Person("u1_member"));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/auth/admin/local-accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync($"/auth/admin/local-accounts/{accountId}/disable")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/auth/admin/local-accounts?limit=0")).StatusCode);

        using (var disabled = await admin.PostAsync($"/auth/admin/local-accounts/{accountId}/disable"))
        {
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            var body = await TestBrowser.JsonAsync(disabled);
            Assert.Equal(1, body.GetProperty("sessions_ended").GetInt32());
            Assert.Equal(1, body.GetProperty("delegations_revoked").GetInt32());
        }

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/auth/admin/local-accounts/{accountId}/disable")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await local.GetAsync("/auth/me")).StatusCode);
        using (var ping = await _env.McpPingAsync(token))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, ping.StatusCode);
        }

        using (var again = await LocalBrowserAsync())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginRawAsync(again, "mgmt-oscar", Password)).StatusCode);
        }

        Assert.Contains("account_disabled", await db.QueryAsSystemAsync<string>("SELECT end_kind FROM ui_session WHERE local_account_id = $1", Ct, accountId));
        Assert.Equal(["admin"], await db.QueryAsSystemAsync<string>("SELECT f.end_kind FROM oauth_token_family f JOIN api_client c ON c.id = f.client_ref WHERE c.client_id = 'mgmt-app'", Ct));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/auth/admin/local-accounts/{accountId}/enable")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/auth/admin/local-accounts/{accountId}/enable")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await local.GetAsync("/auth/me")).StatusCode);
        using (var after = await LocalBrowserAsync())
        {
            Assert.Equal(HttpStatusCode.OK, (await LoginRawAsync(after, "mgmt-oscar", Password)).StatusCode);
        }

        Assert.True(await db.CountAuditAsync("auth.account.disable", "success", Ct, "user") >= 1);
        Assert.True(await db.CountAuditAsync("auth.account.enable", "success", Ct, "user") >= 1);
    }

    [Fact(DisplayName = "ローカルアカウント: 管理 CLI で一覧・無効化・再有効化できる。自分のアカウントは無効化できない（人事管理者）")]
    public async Task LocalAccounts_CliAndSelf()
    {
        var person = await db.InsertPersonAsync("E8103", "e8103@example.test", Ct);
        var accountId = await CreatePasswordAccountAsync(person, "mgmt-pia");

        var (exit, output) = await db.CliAsync("list-local-accounts", "--query", "mgmt-pia");
        Assert.Equal(0, exit);
        Assert.Contains($"{accountId}\tperson\tmgmt-pia\t{person}\ttrue\tfalse\t0\t-\t-", output, StringComparison.Ordinal);

        (exit, output) = await db.CliAsync("disable-account", "--user-name", "mgmt-pia");
        Assert.Equal(0, exit);
        Assert.Equal("0", AuthFixtureDatabase.Field(output, "sessions_ended"));
        Assert.Equal(1, (await db.CliAsync("issue-reset-link", "--user-name", "mgmt-pia")).Exit);
        Assert.Equal(0, (await db.CliAsync("enable-account", "--user-name", "mgmt-pia")).Exit);
        Assert.Equal(1, (await db.CliAsync("enable-account", "--user-name", "mgmt-pia")).Exit);

        // 人事管理者の人は、自分のローカルアカウントを無効化できない（乗っ取ったセッションで自分の資格情報を触らせない規則と同じ）
        var adminAccount = await CreatePasswordAccountAsync(F.Person("hr_head"), "mgmt-head");
        var admin = await _env.LoginAsync(F.Person("hr_head"));
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync($"/auth/admin/local-accounts/{adminAccount}/disable")).StatusCode);
        Assert.True(await db.CountAuditAsync("auth.account.disable", "denied", Ct, "user") >= 1);
    }

    // ---- パスキー ------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "パスキー: 本人は一覧・削除できる。最後の 1 つで、管理系のロールの多要素か、ログインの手段が無くなる場合は 409")]
    public async Task Passkeys_ListAndDelete()
    {
        // パスワードがあり管理系のロールを持たない人は、最後のパスキーも消せる
        var person = await db.InsertPersonAsync("E8104", "e8104@example.test", Ct);
        await CreatePasswordAccountAsync(person, "mgmt-quinn");
        using var browser = await LocalBrowserAsync();
        browser.CsrfToken = (await TestBrowser.JsonAsync(await LoginRawAsync(browser, "mgmt-quinn", Password))).GetProperty("csrf_token").GetString();
        using var authenticator = new TestAuthenticator(AuthTestHost.RpId, AuthTestHost.Origin);
        await RegisterPasskeyAsync(browser, authenticator, "/auth/account/passkeys/options", "/auth/account/passkeys", new { });

        var list = await TestBrowser.JsonAsync(await browser.GetAsync("/auth/account/passkeys"));
        var passkey = Assert.Single(list.EnumerateArray());
        Assert.Equal("テストの認証器", passkey.GetProperty("name").GetString());
        var id = passkey.GetProperty("id").GetString()!;
        Assert.Equal(System.Buffers.Text.Base64Url.EncodeToString(authenticator.CredentialId), id);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/auth/account/passkeys/AAAAAAAAAAAAAAAAAAAAAA/delete")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostAsync("/auth/account/passkeys/!!/delete")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync($"/auth/account/passkeys/{id}/delete")).StatusCode);
        Assert.Empty((await TestBrowser.JsonAsync(await browser.GetAsync("/auth/account/passkeys"))).EnumerateArray());

        // 管理系のロールを持ち TOTP が無い人の最後のパスキーは消せない（パスワードだけではログインできなくなる）
        var adminPerson = await db.InsertPersonAsync("E8105", "e8105@example.test", Ct);
        await CreatePasswordAccountAsync(adminPerson, "mgmt-rita");
        Assert.Equal(0, (await db.CliAsync("grant-role", "--person", adminPerson.ToString(), "--role", "hr_admin")).Exit);
        var (exit, output) = await db.CliAsync("issue-reset-link", "--user-name", "mgmt-rita", "--purpose", "passkey");
        Assert.Equal(0, exit);
        using var adminAuthenticator = new TestAuthenticator(AuthTestHost.RpId, AuthTestHost.Origin);
        using (var anonymous = await LocalBrowserAsync())
        {
            await RegisterPasskeyAsync(anonymous, adminAuthenticator, "/auth/reset/passkey/options", "/auth/reset/passkey", new { token = AuthFixtureDatabase.LinkToken(output) });
        }

        using var adminBrowser = await LocalBrowserAsync();
        using (var options = await adminBrowser.PostAsync("/auth/login/passkey/options"))
        {
            var credential = adminAuthenticator.Get(await TestBrowser.JsonAsync(options));
            using var login = await adminBrowser.PostAsync("/auth/login/passkey", new { credential });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            adminBrowser.CsrfToken = (await TestBrowser.JsonAsync(login)).GetProperty("csrf_token").GetString();
        }

        var adminKey = System.Buffers.Text.Base64Url.EncodeToString(adminAuthenticator.CredentialId);
        using (var denied = await adminBrowser.PostAsync($"/auth/account/passkeys/{adminKey}/delete"))
        {
            Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
            Assert.Equal("urn:jinji:problem:last-factor", (await TestBrowser.JsonAsync(denied)).GetProperty("type").GetString());
        }

        Assert.Single((await TestBrowser.JsonAsync(await adminBrowser.GetAsync("/auth/account/passkeys"))).EnumerateArray());
        Assert.True(await db.CountAuditAsync("auth.passkey.delete", "success", Ct) >= 1);
        Assert.True(await db.CountAuditAsync("auth.passkey.delete", "denied", Ct) >= 1);

        // 管理の一覧ではパスキーの数が見える
        Assert.Contains("\t1\t", (await db.CliAsync("list-local-accounts", "--query", "mgmt-rita")).Output, StringComparison.Ordinal);
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    /// <summary>ローカルアカウントを作り、リンクでパスワードを設定する。アカウント ID を返す。</summary>
    private async Task<Guid> CreatePasswordAccountAsync(Guid person, string userName)
    {
        var (exit, output) = await db.CliAsync("create-local-account", "--person", person.ToString(), "--user-name", userName);
        Assert.True(exit == 0, output);
        using var browser = await LocalBrowserAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync("/auth/reset/password", new { token = AuthFixtureDatabase.LinkToken(output), new_password = Password })).StatusCode);
        return Guid.Parse(AuthFixtureDatabase.Field(output, "account_id"));
    }

    private async Task<TestBrowser> LocalBrowserAsync()
    {
        var browser = new TestBrowser(_env.Host.ApiPort) { Origin = AuthTestHost.Origin };
        await browser.RefreshCsrfAsync();
        return browser;
    }

    private static Task<HttpResponseMessage> LoginRawAsync(TestBrowser browser, string userName, string password) =>
        browser.PostAsync("/auth/login/password", new { user_name = userName, password });

    private static async Task RegisterPasskeyAsync(TestBrowser browser, TestAuthenticator authenticator, string optionsUrl, string completeUrl, object optionsBody)
    {
        using var options = await browser.PostAsync(optionsUrl, optionsBody);
        Assert.Equal(HttpStatusCode.OK, options.StatusCode);
        var credential = authenticator.Create(await TestBrowser.JsonAsync(options));
        var payload = JsonSerializer.SerializeToElement(optionsBody).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value);
        payload["credential"] = credential;
        payload["name"] = "テストの認証器";
        using var complete = await browser.PostAsync(completeUrl, payload);
        Assert.True(complete.StatusCode == HttpStatusCode.NoContent, await complete.Content.ReadAsStringAsync(Ct));
    }
}
