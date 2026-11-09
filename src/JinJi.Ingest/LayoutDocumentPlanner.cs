using JinJi.Application.FieldManagement;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>
/// 配置図の直接登録（ADR-0013 決定 12。0026）: 配置図の JSON を、取込と同じ正規化・検査・計画（<see cref="ProposalPlanner.PlanFloorLayouts"/>）で変更提案にする。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>作業領域（staging・修正依頼）には保存しない。取込なら修正依頼になる行は、登録できない理由として返す（1 件でもあれば登録しない）</item>
/// <item>切り替えの日は登録の有効日にそろえる（JSON の layout.valid_from は使わない）</item>
/// <item>自由記述の項目（建物名・ゾーンの名前など）に保持しない情報らしき値があれば、取込と同じく受け付けない</item>
/// <item>提案の ID は反映の順（建物 → 階 → 改訂。計画の順）に振り直す（保存した提案は ID の順に読んで反映する）</item>
/// </list>
/// </remarks>
public sealed class LayoutDocumentPlanner : ILayoutDocumentPlanner
{
    /// <summary>1 回の登録の要素（外形・固定の部分・ゾーン・席）の上限。</summary>
    public const int MaxElements = 20_000;

    public LayoutDocumentPlan Plan(OrgSnapshot snapshot, ReadOnlyMemory<byte> document, DateOnly effectiveDate, DateOnly today, Guid registrant)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rows = FloorLayoutDocument.Read(document.Span);
        if (rows.Count == 0)
        {
            throw new FormatException("配置図の JSON に建物・階がありません。");
        }

        if (rows.Count > MaxElements)
        {
            throw new FormatException($"1 回に登録できる要素（外形・固定の部分・ゾーン・席）は {MaxElements} 個までです。");
        }

        var profile = new ImportProfile { Target = ImportTargetKind.FloorLayouts };
        var normalizer = new RowNormalizer(profile);
        var problems = new List<string>();
        var labels = new Dictionary<int, string>();
        var normalized = new List<NormalizedRow>(rows.Count);
        foreach (var (row, index) in rows.Select((r, i) => (r, i + 1)))
        {
            labels[index] = Label(row);
            var values = row
                .Where(kv => kv.Value is not null && kv.Key != IngestFields.LayoutValidFrom)
                .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.Ordinal);
            foreach (var field in IngestFields.FreeText.Where(values.ContainsKey).Order(StringComparer.Ordinal))
            {
                var kinds = SensitiveValueDetector.Detect(values[field]);
                if (kinds.Count > 0)
                {
                    problems.Add($"{labels[index]}: {field} に保持しない情報（{string.Join("・", kinds.Select(SensitiveValueDetector.Label))}）らしき値があります");
                }
            }

            normalized.Add(normalizer.Normalize(EntityId.New(), new MappedRow(index, values, string.Empty)));
        }

        var context = new PlanContext(Guid.Empty, registrant, null, snapshot, null, effectiveDate, today, normalizer.EmployeeNumbers) { Profile = profile };
        var result = ProposalPlanner.PlanFloorLayouts(context, normalized);
        problems.AddRange(result.Corrections.Select(c =>
            (c.SourceRowNumber is { } n && labels.TryGetValue(n, out var label) ? label + ": " : string.Empty) + c.Message));

        // 提案の ID を反映の順に振り直す（同じミリ秒の UUIDv7 は順に並ばないので、1 件ごとに時刻を進める）。依存の ID も付け替える
        var start = DateTimeOffset.UtcNow;
        var ids = result.Proposals.Select((p, i) => (p.Id, New: Guid.CreateVersion7(start.AddMilliseconds(i)))).ToDictionary(x => x.Id, x => x.New);
        var proposals = result.Proposals
            .Select(p => p with
            {
                Id = ids[p.Id],
                StagingRowIds = [],
                Basis = p.Basis with { DependsOn = [.. p.Basis.DependsOn.Select(d => ids.GetValueOrDefault(d, d))], RowNumbers = [] },
            })
            .ToList();
        return new LayoutDocumentPlan(proposals, problems);
    }

    /// <summary>行（要素）の見出し: 建物コード・階・要素（席は席番号、ゾーン・固定の部分は名前）。</summary>
    private static string Label(IReadOnlyDictionary<string, string?> row)
    {
        var parts = new List<string?>
        {
            row.GetValueOrDefault(IngestFields.BuildingCode),
            row.GetValueOrDefault(IngestFields.FloorLabel),
            row.GetValueOrDefault(IngestFields.LayoutElement) switch
            {
                "outline" => "外形",
                "fixture" => "固定の部分 " + row.GetValueOrDefault(IngestFields.LayoutName),
                "zone" => "ゾーン " + row.GetValueOrDefault(IngestFields.LayoutName),
                "seat" => "席 " + row.GetValueOrDefault(IngestFields.SeatCode),
                _ => null,
            },
        };
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
    }
}
