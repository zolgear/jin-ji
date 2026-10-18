using JinJi.Domain;
using JinJi.Infrastructure.Database;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// <see cref="FixtureDatabase"/> に、組織の集計・検索・メタ情報のテスト用のデータを足した一時データベース。
/// </summary>
/// <remarks>
/// <code>
/// 人 dual（法人 A）: 主務 S1（出向なし）、兼務 DP2（出向なし）、兼務 B-G1（出向）。発令: 入社（過去）、兼務・出向の開始（3 日後 = 内示）
/// 組織 FUT「新設準備室」（DP1 の下）: 5 日後に新設（組織改編の予定 = 人事管理者だけが見える）
/// 組織 OLD「旧管理室」（D2 の下）: 7 日後に廃止（今は誰にでも見えるが、廃止日は将来の予定）
/// 組織 PAST「髙崎営業所」（D2 の下）: 50 日前に新設（過去の組織改編。異体字の検索にも使う）
/// ポリシー: R1（特命プロジェクト室）の所属者の所属の項目は課内（R1 の中）だけ
/// </code>
/// </remarks>
public sealed class DirectoryFixtureDatabase : FixtureDatabase
{
    internal Guid Dual { get; } = EntityId.New();

    internal Guid FutureUnit { get; } = EntityId.New();

    internal Guid ClosingUnit { get; } = EntityId.New();

    internal Guid PastUnit { get; } = EntityId.New();

    protected override async Task SeedExtraAsync(CancellationToken cancellationToken)
    {
        var f = Fixture;
        await using (var app = await Database.OpenAppAsync(cancellationToken))
        {
            await SampleData.ActAsHrAdminAsync(app, cancellationToken);

            await InsertUnitAsync(app, FutureUnit, "FUT", "新設準備室", "DP1", f.Today.AddDays(5), null, cancellationToken);
            await InsertUnitAsync(app, ClosingUnit, "OLD", "旧管理室", "D2", f.LongAgo, f.Today.AddDays(7), cancellationToken);
            await InsertUnitAsync(app, PastUnit, "PAST", "髙崎営業所", "D2", f.Today.AddDays(-50), null, cancellationToken);

            await SampleData.ExecuteAsync(app, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E9100')", cancellationToken, Dual, f.EntityA);
            await SampleData.ExecuteAsync(
                app,
                """
                INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name, work_email, employment_type, hire_date, valid_period)
                VALUES ($1, $2, '兼務出向', '花子', '兼務出向 花子', '兼務出向花子', 'dual@example.test', 'regular', $3, daterange($3, NULL))
                """,
                cancellationToken,
                EntityId.New(),
                Dual,
                f.LongAgo);
            foreach (var (unit, type, secondment) in new[] { ("S1", "primary", "none"), ("DP2", "concurrent", "none"), ("B-G1", "concurrent", "outbound") })
            {
                await SampleData.ExecuteAsync(
                    app,
                    """
                    INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, secondment_type, valid_period)
                    VALUES ($1, $2, $3, $4, $5, daterange($6, NULL))
                    """,
                    cancellationToken,
                    EntityId.New(),
                    Dual,
                    f.Unit(unit),
                    type,
                    secondment,
                    f.LongAgo);
            }

            foreach (var (type, unit, date) in new[] { ("hire", "S1", f.LongAgo), ("concurrent_start", "DP2", f.Today.AddDays(3)), ("secondment_start", "B-G1", f.Today.AddDays(3)) })
            {
                await SampleData.ExecuteAsync(
                    app,
                    "INSERT INTO personnel_action (id, person_id, action_type, effective_date, org_unit_id, assignment_type) VALUES ($1, $2, $3, $4, $5, $6)",
                    cancellationToken,
                    EntityId.New(),
                    Dual,
                    type,
                    date,
                    f.Unit(unit),
                    type == "hire" ? "primary" : "concurrent");
            }
        }

        var store = new NpgsqlPolicyRuleStore(AppDataSource, TimeProvider.System);
        var rule = PolicyEvaluationTests.Rule(PolicyTargetKind.OrgUnit, "R1", [PersonFields.Assignments], RelationScope.SameLevel("section"));
        await store.InsertProposalAsync(f.HrAdmin(), rule, cancellationToken);
        if (!await store.ApproveAsync(Approver, rule.Id, cancellationToken))
        {
            throw new InvalidOperationException("追加のポリシーを承認できませんでした。");
        }
    }

    private async Task InsertUnitAsync(NpgsqlConnection app, Guid id, string code, string name, string parentKey, DateOnly from, DateOnly? until, CancellationToken cancellationToken)
    {
        var f = Fixture;
        var parent = f.Units.Single(u => u.Code == parentKey);
        var chain = new List<Guid> { id };
        for (UnitRow? current = parent; current is not null; current = current.ParentId is { } p ? f.Units.Single(u => u.Id == p) : null)
        {
            chain.Add(current.Id);
        }

        chain.Reverse();
        await SampleData.ExecuteAsync(app, "INSERT INTO org_unit (id, legal_entity_id) VALUES ($1, $2)", cancellationToken, id, parent.LegalEntityId);
        await SampleData.ExecuteAsync(
            app,
            """
            INSERT INTO org_unit_version (id, org_unit_id, legal_entity_id, code, name, level_key, parent_org_unit_id, head_person_id, path, valid_period)
            VALUES ($1, $2, $3, $4, $5, NULL, $6, NULL, $7::ltree, daterange($8, $9))
            """,
            cancellationToken,
            EntityId.New(),
            id,
            parent.LegalEntityId,
            code,
            name,
            parent.Id,
            string.Join('.', chain.Select(c => c.ToString("N"))),
            from,
            until is { } u ? u : DBNull.Value);
    }
}
