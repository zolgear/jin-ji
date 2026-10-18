using System.Xml.Linq;
using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests;

/// <summary>
/// プロジェクト間の参照が設計（アーキテクチャ概要 §11）どおりであることを確かめる。
/// 参照を増やすときは、設計を見直したうえでこの表を更新する。
/// </summary>
public sealed class ProjectReferenceTests
{
    private static readonly Dictionary<string, string[]> Expected = new(StringComparer.Ordinal)
    {
        // ドメインは何にも依存しない
        ["JinJi.Domain"] = [],
        ["JinJi.Policy"] = ["JinJi.Domain"],
        ["JinJi.Application"] = ["JinJi.Domain", "JinJi.Policy"],
        ["JinJi.Infrastructure"] = ["JinJi.Application", "JinJi.Domain"],
        ["JinJi.Ingest"] = ["JinJi.Application", "JinJi.Domain"],
        ["JinJi.Connectors"] = ["JinJi.Domain", "JinJi.Ingest"],
        // LLM の呼び出し（OpenAI 公式 SDK）。ワーカー（取込）だけが参照する（ADR-0005）
        ["JinJi.Llm"] = ["JinJi.Ingest"],
        ["JinJi.Sync"] = ["JinJi.Application", "JinJi.Domain"],
        // API・MCP は取込系（Ingest / Connectors、Excel ライブラリ等）とインフラを参照しない
        ["JinJi.Api"] = ["JinJi.Application", "JinJi.Policy"],
        ["JinJi.Mcp"] = ["JinJi.Application", "JinJi.Policy"],
        ["JinJi.Worker"] = ["JinJi.Application", "JinJi.Connectors", "JinJi.Ingest", "JinJi.Llm", "JinJi.Sync"],
        // 合成ルート
        ["JinJi.Host"] = ["JinJi.Api", "JinJi.Infrastructure", "JinJi.Mcp", "JinJi.Worker"],
    };

    public static TheoryData<string> Projects => [.. Expected.Keys];

    [Fact(DisplayName = "src の全プロジェクトが表に登録されている")]
    public void AllSourceProjects_AreListed()
    {
        var actual = Directory.GetFiles(Path.Combine(RepositoryPaths.Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Expected.Keys.Order(StringComparer.Ordinal), actual);
    }

    [Theory(DisplayName = "プロジェクト参照が設計どおりである")]
    [MemberData(nameof(Projects))]
    public void ProjectReferences_MatchDesign(string project)
    {
        var references = LoadProject(project)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(Expected[project].Order(StringComparer.Ordinal), references);
    }

    [Fact(DisplayName = "ドメインはパッケージにもフレームワークにも依存しない")]
    public void Domain_HasNoPackageOrFrameworkReferences()
    {
        var project = LoadProject("JinJi.Domain");

        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Empty(project.Descendants("FrameworkReference"));
    }

    [Fact(DisplayName = "Excel ライブラリ（ClosedXML）を直接参照するのは取込コネクタだけ")]
    public void ExcelLibrary_IsReferencedOnlyByConnectors()
    {
        var referencing = Expected.Keys
            .Where(p => LoadProject(p).Descendants("PackageReference").Any(e => e.Attribute("Include")!.Value == "ClosedXML"))
            .ToList();

        Assert.Equal(["JinJi.Connectors"], referencing);
    }

    [Fact(DisplayName = "LLM の SDK（OpenAI）を直接参照するのは JinJi.Llm だけ")]
    public void LlmSdk_IsReferencedOnlyByLlm()
    {
        var referencing = Expected.Keys
            .Where(p => LoadProject(p).Descendants("PackageReference").Any(e => e.Attribute("Include")!.Value == "OpenAI"))
            .ToList();

        Assert.Equal(["JinJi.Llm"], referencing);
    }

    [Theory(DisplayName = "API・MCP は取込系（Connectors・Ingest・Llm・Worker）とその依存（ClosedXML・OpenAI など）を推移的にも参照しない")]
    [InlineData("JinJi.Api")]
    [InlineData("JinJi.Mcp")]
    public void ApiAndMcp_DoNotReachIngestDependencies(string project)
    {
        // プロジェクト参照の推移的な閉包
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([project]);
        while (pending.TryPop(out var current))
        {
            foreach (var reference in Expected[current].Where(closure.Add))
            {
                pending.Push(reference);
            }
        }

        Assert.DoesNotContain("JinJi.Connectors", closure);
        Assert.DoesNotContain("JinJi.Ingest", closure);
        Assert.DoesNotContain("JinJi.Worker", closure);
        Assert.DoesNotContain("JinJi.Infrastructure", closure);
        Assert.DoesNotContain("JinJi.Llm", closure);

        // lock ファイル（実際に復元されるパッケージ）にも取込系の依存が無い
        var lockFile = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", project, "packages.lock.json")).ToLowerInvariant();
        foreach (var package in IngestOnlyPackages.Concat(LlmOnlyPackages))
        {
            Assert.DoesNotContain($"\"{package}\"", lockFile, StringComparison.Ordinal);
        }
    }

    [Fact(DisplayName = "認証のパッケージ（OpenIdConnect・JwtBearer）を直接参照するのは合成ルート JinJi.Host だけで、API・MCP の lock ファイルには無い（ADR-0004）")]
    public void AuthenticationPackages_AreReferencedOnlyByHost()
    {
        string[] packages = ["Microsoft.AspNetCore.Authentication.OpenIdConnect", "Microsoft.AspNetCore.Authentication.JwtBearer"];
        var referencing = Expected.Keys
            .Where(p => LoadProject(p).Descendants("PackageReference").Any(e => packages.Contains(e.Attribute("Include")!.Value)))
            .ToList();

        Assert.Equal(["JinJi.Host"], referencing);
        foreach (var project in new[] { "JinJi.Api", "JinJi.Mcp" })
        {
            var lockFile = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", project, "packages.lock.json")).ToLowerInvariant();
            Assert.All(packages, p => Assert.DoesNotContain($"\"{p.ToLowerInvariant()}\"", lockFile, StringComparison.Ordinal));
        }

        // Host の lock ファイルの認証系（Microsoft.AspNetCore.Authentication.*・Microsoft.IdentityModel.*）は ADR-0004 に挙げた範囲だけ
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "JinJi.Host", "packages.lock.json")));
        var auth = document.RootElement.GetProperty("dependencies").EnumerateObject()
            .SelectMany(framework => framework.Value.EnumerateObject())
            .Select(p => p.Name.ToLowerInvariant())
            .Where(p => p.StartsWith("microsoft.aspnetcore.authentication", StringComparison.Ordinal) || p.StartsWith("microsoft.identitymodel", StringComparison.Ordinal)
                        || p == "system.identitymodel.tokens.jwt" || p == "microsoft.bcl.cryptography")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(AuthenticationPackages.Order(StringComparer.Ordinal), auth);
    }

    /// <summary>人のログインとトークンの検証が持ち込むパッケージ（ADR-0004「推移的依存を含めて 10 件、すべて Microsoft 発行」）。</summary>
    private static readonly string[] AuthenticationPackages =
    [
        "microsoft.aspnetcore.authentication.jwtbearer", "microsoft.aspnetcore.authentication.openidconnect",
        "microsoft.bcl.cryptography", "microsoft.identitymodel.abstractions", "microsoft.identitymodel.jsonwebtokens",
        "microsoft.identitymodel.logging", "microsoft.identitymodel.protocols", "microsoft.identitymodel.protocols.openidconnect",
        "microsoft.identitymodel.tokens", "system.identitymodel.tokens.jwt",
    ];

    [Fact(DisplayName = "JinJi.Llm の lock ファイルの推移的依存は ADR-0005 に列挙したものだけ")]
    public void LlmLockFile_MatchesAdr()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "JinJi.Llm", "packages.lock.json")));
        var packages = document.RootElement.GetProperty("dependencies").EnumerateObject()
            .SelectMany(framework => framework.Value.EnumerateObject())
            .Where(p => p.Value.GetProperty("type").GetString() != "Project")
            .Select(p => p.Name.ToLowerInvariant())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(LlmPackages.Order(StringComparer.Ordinal), packages);
        var adr = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "adr", "0005-llm-dependency.md")).ToLowerInvariant();
        Assert.All(packages, p => Assert.Contains(p, adr, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "MCP の SDK（ModelContextProtocol.AspNetCore）を直接参照するのは JinJi.Mcp だけで、API の lock ファイルには無い（ADR-0006）")]
    public void McpSdk_IsReferencedOnlyByMcp()
    {
        var referencing = Expected.Keys
            .Where(p => LoadProject(p).Descendants("PackageReference").Any(e => e.Attribute("Include")!.Value == "ModelContextProtocol.AspNetCore"))
            .ToList();

        Assert.Equal(["JinJi.Mcp"], referencing);
        var apiLock = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "JinJi.Api", "packages.lock.json")).ToLowerInvariant();
        Assert.All(McpPackages, p => Assert.DoesNotContain($"\"{p}\"", apiLock, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "JinJi.Mcp の lock ファイルの推移的依存は ADR-0006 に列挙したものだけ")]
    public void McpLockFile_MatchesAdr()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "JinJi.Mcp", "packages.lock.json")));
        var packages = document.RootElement.GetProperty("dependencies").EnumerateObject()
            .SelectMany(framework => framework.Value.EnumerateObject())
            .Where(p => p.Value.GetProperty("type").GetString() != "Project")
            .Select(p => p.Name.ToLowerInvariant())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(McpPackages.Order(StringComparer.Ordinal), packages);
        var adr = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "adr", "0006-mcp-sdk.md")).ToLowerInvariant();
        Assert.All(packages, p => Assert.Contains(p, adr, StringComparison.Ordinal));
    }

    /// <summary>JinJi.Mcp の lock ファイルのパッケージ（ADR-0006）。Microsoft.Extensions.* の多くは共有フレームワークのものが使われるので現れない。</summary>
    private static readonly string[] McpPackages =
    [
        "modelcontextprotocol.aspnetcore", "modelcontextprotocol", "modelcontextprotocol.core", "microsoft.extensions.ai.abstractions",
    ];

    /// <summary>JinJi.Llm の lock ファイルのパッケージ（ADR-0005）。Microsoft.Extensions.* は Host では共有フレームワークのものが使われる。</summary>
    private static readonly string[] LlmPackages =
    [
        "openai", "system.clientmodel", "system.memory.data",
        "microsoft.extensions.configuration.abstractions", "microsoft.extensions.dependencyinjection.abstractions",
        "microsoft.extensions.diagnostics.abstractions", "microsoft.extensions.fileproviders.abstractions",
        "microsoft.extensions.hosting.abstractions", "microsoft.extensions.logging.abstractions",
        "microsoft.extensions.options", "microsoft.extensions.primitives",
    ];

    /// <summary>LLM の SDK だけが持ち込むパッケージ（API・MCP の lock ファイルに現れてはならない）。</summary>
    private static readonly string[] LlmOnlyPackages = ["openai", "system.clientmodel", "system.memory.data"];

    [Fact(DisplayName = "取込コネクタの lock ファイルの推移的依存は ADR-0003 に列挙したものだけ")]
    public void ConnectorsLockFile_MatchesAdr()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryPaths.Root, "src", "JinJi.Connectors", "packages.lock.json")));
        var packages = document.RootElement.GetProperty("dependencies").EnumerateObject()
            .SelectMany(framework => framework.Value.EnumerateObject())
            .Where(p => p.Value.GetProperty("type").GetString() != "Project")
            .Select(p => p.Name.ToLowerInvariant())
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(IngestOnlyPackages.Order(StringComparer.Ordinal), packages);
        var adr = File.ReadAllText(Path.Combine(RepositoryPaths.Root, "docs", "adr", "0003-ingest-dependencies.md")).ToLowerInvariant();
        Assert.All(packages, p => Assert.Contains(p, adr, StringComparison.Ordinal));
    }

    /// <summary>取込コネクタ（ClosedXML）だけが持ち込むパッケージ（ADR-0003）。</summary>
    private static readonly string[] IngestOnlyPackages =
    [
        "closedxml", "closedxml.parser", "documentformat.openxml", "documentformat.openxml.framework",
        "excelnumberformat", "rbush.signed", "sixlabors.fonts", "system.io.packaging",
    ];

    private static XDocument LoadProject(string name) =>
        XDocument.Load(Path.Combine(RepositoryPaths.Root, "src", name, $"{name}.csproj"));
}
