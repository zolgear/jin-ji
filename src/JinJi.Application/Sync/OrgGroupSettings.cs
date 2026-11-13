using System.Text.RegularExpressions;

namespace JinJi.Application.Sync;

/// <summary>組織から作る Entra ID のグループの種類。</summary>
public enum OrgGroupType
{
    /// <summary>セキュリティグループ（既定）。</summary>
    Security,

    /// <summary>Microsoft 365 グループ（メール・Teams で使える）。</summary>
    Microsoft365,
}

/// <summary>グループのメンバーの範囲。</summary>
public enum OrgGroupMemberScope
{
    /// <summary>その組織と配下の組織で働く人（既定）。</summary>
    Subtree,

    /// <summary>その組織に直接所属する人だけ。</summary>
    Direct,
}

/// <summary>
/// 組織に対応する Entra ID のグループの設定（同期の設定の <c>orgGroups</c>。ADR-0015）。
/// </summary>
/// <remarks>
/// グループと組織の対応付けは、名前ではなく説明に書く印（<see cref="Marker"/>）で行う。接頭辞は、既存のグループと人の目で見分けるためと、
/// 名前の衝突を避けるためのもの。印の無いグループには触らない。
/// </remarks>
public sealed partial record OrgGroupSettings
{
    /// <summary>表示名の形式に使える差し込み（組織の名前・組織コード・法人名・法人コード）。</summary>
    public static readonly IReadOnlyList<string> DisplayNamePlaceholders = ["name", "code", "company", "entity_code"];

    /// <summary>表示名の接頭辞（必須。既存のグループと衝突しないように付ける。例: <c>JJ-</c>）。</summary>
    public string Prefix { get; init; } = string.Empty;

    /// <summary>接頭辞の後ろの表示名の形式（既定は組織の名前）。</summary>
    public string DisplayNameFormat { get; init; } = "{name}";

    /// <summary><c>mailNickname</c> の接頭辞（ASCII）。後ろに「法人コード-組織コード」が付く。</summary>
    public string MailNicknamePrefix { get; init; } = "jinji-";

    /// <summary>グループの種類（作った後は変えられない）。</summary>
    public OrgGroupType GroupType { get; init; } = OrgGroupType.Security;

    /// <summary>Teams のチームも作るか（Microsoft 365 グループだけ。オーナーが要るので、組織長をオーナーにする）。</summary>
    public bool CreateTeam { get; init; }

    /// <summary>メンバーの範囲。</summary>
    public OrgGroupMemberScope Members { get; init; } = OrgGroupMemberScope.Subtree;

    /// <summary>兼務の人を、兼務先の組織のグループに入れるか（既定 true）。</summary>
    public bool IncludeConcurrent { get; init; } = true;

    /// <summary>出向（送り出し・受入）の人を、出向先の組織のグループに入れるか（既定 true）。</summary>
    public bool IncludeSecondment { get; init; } = true;

    /// <summary>対象の組織の段のキー（空ならすべての組織）。</summary>
    public IReadOnlyList<string> Levels { get; init; } = [];

    /// <summary>廃止された組織のグループの表示名に付ける印。</summary>
    public string RetiredPrefix { get; init; } = "【廃止】";

    /// <summary>jin-ji の人に対応付かないメンバー（手で加えたサービスアカウントなど）も外すか（既定 false: 外さず、参考として出す）。</summary>
    public bool RemoveUnknownMembers { get; init; }

    /// <summary>グループの説明に書く印（組織との対応付け）。</summary>
    public static string Marker(Guid orgUnitId) => $"[jinji-org:{orgUnitId:D}]";

    /// <summary>説明から印の組織の ID を読む（無ければ null）。</summary>
    public static Guid? ReadMarker(string? description) =>
        description is not null && MarkerPattern().Match(description) is { Success: true } m && Guid.TryParseExact(m.Groups[1].Value, "D", out var id) ? id : null;

    /// <summary>表示名（接頭辞 + 形式）。</summary>
    public string FormatDisplayName(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var text = Placeholder().Replace(DisplayNameFormat, m => values.GetValueOrDefault(m.Groups[1].Value) ?? string.Empty);
        return (Prefix + Spaces().Replace(text, " ").Trim()).Trim();
    }

    /// <summary><c>mailNickname</c>（ASCII の英小文字・数字・<c>.</c>・<c>_</c>・<c>-</c> だけ。64 文字まで）。</summary>
    public string FormatMailNickname(string legalEntityCode, string orgCode)
    {
        ArgumentNullException.ThrowIfNull(legalEntityCode);
        ArgumentNullException.ThrowIfNull(orgCode);
        var text = MailNicknamePrefix + Unsafe().Replace($"{legalEntityCode}-{orgCode}".ToLowerInvariant(), "-");
        return text.Length <= 64 ? text : text[..64];
    }

    /// <summary>検査する。不正なら <see cref="FormatException"/>。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Prefix) || Prefix.Length > 32 || Prefix.Any(char.IsControl))
        {
            throw new FormatException("orgGroups.prefix は 1〜32 文字で指定してください（既存のグループと衝突しないための接頭辞）。");
        }

        if (string.IsNullOrWhiteSpace(DisplayNameFormat) || DisplayNameFormat.Length > 200)
        {
            throw new FormatException("orgGroups.displayNameFormat は 1〜200 文字で指定してください。");
        }

        var matches = Placeholder().Matches(DisplayNameFormat);
        var unknown = matches.Select(m => m.Groups[1].Value).Where(p => !DisplayNamePlaceholders.Contains(p)).ToList();
        if (unknown.Count > 0 || matches.Count == 0 || DisplayNameFormat.Count(c => c is '{' or '}') != matches.Count * 2)
        {
            throw new FormatException($"orgGroups.displayNameFormat の差し込みが不正です（使えるのは {string.Join(", ", DisplayNamePlaceholders)}。1 つ以上必要）。");
        }

        if (!MailPrefix().IsMatch(MailNicknamePrefix))
        {
            throw new FormatException("orgGroups.mailNicknamePrefix は ASCII の英小文字・数字・. _ - で 1〜20 文字にしてください。");
        }

        if (CreateTeam && GroupType != OrgGroupType.Microsoft365)
        {
            throw new FormatException("orgGroups.createTeam は groupType が microsoft365 のときだけ指定できます。");
        }

        if (Levels.Any(l => !LevelKey().IsMatch(l)))
        {
            throw new FormatException("orgGroups.levels の段のキーの形が不正です。");
        }

        if (string.IsNullOrWhiteSpace(RetiredPrefix) || RetiredPrefix.Length > 16 || RetiredPrefix.Any(char.IsControl))
        {
            throw new FormatException("orgGroups.retiredPrefix は 1〜16 文字で指定してください。");
        }
    }

    [GeneratedRegex(@"\[jinji-org:([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerPattern();

    [GeneratedRegex(@"\{([a-z_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[^a-z0-9._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Unsafe();

    [GeneratedRegex(@"^[a-z0-9._-]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex MailPrefix();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex LevelKey();
}
