import * as THREE from "three";
import { describe, expect, it } from "vitest";
import type { OrgTree, OrgUnit } from "../data/types";
import demoData from "../demo/demo-data.json";
import { DemoDirectory } from "../demo/directory";
import type { DemoDataset } from "../demo/model";
import { generateLargeDataset } from "../demo/largeDataset";
import { fitFocus, minFocusDistance } from "../features/overview3d/camera3d";
import { layout3d, layoutFlat, type Scene3D } from "../features/overview3d/layout3d";
import { buildForest } from "../lib/orgTree";

const unit = (id: string, parent: string | null): OrgUnit => ({
  id,
  legal_entity: { id: "E", code: "E", name: "法人E" },
  code: id,
  name: `組織${id}`,
  short_name: null,
  level: null,
  parent_id: parent,
  head_person_id: null,
  site: null,
});

/** 6 段の組織（上から 3・3・2・2・2・2 に分かれる。282 組織）。 */
function sixLevels(): OrgTree {
  const units: OrgUnit[] = [];
  const grow = (parent: string | null, depth: number) => {
    const n = depth <= 2 ? 3 : 2;
    for (let i = 0; i < n; i++) {
      const id = `${parent ?? "R"}-${i}`;
      units.push(unit(id, parent));
      if (depth < 6) grow(id, depth + 1);
    }
  };
  grow(null, 1);
  return { as_of: "2026-10-01", units };
}

const camera = () => new THREE.PerspectiveCamera(40, 1440 / 800, 0.5, 8000);
const DIRS = { 立体: new THREE.Vector3(0, 0.58, 1).normalize(), 平面: new THREE.Vector3(0, 1, 0.3).normalize() };

type Node = Scene3D["nodes"][number]["node"];
const heightOfTree = (n: Node): number => (n.children.length === 0 ? 0 : 1 + Math.max(...n.children.map(heightOfTree)));

/** 根から、いちばん深い枝をたどった各段の組織を選んだときのカメラの距離。 */
function distancesAlongDeepestPath(scene: Scene3D, dir: THREE.Vector3): number[] {
  const byId = new Map(scene.nodes.map((p) => [p.node.id, p]));
  const out: number[] = [];
  const deepest = (list: Node[]) => [...list].sort((a, b) => heightOfTree(b) - heightOfTree(a))[0];
  let node = deepest(scene.nodes.filter((p) => p.node.kind === "legal_entity").flatMap((p) => p.node.children));
  while (node) {
    out.push(fitFocus(camera(), scene, byId, { id: node.id, links: [], upperPairs: [] }, dir).distance);
    node = deepest(node.children);
  }
  return out;
}

describe("組織を選んだときのカメラ（fitFocus）", () => {
  const demo = new DemoDirectory(generateLargeDataset());
  const demoForest = buildForest(demo.orgTree(demo.businessDate), demo.stats(demo.businessDate));
  const deepForest = buildForest(sixLevels(), null);
  const small = new DemoDirectory(demoData as DemoDataset);
  const smallForest = buildForest(small.orgTree(small.businessDate), small.stats(small.businessDate));
  // 最後の値は、1 段で近づく倍率の上限。平面の 1 万人は、輪が大きく配下が遠い外の輪に広がるので、配下のある組織ではほとんど寄らず、
  // 配下の無い組織で一気に寄る（放射状の配置の性質。カメラでは均せない）ため、倍率は確かめない
  const cases: Array<[string, Scene3D, THREE.Vector3, number]> = [
    ["350 人・立体", layout3d(smallForest), DIRS.立体, 3],
    ["350 人・平面", layoutFlat(smallForest), DIRS.平面, 3],
    ["1 万人・立体", layout3d(demoForest), DIRS.立体, 3],
    ["1 万人・平面", layoutFlat(demoForest), DIRS.平面, Infinity],
    ["6 段・立体", layout3d(deepForest), DIRS.立体, 3],
    ["6 段・平面", layoutFlat(deepForest), DIRS.平面, 3],
  ];

  it.each(cases)("%s: 段を下るほど近づき、寄りすぎず、1 段ごとの倍率の変化が急でない", (_name, scene, dir, maxStep) => {
    const d = distancesAlongDeepestPath(scene, dir);
    for (let i = 0; i < d.length; i++) {
      // これより近づかない
      expect(d[i]!).toBeGreaterThanOrEqual(minFocusDistance(scene) - 1e-6);
      if (i === 0) continue;
      // 下の段へ進んで遠ざかることはなく、1 段で maxStep 倍を超えて近づかない
      expect(d[i]!).toBeLessThanOrEqual(d[i - 1]! + 1e-6);
      expect(d[i - 1]! / d[i]!).toBeLessThanOrEqual(maxStep);
    }
  });

  it("兼務・出向の相手先が遠いときは、相手先まで引かない（近いときは収める）", () => {
    const scene = layoutFlat(demoForest);
    const byId = new Map(scene.nodes.map((p) => [p.node.id, p]));
    const orgs = scene.nodes.filter((p) => p.node.kind === "org" && p.node.children.length === 0);
    const self = orgs[0]!;
    const far = [...orgs].sort((a, b) => Math.hypot(b.x - self.x, b.z - self.z) - Math.hypot(a.x - self.x, a.z - self.z))[0]!;
    const sibling = scene.nodes.find((p) => p.node.parentId === self.node.parentId && p !== self);
    const alone = fitFocus(camera(), scene, byId, { id: self.node.id, links: [], upperPairs: [] }, DIRS.平面).distance;
    const link = (to: string) => [{ from: self.node.id, to, kind: "concurrent" as const, count: 1 }];
    expect(fitFocus(camera(), scene, byId, { id: self.node.id, links: link(far.node.id), upperPairs: [] }, DIRS.平面).distance).toBeCloseTo(alone, 6);
    expect(alone).toBeLessThan(scene.extent * 0.5);
    if (sibling) {
      const near = fitFocus(camera(), scene, byId, { id: self.node.id, links: link(sibling.node.id), upperPairs: [] }, DIRS.平面).distance;
      expect(near).toBeLessThanOrEqual(alone * 1.35 + 1e-6);
    }
  });
});
