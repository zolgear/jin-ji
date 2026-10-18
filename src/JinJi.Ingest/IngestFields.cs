namespace JinJi.Ingest;

/// <summary>
/// 取込項目の許可リスト。staging（staging_row.data）と正規化結果（normalized_row.data）に入れてよいキーはこれだけ。
/// DB の <c>jinji_ingest_field_keys()</c>（CHECK 制約）と同じ集合（テストで突き合わせる）。
/// </summary>
/// <remarks>
/// ADR-0002 の「保持する項目」に対応する項目だけを並べる。生年月日・性別・住所・給与などのキーは作らない。
/// 足すときは 0005 以降のマイグレーションで DB 側の関数も同じ変更で置き換えること。
/// </remarks>
public static class IngestFields
{
    // 人
    public const string EmployeeNumber = "employee_number";
    public const string LegalEntityCode = "legal_entity_code";
    public const string FullName = "full_name";
    public const string FamilyName = "family_name";
    public const string GivenName = "given_name";
    public const string FullNameKana = "full_name_kana";
    public const string FamilyNameKana = "family_name_kana";
    public const string GivenNameKana = "given_name_kana";
    public const string FamilyNameLatin = "family_name_latin";
    public const string GivenNameLatin = "given_name_latin";
    public const string DisplayName = "display_name";
    public const string WorkEmail = "work_email";
    public const string ExtensionNumber = "extension_number";
    public const string WorkMobilePhone = "work_mobile_phone";

    /// <summary>
    /// 廃止（0017）。自由記述の勤務地。既存の staging の行のために DB の許可リストに残すが、項目マッピングには書けない（<see cref="Retired"/>）。
    /// 人事パッケージの「勤務地」の列は <see cref="SiteName"/> に対応させる（拠点マスタに照合する）。
    /// </summary>
    public const string WorkLocation = "work_location";
    public const string EmploymentType = "employment_type";
    public const string JobType = "job_type";
    public const string HireDate = "hire_date";
    public const string TerminationDate = "termination_date";

    /// <summary>在籍区分（在籍 / 休職中 / 退職 / 出向中 など。人事パッケージの表示値）。</summary>
    public const string EmploymentStatus = "employment_status";

    /// <summary>休職（アカウントの一時停止）の開始日。理由は持たない。</summary>
    public const string SuspensionStartDate = "suspension_start_date";

    // 所属（人の取込）・組織（組織の取込）
    public const string OrgCode = "org_code";
    public const string OrgName = "org_name";
    public const string PositionTitle = "position_title";
    public const string AssignmentType = "assignment_type";
    public const string OrgShortName = "org_short_name";
    public const string OrgLevel = "org_level";
    public const string ParentOrgCode = "parent_org_code";
    public const string ParentOrgName = "parent_org_name";
    public const string OrgHeadEmployeeNumber = "org_head_employee_number";
    public const string OrgValidFrom = "org_valid_from";

    /// <summary>組織の責任者の氏名（組織図に社員番号が無い場合）。</summary>
    public const string OrgHeadName = "org_head_name";

    /// <summary>組織図の備考（自由記述。「廃止」「統合」などの判別に使う）。</summary>
    public const string OrgRemarks = "org_remarks";

    /// <summary>
    /// 上位組織の代わりの候補（列が階層の組織図で、結合セルの補いで決めた上位組織を 1 段飛ばした組織 = 段飛ばしの場合の上位組織）。
    /// </summary>
    public const string ParentOrgAltName = "parent_org_alt_name";

    // 兼務・出向
    public const string ConcurrentOrgName = "concurrent_org_name";
    public const string ConcurrentPositionTitle = "concurrent_position_title";
    public const string SecondmentLegalEntityCode = "secondment_legal_entity_code";
    public const string SecondmentOrgName = "secondment_org_name";
    public const string SecondmentStartDate = "secondment_start_date";

    // 辞令
    public const string ActionDate = "action_date";

    /// <summary>発令内容（自由記述）。</summary>
    public const string ActionText = "action_text";

    /// <summary>辞令の備考（自由記述。「内示」「要確認」など）。</summary>
    public const string ActionNote = "action_note";

    // 外部のディレクトリ（Entra ID 等）
    public const string ExternalId = "external_id";
    public const string AccountEnabled = "account_enabled";

    // 拠点（人・組織の拠点は拠点コードか拠点名で拠点マスタに照合する。拠点マスタの取込は種別・都道府県・所在地・期間も持つ）
    public const string SiteCode = "site_code";
    public const string SiteName = "site_name";

    /// <summary>拠点の種別（拠点マスタ。コード値か「本社」「支店」などの表示名。無ければ拠点名の末尾から推定する）。</summary>
    public const string SiteType = "site_type";

    /// <summary>拠点の所在の都道府県（拠点マスタ。コード・名前。無ければ所在地の先頭から求める）。</summary>
    public const string SitePrefecture = "site_prefecture";

    /// <summary>拠点の所在地（拠点マスタ。事業所の住所 = 会社の情報。人の取込には書けない）。</summary>
    public const string SiteLocation = "site_location";

    public const string SiteValidFrom = "site_valid_from";

    /// <summary>拠点の閉鎖日（最終日。拠点マスタ）。</summary>
    public const string SiteClosedDate = "site_closed_date";

    /// <summary>リモートワークの対象か（はい/いいえ・○/×・有/無 などの表示値を "true" / "false" にそろえる）。</summary>
    public const string RemoteWork = "remote_work";

    // 建物・階・配置図の改訂（0019。配置図の JSON・席の CSV。行は要素ごと: 外形・固定の部分・ゾーン・席）
    public const string BuildingCode = "building_code";
    public const string BuildingName = "building_name";
    public const string BuildingSortOrder = "building_sort_order";

    /// <summary>階の表示名（1F・B1・M2F など。全角・小文字は正規化する）。</summary>
    public const string FloorLabel = "floor_label";

    /// <summary>階の並び順（地上 n 階 = n × 10、地下は負、中二階は間の値）。</summary>
    public const string FloorLevelOrder = "floor_level_order";

    /// <summary>階の種類（office・factory などのコード値か、事務所・製造フロアなどの表示名）。</summary>
    public const string FloorKind = "floor_kind";

    /// <summary>改訂の有効日。</summary>
    public const string LayoutValidFrom = "layout_valid_from";

    /// <summary>配置図の行の要素（outline 外形 / fixture 固定の部分 / zone ゾーン / seat 席 / floor 階だけ）。</summary>
    public const string LayoutElement = "layout_element";

    /// <summary>固定の部分・ゾーンの名前（表示用。保持しない情報の混入を検査する）。</summary>
    public const string LayoutName = "layout_name";

    /// <summary>固定の部分・ゾーンの種類。</summary>
    public const string LayoutKind = "layout_kind";

    /// <summary>外形・固定の部分・ゾーンの多角形（[[x_mm, y_mm], …] の JSON）。</summary>
    public const string LayoutPolygon = "layout_polygon";

    public const string LayoutGridMm = "layout_grid_mm";

    /// <summary>席番号（全角・ハイフンの揺れを正規化する。３F－Ａ０１ → 3F-A01）。</summary>
    public const string SeatCode = "seat_code";

    public const string SeatXMm = "seat_x_mm";
    public const string SeatYMm = "seat_y_mm";
    public const string SeatRotation = "seat_rotation";
    public const string SeatDeskType = "seat_desk_type";

    /// <summary>席の種類（fixed / station か、固定席・持ち場）。</summary>
    public const string SeatKind = "seat_kind";

    public const string SeatCapacity = "seat_capacity";

    /// <summary>設備のタグ（カンマ区切り。許可リストだけ。健康・障害に関わるタグは作らない）。</summary>
    public const string SeatFeatures = "seat_features";

    /// <summary>席が属するゾーンの名前（同じ階のゾーン）。</summary>
    public const string SeatZone = "seat_zone";

    public const string SeatOutOfService = "seat_out_of_service";

    // 人の席・持ち場と組織のエリア（0019。割り当ての CSV）

    /// <summary>ゾーン（エリア）の名前（ゾーンの割り当て・組織のエリア）。</summary>
    public const string ZoneName = "zone_name";

    /// <summary>席の役割（primary 主 / secondary 副。既定は主）。</summary>
    public const string WorkplaceRole = "workplace_role";

    public const string WorkplaceValidFrom = "workplace_valid_from";

    /// <summary>割り当ての最終日（この日の翌日で割り当てを終える）。</summary>
    public const string WorkplaceEndDate = "workplace_end_date";

    // 正規化で作る検索用の表記（取込元の列には対応させない）
    public const string SearchName = "search_name";
    public const string SearchNameKana = "search_name_kana";
    public const string OrgSearchName = "org_search_name";
    public const string ParentOrgSearchName = "parent_org_search_name";
    public const string SiteSearchName = "site_search_name";

    /// <summary>取込元の列に対応させてよい項目（項目マッピングの対象）。</summary>
    public static readonly IReadOnlySet<string> Mappable = new HashSet<string>(StringComparer.Ordinal)
    {
        EmployeeNumber, LegalEntityCode, FullName, FamilyName, GivenName, FullNameKana, FamilyNameKana, GivenNameKana,
        FamilyNameLatin, GivenNameLatin, DisplayName, WorkEmail, ExtensionNumber, WorkMobilePhone,
        EmploymentType, JobType, HireDate, TerminationDate,
        OrgCode, OrgName, PositionTitle, AssignmentType, OrgShortName, OrgLevel, ParentOrgCode, ParentOrgName, OrgHeadEmployeeNumber, OrgValidFrom,
        EmploymentStatus, SuspensionStartDate, OrgHeadName, OrgRemarks, ParentOrgAltName, ConcurrentOrgName, ConcurrentPositionTitle,
        SecondmentLegalEntityCode, SecondmentOrgName, SecondmentStartDate, ActionDate, ActionText, ActionNote, ExternalId, AccountEnabled,
        SiteCode, SiteName, SiteType, SitePrefecture, SiteLocation, SiteValidFrom, SiteClosedDate, RemoteWork,
        BuildingCode, BuildingName, BuildingSortOrder, FloorLabel, FloorLevelOrder, FloorKind, LayoutValidFrom, LayoutElement, LayoutName, LayoutKind,
        LayoutPolygon, LayoutGridMm, SeatCode, SeatXMm, SeatYMm, SeatRotation, SeatDeskType, SeatKind, SeatCapacity, SeatFeatures, SeatZone, SeatOutOfService,
        ZoneName, WorkplaceRole, WorkplaceValidFrom, WorkplaceEndDate,
    };

    /// <summary>配置図の取込（対象 floor_layouts）に書ける項目。</summary>
    public static readonly IReadOnlySet<string> FloorLayout = new HashSet<string>(StringComparer.Ordinal)
    {
        SiteCode, BuildingCode, BuildingName, BuildingSortOrder, FloorLabel, FloorLevelOrder, FloorKind, LayoutValidFrom, LayoutElement, LayoutName, LayoutKind,
        LayoutPolygon, LayoutGridMm, SeatCode, SeatXMm, SeatYMm, SeatRotation, SeatDeskType, SeatKind, SeatCapacity, SeatFeatures, SeatZone, SeatOutOfService,
    };

    /// <summary>割り当ての取込（対象 workplace_assignments）に書ける項目。</summary>
    public static readonly IReadOnlySet<string> WorkplaceAssignment = new HashSet<string>(StringComparer.Ordinal)
    {
        EmployeeNumber, LegalEntityCode, SiteCode, BuildingCode, FloorLabel, SeatCode, ZoneName, WorkplaceRole, WorkplaceValidFrom, WorkplaceEndDate,
    };

    /// <summary>建物・フロアモードの取込（配置図・割り当て）だけに書ける項目。人・組織の取込の列には対応させない。</summary>
    public static readonly IReadOnlySet<string> WorkplaceOnly = FloorLayout.Union(WorkplaceAssignment)
        .Except([EmployeeNumber, LegalEntityCode, SiteCode])
        .ToHashSet(StringComparer.Ordinal);

    /// <summary>拠点マスタの取込（対象 sites）だけに書ける項目。所在地（事業所の住所）を人の取込の列に対応させないため。</summary>
    public static readonly IReadOnlySet<string> SiteMasterOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        SiteType, SitePrefecture, SiteLocation, SiteValidFrom, SiteClosedDate,
    };

    /// <summary>拠点マスタの取込（対象 sites）に書ける項目。</summary>
    public static readonly IReadOnlySet<string> SiteMaster = new HashSet<string>(StringComparer.Ordinal)
    {
        LegalEntityCode, SiteCode, SiteName, SiteType, SitePrefecture, SiteLocation, SiteValidFrom, SiteClosedDate,
    };

    /// <summary>廃止した項目（既存の staging の行のために DB の許可リストに残す。項目マッピングには書けない）。</summary>
    public static readonly IReadOnlySet<string> Retired = new HashSet<string>(StringComparer.Ordinal) { WorkLocation };

    /// <summary>正規化で作る項目。</summary>
    public static readonly IReadOnlySet<string> Derived = new HashSet<string>(StringComparer.Ordinal)
    {
        SearchName, SearchNameKana, OrgSearchName, ParentOrgSearchName, SiteSearchName,
    };

    /// <summary>全項目（DB の許可リストと同じ）。</summary>
    public static readonly IReadOnlySet<string> All = Mappable.Concat(Derived).Concat(Retired).ToHashSet(StringComparer.Ordinal);

    /// <summary>日付の項目。</summary>
    public static readonly IReadOnlySet<string> Dates = new HashSet<string>(StringComparer.Ordinal)
    {
        HireDate, TerminationDate, OrgValidFrom, SuspensionStartDate, SecondmentStartDate, ActionDate, SiteValidFrom, SiteClosedDate,
        LayoutValidFrom, WorkplaceValidFrom, WorkplaceEndDate,
    };

    /// <summary>
    /// 自由記述の項目。生年月日・住所・電話番号らしき値が混ざっていないかを取込時に検査する（<see cref="Domain.Text.SensitiveValueDetector"/>）。
    /// 拠点名も含める（人事パッケージの勤務地の欄に自宅の住所が書かれていることがあるため）。拠点の所在地（<see cref="SiteLocation"/>）は事業所の住所なので検査しない
    /// （拠点マスタの取込だけに書ける。<see cref="SiteMasterOnly"/>）。建物名・ゾーンの名前も含める（ゾーンの名前に個人名や健康の情報を書かれることがあるため）。
    /// </summary>
    public static readonly IReadOnlySet<string> FreeText = new HashSet<string>(StringComparer.Ordinal)
    {
        ActionText, ActionNote, OrgRemarks, SiteName, JobType, PositionTitle, ConcurrentPositionTitle, DisplayName,
        BuildingName, LayoutName, SeatZone, ZoneName,
    };
}
