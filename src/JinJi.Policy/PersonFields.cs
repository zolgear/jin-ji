namespace JinJi.Policy;

/// <summary>
/// 人の投影（<see cref="PersonView"/>）の項目名。ポリシー（policy_rule.fields）とクライアントの項目上限はこの名前で指定する。
/// </summary>
public static class PersonFields
{
    /// <summary>ポリシーで「全項目」を表す。</summary>
    public const string Wildcard = "*";

    public const string EmployeeNumber = "employee_number";
    public const string LegalEntity = "legal_entity";
    public const string DisplayName = "display_name";
    public const string FamilyName = "family_name";
    public const string GivenName = "given_name";
    public const string FamilyNameKana = "family_name_kana";
    public const string GivenNameKana = "given_name_kana";
    public const string FamilyNameLatin = "family_name_latin";
    public const string GivenNameLatin = "given_name_latin";
    public const string WorkEmail = "work_email";
    public const string ExtensionNumber = "extension_number";
    public const string WorkMobilePhone = "work_mobile_phone";

    /// <summary>拠点（人の上書きか、主務の組織の拠点。0017 で自由記述の勤務地 work_location を置き換えた）。</summary>
    public const string Site = "site";

    /// <summary>リモートワークの対象か（有無だけ。ADR-0002）。</summary>
    public const string RemoteWork = "remote_work";

    public const string EmploymentType = "employment_type";
    public const string JobType = "job_type";
    public const string HireDate = "hire_date";
    public const string TerminationDate = "termination_date";

    /// <summary>在籍状態（在籍 / 退職）。</summary>
    public const string EmploymentStatus = "employment_status";

    /// <summary>アカウント状態（有効 / 一時停止）。理由は持たない。</summary>
    public const string AccountStatus = "account_status";

    /// <summary>一時停止の期間（開始日・終了予定日）。人事管理者以外には、業務日付より後の終了予定日を返さない。</summary>
    public const string AccountSuspension = "account_suspension";

    /// <summary>所属・役職（主務・兼務・出向）。</summary>
    public const string Assignments = "assignments";

    /// <summary>直属の上長（人の ID）。</summary>
    public const string Managers = "managers";

    /// <summary>発令。人事管理者以外には発令日が到来したものだけを返す（内示の秘匿）。</summary>
    public const string PersonnelActions = "personnel_actions";

    /// <summary>氏名の履歴。人事管理者だけ（ポリシーでは指定できない）。</summary>
    public const string NameHistory = "name_history";

    /// <summary>全項目。</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        EmployeeNumber, LegalEntity, DisplayName, FamilyName, GivenName, FamilyNameKana, GivenNameKana,
        FamilyNameLatin, GivenNameLatin, WorkEmail, ExtensionNumber, WorkMobilePhone, Site, RemoteWork,
        EmploymentType, JobType, HireDate, TerminationDate, EmploymentStatus, AccountStatus, AccountSuspension,
        Assignments, Managers, PersonnelActions, NameHistory,
    };

    /// <summary>人事管理者だけが見られる項目。ポリシーの対象外で、本人にも見せない。</summary>
    public static readonly IReadOnlySet<string> HrAdminOnly = new HashSet<string>(StringComparer.Ordinal) { NameHistory };

    /// <summary>ポリシーで公開範囲を指定できる項目。</summary>
    public static readonly IReadOnlySet<string> PolicyControllable = All.Except(HrAdminOnly).ToHashSet(StringComparer.Ordinal);
}
