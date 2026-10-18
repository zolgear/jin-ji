using System.Net;
using JinJi.Domain;
using JinJi.Host;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// 人のログインのテスト用の一時データベース。<see cref="FixtureDatabase"/> に、Entra の外部 ID の対応と人事管理者のロールを足す。
/// </summary>
/// <remarks>
/// <code>
/// external_identity(entra_id): EntraOid → u1_member、RetiredOid → retired
/// role_grant: hr_admin・hr_head に hr_admin（管理 CLI の付与）
/// </code>
/// </remarks>
public class AuthFixtureDatabase : FixtureDatabase
{
    public const string TenantId = "6f1a3c1e-2b8d-4c55-9a0e-3d5b7c9e1f20";

    internal string EntraOid { get; } = Guid.NewGuid().ToString("D");

    internal string RetiredOid { get; } = Guid.NewGuid().ToString("D");

    protected override async Task SeedExtraAsync(CancellationToken cancellationToken)
    {
        await using var app = await Database.OpenAppAsync(cancellationToken);
        await SampleData.ActAsHrAdminAsync(app, cancellationToken);
        foreach (var (oid, person) in new[] { (EntraOid, "u1_member"), (RetiredOid, "retired") })
        {
            await SampleData.ExecuteAsync(
                app,
                "INSERT INTO external_identity (id, person_id, source_system, external_id) VALUES ($1, $2, 'entra_id', $3)",
                cancellationToken,
                EntityId.New(),
                Fixture.Person(person),
                oid);
        }

        foreach (var person in new[] { "hr_admin", "hr_head" })
        {
            await SampleData.ExecuteAsync(
                app,
                "INSERT INTO role_grant (id, person_id, role, granted_by_kind) VALUES ($1, $2, 'hr_admin', 'cli')",
                cancellationToken,
                EntityId.New(),
                Fixture.Person(person));
        }
    }

    /// <summary>テストで足す人（法人 A、在籍中）を登録する。</summary>
    internal async Task<Guid> InsertPersonAsync(string employeeNumber, string email, CancellationToken cancellationToken)
    {
        var id = EntityId.New();
        await using var app = await Database.OpenAppAsync(cancellationToken);
        await SampleData.ActAsHrAdminAsync(app, cancellationToken);
        await SampleData.ExecuteAsync(app, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, $3)", cancellationToken, id, Fixture.EntityA, employeeNumber);
        await SampleData.ExecuteAsync(
            app,
            """
            INSERT INTO person_version (id, person_id, family_name, display_name, search_name, work_email, employment_type, hire_date, valid_period)
            VALUES ($1, $2, '試験', '試験 太郎', '試験太郎', $3, 'regular', $4, daterange($4, NULL))
            """,
            cancellationToken,
            EntityId.New(),
            id,
            email,
            Fixture.LongAgo);
        return id;
    }

    /// <summary>人を今日から一時停止にする。</summary>
    internal async Task SuspendAsync(Guid person, CancellationToken cancellationToken)
    {
        await using var app = await Database.OpenAppAsync(cancellationToken);
        await SampleData.ActAsHrAdminAsync(app, cancellationToken);
        await SampleData.ExecuteAsync(
            app,
            "INSERT INTO account_suspension (id, person_id, valid_period) VALUES ($1, $2, daterange($3, NULL))",
            cancellationToken,
            EntityId.New(),
            person,
            Fixture.Today);
    }

    /// <summary>jin-ji 自身（system）の閲覧者で 1 列目を読む（認証の表の確認用）。</summary>
    internal async Task<List<T>> QueryAsSystemAsync<T>(string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var app = await Database.OpenAppAsync(cancellationToken);
        await SampleData.SetSessionViewerAsync(app, "system", string.Empty, null, cancellationToken);
        await using var command = new NpgsqlCommand(sql, app);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetFieldValue<T>(0));
        }

        return rows;
    }

    /// <summary>監査ログの action ごとの件数（人事管理者で読む）。</summary>
    internal async Task<long> CountAuditAsync(string action, string? outcome, CancellationToken cancellationToken, string? actorType = null)
    {
        await using var app = await Database.OpenAppAsync(cancellationToken);
        await SampleData.ActAsHrAdminAsync(app, cancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM audit_event WHERE action = $1 AND ($2::text IS NULL OR outcome = $2) AND ($3::text IS NULL OR actor_type = $3)",
            app);
        command.Parameters.Add(new NpgsqlParameter { Value = action });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)outcome ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)actorType ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    /// <summary>管理 CLI を実行し、標準出力を返す。</summary>
    internal async Task<(int Exit, string Output)> CliAsync(string command, params string[] args)
    {
        using var output = new StringWriter();
        var exit = await AuthCommands.RunAsync(
            command,
            [.. args, $"--ConnectionStrings:App={Database.AppConnectionString}", $"--JinJi:Auth:Login:PublicOrigin={AuthTestHost.Origin}", "--Logging:LogLevel:Default=Warning"],
            output);
        return (exit, output.ToString());
    }

    /// <summary>CLI の出力から値を読む（<c>name: value</c>）。</summary>
    internal static string Field(string output, string name) =>
        output.Split('\n').Select(l => l.TrimEnd('\r')).Single(l => l.StartsWith(name + ": ", StringComparison.Ordinal))[(name.Length + 2)..];

    /// <summary>CLI の出力のリンクの鍵（URL のフラグメント）。</summary>
    internal static string LinkToken(string output) => Field(output, "url").Split("#token=")[1];
}

/// <summary>人のログインを有効にしたテスト用のホスト。公開 URL は https://jinji.test（テストは http で接続し、Cookie は自分で扱う）。</summary>
internal static class AuthTestHost
{
    public const string Origin = "https://jinji.test";
    public const string RpId = "jinji.test";

    public static Task<ApiTestHost> StartAsync(
        FixtureDatabase db,
        TestIssuer issuer,
        IEnumerable<string>? extraArgs = null,
        Action<IServiceCollection>? configureServices = null) =>
        ApiTestHost.StartAsync(
            db.Database.AppConnectionString,
            issuer,
            [
                $"--JinJi:Auth:Login:PublicOrigin={Origin}",
                $"--JinJi:Auth:Login:PasskeyServerDomain={RpId}",
                // テストの既定はロックアウトより先にレート制限が効かないよう広げる（レート制限のテストは個別に絞る）
                "--JinJi:Auth:Login:RateLimit:PerAddress=1000",
                "--JinJi:Auth:Login:RateLimit:PerAddressAndAccount=1000",
                .. extraArgs ?? [],
            ],
            configureServices);

    /// <summary>外部 IdP でログインする（jin-ji → スタブの認可 → jin-ji のコールバック）。コールバックの応答を返す。</summary>
    public static async Task<HttpResponseMessage> LoginWithOidcAsync(TestBrowser browser, StubOidcProvider stub, string provider, Dictionary<string, object> claims, string returnUrl = "/")
    {
        stub.NextClaims = claims;
        using var start = await browser.GetAsync($"/auth/login/{provider}?return_url={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith(stub.Issuer + "/authorize", authorize.ToString(), StringComparison.Ordinal);

        using var idp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var issued = await idp.GetAsync(authorize, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, issued.StatusCode);
        var callback = issued.Headers.Location!;
        Assert.Equal(Origin, callback.GetLeftPart(UriPartial.Authority));
        return await browser.GetAsync(callback.PathAndQuery);
    }
}
