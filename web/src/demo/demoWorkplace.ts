// デモの建物・フロアモードの取得元（WorkplaceAdapter）。API の規則（仕様 §3・§6）を、デモの閲覧者から見た場合に限って簡略に真似る。
// - 席の状態は閲覧者から見た 3 つ（人・空席・使えない席）。席（項目 seat）が見えない人の席は「使えない席」にする
// - 今日の所在は、外部から届いたサンプル（今日だけ）。見えない人の所在で埋まった席は「使えない席」、所在が無いことには意味を持たせない
// - 空席は「割り当ての無い席」の意味だけ。タイムマシンで見た日の割り当てが、その日の所在
import { inPeriod } from "../lib/date";
import {
  ApiError,
  type Building,
  type FloorLayout,
  type FloorPresence,
  type FloorOccupant,
  type FloorSummary,
  type PersonPresence,
  type PersonSeat,
  type PersonWorkplace,
  type SeatStatus,
  type WorkplaceAdapter,
} from "../data/types";
import type { DemoDirectory, PersonField } from "./directory";
import type { DemoPerson } from "./model";
import type { DemoFloor, DemoSeat, DemoSeatAssignment, DemoWorkplace, DemoZone } from "./workplace";

interface SeatInfo {
  seat: DemoSeat;
  floor: DemoFloor;
  zone: DemoZone | undefined;
}

interface SeatState {
  status: SeatStatus;
  /** 閲覧者に見える人の割り当て。 */
  visible: DemoSeatAssignment[];
}

export class DemoWorkplaceService {
  readonly wp: DemoWorkplace;
  private readonly dir: DemoDirectory;
  private readonly today: string;
  private readonly floorById: Map<string, DemoFloor>;
  private readonly seatById = new Map<string, SeatInfo>();
  private readonly bySeat = new Map<string, DemoSeatAssignment[]>();
  private readonly byPerson = new Map<string, DemoSeatAssignment[]>();
  private readonly personById: Map<string, DemoPerson>;
  private readonly stateCache = new Map<string, Map<string, SeatState>>();

  constructor(dir: DemoDirectory, wp: DemoWorkplace) {
    this.dir = dir;
    this.wp = wp;
    this.today = dir.businessDate;
    this.floorById = new Map(wp.floors.map((f) => [f.id, f]));
    this.personById = new Map(dir.data.people.map((p) => [p.id, p]));
    for (const layout of wp.layouts.values()) {
      const floor = this.floorById.get(layout.floor_id)!;
      const zones = new Map(layout.zones.map((z) => [z.zone_key, z]));
      for (const seat of layout.seats) this.seatById.set(seat.seat_id, { seat, floor, zone: seat.zone_key ? zones.get(seat.zone_key) : undefined });
    }
    for (const a of wp.assignments) {
      if (a.seat_id) this.bySeat.set(a.seat_id, [...(this.bySeat.get(a.seat_id) ?? []), a]);
      this.byPerson.set(a.person_id, [...(this.byPerson.get(a.person_id) ?? []), a]);
    }
  }

  private visible(person: DemoPerson | undefined, field: PersonField): boolean {
    return person !== undefined && this.dir.isListed(person) && this.dir.visibleFields(person).includes(field);
  }

  /** その日の席の状態（閲覧者から見た 3 つ）。 */
  private states(floorId: string, asOf: string): Map<string, SeatState> {
    const key = `${floorId}|${asOf}`;
    const hit = this.stateCache.get(key);
    if (hit) return hit;
    const out = new Map<string, SeatState>();
    for (const seat of this.wp.layouts.get(floorId)?.seats ?? []) {
      const active = (this.bySeat.get(seat.seat_id) ?? []).filter((a) => inPeriod(asOf, a.valid_from, a.valid_until));
      const visible = active.filter((a) => this.visible(this.personById.get(a.person_id), "seat"));
      // 見えない人の席は「使えない席」（保守中・予備と区別しない）
      const status: SeatStatus = seat.out_of_service ? "unavailable" : visible.length > 0 ? "occupied" : active.length > 0 ? "unavailable" : "vacant";
      out.set(seat.seat_id, { status, visible });
    }
    if (this.stateCache.size > 200) this.stateCache.clear();
    this.stateCache.set(key, out);
    return out;
  }

  private summary(floor: DemoFloor, asOf: string): FloorSummary {
    const states = this.states(floor.id, asOf);
    const layout = this.wp.layouts.get(floor.id);
    let occupied = 0;
    let vacant = 0;
    let unavailable = 0;
    for (const s of states.values()) {
      if (s.status === "occupied") occupied++;
      else if (s.status === "vacant") vacant++;
      else unavailable++;
    }
    return {
      id: floor.id,
      building_id: floor.building_id,
      label: floor.label,
      level_order: floor.level_order,
      kind: floor.kind,
      layout_id: layout?.layout_id ?? null,
      layout_valid_from: layout?.valid_from ?? null,
      seat_count: states.size,
      occupied_count: occupied,
      vacant_count: vacant,
      unavailable_count: unavailable,
    };
  }

  private building(id: string, asOf: string): Building {
    const b = this.wp.buildings.find((x) => x.id === id);
    if (!b || !this.dir.siteAt(b.site_id, asOf)) throw new ApiError(404, "見つかりません。", "urn:jinji:problem:not-found");
    const floors = this.wp.floors
      .filter((f) => f.building_id === id)
      .sort((x, y) => x.level_order - y.level_order)
      .map((f) => this.summary(f, asOf));
    const site = this.dir.siteAt(b.site_id, asOf);
    return { id: b.id, site_id: b.site_id, site_code: site?.code ?? null, site_name: site?.name ?? null, code: b.code, name: b.name, sort_order: b.sort_order, floors };
  }

  listBuildings(siteId: string, asOf: string): Building[] {
    this.dir.checkAsOf(asOf);
    if (!this.dir.siteAt(siteId, asOf)) throw new ApiError(404, "見つかりません。", "urn:jinji:problem:not-found");
    return this.wp.buildings
      .filter((b) => b.site_id === siteId)
      .sort((a, b) => a.sort_order - b.sort_order)
      .map((b) => this.building(b.id, asOf));
  }

  getBuilding(id: string, asOf: string): Building {
    this.dir.checkAsOf(asOf);
    return this.building(id, asOf);
  }

  private floor(id: string): DemoFloor {
    const f = this.floorById.get(id);
    if (!f) throw new ApiError(404, "見つかりません。", "urn:jinji:problem:not-found");
    return f;
  }

  getFloorLayout(floorId: string, asOf: string): FloorLayout {
    this.dir.checkAsOf(asOf);
    const floor = this.floor(floorId);
    const layout = this.wp.layouts.get(floor.id)!;
    const states = this.states(floor.id, asOf);
    return {
      floor_id: floor.id,
      as_of: asOf,
      layout_id: layout.layout_id,
      valid_from: layout.valid_from,
      outline: layout.outline,
      fixtures: layout.fixtures,
      grid_mm: layout.grid_mm,
      zones: layout.zones,
      seats: layout.seats.map((s) => ({
        seat_id: s.seat_id,
        code: s.code,
        zone_key: s.zone_key,
        x_mm: s.x_mm,
        y_mm: s.y_mm,
        rotation_deg: s.rotation_deg,
        desk_type: s.desk_type,
        seat_kind: s.seat_kind,
        capacity: s.capacity,
        features: s.features,
        status: states.get(s.seat_id)!.status,
      })),
    };
  }

  /** 主務の組織（その日）。 */
  private primaryOrg(person: DemoPerson, asOf: string): { id: string; name: string } | null {
    const a = this.dir.assignmentsAt(person, asOf).find((x) => x.type === "primary");
    const org = a ? this.dir.orgUnit(a.org_unit_id) : undefined;
    return org ? { id: org.id, name: org.name } : null;
  }

  listFloorOccupants(floorId: string, asOf: string): FloorOccupant[] {
    this.dir.checkAsOf(asOf);
    this.floor(floorId);
    const out: FloorOccupant[] = [];
    for (const state of this.states(floorId, asOf).values()) {
      for (const a of state.visible) {
        const person = this.personById.get(a.person_id)!;
        const org = this.primaryOrg(person, asOf);
        out.push({
          person_id: person.id,
          display_name: this.visible(person, "display_name") ? person.display_name : null,
          org_unit_id: org?.id ?? null,
          org_unit_name: org?.name ?? null,
          seat_id: a.seat_id,
          zone_key: a.zone_key,
          role: a.role,
        });
      }
    }
    return out;
  }

  // ---- 今日の所在（外部から届いたもの） ----

  getFloorPresence(floorId: string, date: string): FloorPresence {
    const floor = this.floor(floorId);
    // 今日だけ（過去・将来の日の所在は届かない）
    if (date !== this.today) throw new ApiError(400, "今日の所在だけを扱います。", "urn:jinji:problem:bad-request");
    const states = this.states(floor.id, this.today);
    const unavailable: string[] = [];
    const items: FloorPresence["items"] = [];
    for (const r of this.wp.presence.filter((x) => x.floor_id === floor.id)) {
      const person = this.personById.get(r.person_id);
      if (person && this.visible(person, "presence")) {
        const org = this.primaryOrg(person, this.today);
        // 所在の席番号は、項目 seat も見えるときだけ
        const seat = r.seat_id && this.visible(person, "seat") ? r.seat_id : null;
        items.push({
          person_id: person.id,
          display_name: this.visible(person, "display_name") ? person.display_name : null,
          org_unit_id: org?.id ?? null,
          org_unit_name: org?.name ?? null,
          seat_id: seat,
          zone_key: r.zone_key,
        });
        if (r.seat_id && !seat && states.get(r.seat_id)?.status === "vacant") unavailable.push(r.seat_id);
      } else if (r.seat_id && states.get(r.seat_id)?.status === "vacant") {
        unavailable.push(r.seat_id);
      }
    }
    return { floor_id: floor.id, date, unavailable_seat_ids: unavailable, items };
  }

  // ---- 人の項目（workplace・seat・presence） ----

  project(person: DemoPerson, asOf: string, visible: ReadonlySet<PersonField>): { workplace: PersonWorkplace | null; seat: PersonSeat | null; presence: PersonPresence | null } {
    const active = (this.byPerson.get(person.id) ?? []).find((a) => a.role === "primary" && inPeriod(asOf, a.valid_from, a.valid_until));
    let workplace: PersonWorkplace | null = null;
    let seat: PersonSeat | null = null;
    const buildingOf = (floor: DemoFloor) => this.wp.buildings.find((b) => b.id === floor.building_id)!;
    if (active?.seat_id) {
      const info = this.seatById.get(active.seat_id)!;
      const b = buildingOf(info.floor);
      workplace = {
        site_id: b.site_id,
        building_id: b.id,
        building_code: b.code,
        building_name: b.name,
        floor_id: info.floor.id,
        floor_label: info.floor.label,
        level_order: info.floor.level_order,
        zone_key: info.zone?.zone_key ?? null,
        zone_name: info.zone?.name ?? null,
        source: "seat",
      };
      seat = {
        seat_id: info.seat.seat_id,
        code: info.seat.code,
        building_id: b.id,
        building_name: b.name,
        floor_id: info.floor.id,
        floor_label: info.floor.label,
        zone_key: info.zone?.zone_key ?? null,
        zone_name: info.zone?.name ?? null,
        x_mm: info.seat.x_mm,
        y_mm: info.seat.y_mm,
        rotation_deg: info.seat.rotation_deg,
        kind: info.seat.seat_kind,
      };
    }
    let presence: PersonPresence | null = null;
    // 今日の所在は今日の時点だけ（タイムマシンの対象外）
    if (asOf === this.today && visible.has("presence")) {
      const r = this.wp.presence.find((x) => x.person_id === person.id);
      if (r) {
        const floor = this.floor(r.floor_id);
        const b = this.wp.buildings.find((x) => x.id === floor.building_id)!;
        const info = r.seat_id ? this.seatById.get(r.seat_id) : undefined;
        const zone = r.zone_key ? this.wp.layouts.get(floor.id)?.zones.find((z) => z.zone_key === r.zone_key) : undefined;
        const showSeat = visible.has("seat");
        presence = {
          date: this.today,
          building_id: b.id,
          building_name: b.name,
          floor_id: floor.id,
          floor_label: floor.label,
          zone_key: r.zone_key,
          zone_name: zone?.name ?? null,
          seat_id: showSeat ? r.seat_id : null,
          seat_code: showSeat ? (info?.seat.code ?? null) : null,
        };
      }
    }
    return { workplace, seat, presence };
  }
}

/** デモの WorkplaceAdapter（API と同じく非同期で返す）。 */
export function createDemoWorkplaceAdapter(service: DemoWorkplaceService, later: <T>(fn: () => T) => Promise<T>): WorkplaceAdapter {
  return {
    listBuildings: (siteId, asOf) => later(() => service.listBuildings(siteId, asOf)),
    getBuilding: (id, asOf) => later(() => service.getBuilding(id, asOf)),
    getFloorLayout: (floorId, asOf) => later(() => service.getFloorLayout(floorId, asOf)),
    listFloorOccupants: (floorId, asOf) => later(() => service.listFloorOccupants(floorId, asOf)),
    getFloorPresence: (floorId, date) => later(() => service.getFloorPresence(floorId, date)),
  };
}
