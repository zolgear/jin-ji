import { describe, expect, it } from "vitest";
import { ApiError, type FindPeopleQuery, type Person, type PersonFilters } from "../data/types";
import demoData from "./demo-data.json";
import { DemoDirectory } from "./directory";
import { generateLargeDataset } from "./largeDataset";
import type { DemoDataset } from "./model";
import { findPeople } from "./search";

const data = demoData as DemoDataset;
const dir = new DemoDirectory(data);
const today = data.meta.business_date;

const query = (filters: PersonFilters, extra: Partial<FindPeopleQuery> = {}): FindPeopleQuery => ({ asOf: today, filters, sort: "id", facets: false, limit: 100, ...extra });

/** 全ページを読む（件数なし）。 */
function readAll(d: DemoDirectory, filters: PersonFilters, sort: FindPeopleQuery["sort"] = "id"): Person[] {
  const all: Person[] = [];
  let cursor: string | null = null;
  do {
    const page = findPeople(d, query(filters, { sort, cursor }));
    all.push(...page.items);
    cursor = page.nextCursor;
  } while (cursor);
  return all;
}

const count = (people: Person[], value: (p: Person) => string | null | undefined) => {
  const m: Record<string, number> = {};
  for (const p of people) {
    const v = value(p);
    if (v) m[v] = (m[v] ?? 0) + 1;
  }
  return m;
};
const facetMap = (values: Array<{ value: string; count: number }>) => Object.fromEntries(values.map((v) => [v.value, v.count]));

describe("デモの人の検索（件数の安全性）", () => {
  it("件数は、全ページの一覧の見える値から数え直したものと一致する", () => {
    const tokyo = dir.sites({ asOf: today }).find((s) => s.name === "東京本社")!.id;
    for (const filters of [{}, { sites: [tokyo] }, { remoteWork: true }, { assignmentKinds: ["concurrent" as const] }, { query: "やま" }] as PersonFilters[]) {
      const first = findPeople(dir, query(filters, { facets: true, limit: 1 }));
      const all = readAll(dir, filters);
      expect(first.total).toBe(all.length);
      const f = first.facets!;
      expect(facetMap(f.job_types)).toEqual(count(all, (p) => p.job_type));
      expect(facetMap(f.employment_types)).toEqual(count(all, (p) => p.employment_type));
      expect(facetMap(f.legal_entities)).toEqual(count(all, (p) => p.legal_entity?.id));
      expect(facetMap(f.account_statuses)).toEqual(count(all, (p) => p.account_status));
      expect(facetMap(f.site_types)).toEqual(count(all, (p) => p.site?.type));
      expect(facetMap(f.prefectures)).toEqual(count(all, (p) => p.site?.prefecture_code));
      if (!filters.sites) expect(facetMap(f.sites)).toEqual(count(all, (p) => p.site?.id));
      if (filters.remoteWork === undefined) expect(facetMap(f.remote_work)).toEqual(count(all, (p) => (p.remote_work === null ? null : String(p.remote_work))));
    }
  });

  it("見えない拠点・リモートワーク（内部監査部の人）は件数に数えず、絞り込みにも当たらない（「非公開」の区分も作らない）", () => {
    const audit = data.org_units.find((o) => o.code === "A020")!;
    const auditMembers = data.people.filter((p) => p.assignments.some((a) => a.org_unit_id === audit.id && a.valid_until === null));
    expect(auditMembers.length).toBeGreaterThan(0);
    const site = dir.personSite(auditMembers[0]!, today)!;
    const first = findPeople(dir, query({}, { facets: true, limit: 1 }));
    const raw = data.people.filter((p) => dir.isListed(p) && dir.assignmentsAt(p, today).length > 0 && dir.personSite(p, today)?.id === site.id).length;
    const counted = first.facets!.sites.find((v) => v.value === site.id)?.count ?? 0;
    expect(counted).toBeLessThan(raw);
    expect(first.facets!.remote_work.map((v) => v.value)).toEqual(["true", "false"]);
    const hits = [...readAll(dir, { sites: [site.id] }), ...readAll(dir, { remoteWork: false }), ...readAll(dir, { remoteWork: true })];
    expect(hits.some((p) => auditMembers.some((m) => m.id === p.id))).toBe(false);
  });

  it("拠点の種別・都道府県の候補は決まった順（種別は本社から、都道府県は JIS の順）で、ラベルは都道府県名", () => {
    const f = findPeople(dir, query({}, { facets: true, limit: 1 })).facets!;
    const types = ["head_office", "branch", "sales_office", "factory", "laboratory", "logistics", "store", "other"];
    expect(f.site_types.map((v) => types.indexOf(v.value))).toEqual([...f.site_types.map((v) => types.indexOf(v.value))].sort((a, b) => a - b));
    expect(f.prefectures.map((v) => v.value)).toEqual([...f.prefectures.map((v) => v.value)].sort());
    expect(f.prefectures.find((v) => v.value === "13")?.label).toBe("東京都");
    const osaka = readAll(dir, { prefectures: ["27"] });
    expect(osaka.length).toBeGreaterThan(0);
    expect(osaka.every((p) => p.site?.prefecture_code === "27")).toBe(true);
    expect(readAll(dir, { siteTypes: ["logistics"] }).every((p) => p.site?.type === "logistics")).toBe(true);
  });

  it("社用携帯のように誰にも見えない項目は、検索語にも当たらない", () => {
    const withMobile = generateLargeDataset(2000).people.find((p) => p.work_mobile_phone)!;
    const large = new DemoDirectory(generateLargeDataset(2000));
    expect(findPeople(large, query({ query: withMobile.work_mobile_phone! })).items).toHaveLength(0);
  });

  it("ある条件の件数は、その条件以外に一致する人で数える（選んだ値以外の候補も出る）", () => {
    const tokyo = dir.sites({ asOf: today }).find((s) => s.name === "東京本社")!.id;
    const first = findPeople(dir, query({ sites: [tokyo], remoteWork: true }, { facets: true, limit: 1 }));
    expect(facetMap(first.facets!.sites)).toEqual(count(readAll(dir, { remoteWork: true }), (p) => p.site?.id));
    expect(first.facets!.sites.length).toBeGreaterThan(1);
    expect(facetMap(first.facets!.remote_work)).toEqual(count(readAll(dir, { sites: [tokyo] }), (p) => (p.remote_work === null ? null : String(p.remote_work))));
  });

  it("拠点ごと・リモートワークの並び: 同じ値はひと続き（ページをまたいでも）、見えない人は最後", () => {
    const bySite = readAll(dir, {}, "site");
    const runs = (list: Person[], key: (p: Person) => string) => list.map(key).filter((k, i, a) => i === 0 || a[i - 1] !== k);
    const siteRuns = runs(bySite, (p) => p.site?.id ?? "-");
    expect(new Set(siteRuns).size).toBe(siteRuns.length);
    expect(siteRuns.at(-1)).toBe("-");
    const byRemote = readAll(dir, {}, "remote_work");
    expect(runs(byRemote, (p) => String(p.remote_work))).toEqual(["true", "false", "null"]);
  });

  it("所属の条件は同じ 1 つの所属で満たす", () => {
    const concurrent = readAll(dir, { assignmentKinds: ["concurrent"] });
    expect(concurrent.length).toBeGreaterThan(0);
    for (const p of concurrent) expect(p.assignments!.some((a) => a.type === "concurrent" && a.secondment === "none")).toBe(true);
  });

  it("読み: ひらがな・ローマ字の綴りの揺れでも当たる", () => {
    const target = data.people.find((p) => p.family_name_kana === "ヤマモト")!;
    for (const q of ["やまもと", "ﾔﾏﾓﾄ", "yamamoto", "YAMAMOTO"]) {
      expect(readAll(dir, { query: q }).some((p) => p.id === target.id)).toBe(true);
    }
  });

  it("並べ替え: ページを続けて読むと全件と同じ並び（重複・抜けなし）。フリガナ順は五十音順", () => {
    for (const sort of ["kana", "org", "hire_date", "hire_date_desc", "site", "remote_work"] as const) {
      const full = findPeople(dir, query({}, { sort, limit: 100 })).items.map((p) => p.id);
      const paged = readAll(dir, {}, sort).slice(0, full.length).map((p) => p.id);
      expect(paged).toEqual(full);
    }
    const kana = readAll(dir, {}, "kana").map((p) => `${p.family_name_kana ?? ""}${p.given_name_kana ?? ""}`).filter(Boolean);
    expect(kana.slice(0, 5).every((k, i, a) => i === 0 || a[i - 1]!.localeCompare(k, "ja") <= 0)).toBe(true);
  });

  it("検査: 件数とカーソルの併用・逆の入社年月日・短すぎる語は 400", () => {
    for (const bad of [
      query({}, { facets: true, cursor: "48" }),
      query({ hiredFrom: "2026-01-01", hiredTo: "2025-01-01" }),
      query({ query: "a" }),
    ]) {
      expect(() => findPeople(dir, bad)).toThrow(ApiError);
    }
  });

  it("1 万人でも件数つきの検索が軽い（目安 1 秒未満）", () => {
    const large = new DemoDirectory(generateLargeDataset());
    findPeople(large, query({}, { facets: true, limit: 48, sort: "kana" }));
    const started = performance.now();
    const hq = large.sites({ asOf: large.businessDate }).find((s) => s.name === "東京本社")!.id;
    const page = findPeople(large, query({ sites: [hq] }, { facets: true, limit: 48, sort: "site" }));
    const elapsed = performance.now() - started;
    expect(page.total).toBeGreaterThan(1000);
    expect(page.items).toHaveLength(48);
    expect(elapsed).toBeLessThan(1000);
  });
});
