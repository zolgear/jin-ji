namespace JinJi.Mcp;

/// <summary>
/// MCP サーバーの設定（appsettings の <c>JinJi:Mcp</c>）。<see cref="Enabled"/> は停止スイッチで、設定の再読み込み（appsettings.json の変更）でも効く。
/// </summary>
public sealed class McpOptions
{
    /// <summary>設定の節の名前。</summary>
    public const string SectionName = "JinJi:Mcp";

    /// <summary>
    /// 停止スイッチ（アーキテクチャ概要 §2）。false にすると、MCP のポートの要求（<c>/healthz</c> を除く）をすべて 503 にし、ツールを 1 つも動かさない。
    /// REST API・UI には影響しない。既定は true。
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>MCP の要求の本文の上限（バイト。既定 64 KiB）。ツールの引数は小さいので、大きな本文は読まずに 413 にする。</summary>
    public int MaxRequestBodyBytes { get; set; } = 64 * 1024;

    /// <summary>値を検査する。不正なら起動させない。</summary>
    public void Validate()
    {
        if (MaxRequestBodyBytes is < 1024 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxRequestBodyBytes), MaxRequestBodyBytes, "JinJi:Mcp:MaxRequestBodyBytes は 1024〜1048576 で指定してください。");
        }
    }
}
