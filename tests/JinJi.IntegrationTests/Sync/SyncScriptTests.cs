using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;
using JinJi.Sync;

namespace JinJi.IntegrationTests.Sync;

/// <summary>同期の設定・スクリプト一式の生成（エスケープ・事前条件・スコープ）・実行ログの解釈（DB を使わない）。</summary>
public sealed class SyncScriptTests
{
    /// <summary>悪意のある氏名・組織名（PowerShell・Markdown・HTML・CSV で意味を持つ文字）。</summary>
    public static readonly string[] Malicious =
    [
        "'; Remove-Item -Recurse -Force C:\\ #",
        "$(Invoke-Expression 'calc')",
        "`r`n Stop-Computer",
        "\"; Write-Host pwned; \"",
        "${env:USERPROFILE}",
        "<script>alert(1)</script>",
        "=cmd|' /C calc'!A0",
        "a | b ``` c",
        "@SUM(1+1)",
        "改行\nを含む",
    ];

    [Fact(DisplayName = "設定: サンプルの設定を読める。未知のキー・使えない差し込み・不正な段・除外の指定は拒否する")]
    public void Settings_Parse()
    {
        var sample = SyncSettings.Parse(File.ReadAllBytes(Path.Combine(RepositoryPaths.Root, "samples", "messy-corp", "mappings", "entra_sync.json")));
        Assert.Equal(("messy-corp-entra-sync", "entra_id", 10), (sample.Name, sample.SourceSystem, sample.Fields.Count));
        Assert.Equal(sample.Sha256(), SyncSettings.Parse(Encoding.UTF8.GetBytes(sample.ToCanonicalJson())).Sha256());
        Assert.Equal(sample.Sha256(), (sample with { Comment = "別の説明" }).Sha256());
        Assert.NotEqual(sample.Sha256(), (sample with { DisplayNameFormat = "{family}{given}" }).Sha256());

        foreach (var json in new[]
                 {
                     """{ "unknownKey": 1 }""",
                     """{ "displayNameFormat": "{birthday}" }""",
                     """{ "displayNameFormat": "{family" }""",
                     """{ "departmentLevel": "Department!" }""",
                     """{ "fields": ["birthday"] }""",
                     """{ "fields": [] }""",
                     """{ "exclude": [{ "objectId": "x" }] }""",
                     """{ "exclude": [{ "objectId": "00000000-0000-0000-0000-000000000000", "employeeNumber": "1" }] }""",
                     """{ "jobTitle": "manager" }""",
                     """{ "planMaxAgeDays": 0 }""",
                 })
        {
            Assert.Throws<FormatException>(() => SyncSettings.Parse(Encoding.UTF8.GetBytes(json)));
        }
    }

    [Theory(DisplayName = "設定: 表示名の形式（値の無い差し込みの括弧は取り除く）")]
    [InlineData("{family} {given}", "髙橋 一郎")]
    [InlineData("{family}{given}", "髙橋一郎")]
    [InlineData("{family} {given}（{department}）", "髙橋 一郎（営業一課）")]
    [InlineData("{family} {given} ({company})", "髙橋 一郎")]
    [InlineData("{family_latin} {given_latin}", "")]
    public void Settings_DisplayName(string format, string expected)
    {
        var settings = SyncSettings.Default with { DisplayNameFormat = format };
        settings.Validate();

        Assert.Equal(expected, settings.FormatDisplayName(new Dictionary<string, string?> { ["family"] = "髙橋", ["given"] = "一郎", ["department"] = "営業一課" }));
    }

    [Fact(DisplayName = "スクリプト: 悪意のある氏名・組織名はスクリプト本文に入らず、データ（ASCII の JSON）から同じ値で読める")]
    public void Script_DoesNotEmbedValues()
    {
        var (plan, items) = MaliciousPlan();
        var bundle = new EntraScriptRenderer().Render(plan, items);
        var name = EntraScriptRenderer.BaseName(plan.Id);

        var script = Encoding.UTF8.GetString(bundle.Files[name + ".ps1"]);
        foreach (var value in Malicious)
        {
            Assert.DoesNotContain(value, script, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("Invoke-Expression", script.Replace("（Invoke-Expression", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("iex ", script, StringComparison.OrdinalIgnoreCase);

        // スクリプトは値に依らない（計画 ID・SHA-256・件数だけが違う）
        var plain = new EntraScriptRenderer().Render(plan, [.. items.Select(i => i with { Changes = [.. i.Changes.Select(c => c with { Desired = "x" })] })]);
        Assert.Equal(
            script.Replace(bundle.DataSha256, "#", StringComparison.Ordinal),
            Encoding.UTF8.GetString(plain.Files[name + ".ps1"]).Replace(plain.DataSha256, "#", StringComparison.Ordinal));

        var data = bundle.Files[name + ".data.json"];
        Assert.All(data, b => Assert.True(b < 0x7F));
        using var json = JsonDocument.Parse(data);
        var desired = json.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("action").GetString() == "update_attributes")
            .SelectMany(i => i.GetProperty("changes").EnumerateArray()).Select(c => c.GetProperty("desired").GetString()).ToList();
        Assert.Equal(Malicious, desired);
        Assert.Equal(bundle.DataSha256, Convert.ToHexStringLower(SHA256.HashData(data)));
        Assert.Equal(bundle.DataSha256, new EntraScriptRenderer().DataSha256(plan, items));
    }

    [Fact(DisplayName = "スクリプト: UTF-8 BOM・CRLF。-WhatIf（SupportsShouldProcess）・データの SHA-256 の確認・事前条件・ログ・最小スコープを持つ")]
    public void Script_Structure()
    {
        var (plan, items) = MaliciousPlan();
        var bundle = new EntraScriptRenderer().Render(plan, items);
        var bytes = bundle.Files[EntraScriptRenderer.BaseName(plan.Id) + ".ps1"];

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var script = Encoding.UTF8.GetString(bytes[3..]);
        Assert.DoesNotContain("\n", script.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.StartsWith("#Requires -Version 5.1", script, StringComparison.Ordinal);
        Assert.Contains("[CmdletBinding(SupportsShouldProcess = $true", script, StringComparison.Ordinal);
        Assert.Contains($"$PlanId = '{plan.Id:D}'", script, StringComparison.Ordinal);
        Assert.Contains($"$ExpectedDataSha256 = '{bundle.DataSha256}'", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath $DataPath -Algorithm SHA256", script, StringComparison.Ordinal);
        Assert.Contains("$WriteScopes = @('User.ReadWrite.All')", script, StringComparison.Ordinal);
        Assert.Contains("$ReadScopes = @('User.Read.All')", script, StringComparison.Ordinal);
        Assert.Contains("Connect-MgGraph @connect", script, StringComparison.Ordinal);
        Assert.Contains("$connect = @{ Scopes = $scopes", script, StringComparison.Ordinal);
        Assert.Contains("'skipped_precondition'", script, StringComparison.Ordinal);
        Assert.Contains("'already_applied'", script, StringComparison.Ordinal);
        Assert.Contains("$PSCmdlet.ShouldProcess(", script, StringComparison.Ordinal);
        Assert.Contains("finally {", script, StringComparison.Ordinal);
        Assert.Contains("UTF8Encoding($true)", script, StringComparison.Ordinal);
        Assert.Contains(EntraScriptRenderer.LogFormat, script, StringComparison.Ordinal);
        Assert.DoesNotContain("__", script.Replace("$__", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // 生成は決定的
        Assert.Equal(bundle.Files.Keys, new EntraScriptRenderer().Render(plan, items).Files.Keys);
        Assert.Equal(bytes, new EntraScriptRenderer().Render(plan, items).Files[EntraScriptRenderer.BaseName(plan.Id) + ".ps1"]);
    }

    [Theory(DisplayName = "スクリプト: 最小のスコープ（無効化だけなら EnableDisableAccount + Read、属性・上長があれば ReadWrite、無ければ Read）")]
    [InlineData(new[] { SyncAction.DisableAccount, SyncAction.BlockSignIn }, new[] { "User.EnableDisableAccount.All", "User.Read.All" })]
    [InlineData(new[] { SyncAction.DisableAccount, SyncAction.UpdateAttributes }, new[] { "User.ReadWrite.All" })]
    [InlineData(new[] { SyncAction.SetManager }, new[] { "User.ReadWrite.All" })]
    [InlineData(new SyncAction[0], new[] { "User.Read.All" })]
    public void Script_Scopes(SyncAction[] actions, string[] expected)
    {
        var items = actions.Select((a, i) => Item(i + 1, a, [new SyncChange("accountEnabled", "true", "false")])).ToList();
        Assert.Equal(expected, EntraScriptRenderer.WriteScopes(items));
    }

    [Fact(DisplayName = "差分レポート: IT 全般統制の件数を先頭に出し、Markdown・HTML の記号をエスケープする。CSV は数式を無害化する")]
    public void Report_Escapes()
    {
        var (plan, items) = MaliciousPlan();
        var bundle = new EntraScriptRenderer().Render(plan, items);
        var name = EntraScriptRenderer.BaseName(plan.Id);

        var markdown = Encoding.UTF8.GetString(bundle.Files[name + ".report.md"]);
        Assert.True(markdown.IndexOf("## IT 全般統制（最優先）", StringComparison.Ordinal) < markdown.IndexOf("## 属性・上長の更新", StringComparison.Ordinal));
        Assert.Contains("| **退職者のアカウントの無効化** | 1 |", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", markdown, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("a | b", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("改行\nを含む", markdown, StringComparison.Ordinal);
        Assert.All(markdown.Split('\n').Where(l => l.StartsWith("| 2 |", StringComparison.Ordinal)), l => Assert.Equal(9, l.Split(" | ").Length));

        var csvBytes = bundle.Files[name + ".report.csv"];
        Assert.Equal([0xEF, 0xBB, 0xBF], csvBytes[..3]);
        var csv = Encoding.UTF8.GetString(csvBytes[3..]);
        Assert.Contains("\"'=cmd|' /C calc'!A0\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'@SUM(1+1)\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"\"\"; Write-Host pwned; \"\"\"", csv, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "実行ログ: BOM 付き・要素 1 つのオブジェクト（PowerShell 5.1）を読み、未知の項目の observed は捨て、メッセージを切り詰める")]
    public void Log_Parse()
    {
        var plan = Guid.NewGuid();
        var item = Guid.NewGuid();
        var sha = new string('a', 64);
        var message = new string('x', 2000);
        var log = $$$"""
            {"format":"jinji-entra-sync-log/1","planId":"{{{plan}}}","dataSha256":"{{{sha}}}","whatIf":false,"startedAt":"2026-10-01T03:00:00.0000000Z",
             "entries":{"itemId":"{{{item}}}","outcome":"skipped_precondition","observed":{"department":"旧組織","birthday":"1980-01-01","accountEnabled":true},
                        "message":"{{{message}}}","at":"2026-10-01T03:00:01Z"}}
            """;

        var parsed = new SyncLogParser().Parse([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(log)]);

        Assert.Equal((plan, sha, false), (parsed.PlanId, parsed.DataSha256, parsed.WhatIf));
        var entry = Assert.Single(parsed.Entries);
        Assert.Equal((item, SyncOutcome.SkippedPrecondition), (entry.ItemId, entry.Outcome));
        Assert.Equal(["accountEnabled", "department"], entry.Observed.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("true", entry.Observed["accountEnabled"]);
        Assert.Equal(SyncLogParser.MaxMessageLength, entry.Message!.Length);

        foreach (var bad in new[]
                 {
                     "{}",
                     """{"format":"other","planId":"x"}""",
                     $$"""{"format":"jinji-entra-sync-log/1","planId":"{{plan}}","dataSha256":"zz"}""",
                     $$"""{"format":"jinji-entra-sync-log/1","planId":"{{plan}}","dataSha256":"{{sha}}","entries":[{"itemId":"{{item}}","outcome":"deleted"}]}""",
                     $$"""{"format":"jinji-entra-sync-log/1","planId":"{{plan}}","dataSha256":"{{sha}}","entries":[{"itemId":"1","outcome":"failed"}]}""",
                     "not json",
                 })
        {
            Assert.Throws<SyncLogFormatException>(() => new SyncLogParser().Parse(Encoding.UTF8.GetBytes(bad)));
        }
    }

    /// <summary>退職者の無効化 1 件と、悪意のある値の属性の更新の計画（承認済み）。</summary>
    internal static (SyncPlanRecord Plan, IReadOnlyList<SyncPlanItem> Items) MaliciousPlan()
    {
        var plan = new SyncPlanRecord
        {
            Id = Guid.CreateVersion7(),
            Status = SyncPlanStatus.Approved,
            Settings = SyncSettings.Default with { Name = "<b>name</b> | `x`" },
            SettingsSha256 = new string('b', 64),
            SourceFileName = "entra_users.json",
            SourceSha256 = new string('c', 64),
            DirectoryUserCount = 2,
            JinjiFingerprint = new string('d', 64),
            BusinessDate = new DateOnly(2026, 10, 1),
            ProposedByPersonId = Guid.CreateVersion7(),
            ProposedViaActor = ActorType.User,
            ProposedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            DecidedByPersonId = Guid.CreateVersion7(),
            DecidedAt = DateTimeOffset.UtcNow,
        };
        var changes = Malicious.Select((v, i) => new SyncChange(EntraFields.Attributes[i % EntraFields.Attributes.Count], "old", v)).ToList();
        return (plan, [Item(1, SyncAction.DisableAccount, [new SyncChange("accountEnabled", "true", "false")]), Item(2, SyncAction.UpdateAttributes, changes)]);
    }

    private static SyncPlanItem Item(int sequence, SyncAction action, IReadOnlyList<SyncChange> changes) => new()
    {
        Id = Guid.CreateVersion7(),
        Sequence = sequence,
        Priority = action is SyncAction.DisableAccount or SyncAction.BlockSignIn ? SyncPriority.Critical : SyncPriority.Normal,
        Action = action,
        ObjectId = Guid.NewGuid().ToString("D"),
        UserPrincipalName = "<img src=x onerror=alert(1)>@example.co.jp",
        EmployeeNumber = "=1+1",
        InScript = true,
        Changes = changes,
        Basis = new SyncBasis { Code = "test", Jinji = [new SyncEvidence("department", "org_unit", null, "| `injected` |")] },
        Note = "$(note)",
    };
}
