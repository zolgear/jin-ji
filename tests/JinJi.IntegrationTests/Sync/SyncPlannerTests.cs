using JinJi.Application.Sync;
using JinJi.Sync;

namespace JinJi.IntegrationTests.Sync;

/// <summary>同期計画の差分計算（<see cref="EntraSyncPlanner"/>。DB を使わない）。</summary>
public sealed class SyncPlannerTests
{
    private static readonly SyncSettings NoManager = SyncFixture.Settings("""{ "fields": ["displayName", "givenName", "surname", "department", "jobTitle", "companyName", "officeLocation", "employeeId", "accountEnabled"] }""");

    [Fact(DisplayName = "差分が無ければ項目を作らない（対応付いたアカウントの値が jin-ji と同じ）")]
    public void NoDifference_NoItems()
    {
        var f = new SyncFixture();
        foreach (var key in new[] { "boss", "chief", "member", "solo", "hostboss" })
        {
            f.Account(key);
        }

        var plan = f.Plan(NoManager);

        Assert.Empty(plan.Items);
        Assert.Empty(plan.Warnings);
    }

    [Theory(DisplayName = "属性の項目ごとに Entra の現在値と jin-ji のあるべき値の差分を作る")]
    [InlineData("displayName", "Ichiro Takahashi", "髙橋 一郎")]
    [InlineData("givenName", "高橋", "一郎")]
    [InlineData("surname", "高橋", "髙橋")]
    [InlineData("department", "システム管理室", "営業一課")]
    [InlineData("jobTitle", "主任", "課長")]
    [InlineData("companyName", "(株)テスト", "テスト株式会社")]
    [InlineData("employeeId", "2", "E0002")]
    public void AttributeDifference(string field, string current, string desired)
    {
        var f = new SyncFixture();
        var objectId = f.Account("chief", new() { [field] = current });

        var item = Assert.Single(f.Plan(NoManager).Items);

        Assert.Equal((SyncAction.UpdateAttributes, SyncPriority.Normal, true, objectId), (item.Action, item.Priority, item.InScript, item.ObjectId));
        Assert.Equal(new SyncChange(field, current, desired), Assert.Single(item.Changes));
        Assert.Equal("attributes_differ", item.Basis.Code);
        Assert.Contains(item.Basis.Jinji, e => e.Field == field);
        Assert.Contains(item.Basis.Jinji, e => e.Source == "external_identity" && e.Reference == objectId);
        Assert.Equal("E0002", item.EmployeeNumber);
    }

    [Fact(DisplayName = "Entra の employeeId が空なら社員番号を設定する。勤務地は jin-ji の値に合わせる")]
    public void EmptyEmployeeId_AndOfficeLocation()
    {
        var f = new SyncFixture();
        f.Account("member", new() { ["employeeId"] = null, ["officeLocation"] = "大阪支店" });

        var item = Assert.Single(f.Plan(NoManager).Items);

        Assert.Equal(
            [new SyncChange("officeLocation", "大阪支店", "東京本社"), new SyncChange("employeeId", null, "E0003")],
            item.Changes.OrderByDescending(c => c.Field).ToList());
    }

    [Fact(DisplayName = "jin-ji に値が無い項目は消さない。役職は clearJobTitleWhenEmpty で消せる")]
    public void EmptyJinjiValue_IsNotCleared()
    {
        var f = new SyncFixture();
        f.Account("member", new() { ["jobTitle"] = "主任" });
        Assert.Empty(f.Plan(NoManager).Items);

        var clear = NoManager with { ClearJobTitleWhenEmpty = true };
        Assert.Equal(new SyncChange("jobTitle", "主任", null), Assert.Single(Assert.Single(f.Plan(clear).Items).Changes));
    }

    [Fact(DisplayName = "表示名の形式（姓 名（部署））と、部署名に使う段（上にたどった段の組織）")]
    public void DisplayNameFormat_AndDepartmentLevel()
    {
        var f = new SyncFixture();
        var member = f.Account("member");
        var settings = NoManager with { DisplayNameFormat = "{family} {given}（{department}）", DepartmentLevel = "department" };

        // 他の人のアカウントも形式が変わるので、member の項目だけを見る
        var item = Assert.Single(f.Plan(settings).Items, i => i.ObjectId == member);

        Assert.Equal(
            [new SyncChange("department", "営業一課", "第一営業部"), new SyncChange("displayName", "鈴木 次郎", "鈴木 次郎（第一営業部）")],
            item.Changes.OrderBy(c => c.Field).ToList());
        Assert.Contains(item.Basis.Jinji, e => e.Field == "department" && e.Source == "org_unit" && e.Value == "段 department");
    }

    [Fact(DisplayName = "法人ごとの部署名の段。段の定義に無い段は勤務先の組織そのものにして警告する")]
    public void DepartmentLevelByLegalEntity()
    {
        var f = new SyncFixture();
        var member = f.Account("member", new() { ["department"] = "第一営業部" });
        var hostboss = f.Account("hostboss");
        var settings = NoManager with { DepartmentLevelByLegalEntity = new Dictionary<string, string?> { ["A"] = "department", ["B"] = "team" } };

        var plan = f.Plan(settings);

        Assert.DoesNotContain(plan.Items, i => i.ObjectId == member || i.ObjectId == hostboss);
        Assert.Contains(plan.Items, i => i.EmployeeNumber == "E0002" && i.Changes.Contains(new SyncChange("department", "営業一課", "第一営業部")));
        Assert.Contains(plan.Warnings, w => w.Contains("team", StringComparison.Ordinal) && w.Contains("法人 B", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "出向中の人の部署・役職・会社名は出向先（設定で主務にできる）")]
    public void Secondment_UsesHostOrg()
    {
        var f = new SyncFixture();
        f.Secondment("solo", "B1", "担当");
        f.Account("solo", new() { ["department"] = "営業二課", ["jobTitle"] = null, ["companyName"] = "テスト株式会社" });

        var item = Assert.Single(f.Plan(NoManager).Items);
        Assert.Equal(
            [new SyncChange("companyName", "テスト株式会社", "出向先株式会社"), new SyncChange("department", "営業二課", "出向先部"), new SyncChange("jobTitle", null, "担当")],
            item.Changes.OrderBy(c => c.Field).ToList());

        Assert.Empty(f.Plan(NoManager with { SecondmentUsesHostOrg = false }).Items);
    }

    [Fact(DisplayName = "退職者の有効なアカウントは無効化（critical・先頭）。属性・上長は更新しない")]
    public void Retired_IsDisabledFirst()
    {
        var f = new SyncFixture();
        f.Account("chief", new() { ["department"] = "旧組織" });
        f.Person("member", "E0003", "鈴木", "次郎", "S1", null, terminated: new DateOnly(2026, 9, 30));
        var objectId = f.Account("member", new() { ["accountEnabled"] = true, ["department"] = "旧組織" });

        var items = f.Plan().Items;

        var first = items[0];
        Assert.Equal((1, SyncAction.DisableAccount, SyncPriority.Critical, true, objectId), (first.Sequence, first.Action, first.Priority, first.InScript, first.ObjectId));
        Assert.Equal(new SyncChange("accountEnabled", "true", "false"), Assert.Single(first.Changes));
        Assert.Equal("retired_enabled", first.Basis.Code);
        Assert.Contains(first.Basis.Jinji, e => e.Source == "person_version" && e.Value == "退職年月日 2026-09-30");
        Assert.DoesNotContain(items.Skip(1), i => i.ObjectId == objectId);
    }

    [Fact(DisplayName = "退職日が業務日付以後（退職の予定）なら無効化しない")]
    public void FutureTermination_IsNotDisabled()
    {
        var f = new SyncFixture();
        f.Person("member", "E0003", "鈴木", "次郎", "S1", null, location: "東京本社", terminated: SyncFixture.Today);
        f.Account("member", new() { ["accountEnabled"] = true });

        Assert.Empty(f.Plan(NoManager).Items);
    }

    [Fact(DisplayName = "一時停止中の人の有効なアカウントはサインイン停止（critical）。属性は同期を続ける")]
    public void Suspended_BlocksSignIn()
    {
        var f = new SyncFixture();
        f.Person("member", "E0003", "鈴木", "次郎", "S1", null, location: "東京本社", suspendedFrom: new DateOnly(2026, 6, 1));
        f.Account("member", new() { ["accountEnabled"] = true, ["jobTitle"] = "主任" });
        f.Account("solo", new() { ["accountEnabled"] = false });

        var items = f.Plan(NoManager with { ClearJobTitleWhenEmpty = true }).Items;

        Assert.Equal(
            [(SyncAction.BlockSignIn, SyncPriority.Critical, true), (SyncAction.EnableAccount, SyncPriority.High, false), (SyncAction.UpdateAttributes, SyncPriority.Normal, true)],
            items.Select(i => (i.Action, i.Priority, i.InScript)).ToList());
        Assert.Equal("suspended_enabled", items[0].Basis.Code);
        Assert.Contains(items[0].Basis.Jinji, e => e.Source == "account_suspension" && e.Value == "一時停止中（開始 2026-06-01）");
        Assert.Equal("active_disabled", items[1].Basis.Code);
        Assert.True(f.Plan(NoManager with { ReenableAccounts = true }).Items.Single(i => i.Action == SyncAction.EnableAccount).InScript);
    }

    [Fact(DisplayName = "人ではないアカウントは除外、対応付けの無い有効なアカウントは要対応（high）。どちらもスクリプトに含めない")]
    public void Unlinked_Accounts()
    {
        var f = new SyncFixture();
        f.Unlinked("room", new() { ["displayName"] = "会議室A", ["mail"] = "room-a@example.co.jp", ["accountEnabled"] = true });
        f.Unlinked("shared", new() { ["displayName"] = "総務窓口", ["givenName"] = null, ["surname"] = null, ["accountEnabled"] = true });
        f.Unlinked("ghost", new() { ["displayName"] = "幽霊 太郎", ["givenName"] = "太郎", ["surname"] = "幽霊", ["accountEnabled"] = true });
        f.Unlinked("old", new() { ["displayName"] = "古い 花子", ["givenName"] = "花子", ["surname"] = "古い", ["accountEnabled"] = false });

        var items = f.Plan(NoManager).Items.Where(i => i.Action is SyncAction.ExcludedAccount or SyncAction.UnmatchedAccount).ToList();

        Assert.Equal(
            [("unmatched_enabled", SyncPriority.High), ("unmatched_disabled", SyncPriority.Info), ("non_person", SyncPriority.Info), ("non_person", SyncPriority.Info)],
            items.Select(i => (i.Basis.Code!, i.Priority)).ToList());
        Assert.All(items, i => Assert.False(i.InScript));
        Assert.All(items, i => Assert.Empty(i.Changes));
    }

    [Fact(DisplayName = "設定で除外したアカウントは触らない。退職者のものなら critical の警告にする（スクリプトには含めない）")]
    public void Excluded_ByUpnObjectIdOrEmployeeNumber()
    {
        var f = new SyncFixture();
        f.Person("member", "E0003", "鈴木", "次郎", "S1", null, terminated: new DateOnly(2026, 1, 31));
        var retired = f.Account("member", new() { ["accountEnabled"] = true });
        var admin = f.Account("boss", new() { ["department"] = "旧組織" });
        f.Account("chief", new() { ["department"] = "旧組織" });
        var settings = NoManager with
        {
            Exclude =
            [
                new SyncExclusion { EmployeeNumber = "E0003", Note = "テスト" },
                new SyncExclusion { UserPrincipalName = "E0001@EXAMPLE.CO.JP", Note = "管理者" },
                new SyncExclusion { ObjectId = f.Object("chief") },
            ],
        };

        var items = f.Plan(settings).Items;

        Assert.Equal(3, items.Count);
        Assert.All(items, i => Assert.Equal(SyncAction.ExcludedAccount, i.Action));
        Assert.All(items, i => Assert.False(i.InScript));
        var warning = items[0];
        Assert.Equal((SyncPriority.Critical, "excluded_retired_enabled", retired), (warning.Priority, warning.Basis.Code, warning.ObjectId));
        Assert.Contains(items, i => i.ObjectId == admin && i.Priority == SyncPriority.Info && i.Note!.Contains("管理者", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "アカウントの無い在籍者は作成の推奨（既定はスクリプトに含めない。社用メールが無ければ含められない）")]
    public void MissingAccount_IsRecommendation()
    {
        var f = new SyncFixture();
        f.Person("member", "E0003", "鈴木", "次郎", "S1", null, email: "jiro.suzuki@example.co.jp");
        f.Person("retired", "E0009", "退職", "者", "S1", null, terminated: new DateOnly(2025, 3, 31));
        foreach (var key in new[] { "boss", "chief", "hostboss" })
        {
            f.Account(key);
        }

        var creates = f.Plan(NoManager, accountRest: false).Items.Where(i => i.Action == SyncAction.CreateUser).ToList();
        Assert.Equal(["E0003", "E0004"], creates.Select(c => c.EmployeeNumber));
        Assert.All(creates, c => Assert.False(c.InScript));
        Assert.Contains(new SyncChange("userPrincipalName", null, "jiro.suzuki@example.co.jp"), creates[0].Changes);
        Assert.Contains(new SyncChange("accountEnabled", null, "false"), creates[0].Changes);

        var included = f.Plan(NoManager with { IncludeCreatesInScript = true }, accountRest: false).Items.Where(i => i.Action == SyncAction.CreateUser).ToList();
        Assert.Equal([true, false], included.Select(c => c.InScript));
    }

    [Fact(DisplayName = "上長: 勤務先の組織から上にたどった組織長のアカウント。入力に現在値が無ければ現在値不明として計画する")]
    public void Manager()
    {
        var f = new SyncFixture();
        var boss = f.Account("boss");
        var chief = f.Account("chief");
        f.Account("member", new() { ["manager"] = new Dictionary<string, string> { ["id"] = chief } });
        f.Account("solo", new() { ["manager"] = new Dictionary<string, string> { ["id"] = chief } });

        var items = f.Plan().Items.Where(i => i.Action == SyncAction.SetManager).ToList();

        // 部長（boss）は上位の組織にも本人以外の組織長がいないので上長なし。課長（chief）の上長は部長（現在値不明）。
        // member の上長は課長（一致）。solo（長のいない課）の上長は部長（現在値は課長なので変更）
        Assert.Equal(
            [(f.Object("chief"), new SyncChange("manager", null, boss, false)), (f.Object("solo"), new SyncChange("manager", chief, boss))],
            items.Select(i => (i.ObjectId!, Assert.Single(i.Changes))).ToList());
        Assert.NotNull(items[0].Note);
    }

    [Fact(DisplayName = "上長に Entra のアカウントが対応付いていなければ上長を計画せず警告する")]
    public void Manager_Unmapped()
    {
        var f = new SyncFixture();
        f.Account("member");

        var plan = f.Plan(accountRest: false);

        Assert.DoesNotContain(plan.Items, i => i.Action == SyncAction.SetManager);
        Assert.Contains(plan.Warnings, w => w.Contains("上長", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "設定の fields に無い項目は同期しない。対象外の法人の人のアカウントは参考（対象外）")]
    public void FieldsAndLegalEntities()
    {
        var f = new SyncFixture();
        f.Account("chief", new() { ["department"] = "旧組織", ["jobTitle"] = "主任" });
        f.Account("hostboss", new() { ["department"] = "旧組織" });
        var settings = SyncFixture.Settings("""{ "fields": ["jobTitle"], "legalEntities": ["A"] }""");

        var items = f.Plan(settings).Items;

        Assert.Equal(new SyncChange("jobTitle", "主任", "課長"), Assert.Single(items.Single(i => i.Action == SyncAction.UpdateAttributes).Changes));
        Assert.Equal("out_of_scope", items.Single(i => i.Action == SyncAction.ExcludedAccount).Basis.Code);
    }

    [Fact(DisplayName = "入力に無いプロパティは計画せず警告する（$select 漏れで値を消さない）")]
    public void MissingProperty_IsWarned()
    {
        var f = new SyncFixture();
        var bytes = System.Text.Encoding.UTF8.GetBytes($$"""{"value":[{"id":"{{f.Object("chief")}}","displayName":"違う 名前","accountEnabled":true}]}""");
        var users = GraphDirectoryUsers.Parse(bytes);

        var plan = new EntraSyncPlanner().Plan(new SyncPlanInput(f.Snapshot(), [new ExternalIdentityLink(f.Id("chief"), f.Object("chief"))], users, NoManager, SyncFixture.Today));

        Assert.Equal(new SyncChange("displayName", "違う 名前", "髙橋 一郎"), Assert.Single(plan.Items.Single(i => i.Action == SyncAction.UpdateAttributes).Changes));
        Assert.Contains(plan.Warnings, w => w.Contains("department", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "jin-ji 側の値の指紋: 同じデータなら同じ、所属・退職・外部 ID の対応が変われば変わる。Entra の値には依存しない")]
    public void Fingerprint()
    {
        var f = new SyncFixture();
        var draft = f.Plan();
        var planner = new EntraSyncPlanner();
        var before = planner.Fingerprint(f.Snapshot(), f.Links, SyncSettings.Default, SyncFixture.Today);

        Assert.Equal(before, draft.JinjiFingerprint);
        f.Unlinked("other", new() { ["displayName"] = "別 人", ["givenName"] = "人", ["surname"] = "別", ["accountEnabled"] = true });
        Assert.Equal(before, planner.Fingerprint(f.Snapshot(), f.Links, SyncSettings.Default, SyncFixture.Today));

        f.Person("member", "E0003", "鈴木", "次郎", "S2", null, location: "東京本社");
        var moved = planner.Fingerprint(f.Snapshot(), f.Links, SyncSettings.Default, SyncFixture.Today);
        Assert.NotEqual(before, moved);
        Assert.NotEqual(moved, planner.Fingerprint(f.Snapshot(), [.. f.Links, new ExternalIdentityLink(f.Id("chief"), f.Object("other"))], SyncSettings.Default, SyncFixture.Today));
        Assert.NotEqual(moved, planner.Fingerprint(f.Snapshot(), f.Links, NoManager, SyncFixture.Today));
    }

    [Fact(DisplayName = "Graph の応答: 保持しないプロパティは読まず、@odata.nextLink（一部だけの一覧）・不正な ID・重複は拒否する")]
    public void GraphUsers_Parse()
    {
        var id = Guid.NewGuid().ToString("D");
        var users = GraphDirectoryUsers.Parse(System.Text.Encoding.UTF8.GetBytes(
            $$"""{"value":[{"id":"{{id.ToUpperInvariant()}}","displayName":"山田 太郎","birthday":"1980-01-01","streetAddress":"東京都","accountEnabled":false,"manager":null,"businessPhones":["03"]}]}"""));

        var user = Assert.Single(users);
        Assert.Equal(id, user.ObjectId);
        Assert.Equal(["accountEnabled", "displayName", "manager"], user.Values.Keys.Order(StringComparer.Ordinal));
        Assert.False(user.AccountEnabled);
        Assert.Null(user["manager"]);
        Assert.True(user.Knows("manager"));

        Assert.Throws<FormatException>(() => GraphDirectoryUsers.Parse("""{"value":[],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=x"}"""u8));
        Assert.Throws<FormatException>(() => GraphDirectoryUsers.Parse("""{"value":[{"id":"../../me"}]}"""u8));
        Assert.Throws<FormatException>(() => GraphDirectoryUsers.Parse(System.Text.Encoding.UTF8.GetBytes($$"""{"value":[{"id":"{{id}}"},{"id":"{{id}}"}]}""")));
    }
}
