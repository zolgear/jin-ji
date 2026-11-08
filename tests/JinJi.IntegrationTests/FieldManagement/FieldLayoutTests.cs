using System.Text;
using System.Text.Json;
using JinJi.Application.FieldManagement;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.Ingest;
using JinJi.IntegrationTests.Ingest;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.FieldManagement;

/// <summary>
/// 配置図の直接登録（0026・ADR-0013 決定 12）: 施設管理者の承認なしの反映と取り消し、承認ありの設定での別の人の承認、
/// 登録できる人・形の検査・施設管理者の書き込みの範囲を実 PostgreSQL で確かめる。
/// </summary>
/// <remarks>
/// 拠点 TKY の建物 MAIN に階 3F（席 A01・A02。A01 は使えない席、member が A02）がある。施設管理者は takahashi と head（ロールだけ足す）。
/// </remarks>
public sealed class FieldLayoutTests : IAsyncLifetime
{
    private const string Outline = "[[0,0],[20000,0],[20000,10000],[0,10000]]";

    private IngestDatabase _db = null!;
    private FieldManagementService _management = null!;
    private FieldRegistrationService _service = null!;
    private Guid _floor;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DateOnly Today => IngestDatabase.Today;

    private ViewerContext Facility1 => ViewerContext.ForUser(_db.Id("takahashi"), [Roles.FacilityAdmin], Channel.Ui, FieldLimit.Unrestricted);

    private ViewerContext Facility2 => ViewerContext.ForUser(_db.Id("head"), [Roles.FacilityAdmin], Channel.Ui, FieldLimit.Unrestricted);

    private ViewerContext OrgAdmin => ViewerContext.ForUser(_db.Id("takahashi"), [Roles.OrgAdmin], Channel.Ui, FieldLimit.Unrestricted);

    public async ValueTask InitializeAsync()
    {
        _db = await IngestDatabase.CreateAsync(Ct);
        _management = new FieldManagementService(new NpgsqlFieldManagementStore(_db.DataSource), _db.Snapshots, _db.Audit, TimeProvider.System);
        var store = new NpgsqlFieldRegistrationStore(_db.DataSource, TimeProvider.System);
        _service = new FieldRegistrationService(store, _management, _db.Snapshots, _db.Audit, TimeProvider.System, _db.Notifier, new LayoutDocumentPlanner());
        await SeedAsync();
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    /// <summary>配置図の JSON（建物 MAIN。階ごとに席番号と x 座標）。</summary>
    private static LayoutRegistrationInput Input(DateOnly date, params (string Label, int Order, (string Code, int X)[] Seats, string? Zone)[] floors)
    {
        var document = new
        {
            format = "jinji-floor-layout/v1",
            site_code = "TKY",
            buildings = new[]
            {
                new
                {
                    code = "MAIN",
                    name = "本館",
                    floors = floors.Select(f => new
                    {
                        label = f.Label,
                        level_order = f.Order,
                        kind = "office",
                        layout = new
                        {
                            valid_from = "2000-01-01",
                            outline = JsonSerializer.Deserialize<int[][]>(Outline),
                            zones = f.Zone is null ? [] : new[] { new { name = f.Zone, kind = "office", polygon = new[] { new[] { 0, 0 }, new[] { 10000, 0 }, new[] { 10000, 10000 }, new[] { 0, 10000 } } } },
                            seats = f.Seats.Select(s => new { code = s.Code, x_mm = s.X, y_mm = 2000, desk_type = "desk_1200", kind = "fixed" }).ToArray(),
                        },
                    }).ToArray(),
                },
            },
        };
        return new LayoutRegistrationInput(date, null, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document)));
    }

    /// <summary>その日の階の配置図の席番号（人事管理者の見え方）。</summary>
    private Task<List<string>> SeatsOnAsync(string label, DateOnly date, ViewerContext? viewer = null) =>
        _db.QueryWithAsync<string>(
            viewer ?? _db.HrUser("hr2"),
            """
            SELECT s.code FROM floor_layout l
            JOIN floor_version f ON f.floor_id = l.floor_id AND upper_inf(f.recorded_period) AND f.valid_period @> $2::date
            JOIN layout_seat ls ON ls.layout_id = l.id JOIN seat s ON s.id = ls.seat_id
            WHERE f.label = $1 AND upper_inf(l.recorded_period) AND l.valid_period @> $2::date
            ORDER BY s.code
            """,
            Ct,
            label,
            date);

    private async Task<FieldManagementException> ErrorOfAsync(Func<Task> action) =>
        await Assert.ThrowsAsync<FieldManagementException>(action);

    private async Task SetApprovalAsync()
    {
        var setting = await _management.ProposeModeAsync(OrgAdmin, FieldDomain.FloorLayout, FieldManagementMode.Approval, null, Ct);
        await _management.DecideModeAsync(_db.HrUser("hr2"), setting.Id, true, Ct);
    }

    [Fact(DisplayName = "配置図: 施設管理者は承認なしで登録でき、有効日から切り替わる（新しい階も作る）。有効日の前なら取り消して元の配置図に戻せる")]
    public async Task RegisterLayout_AppliesWithoutApprovalAndWithdraws()
    {
        Assert.Equal(FieldManagementMode.Field, (await _management.ModesAsync(Facility1, Ct))[FieldDomain.FloorLayout]);
        var date = Today.AddDays(7);

        var registration = await _service.RegisterLayoutAsync(
            Facility1,
            Input(date, ("3F", 30, [("A01", 2000), ("A02", 4000), ("A03", 6000)], "3F 西"), ("4F", 40, [("B01", 2000)], null)),
            Ct);

        Assert.Equal((FieldRegistrationKind.Layout, FieldRegistrationStatus.Applied), (registration.Kind, registration.Status));
        Assert.Equal(["A01", "A02"], await SeatsOnAsync("3F", Today));
        Assert.Equal(["A01", "A02", "A03"], await SeatsOnAsync("3F", date));
        Assert.Equal(["B01"], await SeatsOnAsync("4F", date));
        Assert.Equal(["A01", "A02", "A03"], await SeatsOnAsync("3F", date, Facility2));

        // 使えない席の印を省いた席は、今の値を引き継ぐ
        var outOfService = await _db.QueryAsync<string>(
            null,
            """
            SELECT s.code FROM floor_layout l JOIN layout_seat ls ON ls.layout_id = l.id JOIN seat s ON s.id = ls.seat_id
            WHERE l.floor_id = $1 AND upper_inf(l.recorded_period) AND l.valid_period @> $2::date AND ls.out_of_service
            """,
            Ct,
            _floor,
            date);
        Assert.Equal(["A01"], outOfService);

        // 承認なしの改訂は、起案者 = 承認者で、書いた登録を持つ
        var rows = await _db.QueryAsync<bool>(
            null,
            "SELECT proposed_by_person_id = approved_by_person_id AND field_registration_id = $1 FROM floor_layout WHERE floor_id = $2 AND lower(valid_period) = $3",
            Ct,
            registration.Id,
            _floor,
            date);
        Assert.True(Assert.Single(rows));

        // 別の施設管理者も取り消せる。取り消すと 3F は元の配置図（席を含む）、4F の配置図は無くなる
        await _service.WithdrawAsync(Facility2, registration.Id, Ct);
        Assert.Equal(["A01", "A02"], await SeatsOnAsync("3F", date));
        Assert.Empty(await SeatsOnAsync("4F", date));
    }

    [Fact(DisplayName = "配置図: 承認ありにすると同意待ちになり、登録者は承認できず、別の施設管理者の承認で反映する")]
    public async Task RegisterLayout_RequiresApprovalWhenConfigured()
    {
        await SetApprovalAsync();

        var registration = await _service.RegisterLayoutAsync(Facility1, Input(Today, ("3F", 30, [("A01", 2000), ("A02", 4000), ("A05", 8000)], null)), Ct);

        Assert.Equal(FieldRegistrationStatus.PendingConsent, registration.Status);
        Assert.Equal(["A01", "A02"], await SeatsOnAsync("3F", Today));
        Assert.Equal(FieldManagementError.Forbidden, (await ErrorOfAsync(() => _service.ConsentAsync(Facility1, registration.Id, Ct))).Error);
        Assert.Equal(FieldManagementError.NotFound, (await ErrorOfAsync(() => _service.ConsentAsync(_db.User("member"), registration.Id, Ct))).Error);

        // 承認した人が改訂の承認者になる
        var approved = await _service.ConsentAsync(Facility2, registration.Id, Ct);
        Assert.Equal(FieldRegistrationStatus.Applied, approved.Status);
        Assert.Equal(["A01", "A02", "A05"], await SeatsOnAsync("3F", Today));
        var approvers = await _db.QueryAsync<Guid>(
            null,
            "SELECT approved_by_person_id FROM floor_layout WHERE field_registration_id = $1 AND upper_inf(recorded_period)",
            Ct,
            registration.Id);
        Assert.Equal(_db.Id("head"), Assert.Single(approvers));
    }

    [Fact(DisplayName = "配置図: 承認ありでは、別の人の承認を経ない反映を DB が止める")]
    public async Task Database_RejectsSelfApprovedLayoutRegistration()
    {
        await SetApprovalAsync();
        var registration = await _service.RegisterLayoutAsync(Facility1, Input(Today, ("3F", 30, [("A01", 2000)], null)), Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            Facility1,
            "UPDATE field_registration SET status = 'applied', applied_by_person_id = $2, applied_at = now() WHERE id = $1",
            Ct,
            registration.Id,
            _db.Id("takahashi")));
        Assert.Equal("field_registration_layout_approval", ex.ConstraintName);
    }

    [Fact(DisplayName = "配置図: 施設管理者・人事管理者でなければ登録できず、形の誤り・保持しない情報・今と同じ配置図は登録しない")]
    public async Task RegisterLayout_RejectsInvalid()
    {
        var valid = Input(Today, ("3F", 30, [("A01", 2000), ("A09", 6000)], null));
        Assert.Equal(FieldManagementError.Forbidden, (await ErrorOfAsync(() => _service.RegisterLayoutAsync(OrgAdmin, valid, Ct))).Error);
        Assert.Equal(FieldManagementError.Forbidden, (await ErrorOfAsync(() => _service.RegisterLayoutAsync(_db.User("head"), valid, Ct))).Error);

        var outside = await ErrorOfAsync(() => _service.RegisterLayoutAsync(Facility1, Input(Today, ("3F", 30, [("A01", 2000), ("A09", 30000)], null)), Ct));
        Assert.Equal(FieldManagementError.Invalid, outside.Error);
        Assert.Contains("MAIN 3F 席 A09", outside.Message, StringComparison.Ordinal);

        var sensitive = await ErrorOfAsync(() => _service.RegisterLayoutAsync(Facility1, Input(Today, ("3F", 30, [("A01", 2000)], "090-1234-5678")), Ct));
        Assert.Equal(FieldManagementError.Invalid, sensitive.Error);

        var same = await ErrorOfAsync(() => _service.RegisterLayoutAsync(Facility1, Input(Today, ("3F", 30, [("A01", 2000), ("A02", 4000)], null)), Ct));
        Assert.Equal(FieldManagementError.Invalid, same.Error);

        var format = await ErrorOfAsync(() => _service.RegisterLayoutAsync(Facility1, new LayoutRegistrationInput(Today, null, "{}"u8.ToArray()), Ct));
        Assert.Equal(FieldManagementError.Invalid, format.Error);

        // 配置図の管理元は field か approval だけ
        Assert.Equal(
            FieldManagementError.Invalid,
            (await ErrorOfAsync(() => _management.ProposeModeAsync(OrgAdmin, FieldDomain.FloorLayout, FieldManagementMode.Hr, null, Ct))).Error);
    }

    [Fact(DisplayName = "配置図: 施設管理者のロールで広がる書き込みは配置図の表だけ（人の席・所属は書けない）")]
    public async Task FacilityAdmin_CannotWritePeople()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => _db.ExecuteAsync(
            Facility1,
            "INSERT INTO workplace_assignment (id, person_id, floor_id, role, exclusive, valid_period) VALUES ($1, $2, $3, 'primary', false, daterange($4, NULL))",
            Ct,
            EntityId.New(),
            _db.Id("member"),
            _floor,
            Today));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    private async Task SeedAsync()
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
            $"""
            INSERT INTO floor_layout (id, floor_id, outline, proposed_by_person_id, approved_by_person_id, valid_period)
            VALUES ($1, $2, '{Outline}', $3, $4, daterange($5, NULL))
            """,
            Ct,
            layout,
            _floor,
            _db.Id("hr1"),
            _db.Id("hr2"),
            IngestDatabase.LongAgo);
        foreach (var (code, x) in new[] { ("A01", 2000), ("A02", 4000) })
        {
            var seat = EntityId.New();
            await _db.ExecuteAsync(hr, "INSERT INTO seat (id, floor_id, code) VALUES ($1, $2, $3)", Ct, seat, _floor, code);
            await _db.ExecuteAsync(
                hr,
                "INSERT INTO layout_seat (layout_id, floor_id, seat_id, x_mm, y_mm, desk_type, seat_kind, out_of_service) VALUES ($1, $2, $3, $4, 2000, 'desk_1200', 'fixed', $5)",
                Ct,
                layout,
                _floor,
                seat,
                x,
                code == "A01");
            if (code == "A02")
            {
                await _db.ExecuteAsync(
                    hr,
                    "INSERT INTO workplace_assignment (id, person_id, floor_id, seat_id, role, exclusive, valid_period) VALUES ($1, $2, $3, $4, 'primary', true, daterange($5, NULL))",
                    Ct,
                    EntityId.New(),
                    _db.Id("member"),
                    _floor,
                    seat,
                    IngestDatabase.LongAgo);
            }
        }
    }
}
