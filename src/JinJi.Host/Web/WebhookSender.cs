using System.Net;
using System.Text;
using JinJi.Application.Access;
using JinJi.Application.Webhooks;

namespace JinJi.Host.Web;

/// <summary>
/// 社内システムへの Webhook の送信。外向きの通信を最小限に絞る（CIMD の取得・修正依頼の通知と同じ守り。§6.5・§8）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>https だけ。宛先のアドレスは DNS の解決の後に判定し、公開でないアドレス（私設・ループバック・リンクローカルなど）には接続しない
/// （<see cref="PublicAddressGuard"/>。判定したアドレスにそのまま接続するので DNS の再束縛でもすり抜けない）。IP アドレスのホストも同じ</item>
/// <item>リダイレクトに従わない（3xx は失敗）。プロキシ・Cookie・自動の展開は使わない。2xx だけを成功とし、応答の本文は読まない</item>
/// <item>時間の上限は設定（既定 10 秒）。誤りの文には状態コードと理由だけを出し、URL・本文・応答の本文は出さない</item>
/// </list>
/// </remarks>
internal sealed class WebhookSender : IWebhookSender, IDisposable
{
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public WebhookSender(WebhookOptions options)
        : this(options, PublicAddressGuard.CreateHandler(options.Timeout))
    {
    }

    /// <summary>テスト用: 送信の処理を差し替える（テストの受け手はループバックで待ち受けるため）。</summary>
    internal WebhookSender(WebhookOptions options, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        _timeout = options.Timeout;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 64 * 1024 };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("jin-ji-webhook/1");
    }

    public async Task<WebhookSendResult> SendAsync(Uri url, string body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(headers);
        if (url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
        {
            return new WebhookSendResult(false, null, "https 以外の URL です");
        }

        if (IPAddress.TryParse(url.IdnHost.Trim('[', ']'), out var literal) && !PublicAddresses.IsPublic(literal))
        {
            return new WebhookSendResult(false, null, "公開でないアドレスです");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            foreach (var (name, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            return status switch
            {
                >= 200 and < 300 => new WebhookSendResult(true, status, null),
                >= 300 and < 400 => new WebhookSendResult(false, status, "リダイレクトには従いません"),
                _ => new WebhookSendResult(false, status, $"状態コード {status}"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebhookSendResult(false, null, "時間の上限を超えました");
        }
        catch (HttpRequestException ex)
        {
            return new WebhookSendResult(false, null, ex.InnerException is PublicAddressGuard.UnsafeAddressException ? "公開でないアドレスです" : "接続できません");
        }
    }

    public void Dispose() => _http.Dispose();
}
