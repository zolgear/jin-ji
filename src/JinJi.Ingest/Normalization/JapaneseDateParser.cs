using System.Globalization;
using System.Text.RegularExpressions;

namespace JinJi.Ingest.Normalization;

/// <summary>
/// 日付の解釈（西暦の各形式、和暦: 令和・平成・昭和・大正、R6.4.1 形式、Excel のシリアル値）。内部は西暦で持つ（§4.4）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>西暦: 2024-04-01、2024/4/1、2024.4.1、2024年4月1日、20240401、2024-04-01T00:00:00（全角数字も可。NFKC で半角にする）</item>
/// <item>和暦: 令和6年4月1日、令和元年5月1日、R6.4.1、R06/04/01、H31.4.30、S64.1.7、㋿6.4.1。英字の元号は大文字・小文字を問わない</item>
/// <item>元号の期間の外（平成32年、令和元年4月30日 など）は解釈できないとする（推測で直さず、修正依頼にする）</item>
/// <item>2 桁の西暦（24/4/1）は解釈しない（世紀が決まらないため）</item>
/// <item>Excel のシリアル値（45383 など 5 桁の数）は、<c>allowExcelSerial</c> のときだけ解釈する</item>
/// </list>
/// </remarks>
public static partial class JapaneseDateParser
{
    private static readonly (string[] Names, DateOnly Start, DateOnly End)[] Eras =
    [
        (["令和", "r"], new DateOnly(2019, 5, 1), DateOnly.MaxValue),
        (["平成", "h"], new DateOnly(1989, 1, 8), new DateOnly(2019, 4, 30)),
        (["昭和", "s"], new DateOnly(1926, 12, 25), new DateOnly(1989, 1, 7)),
        (["大正", "t"], new DateOnly(1912, 7, 30), new DateOnly(1926, 12, 24)),
    ];

    private static readonly DateOnly ExcelEpoch = new(1899, 12, 30);

    /// <summary>日付を解釈する。解釈できなければ false。</summary>
    public static bool TryParse(string? value, out DateOnly date, bool allowExcelSerial = true)
    {
        date = default;
        var cleaned = TextNormalizer.CleanWhitespace(value);
        if (cleaned is null)
        {
            return false;
        }

        var text = TextNormalizer.Nfkc(cleaned).ToLowerInvariant();

        if (Western().Match(text) is { Success: true } western)
        {
            return TryCreate(Int(western.Groups["y"]), Int(western.Groups["m"]), Int(western.Groups["d"]), out date);
        }

        if (Compact().Match(text) is { Success: true } compact)
        {
            return TryCreate(Int(compact.Groups["y"]), Int(compact.Groups["m"]), Int(compact.Groups["d"]), out date);
        }

        if (Wareki().Match(text) is { Success: true } wareki)
        {
            var name = wareki.Groups["era"].Value;
            var era = Eras.Single(e => e.Names.Contains(name, StringComparer.Ordinal));
            var year = wareki.Groups["y"].Value == "元" ? 1 : Int(wareki.Groups["y"]);
            if (year < 1 || !TryCreate(era.Start.Year + year - 1, Int(wareki.Groups["m"]), Int(wareki.Groups["d"]), out var parsed)
                || parsed < era.Start || parsed > era.End)
            {
                return false;
            }

            date = parsed;
            return true;
        }

        if (allowExcelSerial && Serial().Match(text) is { Success: true } serial)
        {
            date = ExcelEpoch.AddDays(Int(serial.Groups["n"]));
            return true;
        }

        return false;
    }

    /// <summary>日付を解釈し、yyyy-MM-dd で返す。解釈できなければ null。</summary>
    public static string? ToIsoOrNull(string? value, bool allowExcelSerial = true) =>
        TryParse(value, out var date, allowExcelSerial) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;

    private static int Int(Group group) => int.Parse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static bool TryCreate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    [GeneratedRegex(@"^(?<y>[0-9]{4})\s*[-/.年]\s*(?<m>[0-9]{1,2})\s*[-/.月]\s*(?<d>[0-9]{1,2})\s*日?([t\s][0-9]{1,2}:[0-9]{2}(:[0-9]{2}(\.[0-9]+)?)?)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Western();

    [GeneratedRegex(@"^(?<y>[0-9]{4})(?<m>[0-9]{2})(?<d>[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Compact();

    [GeneratedRegex(@"^(?<era>令和|平成|昭和|大正|r|h|s|t)\s*(?<y>元|[0-9]{1,2})\s*[年./-]\s*(?<m>[0-9]{1,2})\s*[月./-]\s*(?<d>[0-9]{1,2})\s*日?$", RegexOptions.CultureInvariant)]
    private static partial Regex Wareki();

    [GeneratedRegex(@"^(?<n>[0-9]{5})(\.0+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Serial();
}
