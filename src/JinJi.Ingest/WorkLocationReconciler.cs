using System.Security.Cryptography;
using System.Text;
using JinJi.Application;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Ingest;

/// <summary>
/// 取込済みの自由記述の勤務地（0017 で廃止）を拠点マスタに照合し、人ごとの拠点の変更提案と、拠点に当たらない値の修正依頼を作る（勤務地から拠点への移行）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>前提: 拠点マスタ（対象 sites の取込）と組織の拠点（組織図の取込）を先に承認しておく。人ごとの拠点は「主務の組織の拠点と違う人だけ」持つので、
/// 組織の拠点が無いまま照合すると全員が人ごとの拠点になる</item>
/// <item>今日有効な版の勤務地を、社員番号・法人・拠点名（<see cref="IngestFields.SiteName"/>）の 3 列の表にして、人の取込（<see cref="ImportService"/>）に
/// そのまま渡す。照合（拠点コード・拠点名の正規化表記・「本社（東京）」の並べ替え）・差分・修正依頼（unknown_site）・監査・職務分掌は人の取込と同じで、
/// 公開側は変えない（実行者とは別の人事管理者が提案を承認したときだけ反映する）</item>
/// <item>取込単位の取込元は <see cref="ImportSourceKind.WorkLocation"/>、ファイル名は <see cref="FileName"/>。同じ照合を繰り返すと、前の照合の未対応の修正依頼は置き換えになる（0015）</item>
/// <item>人事パッケージの「勤務地」の列を拠点名（site_name）に対応させて取り込み直しても同じ結果になる。照合は取り込み直す前の移行のためのもの</item>
/// </list>
/// </remarks>
public sealed class WorkLocationReconciler
{
    public const string FileName = "person_version.work_location";
    public const string MappingName = "jinji-work-location-reconciliation";

    private readonly IWorkLocationSource _source;
    private readonly ImportService _importer;
    private readonly TimeProvider _timeProvider;

    public WorkLocationReconciler(IWorkLocationSource source, ImportService importer, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _source = source;
        _importer = importer;
        _timeProvider = timeProvider;
    }

    /// <summary>照合して作業領域に提案・修正依頼を作る。勤務地の値が 1 つも無ければ null（取込単位を作らない）。</summary>
    /// <param name="executedByPersonId">実行者（提案の起案者。閲覧者が jin-ji 自身のときは必須）。</param>
    public async Task<ImportResult?> RunAsync(ViewerContext viewer, Guid? executedByPersonId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (!viewer.IsHrAdmin)
        {
            throw new AccessDeniedException("勤務地の照合は人事管理者（またはその委任を受けたエージェント・jin-ji 自身）だけが実行できます。");
        }

        var locations = await _source.ListAsync(viewer, BusinessDate.Today(_timeProvider), cancellationToken);
        if (locations.Count == 0)
        {
            return null;
        }

        var rows = locations
            .Select((l, i) => new MappedRow(
                i + 1,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [IngestFields.EmployeeNumber] = l.EmployeeNumber,
                    [IngestFields.LegalEntityCode] = l.LegalEntityCode,
                    [IngestFields.SiteName] = l.WorkLocation,
                },
                Sha256($"{l.LegalEntityCode}\n{l.EmployeeNumber}\n{l.WorkLocation}")))
            .ToList();
        return await _importer.ImportAsync(
            viewer,
            new ImportRequest
            {
                Table = new MappedTable(ImportSourceKind.WorkLocation, rows, [], []),
                Profile = new ImportProfile { Target = ImportTargetKind.People },
                FileName = FileName,
                FileSha256 = Sha256(string.Join('\n', rows.Select(r => r.RowSha256))),
                MappingName = MappingName,
                MappingSha256 = Sha256(MappingName),
                ExecutedByPersonId = executedByPersonId,
            },
            cancellationToken);
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
