namespace JinJi.Application.Sync;

/// <summary>
/// 同期する項目（Microsoft Graph の user のプロパティ名と、上長 manager）。DB の <c>jinji_sync_field_keys()</c> と同じ集合（テストで突き合わせる）。
/// </summary>
/// <remarks>
/// 全社公開レベルの項目（ADR-0002）だけ。Graph の user には birthday・streetAddress なども有るが、jin-ji は読まず・書かない。
/// </remarks>
public static class EntraFields
{
    public const string DisplayName = "displayName";
    public const string GivenName = "givenName";
    public const string Surname = "surname";
    public const string Department = "department";
    public const string JobTitle = "jobTitle";
    public const string CompanyName = "companyName";
    public const string OfficeLocation = "officeLocation";
    public const string EmployeeId = "employeeId";
    public const string AccountEnabled = "accountEnabled";

    /// <summary>上長（<c>/users/{id}/manager</c>。値は上長のオブジェクト ID）。</summary>
    public const string Manager = "manager";

    /// <summary>作成の推奨に使う（同期の対象の項目ではない）。</summary>
    public const string UserPrincipalName = "userPrincipalName";

    /// <summary>作成の推奨に使う（同期の対象の項目ではない）。</summary>
    public const string MailNickname = "mailNickname";

    /// <summary>計画・実行結果に現れてよい項目のすべて。</summary>
    public static readonly IReadOnlyList<string> All =
    [
        DisplayName, GivenName, Surname, Department, JobTitle, CompanyName, OfficeLocation, EmployeeId, AccountEnabled, Manager, UserPrincipalName, MailNickname,
    ];

    /// <summary>設定（<see cref="SyncSettings.Fields"/>）で選べる同期の対象の項目。</summary>
    public static readonly IReadOnlyList<string> Syncable =
    [
        DisplayName, GivenName, Surname, Department, JobTitle, CompanyName, OfficeLocation, EmployeeId, Manager, AccountEnabled,
    ];

    /// <summary><c>PATCH /users/{id}</c> で更新する文字列の属性（<see cref="AccountEnabled"/>・<see cref="Manager"/> を除く）。</summary>
    public static readonly IReadOnlyList<string> Attributes =
    [
        DisplayName, GivenName, Surname, Department, JobTitle, CompanyName, OfficeLocation, EmployeeId,
    ];
}

/// <summary>
/// 外部のディレクトリ（Entra ID）のユーザー 1 人。値は Graph のプロパティ名 → 文字列（真偽値は true / false、上長はオブジェクト ID）。
/// </summary>
/// <param name="ObjectId">オブジェクト ID（UUID の小文字）。</param>
/// <param name="Values">入力に有ったプロパティだけ（null は「空」。キーが無いのは「入力に無いので分からない」）。</param>
/// <param name="SourceIndex">入力の value 配列の位置（0 始まり）。</param>
public sealed record DirectoryUser(string ObjectId, IReadOnlyDictionary<string, string?> Values, int SourceIndex)
{
    /// <summary>プロパティが入力にあったか（無ければ現在値は分からない）。</summary>
    public bool Knows(string field) => Values.ContainsKey(field);

    public string? this[string field] => Values.GetValueOrDefault(field);

    /// <summary>アカウントが有効か（入力に無ければ null）。</summary>
    public bool? AccountEnabled => Values.GetValueOrDefault(EntraFields.AccountEnabled) switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };
}

/// <summary>外部 ID の対応（external_identity）の 1 行。</summary>
public sealed record ExternalIdentityLink(Guid PersonId, string ExternalId);

/// <summary>計画の状態（sync_plan.status）。</summary>
public enum SyncPlanStatus
{
    /// <summary>起案中。</summary>
    Proposed,

    /// <summary>承認済み（スクリプト出力・直接反映ができる）。</summary>
    Approved,

    /// <summary>却下。</summary>
    Rejected,

    /// <summary>新しい計画で置き換えられた。</summary>
    Superseded,

    /// <summary>古い（jin-ji 側が変わった・期限を過ぎた）。作り直す。</summary>
    Stale,

    /// <summary>実行済み（実行結果を取り込んだ）。</summary>
    Applied,
}

/// <summary>項目の優先度。IT 全般統制（退職者の無効化・一時停止者のサインイン停止）が最上位。</summary>
public enum SyncPriority
{
    Critical,
    High,
    Normal,
    Info,
}

/// <summary>項目の操作。</summary>
public enum SyncAction
{
    /// <summary>退職者のアカウントを無効にする（accountEnabled = false）。</summary>
    DisableAccount,

    /// <summary>一時停止中の人のサインインを止める（accountEnabled = false）。</summary>
    BlockSignIn,

    /// <summary>無効なアカウントを有効に戻す（一時停止の終了など。既定では推奨だけ）。</summary>
    EnableAccount,

    /// <summary>属性（表示名・部署など）を更新する。</summary>
    UpdateAttributes,

    /// <summary>上長を設定する。</summary>
    SetManager,

    /// <summary>アカウントを作成する（既定では推奨だけで、スクリプトに含めない）。</summary>
    CreateUser,

    /// <summary>jin-ji の人に対応付けられないアカウント（参考。棚卸しの対象）。</summary>
    UnmatchedAccount,

    /// <summary>人ではない・設定で除外したアカウント（参考）。</summary>
    ExcludedAccount,
}

/// <summary>項目ごとの実行結果（sync_item_result.outcome）。</summary>
public enum SyncOutcome
{
    /// <summary>反映した。</summary>
    Succeeded,

    /// <summary>既にあるべき値になっていた（何もしなかった。冪等）。</summary>
    AlreadyApplied,

    /// <summary>今の値が計画の値と違うのでスキップした（計画の後に Entra 側が変わった）。</summary>
    SkippedPrecondition,

    /// <summary>失敗した（エラーでも続行し、記録する）。</summary>
    Failed,

    /// <summary>ドライラン（-WhatIf）で、反映する予定だった。</summary>
    WouldApply,
}

/// <summary>実行の方式。</summary>
public enum SyncRunMode
{
    /// <summary>生成したスクリプトを管理者が実行し、ログを取り込んだ。</summary>
    Script,

    /// <summary>jin-ji が Graph に直接反映した（既定は無効）。</summary>
    Direct,
}

/// <summary>1 つの項目の変更。</summary>
/// <param name="Field">項目（<see cref="EntraFields"/>）。</param>
/// <param name="Current">計画を作ったときの Entra の値（事前条件）。</param>
/// <param name="Desired">jin-ji から決めたあるべき値。</param>
/// <param name="CurrentKnown">現在値が入力にあったか（無い場合、事前条件は「あるべき値になっていなければ反映」だけになる）。</param>
public sealed record SyncChange(string Field, string? Current, string? Desired, bool CurrentKnown = true);

/// <summary>根拠の 1 件（jin-ji のどのデータから決めたか）。</summary>
/// <param name="Field">根拠を示す項目（<see cref="EntraFields"/>）。</param>
/// <param name="Source">jin-ji のデータの種類（person / person_version / assignment / org_unit / legal_entity / account_suspension / external_identity / settings）。</param>
/// <param name="Reference">行の ID・段のキーなど。</param>
/// <param name="Value">jin-ji の値（終了日などの将来の予定は入れない）。</param>
public sealed record SyncEvidence(string Field, string Source, string? Reference = null, string? Value = null);

/// <summary>項目の根拠。</summary>
public sealed record SyncBasis
{
    /// <summary>種別のコード（retired_enabled・attributes_differ など）。画面の絞り込みと評価に使う。</summary>
    public string? Code { get; init; }

    /// <summary>jin-ji の根拠。</summary>
    public IReadOnlyList<SyncEvidence> Jinji { get; init; } = [];

    /// <summary>入力の Entra のユーザー一覧での位置（0 始まり）。</summary>
    public int? DirectoryIndex { get; init; }
}

/// <summary>計画の項目（sync_plan_item の 1 行）。</summary>
public sealed record SyncPlanItem
{
    public required Guid Id { get; init; }

    /// <summary>計画の中の順序（1 始まり。優先度の高い順）。</summary>
    public required int Sequence { get; init; }

    public required SyncPriority Priority { get; init; }

    public required SyncAction Action { get; init; }

    public Guid? PersonId { get; init; }

    /// <summary>表示用の社員番号（jin-ji の人）。</summary>
    public string? EmployeeNumber { get; init; }

    /// <summary>Entra のオブジェクト ID（作成の推奨では null）。</summary>
    public string? ObjectId { get; init; }

    /// <summary>表示用の UPN（作成の推奨では作るときの UPN）。</summary>
    public string? UserPrincipalName { get; init; }

    /// <summary>スクリプト・直接反映で実行するか（推奨・参考の項目は false）。</summary>
    public required bool InScript { get; init; }

    public IReadOnlyList<SyncChange> Changes { get; init; } = [];

    public SyncBasis Basis { get; init; } = new();

    /// <summary>説明（日本語。取込元の値は書かない）。</summary>
    public string? Note { get; init; }
}

/// <summary>保存した計画（sync_plan の 1 行）。</summary>
public sealed record SyncPlanRecord
{
    public required Guid Id { get; init; }

    public string TargetSystem { get; init; } = SyncPlanService.TargetSystemEntra;

    public required SyncPlanStatus Status { get; init; }

    public required SyncSettings Settings { get; init; }

    public required string SettingsSha256 { get; init; }

    public required string SourceFileName { get; init; }

    public required string SourceSha256 { get; init; }

    public required int DirectoryUserCount { get; init; }

    public required string JinjiFingerprint { get; init; }

    public required DateOnly BusinessDate { get; init; }

    /// <summary>種別ごとの件数（total・critical・in_script・action:disable_account など）。</summary>
    public IReadOnlyDictionary<string, int> Summary { get; init; } = new Dictionary<string, int>();

    public required Guid ProposedByPersonId { get; init; }

    public required Policy.ActorType ProposedViaActor { get; init; }

    public string? ProposedViaClientId { get; init; }

    public required DateTimeOffset ProposedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public Guid? DecidedByPersonId { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    public DateTimeOffset? AppliedAt { get; init; }
}

/// <summary>計画の差分計算の入力。</summary>
/// <param name="Snapshot">業務日付時点の組織・人（属性も業務日付時点。内示は含まない）。</param>
/// <param name="Identities">外部 ID の対応（設定の同期先のもの）。</param>
public sealed record SyncPlanInput(
    Domain.Organization.OrgSnapshot Snapshot,
    IReadOnlyList<ExternalIdentityLink> Identities,
    IReadOnlyList<DirectoryUser> Users,
    SyncSettings Settings,
    DateOnly Today);

/// <summary>差分計算の結果（保存前）。</summary>
/// <param name="Items">項目（優先度の高い順。<see cref="SyncPlanItem.Sequence"/> は 1 から）。</param>
/// <param name="JinjiFingerprint">jin-ji 側のあるべき値の SHA-256（古い計画の検出に使う）。</param>
/// <param name="Warnings">設定の問題など（例: 段のキーが段の定義に無い）。</param>
public sealed record SyncPlanDraft(IReadOnlyList<SyncPlanItem> Items, string JinjiFingerprint, IReadOnlyList<string> Warnings);

/// <summary>スクリプト一式（ファイル名 → 内容）。</summary>
/// <param name="Files">ファイル名（ディレクトリを含まない）→ バイト列。</param>
/// <param name="DataSha256">スクリプトが読む JSON データの SHA-256（スクリプトに埋め込み、実行ログでも照合する）。</param>
public sealed record SyncScriptBundle(IReadOnlyDictionary<string, byte[]> Files, string DataSha256);

/// <summary>実行ログの 1 項目。</summary>
public sealed record SyncLogEntry(Guid ItemId, SyncOutcome Outcome, IReadOnlyDictionary<string, string?> Observed, string? Message, DateTimeOffset? ExecutedAt);

/// <summary>解釈した実行ログ。</summary>
public sealed record SyncExecutionLog(
    Guid PlanId,
    string DataSha256,
    bool WhatIf,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<SyncLogEntry> Entries);

/// <summary>実行の記録（sync_run と sync_item_result）。</summary>
public sealed record SyncRunRecord
{
    public required Guid Id { get; init; }

    public required Guid PlanId { get; init; }

    public required SyncRunMode Mode { get; init; }

    public required bool WhatIf { get; init; }

    public string? LogSha256 { get; init; }

    public required string DataSha256 { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }

    public Guid? RecordedByPersonId { get; init; }

    public required Policy.ActorType RecordedViaActor { get; init; }

    public DateTimeOffset RecordedAt { get; init; }

    public required IReadOnlyList<SyncLogEntry> Results { get; init; }

    /// <summary>ログにあったが計画のスクリプトの項目に無かった（捨てた）件数。</summary>
    public int UnknownEntryCount { get; init; }
}

/// <summary>項目ごとの照合の結果（最新の実行）。</summary>
/// <param name="Outcome">最新の実行（ドライランより本番を優先）の結果。どの実行にも無ければ null（未実行）。</param>
public sealed record SyncItemReconciliation(Guid ItemId, int Sequence, SyncAction Action, SyncPriority Priority, SyncOutcome? Outcome, Guid? RunId, string? Message);

/// <summary>実行結果の取込の結果。</summary>
public sealed record SyncResultImport(
    Guid RunId,
    bool WhatIf,
    IReadOnlyDictionary<SyncOutcome, int> Counts,
    int MissingCount,
    int UnknownEntryCount,
    IReadOnlyList<SyncItemReconciliation> Items);

/// <summary>計画と項目と実行結果（詳細の参照）。</summary>
public sealed record SyncPlanDetail(SyncPlanRecord Plan, IReadOnlyList<SyncPlanItem> Items, IReadOnlyList<SyncItemReconciliation> Reconciliation);

/// <summary>計画が古い（作成後に jin-ji 側が変わった・期限を過ぎた）。計画は stale にした。作り直す。</summary>
public sealed class SyncPlanStaleException : Exception
{
    public SyncPlanStaleException()
    {
    }

    public SyncPlanStaleException(string message)
        : base(message)
    {
    }

    public SyncPlanStaleException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>計画が無い（見えない）。</summary>
public sealed class SyncPlanNotFoundException : Exception
{
    public SyncPlanNotFoundException()
    {
    }

    public SyncPlanNotFoundException(string message)
        : base(message)
    {
    }

    public SyncPlanNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>実行ログの形が不正（計画・データの取り違え、改ざん、未知の形式）。</summary>
public sealed class SyncLogFormatException : Exception
{
    public SyncLogFormatException()
    {
    }

    public SyncLogFormatException(string message)
        : base(message)
    {
    }

    public SyncLogFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
