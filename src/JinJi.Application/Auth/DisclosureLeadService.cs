using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Auth;

/// <summary>先行提供の日数の起案の状態（api_client_disclosure_lead.status）。</summary>
public enum DisclosureLeadStatus
{
    Proposed,
    Approved,
    Rejected,
    Superseded,
}

/// <summary>先行提供の日数の起案（1 行）。</summary>
/// <param name="ProposedByKind">person（UI の人事管理者）/ cli（管理 CLI。起案者は指定された人事管理者）。</param>
public sealed record DisclosureLead(
    Guid Id,
    Guid ClientRef,
    int LeadDays,
    DisclosureLeadStatus Status,
    string? Note,
    string ProposedByKind,
    Guid ProposedBy,
    DateTimeOffset ProposedAt,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt,
    DateTimeOffset? SupersededAt);

/// <summary>先行提供の日数の起案と承認の保存（Infrastructure が実装する。0016）。</summary>
public interface IDisclosureLeadStore
{
    /// <summary>起案の一覧（新しい順）。<paramref name="clientRef"/> を指定すればそのクライアントだけ。</summary>
    Task<IReadOnlyList<DisclosureLead>> ListDisclosureLeadsAsync(AuthActor actor, Guid? clientRef, CancellationToken cancellationToken);

    Task<DisclosureLead?> FindDisclosureLeadAsync(AuthActor actor, Guid id, CancellationToken cancellationToken);

    /// <summary>起案を追加する。同じクライアントの起案中の行が既にあれば false。</summary>
    Task<bool> InsertDisclosureLeadAsync(AuthActor actor, DisclosureLead proposal, CancellationToken cancellationToken);

    /// <summary>
    /// 起案を承認・却下する（決めた人は <paramref name="actor"/>）。承認なら同じクライアントの承認済みの行を置き換え済みにしてから承認する（1 トランザクション）。
    /// 起案中でなくなっていれば false。
    /// </summary>
    Task<bool> DecideDisclosureLeadAsync(AuthActor actor, Guid id, bool approve, DateTimeOffset decidedAt, CancellationToken cancellationToken);
}

/// <summary>
/// 社内システムのクライアントごとの先行提供の日数（有効日の N 日前から将来の予定を見せる。アーキテクチャ概要 §4・§9「先行提供」）の起案と承認。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>内示の秘匿を緩める操作なので、段の定義・ポリシー・同期計画と同じく起案 → 別人の承認で反映する（職務分掌。起案者は承認・却下できない。DB の CHECK 制約でも止める）</item>
/// <item>起案は UI でログインした人事管理者か、管理 CLI（起案者として人事管理者の person を指定する。同期計画の --executed-by と同じ）。
/// 承認・却下は UI でログインした人事管理者だけ（jin-ji 自身・管理 CLI は人を確かめられないので承認できない。同期計画の承認と同じ）</item>
/// <item>対象は client credentials のクライアント（社内システム）だけ。人の委任（認可コード）には効かない（トークンの検証で社内システムにだけ渡す）</item>
/// <item>日数を減らす（0 に戻す）のも同じ手順。すぐに止めたいときはクライアントを停止する（停止は承認なしで次の要求から効く）</item>
/// <item>承認は同じプロセスのトークンのキャッシュを捨てて次の要求から効かせる（他のプロセスはキャッシュの秒数以内）。操作はすべて監査ログに残す</item>
/// </list>
/// </remarks>
public sealed class DisclosureLeadService
{
    /// <summary>メモの上限（DB の CHECK 制約と同じ）。</summary>
    public const int MaxNoteLength = 500;

    private readonly IDisclosureLeadStore _store;
    private readonly IOAuthStore _clients;
    private readonly IAuthStore _auth;
    private readonly AccessTokenValidator _validator;
    private readonly AuthAuditor _audit;
    private readonly TimeProvider _timeProvider;

    public DisclosureLeadService(
        IDisclosureLeadStore store,
        IOAuthStore clients,
        IAuthStore auth,
        AccessTokenValidator validator,
        AuthAuditor audit,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _clients = clients;
        _auth = auth;
        _validator = validator;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    /// <summary>起案の一覧（新しい順）。</summary>
    public Task<IReadOnlyList<DisclosureLead>> ListAsync(AuthActor actor, Guid? clientRef, CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        return _store.ListDisclosureLeadsAsync(actor, clientRef, cancellationToken);
    }

    /// <summary>
    /// 先行提供の日数を起案する。<paramref name="proposedBy"/> は管理 CLI のときの起案者（人事管理者の person）で、UI の人事管理者は本人が起案者になる。
    /// </summary>
    public async Task<DisclosureLead> ProposeAsync(
        AuthActor actor,
        Guid clientRef,
        int leadDays,
        string? note,
        Guid? proposedBy,
        string? requestId,
        CancellationToken cancellationToken)
    {
        RequireOperator(actor);
        var proposer = actor.Kind == AuthActorKind.HrAdmin
            ? actor.PersonId!.Value
            : proposedBy ?? throw Invalid("管理 CLI の起案には、起案者（人事管理者の person ID）を指定してください。");
        if (actor.Kind == AuthActorKind.HrAdmin && proposedBy is { } other && other != proposer)
        {
            throw new AuthAdminException(AuthAdminError.Forbidden, "UI の起案者は本人だけです。");
        }

        if (leadDays is < 0 or > ViewerContext.MaxDisclosureLeadDays)
        {
            throw Invalid($"先行提供の日数は 0〜{ViewerContext.MaxDisclosureLeadDays} で指定してください。");
        }

        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is not null && (trimmed.Length > MaxNoteLength || trimmed.Any(char.IsControl)))
        {
            throw Invalid($"メモは {MaxNoteLength} 文字以内で、改行などの制御文字を含めないでください。");
        }

        var client = await _clients.FindClientByRefAsync(clientRef, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, "クライアントが見つかりません。");
        if (!client.Allows(OAuthGrantTypes.ClientCredentials))
        {
            throw Invalid("先行提供は client credentials のクライアント（社内システム）にだけ設定できます（人の委任には効きません）。");
        }

        if (client.DisclosureLeadDays == leadDays)
        {
            throw Invalid($"今の日数と同じです（{leadDays} 日）。");
        }

        if (actor.Kind != AuthActorKind.HrAdmin && !(await _auth.GetRolesAsync(proposer, cancellationToken)).Contains(Roles.HrAdmin))
        {
            throw new AuthAdminException(AuthAdminError.Forbidden, "起案者は人事管理者にしてください。");
        }

        var proposal = new DisclosureLead(
            EntityId.New(),
            client.Id,
            leadDays,
            DisclosureLeadStatus.Proposed,
            trimmed,
            actor.Kind == AuthActorKind.HrAdmin ? "person" : "cli",
            proposer,
            _timeProvider.GetUtcNow(),
            null,
            null,
            null);
        if (!await _store.InsertDisclosureLeadAsync(actor, proposal, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, "このクライアントには承認待ちの起案があります。先にそれを承認か却下してください。");
        }

        await AuditAsync(actor, OAuthAuditActions.DisclosureLeadPropose, proposal.Id, [], requestId, cancellationToken);
        return proposal;
    }

    /// <summary>起案を承認・却下する（UI でログインした人事管理者だけ。起案者本人は不可）。</summary>
    public async Task<DisclosureLead> DecideAsync(AuthActor actor, Guid id, bool approve, string? requestId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var action = approve ? OAuthAuditActions.DisclosureLeadApprove : OAuthAuditActions.DisclosureLeadReject;
        if (actor.Kind != AuthActorKind.HrAdmin)
        {
            await AuditAsync(actor, action, id, [], requestId, cancellationToken, AuditOutcome.Denied);
            throw new AuthAdminException(AuthAdminError.Forbidden, "先行提供の承認・却下は、UI でログインした人事管理者だけが行えます。");
        }

        var proposal = await _store.FindDisclosureLeadAsync(actor, id, cancellationToken)
            ?? throw new AuthAdminException(AuthAdminError.NotFound, "起案が見つかりません。");
        if (proposal.ProposedBy == actor.PersonId)
        {
            await AuditAsync(actor, action, id, [], requestId, cancellationToken, AuditOutcome.Denied);
            throw new AuthAdminException(AuthAdminError.Forbidden, "起案者は自分の起案を承認・却下できません（職務分掌）。");
        }

        if (proposal.Status != DisclosureLeadStatus.Proposed)
        {
            throw new AuthAdminException(AuthAdminError.Conflict, "この起案は既に承認・却下されています。");
        }

        var now = _timeProvider.GetUtcNow();
        if (!await _store.DecideDisclosureLeadAsync(actor, id, approve, now, cancellationToken))
        {
            throw new AuthAdminException(AuthAdminError.Conflict, "起案の状態が変わりました。やり直してください。");
        }

        if (approve)
        {
            _validator.Invalidate();
        }

        await AuditAsync(actor, action, id, [], requestId, cancellationToken);
        return proposal with
        {
            Status = approve ? DisclosureLeadStatus.Approved : DisclosureLeadStatus.Rejected,
            DecidedBy = actor.PersonId,
            DecidedAt = now,
        };
    }

    private static void RequireOperator(AuthActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Kind is not (AuthActorKind.Cli or AuthActorKind.HrAdmin))
        {
            throw new AuthAdminException(AuthAdminError.Forbidden, "先行提供の設定は管理 CLI か、UI でログインした人事管理者だけが扱えます。");
        }
    }

    private static AuthAdminException Invalid(string message) => new(AuthAdminError.Invalid, message);

    private Task AuditAsync(
        AuthActor actor,
        string action,
        Guid target,
        IReadOnlyList<Guid> subjects,
        string? requestId,
        CancellationToken cancellationToken,
        AuditOutcome outcome = AuditOutcome.Success) =>
        _audit.RecordAsync(AuthAuditor.ForOperator(actor, action, outcome, OAuthAuditTargets.DisclosureLead, target, subjects, requestId), cancellationToken);
}
