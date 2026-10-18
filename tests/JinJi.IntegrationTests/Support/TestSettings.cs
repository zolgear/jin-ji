using System.Globalization;
using Npgsql;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// テスト用の PostgreSQL 接続情報。環境変数から読む。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>JINJI_PG_HOST（既定 127.0.0.1）、JINJI_PG_PORT（既定 25432）</item>
/// <item>JINJI_PG_ADMIN_USER（既定 postgres）、JINJI_PG_ADMIN_PASSWORD: 一時データベースの作成・削除に使う</item>
/// <item>JINJI_PG_OWNER_PASSWORD: jinji_owner（マイグレーション）</item>
/// <item>JINJI_PG_APP_PASSWORD: jinji_app（アプリ用ロール）</item>
/// </list>
/// scripts/dotnet.sh は deploy/compose/.env をそのまま渡す。
/// </remarks>
internal sealed class TestSettings
{
    public const string OwnerRole = "jinji_owner";
    public const string AppRole = "jinji_app";

    private TestSettings(string host, int port, string adminUser, string adminPassword, string ownerPassword, string appPassword)
    {
        Host = host;
        Port = port;
        AdminUser = adminUser;
        AdminPassword = adminPassword;
        OwnerPassword = ownerPassword;
        AppPassword = appPassword;
    }

    public string Host { get; }

    public int Port { get; }

    public string AdminUser { get; }

    public string AdminPassword { get; }

    public string OwnerPassword { get; }

    public string AppPassword { get; }

    public static TestSettings FromEnvironment()
    {
        var port = Environment.GetEnvironmentVariable("JINJI_PG_PORT");
        return new TestSettings(
            Environment.GetEnvironmentVariable("JINJI_PG_HOST") ?? "127.0.0.1",
            string.IsNullOrEmpty(port) ? 25432 : int.Parse(port, CultureInfo.InvariantCulture),
            Environment.GetEnvironmentVariable("JINJI_PG_ADMIN_USER") ?? "postgres",
            Required("JINJI_PG_ADMIN_PASSWORD"),
            Required("JINJI_PG_OWNER_PASSWORD"),
            Required("JINJI_PG_APP_PASSWORD"));
    }

    public string ConnectionString(string database, string user, string password) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = database,
            Username = user,
            Password = password,
            // 一時データベースを確実に削除できるよう、接続はプールしない
            Pooling = false,
        }.ConnectionString;

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"環境変数 {name} が未設定です。deploy/compose/.env を用意し、scripts/dotnet.sh test で実行してください。");
}
