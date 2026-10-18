using System.Globalization;
using JinJi.Infrastructure.Migrations;
using Npgsql;

namespace JinJi.Host.Evaluation;

/// <summary>
/// 評価用の一時データベース（空の DB を作り、マイグレーションを適用し、終わったら捨てる）。
/// 接続情報はテストと同じ環境変数（JINJI_PG_HOST / JINJI_PG_PORT / JINJI_PG_ADMIN_USER / JINJI_PG_ADMIN_PASSWORD / JINJI_PG_OWNER_PASSWORD / JINJI_PG_APP_PASSWORD）。
/// </summary>
internal sealed class EvaluationDatabase : IAsyncDisposable
{
    private readonly IConfiguration _configuration;
    private readonly bool _keep;

    private EvaluationDatabase(IConfiguration configuration, string name, bool keep)
    {
        _configuration = configuration;
        Name = name;
        _keep = keep;
    }

    public string Name { get; }

    public string AppConnectionString => ConnectionString(Name, "jinji_app", Required("JINJI_PG_APP_PASSWORD"));

    public string OwnerConnectionString => ConnectionString(Name, "jinji_owner", Required("JINJI_PG_OWNER_PASSWORD"));

    public static async Task<EvaluationDatabase> CreateAsync(IConfiguration configuration, bool keep, CancellationToken cancellationToken)
    {
        var database = new EvaluationDatabase(configuration, $"jinji_eval_{Guid.NewGuid():N}", keep);
        await database.ExecuteAsAdminAsync($"CREATE DATABASE \"{database.Name}\" OWNER jinji_owner", cancellationToken);
        await new MigrationRunner(database.OwnerConnectionString).ApplyAsync(EmbeddedMigrations.Load(), cancellationToken);
        return database;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_keep)
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsAdminAsync($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", CancellationToken.None);
        }
    }

    private async Task ExecuteAsAdminAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(ConnectionString("postgres", _configuration["JINJI_PG_ADMIN_USER"] ?? "postgres", Required("JINJI_PG_ADMIN_PASSWORD")));
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string ConnectionString(string database, string user, string password)
    {
        var port = _configuration["JINJI_PG_PORT"];
        return new NpgsqlConnectionStringBuilder
        {
            Host = _configuration["JINJI_PG_HOST"] ?? "127.0.0.1",
            Port = string.IsNullOrEmpty(port) ? 25432 : int.Parse(port, CultureInfo.InvariantCulture),
            Database = database,
            Username = user,
            Password = password,
        }.ConnectionString;
    }

    private string Required(string name) =>
        _configuration[name] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"環境変数 {name} が未設定です（deploy/compose/.env を用意し、scripts/dotnet.sh で実行してください）。");
}
