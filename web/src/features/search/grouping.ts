// 検索の結果のグループ化（拠点ごと・リモートワークの有無ごと）。
// API にはグループの並び（sort=site / remote_work）を渡すので、読み込んだ順に区切るだけでページをまたいでも区切りが連続する。
// - 人数はその条件の件数（facets。見える人の見える値だけ）から取り、読み込んだ人数ではなく全体の人数を見出しに出す
// - 拠点・リモートワークが見えない人と、拠点の無い人は最後に 1 つにまとめる（並びの上でも最後。値を推測させる区切りは作らない）
import type { Person, PersonFacets, SiteRef } from "../../data/types";
import { REMOTE_WORK_LABEL } from "../../lib/labels";
import type { Grouping } from "./searchState";

export interface ResultGroup {
  key: string;
  kind: "all" | "site" | "remote" | "rest";
  title: string;
  /** 見出しの補足（拠点の種別・都道府県）。 */
  site: SiteRef | null;
  /** リモートワークの区切りの値。 */
  remote: boolean | null;
  /** グループの全体の人数（件数から分かる場合）。分からなければ null（読み込んだ人数だけ出す）。 */
  total: number | null;
  people: Person[];
}

/** 最後にまとめるグループの名前。 */
export const REST_TITLE: Record<Exclude<Grouping, "none">, string> = {
  site: "拠点の表示なし（非公開・未登録）",
  remote_work: "リモートワークの表示なし（非公開）",
};

/** 件数の候補が上限で切られている可能性があるか（API の TextLimit）。 */
const FACET_LIMIT = 50;

export function groupPeople(items: Person[], group: Grouping, facets: PersonFacets | null, total: number | null): ResultGroup[] {
  if (group === "none") {
    return [{ key: "all", kind: "all", title: "", site: null, remote: null, total, people: items }];
  }
  const values = group === "site" ? (facets?.sites ?? []) : (facets?.remote_work ?? []);
  const counts = new Map(values.map((v) => [v.value, v.count]));
  // 件数の候補がすべて揃っているときだけ、残り（表示なし）の人数を引き算で出す
  const complete = facets !== null && total !== null && values.length < FACET_LIMIT;
  const restTotal = complete ? Math.max(0, total - values.reduce((n, v) => n + v.count, 0)) : null;

  const groups: ResultGroup[] = [];
  const byKey = new Map<string, ResultGroup>();
  for (const p of items) {
    let key: string;
    let make: () => ResultGroup;
    if (group === "site" && p.site) {
      const site = p.site;
      key = `site:${site.id}`;
      make = () => ({ key, kind: "site", title: site.name, site, remote: null, total: counts.get(site.id) ?? null, people: [] });
    } else if (group === "remote_work" && p.remote_work !== null) {
      const value = p.remote_work;
      key = `remote:${value}`;
      make = () => ({ key, kind: "remote", title: REMOTE_WORK_LABEL[String(value)]!, site: null, remote: value, total: counts.get(String(value)) ?? null, people: [] });
    } else {
      key = "rest";
      make = () => ({ key, kind: "rest", title: REST_TITLE[group], site: null, remote: null, total: restTotal, people: [] });
    }
    let g = byKey.get(key);
    if (!g) {
      g = make();
      byKey.set(key, g);
      groups.push(g);
    }
    g.people.push(p);
  }
  return groups;
}
