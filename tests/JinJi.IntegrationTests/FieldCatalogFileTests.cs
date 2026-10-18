using JinJi.IntegrationTests.Support;

namespace JinJi.IntegrationTests;

/// <summary>項目カタログのファイル自体の検査と、禁止名の判定規則の検査（DB を使わない）。</summary>
public sealed class FieldCatalogFileTests
{
    [Fact(DisplayName = "全列の分類が定義済みの分類のいずれかである")]
    public void EveryColumn_HasDefinedCategory()
    {
        var catalog = FieldCatalog.Load();

        var invalid = catalog.Columns
            .Where(c => !catalog.Categories.Contains(c.Category))
            .Select(c => $"{c.Key}（分類: '{c.Category}'）")
            .ToList();

        Assert.True(invalid.Count == 0, "未定義の分類が使われています: " + string.Join(", ", invalid));
    }

    [Fact(DisplayName = "全列に説明がある")]
    public void EveryColumn_HasDescription()
    {
        var missing = FieldCatalog.Load().Columns.Where(c => string.IsNullOrWhiteSpace(c.Description)).Select(c => c.Key).ToList();

        Assert.True(missing.Count == 0, "説明のない列: " + string.Join(", ", missing));
    }

    [Fact(DisplayName = "カタログに保持しない項目を表す名前がない")]
    public void Catalog_HasNoForbiddenNames()
    {
        var catalog = FieldCatalog.Load();
        var names = catalog.Columns.Select(c => c.Table).Distinct().Concat(catalog.Columns.Select(c => c.Key));

        var hits = names
            .Select(n => (Name: n, Hits: ForbiddenNames.Find(n.Replace('.', '_'))))
            .Where(x => x.Hits.Count > 0)
            .Select(x => $"{x.Name}: {string.Join(", ", x.Hits)}")
            .ToList();

        Assert.True(hits.Count == 0, "保持しない項目を表す名前があります: " + string.Join("; ", hits));
    }

    [Theory(DisplayName = "保持しない項目を表す名前を検出する")]
    [InlineData("birth_date")]
    [InlineData("date_of_birth")]
    [InlineData("dob")]
    [InlineData("age")]
    [InlineData("gender")]
    [InlineData("sex")]
    [InlineData("home_address")]
    [InlineData("address")]
    [InlineData("private_email")]
    [InlineData("personal_phone")]
    [InlineData("salary")]
    [InlineData("salary_grade")]
    [InlineData("pay_grade")]
    [InlineData("bonus_amount")]
    [InlineData("my_number")]
    [InlineData("MyNumber")]
    [InlineData("nationality")]
    [InlineData("spouse_name")]
    [InlineData("dependents")]
    [InlineData("leave_reason")]
    [InlineData("suspension_reason")]
    [InlineData("health_status")]
    [InlineData("evaluation_score")]
    [InlineData("legal_name")]
    [InlineData("final_education")]
    public void DetectsForbiddenName(string name)
    {
        Assert.NotEmpty(ForbiddenNames.Find(name));
    }

    [Theory(DisplayName = "保持してよい項目の名前は検出しない")]
    [InlineData("manager_person_id")]
    [InlineData("head_person_id")]
    [InlineData("usage_count")]
    [InlineData("page_size")]
    [InlineData("family_name")]
    [InlineData("display_name")]
    [InlineData("work_email")]
    [InlineData("work_mobile_phone")]
    [InlineData("extension_number")]
    [InlineData("work_location")]
    [InlineData("hire_date")]
    [InlineData("termination_date")]
    [InlineData("employment_type")]
    [InlineData("position_title")]
    [InlineData("valid_period")]
    [InlineData("recorded_period")]
    [InlineData("ip_address")]
    [InlineData("client_ip_address")]
    [InlineData("source_ip_address_hash")]
    [InlineData("mac_address")]
    public void AllowsPermittedName(string name)
    {
        Assert.Empty(ForbiddenNames.Find(name));
    }

    [Theory(DisplayName = "例外の語の並びは語の境界で一致したときだけ外し、他の禁止規則は外さない")]
    [InlineData("home_ip_address")]
    [InlineData("myip_address")]
    [InlineData("ip_addresses_private")]
    [InlineData("ipaddress")]
    [InlineData("ip_address_birth_date")]
    [InlineData("address_ip")]
    public void ExceptionsDoNotHideOtherViolations(string name)
    {
        Assert.NotEmpty(ForbiddenNames.Find(name));
    }

    [Fact(DisplayName = "禁止名の例外はすべて理由を持ち、外す対象の部分文字列だけに当たる")]
    public void Exceptions_HaveReasonAndCoverOnlyTheirSubstring()
    {
        Assert.NotEmpty(ForbiddenNames.Exceptions);
        Assert.All(ForbiddenNames.Exceptions, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Reason), $"{e.Name}: 理由がありません");
            // 例外が無ければ、外す対象の部分文字列だけで検出される（不要な例外や、他の禁止語を隠す例外を作らない）
            Assert.Equal([$"部分文字列 '{e.Substring}'"], ForbiddenNames.Find(e.Name, applyExceptions: false));
            Assert.Empty(ForbiddenNames.Find(e.Name));
        });
    }
}
