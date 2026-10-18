using System.Text.RegularExpressions;

namespace JinJi.Domain.Text;

/// <summary>
/// 人ではないアカウント（共有メールボックス・サービスアカウント・会議室など）の判別。取込（Entra ID の名寄せ）と同期計画が同じ規則を使う。
/// </summary>
/// <remarks>
/// employeeId（社員番号）があれば人とみなす。無ければ、姓・名が欠けているか、表示名・メールが共有・会議室・サービスなどの形のものを人ではないとする。
/// </remarks>
public static partial class NonPersonAccount
{
    /// <summary>人ではないアカウントか。</summary>
    public static bool Looks(string? employeeNumber, string? familyName, string? givenName, string? displayName, string? mail)
    {
        if (!string.IsNullOrWhiteSpace(employeeNumber))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(givenName))
        {
            return true;
        }

        return Pattern().IsMatch($"{displayName} {mail}");
    }

    [GeneratedRegex(@"共有|会議室|窓口|受付|代表|問い合わせ|サービス|\bsvc|room-|noreply|no-reply|info@|admin@|system", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}
