using System.Globalization;
using JinJi.Infrastructure.Migrations;
using Npgsql;

namespace JinJi.Host;

/// <summary>
/// <c>JinJi.Host migrate</c>: 埋め込みの SQL マイグレーションを適用して終了する。
/// 接続文字列は ConnectionStrings:Owner（テーブル所有者ロール jinji_owner）を使う。
/// 未設定なら、開発用に JINJI_PG_* の環境変数（deploy/compose/.env と同じ名前）から組み立てる。
/// </summary>
internal static partial class MigrateCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        using var host = builder.Build();
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("JinJi.Migrate");
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        var connectionString = builder.Configuration.GetConnectionString("Owner") ?? FromDevelopmentVariables(builder.Configuration);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            LogMissingConnectionString(logger);
            return 2;
        }

        var scripts = EmbeddedMigrations.Load();
        try
        {
            var result = await new MigrationRunner(connectionString).ApplyAsync(scripts, lifetime.ApplicationStopping);
            foreach (var script in result.Applied)
            {
                LogApplied(logger, script.FileName);
            }

            LogCompleted(logger, result.Applied.Count, result.PreviouslyApplied);
            return 0;
        }
        catch (MigrationException ex)
        {
            LogFailed(logger, ex);
            return 1;
        }
    }

    /// <summary>開発用: JINJI_PG_HOST / JINJI_PG_PORT / JINJI_PG_DATABASE / JINJI_PG_OWNER_PASSWORD から組み立てる。</summary>
    private static string? FromDevelopmentVariables(IConfiguration configuration)
    {
        var password = configuration["JINJI_PG_OWNER_PASSWORD"];
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        var port = configuration["JINJI_PG_PORT"];
        return new NpgsqlConnectionStringBuilder
        {
            Host = configuration["JINJI_PG_HOST"] ?? "127.0.0.1",
            Port = string.IsNullOrEmpty(port) ? 25432 : int.Parse(port, CultureInfo.InvariantCulture),
            Database = configuration["JINJI_PG_DATABASE"] ?? "jinji",
            Username = "jinji_owner",
            Password = password,
        }.ConnectionString;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "接続文字列 ConnectionStrings:Owner が設定されていません（環境変数 ConnectionStrings__Owner、または開発用の JINJI_PG_OWNER_PASSWORD）")]
    private static partial void LogMissingConnectionString(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "適用しました: {FileName}")]
    private static partial void LogApplied(ILogger logger, string fileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "マイグレーション完了: 今回適用 {Applied} 件、適用済み {PreviouslyApplied} 件")]
    private static partial void LogCompleted(ILogger logger, int applied, int previouslyApplied);

    [LoggerMessage(Level = LogLevel.Error, Message = "マイグレーションに失敗しました")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
