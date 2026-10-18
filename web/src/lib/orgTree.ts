// 組織ツリー（API の平らな一覧）から、法人ごとの木を組む。段は法人ごとに可変なので、深さは木の形で決める。
import type { OrgHeadcount, OrgLink, OrgStats, OrgTree, OrgUnit, SiteRef } from "../data/types";

export interface OrgNode {
  /** 組織の ID。法人の根は "le:<法人 ID>"。 */
  id: string;
  kind: "legal_entity" | "org";
  name: string;
  code: string;
  /** 段の表示名（本部・部・課 など）。段を持たない組織は null。 */
  levelName: string | null;
  levelRank: number | null;
  legalEntityId: string;
  legalEntityName: string;
  headPersonId: string | null;
  parentId: string | null;
  children: OrgNode[];
  /** 法人の根を 0 とする深さ。 */
  depth: number;
  /** この組織に直接所属する人数（集計が無ければ null）。 */
  headcount: OrgHeadcount | null;
  /** 配下を含めた主務の人数（集計が無ければ null）。 */
  totalPrimary: number | null;
  /** 組織の拠点（自分の拠点か、上位組織から引き継いだ拠点）。法人の根と拠点の無い組織は null。 */
  site: SiteRef | null;
  unit: OrgUnit | null;
}

export interface OrgForest {
  roots: OrgNode[];
  byId: Map<string, OrgNode>;
}

export const entityNodeId = (legalEntityId: string) => `le:${legalEntityId}`;

function rankOf(unit: OrgUnit): number | null {
  const rank = unit.level?.rank;
  if (rank === undefined || rank === null) return null;
  const n = typeof rank === "number" ? rank : Number.parseInt(rank, 10);
  return Number.isFinite(n) ? n : null;
}

const collator = new Intl.Collator("ja");

/** 平らな一覧から法人ごとの木を組む。親が一覧に無い組織（循環・欠落）は法人の直下に置く。 */
export function buildForest(tree: OrgTree, stats: OrgStats | null): OrgForest {
  const byId = new Map<string, OrgNode>();
  const roots: OrgNode[] = [];
  for (const unit of tree.units) {
    const entityId = unit.legal_entity.id;
    const rootId = entityNodeId(entityId);
    if (!byId.has(rootId)) {
      const root: OrgNode = {
        id: rootId,
        kind: "legal_entity",
        name: unit.legal_entity.name ?? unit.legal_entity.code ?? "（法人名なし）",
        code: unit.legal_entity.code ?? "",
        levelName: null,
        levelRank: 0,
        legalEntityId: entityId,
        legalEntityName: unit.legal_entity.name ?? "",
        headPersonId: null,
        parentId: null,
        children: [],
        depth: 0,
        headcount: null,
        totalPrimary: null,
        site: null,
        unit: null,
      };
      byId.set(rootId, root);
      roots.push(root);
    }
    byId.set(unit.id, {
      id: unit.id,
      kind: "org",
      name: unit.name,
      code: unit.code,
      levelName: unit.level?.name ?? null,
      levelRank: rankOf(unit),
      legalEntityId: entityId,
      legalEntityName: unit.legal_entity.name ?? "",
      headPersonId: unit.head_person_id ?? null,
      parentId: null,
      children: [],
      depth: 0,
      headcount: stats ? (stats.headcount[unit.id] ?? { primary: 0, concurrent: 0, secondment: 0 }) : null,
      totalPrimary: null,
      site: unit.site ?? null,
      unit,
    });
  }
  const parentOf = new Map(tree.units.map((u) => [u.id, u.parent_id ?? null]));
  for (const unit of tree.units) {
    const node = byId.get(unit.id)!;
    const parent = unit.parent_id ? byId.get(unit.parent_id) : undefined;
    const sameEntity = parent && parent.legalEntityId === node.legalEntityId;
    const target = sameEntity && !createsCycle(parentOf, unit.id) ? parent : byId.get(entityNodeId(node.legalEntityId))!;
    node.parentId = target.id;
    target.children.push(node);
  }
  const sortChildren = (n: OrgNode, depth: number) => {
    n.depth = depth;
    n.children.sort((a, b) => collator.compare(a.code || "~", b.code || "~") || collator.compare(a.name, b.name));
    let total = n.headcount?.primary ?? 0;
    for (const c of n.children) {
      sortChildren(c, depth + 1);
      total += c.totalPrimary ?? 0;
    }
    n.totalPrimary = stats ? total : null;
  };
  roots.sort((a, b) => collator.compare(a.code, b.code));
  roots.forEach((r) => sortChildren(r, 0));
  return { roots, byId };
}

/** 上位組織をたどって自分に戻る（循環している）か。 */
function createsCycle(parentOf: Map<string, string | null>, id: string): boolean {
  let current = parentOf.get(id) ?? null;
  const seen = new Set<string>();
  while (current) {
    if (current === id || seen.has(current)) return true;
    seen.add(current);
    current = parentOf.get(current) ?? null;
  }
  return false;
}

/** ノードの祖先（自分を含まない、近い順）。 */
export function ancestorsOf(forest: OrgForest, id: string): OrgNode[] {
  const out: OrgNode[] = [];
  let current = forest.byId.get(id);
  while (current?.parentId) {
    const parent = forest.byId.get(current.parentId);
    if (!parent) break;
    out.push(parent);
    current = parent;
  }
  return out;
}

/** 深さ優先の全ノード。 */
export function flatten(forest: OrgForest): OrgNode[] {
  const out: OrgNode[] = [];
  const walk = (n: OrgNode) => {
    out.push(n);
    n.children.forEach(walk);
  };
  forest.roots.forEach(walk);
  return out;
}

/** 兼務・出向の受け入れ（相手が主務の側）と送り出し（自分が主務の側）の人数。 */
export interface CrossCounts {
  concurrentIn: number;
  concurrentOut: number;
  secondmentIn: number;
  secondmentOut: number;
}

export interface CrossSummary {
  /**
   * この組織に直接の分。受け入れはこの組織で兼務・出向している人（集計の headcount と同じ数）、
   * 送り出しはこの組織に主務があり、他の組織で兼務・出向している人。
   */
  direct: CrossCounts;
  /**
   * 配下を含めた組織のまとまり（自分 + 配下）と、その外の組織の間の分。
   * 組織図で閉じた組織の線・3D で選んだ組織の弧は、この数を示す（まとまりの内側どうしは線にしない）。
   */
  subtree: CrossCounts;
  /** まとまりの内側どうし（配下の組織の間）の兼務・出向。線には出ない。 */
  internal: { concurrent: number; secondment: number };
}

const zero = (): CrossCounts => ({ concurrentIn: 0, concurrentOut: 0, secondmentIn: 0, secondmentOut: 0 });

/** 自分と配下の組織の ID。 */
export function subtreeIds(forest: OrgForest, id: string): Set<string> {
  const out = new Set<string>();
  const walk = (n: OrgNode) => {
    out.add(n.id);
    n.children.forEach(walk);
  };
  const node = forest.byId.get(id);
  if (node) walk(node);
  return out;
}

/**
 * 組織の兼務・出向の人数を「この組織に直接」と「配下を含む（外との間）」に分けて数える。
 * 詳細パネルの数と図の線の数の意味を揃えるために使う。
 */
export function crossSummary(forest: OrgForest, links: readonly OrgLink[], id: string): CrossSummary {
  const node = forest.byId.get(id);
  const inside = subtreeIds(forest, id);
  const direct = zero();
  const subtree = zero();
  const internal = { concurrent: 0, secondment: 0 };
  // 受け入れの直接分は集計の人数（主務が無い人も含む）に合わせる
  direct.concurrentIn = node?.headcount?.concurrent ?? 0;
  direct.secondmentIn = node?.headcount?.secondment ?? 0;
  for (const l of links) {
    const out = l.kind === "concurrent" ? "concurrentOut" : "secondmentOut";
    const into = l.kind === "concurrent" ? "concurrentIn" : "secondmentIn";
    if (l.from === id) direct[out] += l.count;
    const fromIn = inside.has(l.from);
    const toIn = inside.has(l.to);
    if (fromIn && toIn) internal[l.kind] += l.count;
    else if (fromIn) subtree[out] += l.count;
    else if (toIn) subtree[into] += l.count;
  }
  return { direct, subtree, internal };
}
