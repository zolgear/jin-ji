import { describe, expect, it } from "vitest";
import { createApiAdapter } from "./apiAdapter";
import { workplaceOf } from "./workplace";
import type { Person } from "./types";

type Responses = Record<string, { status: number; body: unknown }>;

function fakeFetch(responses: Responses) {
  const calls: string[] = [];
  const fn = async (input: RequestInfo | URL) => {
    const url = String(input);
    calls.push(url);
    const r = responses[url] ?? { status: 404, body: { title: "Not Found", type: "urn:jinji:problem:not-found" } };
    return new Response(JSON.stringify(r.body), { status: r.status, headers: { "Content-Type": "application/json" } });
  };
  return { fn: fn as typeof fetch, calls };
}

// 2026-10-12T00:00:00+09:00
const now = () => new Date("2026-10-11T15:00:00Z");

const person = (id: string, over: Record<string, unknown> = {}) => ({
  id,
  visible_fields: [],
  display_name: `人 ${id}`,
  assignments: [
    { org_unit_id: "o-con", org_unit_name: "兼務課", type: "concurrent" },
    { org_unit_id: "o-main", org_unit_name: "主務課", type: "primary" },
  ],
  ...over,
});

const floorRef = { id: "f1", building_id: "b1", building_code: "HQ", building_name: "本社", site_id: "s1", label: "3F", level_order: 30, kind: "office" };

describe("建物・フロアモードの API の応答を画面の内部モデルにする", () => {
  it("建物の一覧は生成型の形のまま（occupied_count）。as_of を付け、建物の詳細は building を取り出す", async () => {
    const building = { id: "b1", site_id: "s1", site_code: null, site_name: null, code: "HQ", name: "本社", sort_order: 1, floors: [{ id: "f1", occupied_count: 3, seat_count: 5 }] };
    const { fn, calls } = fakeFetch({
      "/api/v1/sites/s1/buildings?as_of=2026-04-01": { status: 200, body: { as_of: "2026-04-01", site_id: "s1", items: [building] } },
      "/api/v1/buildings/b1": { status: 200, body: { as_of: "2026-10-12", building } },
    });
    const adapter = createApiAdapter({ fetch: fn, now });
    expect(await adapter.workplace.listBuildings("s1", "2026-04-01")).toEqual([building]);
    expect(await adapter.workplace.getBuilding("b1", "2026-10-12")).toEqual(building);
    expect(calls).toHaveLength(2);
  });

  it("配置図: floor・layout を平らにし、kind を seat_kind に、polygon を点にする", async () => {
    const { fn } = fakeFetch({
      "/api/v1/floors/f1/layout": {
        status: 200,
        body: {
          as_of: "2026-10-12",
          floor: floorRef,
          layout: {
            id: "l1",
            valid_from: "2026-04-01",
            grid_mm: 1000,
            outline: [[0, 0], [1000, 0], [1000, 1000]],
            fixtures: [{ kind: "core", name: null, polygon: [[0, 0], [10, 0], [10, 10]] }],
            zones: [{ zone_key: "z1", name: "営業", kind: "office", polygon: [[0, 0], [10, 0], [10, 10]] }],
            seats: [{ seat_id: "a", code: "A-01", zone_key: "z1", x_mm: 1, y_mm: 2, rotation_deg: 90, desk_type: "desk_1400", kind: "station", capacity: 4, features: ["monitor"], status: "unavailable" }],
          },
        },
      },
    });
    const layout = await createApiAdapter({ fetch: fn, now }).workplace.getFloorLayout("f1", "2026-10-12");
    expect(layout.floor_id).toBe("f1");
    expect(layout.layout_id).toBe("l1");
    expect(layout.valid_from).toBe("2026-04-01");
    expect(layout.outline[2]).toEqual([1000, 1000]);
    expect(layout.seats[0]).toMatchObject({ seat_id: "a", seat_kind: "station", status: "unavailable", desk_type: "desk_1400" });
    expect("kind" in layout.seats[0]!).toBe(false);
  });

  it("配置図がまだ無い階（layout が null）は空の配置図", async () => {
    const { fn } = fakeFetch({ "/api/v1/floors/f1/layout": { status: 200, body: { as_of: "2026-10-12", floor: floorRef, layout: null } } });
    const layout = await createApiAdapter({ fetch: fn, now }).workplace.getFloorLayout("f1", "2026-10-12");
    expect(layout).toMatchObject({ floor_id: "f1", layout_id: "", seats: [], zones: [], outline: [] });
  });

  it("在籍者: person から ID・表示名・主務の組織を取り、ページを続けて読む", async () => {
    const { fn, calls } = fakeFetch({
      "/api/v1/floors/f1/occupants?limit=100": {
        status: 200,
        body: { items: [{ person: person("p1"), seat_id: "a", seat_code: "A-01", zone_key: "z1", zone_name: "営業", role: "primary", source: "seat" }], next_cursor: "c2" },
      },
      "/api/v1/floors/f1/occupants?cursor=c2&limit=100": {
        status: 200,
        body: { items: [{ person: person("p2", { display_name: null, assignments: null }), seat_id: null, seat_code: null, zone_key: "z1", zone_name: "営業", role: "secondary", source: "zone" }], next_cursor: null },
      },
    });
    const occupants = await createApiAdapter({ fetch: fn, now }).workplace.listFloorOccupants("f1", "2026-10-12");
    expect(occupants).toEqual([
      { person_id: "p1", display_name: "人 p1", org_unit_id: "o-main", org_unit_name: "主務課", seat_id: "a", zone_key: "z1", role: "primary" },
      { person_id: "p2", display_name: null, org_unit_id: null, org_unit_name: null, seat_id: null, zone_key: "z1", role: "secondary" },
    ]);
    expect(calls).toHaveLength(2);
  });

  it("今日の所在: as_of を付けず、seats の unavailable の席を使えない席にする", async () => {
    const { fn, calls } = fakeFetch({
      "/api/v1/floors/f1/presence": {
        status: 200,
        body: {
          date: "2026-10-12",
          floor: floorRef,
          seats: [
            { seat_id: "a", code: "A-01", status: "occupied" },
            { seat_id: "b", code: "A-02", status: "unavailable" },
          ],
          items: [{ person: person("p1"), seat_id: "a", seat_code: "A-01", zone_key: "z1", zone_name: "営業" }],
        },
      },
    });
    const presence = await createApiAdapter({ fetch: fn, now }).workplace.getFloorPresence("f1", "2026-10-12");
    expect(calls).toEqual(["/api/v1/floors/f1/presence"]);
    expect(presence).toEqual({
      floor_id: "f1",
      date: "2026-10-12",
      unavailable_seat_ids: ["b"],
      items: [{ person_id: "p1", display_name: "人 p1", org_unit_id: "o-main", org_unit_name: "主務課", seat_id: "a", zone_key: "z1" }],
    });
  });

  it("提供元が無い導入先（404 presence-unavailable）では所在は null。ほかの 404 は例外", async () => {
    const none = fakeFetch({ "/api/v1/floors/f1/presence": { status: 404, body: { title: "x", type: "urn:jinji:problem:presence-unavailable" } } });
    expect(await createApiAdapter({ fetch: none.fn, now }).workplace.getFloorPresence("f1", "2026-10-12")).toBeNull();
    const missing = fakeFetch({});
    await expect(createApiAdapter({ fetch: missing.fn, now }).workplace.getFloorPresence("f1", "2026-10-12")).rejects.toMatchObject({ status: 404 });
  });

  it("人の所在の項目は GET /people/{id} の workplace・seat・presence。省かれていれば null", () => {
    const withAll = {
      id: "p",
      workplace: { site_id: "s1", building_id: "b1", building_code: "HQ", building_name: "本社", floor_id: "f1", floor_label: "3F", level_order: 30, zone_key: null, zone_name: null, source: "seat" },
      presence: { date: "2026-10-12", building_id: "b1", building_name: "本社", floor_id: "f1", floor_label: "3F", zone_key: null, zone_name: null, seat_id: null, seat_code: null },
    } as unknown as Person;
    expect(workplaceOf(withAll).workplace?.floor_label).toBe("3F");
    expect(workplaceOf(withAll).presence?.date).toBe("2026-10-12");
    expect(workplaceOf(withAll).seat).toBeNull();
    expect(workplaceOf({ id: "q" } as unknown as Person)).toEqual({ workplace: null, seat: null, presence: null });
  });
});
