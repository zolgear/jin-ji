import { describe, expect, it } from "vitest";
import { LAYOUT_FORMAT, readLayoutDocument, toLayoutDocument } from "./layoutDocument";
import type { Building, FloorLayout } from "./workplace";

const building = {
  id: "b1",
  site_id: "s1",
  site_code: "TKY",
  site_name: "本社",
  code: "MAIN",
  name: "本館",
  sort_order: 0,
  floors: [
    { id: "f3", building_id: "b1", label: "3F", level_order: 30, kind: "office", layout_id: "l3", layout_valid_from: "2026-01-01", seat_count: 1, occupied_count: 0, vacant_count: 0, unavailable_count: 1 },
    { id: "f4", building_id: "b1", label: "4F", level_order: 40, kind: "office", layout_id: null, layout_valid_from: null, seat_count: 0, occupied_count: 0, vacant_count: 0, unavailable_count: 0 },
  ],
} as Building;

const layout: FloorLayout = {
  floor_id: "f3",
  as_of: "2026-04-01",
  layout_id: "l3",
  valid_from: "2026-01-01",
  outline: [
    [0, 0],
    [10000, 0],
    [10000, 5000],
    [0, 5000],
  ],
  fixtures: [],
  grid_mm: 100,
  zones: [{ zone_key: "z1", name: "西", kind: "office", polygon: [[0, 0], [5000, 0], [5000, 5000]] }],
  seats: [
    { seat_id: "s1", code: "A01", zone_key: "z1", x_mm: 1000, y_mm: 1000, rotation_deg: 90, desk_type: "desk_1200", seat_kind: "fixed", capacity: 1, features: ["monitor"], status: "unavailable" },
  ],
};

describe("配置図の JSON", () => {
  it("建物の今の配置図を形式 v1 にする（ゾーンは名前、使えない席の印は書かない、配置図の無い階は階だけ）", () => {
    const doc = toLayoutDocument("TKY", building, new Map([["f3", layout]]));
    expect(doc.format).toBe(LAYOUT_FORMAT);
    expect(doc.site_code).toBe("TKY");
    const [f3, f4] = doc.buildings[0]!.floors;
    expect(f3!.layout!.seats).toEqual([{ code: "A01", x_mm: 1000, y_mm: 1000, rotation_deg: 90, desk_type: "desk_1200", kind: "fixed", capacity: 1, features: ["monitor"], zone: "西" }]);
    expect(f4).toEqual({ label: "4F", level_order: 40, kind: "office" });

    // 書き出したものは読み込める
    expect(readLayoutDocument(JSON.stringify(doc))).toMatchObject({ buildings: 1, floors: 2, seats: 1 });
  });

  it("形式が違う・建物が無い・JSON でないものは読まない", () => {
    expect(() => readLayoutDocument("{")).toThrow("JSON");
    expect(() => readLayoutDocument(JSON.stringify({ format: "other" }))).toThrow(LAYOUT_FORMAT);
    expect(() => readLayoutDocument(JSON.stringify({ format: LAYOUT_FORMAT, buildings: [] }))).toThrow("建物");
  });
});
