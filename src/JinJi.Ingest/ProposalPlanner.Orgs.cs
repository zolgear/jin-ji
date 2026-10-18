using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Ingest.Matching;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>組織の取込（組織図）。</summary>
public sealed partial class ProposalPlanner
{
    /// <summary>仮の組織コードの接頭辞（組織コードの無い組織に採番する）。</summary>
    public const string ProvisionalCodePrefix = "TMP-";

    private sealed record OrgPlan(NormalizedRow Row, Guid Entity, string Code, string NameKey, OrgUnit? Existing, Guid UnitId)
    {
        public bool Abolished { get; set; }

        public DateOnly? AbolishedOn { get; set; }

        public bool AbolishedByParent { get; set; }
    }

    /// <summary>
    /// 組織図の行から、組織の新規・組織の版の差分・組織の廃止を提案する。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>組織は組織コード（法人の中）で突合する。コードの無い組織は、法人・上位組織名・組織名から決まる仮コード（<see cref="ProvisionalCodePrefix"/>）を採番し、
    /// 修正依頼に残す（同じ組織図を取り込み直しても同じ仮コードになる）</item>
    /// <item>段は、指定が無ければ組織名の末尾を法人の段の表示名と照らして決める（「営業本部」→ 本部、「企画ｸﾞﾙｰﾌﾟ」→ グループ）。当てはまらなければ段なし（室など）</item>
    /// <item>上位組織は、同じ組織図の組織（名称の正規化表記が一致）か、jin-ji の組織</item>
    /// <item>備考の「廃止」「統合」「旧組織」「分割」で廃止済みと判断する。廃止済みの組織の配下も廃止済みとみなす。jin-ji に無ければ作らず修正依頼に、
    /// あれば廃止日の翌日で有効期間を終える提案にする（廃止日が分からなければ要確認）</item>
    /// <item>責任者は社員番号、無ければ氏名（法人の中、無ければ全法人で一意）で探す。退職者なら組織長にせず修正依頼と補完提案にする</item>
    /// </list>
    /// </remarks>
    private void OrgUnits(IReadOnlyList<NormalizedRow> rows)
    {
        foreach (var row in rows)
        {
            AddIssueCorrections(row);
        }

        var usable = ExcludeDuplicates(rows, IngestFields.OrgCode, "組織コード");

        // 1 回目: 行の組織を既存の組織か新規の ID に対応させる（同じ組織図の中で上位組織を参照できるように）
        var plans = new List<OrgPlan>();
        foreach (var row in usable)
        {
            if (!TryResolveLegalEntity(row, out var rowEntity) || (rowEntity ?? _context.DefaultLegalEntityId) is not { } entity)
            {
                Correction(row, IngestFields.LegalEntityCode, CorrectionIssue.UnknownLegalEntity, "組織の法人が決まりません（法人のコードを指定してください）。");
                continue;
            }

            var nameKey = OrgNameNormalizer.ToSearchKey(row[IngestFields.OrgName]);
            if (row[IngestFields.OrgCode] is null && nameKey is null)
            {
                Correction(row, IngestFields.OrgCode, CorrectionIssue.MissingRequired, "組織コードも組織名もありません。");
                continue;
            }

            var code = row[IngestFields.OrgCode] ?? ProvisionalCode(entity, row[IngestFields.ParentOrgSearchName], nameKey!);
            var existing = UnitsByCode(entity, code).SingleOrDefault();
            if (existing is null && row[IngestFields.OrgName] is null)
            {
                Correction(row, IngestFields.OrgName, CorrectionIssue.MissingRequired, "新しい組織の組織名がありません。");
                continue;
            }

            var plan = new OrgPlan(row, entity, code, nameKey ?? OrgNameNormalizer.ToSearchKey(existing!.Name)!, existing, existing?.Id ?? EntityId.New());
            if (row[IngestFields.OrgRemarks] is { } remarks && AbolishedRemark().IsMatch(remarks))
            {
                plan.Abolished = true;
                plan.AbolishedOn = remarks.Split([' ', '　'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => JapaneseDateParser.TryParse(t, out var d, allowExcelSerial: false) ? d : (DateOnly?)null)
                    .FirstOrDefault(d => d is not null);
            }

            plans.Add(plan);
        }

        // 廃止済みの組織の配下も廃止済み（備考に表示が無くても）
        bool changed;
        do
        {
            changed = false;
            foreach (var plan in plans.Where(p => !p.Abolished))
            {
                if (ParentPlan(plans, plan) is { Abolished: true } parent)
                {
                    plan.Abolished = true;
                    plan.AbolishedByParent = true;
                    plan.AbolishedOn = parent.AbolishedOn;
                    changed = true;
                }
            }
        }
        while (changed);

        var createProposals = plans.Where(p => p.Existing is null && !p.Abolished).ToDictionary(p => p.UnitId, _ => EntityId.New());
        foreach (var plan in plans)
        {
            var row = plan.Row;
            if (plan.Abolished)
            {
                PlanAbolished(plan);
                continue;
            }

            if (row[IngestFields.OrgCode] is null)
            {
                Correction(row, IngestFields.OrgCode, CorrectionIssue.MissingRequired, "組織コードが無いため、仮のコードで登録します。人事側で組織コードを設定してください。");
            }

            // 段（指定が無ければ組織名の末尾から）
            var scheme = Snapshot.LevelSchemeOf(plan.Entity);
            string? levelKey;
            var levelGiven = row[IngestFields.OrgLevel] is not null;
            if (row[IngestFields.OrgLevel] is { } level)
            {
                levelKey = scheme.Find(level.ToLowerInvariant())?.Key ?? scheme.Levels.FirstOrDefault(l => l.Name == level)?.Key;
                if (levelKey is null)
                {
                    Correction(row, IngestFields.OrgLevel, CorrectionIssue.UnknownOrgLevel, "段が法人の段の定義にありません。");
                    continue;
                }
            }
            else
            {
                levelKey = InferLevel(scheme, row[IngestFields.OrgName] ?? plan.Existing?.Name);
            }

            // 上位組織（同じ組織図の組織か、jin-ji の組織）
            Guid? parent = null;
            var hasParent = row[IngestFields.ParentOrgCode] is not null || row[IngestFields.ParentOrgName] is not null;
            if (hasParent)
            {
                parent = ParentPlan(plans, plan)?.UnitId ?? ResolveOrg(row, plan.Entity, IngestFields.ParentOrgCode, IngestFields.ParentOrgName);
                if (parent is null)
                {
                    continue;
                }
            }

            var head = ResolveHead(row, plan.Entity);
            var site = ResolveSite(row, plan.Entity, out _);

            Guid? proposalId;
            if (plan.Existing is not { } existing)
            {
                var validFrom = Date(row[IngestFields.OrgValidFrom]) ?? _context.EffectiveDate ?? _context.Today;
                proposalId = Add(
                    row,
                    ProposalTargetKind.OrgUnit,
                    ProposalOperation.Create,
                    plan.UnitId,
                    validFrom,
                    Payload(
                        (ProposalFields.LegalEntityId, Id(plan.Entity)),
                        (ProposalFields.Code, plan.Code),
                        (ProposalFields.Name, row[IngestFields.OrgName]),
                        (ProposalFields.ShortName, row[IngestFields.OrgShortName]),
                        (ProposalFields.LevelKey, levelKey),
                        (ProposalFields.ParentOrgUnitId, parent is { } p ? Id(p) : null),
                        (ProposalFields.HeadPersonId, head is { } h ? Id(h) : null),
                        (ProposalFields.SiteId, site is null ? null : Id(site.Id)),
                        (ProposalFields.ValidFrom, Iso(validFrom))),
                    new(),
                    new ProposalBasis { DependsOn = ParentProposal(parent), Note = "組織コードが jin-ji にありません（新規）。" },
                    row[IngestFields.OrgCode] is null ? CreateWithSameNameConfidence : CreateConfidence,
                    id: createProposals[plan.UnitId]);

                // 結合セルの補いで決めた上位組織は、段飛ばし（さらに上の組織の直下）の可能性がある。上位組織の候補を要確認にする（新規の組織だけ）
                if (parent is { } carried && AlternativeParent(plans, plan) is { } alternative && alternative != carried)
                {
                    Add(
                        row,
                        ProposalTargetKind.OrgUnitVersion,
                        ProposalOperation.Supplement,
                        plan.UnitId,
                        null,
                        Payload((ProposalFields.ParentOrgUnitId, Id(carried))),
                        new(),
                        new ProposalBasis
                        {
                            DependsOn = [proposalId.Value],
                            OrgCandidates = [carried, alternative],
                            Note = "組織図の結合セルの補いで上位組織を決めました。段を飛ばして、さらに上の組織の直下にある可能性もあります。上位組織を確かめてください。",
                            Code = SupplementCodes.OrgParentAmbiguous,
                        },
                        0.5m,
                        ProposalStatus.NeedsReview);
                }
            }
            else
            {
                var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
                if (row[IngestFields.OrgName] is { } name && name != existing.Name)
                {
                    diff[ProposalFields.Name] = new FieldChange(existing.Name, name);
                }

                if (row[IngestFields.OrgShortName] is { } shortName && shortName != existing.ShortName)
                {
                    diff[ProposalFields.ShortName] = new FieldChange(existing.ShortName, shortName);
                }

                if ((levelGiven || levelKey is not null) && levelKey != existing.LevelKey)
                {
                    diff[ProposalFields.LevelKey] = new FieldChange(existing.LevelKey, levelKey);
                }

                if (site is not null && site.Id != existing.SiteId)
                {
                    diff[ProposalFields.SiteId] = new FieldChange(existing.SiteId is { } os ? Id(os) : null, Id(site.Id));
                }

                if (hasParent && parent != existing.ParentId)
                {
                    diff[ProposalFields.ParentOrgUnitId] = new FieldChange(existing.ParentId is { } op ? Id(op) : null, Id(parent!.Value));
                }

                // 責任者の欄が空・退職者なら、所属者の役職（課長・リーダーなど、組織の段に合う役職）から組織長を推定する
                var inferred = false;
                if (head is null && existing.HeadPersonId is null && InferHead(existing.Id, levelKey ?? existing.LevelKey) is { } guess)
                {
                    head = guess;
                    inferred = true;
                }

                if (head is { } newHead && newHead != existing.HeadPersonId)
                {
                    diff[ProposalFields.HeadPersonId] = new FieldChange(existing.HeadPersonId is { } oh ? Id(oh) : null, Id(newHead));
                }

                proposalId = diff.Count == 0
                    ? null
                    : Add(
                        row,
                        ProposalTargetKind.OrgUnitVersion,
                        ChangeOperation(),
                        existing.Id,
                        _context.EffectiveDate,
                        new(),
                        diff,
                        new ProposalBasis
                        {
                            DependsOn = ParentProposal(parent),
                            Note = inferred ? "組織図の責任者の欄が空か退職者のため、所属者の役職から組織長を推定しました。" : null,
                        },
                        inferred ? 0.7m : 1.0m);
                head ??= existing.HeadPersonId;
            }

            if (head is null)
            {
                SupplementHead(plan.UnitId, [row.StagingRowId], proposalId is { } pid ? [pid] : [], row.SourceRowNumber);
            }
        }

        List<Guid> ParentProposal(Guid? parent) =>
            parent is { } p && createProposals.TryGetValue(p, out var id) ? [id] : [];
    }

    private void PlanAbolished(OrgPlan plan)
    {
        var row = plan.Row;
        var reason = plan.AbolishedByParent ? "上位組織が廃止済みのため、この組織も廃止済みとみなします。" : "備考に廃止・統合の記載があります。";
        if (plan.Existing is null)
        {
            Correction(row, null, CorrectionIssue.AbolishedOrg, $"廃止済みの組織が組織図に残っています（jin-ji には作りません）。{reason}");
            return;
        }

        if (plan.AbolishedOn is { } on)
        {
            Add(row, ProposalTargetKind.OrgUnitVersion, ProposalOperation.Abolish, plan.Existing.Id, on.AddDays(1), new(), new(), new ProposalBasis { Note = reason }, plan.AbolishedByParent ? 0.7m : 0.9m);
        }
        else
        {
            Add(row, ProposalTargetKind.OrgUnitVersion, ProposalOperation.Supplement, plan.Existing.Id, null, new(), new(), new ProposalBasis { Note = $"{reason}廃止日が分からないので、廃止日を決めてください。", Code = SupplementCodes.AbolishedDateUnknown }, 0.5m, ProposalStatus.NeedsReview);
        }

        Correction(row, null, CorrectionIssue.AbolishedOrg, $"廃止済みの組織が組織図に残っています。{reason}");
    }

    /// <summary>同じ組織図の中の上位組織（コードか、名称の正規化表記が一致する、同じ法人の組織）。</summary>
    private static OrgPlan? ParentPlan(List<OrgPlan> plans, OrgPlan plan)
    {
        var row = plan.Row;
        List<OrgPlan> found;
        if (row[IngestFields.ParentOrgCode] is { } code)
        {
            found = [.. plans.Where(p => p.Entity == plan.Entity && p != plan && string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase))];
        }
        else if (row[IngestFields.ParentOrgSearchName] is { } key)
        {
            found = [.. plans.Where(p => p.Entity == plan.Entity && p != plan && p.NameKey == key)];
        }
        else
        {
            return null;
        }

        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>段飛ばしの場合の上位組織（<see cref="IngestFields.ParentOrgAltName"/>。同じ組織図の組織か、jin-ji の組織。廃止済みは候補にしない）。</summary>
    private Guid? AlternativeParent(List<OrgPlan> plans, OrgPlan plan)
    {
        if (OrgNameNormalizer.ToSearchKey(plan.Row[IngestFields.ParentOrgAltName]) is not { } key)
        {
            return null;
        }

        var found = plans.Where(p => p.Entity == plan.Entity && p != plan && p.NameKey == key).ToList();
        if (found.Count == 1)
        {
            return found[0].Abolished ? null : found[0].UnitId;
        }

        return found.Count == 0 ? ResolveOrgByName(plan.Row[IngestFields.ParentOrgAltName]!, plan.Entity) : null;
    }

    /// <summary>組織の責任者（社員番号、無ければ氏名）。退職者は組織長にしない。</summary>
    private Guid? ResolveHead(NormalizedRow row, Guid entity)
    {
        List<PersonRecord> heads;
        string field;
        if (row[IngestFields.OrgHeadEmployeeNumber] is { } number)
        {
            field = IngestFields.OrgHeadEmployeeNumber;
            heads = [.. Snapshot.People.Where(p => p.LegalEntityId == entity && _context.EmployeeNumbers.Normalize(p.EmployeeNumber) == number)];
        }
        else if (TextNormalizer.ToSearchKey(row[IngestFields.OrgHeadName]) is { } name)
        {
            field = IngestFields.OrgHeadName;
            var all = Snapshot.People.Where(p => PersonMatcher.NameKey(p.Current) == name).ToList();
            heads = all.Where(p => p.LegalEntityId == entity).ToList() is { Count: > 0 } inEntity ? inEntity : all;
        }
        else
        {
            return null;
        }

        if (heads.Count != 1)
        {
            Correction(row, field, CorrectionIssue.UnknownHead, heads.Count == 0 ? "責任者に当たる人が jin-ji にいません。" : "責任者の氏名に当たる人が複数います（同姓同名）。");
            return null;
        }

        if (heads[0].IsTerminatedOn(_context.Today))
        {
            Correction(row, field, CorrectionIssue.StaleHead, "責任者が退職者のままです。現在の責任者を設定してください。");
            return null;
        }

        return heads[0].Id;
    }

    /// <summary>
    /// 組織長の推定: 組織に（主務・兼務で）所属し、組織の段に合う長の役職（課なら課長、チームならリーダー、段なしの室なら室長）を持つ人が 1 人なら、その人。
    /// </summary>
    private Guid? InferHead(Guid unitId, string? levelKey)
    {
        var titles = levelKey is null
            ? ["室長"]
            : ActionTextParser.TitleLevels.Where(t => t.Value == levelKey).Select(t => t.Key).ToList();
        var heads = Snapshot.People
            .Where(p => !p.IsTerminatedOn(_context.Today))
            .Where(p => Snapshot.AssignmentsOf(p.Id).Any(a => a.OrgUnitId == unitId && a.PositionTitle is { } title && titles.Contains(title)))
            .Select(p => p.Id)
            .ToList();
        return heads.Count == 1 ? heads[0] : null;
    }

    /// <summary>組織名の末尾を、法人の段の表示名と照らして段のキーを決める（長い表示名を優先。当てはまらなければ段なし）。</summary>
    public static string? InferLevel(OrgLevelScheme scheme, string? name)
    {
        ArgumentNullException.ThrowIfNull(scheme);
        var display = TextNormalizer.ToDisplay(name);
        return display is null ? null : scheme.Levels.OrderByDescending(l => l.Name.Length).FirstOrDefault(l => display.EndsWith(l.Name, StringComparison.Ordinal))?.Key;
    }

    /// <summary>組織コードの無い組織の仮コード（法人・上位組織名・組織名から決まる。取り込み直しても同じ）。</summary>
    public static string ProvisionalCode(Guid entity, string? parentKey, string nameKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{entity:N}/{parentKey}/{nameKey}"));
        return ProvisionalCodePrefix + Convert.ToHexString(hash)[..8];
    }

    [GeneratedRegex("廃止|統合|旧組織|分割|閉鎖", RegexOptions.CultureInvariant)]
    private static partial Regex AbolishedRemark();
}
