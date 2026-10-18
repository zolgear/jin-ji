using JinJi.Application.Ingest;
using JinJi.Domain.Organization;
using JinJi.Ingest.Matching;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>辞令（発令の一覧）の取込。</summary>
public sealed partial class ProposalPlanner
{
    private const decimal SurnameOnlyMismatchConfidence = 0.8m;

    /// <summary>
    /// 辞令の行から発令の提案を作る。発令内容は <see cref="ActionTextParser"/> でルールにより解釈する（発令内容の解釈に LLM は使わない。
    /// 要確認の行の人・組織の候補の順位付けにだけ、オプトインで LLM を使う。<see cref="Suggestions.ProposalSuggester"/>）。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>人は社員番号で探し、氏名（検索用の正規化表記）と照合する。名が同じで姓だけが違う場合は旧姓・通称の違いとみなして受け入れる（確信度を下げる）。
    /// 名も違えば社員番号の誤記の疑いとして、氏名で探した候補を付けて要確認にする（別人の番号に偶然一致した場合も含む）</item>
    /// <item>社員番号が無い・jin-ji に無い行は、氏名で探した候補を付けて要確認にする（同姓同名なら候補が複数）</item>
    /// <item>組織は名称（法人の別名が前に付けば、その法人の中）で探す。見つからなければ修正依頼と要確認</item>
    /// <item>役職が表す段より上位の組織しか書かれていない（「製造第二部 課長」）ときは所属が一意に定まらないので要確認にする。
    /// 配下のその段の組織のうち本人が今所属している組織があれば、提案の値として示す</item>
    /// <item>発令日が業務日付より後の発令（内示）も提案にする。反映すると、発令と将来の所属・一時停止の行ができ、人事管理者以外には見えない（RLS・投影）</item>
    /// <item>発令の反映は、承認時の所属・一時停止から版を導出する（同じ取込の休職と復職を順に承認すれば、休職期間ができる）</item>
    /// </list>
    /// </remarks>
    private void PersonnelActions(IReadOnlyList<NormalizedRow> rows)
    {
        foreach (var row in rows)
        {
            AddIssueCorrections(row);
        }

        var numbers = Snapshot.People
            .Select(p => (Key: _context.EmployeeNumbers.Normalize(p.EmployeeNumber), Person: p))
            .Where(x => x.Key is not null)
            .ToLookup(x => x.Key!, x => x.Person, StringComparer.Ordinal);
        var names = Snapshot.People
            .Select(p => (Key: PersonMatcher.NameKey(p.Current), Person: p))
            .Where(x => x.Key is not null)
            .ToLookup(x => x.Key!, x => x.Person, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (Date(row[IngestFields.ActionDate]) is not { } date)
            {
                if (!row.HasIssue(IngestFields.ActionDate, IssueCodes.InvalidDate))
                {
                    Correction(row, IngestFields.ActionDate, CorrectionIssue.MissingRequired, "発令日がありません。");
                }

                continue;
            }

            // 人
            PersonRecord? person = null;
            var confidence = 1.0m;
            string? reviewNote = null;
            var candidates = new List<ProposalCandidate>();
            var byName = row[IngestFields.SearchName] is { } nameKey ? names[nameKey].ToList() : [];
            if (row[IngestFields.EmployeeNumber] is { } number)
            {
                var found = numbers[number].ToList();
                if (found.Count == 1 && NameAgrees(row, found[0], out var surnameOnly))
                {
                    person = found[0];
                    if (surnameOnly)
                    {
                        // 姓だけが違う = 戸籍姓（旧姓使用者）や通称で書かれた行。戸籍名は保持しないので、作業領域からも氏名を除く（ADR-0002）
                        confidence = SurnameOnlyMismatchConfidence;
                        _scrub[row.StagingRowId] = NameFields;
                    }
                }
                else
                {
                    Correction(
                        row,
                        IngestFields.EmployeeNumber,
                        CorrectionIssue.SuspectedEmployeeNumberTypo,
                        found.Count == 0 ? "社員番号が jin-ji にありません（誤記の疑い）。" : "社員番号と氏名が一致しません（誤記の疑い）。");
                    reviewNote = "社員番号の誤記の疑いがあります。氏名で探した候補を確認してください。";
                }
            }
            else
            {
                reviewNote = "社員番号がありません。氏名で探した候補を確認してください。";
            }

            if (person is null)
            {
                candidates.AddRange(byName.OrderBy(p => p.Id).Select(p => new ProposalCandidate(p.Id, MatchRule.NameAndOrg, byName.Count == 1 ? PersonMatcher.Confidence[MatchRule.NameAndOrg] : PersonMatcher.Confidence[MatchRule.NameAndOrg] / 2)));
                if (byName.Count > 1)
                {
                    reviewNote += "同姓同名の人が複数います。";
                }
            }

            RecordActionCandidates(row, person, candidates);

            // 発令内容
            var parsed = ActionTextParser.Parse(row[IngestFields.ActionText]);
            if (parsed is null)
            {
                Correction(row, IngestFields.ActionText, CorrectionIssue.UninterpretableAction, "発令内容をルールで解釈できません。");
                ReviewAction(row, person, candidates, date, null, null, "発令内容をルールで解釈できません。" + reviewNote, SupplementCodes.UninterpretableAction);
                continue;
            }

            var subject = person ?? (candidates.Count == 1 ? Snapshot.FindPerson(candidates[0].PersonId) : null);
            if (parsed.IsPlacement && subject is not null && subject.Current.HireDate == date)
            {
                // 入社日と同じ日の「配属」は入社時の配属
                parsed = parsed with { Type = PersonnelActionType.Hire };
            }
            var entity = subject?.LegalEntityId ?? _context.DefaultLegalEntityId;
            Guid? org = null;
            Guid? destination = null;
            string? orgNote = null;

            if (parsed.Type == PersonnelActionType.SecondmentStart && parsed.TargetText is { } target)
            {
                var (code, rest) = _context.Profile.SplitLegalEntityPrefix(target);
                destination = code is null ? null : Snapshot.LegalEntities.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase))?.Id;
                var orgText = rest.Trim().TrimStart('(').TrimEnd(')').Trim();
                if (destination is null)
                {
                    orgNote = "出向先の法人が jin-ji の法人に見つかりません。";
                }
                else if (orgText.Length > 0)
                {
                    org = ResolveOrgByName(orgText, destination);
                    orgNote = org is null ? "出向先の組織が見つかりません。" : null;
                }
            }
            else if (parsed.OrgText is { } orgText)
            {
                org = ResolveOrgByName(orgText, entity);
                if (org is null)
                {
                    orgNote = "発令内容の組織が jin-ji の組織に見つからないか、一意に決まりません。";
                    Correction(row, IngestFields.ActionText, CorrectionIssue.UnknownOrg, orgNote);
                }
            }
            else if (parsed.Type == PersonnelActionType.PositionAppointment && subject is not null)
            {
                org = Snapshot.AssignmentsOf(subject.Id).FirstOrDefault(a => a.Type == AssignmentType.Primary)?.OrgUnitId;
            }

            // 役職の段より上位の組織しか書かれていない（「製造第二部 課長」）
            List<Guid> orgCandidates = [];
            if (org is { } o && parsed.Title is { } title && ActionTextParser.TitleLevels.TryGetValue(title, out var titleLevel)
                && Snapshot.FindUnit(o) is { } unit && Snapshot.LevelSchemeOf(unit.LegalEntityId).Find(titleLevel) is { } level
                && Snapshot.RankOf(o) is { } rank && rank < level.Rank)
            {
                var below = Snapshot.Units.Where(u => u.LevelKey == titleLevel && Snapshot.IsSelfOrDescendantOf(u.Id, o)).Select(u => u.Id).ToHashSet();
                orgCandidates = [.. below.Order()];
                var current = subject is null ? [] : Snapshot.AssignmentsOf(subject.Id).Select(a => a.OrgUnitId).Where(below.Contains).Distinct().ToList();
                orgNote = $"役職（{title}）の段より上位の組織しか書かれておらず、所属が一意に定まりません（配下の候補 {below.Count} 件）。";
                org = current.Count == 1 ? current[0] : null;
                reviewNote = (reviewNote ?? string.Empty) + orgNote + (org is null ? string.Empty : "本人の現在の所属を候補として示します。");
            }
            else if (orgNote is not null)
            {
                reviewNote = (reviewNote ?? string.Empty) + orgNote;
            }

            if (person is null || reviewNote is not null)
            {
                var code = person is null
                    ? row[IngestFields.EmployeeNumber] is null ? SupplementCodes.MissingEmployeeNumber : SupplementCodes.SuspectedEmployeeNumberTypo
                    : SupplementCodes.AmbiguousOrg;
                ReviewAction(row, person, candidates, date, parsed, org, reviewNote ?? "要確認です。", code, destination, orgCandidates);
                continue;
            }

            Add(
                row,
                ProposalTargetKind.PersonnelAction,
                ProposalOperation.Create,
                person.Id,
                date,
                ActionPayload(person.Id, parsed, org, destination),
                new(),
                new ProposalBasis
                {
                    Rule = MatchRule.EmployeeNumber,
                    Note = (date > _context.Today ? "発令日前の発令（内示）です。反映しても人事管理者以外には見えません。" : null)
                        + (confidence < 1.0m ? "氏名の姓だけが違います（旧姓・通称の違いの可能性）。" : null),
                },
                confidence);
        }
    }

    /// <summary>氏名が一致するか。名が同じで姓だけが違えば一致とみなす（旧姓・通称）。</summary>
    private static bool NameAgrees(NormalizedRow row, PersonRecord person, out bool surnameOnly)
    {
        surnameOnly = false;
        if (row[IngestFields.SearchName] is not { } key)
        {
            return true;
        }

        if (key == PersonMatcher.NameKey(person.Current))
        {
            return true;
        }

        var personGiven = TextNormalizer.ToSearchKey(person.Current.GivenName);
        var given = TextNormalizer.ToSearchKey(row[IngestFields.GivenName]);
        if (personGiven is not null
            && (given is not null
                ? given == personGiven
                : key.Length > personGiven.Length && key.EndsWith(personGiven, StringComparison.Ordinal)))
        {
            // 空白の無い氏名（「上田直子」）は姓と名に分けられないので、jin-ji の名で終わるかで判断する
            surnameOnly = true;
            return true;
        }

        return false;
    }

    private void RecordActionCandidates(NormalizedRow row, PersonRecord? person, List<ProposalCandidate> candidates)
    {
        if (person is not null)
        {
            _candidates.Add(new MatchCandidateRecord(Domain.EntityId.New(), row.StagingRowId, MatchOutcome.Unique, MatchRule.EmployeeNumber, person.Id, 1.0m));
            return;
        }

        RecordCandidates(row, new MatchResult(
            candidates.Count switch { 0 => MatchOutcome.None, 1 => MatchOutcome.Unique, _ => MatchOutcome.Ambiguous },
            candidates.Count == 0 ? null : MatchRule.NameAndOrg,
            candidates,
            false,
            false));
    }

    private void ReviewAction(
        NormalizedRow row,
        PersonRecord? person,
        List<ProposalCandidate> candidates,
        DateOnly date,
        ParsedAction? parsed,
        Guid? org,
        string note,
        string code,
        Guid? destination = null,
        IReadOnlyList<Guid>? orgCandidates = null)
    {
        var target = person?.Id ?? (candidates.Count == 1 ? candidates[0].PersonId : (Guid?)null);
        var payload = parsed is null ? new Dictionary<string, string?>() : ActionPayload(target, parsed, org, destination);
        if (target is null)
        {
            payload.Remove(ProposalFields.PersonId);
        }

        Add(
            row,
            ProposalTargetKind.PersonnelAction,
            ProposalOperation.Review,
            target,
            date,
            payload,
            new(),
            new ProposalBasis { Rule = person is null ? MatchRule.NameAndOrg : MatchRule.EmployeeNumber, Candidates = candidates, OrgCandidates = orgCandidates ?? [], Note = note, Code = code },
            candidates.Count == 0 ? (person is null ? 0m : 0.5m) : candidates.Max(c => c.Confidence),
            ProposalStatus.NeedsReview);
    }

    private static Dictionary<string, string?> ActionPayload(Guid? person, ParsedAction parsed, Guid? org, Guid? destination)
    {
        var concurrent = parsed.Type is PersonnelActionType.ConcurrentStart or PersonnelActionType.SecondmentStart;
        return Payload(
            (ProposalFields.PersonId, person is { } p ? Id(p) : null),
            (ProposalFields.ActionType, ActionCode(parsed.Type)),
            (ProposalFields.OrgUnitId, org is { } o ? Id(o) : null),
            (ProposalFields.PositionTitle, parsed.Title),
            (ProposalFields.AssignmentType, org is null ? null : concurrent ? "concurrent" : "primary"),
            (ProposalFields.SecondmentType, parsed.Type == PersonnelActionType.SecondmentStart ? "outbound" : null),
            (ProposalFields.LegalEntityId, destination is { } d ? Id(d) : null));
    }

    /// <summary>発令の種類の DB のコード（personnel_action.action_type）。</summary>
    public static string ActionCode(PersonnelActionType type) => type switch
    {
        PersonnelActionType.Hire => "hire",
        PersonnelActionType.Termination => "termination",
        PersonnelActionType.Transfer => "transfer",
        PersonnelActionType.PositionAppointment => "position_appointment",
        PersonnelActionType.PositionDismissal => "position_dismissal",
        PersonnelActionType.ConcurrentStart => "concurrent_start",
        PersonnelActionType.ConcurrentEnd => "concurrent_end",
        PersonnelActionType.SecondmentStart => "secondment_start",
        PersonnelActionType.SecondmentEnd => "secondment_end",
        PersonnelActionType.SuspensionStart => "suspension_start",
        PersonnelActionType.SuspensionEnd => "suspension_end",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
