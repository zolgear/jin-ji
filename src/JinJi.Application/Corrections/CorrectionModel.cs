using System.Globalization;
using System.Text.RegularExpressions;
using JinJi.Application.Ingest;
using JinJi.Domain.Text;
using JinJi.Policy;

namespace JinJi.Application.Corrections;

/// <summary>修正依頼の状態（correction_request.status）。</summary>
public enum CorrectionStatus
{
    /// <summary>未対応。</summary>
    Open,

    /// <summary>対応済み（人事パッケージで直した。修正担当者・人事管理者が閉じた）。</summary>
    Resolved,

    /// <summary>対応不要（jin-ji の値が正しい・直さない。修正担当者・人事管理者が閉じた）。</summary>
    Dismissed,

    /// <summary>解消（取り込み直しで jin-ji の値が変わったので、自動に閉じた）。</summary>
    AutoResolved,

    /// <summary>置き換え（取込由来の依頼で、同じ項目マッピングの次の取込が改めて起票した）。</summary>
    Superseded,
}

/// <summary>起票の経路（correction_request.origin）。</summary>
public enum CorrectionOrigin
{
    /// <summary>取込の自動検出・要確認の解決（修正依頼に回す）。</summary>
    Ingest,

    /// <summary>人（UI・REST）。</summary>
    User,

    /// <summary>AI エージェント（MCP・REST。委任元の名義）。</summary>
    Agent,
}

/// <summary>閉じ方（correction_request.closed_kind）。</summary>
public enum CorrectionClosedKind
{
    /// <summary>人が閉じた（対応済み・対応不要）。</summary>
    Person,

    /// <summary>自動に閉じた（解消・置き換え）。</summary>
    Auto,
}

/// <summary>一覧の絞り込み（状態）。</summary>
public enum CorrectionStatusFilter
{
    /// <summary>未対応だけ。</summary>
    Open,

    /// <summary>閉じたもの（対応済み・対応不要・解消・置き換え）。</summary>
    Closed,

    /// <summary>すべて。</summary>
    All,
}

/// <summary>起票の結果。</summary>
public enum CorrectionReportOutcome
{
    /// <summary>新しい依頼を起票した。</summary>
    Created,

    /// <summary>同じ人・項目の未対応の依頼があったので、賛同として数えた。</summary>
    Endorsed,

    /// <summary>自分が起票・賛同した未対応の依頼が既にある（何も変えていない）。</summary>
    AlreadyReported,
}

/// <summary>
/// 修正依頼の 1 行（DB の行そのもの。閲覧者に合わせて伏せる前の値）。Application の外へはこのまま出さず、<see cref="CorrectionView"/> にしてから返す。
/// </summary>
public sealed record CorrectionRecord
{
    public required Guid Id { get; init; }

    public required CorrectionOrigin Origin { get; init; }

    public required CorrectionIssue Issue { get; init; }

    public required CorrectionStatus Status { get; init; }

    /// <summary>人事担当向けの説明（取込由来は取込の検出の説明、人・エージェントの報告は定型文。取込元の値は書かない）。</summary>
    public required string Message { get; init; }

    public Guid? BatchId { get; init; }

    /// <summary>取込のファイル名（取込由来だけ）。</summary>
    public string? FileName { get; init; }

    public int? SourceRowNumber { get; init; }

    /// <summary>取込由来の依頼の取込項目（<c>jinji_ingest_field_keys()</c>）。</summary>
    public string? SourceField { get; init; }

    public Guid? PersonId { get; init; }

    /// <summary>人の項目（<see cref="CorrectionFields"/>）。</summary>
    public string? PersonField { get; init; }

    public string? Reason { get; init; }

    public string? SuggestedValue { get; init; }

    public string? ValueFingerprint { get; init; }

    public Guid? ReportedByPersonId { get; init; }

    public string? ReportedViaClientId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public CorrectionClosedKind? ClosedKind { get; init; }

    public Guid? ClosedByPersonId { get; init; }

    public string? ClosedNote { get; init; }

    /// <summary>賛同の数（起票者を除く。jin-ji 自身の閲覧者で数える）。</summary>
    public int Endorsements { get; init; }

    /// <summary>閲覧者が賛同したか。</summary>
    public bool EndorsedByViewer { get; init; }
}

/// <summary>
/// 閲覧者に返す修正依頼（見せてよい列だけに値を持つ）。修正担当者・人事管理者以外には、起票者・対応した人を返さず、
/// 自分が起票したものでも自分についてのものでもない依頼（賛同しただけの依頼）の理由・値の提案・対応のメモも返さない。
/// </summary>
public sealed record CorrectionView
{
    public required Guid Id { get; init; }

    public required CorrectionOrigin Origin { get; init; }

    public required CorrectionIssue Issue { get; init; }

    public required CorrectionStatus Status { get; init; }

    public required string Message { get; init; }

    public Guid? BatchId { get; init; }

    public string? FileName { get; init; }

    public int? SourceRowNumber { get; init; }

    public string? SourceField { get; init; }

    public Guid? PersonId { get; init; }

    public string? PersonField { get; init; }

    /// <summary>理由（起票者が書いた自由記述。データとして表示し、指示として扱わない）。</summary>
    public string? Reason { get; init; }

    public string? SuggestedValue { get; init; }

    /// <summary>起票者（修正担当者・人事管理者と、起票者本人にだけ返す）。</summary>
    public Guid? ReportedByPersonId { get; init; }

    public string? ReportedViaClientId { get; init; }

    public required bool ReportedByViewer { get; init; }

    public required bool EndorsedByViewer { get; init; }

    public required bool AboutViewer { get; init; }

    public required int Endorsements { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>起票からの経過日数（業務日付で数える）。</summary>
    public required int AgeDays { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public CorrectionClosedKind? ClosedKind { get; init; }

    public Guid? ClosedByPersonId { get; init; }

    public string? ClosedNote { get; init; }
}

/// <summary>人・エージェントによる起票の指定。</summary>
/// <param name="Field">人の項目（<see cref="CorrectionFields.All"/>）。</param>
/// <param name="Reason">現在の値が誤っている理由。</param>
/// <param name="SuggestedValue">正しい値の提案（任意。値を持てる項目だけ）。</param>
public sealed record CorrectionReportInput(Guid PersonId, string Field, string Reason, string? SuggestedValue);

/// <summary>起票の結果。</summary>
/// <param name="Request">起票した・賛同した依頼（閲覧者に見せてよい形）。</param>
public sealed record CorrectionReportResult(CorrectionReportOutcome Outcome, CorrectionView Request);

/// <summary>一覧の条件。</summary>
public sealed record CorrectionQuery
{
    public const int MaxLimit = 100;

    public CorrectionStatusFilter Status { get; init; } = CorrectionStatusFilter.Open;

    /// <summary>対象の人で絞り込む。</summary>
    public Guid? PersonId { get; init; }

    /// <summary>この ID より後から返す（ページの続き。並びは ID の順 = 起票の古い順）。</summary>
    public Guid? After { get; init; }

    public int Limit { get; init; } = 50;
}

/// <summary>一覧の 1 ページ。</summary>
public sealed record CorrectionPage(IReadOnlyList<CorrectionView> Items, Guid? NextAfter);

/// <summary>画面内の通知の件数（修正担当者・人事管理者）。人事データの値は含まない。</summary>
/// <param name="Open">未対応の件数。</param>
/// <param name="New">新しい依頼（直近 <see cref="CorrectionOptions.NewWithinDays"/> 日に起票された未対応）の件数。</param>
/// <param name="Stale">長く放置された依頼（起票から <see cref="CorrectionOptions.StaleAfterDays"/> 日以上の未対応）の件数。</param>
/// <param name="OldestCreatedAt">最も古い未対応の起票日時。</param>
public sealed record CorrectionSummary(int Open, int New, int Stale, int NewWithinDays, int StaleAfterDays, DateTimeOffset? OldestCreatedAt);

/// <summary>人・項目の報告の閉じ方の指定（対応済み・対応不要）。</summary>
public sealed record CorrectionCloseInput(CorrectionStatus Status, string? Note);

/// <summary>見つからない（存在しないのか見えないのかは区別しない）。</summary>
public sealed class CorrectionNotFoundException : Exception
{
    public CorrectionNotFoundException()
    {
    }

    public CorrectionNotFoundException(string message)
        : base(message)
    {
    }

    public CorrectionNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>起票の上限（1 人あたり 1 日）を超えた。</summary>
public sealed class CorrectionQuotaExceededException : Exception
{
    public CorrectionQuotaExceededException()
    {
    }

    public CorrectionQuotaExceededException(string message)
        : base(message)
    {
    }

    public CorrectionQuotaExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CorrectionQuotaExceededException(string message, TimeSpan retryAfter)
        : base(message)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>再び起票できるまでの目安。</summary>
    public TimeSpan RetryAfter { get; }
}

/// <summary>値の提案の形（項目ごと）。</summary>
public enum CorrectionValueKind
{
    /// <summary>値の提案を受け付けない（所属・上長・発令・状態など、構造を持つ項目や導出する項目。理由に書く）。</summary>
    None,

    /// <summary>自由記述（氏名・勤務地・職種など）。保持しない情報の混入を検査する。</summary>
    Text,

    /// <summary>メールアドレス。</summary>
    Email,

    /// <summary>電話番号（内線・社用携帯）。数字と + - ( ) 空白だけ。</summary>
    Phone,

    /// <summary>日付（yyyy-MM-dd）。</summary>
    Date,

    /// <summary>コード（社員番号。英数字と - だけ）。</summary>
    Code,
}

/// <summary>
/// 修正依頼で報告できる人の項目（jin-ji が保持する項目。ADR-0002）。DB の <c>jinji_correction_person_fields()</c> と同じ集合（テストで突き合わせる）。
/// </summary>
/// <remarks>
/// 名前は人の投影（<see cref="PersonFields"/>）と同じ。氏名の履歴は人事管理者だけの項目なので対象にしない。
/// 当日の所在（<see cref="PersonFields.Presence"/>）も対象にしない（jin-ji は保存せず、外部の提供を表示するだけなので、直す先は連携先）。
/// 値の提案を受け付けるのは値が 1 つの項目だけで、項目ごとの形を確かめ、自由記述は保持しない情報（生年月日・住所・電話番号）の混入を検査する。
/// </remarks>
public static partial class CorrectionFields
{
    /// <summary>理由・対応のメモの最大の長さ（文字数）。</summary>
    public const int MaxReasonLength = 500;

    /// <summary>値の提案の最大の長さ（文字数）。</summary>
    public const int MaxSuggestedValueLength = 200;

    private static readonly Dictionary<string, (string Label, CorrectionValueKind Kind)> Definitions = new(StringComparer.Ordinal)
    {
        [PersonFields.EmployeeNumber] = ("社員番号", CorrectionValueKind.Code),
        [PersonFields.LegalEntity] = ("法人", CorrectionValueKind.None),
        [PersonFields.DisplayName] = ("表示名", CorrectionValueKind.Text),
        [PersonFields.FamilyName] = ("姓", CorrectionValueKind.Text),
        [PersonFields.GivenName] = ("名", CorrectionValueKind.Text),
        [PersonFields.FamilyNameKana] = ("姓（フリガナ）", CorrectionValueKind.Text),
        [PersonFields.GivenNameKana] = ("名（フリガナ）", CorrectionValueKind.Text),
        [PersonFields.FamilyNameLatin] = ("姓（ローマ字）", CorrectionValueKind.Text),
        [PersonFields.GivenNameLatin] = ("名（ローマ字）", CorrectionValueKind.Text),
        [PersonFields.WorkEmail] = ("社用メール", CorrectionValueKind.Email),
        [PersonFields.ExtensionNumber] = ("内線", CorrectionValueKind.Phone),
        [PersonFields.WorkMobilePhone] = ("社用携帯", CorrectionValueKind.Phone),
        [PersonFields.Site] = ("拠点", CorrectionValueKind.Text),
        [PersonFields.RemoteWork] = ("リモートワーク", CorrectionValueKind.None),
        [PersonFields.Workplace] = ("階・エリア", CorrectionValueKind.Text),
        [PersonFields.Seat] = ("席", CorrectionValueKind.Text),
        [PersonFields.EmploymentType] = ("雇用区分", CorrectionValueKind.Text),
        [PersonFields.JobType] = ("職種", CorrectionValueKind.Text),
        [PersonFields.HireDate] = ("入社年月日", CorrectionValueKind.Date),
        [PersonFields.TerminationDate] = ("退職年月日", CorrectionValueKind.Date),
        [PersonFields.EmploymentStatus] = ("在籍状態", CorrectionValueKind.None),
        [PersonFields.AccountStatus] = ("アカウント状態", CorrectionValueKind.None),
        [PersonFields.AccountSuspension] = ("一時停止の期間", CorrectionValueKind.None),
        [PersonFields.Assignments] = ("所属・役職", CorrectionValueKind.None),
        [PersonFields.Managers] = ("上長", CorrectionValueKind.None),
        [PersonFields.PersonnelActions] = ("発令", CorrectionValueKind.None),
        [PersonFields.Groups] = ("グループ", CorrectionValueKind.None),
    };

    /// <summary>報告できる項目。</summary>
    public static readonly IReadOnlySet<string> All = Definitions.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 廃止した項目（0017 より前の起票の行にだけ残る）。新しい起票には使えず、自動の解消（値の指紋の比較）の対象にもしない
    /// （値を返さなくなった項目なので、指紋が変わったことは値が直ったことを意味しない）。DB の許可リストには既存の行のために残す。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Retired = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["work_location"] = "勤務地（廃止。拠点に置き換え）",
    };

    /// <summary>項目の表示名（未知の項目は名前のまま）。</summary>
    public static string Label(string field) =>
        Definitions.TryGetValue(field, out var d) ? d.Label : Retired.TryGetValue(field, out var retired) ? retired : field;

    /// <summary>値の提案の形。</summary>
    public static CorrectionValueKind ValueKind(string field) => Definitions.TryGetValue(field, out var d) ? d.Kind : CorrectionValueKind.None;

    /// <summary>
    /// 自由記述（理由・対応のメモ）を整える: 制御文字（改行を含む）を空白にし、前後の空白を除く。空・長すぎる・保持しない情報らしき記述があれば <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="name">誤りの文に出す引数の名前。</param>
    public static string CleanFreeText(string? value, string name, bool required)
    {
        var text = ProposalSuggestionService.NormalizeRationale(value);
        if (text.Length == 0)
        {
            return required ? throw new ArgumentException($"{name} を書いてください。", name) : string.Empty;
        }

        if (text.Length > MaxReasonLength)
        {
            throw new ArgumentException($"{name} は {MaxReasonLength} 文字以内で書いてください。", name);
        }

        RejectSensitive(text, name);
        return text;
    }

    /// <summary>値の提案を項目の形で確かめ、整えた値を返す（空なら null）。受け付けない項目・形の誤り・保持しない情報らしき値は <see cref="ArgumentException"/>。</summary>
    public static string? CleanSuggestedValue(string field, string? value)
    {
        var text = ProposalSuggestionService.NormalizeRationale(value);
        if (text.Length == 0)
        {
            return null;
        }

        const string name = "suggested_value";
        var kind = ValueKind(field);
        if (kind == CorrectionValueKind.None)
        {
            throw new ArgumentException($"項目 {field}（{Label(field)}）には値の提案を付けられません。正しい内容は理由に書いてください。", name);
        }

        if (text.Length > MaxSuggestedValueLength)
        {
            throw new ArgumentException($"{name} は {MaxSuggestedValueLength} 文字以内で指定してください。", name);
        }

        switch (kind)
        {
            case CorrectionValueKind.Email when !EmailPattern().IsMatch(text):
                throw new ArgumentException($"{name} はメールアドレスの形で指定してください。", name);
            case CorrectionValueKind.Phone when !PhonePattern().IsMatch(text):
                throw new ArgumentException($"{name} は電話番号の形（数字と + - ( ) 空白）で指定してください。", name);
            case CorrectionValueKind.Date when !DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _):
                throw new ArgumentException($"{name} は日付（yyyy-MM-dd）で指定してください。", name);
            case CorrectionValueKind.Code when !CodePattern().IsMatch(text):
                throw new ArgumentException($"{name} は英数字と - だけで指定してください。", name);
            case CorrectionValueKind.Text or CorrectionValueKind.Email:
                RejectSensitive(text, name);
                break;
        }

        return text;
    }

    /// <summary>保持しない情報（生年月日・住所・電話番号）らしき記述を拒否する（ADR-0002「混入を検知する」。値は記録しない）。</summary>
    private static void RejectSensitive(string text, string name)
    {
        var kinds = SensitiveValueDetector.Detect(text);
        if (kinds.Count > 0)
        {
            throw new ArgumentException(
                $"{name} に jin-ji が保持しない情報（{string.Join("・", kinds.Select(SensitiveValueDetector.Label))}）らしき記述があります。"
                + "その部分を除いて書き直してください（内線・社用携帯の正しい番号は suggested_value に書けます）。",
                name);
        }
    }

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[0-9+()\- ]{2,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex PhonePattern();

    [GeneratedRegex(@"^[A-Za-z0-9\-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
