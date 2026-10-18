using System.Net;
using System.Text;
using JinJi.Application.Corrections;

namespace JinJi.Host.Web;

/// <summary>
/// 修正依頼の通知の Webhook の送信（Teams のワークフロー・Slack の Incoming Webhook）。外向きの通信を最小限に絞る（CIMD の取得と同じ守り。§6.5）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>https だけで、ホストは設定の許可リスト（<see cref="CorrectionWebhookOptions.AllowedHosts"/>）にあるものだけ（起動時の検査に加えて送信の直前にも確かめる）</item>
/// <item>宛先のアドレスは DNS の解決の後に判定し、公開でないアドレスには接続しない（<see cref="PublicAddressGuard"/>）。IP アドレスのホストも同じ</item>
/// <item>リダイレクトに従わない（3xx は失敗）。2xx だけを成功とし、応答の本文は読まない。時間の上限は設定（既定 10 秒）</item>
/// <item>URL は秘密を含む（知っていれば誰でも投稿できる）ので、誤りの文・ログにはホストと状態コードだけを出す</item>
/// </list>
/// </remarks>
internal sealed class CorrectionWebhookSender : ICorrectionWebhookSender, IDisposable
{
    private readonly HttpClient _http;
    private readonly CorrectionWebhookOptions _options;

    public CorrectionWebhookSender(CorrectionOptions options)
        : this(options, PublicAddressGuard.CreateHandler(options.Notice.Webhook.Timeout))
    {
    }

    /// <summary>テスト用: 送信の処理を差し替える（宛先の検査は <see cref="PublicAddressGuard.CreateHandler"/> の接続の処理が行う）。</summary>
    internal CorrectionWebhookSender(CorrectionOptions options, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Notice.Webhook;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 64 * 1024 };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("jin-ji-notice/1");
    }

    public async Task<CorrectionWebhookResult> SendAsync(Uri url, string json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(json);
        if (url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo))
        {
            return new CorrectionWebhookResult(false, null, "https 以外の URL です");
        }

        var allowed = _options.AllowedHosts.Select(h => h.Trim().ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (!allowed.Contains(url.IdnHost.ToLowerInvariant()))
        {
            return new CorrectionWebhookResult(false, null, "許可リストに無いホストです");
        }

        if (IPAddress.TryParse(url.IdnHost.Trim('[', ']'), out var literal) && !PublicAddressGuard.IsPublic(literal))
        {
            return new CorrectionWebhookResult(false, null, "公開でないアドレスです");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            return status switch
            {
                >= 200 and < 300 => new CorrectionWebhookResult(true, status, null),
                >= 300 and < 400 => new CorrectionWebhookResult(false, status, "リダイレクトには従いません"),
                _ => new CorrectionWebhookResult(false, status, $"状態コード {status}"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CorrectionWebhookResult(false, null, "時間の上限を超えました");
        }
        catch (HttpRequestException ex)
        {
            return new CorrectionWebhookResult(false, null, ex.InnerException is PublicAddressGuard.UnsafeAddressException ? "公開でないアドレスです" : "接続できません");
        }
    }

    public void Dispose() => _http.Dispose();
}
