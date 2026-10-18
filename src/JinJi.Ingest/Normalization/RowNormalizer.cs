using JinJi.Application.Ingest;
using JinJi.Domain.Organization;
using JinJi.Domain.Text;
using JinJi.Domain.Workplace;

namespace JinJi.Ingest.Normalization;

/// <summary>正規化した取込元の 1 行（normalized_row）。値は許可リストの取込項目だけ。</summary>
public sealed class NormalizedRow
{
    public NormalizedRow(Guid stagingRowId, int sourceRowNumber, IReadOnlyDictionary<string, string> data, IReadOnlyList<NormalizationIssue> issues)
    {
        StagingRowId = stagingRowId;
        SourceRowNumber = sourceRowNumber;
        Data = data;
        Issues = issues;
    }

    public Guid StagingRowId { get; }

    public int SourceRowNumber { get; }

    /// <summary>取込項目 → 正規化した値（表示用の表記・コード値・yyyy-MM-dd の日付・検索用の正規化表記）。</summary>
    public IReadOnlyDictionary<string, string> Data { get; }

    public IReadOnlyList<NormalizationIssue> Issues { get; }

    public string? this[string field] => Data.GetValueOrDefault(field);

    public bool HasIssue(string field, string code) => Issues.Any(i => i.Field == field && i.Code == code);
}

/// <summary>正規化の問題の種別。</summary>
public static class IssueCodes
{
    /// <summary>必須の値が無い。</summary>
    public const string Missing = "missing";

    /// <summary>日付として解釈できない。</summary>
    public const string InvalidDate = "invalid_date";

    /// <summary>値を解釈できない（雇用区分の表に無い・メールアドレスの形でない等）。</summary>
    public const string InvalidValue = "invalid_value";
}

/// <summary>
/// 取込元の 1 行を正規化する（表示用の表記・検索用の正規化表記・コード値・日付）。問題は値を持たずに項目と種別だけ記録する。
/// </summary>
public sealed class RowNormalizer
{
    private readonly ImportProfile _profile;
    private readonly EmployeeNumberNormalizer _numbers;

    public RowNormalizer(ImportProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        _profile = profile;
        _numbers = new EmployeeNumberNormalizer(profile.EmployeeNumber);
    }

    public EmployeeNumberNormalizer EmployeeNumbers => _numbers;

    public NormalizedRow Normalize(Guid stagingRowId, MappedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var source = new Dictionary<string, string>(row.Values, StringComparer.Ordinal);
        foreach (var (key, value) in _profile.Defaults)
        {
            if (!source.ContainsKey(key) || TextNormalizer.CleanWhitespace(source[key]) is null)
            {
                source[key] = value;
            }
        }

        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var issues = new List<NormalizationIssue>();
        string? Raw(string key) => TextNormalizer.CleanWhitespace(source.GetValueOrDefault(key));
        void Set(string key, string? value)
        {
            if (value is not null)
            {
                data[key] = value;
            }
        }

        // 識別
        Set(IngestFields.EmployeeNumber, _numbers.Normalize(Raw(IngestFields.EmployeeNumber)));
        Set(IngestFields.LegalEntityCode, _profile.ResolveLegalEntityCode(Raw(IngestFields.LegalEntityCode)));
        Set(IngestFields.SecondmentLegalEntityCode, _profile.ResolveLegalEntityCode(Raw(IngestFields.SecondmentLegalEntityCode)));
        if (_profile.Target is ImportTargetKind.People or ImportTargetKind.PersonnelActions && !data.ContainsKey(IngestFields.EmployeeNumber))
        {
            issues.Add(new NormalizationIssue(IngestFields.EmployeeNumber, IssueCodes.Missing));
        }

        // 氏名（表示用の正式表記と、検索用の正規化表記）
        var family = TextNormalizer.ToDisplay(Raw(IngestFields.FamilyName));
        var given = TextNormalizer.ToDisplay(Raw(IngestFields.GivenName));
        var full = TextNormalizer.ToDisplay(Raw(IngestFields.FullName));
        if (family is null && full is not null)
        {
            (family, given) = Split(full, given);
        }

        var familyKana = TextNormalizer.ToKana(Raw(IngestFields.FamilyNameKana));
        var givenKana = TextNormalizer.ToKana(Raw(IngestFields.GivenNameKana));
        var fullKana = TextNormalizer.ToKana(Raw(IngestFields.FullNameKana));
        if (familyKana is null && fullKana is not null)
        {
            (familyKana, givenKana) = Split(fullKana, givenKana);
        }

        Set(IngestFields.FullName, full);
        Set(IngestFields.FamilyName, family);
        Set(IngestFields.GivenName, given);
        Set(IngestFields.FullNameKana, fullKana);
        Set(IngestFields.FamilyNameKana, familyKana);
        Set(IngestFields.GivenNameKana, givenKana);
        Set(IngestFields.FamilyNameLatin, TextNormalizer.ToDisplay(Raw(IngestFields.FamilyNameLatin)));
        Set(IngestFields.GivenNameLatin, TextNormalizer.ToDisplay(Raw(IngestFields.GivenNameLatin)));
        Set(IngestFields.DisplayName, TextNormalizer.ToDisplay(Raw(IngestFields.DisplayName)));
        if (family is not null)
        {
            Set(IngestFields.SearchName, TextNormalizer.ToSearchKey(family + given));
        }

        if (familyKana is not null)
        {
            Set(IngestFields.SearchNameKana, TextNormalizer.ToSearchKey(familyKana + givenKana));
        }

        // 業務連絡先・その他
        if (Raw(IngestFields.WorkEmail) is { } email)
        {
            var normalized = TextNormalizer.Nfkc(email).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            if (normalized.Count(c => c == '@') == 1 && !normalized.StartsWith('@') && !normalized.EndsWith('@'))
            {
                Set(IngestFields.WorkEmail, normalized);
            }
            else
            {
                issues.Add(new NormalizationIssue(IngestFields.WorkEmail, IssueCodes.InvalidValue));
            }
        }

        foreach (var key in new[]
                 {
                     IngestFields.ExtensionNumber, IngestFields.WorkMobilePhone, IngestFields.JobType, IngestFields.PositionTitle,
                     IngestFields.OrgShortName, IngestFields.OrgLevel, IngestFields.EmploymentStatus, IngestFields.OrgHeadName, IngestFields.OrgRemarks,
                     IngestFields.ConcurrentOrgName, IngestFields.ConcurrentPositionTitle, IngestFields.SecondmentOrgName,
                     IngestFields.ActionText, IngestFields.ActionNote,
                 })
        {
            Set(key, TextNormalizer.ToDisplay(Raw(key)));
        }

        Set(IngestFields.ExternalId, Raw(IngestFields.ExternalId) is { } externalId ? TextNormalizer.Nfkc(externalId).ToLowerInvariant() : null);
        if (Raw(IngestFields.AccountEnabled) is { } enabled)
        {
            var flag = TextNormalizer.Nfkc(enabled).ToLowerInvariant() switch
            {
                "true" or "1" or "yes" or "有効" => "true",
                "false" or "0" or "no" or "無効" => "false",
                _ => null,
            };
            if (flag is null)
            {
                issues.Add(new NormalizationIssue(IngestFields.AccountEnabled, IssueCodes.InvalidValue));
            }
            else
            {
                Set(IngestFields.AccountEnabled, flag);
            }
        }

        MapCode(IngestFields.EmploymentType, ValueCodes.EmploymentType);
        MapCode(IngestFields.AssignmentType, ValueCodes.AssignmentType);

        foreach (var key in IngestFields.Dates)
        {
            if (Raw(key) is { } value)
            {
                if (JapaneseDateParser.ToIsoOrNull(value, _profile.AllowExcelSerialDates) is { } date)
                {
                    Set(key, date);
                }
                else
                {
                    issues.Add(new NormalizationIssue(key, IssueCodes.InvalidDate));
                }
            }
        }

        // 組織
        Set(IngestFields.OrgCode, Code(Raw(IngestFields.OrgCode)));
        Set(IngestFields.ParentOrgCode, Code(Raw(IngestFields.ParentOrgCode)));
        Set(IngestFields.OrgName, TextNormalizer.ToDisplay(Raw(IngestFields.OrgName)));
        Set(IngestFields.OrgSearchName, OrgNameNormalizer.ToSearchKey(Raw(IngestFields.OrgName)));
        Set(IngestFields.ParentOrgName, TextNormalizer.ToDisplay(Raw(IngestFields.ParentOrgName)));
        Set(IngestFields.ParentOrgSearchName, OrgNameNormalizer.ToSearchKey(Raw(IngestFields.ParentOrgName)));
        Set(IngestFields.ParentOrgAltName, TextNormalizer.ToDisplay(Raw(IngestFields.ParentOrgAltName)));
        Set(IngestFields.OrgHeadEmployeeNumber, _numbers.Normalize(Raw(IngestFields.OrgHeadEmployeeNumber)));

        // 拠点（人・組織の拠点は拠点コードか拠点名で照合する。拠点マスタは種別・都道府県・所在地も）
        Set(IngestFields.SiteCode, Code(Raw(IngestFields.SiteCode)));
        var siteName = TextNormalizer.ToDisplay(Raw(IngestFields.SiteName));
        Set(IngestFields.SiteName, siteName);
        Set(IngestFields.SiteSearchName, SiteNameKey.From(siteName));
        if (Raw(IngestFields.SiteType) is { } siteType)
        {
            if (SiteCodes.TryInfer(siteType, out var type))
            {
                Set(IngestFields.SiteType, SiteCodes.ToCode(type));
            }
            else
            {
                issues.Add(new NormalizationIssue(IngestFields.SiteType, IssueCodes.InvalidValue));
            }
        }
        else if (_profile.Target == ImportTargetKind.Sites && SiteCodes.TryInfer(siteName, out var inferred))
        {
            // 拠点マスタで種別が空欄なら、拠点名の末尾から推定する（「横浜工場」→ 工場、「千葉物流センター」→ 物流拠点）
            Set(IngestFields.SiteType, SiteCodes.ToCode(inferred));
        }

        var location = TextNormalizer.ToDisplay(Raw(IngestFields.SiteLocation));
        var prefecture = SiteCodes.ParsePrefecture(Raw(IngestFields.SitePrefecture));
        if (Raw(IngestFields.SitePrefecture) is not null && prefecture is null)
        {
            issues.Add(new NormalizationIssue(IngestFields.SitePrefecture, IssueCodes.InvalidValue));
        }

        if (location is not null)
        {
            // 所在地の先頭の都道府県名は、都道府県の項目に分ける（都道府県の列が無ければそこから求める）
            var leading = SiteCodes.ParsePrefecture(location) is { } code && location.StartsWith(SiteCodes.PrefectureName(code)!, StringComparison.Ordinal) ? code : null;
            prefecture ??= leading;
            if (leading is not null && leading == prefecture)
            {
                location = TextNormalizer.CleanWhitespace(location[SiteCodes.PrefectureName(leading)!.Length..]);
            }
        }

        Set(IngestFields.SitePrefecture, prefecture);
        Set(IngestFields.SiteLocation, location);
        if (Raw(IngestFields.RemoteWork) is { } remote)
        {
            if (ParseFlag(remote) is { } flag)
            {
                Set(IngestFields.RemoteWork, flag);
            }
            else
            {
                issues.Add(new NormalizationIssue(IngestFields.RemoteWork, IssueCodes.InvalidValue));
            }
        }

        NormalizeWorkplace(Raw, Set, issues);

        return new NormalizedRow(stagingRowId, row.SourceRowNumber, data, issues);

        void MapCode(string key, Func<string?, string?> map)
        {
            if (Raw(key) is { } value)
            {
                if (map(value) is { } code)
                {
                    Set(key, code);
                }
                else
                {
                    issues.Add(new NormalizationIssue(key, IssueCodes.InvalidValue));
                }
            }
        }
    }

    /// <summary>
    /// 建物・階・配置図・割り当ての項目（0019）。コード・席番号は全角・ハイフンの揺れをそろえ、種類は表示名からコード値にし、整数・多角形・設備のタグの形を確かめる。
    /// </summary>
    private static void NormalizeWorkplace(Func<string, string?> raw, Action<string, string?> set, List<NormalizationIssue> issues)
    {
        set(IngestFields.BuildingCode, WorkplaceCodes.NormalizeCode(raw(IngestFields.BuildingCode)));
        set(IngestFields.SeatCode, WorkplaceCodes.NormalizeCode(raw(IngestFields.SeatCode)));
        set(IngestFields.FloorLabel, WorkplaceCodes.NormalizeFloorLabel(raw(IngestFields.FloorLabel)));
        foreach (var key in new[] { IngestFields.BuildingName, IngestFields.LayoutName, IngestFields.SeatZone, IngestFields.ZoneName })
        {
            set(key, TextNormalizer.ToDisplay(raw(key)));
        }

        foreach (var (key, min, max) in new[]
                 {
                     (IngestFields.BuildingSortOrder, -999, 999), (IngestFields.FloorLevelOrder, -1000, 2000), (IngestFields.LayoutGridMm, 10, 1000),
                     (IngestFields.SeatXMm, 0, WorkplaceCodes.MaxCoordinateMm), (IngestFields.SeatYMm, 0, WorkplaceCodes.MaxCoordinateMm),
                     (IngestFields.SeatRotation, 0, 359), (IngestFields.SeatCapacity, 1, WorkplaceCodes.MaxCapacity),
                 })
        {
            if (raw(key) is not { } text)
            {
                continue;
            }

            if (int.TryParse(TextNormalizer.Nfkc(text).Replace(",", string.Empty, StringComparison.Ordinal), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var number)
                && number >= min && number <= max)
            {
                set(key, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                issues.Add(new NormalizationIssue(key, IssueCodes.InvalidValue));
            }
        }

        MapWorkplace(IngestFields.FloorKind, WorkplaceValueCodes.FloorKind);
        MapWorkplace(IngestFields.LayoutElement, WorkplaceValueCodes.LayoutElement);
        MapWorkplace(IngestFields.LayoutKind, WorkplaceValueCodes.LayoutKind);
        MapWorkplace(IngestFields.SeatKind, WorkplaceValueCodes.SeatKind);
        MapWorkplace(IngestFields.SeatDeskType, v => WorkplaceCodes.DeskTypes.Contains(v.ToLowerInvariant(), StringComparer.Ordinal) ? v.ToLowerInvariant() : null);
        MapWorkplace(IngestFields.WorkplaceRole, WorkplaceValueCodes.Role);
        MapWorkplace(IngestFields.SeatOutOfService, ParseFlag);
        MapWorkplace(IngestFields.SeatFeatures, WorkplaceValueCodes.Features);
        MapWorkplace(IngestFields.LayoutPolygon, WorkplaceValueCodes.Polygon);

        void MapWorkplace(string key, Func<string, string?> map)
        {
            if (raw(key) is not { } value)
            {
                return;
            }

            if (map(TextNormalizer.Nfkc(value).Trim()) is { } code)
            {
                set(key, code);
            }
            else
            {
                issues.Add(new NormalizationIssue(key, IssueCodes.InvalidValue));
            }
        }
    }

    /// <summary>
    /// 有無の表示値を "true" / "false" にする（はい/いいえ・○/×・有/無・あり/なし・可/不可・対象/対象外・Y/N・1/0 など）。解釈できなければ null。
    /// </summary>
    public static string? ParseFlag(string? value)
    {
        if (TextNormalizer.CleanWhitespace(value) is not { } cleaned)
        {
            return null;
        }

        return TextNormalizer.Nfkc(cleaned).Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "y" or "はい" or "有" or "有り" or "あり" or "可" or "対象" or "○" or "〇" or "◯" or "●" or "✓" or "✔" or "レ" => "true",
            "false" or "0" or "no" or "n" or "いいえ" or "無" or "無し" or "なし" or "不可" or "対象外" or "×" or "✕" or "✗" or "-" => "false",
            _ => null,
        };
    }

    /// <summary>コード（法人・組織のコード）の正規化: NFKC と空白の正規化だけ（大文字・小文字は比較の側で無視する）。</summary>
    public static string? Code(string? value) => TextNormalizer.CleanWhitespace(value) is { } cleaned ? TextNormalizer.Nfkc(cleaned) : null;

    /// <summary>氏名を空白で姓と名に分ける（名が別の列にあればそちらを使う）。空白が無ければ全体を姓にする。</summary>
    private static (string Family, string? Given) Split(string full, string? given)
    {
        var space = full.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (full, given) : (full[..space], given ?? full[(space + 1)..]);
    }
}
