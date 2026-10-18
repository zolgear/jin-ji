using JinJi.Domain;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Workplace;

/// <summary>
/// 建物・フロアモード（0019）のスキーマの守り: 排他制約・CHECK 制約・複合外部キー・RLS（内示の線）と、所在・予約・フリーアドレスを持たないこと
/// （所在は保持せず外部の提供を注入して表示する）。アプリ用ロールで接続して確かめる。
/// </summary>
public sealed class WorkplaceSchemaTests(WorkplaceFixtureDatabase db) : IClassFixture<WorkplaceFixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    private ViewerContext Hr => F.HrAdmin();

    // -----------------------------------------------------------------------
    // 建物・階・席・割り当て
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "割り当て: 1 人の主の席は同時に 1 つ、定員 1 の席は同時に 1 人（排他制約）。持ち場（定員 2 以上）は重ねられる")]
    public async Task Assignment_ExclusionConstraints()
    {
        // u1_member は既に主の席（A01）を持つ
        var primary = await Assert.ThrowsAsync<PostgresException>(() => InsertAssignmentAsync("u1_member", "B02", true, "primary"));
        Assert.Equal(("23P01", "workplace_assignment_primary_no_overlap"), (primary.SqlState, primary.ConstraintName));

        // A02 は u2_member の固定席（定員 1）。副の席としても重ねられない
        var seat = await Assert.ThrowsAsync<PostgresException>(() => InsertAssignmentAsync("c_t1_member", "A02", true, "secondary"));
        Assert.Equal(("23P01", "workplace_assignment_seat_no_overlap"), (seat.SqlState, seat.ConstraintName));

        // 持ち場（exclusive = false）は副の席として重ねられる
        Assert.Equal(1, await InsertAssignmentAsync("c_t2_member", "S01", false, "secondary"));

        // 席かゾーンのどちらか一方。定員 1 は席の割り当てだけ
        var target = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            Hr,
            "INSERT INTO workplace_assignment (id, person_id, floor_id, role, exclusive, valid_period) VALUES ($1, $2, $3, 'secondary', false, daterange($4, NULL))",
            EntityId.New(),
            F.Person("c_t3_member"),
            db.Floor3F,
            F.Today));
        Assert.Equal("workplace_assignment_target", target.ConstraintName);
    }

    [Fact(DisplayName = "配置図: 席の置き場所の階は改訂の階・席の階と同じ、ゾーンは同じ改訂のもの（複合外部キー）。起案者 ≠ 承認者・多角形・定員・設備のタグは CHECK 制約")]
    public async Task Layout_Constraints()
    {
        // 1F の席を 3F の改訂に置けない
        var otherFloorSeat = EntityId.New();
        await ExecuteAsync(Hr, "INSERT INTO seat (id, floor_id, code) VALUES ($1, $2, '1F-X01')", otherFloorSeat, db.Floor1F);
        var floor = await Assert.ThrowsAsync<PostgresException>(() => InsertLayoutSeatAsync(db.Layout3F, otherFloorSeat, null, "fixed", 1, []));
        Assert.Equal("layout_seat_seat_floor_fkey", floor.ConstraintName);

        // 改訂に無いゾーン
        var newSeat = EntityId.New();
        await ExecuteAsync(Hr, "INSERT INTO seat (id, floor_id, code) VALUES ($1, $2, '3F-X99')", newSeat, db.Floor3F);
        var zone = await Assert.ThrowsAsync<PostgresException>(() => InsertLayoutSeatAsync(db.Layout3F, newSeat, Guid.NewGuid(), "fixed", 1, []));
        Assert.Equal("layout_seat_zone_fkey", zone.ConstraintName);

        // 定員 2 以上は持ち場だけ・健康・障害に関わるタグ（車椅子対応など）は作らない・席の種類は固定席と持ち場だけ（フリーアドレスは持たない）
        Assert.Equal("layout_seat_capacity_check", (await Assert.ThrowsAsync<PostgresException>(() => InsertLayoutSeatAsync(db.Layout3F, newSeat, null, "fixed", 2, []))).ConstraintName);
        Assert.Equal("layout_seat_features_check", (await Assert.ThrowsAsync<PostgresException>(() => InsertLayoutSeatAsync(db.Layout3F, newSeat, null, "fixed", 1, ["wheelchair"]))).ConstraintName);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => InsertLayoutSeatAsync(db.Layout3F, newSeat, null, "free", 1, []))).SqlState);
        Assert.Equal("23514", (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            Hr,
            "INSERT INTO layout_zone (layout_id, zone_key, name, zone_kind, polygon) VALUES ($1, $2, 'X', 'free_address', '[[0,0],[10,0],[10,10]]'::jsonb)",
            db.Layout3F,
            Guid.NewGuid()))).SqlState);

        // 起案者と承認者が同じ・外形の頂点が足りない・座標が整数でない
        var floorLayout =
            "INSERT INTO floor_layout (id, floor_id, outline, proposed_by_person_id, approved_by_person_id, valid_period) VALUES ($1, $2, $3::jsonb, $4, $5, daterange($6, NULL))";
        var twoPerson = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            Hr, floorLayout, EntityId.New(), db.Floor1F, "[[0,0],[10,0],[10,10]]", F.Person("hr_admin"), F.Person("hr_admin"), F.Today));
        Assert.Equal("floor_layout_two_person", twoPerson.ConstraintName);
        foreach (var outline in new[] { "[[0,0],[10,0]]", "[[0,0],[10.5,0],[10,10]]", "[[0,0],[600000,0],[10,10]]", "{\"x\":1}" })
        {
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                Hr, floorLayout, EntityId.New(), db.Floor1F, outline, F.Person("hr_admin"), F.Person("hr_head"), F.Today));
            Assert.Equal("floor_layout_outline_check", invalid.ConstraintName);
        }

        // 同じ階の承認済みの改訂の有効期間は重ならない
        var overlap = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            Hr, floorLayout, EntityId.New(), db.Floor3F, "[[0,0],[10,0],[10,10]]", F.Person("hr_admin"), F.Person("hr_head"), F.Today));
        Assert.Equal("floor_layout_no_overlap", overlap.ConstraintName);
    }

    [Fact(DisplayName = "RLS: 将来の割り当て（席替えの予定）・将来の改訂とその席の置き場所は、人事管理者以外には見えない")]
    public async Task FutureRows_AreHiddenFromNonAdmins()
    {
        const string assignments = "SELECT count(*) FROM workplace_assignment WHERE person_id = $1";
        Assert.Equal([2L], await db.QueryAsAppAsync<long>(Hr, assignments, Ct, F.Person("u1_member")));
        Assert.Equal([1L], await db.QueryAsAppAsync<long>(F.User("u1_member"), assignments, Ct, F.Person("u1_member")));
        Assert.Equal([1L], await db.QueryAsAppAsync<long>(F.User("sec1_head"), assignments, Ct, F.Person("u1_member")));

        const string seats = "SELECT count(*) FROM layout_seat WHERE seat_id = $1";
        Assert.Equal([1L], await db.QueryAsAppAsync<long>(Hr, seats, Ct, db.Seat("A05")));
        Assert.Equal([0L], await db.QueryAsAppAsync<long>(F.User("sec1_head"), seats, Ct, db.Seat("A05")));
        Assert.Equal([0L], await db.QueryAsAppAsync<long>(F.User("sec1_head"), "SELECT count(*) FROM floor_layout WHERE id = $1", Ct, db.FutureLayout3F));
        Assert.Equal([0L], await db.QueryAsAppAsync<long>(F.User("sec1_head"), "SELECT count(*) FROM layout_zone WHERE layout_id = $1", Ct, db.FutureLayout3F));

        // 一般の閲覧者は建物・階・席・割り当てを書けない
        var write = await Assert.ThrowsAsync<PostgresException>(() => InsertAssignmentAsync("c_t3_member", "B03", true, "primary", F.User("c_t3_member")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, write.SqlState);
    }

    // -----------------------------------------------------------------------
    // 範囲の外（所在・予約・フリーアドレス・組織のエリア）
    // -----------------------------------------------------------------------

    [Fact(DisplayName = "範囲: 当日の所在・座席予約・組織のエリアの表を持たず、建物・フロアモードの表に時刻の列が無い（所在は保持せず外部の提供を注入して表示する）")]
    public async Task Schema_HasNoPresenceOrReservation()
    {
        await using var owner = await db.Database.OpenOwnerAsync(Ct);
        await using (var tables = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND (table_name LIKE '%presence%' OR table_name LIKE '%reservation%' OR table_name LIKE '%booking%' OR table_name = 'org_unit_zone')
            """,
            owner))
        {
            Assert.Equal(0L, await tables.ExecuteScalarAsync(Ct));
        }

        await using var columns = new NpgsqlCommand(
            """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name IN ('building', 'building_version', 'floor', 'floor_version', 'seat', 'floor_layout', 'layout_zone', 'layout_seat', 'workplace_assignment')
              AND (data_type LIKE '%time%' OR data_type = 'interval')
            """,
            owner);
        Assert.Equal(0L, await columns.ExecuteScalarAsync(Ct));
    }

    private Task<int> InsertAssignmentAsync(string person, string seat, bool exclusive, string role, ViewerContext? viewer = null) =>
        ExecuteAsync(
            viewer ?? Hr,
            "INSERT INTO workplace_assignment (id, person_id, floor_id, seat_id, role, exclusive, valid_period) VALUES ($1, $2, $3, $4, $5, $6, daterange($7, NULL))",
            EntityId.New(),
            F.Person(person),
            db.Floor3F,
            db.Seat(seat),
            role,
            exclusive,
            F.Today);

    private Task<int> InsertLayoutSeatAsync(Guid layout, Guid seat, Guid? zone, string kind, short capacity, string[] features) =>
        ExecuteAsync(
            Hr,
            """
            INSERT INTO layout_seat (layout_id, floor_id, seat_id, zone_key, x_mm, y_mm, desk_type, seat_kind, capacity, features)
            VALUES ($1, $2, $3, $4, 100, 100, 'desk_1200', $5, $6, $7)
            """,
            layout,
            db.Floor3F,
            seat,
            zone is { } z ? z : DBNull.Value,
            kind,
            capacity,
            features);

    private async Task<int> ExecuteAsync(ViewerContext viewer, string sql, params object[] parameters)
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await ViewerTransaction.ApplyAsync(connection, transaction, viewer, Ct);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var affected = await command.ExecuteNonQueryAsync(Ct);
        await transaction.CommitAsync(Ct);
        return affected;
    }
}
