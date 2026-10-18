using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Ingest.Matching;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>外部のディレクトリ（Entra ID）のユーザーの取込。</summary>
public sealed partial class ProposalPlanner
{
    /// <summary>
    /// ディレクトリのユーザーを jin-ji の人に対応付け、外部 ID の対応の提案と補完提案を作る。人の属性（氏名・部署など）は取り込まない（正は人事パッケージ）。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>人ではないアカウント（共有メールボックス・サービスアカウント・会議室）: employeeId が無く姓・名も無いもの、または表示名・メールの形で判別する。
    /// 人として取り込まず、要確認の補完提案（対象外の分類）にする</item>
    /// <item>突合は employeeId（社員番号）→ mail / userPrincipalName → 姓名（表示名ではなく surname・givenName。入れ替わりも試す）+ 部署</item>
    /// <item>補完提案（要確認）: 退職者なのにアカウントが有効、部署名が jin-ji の所属（主務・兼務・出向先）と違う（旧組織名など）、employeeId が空、
    /// 全件の取込で在籍者のアカウントが無い</item>
    /// </list>
    /// </remarks>
    private void DirectoryUsers(IReadOnlyList<NormalizedRow> rows)
    {
        foreach (var row in rows)
        {
            AddIssueCorrections(row);
        }

        var matcher = new PersonMatcher(Snapshot, _context.EmployeeNumbers);
        var accounted = new HashSet<Guid>();
        var system = _context.Profile.SourceSystem;
        foreach (var row in rows)
        {
            if (IsNonPerson(row))
            {
                RecordCandidates(row, new MatchResult(MatchOutcome.None, null, [], false, false));
                Add(
                    row,
                    ProposalTargetKind.ExternalIdentity,
                    ProposalOperation.Supplement,
                    null,
                    null,
                    Payload((ProposalFields.SourceSystem, system), (ProposalFields.ExternalId, row[IngestFields.ExternalId])),
                    new(),
                    new ProposalBasis { Note = "人ではないアカウント（共有メールボックス・サービスアカウント・会議室など）とみなし、人として取り込みません。", Code = SupplementCodes.NonPersonAccount },
                    0.9m,
                    ProposalStatus.NeedsReview);
                continue;
            }

            Guid? entity = null;
            if (row[IngestFields.LegalEntityCode] is { } code)
            {
                entity = Snapshot.LegalEntities.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase))?.Id;
            }

            var department = row[IngestFields.OrgName] is { } name ? ResolveOrgByName(name, entity) : null;
            var match = matcher.Match(row, null, department is { } d ? [d] : []);
            if (match.Outcome == MatchOutcome.None && SwappedName(row) is { } swapped)
            {
                match = matcher.Match(swapped, null, department is { } d2 ? [d2] : []);
            }

            RecordCandidates(row, match);
            foreach (var candidate in match.Candidates)
            {
                accounted.Add(candidate.PersonId);
            }

            if (match is not { Outcome: MatchOutcome.Unique, Conflict: false, Rule: MatchRule.EmployeeNumber or MatchRule.WorkEmail })
            {
                Add(
                    row,
                    ProposalTargetKind.ExternalIdentity,
                    ProposalOperation.Review,
                    null,
                    null,
                    Payload((ProposalFields.SourceSystem, system), (ProposalFields.ExternalId, row[IngestFields.ExternalId])),
                    new(),
                    new ProposalBasis
                    {
                        Rule = match.Rule,
                        Candidates = match.Candidates,
                        Note = match.Outcome == MatchOutcome.None ? "jin-ji の人に対応付けられません。" : "対応する人が一意に決まりません（同姓同名・規則の食い違い）。",
                    },
                    match.Candidates.Count == 0 ? 0m : match.Candidates.Max(c => c.Confidence),
                    ProposalStatus.NeedsReview);
                continue;
            }

            var person = Snapshot.FindPerson(match.Candidates[0].PersonId)!;
            var rule = match.Rule!.Value;
            if (row[IngestFields.ExternalId] is { } externalId)
            {
                Add(
                    row,
                    ProposalTargetKind.ExternalIdentity,
                    ProposalOperation.Create,
                    person.Id,
                    null,
                    Payload((ProposalFields.PersonId, Id(person.Id)), (ProposalFields.SourceSystem, system), (ProposalFields.ExternalId, externalId)),
                    new(),
                    new ProposalBasis { Rule = rule },
                    PersonMatcher.Confidence[rule]);
            }

            if (row[IngestFields.EmployeeNumber] is null)
            {
                SupplementPerson(row, person.Id, SupplementCodes.DirectoryEmployeeIdEmpty, "Entra ID の employeeId が空です。社員番号を設定してください。");
            }

            if (person.IsTerminatedOn(_context.Today) && row[IngestFields.AccountEnabled] == "true")
            {
                SupplementPerson(row, person.Id, SupplementCodes.DirectoryRetiredEnabled, "退職済みなのに Entra ID のアカウントが有効です。無効にしてください。");
            }

            if (row[IngestFields.OrgName] is not null && !person.IsTerminatedOn(_context.Today))
            {
                var current = Snapshot.AssignmentsOf(person.Id).Select(a => a.OrgUnitId).ToHashSet();
                if (department is null || !current.Contains(department.Value))
                {
                    SupplementPerson(
                        row,
                        person.Id,
                        SupplementCodes.DirectoryDepartmentMismatch,
                        department is null
                            ? "Entra ID の部署名が jin-ji の組織に見つかりません（廃止済みの旧組織名の可能性）。jin-ji の所属に合わせてください。"
                            : "Entra ID の部署名が jin-ji の所属（主務・兼務・出向先）と違います。jin-ji の所属に合わせてください。");
                }
            }
        }

        if (_context.Profile.FullSnapshot)
        {
            foreach (var person in Snapshot.People.Where(p => !p.IsTerminatedOn(_context.Today) && !accounted.Contains(p.Id)).OrderBy(p => p.Id))
            {
                SupplementPerson(null, person.Id, SupplementCodes.DirectoryAccountMissing, "在籍者ですが Entra ID にアカウントがありません（アカウント未作成の可能性）。");
            }
        }
    }

    /// <summary>人ではないアカウントか（employeeId が無く姓・名も無い、または表示名・メールの形。規則は <see cref="NonPersonAccount"/>）。</summary>
    public static bool IsNonPerson(NormalizedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return NonPersonAccount.Looks(
            row[IngestFields.EmployeeNumber], row[IngestFields.FamilyName], row[IngestFields.GivenName], row[IngestFields.DisplayName], row[IngestFields.WorkEmail]);
    }

    /// <summary>姓と名を入れ替えた行（ディレクトリで姓名が入れ替わっている場合の突合用）。</summary>
    private static NormalizedRow? SwappedName(NormalizedRow row)
    {
        if (row[IngestFields.FamilyName] is not { } family || row[IngestFields.GivenName] is not { } given)
        {
            return null;
        }

        var data = row.Data.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        data[IngestFields.FamilyName] = given;
        data[IngestFields.GivenName] = family;
        data[IngestFields.SearchName] = TextNormalizer.ToSearchKey(given + family) ?? string.Empty;
        data.Remove(IngestFields.EmployeeNumber);
        data.Remove(IngestFields.WorkEmail);
        return new NormalizedRow(row.StagingRowId, row.SourceRowNumber, data, row.Issues);
    }

    private void SupplementPerson(NormalizedRow? row, Guid personId, string code, string note)
    {
        _proposals.Add(new ChangeProposalRecord
        {
            Id = EntityId.New(),
            BatchId = _context.BatchId,
            TargetKind = ProposalTargetKind.Person,
            Operation = ProposalOperation.Supplement,
            TargetId = personId,
            Basis = new ProposalBasis { RowNumbers = row is null ? [] : [row.SourceRowNumber], Note = note, Code = code },
            StagingRowIds = row is null ? [] : [row.StagingRowId],
            Confidence = 0.9m,
            Status = ProposalStatus.NeedsReview,
            ProposedByPersonId = _context.ExecutedByPersonId,
            ProposedViaClientId = _context.ClientId,
        });
    }
}
