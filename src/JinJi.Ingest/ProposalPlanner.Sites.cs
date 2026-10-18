using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Ingest.Normalization;

namespace JinJi.Ingest;

/// <summary>拠点（拠点マスタの取込と、人・組織の拠点の照合）。</summary>
public sealed partial class ProposalPlanner
{
    /// <summary>拠点マスタの取込（拠点の一覧）の提案を作る。</summary>
    public static PlanResult PlanSites(PlanContext context, IReadOnlyList<NormalizedRow> rows)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(rows);
        var planner = new ProposalPlanner(context);
        planner.Sites(rows);
        return planner.Result();
    }

    /// <summary>
    /// 拠点マスタの行から、拠点の新規・拠点の版の差分・拠点の閉鎖を提案する。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>拠点は拠点コード（法人の中）で突合する。拠点コードの無い行は修正依頼にする（組織と違って仮コードは採番しない。人・組織の拠点がコードで当たらなくなるため）</item>
    /// <item>種別は、指定が無ければ拠点名の末尾から推定する（「大阪支店」→ 支社・支店、「横浜工場」→ 工場）。当てはまらなければ other</item>
    /// <item>都道府県は、指定が無ければ所在地の先頭から求める（正規化 <see cref="RowNormalizer"/>）。所在地は事業所の住所で、自由記述の混入の検査はしない</item>
    /// <item>閉鎖日（最終日）があれば、その翌日で有効期間を終える提案（jin-ji に無ければ作らず修正依頼）</item>
    /// </list>
    /// </remarks>
    private void Sites(IReadOnlyList<NormalizedRow> rows)
    {
        foreach (var row in rows)
        {
            AddIssueCorrections(row);
        }

        var usable = ExcludeDuplicates(rows, IngestFields.SiteCode, "拠点コード");
        foreach (var row in usable)
        {
            if (!TryResolveLegalEntity(row, out var rowEntity) || (rowEntity ?? _context.DefaultLegalEntityId) is not { } entity)
            {
                Correction(row, IngestFields.LegalEntityCode, CorrectionIssue.UnknownLegalEntity, "拠点の法人が決まりません（法人のコードを指定してください）。");
                continue;
            }

            if (row[IngestFields.SiteCode] is not { } code)
            {
                Correction(row, IngestFields.SiteCode, CorrectionIssue.MissingRequired, "拠点コードがありません。");
                continue;
            }

            var existing = Snapshot.Sites.SingleOrDefault(s => s.LegalEntityId == entity && string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase));
            var type = row[IngestFields.SiteType]
                ?? (SiteCodes.TryInfer(row[IngestFields.SiteName] ?? existing?.Name, out var inferred) ? SiteCodes.ToCode(inferred) : null);

            if (row[IngestFields.SiteClosedDate] is { } closed)
            {
                if (existing is null)
                {
                    Correction(row, IngestFields.SiteClosedDate, CorrectionIssue.InvalidValue, "閉鎖した拠点が jin-ji にありません（登録しません）。");
                    continue;
                }

                var endsOn = Date(closed)!.Value.AddDays(1);
                Add(
                    row,
                    ProposalTargetKind.SiteVersion,
                    ProposalOperation.Abolish,
                    existing.Id,
                    endsOn,
                    new(),
                    new(),
                    new ProposalBasis { Note = "拠点の閉鎖（閉鎖日の翌日で有効期間を終えます）。この拠点を使う組織・人の拠点は、組織・人の取込で付け替えてください。" },
                    CreateConfidence);
                continue;
            }

            if (existing is null)
            {
                if (row[IngestFields.SiteName] is not { } name)
                {
                    Correction(row, IngestFields.SiteName, CorrectionIssue.MissingRequired, "新しい拠点の拠点名がありません。");
                    continue;
                }

                var validFrom = Date(row[IngestFields.SiteValidFrom]) ?? _context.EffectiveDate ?? _context.Today;
                Add(
                    row,
                    ProposalTargetKind.Site,
                    ProposalOperation.Create,
                    EntityId.New(),
                    validFrom,
                    Payload(
                        (ProposalFields.LegalEntityId, Id(entity)),
                        (ProposalFields.Code, code),
                        (ProposalFields.Name, name),
                        (ProposalFields.SiteType, type ?? SiteCodes.ToCode(SiteType.Other)),
                        (ProposalFields.PrefectureCode, row[IngestFields.SitePrefecture]),
                        (ProposalFields.Location, row[IngestFields.SiteLocation]),
                        (ProposalFields.ValidFrom, Iso(validFrom))),
                    new(),
                    new ProposalBasis { Note = type is null ? "拠点コードが jin-ji にありません（新規）。種別が分からないので「その他」にします。" : "拠点コードが jin-ji にありません（新規）。" },
                    CreateConfidence);
                continue;
            }

            var diff = new Dictionary<string, FieldChange>(StringComparer.Ordinal);
            void Compare(string field, string? current, string? value)
            {
                if (value is not null && !string.Equals(current, value, StringComparison.Ordinal))
                {
                    diff[field] = new FieldChange(current, value);
                }
            }

            Compare(ProposalFields.Name, existing.Name, row[IngestFields.SiteName]);
            Compare(ProposalFields.SiteType, SiteCodes.ToCode(existing.Type), row[IngestFields.SiteType]);
            Compare(ProposalFields.PrefectureCode, existing.PrefectureCode, row[IngestFields.SitePrefecture]);
            Compare(ProposalFields.Location, existing.Location, row[IngestFields.SiteLocation]);
            if (diff.Count > 0)
            {
                Add(row, ProposalTargetKind.SiteVersion, ChangeOperation(), existing.Id, _context.EffectiveDate, new(), diff, new ProposalBasis(), CreateConfidence);
            }
        }
    }

    /// <summary>
    /// 行の拠点（拠点コードか拠点名）を拠点マスタに照合する。コードは法人の中で（無ければ全法人で一意なら）、名前は照合用の正規化表記
    /// （<see cref="SiteNameKey"/>。「本社（東京）」→「東京本社」も試す）で、法人の中で一意か、法人の中に無ければ全法人で一意のとき。
    /// 指定があるのに当たらなければ修正依頼（<see cref="CorrectionIssue.UnknownSite"/>）にして null。<paramref name="given"/> は指定があったか。
    /// </summary>
    private Site? ResolveSite(NormalizedRow row, Guid? entity, out bool given)
    {
        given = row[IngestFields.SiteCode] is not null || row[IngestFields.SiteName] is not null;
        if (!given)
        {
            return null;
        }

        if (row[IngestFields.SiteCode] is { } code)
        {
            var byCode = Snapshot.Sites.Where(s => string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)).ToList();
            var inEntity = entity is null ? byCode : byCode.Where(s => s.LegalEntityId == entity).ToList();
            if (inEntity.Count == 1 || (inEntity.Count == 0 && byCode.Count == 1))
            {
                return inEntity.Count == 1 ? inEntity[0] : byCode[0];
            }
        }

        if (row[IngestFields.SiteName] is { } name && ResolveSiteByName(name, entity) is { } byName)
        {
            return byName;
        }

        var field = row[IngestFields.SiteCode] is not null ? IngestFields.SiteCode : IngestFields.SiteName;
        Correction(row, field, CorrectionIssue.UnknownSite, $"拠点（項目 {field}）が jin-ji の拠点マスタに見つからないか、一意に決まりません。拠点マスタに登録するか、人事側の値を直してください。");
        return null;
    }

    /// <summary>拠点名で拠点を探す（照合用の正規化表記。法人の別名が前に付いていればその法人の中で）。</summary>
    private Site? ResolveSiteByName(string name, Guid? entity)
    {
        var (code, rest) = _context.Profile.SplitLegalEntityPrefix(name);
        if (code is not null && Snapshot.LegalEntities.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.OrdinalIgnoreCase)) is { } prefixed)
        {
            entity = prefixed.Id;
        }

        foreach (var key in SiteNameKey.Candidates(rest))
        {
            var all = Snapshot.Sites.Where(s => SiteNameKey.From(s.Name) == key).ToList();
            var inEntity = entity is null ? all : all.Where(s => s.LegalEntityId == entity).ToList();
            if (inEntity.Count == 1)
            {
                return inEntity[0];
            }

            if (inEntity.Count == 0 && all.Count == 1)
            {
                return all[0];
            }
        }

        return null;
    }

    /// <summary>
    /// 人の拠点の上書きのあるべき値: 行の拠点が主務の組織（<paramref name="unit"/>。無ければ今の主務）の拠点と同じなら上書きなし（null）、違えばその拠点。
    /// 上書きは「異なる場合だけ」持つ（ADR-0002）ので、組織の拠点と同じ値を人に重ねて持たない。
    /// </summary>
    private Guid? DesiredSiteOverride(Site site, Guid? personId, Guid? unit)
    {
        var home = unit
            ?? (personId is { } id
                ? Snapshot.AssignmentsOf(id).FirstOrDefault(a => a.Type == AssignmentType.Primary && a.Secondment == SecondmentType.None)?.OrgUnitId
                    ?? Snapshot.AssignmentsOf(id).FirstOrDefault(a => a.Type == AssignmentType.Primary)?.OrgUnitId
                : null);
        return home is { } h && Snapshot.SiteOfUnit(h)?.Site.Id == site.Id ? null : site.Id;
    }
}
