using JinJi.Application.Ingest;
using JinJi.Domain.Workplace;
using NpgsqlTypes;

namespace JinJi.Infrastructure.Database;

/// <summary>建物・階・配置図の改訂・人の席・持ち場の提案の反映（0019）。</summary>
public sealed partial class NpgsqlIngestStore
{
    private static readonly Column[] BuildingVersionColumns =
    [
        new("building_id", null),
        new("site_id", null),
        new("code", null),
        new("name", "text"),
        new("sort_order", "smallint"),
    ];

    private static readonly Column[] FloorVersionColumns =
    [
        new("floor_id", null),
        new("building_id", null),
        new("label", "text"),
        new("level_order", "integer"),
        new("floor_kind", "text"),
    ];

    private static readonly Column[] WorkplaceAssignmentColumns =
    [
        new("person_id", null),
        new("floor_id", "uuid"),
        new("seat_id", "uuid"),
        new("zone_key", "uuid"),
        new("role", null),
        new("exclusive", "boolean"),
    ];

    /// <summary>建物・フロアモードの提案を反映する。対象の種類でなければ false。</summary>
    private static async Task<bool> ApplyWorkplaceAsync(ViewerTransaction tx, Guid target, ChangeProposalRecord proposal, Guid approver, DateOnly today, CancellationToken cancellationToken)
    {
        switch (proposal.TargetKind, proposal.Operation)
        {
            case (ProposalTargetKind.Building, ProposalOperation.Create):
                var site = Uuid(Required(proposal.Payload, ProposalFields.SiteId));
                await ExecuteAsync(tx, "INSERT INTO building (id, site_id) VALUES ($1, $2)", cancellationToken, P(target), P(site));
                await ExecuteAsync(
                    tx,
                    "INSERT INTO building_version (id, building_id, site_id, code, name, sort_order, valid_period) VALUES ($1, $2, $3, $4, $5, $6, daterange($7, NULL))",
                    cancellationToken,
                    P(Guid.CreateVersion7()),
                    P(target),
                    P(site),
                    P(Required(proposal.Payload, ProposalFields.Code)),
                    P(Required(proposal.Payload, ProposalFields.Name)),
                    P(short.Parse(proposal.Payload.GetValueOrDefault(ProposalFields.SortOrder) ?? "0", System.Globalization.CultureInfo.InvariantCulture)),
                    P(Date(Required(proposal.Payload, ProposalFields.ValidFrom))));
                return true;
            case (ProposalTargetKind.BuildingVersion, ProposalOperation.Correct or ProposalOperation.Change):
                await ReviseAsync(tx, "building_version", BuildingVersionColumns, "building_id = $1", target, proposal, today, new Dictionary<string, string?>(), cancellationToken);
                return true;
            case (ProposalTargetKind.Floor, ProposalOperation.Create):
                var building = Uuid(Required(proposal.Payload, ProposalFields.BuildingId));
                await ExecuteAsync(tx, "INSERT INTO floor (id, building_id) VALUES ($1, $2)", cancellationToken, P(target), P(building));
                await ExecuteAsync(
                    tx,
                    "INSERT INTO floor_version (id, floor_id, building_id, label, level_order, floor_kind, valid_period) VALUES ($1, $2, $3, $4, $5, $6, daterange($7, NULL))",
                    cancellationToken,
                    P(Guid.CreateVersion7()),
                    P(target),
                    P(building),
                    P(Required(proposal.Payload, ProposalFields.Label)),
                    P(int.Parse(Required(proposal.Payload, ProposalFields.LevelOrder), System.Globalization.CultureInfo.InvariantCulture)),
                    P(Required(proposal.Payload, ProposalFields.FloorKind)),
                    P(Date(Required(proposal.Payload, ProposalFields.ValidFrom))));
                return true;
            case (ProposalTargetKind.FloorVersion, ProposalOperation.Correct or ProposalOperation.Change):
                await ReviseAsync(tx, "floor_version", FloorVersionColumns, "floor_id = $1", target, proposal, today, new Dictionary<string, string?>(), cancellationToken);
                return true;
            case (ProposalTargetKind.FloorLayout, ProposalOperation.Create):
                await ApplyLayoutAsync(tx, target, proposal, approver, today, cancellationToken);
                return true;
            case (ProposalTargetKind.WorkplaceAssignment, ProposalOperation.Create):
                await ExecuteAsync(
                    tx,
                    """
                    INSERT INTO workplace_assignment (id, person_id, floor_id, seat_id, zone_key, role, exclusive, valid_period)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, daterange($8, NULL))
                    """,
                    cancellationToken,
                    P(Guid.CreateVersion7()),
                    P(target),
                    P(Uuid(Required(proposal.Payload, ProposalFields.FloorId))),
                    P(proposal.Payload.GetValueOrDefault(ProposalFields.SeatId) is { } seat ? Uuid(seat) : null, NpgsqlDbType.Uuid),
                    P(proposal.Payload.GetValueOrDefault(ProposalFields.ZoneKey) is { } zone ? Uuid(zone) : null, NpgsqlDbType.Uuid),
                    P(Required(proposal.Payload, ProposalFields.Role)),
                    P(proposal.Payload.GetValueOrDefault(ProposalFields.Exclusive) == "true"),
                    P(proposal.EffectiveDate ?? today));
                return true;
            case (ProposalTargetKind.WorkplaceAssignment, ProposalOperation.Correct or ProposalOperation.Change):
                await ReviseAsync(tx, "workplace_assignment", WorkplaceAssignmentColumns, "person_id = $1 AND role = 'primary'", target, proposal, today, new Dictionary<string, string?>(), cancellationToken);
                return true;
            case (ProposalTargetKind.WorkplaceAssignment, ProposalOperation.End):
                await EndRowsAsync(
                    tx, "workplace_assignment", WorkplaceAssignmentColumns, "person_id = $1 AND role = $2",
                    [P(target), P(Required(proposal.Payload, ProposalFields.Role))], Required(proposal), cancellationToken);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 配置図の改訂を反映する: 有効日を含む今の改訂は有効日の前日までに区切り（記録期間を閉じて写しの改訂を足す）、有効日に始まる改訂は置き換え、
    /// 有効日より後の改訂はそのまま残して、新しい改訂の有効期間をその前日までにする。新しい席番号の席はここで作る（同じ席番号の席があればそれを使う）。
    /// </summary>
    private static async Task ApplyLayoutAsync(ViewerTransaction tx, Guid floorId, ChangeProposalRecord proposal, Guid approver, DateOnly today, CancellationToken cancellationToken)
    {
        var draft = LayoutJson.ParseDraft(Required(proposal.Payload, ProposalFields.Layout));
        var from = proposal.EffectiveDate ?? today;

        var existing = new List<(Guid Id, DateOnly From, DateOnly? Until)>();
        await using (var select = tx.CreateCommand(
            """
            SELECT id, lower(valid_period), upper(valid_period) FROM floor_layout
            WHERE floor_id = $1 AND upper_inf(recorded_period) AND (upper_inf(valid_period) OR upper(valid_period) > $2::date)
            ORDER BY lower(valid_period)
            FOR UPDATE
            """))
        {
            select.Parameters.Add(P(floorId));
            select.Parameters.Add(P(from));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add((reader.GetGuid(0), reader.GetFieldValue<DateOnly>(1), reader.IsDBNull(2) ? null : reader.GetFieldValue<DateOnly>(2)));
            }
        }

        DateOnly? until = existing.Where(e => e.From > from).Select(e => (DateOnly?)e.From).Min();
        foreach (var (id, start, _) in existing.Where(e => e.From <= from))
        {
            await ExecuteAsync(tx, "UPDATE floor_layout SET recorded_period = tstzrange(lower(recorded_period), now()) WHERE id = $1", cancellationToken, P(id));
            if (start < from)
            {
                // 区切った写しの改訂（中身・起案者・承認者は元の改訂のまま）
                var copy = Guid.CreateVersion7();
                await ExecuteAsync(
                    tx,
                    """
                    INSERT INTO floor_layout (id, floor_id, outline, fixtures, grid_mm, proposed_by_person_id, approved_by_person_id, valid_period)
                    SELECT $1, floor_id, outline, fixtures, grid_mm, proposed_by_person_id, approved_by_person_id, daterange(lower(valid_period), $3) FROM floor_layout WHERE id = $2
                    """,
                    cancellationToken,
                    P(copy),
                    P(id),
                    P(from));
                await ExecuteAsync(tx, "INSERT INTO layout_zone (layout_id, zone_key, name, zone_kind, polygon) SELECT $1, zone_key, name, zone_kind, polygon FROM layout_zone WHERE layout_id = $2", cancellationToken, P(copy), P(id));
                await ExecuteAsync(
                    tx,
                    """
                    INSERT INTO layout_seat (layout_id, floor_id, seat_id, zone_key, x_mm, y_mm, rotation_deg, desk_type, seat_kind, capacity, features, out_of_service)
                    SELECT $1, floor_id, seat_id, zone_key, x_mm, y_mm, rotation_deg, desk_type, seat_kind, capacity, features, out_of_service FROM layout_seat WHERE layout_id = $2
                    """,
                    cancellationToken,
                    P(copy),
                    P(id));
            }
        }

        var layoutId = Guid.CreateVersion7();
        await ExecuteAsync(
            tx,
            """
            INSERT INTO floor_layout (id, floor_id, outline, fixtures, grid_mm, proposed_by_person_id, approved_by_person_id, valid_period)
            VALUES ($1, $2, $3::jsonb, $4::jsonb, $5, $6, $7, daterange($8, $9))
            """,
            cancellationToken,
            P(layoutId),
            P(floorId),
            P(LayoutJson.WritePolygon(draft.Outline)),
            P(LayoutJson.WriteFixtures(draft.Fixtures)),
            P(draft.GridMm),
            P(proposal.ProposedByPersonId),
            P(approver),
            P(from),
            P(until, NpgsqlDbType.Date));

        foreach (var zone in draft.Zones)
        {
            await ExecuteAsync(
                tx,
                "INSERT INTO layout_zone (layout_id, zone_key, name, zone_kind, polygon) VALUES ($1, $2, $3, $4, $5::jsonb)",
                cancellationToken,
                P(layoutId),
                P(zone.ZoneKey),
                P(zone.Name),
                P(WorkplaceCodes.ToCode(zone.Kind)),
                P(LayoutJson.WritePolygon(zone.Polygon)));
        }

        foreach (var seat in draft.Seats)
        {
            // 席の不変 ID: 同じ階・同じ席番号の席があればそれ（廃止した席番号の再利用を含む）、無ければ作る
            var p = seat.Placement;
            await ExecuteAsync(
                tx,
                "INSERT INTO seat (id, floor_id, code) VALUES ($1, $2, $3) ON CONFLICT (floor_id, code) DO NOTHING",
                cancellationToken,
                P(seat.SeatId),
                P(floorId),
                P(p.Code));
            await ExecuteAsync(
                tx,
                """
                INSERT INTO layout_seat (layout_id, floor_id, seat_id, zone_key, x_mm, y_mm, rotation_deg, desk_type, seat_kind, capacity, features, out_of_service)
                SELECT $1, $2, s.id, $4, $5, $6, $7, $8, $9, $10, $11, $12 FROM seat s WHERE s.floor_id = $2 AND s.code = $3
                """,
                cancellationToken,
                P(layoutId),
                P(floorId),
                P(p.Code),
                P(p.ZoneKey, NpgsqlDbType.Uuid),
                P(p.XMm),
                P(p.YMm),
                P((short)p.RotationDeg),
                P(p.DeskType),
                P(WorkplaceCodes.ToCode(p.Kind)),
                P((short)p.Capacity),
                P(p.Features.ToArray()),
                P(p.OutOfService));
        }
    }
}
