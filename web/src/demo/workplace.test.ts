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

/** 配置図・割り当て・予約の整合（どちらのデモにも当てはまる規則）。 */
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
  // 予約: 1 人 1 日 1 件、席は 1 日 1 人。今日から 14 日先までの平日だけ
  const personDay = new Set<string>();
  const seatDay = new Set<string>();
  for (const r of wp.reservations) {
    expect(r.date >= today && r.date <= addDays(today, wp.presence.days_ahead)).toBe(true);
    expect([0, 6]).not.toContain(new Date(`${r.date}T00:00:00Z`).getUTCDay());
    const pk = `${r.person_id}|${r.date}`;
    expect(personDay.has(pk)).toBe(false);
    personDay.add(pk);
    if (r.seat_id) {
      const sk = `${r.seat_id}|${r.date}`;
      expect(seatDay.has(sk)).toBe(false);
      seatDay.add(sk);
      expect(seats.get(r.seat_id)!.floorId).toBe(r.floor_id);
    }
  }
}

describe("デモの建物・階・席（サンプル 350 人）", () => {
  const wp = generateWorkplace(data, { viewerId: demoMe(data).id });

  it("固定シードで決定的に作る", () => {
    const again = generateWorkplace(data, { viewerId: demoMe(data).id });
    expect(again.assignments).toEqual(wp.assignments);
    expect(again.reservations).toEqual(wp.reservations);
    expect([...again.layouts.keys()]).toEqual([...wp.layouts.keys()]);
  });

  it("東京本社の本館（B1〜5F）・横浜工場の 2 棟・千葉物流センターがある", () => {
    const names = wp.buildings.map((b) => b.name);
    expect(names).toEqual(expect.arrayContaining(["本館", "工場棟", "事務棟", "物流センター"]));
    const main = wp.buildings.find((b) => b.name === "本館")!;
    const labels = wp.floors.filter((f) => f.building_id === main.id).sort((a, b) => a.level_order - b.level_order).map((f) => f.label);
    expect(labels).toEqual(["B1", "1F", "2F", "3F", "4F", "5F"]);
    // 工場はラインの持ち場（定員 2 以上）、フリーアドレスのエリアがある
    const seats = [...wp.layouts.values()].flatMap((l) => l.seats);
    expect(seats.some((s) => s.seat_kind === "station" && s.capacity > 1)).toBe(true);
    expect([...wp.layouts.values()].some((l) => l.zones.some((z) => z.kind === "free_address"))).toBe(true);
  });

  it("配置図・割り当て・予約が整合する", () => {
    checkConsistency(wp, data);
    // 業務日付に在籍する人の多くが席かホームエリアを持つ
    expect(wp.assignments.filter((a) => inPeriod(today, a.valid_from, a.valid_until)).length).toBeGreaterThan(80);
    expect(wp.reservations.filter((r) => r.date === today).length).toBeGreaterThan(10);
  });

  it("デモの閲覧者（フリーアドレス）は今日の席を予約している", () => {
    const mine = wp.reservations.filter((r) => r.person_id === demoMe(data).id);
    expect(mine.some((r) => r.date === today && r.seat_id !== null)).toBe(true);
  });
});

describe("デモの建物・階・席（合成 1 万人）", () => {
  const large = generateLargeDataset();
  const wp = generateWorkplace(large);

  it("配置図・割り当て・予約が整合し、席は 3,000 を超える", () => {
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

  it("階の一覧の数は席の状態の合計と合い、今日だけ空き席数を返す", async () => {
    const [main] = await adapter.workplace.listBuildings(tokyo.id, today);
    for (const f of main!.floors) {
      expect(f.assigned_count + f.vacant_count + f.unavailable_count).toBe(f.seat_count);
      expect(f.today_vacant_count).not.toBeNull();
    }
    const [past] = await adapter.workplace.listBuildings(tokyo.id, "2026-04-01");
    expect(past!.floors.every((f) => f.today_vacant_count === null)).toBe(true);
  });

  it("席（seat）が見えない人の席は「使えない席」になり、在籍者に出ない（内部監査部）", async () => {
    const audit = data.org_units.find((o) => o.code === "A020")!;
    const member = data.people.find((p) => p.assignments.some((a) => a.org_unit_id === audit.id && a.type === "primary" && a.valid_until === null))!;
    const viewOf = (await adapter.getPerson(member.id, today)) as Person;
    expect(viewOf.visible_fields).not.toContain("seat");
    expect(workplaceOf(viewOf).seat).toBeNull();
    // 監査部の人の席を探す（デモの元データから）
    const wp = generateWorkplace(data, { viewerId: demoMe(data).id });
    const a = wp.assignments.find((x) => x.person_id === member.id && inPeriod(today, x.valid_from, x.valid_until));
    if (a) {
      const layout = await adapter.workplace.getFloorLayout(a.floor_id, today);
      expect(layout.seats.find((s) => s.seat_id === a.seat_id)!.status).toBe("unavailable");
      const occupants = await adapter.workplace.listFloorOccupants(a.floor_id, today);
      expect(occupants.some((o) => o.person_id === member.id)).toBe(false);
    }
  });

  it("当日の所在: 過去の日は 400、見えない人の予約は人を返さず使えない席にする", async () => {
    const [main] = await adapter.workplace.listBuildings(tokyo.id, today);
    const floor = main!.floors.find((f) => f.seat_count > 0)!;
    await expect(adapter.workplace.getFloorPresence(floor.id, addDays(today, -1))).rejects.toBeInstanceOf(ApiError);
    await expect(adapter.workplace.getFloorPresence(floor.id, addDays(today, 15))).rejects.toBeInstanceOf(ApiError);
    for (const f of main!.floors.filter((x) => x.seat_count > 0)) {
      const p = await adapter.workplace.getFloorPresence(f.id, today);
      for (const r of p!.reservations) {
        const person = (await adapter.getPerson(r.person_id, today)) as Person;
        expect(person.visible_fields).toContain("presence");
      }
    }
  });

  it("人の項目 presence は今日の時点だけ。予約が無い・見えないときは項目ごと省く（「不在」の値を作らない）", async () => {
    const me = demoMe(data);
    const mine = (await adapter.getPerson(me.id, today)) as Person;
    expect(workplaceOf(mine).presence).not.toBeNull();
    const past = (await adapter.getPerson(me.id, "2026-04-01")) as Record<string, unknown>;
    expect("presence" in past).toBe(false);
    const other = data.people.find((p) => p.termination_date === null && p.id !== me.id && !p.assignments.some((a) => a.org_unit_id === me.assignments[0]!.org_unit_id))!;
    const view = (await adapter.getPerson(other.id, today)) as Record<string, unknown>;
    if (!(view.visible_fields as string[]).includes("presence")) expect("presence" in view).toBe(false);
  });

  it("予約は本人だけ。取り合いは 409、取り消しは記録を残さない", async () => {
    const a = createDemoAdapterFrom(data);
    const mine = await a.workplace.listMyPresence();
    const todays = mine.find((r) => r.date === today)!;
    const date = addDays(today, 1);
    const layout = await a.workplace.getFloorLayout(todays.floor_id, today);
    const presence = await a.workplace.getFloorPresence(todays.floor_id, date);
    const taken = new Set([...presence!.unavailable_seat_ids, ...presence!.reservations.map((r) => r.seat_id)]);
    const free = layout.seats.find((s) => s.seat_kind === "free" && s.status === "vacant" && !taken.has(s.seat_id))!;
    const r = await a.workplace.reserve(date, { floorId: todays.floor_id, seatId: free.seat_id });
    expect(r.seat_code).toBe(free.code);
    const fixed = layout.seats.find((s) => s.seat_kind === "fixed")!;
    await expect(a.workplace.reserve(date, { floorId: todays.floor_id, seatId: fixed.seat_id })).rejects.toBeInstanceOf(ApiError);
    const unavailable = presence!.unavailable_seat_ids[0];
    if (unavailable) {
      await expect(a.workplace.reserve(date, { floorId: todays.floor_id, seatId: unavailable })).rejects.toMatchObject({ status: 409 });
    }
    await a.workplace.cancel(date);
    expect((await a.workplace.listMyPresence()).some((x) => x.date === date)).toBe(false);
  });
});
