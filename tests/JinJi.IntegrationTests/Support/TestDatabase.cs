using JinJi.Infrastructure.Migrations;
using Npgsql;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// テストごとの一時データベース。jinji_owner を所有者として作り、破棄時に削除する。
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly TestSettings _settings;

    private TestDatabase(TestSettings settings, string name)
    {
        _settings = settings;
        Name = name;
    }

    public string Name { get; }

    /// <summary>テーブル所有者ロール（マイグレーション用）の接続文字列。</summary>
    public string OwnerConnectionString => _settings.ConnectionString(Name, TestSettings.OwnerRole, _settings.OwnerPassword);

    /// <summary>アプリ用ロールの接続文字列。</summary>
    public string AppConnectionString => _settings.ConnectionString(Name, TestSettings.AppRole, _settings.AppPassword);

    public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var settings = TestSettings.FromEnvironment();
        var database = new TestDatabase(settings, $"jinji_test_{Guid.NewGuid():N}");

        // 名前は自前で生成した英数字のみなので、識別子として直接埋め込んでよい
        await database.ExecuteAsAdminAsync(
            $"CREATE DATABASE \"{database.Name}\" OWNER {TestSettings.OwnerRole}",
            cancellationToken);
        return database;
    }

    /// <summary>埋め込みのマイグレーションを jinji_owner で適用する。</summary>
    public Task<MigrationResult> MigrateAsync(CancellationToken cancellationToken) =>
        new MigrationRunner(OwnerConnectionString).ApplyAsync(EmbeddedMigrations.Load(), cancellationToken);

    public async Task<NpgsqlConnection> OpenOwnerAsync(CancellationToken cancellationToken) =>
        await OpenAsync(OwnerConnectionString, cancellationToken);

    public async Task<NpgsqlConnection> OpenAppAsync(CancellationToken cancellationToken) =>
        await OpenAsync(AppConnectionString, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await ExecuteAsAdminAsync($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", CancellationToken.None);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task ExecuteAsAdminAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(
            _settings.ConnectionString("postgres", _settings.AdminUser, _settings.AdminPassword),
            cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
