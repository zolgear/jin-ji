import { describe, expect, it } from "vitest";
import { ApiError, type Person } from "../data/types";
import { workplaceOf } from "../data/workplace";
import { addDays, inPeriod } from "../lib/date";
import demoData from "./demo-data.json";
import { createDemoAdapterFrom } from "./demoAdapter";
import { demoMe } from "./demoViewer";
import { generateLargeDataset } from "./largeDataset";
import type { DemoDataset } from "./model";
import { generateWorkplace, type DemoWorkplace } from "./workplace";

const data = demoData as DemoDataset;
const today = data.meta.business_date;

/** 配置図・割り当て・今日の所在の整合（どちらのデモにも当てはまる規則）。 */
function checkConsistency(wp: DemoWorkplace, d: DemoDataset) {
  const seats = new Map<string, { capacity: number; floorId: string; out: boolean }>();
  for (const layout of wp.layouts.values()) {
    for (const s of layout.seats) {
      // 席は階の外形（矩形）の内側、席番号は階の中で一意
      const xs = layout.outline.map((p) => p[0]);
      const ys = layout.outline.map((p) => p[1]);
      expect(s.x_mm > Math.min(...xs) && s.x_mm < Math.max(...xs) && s.y_mm > Math.min(...ys) && s.y_mm < Math.max(...ys)).toBe(true);
      seats.set(s.seat_id, { capacity: s.capacity, floorId: layout.floor_id, out: s.out_of_service });
    }
    expect(new Set(layout.seats.map((s) => s.code)).size).toBe(layout.seats.length);
  }
  const people = new Set(d.people.map((p) => p.id));
  for (const a of wp.assignments) {
    expect(people.has(a.person_id)).toBe(true);
    const seat = seats.get(a.seat_id!);
    expect(seat).toBeDefined();
    expect(seat!.floorId).toBe(a.floor_id);
    expect(seat!.out).toBe(false);
    expect(a.exclusive).toBe(seat!.capacity === 1);
  }
  // 定員 1 の席は同時に 1 人、持ち場は定員まで（業務日付と目盛りの日で確かめる）
  for (const date of [today, ...d.meta.milestones.map((m) => m.date)]) {
    const bySeat = new Map<string, number>();
    const byPerson = new Map<string, number>();
    for (const a of wp.assignments) {
      if (!inPeriod(date, a.valid_from, a.valid_until)) continue;
      bySeat.set(a.seat_id!, (bySeat.get(a.seat_id!) ?? 0) + 1);
      byPerson.set(a.person_id, (byPerson.get(a.person_id) ?? 0) + 1);
    }
    for (const [id, n] of bySeat) expect(n).toBeLessThanOrEqual(seats.get(id)!.capacity);
    // 1 人の主の席は同時に 1 つ
    for (const n of byPerson.values()) expect(n).toBe(1);
  }
  // 今日の所在: 1 人 1 件、席は 1 人。階と席の階が合う
  const personSeen = new Set<string>();
  const seatSeen = new Set<string>();
  for (const r of wp.presence) {
    expect(personSeen.has(r.person_id)).toBe(false);
    personSeen.add(r.person_id);
    if (r.seat_id) {
      expect(seatSeen.has(r.seat_id)).toBe(false);
      seatSeen.add(r.seat_id);
      expect(seats.get(r.seat_id)!.floorId).toBe(r.floor_id);
    }
  }
}

describe("デモの建物・階・席（サンプル 350 人）", () => {
  const wp = generateWorkplace(data);

  it("固定シードで決定的に作る", () => {
    const again = generateWorkplace(data);
    expect(again.assignments).toEqual(wp.assignments);
    expect(again.presence).toEqual(wp.presence);
    expect([...again.layouts.keys()]).toEqual([...wp.layouts.keys()]);
  });

  it("東京本社の本館（B1〜5F）・横浜工場の 2 棟・千葉物流センターがある", () => {
    const names = wp.buildings.map((b) => b.name);
    expect(names).toEqual(expect.arrayContaining(["本館", "工場棟", "事務棟", "物流センター"]));
    const main = wp.buildings.find((b) => b.name === "本館")!;
    const labels = wp.floors.filter((f) => f.building_id === main.id).sort((a, b) => a.level_order - b.level_order).map((f) => f.label);
    expect(labels).toEqual(["B1", "1F", "2F", "3F", "4F", "5F"]);
    // 工場はラインの持ち場（定員 2 以上）。席種別は固定席と持ち場だけ
    const seats = [...wp.layouts.values()].flatMap((l) => l.seats);
    expect(seats.some((s) => s.seat_kind === "station" && s.capacity > 1)).toBe(true);
    expect(seats.every((s) => s.seat_kind !== ("free" as string))).toBe(true);
  });

  it("配置図・割り当て・今日の所在が整合する", () => {
    checkConsistency(wp, data);
    // 業務日付に在籍する人の多くが席を持つ
    expect(wp.assignments.filter((a) => inPeriod(today, a.valid_from, a.valid_until)).length).toBeGreaterThan(80);
    expect(wp.presence.length).toBeGreaterThan(10);
  });
});

describe("デモの建物・階・席（合成 1 万人）", () => {
  const large = generateLargeDataset();
  const wp = generateWorkplace(large);

  it("配置図・割り当て・今日の所在が整合し、席は 3,000 を超える", () => {
    checkConsistency(wp, large);
    const seats = [...wp.layouts.values()].reduce((s, l) => s + l.seats.length, 0);
    expect(seats).toBeGreaterThan(3000);
    // 本館は 12 階を超える（3D の圧縮を確かめられる）
    const main = wp.buildings.find((b) => b.name === "本館")!;
    expect(wp.floors.filter((f) => f.building_id === main.id).length).toBeGreaterThan(12);
  });
});

describe("デモの WorkplaceAdapter（API と同じ規則）", () => {
  const adapter = createDemoAdapterFrom(data);
  const tokyo = data.sites.find((s) => s.name === "東京本社")!;

  it("階の一覧の数は席の状態の合計と合う", async () => {
    const [main] = await adapter.workplace.listBuildings(tokyo.id, today);
    for (const f of main!.floors) {
      expect(f.occupied_count + f.vacant_count + f.unavailable_count).toBe(f.seat_count);
    }
  });

  it("人の項目 workplace・seat・presence は API の応答と同じ形（建物コード・階の順・席の位置と種類）", async () => {
    const wp = generateWorkplace(data);
    const a = wp.assignments.find((x) => x.seat_id && x.role === "primary" && inPeriod(today, x.valid_from, x.valid_until))!;
    const view = workplaceOf((await adapter.getPerson(a.person_id, today)) as Person);
    if (view.seat) {
      expect(view.seat).toMatchObject({ seat_id: a.seat_id });
      expect(["fixed", "station"]).toContain(view.seat.kind);
      expect(view.seat.building_name).toBe(view.workplace!.building_name);
      expect(view.workplace).toMatchObject({ source: "seat", floor_id: view.seat.floor_id });
      expect(typeof view.workplace!.building_code).toBe("string");
      expect(typeof view.workplace!.level_order).toBe("number");
    }
  });

  it("席（seat）が見えない人の席は「使えない席」になり、在籍者に出ない（内部監査部）", async () => {
    const audit = data.org_units.find((o) => o.code === "A020")!;
    const member = data.people.find((p) => p.assignments.some((a) => a.org_unit_id === audit.id && a.type === "primary" && a.valid_until === null))!;
    const viewOf = (await adapter.getPerson(member.id, today)) as Person;
    expect(viewOf.visible_fields).not.toContain("seat");
    expect(workplaceOf(viewOf).seat).toBeNull();
    // 監査部の人の席を探す（デモの元データから）
    const wp = generateWorkplace(data);
    const a = wp.assignments.find((x) => x.person_id === member.id && inPeriod(today, x.valid_from, x.valid_until));
    if (a) {
      const layout = await adapter.workplace.getFloorLayout(a.floor_id, today);
      expect(layout.seats.find((s) => s.seat_id === a.seat_id)!.status).toBe("unavailable");
      const occupants = await adapter.workplace.listFloorOccupants(a.floor_id, today);
      expect(occupants.some((o) => o.person_id === member.id)).toBe(false);
    }
  });

  it("今日の所在: 今日以外は 400、見えない人の所在は人を返さず使えない席にする", async () => {
    const [main] = await adapter.workplace.listBuildings(tokyo.id, today);
    const floor = main!.floors.find((f) => f.seat_count > 0)!;
    await expect(adapter.workplace.getFloorPresence(floor.id, addDays(today, -1))).rejects.toBeInstanceOf(ApiError);
    await expect(adapter.workplace.getFloorPresence(floor.id, addDays(today, 1))).rejects.toBeInstanceOf(ApiError);
    for (const f of main!.floors.filter((x) => x.seat_count > 0)) {
      const p = await adapter.workplace.getFloorPresence(f.id, today);
      for (const r of p!.items) {
        const person = (await adapter.getPerson(r.person_id, today)) as Person;
        expect(person.visible_fields).toContain("presence");
      }
    }
  });

  it("人の項目 presence は今日の時点だけ。所在が無い・見えないときは項目ごと省く（「不在」の値を作らない）", async () => {
    const me = demoMe(data);
    const mine = (await adapter.getPerson(me.id, today)) as Person;
    const past = (await adapter.getPerson(me.id, "2026-04-01")) as Record<string, unknown>;
    expect("presence" in past).toBe(false);
    if (workplaceOf(mine).presence === null) expect("presence" in (mine as Record<string, unknown>)).toBe(false);
    const other = data.people.find((p) => p.termination_date === null && p.id !== me.id && !p.assignments.some((a) => a.org_unit_id === me.assignments[0]!.org_unit_id))!;
    const view = (await adapter.getPerson(other.id, today)) as Record<string, unknown>;
    if (!(view.visible_fields as string[]).includes("presence")) expect("presence" in view).toBe(false);
  });
});
