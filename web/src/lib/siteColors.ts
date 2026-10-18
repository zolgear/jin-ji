// 組織図・3D の「拠点で色分け」の色の割り当て。
//
// 色（dataviz の categorical。src/index.css の --site-1〜3 と同じ値）:
// - 組織図・3D では、どの 2 つの組織も隣り合い得る（all-pairs）。dataviz の参照パレットが all-pairs で検証を通るのは先頭の 3 枠までなので、
//   色を付ける拠点は 3 つまでにし、残りは灰色の「その他」にまとめる（4 枠目の黄は 2 枠目の橙と区別できない。validate_palette.js --pairs all で確認）
// - 色を付けるのは人数（主務、配下を含まない組織ごとの合計）の多い拠点。同数は法人のコード・拠点コードの順
// - 色は拠点に付く（順位ではない）: 選んだ 3 つの中では法人のコード・拠点コードの順に枠を割り当て、順位の入れ替わりで塗り替えない
// - 色だけに頼らない: 札には拠点名を文字で出し、凡例にも名前と人数を出す。色の意味が重なるので、色分けの間は兼務・出向の線を出さない
import type { SiteRef } from "../data/types";
import type { OrgForest } from "./orgTree";

/** 色を付ける拠点の数（all-pairs で検証を通る枠の数）。 */
export const SITE_COLOR_SLOTS = 3;

/** CSS の色（枠の順）。「その他」は灰色。 */
export const SITE_COLOR_VARS = ["var(--site-1)", "var(--site-2)", "var(--site-3)"] as const;
export const SITE_OTHER_VAR = "var(--site-other)";

export interface SiteLegendEntry {
  site: SiteRef;
  /** 色の枠（0〜2）。null は「その他」。 */
  slot: number | null;
  /** 主務の人数（集計が無ければ組織の数）。 */
  weight: number;
  orgCount: number;
}

export interface SiteColoring {
  /** 色を付けた拠点（枠の順）。 */
  colored: SiteLegendEntry[];
  /** 「その他」にまとめた拠点。 */
  other: SiteLegendEntry[];
  /** 人数を数えたか（false なら weight は組織の数）。 */
  byHeadcount: boolean;
  /** 拠点の ID から枠（null は「その他」）。 */
  slotOf: Map<string, number | null>;
}

/** 組織図の組織の拠点から、色を付ける拠点を決める。 */
export function assignSiteColors(forest: OrgForest, entityCodeOf: (legalEntityId: string) => string = () => ""): SiteColoring {
  const bySite = new Map<string, SiteLegendEntry>();
  let byHeadcount = false;
  for (const node of forest.byId.values()) {
    if (node.kind !== "org" || !node.site) continue;
    const entry = bySite.get(node.site.id) ?? { site: { ...node.site, inherited: false }, slot: null, weight: 0, orgCount: 0 };
    entry.orgCount++;
    if (node.headcount) {
      byHeadcount = true;
      entry.weight += node.headcount.primary;
    }
    bySite.set(node.site.id, entry);
  }
  const all = [...bySite.values()];
  if (!byHeadcount) for (const e of all) e.weight = e.orgCount;
  const codeKey = (e: SiteLegendEntry) => `${entityCodeOf(e.site.legal_entity_id)}\u001f${e.site.code}`;
  const byCode = (a: SiteLegendEntry, b: SiteLegendEntry) => (codeKey(a) < codeKey(b) ? -1 : codeKey(a) > codeKey(b) ? 1 : 0);
  const ranked = [...all].sort((a, b) => b.weight - a.weight || byCode(a, b));
  const colored = ranked.slice(0, SITE_COLOR_SLOTS).sort(byCode);
  colored.forEach((e, i) => (e.slot = i));
  const other = ranked.slice(SITE_COLOR_SLOTS);
  return { colored, other, byHeadcount, slotOf: new Map(all.map((e) => [e.site.id, e.slot])) };
}

/** 拠点の CSS の色（拠点が無ければ null）。 */
export function siteColorVar(coloring: SiteColoring, siteId: string | null | undefined): string | null {
  if (!siteId) return null;
  const slot = coloring.slotOf.get(siteId);
  return slot === undefined ? null : slot === null ? SITE_OTHER_VAR : SITE_COLOR_VARS[slot]!;
}
