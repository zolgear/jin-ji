using System.Net;
using System.Net.Sockets;

namespace JinJi.Application.Access;

/// <summary>
/// 公開のアドレスの判定（SSRF の対策）。外向きの通信の宛先の検査（Host の PublicAddressGuard。DNS の解決の後のアドレスで判定する）と、
/// Webhook の送信先の登録の検査（IP アドレスのホスト）が共有する。
/// </summary>
public static class PublicAddresses
{
    /// <summary>
    /// 公開のアドレスか（私設・ループバック・リンクローカル・CGNAT・ベンチマーク・文書用・マルチキャスト・予約・IPv6 の ULA などは false）。
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return IsPublic(address.MapToIPv4());
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224
                     || (b[0] == 100 && b[1] is >= 64 and <= 127)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] is >= 16 and <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                     || (b[0] == 192 && b[1] == 88 && b[2] == 99)
                     || (b[0] == 198 && b[1] is 18 or 19)
                     || (b[0] == 198 && b[1] == 51 && b[2] == 100)
                     || (b[0] == 203 && b[1] == 0 && b[2] == 113));
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast || address.IsIPv6UniqueLocal || address.IsIPv6Teredo)
        {
            return false;
        }

        // 6to4（2002::/16）・NAT64（64:ff9b::/96）は中の IPv4 で判定し、文書用（2001:db8::/32）は拒否する
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
        {
            return IsPublic(new IPAddress(bytes[2..6]));
        }

        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b && bytes[4..12].All(x => x == 0))
        {
            return IsPublic(new IPAddress(bytes[12..16]));
        }

        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
        {
            return false;
        }

        // IPv4 互換（::/96）は使わない。グローバルユニキャスト（2000::/3）だけを許す
        return (bytes[0] & 0xe0) == 0x20;
    }

}
