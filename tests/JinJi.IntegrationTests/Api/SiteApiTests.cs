using System.Globalization;
using System.Net;
using System.Text.Json;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>
/// 拠点（0017）: 人・組織の応答の拠点とリモートワーク、拠点の一覧・詳細（人数は見える人だけ）、検索の条件・件数・並べ替え、OpenAPI の文書との一致。
/// </summary>
/// <remarks>
/// フィクスチャ（<see cref="OrgFixture"/>）: 法人 A の最上位（A-ROOT）に東京本社、営業部長（dept1_head）は人ごとに大阪支社、名古屋支店は 20 日後に開設。
/// ポリシー: 営業部（DP1）の直属の人の拠点・リモートワークは本人だけに見える。
/// </remarks>
public sealed class SiteApiTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>, IAsyncLifetime
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

    [Fact(DisplayName = "人: 拠点は人ごとの拠点か主務の組織（上位組織）の拠点で、inherited で区別する。見えない拠点・リモートワークは null で visible_fields に無い")]
    public async Task Person_SiteAndRemoteWork()
    {
        var (_, head) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("dept1_head")}", AdminToken());
        Assert.Equal(("SITE-OSK", false, "branch", "大阪府"), Site(head));
        Assert.True(head.GetProperty("remote_work").GetBoolean());

        var (_, member) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("u1_member")}", UserToken("sec1_head"));
        Assert.Equal(("SITE-TKY", true, "head_office", "東京都"), Site(member));
        Assert.False(member.GetProperty("remote_work").GetBoolean());

        // 営業部の直属の人（部長）の拠点・リモートワークは本人だけ
        var (_, hidden) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("dept1_head")}", UserToken("sec1_head"));
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("site").ValueKind);
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("remote_work").ValueKind);
        var visible = hidden.GetProperty("visible_fields").EnumerateArray().Select(v => v.GetString()).ToList();
        Assert.DoesNotContain("site", visible);
        Assert.DoesNotContain("remote_work", visible);
        Assert.Contains("display_name", visible);
        var (_, self) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("dept1_head")}", UserToken("dept1_head"));
        Assert.Equal("SITE-OSK", Site(self).Code);

        // 拠点の無い法人の人は null（値が無い）で、項目は見える
        var (_, other) = await _host.GetJsonAsync($"/api/v1/people/{F.Person("b_member")}", AdminToken());
        Assert.Equal(JsonValueKind.Null, other.GetProperty("site").ValueKind);
        Assert.Contains("site", other.GetProperty("visible_fields").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact(DisplayName = "組織: 組織の拠点は自分の拠点か上位組織の拠点（inherited）。拠点の情報は全社公開")]
    public async Task Org_Site()
    {
        var (_, root) = await _host.GetJsonAsync($"/api/v1/orgs/{F.Unit("A-ROOT")}", UserToken("b_member"));
        Assert.Equal(("SITE-TKY", false, "head_office", "東京都"), Site(root));
        var (_, section) = await _host.GetJsonAsync($"/api/v1/orgs/{F.Unit("S1")}", UserToken("b_member"));
        Assert.Equal(("SITE-TKY", true, "head_office", "東京都"), Site(section));
        var (_, tree) = await _host.GetJsonAsync("/api/v1/orgs/tree", UserToken("b_member"));
        Assert.Equal(JsonValueKind.Null, tree.GetProperty("units").EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == F.Unit("B-DP")).GetProperty("site").ValueKind);
    }

    [Fact(DisplayName = "拠点の一覧・詳細: 人数は閲覧者に拠点が見える人だけ。開設の予定の拠点は人事管理者の未来の時点だけ。絞り込みの誤りは 400")]
    public async Task Sites_ListAndDetail()
    {
        var (status, list) = await _host.GetJsonAsync("/api/v1/sites", UserToken("sec1_head"));
        Assert.Equal(HttpStatusCode.OK, status);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["SITE-OSK", "SITE-TKY"], items.Select(i => i.GetProperty("code").GetString()));
        Assert.Equal(0, items[0].GetProperty("member_count").GetInt32());
        var tokyo = items[1];
        Assert.Equal(("千代田区丸の内9-9-1", "13", "東京都"), (tokyo.GetProperty("location").GetString(), tokyo.GetProperty("prefecture_code").GetString(), tokyo.GetProperty("prefecture_name").GetString()));
        Assert.Equal([F.Unit("A-ROOT")], tokyo.GetProperty("org_units").EnumerateArray().Select(o => o.GetProperty("id").GetGuid()));

        // 人数は人の検索の件数と同じ（見える人の見える値だけ）
        var (_, search) = await _host.GetJsonAsync("/api/v1/people?facets=true&limit=1", UserToken("sec1_head"));
        var tokyoFacet = search.GetProperty("facets").GetProperty("sites").EnumerateArray().Single(v => v.GetProperty("value").GetGuid() == F.SiteTokyo.Id);
        Assert.Equal(tokyoFacet.GetProperty("count").GetInt32(), tokyo.GetProperty("member_count").GetInt32());
        Assert.Equal(
            search.GetProperty("facets").GetProperty("remote_work").EnumerateArray().Single(v => v.GetProperty("value").GetString() == "true").GetProperty("count").GetInt32(),
            tokyo.GetProperty("remote_work_count").GetInt32());

        var (_, admin) = await _host.GetJsonAsync($"/api/v1/sites/{F.SiteOsaka.Id}", AdminToken());
        Assert.Equal((1, 1), (admin.GetProperty("member_count").GetInt32(), admin.GetProperty("remote_work_count").GetInt32()));

        // 開設の予定（20 日後）の拠点: 人事管理者が未来の時点を指定したときだけ
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/sites/{F.SiteNagoya.Id}", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/sites/{F.SiteNagoya.Id}", AdminToken())).Status);
        var future = F.Today.AddDays(25).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        Assert.Equal(HttpStatusCode.OK, (await _host.GetJsonAsync($"/api/v1/sites/{F.SiteNagoya.Id}?as_of={future}", AdminToken())).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.GetJsonAsync($"/api/v1/sites?as_of={future}", UserToken("sec1_head"))).Status);

        var (_, filtered) = await _host.GetJsonAsync("/api/v1/sites?prefecture=27&site_type=branch&query=大阪", UserToken("sec1_head"));
        Assert.Equal(["SITE-OSK"], filtered.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("code").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/sites?site_type=headquarters", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/sites?prefecture=48", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync("/api/v1/sites/not-a-uuid", UserToken("sec1_head"))).Status);
    }

    [Fact(DisplayName = "検索: 拠点・種別・都道府県・リモートワークで絞り込み、件数は見える値だけ。sort=site は拠点ごとに並ぶ（見えない人は最後）")]
    public async Task Search_BySite()
    {
        var (_, admin) = await _host.GetJsonAsync($"/api/v1/people?site={F.SiteOsaka.Id}", AdminToken());
        Assert.Equal([F.Person("dept1_head")], admin.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));
        var (_, user) = await _host.GetJsonAsync($"/api/v1/people?site={F.SiteOsaka.Id}", UserToken("sec1_head"));
        Assert.Empty(user.GetProperty("items").EnumerateArray());

        var (_, remote) = await _host.GetJsonAsync("/api/v1/people?remote_work=true&site_type=head_office&prefecture=13&facets=true&limit=100", UserToken("sec1_head"));
        var ids = remote.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToHashSet();
        Assert.Equal(new HashSet<Guid> { F.Person("u2_member"), F.Person("s2_member") }, ids);
        Assert.Equal(2, remote.GetProperty("total").GetInt32());

        var (_, sorted) = await _host.GetJsonAsync("/api/v1/people?sort=site&limit=100", UserToken("sec1_head"));
        var codes = sorted.GetProperty("items").EnumerateArray()
            .Select(p => p.GetProperty("site").ValueKind == JsonValueKind.Null ? "~" : p.GetProperty("site").GetProperty("code").GetString()!)
            .ToList();
        Assert.Equal(codes.Order(StringComparer.Ordinal), codes);

        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/people?remote_work=maybe", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/people?site=tokyo", UserToken("sec1_head"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/people?remote_work=true&ids={F.Person("u1_member")}", UserToken("sec1_head"))).Status);
    }

    [Fact(DisplayName = "OpenAPI: 拠点の一覧・詳細と、拠点を持つ人・組織の応答が文書のスキーマに一致する")]
    public async Task OpenApi_ResponsesMatchDocument()
    {
        var (_, document) = await _host.GetJsonAsync("/openapi/v1.json", null);
        var validator = new OpenApiResponseValidator(document);
        foreach (var (path, template) in new[]
                 {
                     ("/api/v1/sites", "/api/v1/sites"),
                     ($"/api/v1/sites/{F.SiteTokyo.Id}", "/api/v1/sites/{id}"),
                     ($"/api/v1/people/{F.Person("dept1_head")}", "/api/v1/people/{id}"),
                     ($"/api/v1/orgs/{F.Unit("S1")}", "/api/v1/orgs/{id}"),
                     ("/api/v1/people?facets=true&sort=site", "/api/v1/people"),
                 })
        {
            var (_, body) = await _host.GetJsonAsync(path, AdminToken());
            var errors = validator.Validate(validator.ResponseSchema(template), body);
            Assert.True(errors.Count == 0, $"{path}\n{string.Join("\n", errors)}");
        }
    }

    private static (string Code, bool Inherited, string Type, string? Prefecture) Site(JsonElement owner)
    {
        var site = owner.GetProperty("site");
        return (site.GetProperty("code").GetString()!, site.GetProperty("inherited").GetBoolean(), site.GetProperty("type").GetString()!, site.GetProperty("prefecture_name").GetString());
    }
}
