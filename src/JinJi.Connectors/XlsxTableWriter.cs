using ClosedXML.Excel;

namespace JinJi.Connectors;

/// <summary>
/// 表を Excel（xlsx）に書き出す（ClosedXML。修正依頼のレポートの管理 CLI の出力）。ClosedXML は JinJi.Connectors（ワーカー側）だけが参照し、
/// API・MCP のプロセスには入れない（ADR-0003）ので、REST の xlsx は持たず CSV だけにする。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>値はすべて文字列のセルにする（数式・数値・日付として解釈させない。= で始まる値も数式にならない）</item>
/// <item>見出しの行は太字・固定、オートフィルターを付ける。列の幅は文字数から決める（フォントの計測は使わない）</item>
/// </list>
/// </remarks>
public static class XlsxTableWriter
{
    /// <summary>1 つのシートに見出しと行を書き、xlsx のバイト列を返す。</summary>
    public static byte[] Write(string sheetName, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sheetName);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(sheetName);
        var widths = headers.Select(h => Width(h)).ToArray();
        for (var c = 0; c < headers.Count; c++)
        {
            var cell = sheet.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            for (var c = 0; c < headers.Count && c < row.Count; c++)
            {
                var cell = sheet.Cell(r + 2, c + 1);
                cell.Value = row[c];
                cell.Style.NumberFormat.Format = "@";
                widths[c] = Math.Max(widths[c], Width(row[c]));
            }
        }

        for (var c = 0; c < headers.Count; c++)
        {
            sheet.Column(c + 1).Width = Math.Min(60, widths[c] + 2);
        }

        sheet.SheetView.FreezeRows(1);
        if (rows.Count > 0)
        {
            sheet.Range(1, 1, rows.Count + 1, headers.Count).SetAutoFilter();
        }

        using var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        return buffer.ToArray();
    }

    /// <summary>列の幅の目安（全角は 2、半角は 1 として数える）。</summary>
    private static int Width(string value) => value.Sum(ch => ch > 0xFF ? 2 : 1);
}
