using JinJi.Infrastructure.Migrations;

namespace JinJi.IntegrationTests;

/// <summary>マイグレーションのファイル名・チェックサムの規則（DB を使わない）。</summary>
public sealed class MigrationScriptTests
{
    [Fact(DisplayName = "改行コードと BOM の違いはチェックサムに影響しない")]
    public void Checksum_IgnoresLineEndingsAndBom()
    {
        var lf = MigrationScript.Create("0001_first.sql", "CREATE TABLE t1 (id int);\nSELECT 1;\n");
        var crlf = MigrationScript.Create("0001_first.sql", "﻿CREATE TABLE t1 (id int);\r\nSELECT 1;\r\n");

        Assert.Equal(lf.Checksum, crlf.Checksum);
    }

    [Theory(DisplayName = "規則に合わないファイル名は拒否する")]
    [InlineData("1_initial.sql")]
    [InlineData("0001-initial.sql")]
    [InlineData("0001_Initial.sql")]
    [InlineData("0001_initial.txt")]
    public void RejectsInvalidFileName(string fileName)
    {
        Assert.Throws<MigrationException>(() => MigrationScript.Create(fileName, "SELECT 1;"));
    }

    [Fact(DisplayName = "埋め込みのマイグレーションは版番号順に並び重複がない")]
    public void EmbeddedMigrations_AreOrderedAndUnique()
    {
        var scripts = EmbeddedMigrations.Load();

        Assert.NotEmpty(scripts);
        Assert.Equal(scripts.Select(s => s.Version).Order(StringComparer.Ordinal), scripts.Select(s => s.Version));
        Assert.Equal(scripts.Count, scripts.Select(s => s.Version).Distinct().Count());
    }
}
