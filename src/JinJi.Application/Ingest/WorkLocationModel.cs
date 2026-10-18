using JinJi.Policy;

namespace JinJi.Application.Ingest;

/// <summary>取込済みの自由記述の勤務地（0017 で廃止した person_version.work_location）の 1 人分。拠点マスタへの照合の入力。</summary>
/// <param name="EmployeeNumber">社員番号（jin-ji の正規形）。</param>
/// <param name="LegalEntityCode">社員番号を採番した法人のコード。</param>
/// <param name="WorkLocation">今日有効な版の勤務地（空でないもの）。</param>
public sealed record LegacyWorkLocation(string EmployeeNumber, string LegalEntityCode, string WorkLocation);

/// <summary>取込済みの勤務地の読み出し（Infrastructure が実装する。人事管理者・jin-ji 自身の閲覧者で読む）。</summary>
public interface IWorkLocationSource
{
    /// <summary>今日有効な人の属性の版のうち、勤務地が空でないもの（社員番号の順）。</summary>
    Task<IReadOnlyList<LegacyWorkLocation>> ListAsync(ViewerContext viewer, DateOnly today, CancellationToken cancellationToken);
}
