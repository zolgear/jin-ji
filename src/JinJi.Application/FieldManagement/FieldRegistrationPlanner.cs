using System.Globalization;
using JinJi.Application.Ingest;
using JinJi.Domain.Organization;

namespace JinJi.Application.FieldManagement;

/// <summary>直接登録の計画（変更提案・関係する組織と人・同意が要る組織）。</summary>
public sealed record FieldRegistrationPlan(
    IReadOnlyList<ChangeProposalRecord> Proposals,
    IReadOnlyList<Guid> OrgUnitIds,
    IReadOnlyList<Guid> PersonIds,
    IReadOnlyList<Guid> ConsentOrgUnitIds);

/// <summary>
/// 直接登録の入力を、取込と同じ変更提案（差分と旧値）に変え、管理元・範囲・値を検査する（ADR-0013 決定 2・3・5）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>旧値は有効日の組織スナップショット（登録者の見え方）から取る。反映時に DB の行と照合し、作成後に変わっていれば反映しない</item>
/// <item>範囲は今日の範囲（<see cref="RegistrationScope"/>）で判定する。異動・上位組織の付け替えは出し側と受け側の両方を要し、
/// 片側しか持たなければもう片側を「同意が要る組織」にする（どちらも持たなければ拒否）</item>
/// <item>組織長の任免・上位組織の付け替えは、元の上位組織の範囲を要する（同意では補えない。権限を生む変更）</item>
/// <item>反映の順は 組織の新設 → 組織の変更 → 所属 → 人 → 組織の廃止（新しい組織への異動・異動で空にした組織の廃止を 1 回で登録できる）</item>
/// </list>
/// </remarks>
public sealed class FieldRegistrationPlanner
{
    /// <summary>名称の上限（文字）。</summary>
    public const int MaxNameLength = 200;

    /// <summary>コード・略称・役職・内線・社用携帯の上限（文字）。</summary>
    public const int MaxShortLength = 100;

    private static readonly IReadOnlyDictionary<FieldChangeKind, string[]> AllowedKeys = new Dictionary<FieldChangeKind, string[]>
    {
        [FieldChangeKind.Assignment] = [ProposalFields.OrgUnitId, ProposalFields.PositionTitle],
        [FieldChangeKind.Person] = [ProposalFields.SiteId, ProposalFields.RemoteWork, ProposalFields.ExtensionNumber, ProposalFields.WorkMobilePhone],
        [FieldChangeKind.OrgUpdate] =
        [
            ProposalFields.Name, ProposalFields.ShortName, ProposalFields.LevelKey, ProposalFields.ParentOrgUnitId, ProposalFields.HeadPersonId,
            ProposalFields.SiteId,
        ],
        [FieldChangeKind.OrgCreate] =
        [
            ProposalFields.Code, ProposalFields.Name, ProposalFields.ShortName, ProposalFields.LevelKey, ProposalFields.ParentOrgUnitId,
            ProposalFields.HeadPersonId, ProposalFields.SiteId,
        ],
        [FieldChangeKind.OrgAbolish] = [],
    };

    private readonly OrgSnapshot _snapshot;
    private readonly RegistrationScope _scope;
    private readonly IReadOnlyDictionary<FieldDomain, FieldManagementMode> _modes;
    private readonly Guid _registrant;
    private readonly DateOnly _date;

    private readonly List<(int Order, ChangeProposalRecord Proposal)> _proposals = [];
    private readonly HashSet<Guid> _orgs = [];
    private readonly HashSet<Guid> _people = [];
    private readonly HashSet<Guid> _consent = [];
    private readonly Dictionary<Guid, Guid> _transfers = [];
    private readonly HashSet<Guid> _created = [];

    /// <param name="snapshot">有効日の組織（旧値と存在の確認）。</param>
    /// <param name="scope">登録者の今日の範囲。</param>
    /// <param name="modes">領域ごとの管理元。</param>
    /// <param name="registrant">登録者（AI エージェントは委任元）。</param>
    public FieldRegistrationPlanner(
        OrgSnapshot snapshot,
        RegistrationScope scope,
        IReadOnlyDictionary<FieldDomain, FieldManagementMode> modes,
        Guid registrant)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(modes);
        _snapshot = snapshot;
        _scope = scope;
        _modes = modes;
        _registrant = registrant;
        _date = snapshot.AsOf;
    }

    /// <summary>入力から計画を作る。管理元・範囲・値の誤りは <see cref="FieldManagementException"/>。</summary>
    public FieldRegistrationPlan Plan(IReadOnlyList<FieldChangeInput> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            throw Invalid("変更を 1 つ以上指定してください。");
        }

        // 新設の組織を先に数え、同じ登録の中の異動先・上位組織に使えるようにする
        foreach (var change in changes.Where(c => c.Kind == FieldChangeKind.OrgCreate))
        {
            if (change.TargetId is { } id && !_created.Add(id))
            {
                throw Invalid($"新設の組織 {id} が重複しています。");
            }
        }

        var targets = new HashSet<(FieldChangeKind, Guid)>();
        foreach (var change in changes)
        {
            RequireKeys(change);
            switch (change.Kind)
            {
                case FieldChangeKind.OrgCreate:
                    OrgCreate(change);
                    break;
                case FieldChangeKind.OrgUpdate:
                    OrgUpdate(Target(change, targets));
                    break;
                case FieldChangeKind.Assignment:
                    Assignment(Target(change, targets));
                    break;
                case FieldChangeKind.Person:
                    Person(Target(change, targets));
                    break;
                case FieldChangeKind.OrgAbolish:
                    // 廃止は所属の変更をすべて見てから検査する
                    break;
                default:
                    throw Invalid($"変更の種類 {change.Kind} は直接登録できません。");
            }
        }

        foreach (var change in changes.Where(c => c.Kind == FieldChangeKind.OrgAbolish))
        {
            OrgAbolish(Target(change, targets));
        }

        return new FieldRegistrationPlan(
            [.. _proposals.OrderBy(p => p.Order).Select(p => p.Proposal)],
            [.. _orgs.Order()],
            [.. _people.Order()],
            [.. _consent.Order()]);
    }

    // -----------------------------------------------------------------------
    // 組織
    // -----------------------------------------------------------------------

    private void OrgCreate(FieldChangeInput change)
    {
        RequireMode(FieldDomain.OrgStructure);
        var values = change.Values;
        var parentId = RequiredGuid(values, ProposalFields.ParentOrgUnitId);
        var parent = _snapshot.FindUnit(parentId) ?? throw NotFound($"上位組織 {parentId} が {_date:yyyy-MM-dd} にありません。");
        if (!_scope.Covers(parentId))
        {
            throw Forbidden("上位組織の範囲を持たないので、配下に組織を新設できません。");
        }

        var code = Text(values, ProposalFields.Code, MaxShortLength) ?? throw Invalid("新設の組織のコードを指定してください。");
        var name = Text(values, ProposalFields.Name, MaxNameLength) ?? throw Invalid("新設の組織の名称を指定してください。");
        if (_snapshot.Units.Any(u => u.LegalEntityId == parent.LegalEntityId && string.Equals(u.Code, code, StringComparison.Ordinal)))
        {
            throw Invalid($"組織コード {code} は同じ法人で使われています。");
        }

        var head = OptionalPerson(values, ProposalFields.HeadPersonId);
        var site = OptionalSite(values, ProposalFields.SiteId);
        var id = change.TargetId ?? Guid.CreateVersion7();
        if (_snapshot.FindUnit(id) is not null)
        {
            throw Invalid($"組織 {id} は既にあります。");
        }

        _created.Add(id);
        _orgs.Add(id);
        _orgs.Add(parentId);
        var payload = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ProposalFields.LegalEntityId] = Id(parent.LegalEntityId),
            [ProposalFields.Code] = code,
            [ProposalFields.Name] = name,
            [ProposalFields.ShortName] = Text(values, ProposalFields.ShortName, MaxShortLength),
            [ProposalFields.LevelKey] = LevelKey(values),
            [ProposalFields.ParentOrgUnitId] = Id(parentId),
            [ProposalFields.HeadPersonId] = head is { } h ? Id(h) : null,
            [ProposalFields.SiteId] = site is { } s ? Id(s) : null,
            [ProposalFields.ValidFrom] = Date(_date),
        };
        Add(0, ProposalTargetKind.OrgUnit, ProposalOperation.Create, id, payload, new Dictionary<string, FieldChange>());
    }

    private void OrgUpdate(FieldChangeInput change)
    {
        var unitId = change.TargetId!.Value;
        var unit = _snapshot.FindUnit(unitId) ?? throw NotFound($"組織 {unitId} が {_date:yyyy-MM-dd} にありません。");
        var values = change.Values;
        if (!_scope.Covers(unitId))
        {
            throw Forbidden("この組織の範囲を持ちません。");
        }

        _orgs.Add(unitId);
        var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        foreach (var key in values.Keys)
        {
            RequireMode(key == ProposalFields.SiteId ? FieldDomain.OrgSite : FieldDomain.OrgStructure);
        }

        if (values.ContainsKey(ProposalFields.Name))
        {
            Put(diff, ProposalFields.Name, unit.Name, Text(values, ProposalFields.Name, MaxNameLength) ?? throw Invalid("組織の名称は空にできません。"));
        }

        if (values.ContainsKey(ProposalFields.ShortName))
        {
            Put(diff, ProposalFields.ShortName, unit.ShortName, Text(values, ProposalFields.ShortName, MaxShortLength));
        }

        if (values.ContainsKey(ProposalFields.LevelKey))
        {
            Put(diff, ProposalFields.LevelKey, unit.LevelKey, LevelKey(values));
        }

        if (values.ContainsKey(ProposalFields.SiteId))
        {
            var site = OptionalSite(values, ProposalFields.SiteId);
            Put(diff, ProposalFields.SiteId, Id(unit.SiteId), Id(site));
        }

        if (values.ContainsKey(ProposalFields.HeadPersonId))
        {
            var head = OptionalPerson(values, ProposalFields.HeadPersonId);
            if (Put(diff, ProposalFields.HeadPersonId, Id(unit.HeadPersonId), Id(head)) && !_scope.CoversParentOf(unitId))
            {
                throw Forbidden("組織長の任免は、一つ上の組織の範囲を持つ人だけが登録できます。");
            }
        }

        if (values.ContainsKey(ProposalFields.ParentOrgUnitId))
        {
            var parentId = RequiredGuid(values, ProposalFields.ParentOrgUnitId);
            if (_snapshot.FindUnit(parentId) is not { } parent)
            {
                throw NotFound($"上位組織 {parentId} が {_date:yyyy-MM-dd} にありません。");
            }

            if (parent.LegalEntityId != unit.LegalEntityId)
            {
                throw Invalid("別の法人の組織の下には移せません。");
            }

            if (_snapshot.IsSelfOrDescendantOf(parentId, unitId))
            {
                throw Invalid("自分か配下の組織の下には移せません。");
            }

            if (Put(diff, ProposalFields.ParentOrgUnitId, Id(unit.ParentId), Id(parentId)))
            {
                if (!_scope.CoversParentOf(unitId))
                {
                    throw Forbidden("上位組織の付け替えは、元の上位組織の範囲を持つ人だけが登録できます。");
                }

                _orgs.Add(parentId);
                if (!_scope.Covers(parentId))
                {
                    _consent.Add(parentId);
                }
            }
        }

        if (diff.Count == 0)
        {
            throw Invalid($"組織 {unit.Code} の変わる値がありません。");
        }

        Add(1, ProposalTargetKind.OrgUnitVersion, ProposalOperation.Change, unitId, new Dictionary<string, string?>(), diff);
    }

    private void OrgAbolish(FieldChangeInput change)
    {
        RequireMode(FieldDomain.OrgStructure);
        var unitId = change.TargetId!.Value;
        var unit = _snapshot.FindUnit(unitId) ?? throw NotFound($"組織 {unitId} が {_date:yyyy-MM-dd} にありません。");
        if (!_scope.Covers(unitId))
        {
            throw Forbidden("この組織の範囲を持ちません。");
        }

        if (_snapshot.Units.Any(u => u.ParentId == unitId))
        {
            throw Invalid($"組織 {unit.Code} には配下の組織があります。先に配下の組織を廃止するか移してください。");
        }

        var remaining = _snapshot.People
            .Where(p => _snapshot.AssignmentsOf(p.Id).Any(a => a.OrgUnitId == unitId
                && !(a.Type == AssignmentType.Primary && _transfers.TryGetValue(p.Id, out var to) && to != unitId)))
            .Count();
        if (remaining > 0)
        {
            throw Invalid($"組織 {unit.Code} には {_date:yyyy-MM-dd} に所属する人が {remaining} 人います。先に異動を登録してください。");
        }

        _orgs.Add(unitId);
        Add(4, ProposalTargetKind.OrgUnitVersion, ProposalOperation.Abolish, unitId, new Dictionary<string, string?>(), new Dictionary<string, FieldChange>());
    }

    // -----------------------------------------------------------------------
    // 人
    // -----------------------------------------------------------------------

    private void Assignment(FieldChangeInput change)
    {
        RequireMode(FieldDomain.Assignment);
        var personId = change.TargetId!.Value;
        _ = _snapshot.FindPerson(personId) ?? throw NotFound($"人 {personId} が見つかりません。");
        var values = change.Values;
        var current = _snapshot.AssignmentsOf(personId).FirstOrDefault(a => a.Type == AssignmentType.Primary);
        var target = values.ContainsKey(ProposalFields.OrgUnitId)
            ? RequiredGuid(values, ProposalFields.OrgUnitId)
            : current?.OrgUnitId ?? throw Invalid("主務の所属が無い人の異動では、異動先の組織を指定してください。");
        if (_snapshot.FindUnit(target) is null && !_created.Contains(target))
        {
            throw NotFound($"異動先の組織 {target} が {_date:yyyy-MM-dd} にありません。");
        }

        var title = values.ContainsKey(ProposalFields.PositionTitle) ? Text(values, ProposalFields.PositionTitle, MaxShortLength) : current?.PositionTitle;
        Side(target);
        _orgs.Add(target);
        if (current is not null)
        {
            Side(current.OrgUnitId);
            _orgs.Add(current.OrgUnitId);
        }

        if (!Covered(target) && (current is null || !Covered(current.OrgUnitId)))
        {
            throw Forbidden("異動元・異動先のどちらの組織の範囲も持ちません。");
        }

        _people.Add(personId);
        _transfers[personId] = target;
        if (current is null)
        {
            var payload = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ProposalFields.OrgUnitId] = Id(target),
                [ProposalFields.AssignmentType] = "primary",
                [ProposalFields.PositionTitle] = title,
            };
            Add(2, ProposalTargetKind.Assignment, ProposalOperation.Create, personId, payload, new Dictionary<string, FieldChange>());
            return;
        }

        var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        Put(diff, ProposalFields.OrgUnitId, Id(current.OrgUnitId), Id(target));
        Put(diff, ProposalFields.PositionTitle, current.PositionTitle, title);
        if (diff.Count == 0)
        {
            throw Invalid($"人 {personId} の所属・役職の変わる値がありません。");
        }

        Add(2, ProposalTargetKind.Assignment, ProposalOperation.Change, personId, new Dictionary<string, string?>(), diff);
    }

    private void Person(FieldChangeInput change)
    {
        var personId = change.TargetId!.Value;
        var person = _snapshot.FindPerson(personId) ?? throw NotFound($"人 {personId} が見つかりません。");
        var values = change.Values;
        foreach (var key in values.Keys)
        {
            RequireMode(key is ProposalFields.SiteId or ProposalFields.RemoteWork ? FieldDomain.PersonSite : FieldDomain.PersonContact);
        }

        var primary = _snapshot.AssignmentsOf(personId).FirstOrDefault(a => a.Type == AssignmentType.Primary)
            ?? throw Invalid("主務の所属が無い人は、直接登録で変えられません。");
        if (!_scope.Covers(primary.OrgUnitId))
        {
            throw Forbidden("この人の主務の組織の範囲を持ちません。");
        }

        _orgs.Add(primary.OrgUnitId);
        _people.Add(personId);
        var version = person.Current;
        var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        if (values.ContainsKey(ProposalFields.SiteId))
        {
            Put(diff, ProposalFields.SiteId, Id(version.SiteId), Id(OptionalSite(values, ProposalFields.SiteId)));
        }

        if (values.ContainsKey(ProposalFields.RemoteWork))
        {
            var remote = values[ProposalFields.RemoteWork] switch
            {
                "true" => true,
                "false" => false,
                _ => throw Invalid("remote_work は true / false で指定してください。"),
            };
            Put(diff, ProposalFields.RemoteWork, Bool(version.RemoteWork), Bool(remote));
        }

        if (values.ContainsKey(ProposalFields.ExtensionNumber))
        {
            Put(diff, ProposalFields.ExtensionNumber, version.ExtensionNumber, Phone(values, ProposalFields.ExtensionNumber));
        }

        if (values.ContainsKey(ProposalFields.WorkMobilePhone))
        {
            Put(diff, ProposalFields.WorkMobilePhone, version.WorkMobilePhone, Phone(values, ProposalFields.WorkMobilePhone));
        }

        if (diff.Count == 0)
        {
            throw Invalid($"人 {personId} の変わる値がありません。");
        }

        Add(3, ProposalTargetKind.PersonVersion, ProposalOperation.Change, personId, new Dictionary<string, string?>(), diff);
    }

    // -----------------------------------------------------------------------
    // 共通
    // -----------------------------------------------------------------------

    /// <summary>出し側・受け側の組織。範囲を持たなければ同意が要る組織にする（新設の組織は上位組織の範囲で判定済み）。</summary>
    private void Side(Guid unitId)
    {
        if (!Covered(unitId))
        {
            _consent.Add(unitId);
        }
    }

    private bool Covered(Guid unitId) => _created.Contains(unitId) || _scope.Covers(unitId);

    private void RequireMode(FieldDomain domain)
    {
        if (!_modes.GetValueOrDefault(domain, FieldManagementMode.Hr).AllowsDirectRegistration())
        {
            throw Forbidden($"領域 {FieldDomains.Key(domain)} の管理元は人事パッケージなので、直接登録できません。");
        }
    }

    private static void RequireKeys(FieldChangeInput change)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(change.Values);
        var allowed = AllowedKeys[change.Kind];
        var unknown = change.Values.Keys.Where(k => !allowed.Contains(k, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            throw Invalid($"この変更では指定できない項目です: {string.Join(", ", unknown)}");
        }

        if (change.Kind is not (FieldChangeKind.OrgAbolish or FieldChangeKind.OrgCreate) && change.Values.Count == 0)
        {
            throw Invalid("変える項目を指定してください。");
        }
    }

    private static FieldChangeInput Target(FieldChangeInput change, HashSet<(FieldChangeKind, Guid)> seen)
    {
        if (change.TargetId is not { } id)
        {
            throw Invalid("対象（target_id）を指定してください。");
        }

        // 同じ対象への同じ種類の変更は 1 つだけ（旧値の照合が前の変更とぶつかるため）
        var kind = change.Kind == FieldChangeKind.OrgAbolish ? FieldChangeKind.OrgUpdate : change.Kind;
        if (!seen.Add((kind, id)))
        {
            throw Invalid($"同じ対象 {id} への同じ種類の変更が重複しています。1 つにまとめてください。");
        }

        return change;
    }

    private void Add(
        int order,
        ProposalTargetKind kind,
        ProposalOperation operation,
        Guid target,
        IReadOnlyDictionary<string, string?> payload,
        IReadOnlyDictionary<string, FieldChange> diff) =>
        _proposals.Add((order, new ChangeProposalRecord
        {
            Id = Guid.CreateVersion7(),
            BatchId = Guid.Empty,
            TargetKind = kind,
            Operation = operation,
            TargetId = target,
            EffectiveDate = _date,
            Payload = payload,
            Diff = diff,
            Basis = new ProposalBasis(),
            Confidence = 1m,
            Status = ProposalStatus.Proposed,
            ProposedByPersonId = _registrant,
        }));

    /// <summary>差分に加える（値が変わるときだけ）。加えたら true。</summary>
    private static bool Put(Dictionary<string, FieldChange> diff, string key, string? old, string? value)
    {
        if (string.Equals(old, value, StringComparison.Ordinal))
        {
            return false;
        }

        diff[key] = new FieldChange(old, value);
        return true;
    }

    private Guid? OptionalPerson(IReadOnlyDictionary<string, string?> values, string key)
    {
        if (OptionalGuid(values, key) is not { } id)
        {
            return null;
        }

        return _snapshot.FindPerson(id) is null ? throw NotFound($"人 {id} が見つかりません。") : id;
    }

    private Guid? OptionalSite(IReadOnlyDictionary<string, string?> values, string key)
    {
        if (OptionalGuid(values, key) is not { } id)
        {
            return null;
        }

        return _snapshot.FindSite(id) is null ? throw NotFound($"拠点 {id} が {_date:yyyy-MM-dd} にありません。") : id;
    }

    private static string? LevelKey(IReadOnlyDictionary<string, string?> values)
    {
        var key = Text(values, ProposalFields.LevelKey, 63);
        if (key is not null && !System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            throw Invalid("段のキーは英小文字で始まる英小文字・数字・_ で指定してください。");
        }

        return key;
    }

    private static string? Phone(IReadOnlyDictionary<string, string?> values, string key)
    {
        var value = Text(values, key, 32);
        if (value is not null && !value.All(c => char.IsAsciiDigit(c) || c is '-' or '+' or '(' or ')' or ' '))
        {
            throw Invalid($"{key} は数字・ハイフン・括弧・+ で指定してください。");
        }

        return value;
    }

    /// <summary>自由記述の値（前後の空白を除き、空なら null）。上限と制御文字を検査する。</summary>
    private static string? Text(IReadOnlyDictionary<string, string?> values, string key, int max)
    {
        var value = values.GetValueOrDefault(key)?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.Length > max || value.Any(char.IsControl))
        {
            throw Invalid($"{key} は {max} 文字以内で、改行などの制御文字を含めずに指定してください。");
        }

        return value;
    }

    private static Guid RequiredGuid(IReadOnlyDictionary<string, string?> values, string key) =>
        OptionalGuid(values, key) ?? throw Invalid($"{key} を指定してください。");

    private static Guid? OptionalGuid(IReadOnlyDictionary<string, string?> values, string key)
    {
        var value = values.GetValueOrDefault(key);
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return Guid.TryParse(value, CultureInfo.InvariantCulture, out var id) ? id : throw Invalid($"{key} は UUID で指定してください。");
    }

    private static string? Id(Guid? id) => id?.ToString("D");

    private static string Bool(bool value) => value ? "true" : "false";

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static FieldManagementException Invalid(string message) => new(FieldManagementError.Invalid, message);

    private static FieldManagementException Forbidden(string message) => new(FieldManagementError.Forbidden, message);

    private static FieldManagementException NotFound(string message) => new(FieldManagementError.NotFound, message);
}
