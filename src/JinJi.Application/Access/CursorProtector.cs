using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JinJi.Policy;

namespace JinJi.Application.Access;

/// <summary>
/// カーソル（ページの続き・変更フィードの位置）の署名と検証。利用者が中身を書き換えたら検証に失敗する（HMAC-SHA256）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>署名には用途（例: <c>changes</c>、検索条件のハッシュを含む <c>people:...</c>）と閲覧者（クライアント・人・人事管理者か）を含める。
/// 別の閲覧者・別の検索条件のカーソルを流用できない</item>
/// <item>中身は暗号化しない（位置と条件のハッシュだけで、秘密は含まない）</item>
/// <item>REST API と MCP の共通（鍵は設定 <c>JinJi:Api:CursorKey</c> の 1 つ）。用途の文字列は経路ごとに分ける</item>
/// </list>
/// </remarks>
public sealed class CursorProtector
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[] _key;

    public CursorProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfLessThan(key.Length, 32);
        _key = [.. key];
    }

    /// <summary>署名付きのカーソルを作る。</summary>
    public string Protect<T>(string purpose, ViewerContext viewer, T payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        return Base64Url.EncodeToString(body) + "." + Base64Url.EncodeToString(Sign(purpose, viewer, body));
    }

    /// <summary>カーソルを検証して中身を取り出す。署名・形が不正なら false。</summary>
    public bool TryUnprotect<T>(string purpose, ViewerContext viewer, string cursor, out T? payload)
        where T : class
    {
        payload = null;
        if (string.IsNullOrEmpty(cursor) || cursor.Length > 2048 || cursor.Split('.') is not [var bodyPart, var signaturePart])
        {
            return false;
        }

        try
        {
            var body = Base64Url.DecodeFromChars(bodyPart);
            var signature = Base64Url.DecodeFromChars(signaturePart);
            if (!CryptographicOperations.FixedTimeEquals(signature, Sign(purpose, viewer, body)))
            {
                return false;
            }

            payload = JsonSerializer.Deserialize<T>(body, Json);
            return payload is not null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    /// <summary>検索条件などを用途に含めるためのハッシュ。</summary>
    public static string Hash(params string?[] parts)
    {
        var text = string.Join('\n', parts.Select(p => p ?? "\u0000"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private byte[] Sign(string purpose, ViewerContext viewer, byte[] body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentNullException.ThrowIfNull(viewer);
        var binding = string.Join(
            '\n',
            "jinji-cursor-v1",
            purpose,
            AccessCodes.ToCode(viewer.Actor),
            viewer.ClientId ?? string.Empty,
            viewer.EffectivePersonId?.ToString() ?? string.Empty,
            viewer.IsHrAdmin ? "hr_admin" : string.Empty);
        var header = Encoding.UTF8.GetBytes(binding + "\n");
        byte[] message = [.. header, .. body];
        return HMACSHA256.HashData(_key, message);
    }
}
