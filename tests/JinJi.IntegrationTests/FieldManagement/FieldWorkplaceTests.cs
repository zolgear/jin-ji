using JinJi.Application.FieldManagement;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Ingest;
using JinJi.Policy;

namespace JinJi.IntegrationTests.FieldManagement;

/// <summary>
/// 人の席・持ち場の直接登録（0025・ADR-0013 決定 11）: 管理元の既定（現場）、範囲の中の即時反映と未来日の秘匿、席の入れ替え、使っている人がいる席・範囲の外・管理元の拒否、
/// 取り消しを実 PostgreSQL で確かめる。
/// </summary>
/// <remarks>
/// 組織と人は <see cref="IngestDatabase"/>（head は第一営業部 DP1 の長で、営業一課 S1・営業二課 S2 が範囲。hr1・hr2 は人事部 HR）。
/// 階は 1 つ（席 A01・A02・A03 は定員 1、S01 は定員 2 の持ち場、X01 は保守中）。member は A02、hr1 は A03 を使っている。
/// </remarks>
public sealed class FieldWorkplaceTests : IAsyncLifetime
{
    private readonly Dictionary<string, Guid> _seats = [];
    private IngestDatabase _db = null!;
    private FieldManagementService _management = null!;
    private FieldRegistrationService _service = null!;
    private Guid _floor;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => IngestDatabase.Today;

    private ViewerContext OrgAdmin => ViewerContext.ForUser(_db.Id("takahashi"), [Roles.OrgAdmin], Channel.Ui, FieldLimit.Unrestricted);

    private ViewerContext Head => _db.User("head");

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        _management = new FieldManagementService(new NpgsqlFieldManagementStore(_db.DataSource), _db.Snapshots, _db.Audit, TimeProvider.System);
        var store = new NpgsqlFieldRegistrationStore(_db.DataSource, TimeProvider.System);
        _service = new FieldRegistrationService(store, _management, _db.Snapshots, _db.Audit, TimeProvider.System, _db.Notifier);
        await SeedFloorAsync();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    private async Task SetModeAsync(FieldManagementMode mode)
    {
        var setting = await _management.ProposeModeAsync(OrgAdmin, FieldDomain.Workplace, mode, null, Ct);
        await _management.DecideModeAsync(_db.HrUser("hr2"), setting.Id, true, Ct);
    }

    private FieldChangeInput Seat(string person, string? seat) =>
        new(FieldChangeKind.Workplace, _db.Id(person), new Dictionary<string, string?> { [ProposalFields.SeatId] = seat is null ? null : _seats[seat].ToString("D") });

    private static FieldRegistrationInput Input(DateOnly date, params FieldChangeInput[] changes) => new(date, null, changes);

    /// <summary>その日の主の席の席番号（人事管理者の見え方）。</summary>
    private async Task<string?> SeatOfAsync(string person, DateOnly date, ViewerContext? viewer = null) =>
        (await _db.QueryWithAsync<string>(
            viewer ?? _db.HrUser("hr2"),
            """
            SELECT s.code FROM workplace_assignment w JOIN seat s ON s.id = w.seat_id
            WHERE w.person_id = $1 AND w.role = 'primary' AND upper_inf(w.recorded_period) AND w.valid_period @> $2::date
            """,
            Ct,
            _db.Id(person),
            date)).SingleOrDefault();

    private async Task<FieldManagementError> ErrorOfAsync(Func<Task> action) =>
        (await Assert.ThrowsAsync<FieldManagementException>(action)).Error;

    [Fact(DisplayName = "席: 組織長は配下の人の席を有効日から決められ、未来日の席は関係者にだけ見える。有効日の前なら取り消せる")]
    public async Task Register_AssignsSeatAndWithdraws()
    {
        var date = Today.AddDays(7);

        var registration = await _service.RegisterAsync(Head, Input(date, Seat("takahashi", "A01")), Ct);

        Assert.Equal(FieldRegistrationStatus.Applied, registration.Status);
        Assert.Null(await SeatOfAsync("takahashi", Today));
        Assert.Equal("A01", await SeatOfAsync("takahashi", date));
        Assert.Equal("A01", await SeatOfAsync("takahashi", date, Head));
        Assert.Null(await SeatOfAsync("takahashi", date, _db.User("takahashi")));
        Assert.Null(await SeatOfAsync("takahashi", date, _db.User("member")));

        await _service.WithdrawAsync(Head, registration.Id, Ct);
        Assert.Null(await SeatOfAsync("takahashi", date));
    }

    [Fact(DisplayName = "席: 2 人の席の入れ替えを 1 回で登録でき（定員 1 の席を先に空ける順で反映）、席を外すこともできる")]
    public async Task Register_SwapsAndClearsSeats()
    {
        var date = Today.AddDays(3);
        await _service.RegisterAsync(Head, Input(Today, Seat("takahashi", "A01")), Ct);

        var swap = await _service.RegisterAsync(Head, Input(date, Seat("takahashi", "A02"), Seat("member", "A01")), Ct);

        Assert.Equal(FieldRegistrationStatus.Applied, swap.Status);
        Assert.Equal(("A01", "A02"), (await SeatOfAsync("takahashi", Today), await SeatOfAsync("member", Today)));
        Assert.Equal(("A02", "A01"), (await SeatOfAsync("takahashi", date), await SeatOfAsync("member", date)));

        // 取り消しは入れ替えの前の席に戻す
        await _service.WithdrawAsync(Head, swap.Id, Ct);
        Assert.Equal(("A01", "A02"), (await SeatOfAsync("takahashi", date), await SeatOfAsync("member", date)));

        // 席を外す。外した後に同じ人の席を変えた登録は、もう取り消せない（後の登録が同じ行を変えたため）
        var clear = await _service.RegisterAsync(Head, Input(date, Seat("member", null)), Ct);
        Assert.Equal(("A02", (string?)null), (await SeatOfAsync("member", Today), await SeatOfAsync("member", date)));
        await _service.RegisterAsync(Head, Input(date.AddDays(1), Seat("member", "A02")), Ct);
        Assert.Equal(FieldManagementError.Conflict, await ErrorOfAsync(() => _service.WithdrawAsync(Head, clear.Id, Ct)));
    }

    [Fact(DisplayName = "席: 管理元は設定しなくても現場（直接登録できる）。取込（hr）にした導入先では登録できない")]
    public async Task Mode_DefaultsToField()
    {
        Assert.Equal(FieldManagementMode.Field, (await _management.ModesAsync(Head, Ct))[FieldDomain.Workplace]);
        Assert.Equal(["field"], await _db.QueryAsync<string>(null, "SELECT jinji_field_domain_mode('workplace')", Ct));

        await SetModeAsync(FieldManagementMode.Hr);
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.RegisterAsync(Head, Input(Today, Seat("takahashi", "A01")), Ct)));
    }

    [Fact(DisplayName = "席: 使っている人がいる席・保守中の席・範囲の外の人・持ち場の定員超えは拒否する")]
    public async Task Register_RejectsOccupiedOutOfScope()
    {

        // A03 は範囲の外の hr1 が使っている。hr1 の席は head には変えられない
        Assert.Equal(FieldManagementError.Invalid, await ErrorOfAsync(() => _service.RegisterAsync(Head, Input(Today, Seat("takahashi", "A03")), Ct)));
        Assert.Equal(FieldManagementError.Forbidden, await ErrorOfAsync(() => _service.RegisterAsync(Head, Input(Today, Seat("hr1", "A01")), Ct)));
        Assert.Equal(FieldManagementError.Invalid, await ErrorOfAsync(() => _service.RegisterAsync(Head, Input(Today, Seat("takahashi", "X01")), Ct)));

        // 持ち場（定員 2）は 2 人まで
        await _service.RegisterAsync(Head, Input(Today, Seat("takahashi", "S01"), Seat("head", "S01")), Ct);
        Assert.Equal(FieldManagementError.Invalid, await ErrorOfAsync(() => _service.RegisterAsync(Head, Input(Today, Seat("member", "S01")), Ct)));

        // 席の無い人の席は外せない
        Assert.Equal(FieldManagementError.Invalid, await ErrorOfAsync(() => _service.RegisterAsync(OrgAdmin, Input(Today, Seat("hr2", null)), Ct)));
    }

    [Fact(DisplayName = "席: 素案の変更の種類 workplace と値 seat_id・floor_id・zone_key を DB が受け付ける")]
    public async Task Plan_AcceptsWorkplaceChanges()
    {
        var allowed = await _db.QueryAsync<bool>(
            null,
            """SELECT jinji_plan_changes_allowed('[{"kind":"workplace","target_id":"00000000-0000-0000-0000-000000000001","values":{"seat_id":null,"floor_id":"x","zone_key":"y"}}]'::jsonb)""",
            Ct);
        Assert.True(Assert.Single(allowed));
    }

    private async Task SeedFloorAsync()
    {
        var hr = _db.HrUser("hr2");
        var site = EntityId.New();
        var building = EntityId.New();
        _floor = EntityId.New();
        var layout = EntityId.New();
        await _db.ExecuteAsync(hr, "INSERT INTO site (id, legal_entity_id) VALUES ($1, $2)", Ct, site, _db.EntityA);
        await _db.ExecuteAsync(
            hr,
            "INSERT INTO site_version (id, site_id, legal_entity_id, code, name, site_type, valid_period) VALUES ($1, $2, $3, 'TKY', '本社', 'head_office', daterange($4, NULL))",
            Ct,
            EntityId.New(),
            site,
            _db.EntityA,
            IngestDatabase.LongAgo);
        await _db.ExecuteAsync(hr, "INSERT INTO building (id, site_id) VALUES ($1, $2)", Ct, building, site);
        await _db.ExecuteAsync(
            hr,
            "INSERT INTO building_version (id, building_id, site_id, code, name, valid_period) VALUES ($1, $2, $3, 'MAIN', '本館', daterange($4, NULL))",
            Ct,
            EntityId.New(),
            building,
            site,
            IngestDatabase.LongAgo);
        await _db.ExecuteAsync(hr, "INSERT INTO floor (id, building_id) VALUES ($1, $2)", Ct, _floor, building);
        await _db.ExecuteAsync(
            hr,
            "INSERT INTO floor_version (id, floor_id, building_id, label, level_order, floor_kind, valid_period) VALUES ($1, $2, $3, '3F', 30, 'office', daterange($4, NULL))",
            Ct,
            EntityId.New(),
            _floor,
            building,
            IngestDatabase.LongAgo);
        await _db.ExecuteAsync(
            hr,
            """
            INSERT INTO floor_layout (id, floor_id, outline, proposed_by_person_id, approved_by_person_id, valid_period)
            VALUES ($1, $2, '[[0,0],[20000,0],[20000,10000],[0,10000]]', $3, $4, daterange($5, NULL))
            """,
            Ct,
            layout,
            _floor,
            _db.Id("hr1"),
            _db.Id("hr2"),
            IngestDatabase.LongAgo);
        var x = 2000;
        foreach (var (code, kind, capacity, outOfService) in new[] { ("A01", "fixed", 1, false), ("A02", "fixed", 1, false), ("A03", "fixed", 1, false), ("S01", "station", 2, false), ("X01", "fixed", 1, true) })
        {
            _seats[code] = EntityId.New();
            await _db.ExecuteAsync(hr, "INSERT INTO seat (id, floor_id, code) VALUES ($1, $2, $3)", Ct, _seats[code], _floor, code);
            await _db.ExecuteAsync(
                hr,
                """
                INSERT INTO layout_seat (layout_id, floor_id, seat_id, x_mm, y_mm, desk_type, seat_kind, capacity, out_of_service)
                VALUES ($1, $2, $3, $4, 2000, $5, $6, $7, $8)
                """,
                Ct,
                layout,
                _floor,
                _seats[code],
                x,
                kind == "station" ? "station" : "desk_1200",
                kind,
                (short)capacity,
                outOfService);
            x += 2000;
        }

        foreach (var (person, seat) in new[] { ("member", "A02"), ("hr1", "A03") })
        {
            await _db.ExecuteAsync(
                hr,
                "INSERT INTO workplace_assignment (id, person_id, floor_id, seat_id, role, exclusive, valid_period) VALUES ($1, $2, $3, $4, 'primary', true, daterange($5, NULL))",
                Ct,
                EntityId.New(),
                _db.Id(person),
                _floor,
                _seats[seat],
                IngestDatabase.LongAgo);
        }
    }
}
