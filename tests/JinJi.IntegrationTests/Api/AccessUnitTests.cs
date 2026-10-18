using System.Security.Claims;
using JinJi.Application.Access;
using JinJi.Application.Audit;
using JinJi.Application.Changes;
using JinJi.Domain;
using JinJi.Host;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;
using Microsoft.Extensions.Configuration;

namespace JinJi.IntegrationTests.Api;

/// <summary>入口の共通処理の部品（閲覧者の対応付け・不正検知・返却人数・変更フィードの位置）の単体テスト（DB を使わない）。</summary>
public sealed class AccessUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>日本時間の指定日時。</summary>
    private static DateTimeOffset Jst(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, BusinessDate.UtcOffset);

    [Fact(DisplayName = "閲覧者の対応付け: 人・AI エージェント・社内システムを区別し、項目上限はクライアントの登録から取る")]
    public void ViewerFactory_MapsActors()
    {
        var factory = new ViewerContextFactory(Options());
        var person = Guid.NewGuid();

        var user = factory.Create(Principal(("iss", "https://a"), ("sub", person.ToString()), ("client_id", "portal"), ("roles", "hr_admin")), Channel.Api, "r1");
        Assert.Equal(ActorType.User, user.Actor);
        Assert.Equal(person, user.PersonId);
        Assert.True(user.IsHrAdmin);
        Assert.Equal("portal", user.ClientId);
        // 項目の上限の * は全項目だが、当日の所在（presence）は含めない（明示したクライアントにだけ渡す）
        Assert.True(user.FieldLimit.Allows(PersonFields.WorkEmail));
        Assert.True(user.FieldLimit.Allows(PersonFields.Seat));
        Assert.False(user.FieldLimit.Allows(PersonFields.Presence));

        var agent = factory.Create(Principal(("iss", "https://a"), ("sub", person.ToString()), ("client_id", "agent-x"), ("act", "{\"sub\":\"agent-x\"}")), Channel.Mcp, null);
        Assert.Equal(ActorType.Agent, agent.Actor);
        Assert.Equal(person, agent.DelegatorPersonId);
        Assert.Null(agent.PersonId);

        var service = factory.Create(Principal(("iss", "https://a"), ("sub", "attendance"), ("azp", "attendance")), Channel.Api, null);
        Assert.Equal(ActorType.Service, service.Actor);
        Assert.True(service.FieldLimit.Allows(PersonFields.EmployeeNumber));
        Assert.False(service.FieldLimit.Allows(PersonFields.WorkEmail));

        // Entra ID のアプリのトークン: sub はサービスプリンシパルの UUID でも社内システム
        var entraApp = factory.Create(
            Principal(("iss", "https://b"), ("sub", Guid.NewGuid().ToString()), ("azp", "attendance"), ("idtyp", "app"), ("roles", "hr_admin")), Channel.Api, null);
        Assert.Equal(ActorType.Service, entraApp.Actor);
        Assert.False(entraApp.IsHrAdmin);
    }

    [Theory(DisplayName = "閲覧者の対応付け: 未登録の発行者・クライアント、受け付けない種別、不正な act、MCP の社内システムは拒否")]
    [InlineData("https://unknown", "portal", "PERSON", null)]
    [InlineData("https://a", "unknown", "PERSON", null)]
    [InlineData("https://a", "portal", "portal", null)]
    [InlineData("https://a", "attendance", "PERSON", null)]
    [InlineData("https://a", "agent-x", "PERSON", "not-json")]
    [InlineData("https://b", "portal", "PERSON", null)]
    public void ViewerFactory_Rejects(string issuer, string client, string subject, string? act)
    {
        var factory = new ViewerContextFactory(Options());
        List<(string, string)> claims = [("iss", issuer), ("client_id", client), ("sub", subject == "PERSON" ? Guid.NewGuid().ToString() : subject)];
        if (act is not null)
        {
            claims.Add(("act", act));
        }

        Assert.Throws<ViewerRejectedException>(() => factory.Create(Principal([.. claims]), Channel.Api, null));
        Assert.Throws<ViewerRejectedException>(() => factory.Create(Principal(("iss", "https://a"), ("sub", "attendance"), ("client_id", "attendance")), Channel.Mcp, null));
    }

    [Fact(DisplayName = "閲覧者の対応付け: 信用しない発行者のロールは使わない")]
    public void ViewerFactory_IgnoresUntrustedRoles()
    {
        var factory = new ViewerContextFactory(Options());
        var viewer = factory.Create(Principal(("iss", "https://c"), ("sub", Guid.NewGuid().ToString()), ("client_id", "portal"), ("roles", "hr_admin")), Channel.Api, null);
        Assert.False(viewer.IsHrAdmin);
    }

    [Fact(DisplayName = "設定: 不正な発行者・クライアントの設定では起動しない")]
    public void AccessOptions_Validate()
    {
        Assert.Throws<ArgumentException>(() => new AccessOptions { Issuers = [new TokenIssuerOptions { Issuer = "https://a", Audiences = ["x"], ActorTypes = ["user"] }] }.Validate());
        Assert.Throws<ArgumentException>(() => new AccessOptions { Clients = [new ApiClientOptions { ClientId = "c", ActorTypes = ["robot"], Fields = ["*"] }] }.Validate());
        Assert.Throws<ArgumentException>(() => new AccessOptions { Clients = [new ApiClientOptions { ClientId = "c", ActorTypes = ["user"], Fields = ["salary"] }] }.Validate());
        Assert.Throws<ArgumentException>(() => new RateLimitOptions { Api = new RateLimitProfile { PerEndpoint = new() { ["unknown"] = 1 } } }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new RateLimitOptions { Api = new RateLimitProfile { PerUser = 0 } }.Validate());
    }

    [Fact(DisplayName = "不正検知: 業務時間外（日本時間）の大量参照は低いしきい値で検知する。業務時間内は同じ量で検知しない")]
    public void Abuse_OffHours()
    {
        var options = new AbuseOptions { PeopleReturnedThreshold = 1000, OffHours = new OffHoursOptions { PeopleReturnedThreshold = 100 } };

        var daytime = new ManualTimeProvider(Jst(2026, 10, 14, 11)); // 水曜 11 時
        var day = new AbuseDetector(options, daytime);
        Assert.Null(day.Observe("c", new AbuseObservation(200, 150, false)));

        var night = new ManualTimeProvider(Jst(2026, 10, 14, 23)); // 水曜 23 時
        var detector = new AbuseDetector(options, night);
        var transition = detector.Observe("c", new AbuseObservation(200, 150, false));
        Assert.Equal(AbuseRules.OffHoursRead, transition?.Rule);
        Assert.Equal(AbuseState.Throttled, transition?.To);

        var weekend = new AbuseDetector(options, new ManualTimeProvider(Jst(2026, 10, 17, 11))); // 土曜 11 時
        Assert.Equal(AbuseRules.OffHoursRead, weekend.Observe("c", new AbuseObservation(200, 150, false))?.Rule);

        Assert.True(new OffHoursOptions().Contains(Jst(2026, 10, 14, 6, 59)));
        Assert.False(new OffHoursOptions().Contains(Jst(2026, 10, 14, 7)));
        Assert.True(new OffHoursOptions().Contains(Jst(2026, 10, 14, 20)));
    }

    [Fact(DisplayName = "不正検知: 大量の個人参照で自動スロットル。期限が過ぎれば通常に戻り、スロットル中の再発は一時停止。一時停止は自動では解けない")]
    public void Abuse_StagesAndExpiry()
    {
        var time = new ManualTimeProvider(Jst(2026, 10, 14, 11));
        var detector = new AbuseDetector(new AbuseOptions { PeopleReturnedThreshold = 100, ThrottleDuration = TimeSpan.FromMinutes(30) }, time);

        Assert.Null(detector.Observe("c", new AbuseObservation(200, 60, false)));
        Assert.Equal(AbuseRules.MassRead, detector.Observe("c", new AbuseObservation(200, 60, false))?.Rule);
        Assert.Equal(AbuseState.Throttled, detector.StateOf("c"));
        Assert.Equal(AbuseState.Normal, detector.StateOf("other"));

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(AbuseState.Normal, detector.StateOf("c"));

        Assert.Equal(AbuseState.Throttled, detector.Observe("c", new AbuseObservation(200, 100, false))?.To);
        Assert.Equal(AbuseState.Suspended, detector.Observe("c", new AbuseObservation(200, 100, false))?.To);
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(AbuseState.Suspended, detector.StateOf("c"));
        Assert.Null(detector.Observe("c", new AbuseObservation(404, 0, false)));

        Assert.Equal(AbuseState.Normal, detector.Release("c")?.To);
        Assert.Equal(AbuseState.Normal, detector.StateOf("c"));
        Assert.Null(detector.Release("c"));
    }

    [Fact(DisplayName = "不正検知: 窓の外の数は数えない")]
    public void Abuse_WindowSlides()
    {
        var time = new ManualTimeProvider(Jst(2026, 10, 14, 11));
        var detector = new AbuseDetector(new AbuseOptions { DeniedThreshold = 3, Window = TimeSpan.FromMinutes(5) }, time);

        Assert.Null(detector.Observe("c", new AbuseObservation(404, 0, false)));
        Assert.Null(detector.Observe("c", new AbuseObservation(403, 0, false)));
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(detector.Observe("c", new AbuseObservation(404, 0, false)));
        Assert.Null(detector.Observe("c", new AbuseObservation(200, 0, false)));
        Assert.Null(detector.Observe("c", new AbuseObservation(404, 0, false)));
        Assert.Equal(AbuseRules.DeniedBurst, detector.Observe("c", new AbuseObservation(404, 0, false))?.Rule);
    }

    [Fact(DisplayName = "返却人数の制限: 利用者とクライアントの 2 段で数え、窓から外れれば戻る")]
    public void ReturnedPeople_SlidingWindow()
    {
        var time = new ManualTimeProvider(Jst(2026, 10, 14, 11));
        var limiter = new ReturnedPeopleLimiter(
            new RateLimitOptions { Api = new RateLimitProfile { ReturnedPeoplePerUser = 10, ReturnedPeoplePerClient = 15, ReturnedPeopleWindow = TimeSpan.FromHours(1) } },
            time);

        limiter.Add(Channel.Api, "c", "u1", 6);
        Assert.Null(limiter.RetryAfter(Channel.Api, "c", "u1"));
        time.Advance(TimeSpan.FromMinutes(10));
        limiter.Add(Channel.Api, "c", "u1", 4);
        Assert.Equal(TimeSpan.FromMinutes(50), limiter.RetryAfter(Channel.Api, "c", "u1"));
        Assert.Null(limiter.RetryAfter(Channel.Api, "c", "u2"));
        Assert.Null(limiter.RetryAfter(Channel.Mcp, "c", "u1"));

        limiter.Add(Channel.Api, "c", "u2", 5);
        Assert.NotNull(limiter.RetryAfter(Channel.Api, "c", "u3"));

        time.Advance(TimeSpan.FromMinutes(51));
        Assert.Null(limiter.RetryAfter(Channel.Api, "c", "u1"));
    }

    [Fact(DisplayName = "変更フィード: 見せてよい日が来た行は、既に読み進めた位置より前でも拾い直す（重複・取りこぼしなし）")]
    public async Task ChangeFeed_DeliversDeferredChangesWhenDue()
    {
        var time = new ManualTimeProvider(Jst(2026, 10, 14, 11));
        var today = BusinessDate.Today(time);
        var store = new FakeChangeFeedStore();
        var service = new ChangeFeedService(store, new AuditWriter(new RecordingAuditSink(), time), time);
        var viewer = ViewerContext.ForService("sync", FieldLimit.Unrestricted);
        var admin = ViewerContext.ForUser(Guid.NewGuid(), [Roles.HrAdmin], Channel.Api, FieldLimit.Unrestricted);

        var start = (await service.ReadAsync(viewer, null, 10, Ct)).Next;
        var adminStart = (await service.ReadAsync(admin, null, 10, Ct)).Next;
        var now = store.Add(today);                 // 今日から有効
        var later = store.Add(today.AddDays(3));    // 3 日後に有効（内示）
        var past = store.Add(today.AddDays(-1));

        var first = await ReadAllAsync(service, viewer, start);
        Assert.Equal([now, past], first.Items);
        Assert.Equal([now, later, past], (await ReadAllAsync(service, admin, adminStart)).Items);

        time.Advance(TimeSpan.FromDays(2));
        var second = await ReadAllAsync(service, viewer, first.Next);
        Assert.Empty(second.Items);

        time.Advance(TimeSpan.FromDays(1));
        var third = await ReadAllAsync(service, viewer, second.Next);
        Assert.Equal([later], third.Items);
        Assert.Empty((await ReadAllAsync(service, viewer, third.Next)).Items);
    }

    [Fact(DisplayName = "変更フィード: 小さなページで読み進めても、拾い直しと新しい行を重複・取りこぼしなく返す")]
    public async Task ChangeFeed_PagesThroughDeferredAndNew()
    {
        var time = new ManualTimeProvider(Jst(2026, 10, 14, 11));
        var today = BusinessDate.Today(time);
        var store = new FakeChangeFeedStore();
        var service = new ChangeFeedService(store, new AuditWriter(new RecordingAuditSink(), time), time);
        var viewer = ViewerContext.ForService("sync", FieldLimit.Unrestricted);
        var start = (await service.ReadAsync(viewer, null, 10, Ct)).Next;

        var deferred = Enumerable.Range(0, 5).Select(_ => store.Add(today.AddDays(1))).ToList();
        var first = await ReadAllAsync(service, viewer, start, pageSize: 2);
        Assert.Empty(first.Items);

        time.Advance(TimeSpan.FromDays(1));
        var fresh = Enumerable.Range(0, 3).Select(_ => store.Add(BusinessDate.Today(time))).ToList();
        var second = await ReadAllAsync(service, viewer, first.Next, pageSize: 2);
        Assert.Equal([.. deferred, .. fresh], second.Items);
    }

    [Fact(DisplayName = "変更フィード: 先行提供の社内システムには見せてよい日の N 日前から返す。N 日前の前日には返さない。日数が減ったら、戻した間の行を改めて返す")]
    public async Task ChangeFeed_DisclosureLead()
    {
        var time = new ManualTimeProvider(Jst(2026, 10, 14, 11));
        var today = BusinessDate.Today(time);
        var store = new FakeChangeFeedStore();
        var service = new ChangeFeedService(store, new AuditWriter(new RecordingAuditSink(), time), time);
        var client = Guid.NewGuid();
        var lead5 = ViewerContext.ForService("wf", FieldLimit.Unrestricted, null, client, 5);
        var start = (await service.ReadAsync(lead5, null, 10, Ct)).Next;

        var inFive = store.Add(today.AddDays(5));
        var inSix = store.Add(today.AddDays(6));
        var first = await ReadAllAsync(service, lead5, start);
        Assert.Equal([inFive], first.Items);

        // 翌日: 6 日後だった行が 5 日後になり、拾い直して返す
        time.Advance(TimeSpan.FromDays(1));
        var second = await ReadAllAsync(service, lead5, first.Next);
        Assert.Equal([inSix], second.Items);

        // 日数が 2 に減った: 上限を戻す。戻した間（3〜4 日後）の行は、上限がそこに達したときに改めて返す（重複は害にならない）
        var lead2 = ViewerContext.ForService("wf", FieldLimit.Unrestricted, null, client, 2);
        var reduced = await ReadAllAsync(service, lead2, second.Next);
        Assert.Empty(reduced.Items);
        Assert.Equal(BusinessDate.Today(time).AddDays(2), reduced.Next.ActivatedThrough);
        time.Advance(TimeSpan.FromDays(2));
        Assert.Equal([inFive], (await ReadAllAsync(service, lead2, reduced.Next)).Items);

        // 先行提供は社内システムにだけ設定できる（人・AI エージェントには日数の引数が無い。api_client が要る）
        Assert.Throws<ArgumentException>(() => ViewerContext.ForService("wf", FieldLimit.Unrestricted, null, null, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => ViewerContext.ForService("wf", FieldLimit.Unrestricted, null, client, ViewerContext.MaxDisclosureLeadDays + 1));
        Assert.Equal(today.AddDays(5), lead5.DisclosureHorizon(today));
        Assert.Equal(today, lead5.WithoutDisclosureLead().DisclosureHorizon(today));
    }

    [Fact(DisplayName = "Host: API の役割で DB の接続文字列が無ければ、起動時に何を設定すればよいかを示して止まる")]
    public void Host_RequiresDatabaseForApi()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ServerHost.BuildWeb(
            HostRole.Api,
            ["--JINJI_PG_APP_PASSWORD=", "--ConnectionStrings:App=", "--JinJi:Listen:ApiPort=0", "--JinJi:Listen:MetricsPort=0"]));
        Assert.Contains("ConnectionStrings:App", ex.Message, StringComparison.Ordinal);

        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        Assert.Throws<InvalidOperationException>(() => DatabaseRegistration.RequireAppConnectionString(config));
    }

    private static async Task<(List<Guid> Items, ChangeFeedPosition Next)> ReadAllAsync(
        ChangeFeedService service, ViewerContext viewer, ChangeFeedPosition from, int pageSize = 10)
    {
        var items = new List<Guid>();
        var position = from;
        for (var i = 0; i < 100; i++)
        {
            var page = await service.ReadAsync(viewer, position, pageSize, Ct);
            items.AddRange(page.Items.Select(e => e.EntityId));
            position = page.Next;
            if (!page.HasMore)
            {
                return (items, position);
            }
        }

        throw new InvalidOperationException("終わりません。");
    }

    private static AccessOptions Options() => new()
    {
        Issuers =
        [
            new TokenIssuerOptions { Issuer = "https://a", Audiences = ["api"], SigningKeys = [new SigningKeyOptions { PublicKeyPem = "x" }], ActorTypes = ["user", "agent", "service"], TrustRoles = true },
            new TokenIssuerOptions { Issuer = "https://b", Audiences = ["api"], SigningKeys = [new SigningKeyOptions { PublicKeyPem = "x" }], ActorTypes = ["service"] },
            new TokenIssuerOptions { Issuer = "https://c", Audiences = ["api"], SigningKeys = [new SigningKeyOptions { PublicKeyPem = "x" }], ActorTypes = ["user"] },
        ],
        Clients =
        [
            new ApiClientOptions { ClientId = "portal", ActorTypes = ["user"], Fields = ["*"] },
            new ApiClientOptions { ClientId = "agent-x", ActorTypes = ["agent"], Fields = ["*"] },
            new ApiClientOptions { ClientId = "attendance", ActorTypes = ["service"], Fields = [PersonFields.EmployeeNumber, PersonFields.DisplayName] },
        ],
    };

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test"));

    /// <summary>メモリ上の変更の記録。トランザクション ID は追加の順に 1 ずつ増え、すべてコミット済みとする。</summary>
    private sealed class FakeChangeFeedStore : IChangeFeedStore
    {
        private readonly List<StoredChange> _rows = [];

        public Guid Add(DateOnly effective)
        {
            var id = Guid.NewGuid();
            _rows.Add(new StoredChange(_rows.Count + 10, 1, ChangeEntityType.Person, id, effective));
            return id;
        }

        public Task<long> GetWatermarkAsync(ViewerContext viewer, CancellationToken cancellationToken) => Task.FromResult(Watermark);

        public Task<NewChanges> ReadNewAsync(ViewerContext viewer, (long Xid, long Id) after, DateOnly? effectiveThrough, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new NewChanges(
                [.. _rows.Where(r => After(r, after) && (effectiveThrough is null || r.EffectiveDate <= effectiveThrough)).Take(limit)],
                Watermark));

        public Task<IReadOnlyList<StoredChange>> ReadDeferredAsync(
            ViewerContext viewer, (long Xid, long Id) upTo, (long Xid, long Id) after, DateOnly effectiveAfter, DateOnly effectiveThrough, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StoredChange>>(
                [.. _rows.Where(r => !After(r, upTo) && After(r, after) && r.EffectiveDate > effectiveAfter && r.EffectiveDate <= effectiveThrough).Take(limit)]);

        private long Watermark => _rows.Count + 9;

        private static bool After(StoredChange row, (long Xid, long Id) position) =>
            row.TransactionId > position.Xid || (row.TransactionId == position.Xid && row.Sequence > position.Id);
    }
}
