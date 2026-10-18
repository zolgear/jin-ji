using System.Net;
using System.Text.Json;
using JinJi.Application.Auth;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests.Api;

/// <summary>
/// 未来日付の変更の先行提供（社内システムのクライアントごとの例外。0016、§4「将来の予定の秘匿」・§9「先行提供」）:
/// 起案と別人の承認・承認なしでは効かない・日数の境界（N 日前の前日は見えない）・RLS とアプリの一致・人の委任には効かない・変更フィードと取り消しの伝播。
/// </summary>
public sealed class DisclosureLeadTests(RouteFixtureDatabase db) : IClassFixture<RouteFixtureDatabase>, IAsyncLifetime
{
    private const string Loopback = "http://127.0.0.1:43132/callback";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(15);
    private OAuthTestEnvironment _env = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _env = await OAuthTestEnvironment.StartAsync(db);

    public async ValueTask DisposeAsync() => await _env.DisposeAsync();

    [Fact(DisplayName = "先行提供: CLI で起案し、別の人事管理者が UI で承認して初めて効く。起案者は承認できず、承認待ちの重ねての起案・公開クライアントへの起案は拒否。監査に残る")]
    public async Task ProposeAndApprove_WithSegregationOfDuties()
    {
        using var key = await RegisterServiceAsync("wf-approve");
        var token = await ServiceTokenAsync("wf-approve", key);
        var future = RouteFixtureDatabase.Date(db.ReorgDate);

        var (exit, output) = await _env.CliAsync("propose-disclosure-lead", "--client-id", "wf-approve", "--days", "20", "--proposed-by", F.Person("hr_admin").ToString(), "--note", "4/1 の組織改編の事前登録");
        Assert.True(exit == 0, output);
        var proposal = AuthFixtureDatabase.Field(output, "proposal_id");

        // 承認されるまでは効かない
        Assert.Equal(HttpStatusCode.Forbidden, (await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}/approval-route?as_of={future}", token)).Status);
        Assert.Equal(1, (await _env.CliAsync("propose-disclosure-lead", "--client-id", "wf-approve", "--days", "30", "--proposed-by", F.Person("hr_admin").ToString())).Exit);
        // 起案者は人事管理者でなければならない（管理 CLI の起案者の指定）
        Assert.Equal(1, (await _env.CliAsync("propose-disclosure-lead", "--client-id", "wf-approve", "--days", "5", "--proposed-by", F.Person("u2_member").ToString())).Exit);

        // 起案者本人は承認・却下できない（職務分掌）
        using (var proposer = await _env.LoginAsync(F.Person("hr_admin")))
        {
            using var denied = await proposer.PostAsync($"/auth/admin/oauth/disclosure-leads/{proposal}/approve");
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using (var approver = await _env.LoginAsync(F.Person("hr_head")))
        {
            using var approved = await approver.PostAsync($"/auth/admin/oauth/disclosure-leads/{proposal}/approve");
            Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
            var body = await TestBrowser.JsonAsync(approved);
            Assert.Equal(("approved", 20, F.Person("hr_head")), (body.GetProperty("status").GetString(), body.GetProperty("lead_days").GetInt32(), body.GetProperty("decided_by").GetGuid()));

            using var clients = await approver.GetAsync("/auth/admin/oauth/clients");
            var client = (await TestBrowser.JsonAsync(clients)).EnumerateArray().Single(c => c.GetProperty("client_id").GetString() == "wf-approve");
            Assert.Equal(20, client.GetProperty("disclosure_lead_days").GetInt32());

            using var again = await approver.PostAsync($"/auth/admin/oauth/disclosure-leads/{proposal}/reject");
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }

        var (status, route) = await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}/approval-route?as_of={future}", token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(db.NewUnit, route.GetProperty("start").GetProperty("org_unit").GetProperty("id").GetGuid());
        Assert.Equal(F.Person("dp2_member"), route.GetProperty("steps")[0].GetProperty("approver").GetProperty("id").GetGuid());

        var (_, meta) = await _env.Host.GetJsonAsync("/api/v1/meta", token);
        Assert.True(meta.GetProperty("can_view_future").GetBoolean());
        Assert.Equal(future, meta.GetProperty("max_as_of").GetString());
        Assert.Contains(meta.GetProperty("milestones").EnumerateArray(), m => m.GetProperty("date").GetString() == future);

        // 公開クライアント（人の委任だけ）には起案できない
        await _env.RegisterPublicClientAsync("wf-public", Loopback);
        Assert.Equal(1, (await _env.CliAsync("propose-disclosure-lead", "--client-id", "wf-public", "--days", "10", "--proposed-by", F.Person("hr_admin").ToString())).Exit);

        var (listExit, list) = await _env.CliAsync("list-disclosure-leads", "--client-id", "wf-approve");
        Assert.True(listExit == 0, list);
        Assert.Contains("\tapproved\t", list, StringComparison.Ordinal);
        // 監査（他のテストも同じデータベースで起案・承認するので、件数は下限で確かめる）
        Assert.True(await db.CountAuditAsync(OAuthAuditActions.DisclosureLeadPropose, "success", Ct) >= 1);
        Assert.True(await db.CountAuditAsync(OAuthAuditActions.DisclosureLeadApprove, "success", Ct) >= 1);
        Assert.True(await db.CountAuditAsync(OAuthAuditActions.DisclosureLeadApprove, "denied", Ct) >= 1);
    }

    [Fact(DisplayName = "先行提供: 日数の境界。N 日の社内システムは N 日後まで指定でき、N+1 日後は 403。有効日の N 日前の前日（N-1 日）には予定の行が RLS でも見えない")]
    public async Task LeadDays_Boundary_RlsAndAppAgree()
    {
        using var key19 = await RegisterServiceAsync("wf-19");
        using var key20 = await RegisterServiceAsync("wf-20");
        var ref19 = await ApproveLeadAsync("wf-19", 19);
        var ref20 = await ApproveLeadAsync("wf-20", 20);
        var token19 = await ServiceTokenAsync("wf-19", key19);
        var token20 = await ServiceTokenAsync("wf-20", key20);
        var day19 = RouteFixtureDatabase.Date(F.Today.AddDays(19));
        var day20 = RouteFixtureDatabase.Date(db.ReorgDate);
        var day21 = RouteFixtureDatabase.Date(F.Today.AddDays(21));

        // アプリ: 指定できる時点
        Assert.Equal(HttpStatusCode.OK, (await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}?as_of={day19}", token19)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}?as_of={day20}", token19)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _env.Host.GetJsonAsync($"/api/v1/orgs/{db.NewUnit}?as_of={day20}", token20)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _env.Host.GetJsonAsync($"/api/v1/orgs/{db.NewUnit}?as_of={day21}", token20)).Status);

        // 19 日の閲覧者の 19 日後の時点: まだ組織改編の前（mover は U3、NEW は無い）。発令（項目の公開範囲で社内システムには見えない）は下の RLS で確かめる
        var (_, before) = await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}?as_of={day19}", token19);
        Assert.Equal(F.Unit("U3"), Assert.Single(before.GetProperty("assignments").EnumerateArray()).GetProperty("org_unit_id").GetGuid());
        // 将来の終了日（20 日後の異動で終わる U3 の所属）は、その時点からも見えない（19 日後から見て将来）
        Assert.Equal(JsonValueKind.Null, before.GetProperty("assignments")[0].GetProperty("valid_until").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await _env.Host.GetJsonAsync($"/api/v1/orgs/{db.NewUnit}?as_of={day19}", token19)).Status);

        // 20 日の閲覧者の 20 日後の時点: 組織改編の後
        var (_, after) = await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}?as_of={day20}", token20);
        Assert.Equal(db.NewUnit, Assert.Single(after.GetProperty("assignments").EnumerateArray()).GetProperty("org_unit_id").GetGuid());

        // 今日の時点は先行提供の有無で変わらない（将来の終了日は見えない）
        var (_, today) = await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}", token20);
        Assert.Equal(JsonValueKind.Null, today.GetProperty("assignments")[0].GetProperty("valid_until").ValueKind);

        // RLS: 同じ日数で同じ線（DB は承認済みの行から日数を読む）
        const string NewUnitRows = "SELECT count(*) FROM org_unit_version WHERE org_unit_id = $1";
        Assert.Equal(0L, Assert.Single(await db.QueryAsAppAsync<long>(Viewer("wf-19", ref19, 19), NewUnitRows, Ct, db.NewUnit)));
        Assert.Equal(1L, Assert.Single(await db.QueryAsAppAsync<long>(Viewer("wf-20", ref20, 20), NewUnitRows, Ct, db.NewUnit)));
        // 先行提供を外した閲覧者・日数を偽った閲覧者（DB は承認済みの日数を使う）
        Assert.Equal(0L, Assert.Single(await db.QueryAsAppAsync<long>(Viewer("wf-20", ref20, 20).WithoutDisclosureLead(), NewUnitRows, Ct, db.NewUnit)));
        Assert.Equal(0L, Assert.Single(await db.QueryAsAppAsync<long>(Viewer("wf-19", ref19, 90), NewUnitRows, Ct, db.NewUnit)));
        const string FutureActions = "SELECT count(*) FROM personnel_action WHERE person_id = $1 AND action_type = 'transfer'";
        Assert.Equal(0L, Assert.Single(await db.QueryAsAppAsync<long>(Viewer("wf-19", ref19, 19), FutureActions, Ct, db.Mover)));
        Assert.Equal(1L, Assert.Single(await db.QueryAsAppAsync<long>(Viewer("wf-20", ref20, 20), FutureActions, Ct, db.Mover)));
        // AI エージェント・人には GUC を設定しても効かない（社内システムの種別だけ）
        var agent = ViewerContext.ForAgent("wf-20", F.Person("u2_member"), [], Channel.Mcp, FieldLimit.Unrestricted);
        Assert.Equal(0L, Assert.Single(await db.QueryAsAppAsync<long>(agent, NewUnitRows, Ct, db.NewUnit)));
    }

    [Fact(DisplayName = "先行提供: 人の委任（同じクライアントの認可コードのトークン）には効かない。client credentials のトークンだけが未来を見られる")]
    public async Task DelegatedTokens_AreNotAffected()
    {
        var (exit, output) = await _env.CliAsync(
            "register-client", "--client-id", "wf-both", "--name", "ワークフロー", "--auth", "private_key_jwt", "--grant-types", "authorization_code,client_credentials",
            "--redirect-uris", Loopback, "--resources", "api", "--scopes", "jinji.read", "--fields", "*");
        Assert.True(exit == 0, output);
        using var key = await AddKeyAsync("wf-both");
        await ApproveLeadAsync("wf-both", 30);
        var future = RouteFixtureDatabase.Date(db.ReorgDate);

        var service = await ServiceTokenAsync("wf-both", key);
        Assert.Equal(HttpStatusCode.OK, (await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}/approval-route?as_of={future}", service)).Status);

        using var browser = await _env.LoginAsync(F.Person("u2_member"));
        var pkce = Pkce.New();
        var code = await _env.CodeAsync(browser, "wf-both", Loopback, pkce, OAuthTestEnvironment.ApiResource);
        var delegated = await _env.TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = Loopback,
            ["code_verifier"] = pkce.Verifier,
            ["resource"] = OAuthTestEnvironment.ApiResource,
            ["client_assertion_type"] = AuthorizationServer.JwtBearerAssertionType,
            ["client_assertion"] = key.Assertion("wf-both"),
        });
        Assert.Equal(HttpStatusCode.OK, delegated.Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _env.Host.GetJsonAsync($"/api/v1/people/{db.Mover}/approval-route?as_of={future}", delegated.AccessToken)).Status);
        var (_, meta) = await _env.Host.GetJsonAsync("/api/v1/meta", delegated.AccessToken);
        Assert.False(meta.GetProperty("can_view_future").GetBoolean());
        Assert.Equal(RouteFixtureDatabase.Date(F.Today), meta.GetProperty("max_as_of").GetString());
    }

    [Fact(DisplayName = "先行提供: 変更フィードは日数だけ早く届く（初回の読み出しで上限までの予定も届く）。予定の取り消しは先行提供のクライアントにだけ届き、日数 0 の社内システムには届かない")]
    public async Task ChangeFeed_LeadAndWithdrawal()
    {
        using var key = await RegisterServiceAsync("wf-feed");
        await ApproveLeadAsync("wf-feed", 30);
        var lead = await ServiceTokenAsync("wf-feed", key);
        var plain = _env.JwtIssuer.Service("sync");
        var leadCursor = await StartAsync(lead);
        var plainCursor = await StartAsync(plain);

        // 初回: 上限（30 日後）までの既存の予定（mover の異動・NEW の新設）が届く
        var (seen, leadNext) = await PollUntilAsync(lead, leadCursor, ids => ids.Contains(db.Mover) && ids.Contains(db.NewUnit));
        Assert.Contains(db.NewUnit, seen);

        // 10 日後の兼務の予定を足す: 先行提供のクライアントにはすぐ届き、日数 0 の社内システムには届かない
        Guid planned;
        await using (var connection = await OpenAsHrAdminAsync())
        {
            planned = await SampleData.InsertAssignmentAsync(connection, F.Person("s2_member"), F.Unit("S3"), "concurrent", $"[{RouteFixtureDatabase.Date(F.Today.AddDays(10))},)", Ct);
        }

        (_, leadNext) = await PollUntilAsync(lead, leadNext, ids => ids.Contains(F.Person("s2_member")));
        var plainSeen = await PollWithMarkerAsync(plain, plainCursor);
        Assert.DoesNotContain(F.Person("s2_member"), plainSeen.Seen);
        plainCursor = plainSeen.Next;

        // 予定の取り消し（置き換えの無い記録期間の終了）: 先行提供のクライアントに届く。日数 0 の社内システムには届かない
        await using (var connection = await OpenAsHrAdminAsync())
        {
            await SampleData.ExecuteAsync(connection, "UPDATE assignment SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE id = $1", Ct, planned);
        }

        await PollUntilAsync(lead, leadNext, ids => ids.Contains(F.Person("s2_member")));
        Assert.DoesNotContain(F.Person("s2_member"), (await PollWithMarkerAsync(plain, plainCursor)).Seen);

        // RLS: 取り消しの記録は日数 0 の社内システムには見えない（見せてよい日が来ても）
        const string Withdrawals = "SELECT count(*) FROM change_event WHERE entity_id = $1 AND withdrawn";
        Assert.Equal(0L, Assert.Single(await db.QueryAsAppAsync<long>(ViewerContext.ForService("sync", FieldLimit.Unrestricted), Withdrawals, Ct, F.Person("s2_member"))));
        Assert.Equal(1L, Assert.Single(await db.QueryAsAppAsync<long>(db.Fixture.HrAdmin(), Withdrawals, Ct, F.Person("s2_member"))));
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    /// <summary>先行提供の日数を、hr_admin が UI で起案し hr_head が承認する。api_client.id を返す。</summary>
    private async Task<Guid> ApproveLeadAsync(string clientId, int days)
    {
        using var proposer = await _env.LoginAsync(F.Person("hr_admin"));
        using var clients = await proposer.GetAsync("/auth/admin/oauth/clients");
        var clientRef = (await TestBrowser.JsonAsync(clients)).EnumerateArray().Single(c => c.GetProperty("client_id").GetString() == clientId).GetProperty("id").GetGuid();
        using var proposed = await proposer.PostAsync($"/auth/admin/oauth/clients/{clientRef}/disclosure-leads", new { lead_days = days, note = "テスト" });
        Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
        var id = (await TestBrowser.JsonAsync(proposed)).GetProperty("id").GetGuid();

        using var approver = await _env.LoginAsync(F.Person("hr_head"));
        using var approved = await approver.PostAsync($"/auth/admin/oauth/disclosure-leads/{id}/approve");
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        return clientRef;
    }

    private static ViewerContext Viewer(string clientId, Guid clientRef, int days) =>
        ViewerContext.ForService(clientId, FieldLimit.Unrestricted, null, clientRef, days);

    /// <summary>社内システム（client credentials、private_key_jwt、項目の上限なし）を登録し、鍵を返す。</summary>
    private async Task<ClientKey> RegisterServiceAsync(string clientId)
    {
        var (exit, output) = await _env.CliAsync(
            "register-client", "--client-id", clientId, "--name", "社内システム", "--auth", "private_key_jwt", "--grant-types", "client_credentials",
            "--resources", "api", "--scopes", "jinji.read", "--fields", "*");
        Assert.True(exit == 0, output);
        return await AddKeyAsync(clientId);
    }

    private async Task<ClientKey> AddKeyAsync(string clientId)
    {
        var key = new ClientKey();
        var file = Path.Combine(Path.GetTempPath(), $"jinji-test-{Guid.NewGuid():N}.pem");
        try
        {
            await File.WriteAllTextAsync(file, key.PublicKeyPem, Ct);
            var (exit, output) = await _env.CliAsync("add-client-key", "--client-id", clientId, "--kid", key.KeyId, "--key-file", file);
            Assert.True(exit == 0, output);
        }
        finally
        {
            File.Delete(file);
        }

        return key;
    }

    private async Task<string> ServiceTokenAsync(string clientId, ClientKey key)
    {
        var response = await _env.TokenAsync(new()
        {
            ["grant_type"] = "client_credentials",
            ["client_assertion_type"] = AuthorizationServer.JwtBearerAssertionType,
            ["client_assertion"] = key.Assertion(clientId),
        });
        Assert.Equal(HttpStatusCode.OK, response.Status);
        return response.AccessToken;
    }

    private async Task<Npgsql.NpgsqlConnection> OpenAsHrAdminAsync()
    {
        var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(connection, Ct);
        return connection;
    }

    private async Task<string> StartAsync(string token)
    {
        var (status, body) = await _env.Host.GetJsonAsync("/api/v1/changes", token);
        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("next_cursor").GetString()!;
    }

    /// <summary>
    /// 今日有効な変更（目印）を足し、それが届くまで読む（目印より前にコミットされた変更は、届くならそれまでに届く）。読んだ ID と次の位置を返す。
    /// </summary>
    private async Task<(List<Guid> Seen, string Next)> PollWithMarkerAsync(string token, string cursor)
    {
        var marker = F.Person("u3_member");
        await using (var connection = await OpenAsHrAdminAsync())
        {
            await SampleData.InsertAssignmentAsync(connection, marker, F.Unit("R1"), "concurrent", $"[{RouteFixtureDatabase.Date(F.Today)},)", Ct);
        }

        return await PollUntilAsync(token, cursor, ids => ids.Contains(marker));
    }

    /// <summary>条件を満たすまでフィードを読み進める（位置は実行中のトランザクションが終わるまで進まないので、待って読み直す）。</summary>
    private async Task<(List<Guid> Seen, string Next)> PollUntilAsync(string token, string cursor, Func<IReadOnlyCollection<Guid>, bool> done)
    {
        var seen = new List<Guid>();
        var deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            var (status, page) = await _env.Host.GetJsonAsync($"/api/v1/changes?since={Uri.EscapeDataString(cursor)}&limit=50", token);
            Assert.Equal(HttpStatusCode.OK, status);
            seen.AddRange(ApiEndpointTests.Ids(page.GetProperty("items")));
            cursor = page.GetProperty("next_cursor").GetString()!;
            if (done(seen))
            {
                return (seen, cursor);
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("変更フィードに期待した変更が現れませんでした。");
            }

            if (!page.GetProperty("has_more").GetBoolean())
            {
                await Task.Delay(200, Ct);
            }
        }
    }
}
