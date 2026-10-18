using System.Text.RegularExpressions;

namespace JinJi.Domain.Organization;

/// <summary>組織の階層の 1 段（例: 会社・事業部・部・課・チーム）。</summary>
/// <param name="Key">段のキー（英小文字・数字・_。ポリシーの same_level から参照する安定 ID）。</param>
/// <param name="Name">表示名（日本語）。</param>
/// <param name="Rank">順位（上から 1, 2, ...）。小さいほど上位。</param>
public sealed record OrgLevel(string Key, string Name, int Rank);

/// <summary>段の定義が不正。</summary>
public sealed class OrgLevelValidationException : Exception
{
    public OrgLevelValidationException()
    {
    }

    public OrgLevelValidationException(string message)
        : base(message)
    {
    }

    public OrgLevelValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 組織の階層の定義（段の集合）。法人ごとに持ち、法人に定義が無ければグループ共通の既定（<see cref="LegalEntityId"/> が null）を使う。
/// </summary>
/// <remarks>
/// 階層は固定しない（「会社 &gt; 事業部 &gt; 部 &gt; 課 &gt; チーム」も「会社 &gt; 部 &gt; チーム」も表せる）。
/// 定義の変更は公開範囲を変えるので、起案 → 別人の承認で反映する（JinJi.Application の OrgLevelAdministrationService）。
/// </remarks>
public sealed partial class OrgLevelScheme
{
    private readonly Dictionary<string, OrgLevel> _byKey;

    public OrgLevelScheme(Guid? legalEntityId, IEnumerable<OrgLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        LegalEntityId = legalEntityId;
        Levels = [.. levels.OrderBy(l => l.Rank)];
        _byKey = new Dictionary<string, OrgLevel>(StringComparer.Ordinal);
        foreach (var level in Levels)
        {
            _byKey.TryAdd(level.Key, level);
        }
    }

    /// <summary>定義の対象の法人。null はグループ共通の既定。</summary>
    public Guid? LegalEntityId { get; }

    /// <summary>段（順位の昇順）。</summary>
    public IReadOnlyList<OrgLevel> Levels { get; }

    /// <summary>キーの段。定義に無ければ null。</summary>
    public OrgLevel? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>段のキーとして使える文字列か（英小文字で始まり、英小文字・数字・_ で 63 文字まで）。</summary>
    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    /// <summary>定義の形を検査する: 段が 1 つ以上、キーの形式と重複、表示名、順位が 1 から連続。</summary>
    public void Validate()
    {
        if (Levels.Count == 0)
        {
            throw new OrgLevelValidationException("段が 1 つもありません。");
        }

        foreach (var level in Levels)
        {
            if (!IsValidKey(level.Key))
            {
                throw new OrgLevelValidationException($"段のキー {level.Key} は英小文字で始まり、英小文字・数字・_ で 63 文字までにしてください。");
            }

            if (string.IsNullOrWhiteSpace(level.Name))
            {
                throw new OrgLevelValidationException($"段 {level.Key} の表示名が空です。");
            }
        }

        if (_byKey.Count != Levels.Count)
        {
            throw new OrgLevelValidationException("段のキーが重複しています。");
        }

        if (!Levels.Select(l => l.Rank).SequenceEqual(Enumerable.Range(1, Levels.Count)))
        {
            throw new OrgLevelValidationException("段の順位は 1 から始まる連番（重複なし）にしてください。");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
