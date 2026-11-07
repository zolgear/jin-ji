import { describe, expect, it } from "vitest";
import type { FloorOccupant, LayoutSeat } from "../../data/types";
import { applySeatMoves, dropOnSeat, seatMovesOf, setSeat } from "./floorEdit";

const seat = (id: string, capacity = 1, status: LayoutSeat["status"] = "vacant"): LayoutSeat =>
  ({ seat_id: id, code: id.toUpperCase(), zone_key: null, x_mm: 0, y_mm: 0, rotation_deg: 0, desk_type: "desk_1200", seat_kind: capacity > 1 ? "station" : "fixed", capacity, features: [], status }) as LayoutSeat;

const occupant = (person: string, seatId: string | null): FloorOccupant => ({
  person_id: person,
  display_name: `名前${person}`,
  org_unit_id: "o1",
  org_unit_name: "組織",
  seat_id: seatId,
  zone_key: null,
  role: "primary",
});

describe("フロアの編集の計算", () => {
  it("席を決め、元の席に戻すと変更を消す。ほかの変更は残す", () => {
    const other = { kind: "assignment" as const, target_id: "p9", values: { org_unit_id: "o2" } };
    let changes = setSeat([other], "p1", "b", "a");
    expect(seatMovesOf(changes)).toEqual(new Map([["p1", "b"]]));
    changes = setSeat(changes, "p1", null, "a");
    expect(changes).toEqual([other, { kind: "workplace", target_id: "p1", values: { seat_id: null } }]);
    expect(setSeat(changes, "p1", "a", "a")).toEqual([other]);
  });

  it("人のいる定員 1 の席に落とすと入れ替え、席の無い人なら相手の席を外す。持ち場は定員まで足す", () => {
    const occupants = [occupant("p1", "a"), occupant("p2", "b"), occupant("p3", "s"), occupant("p4", "s")];
    const originals = new Map<string, string | null>([["p1", "a"], ["p2", "b"], ["p3", "s"], ["p4", "s"], ["p5", null]]);

    const swap = dropOnSeat([], occupants, { personId: "p1", current: "a" }, seat("b"), originals);
    expect(seatMovesOf(swap)).toEqual(new Map([["p1", "b"], ["p2", "a"]]));

    // 入れ替えた後に元へ戻すと、変更は消える
    const moved = applySeatMoves(occupants, seatMovesOf(swap), new Set(["a", "b", "s"]), new Map());
    expect(dropOnSeat(swap, moved, { personId: "p1", current: "b" }, seat("a"), originals)).toEqual([]);

    const newcomer = dropOnSeat([], occupants, { personId: "p5", current: null }, seat("a"), originals);
    expect(seatMovesOf(newcomer)).toEqual(new Map([["p5", "a"], ["p1", null]]));

    expect(dropOnSeat([], occupants, { personId: "p5", current: null }, seat("s", 2), originals)).toEqual([]);
    expect(seatMovesOf(dropOnSeat([], occupants, { personId: "p1", current: "a" }, seat("t", 2), originals))).toEqual(new Map([["p1", "t"]]));
    expect(dropOnSeat([], occupants, { personId: "p1", current: "a" }, seat("x", 1, "unavailable"), originals)).toEqual([]);
  });

  it("階の人の一覧に変更を重ね、ほかの階から来る人は渡した名前で足し、ほかの階へ移る人は外す", () => {
    const occupants = [occupant("p1", "a"), occupant("p2", "b")];
    const moves = new Map<string, string | null>([["p1", "elsewhere"], ["p2", "a"], ["p7", "b"]]);
    const result = applySeatMoves(occupants, moves, new Set(["a", "b"]), new Map([["p7", { name: "七", orgId: "o7", orgName: "組織七" }]]));
    expect(result.map((o) => [o.person_id, o.seat_id, o.display_name])).toEqual([
      ["p2", "a", "名前p2"],
      ["p7", "b", "七"],
    ]);
  });
});
