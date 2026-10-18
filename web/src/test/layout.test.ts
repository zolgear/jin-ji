import { describe, expect, it } from "vitest";
import type { OrgTree, OrgUnit } from "../data/types";
import { DemoDirectory } from "../demo/directory";
import { generateLargeDataset } from "../demo/largeDataset";
import { NODE_HEIGHT, NODE_WIDTH, STACK_INDENT, labelPlacement, layoutChart, layoutSplitChart, type PlacedNode } from "../features/orgchart/layout";
import { layout3d, layout3dSplit, personDots } from "../features/overview3d/layout3d";
import { buildModule } from "../features/overview3d/modules3d";
import { TIER_COUNT, TIER_SHAPES, plateInnerOf, polygonArea, radiusAt, reachOf } from "../features/overview3d/shapes3d";
import { buildForest, crossSummary, entityNodeId } from "../lib/orgTree";
import { simpleSearchKey, validateQuery } from "../lib/search";
import { splitBySite } from "../lib/siteSplit";

const unit = (id: string, parent: string | null, code = id): OrgUnit => ({
  id,
  legal_entity: { id: "E", code: "E", name: "法人E" },
  code,
  name: `組織${id}`,
  short_name: null,
  level: null,
  parent_id: parent,
  head_person_id: null,
  site: null,
});

const tree: OrgTree = { as_of: "2026-10-01", units: [unit("A", null), unit("B", "A"), unit("C", "B"), unit("D", "A")] };

describe("buildForest", () => {
  it("法人ごとの根の下に組織を組み、配下の人数を合計する", () => {
    const forest = buildForest(tree, {
      asOf: "2026-10-01",
      headcount: { A: { primary: 1, concurrent: 0, secondment: 0 }, C: { primary: 3, concurrent: 1, secondment: 0 } },
      links: [],
    });
    expect(forest.roots).toHaveLength(1);
    expect(forest.roots[0]!.id).toBe(entityNodeId("E"));
    expect(forest.byId.get("A")!.totalPrimary).toBe(4);
    expect(forest.byId.get("C")!.depth).toBe(3);
  });

  it("循環した上位組織は法人の直下に置く", () => {
    const cyclic: OrgTree = { as_of: "2026-10-01", units: [unit("X", "Y"), unit("Y", "X")] };
    const forest = buildForest(cyclic, null);
    expect(forest.roots[0]!.children.map((c) => c.id).sort()).toEqual(["X", "Y"]);
  });
});

describe("crossSummary", () => {
  it("兼務・出向を「この組織に直接」と「配下を含む（外との間）」に分けて数える", () => {
    // A > B > C、A > D。E は別の上位組織
    const withE: OrgTree = { ...tree, units: [...tree.units, unit("E", null)] };
    const forest = buildForest(withE, {
      asOf: "2026-10-01",
      headcount: { C: { primary: 1, concurrent: 0, secondment: 0 }, E: { primary: 1, concurrent: 2, secondment: 0 }, D: { primary: 0, concurrent: 1, secondment: 0 } },
      links: [
        { from: "C", to: "E", kind: "concurrent", count: 2 }, // B の配下から外へ
        { from: "C", to: "D", kind: "concurrent", count: 1 }, // A の配下どうし
        { from: "E", to: "B", kind: "secondment", count: 1 }, // 外から B へ
      ],
    });
    const links = [
      { from: "C", to: "E", kind: "concurrent" as const, count: 2 },
      { from: "C", to: "D", kind: "concurrent" as const, count: 1 },
      { from: "E", to: "B", kind: "secondment" as const, count: 1 },
    ];
    const a = crossSummary(forest, links, "A");
    // A には直接の兼務・出向は無いが、配下を含めると外との間に兼務の送り出し 2・出向の受け入れ 1
    expect(a.direct).toEqual({ concurrentIn: 0, concurrentOut: 0, secondmentIn: 0, secondmentOut: 0 });
    expect(a.subtree).toEqual({ concurrentIn: 0, concurrentOut: 2, secondmentIn: 1, secondmentOut: 0 });
    expect(a.internal).toEqual({ concurrent: 1, secondment: 0 });
    // 図の線（閉じた A に寄せた線）の人数と、配下を含む数が一致する
    const edges = layoutChart(forest, new Map(), links).crossEdges.filter((e) => e.from === "A" || e.to === "A");
    const sum = (kind: string, dir: "from" | "to") => edges.filter((e) => e.kind === kind && e[dir] === "A").reduce((s, e) => s + e.count, 0);
    expect(sum("concurrent", "from")).toBe(a.subtree.concurrentOut);
    expect(sum("secondment", "to")).toBe(a.subtree.secondmentIn);
    const e = crossSummary(forest, links, "E");
    expect(e.direct.concurrentIn).toBe(2);
    expect(e.direct.secondmentOut).toBe(1);
  });
});

describe("layoutChart", () => {
  it("既定では法人の直下まで見せ、閉じた組織の兼務の線は見えている祖先に寄せる", () => {
    const forest = buildForest(tree, null);
    const layout = layoutChart(forest, new Map(), [{ from: "D", to: "C", kind: "concurrent", count: 2 }]);
    const ids = layout.nodes.map((n) => n.node.id);
    expect(ids).toContain("A");
    expect(ids).not.toContain("B");
    expect(layout.nodes.find((n) => n.node.id === "A")!.hiddenChildren).toBe(2);
    // A を開くと B・D が見え、C への兼務は B に寄せる
    const opened = layoutChart(forest, new Map([["A", true]]), [{ from: "D", to: "C", kind: "concurrent", count: 2 }]);
    expect(opened.crossEdges).toEqual([{ id: "x:concurrent:D>B", from: "D", to: "B", kind: "concurrent", count: 2, aggregated: true }]);
    // 両端が同じ組織に寄る線は描かない
    expect(layout.crossEdges).toEqual([]);
  });

  it("左から右: 相手の札のラベルは、右の列の札と重なるときは左に置く", () => {
    const forest = buildForest(tree, null);
    // A・B を開く: 列は 法人 | A | B・D | C
    const layout = layoutChart(forest, new Map([["A", true], ["B", true]]), [], "lr");
    const at = (id: string) => layout.nodes.find((n) => n.node.id === id)!;
    // B の右には C（同じ高さ）があるので左、C の右は空いているので右
    expect(labelPlacement(at("B"), "concurrent", "兼務先 1名", layout.nodes, "lr").side).toBe("left");
    expect(labelPlacement(at("C"), "concurrent", "兼務先 1名", layout.nodes, "lr")).toEqual({ side: "right", dx: 10, dy: 0 });
  });

  it("左から右: 開くと子が現れ、左から右へ深さの順に並ぶ", () => {
    const forest = buildForest(tree, null);
    const layout = layoutChart(forest, new Map([["A", true], ["B", true]]), [], "lr");
    const x = (id: string) => layout.nodes.find((n) => n.node.id === id)!.x;
    expect(x("C")).toBeGreaterThan(x("B"));
    expect(x("B")).toBeGreaterThan(x("A"));
  });
});

/** 札どうしが重なる組の数。 */
function overlaps(nodes: readonly PlacedNode[]): number {
  let count = 0;
  for (let i = 0; i < nodes.length; i++) {
    for (let j = i + 1; j < nodes.length; j++) {
      const a = nodes[i]!;
      const b = nodes[j]!;
      if (Math.abs(a.x - b.x) < NODE_WIDTH && Math.abs(a.y - b.y) < NODE_HEIGHT) count++;
    }
  }
  return count;
}

describe("layoutChart（上から下）", () => {
  // 法人E: A > B > (C)、A > D > (F, G, H)。D の配下はすべて末端なので縦に積む。法人F: P > Q
  const wide: OrgTree = {
    as_of: "2026-10-01",
    units: [
      unit("A", null),
      unit("B", "A"),
      unit("C", "B"),
      unit("D", "A"),
      unit("F", "D"),
      unit("G", "D"),
      unit("H", "D"),
      { ...unit("P", null), legal_entity: { id: "F", code: "F", name: "法人F" } },
      { ...unit("Q", "P"), legal_entity: { id: "F", code: "F", name: "法人F" } },
    ],
  };
  const all = (forest: ReturnType<typeof buildForest>) => new Map([...forest.byId.keys()].map((id) => [id, true]));

  it("既定の向きは上から下で、子は親の下に横一列に並び、親は子の真ん中の上に来る", () => {
    const forest = buildForest(wide, null);
    const layout = layoutChart(forest, new Map([["A", true]]));
    expect(layout.direction).toBe("tb");
    const at = (id: string) => layout.nodes.find((n) => n.node.id === id)!;
    expect(at("B").y).toBe(at("D").y);
    expect(at("B").y).toBeGreaterThan(at("A").y);
    expect(at("A").y).toBeGreaterThan(at(entityNodeId("E")).y);
    expect(at("D").x - at("B").x).toBeGreaterThanOrEqual(NODE_WIDTH);
    expect(at("A").x).toBeCloseTo((at("B").x + at("D").x) / 2);
    expect(layout.treeEdges.every((e) => !e.stacked)).toBe(true);
  });

  it("配下がすべて末端の組織なら、親の下に縦に積む（線は幹から子の左へ）", () => {
    const forest = buildForest(wide, null);
    const layout = layoutChart(forest, all(forest));
    const at = (id: string) => layout.nodes.find((n) => n.node.id === id)!;
    for (const id of ["F", "G", "H"]) expect(at(id).x).toBe(at("D").x + STACK_INDENT);
    expect(at("G").y).toBeGreaterThan(at("F").y);
    expect(at("H").y).toBeGreaterThan(at("G").y);
    expect(layout.treeEdges.filter((e) => e.stacked).map((e) => e.to).sort()).toEqual(["F", "G", "H"]);
    // 子が 1 つなら積まずに真下に置く
    expect(at("C").x).toBe(at("B").x);
    expect(layout.treeEdges.find((e) => e.to === "C")!.stacked).toBe(false);
    expect(overlaps(layout.nodes)).toBe(0);
  });

  it("法人が複数なら、法人ごとの木を上から順に積む", () => {
    const forest = buildForest(wide, null);
    const layout = layoutChart(forest, all(forest));
    const ofEntity = (le: string) => layout.nodes.filter((n) => n.node.legalEntityId === le);
    const bottomOfE = Math.max(...ofEntity("E").map((n) => n.y + NODE_HEIGHT / 2));
    const topOfF = Math.min(...ofEntity("F").map((n) => n.y - NODE_HEIGHT / 2));
    expect(topOfF).toBeGreaterThan(bottomOfE);
  });

  it("1 万人・組織 1,000 超をすべて開いても、札が重ならない", () => {
    const big = new DemoDirectory(generateLargeDataset());
    const forest = buildForest(big.orgTree(big.businessDate), big.stats(big.businessDate));
    const layout = layoutChart(forest, all(forest));
    expect(layout.nodes.length).toBe(forest.byId.size);
    expect(overlaps(layout.nodes)).toBe(0);
  });

  it("相手の札のラベルは札の上に置き、上に札があるときは横に逃がす", () => {
    const forest = buildForest(wide, null);
    const layout = layoutChart(forest, all(forest));
    const at = (id: string) => layout.nodes.find((n) => n.node.id === id)!;
    expect(labelPlacement(at("D"), "concurrent", "兼務先 1名", layout.nodes).side).toBe("above");
    // 縦に積んだ 2 つめの札の上には 1 つめの札がある
    expect(labelPlacement(at("G"), "concurrent", "兼務先 1名", layout.nodes).side).not.toBe("above");
  });
});

describe("layout3d", () => {
  it("1 万人・組織 1,000 超でも配置でき、人の点は組織ごとの上限で切る", () => {
    const big = new DemoDirectory(generateLargeDataset());
    const forest = buildForest(big.orgTree(big.businessDate), big.stats(big.businessDate));
    const started = performance.now();
    const scene = layout3d(forest);
    const dots = personDots(scene.nodes, 120);
    expect(scene.nodes.length).toBe(forest.byId.size);
    expect(dots.length / 3).toBeLessThanOrEqual(10000);
    expect(scene.layers.length).toBe(4);
    expect(performance.now() - started).toBeLessThan(2000);
  });

  it("同じ段の組織のモジュールは（底面の形の外接円で）重ならず、子は親の近くにまとまる", () => {
    const big = new DemoDirectory(generateLargeDataset());
    const forest = buildForest(big.orgTree(big.businessDate), big.stats(big.businessDate));
    const scene = layout3d(forest);
    const byDepth = new Map<number, typeof scene.nodes>();
    for (const p of scene.nodes) byDepth.set(p.node.depth, [...(byDepth.get(p.node.depth) ?? []), p]);
    let slack = Infinity;
    for (const list of byDepth.values()) {
      for (let i = 0; i < list.length; i++) {
        for (let j = i + 1; j < list.length; j++) {
          const a = list[i]!;
          const b = list[j]!;
          slack = Math.min(slack, Math.hypot(a.x - b.x, a.z - b.z) - a.reach - b.reach);
        }
      }
    }
    expect(slack).toBeGreaterThanOrEqual(0);
    // 最下層の組織は、親（課）の真下から離れすぎない（放射状の輪に並べない）
    const byId = new Map(scene.nodes.map((p) => [p.node.id, p]));
    const leaf = scene.nodes.find((p) => p.node.children.length === 0 && p.node.depth === 4)!;
    const parent = byId.get(leaf.node.parentId!)!;
    expect(Math.hypot(leaf.x - parent.x, leaf.z - parent.z)).toBeLessThan(10);
  });
});

describe("shapes3d / modules3d", () => {
  it("段ごとの底面の形はどれも面積 π（底面の面積＝主務の人数を形によらず保つ）", () => {
    for (let t = 0; t < TIER_COUNT; t++) {
      expect(polygonArea(TIER_SHAPES[t]!)).toBeCloseTo(Math.PI, 6);
      // 外接円は円より少しだけ大きい（場所取りの係数）
      expect(reachOf(t)).toBeGreaterThanOrEqual(1);
      expect(reachOf(t)).toBeLessThan(1.25);
      // 人の点は上面の板の内側に収まる
      expect(plateInnerOf(t)).toBeLessThan(radiusAt(t, 0.3) * 0.78);
    }
  });

  it("モジュールのジオメトリは段ごとに作れ、上面の板は上向き", () => {
    for (let t = 0; t < TIER_COUNT; t++) {
      const m = buildModule(t);
      expect(m.shell.getAttribute("position").count).toBeGreaterThan(0);
      const n = m.plate.getAttribute("normal");
      for (let i = 0; i < n.count; i++) expect(n.getY(i)).toBe(1);
      // 上面の板の三角形は上から見て反時計回り（表を上に向ける）
      const pos = m.plate.getAttribute("position");
      const idx = m.plate.getIndex()!;
      const [a, b, c] = [idx.getX(0), idx.getX(1), idx.getX(2)];
      const ux = pos.getX(b) - pos.getX(a), uz = pos.getZ(b) - pos.getZ(a);
      const vx = pos.getX(c) - pos.getX(a), vz = pos.getZ(c) - pos.getZ(a);
      expect(uz * vx - ux * vz).toBeGreaterThan(0);
    }
  });
});

describe("search", () => {
  it("簡易の検索キー", () => {
    expect(simpleSearchKey("ヤマモト　レン")).toBe("やまもとれん");
    expect(simpleSearchKey("ﾔﾏﾓﾄ")).toBe("やまもと");
    expect(validateQuery("a")).not.toBeNull();
    expect(validateQuery("山%")).not.toBeNull();
    expect(validateQuery("山本")).toBeNull();
  });
});

describe("拠点で分ける（配置）", () => {
  const big = new DemoDirectory(generateLargeDataset());
  const forest = buildForest(big.orgTree(big.businessDate), big.stats(big.businessDate));
  const split = splitBySite(forest);
  const all = new Map([...forest.byId.keys()].map((id) => [id, true]));

  for (const direction of ["tb", "lr"] as const) {
    it(`2D（${direction}）: すべて開いても札も区画の枠も重ならず、札は自分の区画の枠の中にある`, () => {
      const layout = layoutSplitChart(split, all, [], direction);
      expect(layout.nodes.length).toBe(split.forest.byId.size);
      expect(overlaps(layout.nodes)).toBe(0);
      const groups = layout.groups;
      expect(groups.length).toBe(split.partitions.length);
      for (let i = 0; i < groups.length; i++) {
        for (let j = i + 1; j < groups.length; j++) {
          const a = groups[i]!;
          const b = groups[j]!;
          const apart = a.x + a.width <= b.x || b.x + b.width <= a.x || a.y + a.height <= b.y || b.y + b.height <= a.y;
          expect(apart).toBe(true);
        }
      }
      const groupOf = new Map(groups.map((g) => [g.partition.key, g]));
      for (const n of layout.nodes) {
        const g = groupOf.get(split.partitionOf.get(n.node.id)!)!;
        expect(n.x).toBeGreaterThanOrEqual(g.x);
        expect(n.x + NODE_WIDTH).toBeLessThanOrEqual(g.x + g.width);
        expect(n.y - NODE_HEIGHT / 2).toBeGreaterThanOrEqual(g.y);
        expect(n.y + NODE_HEIGHT / 2).toBeLessThanOrEqual(g.y + g.height);
      }
      // 区画の中の親子だけが組織の上下の線になる
      expect(layout.treeEdges.every((e) => split.partitionOf.get(e.from) === split.partitionOf.get(e.to))).toBe(true);
    });
  }

  it("3D: 島どうしは床の上で重ならず、段の高さは元の木の深さのまま", () => {
    const scene = layout3dSplit(split);
    expect(scene.nodes.length).toBe(split.forest.byId.size);
    expect(scene.islands.length).toBe(split.partitions.length);
    for (let i = 0; i < scene.islands.length; i++) {
      for (let j = i + 1; j < scene.islands.length; j++) {
        const a = scene.islands[i]!;
        const b = scene.islands[j]!;
        expect(Math.hypot(a.x - b.x, a.z - b.z)).toBeGreaterThanOrEqual(a.radius + b.radius);
      }
    }
    const ys = new Map<number, Set<number>>();
    for (const p of scene.nodes) ys.set(p.node.depth, (ys.get(p.node.depth) ?? new Set()).add(p.y));
    for (const set of ys.values()) expect(set.size).toBe(1);
    // 区画の根には上位への線を引かない（拠点をまたぐ親子は選んだときだけ別に描く）
    expect(scene.segmentChildIds.every((id) => !split.upperOf.has(id))).toBe(true);
    expect(scene.floorY).toBeLessThan(Math.min(...scene.nodes.map((p) => p.y)));
  });
});
