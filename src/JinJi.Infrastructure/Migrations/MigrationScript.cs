using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JinJi.Infrastructure.Migrations;

/// <summary>
/// 1 つのマイグレーション（SQL ファイル 1 つ）。
/// ファイル名は <c>NNNN_name.sql</c>（4 桁の版番号 + 英小文字・数字・下線）とする。
/// </summary>
public sealed partial class MigrationScript
{
    private MigrationScript(string version, string fileName, string sql, string checksum)
    {
        Version = version;
        FileName = fileName;
        Sql = sql;
        Checksum = checksum;
    }

    /// <summary>版番号（例: "0001"）。文字列順と適用順が一致する。</summary>
    public string Version { get; }

    /// <summary>ファイル名（例: "0001_initial_schema.sql"）。</summary>
    public string FileName { get; }

    /// <summary>実行する SQL。改行は LF に正規化済み。</summary>
    public string Sql { get; }

    /// <summary>正規化した SQL の SHA-256（16 進小文字）。適用済みの内容が書き換えられていないかの検証に使う。</summary>
    public string Checksum { get; }

    /// <summary>ファイル名と内容から作る。名前の規則に合わない場合は例外。</summary>
    public static MigrationScript Create(string fileName, string content)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var match = FileNamePattern().Match(fileName);
        if (!match.Success)
        {
            throw new MigrationException(
                $"マイグレーションのファイル名が規則（NNNN_name.sql）に合いません: {fileName}");
        }

        // 改行コードと BOM の違いでチェックサムが変わらないように正規化する
        var sql = content.TrimStart('﻿').ReplaceLineEndings("\n");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sql));
        var checksum = Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);

        return new MigrationScript(match.Groups["version"].Value, fileName, sql, checksum);
    }

    [GeneratedRegex(@"^(?<version>\d{4})_[a-z0-9_]+\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
