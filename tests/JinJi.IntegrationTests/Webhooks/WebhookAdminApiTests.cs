using System.Net;
using System.Text.Json;
using JinJi.Application.Access;
using JinJi.Application.Webhooks;
using JinJi.Host;
using JinJi.Host.Web;
using JinJi.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace JinJi.IntegrationTests.Webhooks;

/// <summary>
/// Webhook の管理の API（<c>/auth/admin/oauth/webhooks</c>）: UI でログインした人事管理者だけ・鍵は登録とローテーションの応答でだけ返る・
/// 送信先の検査・停止と再開・ローテーション・送信の一覧・ping・再送・監査ログ。メトリクスの出力。
/// </summary>
public sealed class WebhookAdminApiTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>, IAsyncLifetime
{
    private const string Loopback = "http://127.0.0.1:43132/callback";
    private OAuthTestEnvironment _env = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _env = await OAuthTestEnvironment.StartAsync(db);

    public async ValueTask DisposeAsync() => await _env.DisposeAsync();

    [Fact(DisplayName = "Webhook の管理: 人事管理者の人だけが登録・変更・ローテーション・ping・再送でき、鍵は応答で 1 回だけ返る。一般の人は 403。操作は監査に残る")]
    public async Task Admin_ManagesWebhooks()
    {
        var (exit, output) = await _env.CliAsync(
            "register-client", "--client-id", "wh-api", "--name", "勤怠システム", "--auth", "private_key_jwt", "--grant-types", "client_credentials",
            "--resources", "api", "--scopes", "jinji.read", "--fields", "employee_number,assignments");
        Assert.True(exit == 0, output);
        await _env.RegisterPublicClientAsync("wh-public", Loopback);

        using var admin = await _env.LoginAsync(F.Person("hr_admin"));
        using var clients = await admin.GetAsync("/auth/admin/oauth/clients");
        var list = (await TestBrowser.JsonAsync(clients)).EnumerateArray().ToList();
        var clientRef = list.Single(c => c.GetProperty("client_id").GetString() == "wh-api").GetProperty("id").GetGuid();
        var publicRef = list.Single(c => c.GetProperty("client_id").GetString() == "wh-public").GetProperty("id").GetGuid();

        // 一般の人は使えない
        using (var member = await _env.LoginAsync(F.Person("u1_member")))
        {
            using var denied = await member.PostAsync($"/auth/admin/oauth/clients/{clientRef}/webhook", new { url = "https://hooks.example.co.jp/jinji" });
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var deniedList = await member.GetAsync("/auth/admin/oauth/webhooks");
            Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
        }

        // 送信先の検査・受け手になれないクライアント
        using (var http = await admin.PostAsync($"/auth/admin/oauth/clients/{clientRef}/webhook", new { url = "http://hooks.example.co.jp/jinji" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        }

        using (var publicClient = await admin.PostAsync($"/auth/admin/oauth/clients/{publicRef}/webhook", new { url = "https://hooks.example.co.jp/jinji" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, publicClient.StatusCode);
        }

        // 登録: 鍵はこの応答でだけ返る
        using var registered = await admin.PostAsync($"/auth/admin/oauth/clients/{clientRef}/webhook", new { url = "https://hooks.example.co.jp/jinji", max_events_per_delivery = 50 });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var issued = await TestBrowser.JsonAsync(registered);
        var webhookId = issued.GetProperty("webhook_id").GetGuid();
        var secret = issued.GetProperty("secret").GetString()!;
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);
        using (var duplicate = await admin.PostAsync($"/auth/admin/oauth/clients/{clientRef}/webhook", new { url = "https://hooks.example.co.jp/other" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        var item = await WebhookAsync(admin, webhookId);
        Assert.Equal(("wh-api", "active", 50, 1000, 10), (
            item.GetProperty("client_id").GetString(), item.GetProperty("status").GetString(), item.GetProperty("max_events_per_delivery").GetInt32(),
            item.GetProperty("summary_threshold").GetInt32(), item.GetProperty("min_interval_seconds").GetInt32()));
        Assert.Single(item.GetProperty("secrets").EnumerateArray());
        Assert.DoesNotContain(secret[6..], item.GetRawText(), StringComparison.Ordinal);

        // 変更（停止・間隔）。送信先の誤りは 400
        using (var updated = await admin.PostAsync($"/auth/admin/oauth/webhooks/{webhookId}/update", new { status = "disabled", min_interval_seconds = 30 }))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var body = await TestBrowser.JsonAsync(updated);
            Assert.Equal(("disabled", 30), (body.GetProperty("status").GetString(), body.GetProperty("min_interval_seconds").GetInt32()));
        }

        using (var bad = await admin.PostAsync($"/auth/admin/oauth/webhooks/{webhookId}/update", new { url = "https://192.168.1.10/hook" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        }

        // ローテーション: 旧鍵も期限まで残る（重ねる期間の上限は 168 時間）
        using (var tooLong = await admin.PostAsync($"/auth/admin/oauth/webhooks/{webhookId}/rotate-secret", new { overlap_hours = 200 }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        }

        using (var rotated = await admin.PostAsync($"/auth/admin/oauth/webhooks/{webhookId}/rotate-secret", new { overlap_hours = 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
            Assert.NotEqual(secret, (await TestBrowser.JsonAsync(rotated)).GetProperty("secret").GetString());
        }

        var secrets = (await WebhookAsync(admin, webhookId)).GetProperty("secrets").EnumerateArray().ToList();
        Assert.Equal(2, secrets.Count);
        Assert.Single(secrets, s => s.GetProperty("expires_at").ValueKind == JsonValueKind.Null);

        // ping・送信の一覧・再送（dead が無ければ 0 件）
        using (var ping = await admin.PostAsync($"/auth/admin/oauth/webhooks/{webhookId}/ping"))
        {
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        using (var deliveries = await admin.GetAsync($"/auth/admin/oauth/webhooks/{webhookId}/deliveries?status=pending"))
        {
            var delivery = Assert.Single((await TestBrowser.JsonAsync(deliveries)).EnumerateArray());
            Assert.Equal(("ping", "pending", 0), (delivery.GetProperty("kind").GetString(), delivery.GetProperty("status").GetString(), delivery.GetProperty("event_count").GetInt32()));
        }

        using (var badStatus = await admin.GetAsync($"/auth/admin/oauth/webhooks/{webhookId}/deliveries?status=unknown"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badStatus.StatusCode);
        }

        using (var redeliver = await admin.PostAsync($"/auth/admin/oauth/webhooks/{webhookId}/redeliver"))
        {
            Assert.Equal(HttpStatusCode.OK, redeliver.StatusCode);
            Assert.Equal(0, (await TestBrowser.JsonAsync(redeliver)).GetProperty("redelivered").GetInt32());
        }

        using (var missing = await admin.PostAsync($"/auth/admin/oauth/webhooks/{Guid.NewGuid()}/ping"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        foreach (var action in new[] { WebhookAuditActions.Register, WebhookAuditActions.Update, WebhookAuditActions.SecretRotate, WebhookAuditActions.Ping, WebhookAuditActions.Redeliver })
        {
            Assert.True(await db.CountAuditAsync(action, "success", Ct) >= 1, action);
        }
    }

    [Fact(DisplayName = "Webhook のメトリクス: 送信の試行をクライアント・結果別に、通知した変更の件数を送り方別に Prometheus 形式で出す")]
    public void Metrics_AreExported()
    {
        var services = new ServiceCollection().AddMetrics().AddSingleton<AccessMetrics>().AddSingleton<WebhookMetrics>().AddSingleton<PrometheusMetrics>();
        using var provider = services.BuildServiceProvider();
        var exporter = provider.GetRequiredService<PrometheusMetrics>();
        var metrics = provider.GetRequiredService<WebhookMetrics>();
        metrics.Attempt("attendance", WebhookAttemptOutcome.Succeeded);
        metrics.Attempt("attendance", WebhookAttemptOutcome.Dead);
        metrics.Events("attendance", WebhookDeliveryKind.Summary, 4200);
        var text = exporter.Render();
        Assert.Contains("jinji_webhook_deliveries_total{client=\"attendance\",outcome=\"succeeded\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("jinji_webhook_deliveries_total{client=\"attendance\",outcome=\"dead\"} 1", text, StringComparison.Ordinal);
        Assert.Contains("jinji_webhook_events_total{client=\"attendance\",kind=\"summary\"} 4200", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "ワーカーだけの役割: Data Protection の設定があれば Webhook の鍵を復号でき、無くてもワーカーは起動できる（鍵を扱うときだけ理由を示して失敗する）")]
    public void WorkerOnly_DataProtection()
    {
        string[] common =
        [
            $"--ConnectionStrings:App={db.Database.AppConnectionString}",
            "--Logging:LogLevel:Default=Warning",
            "--JinJi:Webhooks:Enabled=false",
        ];
        var secret = WebhookSignature.NewSecret();
        using (var host = ServerHost.BuildWorkerOnly([.. common, "--JinJi:DataProtection:AllowUnprotectedKeys=true"]))
        {
            var protector = host.Services.GetRequiredService<IWebhookSecretProtector>();
            var stored = protector.Protect(secret);
            Assert.DoesNotContain(Convert.ToBase64String(secret), stored, StringComparison.Ordinal);
            Assert.Equal(secret, protector.Unprotect(stored));
            Assert.NotNull(host.Services.GetRequiredService<WebhookDispatcher>());
        }

        using var bare = ServerHost.BuildWorkerOnly(common);
        var ex = Assert.Throws<InvalidOperationException>(() => bare.Services.GetRequiredService<IWebhookSecretProtector>().Protect(secret));
        Assert.Contains("JinJi:DataProtection", ex.Message, StringComparison.Ordinal);
    }

    private static async Task<JsonElement> WebhookAsync(TestBrowser admin, Guid id)
    {
        using var response = await admin.GetAsync("/auth/admin/oauth/webhooks");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await TestBrowser.JsonAsync(response)).EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == id);
    }
}
