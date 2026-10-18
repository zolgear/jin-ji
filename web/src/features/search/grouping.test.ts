import { describe, expect, it } from "vitest";
import type { FindPeopleQuery, Person, PersonSearchPage } from "../../data/types";
import demoData from "../../demo/demo-data.json";
import { DemoDirectory } from "../../demo/directory";
import type { DemoDataset } from "../../demo/model";
import { findPeople } from "../../demo/search";
import { groupPeople, REST_TITLE } from "./grouping";
import { effectiveSort, parseSearchState } from "./searchState";

const data = demoData as DemoDataset;
const dir = new DemoDirectory(data);
const today = data.meta.business_date;

/** 画面と同じ読み方（最初のページは件数つき、続きはカーソル）で全件を読む。 */
function load(group: "site" | "remote_work", filters: FindPeopleQuery["filters"] = {}): { first: PersonSearchPage; items: Person[] } {
  const sort = effectiveSort({ sort: "kana", group });
  const first = findPeople(dir, { asOf: today, filters, sort, facets: true, limit: 48 });
  const items = [...first.items];
  let cursor = first.nextCursor;
  while (cursor) {
    const page = findPeople(dir, { asOf: today, filters, sort, facets: false, cursor, limit: 48 });
    items.push(...page.items);
    cursor = page.nextCursor;
  }
  return { first, items };
}

describe("検索の結果のグループ化", () => {
  it("拠点ごと: ページをまたいでも同じ拠点は 1 つの区切りにまとまり、見出しの人数は件数と一致する。見えない人は最後", () => {
    const { first, items } = load("site");
    const groups = groupPeople(items, "site", first.facets, first.total);
    expect(new Set(groups.map((g) => g.key)).size).toBe(groups.length);
    for (const g of groups) expect(g.total).toBe(g.people.length);
    const rest = groups.at(-1)!;
    expect(rest.kind).toBe("rest");
    expect(rest.title).toBe(REST_TITLE.site);
    // 内部監査部の人（拠点が見えない）は最後の区切りに入る
    expect(rest.people.every((p) => !p.visible_fields.includes("site") || p.site === null)).toBe(true);
    // 最初のページだけでも、見出しの人数は全体の人数（読み込んだ人数ではない）
    const partial = groupPeople(first.items, "site", first.facets, first.total);
    expect(partial.at(-1)!.total).toBeGreaterThan(partial.at(-1)!.people.length);
  });

  it("リモートワーク: 対象 → 対象外 → 表示なし の順で、件数の合計は全体の人数", () => {
    const { first, items } = load("remote_work");
    const groups = groupPeople(items, "remote_work", first.facets, first.total);
    expect(groups.map((g) => g.title)).toEqual(["リモートワーク対象", "対象外", REST_TITLE.remote_work]);
    expect(groups.reduce((n, g) => n + (g.total ?? 0), 0)).toBe(first.total);
    expect(groups[0]!.people.every((p) => p.remote_work === true)).toBe(true);
  });

  it("絞り込みと組み合わせても区切りの人数は結果の人数と一致する", () => {
    const { first, items } = load("site", { remoteWork: true });
    const groups = groupPeople(items, "site", first.facets, first.total);
    expect(groups.reduce((n, g) => n + g.people.length, 0)).toBe(first.total);
    for (const g of groups) expect(g.total).toBe(g.people.length);
  });

  it("グループ化しないときは 1 つ（見出しなし）。URL の group から読み戻せる", () => {
    const { first, items } = load("site");
    expect(groupPeople(items, "none", first.facets, first.total)).toHaveLength(1);
    expect(parseSearchState(new URLSearchParams("group=site")).group).toBe("site");
  });
});
