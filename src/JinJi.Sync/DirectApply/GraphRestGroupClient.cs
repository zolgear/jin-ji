using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;

namespace JinJi.Sync.DirectApply;

/// <summary>グループの直接反映と読み取りに使う Graph の操作（ADR-0015。テストではフェイクに差し替える）。</summary>
public interface IGroupDirectoryClient
{
    /// <summary>グループの今の状態（表示名・説明・種類・チームの有無）。無ければ null。メンバー・オーナーは読まない。</summary>
    Task<DirectoryGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>同じ mailNickname のグループ（作成の前に、既にあるかを確かめる）。</summary>
    Task<IReadOnlyList<DirectoryGroup>> FindByMailNicknameAsync(string mailNickname, CancellationToken cancellationToken);

    /// <summary>グループを作り、オブジェクト ID を返す。</summary>
    Task<string> CreateGroupAsync(string displayName, string mailNickname, string description, bool unified, CancellationToken cancellationToken);

    /// <summary>表示名・説明を更新する。</summary>
    Task UpdateGroupAsync(string groupId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken);

    /// <summary>ユーザーがグループのメンバーか。</summary>
    Task<bool> IsMemberAsync(string groupId, string userId, CancellationToken cancellationToken);

    Task AddMemberAsync(string groupId, string userId, CancellationToken cancellationToken);

    Task RemoveMemberAsync(string groupId, string userId, CancellationToken cancellationToken);

    /// <summary>グループのオーナーのオブジェクト ID。</summary>
    Task<IReadOnlyList<string>> ListOwnersAsync(string groupId, CancellationToken cancellationToken);

    Task AddOwnerAsync(string groupId, string userId, CancellationToken cancellationToken);

    /// <summary>Microsoft 365 グループに Teams のチームを作る。</summary>
    Task CreateTeamAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>
    /// 計画の入力にするグループの一覧。すべてのグループの名前と、jin-ji の印の付いたグループのメンバー・オーナーを読む
    /// （印の無いグループのメンバーは読まない。名前の衝突を見るのに要らないため）。
    /// </summary>
    Task<IReadOnlyList<DirectoryGroup>> ReadGroupsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// グループを扱う Graph REST の最小実装（HttpClient。Graph SDK は入れない。ADR-0010 決定 6）。送信先は許可リストのホストだけ。
/// </summary>
/// <remarks>
/// 使う操作: <c>GET /groups</c>・<c>GET /groups/{id}</c>・<c>POST /groups</c>・<c>PATCH /groups/{id}</c>・
/// <c>GET /groups/{id}/members</c>・<c>GET /groups/{id}/owners</c>・<c>POST /groups/{id}/members/$ref</c>・<c>DELETE /groups/{id}/members/{user}/$ref</c>・
/// <c>POST /groups/{id}/owners/$ref</c>・<c>POST /users/{id}/checkMemberGroups</c>・<c>PUT /groups/{id}/team</c>。
/// 実テナントでの検証は未実施（概要 §8 の未解決）。
/// </remarks>
public sealed class GraphRestGroupClient : IGroupDirectoryClient
{
    private const string GroupSelect = "id,displayName,description,mailNickname,groupTypes,resourceProvisioningOptions";

    private readonly HttpClient _http;
    private readonly IGraphTokenSource _tokens;
    private readonly DirectApplyOptions _options;

    public GraphRestGroupClient(HttpClient http, IGraphTokenSource tokens, DirectApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _tokens = tokens;
        _options = options;
    }

    public async Task<DirectoryGroup?> GetGroupAsync(string groupId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Uri($"groups/{Id(groupId)}?$select={GroupSelect}")), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return GraphDirectoryGroups.Parse(Encoding.UTF8.GetBytes("[" + await response.Content.ReadAsStringAsync(cancellationToken) + "]")).Single();
    }

    public async Task<IReadOnlyList<DirectoryGroup>> FindByMailNicknameAsync(string mailNickname, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mailNickname);
        var filter = System.Uri.EscapeDataString($"mailNickname eq '{mailNickname.Replace("'", "''", StringComparison.Ordinal)}'");
        return await ReadPagesAsync(Uri($"groups?$filter={filter}&$select={GroupSelect}"), cancellationToken);
    }

    public async Task<string> CreateGroupAsync(string displayName, string mailNickname, string description, bool unified, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["displayName"] = displayName,
            ["mailNickname"] = mailNickname,
            ["description"] = description,
            ["mailEnabled"] = unified,
            ["securityEnabled"] = true,
            ["groupTypes"] = unified ? ["Unified"] : Array.Empty<string>(),
        });
        using var response = await SendAsync(() => Json(HttpMethod.Post, Uri("groups"), body), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("D")
            : throw new GraphRequestException("Graph の応答に、作ったグループの id がありません。");
    }

    public async Task UpdateGroupAsync(string groupId, IReadOnlyDictionary<string, string?> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0 || changes.Keys.Any(k => k is not ("displayName" or "description")))
        {
            throw new ArgumentException("更新できるのは displayName と description だけです。", nameof(changes));
        }

        var body = JsonSerializer.Serialize(changes);
        using var response = await SendAsync(() => Json(HttpMethod.Patch, Uri($"groups/{Id(groupId)}"), body), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<bool> IsMemberAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new Dictionary<string, object> { ["groupIds"] = new[] { Id(groupId) } });
        using var response = await SendAsync(() => Json(HttpMethod.Post, Uri($"users/{Id(userId)}/checkMemberGroups"), body), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().Any(v => string.Equals(v.GetString(), Id(groupId), StringComparison.OrdinalIgnoreCase));
    }

    public async Task AddMemberAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Post, Uri($"groups/{Id(groupId)}/members/$ref"), Reference($"directoryObjects/{Id(userId)}")), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task RemoveMemberAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Uri($"groups/{Id(groupId)}/members/{Id(userId)}/$ref")), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListOwnersAsync(string groupId, CancellationToken cancellationToken) =>
        await ReadIdsAsync(Uri($"groups/{Id(groupId)}/owners?$select=id"), cancellationToken);

    public async Task AddOwnerAsync(string groupId, string userId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Post, Uri($"groups/{Id(groupId)}/owners/$ref"), Reference($"users/{Id(userId)}")), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task CreateTeamAsync(string groupId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => Json(HttpMethod.Put, Uri($"groups/{Id(groupId)}/team"), "{}"), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<DirectoryGroup>> ReadGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await ReadPagesAsync(Uri($"groups?$select={GroupSelect}&$top=999"), cancellationToken);
        var result = new List<DirectoryGroup>(groups.Count);
        foreach (var group in groups)
        {
            result.Add(OrgGroupSettings.ReadMarker(group.Description) is null
                ? group
                : group with
                {
                    MemberIds = await ReadIdsAsync(Uri($"groups/{group.ObjectId}/members?$select=id&$top=999"), cancellationToken),
                    OwnerIds = await ReadIdsAsync(Uri($"groups/{group.ObjectId}/owners?$select=id"), cancellationToken),
                });
        }

        return [.. result.Select((g, i) => g with { SourceIndex = i })];
    }

    private async Task<List<DirectoryGroup>> ReadPagesAsync(Uri first, CancellationToken cancellationToken)
    {
        var result = new List<DirectoryGroup>();
        await foreach (var page in PagesAsync(first, cancellationToken))
        {
            result.AddRange(GraphDirectoryGroups.Parse(Encoding.UTF8.GetBytes(page.GetRawText())));
        }

        return result;
    }

    private async Task<List<string>> ReadIdsAsync(Uri first, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await foreach (var page in PagesAsync(first, cancellationToken))
        {
            foreach (var entry in page.EnumerateArray())
            {
                if (entry.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), CultureInfo.InvariantCulture, out var parsed))
                {
                    result.Add(parsed.ToString("D"));
                }
            }
        }

        return result;
    }

    /// <summary>value 配列を、@odata.nextLink をたどってすべてのページ分返す（次のページの送信先も許可リストで確かめる）。</summary>
    private async IAsyncEnumerable<JsonElement> PagesAsync(Uri first, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var next = first;
        for (var page = 0; next is not null; page++)
        {
            if (page >= 1000)
            {
                throw new GraphRequestException("Graph のページが多すぎます（1,000 ページを超えました）。");
            }

            var uri = next;
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
            await EnsureSuccessAsync(response, cancellationToken);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
            {
                throw new GraphRequestException("Graph の応答に value 配列がありません。");
            }

            yield return value.Clone();
            next = null;
            if (json.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String)
            {
                next = new Uri(link.GetString()!, UriKind.Absolute);
                GraphRestDirectoryClient.RequireAllowed(_options, next);
            }
        }
    }

    private static string Id(string objectId) =>
        Guid.TryParseExact(objectId, "D", out var id) ? id.ToString("D") : throw new ArgumentException("オブジェクト ID が UUID ではありません。", nameof(objectId));

    private Uri Uri(string relative)
    {
        var uri = new Uri(_options.GraphBaseUri, relative);
        GraphRestDirectoryClient.RequireAllowed(_options, uri);
        return uri;
    }

    private string Reference(string relative) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["@odata.id"] = new Uri(_options.GraphBaseUri, relative).ToString() });

    private static HttpRequestMessage Json(HttpMethod method, Uri uri, string body) =>
        new(method, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _tokens.GetTokenAsync(cancellationToken));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            var response = await _http.SendAsync(request, timeout.Token);
            if (response.StatusCode is not (HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable) || attempt >= _options.MaxRetries)
            {
                return response;
            }

            var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            response.Dispose();
            await Task.Delay(delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay, cancellationToken);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            code = json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var c) ? c.GetString() : null;
        }
        catch (JsonException)
        {
            // 本文が JSON でなければコードなし
        }

        throw new GraphRequestException((int)response.StatusCode, code, "Graph の要求が失敗しました。");
    }
}
