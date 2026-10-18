using System.Globalization;
using JinJi.Application.Ingest;
using JinJi.Domain.Organization;
using JinJi.Ingest.Matching;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>人の取込のうち、兼務・出向・休職（一時停止）・退職の扱い。</summary>
public sealed partial class ProposalPlanner
{
    /// <summary>
    /// 既存の人の兼務・出向・一時停止・退職による所属の終了を提案する（人事パッケージの現在値と jin-ji の差分）。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>兼務: 兼務部署に兼務の所属が無ければ、兼務の所属を追加する</item>
    /// <item>出向: 出向先の組織に所属が無ければ、出向（送り出し）の所属を出向開始日から追加する（籍のある主務の所属はそのまま）</item>
    /// <item>休職: 休職開始日があり、今日一時停止していなければ、一時停止を開始日から追加する（理由は持たない）</item>
    /// <item>退職: 退職日の翌日以後も有効な所属があれば、退職日の翌日で終える</item>
    /// </list>
    /// </remarks>
    private void PlanPersonExtras(NormalizedRow row, PersonRecord person, Guid? entity, MatchResult match)
    {
        var rule = match.Rule!.Value;
        var confidence = PersonMatcher.Confidence[rule];
        var assignments = Snapshot.AssignmentsOf(person.Id);
        var (concurrent, secondment) = ResolveSideOrgs(row, entity ?? person.LegalEntityId);
        var from = _context.EffectiveDate ?? _context.Today;

        if (concurrent is { } c && !assignments.Any(a => a.OrgUnitId == c && a.Type == AssignmentType.Concurrent))
        {
            Add(
                row,
                ProposalTargetKind.Assignment,
                ProposalOperation.Create,
                person.Id,
                from,
                Payload(
                    (ProposalFields.PersonId, Id(person.Id)),
                    (ProposalFields.OrgUnitId, Id(c)),
                    (ProposalFields.AssignmentType, "concurrent"),
                    (ProposalFields.PositionTitle, row[IngestFields.ConcurrentPositionTitle])),
                new(),
                new ProposalBasis { Rule = rule, Note = "兼務の所属がありません。" },
                confidence);
        }

        if (secondment is { } s && !assignments.Any(a => a.OrgUnitId == s))
        {
            Add(
                row,
                ProposalTargetKind.Assignment,
                ProposalOperation.Create,
                person.Id,
                Date(row[IngestFields.SecondmentStartDate]) ?? from,
                Payload(
                    (ProposalFields.PersonId, Id(person.Id)),
                    (ProposalFields.OrgUnitId, Id(s)),
                    (ProposalFields.AssignmentType, "concurrent"),
                    (ProposalFields.SecondmentType, "outbound")),
                new(),
                new ProposalBasis { Rule = rule, Note = "出向先の所属がありません。" },
                confidence);
        }

        if (SuspensionStart(row) is { } suspension && person.Suspension is null)
        {
            Add(
                row,
                ProposalTargetKind.AccountSuspension,
                ProposalOperation.Create,
                person.Id,
                suspension,
                Payload((ProposalFields.PersonId, Id(person.Id))),
                new(),
                new ProposalBasis { Rule = rule, Note = "休職中（アカウントの一時停止。理由は持たない）。" },
                confidence);
        }

        if (Date(row[IngestFields.TerminationDate]) is { } terminated)
        {
            var end = terminated.AddDays(1);
            if (assignments.Any(a => a.ValidUntil is null || a.ValidUntil > end))
            {
                Add(
                    row,
                    ProposalTargetKind.Assignment,
                    ProposalOperation.End,
                    person.Id,
                    end,
                    Payload((ProposalFields.PersonId, Id(person.Id)), (ProposalFields.ActionType, "termination")),
                    new(),
                    new ProposalBasis { Rule = rule, Note = "退職日の翌日で所属を終えます。" },
                    confidence);
            }
        }
    }

    /// <summary>兼務部署と出向先の組織を探す。指定があるのに見つからなければ修正依頼にする。</summary>
    private (Guid? Concurrent, Guid? Secondment) ResolveSideOrgs(NormalizedRow row, Guid? entity)
    {
        Guid? concurrent = null;
        if (row[IngestFields.ConcurrentOrgName] is { } concurrentName)
        {
            concurrent = ResolveOrgByName(concurrentName, entity);
            if (concurrent is null)
            {
                Correction(row, IngestFields.ConcurrentOrgName, CorrectionIssue.UnknownOrg, "兼務部署が jin-ji の組織に見つからないか、一意に決まりません。");
            }
        }

        Guid? secondment = null;
        if (row[IngestFields.SecondmentOrgName] is { } secondmentName)
        {
            var destination = row[IngestFields.SecondmentLegalEntityCode] is { } code
                ? Snapshot.LegalEntities.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase))?.Id
                : null;
            secondment = ResolveOrgByName(secondmentName, destination ?? entity);
            if (secondment is null)
            {
                Correction(row, IngestFields.SecondmentOrgName, CorrectionIssue.UnknownOrg, "出向先部署が jin-ji の組織に見つからないか、一意に決まりません。");
            }
        }

        return (concurrent, secondment);
    }

    /// <summary>休職（一時停止）の開始日。在籍区分が休職でも開始日が無ければ決めない。</summary>
    private static DateOnly? SuspensionStart(NormalizedRow row) => Date(row[IngestFields.SuspensionStartDate]);

    private static DateOnly? Date(string? iso) =>
        iso is null ? null : DateOnly.ParseExact(iso, "yyyy-MM-dd", CultureInfo.InvariantCulture);
}
