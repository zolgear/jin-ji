using JinJi.Application.People;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Organization;

/// <summary>
/// UI・社内システムが画面や問い合わせの前提に使うメタ情報（業務日付・段の定義・時点の目盛り）。人の情報は含まない。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>業務日付は日本時間の日付（<see cref="BusinessDate"/>）。端末の時計に頼らずに「今日」と「未来の時点」の境を決めるため</item>
/// <item>段の定義は法人ごとに、法人に効く定義（法人の定義、無ければグループ共通の既定）を返す。組織の情報と同じく全社公開</item>
/// <item>時点の目盛り（組織改編・一斉異動の日）は、人事管理者以外には業務日付以前だけを返す（組織改編・一斉異動の予定は内示と同じく秘匿する。
/// 先行提供の社内システムには、その上限まで）。
/// RLS で将来の行が見えないうえに、見えている行の終了日も上限で切る（ストアとこのサービスの両方）</item>
/// </list>
/// </remarks>
public sealed class DirectoryMetaService
{
    /// <summary>業務日付の時間帯（表示用）。</summary>
    public const string TimeZone = "Asia/Tokyo";

    private readonly IOrgSnapshotProvider _snapshots;
    private readonly IDirectoryCalendarStore _calendar;
    private readonly TimeProvider _timeProvider;
    private readonly PersonDirectoryOptions _options;

    public DirectoryMetaService(
        IOrgSnapshotProvider snapshots,
        IDirectoryCalendarStore calendar,
        TimeProvider timeProvider,
        PersonDirectoryOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options ?? new PersonDirectoryOptions();
        _options.Validate();
        _snapshots = snapshots;
        _calendar = calendar;
        _timeProvider = timeProvider;
    }

    public async Task<DirectoryMeta> GetAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var today = BusinessDate.Today(_timeProvider);
        var data = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
        // 先行提供の社内システムには、上限（業務日付 + 日数）までの目盛りを返す（RLS もその日まで見せる）
        var until = viewer.IsHrAdmin ? (DateOnly?)null : viewer.DisclosureHorizon(today);
        var milestones = await _calendar.GetMilestonesAsync(viewer, until, _options.MassActionThreshold, cancellationToken);

        var defaultScheme = data.LevelSchemes.FirstOrDefault(s => s.LegalEntityId is null);
        var entities = data.LegalEntities
            .OrderBy(e => e.Code, StringComparer.Ordinal)
            .Select(e =>
            {
                var scheme = data.LevelSchemeOf(e.Id);
                return new LegalEntityLevels(e, scheme.LegalEntityId is null, scheme.Levels);
            })
            .ToList();

        return new DirectoryMeta(
            today,
            viewer.IsHrAdmin || viewer.DisclosureLeadDays > 0,
            until,
            entities,
            defaultScheme?.Levels ?? [],
            [
                .. milestones
                    .Where(m => until is not { } limit || m.Date <= limit)
                    .GroupBy(m => m.Date)
                    .OrderBy(g => g.Key)
                    .Select(g => new DateMilestone(g.Key, [.. g.Select(m => m.Kind).Distinct().Order()])),
            ]);
    }
}

/// <summary>メタ情報。</summary>
/// <param name="BusinessDate">業務日付（日本時間の今日）。</param>
/// <param name="CanViewFuture">業務日付より後の時点を指定できるか（人事管理者と、先行提供を許された社内システム）。</param>
/// <param name="MaxAsOf">指定できる最後の時点（人事管理者は null = 上限なし。先行提供の社内システムは業務日付 + 日数、それ以外は業務日付）。</param>
/// <param name="DefaultLevels">グループ共通の既定の段（法人に定義が無いときに使う）。</param>
/// <param name="Milestones">時点の目盛り（日付の昇順。人事管理者以外には業務日付以前だけ）。</param>
public sealed record DirectoryMeta(
    DateOnly BusinessDate,
    bool CanViewFuture,
    DateOnly? MaxAsOf,
    IReadOnlyList<LegalEntityLevels> LegalEntities,
    IReadOnlyList<OrgLevel> DefaultLevels,
    IReadOnlyList<DateMilestone> Milestones);

/// <summary>法人と、その法人に効く段の定義。</summary>
/// <param name="UsesDefault">法人の定義が無く、グループ共通の既定を使っているか。</param>
public sealed record LegalEntityLevels(LegalEntity LegalEntity, bool UsesDefault, IReadOnlyList<OrgLevel> Levels);

/// <summary>時点の目盛り（1 日に複数の種類があり得る）。</summary>
public sealed record DateMilestone(DateOnly Date, IReadOnlyList<DirectoryMilestoneKind> Kinds);
