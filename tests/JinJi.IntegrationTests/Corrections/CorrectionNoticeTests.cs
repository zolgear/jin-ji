using System.Net;
using System.Text.Json;
using JinJi.Application.Corrections;
using JinJi.Host.Web;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Ingest;
using JinJi.Policy;

namespace JinJi.IntegrationTests.Corrections;

/// <summary>
/// 修正依頼の通知（ワーカーの定期ジョブ・管理 CLI の本体）: Webhook は既定で無効、本文は件数とリンクだけ（人事データの値を載せない）、
/// 送信先の検査（https・許可リスト・公開のアドレス・リダイレクトに従わない）、実行の記録と監査ログ。
/// </summary>
public sealed class CorrectionNoticeTests : IAsyncLifetime
{
    private const string SlackUrl = "https://hooks.slack.example/services/T000/B000/SECRETSECRET";

    private IngestDatabase _db = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _db = await IngestDatabase.CreateAsync(Ct);

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private static CorrectionOptions WithWebhook(CorrectionWebhookFormat format = CorrectionWebhookFormat.Slack) => new()
    {
        Notice = new CorrectionNoticeOptions
        {
            Webhook = new CorrectionWebhookOptions
            {
                Enabled = true,
                Url = SlackUrl,
                Format = format,
                AllowedHosts = ["hooks.slack.example"],
                LinkUrl = "https://jinji.example.co.jp/#/corrections",
            },
        },
    };

    private (CorrectionNoticeService Notice, CorrectionRequestService Corrections) Create(CorrectionOptions options, ICorrectionWebhookSender? sender)
    {
        var store = new NpgsqlCorrectionStore(_db.DataSource);
        var corrections = new CorrectionRequestService(store, _db.Snapshots, new NpgsqlPolicyRuleStore(_db.DataSource, TimeProvider.System), _db.Audit, TimeProvider.System, options);
        return (new CorrectionNoticeService(store, corrections, _db.Audit, TimeProvider.System, sender), corrections);
    }

    [Fact(DisplayName = "通知: Webhook が無効なら送らずに記録だけ。有効なら件数とリンクだけを送り、氏名・理由は載せない。新しい依頼が無ければ送らない")]
    public async Task Notice_SendsCountsOnly()
    {
        var (disabled, corrections) = Create(new CorrectionOptions(), null);
        await corrections.ReportAsync(_db.User("member"), new CorrectionReportInput(_db.Id("takahashi"), PersonFields.ExtensionNumber, "内線表の番号と違う", "1234"), Ct);
        var run = await disabled.RunAsync(Channel.Cli, Ct);
        Assert.Equal(CorrectionNoticeOutcome.Disabled, run.Outcome);
        Assert.Equal((1, 1, 0), (run.Counts.Open, run.Counts.New, run.Counts.Stale));

        var sender = new RecordingSender(200);
        var (notice, _) = Create(WithWebhook(), sender);
        var sent = await notice.RunAsync(Channel.Worker, Ct);
        Assert.Equal((CorrectionNoticeOutcome.Sent, "hooks.slack.example", (int?)200), (sent.Outcome, sent.DestinationHost, sent.HttpStatus));
        var body = Assert.Single(sender.Bodies);
        var text = JsonDocument.Parse(body).RootElement.GetProperty("text").GetString()!;
        Assert.Contains("未対応の修正依頼が 1 件", text, StringComparison.Ordinal);
        Assert.Contains("https://jinji.example.co.jp/#/corrections", text, StringComparison.Ordinal);
        foreach (var secret in new[] { "髙橋", "E0002", "内線表", "1234", _db.Id("takahashi").ToString() })
        {
            Assert.DoesNotContain(secret, body, StringComparison.Ordinal);
        }

        // 前回送ってから新しい依頼が無く、長く放置された依頼も無ければ送らない
        Assert.Equal(CorrectionNoticeOutcome.NothingToSend, (await notice.RunAsync(Channel.Worker, Ct)).Outcome);
        Assert.Single(sender.Bodies);

        // 実行の記録（URL は残さない）と監査ログ
        var hosts = await _db.QueryAsync<string>(null, "SELECT coalesce(destination_host, '-') || ':' || outcome FROM correction_notice_run ORDER BY started_at", Ct);
        Assert.Equal(["-:disabled", "hooks.slack.example:sent", "-:nothing_to_send"], hosts);
        Assert.Empty(await _db.QueryAsync<string>(null, "SELECT destination_host FROM correction_notice_run WHERE destination_host LIKE '%SECRET%'", Ct));
        Assert.Equal(3, (await _db.QueryAsync<long>(null, "SELECT count(*) FROM audit_event WHERE action = 'correction.notify'", Ct)).Single());
    }

    [Fact(DisplayName = "通知: 送信の失敗は failed として記録し、例外にする（次の実行で送り直す）")]
    public async Task Notice_RecordsFailure()
    {
        var (notice, corrections) = Create(WithWebhook(CorrectionWebhookFormat.Teams), new RecordingSender(500));
        await corrections.ReportAsync(_db.User("member"), new CorrectionReportInput(_db.Id("takahashi"), PersonFields.Site, "拠点が違う", null), Ct);
        await Assert.ThrowsAsync<CorrectionNoticeException>(() => notice.RunAsync(Channel.Worker, Ct));
        Assert.Equal(["failed"], await _db.QueryAsync<string>(null, "SELECT outcome FROM correction_notice_run", Ct));
        Assert.Equal(1, (await _db.QueryAsync<long>(null, "SELECT count(*) FROM audit_event WHERE action = 'correction.notify' AND outcome = 'failed'", Ct)).Single());
    }

    [Fact(DisplayName = "Webhook の本文: Teams のワークフローは Adaptive Card の添付、Slack は text。件数とリンクだけ")]
    public void Body_Formats()
    {
        var counts = new CorrectionCounts(12, 3, 2, null);
        var teams = JsonDocument.Parse(CorrectionNoticeService.Body(WithWebhook(CorrectionWebhookFormat.Teams).Notice.Webhook, counts, 14)).RootElement;
        Assert.Equal("message", teams.GetProperty("type").GetString());
        var attachment = teams.GetProperty("attachments")[0];
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("https://jinji.example.co.jp/#/corrections", card.GetProperty("actions")[0].GetProperty("url").GetString());
        Assert.Equal("12 件", card.GetProperty("body")[2].GetProperty("facts")[0].GetProperty("value").GetString());

        var slack = JsonDocument.Parse(CorrectionNoticeService.Body(WithWebhook().Notice.Webhook, counts, 14)).RootElement;
        Assert.Contains("新しい依頼 3 件", slack.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Theory(DisplayName = "Webhook の設定の検査: https・利用者情報なし・許可リストのホストだけ（URL は誤りの文に出さない）")]
    [InlineData("http://hooks.slack.example/services/x", "hooks.slack.example")]
    [InlineData("https://user:pass@hooks.slack.example/services/x", "hooks.slack.example")]
    [InlineData("https://evil.example/services/x", "hooks.slack.example")]
    [InlineData("not a url", "hooks.slack.example")]
    public void Options_RejectUnsafeWebhook(string url, string allowed)
    {
        var options = new CorrectionWebhookOptions { Enabled = true, Url = url, AllowedHosts = [allowed] };
        var ex = Assert.ThrowsAny<ArgumentException>(options.Validate);
        Assert.DoesNotContain("services/x", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pass", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Webhook の送信: 許可リスト外・公開でないアドレス・リダイレクトは送らない／失敗。2xx だけを成功とする")]
    public async Task Sender_GuardsDestination()
    {
        var options = WithWebhook();
        using var handler = new StubHandler(HttpStatusCode.Found);
        using var sender = new CorrectionWebhookSender(options, handler);
        var redirect = await sender.SendAsync(new Uri(SlackUrl), "{}", Ct);
        Assert.False(redirect.Succeeded);
        Assert.Equal(302, redirect.HttpStatus);

        Assert.False((await sender.SendAsync(new Uri("https://evil.example/x"), "{}", Ct)).Succeeded);
        Assert.Equal(1, handler.Calls);

        var loopback = WithWebhook() with
        {
            Notice = new CorrectionNoticeOptions { Webhook = new CorrectionWebhookOptions { Enabled = true, Url = "https://127.0.0.1/x", AllowedHosts = ["127.0.0.1"] } },
        };
        using var direct = new CorrectionWebhookSender(loopback, new StubHandler(HttpStatusCode.OK));
        var refused = await direct.SendAsync(new Uri("https://127.0.0.1/x"), "{}", Ct);
        Assert.Equal((false, "公開でないアドレスです"), (refused.Succeeded, refused.Error));

        // 実際の接続の処理も、私設のアドレスに解決される名前には接続しない（CIMD の取得と同じ守り）
        using var real = new CorrectionWebhookSender(WithWebhook() with
        {
            Notice = new CorrectionNoticeOptions { Webhook = new CorrectionWebhookOptions { Enabled = true, Url = "https://localhost/x", AllowedHosts = ["localhost"], Timeout = TimeSpan.FromSeconds(3) } },
        });
        var local = await real.SendAsync(new Uri("https://localhost/x"), "{}", Ct);
        Assert.Equal((false, "公開でないアドレスです"), (local.Succeeded, local.Error));

        using var ok = new CorrectionWebhookSender(options, new StubHandler(HttpStatusCode.OK));
        Assert.True((await ok.SendAsync(new Uri(SlackUrl), "{}", Ct)).Succeeded);
    }

    private sealed class RecordingSender(int status) : ICorrectionWebhookSender
    {
        public List<string> Bodies { get; } = [];

        public Task<CorrectionWebhookResult> SendAsync(Uri url, string json, CancellationToken cancellationToken)
        {
            Bodies.Add(json);
            return Task.FromResult(new CorrectionWebhookResult(status is >= 200 and < 300, status, status is >= 200 and < 300 ? null : $"状態コード {status}"));
        }
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
