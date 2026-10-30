namespace JinJi.Domain.Organization;

/// <summary>管理元を設定できる領域（ADR-0013 決定 2。DB の jinji_field_domains() と一致させる）。</summary>
public enum FieldDomain
{
    /// <summary>組織の新設・廃止・名称・略称・上位組織・段・組織長。</summary>
    OrgStructure,

    /// <summary>組織の拠点（所在地の移転）。</summary>
    OrgSite,

    /// <summary>人の所属・主務/兼務・役職（異動）。</summary>
    Assignment,

    /// <summary>人ごとの拠点・リモートワークの有無。</summary>
    PersonSite,

    /// <summary>内線・社用携帯。</summary>
    PersonContact,
}

/// <summary>領域の管理元（DB の field_domain_setting.mode）。</summary>
public enum FieldManagementMode
{
    /// <summary>人事パッケージ（取込だけ）。既定。</summary>
    Hr,

    /// <summary>jin-ji（直接登録だけ。取込の差分は提案にせず「人事パッケージとの差」に出す）。</summary>
    Field,

    /// <summary>両方（取込の差分のうち直接登録した値を変えるものは要確認にする）。</summary>
    Both,
}

/// <summary>領域・管理元のコード値（DB・API の値）。</summary>
public static class FieldDomains
{
    private static readonly Dictionary<FieldDomain, string> DomainKeys = new()
    {
        [FieldDomain.OrgStructure] = "org_structure",
        [FieldDomain.OrgSite] = "org_site",
        [FieldDomain.Assignment] = "assignment",
        [FieldDomain.PersonSite] = "person_site",
        [FieldDomain.PersonContact] = "person_contact",
    };

    private static readonly Dictionary<FieldManagementMode, string> ModeKeys = new()
    {
        [FieldManagementMode.Hr] = "hr",
        [FieldManagementMode.Field] = "field",
        [FieldManagementMode.Both] = "both",
    };

    /// <summary>すべての領域（定義の順）。</summary>
    public static IReadOnlyList<FieldDomain> All { get; } = Enum.GetValues<FieldDomain>();

    /// <summary>すべての領域のコード値（DB の jinji_field_domains() と同じ順）。</summary>
    public static IReadOnlyList<string> AllKeys { get; } = [.. All.Select(Key)];

    public static string Key(FieldDomain domain) => DomainKeys[domain];

    public static string Key(FieldManagementMode mode) => ModeKeys[mode];

    public static bool TryParseDomain(string? key, out FieldDomain domain)
    {
        foreach (var (value, code) in DomainKeys)
        {
            if (string.Equals(code, key, StringComparison.Ordinal))
            {
                domain = value;
                return true;
            }
        }

        domain = default;
        return false;
    }

    public static bool TryParseMode(string? key, out FieldManagementMode mode)
    {
        foreach (var (value, code) in ModeKeys)
        {
            if (string.Equals(code, key, StringComparison.Ordinal))
            {
                mode = value;
                return true;
            }
        }

        mode = default;
        return false;
    }

    /// <summary>直接登録を許すか（field・both）。</summary>
    public static bool AllowsDirectRegistration(this FieldManagementMode mode) => mode != FieldManagementMode.Hr;
}

/// <summary>
/// 担当の指名（DB の org_editor_grant。ADR-0013 決定 3）。組織に結び付き、期間 [<paramref name="From"/>, <paramref name="Until"/>) で効く。
/// </summary>
public sealed record OrgEditorGrant(
    Guid Id,
    Guid OrgUnitId,
    Guid PersonId,
    DateOnly From,
    DateOnly Until,
    Guid GrantedBy,
    DateTimeOffset GrantedAt,
    Guid? RevokedBy,
    DateTimeOffset? RevokedAt)
{
    /// <summary>その日に有効か（取り消されておらず、期間に入る）。</summary>
    public bool IsActiveOn(DateOnly date) => RevokedAt is null && From <= date && date < Until;
}

/// <summary>
/// 直接登録の範囲の判定（ADR-0013 決定 3）。DB の jinji_viewer_scope_covers() と同じ規則で、アプリの判定と RLS の両方で同じ線を引く。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>人事管理者・組織管理者は全社</item>
/// <item>それ以外は、対象の組織の祖先（自分を含む）のいずれかで、スナップショットの時点に組織長であるか、（担当を含めるなら）有効な担当の指名を持つこと</item>
/// <item>担当の指名・権限を生む変更の判定には、担当を含めない（再指名の禁止）・一つ上の組織で判定する（<see cref="CoversParentOf"/>）</item>
/// </list>
/// </remarks>
public sealed class RegistrationScope
{
    private readonly OrgSnapshot _snapshot;
    private readonly Guid? _personId;
    private readonly bool _managesAll;
    private readonly HashSet<Guid> _grantedUnits;

    /// <param name="snapshot">判定する日の組織（<see cref="OrgSnapshot.AsOf"/> が判定の日）。</param>
    /// <param name="personId">登録する人（AI エージェントは委任元）。人に紐付かない閲覧者は null。</param>
    /// <param name="managesAll">人事管理者か組織管理者か。</param>
    /// <param name="grants">その人の担当の指名（有効でないものは無視する）。</param>
    public RegistrationScope(OrgSnapshot snapshot, Guid? personId, bool managesAll, IEnumerable<OrgEditorGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(grants);
        _snapshot = snapshot;
        _personId = personId;
        _managesAll = managesAll;
        _grantedUnits = [.. grants.Where(g => g.PersonId == personId && g.IsActiveOn(snapshot.AsOf)).Select(g => g.OrgUnitId)];
    }

    /// <summary>全社の範囲を持つか。</summary>
    public bool ManagesAll => _managesAll;

    /// <summary>組織の範囲を持つか。<paramref name="withGrants"/> が false なら組織長としての範囲だけで判定する。</summary>
    public bool Covers(Guid unitId, bool withGrants = true)
    {
        if (_managesAll)
        {
            return true;
        }

        if (_personId is not { } person || _snapshot.FindUnit(unitId) is null)
        {
            return false;
        }

        foreach (var id in _snapshot.AncestorsOf(unitId))
        {
            if (_snapshot.FindUnit(id) is { } unit
                && (unit.HeadPersonId == person || (withGrants && _grantedUnits.Contains(id))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 権限を生む変更（組織長の任免・上位組織の付け替え）を登録できるか。その組織の上位の組織の範囲を要する。最上位の組織は全社の範囲だけ。
    /// </summary>
    public bool CoversParentOf(Guid unitId)
    {
        if (_managesAll)
        {
            return true;
        }

        return _snapshot.FindUnit(unitId)?.ParentId is { } parent && Covers(parent);
    }

    /// <summary>担当を指名・取り消しできるか（組織長としての範囲。担当は再指名できない）。</summary>
    public bool CanGrantEditors(Guid unitId) => Covers(unitId, withGrants: false);
}
