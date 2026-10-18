using System.Globalization;
using System.Net;
using System.Text.Json;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>
/// 人の多角的な検索の REST API（<c>GET /api/v1/people</c> の絞り込み・並べ替え・件数）。実 DB の RLS とポリシーの下で、
/// 件数（facets）が閲覧者に見える人の見える値だけから数えられていることを、全ページの一覧から数え直して確かめる。
/// </summary>
public sealed class PersonSearchApiTests(DirectoryFixtureDatabase db) : IClassFixture<DirectoryFixtureDatabase>, IAsyncLifetime
{
    private static readonly string[] RelaxedArgs =
    [
        "--JinJi:RateLimit:Api:PerClient=100000",
        "--JinJi:RateLimit:Api:PerUser=100000",
        "--JinJi:RateLimit:Api:PerEndpoint:person_list=100000",
        "--JinJi:RateLimit:Api:ReturnedPeoplePerUser=1000000",
        "--JinJi:RateLimit:Api:ReturnedPeoplePerClient=1000000",
        "--JinJi:Abuse:Enabled=false",
    ];

    private readonly TestIssuer _issuer = new();
    private ApiTestHost _host = null!;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer, RelaxedArgs);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _issuer.Dispose();
    }

    public static TheoryData<string> Viewers => new() { "user:sec1_head", "user:r1_member", "user:c_t1_member", "admin", "agent:u2_member", "service:attendance" };

    private string Token(string viewer) => viewer switch
    {
        "admin" => _issuer.User("portal", F.Person("hr_admin"), "hr_admin"),
        _ when viewer.StartsWith("user:", StringComparison.Ordinal) => _issuer.User("portal", F.Person(viewer[5..])),
        _ when viewer.StartsWith("agent:", StringComparison.Ordinal) => _issuer.Agent("agent-x", F.Person(viewer[6..])),
        _ => _issuer.Service(viewer[8..]),
    };

    [Theory(DisplayName = "件数: 閲覧者ごとに、全ページの一覧の見える値から数え直した人数・件数と一致する（所属の項目が見えない人の所属・内示の兼務は数えない）")]
    [MemberData(nameof(Viewers))]
    public async Task Facets_MatchListedValues(string viewer)
    {
        var token = Token(viewer);
        foreach (var filter in new[] { string.Empty, $"&org={F.Unit("D1")}&include_descendants=true", "&assignment_kind=concurrent&assignment_kind=secondment" })
        {
            var (status, first) = await _host.GetJsonAsync($"/api/v1/people?facets=true&limit=1{filter}", token);
            Assert.Equal(HttpStatusCode.OK, status);
            var all = await ReadAllAsync($"/api/v1/people?limit=100{filter}", token);
            Assert.Equal(all.Count, first.GetProperty("total").GetInt32());

            var facets = first.GetProperty("facets");
            Assert.Equal(CountPeople(all, p => p.GetProperty("legal_entity") is { ValueKind: JsonValueKind.Object } e ? e.GetProperty("id").GetString() : null), Facet(facets, "legal_entities"));
            Assert.Equal(CountPeople(all, p => p.GetProperty("employment_type").GetString()), Facet(facets, "employment_types"));
            Assert.Equal(CountPeople(all, p => p.GetProperty("account_status").GetString()), Facet(facets, "account_statuses"));
            // 所属の件数は条件に一致する所属で数えるので、所属の条件が無いときに一覧の全所属と突き合わせる（条件付きはサービスのテスト）
            if (filter.Length == 0)
            {
                Assert.Equal(CountAssignments(all, a => a.GetProperty("position_title").GetString()), Facet(facets, "positions"));
                Assert.Equal(CountAssignments(all, Kind), Facet(facets, "assignment_kinds"));
            }

            // 段の名前は段の定義から付ける
            foreach (var level in facets.GetProperty("levels").EnumerateArray())
            {
                Assert.False(string.IsNullOrEmpty(level.GetProperty("label").GetString()));
            }
        }
    }

    [Fact(DisplayName = "件数: 兼務・出向の内示（3 日後の発令）は人事管理者以外の件数に現れず、R1 の所属は課内の人にだけ数える")]
    public async Task Facets_HidePlannedAndRestrictedAssignments()
    {
        var url = $"/api/v1/people?facets=true&limit=1&org={F.Unit("R1")}";
        var (_, outsider) = await _host.GetJsonAsync(url, Token("user:sec1_head"));
        Assert.Equal(0, outsider.GetProperty("total").GetInt32());
        var (_, insider) = await _host.GetJsonAsync(url, Token("user:r1_member"));
        Assert.Equal(2, insider.GetProperty("total").GetInt32());
        Assert.Equal(1, Facet(insider.GetProperty("facets"), "positions")["室長"]);
    }

    [Fact(DisplayName = "並べ替え: カーソルで続きを読むと全件と同じ並び。別の並び・別の条件のカーソルは 400")]
    public async Task Sort_CursorFollowsOrder()
    {
        var token = Token("user:sec1_head");
        foreach (var sort in new[] { "kana", "org", "hire_date", "hire_date_desc" })
        {
            var (_, full) = await _host.GetJsonAsync($"/api/v1/people?sort={sort}&limit=100", token);
            var paged = await ReadAllAsync($"/api/v1/people?sort={sort}&limit=4", token);
            Assert.Equal(ApiEndpointTests.Ids(full.GetProperty("items")), paged.Select(p => p.GetProperty("id").GetGuid()).ToList());
        }

        var (_, first) = await _host.GetJsonAsync("/api/v1/people?sort=kana&limit=2", token);
        var cursor = Uri.EscapeDataString(first.GetProperty("next_cursor").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/people?sort=org&limit=2&cursor={cursor}", token)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/people?sort=kana&employment_type=regular&limit=2&cursor={cursor}", token)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _host.GetJsonAsync($"/api/v1/people?sort=kana&limit=2&cursor={cursor}", token)).Status);
    }

    [Fact(DisplayName = "検査: 件数とカーソルの併用・知らない並び・所属の種類・状態・形の不正な ID と日付・逆の範囲・ids との併用は 400")]
    public async Task InvalidParameters_Return400()
    {
        var token = Token("user:sec1_head");
        var (_, first) = await _host.GetJsonAsync("/api/v1/people?limit=2", token);
        var cursor = Uri.EscapeDataString(first.GetProperty("next_cursor").GetString()!);
        foreach (var bad in new[]
                 {
                     $"facets=true&cursor={cursor}", "sort=name", "assignment_kind=main", "employment_status=retired", "account_status=SUSPENDED",
                     "legal_entity=abc", "hired_from=2026/01/01", $"hired_from={Date(F.Today)}&hired_to={Date(F.Today.AddDays(-1))}", "facets=yes",
                     $"ids={F.Person("u1_member")}&level=section", "position=", string.Join('&', Enumerable.Range(0, 21).Select(i => $"job_type=j{i}")),
                 })
        {
            using var response = await _host.GetAsync($"/api/v1/people?{bad}", token);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, bad);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
    }

    private async Task<List<JsonElement>> ReadAllAsync(string url, string token)
    {
        var all = new List<JsonElement>();
        string? cursor = null;
        do
        {
            var (status, body) = await _host.GetJsonAsync(cursor is null ? url : $"{url}&cursor={Uri.EscapeDataString(cursor)}", token);
            Assert.Equal(HttpStatusCode.OK, status);
            all.AddRange(body.GetProperty("items").EnumerateArray().Select(e => e.Clone()));
            cursor = body.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null);
        return all;
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Kind(JsonElement a) =>
        a.GetProperty("secondment").GetString() != "none" ? "secondment" : a.GetProperty("type").GetString() == "primary" ? "primary" : "concurrent";

    private static Dictionary<string, int> Facet(JsonElement facets, string name) =>
        facets.GetProperty(name).EnumerateArray().ToDictionary(v => v.GetProperty("value").GetString()!, v => v.GetProperty("count").GetInt32(), StringComparer.Ordinal);

    private static Dictionary<string, int> CountPeople(IEnumerable<JsonElement> people, Func<JsonElement, string?> value) =>
        people.Select(value).OfType<string>().GroupBy(v => v, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

    private static Dictionary<string, int> CountAssignments(IEnumerable<JsonElement> people, Func<JsonElement, string?> value) =>
        people.Where(p => p.GetProperty("assignments").ValueKind == JsonValueKind.Array)
            .SelectMany(p => p.GetProperty("assignments").EnumerateArray().Select(value).OfType<string>().Distinct(StringComparer.Ordinal))
            .GroupBy(v => v, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}
