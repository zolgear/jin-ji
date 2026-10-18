using System.Globalization;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Ingest.Matching;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>提案の計画の前提（取込単位ごと）。</summary>
/// <param name="Snapshot">今日の組織スナップショット（人事管理者の見え方）。</param>
/// <param name="DefaultLegalEntityId">取込の既定の法人。</param>
/// <param name="EffectiveDate">変更の有効日。null なら差分を訂正として提案する。</param>
public sealed record PlanContext(
    Guid BatchId,
    Guid ExecutedByPersonId,
    string? ClientId,
    OrgSnapshot Snapshot,
    Guid? DefaultLegalEntityId,
    DateOnly? EffectiveDate,
    DateOnly Today,
    EmployeeNumberNormalizer EmployeeNumbers)
{
    /// <summary>取込の設定（法人の別名など）。</summary>
    public ImportProfile Profile { get; init; } = new() { Target = ImportTargetKind.People };
}

/// <summary>提案の計画の結果。</summary>
/// <param name="Scrub">
/// 作業領域に残さない項目（staging の行 → 取込項目）。名寄せで初めて分かる保持しない値（戸籍名で書かれた氏名など）を、保存する前に除くために使う。
/// </param>
public sealed record PlanResult(
    IReadOnlyList<MatchCandidateRecord> Candidates,
    IReadOnlyList<ChangeProposalRecord> Proposals,
    IReadOnlyList<CorrectionRequestRecord> Corrections,
    IReadOnlyDictionary<Guid, IReadOnlyList<string>>? Scrub = null);

/// <summary>
/// 取込結果（正規化した行）と現在の jin-ji のデータ（スナップショット）の差分から、変更提案・補完提案・修正依頼を作る。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>人: 名寄せで一人に決まった人は、属性の版（person_version）と主務の所属（assignment）の差分を提案する。
/// 決まらない（複数候補・規則の衝突・氏名 + 所属だけの一致）行は「要確認」の提案にする（自動で結合しない）</item>
/// <item>社員番号が jin-ji に無い行は新規の人の提案にする。氏名 + 所属が同じ既存の人がいても、社員番号が違うので別人（同姓同名）として扱い、根拠に候補を残す</item>
/// <item>組織: 組織コードで突合し、組織の版の差分か新規の組織を提案する。上位組織が同じ取込の新規の組織なら、その提案を先に承認する必要がある（DependsOn）</item>
/// <item>補完提案: 組織長（上長）が未設定の組織。修正依頼: 社員番号の欠落・日付や値が解釈できない・重複行・存在しない組織や段や組織長</item>
/// <item>取込元の値は根拠・修正依頼のメッセージに書かない（値は提案の payload / diff だけにあり、そこは許可リストの項目だけ）</item>
/// </list>
/// </remarks>
public sealed partial class ProposalPlanner
{
    private const decimal CreateConfidence = 0.9m;
    private const decimal CreateWithSameNameConfidence = 0.7m;
    private const decimal VariantOnlyConfidence = 0.7m;

    private readonly PlanContext _context;
    private readonly List<MatchCandidateRecord> _candidates = [];
    private readonly List<ChangeProposalRecord> _proposals = [];
    private readonly List<CorrectionRequestRecord> _corrections = [];
    private readonly Dictionary<Guid, IReadOnlyList<string>> _scrub = [];

    /// <summary>氏名の項目（戸籍名で書かれた行から除く）。</summary>
    private static readonly IReadOnlyList<string> NameFields = WorkspaceScrub.NameFields;

    private ProposalPlanner(PlanContext context) => _context = context;

    private OrgSnapshot Snapshot => _context.Snapshot;

    /// <summary>人の取込（在籍者名簿）の提案を作る。</summary>
    public static PlanResult PlanPeople(PlanContext context, IReadOnlyList<NormalizedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var planner = new ProposalPlanner(context);
        planner.People(rows);
        return planner.Result();
    }

    /// <summary>組織の取込（組織図）の提案を作る。</summary>
    public static PlanResult PlanOrgUnits(PlanContext context, IReadOnlyList<NormalizedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var planner = new ProposalPlanner(context);
        planner.OrgUnits(rows);
        return planner.Result();
    }

    /// <summary>辞令（発令の一覧）の提案を作る。</summary>
    public static PlanResult PlanPersonnelActions(PlanContext context, IReadOnlyList<NormalizedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var planner = new ProposalPlanner(context);
        planner.PersonnelActions(rows);
        return planner.Result();
    }

    /// <summary>外部のディレクトリ（Entra ID）のユーザーの対応付けと補完提案を作る。</summary>
    public static PlanResult PlanDirectoryUsers(PlanContext context, IReadOnlyList<NormalizedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var planner = new ProposalPlanner(context);
        planner.DirectoryUsers(rows);
        return planner.Result();
    }

    /// <summary>取込の対象に応じて提案を作る。</summary>
    public static PlanResult Plan(PlanContext context, IReadOnlyList<NormalizedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Profile.Target switch
        {
            ImportTargetKind.People => PlanPeople(context, rows),
            ImportTargetKind.OrgUnits => PlanOrgUnits(context, rows),
            ImportTargetKind.PersonnelActions => PlanPersonnelActions(context, rows),
            ImportTargetKind.DirectoryUsers => PlanDirectoryUsers(context, rows),
            ImportTargetKind.Sites => PlanSites(context, rows),
            _ => throw new ArgumentOutOfRangeException(nameof(context)),
        };
    }

    private PlanResult Result() => new(_candidates, _proposals, _corrections, _scrub);

    // -----------------------------------------------------------------------
    // 人
    // -----------------------------------------------------------------------

    private void People(IReadOnlyList<NormalizedRow> rows)
    {
        foreach (var row in rows)
        {
            AddIssueCorrections(row);
        }

        var usable = ExcludeDuplicates(rows, IngestFields.EmployeeNumber, "社員番号");
        usable = ExcludeDuplicates(usable, IngestFields.WorkEmail, "社用メール");

        var matcher = new PersonMatcher(Snapshot, _context.EmployeeNumbers);
        var touchedUnits = new HashSet<Guid>();
        foreach (var row in usable)
        {
            if (!TryResolveLegalEntity(row, out var entity))
            {
                continue;
            }

            var unit = ResolveOrg(row, entity, IngestFields.OrgCode, IngestFields.OrgName);
            if (unit is { } u)
            {
                touchedUnits.Add(u);
            }

            var match = matcher.Match(row, entity, unit is { } id ? [id] : []);
            RecordCandidates(row, match);
            var site = ResolveSite(row, entity, out _);

            var unique = match.Outcome == MatchOutcome.Unique && !match.Conflict;
            if (unique && match.Rule is MatchRule.EmployeeNumber or MatchRule.WorkEmail)
            {
                var person = Snapshot.FindPerson(match.Candidates[0].PersonId)!;
                PlanPersonUpdate(row, person, unit, site, match);
                PlanPersonExtras(row, person, entity, match);
            }
            else if (match.Outcome == MatchOutcome.None || (match.Rule == MatchRule.NameAndOrg && match.EmployeeNumberUnknown))
            {
                // 社員番号が jin-ji に無い → 新規。氏名 + 所属が同じ人がいても社員番号が違うので別人（同姓同名）
                if (row[IngestFields.EmployeeNumber] is not null)
                {
                    PlanPersonCreate(row, entity, unit, site, match.Candidates);
                }
            }
            else
            {
                if (match.Conflict)
                {
                    Correction(row, IngestFields.EmployeeNumber, CorrectionIssue.ConflictingIdentity, "社員番号と社用メールが jin-ji の別の人を指しているか、社用メールが別の社員番号の人のものです。");
                }

                Review(row, entity, unit, site, match);
            }
        }

        // 補完提案: 取込の行が所属する組織のうち、組織長（上長）が未設定のもの
        foreach (var unitId in touchedUnits.Order())
        {
            if (Snapshot.FindUnit(unitId) is { HeadPersonId: null } unit)
            {
                SupplementHead(unit.Id, [], []);
            }
        }
    }

    private void PlanPersonUpdate(NormalizedRow row, PersonRecord person, Guid? unit, Site? site, MatchResult match)
    {
        var current = person.Current;
        var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        void Compare(string field, string? currentValue, string? newValue)
        {
            if (newValue is not null && !string.Equals(currentValue, newValue, StringComparison.Ordinal))
            {
                diff[field] = new FieldChange(currentValue, newValue);
            }
        }

        Compare(ProposalFields.FamilyName, current.FamilyName, row[IngestFields.FamilyName]);
        Compare(ProposalFields.GivenName, current.GivenName, row[IngestFields.GivenName]);
        Compare(ProposalFields.FamilyNameKana, current.FamilyNameKana, row[IngestFields.FamilyNameKana]);
        Compare(ProposalFields.GivenNameKana, current.GivenNameKana, row[IngestFields.GivenNameKana]);
        Compare(ProposalFields.FamilyNameLatin, current.FamilyNameLatin, row[IngestFields.FamilyNameLatin]);
        Compare(ProposalFields.GivenNameLatin, current.GivenNameLatin, row[IngestFields.GivenNameLatin]);
        Compare(ProposalFields.DisplayName, current.DisplayName, row[IngestFields.DisplayName]);
        Compare(ProposalFields.WorkEmail, current.WorkEmail, row[IngestFields.WorkEmail]);
        Compare(ProposalFields.ExtensionNumber, current.ExtensionNumber, row[IngestFields.ExtensionNumber]);
        Compare(ProposalFields.WorkMobilePhone, current.WorkMobilePhone, row[IngestFields.WorkMobilePhone]);
        Compare(ProposalFields.EmploymentType, current.EmploymentType, row[IngestFields.EmploymentType]);
        Compare(ProposalFields.JobType, current.JobType, row[IngestFields.JobType]);
        Compare(ProposalFields.HireDate, Iso(current.HireDate), row[IngestFields.HireDate]);
        Compare(ProposalFields.TerminationDate, Iso(current.TerminationDate), row[IngestFields.TerminationDate]);
        Compare(ProposalFields.RemoteWork, current.RemoteWork ? "true" : "false", row[IngestFields.RemoteWork]);

        // 拠点: 行の拠点が（行の、無ければ今の）主務の組織の拠点と違うときだけ人ごとの拠点として持つ。同じなら上書きを外す
        if (site is not null)
        {
            var desired = DesiredSiteOverride(site, person.Id, row[IngestFields.AssignmentType] == "concurrent" ? null : unit);
            if (desired != current.SiteId)
            {
                diff[ProposalFields.SiteId] = new FieldChange(current.SiteId is { } cs ? Id(cs) : null, desired is { } ds ? Id(ds) : null);
            }
        }

        var nameChanged = diff.ContainsKey(ProposalFields.FamilyName) || diff.ContainsKey(ProposalFields.GivenName);
        if (nameChanged && row[IngestFields.DisplayName] is null)
        {
            // 表示名の列が無ければ、氏名から作り直す
            var newFamily = row[IngestFields.FamilyName] ?? current.FamilyName;
            var newGiven = diff.TryGetValue(ProposalFields.GivenName, out var ng) ? ng.New : current.GivenName;
            Compare(ProposalFields.DisplayName, current.DisplayName, newGiven is null ? newFamily : $"{newFamily} {newGiven}");
        }

        if (nameChanged)
        {
            // 検索用の正規化表記も作り直す（旧値はドメインの版に持たないので null。反映時に照合しない）
            var family = row[IngestFields.FamilyName] ?? current.FamilyName;
            var given = diff.TryGetValue(ProposalFields.GivenName, out var g) ? g.New : current.GivenName;
            diff[ProposalFields.SearchName] = new FieldChange(null, TextNormalizer.ToSearchKey(family + given));
        }

        if (diff.ContainsKey(ProposalFields.FamilyNameKana) || diff.ContainsKey(ProposalFields.GivenNameKana))
        {
            var familyKana = row[IngestFields.FamilyNameKana] ?? current.FamilyNameKana;
            var givenKana = diff.TryGetValue(ProposalFields.GivenNameKana, out var gk) ? gk.New : current.GivenNameKana;
            diff[ProposalFields.SearchNameKana] = new FieldChange(null, TextNormalizer.ToSearchKey(familyKana + givenKana));
        }

        var rule = match.Rule!.Value;
        if (diff.Count > 0)
        {
            var variantOnly = nameChanged && row[IngestFields.SearchName] == PersonMatcher.NameKey(current);
            Add(
                row,
                ProposalTargetKind.PersonVersion,
                ChangeOperation(),
                person.Id,
                _context.EffectiveDate,
                payload: new(),
                diff,
                new ProposalBasis
                {
                    Rule = rule,
                    Note = variantOnly ? "氏名の違いは異体字・表記のゆれだけです（正式表記をどちらにするか確認してください）。" : null,
                },
                variantOnly ? VariantOnlyConfidence : PersonMatcher.Confidence[rule]);
        }

        // 主務の所属（兼務の行は扱わない）
        if (unit is not { } unitId || row[IngestFields.AssignmentType] == "concurrent")
        {
            return;
        }

        var primary = Snapshot.AssignmentsOf(person.Id).FirstOrDefault(a => a.Type == AssignmentType.Primary);
        if (primary is null)
        {
            Add(
                row,
                ProposalTargetKind.Assignment,
                ProposalOperation.Create,
                person.Id,
                _context.EffectiveDate ?? _context.Today,
                Payload((ProposalFields.PersonId, Id(person.Id)), (ProposalFields.OrgUnitId, Id(unitId)), (ProposalFields.AssignmentType, "primary"), (ProposalFields.PositionTitle, row[IngestFields.PositionTitle])),
                new(),
                new ProposalBasis { Rule = rule, Note = "主務の所属がありません。" },
                PersonMatcher.Confidence[rule]);
            return;
        }

        var assignmentDiff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
        if (primary.OrgUnitId != unitId)
        {
            assignmentDiff[ProposalFields.OrgUnitId] = new FieldChange(Id(primary.OrgUnitId), Id(unitId));
        }

        if (row[IngestFields.PositionTitle] is { } title && title != primary.PositionTitle)
        {
            assignmentDiff[ProposalFields.PositionTitle] = new FieldChange(primary.PositionTitle, title);
        }

        if (assignmentDiff.Count > 0)
        {
            Add(row, ProposalTargetKind.Assignment, ChangeOperation(), person.Id, _context.EffectiveDate, new(), assignmentDiff, new ProposalBasis { Rule = rule }, PersonMatcher.Confidence[rule]);
        }
    }

    private void PlanPersonCreate(NormalizedRow row, Guid? entity, Guid? unit, Site? site, IReadOnlyList<ProposalCandidate> sameName)
    {
        var missing = new[] { IngestFields.FamilyName, IngestFields.HireDate, IngestFields.EmploymentType }
            .Where(f => row[f] is null && !row.HasIssue(f, IssueCodes.InvalidDate) && !row.HasIssue(f, IssueCodes.InvalidValue))
            .ToList();
        foreach (var field in missing)
        {
            Correction(row, field, CorrectionIssue.MissingRequired, $"新しい人の登録に必要な項目 {field} がありません。");
        }

        var legalEntity = entity ?? _context.DefaultLegalEntityId;
        if (legalEntity is null)
        {
            Correction(row, IngestFields.LegalEntityCode, CorrectionIssue.UnknownLegalEntity, "新しい人の法人が決まりません（法人のコードを指定してください）。");
        }

        if (missing.Count > 0 || legalEntity is null || row[IngestFields.FamilyName] is null || row[IngestFields.HireDate] is null || row[IngestFields.EmploymentType] is null)
        {
            return;
        }

        var personId = EntityId.New();
        var family = row[IngestFields.FamilyName]!;
        var given = row[IngestFields.GivenName];
        var payload = Payload(
            (ProposalFields.PersonId, Id(personId)),
            (ProposalFields.LegalEntityId, Id(legalEntity.Value)),
            (ProposalFields.EmployeeNumber, row[IngestFields.EmployeeNumber]),
            (ProposalFields.FamilyName, family),
            (ProposalFields.GivenName, given),
            (ProposalFields.FamilyNameKana, row[IngestFields.FamilyNameKana]),
            (ProposalFields.GivenNameKana, row[IngestFields.GivenNameKana]),
            (ProposalFields.FamilyNameLatin, row[IngestFields.FamilyNameLatin]),
            (ProposalFields.GivenNameLatin, row[IngestFields.GivenNameLatin]),
            (ProposalFields.DisplayName, row[IngestFields.DisplayName] ?? (given is null ? family : $"{family} {given}")),
            (ProposalFields.SearchName, row[IngestFields.SearchName]),
            (ProposalFields.SearchNameKana, row[IngestFields.SearchNameKana]),
            (ProposalFields.WorkEmail, row[IngestFields.WorkEmail]),
            (ProposalFields.ExtensionNumber, row[IngestFields.ExtensionNumber]),
            (ProposalFields.WorkMobilePhone, row[IngestFields.WorkMobilePhone]),
            (ProposalFields.SiteId, site is not null && DesiredSiteOverride(site, null, unit) is { } siteId ? Id(siteId) : null),
            (ProposalFields.RemoteWork, row[IngestFields.RemoteWork]),
            (ProposalFields.EmploymentType, row[IngestFields.EmploymentType]),
            (ProposalFields.JobType, row[IngestFields.JobType]),
            (ProposalFields.HireDate, row[IngestFields.HireDate]),
            (ProposalFields.TerminationDate, row[IngestFields.TerminationDate]),
            (ProposalFields.OrgUnitId, unit is { } u ? Id(u) : null),
            (ProposalFields.PositionTitle, unit is null ? null : row[IngestFields.PositionTitle]),
            (ProposalFields.AssignmentType, unit is null ? null : "primary"));

        // 兼務・出向・休職（一時停止）。退職者は所属を退職日の翌日で終える（反映側）
        var (concurrent, secondment) = ResolveSideOrgs(row, legalEntity);
        if (concurrent is { } c)
        {
            payload[ProposalFields.ConcurrentOrgUnitId] = Id(c);
            if (row[IngestFields.ConcurrentPositionTitle] is { } title)
            {
                payload[ProposalFields.ConcurrentPositionTitle] = title;
            }
        }

        if (secondment is { } so)
        {
            payload[ProposalFields.SecondmentOrgUnitId] = Id(so);
            if (row[IngestFields.SecondmentStartDate] is { } start)
            {
                payload[ProposalFields.SecondmentStartDate] = start;
            }
        }

        if (SuspensionStart(row) is { } suspended)
        {
            payload[ProposalFields.SuspensionStartDate] = Iso(suspended);
        }

        Add(
            row,
            ProposalTargetKind.Person,
            ProposalOperation.Create,
            personId,
            DateOnly.ParseExact(row[IngestFields.HireDate]!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            payload,
            new(),
            new ProposalBasis
            {
                Candidates = sameName,
                Note = sameName.Count > 0 ? "氏名と所属が同じ人がいますが、社員番号が違うので別人（同姓同名）として新規に登録します。" : "社員番号が jin-ji にありません（新規）。",
            },
            sameName.Count > 0 ? CreateWithSameNameConfidence : CreateConfidence);
    }

    private void Review(NormalizedRow row, Guid? entity, Guid? unit, Site? site, MatchResult match)
    {
        // 解決（候補から選ぶ・新規として登録する）で使う値も持たせる（解決は取込元の行を読み直さずに、この値から提案を作る）
        var family = row[IngestFields.FamilyName];
        var given = row[IngestFields.GivenName];
        var payload = Payload(
            (ProposalFields.LegalEntityId, (entity ?? _context.DefaultLegalEntityId) is { } e ? Id(e) : null),
            (ProposalFields.EmployeeNumber, row[IngestFields.EmployeeNumber]),
            (ProposalFields.FamilyName, family),
            (ProposalFields.GivenName, given),
            (ProposalFields.FamilyNameKana, row[IngestFields.FamilyNameKana]),
            (ProposalFields.GivenNameKana, row[IngestFields.GivenNameKana]),
            (ProposalFields.FamilyNameLatin, row[IngestFields.FamilyNameLatin]),
            (ProposalFields.GivenNameLatin, row[IngestFields.GivenNameLatin]),
            (ProposalFields.DisplayName, row[IngestFields.DisplayName] ?? (family is null ? null : given is null ? family : $"{family} {given}")),
            (ProposalFields.SearchName, row[IngestFields.SearchName]),
            (ProposalFields.SearchNameKana, row[IngestFields.SearchNameKana]),
            (ProposalFields.WorkEmail, row[IngestFields.WorkEmail]),
            (ProposalFields.ExtensionNumber, row[IngestFields.ExtensionNumber]),
            (ProposalFields.WorkMobilePhone, row[IngestFields.WorkMobilePhone]),
            (ProposalFields.SiteId, site is not null && DesiredSiteOverride(site, null, unit) is { } siteId ? Id(siteId) : null),
            (ProposalFields.RemoteWork, row[IngestFields.RemoteWork]),
            (ProposalFields.EmploymentType, row[IngestFields.EmploymentType]),
            (ProposalFields.JobType, row[IngestFields.JobType]),
            (ProposalFields.HireDate, row[IngestFields.HireDate]),
            (ProposalFields.TerminationDate, row[IngestFields.TerminationDate]),
            (ProposalFields.OrgUnitId, unit is { } u ? Id(u) : null),
            (ProposalFields.PositionTitle, unit is null ? null : row[IngestFields.PositionTitle]));
        var note = match switch
        {
            { Conflict: true } => "社員番号・社用メールの突合の結果が食い違います。",
            { Rule: MatchRule.NameAndOrg } => "氏名と所属だけが一致しました。同姓同名の別人の可能性があるので、自動では結合しません。",
            { Outcome: MatchOutcome.Ambiguous } => "候補が複数あります。",
            _ => "候補が決まりません。",
        };
        Add(
            row,
            ProposalTargetKind.Person,
            ProposalOperation.Review,
            null,
            null,
            payload,
            new(),
            new ProposalBasis { Rule = match.Rule, Candidates = match.Candidates, Note = note },
            match.Candidates.Count == 0 ? 0m : match.Candidates.Max(c => c.Confidence),
            ProposalStatus.NeedsReview);
    }

    private void RecordCandidates(NormalizedRow row, MatchResult match)
    {
        if (match.Candidates.Count == 0)
        {
            _candidates.Add(new MatchCandidateRecord(EntityId.New(), row.StagingRowId, MatchOutcome.None, null, null, 0m));
            return;
        }

        foreach (var candidate in match.Candidates)
        {
            _candidates.Add(new MatchCandidateRecord(EntityId.New(), row.StagingRowId, match.Outcome, candidate.Rule, candidate.PersonId, candidate.Confidence));
        }
    }

    private void SupplementHead(Guid unitId, IReadOnlyList<Guid> stagingRows, IReadOnlyList<Guid> dependsOn, int? rowNumber = null)
    {
        _proposals.Add(new ChangeProposalRecord
        {
            Id = EntityId.New(),
            BatchId = _context.BatchId,
            TargetKind = ProposalTargetKind.OrgUnitVersion,
            Operation = ProposalOperation.Supplement,
            TargetId = unitId,
            Basis = new ProposalBasis
            {
                RowNumbers = rowNumber is { } n ? [n] : [],
                DependsOn = dependsOn,
                Note = "組織長（上長）が未設定です。組織長を決めてください。",
                Code = SupplementCodes.OrgHeadMissing,
            },
            StagingRowIds = stagingRows,
            Confidence = 1.0m,
            Status = ProposalStatus.NeedsReview,
            ProposedByPersonId = _context.ExecutedByPersonId,
            ProposedViaClientId = _context.ClientId,
        });
    }

    // -----------------------------------------------------------------------
    // 共通
    // -----------------------------------------------------------------------

    private ProposalOperation ChangeOperation() => _context.EffectiveDate is null ? ProposalOperation.Correct : ProposalOperation.Change;

    private Guid Add(
        NormalizedRow row,
        ProposalTargetKind target,
        ProposalOperation operation,
        Guid? targetId,
        DateOnly? effectiveDate,
        Dictionary<string, string?> payload,
        Dictionary<string, FieldChange> diff,
        ProposalBasis basis,
        decimal confidence,
        ProposalStatus status = ProposalStatus.Proposed,
        Guid? id = null)
    {
        var proposalId = id ?? EntityId.New();
        _proposals.Add(new ChangeProposalRecord
        {
            Id = proposalId,
            BatchId = _context.BatchId,
            TargetKind = target,
            Operation = operation,
            TargetId = targetId,
            EffectiveDate = effectiveDate,
            Payload = payload,
            Diff = diff,
            Basis = basis with { RowNumbers = [row.SourceRowNumber] },
            StagingRowIds = [row.StagingRowId],
            Confidence = confidence,
            Status = status,
            ProposedByPersonId = _context.ExecutedByPersonId,
            ProposedViaClientId = _context.ClientId,
        });
        return proposalId;
    }

    private void Correction(NormalizedRow row, string? field, CorrectionIssue issue, string message) =>
        _corrections.Add(new CorrectionRequestRecord(EntityId.New(), row.StagingRowId, row.SourceRowNumber, field, issue, message));

    private void AddIssueCorrections(NormalizedRow row)
    {
        foreach (var issue in row.Issues)
        {
            switch (issue.Code)
            {
                case IssueCodes.Missing when issue.Field == IngestFields.EmployeeNumber:
                    Correction(row, issue.Field, CorrectionIssue.MissingEmployeeNumber, "社員番号がありません。");
                    break;
                case IssueCodes.Missing:
                    Correction(row, issue.Field, CorrectionIssue.MissingRequired, $"必須の項目 {issue.Field} がありません。");
                    break;
                case IssueCodes.InvalidDate:
                    Correction(row, issue.Field, CorrectionIssue.InvalidDate, $"日付として解釈できません（項目 {issue.Field}）。");
                    break;
                default:
                    Correction(row, issue.Field, CorrectionIssue.InvalidValue, $"値を解釈できません（項目 {issue.Field}）。");
                    break;
            }
        }
    }

    /// <summary>同じ値（社員番号・社用メール・組織コード）の行が複数あれば、どれも提案にせず修正依頼にする（同一人物と思われる重複行）。</summary>
    private List<NormalizedRow> ExcludeDuplicates(IReadOnlyList<NormalizedRow> rows, string field, string label)
    {
        var duplicates = rows
            .Where(r => r[field] is not null)
            .GroupBy(r => r[field]!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        var excluded = new HashSet<Guid>();
        foreach (var group in duplicates)
        {
            var numbers = string.Join(", ", group.Select(r => r.SourceRowNumber));
            foreach (var row in group)
            {
                Correction(row, field, CorrectionIssue.DuplicateRow, $"同じ{label}の行が複数あります（行 {numbers}）。同一人物・同一組織の重複か確認してください。");
                excluded.Add(row.StagingRowId);
            }
        }

        return [.. rows.Where(r => !excluded.Contains(r.StagingRowId))];
    }

    private bool TryResolveLegalEntity(NormalizedRow row, out Guid? entity)
    {
        entity = _context.DefaultLegalEntityId;
        if (row[IngestFields.LegalEntityCode] is not { } code)
        {
            return true;
        }

        var found = Snapshot.LegalEntities.Where(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
        if (found.Count != 1)
        {
            Correction(row, IngestFields.LegalEntityCode, CorrectionIssue.UnknownLegalEntity, "法人のコードが jin-ji の法人にありません。");
            return false;
        }

        entity = found[0].Id;
        return true;
    }

    /// <summary>
    /// 組織をコード（法人の中で）か、名称（検索用の正規化表記。一意のときだけ）で探す。コードで見つからなければ名称で探す。
    /// 指定があるのにどちらでも見つからなければ修正依頼にする。
    /// </summary>
    private Guid? ResolveOrg(NormalizedRow row, Guid? entity, string codeField, string nameField)
    {
        if (row[codeField] is { } code)
        {
            var byCode = Snapshot.Units.Where(u => (entity is null || u.LegalEntityId == entity) && string.Equals(u.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byCode.Count == 1)
            {
                return byCode[0].Id;
            }
        }

        if (row[nameField] is { } name && ResolveOrgByName(name, entity) is { } byName)
        {
            return byName;
        }

        if (row[codeField] is not null || row[nameField] is not null)
        {
            var field = row[codeField] is not null ? codeField : nameField;
            Correction(row, field, CorrectionIssue.UnknownOrg, $"組織（項目 {field}）が jin-ji の組織に見つからないか、一意に決まりません。");
        }

        return null;
    }

    /// <summary>
    /// 組織を名称で探す（検索用の正規化表記が一致し、一意のとき）。「サンプル物流 物流企画部」のように法人の別名が前に付いていればその法人の中で探す。
    /// 法人の中で見つからなければ、全法人で一意なら返す。
    /// </summary>
    private Guid? ResolveOrgByName(string name, Guid? entity)
    {
        var (code, rest) = _context.Profile.SplitLegalEntityPrefix(name);
        if (code is not null && Snapshot.LegalEntities.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase)) is { } prefixed)
        {
            entity = prefixed.Id;
        }

        if (OrgNameNormalizer.ToSearchKey(rest) is not { } key)
        {
            return null;
        }

        var all = Snapshot.Units.Where(u => OrgNameNormalizer.ToSearchKey(u.Name) == key).ToList();
        var inEntity = entity is null ? all : all.Where(u => u.LegalEntityId == entity).ToList();
        return inEntity.Count == 1 ? inEntity[0].Id : inEntity.Count == 0 && all.Count == 1 ? all[0].Id : null;
    }

    private IEnumerable<OrgUnit> UnitsByCode(Guid entity, string code) =>
        Snapshot.Units.Where(u => u.LegalEntityId == entity && string.Equals(u.Code, code, StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, string?> Payload(params (string Key, string? Value)[] values) =>
        values.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    private static string Id(Guid id) => id.ToString("D");

    private static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
