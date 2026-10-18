using JinJi.Application.Ingest;
using JinJi.Domain.Organization;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest.Matching;

/// <summary>名寄せの結果。</summary>
/// <param name="Candidates">候補（一意なら 1 人）。</param>
/// <param name="Conflict">規則どうしが別の人を指している（社員番号では P、社用メールでは Q など）。要確認にする。</param>
/// <param name="EmployeeNumberUnknown">行に社員番号があるが、jin-ji のどの人とも一致しなかった。</param>
public sealed record MatchResult(
    MatchOutcome Outcome,
    MatchRule? Rule,
    IReadOnlyList<ProposalCandidate> Candidates,
    bool Conflict,
    bool EmployeeNumberUnknown);

/// <summary>
/// 決定的な名寄せ（アーキテクチャ概要 §8。LLM による候補提示は後の手順）。社員番号 → 社用メール → 正規化氏名 + 所属 の順に突合する。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>社員番号: 取込元と jin-ji の両方に同じ規則（<see cref="EmployeeNumberNormalizer"/>）をかけて比べる（ゼロ埋め・全角・区切りのゆれを吸収）。
/// 法人が分かれば法人の中で比べる（社員番号は法人ごとに一意）</item>
/// <item>社用メール: 小文字・NFKC で比べる</item>
/// <item>正規化氏名 + 所属: 検索用の正規化表記（異体字・空白・かな）が一致し、かつ所属組織が一致する人。氏名だけでは突合しない。
/// この規則での一致は、同姓同名の別人の可能性があるので、提案の側で必ず要確認にする（自動で結合しない）</item>
/// <item>複数の候補・規則どうしの衝突は、どちらか一方に決めずに要確認として返す</item>
/// </list>
/// </remarks>
public sealed class PersonMatcher
{
    /// <summary>規則ごとの確信度。</summary>
    public static readonly IReadOnlyDictionary<MatchRule, decimal> Confidence = new Dictionary<MatchRule, decimal>
    {
        [MatchRule.EmployeeNumber] = 1.0m,
        [MatchRule.WorkEmail] = 0.9m,
        [MatchRule.NameAndOrg] = 0.6m,
    };

    private readonly OrgSnapshot _snapshot;
    private readonly ILookup<string, PersonRecord> _byNumber;
    private readonly ILookup<string, PersonRecord> _byEmail;
    private readonly ILookup<string, PersonRecord> _byName;

    public PersonMatcher(OrgSnapshot snapshot, EmployeeNumberNormalizer numbers)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(numbers);
        _snapshot = snapshot;
        _byNumber = snapshot.People
            .Select(p => (Key: numbers.Normalize(p.EmployeeNumber), Person: p))
            .Where(x => x.Key is not null)
            .ToLookup(x => x.Key!, x => x.Person, StringComparer.Ordinal);
        _byEmail = snapshot.People
            .Select(p => (Key: EmailKey(p.Current.WorkEmail), Person: p))
            .Where(x => x.Key is not null)
            .ToLookup(x => x.Key!, x => x.Person, StringComparer.Ordinal);
        _byName = snapshot.People
            .Select(p => (Key: NameKey(p.Current), Person: p))
            .Where(x => x.Key is not null)
            .ToLookup(x => x.Key!, x => x.Person, StringComparer.Ordinal);
    }

    /// <summary>jin-ji の人の氏名の検索用の正規化表記（取込元の search_name と同じ規則）。</summary>
    public static string? NameKey(PersonVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return TextNormalizer.ToSearchKey(version.FamilyName + version.GivenName);
    }

    /// <summary>社用メールの比較用の表記。</summary>
    public static string? EmailKey(string? email) =>
        TextNormalizer.CleanWhitespace(email) is { } cleaned ? TextNormalizer.Nfkc(cleaned).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant() : null;

    /// <param name="row">正規化した行。</param>
    /// <param name="legalEntityId">行の法人（分からなければ null。社員番号を全法人で比べる）。</param>
    /// <param name="orgUnitIds">行の所属組織（正規化氏名 + 所属の突合に使う）。</param>
    public MatchResult Match(NormalizedRow row, Guid? legalEntityId, IReadOnlyCollection<Guid> orgUnitIds)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(orgUnitIds);
        var email = row[IngestFields.WorkEmail];
        var numberUnknown = false;

        if (row[IngestFields.EmployeeNumber] is { } number)
        {
            var found = _byNumber[number].Where(p => legalEntityId is null || p.LegalEntityId == legalEntityId).ToList();
            if (found.Count == 1)
            {
                var conflict = email is not null && _byEmail[email].Any(p => p.Id != found[0].Id);
                return Result(MatchOutcome.Unique, MatchRule.EmployeeNumber, found, conflict, false);
            }

            if (found.Count > 1)
            {
                return Result(MatchOutcome.Ambiguous, MatchRule.EmployeeNumber, found, false, false);
            }

            numberUnknown = true;
        }

        if (email is not null)
        {
            var found = _byEmail[email].ToList();
            if (found.Count >= 1)
            {
                // 行の社員番号が jin-ji に無いのに、社用メールは既存の人のもの → 社員番号の誤りか別人か決められない（衝突）
                return Result(found.Count == 1 ? MatchOutcome.Unique : MatchOutcome.Ambiguous, MatchRule.WorkEmail, found, numberUnknown, numberUnknown);
            }
        }

        if (row[IngestFields.SearchName] is { } name && orgUnitIds.Count > 0)
        {
            var found = _byName[name]
                .Where(p => _snapshot.AssignmentsOf(p.Id).Any(a => orgUnitIds.Contains(a.OrgUnitId)))
                .ToList();
            if (found.Count > 0)
            {
                return Result(found.Count == 1 ? MatchOutcome.Unique : MatchOutcome.Ambiguous, MatchRule.NameAndOrg, found, false, numberUnknown);
            }
        }

        return new MatchResult(MatchOutcome.None, null, [], false, numberUnknown);
    }

    private static MatchResult Result(MatchOutcome outcome, MatchRule rule, List<PersonRecord> people, bool conflict, bool numberUnknown) =>
        new(
            outcome,
            rule,
            [.. people.OrderBy(p => p.Id).Select(p => new ProposalCandidate(p.Id, rule, outcome == MatchOutcome.Unique ? Confidence[rule] : Confidence[rule] / 2))],
            conflict,
            numberUnknown);
}
