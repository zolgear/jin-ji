using JinJi.Policy;

namespace JinJi.Application.Ingest;

/// <summary>取込元の種類（import_batch.source_kind）。</summary>
public enum ImportSourceKind
{
    Csv,
    Xlsx,

    /// <summary>Microsoft Graph の GET /users 応答（JSON。ファイル入力、後で Graph の読み取りに差し替える）。</summary>
    GraphJson,

    /// <summary>取込済みの自由記述の勤務地（0017 で廃止した person_version.work_location）。拠点マスタへの照合（<c>WorkLocationReconciler</c>）。</summary>
    WorkLocation,

    /// <summary>jin-ji の配置図の形式 v1（JSON。建物・階・外形・固定の部分・ゾーン・席を要素ごとの行にして読む。0019）。</summary>
    LayoutJson,
}

/// <summary>取り込む対象（import_batch.target_kind）。</summary>
public enum ImportTargetKind
{
    /// <summary>人（在籍者名簿。人の属性と主務の所属）。</summary>
    People,

    /// <summary>組織（組織図。組織の版）。</summary>
    OrgUnits,

    /// <summary>辞令（発令の一覧。自由記述の発令内容をルールで解釈する）。</summary>
    PersonnelActions,

    /// <summary>外部のディレクトリのユーザー（Entra ID）。外部 ID の対応付けと補完提案だけを作る。</summary>
    DirectoryUsers,

    /// <summary>拠点マスタ（拠点の一覧。拠点の版）。</summary>
    Sites,

    /// <summary>建物・階・配置図の改訂（配置図の JSON・席の CSV。0019）。</summary>
    FloorLayouts,

    /// <summary>人の席・持ち場と組織のエリア（割り当ての CSV。0019）。</summary>
    WorkplaceAssignments,
}

/// <summary>staging に保存する前に捨てた列の理由。</summary>
public enum DroppedColumnKind
{
    /// <summary>項目マッピングに無い列。</summary>
    Unmapped,

    /// <summary>項目マッピングで無視すると指定した列。</summary>
    Ignored,

    /// <summary>保持しない項目（ADR-0002）を表す列名。マッピングの指定に関わらず捨てる。</summary>
    Forbidden,
}

/// <summary>捨てた列。値は持たない（列名と、値のあった件数だけ）。</summary>
public sealed record DroppedColumn(string Name, int Count, DroppedColumnKind Kind);

/// <summary>取込単位（import_batch の 1 行）。</summary>
public sealed record ImportBatchRecord
{
    public required Guid Id { get; init; }

    public required ImportSourceKind SourceKind { get; init; }

    public required ImportTargetKind TargetKind { get; init; }

    public required string FileName { get; init; }

    /// <summary>取込元のファイルの SHA-256（小文字の 16 進）。</summary>
    public required string FileSha256 { get; init; }

    public required string MappingName { get; init; }

    /// <summary>項目マッピング（JSON）の SHA-256（小文字の 16 進）。</summary>
    public required string MappingSha256 { get; init; }

    /// <summary>取込の既定の法人。</summary>
    public Guid? LegalEntityId { get; init; }

    /// <summary>変更の有効日。null なら差分を訂正（同じ有効期間の行の置き換え）として提案する。</summary>
    public DateOnly? EffectiveDate { get; init; }

    /// <summary>取込の実行者（変更提案の起案者になり、承認できない）。</summary>
    public required Guid ExecutedByPersonId { get; init; }

    public required ActorType ExecutedViaActor { get; init; }

    public string? ExecutedViaClientId { get; init; }

    public required int RowCount { get; init; }

    public required IReadOnlyList<DroppedColumn> DroppedColumns { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? FinishedAt { get; init; }
}

/// <summary>staging の行（許可リストを通った取込項目だけ）。</summary>
/// <param name="RowSha256">元の行のハッシュ（保持する列の値だけから作る。捨てた列の値は含めない）。</param>
public sealed record StagingRowRecord(
    Guid Id,
    int SourceRowNumber,
    string RowSha256,
    IReadOnlyDictionary<string, string> Data,
    DateTimeOffset ExpiresAt);

/// <summary>正規化で見つかった問題（項目と種別だけ。値は持たない）。</summary>
public sealed record NormalizationIssue(string Field, string Code);

/// <summary>正規化結果。</summary>
public sealed record NormalizedRowRecord(
    Guid StagingRowId,
    IReadOnlyDictionary<string, string> Data,
    IReadOnlyList<NormalizationIssue> Issues);

/// <summary>名寄せの結果。</summary>
public enum MatchOutcome
{
    /// <summary>候補が 1 人に決まった。</summary>
    Unique,

    /// <summary>候補が複数（要確認）。</summary>
    Ambiguous,

    /// <summary>候補なし。</summary>
    None,
}

/// <summary>名寄せの規則（決定的な突合）。上から順に試す。</summary>
public enum MatchRule
{
    EmployeeNumber,
    WorkEmail,
    NameAndOrg,
}

/// <summary>名寄せ候補（match_candidate の 1 行）。候補なしは <see cref="CandidatePersonId"/> が null の 1 行。</summary>
public sealed record MatchCandidateRecord(
    Guid Id,
    Guid StagingRowId,
    MatchOutcome Outcome,
    MatchRule? Rule,
    Guid? CandidatePersonId,
    decimal Confidence);

/// <summary>変更提案の対象の種別。</summary>
public enum ProposalTargetKind
{
    Person,
    PersonVersion,
    OrgUnit,
    OrgUnitVersion,
    Assignment,

    /// <summary>発令。反映時に、その時点の所属・一時停止から版を導出する。</summary>
    PersonnelAction,

    AccountSuspension,

    /// <summary>外部 ID の対応（Entra ID の objectId 等）。</summary>
    ExternalIdentity,

    /// <summary>拠点（新規: 不変 ID と最初の版）。</summary>
    Site,

    /// <summary>拠点の版（訂正・変更・閉鎖）。</summary>
    SiteVersion,

    /// <summary>建物（新規: 不変 ID と最初の版）。</summary>
    Building,

    /// <summary>建物の版（訂正・変更）。</summary>
    BuildingVersion,

    /// <summary>階（新規: 不変 ID と最初の版）。</summary>
    Floor,

    /// <summary>階の版（訂正・変更）。</summary>
    FloorVersion,

    /// <summary>配置図の改訂（改訂 1 つ分を payload の layout に持つ。承認で floor_layout・ゾーン・席の置き場所になる）。</summary>
    FloorLayout,

    /// <summary>人の席・持ち場（新規・変更・終了）。</summary>
    WorkplaceAssignment,
}

/// <summary>変更提案の操作。</summary>
public enum ProposalOperation
{
    /// <summary>新規（人・組織・所属）。</summary>
    Create,

    /// <summary>訂正: 有効期間はそのままに、記録期間を閉じて新しい行で置き換える。</summary>
    Correct,

    /// <summary>変更: 有効日で版を分ける（有効日の前は旧値、以後は新値）。</summary>
    Change,

    /// <summary>名寄せの要確認（複数候補・規則の衝突など）。このままでは承認できない。</summary>
    Review,

    /// <summary>補完の提案（組織長の未設定など）。このままでは承認できない。</summary>
    Supplement,

    /// <summary>組織・拠点の廃止（有効日の前日で有効期間を終える）。</summary>
    Abolish,

    /// <summary>所属・一時停止の終了（有効日の前日で有効期間を終える。退職・兼務の解除など）。</summary>
    End,
}

/// <summary>変更提案の状態。</summary>
public enum ProposalStatus
{
    /// <summary>起案（承認できる）。</summary>
    Proposed,

    /// <summary>要確認（そのままでは承認できない。却下はできる）。</summary>
    NeedsReview,

    /// <summary>承認して公開側に反映した（承認と反映は同じトランザクション）。</summary>
    Applied,

    Rejected,

    /// <summary>要確認の提案を人が解決した（結果は別の提案として起案し直す。<see cref="ChangeProposalRecord.Resolution"/>）。</summary>
    Resolved,
}

/// <summary>要確認の提案の解決の種別（change_proposal.resolution）。</summary>
public enum ProposalResolution
{
    /// <summary>候補（名寄せ・LLM の候補）から選んだ。</summary>
    SelectedCandidate,

    /// <summary>新規（新しい人）として登録した。</summary>
    RegisteredNew,

    /// <summary>人事パッケージ側の修正依頼に回した。</summary>
    ReferredToCorrection,
}

/// <summary>項目の差分（旧値・新値。値は反映先の列の文字列表現。日付は yyyy-MM-dd、ID は小文字の UUID）。</summary>
public sealed record FieldChange(string? Old, string? New);

/// <summary>提案の根拠の候補（名寄せの候補）。</summary>
public sealed record ProposalCandidate(Guid PersonId, MatchRule Rule, decimal Confidence);

/// <summary>提案の根拠（承認画面で確認する）。取込元の値は持たない（値は payload / diff にある）。</summary>
public sealed record ProposalBasis
{
    /// <summary>取込元の行番号（staging が保持期限で消えても残る）。</summary>
    public IReadOnlyList<int> RowNumbers { get; init; } = [];

    /// <summary>名寄せで一致した規則。</summary>
    public MatchRule? Rule { get; init; }

    /// <summary>名寄せの候補（要確認の提案、同姓同名の別人の可能性など）。</summary>
    public IReadOnlyList<ProposalCandidate> Candidates { get; init; } = [];

    /// <summary>先に承認が要る提案（新しい上位組織の作成など）。</summary>
    public IReadOnlyList<Guid> DependsOn { get; init; } = [];

    /// <summary>説明（機械が生成した定型文。取込元の値は書かない）。</summary>
    public string? Note { get; init; }

    /// <summary>補完提案・要確認の種別（<see cref="SupplementCodes"/>。画面の絞り込みと評価に使う）。</summary>
    public string? Code { get; init; }

    /// <summary>組織の候補（所属のあいまいな辞令の配下の組織、段飛ばしの可能性のある組織の上位組織の候補など）。</summary>
    public IReadOnlyList<Guid> OrgCandidates { get; init; } = [];

    /// <summary>LLM による推定（オプトインの取込だけ。作業領域の情報で、承認を経ないと公開側には出ない）。</summary>
    public LlmSuggestion? Llm { get; init; }
}

/// <summary>LLM が順位を付けた候補（候補の一覧にあった ID だけ。候補外の ID は捨てる）。</summary>
/// <param name="Rank">1 始まりの順位。</param>
/// <param name="Confidence">LLM が付けた確信度（0〜1）。</param>
/// <param name="Reason">LLM が書いた理由（取込元の氏名などは伏せ字にしてから保存する）。</param>
public sealed record LlmRankedCandidate(Guid Id, int Rank, decimal Confidence, string Reason);

/// <summary>
/// LLM による推定（§8「残りのみ LLM」）。要確認の提案の根拠に付ける。人がこれを参考に解決し、別の人が承認したときだけ公開側に出る（§7.1）。
/// </summary>
public sealed record LlmSuggestion
{
    /// <summary>問いの種類（<see cref="LlmQuestionKinds"/>）。</summary>
    public required string Kind { get; init; }

    /// <summary>応答したモデル名。</summary>
    public required string Model { get; init; }

    /// <summary>送信先のホスト名。</summary>
    public required string Endpoint { get; init; }

    /// <summary>人の候補の順位（候補の一覧にある人だけ）。</summary>
    public IReadOnlyList<LlmRankedCandidate> People { get; init; } = [];

    /// <summary>組織の候補の順位（候補の一覧にある組織だけ）。</summary>
    public IReadOnlyList<LlmRankedCandidate> Orgs { get; init; } = [];

    /// <summary>候補の一覧に無い ID を返したため捨てた件数（幻覚）。</summary>
    public int DiscardedIds { get; init; }
}

/// <summary>LLM に回す問いの種類（<see cref="LlmSuggestion.Kind"/>）。</summary>
public static class LlmQuestionKinds
{
    /// <summary>行の人（社員番号の誤記・欠落、同姓同名など）を候補から選ぶ。</summary>
    public const string Person = "person";

    /// <summary>所属のあいまいな辞令の組織を、配下の組織から選ぶ（人の候補も一緒に問うことがある）。</summary>
    public const string AmbiguousOrg = "ambiguous_org";

    /// <summary>段飛ばしの可能性のある組織の上位組織を選ぶ。</summary>
    public const string ParentOrg = "parent_org";

    /// <summary>Entra ID の旧組織名の後継組織を選ぶ。</summary>
    public const string SuccessorOrg = "successor_org";
}

/// <summary>補完提案・要確認の種別（<see cref="ProposalBasis.Code"/>）。</summary>
public static class SupplementCodes
{
    public const string OrgHeadMissing = "org_head_missing";
    public const string AbolishedDateUnknown = "abolished_date_unknown";
    public const string NonPersonAccount = "non_person_account";
    public const string DirectoryEmployeeIdEmpty = "directory_employee_id_empty";
    public const string DirectoryRetiredEnabled = "directory_retired_enabled";
    public const string DirectoryDepartmentMismatch = "directory_department_mismatch";
    public const string DirectoryAccountMissing = "directory_account_missing";
    public const string AmbiguousOrg = "ambiguous_org";
    public const string SuspectedEmployeeNumberTypo = "suspected_employee_number_typo";
    public const string MissingEmployeeNumber = "missing_employee_number";
    public const string UninterpretableAction = "uninterpretable_action";

    /// <summary>列が階層の組織図で、結合セルの補いで決めた上位組織が段飛ばしの可能性がある（上位組織の候補を確かめる）。</summary>
    public const string OrgParentAmbiguous = "org_parent_ambiguous";

    /// <summary>
    /// 管理元が jin-ji（field）の領域の差分（ADR-0013 決定 2）。人事パッケージとの差として出すだけで、承認できない（却下で確認済みにする）。
    /// </summary>
    public const string FieldManaged = "field_managed";

    /// <summary>管理元が両方（both）の領域で、直接登録した値を変える差分（承認するまで上書きしない。承認する前に現場の登録と突き合わせる）。</summary>
    public const string FieldRegistered = "field_registered";
}

/// <summary>変更提案（change_proposal の 1 行）。</summary>
public sealed record ChangeProposalRecord
{
    public required Guid Id { get; init; }

    public required Guid BatchId { get; init; }

    public required ProposalTargetKind TargetKind { get; init; }

    public required ProposalOperation Operation { get; init; }

    /// <summary>対象の人・組織の ID（新規は採番済みの ID。名寄せの要確認では null）。</summary>
    public Guid? TargetId { get; init; }

    /// <summary>変更の有効日（新規では有効期間の開始日）。訂正では null（今日有効な行を訂正する）。</summary>
    public DateOnly? EffectiveDate { get; init; }

    /// <summary>新規に作る行の値（キーは <see cref="ProposalFields"/>）。</summary>
    public IReadOnlyDictionary<string, string?> Payload { get; init; } = new Dictionary<string, string?>();

    /// <summary>現在の値との差分（キーは <see cref="ProposalFields"/>）。旧値は反映時に照合する（提案の作成後に変わっていたら反映しない）。</summary>
    public IReadOnlyDictionary<string, FieldChange> Diff { get; init; } = new Dictionary<string, FieldChange>();

    public required ProposalBasis Basis { get; init; }

    public IReadOnlyList<Guid> StagingRowIds { get; init; } = [];

    public required decimal Confidence { get; init; }

    public required ProposalStatus Status { get; init; }

    /// <summary>起案者（取込の実行者）。</summary>
    public required Guid ProposedByPersonId { get; init; }

    public string? ProposedViaClientId { get; init; }

    public Guid? DecidedByPersonId { get; init; }

    /// <summary>解決の結果の提案なら、元の要確認の提案。</summary>
    public Guid? ResolvedFromProposalId { get; init; }

    /// <summary>解決の結果の提案なら、解決した人（この人は承認できない）。</summary>
    public Guid? ResolvedByPersonId { get; init; }

    /// <summary>解決した要確認の提案なら、解決の種別。</summary>
    public ProposalResolution? Resolution { get; init; }
}

/// <summary>修正依頼の種別（correction_request.issue）。</summary>
public enum CorrectionIssue
{
    MissingEmployeeNumber,
    MissingRequired,
    InvalidDate,
    InvalidValue,
    DuplicateRow,
    UnknownLegalEntity,
    UnknownOrg,
    UnknownOrgLevel,
    UnknownHead,
    ConflictingIdentity,

    /// <summary>自由記述の項目に生年月日・住所・電話番号らしき値がある（値は staging に保存せず、記録もしない）。</summary>
    SuspectedSensitiveValue,

    /// <summary>廃止済みの組織が取込元に残っている。</summary>
    AbolishedOrg,

    /// <summary>組織の責任者が退職者のまま。</summary>
    StaleHead,

    /// <summary>発令内容をルールで解釈できない。</summary>
    UninterpretableAction,

    /// <summary>社員番号と氏名が一致しない（誤記の疑い）。</summary>
    SuspectedEmployeeNumberTypo,

    /// <summary>要確認の提案を、人が人事パッケージ側の修正に回した。</summary>
    ReferredFromReview,

    /// <summary>人・AI エージェントが値の誤りを報告した（取込とは関係なく起票する。<see cref="Corrections.CorrectionRequestService"/>）。</summary>
    ReportedValue,

    /// <summary>拠点コード・拠点名（勤務地の照合を含む）が拠点マスタに無い、または一つに決まらない。</summary>
    UnknownSite,

    /// <summary>建物が jin-ji に無い、または一つに決まらない。</summary>
    UnknownBuilding,

    /// <summary>階が jin-ji に無い、または一つに決まらない。</summary>
    UnknownFloor,

    /// <summary>席番号がその階の配置図に無い、または一つに決まらない。</summary>
    UnknownSeat,

    /// <summary>ゾーン（エリア）がその階の配置図に無い。</summary>
    UnknownZone,

    /// <summary>席の拠点（建物の拠点）と人の拠点が食い違う（承認は止めない。席か拠点のどちらを直すかは人が判断する）。</summary>
    SiteMismatch,
}

/// <summary>修正依頼（人事パッケージ側で直すべき項目）。メッセージに取込元の値は書かない。</summary>
public sealed record CorrectionRequestRecord(
    Guid Id,
    Guid? StagingRowId,
    int? SourceRowNumber,
    string? FieldKey,
    CorrectionIssue Issue,
    string Message);

/// <summary>1 回の取込で作業領域に保存するもの（1 トランザクションで保存する）。</summary>
/// <param name="Llm">LLM への送信の記録（LLM を使った取込だけ）。</param>
public sealed record ImportWorkspace(
    ImportBatchRecord Batch,
    IReadOnlyList<StagingRowRecord> StagingRows,
    IReadOnlyList<NormalizedRowRecord> NormalizedRows,
    IReadOnlyList<MatchCandidateRecord> Candidates,
    IReadOnlyList<ChangeProposalRecord> Proposals,
    IReadOnlyList<CorrectionRequestRecord> Corrections,
    LlmUsageRecord? Llm = null);

/// <summary>
/// LLM への送信の記録（llm_request_log の 1 行）。送信先・モデル・送った項目名と件数・回数・トークン数・料金の目安だけで、送った値は持たない。
/// </summary>
public sealed record LlmUsageRecord
{
    public required string EndpointHost { get; init; }

    public required string Model { get; init; }

    /// <summary>送った項目名（<c>jinji_llm_field_keys()</c>）→ 件数。</summary>
    public required IReadOnlyDictionary<string, int> SentFields { get; init; }

    /// <summary>LLM に回した要確認の提案の数。</summary>
    public int Questions { get; init; }

    /// <summary>呼び出し回数（リトライを含む）。</summary>
    public int Calls { get; init; }

    /// <summary>候補外の ID を返したため捨てた件数。</summary>
    public int DiscardedIds { get; init; }

    /// <summary>スキーマに合わない応答の件数。</summary>
    public int SchemaViolations { get; init; }

    /// <summary>タイムアウト・エラーで推定できなかった件数。</summary>
    public int Failed { get; init; }

    /// <summary>1 回の取込の呼び出しの上限で LLM に回さなかった件数。</summary>
    public int Skipped { get; init; }

    public int InputTokens { get; init; }

    public int OutputTokens { get; init; }

    /// <summary>料金の目安（米ドル。単価が未設定なら null）。</summary>
    public decimal? EstimatedCostUsd { get; init; }
}

/// <summary>
/// 変更提案の payload / diff のキー（反映先の列名）。DB の <c>jinji_proposal_field_keys()</c> と同じ集合（テストで突き合わせる）。
/// </summary>
public static class ProposalFields
{
    public const string PersonId = "person_id";
    public const string LegalEntityId = "legal_entity_id";
    public const string EmployeeNumber = "employee_number";
    public const string FamilyName = "family_name";
    public const string GivenName = "given_name";
    public const string FamilyNameKana = "family_name_kana";
    public const string GivenNameKana = "given_name_kana";
    public const string FamilyNameLatin = "family_name_latin";
    public const string GivenNameLatin = "given_name_latin";
    public const string DisplayName = "display_name";
    public const string SearchName = "search_name";
    public const string SearchNameKana = "search_name_kana";
    public const string WorkEmail = "work_email";
    public const string ExtensionNumber = "extension_number";
    public const string WorkMobilePhone = "work_mobile_phone";

    /// <summary>廃止（0017）。自由記述の勤務地。既存の提案の行のために DB の許可リストに残し、新しい提案には書かない。</summary>
    public const string WorkLocation = "work_location";
    public const string EmploymentType = "employment_type";
    public const string JobType = "job_type";
    public const string HireDate = "hire_date";
    public const string TerminationDate = "termination_date";
    public const string OrgUnitId = "org_unit_id";
    public const string AssignmentType = "assignment_type";
    public const string PositionTitle = "position_title";
    public const string SecondmentType = "secondment_type";
    public const string Code = "code";
    public const string Name = "name";
    public const string ShortName = "short_name";
    public const string LevelKey = "level_key";
    public const string ParentOrgUnitId = "parent_org_unit_id";
    public const string HeadPersonId = "head_person_id";
    public const string ValidFrom = "valid_from";
    public const string ActionType = "action_type";
    public const string ConcurrentOrgUnitId = "concurrent_org_unit_id";
    public const string ConcurrentPositionTitle = "concurrent_position_title";
    public const string SecondmentOrgUnitId = "secondment_org_unit_id";
    public const string SecondmentStartDate = "secondment_start_date";
    public const string SuspensionStartDate = "suspension_start_date";
    public const string SourceSystem = "source_system";
    public const string ExternalId = "external_id";

    /// <summary>拠点（人の拠点の上書き・組織の拠点。site.id）。</summary>
    public const string SiteId = "site_id";

    /// <summary>リモートワークの対象か（"true" / "false"）。</summary>
    public const string RemoteWork = "remote_work";

    /// <summary>拠点の種別（site_version.site_type のコード値）。</summary>
    public const string SiteType = "site_type";

    /// <summary>拠点の所在の都道府県のコード。</summary>
    public const string PrefectureCode = "prefecture_code";

    /// <summary>拠点の所在地（都道府県より後）。</summary>
    public const string Location = "location";

    /// <summary>建物（building.id）。</summary>
    public const string BuildingId = "building_id";

    /// <summary>建物の並び順。</summary>
    public const string SortOrder = "sort_order";

    /// <summary>階（floor.id）。</summary>
    public const string FloorId = "floor_id";

    /// <summary>階の表示名。</summary>
    public const string Label = "label";

    /// <summary>階の並び順。</summary>
    public const string LevelOrder = "level_order";

    /// <summary>階の種類のコード値。</summary>
    public const string FloorKind = "floor_kind";

    /// <summary>配置図の改訂 1 つ分（<c>LayoutJson.WriteDraft</c> の JSON）。</summary>
    public const string Layout = "layout";

    /// <summary>席（seat.id）。</summary>
    public const string SeatId = "seat_id";

    /// <summary>ゾーン（改訂をまたぐゾーンの ID）。</summary>
    public const string ZoneKey = "zone_key";

    /// <summary>席の役割（primary / secondary）。</summary>
    public const string Role = "role";

    /// <summary>定員 1 の席か（"true" / "false"）。</summary>
    public const string Exclusive = "exclusive";

    /// <summary>人の属性の版（person_version）の列のうち、提案で変えられるもの。</summary>
    public static readonly IReadOnlyList<string> PersonVersionColumns =
    [
        FamilyName, GivenName, FamilyNameKana, GivenNameKana, FamilyNameLatin, GivenNameLatin, DisplayName, SearchName, SearchNameKana,
        WorkEmail, ExtensionNumber, WorkMobilePhone, SiteId, RemoteWork, EmploymentType, JobType, HireDate, TerminationDate,
    ];

    /// <summary>拠点の版（site_version）の列のうち、提案で変えられるもの。</summary>
    public static readonly IReadOnlyList<string> SiteVersionColumns = [Code, Name, SiteType, PrefectureCode, Location];

    /// <summary>建物の版（building_version）の列のうち、提案で変えられるもの。</summary>
    public static readonly IReadOnlyList<string> BuildingVersionColumns = [Name, SortOrder];

    /// <summary>階の版（floor_version）の列のうち、提案で変えられるもの。</summary>
    public static readonly IReadOnlyList<string> FloorVersionColumns = [Label, LevelOrder, FloorKind];

    /// <summary>人の席（workplace_assignment）の列のうち、提案で変えられるもの。</summary>
    public static readonly IReadOnlyList<string> WorkplaceAssignmentColumns = [FloorId, SeatId, ZoneKey, Exclusive];

    /// <summary>検索用の正規化表記。旧値を DB と照合しない（ドメインの版に持たないため、差分の旧値は null で記録する）。</summary>
    public static readonly IReadOnlySet<string> Derived = new HashSet<string>(StringComparer.Ordinal) { SearchName, SearchNameKana };

    /// <summary>全キー。</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PersonId, LegalEntityId, EmployeeNumber, FamilyName, GivenName, FamilyNameKana, GivenNameKana, FamilyNameLatin, GivenNameLatin,
        DisplayName, SearchName, SearchNameKana, WorkEmail, ExtensionNumber, WorkMobilePhone, WorkLocation, EmploymentType, JobType,
        HireDate, TerminationDate, OrgUnitId, AssignmentType, PositionTitle, SecondmentType, Code, Name, ShortName, LevelKey,
        ParentOrgUnitId, HeadPersonId, ValidFrom,
        ActionType, ConcurrentOrgUnitId, ConcurrentPositionTitle, SecondmentOrgUnitId, SecondmentStartDate, SuspensionStartDate,
        SourceSystem, ExternalId, SiteId, RemoteWork, SiteType, PrefectureCode, Location,
        BuildingId, SortOrder, FloorId, Label, LevelOrder, FloorKind, Layout, SeatId, ZoneKey, Role, Exclusive,
    };
}
