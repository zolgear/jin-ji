using JinJi.Ingest;
using JinJi.Ingest.Normalization;

namespace JinJi.Connectors;

/// <summary>
/// 列が階層になっている組織図（会社 / 本部 / 部・室 / 課・グループ / チーム）を、1 行 1 組織の「組織名・上位組織名」の表にする。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>各行の組織は、値のある最も下の階層の列。それより上の列の値は上位組織への参照（同じ行に明示された上位組織）</item>
/// <item>上の列が空欄なら（結合セル由来）、上の行の値を補う。上の列に値がある行では、それより下の列の補いを捨てる。会社の列に値がある行では全部捨てる</item>
/// <item>上位組織は、同じ行に明示された最も近い上の列の値。無ければ補った値のうち最も近いもの。ただし <see cref="HierarchySpec.NoCarrySuffixes"/> で終わる組織（室など）は
/// 補いでは上位組織にしない（会社直下の室の次の行の部を、その室の配下と誤らないため）</item>
/// <item>結合セルの補いには本質的なあいまいさがある（課の次の行のチームが、その課の配下か、課を飛ばした部の直下か）。ここでは上の行の課の配下とみなす。
/// 段飛ばしのチームは、組織図だけからは判別できない。<see cref="HierarchySpec.SkipCandidateColumns"/> の列の組織では、補いを 1 段飛ばした上位組織を
/// <c>$parent_org_alt_name</c> に出し、取込で要確認（上位組織の候補。LLM の候補提示・人の確認）にする</item>
/// <item>出力の列は <c>$org_name</c>・<c>$parent_org_name</c>（<see cref="ColumnMapper"/> が取込項目に対応させる）。会社の列は補った値で置き換える</item>
/// </list>
/// </remarks>
public static class HierarchyFlattener
{
    public static RawTable Flatten(RawTable table, HierarchySpec spec)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(spec);

        var keys = table.Headers.Select(ConnectorMapping.HeaderKey).ToList();
        int Index(string header) => keys.IndexOf(ConnectorMapping.HeaderKey(header));
        var levels = spec.Columns.Select(Index).ToList();
        if (levels.Any(i => i < 0))
        {
            throw new FormatException("source.hierarchy.columns の列が取込元にありません: " + string.Join(", ", spec.Columns.Where(c => Index(c) < 0)));
        }

        var entity = spec.EntityColumn is null ? -1 : Index(spec.EntityColumn);
        var keep = Enumerable.Range(0, table.Headers.Count).Where(i => !levels.Contains(i)).ToList();
        var headers = keep.Select(i => table.Headers[i])
            .Concat([ColumnMapper.DerivedPrefix + IngestFields.OrgName, ColumnMapper.DerivedPrefix + IngestFields.ParentOrgName, ColumnMapper.DerivedPrefix + IngestFields.ParentOrgAltName])
            .ToList();
        var skipCandidates = (spec.SkipCandidateColumns ?? []).Select(Index).ToHashSet();
        if (skipCandidates.Contains(-1))
        {
            throw new FormatException("source.hierarchy.skipCandidateColumns の列が取込元にありません。");
        }


        var carried = new string?[levels.Count];
        string? carriedEntity = null;
        var rows = new List<RawRow>();
        foreach (var row in table.Rows)
        {
            string Cell(int column) => column >= 0 && column < row.Cells.Count ? row.Cells[column].Trim() : string.Empty;

            var cells = keep.Select(i => i < row.Cells.Count ? row.Cells[i] : string.Empty).ToList();
            if (entity >= 0)
            {
                if (Cell(entity).Length > 0)
                {
                    carriedEntity = Cell(entity);
                    Array.Clear(carried);
                }
                else if (carriedEntity is not null)
                {
                    cells[keep.IndexOf(entity)] = carriedEntity;
                }
            }

            var values = levels.Select(Cell).ToList();
            var deepest = values.FindLastIndex(v => v.Length > 0);
            if (deepest < 0)
            {
                rows.Add(new RawRow(row.SourceRowNumber, [.. cells, string.Empty, string.Empty, string.Empty]));
                continue;
            }

            string? parent = null;
            for (var c = deepest - 1; c >= 0 && parent is null; c--)
            {
                if (values[c].Length > 0)
                {
                    parent = values[c];
                }
            }

            string? alternative = null;
            for (var c = deepest - 1; c >= 0 && parent is null; c--)
            {
                if (carried[c] is { } value && !NoCarry(value))
                {
                    parent = value;

                    // 段飛ばしの候補: 補った上位組織を飛ばした、さらに上の補い（室などは候補にしない）
                    if (skipCandidates.Contains(levels[deepest]))
                    {
                        for (var u = c - 1; u >= 0; u--)
                        {
                            if (carried[u] is { } upper)
                            {
                                alternative = NoCarry(upper) ? null : upper;
                                break;
                            }
                        }
                    }
                }
                else if (carried[c] is not null)
                {
                    // 室など、補いでは上位組織にしない組織があれば、それより上もたどらない（会社直下とみなす）
                    break;
                }
            }

            for (var c = 0; c < levels.Count; c++)
            {
                if (values[c].Length > 0)
                {
                    carried[c] = values[c];
                    for (var d = c + 1; d < levels.Count; d++)
                    {
                        carried[d] = null;
                    }
                }
            }

            rows.Add(new RawRow(row.SourceRowNumber, [.. cells, values[deepest], parent ?? string.Empty, alternative ?? string.Empty]));
        }

        return new RawTable(headers, rows);

        bool NoCarry(string value)
        {
            var display = TextNormalizer.ToDisplay(value) ?? string.Empty;
            return spec.NoCarrySuffixes.Any(s => display.EndsWith(s, StringComparison.Ordinal));
        }
    }
}
