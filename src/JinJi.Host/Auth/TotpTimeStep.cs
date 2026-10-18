using System.Globalization;
using System.Security.Cryptography;

namespace JinJi.Host.Auth;

/// <summary>
/// TOTP の符号がどの時間窓（30 秒）のものかを求める（符号の再利用の検知のため。RFC 6238 §5.2）。
/// </summary>
/// <remarks>
/// 符号の検証そのものは ASP.NET Core Identity（<c>AuthenticatorTokenProvider</c>。前後 2 つの時間窓を受け付ける）に任せ、ここは検証を通った符号の時間窓を
/// 特定するだけ（Identity は一致した時間窓を返さないため）。計算は RFC 6238 の既定（HMAC-SHA1・6 桁・30 秒）で、Identity と同じ。
/// </remarks>
internal static class TotpTimeStep
{
    private const int StepSeconds = 30;

    /// <summary>Identity が受け付ける範囲（前後 2 つ）より 1 つ広く探す（Identity は秒を丸め、こちらは切り捨てるため）。</summary>
    private const int Window = 3;

    /// <summary>符号が一致する時間窓（複数なら最も新しいもの）。一致しなければ null。</summary>
    public static long? Match(string base32Key, string code, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(base32Key);
        // Identity と同じく数値として比べる（先頭の 0 の有無を問わない）
        if (!int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) || Decode(base32Key) is not { Length: > 0 } key)
        {
            return null;
        }

        var current = now.ToUnixTimeSeconds() / StepSeconds;
        long? matched = null;
        for (var step = current - Window; step <= current + Window; step++)
        {
            if (Compute(key, step) == expected)
            {
                matched = step;
            }
        }

        return matched;
    }

    private static int Compute(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return binary % 1_000_000;
    }

    /// <summary>Base32（RFC 4648。大文字小文字・空白・パディングを問わない）。読めなければ null。</summary>
    private static byte[]? Decode(string input)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in input)
        {
            if (c is '=' || char.IsWhiteSpace(c))
            {
                continue;
            }

            var value = Alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
