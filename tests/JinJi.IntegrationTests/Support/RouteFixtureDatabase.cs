using System.Globalization;
using JinJi.Domain;
using Npgsql;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// 承認ルート・先行提供のテスト用の一時データベース。<see cref="AuthFixtureDatabase"/>（人事管理者のロール）に、経路の場合分けのデータを足す。
/// </summary>
/// <remarks>
/// <code>
/// 組織 RX「退職長課」（section、DP2 の下）: 組織長は retired（先月退職）。所属: rx_member
/// 組織 NEW「新設課」（section、DP2 の下）: 20 日後に新設（組織改編の予定）。組織長は dp2_member（20 日後から NEW に兼務・役職 課長）
/// 人 mover: 主務 U3（企画係。長なし）→ 20 日後に NEW へ異動（発令と将来の所属 = 内示）
/// 一時停止: unit1_head（U1 第一係の長）を今日から
/// </code>
/// </remarks>
public sealed class RouteFixtureDatabase : AuthFixtureDatabase
{
    internal Guid RetiredHeadUnit { get; } = EntityId.New();

    internal Guid NewUnit { get; } = EntityId.New();

    internal Guid RxMember { get; } = EntityId.New();

    internal Guid Mover { get; } = EntityId.New();

    /// <summary>mover の 20 日後からの所属（NEW）。取り消しのテストで記録期間を閉じる。</summary>
    internal Guid MoverFutureAssignment { get; } = EntityId.New();

    /// <summary>組織改編の日（今日から 20 日後）。</summary>
    internal DateOnly ReorgDate => Fixture.Today.AddDays(20);

    protected override async Task SeedExtraAsync(CancellationToken cancellationToken)
    {
        await base.SeedExtraAsync(cancellationToken);
        var f = Fixture;
        await using var app = await Database.OpenAppAsync(cancellationToken);
        await SampleData.ActAsHrAdminAsync(app, cancellationToken);

        await InsertUnitAsync(app, RetiredHeadUnit, "RX", "退職長課", "DP2", f.Person("retired"), f.LongAgo, cancellationToken);
        await InsertUnitAsync(app, NewUnit, "NEW", "新設課", "DP2", f.Person("dp2_member"), ReorgDate, cancellationToken);

        await InsertPersonAsync(app, RxMember, "E9201", "退職長課員", cancellationToken);
        await InsertAssignmentAsync(app, RxMember, RetiredHeadUnit, "primary", null, f.LongAgo, null, cancellationToken);

        await InsertPersonAsync(app, Mover, "E9202", "異動予定", cancellationToken);
        await InsertAssignmentAsync(app, Mover, f.Unit("U3"), "primary", null, f.LongAgo, ReorgDate, cancellationToken);
        await InsertAssignmentAsync(app, Mover, NewUnit, "primary", null, ReorgDate, null, cancellationToken, MoverFutureAssignment);
        await InsertAssignmentAsync(app, f.Person("dp2_member"), NewUnit, "concurrent", "課長", ReorgDate, null, cancellationToken);
        await SampleData.ExecuteAsync(
            app,
            "INSERT INTO personnel_action (id, person_id, action_type, effective_date, org_unit_id, assignment_type) VALUES ($1, $2, 'transfer', $3, $4, 'primary')",
            cancellationToken,
            EntityId.New(),
            Mover,
            ReorgDate,
            NewUnit);

        await SampleData.ExecuteAsync(
            app,
            "INSERT INTO account_suspension (id, person_id, valid_period) VALUES ($1, $2, daterange($3, NULL))",
            cancellationToken,
            EntityId.New(),
            f.Person("unit1_head"),
            f.Today);
    }

    private static async Task InsertPersonAsync(NpgsqlConnection app, Guid id, string employeeNumber, string name, CancellationToken cancellationToken)
    {
        await SampleData.ExecuteAsync(app, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, $3)", cancellationToken, id, OrgFixture.Shared.EntityA, employeeNumber);
        await SampleData.ExecuteAsync(
            app,
            """
            INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name, work_email, employment_type, hire_date, valid_period)
            VALUES ($1, $2, $3, '一郎', $4, $5, $6, 'regular', $7, daterange($7, NULL))
            """,
            cancellationToken,
            EntityId.New(),
            id,
            name,
            name + " 一郎",
            name + "一郎",
            $"{employeeNumber.ToLowerInvariant()}@example.test",
            OrgFixture.Shared.LongAgo);
    }

    private static async Task InsertAssignmentAsync(
        NpgsqlConnection app,
        Guid person,
        Guid unit,
        string type,
        string? position,
        DateOnly from,
        DateOnly? until,
        CancellationToken cancellationToken,
        Guid? id = null)
    {
        await SampleData.ExecuteAsync(
            app,
            """
            INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, position_title, valid_period)
            VALUES ($1, $2, $3, $4, $5, daterange($6, $7))
            """,
            cancellationToken,
            id ?? EntityId.New(),
            person,
            unit,
            type,
            position is null ? DBNull.Value : position,
            from,
            until is { } u ? u : DBNull.Value);
    }

    private async Task InsertUnitAsync(
        NpgsqlConnection app,
        Guid id,
        string code,
        string name,
        string parentKey,
        Guid head,
        DateOnly from,
        CancellationToken cancellationToken)
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
            VALUES ($1, $2, $3, $4, $5, 'section', $6, $7, $8::ltree, daterange($9, NULL))
            """,
            cancellationToken,
            EntityId.New(),
            id,
            parent.LegalEntityId,
            code,
            name,
            parent.Id,
            head,
            string.Join('.', chain.Select(c => c.ToString("N"))),
            from);
    }

    /// <summary>日付を yyyy-MM-dd に。</summary>
    internal static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
