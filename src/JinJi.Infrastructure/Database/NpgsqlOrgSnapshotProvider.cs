using System.Data;
using JinJi.Application;
using JinJi.Domain.Organization;
using JinJi.Domain.Workplace;
using JinJi.Policy;
using Npgsql;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 組織ツリーのスナップショットを PostgreSQL から読み込む。閲覧者を RLS に渡し、読み取り専用・REPEATABLE READ の 1 トランザクションで読む。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>拠点: 現在記録中で、有効期間が as_of を含む版</item>
/// <item>段の定義: 承認済みの定義（時点指定に関わらず今の定義。段の定義は二つの時間軸を持たない）</item>
/// <item>組織・所属・一時停止: 現在記録中で、有効期間が as_of を含む行（所属・一時停止は有効期間も読む。終了日を誰に返すかは投影が決める）</item>
/// <item>人: 属性の基準日に有効な版があり、入社年月日が as_of 以前の人</item>
/// <item>氏名の履歴・発令: 現在記録中のすべての行（RLS により、人事管理者以外は今日有効な版・発令日到来済みの発令だけになる）</item>
/// <item>建物・階・配置図の改訂（ゾーン・席の置き場所）・人の席・組織のエリア: 現在記録中で、有効期間が as_of を含む行（0019。RLS により、
/// 人事管理者以外には将来の版・改訂・割り当てが現れない）</item>
/// </list>
/// </remarks>
public sealed class NpgsqlOrgSnapshotProvider : IOrgSnapshotProvider
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlOrgSnapshotProvider(NpgsqlDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
    }

    public async Task<OrgSnapshot> LoadAsync(ViewerContext viewer, DateOnly asOf, DateOnly attributesAsOf, CancellationToken cancellationToken)
    {
        await using var tx = await ViewerTransaction.BeginAsync(_dataSource, viewer, cancellationToken, IsolationLevel.RepeatableRead, readOnly: true);

        var legalEntities = await ReadAllAsync(tx, "SELECT id, code, name FROM legal_entity", [], r =>
            new LegalEntity(r.GetGuid(0), r.GetString(1), r.GetString(2)), cancellationToken);

        var units = await ReadAllAsync(
            tx,
            """
            SELECT org_unit_id, legal_entity_id, code, name, short_name, level_key, parent_org_unit_id, head_person_id, site_id
            FROM org_unit_version
            WHERE upper_inf(recorded_period) AND valid_period @> $1
            """,
            [asOf],
            r => new OrgUnit(
                r.GetGuid(0),
                r.GetGuid(1),
                r.GetString(2),
                r.GetString(3),
                NullableString(r, 4),
                NullableString(r, 5),
                NullableGuid(r, 6),
                NullableGuid(r, 7)) { SiteId = NullableGuid(r, 8) },
            cancellationToken);

        // 拠点: 現在記録中で、有効期間が as_of を含む版（RLS により、人事管理者以外には開設・変更の予定の版が現れない）
        var sites = await ReadAllAsync(
            tx,
            """
            SELECT site_id, legal_entity_id, code, name, site_type, prefecture_code, location
            FROM site_version
            WHERE upper_inf(recorded_period) AND valid_period @> $1
            """,
            [asOf],
            r => new Site(
                r.GetGuid(0),
                r.GetGuid(1),
                r.GetString(2),
                r.GetString(3),
                SiteCodes.TryParse(r.GetString(4), out var type) ? type : throw new InvalidOperationException($"DB に未知の拠点の種別があります: {r.GetString(4)}"),
                NullableString(r, 5),
                NullableString(r, 6)),
            cancellationToken);

        var versions = await ReadAllAsync(
            tx,
            """
            SELECT person_id, id, lower(valid_period), upper(valid_period),
                   family_name, given_name, family_name_kana, given_name_kana, family_name_latin, given_name_latin, display_name,
                   work_email, extension_number, work_mobile_phone, site_id, remote_work, employment_type, job_type, hire_date, termination_date
            FROM person_version
            WHERE upper_inf(recorded_period)
            ORDER BY person_id, lower(valid_period)
            """,
            [],
            r => (PersonId: r.GetGuid(0), Version: ReadVersion(r)),
            cancellationToken);

        var actions = await ReadAllAsync(
            tx,
            """
            SELECT id, person_id, action_type, effective_date, org_unit_id, assignment_type, position_title, document_number
            FROM personnel_action
            WHERE upper_inf(recorded_period)
            ORDER BY person_id, effective_date, id
            """,
            [],
            r => new PersonnelActionRecord(
                r.GetGuid(0),
                r.GetGuid(1),
                DbCodes.ParseActionType(r.GetString(2)),
                r.GetFieldValue<DateOnly>(3),
                NullableGuid(r, 4),
                r.IsDBNull(5) ? null : DbCodes.ParseAssignmentType(r.GetString(5)),
                NullableString(r, 6),
                NullableString(r, 7)),
            cancellationToken);

        // 現在記録中の一時停止は人ごとに期間が重ならない（排他制約）ので、時点を含む行は人ごとに高々 1 行
        var suspensions = (await ReadAllAsync(
            tx,
            "SELECT person_id, lower(valid_period), upper(valid_period) FROM account_suspension WHERE upper_inf(recorded_period) AND valid_period @> $1",
            [asOf],
            r => (PersonId: r.GetGuid(0), Period: new SuspensionPeriod(r.GetFieldValue<DateOnly>(1), NullableDate(r, 2))),
            cancellationToken)).ToDictionary(s => s.PersonId, s => s.Period);

        var assignments = await ReadAllAsync(
            tx,
            """
            SELECT person_id, org_unit_id, assignment_type, position_title, secondment_type, lower(valid_period), upper(valid_period)
            FROM assignment
            WHERE upper_inf(recorded_period) AND valid_period @> $1
            """,
            [asOf],
            r => new Assignment(
                r.GetGuid(0),
                r.GetGuid(1),
                DbCodes.ParseAssignmentType(r.GetString(2)),
                NullableString(r, 3),
                DbCodes.ParseSecondmentType(r.GetString(4)),
                r.GetFieldValue<DateOnly>(5),
                NullableDate(r, 6)),
            cancellationToken);

        // 段の定義: 有効な（承認済みの）定義。範囲（法人・既定）ごとに高々 1 つ（0004 の排他制約）
        var levels = await ReadAllAsync(
            tx,
            """
            SELECT s.id, s.legal_entity_id, l.level_key, l.name, l.rank
            FROM org_level_scheme s JOIN org_level l ON l.scheme_id = s.id
            WHERE s.status = 'approved'
            ORDER BY s.id, l.rank
            """,
            [],
            r => (SchemeId: r.GetGuid(0), LegalEntityId: NullableGuid(r, 1), Level: new OrgLevel(r.GetString(2), r.GetString(3), r.GetInt32(4))),
            cancellationToken);
        var schemes = levels
            .GroupBy(l => (l.SchemeId, l.LegalEntityId))
            .Select(g => new OrgLevelScheme(g.Key.LegalEntityId, g.Select(l => l.Level)))
            .ToList();

        var people = await ReadAllAsync(tx, "SELECT id, legal_entity_id, employee_number FROM person", [], r =>
            (Id: r.GetGuid(0), LegalEntityId: r.GetGuid(1), EmployeeNumber: r.GetString(2)), cancellationToken);

        var workplace = await LoadWorkplaceAsync(tx, asOf, cancellationToken);

        await tx.CommitAsync(cancellationToken);

        var versionsByPerson = versions.ToLookup(v => v.PersonId, v => v.Version);
        var actionsByPerson = actions.ToLookup(a => a.PersonId);
        var records = new List<PersonRecord>();
        foreach (var person in people)
        {
            var history = versionsByPerson[person.Id].ToList();
            if (history.FirstOrDefault(v => v.IsValidOn(attributesAsOf)) is not { } current || current.HireDate > asOf)
            {
                continue;
            }

            records.Add(new PersonRecord
            {
                Id = person.Id,
                LegalEntityId = person.LegalEntityId,
                EmployeeNumber = person.EmployeeNumber,
                Current = current,
                History = history,
                Suspension = suspensions.GetValueOrDefault(person.Id),
                Actions = [.. actionsByPerson[person.Id]],
            });
        }

        return OrgSnapshot.Create(asOf, legalEntities, units, records, assignments, schemes, sites, workplace);
    }

    /// <summary>その時点の建物・階・配置図・人の席・組織のエリア（同じトランザクションで読む）。</summary>
    private static async Task<WorkplaceMap> LoadWorkplaceAsync(ViewerTransaction tx, DateOnly asOf, CancellationToken cancellationToken)
    {
        object[] date = [asOf];
        var buildings = await ReadAllAsync(
            tx,
            "SELECT building_id, site_id, code, name, sort_order FROM building_version WHERE upper_inf(recorded_period) AND valid_period @> $1",
            date,
            r => new Building(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.GetInt16(4)),
            cancellationToken);
        if (buildings.Count == 0)
        {
            return WorkplaceMap.Empty;
        }

        var floors = await ReadAllAsync(
            tx,
            "SELECT floor_id, building_id, label, level_order, floor_kind FROM floor_version WHERE upper_inf(recorded_period) AND valid_period @> $1",
            date,
            r => new Floor(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetInt32(3), WorkplaceCodes.Parse<FloorKind>(r.GetString(4))),
            cancellationToken);

        var zones = (await ReadAllAsync(
            tx,
            """
            SELECT z.layout_id, z.zone_key, z.name, z.zone_kind, z.polygon::text
            FROM layout_zone z JOIN floor_layout l ON l.id = z.layout_id
            WHERE upper_inf(l.recorded_period) AND l.valid_period @> $1
            """,
            date,
            r => (LayoutId: r.GetGuid(0), Zone: new LayoutZone(r.GetGuid(1), r.GetString(2), WorkplaceCodes.Parse<ZoneKind>(r.GetString(3)), LayoutJson.ParsePolygon(r.GetString(4)))),
            cancellationToken)).ToLookup(z => z.LayoutId, z => z.Zone);

        var seats = (await ReadAllAsync(
            tx,
            """
            SELECT s.layout_id, s.seat_id, t.code, s.zone_key, s.x_mm, s.y_mm, s.rotation_deg, s.desk_type, s.seat_kind, s.capacity, s.features, s.out_of_service
            FROM layout_seat s
            JOIN floor_layout l ON l.id = s.layout_id
            JOIN seat t ON t.id = s.seat_id
            WHERE upper_inf(l.recorded_period) AND l.valid_period @> $1
            ORDER BY t.code
            """,
            date,
            r => (LayoutId: r.GetGuid(0), Seat: new LayoutSeat(
                r.GetGuid(1),
                r.GetString(2),
                NullableGuid(r, 3),
                r.GetInt32(4),
                r.GetInt32(5),
                r.GetInt16(6),
                r.GetString(7),
                WorkplaceCodes.Parse<SeatKind>(r.GetString(8)),
                r.GetInt16(9),
                r.GetFieldValue<string[]>(10),
                r.GetBoolean(11))),
            cancellationToken)).ToLookup(s => s.LayoutId, s => s.Seat);

        var layouts = await ReadAllAsync(
            tx,
            """
            SELECT id, floor_id, lower(valid_period), upper(valid_period), grid_mm, outline::text, fixtures::text
            FROM floor_layout WHERE upper_inf(recorded_period) AND valid_period @> $1
            """,
            date,
            r => new FloorLayout(
                r.GetGuid(0),
                r.GetGuid(1),
                r.GetFieldValue<DateOnly>(2),
                NullableDate(r, 3),
                r.GetInt32(4),
                LayoutJson.ParsePolygon(r.GetString(5)),
                LayoutJson.ParseFixtures(r.GetString(6)),
                [],
                []),
            cancellationToken);
        layouts = [.. layouts.Select(l => l with { Zones = [.. zones[l.Id]], Seats = [.. seats[l.Id]] })];

        var placements = await ReadAllAsync(
            tx,
            """
            SELECT id, person_id, floor_id, seat_id, zone_key, role, exclusive, lower(valid_period), upper(valid_period)
            FROM workplace_assignment WHERE upper_inf(recorded_period) AND valid_period @> $1
            """,
            date,
            r => new WorkplaceAssignment(
                r.GetGuid(0),
                r.GetGuid(1),
                r.GetGuid(2),
                NullableGuid(r, 3),
                NullableGuid(r, 4),
                WorkplaceCodes.Parse<WorkplaceRole>(r.GetString(5)),
                r.GetBoolean(6),
                r.GetFieldValue<DateOnly>(7),
                NullableDate(r, 8)),
            cancellationToken);

        return WorkplaceMap.Create(buildings, floors, layouts, placements);
    }

    private static PersonVersion ReadVersion(NpgsqlDataReader r) => new()
    {
        VersionId = r.GetGuid(1),
        ValidFrom = r.GetFieldValue<DateOnly>(2),
        ValidUntil = NullableDate(r, 3),
        FamilyName = r.GetString(4),
        GivenName = NullableString(r, 5),
        FamilyNameKana = NullableString(r, 6),
        GivenNameKana = NullableString(r, 7),
        FamilyNameLatin = NullableString(r, 8),
        GivenNameLatin = NullableString(r, 9),
        DisplayName = r.GetString(10),
        WorkEmail = NullableString(r, 11),
        ExtensionNumber = NullableString(r, 12),
        WorkMobilePhone = NullableString(r, 13),
        SiteId = NullableGuid(r, 14),
        RemoteWork = r.GetBoolean(15),
        EmploymentType = r.GetString(16),
        JobType = NullableString(r, 17),
        HireDate = r.GetFieldValue<DateOnly>(18),
        TerminationDate = NullableDate(r, 19),
    };

    private static string? NullableString(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetString(ordinal);

    private static DateOnly? NullableDate(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetFieldValue<DateOnly>(ordinal);

    private static Guid? NullableGuid(NpgsqlDataReader r, int ordinal) => r.IsDBNull(ordinal) ? null : r.GetGuid(ordinal);

    private static async Task<List<T>> ReadAllAsync<T>(
        ViewerTransaction tx,
        string sql,
        object[] parameters,
        Func<NpgsqlDataReader, T> map,
        CancellationToken cancellationToken)
    {
        await using var command = tx.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(map(reader));
        }

        return rows;
    }
}
