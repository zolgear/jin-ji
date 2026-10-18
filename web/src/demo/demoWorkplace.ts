// デモの建物・フロアモードの取得元（WorkplaceAdapter）。API の規則（仕様 §3・§6）を、デモの閲覧者から見た場合に限って簡略に真似る。
// - 席の状態は閲覧者から見た 3 つ（人・空席・使えない席）。席（項目 seat）が見えない人の席は「使えない席」にする
// - 当日の所在は今日から予約できる先の日数までだけ。見えない人の予約で埋まった席は「使えない席」、予約が無いことには意味を持たせない
// - 予約・取り消しはデモの閲覧者（本人）だけ。デモの中だけで覚え、再読み込みで元に戻る
import { addDays, inPeriod } from "../lib/date";
import {
  ApiError,
  type Building,
  type FloorLayout,
  type FloorOccupant,
  type FloorPresence,
  type FloorSummary,
  type MyReservation,
  type PersonPresence,
  type PersonSeat,
  type PersonWorkplace,
  type ReservationTarget,
  type SeatStatus,
  type WorkplaceAdapter,
} from "../data/types";
import type { DemoDirectory, PersonField } from "./directory";
import type { DemoPerson } from "./model";
import type { DemoFloor, DemoReservation, DemoSeat, DemoSeatAssignment, DemoWorkplace, DemoZone } from "./workplace";

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
  private readonly viewer: DemoPerson;
  private readonly today: string;
  private readonly floorById: Map<string, DemoFloor>;
  private readonly seatById = new Map<string, SeatInfo>();
  private readonly zoneByKey = new Map<string, { zone: DemoZone; floor: DemoFloor }>();
  private readonly bySeat = new Map<string, DemoSeatAssignment[]>();
  private readonly byPerson = new Map<string, DemoSeatAssignment[]>();
  private readonly personById: Map<string, DemoPerson>;
  private readonly stateCache = new Map<string, Map<string, SeatState>>();

  constructor(dir: DemoDirectory, wp: DemoWorkplace, viewer: DemoPerson) {
    this.dir = dir;
    this.wp = wp;
    this.viewer = viewer;
    this.today = dir.businessDate;
    this.floorById = new Map(wp.floors.map((f) => [f.id, f]));
    this.personById = new Map(dir.data.people.map((p) => [p.id, p]));
    for (const layout of wp.layouts.values()) {
      const floor = this.floorById.get(layout.floor_id)!;
      const zones = new Map(layout.zones.map((z) => [z.zone_key, z]));
      for (const z of layout.zones) this.zoneByKey.set(z.zone_key, { zone: z, floor });
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

  private reservationsOn(date: string, floorId?: string): DemoReservation[] {
    return this.wp.reservations.filter((r) => r.date === date && (!floorId || r.floor_id === floorId));
  }

  private summary(floor: DemoFloor, asOf: string): FloorSummary {
    const states = this.states(floor.id, asOf);
    let assigned = 0;
    let vacant = 0;
    let unavailable = 0;
    for (const s of states.values()) {
      if (s.status === "occupied") assigned++;
      else if (s.status === "vacant") vacant++;
      else unavailable++;
    }
    // 今日を表示しているときだけ、今日の空き席数（予約で埋まった席を除く。人数は返さない）
    let todayVacant: number | null = null;
    if (asOf === this.today) {
      const reserved = new Set(this.reservationsOn(this.today, floor.id).map((r) => r.seat_id));
      todayVacant = [...states.entries()].filter(([id, s]) => s.status === "vacant" && !reserved.has(id)).length;
    }
    return {
      id: floor.id,
      building_id: floor.building_id,
      label: floor.label,
      level_order: floor.level_order,
      kind: floor.kind,
      seat_count: states.size,
      assigned_count: assigned,
      vacant_count: vacant,
      unavailable_count: unavailable,
      today_vacant_count: todayVacant,
    };
  }

  private building(id: string, asOf: string): Building {
    const b = this.wp.buildings.find((x) => x.id === id);
    if (!b || !this.dir.siteAt(b.site_id, asOf)) throw new ApiError(404, "見つかりません。", "urn:jinji:problem:not-found");
    const floors = this.wp.floors
      .filter((f) => f.building_id === id)
      .sort((x, y) => x.level_order - y.level_order)
      .map((f) => this.summary(f, asOf));
    return { id: b.id, site_id: b.site_id, code: b.code, name: b.name, sort_order: b.sort_order, floors };
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
      zones: layout.zones.map((z) => ({
        ...z,
        home_org_units: this.wp.homeZones
          .filter((h) => h.zone_key === z.zone_key && inPeriod(asOf, h.valid_from, h.valid_until))
          .map((h) => this.dir.orgUnit(h.org_unit_id))
          .filter((o) => o !== undefined && inPeriod(asOf, o.valid_from, o.valid_until))
          .map((o) => ({ id: o!.id, name: o!.name })),
      })),
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

  // ---- 当日の所在 ----

  private checkDate(date: string): void {
    if (date < this.today) throw new ApiError(400, "過去の日の予約は扱えません。", "urn:jinji:problem:bad-request");
    if (date > addDays(this.today, this.wp.presence.days_ahead)) {
      throw new ApiError(400, `予約できるのは ${this.wp.presence.days_ahead} 日先までです。`, "urn:jinji:problem:bad-request");
    }
  }

  getFloorPresence(floorId: string, date: string): FloorPresence {
    this.checkDate(date);
    const floor = this.floor(floorId);
    const states = this.states(floor.id, this.today);
    const unavailable: string[] = [];
    const reservations: FloorPresence["reservations"] = [];
    const reserved = new Set<string>();
    for (const r of this.reservationsOn(date, floor.id)) {
      if (r.seat_id) reserved.add(r.seat_id);
      const person = this.personById.get(r.person_id);
      if (person && this.visible(person, "presence")) {
        const org = this.primaryOrg(person, this.today);
        // 予約の席番号は、項目 seat も見えるときだけ
        const seat = r.seat_id && this.visible(person, "seat") ? r.seat_id : null;
        reservations.push({
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
    const vacant = [...states.entries()].filter(([id, s]) => s.status === "vacant" && !reserved.has(id)).length;
    return { floor_id: floor.id, date, unavailable_seat_ids: unavailable, reservations, vacant_count: vacant };
  }

  private toMine(r: DemoReservation): MyReservation {
    const floor = this.floor(r.floor_id);
    const building = this.wp.buildings.find((b) => b.id === floor.building_id)!;
    const zone = this.zoneByKey.get(r.zone_key)?.zone;
    return {
      date: r.date,
      site_id: building.site_id,
      building_id: building.id,
      building_name: building.name,
      floor_id: floor.id,
      floor_label: floor.label,
      zone_key: r.zone_key || null,
      zone_name: zone?.name ?? null,
      seat_id: r.seat_id,
      seat_code: r.seat_id ? (this.seatById.get(r.seat_id)?.seat.code ?? null) : null,
    };
  }

  listMyPresence(): MyReservation[] {
    return this.wp.reservations
      .filter((r) => r.person_id === this.viewer.id && r.date >= this.today)
      .sort((a, b) => a.date.localeCompare(b.date))
      .map((r) => this.toMine(r));
  }

  /** 本人の予約（席: その日に有効な配置図の free の席と、本人の固定席。エリア: restricted 以外）。 */
  reserve(date: string, target: ReservationTarget): MyReservation {
    this.checkDate(date);
    const floor = this.floor(target.floorId);
    let next: DemoReservation;
    if ("seatId" in target) {
      const info = this.seatById.get(target.seatId);
      if (!info || info.floor.id !== floor.id) throw new ApiError(404, "席が見つかりません。", "urn:jinji:problem:not-found");
      const own = (this.byPerson.get(this.viewer.id) ?? []).some((a) => a.seat_id === target.seatId && inPeriod(this.today, a.valid_from, a.valid_until));
      if (info.seat.out_of_service || info.zone?.kind === "restricted" || (info.seat.seat_kind !== "free" && !own)) {
        throw new ApiError(400, "この席は予約できません。", "urn:jinji:problem:bad-request");
      }
      const other = this.wp.reservations.find((r) => r.date === date && r.seat_id === target.seatId && r.person_id !== this.viewer.id);
      if (other) throw new ApiError(409, "この席はすでに予約されています。", "urn:jinji:problem:conflict");
      next = { person_id: this.viewer.id, date, floor_id: floor.id, seat_id: target.seatId, zone_key: info.seat.zone_key ?? "" };
    } else {
      const z = this.zoneByKey.get(target.zoneKey);
      if (!z || z.floor.id !== floor.id || z.zone.kind === "restricted") throw new ApiError(400, "このエリアは予約できません。", "urn:jinji:problem:bad-request");
      next = { person_id: this.viewer.id, date, floor_id: floor.id, seat_id: null, zone_key: target.zoneKey };
    }
    // 1 人 1 日 1 件（変更は置き換え。履歴を残さない）
    this.wp.reservations = this.wp.reservations.filter((r) => !(r.person_id === this.viewer.id && r.date === date));
    this.wp.reservations.push(next);
    return this.toMine(next);
  }

  cancel(date: string): void {
    this.checkDate(date);
    this.wp.reservations = this.wp.reservations.filter((r) => !(r.person_id === this.viewer.id && r.date === date));
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
        building_name: b.name,
        floor_id: info.floor.id,
        floor_label: info.floor.label,
        zone_key: info.zone?.zone_key ?? null,
        zone_name: info.zone?.name ?? null,
        inherited: false,
      };
      seat = { seat_id: info.seat.seat_id, code: info.seat.code, floor_id: info.floor.id, x_mm: info.seat.x_mm, y_mm: info.seat.y_mm };
    } else {
      // 主務の組織（・上位組織）のホームエリアを引き継ぐ（フリーアドレス）
      const primary = this.dir.assignmentsAt(person, asOf).find((a) => a.type === "primary");
      if (primary) {
        for (const org of this.dir.ancestors(primary.org_unit_id)) {
          const hz = this.wp.homeZones.find((h) => h.org_unit_id === org.id && inPeriod(asOf, h.valid_from, h.valid_until));
          const z = hz ? this.zoneByKey.get(hz.zone_key) : undefined;
          if (!z) continue;
          const b = buildingOf(z.floor);
          workplace = { site_id: b.site_id, building_id: b.id, building_name: b.name, floor_id: z.floor.id, floor_label: z.floor.label, zone_key: z.zone.zone_key, zone_name: z.zone.name, inherited: true };
          break;
        }
      }
    }
    let presence: PersonPresence | null = null;
    // 当日の所在は今日の時点だけ（タイムマシンの対象外）
    if (asOf === this.today && visible.has("presence")) {
      const r = this.wp.reservations.find((x) => x.person_id === person.id && x.date === this.today);
      if (r) {
        const mine = this.toMine(r);
        const showSeat = visible.has("seat");
        presence = {
          date: mine.date,
          building_id: mine.building_id,
          building_name: mine.building_name,
          floor_id: mine.floor_id,
          floor_label: mine.floor_label,
          zone_key: mine.zone_key,
          zone_name: mine.zone_name,
          seat_id: showSeat ? mine.seat_id : null,
          seat_code: showSeat ? mine.seat_code : null,
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
    getPresenceSettings: () => later(() => ({ ...service.wp.presence })),
    getFloorPresence: (floorId, date) => later(() => service.getFloorPresence(floorId, date)),
    listMyPresence: () => later(() => service.listMyPresence()),
    reserve: (date, target) => later(() => service.reserve(date, target)),
    cancel: (date) => later(() => service.cancel(date)),
  };
}
