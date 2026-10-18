import { describe, expect, it } from "vitest";
import type { OrgTree, OrgUnit, SiteRef } from "../data/types";
import { DemoDirectory } from "../demo/directory";
import { generateLargeDataset } from "../demo/largeDataset";
import { buildForest } from "./orgTree";
import { assignSiteColors, siteColorVar, SITE_COLOR_SLOTS, SITE_OTHER_VAR } from "./siteColors";

const site = (id: string, code = id): SiteRef => ({
  id,
  legal_entity_id: "E",
  code,
  name: `拠点${id}`,
  type: "branch",
  prefecture_code: "13",
  prefecture_name: "東京都",
  inherited: false,
});
const unit = (id: string, s: SiteRef | null): OrgUnit => ({
  id,
  legal_entity: { id: "E", code: "E", name: "法人E" },
  code: id,
  name: `組織${id}`,
  short_name: null,
  level: null,
  parent_id: null,
  head_person_id: null,
  site: s,
});

describe("拠点で色分け", () => {
  const sites = ["a", "b", "c", "d", "e"].map((x) => site(x, `S${x}`));
  const tree: OrgTree = { as_of: "2026-10-01", units: [...sites.map((s, i) => unit(`o${i}`, s)), unit("none", null)] };
  // 人数: d > b > e > a > c
  const headcount = { o0: 3, o1: 8, o2: 1, o3: 20, o4: 5 } as Record<string, number>;
  const stats = { asOf: "2026-10-01", links: [], headcount: Object.fromEntries(Object.entries(headcount).map(([k, v]) => [k, { primary: v, concurrent: 0, secondment: 0 }])) };

  it("人数の多い 3 拠点に色を付け、残りは「その他」。枠は拠点コードの順（順位の入れ替わりで塗り替えない）", () => {
    const c = assignSiteColors(buildForest(tree, stats));
    expect(SITE_COLOR_SLOTS).toBe(3);
    expect(c.colored.map((e) => [e.site.id, e.slot])).toEqual([["b", 0], ["d", 1], ["e", 2]]);
    expect(c.other.map((e) => e.site.id)).toEqual(["a", "c"]);
    expect(siteColorVar(c, "d")).toBe("var(--site-2)");
    expect(siteColorVar(c, "a")).toBe(SITE_OTHER_VAR);
    expect(siteColorVar(c, null)).toBeNull();
    // b と d の人数が入れ替わっても、同じ 3 拠点なら色は変わらない
    const swapped = { ...stats, headcount: { ...stats.headcount, o1: { primary: 30, concurrent: 0, secondment: 0 } } };
    expect(assignSiteColors(buildForest(tree, swapped)).colored.map((e) => [e.site.id, e.slot])).toEqual([["b", 0], ["d", 1], ["e", 2]]);
  });

  it("集計が無ければ組織の数で決める", () => {
    const c = assignSiteColors(buildForest(tree, null));
    expect(c.byHeadcount).toBe(false);
    expect(c.colored).toHaveLength(3);
    expect(c.colored.every((e) => e.weight === 1)).toBe(true);
  });

  it("合成 1 万人: 拠点は 3 色 + その他にまとまる", () => {
    const dir = new DemoDirectory(generateLargeDataset(3000));
    const c = assignSiteColors(buildForest(dir.orgTree(dir.businessDate), dir.stats(dir.businessDate)));
    expect(c.colored).toHaveLength(3);
    expect(c.other.length).toBeGreaterThan(10);
    expect(c.colored.some((e) => e.site.name === "東京本社")).toBe(true);
  });
});
