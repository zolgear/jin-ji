using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Net.Http.Headers;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// テスト用のブラウザ（Cookie を名前で持ち、転送は自分でたどる）。jin-ji の Cookie は Secure だが、テストは http で接続するので
/// HttpClient の Cookie コンテナに任せず、Set-Cookie を自分で読んで送る。
/// </summary>
internal sealed class TestBrowser : IDisposable
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public TestBrowser(int port)
    {
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
        };
    }

    /// <summary>今持っている Cookie。</summary>
    public IReadOnlyDictionary<string, string> Cookies => _cookies;

    /// <summary>最後の応答の Set-Cookie（生の値）。</summary>
    public IReadOnlyList<string> LastSetCookies { get; private set; } = [];

    /// <summary>付ける Origin ヘッダー（パスキーの検証で使う）。</summary>
    public string? Origin { get; set; }

    public string? CsrfToken { get; set; }

    public void SetCookie(string name, string value) => _cookies[name] = value;

    public void RemoveCookie(string name) => _cookies.Remove(name);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? bearer = null, bool csrf = true)
    {
        using var request = new HttpRequestMessage(method, url);
        if (_cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (Origin is not null)
        {
            request.Headers.Add("Origin", Origin);
        }

        if (csrf && method != HttpMethod.Get && CsrfToken is not null)
        {
            request.Headers.Add("X-JinJi-CSRF", CsrfToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        Absorb(response);
        return response;
    }

    public Task<HttpResponseMessage> GetAsync(string url, string? bearer = null) => SendAsync(HttpMethod.Get, url, bearer: bearer);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, bool csrf = true) => SendAsync(HttpMethod.Post, url, body ?? new { }, csrf: csrf);

    /// <summary>JSON の応答を読む。</summary>
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(string.IsNullOrEmpty(text) ? "null" : text);
        return document.RootElement.Clone();
    }

    /// <summary>CSRF の要求トークンを取る（以後の POST に付ける）。</summary>
    public async Task<string> RefreshCsrfAsync()
    {
        using var response = await GetAsync("/auth/csrf");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        CsrfToken = (await JsonAsync(response)).GetProperty("token").GetString();
        return CsrfToken!;
    }

    public void Dispose() => _http.Dispose();

    private void Absorb(HttpResponseMessage response)
    {
        LastSetCookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];
        foreach (var header in LastSetCookies)
        {
            var cookie = SetCookieHeaderValue.Parse(header);
            var name = cookie.Name.ToString();
            var expired = cookie.Expires is { } expires && expires < DateTimeOffset.UtcNow || cookie.MaxAge is { } age && age <= TimeSpan.Zero;
            if (expired || cookie.Value.Length == 0)
            {
                _cookies.Remove(name);
            }
            else
            {
                _cookies[name] = cookie.Value.ToString();
            }
        }
    }
}
