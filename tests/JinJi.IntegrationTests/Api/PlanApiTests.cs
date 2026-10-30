using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JinJi.IntegrationTests.Ingest;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>素案の REST API（/api/v1/plans。ADR-0013 決定 8・9）。取込の一時データベースに API を立てる。</summary>
public sealed class PlanApiTests : IAsyncLifetime
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

    [Fact(DisplayName = "API: 作成 → シミュレーション → 申請版 → CSV。他の人には見えず、形の誤りは 400。応答は文書のスキーマに一致する")]
    public async Task Create_Simulate_Freeze()
    {
        var body = new
        {
            title = "来期の体制案",
            base_date = IngestDatabase.Today.ToString("yyyy-MM-dd"),
            effective_date = IngestDatabase.Today.AddDays(30).ToString("yyyy-MM-dd"),
            changes = new[] { new { kind = "assignment", target_id = _db.Id("takahashi"), values = new Dictionary<string, string?> { ["org_unit_id"] = _db.Id("S2").ToString() } } },
        };
        var (status, created) = await PostAsync("/api/v1/plans", body, Member);
        Assert.Equal(HttpStatusCode.OK, status);
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/v1/plans", new { title = "x", base_date = "2026-01-01", effective_date = "2026-01-01", changes = new[] { new { kind = "salary" } } }, Member)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/plans/{id}", _issuer.User("portal", _db.Id("head")))).Status);

        var (_, simulation) = await _host.GetJsonAsync($"/api/v1/plans/{id}/simulation", Member);
        Assert.Single(simulation.GetProperty("moves").EnumerateArray());
        var (frozen, version) = await PostAsync($"/api/v1/plans/{id}/versions", new { }, Member);
        Assert.Equal(HttpStatusCode.OK, frozen);
        Assert.Equal(1, version.GetProperty("version_no").GetInt32());

        using (var csv = await _host.GetAsync($"/api/v1/plans/{id}/versions/1/changes.csv", Member))
        {
            Assert.Equal(HttpStatusCode.OK, csv.StatusCode);
            Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        }

        var (_, document) = await _host.GetJsonAsync("/openapi/v1.json", null);
        var validator = new OpenApiResponseValidator(document);
        foreach (var (path, value) in new[]
                 {
                     ("/api/v1/plans", (await _host.GetJsonAsync("/api/v1/plans", Member)).Body),
                     ("/api/v1/plans/{planId}", (await _host.GetJsonAsync($"/api/v1/plans/{id}", Member)).Body),
                     ("/api/v1/plans/{planId}/simulation", simulation),
                     ("/api/v1/plans/{planId}/versions", (await _host.GetJsonAsync($"/api/v1/plans/{id}/versions", Member)).Body),
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
