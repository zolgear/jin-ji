// 組織図・3D の「拠点で分ける」: 拠点ごとに区画を作り、その拠点に属する組織だけで木を組み直す。
//
// 意味の決め方:
// - 区画は「法人 × 拠点」ごと。拠点は法人に属する（overview §4.7）ので、1 つの区画に別の法人の組織は混ざらない
// - 組織の拠点は自分の拠点か、上位から引き継いだ拠点（OrgSnapshot の解決と同じ。API の site をそのまま使う）
// - 上位の組織が同じ区画にあれば通常の親子。別の区画（別の拠点）にあれば、その組織は自分の区画の中で根になり、
//   本来の上位組織を upperOf に持つ（「上位: 〇〇部（東京本社）」の表示に使う。拠点をまたぐ親子を見失わないため）
// - 法人の直下の組織は区画の根になる（法人は区画の見出しに出すので、上位としては扱わない）
// - 拠点の無い組織は、法人ごとの「拠点なし」の区画にまとめる
// - 区画の並び: 法人の順（法人のコード順。通常の組織図と同じ）、その中で主務の人数の多い順（集計が無ければ組織の数）。
//   同数は拠点コードの順。「拠点なし」は法人の中で最後
// - 区画の中の木の配下を含めた人数（totalPrimary）は、その区画の中の配下だけで数え直す（見えている木と数を揃える）
import type { SiteRef } from "../data/types";
import type { OrgForest, OrgNode } from "./orgTree";

export interface SitePartition {
  /** 区画の ID（"site:<法人 ID>:<拠点 ID>" か "nosite:<法人 ID>"）。 */
  key: string;
  /** 拠点（inherited は false にした写し）。拠点なしの区画は null。 */
  site: SiteRef | null;
  legalEntityId: string;
  legalEntityName: string;
  legalEntityCode: string;
  /** 区画の中の木の根（並びは元の木の深さ優先の順）。 */
  roots: OrgNode[];
  orgCount: number;
  /** 主務の人数（配下を含まない組織ごとの合計。集計が無ければ null）。 */
  headcount: number | null;
}

/** 区画の根の、本来の上位組織（別の拠点にある）。 */
export interface CrossSiteUpper {
  id: string;
  name: string;
  levelName: string | null;
  /** 上位組織の拠点名（拠点なしなら null）。 */
  siteName: string | null;
  partitionKey: string;
}

export interface SiteSplit {
  partitions: SitePartition[];
  /** 区画の中だけで組み直した木（roots は区画の順・区画の中の根の順。法人のノードは含まない）。 */
  forest: OrgForest;
  /** 組織の ID から区画の ID。 */
  partitionOf: Map<string, string>;
  /** 区画の根の ID から、本来の上位組織（別の拠点）。 */
  upperOf: Map<string, CrossSiteUpper>;
}

export const partitionKeyOf = (node: OrgNode): string =>
  node.site ? `site:${node.legalEntityId}:${node.site.id}` : `nosite:${node.legalEntityId}`;

/** 法人ごとの組織の木を、拠点ごとの区画に分ける。 */
export function splitBySite(forest: OrgForest): SiteSplit {
  const partitionOf = new Map<string, string>();
  const partitions = new Map<string, SitePartition>();
  const entityOrder = new Map(forest.roots.map((r, i) => [r.legalEntityId, i]));
  const entityCode = new Map(forest.roots.map((r) => [r.legalEntityId, r.code]));
  const byId = new Map<string, OrgNode>();
  const upperOf = new Map<string, CrossSiteUpper>();
  let byHeadcount = false;

  // 元の木を深さ優先でたどり、区画の木のノード（写し）を作る。親が先に作られるので、子は親の写しにつなげられる
  const walk = (n: OrgNode) => {
    if (n.kind === "org") {
      const key = partitionKeyOf(n);
      partitionOf.set(n.id, key);
      let partition = partitions.get(key);
      if (!partition) {
        partition = {
          key,
          site: n.site ? { ...n.site, inherited: false } : null,
          legalEntityId: n.legalEntityId,
          legalEntityName: n.legalEntityName,
          legalEntityCode: entityCode.get(n.legalEntityId) ?? "",
          roots: [],
          orgCount: 0,
          headcount: null,
        };
        partitions.set(key, partition);
      }
      partition.orgCount++;
      if (n.headcount) {
        byHeadcount = true;
        partition.headcount = (partition.headcount ?? 0) + n.headcount.primary;
      }
      const original = n.parentId ? forest.byId.get(n.parentId) : undefined;
      const sameParent = original && original.kind === "org" && partitionOf.get(original.id) === key ? byId.get(original.id) : undefined;
      const copy: OrgNode = { ...n, children: [], parentId: sameParent?.id ?? null, totalPrimary: null };
      byId.set(n.id, copy);
      if (sameParent) sameParent.children.push(copy);
      else {
        partition.roots.push(copy);
        if (original && original.kind === "org") {
          upperOf.set(n.id, {
            id: original.id,
            name: original.name,
            levelName: original.levelName,
            siteName: original.site?.name ?? null,
            partitionKey: partitionOf.get(original.id)!,
          });
        }
      }
    }
    n.children.forEach(walk);
  };
  forest.roots.forEach(walk);

  // 区画の中の配下を含めた人数を数え直す
  const total = (n: OrgNode): number => {
    let sum = n.headcount?.primary ?? 0;
    for (const c of n.children) sum += total(c);
    n.totalPrimary = n.headcount ? sum : null;
    return sum;
  };
  for (const p of partitions.values()) p.roots.forEach(total);

  const weight = (p: SitePartition) => (byHeadcount ? (p.headcount ?? 0) : p.orgCount);
  const ordered = [...partitions.values()].sort(
    (a, b) =>
      (entityOrder.get(a.legalEntityId) ?? 0) - (entityOrder.get(b.legalEntityId) ?? 0) ||
      Number(a.site === null) - Number(b.site === null) ||
      weight(b) - weight(a) ||
      (a.site?.code ?? "").localeCompare(b.site?.code ?? ""),
  );
  return {
    partitions: ordered,
    forest: { roots: ordered.flatMap((p) => p.roots), byId },
    partitionOf,
    upperOf,
  };
}

/** 区画の見出しの名前（拠点名か「拠点なし」）。 */
export const partitionName = (p: SitePartition): string => p.site?.name ?? "拠点なし";
