using JinJi.Application.FieldManagement;
using JinJi.Application.Ingest;
using JinJi.Domain.Organization;

namespace JinJi.Ingest;

/// <summary>
/// 取込の提案を領域の管理元で振り分ける（ADR-0013 決定 2）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>管理元が field（jin-ji）の領域の差分は、提案にせず「人事パッケージとの差」にする: 要確認（<see cref="SupplementCodes.FieldManaged"/>）にして承認できなくする。
/// 人の版・組織の版の差分は、領域ごとに分けて、hr の領域の分だけを起案中に残す</item>
/// <item>管理元が both の領域で、今日有効な値を直接登録が書いた対象の差分は、起案中のまま印（<see cref="SupplementCodes.FieldRegistered"/>）を付ける
/// （承認するまで上書きしない。承認する人が現場の登録と突き合わせる）</item>
/// <item>hr（既定）の領域は何もしない。直接登録の対象外の項目（氏名・在籍・雇用区分・入退社など）も何もしない</item>
/// </list>
/// </remarks>
public static class FieldModeFilter
{
    private static readonly IReadOnlySet<string> AssignmentActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "transfer", "position_appointment", "position_dismissal", "concurrent_start", "concurrent_end", "secondment_start", "secondment_end",
    };

    /// <summary>提案を振り分ける。管理元がすべて hr なら、そのまま返す。</summary>
    public static IReadOnlyList<ChangeProposalRecord> Apply(
        IReadOnlyList<ChangeProposalRecord> proposals,
        IReadOnlyDictionary<FieldDomain, FieldManagementMode> modes,
        FieldWrittenTargets written)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(written);
        if (modes.Values.All(m => m == FieldManagementMode.Hr))
        {
            return proposals;
        }

        var result = new List<ChangeProposalRecord>(proposals.Count);
        foreach (var proposal in proposals)
        {
            if (proposal.Status != ProposalStatus.Proposed || proposal.TargetId is not { } target)
            {
                result.Add(proposal);
                continue;
            }

            switch (proposal.TargetKind, proposal.Operation)
            {
                case (ProposalTargetKind.PersonVersion, ProposalOperation.Correct or ProposalOperation.Change):
                    result.AddRange(Split(proposal, PersonDomain, modes, written.People.Contains(target)));
                    break;
                case (ProposalTargetKind.OrgUnitVersion, ProposalOperation.Correct or ProposalOperation.Change):
                    result.AddRange(Split(proposal, OrgDomain, modes, written.OrgUnits.Contains(target)));
                    break;
                case (ProposalTargetKind.OrgUnit, ProposalOperation.Create):
                case (ProposalTargetKind.OrgUnitVersion, ProposalOperation.Abolish):
                    result.Add(Whole(proposal, FieldDomain.OrgStructure, modes, written.OrgUnits.Contains(target)));
                    break;
                case (ProposalTargetKind.Assignment, _):
                    result.Add(Whole(proposal, FieldDomain.Assignment, modes, written.Assignments.Contains(target)));
                    break;
                case (ProposalTargetKind.PersonnelAction, _)
                    when proposal.Payload.GetValueOrDefault(ProposalFields.ActionType) is { } action && AssignmentActions.Contains(action):
                    result.Add(Whole(proposal, FieldDomain.Assignment, modes, written.Assignments.Contains(target)));
                    break;
                default:
                    result.Add(proposal);
                    break;
            }
        }

        return result;
    }

    private static FieldDomain? PersonDomain(string key) => key switch
    {
        ProposalFields.SiteId or ProposalFields.RemoteWork => FieldDomain.PersonSite,
        ProposalFields.ExtensionNumber or ProposalFields.WorkMobilePhone => FieldDomain.PersonContact,
        _ => null,
    };

    private static FieldDomain? OrgDomain(string key) => key == ProposalFields.SiteId ? FieldDomain.OrgSite : FieldDomain.OrgStructure;

    private static ChangeProposalRecord Whole(
        ChangeProposalRecord proposal,
        FieldDomain domain,
        IReadOnlyDictionary<FieldDomain, FieldManagementMode> modes,
        bool registered) => modes.GetValueOrDefault(domain) switch
        {
            FieldManagementMode.Field => Managed(proposal, [domain]),
            FieldManagementMode.Both when registered => Marked(proposal),
            _ => proposal,
        };

    /// <summary>差分を項目の領域で分ける。field の領域の項目は「人事パッケージとの差」の提案に分け、残りを元の提案に残す。</summary>
    private static IEnumerable<ChangeProposalRecord> Split(
        ChangeProposalRecord proposal,
        Func<string, FieldDomain?> domainOf,
        IReadOnlyDictionary<FieldDomain, FieldManagementMode> modes,
        bool registered)
    {
        var field = proposal.Diff.Where(d => domainOf(d.Key) is { } domain && modes.GetValueOrDefault(domain) == FieldManagementMode.Field).ToList();
        var both = registered && proposal.Diff.Keys.Any(k => domainOf(k) is { } domain && modes.GetValueOrDefault(domain) == FieldManagementMode.Both);
        if (field.Count == 0)
        {
            yield return both ? Marked(proposal) : proposal;
            yield break;
        }

        var domains = field.Select(d => domainOf(d.Key)!.Value).Distinct().Order().ToList();
        if (field.Count == proposal.Diff.Count)
        {
            yield return Managed(proposal, domains);
            yield break;
        }

        var rest = proposal with { Diff = proposal.Diff.Where(d => !field.Any(f => f.Key == d.Key)).ToDictionary(StringComparer.Ordinal) };
        yield return both ? Marked(rest) : rest;
        yield return Managed(
            proposal with { Id = Guid.CreateVersion7(), Diff = field.ToDictionary(StringComparer.Ordinal) },
            domains);
    }

    private static ChangeProposalRecord Managed(ChangeProposalRecord proposal, IReadOnlyList<FieldDomain> domains) => proposal with
    {
        Status = ProposalStatus.NeedsReview,
        Basis = proposal.Basis with
        {
            Code = SupplementCodes.FieldManaged,
            Note = $"管理元が jin-ji の領域（{string.Join("・", domains.Select(FieldDomains.Key))}）の差分です。人事パッケージとの差として確認し、承認はしません。",
        },
    };

    private static ChangeProposalRecord Marked(ChangeProposalRecord proposal) => proposal with
    {
        Basis = proposal.Basis with
        {
            Code = SupplementCodes.FieldRegistered,
            Note = "現場・IT 部門が直接登録した値を変える差分です。承認する前に、現場の登録と突き合わせてください。",
        },
    };
}
