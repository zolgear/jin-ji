using JinJi.Domain;
using JinJi.Infrastructure.Database;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// 建物・フロアモード（0019）のテスト用の一時データベース。<see cref="AuthFixtureDatabase"/>（人のログインができる）に、建物・階・配置図・席の割り当てを足す。
/// </summary>
/// <remarks>
/// <code>
/// 拠点 SITE-TKY（東京本社）
///   建物 MAIN 本館
///     1F（level 10、lobby。配置図なし）
///     3F（level 30、office）。配置図は 10 年前から（外形 40m × 20m）
///       ゾーン: 3F 西（office、x 0〜20m）・3F 東（office、x 20〜40m）・3F 書庫（restricted、東の奥。席なし）
///       席: A01〜A04（固定席・西。A04 は保守中）・S01（持ち場・定員 2・西）・B01〜B03（固定席・東。モニター付き。割り当てなし）
///       30 日後の改訂（予定）: A05 を足す（人事管理者以外には見えない）
/// 席の割り当て: u1_member → A01（10 日後に A03 へ移る = 席替えの予定）、u2_member → A02（個人単位の例外で席は本人だけ）、
///   s2_member・dp2_member → S01（持ち場）
/// </code>
/// </remarks>
public class WorkplaceFixtureDatabase : AuthFixtureDatabase
{
    internal Guid Building { get; } = EntityId.New();

    internal Guid Floor1F { get; } = EntityId.New();

    internal Guid Floor3F { get; } = EntityId.New();

    internal Guid Layout3F { get; } = EntityId.New();

    internal Guid FutureLayout3F { get; } = EntityId.New();

    internal Guid ZoneWest { get; } = EntityId.New();

    internal Guid ZoneEast { get; } = EntityId.New();

    internal Guid ZoneArchive { get; } = EntityId.New();

    /// <summary>席番号 → 席 ID。</summary>
    internal Dictionary<string, Guid> Seats { get; } = new(StringComparer.Ordinal);

    /// <summary>u1_member の席替えの日（A01 → A03）。</summary>
    internal DateOnly MoveDate => Fixture.Today.AddDays(10);

    /// <summary>3F の次の改訂の日（A05 を足す）。</summary>
    internal DateOnly RevisionDate => Fixture.Today.AddDays(30);

    internal Guid Seat(string code) => Seats[code];

    protected override async Task SeedExtraAsync(CancellationToken cancellationToken)
    {
        await base.SeedExtraAsync(cancellationToken);
        await using (var app = await Database.OpenAppAsync(cancellationToken))
        {
            await SampleData.ActAsHrAdminAsync(app, cancellationToken);
            await SeedWorkplaceAsync(app, cancellationToken);
        }

        // u2_member の席は本人だけ（個人単位の例外。安全上の配慮などで席を隠した人）
        var store = new NpgsqlPolicyRuleStore(AppDataSource, TimeProvider.System);
        var rule = new PolicyRule
        {
            Id = EntityId.New(),
            TargetKind = PolicyTargetKind.Person,
            PersonId = Fixture.Person("u2_member"),
            Fields = new HashSet<string>(StringComparer.Ordinal) { PersonFields.Seat },
            Allow = RelationScope.Self,
        };
        await store.InsertProposalAsync(Fixture.HrAdmin(), rule, cancellationToken);
        if (!await store.ApproveAsync(Approver, rule.Id, cancellationToken))
        {
            throw new InvalidOperationException("フィクスチャの個人単位の例外を承認できませんでした。");
        }
    }

    private async Task SeedWorkplaceAsync(NpgsqlConnection app, CancellationToken cancellationToken)
    {
        var longAgo = Fixture.LongAgo;
        var site = Fixture.SiteTokyo.Id;
        var approver = Fixture.Person("hr_head");
        var proposer = Fixture.Person("hr_admin");
        await SampleData.ExecuteAsync(app, "INSERT INTO building (id, site_id) VALUES ($1, $2)", cancellationToken, Building, site);
        await SampleData.ExecuteAsync(
            app,
            "INSERT INTO building_version (id, building_id, site_id, code, name, sort_order, valid_period) VALUES ($1, $2, $3, 'MAIN', '本館', 0, daterange($4, NULL))",
            cancellationToken,
            EntityId.New(),
            Building,
            site,
            longAgo);
        foreach (var (floor, label, order, kind) in new[] { (Floor1F, "1F", 10, "lobby"), (Floor3F, "3F", 30, "office") })
        {
            await SampleData.ExecuteAsync(app, "INSERT INTO floor (id, building_id) VALUES ($1, $2)", cancellationToken, floor, Building);
            await SampleData.ExecuteAsync(
                app,
                "INSERT INTO floor_version (id, floor_id, building_id, label, level_order, floor_kind, valid_period) VALUES ($1, $2, $3, $4, $5, $6, daterange($7, NULL))",
                cancellationToken,
                EntityId.New(),
                floor,
                Building,
                label,
                order,
                kind,
                longAgo);
        }

        // 席（S01 は持ち場・定員 2、A04 は保守中）
        var seats = new (string Code, Guid? Zone, int X, int Y, string Kind, int Capacity, bool OutOfService)[]
        {
            ("A01", ZoneWest, 2000, 2000, "fixed", 1, false),
            ("A02", ZoneWest, 4000, 2000, "fixed", 1, false),
            ("A03", ZoneWest, 6000, 2000, "fixed", 1, false),
            ("A04", ZoneWest, 8000, 2000, "fixed", 1, true),
            ("S01", ZoneWest, 10000, 8000, "station", 2, false),
            ("B01", ZoneEast, 22000, 2000, "fixed", 1, false),
            ("B02", ZoneEast, 24000, 2000, "fixed", 1, false),
            ("B03", ZoneEast, 26000, 2000, "fixed", 1, false),
        };
        foreach (var seat in seats.Select(s => s.Code).Append("A05"))
        {
            Seats[seat] = EntityId.New();
            await SampleData.ExecuteAsync(app, "INSERT INTO seat (id, floor_id, code) VALUES ($1, $2, $3)", cancellationToken, Seats[seat], Floor3F, "3F-" + seat);
        }

        foreach (var (layout, from, until) in new[] { (Layout3F, longAgo, (DateOnly?)RevisionDate), (FutureLayout3F, RevisionDate, null) })
        {
            await SampleData.ExecuteAsync(
                app,
                """
                INSERT INTO floor_layout (id, floor_id, outline, fixtures, grid_mm, proposed_by_person_id, approved_by_person_id, valid_period)
                VALUES ($1, $2, '[[0,0],[40000,0],[40000,20000],[0,20000]]', '[{"kind":"core","name":"コア","polygon":[[18000,9000],[22000,9000],[22000,11000],[18000,11000]]}]', 100, $3, $4, daterange($5, $6))
                """,
                cancellationToken,
                layout,
                Floor3F,
                proposer,
                approver,
                from,
                until is null ? DBNull.Value : until.Value);
            foreach (var (zone, name, kind, polygon) in new[]
                     {
                         (ZoneWest, "3F 西", "office", "[[0,0],[20000,0],[20000,20000],[0,20000]]"),
                         (ZoneEast, "3F 東", "office", "[[20000,0],[40000,0],[40000,20000],[20000,20000]]"),
                         (ZoneArchive, "3F 書庫", "restricted", "[[30000,15000],[40000,15000],[40000,20000],[30000,20000]]"),
                     })
            {
                await SampleData.ExecuteAsync(
                    app,
                    "INSERT INTO layout_zone (layout_id, zone_key, name, zone_kind, polygon) VALUES ($1, $2, $3, $4, $5::jsonb)",
                    cancellationToken,
                    layout,
                    zone,
                    name,
                    kind,
                    polygon);
            }

            var placed = layout == FutureLayout3F ? seats.Append(("A05", ZoneWest, 12000, 2000, "fixed", 1, false)) : seats;
            foreach (var s in placed)
            {
                await SampleData.ExecuteAsync(
                    app,
                    """
                    INSERT INTO layout_seat (layout_id, floor_id, seat_id, zone_key, x_mm, y_mm, rotation_deg, desk_type, seat_kind, capacity, features, out_of_service)
                    VALUES ($1, $2, $3, $4, $5, $6, 0, $7, $8, $9, $10, $11)
                    """,
                    cancellationToken,
                    layout,
                    Floor3F,
                    Seats[s.Code],
                    s.Zone is { } z ? z : DBNull.Value,
                    s.X,
                    s.Y,
                    s.Kind == "station" ? "station" : "desk_1200",
                    s.Kind,
                    (short)s.Capacity,
                    s.Code.StartsWith('B') ? new[] { "monitor" } : Array.Empty<string>(),
                    s.OutOfService);
            }
        }

        // 席の割り当て（u1_member は 10 日後に A01 → A03 = 席替えの予定）
        var assignments = new (string Person, string Seat, bool Exclusive, DateOnly From, DateOnly? Until)[]
        {
            ("u1_member", "A01", true, longAgo, MoveDate),
            ("u1_member", "A03", true, MoveDate, null),
            ("u2_member", "A02", true, longAgo, null),
            ("s2_member", "S01", false, longAgo, null),
            ("dp2_member", "S01", false, longAgo, null),
        };
        foreach (var a in assignments)
        {
            await SampleData.ExecuteAsync(
                app,
                """
                INSERT INTO workplace_assignment (id, person_id, floor_id, seat_id, role, exclusive, valid_period)
                VALUES ($1, $2, $3, $4, 'primary', $5, daterange($6, $7))
                """,
                cancellationToken,
                EntityId.New(),
                Fixture.Person(a.Person),
                Floor3F,
                Seats[a.Seat],
                a.Exclusive,
                a.From,
                a.Until is null ? DBNull.Value : a.Until.Value);
        }
    }
}
