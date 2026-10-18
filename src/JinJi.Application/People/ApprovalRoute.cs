using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Policy;

namespace JinJi.Application.People;

/// <summary>組織長が空席の段の扱い。</summary>
public enum ApprovalVacancyMode
{
    /// <summary>空席の段を飛ばして上位の組織の組織長へ繰り上げ、飛ばした組織を次の段に記録する（既定）。</summary>
    Promote,

    /// <summary>空席の段も段として返す（承認者なし）。</summary>
    Vacant,
}

/// <summary>承認ルートの段の状態。</summary>
public enum ApprovalStepStatus
{
    /// <summary>承認者（組織長）がいる。一時停止中かどうかは承認者の account_status で示す（代わりの人は推測しない）。</summary>
    Assigned,

    /// <summary>組織長が空席（<see cref="ApprovalVacancyMode.Vacant"/> のときだけ現れる）。</summary>
    Vacant,
}

/// <summary>段として返さずに飛ばした組織の理由。</summary>
public enum ApprovalSkipReason
{
    /// <summary>組織長が空席（組織長がいない・退職済み・閲覧者に見えない）。</summary>
    Vacant,

    /// <summary>起点の人自身が組織長（自分の申請を自分で承認させない）。</summary>
    Self,
}

/// <summary>承認ルートの問い合わせ。</summary>
/// <remarks>
/// 段（<see cref="LevelKeys"/>）と役職（<see cref="Positions"/>）は、経路をたどった結果から該当する段だけを返す絞り込み（同じ条件の中は OR、条件同士は AND）。
/// 段は法人ごとの段の定義のキー（例: department）で指定する。段の定義に無いキーは何にも当たらない（人の検索の level と同じ）。
/// </remarks>
public sealed record ApprovalRouteQuery
{
    /// <summary>起点の人。</summary>
    public required Guid PersonId { get; init; }

    /// <summary>起点の所属組織（兼務・出向の所属を起点にするとき）。null なら主務。</summary>
    public Guid? StartOrgUnitId { get; init; }

    public ApprovalVacancyMode Vacancy { get; init; } = ApprovalVacancyMode.Promote;

    /// <summary>段のキーで絞り込む（その段の組織の組織長。繰り上げのときは、その段を飛ばして繰り上げた先の段も当たる）。</summary>
    public IReadOnlySet<string> LevelKeys { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>承認者の役職（その組織への所属の役職。検索用の正規化表記の完全一致）で絞り込む。</summary>
    public IReadOnlySet<string> Positions { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>形を検査する。不正なら <see cref="ArgumentException"/>。</summary>
    public void Validate()
    {
        Check("level", LevelKeys, 63);
        Check("position", Positions, PersonSearch.MaxValueLength);
        if (LevelKeys.FirstOrDefault(k => !OrgLevelScheme.IsValidKey(k)) is { } bad)
        {
            throw new ArgumentException($"level は段のキー（英小文字で始まる英小文字・数字・_）で指定してください（{bad} は使えません）。", nameof(LevelKeys));
        }
    }

    private static void Check(string name, IReadOnlySet<string> values, int maxLength)
    {
        if (values.Count > PersonSearch.MaxValuesPerFilter)
        {
            throw new ArgumentException($"{name} は {PersonSearch.MaxValuesPerFilter} 件までです。", name);
        }

        if (values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > maxLength || v.Any(char.IsControl)))
        {
            throw new ArgumentException($"{name} の値は空でない {maxLength} 文字以内の文字列で指定してください（制御文字は使えません）。", name);
        }
    }
}

/// <summary>経路に現れる組織（組織の情報は全社公開）。</summary>
/// <param name="LevelKey">段のキー（法人の段の定義に無い・段を持たない組織は null）。</param>
public sealed record ApprovalOrgUnit(Guid Id, Guid LegalEntityId, string Code, string Name, string? LevelKey, string? LevelName, int? LevelRank);

/// <summary>経路の起点（起点の人の所属）。</summary>
public sealed record ApprovalStart(ApprovalOrgUnit OrgUnit, AssignmentType Type, SecondmentType Secondment, string? PositionTitle);

/// <summary>段として返さずに飛ばした組織。</summary>
public sealed record ApprovalSkippedUnit(ApprovalOrgUnit OrgUnit, ApprovalSkipReason Reason);

/// <summary>承認ルートの 1 段。</summary>
/// <param name="Order">絞り込む前の経路での順番（1 から。下の段が先）。</param>
/// <param name="Approver">承認者（組織長。閲覧者に見える項目だけの投影）。空席なら null。</param>
/// <param name="ApproverPosition">承認者のその組織での役職（その組織への所属の役職。所属の項目が見えない・その組織に所属が無ければ null）。</param>
/// <param name="SkippedUnits">この段の手前で飛ばした組織（空席の繰り上げ・本人が組織長）。下の組織が先。</param>
public sealed record ApprovalStep(
    int Order,
    ApprovalOrgUnit OrgUnit,
    ApprovalStepStatus Status,
    PersonView? Approver,
    string? ApproverPosition,
    IReadOnlyList<ApprovalSkippedUnit> SkippedUnits);

/// <summary>承認ルート。</summary>
/// <param name="Start">起点の所属。所属が無ければ null（経路は空）。</param>
/// <param name="Steps">下の段から順の承認者。空なら承認者を決められない（自動で承認しないこと）。</param>
/// <param name="UnresolvedUnits">最上位まで繰り上げても承認者が見つからなかった組織（下の組織が先）。</param>
public sealed record ApprovalRoute(
    Guid PersonId,
    DateOnly AsOf,
    ApprovalVacancyMode Vacancy,
    ApprovalStart? Start,
    IReadOnlyList<ApprovalStep> Steps,
    IReadOnlyList<ApprovalSkippedUnit> UnresolvedUnits)
{
    /// <summary>返した人（承認者。重複なし）。</summary>
    public IReadOnlyList<Guid> ApproverIds => [.. Steps.Where(s => s.Approver is not null).Select(s => s.Approver!.PersonId).Distinct()];
}

/// <summary>
/// 承認ルートを組み立てる（アーキテクチャ概要 §9「承認ルート」）。スナップショットと投影だけを使い、DB に依存しない。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>起点の人の所属組織（既定は主務、指定すれば兼務・出向の所属）から上位の組織へたどり、各組織の組織長を下から順に承認者とする（組織長の連鎖）</item>
/// <item>起点の人自身が組織長の組織は飛ばす（<see cref="ApprovalSkipReason.Self"/>）</item>
/// <item>組織長が空席の組織（組織長がいない・時点で退職済み・閲覧者に見えない）は、既定では飛ばして上位へ繰り上げ、飛ばした組織を次の段に記録する。
/// <see cref="ApprovalVacancyMode.Vacant"/> なら空席の段として返す。見えない組織長を空席と同じに扱うのは、組織の参照（組織長の ID は見える人だけ）と同じく
/// 見えない人の存在を明かさないため</item>
/// <item>一時停止中の組織長は承認者として返す（状態は承認者の account_status）。代わりの人（代理承認）は jin-ji が持たないので推測しない</item>
/// <item>承認者は閲覧者の権限で投影する（社内システムなら項目の上限）。起点の人が見えない、または所属・上長の項目が見えなければ経路を返さない（null）</item>
/// </list>
/// </remarks>
internal sealed class ApprovalRouteBuilder(OrgSnapshot data, PersonProjector projector, ViewerContext viewer)
{
    /// <summary>経路を組み立てる。起点の人が見えない・起点の所属が無い（指定した組織に所属していない）なら null。</summary>
    public ApprovalRoute? Build(ApprovalRouteQuery query)
    {
        if (projector.Project(viewer, query.PersonId) is not { } target
            || !target.CanSee(PersonFields.Assignments)
            || !target.CanSee(PersonFields.Managers))
        {
            return null;
        }

        var assignments = target.Assignments ?? [];
        AssignmentView? start;
        if (query.StartOrgUnitId is { } requested)
        {
            start = assignments.FirstOrDefault(a => a.OrgUnitId == requested);
            if (start is null)
            {
                return null;
            }
        }
        else
        {
            start = assignments.FirstOrDefault(a => a.Type == AssignmentType.Primary);
        }

        if (start is null || data.FindUnit(start.OrgUnitId) is null)
        {
            return new ApprovalRoute(query.PersonId, data.AsOf, query.Vacancy, null, [], []);
        }

        var steps = new List<ApprovalStep>();
        var pending = new List<ApprovalSkippedUnit>();
        foreach (var unitId in data.AncestorsOf(start.OrgUnitId))
        {
            var unit = data.FindUnit(unitId)!;
            var org = ToOrg(unit);
            if (unit.HeadPersonId == query.PersonId)
            {
                pending.Add(new ApprovalSkippedUnit(org, ApprovalSkipReason.Self));
                continue;
            }

            if (Approver(unit) is not { } approver)
            {
                if (query.Vacancy == ApprovalVacancyMode.Vacant)
                {
                    steps.Add(new ApprovalStep(steps.Count + 1, org, ApprovalStepStatus.Vacant, null, null, [.. pending]));
                    pending.Clear();
                }
                else
                {
                    pending.Add(new ApprovalSkippedUnit(org, ApprovalSkipReason.Vacant));
                }

                continue;
            }

            var position = approver.Assignments?.FirstOrDefault(a => a.OrgUnitId == unit.Id)?.PositionTitle;
            steps.Add(new ApprovalStep(steps.Count + 1, org, ApprovalStepStatus.Assigned, approver, position, [.. pending]));
            pending.Clear();
        }

        var positions = query.Positions.Select(SearchKey.From).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var filtered = steps
            .Where(s => query.LevelKeys.Count == 0 || MatchesLevel(s, query.LevelKeys))
            .Where(s => positions.Count == 0 || (s.ApproverPosition is { } title && SearchKey.From(title) is { } key && positions.Contains(key)))
            .ToList();
        return new ApprovalRoute(
            query.PersonId,
            data.AsOf,
            query.Vacancy,
            new ApprovalStart(ToOrg(data.FindUnit(start.OrgUnitId)!), start.Type, start.Secondment, start.PositionTitle),
            filtered,
            [.. pending]);
    }

    /// <summary>段が当たるか: 段の組織そのもの、または繰り上げ・本人のために飛ばした組織のどれかがその段。</summary>
    private static bool MatchesLevel(ApprovalStep step, IReadOnlySet<string> levelKeys) =>
        (step.OrgUnit.LevelKey is { } key && levelKeys.Contains(key))
        || step.SkippedUnits.Any(s => s.OrgUnit.LevelKey is { } skipped && levelKeys.Contains(skipped));

    /// <summary>組織の承認者。組織長がいない・スナップショットに居ない・時点で退職済み・閲覧者に見えないなら null（空席と同じ）。</summary>
    private PersonView? Approver(OrgUnit unit)
    {
        if (unit.HeadPersonId is not { } head || data.FindPerson(head) is not { } person || person.IsTerminatedOn(data.AsOf))
        {
            return null;
        }

        return projector.Project(viewer, head);
    }

    private ApprovalOrgUnit ToOrg(OrgUnit unit)
    {
        var level = unit.LevelKey is { } key ? data.LevelSchemeOf(unit.LegalEntityId).Find(key) : null;
        return new ApprovalOrgUnit(unit.Id, unit.LegalEntityId, unit.Code, unit.Name, level?.Key, level?.Name, level?.Rank);
    }
}

/// <summary>承認ルートのコード値（REST・MCP の引数）。</summary>
public static class ApprovalRouteCodes
{
    public static readonly IReadOnlyList<string> Vacancies = ["promote", "vacant"];

    public static bool TryParseVacancy(string? value, out ApprovalVacancyMode mode)
    {
        (var ok, mode) = value switch
        {
            "promote" => (true, ApprovalVacancyMode.Promote),
            "vacant" => (true, ApprovalVacancyMode.Vacant),
            _ => (false, ApprovalVacancyMode.Promote),
        };
        return ok;
    }
}
