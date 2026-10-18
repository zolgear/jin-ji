using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using JinJi.Application.Ingest;
using JinJi.Ingest;

namespace JinJi.Connectors;

/// <summary>
/// 項目マッピングで取込元の列を取込項目に対応させ、それ以外の列を捨てる（staging に保存する前。ADR-0002）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>保持しない項目を表す列名（<see cref="ForbiddenColumnNames"/>）は、マッピングに関わらず最初に捨てる（forbidden）</item>
/// <item>マッピングの候補の列は取込項目に対応させる。候補が複数の列に当たれば、行ごとに候補の順で値のある最初の列を使う（通称が空なら氏名、など）</item>
/// <item>discardWhenPresent の列に値がある行では、指定の項目を捨てる（旧姓使用者の戸籍名の読みなど、保持しない値を残さない）</item>
/// <item>無視すると指定した列は ignored、それ以外は unmapped として捨てる</item>
/// <item>捨てた列は、列名と値のあった件数だけを記録する（値は記録しない）。見出しも値も無い列は記録しない</item>
/// <item>元の行のハッシュは、保持する列の見出しと値だけから作る（捨てた列の値を含めると、取り得る値の少ない生年月日などを総当たりで復元できるため）</item>
/// </list>
/// </remarks>
public static class ColumnMapper
{
    /// <summary>この接頭辞で始まる見出しは、コネクタが作った列（階層の組織図の組織名など）。名前が取込項目ならその項目に対応させる。</summary>
    public const string DerivedPrefix = "$";

    public static MappedTable Map(RawTable table, ConnectorMapping mapping, ImportSourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(mapping);

        var headerKeys = table.Headers.Select(ConnectorMapping.HeaderKey).ToList();
        var forbidden = new HashSet<int>();
        for (var i = 0; i < table.Headers.Count; i++)
        {
            if (table.Headers[i].Length > 0 && !table.Headers[i].StartsWith(DerivedPrefix, StringComparison.Ordinal) && ForbiddenColumnNames.IsForbidden(table.Headers[i]))
            {
                forbidden.Add(i);
            }
        }

        // 取込項目 → 列（候補の順）。行ごとに、値のある最初の列を使う（例: 通称（旧姓使用）が空なら氏名）
        var assigned = new Dictionary<int, string>();
        var columnsOf = new List<(string Field, List<int> Columns)>();
        var missing = new List<string>();
        foreach (var (field, candidates) in mapping.Fields)
        {
            var columns = new List<int>();
            foreach (var key in candidates.Select(ConnectorMapping.HeaderKey))
            {
                var index = headerKeys.FindIndex(h => h.Length > 0 && h == key);
                if (index >= 0 && !forbidden.Contains(index) && !assigned.ContainsKey(index))
                {
                    assigned[index] = field;
                    columns.Add(index);
                }
            }

            if (columns.Count > 0)
            {
                columnsOf.Add((field, columns));
            }
            else
            {
                missing.Add(field);
            }
        }

        for (var i = 0; i < table.Headers.Count; i++)
        {
            var header = table.Headers[i];
            if (header.StartsWith(DerivedPrefix, StringComparison.Ordinal) && IngestFields.Mappable.Contains(header[1..]) && !assigned.ContainsKey(i))
            {
                assigned[i] = header[1..];
                columnsOf.Add((header[1..], [i]));
            }
        }

        var discard = mapping.DiscardWhenPresent
            .Select(d => (Column: headerKeys.IndexOf(ConnectorMapping.HeaderKey(d.Key)), Fields: d.Value))
            .Where(d => d.Column >= 0)
            .ToList();

        var ignored = mapping.Ignore.Select(ConnectorMapping.HeaderKey).ToHashSet(StringComparer.Ordinal);
        var dropped = new List<DroppedColumn>();
        var columnCount = Math.Max(table.Headers.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Cells.Count));
        for (var i = 0; i < columnCount; i++)
        {
            if (assigned.ContainsKey(i) || discard.Any(d => d.Column == i))
            {
                continue;
            }

            var header = i < table.Headers.Count ? table.Headers[i] : string.Empty;
            var count = table.Rows.Count(r => i < r.Cells.Count && !string.IsNullOrWhiteSpace(r.Cells[i]));
            if (header.Length == 0 && count == 0)
            {
                continue;
            }

            var dropKind = forbidden.Contains(i) ? DroppedColumnKind.Forbidden
                : ignored.Contains(headerKeys.ElementAtOrDefault(i) ?? string.Empty) && header.Length > 0 ? DroppedColumnKind.Ignored
                : DroppedColumnKind.Unmapped;
            dropped.Add(new DroppedColumn(header.Length > 0 ? header : $"(列 {(i + 1).ToString(CultureInfo.InvariantCulture)})", count, dropKind));
        }

        var rows = new List<MappedRow>();
        foreach (var row in table.Rows)
        {
            string Cell(int column) => column < row.Cells.Count ? row.Cells[column] : string.Empty;

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var hash = new StringBuilder();
            foreach (var (column, _) in assigned.OrderBy(a => a.Key))
            {
                hash.Append(table.Headers[column]).Append('\u001f').Append(Cell(column)).Append('\u001e');
            }

            foreach (var (field, columns) in columnsOf)
            {
                if (columns.Select(Cell).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)) is { } value)
                {
                    values[field] = value.Trim();
                }
            }

            foreach (var (column, fields) in discard)
            {
                if (!string.IsNullOrWhiteSpace(Cell(column)))
                {
                    foreach (var field in fields)
                    {
                        values.Remove(field);
                    }
                }
            }

            if (values.Count == 0)
            {
                continue;
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hash.ToString())));
            rows.Add(new MappedRow(row.SourceRowNumber, values, sha));
        }

        return new MappedTable(kind, rows, dropped, missing);
    }
}

/// <summary>取込元のファイルを読み、項目マッピングを通す（コネクタの入口）。</summary>
public static class SourceReader
{
    /// <summary>ファイルの内容を読み、項目マッピングを通した表にする。値は許可リストの取込項目だけになる。</summary>
    public static MappedTable Read(ReadOnlySpan<byte> content, string fileName, ConnectorMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(mapping);
        var kind = mapping.Source.Kind ?? KindOf(fileName);
        RawTable raw;
        if (kind == ImportSourceKind.Xlsx)
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            raw = XlsxTableReader.Read(stream, mapping.Source.Sheet, mapping.Source.HeaderRow, mapping.Source.FillMergedCells);
        }
        else if (kind == ImportSourceKind.GraphJson)
        {
            raw = GraphUsersJson.Read(content);
        }
        else
        {
            var (text, _) = TextEncodingDetector.Decode(content, mapping.Source.Encoding);
            var delimiter = mapping.Source.Delimiter ?? (Path.GetExtension(fileName).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',');
            raw = CsvTableReader.Read(text, delimiter, mapping.Source.HeaderRow);
        }

        if (mapping.Source.Hierarchy is { } hierarchy)
        {
            raw = HierarchyFlattener.Flatten(raw, hierarchy);
        }

        return ColumnMapper.Map(raw, mapping, kind);
    }

    /// <summary>拡張子から取込元の種類を決める。</summary>
    public static ImportSourceKind KindOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".xlsx" => ImportSourceKind.Xlsx,
        ".csv" or ".tsv" or ".txt" => ImportSourceKind.Csv,
        ".json" => ImportSourceKind.GraphJson,
        var other => throw new FormatException($"取込元の種類を拡張子から決められません: {other}（source.kind を指定してください）"),
    };
}
