namespace JinJi.Connectors;

/// <summary>
/// jin-ji の配置図の形式 v1（JSON）を、要素ごとの行の表にする（0019）。読み方は <see cref="JinJi.Ingest.FloorLayoutDocument"/>（配置図の直接登録と共通）。
/// 見出しは取込項目の名前で、項目マッピングは同じ名前の列を対応させる（samples の floor-layouts マッピング）。
/// </summary>
public static class FloorLayoutJson
{
    /// <summary>形式の名前。</summary>
    public const string Format = JinJi.Ingest.FloorLayoutDocument.Format;

    /// <summary>読む。形式が違う・構造が不正なら <see cref="FormatException"/>。</summary>
    public static RawTable Read(ReadOnlySpan<byte> content)
    {
        var headers = JinJi.Ingest.FloorLayoutDocument.Headers;
        var rows = JinJi.Ingest.FloorLayoutDocument.Read(content);
        return new RawTable(headers, [.. rows.Select((row, i) => new RawRow(i + 1, [.. headers.Select(h => row.GetValueOrDefault(h) ?? string.Empty)]))]);
    }
}
