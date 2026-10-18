using System.Globalization;
using System.Net;
using System.Text.Json;
using JinJi.Application.Presence;
using JinJi.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JinJi.IntegrationTests.Workplace;

/// <summary>
/// 当日の所在の拡張点（<see cref="IPresenceProvider"/>）: jin-ji は所在を保持せず、注入した提供の今日の所在に jin-ji の認可（公開範囲の既定 = 本人と同じ部・
/// 個人単位の例外・クライアントの項目の上限。<c>*</c> は presence を含まない）を適用して返す。時点指定では提供を呼ばない。応答は no-store で、
/// 変更フィード・監査ログに所在を残さない。
/// </summary>
/// <remarks>
/// 提供（<see cref="FixedPresenceProvider"/>）の今日の所在: u1_member → 3F-B01、u2_member → 3F-B02（席は本人だけ = 個人単位の例外）、
/// s2_member → 3F 東（エリアだけ）、dp2_member → 3F-B03（企画部 = 営業部の人には見えない）、b_member → jin-ji に無い席（捨てる）、hr_admin（人事部）→ 3F 西（エリアだけ）。
/// </remarks>
public sealed class PresenceProviderTests(WorkplaceFixtureDatabase db) : IClassFixture<WorkplaceFixtureDatabase>, IAsyncLifetime
{
    private const string Loopback = "http://127.0.0.1:43125/callback";

    /// <summary>項目の上限に presence を明示したクライアント（jin-ji 自身のフロア表示を想定）。</summary>
    private const string FloorClient = "floor-portal";

    private static readonly string[] FloorClientArgs =
    [
        $"--JinJi:Auth:Clients:5:ClientId={FloorClient}",
        "--JinJi:Auth:Clients:5:ActorTypes:0=user",
        "--JinJi:Auth:Clients:5:Fields:0=*",
        "--JinJi:Auth:Clients:5:Fields:1=presence",
    ];

    private readonly TestIssuer _issuer = new();
    private ApiTestHost _host = null!;
    private FixedPresenceProvider _provider = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync()
    {
        _provider = Provider();
        _host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer, FloorClientArgs, Register(_provider));
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _issuer.Dispose();
    }

    private string Token(string person, string client = FloorClient) => _issuer.User(client, F.Person(person));

    private string AdminToken() => _issuer.User(FloorClient, F.Person("hr_admin"), "hr_admin");

    [Fact(DisplayName = "人事管理者: 所在だけは全項目の閲覧の例外を効かせず、同じ部の人（と本人）だけ。別の部の人の所在は階の所在・人の応答のどちらにも出ない")]
    public async Task HrAdmin_PresenceIsNarrowedToo()
    {
        var (status, floor) = await _host.GetJsonAsync($"/api/v1/floors/{db.Floor3F}/presence", AdminToken());
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([F.Person("hr_admin")], floor.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("person").GetProperty("id").GetGuid()));
        var seats = floor.GetProperty("seats").EnumerateArray().ToDictionary(x => x.GetProperty("code").GetString()!, x => x.GetProperty("status").GetString());
        Assert.All(seats.Values, v => Assert.Equal("unavailable", v));

        // 別の部（営業部）の人: 席・階は見えるが、所在は返さない
        var (_, other) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", AdminToken());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("presence").ValueKind);
        Assert.DoesNotContain("presence", Visible(other));
        Assert.Contains("seat", Visible(other));

        // 本人の所在は見える
        var (_, self) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("hr_admin")}", AdminToken());
        Assert.Equal("3F 西", self.GetProperty("presence").GetProperty("zone_name").GetString());
    }

    [Fact(DisplayName = "階の所在: 見える人だけ（既定は本人と同じ部）。席番号は席も見えるときだけ、見えない人の所在の席は unavailable。no-store で今日だけ問い合わせる")]
    public async Task Floor_AppliesAuthorization()
    {
        var changesBefore = await CountAsync("SELECT count(*) FROM change_event");
        using var response = await _host.GetAsync($"/api/v1/floors/{db.Floor3F}/presence", Token("sec1_head"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = document.RootElement;
        Assert.Equal(F.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), root.GetProperty("date").GetString());

        var items = root.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("person").GetProperty("id").GetGuid());
        Assert.Equal(new[] { "u1_member", "u2_member", "s2_member" }.Select(F.Person).Order(), items.Keys.Order());
        Assert.Equal(("3F-B01", "3F 東"), (items[F.Person("u1_member")].GetProperty("seat_code").GetString(), items[F.Person("u1_member")].GetProperty("zone_name").GetString()));
        Assert.Equal(JsonValueKind.Null, items[F.Person("u2_member")].GetProperty("seat_code").ValueKind);
        Assert.Equal("3F 東", items[F.Person("u2_member")].GetProperty("zone_name").GetString());
        Assert.Equal(JsonValueKind.Null, items[F.Person("s2_member")].GetProperty("seat_id").ValueKind);

        var seats = root.GetProperty("seats").EnumerateArray().ToDictionary(s => s.GetProperty("code").GetString()!, s => s.GetProperty("status").GetString());
        Assert.Equal(3, seats.Count);
        Assert.Equal(("occupied", "unavailable", "unavailable"), (seats["3F-B01"], seats["3F-B02"], seats["3F-B03"]));
        Assert.DoesNotContain("vacant", seats.Values);

        // 企画部の dp2_member には、自分（同じ部）だけが見える
        var (_, own) = await _host.GetJsonAsync($"/api/v1/floors/{db.Floor3F}/presence", Token("dp2_member"));
        Assert.Equal([F.Person("dp2_member")], own.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("person").GetProperty("id").GetGuid()));

        // 項目の上限が * だけのクライアントには、所在は見えない（* は presence を含まない）
        var (_, plain) = await _host.GetJsonAsync($"/api/v1/floors/{db.Floor3F}/presence", Token("sec1_head", "portal"));
        Assert.Empty(plain.GetProperty("items").EnumerateArray());

        // 提供への問い合わせは今日だけ。所在は変更フィード・監査ログに残さない
        Assert.All(_provider.Queries, q => Assert.Equal(F.Today, q.Date));
        Assert.Equal(changesBefore, await CountAsync("SELECT count(*) FROM change_event"));
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM audit_event WHERE action LIKE '%presence%'"));

        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/floors/{Guid.NewGuid()}/presence", Token("sec1_head"))).Status);
    }

    [Fact(DisplayName = "人の presence: 1 人の参照で、今日・見える人・presence を明示したクライアントだけ。時点指定では提供を呼ばない。一覧には出さない")]
    public async Task Person_PresenceIsTodayOnly()
    {
        using var response = await _host.GetAsync($"/api/v1/people/{F.Person("u1_member")}", Token("sec1_head"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var presence = document.RootElement.GetProperty("presence");
        Assert.Equal(("3F", "3F-B01", "3F 東"), (presence.GetProperty("floor_label").GetString(), presence.GetProperty("seat_code").GetString(), presence.GetProperty("zone_name").GetString()));
        Assert.Contains("presence", Visible(document.RootElement));

        // * だけのクライアント・別の部の人・時点指定（過去）では返さない
        var (_, plain) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", Token("sec1_head", "portal"));
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("presence").ValueKind);
        var (_, other) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("dp2_member")}", Token("sec1_head"));
        Assert.Equal(JsonValueKind.Null, other.GetProperty("presence").ValueKind);
        Assert.DoesNotContain("presence", Visible(other));

        var calls = _provider.Queries.Count;
        var yesterday = F.Today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (_, past) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}?as_of={yesterday}", Token("sec1_head"));
        Assert.Equal(JsonValueKind.Null, past.GetProperty("presence").ValueKind);
        Assert.Equal(calls, _provider.Queries.Count);

        // 一覧・一括取得には出さない（提供も呼ばない）
        var (_, batch) = await _host.GetJsonAsync($"/api/v1/people?ids={F.Person("u1_member")}", Token("sec1_head"));
        Assert.All(batch.GetProperty("items").EnumerateArray(), p => Assert.DoesNotContain("presence", Visible(p)));
        Assert.Equal(calls, _provider.Queries.Count);
    }

    [Fact(DisplayName = "MCP: presence は項目の上限に presence を明示したクライアントの get_person だけ。* のクライアント・階の在籍者には出さない")]
    public async Task Mcp_PresenceOnlyForExplicitClients()
    {
        var provider = Provider();
        await using var env = await OAuthTestEnvironment.StartAsync(db, configureServices: Register(provider));
        await env.RegisterPublicClientAsync("presence-agent", Loopback, fields: "*,presence", mcpTools: "*");
        await env.RegisterPublicClientAsync("plain-agent", Loopback, fields: "*", mcpTools: "*");
        var explicitClient = new McpTestClient(env.Host.McpPort, await env.AccessTokenAsync(F.Person("sec1_head"), "presence-agent", Loopback));
        var plainClient = new McpTestClient(env.Host.McpPort, await env.AccessTokenAsync(F.Person("sec1_head"), "plain-agent", Loopback));

        var person = (await explicitClient.CallAsync("get_person", new { person_id = F.Person("u1_member") })).Structured.GetProperty("person");
        Assert.Equal("3F-B01", person.GetProperty("presence").GetProperty("seat_code").GetString());

        var plain = (await plainClient.CallAsync("get_person", new { person_id = F.Person("u1_member") })).Structured.GetProperty("person");
        Assert.False(plain.TryGetProperty("presence", out var none) && none.ValueKind != JsonValueKind.Null);

        var occupants = (await explicitClient.CallAsync("list_floor_occupants", new { floor_id = db.Floor3F })).Structured.GetProperty("items");
        Assert.All(occupants.EnumerateArray(), o => Assert.False(o.GetProperty("person").TryGetProperty("presence", out var p) && p.ValueKind != JsonValueKind.Null));
        Assert.All(provider.Queries, q => Assert.Equal(F.Today, q.Date));

        // 人事管理者の委任でも、別の部の人の所在は返さない
        await env.RegisterPublicClientAsync("presence-admin-agent", Loopback, fields: "*,presence", mcpTools: "*");
        var adminClient = new McpTestClient(env.Host.McpPort, await env.AccessTokenAsync(F.Person("hr_admin"), "presence-admin-agent", Loopback));
        var byAdmin = (await adminClient.CallAsync("get_person", new { person_id = F.Person("u1_member") })).Structured.GetProperty("person");
        Assert.False(byAdmin.TryGetProperty("presence", out var hidden) && hidden.ValueKind != JsonValueKind.Null);
        Assert.True(byAdmin.TryGetProperty("seat", out _));
        var own = (await adminClient.CallAsync("get_person", new { person_id = F.Person("hr_admin") })).Structured.GetProperty("person");
        Assert.Equal("3F 西", own.GetProperty("presence").GetProperty("zone_name").GetString());
    }

    private FixedPresenceProvider Provider() => new(
    [
        new PresenceEntry(F.Person("u1_member"), db.Floor3F, db.Seat("B01"), null),
        new PresenceEntry(F.Person("u2_member"), db.Floor3F, db.Seat("B02"), null),
        new PresenceEntry(F.Person("s2_member"), db.Floor3F, null, db.ZoneEast),
        new PresenceEntry(F.Person("dp2_member"), db.Floor3F, db.Seat("B03"), null),
        new PresenceEntry(F.Person("b_member"), db.Floor3F, Guid.NewGuid(), null),
        new PresenceEntry(F.Person("hr_admin"), db.Floor3F, null, db.ZoneWest),
    ]);

    private static Action<IServiceCollection> Register(FixedPresenceProvider provider) =>
        services => services.Replace(ServiceDescriptor.Singleton<IPresenceProvider>(provider));

    private async Task<long> CountAsync(string sql) => (await db.QueryAsAppAsync<long>(Policy.ViewerContext.ForSystem(Policy.Channel.Cli), sql, Ct)).Single();

    private static List<string?> Visible(JsonElement person) => [.. person.GetProperty("visible_fields").EnumerateArray().Select(v => v.GetString())];
}
