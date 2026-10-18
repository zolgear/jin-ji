using System.Globalization;
using System.Net;
using System.Text.Json;
using JinJi.IntegrationTests.Support;
using Npgsql;

namespace JinJi.IntegrationTests.Api;

/// <summary>変更フィード（GET /api/v1/changes）の端から端まで。データを書き足すので、他のテストクラスとは別のデータベースを使う。</summary>
public sealed class ChangeFeedApiTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>, IAsyncLifetime
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(15);

    private readonly TestIssuer _issuer = new();
    private ApiTestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    public async ValueTask InitializeAsync() => _host = await ApiTestHost.StartAsync(db.Database.AppConnectionString, _issuer);

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _issuer.Dispose();
    }

    [Fact(DisplayName = "変更フィード: 書き足した人が届く。将来の予定は人事管理者以外には届かず、人事管理者には届く。本文は種別と ID だけ")]
    public async Task NewChangesAppear_FutureChangesOnlyForAdmin()
    {
        var service = _issuer.Service("sync");
        var admin = _issuer.User("portal", F.Person("hr_admin"), "hr_admin");
        var serviceCursor = await StartAsync(service);
        var adminCursor = await StartAsync(admin);

        await using (var connection = await OpenAsHrAdminAsync())
        {
            await SampleData.InsertAssignmentAsync(connection, F.Person("u3_member"), F.Unit("S3"), "concurrent", Range(F.Today), Ct);
            await SampleData.InsertAssignmentAsync(connection, F.Person("dp2_member"), F.Unit("S3"), "concurrent", Range(F.Today.AddDays(5)), Ct);
        }

        var (seen, items) = await PollUntilAsync(service, serviceCursor, ids => ids.Contains(F.Person("u3_member")));
        Assert.Contains(F.Person("u3_member"), seen);
        Assert.DoesNotContain(F.Person("dp2_member"), seen);
        Assert.All(items, item => Assert.Equal(["id", "type"], item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        Assert.Contains(items, item => item.GetProperty("type").GetString() == "person");

        var (adminSeen, _) = await PollUntilAsync(admin, adminCursor, ids => ids.Contains(F.Person("u3_member")) && ids.Contains(F.Person("dp2_member")));
        Assert.Contains(F.Person("dp2_member"), adminSeen);
    }

    [Fact(DisplayName = "変更フィード: 後からコミットされた変更を取りこぼさない（実行中のトランザクションより後の行は、それが終わるまで返さない）")]
    public async Task LateCommit_IsNotMissed()
    {
        var service = _issuer.Service("sync");
        var cursor = await StartAsync(service);

        await using var slow = await OpenAsHrAdminAsync();
        await using var transaction = await slow.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand(
            "INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, valid_period) VALUES ($1, $2, $3, 'concurrent', $4::daterange)",
            slow,
            transaction))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = Domain.EntityId.New() });
            command.Parameters.Add(new NpgsqlParameter { Value = F.Person("s3_member") });
            command.Parameters.Add(new NpgsqlParameter { Value = F.Unit("R1") });
            command.Parameters.Add(new NpgsqlParameter { Value = Range(F.Today) });
            await command.ExecuteNonQueryAsync(Ct);
        }

        await using (var fast = await OpenAsHrAdminAsync())
        {
            await SampleData.InsertAssignmentAsync(fast, F.Person("r1_member"), F.Unit("S3"), "concurrent", Range(F.Today), Ct);
        }

        // 先に始まった遅いトランザクションが終わるまでは、後からコミットされた行も返さない（位置を進めない）
        var (status, page) = await _host.GetJsonAsync($"/api/v1/changes?since={Uri.EscapeDataString(cursor)}", service);
        Assert.Equal(HttpStatusCode.OK, status);
        var during = ApiEndpointTests.Ids(page.GetProperty("items"));
        Assert.DoesNotContain(F.Person("r1_member"), during);
        Assert.DoesNotContain(F.Person("s3_member"), during);

        await transaction.CommitAsync(Ct);
        var (seen, _) = await PollUntilAsync(service, page.GetProperty("next_cursor").GetString()!, ids => ids.Contains(F.Person("r1_member")) && ids.Contains(F.Person("s3_member")));
        Assert.Contains(F.Person("s3_member"), seen);
    }

    [Fact(DisplayName = "変更フィード: 改ざんしたカーソル・別の利用者のカーソル・過大な limit は 400")]
    public async Task Cursor_IsTamperProof()
    {
        var service = _issuer.Service("sync");
        var cursor = await StartAsync(service);
        var tampered = cursor[..^2] + (cursor[^2] == 'A' ? "B" : "A") + cursor[^1];

        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/changes?since={Uri.EscapeDataString(tampered)}", service)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync($"/api/v1/changes?since={Uri.EscapeDataString(cursor)}", _issuer.User("portal", F.Person("u1_member")))).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await _host.GetJsonAsync("/api/v1/changes?limit=1001", service)).Status);
        Assert.Equal(HttpStatusCode.OK, (await _host.GetJsonAsync($"/api/v1/changes?since={Uri.EscapeDataString(cursor)}", service)).Status);
    }

    private static string Range(DateOnly from) => $"[{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)},)";

    private async Task<NpgsqlConnection> OpenAsHrAdminAsync()
    {
        var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(connection, Ct);
        return connection;
    }

    private async Task<string> StartAsync(string token)
    {
        var (status, body) = await _host.GetJsonAsync("/api/v1/changes", token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body.GetProperty("items").EnumerateArray());
        return body.GetProperty("next_cursor").GetString()!;
    }

    /// <summary>
    /// 条件を満たすまでフィードを読み進める。位置は実行中のトランザクション（同じサーバーで並行して動く他のテストを含む）が終わるまで進まないので、待って読み直す。
    /// </summary>
    private async Task<(List<Guid> Seen, List<JsonElement> Items)> PollUntilAsync(string token, string cursor, Func<IReadOnlyCollection<Guid>, bool> done)
    {
        var seen = new List<Guid>();
        var items = new List<JsonElement>();
        var deadline = DateTime.UtcNow + PollTimeout;
        while (true)
        {
            var (status, page) = await _host.GetJsonAsync($"/api/v1/changes?since={Uri.EscapeDataString(cursor)}&limit=50", token);
            Assert.Equal(HttpStatusCode.OK, status);
            items.AddRange(page.GetProperty("items").EnumerateArray());
            seen.AddRange(ApiEndpointTests.Ids(page.GetProperty("items")));
            cursor = page.GetProperty("next_cursor").GetString()!;
            if (done(seen))
            {
                return (seen, items);
            }

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("変更フィードに期待した変更が現れませんでした。");
            }

            if (!page.GetProperty("has_more").GetBoolean())
            {
                await Task.Delay(200, Ct);
            }
        }
    }
}
