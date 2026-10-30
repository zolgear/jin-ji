using JinJi.Application.Ingest;
using JinJi.Policy;

namespace JinJi.Application.FieldManagement;

/// <summary>直接登録の状態（field_registration.status）。</summary>
public enum FieldRegistrationStatus
{
    /// <summary>下書き（AI エージェントが委任元の名義で作った。委任元の確定で反映する）。</summary>
    Draft,

    /// <summary>相手側の同意待ち（登録者の範囲の外の組織にかかる）。</summary>
    PendingConsent,

    /// <summary>反映済み。</summary>
    Applied,

    /// <summary>有効日の前に取り消した。</summary>
    Withdrawn,

    /// <summary>反映せずに破棄した。</summary>
    Discarded,

    /// <summary>下書き・同意待ちのまま期限を過ぎた。</summary>
    Expired,
}

/// <summary>直接登録の変更の種類（入力。1 つの変更が 1 つの変更提案になる）。</summary>
public enum FieldChangeKind
{
    /// <summary>人の主務の所属・役職（異動）。値: org_unit_id・position_title。</summary>
    Assignment,

    /// <summary>人の拠点・リモートワーク・内線・社用携帯。値: site_id・remote_work・extension_number・work_mobile_phone。</summary>
    Person,

    /// <summary>組織の名称・略称・段・上位組織・組織長・拠点。値: name・short_name・level_key・parent_org_unit_id・head_person_id・site_id。</summary>
    OrgUpdate,

    /// <summary>組織の新設。値: code・name・short_name・level_key・parent_org_unit_id（必須）・head_person_id・site_id。</summary>
    OrgCreate,

    /// <summary>組織の廃止（有効日の前日で終える。所属者・配下の組織が残っていれば登録できない）。</summary>
    OrgAbolish,
}

/// <summary>直接登録の 1 つの変更（入力）。値のキーは <see cref="ProposalFields"/>。キーが無い項目は変えない。</summary>
/// <param name="TargetId">対象の人・組織（新設では省略。省略なら採番する）。</param>
public sealed record FieldChangeInput(FieldChangeKind Kind, Guid? TargetId, IReadOnlyDictionary<string, string?> Values);

/// <summary>直接登録の入力。</summary>
public sealed record FieldRegistrationInput(DateOnly EffectiveDate, string? Note, IReadOnlyList<FieldChangeInput> Changes);

/// <summary>直接登録（field_registration の 1 行と、その変更提案）。</summary>
public sealed record FieldRegistration
{
    public required Guid Id { get; init; }

    public required FieldRegistrationStatus Status { get; init; }

    public required DateOnly EffectiveDate { get; init; }

    public string? Note { get; init; }

    /// <summary>登録者（AI エージェントの下書きは委任元）。</summary>
    public required Guid RegisteredBy { get; init; }

    /// <summary>人（<see cref="ActorType.User"/>）か AI エージェント（<see cref="ActorType.Agent"/>）か。</summary>
    public required ActorType RegisteredVia { get; init; }

    public string? RegisteredViaClientId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>下書き・同意待ちの期限。</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>登録が関係する組織。</summary>
    public IReadOnlyList<Guid> OrgUnitIds { get; init; } = [];

    /// <summary>登録で変える人。</summary>
    public IReadOnlyList<Guid> PersonIds { get; init; } = [];

    /// <summary>登録者の範囲の外で、相手側の同意が要る組織。</summary>
    public IReadOnlyList<Guid> ConsentOrgUnitIds { get; init; } = [];

    public Guid? ConsentedBy { get; init; }

    public DateTimeOffset? ConsentedAt { get; init; }

    public Guid? AppliedBy { get; init; }

    public DateTimeOffset? AppliedAt { get; init; }

    public Guid? WithdrawnBy { get; init; }

    public DateTimeOffset? WithdrawnAt { get; init; }

    public Guid? ClosedBy { get; init; }

    public DateTimeOffset? ClosedAt { get; init; }

    /// <summary>変更提案（反映の順）。</summary>
    public IReadOnlyList<ChangeProposalRecord> Proposals { get; init; } = [];

    /// <summary>期限を過ぎた下書き・同意待ちか。</summary>
    public bool IsExpiredAt(DateTimeOffset now) => Status is FieldRegistrationStatus.Draft or FieldRegistrationStatus.PendingConsent && ExpiresAt <= now;
}

/// <summary>直接登録の一覧の絞り込み。</summary>
/// <param name="Mine">自分が登録したものだけ。false なら閲覧者に見えるすべて（自分の範囲の登録を含む）。</param>
/// <param name="Statuses">状態で絞る（空ならすべて）。</param>
public sealed record FieldRegistrationQuery(bool Mine, IReadOnlyCollection<FieldRegistrationStatus> Statuses, int Limit = 100);

/// <summary>直接登録の保存（Infrastructure が実装する。0021）。最終的な線（誰がどこに書けるか・状態の遷移）は DB が引く。</summary>
public interface IFieldRegistrationStore
{
    /// <summary>
    /// 登録（下書き）と変更提案を保存する。<paramref name="next"/> が <see cref="FieldRegistrationStatus.Applied"/> なら同じトランザクションで反映し、
    /// <see cref="FieldRegistrationStatus.PendingConsent"/> なら同意待ちにする。
    /// </summary>
    /// <exception cref="ProposalConflictException">対象のデータが変わっていた・他の変更と衝突した。</exception>
    /// <exception cref="FieldManagementException">DB が範囲の外の書き込みを拒否した（<see cref="FieldManagementError.Forbidden"/>）。</exception>
    Task SaveAsync(ViewerContext viewer, FieldRegistration registration, FieldRegistrationStatus next, DateOnly today, CancellationToken cancellationToken);

    /// <summary>
    /// 下書き・同意待ちの登録を反映するか同意待ちにする（1 トランザクション）。<paramref name="consent"/> なら閲覧者を同意者として記録してから反映する。
    /// 期待した状態でなければ false。
    /// </summary>
    Task<bool> AdvanceAsync(
        ViewerContext viewer,
        FieldRegistration registration,
        FieldRegistrationStatus next,
        bool consent,
        DateOnly today,
        CancellationToken cancellationToken);

    /// <summary>反映済みの登録を取り消す（書いた行の記録期間を閉じ、閉じた行を元に戻す）。反映済みでなければ false。</summary>
    /// <exception cref="ProposalConflictException">登録の後に同じ行が変わっていた。</exception>
    Task<bool> WithdrawAsync(ViewerContext viewer, Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>下書き・同意待ちの登録を破棄（閲覧者が）・期限切れ（<paramref name="expired"/>）にする。提案は却下にする。対象の状態でなければ false。</summary>
    Task<bool> CloseAsync(ViewerContext viewer, Guid id, bool expired, DateTimeOffset now, CancellationToken cancellationToken);

    Task<FieldRegistration?> FindAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken);

    /// <summary>閲覧者に見える登録（新しい順。提案は含めない）。</summary>
    Task<IReadOnlyList<FieldRegistration>> ListAsync(ViewerContext viewer, FieldRegistrationQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// その日に有効な公開側の行のうち、直接登録が書いたものの対象（取込の管理元 both の判定に使う。人事管理者の閲覧者）。
    /// </summary>
    Task<FieldWrittenTargets> FieldWrittenAsync(ViewerContext viewer, DateOnly date, CancellationToken cancellationToken);
}

/// <summary>その日に有効な行を直接登録が書いた対象。</summary>
/// <param name="OrgUnits">組織の版。</param>
/// <param name="People">人の版。</param>
/// <param name="Assignments">主務の所属（人）。</param>
public sealed record FieldWrittenTargets(IReadOnlySet<Guid> OrgUnits, IReadOnlySet<Guid> People, IReadOnlySet<Guid> Assignments)
{
    public static FieldWrittenTargets None { get; } = new(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());
}
