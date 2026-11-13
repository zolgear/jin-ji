using System.Globalization;
using System.Text.Json;
using JinJi.Application.Sync;

namespace JinJi.Sync;

/// <summary>
/// Entra ID のグループの一覧（Graph の <c>GET /groups</c> の JSON）の解釈。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>形は <c>{ "value": [ { "id", "displayName", "description", "mailNickname", "groupTypes", "resourceProvisioningOptions", "members": [ { "id" } ], "owners": [ { "id" } ] } ] }</c>。配列だけでもよい</item>
/// <item><c>members</c>・<c>owners</c> は、そのグループの全員を入れる（Graph の <c>$expand</c> は 20 件までなので、<c>/groups/{id}/members</c> をすべてのページ読んで入れる）。
/// キーが無ければ「分からない」として扱い、外す項目は作らない</item>
/// <item>読むのはここに挙げた項目だけ。ほかは捨てる</item>
/// </list>
/// </remarks>
public static class GraphDirectoryGroups
{
    public static IReadOnlyList<DirectoryGroup> Parse(ReadOnlySpan<byte> content)
    {
        using var document = JsonDocument.Parse(content.ToArray());
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String)
        {
            throw new FormatException("Graph の応答に @odata.nextLink があります。すべてのページを結合した一覧を渡してください。");
        }

        var groups = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array ? value
            : throw new FormatException("Graph の応答に value 配列がありません。");

        var result = new List<DirectoryGroup>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"value の {index} 番目がオブジェクトではありません。");
            }

            var id = ObjectId(group, $"value の {index} 番目の id");
            if (!seen.Add(id))
            {
                throw new FormatException($"value の {index} 番目の id が重複しています。");
            }

            result.Add(new DirectoryGroup(
                id,
                Text(group, "displayName"),
                Text(group, "description"),
                Text(group, "mailNickname"),
                Strings(group, "groupTypes").Contains("Unified", StringComparer.OrdinalIgnoreCase),
                group.TryGetProperty("resourceProvisioningOptions", out var options) && options.ValueKind == JsonValueKind.Array
                    ? Strings(group, "resourceProvisioningOptions").Contains("Team", StringComparer.OrdinalIgnoreCase)
                    : null,
                Ids(group, "members", index),
                Ids(group, "owners", index),
                index));
            index++;
        }

        return result;
    }

    private static string ObjectId(JsonElement element, string what) =>
        element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("D")
            : throw new FormatException($"{what} が UUID ではありません。");

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];

    private static List<string>? Ids(JsonElement group, string name, int index)
    {
        if (!group.TryGetProperty(name, out var list) || list.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"value の {index} 番目の {name} が配列ではありません。");
        }

        return [.. list.EnumerateArray().Select(m => ObjectId(m, $"value の {index} 番目の {name} の id")).Distinct(StringComparer.Ordinal)];
    }
}
