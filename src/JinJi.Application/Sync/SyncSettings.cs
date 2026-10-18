using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JinJi.Application.Sync;

/// <summary>役職（jobTitle）の扱い。</summary>
public enum JobTitleMode
{
    /// <summary>勤務先の所属（主務、出向中は出向先）の役職。</summary>
    Primary,

    /// <summary>同期しない。</summary>
    None,
}

/// <summary>会社名（companyName）の扱い。</summary>
public enum CompanyNameMode
{
    /// <summary>勤務先の組織の法人名。</summary>
    LegalEntityName,

    /// <summary>同期しない。</summary>
    None,
}

/// <summary>設定で除外するユーザー（管理者アカウントなど）。どれか 1 つで指定する。</summary>
public sealed record SyncExclusion
{
    /// <summary>Entra のオブジェクト ID。</summary>
    public string? ObjectId { get; init; }

    /// <summary>UPN（大文字小文字を区別しない）。</summary>
    public string? UserPrincipalName { get; init; }

    /// <summary>jin-ji の社員番号（その人に対応付いたアカウントすべて）。</summary>
    public string? EmployeeNumber { get; init; }

    /// <summary>除外の理由の説明（レポートに出す）。</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Entra ID への同期の設定（同期する項目と書式・対象の法人・除外するユーザー）。JSON のファイルで渡す。
/// </summary>
/// <remarks>
/// <para>
/// <b>設定ファイルにした理由</b>（承認の対象の作業領域データにしない理由）: 設定は計画を通してしか Entra に効かず、計画は承認（起案者 ≠ 承認者）を経ないと
/// スクリプト出力・直接反映に使えない。計画には使った設定の全文と SHA-256 を保存し、承認画面・差分レポートの先頭に出すので、
/// <b>計画の承認が設定の承認を兼ねる</b>。設定だけの起案・承認の流れを別に持つと、少人数の運用（§1）で承認が二重になる。
/// 取込の項目マッピングと同じ扱い（ファイル + SHA-256 の記録）で、設定の変更の承認（§5.5）を独立させるのは後の手順。
/// </para>
/// <para>
/// 除外の設定は IT 全般統制に関わる（除外すると退職者のアカウントが無効にならない）。除外したアカウントが退職者・一時停止中の人のものなら、
/// 計画の先頭（critical）に警告として出す。
/// </para>
/// </remarks>
public sealed partial record SyncSettings
{
    /// <summary>表示名の形式に使える差し込み。</summary>
    public static readonly IReadOnlyList<string> DisplayNamePlaceholders =
    [
        "family", "given", "family_kana", "given_kana", "family_latin", "given_latin", "display_name", "department", "company", "employee_number",
    ];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false) },
    };

    /// <summary>説明（JSON の <c>$comment</c>。動作には関係しない）。</summary>
    [JsonPropertyName("$comment")]
    public string? Comment { get; init; }

    /// <summary>設定の名前（レポートに出す）。</summary>
    public string Name { get; init; } = "entra-sync";

    /// <summary>外部 ID の対応（external_identity.source_system）の同期先の名前。</summary>
    public string SourceSystem { get; init; } = "entra_id";

    /// <summary>対象の法人のコード（雇用元の法人。空ならすべて）。</summary>
    public IReadOnlyList<string> LegalEntities { get; init; } = [];

    /// <summary>表示名の形式（例: <c>{family} {given}</c>、<c>{family} {given}（{department}）</c>）。</summary>
    public string DisplayNameFormat { get; init; } = "{family} {given}";

    /// <summary>
    /// 部署名（department）に使う段のキー（例: department）。勤務先の組織から上にたどり、その段の組織の名前を使う。
    /// null・見つからなければ勤務先の組織そのものの名前。
    /// </summary>
    public string? DepartmentLevel { get; init; }

    /// <summary>法人ごとの部署名の段（法人のコード → 段のキー。null は勤務先の組織そのもの）。無い法人は <see cref="DepartmentLevel"/>。</summary>
    public IReadOnlyDictionary<string, string?> DepartmentLevelByLegalEntity { get; init; } = new Dictionary<string, string?>();

    /// <summary>役職の扱い。</summary>
    public JobTitleMode JobTitle { get; init; } = JobTitleMode.Primary;

    /// <summary>jin-ji に役職が無いとき、Entra の役職を消すか（既定 false: 消さない）。</summary>
    public bool ClearJobTitleWhenEmpty { get; init; }

    /// <summary>会社名の扱い。</summary>
    public CompanyNameMode CompanyName { get; init; } = CompanyNameMode.LegalEntityName;

    /// <summary>出向中の人の部署・役職・会社名に出向先を使うか（既定 true。Entra の部署は出向先を指す運用が多い）。</summary>
    public bool SecondmentUsesHostOrg { get; init; } = true;

    /// <summary>同期する項目（<see cref="EntraFields.Syncable"/> の部分集合。既定はすべて）。</summary>
    public IReadOnlyList<string> Fields { get; init; } = EntraFields.Syncable;

    /// <summary>除外するユーザー。</summary>
    public IReadOnlyList<SyncExclusion> Exclude { get; init; } = [];

    /// <summary>無効なアカウントを有効に戻す項目をスクリプトに含めるか（既定 false: 推奨として出すだけ）。</summary>
    public bool ReenableAccounts { get; init; }

    /// <summary>作成の推奨をスクリプトに含めるか（既定 false。含めても無効の状態で作る）。</summary>
    public bool IncludeCreatesInScript { get; init; }

    /// <summary>計画の有効期限（日。過ぎたら古い計画として扱う）。</summary>
    public int PlanMaxAgeDays { get; init; } = 7;

    /// <summary>既定の設定。</summary>
    public static SyncSettings Default { get; } = new();

    /// <summary>JSON の設定を読み、検査する。未知のキーは拒否する（書き間違いの防止）。</summary>
    public static SyncSettings Parse(ReadOnlySpan<byte> json)
    {
        SyncSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<SyncSettings>(json, ReadOptions) ?? throw new FormatException("同期の設定が空です。");
        }
        catch (JsonException ex)
        {
            throw new FormatException($"同期の設定を読めません: {ex.Message}", ex);
        }

        settings.Validate();
        return settings;
    }

    /// <summary>正規化した JSON（保存用。キーの順序が決まっていて、SHA-256 の計算に使う）。</summary>
    public string ToCanonicalJson()
    {
        var copy = this with
        {
            Comment = null,
            LegalEntities = [.. LegalEntities.Order(StringComparer.Ordinal)],
            DepartmentLevelByLegalEntity = new SortedDictionary<string, string?>(DepartmentLevelByLegalEntity.ToDictionary(), StringComparer.Ordinal),
            Fields = [.. EntraFields.Syncable.Where(Fields.Contains)],
        };
        return JsonSerializer.Serialize(copy, WriteOptions);
    }

    /// <summary>正規化した JSON の SHA-256。</summary>
    public string Sha256() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson())));

    /// <summary>項目を同期するか。</summary>
    public bool Syncs(string field) => Fields.Contains(field);

    /// <summary>法人の部署名の段。</summary>
    public string? DepartmentLevelFor(string legalEntityCode) =>
        DepartmentLevelByLegalEntity.TryGetValue(legalEntityCode, out var level) ? level : DepartmentLevel;

    /// <summary>形を検査する。不正なら <see cref="FormatException"/>。</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(SourceSystem))
        {
            throw new FormatException("name・sourceSystem は空にできません。");
        }

        if (string.IsNullOrWhiteSpace(DisplayNameFormat) || DisplayNameFormat.Length > 200)
        {
            throw new FormatException("displayNameFormat は 1〜200 文字で指定してください。");
        }

        var unknown = Placeholder().Matches(DisplayNameFormat).Select(m => m.Groups[1].Value).Where(p => !DisplayNamePlaceholders.Contains(p)).ToList();
        if (unknown.Count > 0)
        {
            throw new FormatException($"displayNameFormat に使えない差し込みがあります: {string.Join(", ", unknown)}（使えるのは {string.Join(", ", DisplayNamePlaceholders)}）。");
        }

        if (DisplayNameFormat.Count(c => c is '{' or '}') != Placeholder().Matches(DisplayNameFormat).Count * 2)
        {
            throw new FormatException("displayNameFormat の波かっこが対応していません。");
        }

        var invalidFields = Fields.Where(f => !EntraFields.Syncable.Contains(f)).ToList();
        if (invalidFields.Count > 0 || Fields.Count == 0)
        {
            throw new FormatException($"fields に同期できない項目があります: {string.Join(", ", invalidFields)}（選べるのは {string.Join(", ", EntraFields.Syncable)}）。");
        }

        foreach (var level in DepartmentLevelByLegalEntity.Values.Append(DepartmentLevel).OfType<string>())
        {
            if (!LevelKey().IsMatch(level))
            {
                throw new FormatException($"段のキー {level} の形が不正です。");
            }
        }

        foreach (var exclusion in Exclude)
        {
            var given = new[] { exclusion.ObjectId, exclusion.UserPrincipalName, exclusion.EmployeeNumber }.Count(v => !string.IsNullOrWhiteSpace(v));
            if (given != 1)
            {
                throw new FormatException("exclude の各要素は objectId・userPrincipalName・employeeNumber のどれか 1 つで指定してください。");
            }

            if (exclusion.ObjectId is { } id && !Guid.TryParseExact(id, "D", out _))
            {
                throw new FormatException("exclude の objectId は UUID で指定してください。");
            }
        }

        if (PlanMaxAgeDays is < 1 or > 90)
        {
            throw new FormatException("planMaxAgeDays は 1〜90 で指定してください。");
        }
    }

    /// <summary>表示名の形式に値を差し込む（値の無い差し込みは空。前後と連続する空白は詰める）。</summary>
    public string FormatDisplayName(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var text = Placeholder().Replace(DisplayNameFormat, m => values.GetValueOrDefault(m.Groups[1].Value) ?? string.Empty);

        // 値が無くて空になった括弧（「（）」「()」）は取り除く
        text = EmptyBrackets().Replace(text, string.Empty);
        return Spaces().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\{([a-z_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex LevelKey();

    [GeneratedRegex(@"\s*(（\s*）|\(\s*\)|［\s*］|\[\s*\])", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"[ \t]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();
}
