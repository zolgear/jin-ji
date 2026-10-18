namespace JinJi.IntegrationTests.Support;

/// <summary>
/// ADR-0002「保持しない項目」を表す名前の検出。テーブル名・列名に使われていないことをテストで確かめる。
/// </summary>
/// <remarks>
/// 名前を下線で区切った語（Tokens）と、名前に含まれる部分文字列（Substrings）の両方で判定する。
/// 「age」のように他の語の一部になりやすいもの（manager, usage 等）は語単位でだけ判定する。
/// 誤検知した場合は、名前を変えるのが原則。どうしても必要な語の並びだけ <see cref="Exceptions"/> に理由付きで列挙する。
/// </remarks>
internal static class ForbiddenNames
{
    /// <summary>語として現れたら禁止する（下線区切り）。</summary>
    private static readonly HashSet<string> Tokens = new(StringComparer.Ordinal)
    {
        // 生年月日・年齢・性別
        "age", "dob", "sex",
        // 住所・私用連絡先
        "home", "private", "personal", "zip", "postal", "domicile",
        // 家族・扶養
        "spouse", "dependent", "dependents", "household", "child", "children",
        // 給与・賞与・給与等級・税・口座
        "pay", "wage", "wages", "tax", "bank", "pension",
        // 評価
        "evaluation", "appraisal", "rating", "performance",
        // 健康・休職理由・障害
        "health", "medical", "illness", "disability", "reason", "diagnosis",
        // 国籍・宗教
        "religion",
        // 学歴・職歴
        "education", "school", "university", "resume",
        // 戸籍
        "koseki",
    };

    /// <summary>部分文字列として現れたら禁止する。</summary>
    private static readonly string[] Substrings =
    [
        "birth", "gender", "address", "salary", "payroll", "grade", "bonus", "allowance",
        "mynumber", "my_number", "individual_number", "kojin_bango",
        "nationalit", "citizenship", "passport",
        "family_member", "marital", "insurance",
        "legal_name", "registered_name", "maiden",
    ];

    /// <summary>
    /// 部分文字列の判定から外す語の並び（下線区切りの語の列として完全に一致したときだけ外す）と、その理由。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>外すのは部分文字列の判定だけ。語単位の判定（home, private 等）は外さないので、home_ip_address のような名前は検出される</item>
    /// <item>語の並びの境界は下線で判定する。myip_address のように語の一部になっている場合は外さない</item>
    /// <item>追加するときは、保持しない項目（ADR-0002）を表していないことを理由に書く</item>
    /// </list>
    /// </remarks>
    public static readonly IReadOnlyList<NameException> Exceptions =
    [
        new("ip_address", "address", "接続元の IP アドレス（監査ログ・異常検知・レート制限で使う）。住所ではない"),
        new("mac_address", "address", "機器の MAC アドレス（端末・アカウント配布の連携で使う見込み）。住所ではない"),
    ];

    /// <summary>名前に当てはまった禁止規則を返す（空なら問題なし）。</summary>
    /// <param name="name">テーブル名・列名。</param>
    /// <param name="applyExceptions">例外（<see cref="Exceptions"/>）を適用するか。例外自体の検査にだけ false を使う。</param>
    public static IReadOnlyList<string> Find(string name, bool applyExceptions = true)
    {
        ArgumentNullException.ThrowIfNull(name);
        var lower = name.ToLowerInvariant();
        var tokens = lower.Split('_', StringSplitOptions.RemoveEmptyEntries);

        var hits = new List<string>();
        hits.AddRange(tokens.Where(Tokens.Contains).Select(t => $"語 '{t}'"));

        // 例外の語の並びを取り除いた残りの語ごとに、部分文字列で判定する
        var remaining = applyExceptions ? RemoveExceptions(tokens) : [string.Join('_', tokens)];
        hits.AddRange(Substrings
            .Where(s => remaining.Any(part => part.Contains(s, StringComparison.Ordinal)))
            .Select(s => $"部分文字列 '{s}'"));
        return hits;
    }

    /// <summary>
    /// 例外の語の並びに一致した部分を取り除き、残りを連続する語の塊（下線でつないだ文字列）として返す。
    /// 塊は下線でつなぐので、下線をまたぐ部分文字列（my_number 等）も検出できる。
    /// </summary>
    private static List<string> RemoveExceptions(string[] tokens)
    {
        var parts = new List<string>();
        var current = new List<string>();
        for (var i = 0; i < tokens.Length;)
        {
            var match = Exceptions.Select(e => e.Tokens).FirstOrDefault(e => tokens.Skip(i).Take(e.Length).SequenceEqual(e));
            if (match is not null)
            {
                Flush();
                i += match.Length;
                continue;
            }

            current.Add(tokens[i]);
            i++;
        }

        Flush();
        return parts;

        void Flush()
        {
            if (current.Count > 0)
            {
                parts.Add(string.Join('_', current));
                current.Clear();
            }
        }
    }
}

/// <summary>禁止名の判定の例外。Name（語の並び）が含む禁止の部分文字列は Substring だけであることをテストで確かめる。</summary>
internal sealed record NameException(string Name, string Substring, string Reason)
{
    public string[] Tokens { get; } = Name.Split('_');
}
