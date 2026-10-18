using JinJi.Host;
using JinJi.Worker.Ingest;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>開発用の CLI（<c>JinJi.Host import</c>）とワーカーのジョブ。</summary>
public sealed class ImportCommandTests : IAsyncLifetime
{
    private IngestDatabase _db = null!;
    private string _directory = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        _directory = Directory.CreateTempSubdirectory("jinji-import-").FullName;
    }

    public async ValueTask DisposeAsync()
    {
        Directory.Delete(_directory, recursive: true);
        await _db.DisposeAsync();
    }

    [Fact(DisplayName = "CLI: import <mapping.json> <file> --executed-by で取り込み、捨てた列は名前と件数だけを出す")]
    public async Task Cli_ImportsCsv()
    {
        var (mapping, file) = await WriteFilesAsync();
        using var output = new StringWriter();

        var exit = await ImportCommand.RunAsync(
            [mapping, file, "--executed-by", _db.Id("hr1").ToString(), $"--ConnectionStrings:App={_db.Database.AppConnectionString}"],
            output);

        Assert.Equal(0, exit);
        var text = output.ToString();
        Assert.Contains("rows: 1", text, StringComparison.Ordinal);
        Assert.Contains("proposals: 1", text, StringComparison.Ordinal);
        Assert.Contains("dropped: 生年月日 (forbidden, 1)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1980", text, StringComparison.Ordinal);
        Assert.Equal(
            ["system:cli:success"],
            await _db.QueryAsync<string>(null, "SELECT actor_type || ':' || channel || ':' || outcome FROM audit_event WHERE action = 'ingest.import'", Ct));
        Assert.Equal([_db.Id("hr1")], await _db.QueryAsync<Guid>(null, "SELECT executed_by_person_id FROM import_batch", Ct));
    }

    [Theory(DisplayName = "CLI: 引数が足りない・実行者が UUID でない・有効日の形が違えば使い方を出して終了する")]
    [InlineData("only-one-arg")]
    [InlineData("a.json", "b.csv")]
    [InlineData("a.json", "b.csv", "--executed-by", "not-a-uuid")]
    [InlineData("a.json", "b.csv", "--executed-by", "0190a5f0-0000-7000-8000-000000000000", "--effective-date", "2026/04/01")]
    public async Task Cli_RejectsInvalidArguments(params string[] args) => Assert.Equal(2, await ImportCommand.RunAsync(args, TextWriter.Null));

    [Fact(DisplayName = "ワーカー: ジョブは項目マッピングとファイルを読み、閲覧者（system・worker）と実行者で取り込む")]
    public async Task WorkerJob_Imports()
    {
        var (mapping, file) = await WriteFilesAsync();
        var job = new ImportJob(_db.Importer);

        var result = await job.RunAsync(IngestDatabase.Worker, new ImportJobRequest(mapping, file, _db.Id("hr1")), Ct);

        Assert.Equal(1, result.RowCount);
        var batch = await _db.Store.FindBatchAsync(_db.HrUser("hr2"), result.BatchId, Ct);
        Assert.Equal("people.csv", batch!.FileName);
        Assert.Matches("^[0-9a-f]{64}$", batch.FileSha256);
        Assert.Equal("cli-people", batch.MappingName);
    }

    private async Task<(string Mapping, string File)> WriteFilesAsync()
    {
        var mapping = Path.Combine(_directory, "mapping.json");
        var file = Path.Combine(_directory, "people.csv");
        await File.WriteAllTextAsync(mapping, """
            {
              "name": "cli-people",
              "target": "people",
              "legalEntityCode": "A",
              "fields": { "employee_number": ["社員番号"], "extension_number": ["内線"] }
            }
            """, Ct);
        await File.WriteAllBytesAsync(file, Connectors.TextEncodingDetector.ShiftJis.GetBytes("社員番号,内線,生年月日\r\nE0002,1234,1980/01/02\r\n"), Ct);
        return (mapping, file);
    }
}
