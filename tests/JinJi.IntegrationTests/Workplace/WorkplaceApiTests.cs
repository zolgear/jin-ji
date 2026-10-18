using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Workplace;

/// <summary>
/// 建物・フロアモードの REST API（0019。仕様 §6.1）: 人の応答の workplace・seat、建物・階の一覧の件数、配置図の席の状態（見えない人の席は使えない席）、
/// 階の在籍者、席の検索、時点指定の線（将来の席替えは内示と同じく人事管理者だけ）、OpenAPI の文書との一致。当日の所在の提供が無い導入先（既定）。
/// </summary>
/// <remarks>フィクスチャは <see cref="WorkplaceFixtureDatabase"/>。u2_member の席は個人単位の例外で本人だけ。</remarks>
public sealed class WorkplaceApiTests(WorkplaceFixtureDatabase db) : IClassFixture<WorkplaceFixtureDatabase>, IAsyncLifetime
{
    private readonly TestIssuer _issuer = new();
    private ApiTestHost _host = null!;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _issuer.Dispose();
    }

    private string UserToken(string key) => _issuer.User("portal", F.Person(key));

    private string AdminToken() => _issuer.User("portal", F.Person("hr_admin"), "hr_admin");

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact(DisplayName = "人: workplace は席・ゾーンの割り当て、seat は主の席。割り当ての無い人・見えない席は null で visible_fields に無い")]
    public async Task Person_WorkplaceAndSeat()
    {
        var (_, u1) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", UserToken("sec1_head"));
        var seat = u1.GetProperty("seat");
        Assert.Equal(("3F-A01", "3F", 2000), (seat.GetProperty("code").GetString(), seat.GetProperty("floor_label").GetString(), seat.GetProperty("x_mm").GetInt32()));
        var workplace = u1.GetProperty("workplace");
        Assert.Equal(("本館", "3F", "3F 西", "seat"), (
            workplace.GetProperty("building_name").GetString(), workplace.GetProperty("floor_label").GetString(), workplace.GetProperty("zone_name").GetString(),
            workplace.GetProperty("source").GetString()));
        Assert.False(workplace.TryGetProperty("inherited", out _));

        // 席の割り当ての無い sec1_head は、階・席とも無い（組織から引き継がない）
        var (_, head) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("sec1_head")}", UserToken("u1_member"));
        Assert.Equal(JsonValueKind.Null, head.GetProperty("workplace").ValueKind);
        Assert.Equal(JsonValueKind.Null, head.GetProperty("seat").ValueKind);

        // u2_member の席は本人だけ（個人単位の例外）。階・エリアは見える
        var (_, hidden) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}", UserToken("sec1_head"));
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("seat").ValueKind);
        Assert.DoesNotContain("seat", Visible(hidden));
        Assert.Contains("workplace", Visible(hidden));
        var (_, self) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u2_member")}", UserToken("u2_member"));
        Assert.Equal("3F-A02", self.GetProperty("seat").GetProperty("code").GetString());

        // 当日の所在は提供が無いので返さない（項目にも入れない）
        Assert.DoesNotContain("presence", Visible(u1));
        Assert.Equal(JsonValueKind.Null, u1.GetProperty("presence").ValueKind);

        // 勤怠向けの社内システム（項目の上限に席が無い）には返さない（新しい項目を黙って渡さない）
        var (_, service) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", _issuer.Service("attendance"));
        Assert.DoesNotContain("seat", Visible(service));
        Assert.DoesNotContain("workplace", Visible(service));
    }

    [Fact(DisplayName = "建物・階の一覧: 件数は閲覧者に見える人だけ。見えない人の席・保守中の席は使えない席に数え、空席に数えない")]
    public async Task Buildings_CountsByViewer()
    {
        var (status, list) = await _host.GetJsonAsync($"/api/v1/sites/{F.SiteTokyo.Id}/buildings", UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.OK, status);
        var building = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(("MAIN", "本館"), (building.GetProperty("code").GetString(), building.GetProperty("name").GetString()));
        var floors = building.GetProperty("floors").EnumerateArray().ToList();
        Assert.Equal(["1F", "3F"], floors.Select(f => f.GetProperty("label").GetString()));
        Assert.Equal(JsonValueKind.Null, floors[0].GetProperty("layout_id").ValueKind);

        // 3F: 8 席。使用中 = A01・S01（u1・s2/dp2）、使えない = A02（u2 の席は見えない）・A04（保守中）、空席 = A03・B01〜B03
        var f3 = floors[1];
        Assert.Equal((8, 2, 4, 2), (f3.GetProperty("seat_count").GetInt32(), f3.GetProperty("occupied_count").GetInt32(), f3.GetProperty("vacant_count").GetInt32(),
            f3.GetProperty("unavailable_count").GetInt32()));
        Assert.False(f3.TryGetProperty("presence_vacant_count", out _));

        // 本人（u2_member）には自分の席が使用中に見える
        var (_, own) = await _host.GetJsonAsync($"/api/v1/buildings/{db.Building}", UserToken("u2_member"));
        var ownFloor = own.GetProperty("building").GetProperty("floors").EnumerateArray().Single(f => f.GetProperty("label").GetString() == "3F");
        Assert.Equal((3, 1), (ownFloor.GetProperty("occupied_count").GetInt32(), ownFloor.GetProperty("unavailable_count").GetInt32()));

        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/sites/{Guid.NewGuid()}/buildings", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync("/api/v1/buildings/not-a-uuid", UserToken("sec1_head"))).Status);
    }

    [Fact(DisplayName = "配置図: 席の状態は空席・使用中・使えない席の 3 つで、人は返さない。ETag が一致すれば 304")]
    public async Task Layout_StatusesAndETag()
    {
        using var response = await _host.GetAsync($"/api/v1/floors/{db.Floor3F}/layout", UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("private", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var layout = document.RootElement.GetProperty("layout");
        var statuses = layout.GetProperty("seats").EnumerateArray().ToDictionary(s => s.GetProperty("code").GetString()!, s => s.GetProperty("status").GetString());
        Assert.Equal("occupied", statuses["3F-A01"]);
        Assert.Equal("unavailable", statuses["3F-A02"]);
        Assert.Equal("vacant", statuses["3F-A03"]);
        Assert.Equal("unavailable", statuses["3F-A04"]);
        Assert.DoesNotContain("3F-A05", statuses.Keys);
        Assert.DoesNotContain("person", document.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(4, layout.GetProperty("outline").GetArrayLength());
        var zones = layout.GetProperty("zones").EnumerateArray().ToDictionary(z => z.GetProperty("name").GetString()!, z => z.GetProperty("kind").GetString());
        Assert.Equal(("office", "office", "restricted"), (zones["3F 西"], zones["3F 東"], zones["3F 書庫"]));

        using var again = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/floors/{db.Floor3F}/layout");
        again.Headers.Authorization = new AuthenticationHeaderValue("Bearer", UserToken("sec1_head"));
        again.Headers.IfNoneMatch.Add(response.Headers.ETag!);
        using var cached = await _host.Http.SendAsync(again, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);

        // 1F は配置図が無い
        var (_, lobby) = await _host.GetJsonAsync($"/api/v1/floors/{db.Floor1F}/layout", UserToken("sec1_head"));
        Assert.Equal(JsonValueKind.Null, lobby.GetProperty("layout").ValueKind);
    }

    [Fact(DisplayName = "時点: 将来の席替え・改訂は人事管理者の未来の時点だけ（それ以外は 403）。過去の時点は誰でも見られる")]
    public async Task AsOf_FutureLine()
    {
        var future = Iso(db.RevisionDate.AddDays(1));
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.GetJsonAsync($"/api/v1/floors/{db.Floor3F}/layout?as_of={future}", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.GetJsonAsync($"/api/v1/floors/{db.Floor3F}/occupants?as_of={future}", UserToken("sec1_head"))).Status);

        var (_, admin) = await _host.GetJsonAsync($"/api/v1/floors/{db.Floor3F}/layout?as_of={future}", AdminToken());
        var statuses = admin.GetProperty("layout").GetProperty("seats").EnumerateArray().ToDictionary(s => s.GetProperty("code").GetString()!, s => s.GetProperty("status").GetString());
        Assert.Equal(("vacant", "occupied", "vacant"), (statuses["3F-A01"], statuses["3F-A03"], statuses["3F-A05"]));
        var (_, person) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}?as_of={Iso(db.MoveDate)}", AdminToken());
        Assert.Equal("3F-A03", person.GetProperty("seat").GetProperty("code").GetString());

        // 今日の席は、席替えの予定（終了日）を推測させない: 今日の A03 は空席
        var (_, today) = await _host.GetJsonAsync($"/api/v1/floors/{db.Floor3F}/layout", UserToken("u1_member"));
        Assert.Equal("vacant", today.GetProperty("layout").GetProperty("seats").EnumerateArray().Single(s => s.GetProperty("code").GetString() == "3F-A03").GetProperty("status").GetString());

        var (pastStatus, _) = await _host.GetJsonAsync($"/api/v1/floors/{db.Floor3F}/layout?as_of={Iso(F.Today.AddDays(-30))}", UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.OK, pastStatus);
    }

    [Fact(DisplayName = "階の在籍者: 席の人は席が見える人だけ。席の割り当ての無い人は出ない。席番号の順で、cursor で続きを取る")]
    public async Task Occupants_Paging()
    {
        var all = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var path = $"/api/v1/floors/{db.Floor3F}/occupants?limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var (status, page) = await _host.GetJsonAsync(path, UserToken("sec1_head"));
            Assert.Equal(HttpStatusCode.OK, status);
            all.AddRange(page.GetProperty("items").EnumerateArray());
            cursor = page.GetProperty("next_cursor").ValueKind == JsonValueKind.Null ? null : page.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null);

        var bySeat = all.Where(o => o.GetProperty("source").GetString() == "seat").Select(o => (o.GetProperty("seat_code").GetString(), o.GetProperty("person").GetProperty("id").GetGuid())).ToList();
        Assert.Equal(F.Person("u1_member"), bySeat.Single(s => s.Item1 == "3F-A01").Item2);
        Assert.DoesNotContain(bySeat, s => s.Item2 == F.Person("u2_member"));
        Assert.Equal(2, bySeat.Count(s => s.Item1 == "3F-S01"));
        Assert.Equal(bySeat.Select(s => s.Item1).Order(StringComparer.Ordinal), bySeat.Select(s => s.Item1));

        // 割り当ての無い人（sec1_head など）は在籍者に出ない（組織から引き継がない）
        Assert.All(all, o => Assert.Equal("seat", o.GetProperty("source").GetString()));
        Assert.DoesNotContain(all, o => o.GetProperty("person").GetProperty("id").GetGuid() == F.Person("sec1_head"));

        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/floors/{db.Floor3F}/occupants?cursor=forged", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/floors/{Guid.NewGuid()}/occupants", UserToken("sec1_head"))).Status);
    }

    [Fact(DisplayName = "席の検索: 空席（割り当ての無い席）だけ・種類・設備で絞る。範囲の無い検索・未知の値は 400。所在の提供が無ければ階の所在は 404、予約の口は無い")]
    public async Task Seats_Search()
    {
        var (_, vacant) = await _host.GetJsonAsync($"/api/v1/seats?floor={db.Floor3F}&vacant=true", UserToken("sec1_head"));
        Assert.Equal(["3F-A03", "3F-B01", "3F-B02", "3F-B03"], vacant.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("code").GetString()));
        Assert.False(vacant.TryGetProperty("presence_date", out _));

        var (_, monitors) = await _host.GetJsonAsync($"/api/v1/seats?building={db.Building}&kind=fixed&feature=monitor&limit=2", UserToken("sec1_head"));
        Assert.Equal(["3F-B01", "3F-B02"], monitors.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("code").GetString()));
        Assert.True(monitors.GetProperty("has_more").GetBoolean());
        var (_, stations) = await _host.GetJsonAsync($"/api/v1/seats?floor={db.Floor3F}&kind=station", UserToken("sec1_head"));
        Assert.Equal(["3F-S01"], stations.GetProperty("items").EnumerateArray().Select(s => s.GetProperty("code").GetString()));

        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/seats?vacant=true", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/seats?floor={db.Floor3F}&feature=wheelchair", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/seats?floor={db.Floor3F}&kind=free", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync("/api/v1/me/presence", UserToken("sec1_head"))).Status);

        // 所在の提供が無い（既定）: 階の所在は 404（type で機能が無いことを示す）。応答は no-store
        using var presence = await _host.GetAsync($"/api/v1/floors/{db.Floor3F}/presence", UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.NotFound, presence.StatusCode);
        Assert.True(presence.Headers.CacheControl?.NoStore);
        Assert.Contains("presence-unavailable", await presence.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact(DisplayName = "OpenAPI: 建物・階・配置図・在籍者・席の検索と、席を持つ人の応答が文書のスキーマに一致する")]
    public async Task OpenApi_ResponsesMatchDocument()
    {
        var (_, document) = await _host.GetJsonAsync("/openapi/v1.json", null);
        var validator = new OpenApiResponseValidator(document);
        foreach (var (path, template) in new[]
                 {
                     ($"/api/v1/sites/{F.SiteTokyo.Id}/buildings", "/api/v1/sites/{id}/buildings"),
                     ($"/api/v1/buildings/{db.Building}", "/api/v1/buildings/{id}"),
                     ($"/api/v1/floors/{db.Floor3F}/layout", "/api/v1/floors/{id}/layout"),
                     ($"/api/v1/floors/{db.Floor1F}/layout", "/api/v1/floors/{id}/layout"),
                     ($"/api/v1/floors/{db.Floor3F}/occupants", "/api/v1/floors/{id}/occupants"),
                     ($"/api/v1/seats?site={F.SiteTokyo.Id}", "/api/v1/seats"),
                     ($"/api/v1/people/{F.Person("u1_member")}", "/api/v1/people/{id}"),
                     ($"/api/v1/people/{F.Person("sec1_head")}", "/api/v1/people/{id}"),
                 })
        {
            var (_, body) = await _host.GetJsonAsync(path, AdminToken());
            var errors = validator.Validate(validator.ResponseSchema(template), body);
            Assert.True(errors.Count == 0, $"{path}\n{string.Join("\n", errors)}");
        }
    }

    private static List<string?> Visible(JsonElement person) => [.. person.GetProperty("visible_fields").EnumerateArray().Select(v => v.GetString())];
}
