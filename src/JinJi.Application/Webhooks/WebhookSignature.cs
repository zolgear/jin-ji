using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JinJi.Application.Webhooks;

/// <summary>
/// Webhook の署名（HMAC-SHA256）。形は Standard Webhooks（https://www.standardwebhooks.com/）に合わせ、受け手が既存の検証の実装を使えるようにする。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>ヘッダー: <c>webhook-id</c>（イベント ID = 本文の id。冪等キー・nonce）、<c>webhook-timestamp</c>（送った時刻の Unix 秒。試行ごとに新しくする）、
/// <c>webhook-signature</c>（<c>v1,&lt;Base64 の署名&gt;</c> を空白で区切って並べる。鍵のローテーション中は新旧の鍵の 2 つ）</item>
/// <item>署名の対象: <c>{webhook-id}.{webhook-timestamp}.{本文}</c>（UTF-8）。時刻と本文に加えてイベント ID も含め、ヘッダーの付け替えを防ぐ</item>
/// <item>鍵: 32 バイトの乱数。受け手には <c>whsec_&lt;Base64&gt;</c> の形で作成・ローテーションのときに 1 回だけ渡す</item>
/// <item>受け手の検証: 時刻が今から ±5 分以内か、署名のどれかが一致するか（定数時間で比べる）、イベント ID を時刻の許容幅の間は覚えて重ねて処理しない（リプレイ・重複の防止）</item>
/// </list>
/// </remarks>
public static class WebhookSignature
{
    public const string IdHeader = "webhook-id";
    public const string TimestampHeader = "webhook-timestamp";
    public const string SignatureHeader = "webhook-signature";

    /// <summary>受け手に渡す鍵の接頭辞。</summary>
    public const string SecretPrefix = "whsec_";

    /// <summary>鍵の長さ（バイト）。</summary>
    public const int SecretBytes = 32;

    /// <summary>受け手が受け付ける時刻のずれの目安（文書と検証の例で使う）。</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>新しい鍵（32 バイトの乱数）。</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    /// <summary>受け手に渡す形（<c>whsec_</c> + Base64）。</summary>
    public static string Format(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return SecretPrefix + Convert.ToBase64String(secret);
    }

    /// <summary>受け手に渡した形から鍵を取り出す（検証の例・テストに使う）。</summary>
    public static byte[] Parse(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.FromBase64String(secret.StartsWith(SecretPrefix, StringComparison.Ordinal) ? secret[SecretPrefix.Length..] : secret);
    }

    /// <summary><c>webhook-signature</c> の値（鍵ごとに <c>v1,&lt;署名&gt;</c> を空白で区切る）。</summary>
    public static string Sign(IEnumerable<byte[]> secrets, string id, long timestamp, string body)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(body);
        var content = Encoding.UTF8.GetBytes(SignedContent(id, timestamp, body));
        var signatures = secrets.Select(secret => "v1," + Convert.ToBase64String(HMACSHA256.HashData(secret, content))).ToList();
        if (signatures.Count == 0)
        {
            throw new ArgumentException("署名の鍵がありません。", nameof(secrets));
        }

        return string.Join(' ', signatures);
    }

    /// <summary>
    /// 受け手の側の検証（文書の例と同じ手順。テストと、受け手の参考実装として使う）。時刻が許容幅の外・署名が一致しなければ false。
    /// イベント ID の重複の検査（リプレイ）は受け手が覚えておく ID の集合で行う（ここでは行わない）。
    /// </summary>
    public static bool Verify(byte[] secret, string? id, string? timestamp, string? signatureHeader, string body, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentNullException.ThrowIfNull(body);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signatureHeader)
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        var sent = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if ((now - sent).Duration() > (tolerance ?? Tolerance))
        {
            return false;
        }

        var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(SignedContent(id, seconds, body)));
        foreach (var part in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!part.StartsWith("v1,", StringComparison.Ordinal))
            {
                continue;
            }

            byte[] actual;
            try
            {
                actual = Convert.FromBase64String(part[3..]);
            }
            catch (FormatException)
            {
                continue;
            }

            if (CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static string SignedContent(string id, long timestamp, string body) =>
        string.Create(CultureInfo.InvariantCulture, $"{id}.{timestamp}.{body}");
}
