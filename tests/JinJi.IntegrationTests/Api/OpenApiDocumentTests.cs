using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests.Api;

/// <summary>
/// 保存した OpenAPI の文書（web/openapi/v1.json。UI の型 schema.gen.ts の元）が、API の文書と一致すること。
/// </summary>
/// <remarks>
/// 文書は、人のログインとトークンの発行（管理 API・同意）を有効にした構成で比べる（保存した文書はその構成の API から取ったもの）。
/// 文書を更新するときは、環境変数 <c>JINJI_UPDATE_OPENAPI=1</c> を付けてこのテストを流す（<c>JINJI_DOTNET_PASS_ENV=JINJI_UPDATE_OPENAPI</c>）。
/// その後 <c>scripts/pnpm.sh gen:api</c> で UI の型を作り直す。servers の URL は待ち受けのポートで変わるので、保存した文書の値に置き換えて比べる。
/// </remarks>
public sealed class OpenApiDocumentTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>, IAsyncLifetime
{
    private const string SavedServer = "https://127.0.0.1:28080/";

    private static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private OAuthTestEnvironment _env = null!;

    public async ValueTask InitializeAsync() => _env = await OAuthTestEnvironment.StartAsync(db);

    public async ValueTask DisposeAsync() => await _env.DisposeAsync();

    [Fact(DisplayName = "保存した OpenAPI の文書（web/openapi/v1.json）が API の文書と一致する")]
    public async Task SavedDocument_MatchesApi()
    {
        using var response = await _env.Host.GetAsync("/openapi/v1.json", null);
        var live = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
        live["servers"] = new JsonArray(new JsonObject { ["url"] = SavedServer });

        var path = Path.Combine(RepositoryPaths.Root, "web", "openapi", "v1.json");
        if (Environment.GetEnvironmentVariable("JINJI_UPDATE_OPENAPI") == "1")
        {
            await File.WriteAllTextAsync(path, live.ToJsonString(Write) + "\n", TestContext.Current.CancellationToken);
        }

        var saved = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.True(
            JsonNode.DeepEquals(live, saved),
            "web/openapi/v1.json が API の文書と違います。JINJI_UPDATE_OPENAPI=1 でこのテストを流して保存し直し、scripts/pnpm.sh gen:api で型を作り直してください。");
    }
}
