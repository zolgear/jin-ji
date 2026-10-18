using System.Text;
using System.Text.Json;
using JinJi.Host;
using JinJi.Sync;

namespace JinJi.IntegrationTests.Sync;

/// <summary>同期の CLI（sync-plan・sync-script・sync-result・sync-apply）。</summary>
public sealed class SyncCommandTests : IAsyncLifetime
{
    private SyncDatabase _sync = null!;
    private string _directory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Connection => $"--ConnectionStrings:App={_sync.Db.Database.AppConnectionString}";

    public async ValueTask InitializeAsync()
    {
        _sync = await SyncDatabase.CreateAsync(Ct);
        _directory = Directory.CreateTempSubdirectory("jinji-sync-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        await _sync.DisposeAsync();
    }

    [Fact(DisplayName = "CLI: sync-plan で計画を作り、承認後に sync-script で書き出し、sync-result で実行ログを照合する（人の値は出力しない）")]
    public async Task PlanScriptResult()
    {
        var users = Path.Combine(_directory, "entra_users.json");
        await File.WriteAllBytesAsync(users, _sync.UsersJson(new() { ["member"] = new() { ["department"] = "旧組織" } }), Ct);
        var settings = Path.Combine(_directory, "sync.json");
        await File.WriteAllTextAsync(settings, """{ "fields": ["department", "accountEnabled"] }""", Ct);

        using var planOutput = new StringWriter();
        Assert.Equal(0, await SyncCommands.PlanAsync([users, "--executed-by", _sync.Id("hr1").ToString(), "--settings", settings, Connection], planOutput));
        var text = planOutput.ToString();
        Assert.Contains("critical: 1 (disable_account 1, block_sign_in 0)", text, StringComparison.Ordinal);
        Assert.Contains("items: 5 (in_script 2)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("旧組織", text, StringComparison.Ordinal);
        Assert.DoesNotContain("鈴木", text, StringComparison.Ordinal);
        var planId = Guid.Parse(text.Split('\n').Single(l => l.StartsWith("plan_id: ", StringComparison.Ordinal))["plan_id: ".Length..].Trim());

        var output = Path.Combine(_directory, "out");
        Assert.Equal(1, await SyncCommands.ScriptAsync([planId.ToString(), output, Connection], TextWriter.Null));
        await _sync.Service.ApproveAsync(_sync.Hr("hr2"), planId, Ct);
        using var scriptOutput = new StringWriter();
        Assert.Equal(0, await SyncCommands.ScriptAsync([planId.ToString(), output, Connection], scriptOutput));
        Assert.Equal(4, Directory.GetFiles(output).Length);
        var data = await File.ReadAllBytesAsync(Path.Combine(output, EntraScriptRenderer.BaseName(planId) + ".data.json"), Ct);
        using var document = JsonDocument.Parse(data);
        var entries = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => new { itemId = i.GetProperty("itemId").GetString(), outcome = "succeeded", observed = new { }, message = (string?)null, at = "2026-10-01T00:00:00Z" })
            .ToList();
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data));
        var log = Path.Combine(_directory, "run.log.json");
        await File.WriteAllTextAsync(
            log,
            JsonSerializer.Serialize(new { format = EntraScriptRenderer.LogFormat, planId, dataSha256 = sha, whatIf = false, entries }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            Ct);

        using var resultOutput = new StringWriter();
        Assert.Equal(0, await SyncCommands.ResultAsync([planId.ToString(), log, Connection], resultOutput));
        Assert.Contains("succeeded: 2", resultOutput.ToString(), StringComparison.Ordinal);
        Assert.Contains("missing: 0", resultOutput.ToString(), StringComparison.Ordinal);
        Assert.Equal(["system:cli:success"], await _sync.Db.QueryAsync<string>(null, "SELECT actor_type || ':' || channel || ':' || outcome FROM audit_event WHERE action = 'sync.result.import'", Ct));
    }

    [Fact(DisplayName = "CLI: 直接反映は既定で無効（終了コード 3）。引数の誤りは使い方を出して 2")]
    public async Task ApplyDisabled_AndUsage()
    {
        Assert.Equal(3, await SyncCommands.ApplyAsync([Guid.NewGuid().ToString(), Connection], TextWriter.Null));
        Assert.Equal(2, await SyncCommands.PlanAsync(["users.json"], TextWriter.Null));
        Assert.Equal(2, await SyncCommands.PlanAsync(["users.json", "--executed-by", "x"], TextWriter.Null));
        Assert.Equal(2, await SyncCommands.ScriptAsync(["not-a-uuid", "out"], TextWriter.Null));
        Assert.Equal(2, await SyncCommands.ResultAsync([Guid.NewGuid().ToString()], TextWriter.Null));
        Assert.Equal(2, await SyncCommands.ApplyAsync([], TextWriter.Null));
    }
}
