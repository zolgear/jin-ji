using JinJi.Application.Ingest;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>
/// 取込の設定のうち、コネクタ（取得方法・列の対応）に依らない部分。項目マッピングの JSON から作る（JinJi.Connectors の ConnectorMapping）。
/// </summary>
public sealed record ImportProfile
{
    /// <summary>staging の既定の保持日数。</summary>
    public const int DefaultStagingRetentionDays = 90;

    public required ImportTargetKind Target { get; init; }

    /// <summary>取込の既定の法人のコード。行に法人のコードがあればそちらを使う。どちらも無く、法人が 1 つだけならその法人。</summary>
    public string? LegalEntityCode { get; init; }

    /// <summary>社員番号の正規化の規則（ゼロ埋めの桁数など）。</summary>
    public EmployeeNumberRules EmployeeNumber { get; init; } = new();

    /// <summary>取込元に値が無いときの既定値（取込項目 → 値。例: employment_type → 正社員）。</summary>
    public IReadOnlyDictionary<string, string> Defaults { get; init; } = new Dictionary<string, string>();

    /// <summary>staging の保持日数（既定 90 日。§4.5）。</summary>
    public int StagingRetentionDays { get; init; } = DefaultStagingRetentionDays;

    /// <summary>staging の既定の保持日数のうち、名寄せで人に対応付けられなかった行のもの。</summary>
    public const int DefaultUnidentifiedStagingRetentionDays = 14;

    /// <summary>
    /// 名寄せで人に対応付けられなかった行（要確認で対象の人が決まらない行）の staging の保持日数（既定 14 日。<see cref="StagingRetentionDays"/> より長くはしない）。
    /// その行の氏名は戸籍名（旧姓使用者の戸籍姓）の可能性があるので、短く持つ（ADR-0002）。人が解決・却下したら、その時点で氏名を消す。
    /// </summary>
    public int UnidentifiedStagingRetentionDays { get; init; } = DefaultUnidentifiedStagingRetentionDays;

    /// <summary>日付の項目で Excel のシリアル値（5 桁の数）を解釈するか（既定 true）。</summary>
    public bool AllowExcelSerialDates { get; init; } = true;

    /// <summary>
    /// 法人の別名（「株式会社サンプル商事」「(株)サンプル商事」「サンプル商事」→ 法人のコード）。法人のコードの項目の値と、
    /// 自由記述（辞令の出向先・「サンプル物流 物流企画部」のような法人名付きの組織名）の法人名の解釈に使う。比較は検索用の正規化表記で行う。
    /// </summary>
    public IReadOnlyDictionary<string, string> LegalEntityAliases { get; init; } = new Dictionary<string, string>();

    /// <summary>外部のディレクトリのユーザーの取込で、外部 ID の対応に記録するシステム名（既定 entra_id）。</summary>
    public string SourceSystem { get; init; } = "entra_id";

    /// <summary>
    /// 外部のディレクトリのユーザーの取込が全件か（既定 false）。全件なら、在籍者なのに取込元に居ない人を補完提案（アカウント未作成）にする。
    /// </summary>
    public bool FullSnapshot { get; init; }

    /// <summary>法人の別名から法人のコードを引く。別名に無ければ値をそのまま返す。</summary>
    public string? ResolveLegalEntityCode(string? value)
    {
        if (TextNormalizer.ToSearchKey(value) is not { } key)
        {
            return null;
        }

        foreach (var (alias, code) in LegalEntityAliases)
        {
            if (TextNormalizer.ToSearchKey(alias) == key)
            {
                return code;
            }
        }

        return RowNormalizer.Code(value);
    }

    /// <summary>文字列の先頭が法人の別名なら、その法人のコードと残りを返す（長い別名を優先）。</summary>
    public (string? Code, string Remainder) SplitLegalEntityPrefix(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = TextNormalizer.Nfkc(value).Trim();
        foreach (var (alias, code) in LegalEntityAliases.OrderByDescending(a => a.Key.Length))
        {
            var normalizedAlias = TextNormalizer.Nfkc(alias);
            if (text.StartsWith(normalizedAlias, StringComparison.Ordinal))
            {
                return (code, text[normalizedAlias.Length..].Trim());
            }
        }

        return (null, text);
    }

    public void Validate()
    {
        EmployeeNumber.Validate();
        if (StagingRetentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(StagingRetentionDays), StagingRetentionDays, "1〜3650 日で指定してください。");
        }

        if (UnidentifiedStagingRetentionDays is < 1 or > 3650)
        {
            throw new ArgumentOutOfRangeException(nameof(UnidentifiedStagingRetentionDays), UnidentifiedStagingRetentionDays, "1〜3650 日で指定してください。");
        }

        var unknown = Defaults.Keys.Where(k => !IngestFields.Mappable.Contains(k)).ToList();
        if (unknown.Count > 0)
        {
            throw new ArgumentException("既定値に取込項目ではないキーがあります: " + string.Join(", ", unknown), nameof(Defaults));
        }
    }
}
