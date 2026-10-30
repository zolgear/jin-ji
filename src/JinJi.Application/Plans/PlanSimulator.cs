using System.Globalization;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.Application.Plans;

/// <summary>シミュレーションの組織の状態。</summary>
public enum PlanUnitState
{
    Unchanged,
    Created,
    Changed,
    Abolished,
}

/// <summary>素案を適用した姿の組織（今の姿との比較つき）。</summary>
/// <param name="Before">今の主務の所属者の数（閲覧者に所属が見える人だけ）。新設は 0。</param>
/// <param name="After">素案を適用した後の主務の所属者の数。廃止は 0。</param>
public sealed record PlanUnit(
    Guid Id,
    string Code,
    string Name,
    string? ShortName,
    string? LevelKey,
    Guid? ParentId,
    Guid? HeadPersonId,
    Guid? SiteId,
    PlanUnitState State,
    int Before,
    int After);

/// <summary>異動（主務の所属・役職の変更）。</summary>
public sealed record PlanMove(int Index, Guid PersonId, Guid? FromUnitId, Guid ToUnitId, string? FromTitle, string? ToTitle);

/// <summary>項目の変更（人の拠点・連絡先、組織の名称・上位組織など）。</summary>
public sealed record PlanFieldChange(int Index, Guid TargetId, string Field, string? Old, string? New);

/// <summary>グループの加入・脱退。</summary>
public sealed record PlanGroupChange(int Index, Guid GroupId, Guid PersonId, bool Join, GroupRole? Role);

/// <summary>素案の変更のうち、適用できなかったもの（参照先が無いなど）。</summary>
public sealed record PlanWarning(int Index, string Message);

/// <summary>素案を適用した姿（ADR-0013 決定 8・9。提案資料の元）。人は ID だけ（氏名は閲覧者の権限で人の API から引く）。</summary>
public sealed record PlanSimulation(
    DateOnly BaseDate,
    IReadOnlyList<PlanUnit> Units,
    IReadOnlyList<PlanMove> Moves,
    IReadOnlyList<PlanFieldChange> PersonChanges,
    IReadOnlyList<PlanFieldChange> OrgChanges,
    IReadOnlyList<PlanGroupChange> GroupChanges,
    IReadOnlyList<PlanWarning> Warnings);

/// <summary>
/// 素案を基準日の組織に重ねた姿を作る（公開側は変えない）。素案は試す案なので、適用できない変更は警告にして先へ進む。
/// </summary>
/// <remarks>
/// 人の扱い: 異動・人数は、閲覧者に項目 assignments が見える人だけを数え・並べる。人の項目の変更は、その項目が閲覧者に見える人だけを並べる
/// （素案を通して、見えない項目が見えるようにはしない）。
/// </remarks>
public static class PlanSimulator
{
    public static PlanSimulation Simulate(OrgSnapshot snapshot, IReadOnlyList<PlanChange> changes, Func<Guid, string, bool> canSee)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(canSee);

        var units = snapshot.Units.ToDictionary(u => u.Id, u => new Draft(u, PlanUnitState.Unchanged));
        var primary = new Dictionary<Guid, (Guid Unit, string? Title)>();
        foreach (var person in snapshot.People.Where(p => canSee(p.Id, PersonFields.Assignments)))
        {
            if (snapshot.AssignmentsOf(person.Id).FirstOrDefault(a => a.Type == AssignmentType.Primary) is { } a)
            {
                primary[person.Id] = (a.OrgUnitId, a.PositionTitle);
            }
        }

        var before = primary.Values.GroupBy(p => p.Unit).ToDictionary(g => g.Key, g => g.Count());
        var after = new Dictionary<Guid, (Guid Unit, string? Title)>(primary);
        var moves = new List<PlanMove>();
        var personChanges = new List<PlanFieldChange>();
        var orgChanges = new List<PlanFieldChange>();
        var groupChanges = new List<PlanGroupChange>();
        var warnings = new List<PlanWarning>();

        // 新設を先に置き、同じ素案の中の異動先・上位組織に使えるようにする
        for (var i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (c.Kind != PlanChangeKind.OrgCreate)
            {
                continue;
            }

            var id = c.TargetId ?? Guid.NewGuid();
            var parent = Id(c.Values, "parent_org_unit_id");
            if (units.ContainsKey(id) || parent is null || !units.ContainsKey(parent.Value) || Text(c.Values, "name") is not { } name)
            {
                warnings.Add(new PlanWarning(i, "新設の組織に、既存と重ならない ID・名称・ある上位組織が要ります。"));
                continue;
            }

            var parentUnit = units[parent.Value].Unit;
            units[id] = new Draft(
                new OrgUnit(id, parentUnit.LegalEntityId, Text(c.Values, "code") ?? "", name, Text(c.Values, "short_name"), Text(c.Values, "level_key"), parent, Id(c.Values, "head_person_id"))
                {
                    SiteId = Id(c.Values, "site_id"),
                },
                PlanUnitState.Created);
        }

        for (var i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            switch (c.Kind)
            {
                case PlanChangeKind.OrgCreate:
                    break;
                case PlanChangeKind.OrgUpdate when c.TargetId is { } unitId && units.TryGetValue(unitId, out var draft) && draft.State != PlanUnitState.Abolished:
                {
                    var u = draft.Unit;
                    var next = u;
                    foreach (var (key, value) in c.Values)
                    {
                        string? old;
                        switch (key)
                        {
                            case "name" when value is { Length: > 0 }:
                                (old, next) = (u.Name, next with { Name = value });
                                break;
                            case "short_name":
                                (old, next) = (u.ShortName, next with { ShortName = value });
                                break;
                            case "level_key":
                                (old, next) = (u.LevelKey, next with { LevelKey = value });
                                break;
                            case "parent_org_unit_id" when Guid.TryParse(value, CultureInfo.InvariantCulture, out var p) && units.ContainsKey(p) && !IsDescendant(units, p, unitId):
                                (old, next) = (u.ParentId?.ToString("D"), next with { ParentId = p });
                                break;
                            case "head_person_id":
                                (old, next) = (u.HeadPersonId?.ToString("D"), next with { HeadPersonId = Guid.TryParse(value, CultureInfo.InvariantCulture, out var h) ? h : null });
                                break;
                            case "site_id":
                                (old, next) = (u.SiteId?.ToString("D"), next with { SiteId = Guid.TryParse(value, CultureInfo.InvariantCulture, out var s) ? s : null });
                                break;
                            default:
                                warnings.Add(new PlanWarning(i, $"組織の項目 {key} の値を適用できません。"));
                                continue;
                        }

                        orgChanges.Add(new PlanFieldChange(i, unitId, key, old, value));
                    }

                    units[unitId] = draft with { Unit = next, State = draft.State == PlanUnitState.Created ? PlanUnitState.Created : PlanUnitState.Changed };
                    break;
                }

                case PlanChangeKind.OrgAbolish when c.TargetId is { } unitId && units.TryGetValue(unitId, out var draft):
                    units[unitId] = draft with { State = PlanUnitState.Abolished };
                    orgChanges.Add(new PlanFieldChange(i, unitId, "abolish", null, null));
                    break;
                case PlanChangeKind.Assignment when c.TargetId is { } personId && snapshot.FindPerson(personId) is not null:
                {
                    var to = c.Values.ContainsKey("org_unit_id") ? Id(c.Values, "org_unit_id") : after.GetValueOrDefault(personId).Unit;
                    if (to is not { } target || !units.ContainsKey(target))
                    {
                        warnings.Add(new PlanWarning(i, "異動先の組織がありません。"));
                        break;
                    }

                    if (!canSee(personId, PersonFields.Assignments))
                    {
                        break;
                    }

                    after.TryGetValue(personId, out var from);
                    var title = c.Values.ContainsKey("position_title") ? Text(c.Values, "position_title") : from.Title;
                    after[personId] = (target, title);
                    moves.Add(new PlanMove(i, personId, primary.ContainsKey(personId) ? from.Unit : null, target, from.Title, title));
                    break;
                }

                case PlanChangeKind.Person when c.TargetId is { } personId && snapshot.FindPerson(personId) is { } person:
                    foreach (var (key, value) in c.Values)
                    {
                        var field = key is "site_id" ? PersonFields.Site : key;
                        if (!canSee(personId, field))
                        {
                            continue;
                        }

                        var current = person.Current;
                        var old = key switch
                        {
                            "site_id" => current.SiteId?.ToString("D"),
                            "remote_work" => current.RemoteWork ? "true" : "false",
                            "extension_number" => current.ExtensionNumber,
                            "work_mobile_phone" => current.WorkMobilePhone,
                            _ => null,
                        };
                        personChanges.Add(new PlanFieldChange(i, personId, key, old, value));
                    }

                    break;
                case PlanChangeKind.GroupJoin or PlanChangeKind.GroupLeave when c.TargetId is { } groupId && Id(c.Values, "person_id") is { } member:
                    GroupRole? role = GroupCodes.TryParseRole(Text(c.Values, "role") ?? "member", out var r) ? r : null;
                    if (canSee(member, PersonFields.Groups))
                    {
                        groupChanges.Add(new PlanGroupChange(i, groupId, member, c.Kind == PlanChangeKind.GroupJoin, c.Kind == PlanChangeKind.GroupJoin ? role : null));
                    }

                    break;
                default:
                    warnings.Add(new PlanWarning(i, "対象が見つからないか、適用できない変更です。"));
                    break;
            }
        }

        var counts = after.Values.GroupBy(p => p.Unit).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (unitId, draft) in units.Where(u => u.Value.State == PlanUnitState.Abolished))
        {
            if (counts.GetValueOrDefault(unitId) > 0)
            {
                warnings.Add(new PlanWarning(-1, $"廃止する組織 {draft.Unit.Code} に所属者が残っています。"));
            }

            if (units.Values.Any(d => d.Unit.ParentId == unitId && d.State != PlanUnitState.Abolished))
            {
                warnings.Add(new PlanWarning(-1, $"廃止する組織 {draft.Unit.Code} に配下の組織が残っています。"));
            }
        }

        var result = units.Values
            .Select(d => new PlanUnit(
                d.Unit.Id,
                d.Unit.Code,
                d.Unit.Name,
                d.Unit.ShortName,
                d.Unit.LevelKey,
                d.Unit.ParentId,
                d.Unit.HeadPersonId,
                d.Unit.SiteId,
                d.State,
                d.State == PlanUnitState.Created ? 0 : before.GetValueOrDefault(d.Unit.Id),
                d.State == PlanUnitState.Abolished ? 0 : counts.GetValueOrDefault(d.Unit.Id)))
            .OrderBy(u => u.Code, StringComparer.Ordinal)
            .ThenBy(u => u.Id)
            .ToList();
        return new PlanSimulation(snapshot.AsOf, result, moves, personChanges, orgChanges, groupChanges, warnings);
    }

    private static bool IsDescendant(Dictionary<Guid, Draft> units, Guid candidate, Guid ancestor)
    {
        for (Guid? id = candidate; id is { } current; id = units.TryGetValue(current, out var d) ? d.Unit.ParentId : null)
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private static string? Text(IReadOnlyDictionary<string, string?> values, string key) =>
        values.GetValueOrDefault(key)?.Trim() is { Length: > 0 } text ? text : null;

    private static Guid? Id(IReadOnlyDictionary<string, string?> values, string key) =>
        Guid.TryParse(values.GetValueOrDefault(key), CultureInfo.InvariantCulture, out var id) ? id : null;

    private sealed record Draft(OrgUnit Unit, PlanUnitState State);
}
