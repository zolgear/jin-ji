import { describe, expect, it } from "vitest";
import type { OrgUnit } from "../../../data/types";
import { abolishUnit, applyOrgChanges, createUnit, isWithin, movePerson, movesOf, orderOf, placeIn, setOrder, undoMove, updateUnit } from "./model";

const unit = (id: string, parent: string | null, head: string | null = null): OrgUnit =>
  ({ id, code: id.toUpperCase(), name: `組織${id}`, parent_id: parent, head_person_id: head, level: null, short_name: null, site: null, legal_entity: { id: "le", code: "LE", name: "法人" } }) as unknown as OrgUnit;

const base = [unit("a", null), unit("b", "a"), unit("c", "a"), unit("d", "c")];

describe("素案ボードの計算", () => {
  it("同じ人を 2 度動かすと 1 件の異動にまとめ、元に戻すと消す", () => {
    const from = { org: "b", title: null };
    let changes = movePerson([], "p1", "c", from);
    changes = movePerson(changes, "p1", "d", from);
    expect(changes).toEqual([{ kind: "assignment", target_id: "p1", values: { org_unit_id: "d" } }]);
    expect(movesOf(changes).get("p1")?.to).toBe("d");
    expect(movePerson(changes, "p1", "b", from)).toEqual([]);
  });

  it("役職を変えた異動は、組織を戻しても役職の分を残す", () => {
    const from = { org: "b", title: "主任" };
    let changes = movePerson([], "p1", "c", from, "課長");
    changes = movePerson(changes, "p1", "b", from);
    expect(changes).toEqual([{ kind: "assignment", target_id: "p1", values: { org_unit_id: "b", position_title: "課長" } }]);
    expect(movePerson(changes, "p1", "b", from, "主任")).toEqual([]);
  });

  it("上位組織の付け替えを重ね、元の値に戻すと変更を消す", () => {
    const tree = applyOrgChanges(base, []);
    const changes = updateUnit([], tree, "d", "parent_org_unit_id", "b");
    const after = applyOrgChanges(base, changes);
    expect(after.byId.get("d")).toMatchObject({ parentId: "b", state: "changed", changed: ["parent_org_unit_id"] });
    expect(after.children.get("b")?.map((u) => u.id)).toEqual(["d"]);
    expect(isWithin(after, "d", "c")).toBe(false);
    expect(updateUnit(changes, after, "d", "parent_org_unit_id", "c")).toEqual([]);
  });

  it("新設した組織の変更は新設に書き込み、取り消すと配下の新設とそこへの異動も消す", () => {
    let changes = createUnit([], "n1", "b", "新しい課", "B-N1");
    changes = createUnit(changes, "n2", "n1", "新しい係", "B-N2");
    changes = movePerson(changes, "p1", "n2", { org: "c", title: null });
    let tree = applyOrgChanges(base, changes);
    changes = updateUnit(changes, tree, "n1", "name", "改めた課");
    tree = applyOrgChanges(base, changes);
    expect(tree.byId.get("n1")).toMatchObject({ name: "改めた課", state: "created" });
    expect(isWithin(tree, "n2", "b")).toBe(true);
    expect(abolishUnit(changes, tree, "n1")).toEqual([]);
  });

  it("既存の組織の廃止は印を付け、取り消せる", () => {
    const tree = applyOrgChanges(base, []);
    const changes = abolishUnit([], tree, "d");
    expect(applyOrgChanges(base, changes).byId.get("d")?.state).toBe("abolished");
    expect(abolishUnit(changes, tree, "d")).toBe(changes);
    expect(abolishUnit(changes, tree, "d", true)).toEqual([]);
  });

  it("並び順は今の並びと同じなら変更にせず、異動で組織を出た人は並び順から外す", () => {
    const now = ["p1", "p2", "p3"];
    expect(placeIn(now, "p3", { personId: "p1", before: true })).toEqual(["p3", "p1", "p2"]);
    expect(placeIn(now, "p1", { personId: "p3", before: false })).toEqual(["p2", "p3", "p1"]);
    expect(placeIn(now, "p4", null)).toEqual(["p1", "p2", "p3", "p4"]);

    let changes = setOrder([], "b", ["p3", "p1", "p2"], now);
    expect(orderOf(changes, "b")).toEqual(["p3", "p1", "p2"]);
    expect(setOrder(changes, "b", now, now)).toEqual([]);

    // p3 が c へ異動すると b の並びから外れる。c で並べてから異動を取り消すと、c の並びからも外れる
    changes = movePerson(changes, "p3", "c", { org: "b", title: null });
    expect(orderOf(changes, "b")).toEqual(["p1", "p2"]);
    changes = setOrder(changes, "c", ["p3", "q1"], ["q1"]);
    changes = undoMove(changes, "p3");
    expect(orderOf(changes, "c")).toEqual(["q1"]);
    expect(orderOf(changes, "b")).toEqual(["p1", "p2"]);
  });
});
