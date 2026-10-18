using System.Text.Json;

namespace JinJi.Connectors;

/// <summary>
/// 外部のディレクトリのユーザー一覧の読み取り口。今は Microsoft Graph の <c>GET /users</c> 応答のファイル（<see cref="GraphUsersJsonFile"/>）を読む。
/// Graph への接続（ページングを含む）は、同じ口の別の実装として後で足す（読み取り専用の資格情報。overview §8）。
/// </summary>
public interface IDirectoryUserSource
{
    /// <summary>ユーザーを表にする（見出しは Graph のプロパティ名。行番号は value 配列の 0 始まりの位置 + 1）。</summary>
    Task<RawTable> ReadUsersAsync(CancellationToken cancellationToken);
}

/// <summary>Graph の <c>GET /users</c> 応答（<c>{"value": [...]}</c>）のファイル。</summary>
public sealed class GraphUsersJsonFile(byte[] content) : IDirectoryUserSource
{
    public Task<RawTable> ReadUsersAsync(CancellationToken cancellationToken) => Task.FromResult(GraphUsersJson.Read(content));
}

/// <summary>
/// Graph の <c>GET /users</c> 応答（JSON）を表にする。プロパティ名を見出しにし、値は文字列にする
/// （真偽値は true / false、配列は最初の要素、オブジェクトは捨てる、null は空）。
/// </summary>
/// <remarks>
/// 取込項目への対応と、保持しない項目（Graph にも birthday・streetAddress などがある）の除外は、CSV と同じく項目マッピング（<see cref="ColumnMapper"/>）が行う。
/// </remarks>
public static class GraphUsersJson
{
    public static RawTable Read(ReadOnlySpan<byte> content)
    {
        using var document = JsonDocument.Parse(content.ToArray());
        var root = document.RootElement;
        var users = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array ? value
            : throw new FormatException("Graph の応答に value 配列がありません。");

        var headers = new List<string>();
        var records = new List<Dictionary<string, string>>();
        foreach (var user in users.EnumerateArray())
        {
            if (user.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("value の要素がオブジェクトではありません。");
            }

            var record = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in user.EnumerateObject())
            {
                if (!headers.Contains(property.Name, StringComparer.Ordinal))
                {
                    headers.Add(property.Name);
                }

                record[property.Name] = Text(property.Value);
            }

            records.Add(record);
        }

        var rows = records.Select((r, i) => new RawRow(i + 1, [.. headers.Select(h => r.GetValueOrDefault(h) ?? string.Empty)])).ToList();
        return new RawTable(headers, rows);
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Array => value.EnumerateArray().Select(Text).FirstOrDefault(t => t.Length > 0) ?? string.Empty,
        _ => string.Empty,
    };
}
