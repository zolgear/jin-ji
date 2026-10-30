using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.FieldManagement;

/// <summary>
/// 現場・IT 部門の直接登録の土台（ADR-0013 決定 2・3）: 領域ごとの管理元の起案と承認、担当の指名、範囲の判定。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>管理元は書き込める人を変える設定なので、起案 → 別人の承認で反映する（職務分掌。ADR-0008 決定 7。DB の CHECK 制約でも止める）。
/// 起案・承認は人事管理者か組織管理者の「人」だけ（AI エージェントは不可。ADR-0013 決定 4）</item>
/// <item>担当の指名・取り消しは、その組織の組織長としての範囲（担当としての範囲では指名できない＝再指名の禁止）か、人事管理者・組織管理者の「人」。
/// 自分は指名できない。期間は今日以降に始まり、<see cref="MaxGrantDays"/> 日まで</item>
/// <item>範囲の判定（<see cref="ScopeAsync"/>）は DB の jinji_viewer_scope_covers() と同じ規則（<see cref="RegistrationScope"/>）。
/// 直接登録はアプリと RLS の両方で同じ線を引く</item>
/// <item>操作はすべて監査ログに残す（拒否・失敗も）</item>
/// </list>
/// </remarks>
public sealed class FieldManagementService
{
    public const string ProposeModeAction = "field.domain.propose";
    public const string ApproveModeAction = "field.domain.approve";
    public const string RejectModeAction = "field.domain.reject";
    public const string GrantEditorAction = "field.editor.grant";
    public const string RevokeEditorAction = "field.editor.revoke";

    /// <summary>メモの上限（DB の CHECK 制約と同じ）。</summary>
    public const int MaxNoteLength = 500;

    /// <summary>指名の期間の上限（日数。ADR-0013 の未決の案: 1 年）。</summary>
    public const int MaxGrantDays = 366;

    private const string SettingTarget = "field_domain_setting";
    private const string GrantTarget = "org_editor_grant";

    private readonly IFieldManagementStore _store;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;

    public FieldManagementService(IFieldManagementStore store, IOrgSnapshotProvider snapshots, AuditWriter audit, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _snapshots = snapshots;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    /// <summary>領域ごとの今の管理元（承認済みの行。無ければ <see cref="FieldManagementMode.Hr"/>）。</summary>
    public async Task<IReadOnlyDictionary<FieldDomain, FieldManagementMode>> ModesAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var approved = (await _store.ListSettingsAsync(viewer, cancellationToken))
            .Where(s => s.Status == FieldDomainSettingStatus.Approved)
            .ToDictionary(s => s.Domain, s => s.Mode);
        return FieldDomains.All.ToDictionary(d => d, d => approved.GetValueOrDefault(d, FieldManagementMode.Hr));
    }

    /// <summary>管理元の起案の一覧（新しい順）。</summary>
    public Task<IReadOnlyList<FieldDomainSetting>> ListSettingsAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return _store.ListSettingsAsync(viewer, cancellationToken);
    }

    /// <summary>管理元を起案する（人事管理者か組織管理者の人）。</summary>
    public async Task<FieldDomainSetting> ProposeModeAsync(
        ViewerContext viewer,
        FieldDomain domain,
        FieldManagementMode mode,
        string? note,
        CancellationToken cancellationToken)
    {
        await RequireFieldAdminUserAsync(viewer, ProposeModeAction, null, cancellationToken);
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmed is not null && (trimmed.Length > MaxNoteLength || trimmed.Any(char.IsControl)))
        {
            throw Invalid($"メモは {MaxNoteLength} 文字以内で、改行などの制御文字を含めないでください。");
        }

        var current = (await ModesAsync(viewer, cancellationToken))[domain];
        if (current == mode)
        {
            throw Invalid($"今の管理元と同じです（{FieldDomains.Key(mode)}）。");
        }

        var setting = new FieldDomainSetting(
            EntityId.New(),
            domain,
            mode,
            FieldDomainSettingStatus.Proposed,
            trimmed,
            viewer.PersonId!.Value,
            _timeProvider.GetUtcNow(),
            null,
            null,
            null);
        if (!await _store.InsertSettingAsync(viewer, setting, cancellationToken))
        {
            await _audit.RecordAsync(viewer, ProposeModeAction, AuditOutcome.Failed, [], cancellationToken, SettingTarget, setting.Id);
            throw new FieldManagementException(FieldManagementError.Conflict, "この領域には承認待ちの起案があります。先にそれを承認か却下してください。");
        }

        await _audit.RecordAsync(viewer, ProposeModeAction, AuditOutcome.Success, [], cancellationToken, SettingTarget, setting.Id);
        return setting;
    }

    /// <summary>管理元の起案を承認・却下する（人事管理者か組織管理者の人。起案者本人は不可）。</summary>
    public async Task<FieldDomainSetting> DecideModeAsync(ViewerContext viewer, Guid id, bool approve, CancellationToken cancellationToken)
    {
        var action = approve ? ApproveModeAction : RejectModeAction;
        await RequireFieldAdminUserAsync(viewer, action, id, cancellationToken);
        var setting = await _store.FindSettingAsync(viewer, id, cancellationToken)
            ?? throw new FieldManagementException(FieldManagementError.NotFound, "起案が見つかりません。");
        if (setting.ProposedBy == viewer.PersonId)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, SettingTarget, id);
            throw new FieldManagementException(FieldManagementError.Forbidden, "起案者は自分の起案を承認・却下できません（職務分掌）。");
        }

        if (setting.Status != FieldDomainSettingStatus.Proposed)
        {
            throw new FieldManagementException(FieldManagementError.Conflict, "この起案は既に承認・却下されています。");
        }

        var now = _timeProvider.GetUtcNow();
        if (!await _store.DecideSettingAsync(viewer, id, approve, now, cancellationToken))
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, [], cancellationToken, SettingTarget, id);
            throw new FieldManagementException(FieldManagementError.Conflict, "起案の状態が変わりました。やり直してください。");
        }

        await _audit.RecordAsync(viewer, action, AuditOutcome.Success, [], cancellationToken, SettingTarget, id);
        return setting with
        {
            Status = approve ? FieldDomainSettingStatus.Approved : FieldDomainSettingStatus.Rejected,
            DecidedBy = viewer.PersonId,
            DecidedAt = now,
        };
    }

    /// <summary>担当の指名の一覧（取り消し済みを含む。新しい順）。</summary>
    public Task<IReadOnlyList<OrgEditorGrant>> ListGrantsAsync(ViewerContext viewer, Guid? orgUnitId, Guid? personId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return _store.ListGrantsAsync(viewer, orgUnitId, personId, cancellationToken);
    }

    /// <summary>担当を指名する。期間は [<paramref name="from"/>, <paramref name="until"/>)。</summary>
    public async Task<OrgEditorGrant> GrantEditorAsync(
        ViewerContext viewer,
        Guid orgUnitId,
        Guid personId,
        DateOnly from,
        DateOnly until,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var today = BusinessDate.Today(_timeProvider);
        await RequireUserAsync(viewer, GrantEditorAction, [personId], null, cancellationToken);
        if (from < today)
        {
            throw Invalid("指名の期間は今日以降に始めてください。");
        }

        if (until <= from || until.DayNumber - from.DayNumber > MaxGrantDays)
        {
            throw Invalid($"指名の期間は 1〜{MaxGrantDays} 日にしてください（終わりの日は含みません）。");
        }

        if (personId == viewer.PersonId)
        {
            await _audit.RecordAsync(viewer, GrantEditorAction, AuditOutcome.Denied, [personId], cancellationToken, GrantTarget);
            throw new FieldManagementException(FieldManagementError.Forbidden, "自分を指名できません。");
        }

        var scope = await ScopeAsync(viewer, from, cancellationToken);
        var snapshot = await _snapshots.LoadAsync(viewer, from, today, cancellationToken);
        if (snapshot.FindUnit(orgUnitId) is null || snapshot.FindPerson(personId) is null)
        {
            await _audit.RecordAsync(viewer, GrantEditorAction, AuditOutcome.Failed, [personId], cancellationToken, GrantTarget);
            throw new FieldManagementException(FieldManagementError.NotFound, "組織か人が見つかりません。");
        }

        if (!scope.CanGrantEditors(orgUnitId))
        {
            await _audit.RecordAsync(viewer, GrantEditorAction, AuditOutcome.Denied, [personId], cancellationToken, GrantTarget);
            throw new FieldManagementException(FieldManagementError.Forbidden, "この組織の担当を指名できるのは、組織長（上位の組織長を含む）・組織管理者・人事管理者だけです。");
        }

        var grant = new OrgEditorGrant(EntityId.New(), orgUnitId, personId, from, until, viewer.PersonId!.Value, _timeProvider.GetUtcNow(), null, null);
        if (!await _store.InsertGrantAsync(viewer, grant, cancellationToken))
        {
            await _audit.RecordAsync(viewer, GrantEditorAction, AuditOutcome.Failed, [personId], cancellationToken, GrantTarget, grant.Id);
            throw new FieldManagementException(FieldManagementError.Conflict, "この人は、この組織の担当に同じ期間で既に指名されています。");
        }

        await _audit.RecordAsync(viewer, GrantEditorAction, AuditOutcome.Success, [personId], cancellationToken, GrantTarget, grant.Id);
        return grant;
    }

    /// <summary>担当の指名を取り消す（今日の時点で組織長としての範囲を持つ人・組織管理者・人事管理者）。</summary>
    public async Task<OrgEditorGrant> RevokeEditorAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireUserAsync(viewer, RevokeEditorAction, [], id, cancellationToken);
        var grant = await _store.FindGrantAsync(viewer, id, cancellationToken)
            ?? throw new FieldManagementException(FieldManagementError.NotFound, "指名が見つかりません。");
        if (grant.RevokedAt is not null)
        {
            throw new FieldManagementException(FieldManagementError.Conflict, "この指名は既に取り消されています。");
        }

        var scope = await ScopeAsync(viewer, BusinessDate.Today(_timeProvider), cancellationToken);
        if (!scope.CanGrantEditors(grant.OrgUnitId))
        {
            await _audit.RecordAsync(viewer, RevokeEditorAction, AuditOutcome.Denied, [grant.PersonId], cancellationToken, GrantTarget, id);
            throw new FieldManagementException(FieldManagementError.Forbidden, "この組織の指名を取り消せるのは、組織長（上位の組織長を含む）・組織管理者・人事管理者だけです。");
        }

        var now = _timeProvider.GetUtcNow();
        if (!await _store.RevokeGrantAsync(viewer, id, now, cancellationToken))
        {
            await _audit.RecordAsync(viewer, RevokeEditorAction, AuditOutcome.Failed, [grant.PersonId], cancellationToken, GrantTarget, id);
            throw new FieldManagementException(FieldManagementError.Conflict, "指名の状態が変わりました。やり直してください。");
        }

        await _audit.RecordAsync(viewer, RevokeEditorAction, AuditOutcome.Success, [grant.PersonId], cancellationToken, GrantTarget, id);
        return grant with { RevokedBy = viewer.PersonId, RevokedAt = now };
    }

    /// <summary>
    /// 閲覧者（AI エージェントは委任元）の、その日の直接登録の範囲。人に紐付かない閲覧者は、人事管理者・組織管理者のロールが無ければ範囲を持たない。
    /// </summary>
    public async Task<RegistrationScope> ScopeAsync(ViewerContext viewer, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var person = viewer.EffectivePersonId;
        var snapshot = await _snapshots.LoadAsync(viewer, date, BusinessDate.Today(_timeProvider), cancellationToken);
        var grants = person is { } id && !viewer.ManagesAllOrgs
            ? await _store.ListGrantsAsync(viewer, null, id, cancellationToken)
            : [];
        return new RegistrationScope(snapshot, person, viewer.ManagesAllOrgs, grants);
    }

    private async Task RequireUserAsync(ViewerContext viewer, string action, IReadOnlyList<Guid> subjects, Guid? target, CancellationToken cancellationToken)
    {
        if (viewer.Actor != ActorType.User || viewer.PersonId is null)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, subjects, cancellationToken, GrantTarget, target);
            throw new FieldManagementException(FieldManagementError.Forbidden, "担当の指名・取り消しは、ログインした人だけが行えます（AI エージェントは不可）。");
        }
    }

    private async Task RequireFieldAdminUserAsync(ViewerContext viewer, string action, Guid? target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        if (viewer.Actor != ActorType.User || viewer.PersonId is null || !viewer.ManagesAllOrgs)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, SettingTarget, target);
            throw new FieldManagementException(FieldManagementError.Forbidden, "管理元の起案・承認は、ログインした人事管理者か組織管理者だけが行えます。");
        }
    }

    private static FieldManagementException Invalid(string message) => new(FieldManagementError.Invalid, message);
}
