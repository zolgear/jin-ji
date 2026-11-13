using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;
using JinJi.Domain;
using JinJi.Domain.Organization;

namespace JinJi.Sync;

/// <summary>
/// 組織に対応する Entra ID のグループの計画（ADR-0015）。jin-ji の組織・所属（業務日付時点）と Entra のグループの一覧の差分から項目を作る。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>グループと組織の対応付けは、説明に書いた印（<see cref="OrgGroupSettings.Marker"/>）だけで行う。印の無いグループは変更しない。
/// 同じ表示名・mailNickname の印の無いグループがあれば、作成せずに衝突として出す</item>
/// <item>メンバーは「その組織（と配下）で働く人」のうち、Entra のアカウントがちょうど 1 つ対応付いている人。対応の無い人・複数ある人は参考として出す</item>
/// <item>外すのは、jin-ji の人に対応付いたメンバーだけ（設定で、対応付かないメンバーも外せる）。設定で除外したユーザーは加えも外しもしない</item>
/// <item>グループは削除しない。廃止された組織のグループは、表示名に廃止の印を付ける。メンバーは引き継ぎのために残し、退職した人だけを外す</item>
/// <item>メンバー・オーナーの一覧が入力に無いグループは、現在の状態が分からないので、加える項目だけを作る（実行時に確かめる）。外す項目は作らない</item>
/// </list>
/// </remarks>
public sealed class EntraGroupPlanner : IGroupSyncPlanner
{
    private static readonly JsonSerializerOptions FingerprintJson = new() { WriteIndented = false };

    public SyncPlanDraft Plan(GroupSyncPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var settings = input.Settings;
        var options = settings.OrgGroups ?? throw new FormatException("同期の設定に orgGroups がありません（組織のグループの計画には接頭辞などの設定が要ります）。");
        var snapshot = input.Snapshot;
        var warnings = new List<string>();
        var desired = DesiredGroups(snapshot, settings, options, input.Today, warnings);

        // 外部 ID の対応（人 ⇔ Entra のアカウント）
        var personByObject = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var objectsByPerson = new Dictionary<Guid, List<string>>();
        foreach (var link in input.Identities)
        {
            if (!Guid.TryParse(link.ExternalId, CultureInfo.InvariantCulture, out var parsed))
            {
                continue;
            }

            var objectId = parsed.ToString("D");
            personByObject[objectId] = link.PersonId;
            if (!objectsByPerson.TryGetValue(link.PersonId, out var list))
            {
                objectsByPerson[link.PersonId] = list = [];
            }

            list.Add(objectId);
        }

        var excludedObjects = settings.Exclude.Select(e => e.ObjectId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedNumbers = settings.Exclude.Select(e => e.EmployeeNumber).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (settings.Exclude.Any(e => e.UserPrincipalName is not null))
        {
            warnings.Add("除外の設定のうち userPrincipalName で指定したものは、グループの計画では使えません（objectId か employeeNumber で指定してください）。");
        }

        bool Excluded(Guid personId, string objectId) =>
            excludedObjects.Contains(objectId) || (snapshot.FindPerson(personId) is { } p && excludedNumbers.Contains(p.EmployeeNumber));

        // 印でグループと組織を対応付ける
        var owned = new Dictionary<Guid, DirectoryGroup>();
        var duplicated = new HashSet<Guid>();
        foreach (var group in input.Groups)
        {
            if (OrgGroupSettings.ReadMarker(group.Description) is not { } orgId)
            {
                continue;
            }

            if (!owned.TryAdd(orgId, group))
            {
                duplicated.Add(orgId);
            }
        }

        var foreign = input.Groups.Where(g => OrgGroupSettings.ReadMarker(g.Description) is null).ToList();
        var drafts = new List<Draft>();
        var withoutAccount = new Dictionary<Guid, int>();
        var manyAccounts = new Dictionary<Guid, int>();

        foreach (var want in desired.Values.OrderBy(d => d.DisplayName, StringComparer.Ordinal).ThenBy(d => d.Unit.Id))
        {
            var unitEvidence = new SyncEvidence(GroupFields.DisplayName, "org_unit", want.Unit.Id.ToString("D"), want.Unit.Name);
            if (duplicated.Contains(want.Unit.Id))
            {
                drafts.Add(Draft.ForGroup(SyncPriority.High, SyncAction.GroupConflict, want, owned[want.Unit.Id].ObjectId, false, [], "marker_duplicated", [unitEvidence],
                    "この組織の印の付いたグループが複数あります。1 つだけに印を残してください（このグループは変更しません）。"));
                continue;
            }

            owned.TryGetValue(want.Unit.Id, out var current);
            if (current is null)
            {
                var clash = foreign.FirstOrDefault(g =>
                    string.Equals(g.DisplayName, want.DisplayName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(g.MailNickname, want.MailNickname, StringComparison.OrdinalIgnoreCase));
                if (clash is not null)
                {
                    drafts.Add(Draft.ForGroup(SyncPriority.High, SyncAction.GroupConflict, want, clash.ObjectId, false,
                        [new SyncChange(GroupFields.DisplayName, clash.DisplayName, want.DisplayName)], "name_conflict", [unitEvidence],
                        "同じ名前（または mailNickname）のグループが既にあります。jin-ji の印が無いので変更しません。接頭辞を変えるか、このグループの説明に印を書いて引き継いでください。") with
                    {
                        DirectoryIndex = clash.SourceIndex,
                    });
                    continue;
                }

                drafts.Add(Draft.ForGroup(SyncPriority.Normal, SyncAction.CreateGroup, want, null, true,
                    [
                        new SyncChange(GroupFields.DisplayName, null, want.DisplayName),
                        new SyncChange(GroupFields.MailNickname, null, want.MailNickname),
                        new SyncChange(GroupFields.Description, null, want.Description),
                        new SyncChange(GroupFields.Type, null, TypeCode(options.GroupType)),
                    ],
                    "group_missing", [unitEvidence], null));
            }
            else
            {
                if (current.Unified != (options.GroupType == OrgGroupType.Microsoft365))
                {
                    drafts.Add(Draft.ForGroup(SyncPriority.High, SyncAction.GroupConflict, want, current.ObjectId, false,
                        [new SyncChange(GroupFields.Type, TypeCode(current.Unified ? OrgGroupType.Microsoft365 : OrgGroupType.Security), TypeCode(options.GroupType))],
                        "type_mismatch", [unitEvidence],
                        "グループの種類が設定と違います。種類は作った後で変えられないので、変更しません（メンバーは合わせます）。") with
                    {
                        DirectoryIndex = current.SourceIndex,
                    });
                }

                var changes = new List<SyncChange>();
                if (!string.Equals(current.DisplayName, want.DisplayName, StringComparison.Ordinal))
                {
                    changes.Add(new SyncChange(GroupFields.DisplayName, current.DisplayName, want.DisplayName));
                }

                if (!string.Equals(current.Description, want.Description, StringComparison.Ordinal))
                {
                    changes.Add(new SyncChange(GroupFields.Description, current.Description, want.Description));
                }

                if (changes.Count > 0)
                {
                    drafts.Add(Draft.ForGroup(SyncPriority.Normal, SyncAction.UpdateGroup, want, current.ObjectId, true, changes, "group_differs", [unitEvidence], null) with
                    {
                        DirectoryIndex = current.SourceIndex,
                    });
                }
            }

            // メンバー
            var wantObjects = new Dictionary<string, Guid>(StringComparer.Ordinal);
            foreach (var personId in want.Members)
            {
                var accounts = objectsByPerson.GetValueOrDefault(personId) ?? [];
                if (accounts.Count == 0)
                {
                    withoutAccount[personId] = withoutAccount.GetValueOrDefault(personId) + 1;
                }
                else if (accounts.Count > 1)
                {
                    manyAccounts[personId] = manyAccounts.GetValueOrDefault(personId) + 1;
                }
                else if (!Excluded(personId, accounts[0]))
                {
                    wantObjects[accounts[0]] = personId;
                }
            }

            var members = current?.MemberIds;
            var known = current is null || members is not null;
            var has = members?.ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var (objectId, personId) in wantObjects.Where(w => !has.Contains(w.Key)))
            {
                drafts.Add(Draft.ForMember(SyncPriority.Normal, SyncAction.AddGroupMember, want, current?.ObjectId, personId, objectId, true,
                    new SyncChange(GroupFields.Member, known ? "false" : null, "true", known), "member_missing",
                    [new SyncEvidence(GroupFields.Member, "assignment", want.Unit.Id.ToString("D"), want.MemberSource.GetValueOrDefault(personId))], null));
            }

            if (current is not null && members is null)
            {
                warnings.Add($"グループ {current.DisplayName} のメンバーの一覧が入力にありません。外す項目は作っていません。");
            }

            var unknownMembers = 0;
            foreach (var objectId in has.Where(m => !wantObjects.ContainsKey(m)))
            {
                if (personByObject.TryGetValue(objectId, out var personId))
                {
                    if (Excluded(personId, objectId))
                    {
                        continue;
                    }

                    drafts.Add(Draft.ForMember(SyncPriority.High, SyncAction.RemoveGroupMember, want, current!.ObjectId, personId, objectId, true,
                        new SyncChange(GroupFields.Member, "true", "false"), "member_not_in_org",
                        [new SyncEvidence(GroupFields.Member, "assignment", want.Unit.Id.ToString("D"), "業務日付時点で、この組織（の範囲）に所属がありません")], null));
                }
                else if (options.RemoveUnknownMembers && !excludedObjects.Contains(objectId))
                {
                    drafts.Add(Draft.ForMember(SyncPriority.High, SyncAction.RemoveGroupMember, want, current!.ObjectId, null, objectId, true,
                        new SyncChange(GroupFields.Member, "true", "false"), "member_unknown",
                        [new SyncEvidence(GroupFields.Member, "settings", "removeUnknownMembers", "true")], "jin-ji の人に対応付かないメンバーです（設定により外します）。"));
                }
                else
                {
                    unknownMembers++;
                }
            }

            if (unknownMembers > 0)
            {
                drafts.Add(Draft.ForGroup(SyncPriority.Info, SyncAction.GroupNote, want, current!.ObjectId, false, [], "unknown_members", [unitEvidence],
                    $"jin-ji の人に対応付かないメンバーが {unknownMembers} 件あります（手で加えたアカウントなど）。外しません。") with
                {
                    DirectoryIndex = current.SourceIndex,
                });
            }

            // オーナー（組織長）とチームは Microsoft 365 グループだけ
            if (options.GroupType != OrgGroupType.Microsoft365 || (current is not null && !current.Unified))
            {
                continue;
            }

            var headObject = want.Unit.HeadPersonId is { } head && objectsByPerson.GetValueOrDefault(head) is { Count: 1 } headAccounts && !Excluded(head, headAccounts[0])
                ? headAccounts[0]
                : null;
            var owners = current?.OwnerIds;
            var ownerKnown = current is null || owners is not null;
            var hasOwner = owners is { Count: > 0 };
            if (headObject is not null && !(owners?.Contains(headObject, StringComparer.Ordinal) ?? false))
            {
                drafts.Add(Draft.ForMember(SyncPriority.Normal, SyncAction.AddGroupOwner, want, current?.ObjectId, want.Unit.HeadPersonId, headObject, true,
                    new SyncChange(GroupFields.Owner, ownerKnown ? "false" : null, "true", ownerKnown), "owner_missing",
                    [new SyncEvidence(GroupFields.Owner, "org_unit", want.Unit.Id.ToString("D"), "組織長")], null));
                hasOwner = true;
            }

            if (options.CreateTeam && current?.HasTeam != true)
            {
                if (hasOwner)
                {
                    var teamKnown = current is null || current.HasTeam is not null;
                    drafts.Add(Draft.ForGroup(SyncPriority.Normal, SyncAction.CreateTeam, want, current?.ObjectId, true,
                        [new SyncChange(GroupFields.Team, teamKnown ? "false" : null, "true", teamKnown)], "team_missing", [unitEvidence], null));
                }
                else
                {
                    drafts.Add(Draft.ForGroup(SyncPriority.Info, SyncAction.GroupNote, want, current?.ObjectId, false, [], "team_without_owner", [unitEvidence],
                        "チームにはオーナーが要りますが、組織長がいないか、組織長に Entra のアカウントの対応がありません。チームは作りません。"));
                }
            }
        }

        // 印はあるが、対象の組織に無いグループ（廃止・対象外）
        foreach (var (orgId, group) in owned.Where(o => !desired.ContainsKey(o.Key) && !duplicated.Contains(o.Key)).OrderBy(o => o.Value.DisplayName, StringComparer.Ordinal))
        {
            if (snapshot.FindUnit(orgId) is { } unit)
            {
                drafts.Add(new Draft(SyncPriority.Info, SyncAction.GroupNote, null, group.ObjectId, unit.Id, group.ObjectId, group.DisplayName, false, [], "out_of_scope",
                    [new SyncEvidence(GroupFields.DisplayName, "org_unit", unit.Id.ToString("D"), unit.Name)],
                    "設定の対象（法人・段）ではない組織のグループです。変更しません。", group.SourceIndex));
                continue;
            }

            // 廃止された組織は業務日付時点のデータに無い。組織の ID は印からしか分からないので、org_unit への参照は持たない
            var name = group.DisplayName ?? string.Empty;
            var description = group.Description ?? string.Empty;
            var changes = new List<SyncChange>();
            if (!name.StartsWith(options.RetiredPrefix, StringComparison.Ordinal))
            {
                changes.Add(new SyncChange(GroupFields.DisplayName, group.DisplayName, options.RetiredPrefix + name));
            }

            if (!description.Contains(RetiredNote, StringComparison.Ordinal))
            {
                changes.Add(new SyncChange(GroupFields.Description, group.Description, $"{RetiredNote} {description}".Trim()));
            }

            var evidence = new SyncEvidence(GroupFields.DisplayName, "org_unit", orgId.ToString("D"), "業務日付時点で有効な組織がありません（廃止）");
            if (changes.Count > 0)
            {
                drafts.Add(new Draft(SyncPriority.Normal, SyncAction.RetireGroup, null, group.ObjectId, null, group.ObjectId, group.DisplayName, true, changes, "org_abolished", [evidence],
                    "組織が廃止されています。グループは削除せず、廃止の印を付けます。メンバーは引き継ぎのために残します。", group.SourceIndex));
            }

            // メンバーは残す（引き継ぎ先が決まるまで、権限を持ったままにする。空のグループだけを残しても意味が無い）。
            // 外すのは退職した人だけ（在籍していない人に権限を残さない）
            foreach (var objectId in group.MemberIds ?? [])
            {
                if (personByObject.TryGetValue(objectId, out var personId) && !Excluded(personId, objectId)
                    && (snapshot.FindPerson(personId) is not { } member || member.IsTerminatedOn(input.Today)))
                {
                    drafts.Add(new Draft(SyncPriority.High, SyncAction.RemoveGroupMember, personId, objectId, null, group.ObjectId, group.DisplayName, true,
                        [new SyncChange(GroupFields.Member, "true", "false")], "member_retired",
                        [new SyncEvidence(GroupFields.Member, "person", personId.ToString("D"), "業務日付時点で在籍していません")], null, group.SourceIndex));
                }
            }
        }

        foreach (var (personId, count) in withoutAccount.OrderBy(p => snapshot.FindPerson(p.Key)?.EmployeeNumber, StringComparer.Ordinal))
        {
            drafts.Add(new Draft(SyncPriority.Info, SyncAction.GroupNote, personId, null, null, null, null, false, [], "member_without_account",
                [new SyncEvidence(GroupFields.Member, "external_identity", null, null)],
                $"Entra のアカウントの対応が無いので、グループに加えられません（対象のグループ {count} 件）。", null));
        }

        foreach (var (personId, count) in manyAccounts.OrderBy(p => snapshot.FindPerson(p.Key)?.EmployeeNumber, StringComparer.Ordinal))
        {
            drafts.Add(new Draft(SyncPriority.Info, SyncAction.GroupNote, personId, null, null, null, null, false, [], "member_many_accounts",
                [new SyncEvidence(GroupFields.Member, "external_identity", null, null)],
                $"Entra のアカウントの対応が複数あるので、グループに加えません（対象のグループ {count} 件）。", null));
        }

        var sameName = desired.Values.GroupBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (sameName.Count > 0)
        {
            warnings.Add($"同じ表示名になる組織があります: {string.Join("、", sameName.Take(5))}（displayNameFormat に {{code}} や {{company}} を入れると区別できます）。");
        }

        // 既にあるグループを対象にする項目には、計画時の印の組織 ID を持たせる（反映の直前に照合する）
        var markerByGroup = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in input.Groups)
        {
            if (OrgGroupSettings.ReadMarker(group.Description) is { } marked)
            {
                markerByGroup.TryAdd(group.ObjectId, marked);
            }
        }

        var items = drafts
            .OrderBy(d => d.Priority)
            .ThenBy(d => d.Action)
            .ThenBy(d => d.GroupDisplayName ?? "~", StringComparer.Ordinal)
            .ThenBy(d => d.PersonId is { } p && snapshot.FindPerson(p) is { } r ? r.EmployeeNumber : "~", StringComparer.Ordinal)
            .ThenBy(d => d.ObjectId ?? string.Empty, StringComparer.Ordinal)
            .Select((d, i) => new SyncPlanItem
            {
                Id = EntityId.New(),
                Sequence = i + 1,
                Priority = d.Priority,
                Action = d.Action,
                PersonId = d.PersonId,
                EmployeeNumber = d.PersonId is { } p && snapshot.FindPerson(p) is { } r ? r.EmployeeNumber : null,
                ObjectId = d.ObjectId,
                OrgUnitId = d.OrgUnitId,
                GroupObjectId = d.GroupObjectId,
                GroupMarkerOrgUnitId = d.GroupObjectId is { } target && markerByGroup.TryGetValue(target, out var marker) ? marker : null,
                GroupDisplayName = d.GroupDisplayName,
                InScript = d.InScript,
                Changes = d.Changes,
                Basis = new SyncBasis { Code = d.Code, Jinji = d.Evidence, DirectoryIndex = d.DirectoryIndex },
                Note = d.Note,
            })
            .ToList();
        return new SyncPlanDraft(items, Fingerprint(desired, input.Identities, settings), warnings);
    }

    public string Fingerprint(OrgSnapshot snapshot, IReadOnlyList<ExternalIdentityLink> identities, SyncSettings settings, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(settings);
        var options = settings.OrgGroups ?? throw new FormatException("同期の設定に orgGroups がありません。");
        return Fingerprint(DesiredGroups(snapshot, settings, options, today, []), identities, settings);
    }

    /// <summary>廃止された組織のグループの説明の先頭に付ける語。</summary>
    internal const string RetiredNote = "（廃止された組織）";

    internal static string TypeCode(OrgGroupType type) => type == OrgGroupType.Microsoft365 ? "microsoft365" : "security";

    /// <summary>1 つの組織のグループのあるべき状態。</summary>
    private sealed record DesiredGroup(
        OrgUnit Unit,
        string DisplayName,
        string MailNickname,
        string Description,
        IReadOnlySet<Guid> Members,
        IReadOnlyDictionary<Guid, string> MemberSource);

    private sealed record Draft(
        SyncPriority Priority,
        SyncAction Action,
        Guid? PersonId,
        string? ObjectId,
        Guid? OrgUnitId,
        string? GroupObjectId,
        string? GroupDisplayName,
        bool InScript,
        IReadOnlyList<SyncChange> Changes,
        string Code,
        IReadOnlyList<SyncEvidence> Evidence,
        string? Note,
        int? DirectoryIndex)
    {
        /// <summary>グループそのものの項目（対象のオブジェクトはグループ。作成では null）。</summary>
        public static Draft ForGroup(SyncPriority priority, SyncAction action, DesiredGroup want, string? groupObjectId, bool inScript, IReadOnlyList<SyncChange> changes, string code,
            IReadOnlyList<SyncEvidence> evidence, string? note) =>
            new(priority, action, null, groupObjectId, want.Unit.Id, groupObjectId, want.DisplayName, inScript, changes, code, evidence, note, null);

        /// <summary>メンバー・オーナーの項目（対象のオブジェクトはユーザー）。</summary>
        public static Draft ForMember(SyncPriority priority, SyncAction action, DesiredGroup want, string? groupObjectId, Guid? personId, string userObjectId, bool inScript, SyncChange change,
            string code, IReadOnlyList<SyncEvidence> evidence, string? note) =>
            new(priority, action, personId, userObjectId, want.Unit.Id, groupObjectId, want.DisplayName, inScript, [change], code, evidence, note, null);
    }

    /// <summary>対象の組織ごとのあるべきグループ。</summary>
    private static Dictionary<Guid, DesiredGroup> DesiredGroups(OrgSnapshot snapshot, SyncSettings settings, OrgGroupSettings options, DateOnly today, List<string> warnings)
    {
        var scope = new HashSet<Guid>();
        if (settings.LegalEntities.Count == 0)
        {
            scope = [.. snapshot.LegalEntities.Select(e => e.Id)];
        }
        else
        {
            foreach (var code in settings.LegalEntities)
            {
                if (snapshot.LegalEntities.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase)) is { } entity)
                {
                    scope.Add(entity.Id);
                }
                else
                {
                    warnings.Add($"設定の対象の法人 {code} が jin-ji にありません。");
                }
            }
        }

        var targets = snapshot.Units
            .Where(u => scope.Contains(u.LegalEntityId) && (options.Levels.Count == 0 || (u.LevelKey is { } key && options.Levels.Contains(key, StringComparer.Ordinal))))
            .ToDictionary(u => u.Id);
        var members = targets.Keys.ToDictionary(id => id, _ => new HashSet<Guid>());
        var sources = targets.Keys.ToDictionary(id => id, _ => new Dictionary<Guid, string>());
        foreach (var person in snapshot.People.Where(p => !p.IsTerminatedOn(today)))
        {
            foreach (var assignment in snapshot.AssignmentsOf(person.Id))
            {
                var kind = assignment.Secondment != SecondmentType.None ? "secondment" : assignment.Type == AssignmentType.Primary ? "primary" : "concurrent";
                if ((kind == "secondment" && !options.IncludeSecondment) || (kind == "concurrent" && !options.IncludeConcurrent) || snapshot.FindUnit(assignment.OrgUnitId) is null)
                {
                    continue;
                }

                // AncestorsOf は自分自身から上位へ
                var chain = options.Members == OrgGroupMemberScope.Subtree ? snapshot.AncestorsOf(assignment.OrgUnitId) : [assignment.OrgUnitId];
                foreach (var unitId in chain.Where(targets.ContainsKey))
                {
                    members[unitId].Add(person.Id);
                    sources[unitId].TryAdd(person.Id, $"{kind}:{assignment.OrgUnitId:D}");
                }
            }
        }

        var result = new Dictionary<Guid, DesiredGroup>();
        foreach (var unit in targets.Values)
        {
            var entity = snapshot.FindLegalEntity(unit.LegalEntityId);
            var displayName = options.FormatDisplayName(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["name"] = unit.Name,
                ["code"] = unit.Code,
                ["company"] = entity?.Name,
                ["entity_code"] = entity?.Code,
            });
            result[unit.Id] = new DesiredGroup(
                unit,
                displayName,
                options.FormatMailNickname(entity?.Code ?? "x", unit.Code),
                $"{unit.Name}（jin-ji が管理。手で変更しないでください） {OrgGroupSettings.Marker(unit.Id)}",
                members[unit.Id],
                sources[unit.Id]);
        }

        return result;
    }

    private static string Fingerprint(Dictionary<Guid, DesiredGroup> desired, IReadOnlyList<ExternalIdentityLink> identities, SyncSettings settings)
    {
        var canonical = new
        {
            settings = settings.Sha256(),
            groups = desired.Values.OrderBy(d => d.Unit.Id).Select(d => new
            {
                id = d.Unit.Id,
                name = d.DisplayName,
                mail = d.MailNickname,
                description = d.Description,
                head = d.Unit.HeadPersonId,
                members = d.Members.Order(),
            }),
            identities = identities.OrderBy(l => l.PersonId).ThenBy(l => l.ExternalId, StringComparer.Ordinal).Select(l => new[] { l.PersonId.ToString("D"), l.ExternalId }),
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, FingerprintJson))));
    }
}
