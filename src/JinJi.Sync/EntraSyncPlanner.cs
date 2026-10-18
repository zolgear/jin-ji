using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JinJi.Application.Sync;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;

namespace JinJi.Sync;

/// <summary>
/// jin-ji の公開データ（業務日付時点）と Entra ID のユーザー一覧の差分から、同期計画の項目を作る（overview §8「同期」）。
/// </summary>
/// <remarks>
/// <para><b>あるべき値</b>（jin-ji 側。設定 <see cref="SyncSettings"/> で書式を決める）</para>
/// <list type="bullet">
/// <item>入力は業務日付時点のスナップショット（組織・所属・一時停止は業務日付に有効な行、属性は業務日付の版、入社前の人は含まない）。
/// 内示（発令日前の異動）・将来の一時停止・退職の予定は反映しない。退職は最終在籍日を過ぎてから</item>
/// <item>勤務先の所属: 主務（出向中で <see cref="SyncSettings.SecondmentUsesHostOrg"/> なら出向先の兼務）。部署名はその組織から上にたどった設定の段の組織の名前、
/// 役職はその所属の役職、会社名はその組織の法人名</item>
/// <item>上長: 勤務先の組織から上にたどり、本人以外の組織長が最初に見つかった組織の組織長（退職者は飛ばす）。そのアカウントが 1 つに決まるときだけ</item>
/// <item>アカウント: 退職済み・一時停止中なら無効、それ以外は有効</item>
/// <item>jin-ji に値が無い項目は消さない（取込と同じ「値が無いことでは消さない」。役職だけ設定で消せる）</item>
/// </list>
/// <para><b>項目</b>（優先度の順に並べる。IT 全般統制の項目を先頭にする）</para>
/// <list type="bullet">
/// <item>critical: 退職者のアカウントの無効化、一時停止中の人のサインイン停止、除外の設定で無効化されない退職者・一時停止中の人（警告）</item>
/// <item>high: 対応付けの無い有効なアカウント（棚卸しが必要）、無効なアカウントの再有効化（既定は推奨だけ）</item>
/// <item>normal: 属性の更新、上長の設定、アカウントの作成（推奨。既定はスクリプトに含めない）</item>
/// <item>info: 人ではないアカウント・除外・対象外の法人・無効で対応付けの無いアカウント</item>
/// </list>
/// <para>対応付けは外部 ID の対応（external_identity）だけで行う（名寄せは取込の役割。対応付けの無いアカウントは触らない）。</para>
/// </remarks>
public sealed class EntraSyncPlanner : ISyncPlanner
{
    private static readonly JsonSerializerOptions FingerprintJson = new() { WriteIndented = false };

    public SyncPlanDraft Plan(SyncPlanInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var settings = input.Settings;
        var snapshot = input.Snapshot;
        var warnings = new List<string>();
        var scope = Scope(snapshot, settings, warnings);
        var states = DesiredStates(snapshot, settings, input.Today, scope);
        WarnLevels(snapshot, settings, warnings);

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

        var inputObjects = input.Users.Select(u => u.ObjectId).ToHashSet(StringComparer.Ordinal);
        var drafts = new List<Draft>();
        var unknownFields = new SortedSet<string>(StringComparer.Ordinal);
        var unmappedManagers = 0;
        foreach (var user in input.Users)
        {
            var basis = new SyncBasis { DirectoryIndex = user.SourceIndex };
            var upn = user[EntraFields.UserPrincipalName] ?? user["mail"];
            if (!personByObject.TryGetValue(user.ObjectId, out var personId))
            {
                drafts.Add(UnmappedAccount(user, settings, basis, upn));
                continue;
            }

            var identity = new SyncEvidence("id", "external_identity", user.ObjectId, personId.ToString("D"));
            basis = basis with { Jinji = [identity] };
            var person = snapshot.FindPerson(personId);
            if (person is null)
            {
                // 外部 ID の対応はあるが、業務日付時点の人がいない（入社前・匿名化済みなど）
                drafts.Add(new Draft(user.AccountEnabled == true ? SyncPriority.High : SyncPriority.Info, SyncAction.UnmatchedAccount, personId, user.ObjectId, upn, false, [],
                    basis with { Code = "person_not_current" }, "jin-ji の人に対応付いていますが、業務日付時点の人がいません（入社前・匿名化済みなど）。アカウントの扱いを確認してください。"));
                continue;
            }

            if (!states.TryGetValue(personId, out var state))
            {
                drafts.Add(new Draft(SyncPriority.Info, SyncAction.ExcludedAccount, personId, user.ObjectId, upn, false, [], basis with { Code = "out_of_scope" },
                    "設定の対象の法人の人ではないので同期しません。"));
                continue;
            }

            if (Exclusion(settings, user, person) is { } exclusion)
            {
                drafts.Add(Excluded(user, state, exclusion, basis, upn));
                continue;
            }

            drafts.AddRange(AccountItems(user, state, settings, basis, upn));
            if (state.Retired)
            {
                // 退職者は無効化だけ（属性・上長は更新しない）
                continue;
            }

            if (AttributeItem(user, state, settings, basis, upn, unknownFields) is { } attributes)
            {
                drafts.Add(attributes);
            }

            if (settings.Syncs(EntraFields.Manager) && state.ManagerPersonId is { } managerId)
            {
                var managerObjects = objectsByPerson.GetValueOrDefault(managerId) ?? [];
                if (managerObjects.Count == 1)
                {
                    var desired = managerObjects[0];
                    var known = user.Knows(EntraFields.Manager);
                    if (!known || !string.Equals(user[EntraFields.Manager], desired, StringComparison.Ordinal))
                    {
                        drafts.Add(new Draft(
                            SyncPriority.Normal,
                            SyncAction.SetManager,
                            personId,
                            user.ObjectId,
                            upn,
                            true,
                            [new SyncChange(EntraFields.Manager, known ? user[EntraFields.Manager] : null, desired, known)],
                            basis with
                            {
                                Code = "manager_differs",
                                Jinji = [identity, .. state.ManagerEvidence, new SyncEvidence(EntraFields.Manager, "external_identity", desired, managerId.ToString("D"))],
                            },
                            known ? null : "入力に上長の現在値がありません（$expand=manager で取得してください）。あるべき上長になっていなければ設定します。"));
                    }
                }
                else
                {
                    unmappedManagers++;
                }
            }
        }

        // アカウントの無い在籍者（作成の推奨）
        foreach (var state in states.Values.Where(s => !s.Retired && !objectsByPerson.ContainsKey(s.Person.Id)).OrderBy(s => s.Person.EmployeeNumber, StringComparer.Ordinal))
        {
            if (settings.Exclude.Any(e => e.EmployeeNumber is { } number && string.Equals(number, state.Person.EmployeeNumber, StringComparison.Ordinal)))
            {
                continue;
            }

            drafts.Add(CreateRecommendation(state, settings));
        }

        // 対応付いているのに入力に無いアカウント（一覧が一部だけの可能性）
        var missingFromInput = input.Identities.Count(l => Guid.TryParse(l.ExternalId, CultureInfo.InvariantCulture, out var g) && !inputObjects.Contains(g.ToString("D")) && states.ContainsKey(l.PersonId));
        if (missingFromInput > 0)
        {
            warnings.Add($"jin-ji で対応付いている Entra のアカウントのうち {missingFromInput} 件が入力にありません（一覧が一部だけか、Entra で削除済み）。");
        }

        if (unknownFields.Count > 0)
        {
            warnings.Add($"入力に無い項目は計画していません: {string.Join(", ", unknownFields)}（$select に含めてください）。");
        }

        if (unmappedManagers > 0)
        {
            warnings.Add($"上長のアカウントが 1 つに決まらない（対応付けが無い・複数ある）ため上長を計画しなかった人: {unmappedManagers} 人。");
        }

        var items = drafts
            .OrderBy(d => d.Priority)
            .ThenBy(d => d.Action)
            .ThenBy(d => d.PersonId is { } p && snapshot.FindPerson(p) is { } r ? r.EmployeeNumber : "~", StringComparer.Ordinal)
            .ThenBy(d => d.ObjectId ?? d.UserPrincipalName ?? string.Empty, StringComparer.Ordinal)
            .Select((d, i) => new SyncPlanItem
            {
                Id = EntityId.New(),
                Sequence = i + 1,
                Priority = d.Priority,
                Action = d.Action,
                PersonId = d.PersonId,
                EmployeeNumber = d.PersonId is { } p && snapshot.FindPerson(p) is { } r ? r.EmployeeNumber : null,
                ObjectId = d.ObjectId,
                UserPrincipalName = d.UserPrincipalName,
                InScript = d.InScript,
                Changes = d.Changes,
                Basis = d.Basis,
                Note = d.Note,
            })
            .ToList();
        return new SyncPlanDraft(items, Fingerprint(states, input.Identities, settings), warnings);
    }

    public string Fingerprint(OrgSnapshot snapshot, IReadOnlyList<ExternalIdentityLink> identities, SyncSettings settings, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(settings);
        var states = DesiredStates(snapshot, settings, today, Scope(snapshot, settings, []));
        return Fingerprint(states, identities, settings);
    }

    // -----------------------------------------------------------------------
    // あるべき値
    // -----------------------------------------------------------------------

    /// <summary>1 人のあるべき値。</summary>
    private sealed record DesiredState(
        PersonRecord Person,
        bool Retired,
        bool Suspended,
        IReadOnlyDictionary<string, string?> Attributes,
        IReadOnlyDictionary<string, IReadOnlyList<SyncEvidence>> Evidence,
        IReadOnlyList<SyncEvidence> AccountEvidence,
        Guid? ManagerPersonId,
        IReadOnlyList<SyncEvidence> ManagerEvidence);

    /// <summary>対象の法人（雇用元）。設定が空ならすべて。</summary>
    private static HashSet<Guid> Scope(OrgSnapshot snapshot, SyncSettings settings, List<string> warnings)
    {
        if (settings.LegalEntities.Count == 0)
        {
            return [.. snapshot.LegalEntities.Select(e => e.Id)];
        }

        var scope = new HashSet<Guid>();
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

        return scope;
    }

    private static void WarnLevels(OrgSnapshot snapshot, SyncSettings settings, List<string> warnings)
    {
        foreach (var entity in snapshot.LegalEntities)
        {
            if (settings.DepartmentLevelFor(entity.Code) is { } level && snapshot.LevelSchemeOf(entity.Id).Find(level) is null)
            {
                warnings.Add($"部署名の段 {level} は法人 {entity.Code} の段の定義にありません（勤務先の組織の名前を使います）。");
            }
        }
    }

    private static Dictionary<Guid, DesiredState> DesiredStates(OrgSnapshot snapshot, SyncSettings settings, DateOnly today, HashSet<Guid> scope)
    {
        var states = new Dictionary<Guid, DesiredState>();
        foreach (var person in snapshot.People.Where(p => scope.Contains(p.LegalEntityId)))
        {
            var version = person.Current;
            var assignments = snapshot.AssignmentsOf(person.Id);
            var primary = assignments.FirstOrDefault(a => a.Type == AssignmentType.Primary);
            var host = settings.SecondmentUsesHostOrg ? assignments.FirstOrDefault(a => a.Secondment == SecondmentType.Outbound) : null;
            var work = host ?? primary ?? assignments.FirstOrDefault();
            var workUnit = work is null ? null : snapshot.FindUnit(work.OrgUnitId);
            var entity = snapshot.FindLegalEntity(workUnit?.LegalEntityId ?? person.LegalEntityId);
            var level = entity is null ? settings.DepartmentLevel : settings.DepartmentLevelFor(entity.Code);
            var departmentUnit = workUnit is null ? null : DepartmentUnit(snapshot, workUnit, level);

            var versionRef = version.VersionId.ToString("D");
            var workRef = work is null ? null : $"{(host is not null ? "secondment" : Code(work.Type))}:{work.OrgUnitId:D}";
            var values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["family"] = version.FamilyName,
                ["given"] = version.GivenName,
                ["family_kana"] = version.FamilyNameKana,
                ["given_kana"] = version.GivenNameKana,
                ["family_latin"] = version.FamilyNameLatin,
                ["given_latin"] = version.GivenNameLatin,
                ["display_name"] = version.DisplayName,
                ["department"] = departmentUnit?.Name,
                ["company"] = entity?.Name,
                ["employee_number"] = person.EmployeeNumber,
            };

            var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
            var evidence = new Dictionary<string, IReadOnlyList<SyncEvidence>>(StringComparer.Ordinal);
            void Set(string field, string? value, params SyncEvidence[] sources)
            {
                if (settings.Syncs(field))
                {
                    attributes[field] = string.IsNullOrWhiteSpace(value) ? null : value;
                    evidence[field] = sources;
                }
            }

            Set(EntraFields.DisplayName, settings.FormatDisplayName(values),
                new SyncEvidence(EntraFields.DisplayName, "person_version", versionRef, $"{version.FamilyName} {version.GivenName}".Trim()),
                new SyncEvidence(EntraFields.DisplayName, "settings", "displayNameFormat", settings.DisplayNameFormat));
            Set(EntraFields.GivenName, version.GivenName, new SyncEvidence(EntraFields.GivenName, "person_version", versionRef, version.GivenName));
            Set(EntraFields.Surname, version.FamilyName, new SyncEvidence(EntraFields.Surname, "person_version", versionRef, version.FamilyName));
            Set(EntraFields.Department, departmentUnit?.Name,
                new SyncEvidence(EntraFields.Department, "assignment", workRef, workUnit?.Name),
                new SyncEvidence(EntraFields.Department, "org_unit", departmentUnit?.Id.ToString("D"), level is null ? "勤務先の組織" : $"段 {level}"));
            if (settings.JobTitle == JobTitleMode.Primary)
            {
                Set(EntraFields.JobTitle, work?.PositionTitle, new SyncEvidence(EntraFields.JobTitle, "assignment", workRef, work?.PositionTitle));
            }

            if (settings.CompanyName == CompanyNameMode.LegalEntityName)
            {
                Set(EntraFields.CompanyName, entity?.Name, new SyncEvidence(EntraFields.CompanyName, "legal_entity", entity?.Id.ToString("D"), entity?.Code));
            }

            // 勤務地は人の拠点の名前（人ごとの拠点、無ければ主務の組織〔か上位組織〕の拠点。API の人の site と同じ規則。0017 で自由記述の勤務地を置き換えた）
            var site = snapshot.SiteOf(person.Id, version);
            Set(
                EntraFields.OfficeLocation,
                site?.Site.Name,
                site is null || !site.Inherited
                    ? new SyncEvidence(EntraFields.OfficeLocation, "person_version", versionRef, site is null ? "拠点なし" : $"人の拠点 {site.Site.Code}")
                    : new SyncEvidence(EntraFields.OfficeLocation, "site", site.Site.Id.ToString("D"), $"主務の組織の拠点 {site.Site.Code}"));
            Set(EntraFields.EmployeeId, person.EmployeeNumber, new SyncEvidence(EntraFields.EmployeeId, "person", person.Id.ToString("D"), person.EmployeeNumber));

            var retired = person.IsTerminatedOn(today);
            var suspended = !retired && person.IsSuspended;
            IReadOnlyList<SyncEvidence> accountEvidence = retired
                ? [new SyncEvidence(EntraFields.AccountEnabled, "person_version", versionRef, $"退職年月日 {version.TerminationDate:yyyy-MM-dd}")]
                : suspended
                    ? [new SyncEvidence(EntraFields.AccountEnabled, "account_suspension", person.Id.ToString("D"), $"一時停止中（開始 {person.Suspension!.From:yyyy-MM-dd}）")]
                    : [new SyncEvidence(EntraFields.AccountEnabled, "person", person.Id.ToString("D"), "在籍・一時停止なし")];

            var (managerId, managerUnit) = workUnit is null ? (null, null) : Manager(snapshot, person.Id, workUnit, today);
            IReadOnlyList<SyncEvidence> managerEvidence = managerId is null
                ? []
                : [new SyncEvidence(EntraFields.Manager, "org_unit", managerUnit!.Value.ToString("D"), $"組織長 {managerId:D}")];

            states[person.Id] = new DesiredState(person, retired, suspended, attributes, evidence, accountEvidence, managerId, managerEvidence);
        }

        return states;
    }

    /// <summary>部署名に使う組織: 勤務先の組織から上にたどり、段のキーが一致する最初の組織。無ければ勤務先の組織そのもの。</summary>
    private static OrgUnit DepartmentUnit(OrgSnapshot snapshot, OrgUnit workUnit, string? level)
    {
        if (level is null)
        {
            return workUnit;
        }

        foreach (var id in snapshot.AncestorsOf(workUnit.Id))
        {
            if (snapshot.FindUnit(id) is { } unit && string.Equals(unit.LevelKey, level, StringComparison.Ordinal))
            {
                return unit;
            }
        }

        return workUnit;
    }

    /// <summary>上長: 勤務先の組織から上にたどり、本人以外で退職していない組織長が最初に見つかった組織の組織長。</summary>
    private static (Guid? Manager, Guid? Unit) Manager(OrgSnapshot snapshot, Guid personId, OrgUnit workUnit, DateOnly today)
    {
        foreach (var id in snapshot.AncestorsOf(workUnit.Id))
        {
            if (snapshot.FindUnit(id)?.HeadPersonId is { } head && head != personId && snapshot.FindPerson(head) is { } manager && !manager.IsTerminatedOn(today))
            {
                return (head, id);
            }
        }

        return (null, null);
    }

    private static string Fingerprint(Dictionary<Guid, DesiredState> states, IReadOnlyList<ExternalIdentityLink> identities, SyncSettings settings)
    {
        var canonical = new
        {
            settings = settings.Sha256(),
            people = states.Values.OrderBy(s => s.Person.Id).Select(s => new
            {
                id = s.Person.Id,
                number = s.Person.EmployeeNumber,
                retired = s.Retired,
                suspended = s.Suspended,
                attributes = s.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new[] { a.Key, a.Value }),
                manager = s.ManagerPersonId,
            }),
            identities = identities.OrderBy(l => l.PersonId).ThenBy(l => l.ExternalId, StringComparer.Ordinal).Select(l => new[] { l.PersonId.ToString("D"), l.ExternalId }),
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, FingerprintJson))));
    }

    // -----------------------------------------------------------------------
    // 項目
    // -----------------------------------------------------------------------

    private sealed record Draft(
        SyncPriority Priority,
        SyncAction Action,
        Guid? PersonId,
        string? ObjectId,
        string? UserPrincipalName,
        bool InScript,
        IReadOnlyList<SyncChange> Changes,
        SyncBasis Basis,
        string? Note);

    private static Draft UnmappedAccount(DirectoryUser user, SyncSettings settings, SyncBasis basis, string? upn)
    {
        if (NonPersonAccount.Looks(user[EntraFields.EmployeeId], user[EntraFields.Surname], user[EntraFields.GivenName], user[EntraFields.DisplayName], user["mail"]))
        {
            return new Draft(SyncPriority.Info, SyncAction.ExcludedAccount, null, user.ObjectId, upn, false, [], basis with { Code = "non_person" },
                "人ではないアカウント（共有メールボックス・サービスアカウント・会議室など）とみなし、同期しません。");
        }

        if (settings.Exclude.Any(e => Matches(e, user)))
        {
            return new Draft(SyncPriority.Info, SyncAction.ExcludedAccount, null, user.ObjectId, upn, false, [], basis with { Code = "excluded_by_settings" },
                "設定で除外したアカウントです。");
        }

        return user.AccountEnabled == true
            ? new Draft(SyncPriority.High, SyncAction.UnmatchedAccount, null, user.ObjectId, upn, false, [], basis with { Code = "unmatched_enabled" },
                "jin-ji の人に対応付いていない有効なアカウントです。取込（Entra ID）の要確認を解決するか、不要なら無効にしてください（棚卸し）。")
            : new Draft(SyncPriority.Info, SyncAction.UnmatchedAccount, null, user.ObjectId, upn, false, [], basis with { Code = "unmatched_disabled" },
                "jin-ji の人に対応付いていない無効なアカウントです。");
    }

    private static SyncExclusion? Exclusion(SyncSettings settings, DirectoryUser user, PersonRecord person) =>
        settings.Exclude.FirstOrDefault(e => Matches(e, user) || (e.EmployeeNumber is { } number && string.Equals(number, person.EmployeeNumber, StringComparison.Ordinal)));

    private static bool Matches(SyncExclusion exclusion, DirectoryUser user) =>
        (exclusion.ObjectId is { } id && Guid.TryParse(id, CultureInfo.InvariantCulture, out var g) && g.ToString("D") == user.ObjectId)
        || (exclusion.UserPrincipalName is { } upn
            && (string.Equals(upn, user[EntraFields.UserPrincipalName], StringComparison.OrdinalIgnoreCase) || string.Equals(upn, user["mail"], StringComparison.OrdinalIgnoreCase)));

    private static Draft Excluded(DirectoryUser user, DesiredState state, SyncExclusion exclusion, SyncBasis basis, string? upn)
    {
        var note = $"設定で除外したアカウントです（{exclusion.Note ?? "理由の記載なし"}）。";
        if ((state.Retired || state.Suspended) && user.AccountEnabled == true)
        {
            // 除外したアカウントが退職者・一時停止中の人のものなら、無効化されないことを最上位で警告する（IT 全般統制）
            return new Draft(SyncPriority.Critical, SyncAction.ExcludedAccount, state.Person.Id, user.ObjectId, upn, false,
                [new SyncChange(EntraFields.AccountEnabled, "true", "false")],
                basis with { Code = state.Retired ? "excluded_retired_enabled" : "excluded_suspended_enabled", Jinji = [.. basis.Jinji, .. state.AccountEvidence] },
                note + (state.Retired ? "退職者ですがアカウントが有効のままです。" : "一時停止中ですがアカウントが有効のままです。") + "除外の設定を見直すか、手作業で無効にしてください。");
        }

        return new Draft(SyncPriority.Info, SyncAction.ExcludedAccount, state.Person.Id, user.ObjectId, upn, false, [], basis with { Code = "excluded_by_settings" }, note);
    }

    private static IEnumerable<Draft> AccountItems(DirectoryUser user, DesiredState state, SyncSettings settings, SyncBasis basis, string? upn)
    {
        if (!settings.Syncs(EntraFields.AccountEnabled) || user.AccountEnabled is not { } enabled)
        {
            yield break;
        }

        var withEvidence = basis with { Jinji = [.. basis.Jinji, .. state.AccountEvidence] };
        if (state.Retired && enabled)
        {
            yield return new Draft(SyncPriority.Critical, SyncAction.DisableAccount, state.Person.Id, user.ObjectId, upn, true,
                [new SyncChange(EntraFields.AccountEnabled, "true", "false")], withEvidence with { Code = "retired_enabled" },
                "退職済みなのにアカウントが有効です。無効にします。");
        }
        else if (state.Suspended && enabled)
        {
            yield return new Draft(SyncPriority.Critical, SyncAction.BlockSignIn, state.Person.Id, user.ObjectId, upn, true,
                [new SyncChange(EntraFields.AccountEnabled, "true", "false")], withEvidence with { Code = "suspended_enabled" },
                "一時停止中なのにアカウントが有効です。サインインを止めます（accountEnabled = false）。");
        }
        else if (!state.Retired && !state.Suspended && !enabled)
        {
            yield return new Draft(SyncPriority.High, SyncAction.EnableAccount, state.Person.Id, user.ObjectId, upn, settings.ReenableAccounts,
                [new SyncChange(EntraFields.AccountEnabled, "false", "true")], withEvidence with { Code = "active_disabled" },
                settings.ReenableAccounts
                    ? "在籍中で一時停止もないのにアカウントが無効です。有効に戻します。"
                    : "在籍中で一時停止もないのにアカウントが無効です（一時停止の終了など）。情シスの判断で無効にしている可能性があるため、既定ではスクリプトに含めません（設定 reenableAccounts）。");
        }
    }

    private static Draft? AttributeItem(DirectoryUser user, DesiredState state, SyncSettings settings, SyncBasis basis, string? upn, SortedSet<string> unknownFields)
    {
        var changes = new List<SyncChange>();
        var evidence = new List<SyncEvidence>(basis.Jinji);
        foreach (var field in EntraFields.Attributes)
        {
            if (!state.Attributes.TryGetValue(field, out var desired))
            {
                continue;
            }

            // jin-ji に値が無ければ消さない（役職だけ設定で消せる）
            if (desired is null && !(field == EntraFields.JobTitle && settings.ClearJobTitleWhenEmpty))
            {
                continue;
            }

            if (!user.Knows(field))
            {
                unknownFields.Add(field);
                continue;
            }

            var current = user[field];
            if (!string.Equals(current, desired, StringComparison.Ordinal))
            {
                changes.Add(new SyncChange(field, current, desired));
                evidence.AddRange(state.Evidence[field]);
            }
        }

        return changes.Count == 0
            ? null
            : new Draft(SyncPriority.Normal, SyncAction.UpdateAttributes, state.Person.Id, user.ObjectId, upn, true, changes,
                basis with { Code = "attributes_differ", Jinji = evidence }, null);
    }

    private static Draft CreateRecommendation(DesiredState state, SyncSettings settings)
    {
        var email = state.Person.Current.WorkEmail;
        var changes = new List<SyncChange>();
        if (email is not null)
        {
            changes.Add(new SyncChange(EntraFields.UserPrincipalName, null, email));
            changes.Add(new SyncChange(EntraFields.MailNickname, null, email.Split('@')[0]));
        }

        foreach (var field in EntraFields.Attributes)
        {
            if (state.Attributes.GetValueOrDefault(field) is { } value)
            {
                changes.Add(new SyncChange(field, null, value));
            }
        }

        // 作成するなら無効の状態で作る（初期パスワードは管理者が設定して有効にする）
        changes.Add(new SyncChange(EntraFields.AccountEnabled, null, "false"));
        var canCreate = email is not null;
        var evidence = state.Evidence.Values.SelectMany(e => e).Prepend(new SyncEvidence("id", "person", state.Person.Id.ToString("D"), state.Person.EmployeeNumber)).ToList();
        return new Draft(
            SyncPriority.Normal,
            SyncAction.CreateUser,
            state.Person.Id,
            null,
            email,
            settings.IncludeCreatesInScript && canCreate,
            changes,
            new SyncBasis { Code = "account_missing", Jinji = evidence },
            canCreate
                ? "在籍者ですが Entra にアカウントが（対応付いて）いません。作成を推奨します（作成は管理者の判断。既定ではスクリプトに含めません）。既にあるなら取込（Entra ID）で対応付けてください。"
                : "在籍者ですが Entra にアカウントが（対応付いて）いません。社用メールが無いので UPN を決められません。");
    }

    private static string Code<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
}
