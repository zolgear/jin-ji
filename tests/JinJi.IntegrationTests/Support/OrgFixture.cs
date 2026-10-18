using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// 認可のテスト用の組織データ。同じデータから「メモリ上のスナップショット」と「DB の行」の両方を作る。
/// </summary>
/// <remarks>
/// 日付は実行日の業務日付（<see cref="Today"/>）からの相対で決める（RLS は DB の現在時刻で内示を判定するため）。
/// <code>
/// 段の定義: 既定（法人 A・B）= 本部 division 1 / 部 department 2 / 課 section 3 / 係 unit 4（0004 の初期の定義と同じ）
///           法人 C = 会社 company 1 / 事業部 business_unit 2 / 部 department 3 / 課 section 4 / チーム team 5（5 段）
///           法人 D = 会社 company 1 / 部 department 2 / チーム team 3（3 段。同じキーでも順位が C と違い、section は無い）
/// 法人 A（既定の定義）
///   A-ROOT 本社（段なし、最上位 = 順位 0）                 長: president
///     D1 第一本部（division）                                長: div1_head
///       DP1 営業部（department）                             長: dept1_head
///         S1 営業一課（section）                             長: sec1_head        直属: s1_member（内示あり）, suspended（一時停止中）
///           U1 第一係（unit）                                長: unit1_head       u1_member（氏名変更あり）, concurrent（兼務）
///           U2 第二係（unit、長なし）                        u2_member（個人単位の例外あり）
///         S2 営業二課（section）                             長: sec2_head        s2_member, concurrent（主務）, leaving（15 日後に退職予定 = 内示）
///       DP2 企画部（department、課の無い部）                 長: dept2_head       dp2_member
///         U3 企画係（unit、課を飛ばして部の直下）           u3_member
///       R1 特命プロジェクト室（段なし、本部直下）            長: r1_head          r1_member（組織単位の規則あり）
///       S3 本部付課（section、部を飛ばした本部直下・長なし） s3_member
///     D2 管理本部（division）                                長: div2_head
///       DP3 人事部（department）                             長: hr_head          hr_admin
///   （所属なし）retired（先月退職）, long_retired（200 日前に退職 = 一般公開の期間を過ぎた）, future_hire（入社予定 = 内示）
/// 拠点（法人 A）: SITE-TKY 東京本社（A-ROOT の拠点。法人 A の組織はすべて引き継ぐ）・SITE-OSK 大阪支社（dept1_head の人ごとの拠点）・
///   SITE-NGY 名古屋支店（20 日後に開設 = 予定。人事管理者だけに見える）。リモートワークの対象: dept1_head・u2_member・s2_member
/// 法人 B（既定の定義）
///   B-DP 子会社事業部（department、本部の無い法人の最上位） 長: b_head           seconded（法人 A から出向）
///     B-G1 子会社グループ（段なし、長なし）                  b_member
/// 法人 C（5 段）
///   C-ROOT 会社C（company）                                  長: c_president
///     C-BU1 第一事業部（business_unit）                      長: c_bu1_head
///       C-DP1 営業部（department）                           長: c_dp1_head
///         C-S1 営業課（section）                             長: c_s1_head
///           C-T1 チームA（team、長なし）                     c_t1_member
///           C-T2 チームB（team、長なし）                     c_t2_member
///       C-S2 事業部直轄課（section、部を飛ばす・長なし）     c_s2_member
///       C-P1 プロジェクト室（段なし、長なし）                c_p1_member
///         C-T3 室内チーム（team、段なしの組織の下・長なし）  c_t3_member
/// 法人 D（3 段）
///   D-ROOT 会社D（company）                                  長: d_president
///     D-DP1 開発部（department）                             長: d_dp1_head
///       D-T1 開発チーム（team、長なし）                      d_t1_member
///     D-X 社長室（段なし、長なし）                           d_x_member
/// </code>
/// </remarks>
internal sealed class OrgFixture
{
    private static readonly Lazy<OrgFixture> SharedInstance = new(() => new OrgFixture(BusinessDate.Today(TimeProvider.System)));

    private readonly Dictionary<string, Guid> _units = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _people = new(StringComparer.Ordinal);

    private OrgFixture(DateOnly today)
    {
        Today = today;
        LongAgo = today.AddYears(-10);
        EntityA = EntityId.New();
        EntityB = EntityId.New();
        EntityC = EntityId.New();
        EntityD = EntityId.New();
        LegalEntities =
        [
            new LegalEntity(EntityA, "A", "テスト本社株式会社"), new LegalEntity(EntityB, "B", "テスト子会社株式会社"),
            new LegalEntity(EntityC, "C", "テスト五段株式会社"), new LegalEntity(EntityD, "D", "テスト三段株式会社"),
        ];
        LevelSchemes =
        [
            DefaultLevelScheme,
            new OrgLevelScheme(EntityC, [new("company", "会社", 1), new("business_unit", "事業部", 2), new("department", "部", 3), new("section", "課", 4), new("team", "チーム", 5)]),
            new OrgLevelScheme(EntityD, [new("company", "会社", 1), new("department", "部", 2), new("team", "チーム", 3)]),
        ];

        foreach (var key in new[]
                 {
                     "A-ROOT", "D1", "DP1", "S1", "U1", "U2", "S2", "DP2", "U3", "R1", "S3", "D2", "DP3", "B-DP", "B-G1",
                     "C-ROOT", "C-BU1", "C-DP1", "C-S1", "C-T1", "C-T2", "C-S2", "C-P1", "C-T3", "D-ROOT", "D-DP1", "D-T1", "D-X",
                 })
        {
            _units[key] = EntityId.New();
        }

        foreach (var key in new[]
                 {
                     "president", "div1_head", "dept1_head", "sec1_head", "unit1_head", "u1_member", "u2_member", "s1_member",
                     "sec2_head", "s2_member", "concurrent", "dept2_head", "dp2_member", "u3_member", "r1_head", "r1_member",
                     "s3_member", "div2_head", "hr_head", "hr_admin", "suspended", "retired", "seconded", "b_head", "b_member",
                     "future_hire", "leaving", "long_retired",
                     "c_president", "c_bu1_head", "c_dp1_head", "c_s1_head", "c_t1_member", "c_t2_member", "c_s2_member", "c_p1_member", "c_t3_member",
                     "d_president", "d_dp1_head", "d_t1_member", "d_x_member",
                 })
        {
            _people[key] = EntityId.New();
        }

        Units =
        [
            Unit("A-ROOT", EntityA, "本社", null, null, "president"),
            Unit("D1", EntityA, "第一本部", "division", "A-ROOT", "div1_head"),
            Unit("DP1", EntityA, "営業部", "department", "D1", "dept1_head"),
            Unit("S1", EntityA, "営業一課", "section", "DP1", "sec1_head"),
            Unit("U1", EntityA, "第一係", "unit", "S1", "unit1_head"),
            Unit("U2", EntityA, "第二係", "unit", "S1", null),
            Unit("S2", EntityA, "営業二課", "section", "DP1", "sec2_head"),
            Unit("DP2", EntityA, "企画部", "department", "D1", "dept2_head"),
            Unit("U3", EntityA, "企画係", "unit", "DP2", null),
            Unit("R1", EntityA, "特命プロジェクト室", null, "D1", "r1_head"),
            Unit("S3", EntityA, "本部付課", "section", "D1", null),
            Unit("D2", EntityA, "管理本部", "division", "A-ROOT", "div2_head"),
            Unit("DP3", EntityA, "人事部", "department", "D2", "hr_head"),
            Unit("B-DP", EntityB, "子会社事業部", "department", null, "b_head"),
            Unit("B-G1", EntityB, "子会社グループ", null, "B-DP", null),
            Unit("C-ROOT", EntityC, "会社C", "company", null, "c_president"),
            Unit("C-BU1", EntityC, "第一事業部", "business_unit", "C-ROOT", "c_bu1_head"),
            Unit("C-DP1", EntityC, "営業部", "department", "C-BU1", "c_dp1_head"),
            Unit("C-S1", EntityC, "営業課", "section", "C-DP1", "c_s1_head"),
            Unit("C-T1", EntityC, "チームA", "team", "C-S1", null),
            Unit("C-T2", EntityC, "チームB", "team", "C-S1", null),
            Unit("C-S2", EntityC, "事業部直轄課", "section", "C-BU1", null),
            Unit("C-P1", EntityC, "プロジェクト室", null, "C-BU1", null),
            Unit("C-T3", EntityC, "室内チーム", "team", "C-P1", null),
            Unit("D-ROOT", EntityD, "会社D", "company", null, "d_president"),
            Unit("D-DP1", EntityD, "開発部", "department", "D-ROOT", "d_dp1_head"),
            Unit("D-T1", EntityD, "開発チーム", "team", "D-DP1", null),
            Unit("D-X", EntityD, "社長室", null, "D-ROOT", null),
        ];

        SiteTokyo = new SiteRow(EntityId.New(), EntityA, "SITE-TKY", "東京本社", "head_office", "13", "千代田区丸の内9-9-1", LongAgo);
        SiteOsaka = new SiteRow(EntityId.New(), EntityA, "SITE-OSK", "大阪支社", "branch", "27", "大阪市北区梅田9-9-3", LongAgo);
        SiteNagoya = new SiteRow(EntityId.New(), EntityA, "SITE-NGY", "名古屋支店", "branch", "23", null, today.AddDays(20));
        Sites = [SiteTokyo, SiteOsaka, SiteNagoya];
        Units = [.. Units.Select(u => u.Id == _units["A-ROOT"] ? u with { SiteId = SiteTokyo.Id } : u)];

        var versions = new List<VersionRow>();
        var assignments = new List<AssignmentRow>();
        var actions = new List<ActionRow>();

        void Employee(string key, Guid entity, string unit, string? position = null, string secondment = "none")
        {
            versions.Add(Version(key, LongAgo, null, key, LongAgo, null));
            assignments.Add(new AssignmentRow(EntityId.New(), Person(key), Unit(unit), "primary", position, secondment, LongAgo, null));
            actions.Add(new ActionRow(EntityId.New(), Person(key), "hire", LongAgo, Unit(unit), "primary"));
            People.Add(new PersonRow(Person(key), entity, $"E{People.Count + 1:D4}"));
        }

        People = [];
        Employee("president", EntityA, "A-ROOT", "社長");
        Employee("div1_head", EntityA, "D1", "本部長");
        Employee("dept1_head", EntityA, "DP1", "部長");
        Employee("sec1_head", EntityA, "S1", "課長");
        Employee("unit1_head", EntityA, "U1", "係長");
        Employee("u2_member", EntityA, "U2");
        Employee("sec2_head", EntityA, "S2", "課長");
        Employee("s2_member", EntityA, "S2");
        Employee("concurrent", EntityA, "S2");
        Employee("dept2_head", EntityA, "DP2", "部長");
        Employee("dp2_member", EntityA, "DP2");
        Employee("u3_member", EntityA, "U3");
        Employee("r1_head", EntityA, "R1", "室長");
        Employee("r1_member", EntityA, "R1");
        Employee("s3_member", EntityA, "S3");
        Employee("div2_head", EntityA, "D2", "本部長");
        Employee("hr_head", EntityA, "DP3", "部長");
        Employee("hr_admin", EntityA, "DP3");
        Employee("suspended", EntityA, "S1");
        Employee("b_head", EntityB, "B-DP", "事業部長");
        Employee("b_member", EntityB, "B-G1");
        Employee("c_president", EntityC, "C-ROOT", "社長");
        Employee("c_bu1_head", EntityC, "C-BU1", "事業部長");
        Employee("c_dp1_head", EntityC, "C-DP1", "部長");
        Employee("c_s1_head", EntityC, "C-S1", "課長");
        Employee("c_t1_member", EntityC, "C-T1");
        Employee("c_t2_member", EntityC, "C-T2");
        Employee("c_s2_member", EntityC, "C-S2");
        Employee("c_p1_member", EntityC, "C-P1");
        Employee("c_t3_member", EntityC, "C-T3");
        Employee("d_president", EntityD, "D-ROOT", "社長");
        Employee("d_dp1_head", EntityD, "D-DP1", "部長");
        Employee("d_t1_member", EntityD, "D-T1");
        Employee("d_x_member", EntityD, "D-X");

        // 兼務: 主務は S2、兼務で U1
        assignments.Add(new AssignmentRow(EntityId.New(), Person("concurrent"), Unit("U1"), "concurrent", null, "none", Today.AddYears(-1), null));
        actions.Add(new ActionRow(EntityId.New(), Person("concurrent"), "concurrent_start", Today.AddYears(-1), Unit("U1"), "concurrent"));

        // 氏名の変更: 旧姓の版（過去）と現在の版
        People.Add(new PersonRow(Person("u1_member"), EntityA, "E9001"));
        OldNameVersionId = EntityId.New();
        versions.Add(Version("u1_member", LongAgo, Today.AddDays(-100), "旧姓", LongAgo, null) with { Id = OldNameVersionId });
        versions.Add(Version("u1_member", Today.AddDays(-100), null, "新姓", LongAgo, null));
        assignments.Add(new AssignmentRow(EntityId.New(), Person("u1_member"), Unit("U1"), "primary", null, "none", LongAgo, null));
        actions.Add(new ActionRow(EntityId.New(), Person("u1_member"), "hire", LongAgo, Unit("U1"), "primary"));

        // 内示: s1_member は 10 日後に S2 へ異動する（発令と、導出された将来の所属）
        People.Add(new PersonRow(Person("s1_member"), EntityA, "E9002"));
        versions.Add(Version("s1_member", LongAgo, null, "s1_member", LongAgo, null));
        assignments.Add(new AssignmentRow(EntityId.New(), Person("s1_member"), Unit("S1"), "primary", null, "none", LongAgo, Today.AddDays(10)));
        assignments.Add(new AssignmentRow(EntityId.New(), Person("s1_member"), Unit("S2"), "primary", null, "none", Today.AddDays(10), null));
        actions.Add(new ActionRow(EntityId.New(), Person("s1_member"), "hire", LongAgo, Unit("S1"), "primary"));
        FutureTransferActionId = EntityId.New();
        actions.Add(new ActionRow(FutureTransferActionId, Person("s1_member"), "transfer", Today.AddDays(10), Unit("S2"), "primary"));

        // 退職者: 先月退職。所属は退職日まで。属性の版は開いたまま（退職年月日を持つ）
        People.Add(new PersonRow(Person("retired"), EntityA, "E9003"));
        versions.Add(Version("retired", LongAgo, null, "retired", LongAgo, Today.AddDays(-31)));
        assignments.Add(new AssignmentRow(EntityId.New(), Person("retired"), Unit("S2"), "primary", null, "none", LongAgo, Today.AddDays(-30)));
        actions.Add(new ActionRow(EntityId.New(), Person("retired"), "hire", LongAgo, Unit("S2"), "primary"));
        actions.Add(new ActionRow(EntityId.New(), Person("retired"), "termination", Today.AddDays(-30), null, null));

        // 退職予定（内示）: 15 日後が最終在籍日。所属はその翌日まで、退職の発令は翌日付
        People.Add(new PersonRow(Person("leaving"), EntityA, "E9006"));
        versions.Add(Version("leaving", LongAgo, null, "leaving", LongAgo, Today.AddDays(15)));
        assignments.Add(new AssignmentRow(EntityId.New(), Person("leaving"), Unit("S2"), "primary", null, "none", LongAgo, Today.AddDays(16)));
        actions.Add(new ActionRow(EntityId.New(), Person("leaving"), "hire", LongAgo, Unit("S2"), "primary"));
        actions.Add(new ActionRow(EntityId.New(), Person("leaving"), "termination", Today.AddDays(16), null, null));

        // 退職から時間が経った人: 200 日前に退職（既定の公開期間 90 日を過ぎた）
        People.Add(new PersonRow(Person("long_retired"), EntityA, "E9007"));
        versions.Add(Version("long_retired", LongAgo, null, "long_retired", LongAgo, Today.AddDays(-200)));
        assignments.Add(new AssignmentRow(EntityId.New(), Person("long_retired"), Unit("S2"), "primary", null, "none", LongAgo, Today.AddDays(-199)));
        actions.Add(new ActionRow(EntityId.New(), Person("long_retired"), "hire", LongAgo, Unit("S2"), "primary"));
        actions.Add(new ActionRow(EntityId.New(), Person("long_retired"), "termination", Today.AddDays(-199), null, null));

        // 出向: 法人 A の社員が法人 B の組織へ出向（雇用元は A のまま）
        People.Add(new PersonRow(Person("seconded"), EntityA, "E9004"));
        versions.Add(Version("seconded", LongAgo, null, "seconded", LongAgo, null));
        assignments.Add(new AssignmentRow(EntityId.New(), Person("seconded"), Unit("B-DP"), "primary", null, "outbound", Today.AddYears(-1), null));
        actions.Add(new ActionRow(EntityId.New(), Person("seconded"), "secondment_start", Today.AddYears(-1), Unit("B-DP"), "primary"));

        // 入社予定（内示）: 20 日後に入社
        People.Add(new PersonRow(Person("future_hire"), EntityA, "E9005"));
        versions.Add(Version("future_hire", Today.AddDays(20), null, "future_hire", Today.AddDays(20), null));
        assignments.Add(new AssignmentRow(EntityId.New(), Person("future_hire"), Unit("U2"), "primary", null, "none", Today.AddDays(20), null));
        FutureHireActionId = EntityId.New();
        actions.Add(new ActionRow(FutureHireActionId, Person("future_hire"), "hire", Today.AddDays(20), Unit("U2"), "primary"));

        // 一時停止中（理由は持たない）
        Suspensions = [new SuspensionRow(EntityId.New(), Person("suspended"), Today.AddDays(-30), Today.AddDays(60))];
        actions.Add(new ActionRow(EntityId.New(), Person("suspended"), "suspension_start", Today.AddDays(-30), null, null));

        Versions = [.. versions.Select(v =>
            v.PersonId == Person("dept1_head") ? v with { SiteId = SiteOsaka.Id, RemoteWork = true }
            : v.PersonId == Person("u2_member") || v.PersonId == Person("s2_member") ? v with { RemoteWork = true }
            : v)];
        Assignments = assignments;
        Actions = actions;
    }

    /// <summary>実行日の業務日付で作った共有のフィクスチャ。</summary>
    public static OrgFixture Shared => SharedInstance.Value;

    public DateOnly Today { get; }

    public DateOnly LongAgo { get; }

    public Guid EntityA { get; }

    public Guid EntityB { get; }

    /// <summary>5 段の定義を持つ法人。</summary>
    public Guid EntityC { get; }

    /// <summary>3 段の定義を持つ法人。</summary>
    public Guid EntityD { get; }

    /// <summary>グループ共通の既定の段の定義（0004 のマイグレーションの初期の定義と同じ）。</summary>
    public static OrgLevelScheme DefaultLevelScheme { get; } =
        new(null, [new("division", "本部", 1), new("department", "部", 2), new("section", "課", 3), new("unit", "係", 4)]);

    /// <summary>拠点（東京本社・大阪支社・名古屋支店〔開設予定〕）。</summary>
    public IReadOnlyList<SiteRow> Sites { get; }

    public SiteRow SiteTokyo { get; }

    public SiteRow SiteOsaka { get; }

    /// <summary>20 日後に開設する拠点（予定。人事管理者だけに見える）。</summary>
    public SiteRow SiteNagoya { get; }

    /// <summary>有効な段の定義（既定・法人 C・法人 D）。</summary>
    public IReadOnlyList<OrgLevelScheme> LevelSchemes { get; }

    public IReadOnlyList<LegalEntity> LegalEntities { get; }

    public IReadOnlyList<UnitRow> Units { get; private set; }

    public List<PersonRow> People { get; }

    public IReadOnlyList<VersionRow> Versions { get; private set; }

    public IReadOnlyList<AssignmentRow> Assignments { get; }

    public IReadOnlyList<SuspensionRow> Suspensions { get; }

    public IReadOnlyList<ActionRow> Actions { get; }

    /// <summary>u1_member の旧姓の版。</summary>
    public Guid OldNameVersionId { get; }

    /// <summary>s1_member の 10 日後の異動の発令（内示）。</summary>
    public Guid FutureTransferActionId { get; }

    /// <summary>future_hire の入社の発令（内示）。</summary>
    public Guid FutureHireActionId { get; }

    public Guid Unit(string key) => _units[key];

    public Guid Person(string key) => _people[key];

    public string PersonKey(Guid id) => _people.Single(p => p.Value == id).Key;

    public string UnitKey(Guid id) => _units.Single(u => u.Value == id).Key;

    /// <summary>
    /// 全データ（人事管理者に見える範囲）からスナップショットを作る。DB の NpgsqlOrgSnapshotProvider と同じ選び方をする。
    /// </summary>
    public OrgSnapshot BuildSnapshot(DateOnly? asOf = null, DateOnly? attributesAsOf = null)
    {
        var date = asOf ?? Today;
        var attrDate = attributesAsOf ?? date;

        var units = Units
            .Where(u => Covers(u.From, u.Until, date))
            .Select(u => new OrgUnit(u.Id, u.LegalEntityId, u.Code, u.Name, null, u.LevelKey, u.ParentId, u.HeadPersonId) { SiteId = u.SiteId });

        var people = new List<PersonRecord>();
        foreach (var person in People)
        {
            var history = Versions.Where(v => v.PersonId == person.Id).OrderBy(v => v.From).Select(ToDomain).ToList();
            if (history.FirstOrDefault(v => v.IsValidOn(attrDate)) is not { } current || current.HireDate > date)
            {
                continue;
            }

            people.Add(new PersonRecord
            {
                Id = person.Id,
                LegalEntityId = person.LegalEntityId,
                EmployeeNumber = person.EmployeeNumber,
                Current = current,
                History = history,
                Suspension = Suspensions.Where(s => s.PersonId == person.Id && Covers(s.From, s.Until, date)).Select(s => new SuspensionPeriod(s.From, s.Until)).SingleOrDefault(),
                Actions =
                [
                    .. Actions.Where(a => a.PersonId == person.Id).OrderBy(a => a.EffectiveDate).Select(a => new PersonnelActionRecord(
                        a.Id,
                        a.PersonId,
                        ParseActionType(a.ActionType),
                        a.EffectiveDate,
                        a.OrgUnitId,
                        a.AssignmentType is null ? null : ParseAssignmentType(a.AssignmentType),
                        null,
                        null)),
                ],
            });
        }

        var assignments = Assignments
            .Where(a => Covers(a.From, a.Until, date))
            .Select(a => new Assignment(a.PersonId, a.OrgUnitId, ParseAssignmentType(a.Type), a.Position, ParseSecondment(a.Secondment), a.From, a.Until));

        var sites = Sites
            .Where(x => Covers(x.From, null, date))
            .Select(x => new Site(x.Id, x.LegalEntityId, x.Code, x.Name, SiteCodes.TryParse(x.Type, out var type) ? type : SiteType.Other, x.PrefectureCode, x.Location));
        return OrgSnapshot.Create(date, LegalEntities, units, people, assignments, LevelSchemes, sites);
    }

    /// <summary>
    /// DB に登録する。接続には hr_admin の閲覧者が設定済みであること（<see cref="SampleData.ActAsHrAdminAsync"/>）。
    /// </summary>
    public async Task InsertAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var entity in LegalEntities)
        {
            await SampleData.ExecuteAsync(connection, "INSERT INTO legal_entity (id, code, name) VALUES ($1, $2, $3)", cancellationToken, entity.Id, entity.Code, entity.Name);
        }

        foreach (var unit in Units)
        {
            await SampleData.ExecuteAsync(connection, "INSERT INTO org_unit (id, legal_entity_id) VALUES ($1, $2)", cancellationToken, unit.Id, unit.LegalEntityId);
        }

        foreach (var site in Sites)
        {
            await SampleData.ExecuteAsync(connection, "INSERT INTO site (id, legal_entity_id) VALUES ($1, $2)", cancellationToken, site.Id, site.LegalEntityId);
            await SampleData.ExecuteAsync(
                connection,
                """
                INSERT INTO site_version (id, site_id, legal_entity_id, code, name, site_type, prefecture_code, location, valid_period)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, daterange($9, NULL))
                """,
                cancellationToken,
                EntityId.New(),
                site.Id,
                site.LegalEntityId,
                site.Code,
                site.Name,
                site.Type,
                Db(site.PrefectureCode),
                Db(site.Location),
                site.From);
        }

        foreach (var person in People)
        {
            await SampleData.ExecuteAsync(
                connection,
                "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, $3)",
                cancellationToken,
                person.Id,
                person.LegalEntityId,
                person.EmployeeNumber);
        }

        foreach (var unit in Units)
        {
            var path = string.Join('.', Ancestors(unit).Reverse().Select(u => u.Id.ToString("N")));
            await SampleData.ExecuteAsync(
                connection,
                """
                INSERT INTO org_unit_version (id, org_unit_id, legal_entity_id, code, name, level_key, parent_org_unit_id, head_person_id, path, valid_period, site_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::ltree, daterange($10, $11), $12)
                """,
                cancellationToken,
                EntityId.New(),
                unit.Id,
                unit.LegalEntityId,
                unit.Code,
                unit.Name,
                Db(unit.LevelKey),
                Db(unit.ParentId),
                Db(unit.HeadPersonId),
                path,
                unit.From,
                Db(unit.Until),
                Db(unit.SiteId));
        }

        foreach (var v in Versions)
        {
            await SampleData.ExecuteAsync(
                connection,
                """
                INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name,
                                            work_email, extension_number, work_mobile_phone, employment_type, hire_date, termination_date, valid_period,
                                            site_id, remote_work)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'regular', $10, $11, daterange($12, $13), $14, $15)
                """,
                cancellationToken,
                v.Id,
                v.PersonId,
                v.FamilyName,
                Db(v.GivenName),
                v.DisplayName,
                v.DisplayName,
                Db(v.WorkEmail),
                Db(v.ExtensionNumber),
                Db(v.WorkMobilePhone),
                v.HireDate,
                Db(v.TerminationDate),
                v.From,
                Db(v.Until),
                Db(v.SiteId),
                v.RemoteWork);
        }

        foreach (var a in Actions)
        {
            await SampleData.ExecuteAsync(
                connection,
                "INSERT INTO personnel_action (id, person_id, action_type, effective_date, org_unit_id, assignment_type) VALUES ($1, $2, $3, $4, $5, $6)",
                cancellationToken,
                a.Id,
                a.PersonId,
                a.ActionType,
                a.EffectiveDate,
                Db(a.OrgUnitId),
                Db(a.AssignmentType));
        }

        foreach (var a in Assignments)
        {
            await SampleData.ExecuteAsync(
                connection,
                """
                INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, position_title, secondment_type, valid_period)
                VALUES ($1, $2, $3, $4, $5, $6, daterange($7, $8))
                """,
                cancellationToken,
                a.Id,
                a.PersonId,
                a.OrgUnitId,
                a.Type,
                Db(a.Position),
                a.Secondment,
                a.From,
                Db(a.Until));
        }

        foreach (var s in Suspensions)
        {
            await SampleData.ExecuteAsync(
                connection,
                "INSERT INTO account_suspension (id, person_id, valid_period) VALUES ($1, $2, daterange($3, $4))",
                cancellationToken,
                s.Id,
                s.PersonId,
                s.From,
                Db(s.Until));
        }
    }

    /// <summary>人事管理者の閲覧者。</summary>
    public ViewerContext HrAdmin(FieldLimit? limit = null) =>
        ViewerContext.ForUser(Person("hr_admin"), [Roles.HrAdmin], Channel.Ui, limit ?? FieldLimit.Unrestricted);

    /// <summary>一般の閲覧者（ロールなし）。</summary>
    public ViewerContext User(string key, FieldLimit? limit = null) =>
        ViewerContext.ForUser(Person(key), [], Channel.Ui, limit ?? FieldLimit.Unrestricted);

    private static object Db(object? value) => value ?? DBNull.Value;

    private static bool Covers(DateOnly from, DateOnly? until, DateOnly date) => from <= date && (until is null || date < until.Value);


    private static AssignmentType ParseAssignmentType(string code) => code == "primary" ? AssignmentType.Primary : AssignmentType.Concurrent;

    private static SecondmentType ParseSecondment(string code) => code switch
    {
        "outbound" => SecondmentType.Outbound,
        "inbound" => SecondmentType.Inbound,
        _ => SecondmentType.None,
    };

    private static PersonnelActionType ParseActionType(string code) => code switch
    {
        "hire" => PersonnelActionType.Hire,
        "termination" => PersonnelActionType.Termination,
        "transfer" => PersonnelActionType.Transfer,
        "concurrent_start" => PersonnelActionType.ConcurrentStart,
        "secondment_start" => PersonnelActionType.SecondmentStart,
        "suspension_start" => PersonnelActionType.SuspensionStart,
        _ => throw new ArgumentException(code, nameof(code)),
    };

    private static PersonVersion ToDomain(VersionRow v) => new()
    {
        VersionId = v.Id,
        ValidFrom = v.From,
        ValidUntil = v.Until,
        FamilyName = v.FamilyName,
        GivenName = v.GivenName,
        DisplayName = v.DisplayName,
        WorkEmail = v.WorkEmail,
        ExtensionNumber = v.ExtensionNumber,
        WorkMobilePhone = v.WorkMobilePhone,
        SiteId = v.SiteId,
        RemoteWork = v.RemoteWork,
        EmploymentType = "regular",
        HireDate = v.HireDate,
        TerminationDate = v.TerminationDate,
    };

    private UnitRow Unit(string key, Guid entity, string name, string? levelKey, string? parent, string? head) =>
        new(_units[key], entity, key, name, levelKey, parent is null ? null : _units[parent], head is null ? null : _people[head], LongAgo, null);

    private VersionRow Version(string key, DateOnly from, DateOnly? until, string familyName, DateOnly hireDate, DateOnly? terminationDate) =>
        new(
            EntityId.New(),
            Person(key),
            from,
            until,
            familyName,
            "太郎",
            $"{familyName} 太郎",
            $"{key}@example.test",
            $"1{People.Count:D3}",
            $"090-0000-{People.Count:D4}",
            hireDate,
            terminationDate);

    private IEnumerable<UnitRow> Ancestors(UnitRow unit)
    {
        for (UnitRow? current = unit; current is not null; current = current.ParentId is { } parent ? Units.Single(u => u.Id == parent) : null)
        {
            yield return current;
        }
    }
}

internal sealed record PersonRow(Guid Id, Guid LegalEntityId, string EmployeeNumber);

internal sealed record UnitRow(Guid Id, Guid LegalEntityId, string Code, string Name, string? LevelKey, Guid? ParentId, Guid? HeadPersonId, DateOnly From, DateOnly? Until)
{
    public Guid? SiteId { get; init; }
}

/// <param name="Type">拠点の種別のコード（site_version.site_type）。</param>
internal sealed record SiteRow(Guid Id, Guid LegalEntityId, string Code, string Name, string Type, string? PrefectureCode, string? Location, DateOnly From);

internal sealed record VersionRow(
    Guid Id,
    Guid PersonId,
    DateOnly From,
    DateOnly? Until,
    string FamilyName,
    string? GivenName,
    string DisplayName,
    string? WorkEmail,
    string? ExtensionNumber,
    string? WorkMobilePhone,
    DateOnly HireDate,
    DateOnly? TerminationDate)
{
    /// <summary>人ごとの拠点（主務の組織の拠点と違う人だけ）。</summary>
    public Guid? SiteId { get; init; }

    public bool RemoteWork { get; init; }
}

internal sealed record AssignmentRow(Guid Id, Guid PersonId, Guid OrgUnitId, string Type, string? Position, string Secondment, DateOnly From, DateOnly? Until);

internal sealed record SuspensionRow(Guid Id, Guid PersonId, DateOnly From, DateOnly? Until);

internal sealed record ActionRow(Guid Id, Guid PersonId, string ActionType, DateOnly EffectiveDate, Guid? OrgUnitId, string? AssignmentType);
