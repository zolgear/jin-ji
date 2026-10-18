using System.Net;
using System.Net.Http.Headers;
using JinJi.Application.Auth;
using JinJi.Host.Web;

namespace JinJi.Host.Auth;

/// <summary>
/// CIMD（Client ID Metadata Document）の文書の取得。SSRF の対策として、外向きの通信を最小限に絞る。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>https だけ。プロキシ・Cookie・自動の展開は使わない</item>
/// <item>宛先のアドレスは DNS の解決の後に判定し、私設・ループバック・リンクローカル・CGNAT・マルチキャスト・文書用などの公開でないアドレスには接続しない
/// （判定したアドレスにそのまま接続するので、DNS の再束縛でもすり抜けない）。IP アドレスのホストも同じ</item>
/// <item>リダイレクトに従わない（3xx は失敗）。200 で JSON（<c>application/json</c>）だけ</item>
/// <item>大きさ（既定 5 KiB）・時間（既定 5 秒）の上限。HTTP キャッシュ（Cache-Control の max-age・no-store・no-cache、Expires）を読む</item>
/// </list>
/// </remarks>
internal sealed class CimdFetcher : IClientMetadataFetcher, IDisposable
{
    private readonly HttpClient _http;
    private readonly CimdOptions _options;
    private readonly TimeProvider _timeProvider;

    public CimdFetcher(AuthorizationServerOptions options, TimeProvider timeProvider)
        : this(options, timeProvider, CreateHandler(options.Cimd))
    {
    }

    /// <summary>テスト用: 送信の処理を差し替える（宛先の検査は <see cref="CreateHandler"/> の接続の処理が行う）。</summary>
    internal CimdFetcher(AuthorizationServerOptions options, TimeProvider timeProvider, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Cimd;
        _timeProvider = timeProvider;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = _options.MaxDocumentBytes };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("jin-ji-cimd/1");
    }

    public async Task<ClientMetadataFetch> FetchAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return ClientMetadataFetch.Failed("https 以外の URL です");
        }

        if (IPAddress.TryParse(url.IdnHost.Trim('[', ']'), out var literal) && !IsPublic(literal))
        {
            return ClientMetadataFetch.Failed("公開でないアドレスです");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.FetchTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                return ClientMetadataFetch.Failed("リダイレクトには従いません");
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return ClientMetadataFetch.Failed($"状態コード {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentType?.MediaType is not "application/json")
            {
                return ClientMetadataFetch.Failed("application/json ではありません");
            }

            if (response.Content.Headers.ContentLength > _options.MaxDocumentBytes)
            {
                return ClientMetadataFetch.Failed("文書が大きすぎます");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[_options.MaxDocumentBytes + 1];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token)) > 0)
            {
                length += read;
            }

            if (length > _options.MaxDocumentBytes)
            {
                return ClientMetadataFetch.Failed("文書が大きすぎます");
            }

            return new ClientMetadataFetch(System.Text.Encoding.UTF8.GetString(buffer, 0, length), CacheFor(response), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ClientMetadataFetch.Failed("時間の上限を超えました");
        }
        catch (HttpRequestException ex)
        {
            return ClientMetadataFetch.Failed(ex.InnerException is PublicAddressGuard.UnsafeAddressException ? "公開でないアドレスです" : "接続できません");
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>HTTP キャッシュから使い回しの時間を決める。指定が無ければ null。</summary>
    private TimeSpan? CacheFor(HttpResponseMessage response)
    {
        var cache = response.Headers.CacheControl;
        if (cache is { NoStore: true } or { NoCache: true })
        {
            return TimeSpan.Zero;
        }

        if (cache?.MaxAge is { } maxAge)
        {
            return maxAge < TimeSpan.Zero ? TimeSpan.Zero : maxAge;
        }

        if (response.Content.Headers.Expires is { } expires)
        {
            var now = response.Headers.Date ?? _timeProvider.GetUtcNow();
            return expires <= now ? TimeSpan.Zero : expires - now;
        }

        return null;
    }

    /// <summary>宛先を検査する送信の処理（DNS の解決の後のアドレスで判定し、そのアドレスに接続する。<see cref="PublicAddressGuard"/>）。</summary>
    internal static SocketsHttpHandler CreateHandler(CimdOptions options) => PublicAddressGuard.CreateHandler(options.FetchTimeout);

    /// <summary>公開のアドレスか（<see cref="PublicAddressGuard.IsPublic"/>）。</summary>
    public static bool IsPublic(IPAddress address) => PublicAddressGuard.IsPublic(address);
}
