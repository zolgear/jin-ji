using System.IO.Compression;
using System.Net;
using JinJi.IntegrationTests.Support;
using JinJi.IntegrationTests.Sync;

namespace JinJi.IntegrationTests.Api;

/// <summary>同期計画の REST API（/api/v1/sync/plans）。人事管理者の人だけが一覧・承認・スクリプトの取得をできる。</summary>
public sealed class SyncApiTests : IAsyncLifetime
{
    private readonly TestIssuer _issuer = new();
    private SyncDatabase _sync = null!;
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _sync = await SyncDatabase.CreateAsync(Ct);
        _host = await ApiTestHost.StartAsync(_sync.Db.Database.AppConnectionString, _issuer);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await _sync.DisposeAsync();
        _issuer.Dispose();
    }

    [Fact(DisplayName = "API: 一覧・詳細・承認・スクリプトの取得は人事管理者の人だけ。起案者の承認は 403、承認前のスクリプトは 409、無い計画は 404")]
    public async Task Endpoints()
    {
        var created = await _sync.CreatePlanAsync(Ct);
        var id = created.Plan.Id;
        var approver = _issuer.User("portal", _sync.Id("hr2"), "hr_admin");
        var proposer = _issuer.User("portal", _sync.Id("hr1"), "hr_admin");

        var (status, list) = await _host.GetJsonAsync("/api/v1/sync/plans", approver);
        Assert.Equal(HttpStatusCode.OK, status);
        var summary = list.EnumerateArray().Single();
        Assert.Equal((id, "proposed", 1), (summary.GetProperty("id").GetGuid(), summary.GetProperty("status").GetString(), summary.GetProperty("critical_count").GetInt32()));
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/sync/plans?limit=1000", approver)).Status);

        var (got, detail) = await _host.GetJsonAsync($"/api/v1/sync/plans/{id}", approver);
        Assert.Equal(HttpStatusCode.OK, got);
        var first = detail.GetProperty("items")[0];
        Assert.Equal(("disable_account", "critical", "retired_enabled"), (first.GetProperty("action").GetString(), first.GetProperty("priority").GetString(), first.GetProperty("code").GetString()));
        Assert.Equal("accountEnabled", first.GetProperty("changes")[0].GetProperty("field").GetString());

        foreach (var token in new[] { _issuer.User("portal", _sync.Id("takahashi")), _issuer.Agent("agent-x", _sync.Id("hr2"), "hr_admin"), _issuer.Service("sync") })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await _host.GetJsonAsync("/api/v1/sync/plans", token)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, await PostAsync($"/api/v1/sync/plans/{id}/approve", token));
            Assert.Equal(HttpStatusCode.Forbidden, (await _host.GetAsync($"/api/v1/sync/plans/{id}/script", token)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Conflict, (await _host.GetAsync($"/api/v1/sync/plans/{id}/script", approver)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, await PostAsync($"/api/v1/sync/plans/{id}/approve", proposer));
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync($"/api/v1/sync/plans/{Guid.NewGuid()}/approve", approver));
        Assert.Equal(HttpStatusCode.NotFound, await PostAsync("/api/v1/sync/plans/not-a-uuid/approve", approver));

        Assert.Equal(HttpStatusCode.OK, await PostAsync($"/api/v1/sync/plans/{id}/approve", approver));
        Assert.Equal(HttpStatusCode.Conflict, await PostAsync($"/api/v1/sync/plans/{id}/approve", approver));
        Assert.Equal(HttpStatusCode.Conflict, await PostAsync($"/api/v1/sync/plans/{id}/reject", approver));

        using var response = await _host.GetAsync($"/api/v1/sync/plans/{id}/script", proposer);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        await using var stream = await response.Content.ReadAsStreamAsync(Ct);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(
            [".data.json", ".ps1", ".report.csv", ".report.md"],
            zip.Entries.Select(e => e.Name.Replace($"jinji-entra-sync-{id:D}", string.Empty, StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        // 承認前の取得（409）は失敗、承認後は成功として残る
        Assert.Equal(
            ["user:failed", "user:success"],
            await _sync.Db.QueryAsync<string>(null, "SELECT actor_type || ':' || outcome FROM audit_event WHERE action = 'sync.script.export' ORDER BY occurred_at, id", Ct));
    }

    [Fact(DisplayName = "API: jin-ji のデータが変わった計画の承認は 409 で、計画は古い計画になる")]
    public async Task Approve_Stale()
    {
        var created = await _sync.CreatePlanAsync(Ct);
        await _sync.Db.ExecuteAsync(
            _sync.Hr("hr2"),
            "INSERT INTO external_identity (id, person_id, source_system, external_id) VALUES ($1, $2, 'entra_id', $3)",
            Ct,
            Domain.EntityId.New(),
            _sync.Id("hr1"),
            Guid.NewGuid().ToString("D"));

        Assert.Equal(HttpStatusCode.Conflict, await PostAsync($"/api/v1/sync/plans/{created.Plan.Id}/approve", _issuer.User("portal", _sync.Id("hr2"), "hr_admin")));
        var (_, detail) = await _host.GetJsonAsync($"/api/v1/sync/plans/{created.Plan.Id}", _issuer.User("portal", _sync.Id("hr2"), "hr_admin"));
        Assert.Equal("stale", detail.GetProperty("plan").GetProperty("status").GetString());
    }

    private async Task<HttpStatusCode> PostAsync(string path, string token)
    {
        using var response = await _host.PostAsync(path, token);
        return response.StatusCode;
    }
}
