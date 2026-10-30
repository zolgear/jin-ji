import { describe, expect, it } from "vitest";
import { orderUnits } from "../features/field/shared";
import demoData from "./demo-data.json";
import { createDemoField, simulateDemo } from "./demoField";
import { DemoDirectory } from "./directory";
import type { DemoDataset } from "./model";

const data = demoData as DemoDataset;
const dir = new DemoDirectory(data);
const later = <T,>(fn: () => T) => Promise.resolve().then(fn);

/** 主務の所属者がいて、配下の組織を持たない組織と、その所属者。 */
function leafWithMember() {
  const units = dir.orgTree(dir.businessDate).units;
  for (const p of data.people) {
    const a = dir.assignmentsAt(p, dir.businessDate).find((x) => x.type === "primary");
    if (a && !units.some((u) => u.parent_id === a.org_unit_id)) return { person: p.id, unit: a.org_unit_id, units };
  }
  throw new Error("デモデータに該当する人がいません");
}

describe("素案のシミュレーション（デモ）", () => {
  it("新設した組織への異動で人数が移り、元の組織は減る。公開側（デモのデータ）は変えない", () => {
    const { person, unit, units } = leafWithMember();
    const parent = units.find((u) => u.id === unit)!.parent_id!;
    const created = "00000000-0000-4000-8000-0000000000aa";
    const sim = simulateDemo(dir, dir.businessDate, [
      { kind: "org_create", target_id: created, values: { name: "新しい課", code: "NEW", parent_org_unit_id: parent } },
      { kind: "assignment", target_id: person, values: { org_unit_id: created } },
    ]);
    const from = sim.units.find((u) => u.id === unit)!;
    const to = sim.units.find((u) => u.id === created)!;
    expect(to).toMatchObject({ state: "created", before: 0, after: 1, parent_id: parent });
    expect(from.after).toBe(from.before - 1);
    expect(sim.moves).toHaveLength(1);
    expect(dir.assignmentsAt(data.people.find((p) => p.id === person)!, dir.businessDate).find((a) => a.type === "primary")!.org_unit_id).toBe(unit);
  });

  it("所属者が残る組織の廃止は警告にする", () => {
    const { unit } = leafWithMember();
    const sim = simulateDemo(dir, dir.businessDate, [{ kind: "org_abolish", target_id: unit, values: {} }]);
    expect(sim.units.find((u) => u.id === unit)).toMatchObject({ state: "abolished", after: 0 });
    expect(sim.warnings.some((w) => w.index === -1)).toBe(true);
  });

  it("申請版は固定され、承認の記録の後にだけ登録できる", async () => {
    const field = createDemoField(new DemoDirectory(data), later);
    const { person, unit } = leafWithMember();
    const plan = await field.createPlan({
      title: "来期案",
      note: null,
      base_date: dir.businessDate,
      effective_date: dir.businessDate,
      changes: [{ kind: "assignment", target_id: person, values: { org_unit_id: unit } }],
    });
    const v = await field.freeze(plan.id);
    await field.updatePlan(plan.id, { title: "来期案", note: null, base_date: dir.businessDate, effective_date: dir.businessDate, changes: [] });
    expect((await field.versions(plan.id))[0]!.changes).toHaveLength(1);
    await expect(field.registerPlan(plan.id, v.version_no)).rejects.toMatchObject({ status: 409 });
    await field.recordApproval(plan.id, v.version_no, dir.businessDate, "WF-1");
    expect((await field.registerPlan(plan.id, v.version_no)).registration?.status).toBe("applied");
  });

  it("組織を親 → 子の順に並べ、深さを付ける", () => {
    const ordered = orderUnits(dir.orgTree(dir.businessDate).units);
    const position = new Map(ordered.map((x, i) => [x.unit.id, i]));
    for (const { unit } of ordered) {
      if (unit.parent_id && position.has(unit.parent_id)) expect(position.get(unit.parent_id)!).toBeLessThan(position.get(unit.id)!);
    }
  });
});
