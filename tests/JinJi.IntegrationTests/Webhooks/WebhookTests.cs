using System.Security.Cryptography;
using System.Text.Json;
using JinJi.Application.Access;
using JinJi.Application.Audit;
using JinJi.Application.Auth;
using JinJi.Application.Changes;
using JinJi.Application.People;
using JinJi.Application.Webhooks;
using JinJi.Domain;
using JinJi.Host.Auth;
using JinJi.Host.Web;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;
using Microsoft.AspNetCore.DataProtection;

namespace JinJi.IntegrationTests.Webhooks;

/// <summary>
/// 社内システムへの Webhook（0018、アーキテクチャ概要 §9「Webhook の実装の規則」）: 本文は種別と ID だけ・署名とリプレイの検証・冪等キー・
/// 指数バックオフの再送と dead・再送の指示・鍵のローテーション・見えない人の変更は送らない・先行提供の受け手ごとの差・取り消しの宛先・
/// 大量の変更の要約・複数のワーカーでも二重に送らないこと。受け手はテストの中で立てる https のサーバー。
/// </summary>
public sealed class WebhookTests(RouteFixtureDatabase db) : IClassFixture<RouteFixtureDatabase>, IAsyncLifetime
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(30);

    private readonly CursorProtector _cursors = new(RandomNumberGenerator.GetBytes(32));
    private readonly WebhookSecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UtcNow);
    private WebhookReceiver _receiver = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    private static AuthActor HrAdmin(OrgFixture f) => AuthActor.HrAdmin(f.Person("hr_admin"));

    public async ValueTask InitializeAsync() => _receiver = await WebhookReceiver.StartAsync();

    public async ValueTask DisposeAsync()
    {
        // 同じデータベースの次のテストのワーカーが、このテストの受け手に送らないように止める
        await using (var connection = await db.Database.OpenAppAsync(CancellationToken.None))
        {
            await SampleData.SetSessionViewerAsync(connection, "system", string.Empty, null, CancellationToken.None);
            await SampleData.ExecuteAsync(connection, "UPDATE webhook_subscription SET status = 'disabled', updated_by_kind = 'cli', updated_by_person_id = NULL", CancellationToken.None);
        }

        await _receiver.DisposeAsync();
    }

    [Fact(DisplayName = "Webhook: 変更を種別と ID だけで送り、氏名などの値は載せない。webhook-id は本文の id（イベント ID）、署名は登録の応答で受け取った鍵で検証でき、本文の改ざん・古い時刻（リプレイ）は検証に失敗する。送信は監査に残る")]
    public async Task Delivers_IdsOnly_WithVerifiableSignature()
    {
        var client = await ClientAsync("wh-basic");
        var issued = await RegisterAsync(client, "basic");
        Assert.StartsWith(WebhookSignature.SecretPrefix, issued.Secret, StringComparison.Ordinal);
        Assert.DoesNotContain(issued.Secret, issued.ToString(), StringComparison.Ordinal);

        // 鍵は Data Protection で暗号化した値だけを保存する
        var stored = await db.QueryAsAppAsync<string>(ViewerContext.ForSystem(Channel.Worker), "SELECT protected_secret FROM webhook_secret WHERE subscription_id = $1", Ct, issued.Subscription.Id);
        Assert.DoesNotContain(issued.Secret[WebhookSignature.SecretPrefix.Length..], Assert.Single(stored), StringComparison.Ordinal);

        var person = F.Person("s2_member");
        await InsertTodayAssignmentAsync(person);
        var dispatcher = Dispatcher(Options());
        var received = await RunUntilAsync(dispatcher, () => Events("basic").Any(e => e.Id == person));

        var hook = received.Last(r => r.Path == "/basic");
        using var body = JsonDocument.Parse(hook.Body);
        Assert.Equal("changes", body.RootElement.GetProperty("type").GetString());
        Assert.Equal(hook.Id, body.RootElement.GetProperty("id").GetString());
        Assert.Equal("wh-basic", body.RootElement.GetProperty("client_id").GetString());
        var item = body.RootElement.GetProperty("events").EnumerateArray().First(e => e.GetProperty("id").GetGuid() == person);
        Assert.Equal(["type", "id", "effective_date", "withdrawn"], item.EnumerateObject().Select(p => p.Name));
        Assert.Equal(RouteFixtureDatabase.Date(F.Today), item.GetProperty("effective_date").GetString());
        Assert.False(item.GetProperty("withdrawn").GetBoolean());
        // 値（氏名・組織名など）は載せない
        Assert.DoesNotContain("s2_member", hook.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("display_name", hook.Body, StringComparison.Ordinal);

        // 変更フィードの位置: 受け手のクライアントのカーソルとして検証できる（別のクライアントでは使えない）
        var cursor = body.RootElement.GetProperty("changes_since").GetString()!;
        Assert.True(_cursors.TryUnprotect<ChangeFeedPosition>(WebhookDispatcher.ChangeCursorPurpose, ViewerContext.ForService("wh-basic", FieldLimit.Unrestricted), cursor, out _));
        Assert.False(_cursors.TryUnprotect<ChangeFeedPosition>(WebhookDispatcher.ChangeCursorPurpose, ViewerContext.ForService("other", FieldLimit.Unrestricted), cursor, out _));

        // 署名: 鍵で検証できる。本文の改ざん・許容幅（5 分）より古い時刻・別の鍵は失敗する
        var secret = WebhookSignature.Parse(issued.Secret);
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(hook.Timestamp!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(WebhookSignature.Verify(secret, hook.Id, hook.Timestamp, hook.Signature, hook.Body, sentAt));
        Assert.False(WebhookSignature.Verify(secret, hook.Id, hook.Timestamp, hook.Signature, hook.Body.Replace("false", "true ", StringComparison.Ordinal), sentAt));
        Assert.False(WebhookSignature.Verify(secret, hook.Id, hook.Timestamp, hook.Signature, hook.Body, sentAt.AddMinutes(6)));
        Assert.False(WebhookSignature.Verify(secret, Guid.NewGuid().ToString("D"), hook.Timestamp, hook.Signature, hook.Body, sentAt));
        Assert.False(WebhookSignature.Verify(WebhookSignature.NewSecret(), hook.Id, hook.Timestamp, hook.Signature, hook.Body, sentAt));

        Assert.True(await db.CountAuditAsync(WebhookAuditActions.Deliver, "success", Ct, "system") >= 1);
        Assert.True(await db.CountAuditAsync(WebhookAuditActions.Register, "success", Ct) >= 1);
    }

    [Fact(DisplayName = "Webhook: 失敗したら指数バックオフで再送し（同じイベント ID・同じ本文）、上限回数で dead。dead は人事管理者の人の指示で再送でき、管理 CLI からは再送できない")]
    public async Task Retries_WithBackoff_ThenDead_ThenRedeliver()
    {
        var client = await ClientAsync("wh-retry");
        var issued = await RegisterAsync(client, "retry");
        _receiver.Respond("retry", () => 503);
        var options = Options() with { MaxAttempts = 3, RetryBaseDelay = TimeSpan.FromMinutes(1), RetryMaxDelay = TimeSpan.FromHours(1) };
        var dispatcher = Dispatcher(options);
        await InsertTodayAssignmentAsync(F.Person("s3_member"));

        // 1 回目: 失敗して送信待ちに戻る（次の試行は 1 分〜1 分 6 秒後）
        await RunUntilAsync(dispatcher, () => _receiver.For("retry").Count >= 1);
        var first = Assert.Single(_receiver.For("retry"));
        var delivery = await SingleDeliveryAsync(issued.Subscription.Id);
        Assert.Equal((WebhookDeliveryStatus.Pending, 1, 503), (delivery.Status, delivery.Attempts, delivery.LastHttpStatus));
        Assert.InRange(delivery.NextAttemptAt - delivery.LastAttemptAt!.Value, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(66));

        // 待ちの間は送らない
        _time.Advance(TimeSpan.FromSeconds(30));
        await dispatcher.RunOnceAsync(Ct);
        Assert.Single(_receiver.For("retry"));

        // 2 回目（待ちは 2 倍）、3 回目で dead
        _time.Advance(TimeSpan.FromSeconds(40));
        await dispatcher.RunOnceAsync(Ct);
        delivery = await SingleDeliveryAsync(issued.Subscription.Id);
        Assert.Equal((WebhookDeliveryStatus.Pending, 2), (delivery.Status, delivery.Attempts));
        Assert.InRange(delivery.NextAttemptAt - delivery.LastAttemptAt!.Value, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(132));
        _time.Advance(TimeSpan.FromSeconds(140));
        await dispatcher.RunOnceAsync(Ct);
        delivery = await SingleDeliveryAsync(issued.Subscription.Id);
        Assert.Equal((WebhookDeliveryStatus.Dead, 3), (delivery.Status, delivery.Attempts));
        Assert.All(_receiver.For("retry"), r => Assert.Equal((first.Id, first.Body), (r.Id, r.Body)));
        Assert.True(await db.CountAuditAsync(WebhookAuditActions.Dead, "failed", Ct, "system") >= 1);

        // dead は送らない
        _time.Advance(TimeSpan.FromMinutes(10));
        await dispatcher.RunOnceAsync(Ct);
        Assert.Equal(3, _receiver.For("retry").Count);

        // 管理画面の件数
        var view = (await Admin().ListAsync(HrAdmin(F), Ct)).Single(v => v.Subscription.Id == issued.Subscription.Id);
        Assert.Equal((0, 1), (view.Counts.Pending, view.Counts.Dead));
        Assert.Equal("状態コード 503", view.Subscription.LastFailure);

        // 再送: 管理 CLI は不可（再送した人を記録するため）、人事管理者の人なら送信待ちに戻り、同じイベント ID で届く
        var denied = await Assert.ThrowsAsync<AuthAdminException>(() => Admin().RedeliverAsync(AuthActor.Cli, issued.Subscription.Id, delivery.Id, null, Ct));
        Assert.Equal(AuthAdminError.Forbidden, denied.Error);
        _receiver.Respond("retry", () => 200);
        Assert.Equal(1, await Admin().RedeliverAsync(HrAdmin(F), issued.Subscription.Id, delivery.Id, null, Ct));
        var again = await Assert.ThrowsAsync<AuthAdminException>(() => Admin().RedeliverAsync(HrAdmin(F), issued.Subscription.Id, delivery.Id, null, Ct));
        Assert.Equal(AuthAdminError.Conflict, again.Error);
        _time.Advance(TimeSpan.FromSeconds(30));
        await dispatcher.RunOnceAsync(Ct);
        delivery = await SingleDeliveryAsync(issued.Subscription.Id);
        Assert.Equal((WebhookDeliveryStatus.Succeeded, 1, F.Person("hr_admin")), (delivery.Status, delivery.Attempts, delivery.RedeliveredBy));
        Assert.Equal(first.Id, _receiver.For("retry")[^1].Id);
        Assert.True(await db.CountAuditAsync(WebhookAuditActions.Redeliver, "success", Ct) >= 1);
        Assert.True(await db.CountAuditAsync(WebhookAuditActions.Redeliver, "denied", Ct) >= 1);
    }

    [Fact(DisplayName = "Webhook: 鍵のローテーション中は新旧両方の鍵で署名し（受け手はどちらでも検証できる）、重ねる期間を過ぎると新しい鍵だけになる")]
    public async Task SecretRotation_SignsWithBothKeys()
    {
        var client = await ClientAsync("wh-rotate");
        var issued = await RegisterAsync(client, "rotate");
        var rotated = await Admin().RotateSecretAsync(HrAdmin(F), issued.Subscription.Id, TimeSpan.FromMinutes(5), null, Ct);
        Assert.NotEqual(issued.Secret, rotated.Secret);
        var dispatcher = Dispatcher(Options());

        await InsertTodayAssignmentAsync(F.Person("s1_member"));
        await RunUntilAsync(dispatcher, () => _receiver.For("rotate").Count >= 1);
        var during = _receiver.For("rotate")[^1];
        Assert.Equal(2, during.Signature!.Split(' ').Length);
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(during.Timestamp!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.True(WebhookSignature.Verify(WebhookSignature.Parse(issued.Secret), during.Id, during.Timestamp, during.Signature, during.Body, sentAt));
        Assert.True(WebhookSignature.Verify(WebhookSignature.Parse(rotated.Secret), during.Id, during.Timestamp, during.Signature, during.Body, sentAt));

        // 期限（5 分）を過ぎた後は新しい鍵だけ
        _time.Advance(TimeSpan.FromMinutes(6));
        var count = _receiver.For("rotate").Count;
        await InsertTodayAssignmentAsync(F.Person("s1_member"));
        await RunUntilAsync(dispatcher, () => _receiver.For("rotate").Count > count);
        var after = _receiver.For("rotate")[^1];
        Assert.Single(after.Signature!.Split(' '));
        sentAt = DateTimeOffset.FromUnixTimeSeconds(long.Parse(after.Timestamp!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(WebhookSignature.Verify(WebhookSignature.Parse(issued.Secret), after.Id, after.Timestamp, after.Signature, after.Body, sentAt));
        Assert.True(WebhookSignature.Verify(WebhookSignature.Parse(rotated.Secret), after.Id, after.Timestamp, after.Signature, after.Body, sentAt));

        var view = (await Admin().ListAsync(HrAdmin(F), Ct)).Single(v => v.Subscription.Id == issued.Subscription.Id);
        Assert.Single(view.Secrets);
        Assert.True(await db.CountAuditAsync(WebhookAuditActions.SecretRotate, "success", Ct) >= 1);
        await Assert.ThrowsAsync<AuthAdminException>(() => Admin().RotateSecretAsync(HrAdmin(F), issued.Subscription.Id, TimeSpan.FromDays(8), null, Ct));
    }

    [Fact(DisplayName = "Webhook: 先行提供の日数で送る日が受け手ごとに違う。30 日の受け手には登録の直後に上限までの予定が届き（見せてよい日つき）、10 日後の予定もすぐ届く。日数 0 の受け手には届かない（RLS）")]
    public async Task DisclosureLead_DiffersPerReceiver()
    {
        var lead = await ClientAsync("wh-lead", leadDays: 30);
        var plain = await ClientAsync("wh-plain");
        await RegisterAsync(lead, "lead");
        await RegisterAsync(plain, "plain");
        var dispatcher = Dispatcher(Options());

        // 登録の直後: 上限（30 日後）までの既存の予定（mover の異動・NEW の新設）
        await RunUntilAsync(dispatcher, () => Events("lead").Any(e => e.Id == db.Mover) && Events("lead").Any(e => e.Id == db.NewUnit));
        Assert.Contains(Events("lead"), e => e.Id == db.NewUnit && e.Type == "org_unit" && e.EffectiveDate == RouteFixtureDatabase.Date(db.ReorgDate));

        var s2 = F.Person("s2_member");
        await using (var connection = await OpenAsHrAdminAsync())
        {
            await SampleData.InsertAssignmentAsync(connection, s2, F.Unit("S3"), "concurrent", $"[{RouteFixtureDatabase.Date(F.Today.AddDays(10))},)", Ct);
        }

        await RunUntilAsync(dispatcher, () => Events("lead").Any(e => e.Id == s2 && e.EffectiveDate == RouteFixtureDatabase.Date(F.Today.AddDays(10))));

        // 日数 0 の受け手: 目印（今日の変更）が届くまでに、予定は 1 件も届かない
        var marker = await InsertTodayAssignmentAsync(F.Person("u3_member"));
        await RunUntilAsync(dispatcher, () => Events("plain").Any(e => e.Id == marker));
        var plainEvents = Events("plain");
        Assert.DoesNotContain(plainEvents, e => e.Id == db.Mover || e.Id == db.NewUnit || e.Id == s2);
        Assert.All(plainEvents, e => Assert.True(string.CompareOrdinal(e.EffectiveDate, RouteFixtureDatabase.Date(F.Today)) <= 0));
    }

    [Fact(DisplayName = "Webhook: 予定の取り消し（withdrawn）は先行提供の受け手にだけ届く。通知済みなら今は見えない人（取り消された入社予定）にも届き、通知する前に取り消された予定は届かない（予定があったことを明かさない）")]
    public async Task Withdrawn_OnlyToLeadReceivers_ThatWereNotified()
    {
        var lead = await ClientAsync("wh-wlead", leadDays: 30);
        var plain = await ClientAsync("wh-wplain");
        await RegisterAsync(lead, "wlead");
        await RegisterAsync(plain, "wplain");
        var dispatcher = Dispatcher(Options());
        await RunUntilAsync(dispatcher, () => Events("wlead").Any(e => e.Id == db.Mover));

        // 10 日後の入社予定 → 先行提供の受け手に届く → 取り消すと withdrawn が届く（今は見えない人だが通知済み）
        var hire = await InsertFutureHireAsync("E9301", 10);
        await RunUntilAsync(dispatcher, () => Events("wlead").Any(e => e.Id == hire && !e.Withdrawn));
        await WithdrawHireAsync(hire);
        await RunUntilAsync(dispatcher, () => Events("wlead").Any(e => e.Id == hire && e.Withdrawn));

        // 通知する前に取り消された入社予定: 予定の記録も取り消しの記録も届かない
        var unseen = await InsertFutureHireAsync("E9302", 12, withdraw: true);
        var marker = await InsertTodayAssignmentAsync(F.Person("u3_member"));
        await RunUntilAsync(dispatcher, () => Events("wlead").Any(e => e.Id == marker) && Events("wplain").Any(e => e.Id == marker));
        Assert.DoesNotContain(Events("wlead"), e => e.Id == unseen);

        // 日数 0 の受け手には、予定も取り消しも届かない
        Assert.DoesNotContain(Events("wplain"), e => e.Id == hire || e.Id == unseen || e.Withdrawn);
    }

    [Fact(DisplayName = "Webhook: 受け手に見えない人（退職後の公開期間を過ぎた人）の変更は、変更フィードの記録にあっても送らない")]
    public async Task InvisiblePeople_AreNotSent()
    {
        var client = await ClientAsync("wh-visible");
        await RegisterAsync(client, "visible");
        var dispatcher = Dispatcher(Options());

        var hidden = F.Person("long_retired");
        await InsertTodayAssignmentAsync(hidden);
        var marker = await InsertTodayAssignmentAsync(F.Person("u3_member"));
        await RunUntilAsync(dispatcher, () => Events("visible").Any(e => e.Id == marker));
        Assert.DoesNotContain(Events("visible"), e => e.Id == hidden);

        // 変更フィードの記録（RLS の内側）には現れている（送らないのは Webhook の判定）
        var rows = await db.QueryAsAppAsync<long>(ViewerContext.ForService("wh-visible", FieldLimit.Unrestricted), "SELECT count(*) FROM change_event WHERE entity_id = $1", Ct, hidden);
        Assert.True(Assert.Single(rows) >= 1);
    }

    [Fact(DisplayName = "Webhook: 1 回の収集の変更が登録の件数以下なら件数ずつまとめて送り、要約の件数を超えたら 1 件ずつ送らずに要約（件数と変更フィードの位置）を 1 つ送る")]
    public async Task ManyChanges_AreBatchedOrSummarized()
    {
        var client = await ClientAsync("wh-bulk");
        var issued = await RegisterAsync(client, "bulk", new WebhookSettings(MaxEventsPerDelivery: 2, SummaryThreshold: 3, MinIntervalSeconds: 1));
        var dispatcher = Dispatcher(Options());

        // 3 人（要約の件数以下）: 2 件と 1 件の 2 回
        string[] three = ["s1_member", "s2_member", "s3_member"];
        await InsertTodayAssignmentsAsync(three);
        await RunUntilAsync(dispatcher, () => three.All(k => Events("bulk").Any(e => e.Id == F.Person(k))));
        var batches = _receiver.For("bulk").Select(r => JsonDocument.Parse(r.Body)).ToList();
        Assert.All(batches, b => Assert.Equal("changes", b.RootElement.GetProperty("type").GetString()));
        Assert.All(batches, b => Assert.InRange(b.RootElement.GetProperty("events").GetArrayLength(), 1, 2));

        // 5 人（要約の件数を超える）: 要約 1 つ
        var before = _receiver.For("bulk").Count;
        string[] five = ["u1_member", "u2_member", "u3_member", "s1_member", "s2_member"];
        await InsertTodayAssignmentsAsync(five);
        await RunUntilAsync(dispatcher, () => _receiver.For("bulk").Skip(before).Any(r => r.Body.Contains("changes.summary", StringComparison.Ordinal)));
        var summary = _receiver.For("bulk").Skip(before).Select(r => JsonDocument.Parse(r.Body)).Single(b => b.RootElement.GetProperty("type").GetString() == "changes.summary");
        var counts = summary.RootElement.GetProperty("summary");
        Assert.True(counts.GetProperty("total").GetInt32() >= 5);
        Assert.True(counts.GetProperty("person").GetInt32() >= 5);
        Assert.False(summary.RootElement.TryGetProperty("events", out _));
        Assert.True(_cursors.TryUnprotect<ChangeFeedPosition>(
            WebhookDispatcher.ChangeCursorPurpose,
            ViewerContext.ForService("wh-bulk", FieldLimit.Unrestricted),
            summary.RootElement.GetProperty("changes_since").GetString()!,
            out _));
        var deliveries = await Admin().ListDeliveriesAsync(HrAdmin(F), issued.Subscription.Id, null, 50, Ct);
        Assert.Contains(deliveries, d => d.Kind == WebhookDeliveryKind.Summary && d.EventCount >= 5);
    }

    [Fact(DisplayName = "Webhook: 2 つのワーカーが同時に収集・送信しても、同じ送信を二重に送らず、同じ変更を同じ受け手に二重に積まない")]
    public async Task ConcurrentWorkers_DoNotDoubleSend()
    {
        string[] paths = ["c1", "c2", "c3"];
        foreach (var path in paths)
        {
            await RegisterAsync(await ClientAsync("wh-" + path), path, new WebhookSettings(MaxEventsPerDelivery: 1, SummaryThreshold: 100, MinIntervalSeconds: 1));
        }

        string[] people = ["s1_member", "s2_member", "s3_member", "u1_member"];
        foreach (var key in people)
        {
            await InsertTodayAssignmentAsync(F.Person(key));
        }

        var options = Options();
        var first = Dispatcher(options);
        var second = Dispatcher(options);
        var deadline = DateTime.UtcNow + PollTimeout;
        while (!paths.All(p => people.All(k => Events(p).Any(e => e.Id == F.Person(k)))))
        {
            Assert.True(DateTime.UtcNow < deadline, "期待した送信が届きませんでした。");
            await Task.WhenAll(first.RunOnceAsync(Ct), second.RunOnceAsync(Ct));
            _time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(100, Ct);
        }

        // 余分に回しても増えない
        for (var i = 0; i < 3; i++)
        {
            await Task.WhenAll(first.RunOnceAsync(Ct), second.RunOnceAsync(Ct));
            _time.Advance(TimeSpan.FromSeconds(2));
        }

        foreach (var path in paths)
        {
            var received = _receiver.For(path);
            Assert.Equal(received.Count, received.Select(r => r.Id).Distinct().Count());
            foreach (var key in people)
            {
                Assert.Single(Events(path), e => e.Id == F.Person(key));
            }
        }
    }

    [Fact(DisplayName = "Webhook: 登録は client credentials で REST API を読める社内システムだけ。送信先は https・公開のアドレスだけ（http・利用者情報・私設のアドレス・localhost は拒否）。同じクライアントに 2 つ目は登録できない")]
    public async Task Registration_ValidatesReceiverAndUrl()
    {
        var service = await ClientAsync("wh-valid");
        var delegated = await ClientAsync("wh-delegated", grants: ["authorization_code"]);
        var admin = Admin();
        foreach (var url in new[] { "http://receiver.example/hook", "https://user:pw@receiver.example/hook", "https://10.0.0.5/hook", "https://127.0.0.1/hook", "https://[::1]/hook", "https://localhost/hook", "https://receiver.example/hook#x", "ftp://receiver.example/" })
        {
            var ex = await Assert.ThrowsAsync<AuthAdminException>(() => admin.RegisterAsync(HrAdmin(F), service, url, new WebhookSettings(), null, Ct));
            Assert.Equal(AuthAdminError.Invalid, ex.Error);
        }

        Assert.Equal(AuthAdminError.Invalid, (await Assert.ThrowsAsync<AuthAdminException>(() =>
            admin.RegisterAsync(HrAdmin(F), delegated, "https://receiver.example/hook", new WebhookSettings(), null, Ct))).Error);
        Assert.Equal(AuthAdminError.Invalid, (await Assert.ThrowsAsync<AuthAdminException>(() =>
            admin.RegisterAsync(HrAdmin(F), service, "https://receiver.example/hook", new WebhookSettings(MaxEventsPerDelivery: 50, SummaryThreshold: 10), null, Ct))).Error);

        await admin.RegisterAsync(HrAdmin(F), service, "https://receiver.example/hook", new WebhookSettings(), null, Ct);
        Assert.Equal(AuthAdminError.Conflict, (await Assert.ThrowsAsync<AuthAdminException>(() =>
            admin.RegisterAsync(HrAdmin(F), service, "https://receiver.example/other", new WebhookSettings(), null, Ct))).Error);

        // 一般の人・AI エージェント（委任元は人事管理者）・社内システムには登録も参照もできない（RLS）
        Assert.Equal(AuthAdminError.Forbidden, (await Assert.ThrowsAsync<AuthAdminException>(() =>
            admin.ListAsync(AuthActor.System, Ct))).Error);
        const string Rows = "SELECT count(*) FROM webhook_subscription";
        Assert.Equal(0L, Assert.Single(await db.QueryAsAppAsync<long>(ViewerContext.ForService("wh-valid", FieldLimit.Unrestricted), Rows, Ct)));
        Assert.Equal(0L, Assert.Single(await db.QueryAsAppAsync<long>(
            ViewerContext.ForAgent("agent-x", F.Person("hr_admin"), [Roles.HrAdmin], Channel.Mcp, FieldLimit.Unrestricted), Rows, Ct)));
        Assert.Equal(0L, Assert.Single(await db.QueryAsAppAsync<long>(
            ViewerContext.ForUser(F.Person("u1_member"), [], Channel.Ui, FieldLimit.Unrestricted), "SELECT count(*) FROM webhook_secret", Ct)));
        Assert.True(Assert.Single(await db.QueryAsAppAsync<long>(
            ViewerContext.ForUser(F.Person("hr_admin"), [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted), Rows, Ct)) >= 1);
    }

    [Fact(DisplayName = "Webhook の送信（SSRF の対策）: 本番の送信はループバック・私設のアドレスに解決される名前と IP アドレスのホストに接続せず、リダイレクトに従わない")]
    public async Task Sender_RefusesPrivateAddresses_AndRedirects()
    {
        using var production = new WebhookSender(Options());
        var loopbackName = await production.SendAsync(new Uri($"https://localhost:{_receiver.Port}/hook"), "{}", [], Ct);
        Assert.Equal((false, "公開でないアドレスです"), (loopbackName.Succeeded, loopbackName.Error));
        var literal = await production.SendAsync(new Uri($"https://127.0.0.1:{_receiver.Port}/hook"), "{}", [], Ct);
        Assert.Equal((false, "公開でないアドレスです"), (literal.Succeeded, literal.Error));
        var http = await production.SendAsync(new Uri("http://receiver.example/hook"), "{}", [], Ct);
        Assert.False(http.Succeeded);
        Assert.Empty(_receiver.For("hook"));

        // リダイレクト（3xx）は失敗で、転送先には送らない
        _receiver.Respond("moved", () => 302);
        using var sender = _receiver.Sender(Options());
        var moved = await sender.SendAsync(new Uri(WebhookReceiver.Url("moved")), "{}", [], Ct);
        Assert.Equal((false, 302, "リダイレクトには従いません"), (moved.Succeeded, moved.HttpStatus, moved.Error));
        Assert.Empty(_receiver.For("redirected"));
    }

    [Fact(DisplayName = "Webhook: 停止したクライアント・停止した登録には送らない（位置は進めない）。再開すると止めていた間の変更が届く。送信の確かめ（ping）は人を含まない本文")]
    public async Task SuspendedClient_IsSkipped_UntilResumed()
    {
        var clientRef = await ClientAsync("wh-pause");
        var issued = await RegisterAsync(clientRef, "pause");
        var dispatcher = Dispatcher(Options());
        var oauth = new NpgsqlOAuthStore(db.AppDataSource);
        var client = (await oauth.FindClientByRefAsync(clientRef, Ct))!;
        Assert.True(await oauth.UpdateClientAsync(AuthActor.Cli, client with { Status = OAuthClientStatus.Suspended }, null, Ct));

        var person = F.Person("s3_member");
        await InsertTodayAssignmentAsync(person);
        var marker = await InsertTodayAssignmentAsync(F.Person("u3_member"));
        var watch = await ClientAsync("wh-pause-watch");
        await RegisterAsync(watch, "pause-watch");
        var marker2 = await InsertTodayAssignmentAsync(F.Person("u2_member"));
        await RunUntilAsync(dispatcher, () => Events("pause-watch").Any(e => e.Id == marker2));
        Assert.Empty(_receiver.For("pause"));

        Assert.True(await oauth.UpdateClientAsync(AuthActor.Cli, client with { Status = OAuthClientStatus.Active }, null, Ct));
        await RunUntilAsync(dispatcher, () => Events("pause").Any(e => e.Id == person) && Events("pause").Any(e => e.Id == marker));

        // 登録の停止
        await Admin().UpdateAsync(HrAdmin(F), issued.Subscription.Id, null, WebhookStatus.Disabled, new WebhookSettings(), null, Ct);
        var count = _receiver.For("pause").Count;
        var marker3 = await InsertTodayAssignmentAsync(F.Person("u1_member"));
        await RunUntilAsync(dispatcher, () => Events("pause-watch").Any(e => e.Id == marker3));
        Assert.Equal(count, _receiver.For("pause").Count);

        // ping: 再開してから積むと届く（人を含まない）
        await Admin().UpdateAsync(HrAdmin(F), issued.Subscription.Id, null, WebhookStatus.Active, new WebhookSettings(), null, Ct);
        var ping = await Admin().PingAsync(HrAdmin(F), issued.Subscription.Id, null, Ct);
        await RunUntilAsync(dispatcher, () => _receiver.For("pause").Any(r => r.Id == ping.ToString("D")));
        var body = _receiver.For("pause").Single(r => r.Id == ping.ToString("D")).Body;
        Assert.Contains("\"type\":\"ping\"", body, StringComparison.Ordinal);
        Assert.Empty(WebhookPayload.PersonIds(body));
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private sealed record ReceivedEvent(string Type, Guid Id, string EffectiveDate, bool Withdrawn);

    private static WebhookOptions Options() => new()
    {
        Interval = TimeSpan.FromSeconds(1),
        InitialDelay = TimeSpan.Zero,
        RetryBaseDelay = TimeSpan.FromSeconds(30),
    };

    private WebhookDispatcher Dispatcher(WebhookOptions options)
    {
        var ds = db.AppDataSource;
        var audit = new AuditWriter(new NpgsqlAuditSink(ds), TimeProvider.System);
        var changes = new ChangeFeedService(new NpgsqlChangeFeedStore(ds), audit, TimeProvider.System);
        var people = new PersonDirectoryService(new NpgsqlOrgSnapshotProvider(ds), new NpgsqlPolicyRuleStore(ds, TimeProvider.System), audit, TimeProvider.System);
        return new WebhookDispatcher(
            new NpgsqlWebhookStore(ds), new NpgsqlOAuthStore(ds), changes, people, _receiver.Sender(options), _protector, audit, options, _time, null, _cursors);
    }

    private WebhookAdministrationService Admin()
    {
        var ds = db.AppDataSource;
        var audit = new AuditWriter(new NpgsqlAuditSink(ds), TimeProvider.System);
        return new WebhookAdministrationService(
            new NpgsqlWebhookStore(ds),
            new NpgsqlOAuthStore(ds),
            new ChangeFeedService(new NpgsqlChangeFeedStore(ds), audit, TimeProvider.System),
            _protector,
            new AuthAuditor(new NpgsqlAuthStore(ds), TimeProvider.System),
            Options(),
            _time);
    }

    /// <summary>社内システムのクライアント（client credentials、REST API、jinji.read、項目の上限なし）。先行提供の日数は起案と別人の承認で付ける。</summary>
    private async Task<Guid> ClientAsync(string clientId, int leadDays = 0, string[]? grants = null)
    {
        var oauth = new NpgsqlOAuthStore(db.AppDataSource);
        var now = DateTimeOffset.UtcNow;
        var client = new OAuthClient
        {
            Id = EntityId.New(),
            ClientId = clientId,
            Registration = OAuthClientRegistration.Registered,
            Status = OAuthClientStatus.Active,
            DisplayName = "社内システム " + clientId,
            AuthMethod = grants is null ? ClientAuthMethod.PrivateKeyJwt : ClientAuthMethod.None,
            GrantTypes = grants ?? [OAuthGrantTypes.ClientCredentials],
            RedirectUris = grants is null ? [] : ["http://127.0.0.1:43132/callback"],
            Resources = [OAuthResource.Api],
            Scopes = [OAuthScopes.Read],
            Fields = ["*"],
            CreatedAt = now,
            UpdatedAt = now,
        };
        Assert.True(await oauth.CreateClientAsync(AuthActor.Cli, client, Ct));
        if (leadDays > 0)
        {
            var proposal = new DisclosureLead(EntityId.New(), client.Id, leadDays, DisclosureLeadStatus.Proposed, null, "cli", F.Person("hr_admin"), now, null, null, null);
            Assert.True(await oauth.InsertDisclosureLeadAsync(AuthActor.Cli, proposal, Ct));
            Assert.True(await oauth.DecideDisclosureLeadAsync(AuthActor.HrAdmin(F.Person("hr_head")), proposal.Id, true, now, Ct));
        }

        return client.Id;
    }

    private async Task<WebhookSecretIssued> RegisterAsync(Guid clientRef, string path, WebhookSettings? settings = null)
    {
        await WaitUntilPriorChangesPassedAsync();
        return await Admin().RegisterAsync(HrAdmin(F), clientRef, WebhookReceiver.Url(path), settings ?? new WebhookSettings(MinIntervalSeconds: 1), null, Ct);
    }

    /// <summary>
    /// それまでにコミットした変更（フィクスチャの登録・前のテストの変更）が、変更フィードの今の位置より前になるまで待つ。
    /// </summary>
    /// <remarks>
    /// 登録の読み始めの位置は「実行中のどのトランザクションよりも前」（pg_snapshot_xmin）で、同じ PostgreSQL の他のデータベースの
    /// トランザクションでも遅れる。並行して動く他のテストのトランザクションが残っていると、登録より前にコミットした変更
    /// （例: フィクスチャの s2_member の 10 年前からの所属）も受け手に届く（少なくとも 1 回届ける規則のとおりで、取りこぼしはしない）。
    /// このクラスのテストは「登録の後の変更だけが届く」ことを前提に件数や有効日を確かめるので、登録の前に位置が追い付くのを待つ。
    /// </remarks>
    private async Task WaitUntilPriorChangesPassedAsync()
    {
        // 今のトランザクション ID（これより前に始まったトランザクションが終われば、位置はこれ以上になる）
        var marker = Assert.Single(await db.QueryAsAppAsync<long>(null, "SELECT pg_current_xact_id()::text::bigint", Ct));
        var store = new NpgsqlChangeFeedStore(db.AppDataSource);
        var deadline = DateTime.UtcNow + PollTimeout;
        while (await store.GetWatermarkAsync(ViewerContext.ForSystem(Channel.Worker), Ct) < marker)
        {
            Assert.True(DateTime.UtcNow < deadline, "変更フィードの位置が進みませんでした。");
            await Task.Delay(100, Ct);
        }
    }

    /// <summary>受け手が受け取った変更（要約・ping を除く）。</summary>
    private List<ReceivedEvent> Events(string path) =>
        [.. _receiver.For(path).SelectMany(r =>
        {
            using var body = JsonDocument.Parse(r.Body);
            return body.RootElement.TryGetProperty("events", out var events)
                ? events.EnumerateArray().Select(e => new ReceivedEvent(
                    e.GetProperty("type").GetString()!, e.GetProperty("id").GetGuid(), e.GetProperty("effective_date").GetString()!, e.GetProperty("withdrawn").GetBoolean())).ToList()
                : [];
        })];

    /// <summary>条件を満たすまで実行を繰り返す（変更フィードの位置は実行中のトランザクションが終わるまで進まないので、待って回す）。</summary>
    private async Task<IReadOnlyList<ReceivedWebhook>> RunUntilAsync(WebhookDispatcher dispatcher, Func<bool> done)
    {
        var deadline = DateTime.UtcNow + PollTimeout;
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "期待した Webhook が届きませんでした。");
            _time.Advance(TimeSpan.FromSeconds(2));
            var result = await dispatcher.RunOnceAsync(Ct);
            Assert.Empty(result.Failures);
            if (!done())
            {
                await Task.Delay(150, Ct);
            }
        }

        return _receiver.Received;
    }

    private async Task<WebhookDelivery> SingleDeliveryAsync(Guid subscription) =>
        Assert.Single(await Admin().ListDeliveriesAsync(HrAdmin(F), subscription, null, 10, Ct));

    private async Task<Npgsql.NpgsqlConnection> OpenAsHrAdminAsync()
    {
        var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(connection, Ct);
        return connection;
    }

    /// <summary>今日から有効な兼務を足す（今日の変更）。人の ID を返す。</summary>
    private async Task<Guid> InsertTodayAssignmentAsync(Guid person)
    {
        await using var connection = await OpenAsHrAdminAsync();
        await SampleData.InsertAssignmentAsync(connection, person, F.Unit("R1"), "concurrent", $"[{RouteFixtureDatabase.Date(F.Today)},)", Ct);
        return person;
    }

    /// <summary>複数の人の今日の変更を 1 つのトランザクションで足す。</summary>
    private async Task InsertTodayAssignmentsAsync(IEnumerable<string> keys)
    {
        await using var connection = await OpenAsHrAdminAsync();
        await using var tx = await connection.BeginTransactionAsync(Ct);
        foreach (var key in keys)
        {
            await SampleData.InsertAssignmentAsync(connection, F.Person(key), F.Unit("R1"), "concurrent", $"[{RouteFixtureDatabase.Date(F.Today)},)", Ct);
        }

        await tx.CommitAsync(Ct);
    }

    /// <summary><paramref name="days"/> 日後の入社予定の人を足す（<paramref name="withdraw"/> なら同じトランザクションの直後に取り消す）。</summary>
    private async Task<Guid> InsertFutureHireAsync(string employeeNumber, int days, bool withdraw = false)
    {
        var id = EntityId.New();
        var hire = F.Today.AddDays(days);
        await using var connection = await OpenAsHrAdminAsync();
        await SampleData.ExecuteAsync(connection, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, $3)", Ct, id, OrgFixture.Shared.EntityA, employeeNumber);
        await SampleData.ExecuteAsync(
            connection,
            """
            INSERT INTO person_version (id, person_id, family_name, display_name, search_name, work_email, employment_type, hire_date, valid_period)
            VALUES ($1, $2, '予定', '予定 花子', '予定花子', $3, 'regular', $4, daterange($4, NULL))
            """,
            Ct,
            EntityId.New(),
            id,
            $"{employeeNumber.ToLowerInvariant()}@example.co.jp",
            hire);
        await SampleData.InsertAssignmentAsync(connection, id, F.Unit("S3"), "primary", $"[{RouteFixtureDatabase.Date(hire)},)", Ct);
        if (withdraw)
        {
            await WithdrawHireAsync(id);
        }

        return id;
    }

    /// <summary>入社予定の取り消し（人の版・所属の記録期間を閉じる）。</summary>
    private async Task WithdrawHireAsync(Guid person)
    {
        await using var connection = await OpenAsHrAdminAsync();
        await SampleData.ExecuteAsync(connection, "UPDATE person_version SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE person_id = $1 AND upper_inf(recorded_period)", Ct, person);
        await SampleData.ExecuteAsync(connection, "UPDATE assignment SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE person_id = $1 AND upper_inf(recorded_period)", Ct, person);
    }
}
