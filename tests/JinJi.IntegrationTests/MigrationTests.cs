using JinJi.Infrastructure.Migrations;
using JinJi.IntegrationTests.Support;
using Npgsql;

namespace JinJi.IntegrationTests;

/// <summary>SQL マイグレーションのランナー。</summary>
public sealed class MigrationTests : IAsyncLifetime
{
    private TestDatabase _database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _database = await TestDatabase.CreateAsync(Ct);

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact(DisplayName = "空のデータベースに全マイグレーションを適用できる")]
    public async Task AppliesAllMigrationsToEmptyDatabase()
    {
        var scripts = EmbeddedMigrations.Load();
        Assert.NotEmpty(scripts);

        var result = await _database.MigrateAsync(Ct);

        Assert.Equal(scripts.Select(s => s.Version), result.Applied.Select(s => s.Version));
        Assert.Equal(0, result.PreviouslyApplied);
        Assert.Equal(scripts.Count, await CountHistoryAsync());
    }

    [Fact(DisplayName = "二度目の実行では何も適用しない")]
    public async Task SecondRun_AppliesNothing()
    {
        await _database.MigrateAsync(Ct);

        var second = await _database.MigrateAsync(Ct);

        Assert.Empty(second.Applied);
        Assert.Equal(EmbeddedMigrations.Load().Count, second.PreviouslyApplied);
    }

    [Fact(DisplayName = "適用済みのファイルが書き換えられていたら失敗する")]
    public async Task FailsWhenAppliedScriptWasModified()
    {
        var runner = new MigrationRunner(_database.OwnerConnectionString);
        await runner.ApplyAsync([MigrationScript.Create("0001_first.sql", "CREATE TABLE t1 (id int);")], Ct);

        var ex = await Assert.ThrowsAsync<MigrationException>(() => runner.ApplyAsync(
            [MigrationScript.Create("0001_first.sql", "CREATE TABLE t1 (id bigint);")], Ct));
        Assert.Contains("チェックサム", ex.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "失敗したファイルは丸ごと取り消され先行のファイルは残る")]
    public async Task FailedScript_IsRolledBackWhilePreviousScriptsRemain()
    {
        var runner = new MigrationRunner(_database.OwnerConnectionString);
        MigrationScript[] scripts =
        [
            MigrationScript.Create("0001_first.sql", "CREATE TABLE t1 (id int);"),
            MigrationScript.Create("0002_broken.sql", "CREATE TABLE t2 (id int); SELECT * FROM no_such_table;"),
        ];

        await Assert.ThrowsAsync<MigrationException>(() => runner.ApplyAsync(scripts, Ct));

        Assert.True(await TableExistsAsync("t1"));
        Assert.False(await TableExistsAsync("t2"));
        Assert.Equal(1, await CountHistoryAsync());
    }

    [Fact(DisplayName = "適用済みの最新版より古い未適用ファイルがあれば失敗する")]
    public async Task FailsOnPendingScriptOlderThanLatestApplied()
    {
        var runner = new MigrationRunner(_database.OwnerConnectionString);
        await runner.ApplyAsync([MigrationScript.Create("0002_second.sql", "SELECT 1;")], Ct);

        await Assert.ThrowsAsync<MigrationException>(() => runner.ApplyAsync(
            [
                MigrationScript.Create("0001_first.sql", "SELECT 1;"),
                MigrationScript.Create("0002_second.sql", "SELECT 1;"),
            ],
            Ct));
    }

    private async Task<long> CountHistoryAsync()
    {
        await using var connection = await _database.OpenOwnerAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM schema_migration", connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = await _database.OpenOwnerAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT to_regclass($1) IS NOT NULL", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = table });
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }
}
