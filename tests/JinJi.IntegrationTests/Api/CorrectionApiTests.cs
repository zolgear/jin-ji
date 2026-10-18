using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JinJi.IntegrationTests.Ingest;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>修正依頼の REST API（/api/v1/corrections）。取込の一時データベースに API を立てる。</summary>
public sealed class CorrectionApiTests : IAsyncLifetime
{
    private readonly TestIssuer _issuer = new();
    private IngestDatabase _db = null!;
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Member => _issuer.User("portal", _db.Id("member"));

    private string Corrector => _issuer.User("portal", _db.Id("head"), "hr_corrector");

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

    [Fact(DisplayName = "API: 起票 → 一覧（人の要約つき）→ 状態の更新。権限・形の誤り・見えない ID・閉じた依頼はそれぞれ 403・400・404・409")]
    public async Task Create_List_Close()
    {
        var body = new { person_id = _db.Id("takahashi"), field = "extension_number", reason = "内線表と違います", suggested_value = "1234" };
        var (status, created) = await PostAsync("/api/v1/corrections", body, Member);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("created", created.GetProperty("outcome").GetString());
        var id = created.GetProperty("correction").GetProperty("id").GetGuid();
        Assert.True(created.GetProperty("correction").GetProperty("reported_by_me").GetBoolean());

        Assert.Equal("endorsed", (await PostAsync("/api/v1/corrections", body with { }, _issuer.Agent("agent-x", _db.Id("hr1")))).Body.GetProperty("outcome").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("/api/v1/corrections", body, _issuer.Service("attendance"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/v1/corrections", new { person_id = _db.Id("takahashi"), field = "name_history", reason = "x" }, Member)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("/api/v1/corrections", new { person_id = _db.Id("takahashi") }, Member)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/api/v1/corrections", body with { person_id = Guid.NewGuid() }, Member)).Status);

        var (listed, list) = await _host.GetJsonAsync("/api/v1/corrections", Corrector);
        Assert.Equal(HttpStatusCode.OK, listed);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal((id, 1), (item.GetProperty("id").GetGuid(), item.GetProperty("endorsements").GetInt32()));
        Assert.Equal(_db.Id("member"), item.GetProperty("reported_by_person_id").GetGuid());
        var person = Assert.Single(list.GetProperty("people").EnumerateArray());
        Assert.Equal(("E0002", "髙橋 一郎"), (person.GetProperty("employee_number").GetString(), person.GetProperty("display_name").GetString()));

        var (_, filtered) = await _host.GetJsonAsync($"/api/v1/corrections?person_id={_db.Id("head")}", Corrector);
        Assert.Empty(filtered.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/corrections?person_id=x", Corrector)).Status);

        // 関係の無い人には見えない（404）、一般の人の件数は 403
        Assert.Equal(HttpStatusCode.NotFound, (await _host.GetJsonAsync($"/api/v1/corrections/{id}", _issuer.User("portal", _db.Id("hr2")))).Status);
        Assert.Equal(HttpStatusCode.OK, (await _host.GetJsonAsync($"/api/v1/corrections/{id}", Member)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.GetJsonAsync("/api/v1/corrections/summary", Member)).Status);
        var (_, summary) = await _host.GetJsonAsync("/api/v1/corrections/summary", Corrector);
        Assert.Equal((1, 1, 0), (summary.GetProperty("open").GetInt32(), summary.GetProperty("new").GetInt32(), summary.GetProperty("stale").GetInt32()));

        var close = $"/api/v1/corrections/{id}/close";
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(close, new { status = "resolved" }, Member)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(close, new { status = "resolved" }, _issuer.Agent("agent-x", _db.Id("head"), "hr_corrector"))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(close, new { status = "auto_resolved" }, Corrector)).Status);
        var (closedStatus, closed) = await PostAsync(close, new { status = "dismissed", note = "人事パッケージの値が正しい" }, Corrector);
        Assert.Equal(HttpStatusCode.OK, closedStatus);
        Assert.Equal(("dismissed", "person"), (closed.GetProperty("status").GetString(), closed.GetProperty("closed_kind").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(close, new { status = "resolved" }, Corrector)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync($"/api/v1/corrections/{Guid.NewGuid()}/close", new { status = "resolved" }, Corrector)).Status);

        var (_, open) = await _host.GetJsonAsync("/api/v1/corrections?status=open", Corrector);
        Assert.Empty(open.GetProperty("items").EnumerateArray());
        var (_, closedList) = await _host.GetJsonAsync("/api/v1/corrections?status=closed", Corrector);
        Assert.Single(closedList.GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/corrections?status=everything", Corrector)).Status);
    }

    [Fact(DisplayName = "API: CSV のレポートは修正担当者・人事管理者の人だけ。UTF-8 BOM・text/csv・no-store・ファイル名")]
    public async Task Report_Csv()
    {
        await PostAsync("/api/v1/corrections", new { person_id = _db.Id("takahashi"), field = "site", reason = "拠点が違う" }, Member);

        using (var denied = await _host.GetAsync("/api/v1/corrections/report.csv", Member))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        using var response = await _host.GetAsync("/api/v1/corrections/report.csv?status=all", Corrector);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
        Assert.StartsWith("jinji-corrections-all-", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'), StringComparison.Ordinal);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        Assert.StartsWith("状態,経過日数,社員番号,氏名", text, StringComparison.Ordinal);
        Assert.Contains("E0002", text, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "OpenAPI: 修正依頼の応答が文書のスキーマ（required・null・enum・型）に一致する")]
    public async Task OpenApi_ResponsesMatchDocument()
    {
        await PostAsync("/api/v1/corrections", new { person_id = _db.Id("takahashi"), field = "site", reason = "拠点が違う" }, Member);
        await _db.ImportCsvAsync("社員番号,内線\n,1234\n", """
            { "name": "people-csv", "target": "people", "legalEntityCode": "A", "fields": { "employee_number": ["社員番号"], "extension_number": ["内線"] } }
            """, Ct);
        var (_, document) = await _host.GetJsonAsync("/openapi/v1.json", null);
        var validator = new OpenApiResponseValidator(document);
        var (_, list) = await _host.GetJsonAsync("/api/v1/corrections?status=all", Corrector);
        Assert.True(list.GetProperty("items").GetArrayLength() >= 2);
        foreach (var (path, value) in new[]
                 {
                     ("/api/v1/corrections", list),
                     ("/api/v1/corrections", (await _host.GetJsonAsync("/api/v1/corrections", Member)).Body),
                     ("/api/v1/corrections/summary", (await _host.GetJsonAsync("/api/v1/corrections/summary", Corrector)).Body),
                     ("/api/v1/corrections/{correctionId}", (await _host.GetJsonAsync($"/api/v1/corrections/{list.GetProperty("items")[0].GetProperty("id").GetGuid()}", Corrector)).Body),
                 })
        {
            var errors = validator.Validate(validator.ResponseSchema(path), value);
            Assert.True(errors.Count == 0, $"{path}\n{string.Join("\n", errors)}");
        }

        var (_, created) = await PostAsync("/api/v1/corrections", new { person_id = _db.Id("head"), field = "hire_date", reason = "入社日が違う", suggested_value = "2020-04-01" }, Member);
        var createErrors = validator.Validate(validator.ResponseSchema("/api/v1/corrections", "post"), created);
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
