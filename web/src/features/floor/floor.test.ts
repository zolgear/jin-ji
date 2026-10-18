import { describe, expect, it } from "vitest";
import type { FloorLayout, FloorOccupant, FloorPresence, LayoutSeat, OrgTree } from "../../data/types";
import { COMPRESS_THRESHOLD, isLowEndDevice, placeBuildings, stackFloors } from "./floorLayout3d";
import { boundsOf, buildFloorView, chairPosition, groupResolver, pointInPolygon, presenceOverlayOn, seatOfPerson, type OrgGroup } from "./floorModel";
import { fitViewport, panBy, toSvgY, zoomAt } from "./planViewport";

const seat = (id: string, x: number, kind: LayoutSeat["seat_kind"], status: LayoutSeat["status"], zone = "z1"): LayoutSeat => ({
  seat_id: id,
  code: id.toUpperCase(),
  zone_key: zone,
  x_mm: x,
  y_mm: 1000,
  rotation_deg: 0,
  desk_type: "desk_1200",
  seat_kind: kind,
  capacity: 1,
  features: [],
  status,
});

const layout: FloorLayout = {
  floor_id: "f1",
  as_of: "2026-10-01",
  layout_id: "r1",
  valid_from: "2000-01-01",
  outline: [
    [0, 0],
    [20000, 0],
    [20000, 10000],
    [0, 10000],
  ],
  fixtures: [],
  grid_mm: 100,
  zones: [
    { zone_key: "z1", name: "3F Aエリア", kind: "office", polygon: [[0, 0], [10000, 0], [10000, 10000], [0, 10000]] },
    { zone_key: "z2", name: "3F Bエリア", kind: "office", polygon: [[10000, 0], [20000, 0], [20000, 10000], [10000, 10000]] },
  ],
  seats: [
    seat("a1", 1000, "fixed", "occupied"),
    seat("a2", 2000, "fixed", "occupied"),
    seat("a3", 3000, "fixed", "vacant"),
    seat("a4", 4000, "fixed", "unavailable"),
    seat("b1", 11000, "fixed", "vacant", "z2"),
    seat("b2", 12000, "fixed", "vacant", "z2"),
    seat("b3", 13000, "fixed", "vacant", "z2"),
    seat("b4", 14000, "fixed", "vacant", "z2"),
  ],
};

const groups: Record<string, OrgGroup> = {
  o1: { id: "d1", name: "総務部", code: "A1" },
  o2: { id: "d2", name: "営業部", code: "A2" },
};
const groupOf = (id: string | null) => (id ? (groups[id] ?? null) : null);
const occ = (person: string, seatId: string, org = "o1"): FloorOccupant => ({
  person_id: person,
  display_name: person,
  org_unit_id: org,
  org_unit_name: org,
  seat_id: seatId,
  zone_key: null,
  role: "primary",
});
const occupants = [occ("p1", "a1"), occ("p2", "a2")];

describe("buildFloorView（席の状態と今日の所在の重ね）", () => {
  it("当日の所在が無ければ、席の状態は配置図のまま 3 つ（人・空席・使えない席）", () => {
    const v = buildFloorView({ layout, occupants, presence: null, groupOf });
    expect(v.seats.map((s) => s.state)).toEqual(["person", "person", "vacant", "unavailable", "vacant", "vacant", "vacant", "vacant"]);
    expect(v.counts).toEqual({ seats: 8, used: 2, vacant: 5, unavailable: 1 });
    expect(v.presenceShown).toBe(false);
    expect(v.seats.some((s) => s.today)).toBe(false);
  });

  it("見える人の所在は「今日」の印の人、見えない人の所在は使えない席。状態は 3 つのまま増えない", () => {
    const presence: FloorPresence = {
      floor_id: "f1",
      date: "2026-10-01",
      unavailable_seat_ids: ["b2"],
      items: [
        { person_id: "p3", display_name: "p3", org_unit_id: "o2", org_unit_name: "営業課", seat_id: "b1", zone_key: "z2" },
        // 割り当ての席に来ている人
        { person_id: "p1", display_name: "p1", org_unit_id: "o1", org_unit_name: "総務課", seat_id: "a1", zone_key: "z1" },
        // エリアだけの所在は席に出さない
        { person_id: "p4", display_name: "p4", org_unit_id: "o2", org_unit_name: "営業課", seat_id: null, zone_key: "z2" },
      ],
    };
    const v = buildFloorView({ layout, occupants, presence, groupOf });
    const by = new Map(v.seats.map((s) => [s.seat.seat_id, s]));
    expect(by.get("b1")!.state).toBe("person");
    expect(by.get("b1")!.today).toBe(true);
    expect(by.get("b2")!.state).toBe("unavailable");
    expect(by.get("a1")!.state).toBe("person");
    expect(by.get("a1")!.today).toBe(true);
    expect(by.get("a1")!.people).toHaveLength(1);
    // 所在の無い人は割り当ての席のまま（「不在」にしない）
    expect(by.get("a2")!.state).toBe("person");
    expect(by.get("a2")!.today).toBe(false);
    expect(new Set(v.seats.map((s) => s.state))).toEqual(new Set(["person", "vacant", "unavailable"]));
    expect(v.counts.vacant).toBe(3);
    expect(v.zoneOnly.map((r) => r.person_id)).toEqual(["p4"]);
    expect(seatOfPerson(v, "p3")?.seat.seat_id).toBe("b1");
  });

  it("色は席の多い組織 3 つまで（4 つ目以降は「その他」）。エリアの札は席の半分以上を占める組織", () => {
    const many = ["o1", "o2", "o3", "o4"];
    const g = (id: string | null) => (id ? { id: `d-${id}`, name: id, code: id } : null);
    const occ4 = [occ("q1", "a1", "o1"), occ("q2", "a2", "o1"), occ("q3", "a3", "o2"), occ("q4", "b1", "o3"), occ("q5", "b2", "o4"), occ("q6", "b3", "o4")];
    const v = buildFloorView({ layout: { ...layout, seats: layout.seats.map((s) => ({ ...s, status: s.status === "unavailable" ? "unavailable" : "occupied" })) }, occupants: occ4, presence: null, groupOf: g });
    expect(v.legend.filter((e) => e.slot !== null)).toHaveLength(3);
    // 同数は組織のコードの順（o1・o4 が 2 席、o2・o3 が 1 席 → o3 が「その他」）
    expect(v.legend.find((e) => e.group.id === "d-o3")!.slot).toBeNull();
    expect(many).toHaveLength(4);
    expect(v.zones[0]!.label).toBe("o1");
    expect(v.zones[1]!.label).toBe("o4");
    const mono = buildFloorView({ layout, occupants, presence: null, groupOf, monochrome: true });
    expect(mono.seats.filter((s) => s.state === "person").every((s) => s.slot === null)).toBe(true);
  });

  it("今日の所在は今日を表示しているときだけ（過去・将来の日、提供元が無い、presence=0 では重ねない）", () => {
    expect(presenceOverlayOn("2026-10-01", "2026-10-01", true, null)).toBe(true);
    expect(presenceOverlayOn("2026-09-30", "2026-10-01", true, null)).toBe(false);
    expect(presenceOverlayOn("2026-10-02", "2026-10-01", true, null)).toBe(false);
    expect(presenceOverlayOn("2026-10-01", "2026-10-01", false, null)).toBe(false);
    expect(presenceOverlayOn("2026-10-01", "2026-10-01", true, "0")).toBe(false);
  });
});

describe("配置図の小さな計算", () => {
  it("椅子の位置は向きの反対側（0° は +y を向き、手前に座る）", () => {
    expect(chairPosition({ x_mm: 1000, y_mm: 1000, rotation_deg: 0, seat_kind: "fixed" })).toEqual([1000, 350]);
    expect(chairPosition({ x_mm: 1000, y_mm: 1000, rotation_deg: 180, seat_kind: "fixed" })).toEqual([1000, 1650]);
    expect(chairPosition({ x_mm: 1000, y_mm: 1000, rotation_deg: 90, seat_kind: "station" })).toEqual([1000, 1000]);
  });

  it("点の多角形内判定と外接矩形", () => {
    expect(pointInPolygon([5, 5], layout.zones[0]!.polygon.map(([x, y]) => [x / 1000, y / 1000]))).toBe(true);
    expect(pointInPolygon([15000, 5000], layout.zones[0]!.polygon)).toBe(false);
    expect(boundsOf(layout.outline)).toEqual({ minX: 0, minY: 0, maxX: 20000, maxY: 10000 });
  });

  it("組織の色分けのまとまりは段 department の祖先（無ければ 2 段目）", () => {
    const unit = (id: string, parent: string | null, key: string | null) => ({
      id,
      legal_entity: { id: "e", code: "E", name: "E" },
      code: id,
      name: id,
      short_name: null,
      level: key ? { key, name: key, rank: 1 } : null,
      parent_id: parent,
      head_person_id: null,
      site: null,
    });
    const tree = { as_of: "2026-10-01", units: [unit("div", null, "division"), unit("dep", "div", "department"), unit("sec", "dep", "section"), unit("x", null, null), unit("y", "x", null), unit("z", "y", null)] } as unknown as OrgTree;
    const of = groupResolver(tree);
    expect(of("sec")?.id).toBe("dep");
    expect(of("div")?.id).toBe("div");
    expect(of("z")?.id).toBe("y");
    expect(of("missing")).toBeNull();
  });
});

describe("3D の積み方（分解・圧縮）", () => {
  const floors = (n: number) => Array.from({ length: n }, (_, i) => ({ id: `f${i}`, level_order: (i - 1) * 10 }));

  it("階は下から順に積み、選んだ階の上は広く空ける", () => {
    const s = stackFloors(floors(6), "f2", 24);
    expect(s.map((x) => x.y)).toEqual([...s.map((x) => x.y)].sort((a, b) => a - b));
    expect(s.every((x) => !x.compressed)).toBe(true);
    const gap = (i: number) => s[i + 1]!.y - s[i]!.y;
    expect(gap(2)).toBeGreaterThan(gap(1));
  });

  it(`${COMPRESS_THRESHOLD} 階を超える建物は、選んだ階の前後 2 階だけを離し、ほかは詰める`, () => {
    const s = stackFloors(floors(16), "f8", 24);
    expect(s.filter((x) => !x.compressed).map((x) => x.id)).toEqual(["f6", "f7", "f8", "f9", "f10"]);
    expect(s[2]!.y - s[1]!.y).toBeLessThan(s[8]!.y - s[7]!.y);
    // 拠点の段（建物を並べる）はすべて詰める
    expect(stackFloors(floors(4), null, 24, true).every((x) => x.compressed)).toBe(true);
  });

  it("建物は横に並べ、重ならない", () => {
    const b = (id: string) => ({ id, site_id: "s", code: id, name: id, sort_order: 0, floors: [] });
    const placed = placeBuildings([b("a"), b("b")], new Map([["a", { width: 60, depth: 30 }], ["b", { width: 30, depth: 20 }]]));
    expect(placed[1]!.x).toBeGreaterThan(placed[0]!.x + placed[0]!.width);
  });

  it("性能の低い端末は LOD を下げる", () => {
    expect(isLowEndDevice({ hardwareConcurrency: 2 }, null)).toBe(true);
    expect(isLowEndDevice({ hardwareConcurrency: 8, deviceMemory: 8 }, 16)).toBe(false);
    expect(isLowEndDevice({ hardwareConcurrency: 8 }, 900)).toBe(true);
  });
});

describe("2D の平面図の表示範囲", () => {
  const b = { minX: 0, minY: 0, maxX: 20000, maxY: 10000 };

  it("全体が収まり、縦横比を表示領域に合わせる", () => {
    const v = fitViewport(b, 1);
    expect(v.w).toBeGreaterThanOrEqual(20000);
    expect(v.h).toBe(v.w);
    expect(toSvgY(10000, b)).toBe(0);
  });

  it("指した点を保って拡大し、上限・下限で止める", () => {
    const v = fitViewport(b, 2);
    const z = zoomAt(v, 2, 0.25, 0.5, { minW: 1000, maxW: 50000 });
    const before = v.cx - v.w / 2 + 0.25 * v.w;
    const after = z.cx - z.w / 2 + 0.25 * z.w;
    expect(after).toBeCloseTo(before, 6);
    expect(z.w).toBeCloseTo(v.w / 2, 6);
    expect(zoomAt(v, 1000, 0.5, 0.5, { minW: 3000, maxW: 50000 }).w).toBe(3000);
    expect(panBy(v, 100, 0, 1000).cx).toBeLessThan(v.cx);
  });
});
