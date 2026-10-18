using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JinJi.Application.Ingest;
using JinJi.Ingest;
using JinJi.IntegrationTests.Ingest;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>要確認の提案の参照と解決（/api/v1/ingest/proposals）の REST API。取込の一時データベースに API を立てる。</summary>
public sealed class IngestApiTests : IAsyncLifetime
{
    private const string ActionMapping = """
        {
          "name": "jirei",
          "target": "personnel_actions",
          "fields": { "action_date": ["発令日"], "employee_number": ["社員番号"], "full_name": ["氏名"], "action_text": ["発令内容"] }
        }
        """;

    private readonly TestIssuer _issuer = new();
    private IngestDatabase _db = null!;
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    [Fact(DisplayName = "API: 要確認の提案の解決は人事管理者の人だけ。一般・エージェントは 403、無い提案は 404、解決済みは 409、不正な選択は 400")]
    public async Task Resolve_Endpoint()
    {
        var csv = $"発令日,社員番号,氏名,発令内容\n{IngestDatabase.Today.AddDays(-1):yyyy/MM/dd},,鈴木 次郎,営業一課に配置換えを命ずる\n";
        var result = await _db.ImportCsvAsync(csv, ActionMapping, Ct);
        var review = Assert.Single(await _db.Store.ListProposalsAsync(_db.HrUser("hr2"), result.BatchId, Ct), p => p.Status == ProposalStatus.NeedsReview);
        var path = $"/api/v1/ingest/proposals/{review.Id}/resolve";
        var body = new { resolution = "select_candidate", person_id = _db.Id("member") };

        var resolver = _issuer.User("portal", _db.Id("head"), "hr_admin");
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(path, body, _issuer.User("portal", _db.Id("takahashi")))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync(path, body, _issuer.Agent("agent-x", _db.Id("hr2"), "hr_admin"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync($"/api/v1/ingest/proposals/{Guid.NewGuid()}/resolve", body, resolver)).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("/api/v1/ingest/proposals/not-a-uuid/resolve", body, resolver)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(path, new { resolution = "approve" }, resolver)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(path, new { resolution = "select_candidate", person_id = _db.Id("takahashi") }, resolver)).Status);

        var (got, proposal) = await _host.GetJsonAsync($"/api/v1/ingest/proposals/{review.Id}", resolver);
        Assert.Equal(HttpStatusCode.OK, got);
        Assert.Equal(("needs_review", "missing_employee_number"), (proposal.GetProperty("status").GetString(), proposal.GetProperty("code").GetString()));
        Assert.Equal(_db.Id("member"), proposal.GetProperty("candidates")[0].GetProperty("person_id").GetGuid());
        Assert.False(proposal.TryGetProperty("payload", out _));

        var (status, resolved) = await PostAsync(path, body, resolver);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(("resolved", "selected_candidate"), (resolved.GetProperty("status").GetString(), resolved.GetProperty("resolution").GetString()));
        var derived = resolved.GetProperty("derived_proposal_ids")[0].GetGuid();
        Assert.Equal(ProposalStatus.Proposed, (await _db.Store.FindProposalAsync(_db.HrUser("hr2"), derived, Ct))!.Status);

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync(path, new { resolution = "reject" }, resolver)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.GetJsonAsync($"/api/v1/ingest/proposals/{review.Id}", _issuer.User("portal", _db.Id("takahashi")))).Status);
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
        using var json = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "null" : text);
        return (response.StatusCode, json.RootElement.Clone());
    }
}
