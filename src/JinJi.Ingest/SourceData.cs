using JinJi.Application.Ingest;

namespace JinJi.Ingest;

/// <summary>
/// 項目マッピングを通した取込元の 1 行。値は許可リストの取込項目（<see cref="IngestFields.Mappable"/>）だけで、捨てた列の値は持たない。
/// </summary>
/// <param name="SourceRowNumber">取込元の行番号（CSV はレコードの番号、Excel はシートの行番号。1 始まり）。</param>
/// <param name="Values">取込項目 → 値（前後の空白を除いた生の値。空のセルは含めない）。</param>
/// <param name="RowSha256">元の行のハッシュ（保持する列の名前と値だけから作る。捨てた列の値は含めない）。</param>
public sealed record MappedRow(int SourceRowNumber, IReadOnlyDictionary<string, string> Values, string RowSha256);

/// <summary>項目マッピングを通した取込元（コネクタの出力）。</summary>
/// <param name="DroppedColumns">捨てた列の名前・値のあった件数・理由。値は持たない。</param>
/// <param name="MissingFields">マッピングに書いたが、取込元にどの候補の列も無かった取込項目。</param>
public sealed record MappedTable(
    ImportSourceKind SourceKind,
    IReadOnlyList<MappedRow> Rows,
    IReadOnlyList<DroppedColumn> DroppedColumns,
    IReadOnlyList<string> MissingFields);
