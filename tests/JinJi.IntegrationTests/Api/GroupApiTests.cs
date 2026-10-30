using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JinJi.IntegrationTests.Ingest;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>グループの REST API（/api/v1/groups。ADR-0014）。取込の一時データベースに API を立てる。</summary>
public sealed class GroupApiTests : IAsyncLifetime
{
    private readonly TestIssuer _issuer = new();
    private IngestDatabase _db = null!;
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Member => _issuer.User("portal", _db.Id("member"));

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        _host = await ApiTestHost.StartAsync(_db.Database.AppConnectionString, _issuer);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _db.DisposeAsync();
        _issuer.Dispose();
    }

    [Fact(DisplayName = "API: 作成 → メンバーの追加 → 一覧・参照・人の項目 groups。エージェントの変更・形の誤り・見えない ID は 403・400・404。応答は文書のスキーマに一致する")]
    public async Task Create_AddMember_List()
    {
        var (status, created) = await PostAsync("/api/v1/groups", new { name = "新製品プロジェクト", kind = "project", owner_org_unit_id = _db.Id("S2") }, Member);
        Assert.Equal(HttpStatusCode.OK, status);
        var id = created.GetProperty("group").GetProperty("id").GetGuid();
        Assert.Equal("owner", Assert.Single(created.GetProperty("members").EnumerateArray()).GetProperty("role").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/v1/groups", new { name = "x", kind = "department" }, Member)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/api/v1/groups", new { name = "x", kind = "team" }, _issuer.Agent("agent-x", _db.Id("member")))).Status);

        var (added, detail) = await PostAsync($"/api/v1/groups/{id}/members", new { person_id = _db.Id("takahashi"), role = "lead" }, Member);
        Assert.Equal(HttpStatusCode.OK, added);
        Assert.Equal(2, detail.GetProperty("members").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/api/v1/groups/{id}/members", new { person_id = _db.Id("takahashi") }, Member)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync($"/api/v1/groups/{id}/members", new { person_id = _db.Id("hr1") }, _issuer.User("portal", _db.Id("hr2")))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/groups/{Guid.NewGuid()}", Member)).Status);

        var (_, mine) = await _host.GetJsonAsync($"/api/v1/groups?person_id={_db.Id("takahashi")}", Member);
        Assert.Equal(id, Assert.Single(mine.EnumerateArray()).GetProperty("id").GetGuid());
        var (_, person) = await _host.GetJsonAsync($"/api/v1/people/{_db.Id("takahashi")}", Member);
        Assert.Equal("lead", Assert.Single(person.GetProperty("groups").EnumerateArray()).GetProperty("role").GetString());

        var (_, document) = await _host.GetJsonAsync("/openapi/v1.json", null);
        var validator = new OpenApiResponseValidator(document);
        foreach (var (path, value) in new[]
                 {
                     ("/api/v1/groups", (await _host.GetJsonAsync("/api/v1/groups", Member)).Body),
                     ("/api/v1/groups/{groupId}", (await _host.GetJsonAsync($"/api/v1/groups/{id}", Member)).Body),
                     ("/api/v1/people/{id}", person),
                 })
        {
            var errors = validator.Validate(validator.ResponseSchema(path), value);
            Assert.True(errors.Count == 0, $"{path}\n{string.Join("\n", errors)}");
        }
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string path, object body, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _host.Http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
