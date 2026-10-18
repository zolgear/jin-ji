using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using JinJi.Application.Auth;
using JinJi.Host;
using JinJi.Host.Auth;
using JinJi.IntegrationTests.Support;
using Microsoft.Extensions.Configuration;

namespace JinJi.IntegrationTests.Auth;

/// <summary>人のログインの設定・Data Protection の鍵・管理 CLI の入力の検査。</summary>
public sealed class AuthHostTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(DisplayName = "Data Protection: 鍵は PostgreSQL に証明書で暗号化して保存する（平文の鍵を持たない）")]
    public async Task DataProtection_KeysAreEncryptedWithCertificate()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=jinji-dp-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var pfx = Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12, "pw"));
        using var issuer = new TestIssuer();

        // 鍵がまだ無い DB で起動し、最初の鍵を作らせる
        await using var database = await TestDatabase.CreateAsync(Ct);
        await database.MigrateAsync(Ct);
        await using (var host = await ApiTestHost.StartAsync(
                         database.AppConnectionString,
                         issuer,
                         [
                             $"--JinJi:Auth:Login:PublicOrigin={AuthTestHost.Origin}",
                             "--JinJi:DataProtection:AllowUnprotectedKeys=false",
                             $"--JinJi:DataProtection:Certificate:Base64={pfx}",
                             "--JinJi:DataProtection:Certificate:Password=pw",
                         ]))
        {
            using var browser = new TestBrowser(host.ApiPort);
            await browser.RefreshCsrfAsync();
        }

        await using var app = await database.OpenAppAsync(Ct);
        await SampleData.SetSessionViewerAsync(app, "system", string.Empty, null, Ct);
        await using var command = new Npgsql.NpgsqlCommand("SELECT xml FROM data_protection_key", app);
        var created = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(Ct))
        {
            while (await reader.ReadAsync(Ct))
            {
                created.Add(reader.GetString(0));
            }
        }

        var xml = Assert.Single(created);
        Assert.Contains("EncryptedData", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<masterKey", xml, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Data Protection: 証明書も明示の許可も無ければ起動しない。不正な証明書の指定も起動しない")]
    public async Task DataProtection_RequiresCertificateOrExplicitOptIn()
    {
        string[] baseArgs =
        [
            $"--ConnectionStrings:App={db.Database.AppConnectionString}",
            "--JinJi:Listen:ApiPort=0",
            "--JinJi:Listen:MetricsPort=0",
        ];

        var ex = Assert.Throws<InvalidOperationException>(() => ServerHost.BuildWeb(HostRole.Api, baseArgs));
        Assert.Contains("JinJi:DataProtection", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => ServerHost.BuildWeb(HostRole.Api, [.. baseArgs, "--JinJi:DataProtection:Certificate:Base64=bm90LWEtY2VydA=="]));

        // MCP だけの役割は人のログイン（Cookie・Data Protection の鍵）を持たない
        await using var mcp = ServerHost.BuildWeb(HostRole.Mcp, [.. baseArgs, "--JinJi:Listen:McpPort=0"]);
        Assert.Null(mcp.Services.GetService(typeof(HumanLoginMarker)));
    }

    [Theory(DisplayName = "設定: 人のログインの不正な設定では起動しない")]
    [InlineData("--JinJi:Auth:Login:PublicOrigin=http://jinji.example")]
    [InlineData("--JinJi:Auth:Login:PublicOrigin=https://jinji.example/app")]
    [InlineData("--JinJi:Auth:Login:PasskeyServerDomain=jinji.test")]
    [InlineData("--JinJi:Auth:Login:Session:IdleTimeout=13:00:00")]
    [InlineData("--JinJi:Auth:Login:RateLimit:PerAddressAndAccount=50")]
    [InlineData("--JinJi:Auth:Login:Providers:0:Name=local")]
    public void LoginOptions_RejectInvalidValues(string arg)
    {
        string[] args = arg.Contains("Providers", StringComparison.Ordinal)
            ? [arg, "--JinJi:Auth:Login:PublicOrigin=https://jinji.test", "--JinJi:Auth:Login:Providers:0:Type=oidc", "--JinJi:Auth:Login:Providers:0:ClientId=x", "--JinJi:Auth:Login:Providers:0:Authority=https://idp.test"]
            : [arg];
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(args.Select(a => a[2..].Split('=', 2)).Select(kv => new KeyValuePair<string, string?>(kv[0], kv[1])))
            .Build();

        Assert.ThrowsAny<ArgumentException>(() => { _ = AuthRegistration.Login(configuration); });
    }

    [Fact(DisplayName = "設定: 既定値（セッション 8 / 12 時間、ロックアウト 5 回 15 分、リンク 1 時間）")]
    public void LoginOptions_Defaults()
    {
        var options = AuthRegistration.Login(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        Assert.Equal(TimeSpan.FromHours(8), options.Session.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(12), options.Session.AbsoluteTimeout);
        Assert.Equal(5, options.Lockout.MaxFailedAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Lockout.Duration);
        Assert.Equal(TimeSpan.FromHours(1), options.ResetLinkLifetime);
        Assert.True(options.RateLimit.PerAddressAndAccount < options.Lockout.MaxFailedAttempts);
        Assert.Null(options.PasskeyServerDomain);
    }

    [Fact(DisplayName = "管理 CLI: 引数の誤りは 2、知らないロール・無い人は 1。未紐付けのログインの一覧を出す")]
    public async Task Cli_ValidatesArguments()
    {
        Assert.Equal(2, (await db.CliAsync("create-local-account", "--user-name", "x")).Exit);
        Assert.Equal(2, (await db.CliAsync("bind-login", "--person", Guid.NewGuid().ToString())).Exit);
        Assert.Equal(2, (await db.CliAsync("issue-reset-link", "--user-name", "x", "--lifetime", "soon")).Exit);
        Assert.Equal(1, (await db.CliAsync("grant-role", "--person", db.Fixture.Person("u1_member").ToString(), "--role", "superuser")).Exit);
        Assert.Equal(1, (await db.CliAsync("create-local-account", "--person", Guid.NewGuid().ToString(), "--user-name", "nobody-person")).Exit);
        Assert.Equal(1, (await db.CliAsync("create-local-account", "--person", db.Fixture.Person("u1_member").ToString(), "--user-name", "bad name!")).Exit);
        Assert.Equal(1, (await db.CliAsync("grant-role", "--person", db.Fixture.Person("hr_admin").ToString(), "--role", "hr_admin")).Exit);

        var (exit, output) = await db.CliAsync("list-unbound-logins", "--all", "true");
        Assert.Equal(0, exit);
        Assert.StartsWith("id\tprovider\tsubject", output, StringComparison.Ordinal);

        Assert.Equal(0, (await db.CliAsync("grant-role", "--person", db.Fixture.Person("dept2_head").ToString(), "--role", "hr_admin")).Exit);
        Assert.Equal(0, (await db.CliAsync("revoke-role", "--person", db.Fixture.Person("dept2_head").ToString(), "--role", "hr_admin")).Exit);
        Assert.Equal(1, (await db.CliAsync("revoke-role", "--person", db.Fixture.Person("dept2_head").ToString(), "--role", "hr_admin")).Exit);
        Assert.True(await db.CountAuditAsync("auth.role.grant", "success", Ct, "system") >= 1);
    }

    [Fact(DisplayName = "秘密の値: 256 ビットの乱数で、保存は SHA-256")]
    public void Secrets_AreRandomAndHashed()
    {
        var token = AuthSecrets.NewToken();
        Assert.Equal(43, token.Length);
        Assert.NotEqual(token, AuthSecrets.NewToken());
        Assert.Matches("^[0-9a-f]{64}$", AuthSecrets.Sha256(token));
        Assert.Equal("ALICE", LocalAccountNames.Normalize(" ａｌｉｃｅ "));
        Assert.False(LocalAccountNames.IsValid("a b"));
    }

    [Fact(DisplayName = "セキュリティヘッダー: パスキーは同じオリジンだけに許し、/auth の応答はキャッシュさせない")]
    public async Task SecurityHeaders_AllowPasskeysOnSameOrigin()
    {
        using var issuer = new TestIssuer();
        await using var host = await AuthTestHost.StartAsync(db, issuer);
        using var browser = new TestBrowser(host.ApiPort);
        using var response = await browser.GetAsync("/auth/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = string.Join(",", response.Headers.GetValues("Permissions-Policy"));
        Assert.Contains("publickey-credentials-get=(self)", policy, StringComparison.Ordinal);
        Assert.Contains("publickey-credentials-create=(self)", policy, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("connect-src 'self'", string.Join(",", response.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
    }
}
