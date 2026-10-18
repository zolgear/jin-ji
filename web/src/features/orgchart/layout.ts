// 組織図の配置。d3-hierarchy の tree で、折りたたんだ枝は描かない。向きは 2 通り:
// - 上から下（既定）: 親が上、子がその下に横一列。法人が複数あれば、法人ごとの木を上から順に積む（横一列に並べると横に長くなりすぎるため）。
//   配下がすべて末端の組織（子を持たない）なら、横に並べずに親の下へ縦に積む（横に長くなるのを抑える。一般的な組織図の「ぶら下げ」）。
// - 左から右: 法人 → 本部 → 部 → 課 … を左から右へ。
// 「拠点で分ける」では、拠点ごとの区画の中で同じ向きに木を置き、区画の枠を棚詰めに並べる（layoutSplitChart）。
import { hierarchy, tree as d3tree } from "d3-hierarchy";
import type { OrgLink } from "../../data/types";
import type { OrgForest, OrgNode } from "../../lib/orgTree";
import type { SiteSplit, SitePartition } from "../../lib/siteSplit";

export const NODE_WIDTH = 232;
export const NODE_HEIGHT = 76;
const COLUMN_GAP = 72;
const ROW_GAP = 14;

/** 組織図の向き。tb = 上から下（既定）、lr = 左から右。 */
export type ChartDirection = "tb" | "lr";
export const DEFAULT_DIRECTION: ChartDirection = "tb";

// 上から下の寸法
/** 段の間（親子の線の横の線が通る）。 */
const LEVEL_GAP = 52;
/** 兄弟の札の間。 */
const SIBLING_GAP = 20;
/** 親の違う札（いとこ）の間。枝の切れ目が分かるように兄弟より広げる。 */
const SUBTREE_GAP = 44;
/** 法人ごとの木を上下に積むときの間。 */
const ENTITY_GAP = 88;
/** 縦に積む子の左端の、親の左端からのずれ（この間を縦の幹が通る）。 */
export const STACK_INDENT = 32;
/** 縦の幹の、親の左端からの位置。OrgNodeCard の幹の出口と同じ値。 */
export const STACK_TRUNK = 14;
/** 縦に積む子の札の間。 */
const STACK_GAP = 10;
/** 配下がすべて末端の組織で、この数以上なら縦に積む。 */
export const STACK_MIN = 2;

export interface PlacedNode {
  node: OrgNode;
  /** 札の左端。 */
  x: number;
  /** 札の上下の中央。 */
  y: number;
  /** 折りたたまれて見えない子の数（直下）。 */
  hiddenChildren: number;
}

export interface CrossEdge {
  id: string;
  from: string;
  to: string;
  kind: OrgLink["kind"];
  count: number;
  /** 閉じた組織の配下の分を、見えている祖先に寄せて数えたものを含むか。 */
  aggregated: boolean;
}

export interface TreeEdge {
  id: string;
  from: string;
  to: string;
  /** 縦に積んだ子への線か（親の下の幹から子の左へ入る）。 */
  stacked: boolean;
}

export interface ChartLayout {
  direction: ChartDirection;
  nodes: PlacedNode[];
  treeEdges: TreeEdge[];
  crossEdges: CrossEdge[];
  /** 拠点で分けたときの区画の枠（分けていなければ空）。 */
  groups: PlacedGroup[];
}

/** 既定で開く深さ（法人 = 0）。法人の直下の組織まで見せ、その下は閉じておく。 */
export const DEFAULT_OPEN_DEPTH = 1;

/** 開いているか: expanded に明示が無ければ深さで決める。 */
export function isOpen(node: OrgNode, expanded: ReadonlyMap<string, boolean>): boolean {
  return expanded.get(node.id) ?? node.depth < DEFAULT_OPEN_DEPTH;
}

/** 見えている直下の子を縦に積むか（すべて末端の組織で STACK_MIN 以上）。 */
function stacksChildren(node: OrgNode, expanded: ReadonlyMap<string, boolean>): boolean {
  return isOpen(node, expanded) && node.children.length >= STACK_MIN && node.children.every((c) => c.children.length === 0);
}

/** 左から右: 根の並び（roots）を仮の根の下に並べる。座標は根の列の左端を 0 とする。 */
function placeLeftToRight(roots: readonly OrgNode[], expanded: ReadonlyMap<string, boolean>): Pick<ChartLayout, "nodes" | "treeEdges"> {
  type Item = { node: OrgNode | null; children: Item[] };
  const toItem = (n: OrgNode): Item => ({ node: n, children: isOpen(n, expanded) ? n.children.map(toItem) : [] });
  const root = hierarchy<Item>({ node: null, children: roots.map(toItem) }, (d) => d.children);
  d3tree<Item>()
    .nodeSize([NODE_HEIGHT + ROW_GAP, NODE_WIDTH + COLUMN_GAP])
    .separation((a, b) => (a.parent === b.parent ? 1 : 1.25))(root);

  const nodes: PlacedNode[] = [];
  const treeEdges: TreeEdge[] = [];
  root.each((d) => {
    const n = d.data.node;
    if (!n) return;
    // d3 の tree は縦向き（x が横並び）なので、x と y を入れ替えて左から右へ置く。virtual root の分 1 段戻す
    nodes.push({
      node: n,
      x: (d.depth - 1) * (NODE_WIDTH + COLUMN_GAP),
      y: d.x ?? 0,
      hiddenChildren: isOpen(n, expanded) ? 0 : n.children.length,
    });
    if (d.parent?.data.node) {
      treeEdges.push({ id: `t:${d.parent.data.node.id}>${n.id}`, from: d.parent.data.node.id, to: n.id, stacked: false });
    }
  });
  return { nodes, treeEdges };
}

/**
 * 上から下: 根の並び（roots）を仮の根の下に横一列に並べる。top は一番上の段の札の上端。
 * 法人ごとの木は 1 本ずつ（根 1 つ）、拠点の区画では区画の中の根を横に並べる。
 */
function placeTopToBottom(
  roots: readonly OrgNode[],
  expanded: ReadonlyMap<string, boolean>,
  top: number,
): Pick<ChartLayout, "nodes" | "treeEdges"> & { bottom: number } {
  const level = NODE_HEIGHT + LEVEL_GAP;
  const stackStep = NODE_HEIGHT + STACK_GAP;
  // 縦に積む子の列は、d3 の上では「親の真下に縦に連なる幅の広い空の札」として場所を取る（隣の枝の札が列に食い込まないように）
  type Item = { node: OrgNode | null; width: number; children: Item[] };
  const stackWidth = NODE_WIDTH + 2 * STACK_INDENT;
  const spacer = (rows: number): Item => ({ node: null, width: stackWidth, children: rows > 1 ? [spacer(rows - 1)] : [] });
  const toItem = (n: OrgNode): Item => {
    if (!isOpen(n, expanded)) return { node: n, width: NODE_WIDTH, children: [] };
    if (stacksChildren(n, expanded)) {
      const height = n.children.length * stackStep - STACK_GAP;
      return { node: n, width: NODE_WIDTH, children: [spacer(Math.ceil(height / level))] };
    }
    return { node: n, width: NODE_WIDTH, children: n.children.map(toItem) };
  };

  const nodes: PlacedNode[] = [];
  const treeEdges: TreeEdge[] = [];
  // 仮の根（幅 0）の下に根を並べる。根どうしは別の枝なので、いとこと同じ間を空ける
  const root = hierarchy<Item>({ node: null, width: 0, children: roots.map(toItem) }, (d) => d.children);
  // nodeSize の横を 1 にして、separation で中心の間の距離を px で返す（札の幅が違う空の札を含むため）
  const placed = d3tree<Item>()
    .nodeSize([1, level])
    .separation((a, b) => (a.data.width + b.data.width) / 2 + (a.parent === b.parent && a.depth > 1 ? SIBLING_GAP : SUBTREE_GAP))(root);
  let bottom = top;
  placed.each((d) => {
    const n = d.data.node;
    if (!n) return;
    const x = d.x - NODE_WIDTH / 2;
    const y = top + (d.depth - 1) * level + NODE_HEIGHT / 2;
    nodes.push({ node: n, x, y, hiddenChildren: isOpen(n, expanded) ? 0 : n.children.length });
    bottom = Math.max(bottom, y + NODE_HEIGHT / 2);
    const parent = d.parent?.data.node;
    if (parent) treeEdges.push({ id: `t:${parent.id}>${n.id}`, from: parent.id, to: n.id, stacked: false });
    if (stacksChildren(n, expanded)) {
      n.children.forEach((c, i) => {
        const cy = y + level + i * stackStep;
        nodes.push({ node: c, x: x + STACK_INDENT, y: cy, hiddenChildren: 0 });
        treeEdges.push({ id: `t:${n.id}>${c.id}`, from: n.id, to: c.id, stacked: true });
        bottom = Math.max(bottom, cy + NODE_HEIGHT / 2);
      });
    }
  });
  return { nodes, treeEdges, bottom };
}

function layoutTopToBottom(forest: OrgForest, expanded: ReadonlyMap<string, boolean>): Pick<ChartLayout, "nodes" | "treeEdges"> {
  const nodes: PlacedNode[] = [];
  const treeEdges: TreeEdge[] = [];
  let top = 0;
  for (const entity of forest.roots) {
    const placed = placeTopToBottom([entity], expanded, top);
    nodes.push(...placed.nodes);
    treeEdges.push(...placed.treeEdges);
    top = placed.bottom + ENTITY_GAP;
  }
  return { nodes, treeEdges };
}

/** 兼務・出向の線: 見えない組織は、見えている最も近い祖先（forest の上で）に寄せて集計する。 */
function crossEdgesOf(forest: OrgForest, nodes: readonly PlacedNode[], links: readonly OrgLink[]): CrossEdge[] {
  const visible = new Set(nodes.map((n) => n.node.id));
  const visibleAncestor = (id: string): string | null => {
    let current = forest.byId.get(id);
    while (current && !visible.has(current.id)) {
      current = current.parentId ? forest.byId.get(current.parentId) : undefined;
    }
    return current?.id ?? null;
  };
  const merged = new Map<string, CrossEdge>();
  for (const link of links) {
    const from = visibleAncestor(link.from);
    const to = visibleAncestor(link.to);
    if (!from || !to || from === to) continue;
    const id = `x:${link.kind}:${from}>${to}`;
    const edge = merged.get(id) ?? { id, from, to, kind: link.kind, count: 0, aggregated: false };
    edge.count += link.count;
    if (from !== link.from || to !== link.to) edge.aggregated = true;
    merged.set(id, edge);
  }
  return [...merged.values()];
}

export function layoutChart(
  forest: OrgForest,
  expanded: ReadonlyMap<string, boolean>,
  links: OrgLink[] = [],
  direction: ChartDirection = DEFAULT_DIRECTION,
): ChartLayout {
  const { nodes, treeEdges } = direction === "tb" ? layoutTopToBottom(forest, expanded) : placeLeftToRight(forest.roots, expanded);
  return { direction, nodes, treeEdges, crossEdges: crossEdgesOf(forest, nodes, links), groups: [] };
}

// ---- 拠点で分ける ----

/** 区画の枠の内側の余白。 */
export const GROUP_PAD = 22;
/** 区画の見出しの高さ（枠の上端から札の並びの上端まで）。 */
export const GROUP_HEAD = 60;
/** 区画どうしの間。 */
const GROUP_GAP = 36;
/** 区画の枠の最小の幅（見出しが収まるように）。 */
const GROUP_MIN_WIDTH = NODE_WIDTH + 2 * GROUP_PAD + 40;

export interface PlacedGroup {
  partition: SitePartition;
  /** 枠の左上と大きさ。 */
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * 拠点で分けた組織図の配置。区画ごとに木を置き（上から下では区画の中の根を横一列、左から右では縦一列）、
 * 区画の枠を棚詰め（左から右へ並べ、決めた幅を超えたら次の行）にする。並びは区画の順（法人の順・人数の多い順）で、
 * 法人が変わったら行を改める（法人ごとのまとまりが分かるように）。
 * 行の幅は、区画の面積の合計から横長（およそ 16:10）になる幅にし、一番幅の広い区画より狭くはしない。
 */
export function layoutSplitChart(
  split: SiteSplit,
  expanded: ReadonlyMap<string, boolean>,
  links: OrgLink[] = [],
  direction: ChartDirection = DEFAULT_DIRECTION,
): ChartLayout {
  const boxes = split.partitions.map((partition) => {
    const placed = direction === "tb" ? placeTopToBottom(partition.roots, expanded, 0) : placeLeftToRight(partition.roots, expanded);
    let [x0, y0, x1, y1] = [Infinity, Infinity, -Infinity, -Infinity];
    for (const n of placed.nodes) {
      x0 = Math.min(x0, n.x);
      x1 = Math.max(x1, n.x + NODE_WIDTH);
      y0 = Math.min(y0, n.y - NODE_HEIGHT / 2);
      y1 = Math.max(y1, n.y + NODE_HEIGHT / 2);
    }
    if (placed.nodes.length === 0) [x0, y0, x1, y1] = [0, 0, 0, 0];
    const width = Math.max(GROUP_MIN_WIDTH, x1 - x0 + 2 * GROUP_PAD);
    const height = y1 - y0 + GROUP_HEAD + GROUP_PAD;
    return { partition, placed, x0, y0, width, height };
  });
  const area = boxes.reduce((s, b) => s + (b.width + GROUP_GAP) * (b.height + GROUP_GAP), 0);
  const rowWidth = Math.max(Math.sqrt(area * 1.6), ...boxes.map((b) => b.width));

  const nodes: PlacedNode[] = [];
  const treeEdges: TreeEdge[] = [];
  const groups: PlacedGroup[] = [];
  let cx = 0;
  let cy = 0;
  let rowHeight = 0;
  for (const b of boxes) {
    const entityChanged = groups.length > 0 && groups[groups.length - 1]!.partition.legalEntityId !== b.partition.legalEntityId;
    if (cx > 0 && (cx + b.width > rowWidth || entityChanged)) {
      cx = 0;
      cy += rowHeight + GROUP_GAP;
      rowHeight = 0;
    }
    groups.push({ partition: b.partition, x: cx, y: cy, width: b.width, height: b.height });
    // 札の並びを枠の中（見出しの下）へ。札の並びが枠より狭いとき（見出しの最小の幅）は左に寄せる
    const dx = cx + GROUP_PAD - b.x0;
    const dy = cy + GROUP_HEAD - b.y0;
    for (const n of b.placed.nodes) nodes.push({ ...n, x: n.x + dx, y: n.y + dy });
    treeEdges.push(...b.placed.treeEdges);
    cx += b.width + GROUP_GAP;
    rowHeight = Math.max(rowHeight, b.height);
  }
  return { direction, nodes, treeEdges, crossEdges: crossEdgesOf(split.forest, nodes, links), groups };
}

/**
 * 兼務・出向の線の出入り口の位置（札の辺の割合）。左から右では右の辺の上からの割合、上から下では上の辺の左からの割合。
 * 種類ごとに分け、組織の上下の線（右の中央・上の中央）とも重ねない。
 */
export const CROSS_HANDLE_RATIO = { concurrent: 0.22, secondment: 0.82 } as const;
const LABEL_HEIGHT = 20;
const LABEL_OFFSET = 10;
/** 上から下で、ラベルを札の上に置くときの札との間。 */
const LABEL_ABOVE_OFFSET = 4;

/** ラベルの幅の見積もり（11px の文字。全角は 11px、半角は約 6.2px。線の見本と余白を足す）。 */
export function estimateLabelWidth(text: string): number {
  let w = 0;
  for (const ch of text) w += /[\x20-\x7e]/.test(ch) ? 6.2 : 11;
  return Math.ceil(w) + 14 + 4 + 12 + 2;
}

/** ラベルの置き場所。side は札のどちら側か、dx・dy は線の出入り口からのずれ（ラベルの基準点。CrossEdge が side に応じて寄せる）。 */
export interface LabelPlacement {
  side: "right" | "left" | "above";
  dx: number;
  dy: number;
}

/**
 * 相手の組織の札に添える人数のラベルを、札のどこに置くか。他の札と重ならない最初の候補を選び、どれも重なるときは最初の候補
 * （ラベルは札より手前に出す）。
 * - 左から右: 右 → 左。右には次の列の札があることが多い（列の間は COLUMN_GAP しかない）ので、そのときは左。
 * - 上から下: 上 → 右 → 左。出入り口が上の辺にあるので、まず札の上（段の間）に置く。縦に積んだ札は上に別の札があるので横に逃がす。
 */
export function labelPlacement(
  partner: PlacedNode,
  kind: OrgLink["kind"],
  text: string,
  nodes: readonly PlacedNode[],
  direction: ChartDirection = DEFAULT_DIRECTION,
): LabelPlacement {
  const width = estimateLabelWidth(text);
  const hits = (left: number, right: number, top: number, bottom: number) =>
    nodes.some(
      (n) =>
        n.node.id !== partner.node.id &&
        left < n.x + NODE_WIDTH &&
        right > n.x &&
        top < n.y + NODE_HEIGHT / 2 &&
        bottom > n.y - NODE_HEIGHT / 2,
    );
  const cardTop = partner.y - NODE_HEIGHT / 2;
  // 線の出入り口
  const hx = direction === "tb" ? partner.x + NODE_WIDTH * CROSS_HANDLE_RATIO[kind] : partner.x + NODE_WIDTH;
  const hy = direction === "tb" ? cardTop : cardTop + NODE_HEIGHT * CROSS_HANDLE_RATIO[kind];
  // 横に置くときの高さ（上から下では札の上寄り。左から右では出入り口と同じ高さ）
  const sideY = direction === "tb" ? cardTop + NODE_HEIGHT * 0.22 : hy;
  const right = partner.x + NODE_WIDTH + LABEL_OFFSET;
  const left = partner.x - LABEL_OFFSET;
  const candidates: Array<{ place: LabelPlacement; box: [number, number, number, number] }> = [
    {
      place: { side: "right", dx: right - hx, dy: sideY - hy },
      box: [right, right + width, sideY - LABEL_HEIGHT / 2, sideY + LABEL_HEIGHT / 2],
    },
    {
      place: { side: "left", dx: left - hx, dy: sideY - hy },
      box: [left - width, left, sideY - LABEL_HEIGHT / 2, sideY + LABEL_HEIGHT / 2],
    },
  ];
  if (direction === "tb") {
    // 出入り口のすぐ右から、札の上に置く（線の出だしを隠さない）
    const ax = hx + 6;
    const ay = cardTop - LABEL_ABOVE_OFFSET;
    candidates.unshift({ place: { side: "above", dx: ax - hx, dy: ay - hy }, box: [ax, ax + width, ay - LABEL_HEIGHT, ay] });
  }
  const free = candidates.find((c) => !hits(...c.box));
  return (free ?? candidates[0]!).place;
}
