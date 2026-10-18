namespace JinJi.Api;

/// <summary>REST API の設定（appsettings の <c>JinJi:Api</c>）。</summary>
public sealed class ApiOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Api";

    /// <summary>
    /// カーソルの署名鍵（Base64、32 バイト以上）。複数のプロセス・再起動をまたいでカーソル（特に変更フィードの位置）を使い続けるために設定する。
    /// 未設定なら起動ごとに乱数で作る（再起動の後は古いカーソルが 400 になり、受け手は一覧から取り直す）。
    /// </summary>
    public string? CursorKey { get; init; }

    /// <summary>鍵を取り出す。未設定なら null。不正なら例外。</summary>
    public byte[]? CursorKeyBytes()
    {
        if (string.IsNullOrWhiteSpace(CursorKey))
        {
            return null;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(CursorKey);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("JinJi:Api:CursorKey は Base64 で指定してください。", nameof(CursorKey), ex);
        }

        return key.Length >= 32 ? key : throw new ArgumentException("JinJi:Api:CursorKey は 32 バイト以上にしてください。", nameof(CursorKey));
    }
}
