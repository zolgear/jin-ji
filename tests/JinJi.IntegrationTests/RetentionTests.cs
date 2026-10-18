using System.Diagnostics.Metrics;
using JinJi.Application.Access;
using JinJi.Application.Retention;
using JinJi.Domain;
using JinJi.Host;
using JinJi.Host.Web;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace JinJi.IntegrationTests;

/// <summary>
/// 保持期限の消去（0014・<see cref="RetentionService"/>）。終わった行だけを、保持期間の後に、参照の順に消す。生きている系列と期間内の行は残す。
/// </summary>
public sealed class RetentionTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    [Fact(DisplayName = "消去: 保持期間を過ぎた終わった行（セッション・未紐付けのログイン・リンク・認可の要求・コード・トークン・系列・同意・jti・承認待ちの CIMD）を消し、件数を記録する")]
    public async Task Purge_DeletesOnlyFinishedRowsPastRetention()
    {
        var seed = await SeedAsync();

        var (exit, output) = await PurgeAsync();
        Assert.True(exit == 0, output);
        var counts = Counts(output);
        Assert.Equal(2, counts["oauth_access_token"]);
        Assert.Equal(2, counts["oauth_refresh_token"]);
        Assert.Equal(1, counts["oauth_authorization_code"]);
        Assert.Equal(1, counts["oauth_token_family"]);
        Assert.Equal(1, counts["oauth_consent"]);
        Assert.Equal(2, counts["oauth_authorization_request"]);
        Assert.Equal(1, counts["oauth_client_assertion"]);
        Assert.Equal(1, counts["pending_client"]);
        Assert.Equal(1, counts["ui_session"]);
        Assert.Equal(1, counts["unbound_login"]);
        Assert.Equal(1, counts["account_reset_link"]);
        Assert.Equal(0, counts["staging_row"]);
        Assert.Equal(14, counts["total"]);

        // 残る行: 生きている系列（期限を過ぎたコード・要求・同意も）、期間内のトークン・要求・セッション・未紐付けのログイン・リンク、却下した CIMD
        Assert.Equal([seed.LiveFamily], await db.QueryAsSystemAsync<Guid>("SELECT id FROM oauth_token_family WHERE client_ref = $1", Ct, seed.Client));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM oauth_authorization_code WHERE family_id = $1", seed.LiveFamily));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM oauth_consent WHERE client_ref = $1", seed.Client));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM oauth_authorization_request WHERE client_ref = $1", seed.Client));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM oauth_access_token WHERE client_ref = $1", seed.Client));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM oauth_refresh_token WHERE family_id = $1", seed.LiveFamily));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM oauth_client_assertion WHERE client_ref = $1", seed.Client));
        Assert.Equal(
            ["retention-pending-new", "retention-rejected"],
            (await db.QueryAsSystemAsync<string>("SELECT display_name FROM api_client WHERE client_id LIKE 'https://pending.example/%' ORDER BY display_name", Ct)));
        Assert.Equal(2L, await CountAsync("SELECT count(*) FROM ui_session WHERE login_binding_id = $1", seed.Binding));
        Assert.Equal(["retention-recent"], await db.QueryAsSystemAsync<string>("SELECT subject FROM unbound_login WHERE subject LIKE 'retention-%'", Ct));
        Assert.Equal(1L, await CountAsync("SELECT count(*) FROM account_reset_link WHERE local_account_id = $1", seed.Account));

        // 2 回目は何も消さない。実行ごとに retention_run と監査ログ（retention.purge）に残る
        (exit, output) = await PurgeAsync();
        Assert.Equal(0, exit);
        Assert.Equal(0, Counts(output)["total"]);
        var runs = await db.QueryAsAppAsync<int>(F.HrAdmin(), "SELECT oauth_access_token FROM retention_run WHERE trigger = 'cli' ORDER BY started_at", Ct);
        Assert.Equal([2, 0], runs);
        Assert.Equal(
            [2L],
            await db.QueryAsAppAsync<long>(F.HrAdmin(), "SELECT count(*) FROM audit_event WHERE action = 'retention.purge' AND channel = 'cli' AND outcome = 'success' AND actor_type = 'system'", Ct));
        Assert.Equal(
            ["retention_run"],
            await db.QueryAsAppAsync<string>(F.HrAdmin(), "SELECT DISTINCT target_type FROM audit_event WHERE action = 'retention.purge'", Ct));

        // 監査ログ自体は消さない
        Assert.True(await db.CountAuditAsync(RetentionService.AuditAction, null, Ct) >= 2);
    }

    [Fact(DisplayName = "ワーカー: 定期実行が保持期限の消去を動かし、retention_run に記録する（JinJi:Retention:Enabled=false なら動かない）")]
    public async Task Worker_RunsPeriodically()
    {
        var before = (await db.QueryAsAppAsync<long>(F.HrAdmin(), "SELECT count(*) FROM retention_run WHERE trigger = 'worker'", Ct)).Single();
        using (var host = ServerHost.BuildWorkerOnly(WorkerArgs("--JinJi:Retention:Enabled=false")))
        {
            await host.StartAsync(Ct);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            await host.StopAsync(Ct);
        }

        Assert.Equal(before, (await db.QueryAsAppAsync<long>(F.HrAdmin(), "SELECT count(*) FROM retention_run WHERE trigger = 'worker'", Ct)).Single());

        using (var host = ServerHost.BuildWorkerOnly(WorkerArgs()))
        {
            await host.StartAsync(Ct);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while ((await db.QueryAsAppAsync<long>(F.HrAdmin(), "SELECT count(*) FROM retention_run WHERE trigger = 'worker'", Ct)).Single() == before)
            {
                Assert.True(DateTime.UtcNow < deadline, "ワーカーの消去が 30 秒以内に動きませんでした。");
                await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
            }

            await host.StopAsync(Ct);
        }

        Assert.True(await db.CountAuditAsync(RetentionService.AuditAction, "success", Ct, "system") >= 1);
    }

    [Fact(DisplayName = "メトリクス: 消した行数を表別に、実行回数を契機・結果別に Prometheus 形式で出す")]
    public void Metrics_AreExported()
    {
        var services = new ServiceCollection().AddMetrics().AddSingleton<AccessMetrics>().AddSingleton<RetentionMetrics>().AddSingleton<PrometheusMetrics>();
        using var provider = services.BuildServiceProvider();
        var exporter = provider.GetRequiredService<PrometheusMetrics>();
        provider.GetRequiredService<RetentionMetrics>().Record(
            new RetentionResult(EntityId.New(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, true, new Dictionary<RetentionTarget, int>
            {
                [RetentionTarget.UiSession] = 3,
                [RetentionTarget.AccessToken] = 0,
            }),
            Channel.Worker);

        var text = exporter.Render();
        Assert.Contains("jinji_retention_deleted_total{table=\"ui_session\"} 3", text, StringComparison.Ordinal);
        Assert.DoesNotContain("table=\"oauth_access_token\"", text, StringComparison.Ordinal);
        Assert.Contains("jinji_retention_runs_total{outcome=\"success\",trigger=\"worker\"} 1", text, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "設定: 保持期間は 1 日〜3660 日、間隔は 1 分〜1 日、1 回の行数は 100〜100,000 だけを受け付ける")]
    [InlineData("UiSessions", "12:00:00")]
    [InlineData("OAuthTokens", "3661.00:00:00")]
    [InlineData("Interval", "00:00:30")]
    [InlineData("BatchSize", "10")]
    public void Options_RejectOutOfRange(string name, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [$"JinJi:Retention:{name}"] = value }).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => ApplicationSettings.Retention(configuration));
        Assert.Equal(TimeSpan.FromDays(30), ApplicationSettings.Retention(new ConfigurationBuilder().Build()).UiSessions);
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private sealed record Seed(Guid Client, Guid LiveFamily, Guid Binding, Guid Account);

    /// <summary>消える行と残る行を 1 組ずつ作る（時刻は DB の now() からの相対）。</summary>
    private async Task<Seed> SeedAsync()
    {
        var person = F.Person("u2_member");
        var client = EntityId.New();
        string Hash() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await ExecAsync(
            """
            INSERT INTO api_client (id, client_id, registration, status, auth_method, grant_types, redirect_uris, resources, scopes, fields, created_by_kind)
            VALUES ($1, 'retention-app', 'registered', 'active', 'none', '{authorization_code,refresh_token}', '{http://127.0.0.1/cb}', '{mcp}', '{jinji.read}', '{*}', 'cli')
            """,
            client);

        // 要求（決定済みなら同意とコードも）を作る。ago は要求の作成からの経過
        async Task<Guid> RequestAsync(string ago, bool decided)
        {
            var request = EntityId.New();
            await ExecAsync(
                $$"""
                INSERT INTO oauth_authorization_request (id, handle_sha256, client_ref, redirect_uri, resource, scopes, code_challenge, created_at, expires_at, decided_at, decision, person_id)
                VALUES ($1, $2, $3, 'http://127.0.0.1/cb', 'mcp', '{jinji.read}', repeat('A', 43), now() - interval '{{ago}}', now() - interval '{{ago}}' + interval '10 minutes',
                        {{(decided ? $"now() - interval '{ago}', 'approved', $4" : "NULL, NULL, NULL")}})
                """,
                [request, Hash(), client, .. decided ? new object[] { person } : []]);
            return request;
        }

        async Task<Guid> FamilyAsync(string createdAgo, string expiresIn, Guid request)
        {
            var family = EntityId.New();
            await ExecAsync(
                $"INSERT INTO oauth_token_family (id, client_ref, person_id, resource, scopes, created_at, expires_at) VALUES ($1, $2, $3, 'mcp', '{{jinji.read}}', now() - interval '{createdAgo}', now() + interval '{expiresIn}')",
                family, client, person);
            await ExecAsync(
                $"INSERT INTO oauth_consent (id, request_id, client_ref, person_id, redirect_uri, resource, scopes, granted_at) VALUES ($1, $2, $3, $4, 'http://127.0.0.1/cb', 'mcp', '{{jinji.read}}', now() - interval '{createdAgo}')",
                EntityId.New(), request, client, person);
            await ExecAsync(
                $$"""
                INSERT INTO oauth_authorization_code (id, code_sha256, request_id, client_ref, person_id, redirect_uri, resource, scopes, code_challenge, created_at, expires_at, used_at, family_id)
                VALUES ($1, $2, $3, $4, $5, 'http://127.0.0.1/cb', 'mcp', '{jinji.read}', repeat('A', 43), now() - interval '{{createdAgo}}', now() - interval '{{createdAgo}}' + interval '60 seconds', now() - interval '{{createdAgo}}', $6)
                """,
                EntityId.New(), Hash(), request, client, person, family);
            return family;
        }

        async Task AccessTokenAsync(Guid family, string issuedAgo) =>
            await ExecAsync(
                $"INSERT INTO oauth_access_token (id, token_sha256, family_id, client_ref, person_id, resource, scopes, issued_at, expires_at) VALUES ($1, $2, $3, $4, $5, 'mcp', '{{jinji.read}}', now() - interval '{issuedAgo}', now() - interval '{issuedAgo}' + interval '10 minutes')",
                EntityId.New(), Hash(), family, client, person);

        async Task RefreshTokenAsync(Guid family, string issuedAgo) =>
            await ExecAsync(
                $"INSERT INTO oauth_refresh_token (id, token_sha256, family_id, issued_at, expires_at, used_at) VALUES ($1, $2, $3, now() - interval '{issuedAgo}', now() - interval '{issuedAgo}' + interval '7 days', now() - interval '{issuedAgo}')",
                EntityId.New(), Hash(), family);

        // 消える系列: 70 日前に付与し、40 日前に期限切れ（トークン・コード・同意・要求も消える）
        var old = await FamilyAsync("70 days", "-40 days", await RequestAsync("70 days", decided: true));
        await AccessTokenAsync(old, "70 days");
        await RefreshTokenAsync(old, "70 days");

        // 残る系列: 期限内。期限を過ぎたコード・要求・同意は残り、7 日を過ぎて期限切れのトークンだけが消える
        var live = await FamilyAsync("20 days", "10 days", await RequestAsync("20 days", decided: true));
        await AccessTokenAsync(live, "10 days");
        await AccessTokenAsync(live, "5 minutes");
        await RefreshTokenAsync(live, "20 days");
        await RefreshTokenAsync(live, "10 days");

        // 未決定の要求: 期限から 7 日を過ぎたものだけ
        await RequestAsync("9 days", decided: false);
        await RequestAsync("1 day", decided: false);

        // jti: 期限を過ぎたものだけ
        await ExecAsync("INSERT INTO oauth_client_assertion (client_ref, jti, expires_at) VALUES ($1, 'old', now() - interval '1 minute'), ($1, 'new', now() + interval '5 minutes')", client);

        // CIMD: 承認待ちのまま 30 日を過ぎたものだけ（却下したものは残す）
        foreach (var (name, status, ago) in new[] { ("retention-pending-old", "pending_approval", "40 days"), ("retention-pending-new", "pending_approval", "10 days"), ("retention-rejected", "rejected", "40 days") })
        {
            await ExecAsync(
                $$"""
                INSERT INTO api_client (id, client_id, registration, status, display_name, auth_method, grant_types, resources, scopes, fields, created_by_kind, created_at, updated_at)
                VALUES ($1, $2, 'cimd', '{{status}}', $3, 'none', '{authorization_code}', '{mcp}', '{jinji.read}', '{*}', 'cimd', now() - interval '{{ago}}', now() - interval '{{ago}}')
                """,
                EntityId.New(), $"https://pending.example/{name}", name);
        }

        // セッション: 終了から 30 日を過ぎたものだけ（10 日前に終わったもの・生きているものは残す）
        var binding = EntityId.New();
        await ExecAsync(
            "INSERT INTO login_binding (id, provider, issuer, subject, person_id, basis, created_by_kind) VALUES ($1, 'google', 'https://idp.retention.test', $2, $3, 'manual', 'cli')",
            binding, Guid.NewGuid().ToString("N"), person);
        foreach (var (ago, ended) in new[] { ("41 days", true), ("11 days", true), ("1 minute", false) })
        {
            var id = EntityId.New();
            await ExecAsync(
                $$"""
                INSERT INTO ui_session (id, key_sha256, principal_kind, person_id, login_binding_id, provider, methods, created_at, last_seen_at, expires_at, ended_at, end_kind)
                VALUES ($1, $2, 'person', $3, $4, 'google', '{fed}', now() - interval '{{ago}}', now() - interval '{{ago}}', now() - interval '{{ago}}' + interval '12 hours',
                        {{(ended ? $"now() - interval '{ago}' + interval '1 hour', 'logout'" : "NULL, NULL")}})
                """,
                id, Hash(), person, binding);
        }

        // 未紐付けのログイン: 最後の試行から 90 日を過ぎたものだけ
        foreach (var (subject, ago) in new[] { ("retention-old", "100 days"), ("retention-recent", "10 days") })
        {
            await ExecAsync(
                $"INSERT INTO unbound_login (id, provider, issuer, subject, first_attempted_at, last_attempted_at) VALUES ($1, 'google', 'https://idp.retention.test', $2, now() - interval '{ago}', now() - interval '{ago}')",
                EntityId.New(), subject);
        }

        // リンク: 期限から 30 日を過ぎたものだけ（管理 CLI が今発行したものは残す）
        var (exit, output) = await db.CliAsync("create-local-account", "--person", F.Person("u3_member").ToString(), "--user-name", "retention-sam");
        Assert.True(exit == 0, output);
        var account = Guid.Parse(AuthFixtureDatabase.Field(output, "account_id"));
        await ExecAsync(
            "INSERT INTO account_reset_link (id, local_account_id, purpose, token_sha256, issued_by_kind, issued_at, expires_at) VALUES ($1, $2, 'password', $3, 'cli', now() - interval '41 days', now() - interval '41 days' + interval '1 hour')",
            EntityId.New(), account, Hash());

        return new Seed(client, live, binding, account);
    }

    private async Task<(int Exit, string Output)> PurgeAsync()
    {
        using var output = new StringWriter();
        var exit = await RetentionCommand.RunAsync(
            [$"--ConnectionStrings:App={db.Database.AppConnectionString}", "--Logging:LogLevel:Default=Warning"],
            output);
        return (exit, output.ToString());
    }

    private string[] WorkerArgs(params string[] extra) =>
    [
        $"--ConnectionStrings:App={db.Database.AppConnectionString}",
        "--Logging:LogLevel:Default=Warning",
        "--JinJi:Retention:InitialDelay=00:00:00",
        .. extra,
    ];

    private static Dictionary<string, int> Counts(string output) =>
        output.Split('\n').Select(l => l.TrimEnd('\r').Split(": ", 2)).Where(p => p.Length == 2 && int.TryParse(p[1], out _))
            .ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);

    private async Task<long> CountAsync(string sql, params object[] parameters) =>
        (await db.QueryAsSystemAsync<long>(sql, Ct, parameters)).Single();

    private async Task ExecAsync(string sql, params object[] parameters)
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.SetSessionViewerAsync(connection, "system", string.Empty, null, Ct);
        await SampleData.ExecuteAsync(connection, sql, Ct, parameters);
    }
}
