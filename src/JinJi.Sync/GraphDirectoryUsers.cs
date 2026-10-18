using System.Globalization;
using System.Text.Json;
using JinJi.Application.Sync;

namespace JinJi.Sync;

/// <summary>
/// 同期計画の入力（Entra のユーザー一覧）の読み取り口。今は Microsoft Graph の <c>GET /users</c> 応答のファイル（<see cref="GraphUsersFile"/>）。
/// Graph への読み取りの接続（ページング・<c>$expand=manager</c>）は、同じ口の別の実装として足す（読み取り専用の資格情報。overview §8）。
/// </summary>
/// <remarks>
/// 取込のコネクタの <c>IDirectoryUserSource</c>（JinJi.Connectors）は表（RawTable）を返し、オブジェクトの値（<c>manager</c>）を捨てる。
/// 同期では上長の現在値・プロパティが入力にあったか（無ければ現在値は分からない）が要るので、型付きで読む別の口にした。
/// JinJi.Sync は Connectors（ClosedXML）を参照しない（ProjectReferenceTests）。
/// </remarks>
public interface IDirectoryUserSnapshot
{
    Task<IReadOnlyList<DirectoryUser>> ReadUsersAsync(CancellationToken cancellationToken);
}

/// <summary>Graph の <c>GET /users</c> 応答（<c>{"value": [...]}</c>）のファイル。</summary>
public sealed class GraphUsersFile(byte[] content) : IDirectoryUserSnapshot
{
    public Task<IReadOnlyList<DirectoryUser>> ReadUsersAsync(CancellationToken cancellationToken) =>
        Task.FromResult(GraphDirectoryUsers.Parse(content));
}

/// <summary>
/// Graph の <c>GET /users</c> 応答（JSON）を読む。読むのは同期の項目（<see cref="EntraFields"/>）と UPN・mail だけで、
/// 他のプロパティ（birthday・streetAddress など保持しない項目を含む）は読まずに捨てる（ADR-0002）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>上長は <c>$expand=manager($select=id)</c> の <c>manager</c> オブジェクトの <c>id</c>（null は上長なし）。プロパティが無ければ現在値は分からない</item>
/// <item>値は文字列にする（真偽値は true / false、配列は最初の空でない要素）。空文字は null と同じに扱う</item>
/// <item>オブジェクト ID は UUID に限る（スクリプト・直接反映の URL に入るので、形の不正な ID は読み込みで拒否する）</item>
/// <item><c>@odata.nextLink</c> があれば拒否する（一部だけの一覧で計画を作ると、無いユーザーを「アカウントなし」と誤るため）</item>
/// </list>
/// </remarks>
public static class GraphDirectoryUsers
{
    /// <summary>入力から読むプロパティ（同期の項目と、表示・作成の推奨に使う mail）。</summary>
    private static readonly HashSet<string> Read = new(StringComparer.Ordinal)
    {
        EntraFields.DisplayName, EntraFields.GivenName, EntraFields.Surname, EntraFields.Department, EntraFields.JobTitle, EntraFields.CompanyName,
        EntraFields.OfficeLocation, EntraFields.EmployeeId, EntraFields.AccountEnabled, EntraFields.UserPrincipalName, "mail",
    };

    public static IReadOnlyList<DirectoryUser> Parse(ReadOnlySpan<byte> content)
    {
        using var document = JsonDocument.Parse(content.ToArray());
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String)
        {
            throw new FormatException("Graph の応答に @odata.nextLink があります。すべてのページを結合した一覧を渡してください。");
        }

        var users = root.ValueKind == JsonValueKind.Array ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array ? value
            : throw new FormatException("Graph の応答に value 配列がありません。");

        var result = new List<DirectoryUser>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var user in users.EnumerateArray())
        {
            if (user.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"value の {index} 番目がオブジェクトではありません。");
            }

            if (!user.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String
                || !Guid.TryParse(idElement.GetString(), CultureInfo.InvariantCulture, out var objectId))
            {
                throw new FormatException($"value の {index} 番目の id が UUID ではありません。");
            }

            var id = objectId.ToString("D");
            if (!seen.Add(id))
            {
                throw new FormatException($"value の {index} 番目の id が重複しています。");
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var property in user.EnumerateObject())
            {
                if (Read.Contains(property.Name))
                {
                    values[property.Name] = Text(property.Value);
                }
                else if (property.Name == EntraFields.Manager)
                {
                    values[EntraFields.Manager] = ManagerId(property.Value, index);
                }
            }

            result.Add(new DirectoryUser(id, values, index));
            index++;
        }

        return result;
    }

    private static string? ManagerId(JsonElement manager, int index)
    {
        if (manager.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (manager.ValueKind == JsonValueKind.Object && manager.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
            && Guid.TryParse(id.GetString(), CultureInfo.InvariantCulture, out var managerId))
        {
            return managerId.ToString("D");
        }

        throw new FormatException($"value の {index} 番目の manager の id が UUID ではありません。");
    }

    private static string? Text(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => value.EnumerateArray().Select(Text).FirstOrDefault(t => t is not null),
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
