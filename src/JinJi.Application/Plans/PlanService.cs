using System.Globalization;
using System.Text;
using JinJi.Application.Audit;
using JinJi.Application.FieldManagement;
using JinJi.Application.Groups;
using JinJi.Application.People;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Plans;

/// <summary>素案の設定（appsettings の <c>JinJi:Plans</c>）。</summary>
public sealed record PlanOptions
{
    public const string SectionName = "JinJi:Plans";

    /// <summary>申請版の承認の結果を記録できる社内システム（ワークフロー製品）のクライアント ID。空なら人だけが記録する。</summary>
    public IReadOnlyList<string> ApprovalClients { get; init; } = [];
}

/// <summary>素案から直接登録にした結果。</summary>
/// <param name="Registration">直接登録（組織・人の変更が無ければ null）。</param>
/// <param name="GroupChanges">反映したグループの加入・脱退の数。</param>
public sealed record PlanRegistrationResult(FieldRegistration? Registration, int GroupChanges);

/// <summary>
/// 素案・申請版・提案資料（ADR-0013 決定 8・9）: 作成・更新・共有・シミュレーション・申請版の固定・承認の記録・直接登録への変換・CSV の書き出し。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>素案は公開されない作業領域で、範囲や管理元にかかわらず誰でも作れる。AI エージェントも委任元の名義で書ける</item>
/// <item>見えるのは作成者と共有した人だけ。シミュレーションの人の項目は、閲覧者自身の公開範囲で投影する（素案を通して見えない項目を見せない）</item>
/// <item>申請版の承認の記録は、申請版の組織すべての範囲を持つ人（組織管理者・人事管理者を含む）か、設定で指定した社内システムだけ</item>
/// <item>直接登録への変換は、操作した人の範囲で直接登録の規則（<see cref="FieldRegistrationService"/>）に通す。申請版からは承認済みのものだけ</item>
/// <item>操作は監査ログに残す（拒否・失敗も）</item>
/// </list>
/// </remarks>
public sealed class PlanService
{
    public const string ReadAction = "plan.read";
    public const string WriteAction = "plan.write";
    public const string ShareAction = "plan.share";
    public const string DeleteAction = "plan.delete";
    public const string FreezeAction = "plan.version.create";
    public const string ApprovalAction = "plan.version.approval";
    public const string RegisterAction = "plan.register";
    public const string ExportAction = "plan.export";

    private const string TargetType = "plan";

    private readonly IPlanStore _store;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly PersonDirectoryService _people;
    private readonly FieldManagementService _scopes;
    private readonly FieldRegistrationService _registrations;
    private readonly GroupService _groups;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;
    private readonly PlanOptions _options;

    public PlanService(
        IPlanStore store,
        IOrgSnapshotProvider snapshots,
        PersonDirectoryService people,
        FieldManagementService scopes,
        FieldRegistrationService registrations,
        GroupService groups,
        AuditWriter audit,
        TimeProvider timeProvider,
        PlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _snapshots = snapshots;
        _people = people;
        _scopes = scopes;
        _registrations = registrations;
        _groups = groups;
        _audit = audit;
        _timeProvider = timeProvider;
        _options = options ?? new PlanOptions();
    }

    public async Task<IReadOnlyList<Plan>> ListAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var plans = await _store.ListAsync(viewer, cancellationToken);
        await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, [], cancellationToken, TargetType);
        return plans;
    }

    public async Task<Plan> GetAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var plan = await FindAsync(viewer, ReadAction, id, cancellationToken);
        await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, [], cancellationToken, TargetType, id);
        return plan;
    }

    /// <summary>素案を作る（人・AI エージェント。作成者は本人か委任元）。</summary>
    public async Task<Plan> CreateAsync(ViewerContext viewer, PlanInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(input);
        var owner = await RequireAuthorAsync(viewer, WriteAction, null, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        Plan plan;
        try
        {
            var valid = Validate(input);
            plan = new Plan
            {
                Id = Guid.CreateVersion7(),
                Title = valid.Title,
                Note = valid.Note,
                BaseDate = valid.BaseDate,
                EffectiveDate = valid.EffectiveDate,
                Changes = valid.Changes,
                Owner = owner,
                CreatedVia = viewer.Actor,
                CreatedAt = now,
                UpdatedAt = now,
            };
        }
        catch (PlanException ex)
        {
            await _audit.RecordAsync(viewer, WriteAction, Outcome(ex), [], cancellationToken, TargetType);
            throw;
        }

        await _store.InsertAsync(viewer, plan, cancellationToken);
        await _audit.RecordAsync(viewer, WriteAction, AuditOutcome.Success, [], cancellationToken, TargetType, plan.Id);
        return plan;
    }

    /// <summary>素案の中身を置き換える（作成者だけ）。</summary>
    public async Task<Plan> UpdateAsync(ViewerContext viewer, Guid id, PlanInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(input);
        var owner = await RequireAuthorAsync(viewer, WriteAction, id, cancellationToken);
        var plan = await FindAsync(viewer, WriteAction, id, cancellationToken);
        await RequireOwnerAsync(viewer, WriteAction, plan, owner, cancellationToken);
        PlanInput valid;
        try
        {
            valid = Validate(input);
        }
        catch (PlanException ex)
        {
            await _audit.RecordAsync(viewer, WriteAction, Outcome(ex), [], cancellationToken, TargetType, id);
            throw;
        }

        var now = _timeProvider.GetUtcNow();
        await _store.UpdateAsync(viewer, id, valid, now, cancellationToken);
        await _audit.RecordAsync(viewer, WriteAction, AuditOutcome.Success, [], cancellationToken, TargetType, id);
        return plan with { Title = valid.Title, Note = valid.Note, BaseDate = valid.BaseDate, EffectiveDate = valid.EffectiveDate, Changes = valid.Changes, UpdatedAt = now };
    }

    /// <summary>共有する人を置き換える（作成者の人だけ）。</summary>
    public async Task<Plan> ShareAsync(ViewerContext viewer, Guid id, IReadOnlyList<Guid> people, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(people);
        var owner = await RequireUserAsync(viewer, ShareAction, id, cancellationToken);
        var plan = await FindAsync(viewer, ShareAction, id, cancellationToken);
        await RequireOwnerAsync(viewer, ShareAction, plan, owner, cancellationToken);
        var distinct = people.Distinct().Where(p => p != owner).ToList();
        if (distinct.Count > 100)
        {
            await _audit.RecordAsync(viewer, ShareAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.Invalid, "共有できるのは 100 人までです。");
        }

        var today = Today;
        var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
        if (distinct.Any(p => snapshot.FindPerson(p) is null))
        {
            await _audit.RecordAsync(viewer, ShareAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.NotFound, "共有する人が見つかりません。");
        }

        var now = _timeProvider.GetUtcNow();
        await _store.ShareAsync(viewer, id, distinct, now, cancellationToken);
        await _audit.RecordAsync(viewer, ShareAction, AuditOutcome.Success, distinct, cancellationToken, TargetType, id);
        return plan with { SharedWith = distinct, UpdatedAt = now };
    }

    public async Task DeleteAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var owner = await RequireAuthorAsync(viewer, DeleteAction, id, cancellationToken);
        var plan = await FindAsync(viewer, DeleteAction, id, cancellationToken);
        await RequireOwnerAsync(viewer, DeleteAction, plan, owner, cancellationToken);
        await _store.DeleteAsync(viewer, id, cancellationToken);
        await _audit.RecordAsync(viewer, DeleteAction, AuditOutcome.Success, [], cancellationToken, TargetType, id);
    }

    /// <summary>素案（か申請版）を基準日の組織に重ねた姿（提案資料の元）。</summary>
    public async Task<PlanSimulation> SimulateAsync(ViewerContext viewer, Guid id, int? versionNo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var (baseDate, changes) = await ContentAsync(viewer, ReadAction, id, versionNo, cancellationToken);
        var simulation = await SimulateAsync(viewer, baseDate, changes, cancellationToken);
        await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, [], cancellationToken, TargetType, id);
        return simulation;
    }

    /// <summary>今の中身を次の版番号の申請版にする（作成者だけ）。</summary>
    public async Task<PlanVersion> FreezeAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var owner = await RequireAuthorAsync(viewer, FreezeAction, id, cancellationToken);
        var plan = await FindAsync(viewer, FreezeAction, id, cancellationToken);
        await RequireOwnerAsync(viewer, FreezeAction, plan, owner, cancellationToken);
        if (plan.Changes.Count == 0)
        {
            await _audit.RecordAsync(viewer, FreezeAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.Invalid, "変更の無い素案は申請版にできません。");
        }

        var version = await _store.FreezeAsync(viewer, id, _timeProvider.GetUtcNow(), cancellationToken)
            ?? throw new PlanException(PlanError.NotFound, "素案が見つかりません。");
        await _audit.RecordAsync(viewer, FreezeAction, AuditOutcome.Success, [], cancellationToken, TargetType, id);
        return version;
    }

    public async Task<IReadOnlyList<PlanVersion>> ListVersionsAsync(ViewerContext viewer, Guid id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        _ = await FindAsync(viewer, ReadAction, id, cancellationToken);
        return await _store.ListVersionsAsync(viewer, id, cancellationToken);
    }

    /// <summary>申請版を参照する（素案が見える人）。</summary>
    public async Task<PlanVersion> GetVersionAsync(ViewerContext viewer, Guid id, int versionNo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        _ = await FindAsync(viewer, ReadAction, id, cancellationToken);
        return await _store.FindVersionAsync(viewer, id, versionNo, cancellationToken)
            ?? throw new PlanException(PlanError.NotFound, "申請版が見つかりません。");
    }

    /// <summary>
    /// 申請版の承認の結果を記録する（1 回だけ）。人なら申請版の組織すべての範囲を持つこと、社内システムなら設定で指定したクライアントであること。
    /// </summary>
    public async Task<PlanVersion> RecordApprovalAsync(
        ViewerContext viewer,
        Guid id,
        int versionNo,
        DateOnly approvedOn,
        string? reference,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var service = viewer.Actor == ActorType.Service;
        if (service ? viewer.ClientId is not { } client || !_options.ApprovalClients.Contains(client, StringComparer.Ordinal)
                    : viewer.Actor != ActorType.User || viewer.PersonId is null)
        {
            await _audit.RecordAsync(viewer, ApprovalAction, AuditOutcome.Denied, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.Forbidden, "承認の結果を記録できるのは、範囲を持つ人か、指定された社内システムだけです。");
        }

        var version = service
            ? await _store.FindVersionAsync(viewer, id, versionNo, cancellationToken)
            : await GetVersionAsync(viewer, id, versionNo, cancellationToken);
        if (version is null || version.PlanId != id)
        {
            await _audit.RecordAsync(viewer, ApprovalAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.NotFound, "申請版が見つかりません。");
        }

        var value = reference?.Trim();
        if (value is { Length: > 100 } || value?.Any(char.IsControl) == true || approvedOn > Today)
        {
            await _audit.RecordAsync(viewer, ApprovalAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.Invalid, "承認日は今日以前、申請番号は 100 文字以内で指定してください。");
        }

        if (!service && !viewer.ManagesAllOrgs)
        {
            var scope = await _scopes.ScopeAsync(viewer, Today, cancellationToken);
            var snapshot = await _snapshots.LoadAsync(viewer, version.BaseDate, Today, cancellationToken);
            if (!OrgsOf(snapshot, version.Changes).All(u => scope.Covers(u)))
            {
                await _audit.RecordAsync(viewer, ApprovalAction, AuditOutcome.Denied, [], cancellationToken, TargetType, id);
                throw new PlanException(PlanError.Forbidden, "申請版の組織すべての範囲を持つ人だけが、承認の結果を記録できます。");
            }
        }

        var now = _timeProvider.GetUtcNow();
        if (!await _store.RecordApprovalAsync(viewer, version.Id, approvedOn, string.IsNullOrEmpty(value) ? null : value, now, cancellationToken))
        {
            await _audit.RecordAsync(viewer, ApprovalAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.Conflict, "承認の結果は記録済みです。");
        }

        await _audit.RecordAsync(viewer, ApprovalAction, AuditOutcome.Success, [], cancellationToken, TargetType, id);
        return version with
        {
            ApprovedOn = approvedOn,
            ApprovalReference = string.IsNullOrEmpty(value) ? null : value,
            ApprovalRecordedBy = service ? null : viewer.PersonId,
            ApprovalRecordedByClient = service ? viewer.ClientId : null,
            ApprovalRecordedAt = now,
        };
    }

    /// <summary>
    /// 素案（か承認済みの申請版）の変更を、操作した人の範囲で直接登録にする。<paramref name="indexes"/> を省略したらすべての変更。
    /// グループの加入・脱退は、グループの変更の規則（<see cref="GroupService"/>）で今日反映する。
    /// </summary>
    public async Task<PlanRegistrationResult> RegisterAsync(
        ViewerContext viewer,
        Guid id,
        int? versionNo,
        IReadOnlyList<int>? indexes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireUserAsync(viewer, RegisterAction, id, cancellationToken);
        var plan = await FindAsync(viewer, RegisterAction, id, cancellationToken);
        PlanVersion? version = null;
        if (versionNo is { } no)
        {
            version = await GetVersionAsync(viewer, id, no, cancellationToken);
            if (version.ApprovedOn is null)
            {
                await _audit.RecordAsync(viewer, RegisterAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
                throw new PlanException(PlanError.Conflict, "承認の結果を記録した申請版だけを登録できます。");
            }
        }

        var changes = version?.Changes ?? plan.Changes;
        var selected = (indexes ?? [.. Enumerable.Range(0, changes.Count)]).Distinct().Order().ToList();
        if (selected.Count == 0 || selected.Any(i => i < 0 || i >= changes.Count))
        {
            await _audit.RecordAsync(viewer, RegisterAction, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.Invalid, "登録する変更の番号が正しくありません。");
        }

        var field = selected.Where(i => PlanChanges.FieldKind(changes[i].Kind) is not null).ToList();
        FieldRegistration? registration = null;
        if (field.Count > 0)
        {
            try
            {
                registration = await _registrations.RegisterAsync(
                    viewer,
                    new FieldRegistrationInput(
                        version?.EffectiveDate ?? plan.EffectiveDate,
                        $"素案「{Truncate(plan.Title, 100)}」{(version is null ? string.Empty : $"の申請版 {version.VersionNo}")}から",
                        [.. field.Select(i => new FieldChangeInput(PlanChanges.FieldKind(changes[i].Kind)!.Value, changes[i].TargetId, changes[i].Values))]),
                    cancellationToken);
            }
            catch (FieldManagementException ex)
            {
                await _audit.RecordAsync(viewer, RegisterAction, ex.Error == FieldManagementError.Forbidden ? AuditOutcome.Denied : AuditOutcome.Failed, [], cancellationToken, TargetType, id);
                throw new PlanException(
                    ex.Error switch
                    {
                        FieldManagementError.Forbidden => PlanError.Forbidden,
                        FieldManagementError.NotFound => PlanError.NotFound,
                        FieldManagementError.Conflict => PlanError.Conflict,
                        _ => PlanError.Invalid,
                    },
                    ex.Message);
            }

            await _store.AddRegistrationLinksAsync(
                viewer, [.. field.Select(i => new PlanRegistrationLink(id, version?.Id, i, registration.Id))], cancellationToken);
        }

        var groups = 0;
        foreach (var i in selected.Where(i => changes[i].Kind is PlanChangeKind.GroupJoin or PlanChangeKind.GroupLeave))
        {
            var change = changes[i];
            if (change.TargetId is not { } group || !Guid.TryParse(change.Values.GetValueOrDefault("person_id"), CultureInfo.InvariantCulture, out var person))
            {
                continue;
            }

            try
            {
                _ = change.Kind == PlanChangeKind.GroupJoin
                    ? await _groups.AddMemberAsync(
                        viewer, group, person, GroupCodes.TryParseRole(change.Values.GetValueOrDefault("role"), out var role) ? role : GroupRole.Member, null, null, cancellationToken)
                    : await _groups.RemoveMemberAsync(viewer, group, person, cancellationToken);
                groups++;
            }
            catch (GroupException ex)
            {
                throw new PlanException(ex.Error == GroupError.Forbidden ? PlanError.Forbidden : PlanError.Conflict, $"変更 {i}（グループ）: {ex.Message}");
            }
        }

        await _audit.RecordAsync(viewer, RegisterAction, AuditOutcome.Success, registration?.PersonIds ?? [], cancellationToken, TargetType, id);
        return new PlanRegistrationResult(registration, groups);
    }

    /// <summary>人事へ渡す変更の一覧（CSV。UTF-8 BOM）。人の名前は閲覧者に見える人だけ。</summary>
    public async Task<byte[]> ExportCsvAsync(ViewerContext viewer, Guid id, int? versionNo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var (baseDate, changes) = await ContentAsync(viewer, ExportAction, id, versionNo, cancellationToken);
        var simulation = await SimulateAsync(viewer, baseDate, changes, cancellationToken);
        var effective = versionNo is { } no ? (await GetVersionAsync(viewer, id, no, cancellationToken)).EffectiveDate : (await FindAsync(viewer, ExportAction, id, cancellationToken)).EffectiveDate;
        var personIds = simulation.Moves.Select(m => m.PersonId).Concat(simulation.PersonChanges.Select(c => c.TargetId)).Concat(simulation.GroupChanges.Select(g => g.PersonId)).Distinct().ToList();
        var people = (await _people.GetPeopleAsync(viewer, personIds, baseDate, cancellationToken)).ToDictionary(v => v.PersonId);
        var units = simulation.Units.ToDictionary(u => u.Id);

        string Person(Guid p) => people.TryGetValue(p, out var v) ? $"{v.EmployeeNumber} {v.DisplayName}".Trim() : p.ToString("D");
        string Unit(Guid? u) => u is { } x && units.TryGetValue(x, out var unit) ? $"{unit.Code} {unit.Name}" : u?.ToString("D") ?? string.Empty;

        var csv = new StringBuilder();
        csv.Append("番号,種類,対象,項目,現在の値,素案の値,有効日\r\n");
        var date = effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        foreach (var m in simulation.Moves)
        {
            Row(csv, m.Index, "異動", Person(m.PersonId), "所属", Unit(m.FromUnitId), Unit(m.ToUnitId), date);
            if (!string.Equals(m.FromTitle, m.ToTitle, StringComparison.Ordinal))
            {
                Row(csv, m.Index, "異動", Person(m.PersonId), "役職", m.FromTitle, m.ToTitle, date);
            }
        }

        foreach (var c in simulation.PersonChanges)
        {
            Row(csv, c.Index, "人", Person(c.TargetId), c.Field, c.Old, c.New, date);
        }

        foreach (var c in simulation.OrgChanges)
        {
            Row(csv, c.Index, c.Field == "abolish" ? "組織の廃止" : "組織", Unit(c.TargetId), c.Field == "abolish" ? string.Empty : c.Field, c.Old, c.New, date);
        }

        foreach (var u in simulation.Units.Where(u => u.State == PlanUnitState.Created))
        {
            Row(csv, -1, "組織の新設", $"{u.Code} {u.Name}", "上位組織", null, Unit(u.ParentId), date);
        }

        foreach (var g in simulation.GroupChanges)
        {
            Row(csv, g.Index, g.Join ? "グループに加入" : "グループから脱退", Person(g.PersonId), "グループ", null, g.GroupId.ToString("D"), date);
        }

        await _audit.RecordAsync(viewer, ExportAction, AuditOutcome.Success, [.. people.Keys], cancellationToken, TargetType, id);
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
    }

    private async Task<PlanSimulation> SimulateAsync(ViewerContext viewer, DateOnly baseDate, IReadOnlyList<PlanChange> changes, CancellationToken cancellationToken)
    {
        var date = baseDate > viewer.DisclosureHorizon(Today) ? Today : baseDate;
        var snapshot = await _snapshots.LoadAsync(viewer, date, Today, cancellationToken);
        var fields = await _people.VisibleFieldsAsync(viewer, [.. snapshot.People.Select(p => p.Id)], date, cancellationToken);
        return PlanSimulator.Simulate(snapshot, changes, (person, field) => fields.TryGetValue(person, out var visible) && visible.Contains(field));
    }

    private async Task<(DateOnly BaseDate, IReadOnlyList<PlanChange> Changes)> ContentAsync(
        ViewerContext viewer,
        string action,
        Guid id,
        int? versionNo,
        CancellationToken cancellationToken)
    {
        var plan = await FindAsync(viewer, action, id, cancellationToken);
        if (versionNo is not { } no)
        {
            return (plan.BaseDate, plan.Changes);
        }

        var version = await _store.FindVersionAsync(viewer, id, no, cancellationToken)
            ?? throw new PlanException(PlanError.NotFound, "申請版が見つかりません。");
        return (version.BaseDate, version.Changes);
    }

    /// <summary>変更が関係する組織（対象の組織・異動先・上位組織・人の今の主務の組織）。</summary>
    private static IEnumerable<Guid> OrgsOf(OrgSnapshot snapshot, IReadOnlyList<PlanChange> changes)
    {
        var orgs = new HashSet<Guid>();
        var created = changes.Where(c => c.Kind == PlanChangeKind.OrgCreate && c.TargetId is not null).Select(c => c.TargetId!.Value).ToHashSet();
        foreach (var c in changes)
        {
            switch (c.Kind)
            {
                case PlanChangeKind.OrgUpdate or PlanChangeKind.OrgAbolish when c.TargetId is { } unit:
                    orgs.Add(unit);
                    break;
                case PlanChangeKind.Assignment or PlanChangeKind.Person when c.TargetId is { } person:
                    if (snapshot.AssignmentsOf(person).FirstOrDefault(a => a.Type == AssignmentType.Primary) is { } a)
                    {
                        orgs.Add(a.OrgUnitId);
                    }

                    break;
            }

            foreach (var key in new[] { "org_unit_id", "parent_org_unit_id" })
            {
                if (Guid.TryParse(c.Values.GetValueOrDefault(key), CultureInfo.InvariantCulture, out var unit) && !created.Contains(unit))
                {
                    orgs.Add(unit);
                }
            }
        }

        return orgs;
    }

    private PlanInput Validate(PlanInput input)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200 || title.Any(char.IsControl))
        {
            throw new PlanException(PlanError.Invalid, "題名は 1〜200 文字で、改行などの制御文字を含めずに指定してください。");
        }

        var note = input.Note?.Trim();
        if (note is { Length: > 2000 })
        {
            throw new PlanException(PlanError.Invalid, "メモは 2000 文字以内で指定してください。");
        }

        if (input.Changes is null || input.Changes.Count > PlanChanges.MaxChanges)
        {
            throw new PlanException(PlanError.Invalid, $"変更は {PlanChanges.MaxChanges} 件までです。");
        }

        foreach (var (change, i) in input.Changes.Select((c, i) => (c, i)))
        {
            var unknown = change.Values.Keys.FirstOrDefault(k => !PlanChanges.ValueKeys.Contains(k));
            if (unknown is not null || change.Values.Values.Any(v => v is { Length: > 200 }))
            {
                throw new PlanException(PlanError.Invalid, $"変更 {i}: 項目 {unknown ?? "の値"} は使えません（値は 200 文字まで）。");
            }
        }

        if (input.EffectiveDate < input.BaseDate)
        {
            throw new PlanException(PlanError.Invalid, "予定の有効日は基準日以降にしてください。");
        }

        return input with { Title = title, Note = string.IsNullOrEmpty(note) ? null : note };
    }

    private async Task<Plan> FindAsync(ViewerContext viewer, string action, Guid id, CancellationToken cancellationToken)
    {
        var plan = await _store.FindAsync(viewer, id, cancellationToken);
        if (plan is null)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.NotFound, "素案が見つかりません。");
        }

        return plan;
    }

    private async Task RequireOwnerAsync(ViewerContext viewer, string action, Plan plan, Guid person, CancellationToken cancellationToken)
    {
        if (plan.Owner != person)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, plan.Id);
            throw new PlanException(PlanError.Forbidden, "素案を変えられるのは作成者だけです（共有された人は見るだけです）。");
        }
    }

    /// <summary>作成者になれる閲覧者（人・AI エージェントの委任元）。</summary>
    private async Task<Guid> RequireAuthorAsync(ViewerContext viewer, string action, Guid? id, CancellationToken cancellationToken)
    {
        if (viewer.Actor is not (ActorType.User or ActorType.Agent) || viewer.EffectivePersonId is not { } person)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.Forbidden, "素案を書けるのは、人か、人から委任された AI エージェントだけです。");
        }

        return person;
    }

    private async Task<Guid> RequireUserAsync(ViewerContext viewer, string action, Guid? id, CancellationToken cancellationToken)
    {
        if (viewer.Actor != ActorType.User || viewer.PersonId is not { } person)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, id);
            throw new PlanException(PlanError.Forbidden, "この操作は、ログインした人だけが行えます。");
        }

        return person;
    }

    private DateOnly Today => BusinessDate.Today(_timeProvider);

    private static AuditOutcome Outcome(PlanException ex) => ex.Error == PlanError.Forbidden ? AuditOutcome.Denied : AuditOutcome.Failed;

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static void Row(StringBuilder csv, int index, string kind, string target, string field, string? old, string? value, string date)
    {
        csv.AppendJoin(',', new[] { index >= 0 ? (index + 1).ToString(CultureInfo.InvariantCulture) : string.Empty, kind, target, field, old ?? string.Empty, value ?? string.Empty, date }.Select(Escape));
        csv.Append("\r\n");
    }

    /// <summary>CSV の値（引用符で囲む。表計算ソフトの式として解釈される先頭の文字は ' を前に付ける）。</summary>
    private static string Escape(string value)
    {
        var safe = value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return "\"" + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
