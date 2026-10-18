import { describe, expect, it } from "vitest";
import type { OrgStats, OrgTree, OrgUnit, SiteRef } from "../data/types";
import demoData from "../demo/demo-data.json";
import { DemoDirectory } from "../demo/directory";
import type { DemoDataset } from "../demo/model";
import { buildForest } from "./orgTree";
import { partitionName, splitBySite } from "./siteSplit";

const site = (id: string, entity = "E", code = id, inherited = false): SiteRef => ({
  id,
  legal_entity_id: entity,
  code,
  name: `拠点${id}`,
  type: "branch",
  prefecture_code: "13",
  prefecture_name: "東京都",
  inherited,
});
const unit = (id: string, parent: string | null, s: SiteRef | null, entity = "E"): OrgUnit => ({
  id,
  legal_entity: { id: entity, code: entity, name: `法人${entity}` },
  code: id,
  name: `組織${id}`,
  short_name: null,
  level: null,
  parent_id: parent,
  head_person_id: null,
  site: s,
});
const statsOf = (headcount: Record<string, number>): OrgStats => ({
  asOf: "2026-10-01",
  links: [],
  headcount: Object.fromEntries(Object.entries(headcount).map(([k, v]) => [k, { primary: v, concurrent: 0, secondment: 0 }])),
});

describe("拠点で分ける", () => {
  // 法人 E: 本部 h（拠点 T）の下に部 a（T を引き継ぐ）と部 b（拠点 Y）。b の下に課 b1（Y を引き継ぐ）・課 b2（拠点 T に戻る）
  const T = site("T");
  const Y = site("Y");
  const tree: OrgTree = {
    as_of: "2026-10-01",
    units: [
      unit("h", null, T),
      unit("a", "h", { ...T, inherited: true }),
      unit("b", "h", Y),
      unit("b1", "b", { ...Y, inherited: true }),
      unit("b2", "b", T),
      unit("n", null, null),
    ],
  };
  const stats = statsOf({ h: 2, a: 5, b: 10, b1: 4, b2: 3, n: 1 });

  it("拠点ごとに区画を作り、親が別の拠点の組織は区画の根にして本来の上位を持つ", () => {
    const s = splitBySite(buildForest(tree, stats));
    expect(s.partitions.map((p) => [partitionName(p), p.roots.map((r) => r.id), p.orgCount, p.headcount])).toEqual([
      // 人数の多い順（Y = 14 > T = 10）。拠点なしは最後
      ["拠点Y", ["b"], 2, 14],
      ["拠点T", ["h", "b2"], 3, 10],
      ["拠点なし", ["n"], 1, 1],
    ]);
    // b の上位 h は同じ法人だが別の拠点 → b は Y の根。b2 の上位 b は Y → b2 は T の根（上位は b）
    expect(s.upperOf.get("b")).toMatchObject({ id: "h", name: "組織h", siteName: "拠点T", partitionKey: "site:E:T" });
    expect(s.upperOf.get("b2")).toMatchObject({ id: "b", siteName: "拠点Y", partitionKey: "site:E:Y" });
    // 法人の直下は上位として扱わない
    expect(s.upperOf.has("h")).toBe(false);
    expect(s.upperOf.has("n")).toBe(false);
    // 区画の中の親子だけが残る
    const h = s.forest.byId.get("h")!;
    expect(h.children.map((c) => c.id)).toEqual(["a"]);
    expect(s.forest.byId.get("b2")!.parentId).toBeNull();
    expect(s.forest.byId.get("b1")!.parentId).toBe("b");
    // 配下を含めた人数は区画の中で数え直す（元の木では h は 24）
    expect(h.totalPrimary).toBe(7);
    expect(s.forest.byId.get("b")!.totalPrimary).toBe(14);
    // 元の木は変えない
    expect(buildForest(tree, stats).byId.get("h")!.children).toHaveLength(2);
    // 区画の拠点は「引き継ぎ」ではない
    expect(s.partitions.every((p) => p.site === null || p.site.inherited === false)).toBe(true);
    // 深さは元の木のまま（3D の段の高さ・既定で開く深さを揃える）
    expect(s.forest.byId.get("b2")!.depth).toBe(3);
  });

  it("集計が無ければ組織の数で並べる", () => {
    const s = splitBySite(buildForest(tree, null));
    expect(s.partitions.map((p) => [partitionName(p), p.headcount])).toEqual([
      ["拠点T", null],
      ["拠点Y", null],
      ["拠点なし", null],
    ]);
  });

  it("法人が複数あれば、法人の順に区画をまとめ、拠点なしも法人ごとに分ける", () => {
    const L = site("L", "F");
    const multi: OrgTree = {
      as_of: "2026-10-01",
      units: [unit("x", null, T), unit("y", null, null), unit("p", null, L, "F"), unit("q", "p", null, "F"), unit("r", null, null, "F")],
    };
    const s = splitBySite(buildForest(multi, statsOf({ x: 1, y: 50, p: 2, q: 3, r: 100 })));
    expect(s.partitions.map((p) => [p.key, p.roots.map((r) => r.id)])).toEqual([
      ["site:E:T", ["x"]],
      ["nosite:E", ["y"]],
      ["site:F:L", ["p"]],
      ["nosite:F", ["q", "r"]],
    ]);
    expect(s.partitions.map((p) => p.legalEntityName)).toEqual(["法人E", "法人E", "法人F", "法人F"]);
    // 拠点なしの組織の上位（拠点あり）も分かる
    expect(s.upperOf.get("q")).toMatchObject({ id: "p", siteName: "拠点L" });
    // すべての組織がちょうど 1 つの区画に入る
    expect(s.forest.byId.size).toBe(5);
    expect(s.forest.roots.map((r) => r.id)).toEqual(["x", "y", "p", "q", "r"]);
  });

  it("時点（タイムマシン）で組織が増減すると区画の中身も変わる", () => {
    const dir = new DemoDirectory(demoData as unknown as DemoDataset);
    const at = (asOf: string) => splitBySite(buildForest(dir.orgTree(asOf), dir.stats(asOf)));
    const before = at("2025-03-31");
    const after = at("2025-04-01");
    const tokyo = (s: ReturnType<typeof at>) => s.partitions.find((p) => p.site?.name === "東京本社")!;
    const names = (s: ReturnType<typeof at>) => new Set([...s.forest.byId.values()].map((n) => n.name));
    // 営業第三部（と配下の第五営業課）は 2025-04-01 に廃止
    expect(names(before).has("営業第三部")).toBe(true);
    expect(names(after).has("営業第三部")).toBe(false);
    expect(names(after).has("第五営業課")).toBe(false);
    expect(tokyo(after).orgCount).toBeLessThan(tokyo(before).orgCount);
    // どの時点でも、組織はすべてちょうど 1 つの区画に入る
    for (const s of [before, after]) {
      expect(s.partitions.reduce((n, p) => n + p.orgCount, 0)).toBe(s.forest.byId.size);
    }
    // 区画は 2 法人の 4 拠点（法人のコード順 L → P。拠点なしは無い）
    expect(after.partitions.map(partitionName)).toEqual(["千葉物流センター", "大阪物流センター", "東京本社", "横浜工場"]);
  });
});
