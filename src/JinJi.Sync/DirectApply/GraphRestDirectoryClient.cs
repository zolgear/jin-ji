using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;

namespace JinJi.Sync.DirectApply;

/// <summary>Graph の要求の失敗（状態コードと Graph のエラーコード。本文の値・トークンは含めない）。</summary>
public sealed class GraphRequestException : Exception
{
    public GraphRequestException()
    {
    }

    public GraphRequestException(string message)
        : base(message)
    {
    }

    public GraphRequestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GraphRequestException(int statusCode, string? errorCode, string message)
        : base($"{message}（HTTP {statusCode}{(errorCode is null ? string.Empty : ", " + errorCode)}）")
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public int StatusCode { get; }

    public string? ErrorCode { get; }
}

/// <summary>
/// ディレクトリへの書き込みの口（直接反映）。テストではモックに差し替える。実装は Graph REST（<see cref="GraphRestDirectoryClient"/>）。
/// </summary>
public interface IDirectoryWriteClient
{
    /// <summary>ユーザーの現在値（指定のプロパティ。値は計画と同じ文字列。真偽値は true / false）。</summary>
    Task<IReadOnlyDictionary<string, string?>> GetUserAsync(string objectId, IReadOnlyList<string> properties, CancellationToken cancellationToken);

    /// <summary>上長のオブジェクト ID（上長がいなければ null）。</summary>
    Task<string?> GetManagerIdAsync(string objectId, CancellationToken cancellationToken);

    /// <summary><c>PATCH /users/{id}</c>。</summary>
    Task UpdateUserAsync(string objectId, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken);

    /// <summary><c>PUT /users/{id}/manager/$ref</c>。</summary>
    Task SetManagerAsync(string objectId, string managerObjectId, CancellationToken cancellationToken);
}

/// <summary>
/// Graph REST の最小実装（HttpClient。Graph SDK は入れない）。<c>GET /users/{id}</c>・<c>GET /users/{id}/manager</c>・<c>PATCH /users/{id}</c>・
/// <c>PUT /users/{id}/manager/$ref</c> だけ。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>オブジェクト ID は UUID に限る（URL に入れる前に確かめる）。プロパティは同期の項目の許可リストだけ</item>
/// <item>送信先は設定の許可リストのホストだけ（<see cref="DirectApplyOptions.AllowedHosts"/>）</item>
/// <item>429・503 は Retry-After（上限あり）を待って設定の回数まで再送する</item>
/// </list>
/// </remarks>
public sealed class GraphRestDirectoryClient : IDirectoryWriteClient
{
    private static readonly HashSet<string> Properties = new(EntraFields.Attributes.Append(EntraFields.AccountEnabled), StringComparer.Ordinal);

    private readonly HttpClient _http;
    private readonly IGraphTokenSource _tokens;
    private readonly DirectApplyOptions _options;

    public GraphRestDirectoryClient(HttpClient http, IGraphTokenSource tokens, DirectApplyOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _tokens = tokens;
        _options = options;
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetUserAsync(string objectId, IReadOnlyList<string> properties, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var select = properties.Where(Properties.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (select.Count != properties.Count)
        {
            throw new ArgumentException("読めないプロパティが含まれています。", nameof(properties));
        }

        var uri = UserUri(objectId, "?$select=" + string.Join(',', select.Prepend("id")));
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in select)
        {
            values[property] = json.RootElement.TryGetProperty(property, out var value) ? Text(value) : null;
        }

        return values;
    }

    public async Task<string?> GetManagerIdAsync(string objectId, CancellationToken cancellationToken)
    {
        var uri = UserUri(objectId, "/manager?$select=id");
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.TryGetProperty("id", out var id) && Guid.TryParse(id.GetString(), CultureInfo.InvariantCulture, out var g) ? g.ToString("D") : null;
    }

    public async Task UpdateUserAsync(string objectId, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0 || changes.Keys.Any(k => !Properties.Contains(k)))
        {
            throw new ArgumentException("更新できないプロパティが含まれています。", nameof(changes));
        }

        var uri = UserUri(objectId, string.Empty);
        var body = JsonSerializer.Serialize(changes);
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task SetManagerAsync(string objectId, string managerObjectId, CancellationToken cancellationToken)
    {
        var uri = UserUri(objectId, "/manager/$ref");
        var managerUri = UserUri(managerObjectId, string.Empty);
        var body = JsonSerializer.Serialize(new Dictionary<string, string> { ["@odata.id"] = managerUri.ToString() });
        using var response = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    /// <summary>送信先が許可リストのホストか（無ければ何も送らない）。</summary>
    public static void RequireAllowed(DirectApplyOptions options, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps || !options.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"送信先 {uri.Host} は許可リスト（{DirectApplyOptions.SectionName}:AllowedHosts）にありません。");
        }
    }

    private Uri UserUri(string objectId, string suffix)
    {
        if (!Guid.TryParseExact(objectId, "D", out var id))
        {
            throw new ArgumentException("オブジェクト ID が UUID ではありません。", nameof(objectId));
        }

        var uri = new Uri(_options.GraphBaseUri, $"users/{id:D}{suffix}");
        RequireAllowed(_options, uri);
        return uri;
    }

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

    private static string? Text(JsonElement value)
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
