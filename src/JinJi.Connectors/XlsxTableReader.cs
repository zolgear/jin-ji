using System.Globalization;
using ClosedXML.Excel;

namespace JinJi.Connectors;

/// <summary>
/// Excel（xlsx）の読み込み（ClosedXML）。最初のシートか指定したシートの、使われている範囲を読む。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>値はセルの値（数式はキャッシュされた結果）を文字列にする: 日付は yyyy-MM-dd（時刻があれば yyyy-MM-ddTHH:mm:ss）、
/// 整数の数値は小数点なし（社員番号の 123.0 を 123 にする）、真偽値は TRUE / FALSE、エラーは空</item>
/// <item>結合セルの値の補完（<c>fillMergedCells</c>）: 結合範囲のすべてのセルに左上のセルの値を入れる。組織図の Excel で
/// 上位組織の列が縦に結合されている場合など。補完しない場合、左上以外のセルは空</item>
/// <item>マクロ付きのブック（xlsm）・旧形式（xls）は扱わない</item>
/// </list>
/// </remarks>
public static class XlsxTableReader
{
    public static RawTable Read(Stream stream, string? sheet = null, int headerRow = 1, bool fillMergedCells = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(headerRow, 1);

        using var workbook = new XLWorkbook(stream);
        IXLWorksheet worksheet;
        if (sheet is null)
        {
            worksheet = workbook.Worksheets.First();
        }
        else if (!workbook.TryGetWorksheet(sheet, out worksheet))
        {
            throw new FormatException($"シート {sheet} がありません。");
        }

        var used = worksheet.RangeUsed();
        if (used is null)
        {
            return new RawTable([], []);
        }

        var lastRow = used.LastRow().RowNumber();
        var firstColumn = 1;
        var lastColumn = used.LastColumn().ColumnNumber();
        if (lastRow < headerRow)
        {
            return new RawTable([], []);
        }

        string Cell(int row, int column)
        {
            var cell = worksheet.Cell(row, column);
            if (fillMergedCells && cell.IsMerged())
            {
                cell = cell.MergedRange().FirstCell();
            }

            return Text(cell.Value);
        }

        var headers = Enumerable.Range(firstColumn, lastColumn - firstColumn + 1).Select(c => Cell(headerRow, c).Trim()).ToList();
        var rows = new List<RawRow>();
        for (var r = headerRow + 1; r <= lastRow; r++)
        {
            var cells = Enumerable.Range(firstColumn, lastColumn - firstColumn + 1).Select(c => Cell(r, c)).ToList();
            if (cells.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            rows.Add(new RawRow(r, cells));
        }

        return new RawTable(headers, rows);
    }

    private static string Text(XLCellValue value)
    {
        if (value.IsBlank || value.IsError)
        {
            return string.Empty;
        }

        if (value.IsDateTime)
        {
            var dateTime = value.GetDateTime();
            return dateTime.TimeOfDay == TimeSpan.Zero
                ? dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : dateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (value.IsNumber)
        {
            var number = value.GetNumber();
            return number == Math.Floor(number) && Math.Abs(number) < 1e15
                ? ((long)number).ToString(CultureInfo.InvariantCulture)
                : number.ToString("R", CultureInfo.InvariantCulture);
        }

        if (value.IsBoolean)
        {
            return value.GetBoolean() ? "TRUE" : "FALSE";
        }

        if (value.IsTimeSpan)
        {
            return value.GetTimeSpan().ToString("c", CultureInfo.InvariantCulture);
        }

        return value.GetText();
    }
}
