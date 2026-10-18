using System.Globalization;
using System.Text.Json.Nodes;
using JinJi.Application.Ingest;
using JinJi.Domain.Organization;
using JinJi.Ingest.Matching;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest.Suggestions;

/// <summary>
/// LLM に送ってよい項目（送信内容の要約のキー）。DB の <c>jinji_llm_field_keys()</c>（llm_request_log の CHECK 制約）と同じ集合（テストで突き合わせる）。
/// </summary>
/// <remarks>
/// 送るのは判定に要るものだけ（§8）: 取込元の行の氏名（業務上の氏名として書かれたもの）・社員番号・発令の文面・組織名・役職、
/// 候補の人の業務上の氏名・社員番号・所属・役職、候補の組織の名称・上位組織名・段・組織長の有無・配下の組織名。
/// 社用メール・内線・入社日などは送らない。保持しない項目（ADR-0002）はそもそも作業領域に無い。
/// </remarks>
public static class LlmFields
{
    public const string RowName = "row.name";
    public const string RowEmployeeNumber = "row.employee_number";
    public const string RowActionText = "row.action_text";
    public const string RowOrgName = "row.org_name";
    public const string RowPositionTitle = "row.position_title";
    public const string SubjectOrgs = "subject.orgs";
    public const string SubjectTitle = "subject.title";
    public const string PersonName = "person.name";
    public const string PersonEmployeeNumber = "person.employee_number";
    public const string PersonOrgs = "person.orgs";
    public const string PersonTitle = "person.title";
    public const string OrgName = "org.name";
    public const string OrgParentName = "org.parent_name";
    public const string OrgLevel = "org.level";
    public const string OrgHasHead = "org.has_head";
    public const string OrgChildren = "org.children";
    public const string TargetOrgName = "target.org_name";
    public const string TargetOrgLevel = "target.org_level";

    /// <summary>全項目（DB の許可リストと同じ）。</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        RowName, RowEmployeeNumber, RowActionText, RowOrgName, RowPositionTitle, SubjectOrgs, SubjectTitle,
        PersonName, PersonEmployeeNumber, PersonOrgs, PersonTitle, OrgName, OrgParentName, OrgLevel, OrgHasHead, OrgChildren,
        TargetOrgName, TargetOrgLevel,
    };
}

/// <summary>LLM に回す 1 件の問い（要確認の提案 1 件に対応）。</summary>
public sealed class LlmQuestion
{
    public required ChangeProposalRecord Proposal { get; init; }

    /// <summary>問いの種類（<see cref="LlmQuestionKinds"/>）。</summary>
    public required string Kind { get; init; }

    /// <summary>問いの文（固定文。取込元の値を含まない）。</summary>
    public required string Instruction { get; init; }

    /// <summary>データ（<see cref="LlmFields"/> の項目だけ）。</summary>
    public required JsonObject Data { get; init; }

    /// <summary>人の候補のラベル（P1, P2, ...）→ jin-ji ID。</summary>
    public required IReadOnlyDictionary<string, Guid> People { get; init; }

    /// <summary>組織の候補のラベル（O1, O2, ...）→ jin-ji ID。</summary>
    public required IReadOnlyDictionary<string, Guid> Orgs { get; init; }

    /// <summary>送る項目名 → 件数（値は持たない）。</summary>
    public required IReadOnlyDictionary<string, int> SentFields { get; init; }

    /// <summary>LLM の理由の文から伏せる値（取込元の行の氏名・社員番号。戸籍名の可能性があるので作業領域に残さない）。</summary>
    public required IReadOnlyList<string> Redactions { get; init; }

    /// <summary>行番号（順序を決めるため）。</summary>
    public int RowNumber => Proposal.Basis.RowNumbers.Count > 0 ? Proposal.Basis.RowNumbers[0] : int.MaxValue;
}

/// <summary>
/// 要確認の提案から、LLM への問いを作る（決定的。同じ入力なら同じ問い）。
/// </summary>
/// <remarks>
/// 対象（取込の精度の評価で「ルールでは決まらない」とした 4 種）:
/// <list type="bullet">
/// <item>社員番号の誤記・欠落・同姓同名（辞令・人・Entra の名寄せの要確認）: 人の候補の順位</item>
/// <item>所属のあいまいな辞令（「製造第二部 課長」）: 配下の組織の候補の順位</item>
/// <item>段飛ばしの可能性のある組織（結合セルの補い）: 上位組織の候補の順位</item>
/// <item>Entra ID の旧組織名: 後継組織の候補の順位</item>
/// </list>
/// 人の候補は名寄せの候補に、氏名の名・姓の一致・社員番号の 1 文字違いの人を足す（旧姓・誤記に備える。上限 <see cref="LlmOptions.MaxCandidates"/>）。
/// 送るのは <see cref="LlmFields"/> の項目だけで、候補は jin-ji ID ではなくラベル（P1・O1）で示す。
/// </remarks>
public sealed class LlmQuestionBuilder
{
    private const int MaxChildren = 10;

    private readonly OrgSnapshot _snapshot;
    private readonly IReadOnlyDictionary<Guid, NormalizedRow> _rows;
    private readonly Dictionary<Guid, OrgInfo> _orgs = [];
    private readonly ILookup<Guid, OrgInfo> _children;
    private readonly EmployeeNumberNormalizer _numbers;
    private readonly int _maxCandidates;
    private readonly DateOnly _today;

    private sealed record OrgInfo(Guid Id, Guid Entity, string Name, string? LevelKey, Guid? ParentId, bool HasHead);

    public LlmQuestionBuilder(PlanContext context, IReadOnlyList<NormalizedRow> rows, IReadOnlyList<ChangeProposalRecord> proposals, int maxCandidates)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(proposals);
        _snapshot = context.Snapshot;
        _numbers = context.EmployeeNumbers;
        _today = context.Today;
        _maxCandidates = maxCandidates;
        _rows = rows.ToDictionary(r => r.StagingRowId);

        // 組織: jin-ji の組織と、同じ取込で新規に作る組織（組織図の取込では、まだ jin-ji に無い）
        foreach (var unit in _snapshot.Units)
        {
            _orgs[unit.Id] = new OrgInfo(unit.Id, unit.LegalEntityId, unit.Name, unit.LevelKey, unit.ParentId, unit.HeadPersonId is not null);
        }

        foreach (var created in proposals.Where(p => p is { TargetKind: ProposalTargetKind.OrgUnit, Operation: ProposalOperation.Create, TargetId: not null }))
        {
            var payload = created.Payload;
            if (payload.GetValueOrDefault(ProposalFields.Name) is { } name && payload.GetValueOrDefault(ProposalFields.LegalEntityId) is { } entity)
            {
                _orgs[created.TargetId!.Value] = new OrgInfo(
                    created.TargetId.Value,
                    Guid.Parse(entity, CultureInfo.InvariantCulture),
                    name,
                    payload.GetValueOrDefault(ProposalFields.LevelKey),
                    payload.GetValueOrDefault(ProposalFields.ParentOrgUnitId) is { } parent ? Guid.Parse(parent, CultureInfo.InvariantCulture) : null,
                    payload.GetValueOrDefault(ProposalFields.HeadPersonId) is not null);
            }
        }

        _children = _orgs.Values.Where(o => o.ParentId is not null).ToLookup(o => o.ParentId!.Value);
    }

    /// <summary>要確認の提案のうち、LLM に回すものの問いを作る（行番号の順）。</summary>
    public IReadOnlyList<LlmQuestion> Build(IEnumerable<ChangeProposalRecord> proposals)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        return [.. proposals.Where(p => p.Status == ProposalStatus.NeedsReview).Select(Build).OfType<LlmQuestion>().OrderBy(q => q.RowNumber).ThenBy(q => q.Proposal.Id)];
    }

    private LlmQuestion? Build(ChangeProposalRecord proposal)
    {
        var row = proposal.StagingRowIds.Count > 0 ? _rows.GetValueOrDefault(proposal.StagingRowIds[0]) : null;
        switch (proposal.TargetKind, proposal.Operation, proposal.Basis.Code)
        {
            case (ProposalTargetKind.PersonnelAction, ProposalOperation.Review, not SupplementCodes.UninterpretableAction) when row is not null:
            {
                var askPeople = proposal.Basis.Code is SupplementCodes.MissingEmployeeNumber or SupplementCodes.SuspectedEmployeeNumberTypo;
                var askOrgs = proposal.Basis.OrgCandidates.Count >= 2;
                if (!askPeople && !askOrgs)
                {
                    return null;
                }

                return Question(
                    proposal,
                    askOrgs ? LlmQuestionKinds.AmbiguousOrg : LlmQuestionKinds.Person,
                    askOrgs
                        ? "辞令の発令内容（row.action_text）の役職が就く組織を、候補の組織（org_candidates）から選び、可能性の高い順に並べてください。発令内容には上位の組織しか書かれていません。"
                          + "対象者の現在の所属（subject・person_candidates の orgs）や、組織長がすでにいるか（has_head）を手がかりにしてください。"
                          + (askPeople ? "あわせて、行が指す人を候補の人（person_candidates）から可能性の高い順に並べてください。社員番号の誤記・欠落、旧姓・通称の違い、同姓同名がありえます。" : string.Empty)
                        : "取込元の辞令の行（row）が、候補の人（person_candidates）のうち誰を指すかを判定し、可能性の高い順に並べてください。社員番号の誤記・欠落、旧姓・通称の違い、同姓同名がありえます。発令内容の組織と候補の現在の所属も手がかりにしてください。",
                    row,
                    askPeople ? PersonCandidates(proposal, row) : [],
                    askOrgs ? proposal.Basis.OrgCandidates : [],
                    subject: proposal.TargetId,
                    rowFields: [IngestFields.FullName, IngestFields.EmployeeNumber, IngestFields.ActionText]);
            }

            case (ProposalTargetKind.Person or ProposalTargetKind.ExternalIdentity, ProposalOperation.Review, _) when row is not null:
                return Question(
                    proposal,
                    LlmQuestionKinds.Person,
                    "取込元の行（row）が、候補の人（person_candidates）のうち誰を指すかを判定し、可能性の高い順に並べてください。社員番号の誤記・欠落、旧姓・通称の違い、同姓同名がありえます。",
                    row,
                    PersonCandidates(proposal, row),
                    [],
                    subject: null,
                    rowFields: [IngestFields.FullName, IngestFields.EmployeeNumber, IngestFields.OrgName, IngestFields.PositionTitle]);

            case (ProposalTargetKind.OrgUnitVersion, ProposalOperation.Supplement, SupplementCodes.OrgParentAmbiguous) when proposal.Basis.OrgCandidates.Count >= 2 && proposal.TargetId is { } unit:
            {
                var target = new JsonObject();
                var sent = new Dictionary<string, int>(StringComparer.Ordinal);
                var name = _orgs.GetValueOrDefault(unit)?.Name ?? row?[IngestFields.OrgName];
                if (name is null)
                {
                    return null;
                }

                Put(target, "org_name", name, LlmFields.TargetOrgName, sent);
                Put(target, "org_level", LevelName(_orgs.GetValueOrDefault(unit)), LlmFields.TargetOrgLevel, sent);
                return Question(
                    proposal,
                    LlmQuestionKinds.ParentOrg,
                    "組織図の結合セルの補いで、組織（target）の上位組織があいまいになっています。候補の組織（org_candidates）のどれが本当の上位組織か、可能性の高い順に並べてください。"
                    + "段（本部・部・課・チームなど）を飛ばして配置されることがあります。候補の配下の組織（children）の並びも手がかりにしてください。",
                    null,
                    [],
                    proposal.Basis.OrgCandidates,
                    subject: null,
                    rowFields: [],
                    extra: ("target", target, sent));
            }

            case (ProposalTargetKind.Person, ProposalOperation.Supplement, SupplementCodes.DirectoryDepartmentMismatch) when row is not null && proposal.TargetId is { } person:
            {
                // 部署名が jin-ji の組織に当たるもの（所属の食い違い）は後継組織の問いではない。旧組織名（当たらないもの）だけを回す
                if (row[IngestFields.OrgName] is not { } department || OrgNameNormalizer.ToSearchKey(department) is not { } key
                    || _orgs.Values.Any(o => OrgNameNormalizer.ToSearchKey(o.Name) == key))
                {
                    return null;
                }

                var candidates = Neighborhood(person);
                if (candidates.Count < 1)
                {
                    return null;
                }

                return Question(
                    proposal,
                    LlmQuestionKinds.SuccessorOrg,
                    "Entra ID の部署名（row.org_name）は jin-ji に無い旧組織名（廃止・統合・分割された組織）です。その後継にあたる現在の組織を、候補の組織（org_candidates）から可能性の高い順に並べてください。"
                    + "対象者の現在の所属（subject）も手がかりにしてください。",
                    row,
                    [],
                    candidates,
                    subject: person,
                    rowFields: [IngestFields.OrgName]);
            }

            default:
                return null;
        }
    }

    private LlmQuestion? Question(
        ChangeProposalRecord proposal,
        string kind,
        string instruction,
        NormalizedRow? row,
        IReadOnlyList<Guid> people,
        IReadOnlyList<Guid> orgs,
        Guid? subject,
        IReadOnlyList<string> rowFields,
        (string Key, JsonObject Value, Dictionary<string, int> Sent)? extra = null)
    {
        if (people.Count == 0 && orgs.Count == 0)
        {
            return null;
        }

        var sent = extra?.Sent ?? new Dictionary<string, int>(StringComparer.Ordinal);
        var data = new JsonObject();
        var redactions = new List<string>();
        if (row is not null && rowFields.Count > 0)
        {
            var rowData = new JsonObject();
            foreach (var field in rowFields)
            {
                switch (field)
                {
                    case IngestFields.FullName:
                        var name = RowName(row);
                        Put(rowData, "name", name, LlmFields.RowName, sent);
                        // 氏名全体は必ず、姓・名は 2 文字以上のものだけ伏せる（1 文字では組織名などの別の語も伏せてしまう）
                        redactions.AddRange(new[] { name, row[IngestFields.FullName] }.OfType<string>());
                        redactions.AddRange(new[] { row[IngestFields.FamilyName], row[IngestFields.GivenName] }.OfType<string>().Where(v => v.Length >= 2));
                        break;
                    case IngestFields.EmployeeNumber:
                        Put(rowData, "employee_number", row[IngestFields.EmployeeNumber], LlmFields.RowEmployeeNumber, sent);
                        if (row[IngestFields.EmployeeNumber] is { } number)
                        {
                            redactions.Add(number);
                        }

                        break;
                    case IngestFields.ActionText:
                        Put(rowData, "action_text", row[IngestFields.ActionText], LlmFields.RowActionText, sent);
                        break;
                    case IngestFields.OrgName:
                        Put(rowData, "org_name", row[IngestFields.OrgName], LlmFields.RowOrgName, sent);
                        break;
                    case IngestFields.PositionTitle:
                        Put(rowData, "position_title", row[IngestFields.PositionTitle], LlmFields.RowPositionTitle, sent);
                        break;
                    default:
                        throw new InvalidOperationException($"LLM に送らない項目です: {field}");
                }
            }

            data["row"] = rowData;
        }

        if (extra is { } e)
        {
            data[e.Key] = e.Value;
        }

        if (subject is { } subjectId && _snapshot.FindPerson(subjectId) is { } subjectPerson)
        {
            var subjectData = new JsonObject();
            PutList(subjectData, "orgs", OrgNamesOf(subjectPerson.Id), LlmFields.SubjectOrgs, sent);
            Put(subjectData, "title", PrimaryTitle(subjectPerson.Id), LlmFields.SubjectTitle, sent);
            data["subject"] = subjectData;
        }

        var peopleLabels = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (people.Count > 0)
        {
            var list = new JsonArray();
            foreach (var personId in people)
            {
                if (_snapshot.FindPerson(personId) is not { } person)
                {
                    continue;
                }

                var label = $"P{peopleLabels.Count + 1}";
                peopleLabels[label] = personId;
                var item = new JsonObject { ["id"] = label };
                Put(item, "name", person.Current.DisplayName, LlmFields.PersonName, sent);
                Put(item, "employee_number", person.EmployeeNumber, LlmFields.PersonEmployeeNumber, sent);
                PutList(item, "orgs", OrgNamesOf(person.Id), LlmFields.PersonOrgs, sent);
                Put(item, "title", PrimaryTitle(person.Id), LlmFields.PersonTitle, sent);
                list.Add(item);
            }

            data["person_candidates"] = list;
        }

        var orgLabels = new Dictionary<string, Guid>(StringComparer.Ordinal);
        if (orgs.Count > 0)
        {
            var list = new JsonArray();
            foreach (var orgId in orgs.Distinct().Take(Math.Max(_maxCandidates, 20)))
            {
                if (_orgs.GetValueOrDefault(orgId) is not { } org)
                {
                    continue;
                }

                var label = $"O{orgLabels.Count + 1}";
                orgLabels[label] = orgId;
                var item = new JsonObject { ["id"] = label };
                Put(item, "name", org.Name, LlmFields.OrgName, sent);
                Put(item, "parent_name", org.ParentId is { } parent ? _orgs.GetValueOrDefault(parent)?.Name : null, LlmFields.OrgParentName, sent);
                Put(item, "level", LevelName(org), LlmFields.OrgLevel, sent);
                item["has_head"] = org.HasHead;
                Count(sent, LlmFields.OrgHasHead);
                PutList(item, "children", [.. _children[org.Id].Select(c => c.Name).Order(StringComparer.Ordinal).Take(MaxChildren)], LlmFields.OrgChildren, sent);
                list.Add(item);
            }

            data["org_candidates"] = list;
        }

        if (peopleLabels.Count == 0 && orgLabels.Count == 0)
        {
            return null;
        }

        return new LlmQuestion
        {
            Proposal = proposal,
            Kind = kind,
            Instruction = instruction,
            Data = data,
            People = peopleLabels,
            Orgs = orgLabels,
            SentFields = sent,
            Redactions = [.. redactions.Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).OrderByDescending(r => r.Length)],
        };
    }

    /// <summary>
    /// 人の候補: 名寄せの候補（と対象の人）に、氏名の名・姓が一致する人・社員番号が 1 文字違いの人を足す（旧姓・通称・誤記に備える）。
    /// 一致の強い順（氏名全体 → 名 → 社員番号 → 姓）、在籍者を先に、上限まで。
    /// </summary>
    private List<Guid> PersonCandidates(ChangeProposalRecord proposal, NormalizedRow row)
    {
        var result = new List<Guid>();
        foreach (var id in proposal.Basis.Candidates.Select(c => c.PersonId).Concat(proposal.TargetId is { } t ? [t] : []))
        {
            if (!result.Contains(id) && _snapshot.FindPerson(id) is not null)
            {
                result.Add(id);
            }
        }

        var nameKey = row[IngestFields.SearchName];
        var given = TextNormalizer.ToSearchKey(row[IngestFields.GivenName]);
        var family = TextNormalizer.ToSearchKey(row[IngestFields.FamilyName]);
        var number = row[IngestFields.EmployeeNumber];
        var scored = new List<(Guid Id, int Score, bool Active)>();
        foreach (var person in _snapshot.People)
        {
            var score = 0;
            var key = PersonMatcher.NameKey(person.Current);
            var personGiven = TextNormalizer.ToSearchKey(person.Current.GivenName);
            var personFamily = TextNormalizer.ToSearchKey(person.Current.FamilyName);
            if (nameKey is not null && key == nameKey)
            {
                score += 8;
            }

            if (personGiven is not null && nameKey is not null
                && (given is not null ? given == personGiven : nameKey.Length > personGiven.Length && nameKey.EndsWith(personGiven, StringComparison.Ordinal)))
            {
                score += 4;
            }

            if (number is not null && _numbers.Normalize(person.EmployeeNumber) is { } personNumber && personNumber != number && NearNumber(number, personNumber))
            {
                score += 2;
            }

            // 空白の無い氏名（「上田直子」）は姓と名に分けられない（姓 = 全体）ので、jin-ji の姓で始まるかで判断する
            if (personFamily is not null && nameKey is not null
                && (given is not null && family is not null ? family == personFamily : nameKey.Length > personFamily.Length && nameKey.StartsWith(personFamily, StringComparison.Ordinal)))
            {
                score += 1;
            }

            if (score > 0)
            {
                scored.Add((person.Id, score, !person.IsTerminatedOn(_today)));
            }
        }

        foreach (var (id, _, _) in scored.OrderByDescending(s => s.Score).ThenByDescending(s => s.Active).ThenBy(s => s.Id))
        {
            if (result.Count >= _maxCandidates)
            {
                break;
            }

            if (!result.Contains(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    /// <summary>後継組織の候補: 本人の現在の所属・その上位組織・上位組織の配下（兄弟）の組織（同じ法人）。</summary>
    private List<Guid> Neighborhood(Guid personId)
    {
        var result = new List<Guid>();
        void AddUnit(Guid id)
        {
            if (!result.Contains(id) && _orgs.ContainsKey(id))
            {
                result.Add(id);
            }
        }

        var current = _snapshot.AssignmentsOf(personId).Select(a => a.OrgUnitId).Distinct().ToList();
        foreach (var unit in current)
        {
            AddUnit(unit);
        }

        foreach (var unit in current)
        {
            for (var parent = _orgs.GetValueOrDefault(unit)?.ParentId; parent is { } p; parent = _orgs.GetValueOrDefault(p)?.ParentId)
            {
                AddUnit(p);
            }
        }

        foreach (var ancestor in result.ToList())
        {
            foreach (var child in _children[ancestor].OrderBy(c => c.Name, StringComparer.Ordinal))
            {
                AddUnit(child.Id);
            }
        }

        return [.. result.Take(20)];
    }

    /// <summary>社員番号が 1 文字違い（同じ長さで 1 文字置き換え、または隣り合う 2 文字の入れ替え）か。</summary>
    private static bool NearNumber(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diffs = Enumerable.Range(0, a.Length).Where(i => a[i] != b[i]).ToList();
        return diffs.Count == 1 || (diffs.Count == 2 && diffs[1] == diffs[0] + 1 && a[diffs[0]] == b[diffs[1]] && a[diffs[1]] == b[diffs[0]]);
    }

    private static string? RowName(NormalizedRow row) =>
        row[IngestFields.FullName] ?? (row[IngestFields.FamilyName] is { } family ? row[IngestFields.GivenName] is { } given ? $"{family} {given}" : family : null);

    private List<string> OrgNamesOf(Guid personId) =>
        [.. _snapshot.AssignmentsOf(personId).Select(a => _orgs.GetValueOrDefault(a.OrgUnitId)?.Name).OfType<string>().Distinct(StringComparer.Ordinal)];

    private string? PrimaryTitle(Guid personId) =>
        _snapshot.AssignmentsOf(personId).FirstOrDefault(a => a.Type == AssignmentType.Primary)?.PositionTitle;

    private string? LevelName(OrgInfo? org) =>
        org is null ? null : org.LevelKey is { } key ? _snapshot.LevelSchemeOf(org.Entity).Find(key)?.Name ?? key : "段なし";

    private static void Put(JsonObject target, string key, string? value, string field, Dictionary<string, int> sent)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        target[key] = LlmText.Sanitize(value);
        Count(sent, field);
    }

    private static void PutList(JsonObject target, string key, IReadOnlyList<string> values, string field, Dictionary<string, int> sent)
    {
        if (values.Count == 0)
        {
            return;
        }

        target[key] = new JsonArray([.. values.Select(v => (JsonNode)JsonValue.Create(LlmText.Sanitize(v))!)]);
        Count(sent, field);
    }

    private static void Count(Dictionary<string, int> sent, string field)
    {
        if (!LlmFields.All.Contains(field))
        {
            throw new InvalidOperationException($"LLM に送らない項目です: {field}");
        }

        sent[field] = sent.GetValueOrDefault(field) + 1;
    }
}

/// <summary>LLM に送る文字列の扱い。</summary>
public static class LlmText
{
    /// <summary>データの区切り（&lt;data-…&gt;）に見える文字列と制御文字を除く（データから抜け出させない）。長さも切り詰める。</summary>
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var text = new string([.. value.Where(c => !char.IsControl(c))]);
        text = text.Replace("<data", "＜data", StringComparison.OrdinalIgnoreCase).Replace("</data", "＜/data", StringComparison.OrdinalIgnoreCase);
        return text.Length > 200 ? text[..200] : text;
    }
}
