using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JinJi.IntegrationTests.Ingest;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>現場・IT 部門の直接登録の REST API（/api/v1/field。ADR-0013）。取込の一時データベースに API を立てる。</summary>
public sealed class FieldApiTests : IAsyncLifetime
{
    private readonly TestIssuer _issuer = new();
    private IngestDatabase _db = null!;
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Head => _issuer.User("portal", _db.Id("head"));

    private string OrgAdmin => _issuer.User("portal", _db.Id("takahashi"), "org_admin");

    private string HrAdmin => _issuer.User("portal", _db.Id("hr2"), "hr_admin");

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

    private async Task EnableAssignmentAsync()
    {
        var (proposed, setting) = await PostAsync("/api/v1/field/settings", new { domain = "assignment", mode = "field", note = "異動は現場で登録する" }, OrgAdmin);
        Assert.Equal(HttpStatusCode.OK, proposed);
        var id = setting.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync($"/api/v1/field/settings/{id}/approve", new { }, OrgAdmin)).Status);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync($"/api/v1/field/settings/{id}/approve", new { }, HrAdmin)).Status);
    }

    private object Transfer(string person, string to, DateOnly date) => new
    {
        effective_date = date.ToString("yyyy-MM-dd"),
        note = "来月の体制",
        changes = new[] { new { kind = "assignment", target_id = _db.Id(person), values = new Dictionary<string, string?> { ["org_unit_id"] = _db.Id(to).ToString() } } },
    };

    [Fact(DisplayName = "API: 管理元の起案と承認 → 範囲 → 登録 → 一覧・参照 → 取り消し。範囲の外・形の誤りは 403・400")]
    public async Task Register_List_Withdraw()
    {
        var (_, modes) = await _host.GetJsonAsync("/api/v1/field/modes", Head);
        Assert.All(modes.EnumerateArray(), m => Assert.Equal("hr", m.GetProperty("mode").GetString()));
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/api/v1/field/registrations", Transfer("takahashi", "S2", IngestDatabase.Today), Head)).Status);

        await EnableAssignmentAsync();
        var (_, scope) = await _host.GetJsonAsync("/api/v1/field/scope", Head);
        Assert.False(scope.GetProperty("manages_all").GetBoolean());
        Assert.Contains(_db.Id("S1"), scope.GetProperty("org_unit_ids").EnumerateArray().Select(e => e.GetGuid()));
        Assert.DoesNotContain(_db.Id("HR"), scope.GetProperty("org_unit_ids").EnumerateArray().Select(e => e.GetGuid()));

        var date = IngestDatabase.Today.AddDays(7);
        var (status, created) = await PostAsync("/api/v1/field/registrations", Transfer("takahashi", "S2", date), Head);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", created.GetProperty("status").GetString());
        var change = Assert.Single(created.GetProperty("changes").EnumerateArray());
        Assert.Equal(_db.Id("S2").ToString(), change.GetProperty("diff").GetProperty("org_unit_id").GetProperty("new").GetString());
        var id = created.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/api/v1/field/registrations", Transfer("hr1", "D2", date), Head)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/v1/field/registrations", new { effective_date = "2026-01-01", changes = new[] { new { kind = "salary" } } }, Head)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/field/registrations?status=done", Head)).Status);

        var (_, mine) = await _host.GetJsonAsync("/api/v1/field/registrations?mine=true&status=applied", Head);
        Assert.Equal(id, Assert.Single(mine.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/field/registrations/{id}", _issuer.User("portal", _db.Id("member")))).Status);

        var (withdrawn, body) = await PostAsync($"/api/v1/field/registrations/{id}/withdraw", new { }, Head);
        Assert.Equal(HttpStatusCode.OK, withdrawn);
        Assert.Equal("withdrawn", body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/api/v1/field/registrations/{id}/withdraw", new { }, Head)).Status);
    }

    [Fact(DisplayName = "API: AI エージェントの登録は下書き。エージェントは確定できず、委任元の人が確定する")]
    public async Task Agent_DraftThenConfirm()
    {
        await EnableAssignmentAsync();
        var agent = _issuer.Agent("agent-x", _db.Id("head"));

        var (status, draft) = await PostAsync("/api/v1/field/registrations", Transfer("takahashi", "S2", IngestDatabase.Today), agent);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("draft", "agent"), (draft.GetProperty("status").GetString(), draft.GetProperty("registered_via").GetString()));
        var id = draft.GetProperty("id").GetGuid();

        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync($"/api/v1/field/registrations/{id}/confirm", new { }, agent)).Status);
        var (confirmed, body) = await PostAsync($"/api/v1/field/registrations/{id}/confirm", new { }, Head);
        Assert.Equal(HttpStatusCode.OK, confirmed);
        Assert.Equal("applied", body.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/api/v1/field/registrations", Transfer("takahashi", "S1", IngestDatabase.Today), _issuer.Service("attendance"))).Status);
    }

    [Fact(DisplayName = "API: 担当の指名と取り消し")]
    public async Task Grant_AndRevoke()
    {
        var today = IngestDatabase.Today;
        var body = new { org_unit_id = _db.Id("S1"), person_id = _db.Id("member"), from = today.ToString("yyyy-MM-dd"), until = today.AddDays(30).ToString("yyyy-MM-dd") };
        var (status, grant) = await PostAsync("/api/v1/field/grants", body, Head);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync("/api/v1/field/grants", body, Head)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/api/v1/field/grants", body with { org_unit_id = _db.Id("HR") }, Head)).Status);

        var (_, grants) = await _host.GetJsonAsync($"/api/v1/field/grants?person_id={_db.Id("member")}", Head);
        Assert.Single(grants.EnumerateArray());
        var (revoked, revokedBody) = await PostAsync($"/api/v1/field/grants/{grant.GetProperty("id").GetGuid()}/revoke", new { }, Head);
        Assert.Equal(HttpStatusCode.OK, revoked);
        Assert.Equal(_db.Id("head"), revokedBody.GetProperty("revoked_by").GetGuid());
    }

    [Fact(DisplayName = "OpenAPI: 直接登録の応答が文書のスキーマ（required・null・enum・型）に一致する")]
    public async Task OpenApi_ResponsesMatchDocument()
    {
        await EnableAssignmentAsync();
        var (_, created) = await PostAsync("/api/v1/field/registrations", Transfer("takahashi", "S2", IngestDatabase.Today.AddDays(3)), Head);
        var id = created.GetProperty("id").GetGuid();
        var (_, document) = await _host.GetJsonAsync("/openapi/v1.json", null);
        var validator = new OpenApiResponseValidator(document);
        foreach (var (path, value) in new[]
                 {
                     ("/api/v1/field/modes", (await _host.GetJsonAsync("/api/v1/field/modes", Head)).Body),
                     ("/api/v1/field/settings", (await _host.GetJsonAsync("/api/v1/field/settings", Head)).Body),
                     ("/api/v1/field/scope", (await _host.GetJsonAsync("/api/v1/field/scope", Head)).Body),
                     ("/api/v1/field/registrations", (await _host.GetJsonAsync("/api/v1/field/registrations", Head)).Body),
                     ("/api/v1/field/registrations/{registrationId}", (await _host.GetJsonAsync($"/api/v1/field/registrations/{id}", Head)).Body),
                 })
        {
            var errors = validator.Validate(validator.ResponseSchema(path), value);
            Assert.True(errors.Count == 0, $"{path}\n{string.Join("\n", errors)}");
        }

        var createErrors = validator.Validate(validator.ResponseSchema("/api/v1/field/registrations", "post"), created);
        Assert.True(createErrors.Count == 0, string.Join("\n", createErrors));
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
