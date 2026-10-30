using JinJi.Application.Audit;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.FieldManagement;

/// <summary>
/// 現場・IT 部門の直接登録（ADR-0013 決定 1・3〜7）: 登録・AI エージェントの下書きの確定・相手側の同意・有効日の前の取り消し・破棄。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>登録は承認を経ずに反映する。反映は取込の承認と同じ経路（変更提案・旧値の照合・段の検査・発令・変更フィード）を通る</item>
/// <item>AI エージェントは委任元の名義の下書きまで（決定 4）。反映するのは委任元の人の確定だけ</item>
/// <item>有効日は今日以降（遡及は組織管理者・人事管理者だけ）。1 回の登録で変える人は <see cref="MaxPeople"/> 人まで（同じく除く）</item>
/// <item>登録者の範囲の外の組織にかかる変更は同意待ちにし、その組織の範囲を持つ別の人の同意で反映する。下書き・同意待ちは
/// <see cref="PendingDays"/> 日で期限切れになる</item>
/// <item>取り消しは有効日の前だけ。登録者か、登録の組織すべての範囲を持つ人（組織管理者・人事管理者を含む）が行える</item>
/// <item>操作はすべて監査ログに残す（拒否・失敗も）。最終的な線（範囲・管理元・人の版の列・権限を生む変更）は DB も引く（0021）</item>
/// </list>
/// </remarks>
public sealed class FieldRegistrationService
{
    public const string RegisterAction = "field.registration.register";
    public const string ConfirmAction = "field.registration.confirm";
    public const string ConsentAction = "field.registration.consent";
    public const string WithdrawAction = "field.registration.withdraw";
    public const string DiscardAction = "field.registration.discard";

    /// <summary>1 回の登録で変える人の上限（ADR-0013 決定 7。組織管理者・人事管理者は除く）。</summary>
    public const int MaxPeople = 50;

    /// <summary>1 回の登録の変更の上限。</summary>
    public const int MaxChanges = 200;

    /// <summary>下書き・同意待ちの期限（日数。ADR-0013 の未決の案: 14 日）。</summary>
    public const int PendingDays = 14;

    private const string TargetType = "field_registration";

    private readonly IFieldRegistrationStore _store;
    private readonly FieldManagementService _management;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly IOrgDataChangeNotifier? _changes;

    public FieldRegistrationService(
        IFieldRegistrationStore store,
        FieldManagementService management,
        IOrgSnapshotProvider snapshots,
        AuditWriter audit,
        TimeProvider timeProvider,
        IOrgDataChangeNotifier? changes = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(management);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _management = management;
        _snapshots = snapshots;
        _audit = audit;
        _timeProvider = timeProvider;
        _changes = changes;
    }

    /// <summary>
    /// 直接登録する。人は範囲の中ならすぐに反映し（相手側の同意が要れば同意待ち）、AI エージェントは委任元の名義の下書きにする。
    /// </summary>
    public async Task<FieldRegistration> RegisterAsync(ViewerContext viewer, FieldRegistrationInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(input);
        if (viewer.Actor is not (ActorType.User or ActorType.Agent) || viewer.EffectivePersonId is not { } registrant)
        {
            await _audit.RecordAsync(viewer, RegisterAction, AuditOutcome.Denied, [], cancellationToken, TargetType);
            throw new FieldManagementException(FieldManagementError.Forbidden, "直接登録は、ログインした人か、人から委任された AI エージェント（下書き）だけが行えます。");
        }

        var today = BusinessDate.Today(_timeProvider);
        var now = _timeProvider.GetUtcNow();
        FieldRegistrationPlan plan;
        try
        {
            Validate(viewer, input, today);
            plan = await PlanAsync(viewer, registrant, input, today, cancellationToken);
        }
        catch (FieldManagementException ex)
        {
            await _audit.RecordAsync(viewer, RegisterAction, Outcome(ex), [], cancellationToken, TargetType);
            throw;
        }

        var registration = new FieldRegistration
        {
            Id = Guid.CreateVersion7(),
            Status = FieldRegistrationStatus.Draft,
            EffectiveDate = input.EffectiveDate,
            Note = Note(input.Note),
            RegisteredBy = registrant,
            RegisteredVia = viewer.Actor,
            RegisteredViaClientId = viewer.Actor == ActorType.Agent ? viewer.ClientId : null,
            CreatedAt = now,
            ExpiresAt = now.AddDays(PendingDays),
            OrgUnitIds = plan.OrgUnitIds,
            PersonIds = plan.PersonIds,
            ConsentOrgUnitIds = plan.ConsentOrgUnitIds,
            Proposals = [.. plan.Proposals.Select(p => p with { ProposedViaClientId = viewer.Actor == ActorType.Agent ? viewer.ClientId : null })],
        };

        var next = viewer.Actor == ActorType.Agent ? FieldRegistrationStatus.Draft
            : plan.ConsentOrgUnitIds.Count > 0 ? FieldRegistrationStatus.PendingConsent
            : FieldRegistrationStatus.Applied;
        await SaveAsync(viewer, RegisterAction, registration, plan.PersonIds, next == FieldRegistrationStatus.Applied, cancellationToken, () =>
            _store.SaveAsync(viewer, registration, next, today, cancellationToken));
        return registration with { Status = next };
    }

    /// <summary>AI エージェントの下書きを、委任元の人が確定する（範囲の中ならすぐに反映し、相手側の同意が要れば同意待ちにする）。</summary>
    public async Task<FieldRegistration> ConfirmAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var registration = await RequireAsync(viewer, ConfirmAction, id, FieldRegistrationStatus.Draft, cancellationToken);
        if (viewer.Actor != ActorType.User || viewer.PersonId != registration.RegisteredBy)
        {
            await Deny(viewer, ConfirmAction, registration, "下書きを確定できるのは、下書きの名義の人だけです（AI エージェントは確定できません）。", cancellationToken);
        }

        var today = BusinessDate.Today(_timeProvider);
        var scope = await _management.ScopeAsync(viewer, today, cancellationToken);
        var own = registration.OrgUnitIds.Except(registration.ConsentOrgUnitIds);
        if (!own.All(u => scope.Covers(u)))
        {
            await Deny(viewer, ConfirmAction, registration, "下書きの組織の範囲を、今は持っていません。", cancellationToken);
        }

        if (registration.EffectiveDate < today && !viewer.ManagesAllOrgs)
        {
            await Fail(viewer, ConfirmAction, registration, FieldManagementError.Invalid, "有効日を過ぎた下書きは確定できません。作り直してください。", cancellationToken);
        }

        var next = registration.ConsentOrgUnitIds.Count > 0 ? FieldRegistrationStatus.PendingConsent : FieldRegistrationStatus.Applied;
        await AdvanceAsync(viewer, ConfirmAction, registration, next, consent: false, today, cancellationToken);
        return registration with { Status = next };
    }

    /// <summary>同意待ちの登録に、相手側の組織の範囲を持つ人が同意して反映する。</summary>
    public async Task<FieldRegistration> ConsentAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var registration = await RequireAsync(viewer, ConsentAction, id, FieldRegistrationStatus.PendingConsent, cancellationToken);
        if (viewer.Actor != ActorType.User || viewer.PersonId is not { } person || person == registration.RegisteredBy)
        {
            await Deny(viewer, ConsentAction, registration, "同意は、登録者とは別の、ログインした人だけが行えます。", cancellationToken);
        }

        var today = BusinessDate.Today(_timeProvider);
        var scope = await _management.ScopeAsync(viewer, today, cancellationToken);
        if (!registration.ConsentOrgUnitIds.All(u => scope.Covers(u)))
        {
            await Deny(viewer, ConsentAction, registration, "同意が要る組織の範囲を持ちません。", cancellationToken);
        }

        await AdvanceAsync(viewer, ConsentAction, registration, FieldRegistrationStatus.Applied, consent: true, today, cancellationToken);
        return registration with { Status = FieldRegistrationStatus.Applied, ConsentedBy = viewer.PersonId };
    }

    /// <summary>反映済みの登録を、有効日の前に取り消す。</summary>
    public async Task<FieldRegistration> WithdrawAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var registration = await RequireAsync(viewer, WithdrawAction, id, FieldRegistrationStatus.Applied, cancellationToken);
        if (viewer.Actor != ActorType.User || viewer.PersonId is not { } person)
        {
            await Deny(viewer, WithdrawAction, registration, "取り消しは、ログインした人だけが行えます。", cancellationToken);
            throw new InvalidOperationException();
        }

        var today = BusinessDate.Today(_timeProvider);
        if (registration.EffectiveDate <= today)
        {
            await Fail(viewer, WithdrawAction, registration, FieldManagementError.Conflict, "有効日を迎えた登録は取り消せません。新しく登録し直してください。", cancellationToken);
        }

        if (person != registration.RegisteredBy && !viewer.ManagesAllOrgs)
        {
            var scope = await _management.ScopeAsync(viewer, today, cancellationToken);
            if (!registration.OrgUnitIds.All(u => scope.Covers(u)))
            {
                await Deny(viewer, WithdrawAction, registration, "取り消せるのは、登録者か、登録の組織すべての範囲を持つ人です。", cancellationToken);
            }
        }

        var now = _timeProvider.GetUtcNow();
        bool withdrawn;
        try
        {
            withdrawn = await _store.WithdrawAsync(viewer, id, now, cancellationToken);
        }
        catch (ProposalConflictException ex)
        {
            await _audit.RecordAsync(viewer, WithdrawAction, AuditOutcome.Failed, registration.PersonIds, cancellationToken, TargetType, id);
            throw new FieldManagementException(FieldManagementError.Conflict, ex.Message, ex);
        }
        catch (FieldManagementException)
        {
            await _audit.RecordAsync(viewer, WithdrawAction, AuditOutcome.Denied, registration.PersonIds, cancellationToken, TargetType, id);
            throw;
        }

        if (!withdrawn)
        {
            await Fail(viewer, WithdrawAction, registration, FieldManagementError.Conflict, "登録の状態が変わったので取り消せませんでした。", cancellationToken);
        }

        _changes?.NotifyChanged();
        await _audit.RecordAsync(viewer, WithdrawAction, AuditOutcome.Success, registration.PersonIds, cancellationToken, TargetType, id);
        return registration with { Status = FieldRegistrationStatus.Withdrawn, WithdrawnBy = person, WithdrawnAt = now };
    }

    /// <summary>下書き・同意待ちの登録を破棄する（登録者か、組織管理者・人事管理者の人）。</summary>
    public async Task<FieldRegistration> DiscardAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var registration = await FindVisibleAsync(viewer, DiscardAction, id, cancellationToken);
        if (registration.Status is not (FieldRegistrationStatus.Draft or FieldRegistrationStatus.PendingConsent))
        {
            await Fail(viewer, DiscardAction, registration, FieldManagementError.Conflict, "下書き・同意待ちの登録だけを破棄できます。", cancellationToken);
        }

        if (viewer.Actor != ActorType.User || viewer.PersonId is not { } person || (person != registration.RegisteredBy && !viewer.ManagesAllOrgs))
        {
            await Deny(viewer, DiscardAction, registration, "破棄できるのは、登録者か、組織管理者・人事管理者の人です。", cancellationToken);
            throw new InvalidOperationException();
        }

        var now = _timeProvider.GetUtcNow();
        if (!await _store.CloseAsync(viewer, id, expired: false, now, cancellationToken))
        {
            await Fail(viewer, DiscardAction, registration, FieldManagementError.Conflict, "登録の状態が変わったので破棄できませんでした。", cancellationToken);
        }

        await _audit.RecordAsync(viewer, DiscardAction, AuditOutcome.Success, registration.PersonIds, cancellationToken, TargetType, id);
        return registration with { Status = FieldRegistrationStatus.Discarded, ClosedBy = person, ClosedAt = now };
    }

    /// <summary>閲覧者に見える登録（変更提案を含む）。期限を過ぎた下書き・同意待ちは期限切れにしてから返す。</summary>
    public async Task<FieldRegistration> GetAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var registration = await _store.FindAsync(viewer, id, cancellationToken)
            ?? throw new FieldManagementException(FieldManagementError.NotFound, "登録が見つかりません。");
        return await ExpireIfDueAsync(viewer, registration, cancellationToken);
    }

    /// <summary>閲覧者に見える登録の一覧（新しい順。自分の登録・同意した登録・自分の範囲の組織の登録）。</summary>
    public async Task<IReadOnlyList<FieldRegistration>> ListAsync(ViewerContext viewer, FieldRegistrationQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(query);
        var registrations = await _store.ListAsync(viewer, query, cancellationToken);
        var result = new List<FieldRegistration>(registrations.Count);
        foreach (var registration in registrations)
        {
            result.Add(await ExpireIfDueAsync(viewer, registration, cancellationToken));
        }

        return result;
    }

    private async Task<FieldRegistration> ExpireIfDueAsync(ViewerContext viewer, FieldRegistration registration, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (!registration.IsExpiredAt(now) || viewer.Actor is not (ActorType.User or ActorType.System))
        {
            return registration;
        }

        return await _store.CloseAsync(viewer, registration.Id, expired: true, now, cancellationToken)
            ? registration with { Status = FieldRegistrationStatus.Expired, ClosedAt = now }
            : registration;
    }

    private void Validate(ViewerContext viewer, FieldRegistrationInput input, DateOnly today)
    {
        if (input.Changes is null || input.Changes.Count == 0 || input.Changes.Count > MaxChanges)
        {
            throw new FieldManagementException(FieldManagementError.Invalid, $"変更を 1〜{MaxChanges} 件指定してください。");
        }

        if (input.EffectiveDate < today && !viewer.ManagesAllOrgs)
        {
            throw new FieldManagementException(FieldManagementError.Invalid, "有効日は今日以降にしてください（過去の日付の訂正は組織管理者・人事管理者だけが行えます）。");
        }

        _ = Note(input.Note);
    }

    private async Task<FieldRegistrationPlan> PlanAsync(
        ViewerContext viewer,
        Guid registrant,
        FieldRegistrationInput input,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var modes = await _management.ModesAsync(viewer, cancellationToken);
        var scope = await _management.ScopeAsync(viewer, today, cancellationToken);
        var snapshot = await _snapshots.LoadAsync(viewer, input.EffectiveDate, today, cancellationToken);
        var plan = new FieldRegistrationPlanner(snapshot, scope, modes, registrant).Plan(input.Changes);
        if (plan.PersonIds.Count > MaxPeople && !viewer.ManagesAllOrgs)
        {
            throw new FieldManagementException(
                FieldManagementError.Invalid,
                $"1 回の登録で変えられる人は {MaxPeople} 人までです（{plan.PersonIds.Count} 人）。大きな改編は組織管理者・人事管理者が行います。");
        }

        // 組織の提案は、取込の承認と同じく段の定義と段の順位を検査する
        foreach (var proposal in plan.Proposals.Where(p => p.TargetKind is ProposalTargetKind.OrgUnit or ProposalTargetKind.OrgUnitVersion
                     && p.Operation != ProposalOperation.Abolish))
        {
            try
            {
                await ProposalReviewService.ValidateOrgAsync(_snapshots, viewer, proposal, today, cancellationToken);
            }
            catch (OrgLevelValidationException ex)
            {
                throw new FieldManagementException(FieldManagementError.Invalid, ex.Message, ex);
            }
        }

        return plan;
    }

    private async Task SaveAsync(
        ViewerContext viewer,
        string action,
        FieldRegistration registration,
        IReadOnlyList<Guid> subjects,
        bool applied,
        CancellationToken cancellationToken,
        Func<Task> save)
    {
        try
        {
            await save();
        }
        catch (ProposalConflictException ex)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, subjects, cancellationToken, TargetType, registration.Id);
            throw new FieldManagementException(FieldManagementError.Conflict, ex.Message, ex);
        }
        catch (FieldManagementException ex)
        {
            await _audit.RecordAsync(viewer, action, Outcome(ex), subjects, cancellationToken, TargetType, registration.Id);
            throw;
        }

        if (applied)
        {
            _changes?.NotifyChanged();
        }

        await _audit.RecordAsync(viewer, action, AuditOutcome.Success, subjects, cancellationToken, TargetType, registration.Id);
    }

    private async Task AdvanceAsync(
        ViewerContext viewer,
        string action,
        FieldRegistration registration,
        FieldRegistrationStatus next,
        bool consent,
        DateOnly today,
        CancellationToken cancellationToken)
    {
        var advanced = true;
        await SaveAsync(viewer, action, registration, registration.PersonIds, next == FieldRegistrationStatus.Applied, cancellationToken, async () =>
            advanced = await _store.AdvanceAsync(viewer, registration, next, consent, today, cancellationToken));
        if (!advanced)
        {
            await Fail(viewer, action, registration, FieldManagementError.Conflict, "登録の状態が変わったので進められませんでした。", cancellationToken);
        }
    }

    private async Task<FieldRegistration> RequireAsync(
        ViewerContext viewer,
        string action,
        Guid id,
        FieldRegistrationStatus expected,
        CancellationToken cancellationToken)
    {
        var registration = await FindVisibleAsync(viewer, action, id, cancellationToken);
        if (registration.Status != expected)
        {
            await Fail(viewer, action, registration, FieldManagementError.Conflict, $"この操作は {Code(expected)} の登録にだけ行えます（今は {Code(registration.Status)}）。", cancellationToken);
        }

        return registration;
    }

    private async Task<FieldRegistration> FindVisibleAsync(ViewerContext viewer, string action, Guid id, CancellationToken cancellationToken)
    {
        var registration = await _store.FindAsync(viewer, id, cancellationToken);
        if (registration is null)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new FieldManagementException(FieldManagementError.NotFound, "登録が見つかりません。");
        }

        return await ExpireIfDueAsync(viewer, registration, cancellationToken);
    }

    private async Task Deny(ViewerContext viewer, string action, FieldRegistration registration, string message, CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, registration.PersonIds, cancellationToken, TargetType, registration.Id);
        throw new FieldManagementException(FieldManagementError.Forbidden, message);
    }

    private async Task Fail(ViewerContext viewer, string action, FieldRegistration registration, FieldManagementError error, string message, CancellationToken cancellationToken)
    {
        await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, registration.PersonIds, cancellationToken, TargetType, registration.Id);
        throw new FieldManagementException(error, message);
    }

    private static AuditOutcome Outcome(FieldManagementException ex) =>
        ex.Error == FieldManagementError.Forbidden ? AuditOutcome.Denied : AuditOutcome.Failed;

    private static string? Note(string? note)
    {
        var value = note?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.Length > FieldManagementService.MaxNoteLength || value.Any(char.IsControl)
            ? throw new FieldManagementException(FieldManagementError.Invalid, $"メモは {FieldManagementService.MaxNoteLength} 文字以内で、改行などの制御文字を含めずに指定してください。")
            : value;
    }

    /// <summary>状態のコード値（DB・API の値）。</summary>
    public static string Code(FieldRegistrationStatus status) => status switch
    {
        FieldRegistrationStatus.Draft => "draft",
        FieldRegistrationStatus.PendingConsent => "pending_consent",
        FieldRegistrationStatus.Applied => "applied",
        FieldRegistrationStatus.Withdrawn => "withdrawn",
        FieldRegistrationStatus.Discarded => "discarded",
        FieldRegistrationStatus.Expired => "expired",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    /// <summary>状態のコード値を読む。</summary>
    public static bool TryParseStatus(string? code, out FieldRegistrationStatus status)
    {
        foreach (var value in Enum.GetValues<FieldRegistrationStatus>())
        {
            if (string.Equals(Code(value), code, StringComparison.Ordinal))
            {
                status = value;
                return true;
            }
        }

        status = default;
        return false;
    }
}
