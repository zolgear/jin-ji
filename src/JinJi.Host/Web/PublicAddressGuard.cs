using System.Net;
using System.Net.Sockets;
using JinJi.Application.Access;

namespace JinJi.Host.Web;

/// <summary>
/// 外向きの通信の宛先の検査（SSRF の対策）。CIMD の文書の取得（§6.5）と、修正依頼の通知の Webhook（§8）・社内システムへの Webhook（§9）が共有する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>宛先のアドレスは DNS の解決の後に判定し、私設・ループバック・リンクローカル・CGNAT・マルチキャスト・文書用などの公開でないアドレスには接続しない
/// （判定したアドレスにそのまま接続するので、DNS の再束縛でもすり抜けない）</item>
/// <item>プロキシ・Cookie・自動の展開・リダイレクトは使わない</item>
/// </list>
/// </remarks>
internal static class PublicAddressGuard
{
    /// <summary>宛先を検査する送信の処理（DNS の解決の後のアドレスで判定し、そのアドレスに接続する）。</summary>
    public static SocketsHttpHandler CreateHandler(TimeSpan connectTimeout) => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        MaxResponseHeadersLength = 16,
        ConnectTimeout = connectTimeout,
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = ConnectAsync,
    };

    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
        {
            // 1 つでも公開でないアドレスを返す名前は使わない（解決のたびに変わる名前で私設のアドレスを混ぜさせない）
            throw new UnsafeAddressException(context.DnsEndPoint.Host);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>公開のアドレスか（判定は <see cref="PublicAddresses.IsPublic"/>。Webhook の登録の検査と共有する）。</summary>
    public static bool IsPublic(IPAddress address) => PublicAddresses.IsPublic(address);

    /// <summary>公開でないアドレスへの接続を止めた。</summary>
    internal sealed class UnsafeAddressException(string host) : IOException($"公開でないアドレスに解決されました: {host}");
}
