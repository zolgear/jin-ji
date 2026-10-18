// 3D 組織俯瞰の配置。法人ごとに、組織の深さ（段）を高さの層として積む。
// 層の中の位置は d3-hierarchy の pack（円の詰め込み）で決める（cone tree の形）:
// 子は親の真下の円の中にまとまり、配下のまとまりごとに隙間が空く。
// 放射状の木（同じ深さを同じ半径の輪に並べる）だと、1 万人・組織 1,000 超では最下層の組織が細い輪に密集して読めないため。
// 人は 1 人ずつのオブジェクトにせず、組織ごとの集約（モジュールの底面の大きさ・人の点の数）で表す。
// 組織の底面の形は段ごとに違う（shapes3d.ts）。radius は底面と同じ面積の円の半径、場所取りは形の外接円（reach）で行う。
//
// 「拠点で分ける」（layout3dSplit）では、拠点の区画ごとに木を詰めた「島」を作り、島どうしを離して床の上に並べる。
// 段の高さ（y）は元の木の深さのまま（島が違っても同じ高さは同じ段）。法人の塔は置かない（島の見出しに法人名を出す）。
import { hierarchy, pack, packSiblings } from "d3-hierarchy";
import type { OrgForest, OrgNode } from "../../lib/orgTree";
import type { SitePartition, SiteSplit } from "../../lib/siteSplit";
import { PLATE_LEVEL, plateInnerOf, reachOf, tierOf } from "./shapes3d";

/** 層の間隔の最小（ワールド座標）。 */
export const LAYER_GAP = 7;
/** モジュールの周りに空ける余白（隣の組織と離す）。 */
const MARGIN = 0.55;

export interface Placed3D {
  node: OrgNode;
  x: number;
  y: number;
  z: number;
  /** 底面と同じ面積の円の半径（人数に応じて。面積＝主務の人数）。 */
  radius: number;
  /** 底面の形の外接円の半径（radius × 形ごとの係数）。隣の組織と重ならないように場所を取るのに使う。 */
  reach: number;
  /** 形の種類（0＝法人、1〜4＝組織の段。4 段目より深い組織は 4）。shapes3d.ts の TIER_SHAPES の添字。 */
  tier: number;
  /** 何番目の法人か（色分け用）。 */
  entityIndex: number;
}

export interface Layer3D {
  /** 何番目の法人か（拠点で分けたときは何番目の島か）。 */
  entityIndex: number;
  depth: number;
  x: number;
  z: number;
  y: number;
  radius: number;
  /** 段の表示名（その深さで最も多いもの）。 */
  label: string;
  /** その深さの組織がすべて同じ段名か。 */
  uniform: boolean;
  /** その深さの組織の数。 */
  orgCount: number;
}

/** モジュールの高さ。人数は底面の面積だけで表し、高さは一定にする（高さも変えると体積が人数の 2 乗で増えて見え、多い組織を誇張するため）。 */
export const ORG_HEIGHT = 0.8;
export const ENTITY_HEIGHT = 1.4;
export const heightOf = (p: Placed3D) => (p.node.kind === "legal_entity" ? ENTITY_HEIGHT : ORG_HEIGHT);

export interface Scene3D {
  nodes: Placed3D[];
  layers: Layer3D[];
  /** 親子の線（[x1,y1,z1,x2,y2,z2] の並び）。 */
  segments: Float32Array;
  /** segments の各線の子の組織の ID（強調する線を選ぶのに使う）。 */
  segmentChildIds: string[];
  /** 全体の中心と大きさ（カメラの初期位置用）。 */
  center: [number, number, number];
  extent: number;
  /** 床の高さ（一番下の段より下）。 */
  floorY: number;
  /** 拠点で分けたときの島（分けていなければ空）。 */
  islands: Island3D[];
}

/** 拠点の区画の島。床の上の円（中心と半径）と、島の中の一番上の段の高さ。 */
export interface Island3D {
  partition: SitePartition;
  /** 人数の多い順の順位（0 が真ん中の島）。Layer3D・Placed3D の entityIndex と同じ。 */
  index: number;
  x: number;
  z: number;
  radius: number;
  topY: number;
}

/** 底面と同じ面積の円の半径。面積が主務の人数に比例するように平方根をとる（兼務・出向は含めない。凡例と揃える）。 */
export function radiusFor(node: OrgNode): number {
  if (node.kind === "legal_entity") return 1.4;
  const own = node.headcount?.primary ?? 0;
  return 0.4 + 0.2 * Math.sqrt(own);
}

/** 底面の外接円の半径（pack の場所取り）。 */
const reachFor = (node: OrgNode) => radiusFor(node) * reachOf(tierOf(node));

/**
 * pack に渡す要素。self は「配下を持つ組織の自分のモジュール」の場所取り（その組織が子のまとまりより太いとき）。
 * node が null のものは、拠点の区画の根を束ねる仮の根（描かない）。
 */
interface PackItem {
  node: OrgNode | null;
  self?: boolean;
  children: PackItem[];
}

/** 配下を持つ組織のうち、自分のモジュールが子のまとまりの円に収まらないもの（場所取りの要素を足す）。 */
function packItems(top: PackItem) {
  return pack<PackItem>()
    .radius((d) => (d.data.node ? reachFor(d.data.node) : 0) + MARGIN)
    // まとまりの階層が上ほど隙間を広く（課の中のチームは詰め、部・本部の間は空ける）
    .padding((d) => 0.35 + 0.75 * Math.min(d.height - 1, 3))(hierarchy<PackItem>(top, (d) => d.children));
}

const toPackItem =
  (needsSelf: ReadonlySet<string>) =>
  (n: OrgNode): PackItem => {
    const children = n.children.map(toPackItem(needsSelf));
    if (children.length > 0 && needsSelf.has(n.id)) children.unshift({ node: n, self: true, children: [] });
    return { node: n, children };
  };

/** 木（roots を仮の根で束ねる。根が 1 つならそのまま）を 2 回詰める: 1 回目で自分のモジュールが収まらない組織を調べ、2 回目でその分の場所を取る。 */
function packRoots(roots: readonly OrgNode[]) {
  const build = (needsSelf: ReadonlySet<string>): PackItem => {
    const items = roots.map(toPackItem(needsSelf));
    return items.length === 1 ? items[0]! : { node: null, children: items };
  };
  let h = packItems(build(new Set()));
  const needsSelf = new Set<string>();
  h.each((d) => {
    const n = d.data.node;
    if (n && d.children && d.r < reachFor(n) + MARGIN) needsSelf.add(n.id);
  });
  if (needsSelf.size > 0) h = packItems(build(needsSelf));
  return h;
}

/** 層（段）の円盤（組織の深さごと）。段の名前は、その深さで最も多い段の表示名。 */
function layersOf(
  h: ReturnType<typeof packRoots>,
  entityIndex: number,
  cx: number,
  cz: number,
  gap: number,
  layerRadius: ReadonlyMap<number, number>,
): Layer3D[] {
  const byDepth = new Map<number, { names: Map<string, number>; orgCount: number }>();
  h.each((d) => {
    const n = d.data.node;
    if (!n || d.data.self || n.kind !== "org") return;
    const entry = byDepth.get(n.depth) ?? { names: new Map(), orgCount: 0 };
    entry.orgCount++;
    if (n.levelName) entry.names.set(n.levelName, (entry.names.get(n.levelName) ?? 0) + 1);
    byDepth.set(n.depth, entry);
  });
  return [...byDepth.entries()]
    .sort((a, b) => a[0] - b[0])
    .map(([depth, { names, orgCount }]) => ({
      entityIndex,
      depth,
      x: cx,
      z: cz,
      y: -depth * gap,
      radius: (layerRadius.get(depth) ?? 0) + 1.5,
      label: [...names.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? "",
      uniform: names.size <= 1,
      orgCount,
    }));
}

export function layout3d(forest: OrgForest): Scene3D {
  const nodes: Placed3D[] = [];
  const layers: Layer3D[] = [];
  let offsetX = 0;

  forest.roots.forEach((root, entityIndex) => {
    const h = packRoots([root]);
    const ox = h.x;
    const oz = h.y;
    const outer = h.r + 1;
    const cx = offsetX + outer;
    // 広い法人ほど層の間隔も広げ、平たい円盤にならないようにする
    const gap = Math.max(LAYER_GAP, outer * 0.26);
    const layerRadius = new Map<number, number>();
    h.each((d) => {
      const n = d.data.node;
      if (!n || d.data.self) return;
      const placed: Placed3D = {
        node: n,
        x: cx + d.x - ox,
        y: -d.depth * gap,
        z: d.y - oz,
        radius: radiusFor(n),
        reach: reachFor(n),
        tier: tierOf(n),
        entityIndex,
      };
      nodes.push(placed);
      const reach = Math.hypot(placed.x - cx, placed.z) + placed.reach;
      layerRadius.set(d.depth, Math.max(layerRadius.get(d.depth) ?? 0, reach));
    });
    layers.push(...layersOf(h, entityIndex, cx, 0, gap, layerRadius));
    offsetX = cx + outer + 12;
  });
  return finish(nodes, layers, []);
}

/** 島どうしの間（床の上の円の縁どうし）。 */
const ISLAND_GAP = 14;

/**
 * 拠点で分けた配置。区画ごとに、区画の中の根を束ねて詰めた島を作り、島を円の詰め込み（packSiblings）で並べる。
 * 人数の多い島が真ん中に来て、残りがその周りを囲む。段の高さは元の木の深さ（全島で共通の間隔）。
 */
export function layout3dSplit(split: SiteSplit): Scene3D {
  const packed = split.partitions.map((partition) => {
    const h = packRoots(partition.roots);
    return { partition, h, outer: h.r + 1.5 };
  });
  // 段の間隔は島によらず共通（同じ高さが同じ段）。一番広い島に合わせつつ、広げすぎない
  const gap = Math.max(LAYER_GAP, Math.min(18, Math.max(0, ...packed.map((p) => p.outer)) * 0.22));
  // 円の詰め込みは先頭の円を真ん中に置くので、人数（集計が無ければ組織の数）の多い島から詰める
  const weight = (p: SitePartition) => p.headcount ?? p.orgCount;
  const order = packed.map((_, i) => i).sort((x, y) => weight(packed[y]!.partition) - weight(packed[x]!.partition) || x - y);
  // 間は島の大きさにも比例させる（大きな島どうしは、斜めから見ると段の輪が重なって見えるため）
  const circles = order.map((i) => ({ r: packed[i]!.outer * 1.12 + ISLAND_GAP / 2, x: 0, y: 0, i }));
  packSiblings(circles);
  const rankOf = new Map(circles.map((c, rank) => [c.i, rank]));
  const at = new Map(circles.map((c) => [c.i, c]));

  const nodes: Placed3D[] = [];
  const layers: Layer3D[] = [];
  const islands: Island3D[] = [];
  packed.forEach(({ partition, h, outer }, index) => {
    const cx = at.get(index)!.x;
    const cz = at.get(index)!.y;
    const rank = rankOf.get(index)!;
    const layerRadius = new Map<number, number>();
    let topY = -Infinity;
    h.each((d) => {
      const n = d.data.node;
      if (!n || d.data.self) return;
      const placed: Placed3D = {
        node: n,
        x: cx + d.x - h.x,
        y: -n.depth * gap,
        z: cz + d.y - h.y,
        radius: radiusFor(n),
        reach: reachFor(n),
        tier: tierOf(n),
        entityIndex: rank,
      };
      nodes.push(placed);
      topY = Math.max(topY, placed.y + heightOf(placed));
      const reach = Math.hypot(placed.x - cx, placed.z - cz) + placed.reach;
      layerRadius.set(n.depth, Math.max(layerRadius.get(n.depth) ?? 0, reach));
    });
    layers.push(...layersOf(h, rank, cx, cz, gap, layerRadius));
    islands.push({ partition, index: rank, x: cx, z: cz, radius: outer, topY: Number.isFinite(topY) ? topY : 0 });
  });
  return finish(nodes, layers, islands, gap);
}

/** 親子の線・全体の中心と大きさ・床の高さをまとめる。 */
function finish(nodes: Placed3D[], layers: Layer3D[], islands: Island3D[], gap?: number): Scene3D {
  const positions = new Map(nodes.map((p) => [p.node.id, p]));
  const segs: number[] = [];
  const segmentChildIds: string[] = [];
  for (const p of nodes) {
    const parent = p.node.parentId ? positions.get(p.node.parentId) : undefined;
    if (parent) {
      segs.push(parent.x, parent.y, parent.z, p.x, p.y + heightOf(p), p.z);
      segmentChildIds.push(p.node.id);
    }
  }

  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, minZ = Infinity, maxZ = -Infinity;
  for (const p of nodes) {
    minX = Math.min(minX, p.x); maxX = Math.max(maxX, p.x);
    minY = Math.min(minY, p.y); maxY = Math.max(maxY, p.y);
    minZ = Math.min(minZ, p.z); maxZ = Math.max(maxZ, p.z);
  }
  for (const i of islands) {
    minX = Math.min(minX, i.x - i.radius); maxX = Math.max(maxX, i.x + i.radius);
    minZ = Math.min(minZ, i.z - i.radius); maxZ = Math.max(maxZ, i.z + i.radius);
  }
  if (nodes.length === 0) {
    minX = maxX = minY = maxY = minZ = maxZ = 0;
  }
  // 床は一番下の段より、段の間隔の 0.8 倍だけ下
  const step = gap ?? (layers.length > 0 ? Math.abs(layers[0]!.y) / Math.max(1, layers[0]!.depth) : LAYER_GAP);
  const floorY = Math.min(0, ...layers.map((l) => l.y)) - step * 0.8;
  return {
    nodes,
    layers,
    segments: new Float32Array(segs),
    segmentChildIds,
    center: [(minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2],
    extent: Math.max(maxX - minX, maxZ - minZ, (maxY - minY) * 2, 10),
    floorY,
    islands,
  };
}

/**
 * 組織ごとの人の点の配置（モジュールの上面の板に黄金角の螺旋で並べる）。
 * 1 組織あたりの点は perOrgLimit まで（それ以上は底面の大きさで表す）。
 */
export function personDots(nodes: Placed3D[], perOrgLimit = 120): Float32Array {
  const out: number[] = [];
  const golden = Math.PI * (3 - Math.sqrt(5));
  for (const p of nodes) {
    const n = Math.min(p.node.headcount?.primary ?? 0, perOrgLimit);
    // 上面の板（縁の内側）に収まる半径
    const plate = p.radius * plateInnerOf(p.tier);
    for (let i = 0; i < n; i++) {
      const r = plate * Math.sqrt((i + 0.5) / Math.max(n, 1));
      const a = i * golden;
      out.push(p.x + r * Math.cos(a), p.y + heightOf(p) * PLATE_LEVEL + 0.03, p.z + r * Math.sin(a));
    }
  }
  return new Float32Array(out);
}

/** 点の集まりを囲む球（カメラを合わせるのに使う）。margin は底面の外接円の半径に足す余白。 */
export function boundingSphere(points: Placed3D[], margin = 1): { center: [number, number, number]; radius: number } {
  if (points.length === 0) return { center: [0, 0, 0], radius: 10 };
  let minX = Infinity, maxX = -Infinity, minY = Infinity, maxY = -Infinity, minZ = Infinity, maxZ = -Infinity;
  for (const p of points) {
    minX = Math.min(minX, p.x - p.reach); maxX = Math.max(maxX, p.x + p.reach);
    minY = Math.min(minY, p.y); maxY = Math.max(maxY, p.y + heightOf(p));
    minZ = Math.min(minZ, p.z - p.reach); maxZ = Math.max(maxZ, p.z + p.reach);
  }
  const c: [number, number, number] = [(minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2];
  let r = 0;
  for (const p of points) {
    const d = Math.hypot(p.x - c[0], p.y - c[1], p.z - c[2]) + p.reach + margin;
    r = Math.max(r, d);
  }
  return { center: c, radius: Math.max(r, 4) };
}
