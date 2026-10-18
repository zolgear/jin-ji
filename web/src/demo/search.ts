// デモの人の多角的な検索（API の GET /people の絞り込み・並べ替え・件数）。規則は API 側（PersonSearchEngine）と同じものを簡略に真似る:
// - 照合・絞り込み・件数・並べ替えに使うのは、デモの閲覧者に見える項目の値だけ（見えない値は数えず、「非公開」の区分も作らない）
// - ある条件の件数は、その条件以外のすべての条件に一致する人で数える。所属の条件は同じ 1 つの所属で満たす
// - 件数は最初のページだけ。続きは件数の位置（オフセット）のカーソル（デモだけの簡略化。API は署名付きのキーセット）
import { matchesAny, simpleSearchKey, validateQuery } from "../lib/search";
import { READING_MIN_MATCH_LENGTH, readingKey } from "../lib/readingKey";
import { PREFECTURES, SITE_TYPES } from "../lib/labels";
import { ApiError, type AssignmentKind, type FacetValue, type FindPeopleQuery, type PersonFacets, type PersonSearchPage } from "../data/types";
import type { DemoDirectory, PersonField } from "./directory";
import type { DemoAssignment, DemoPerson } from "./model";

const ASSIGNMENT_KINDS: AssignmentKind[] = ["primary", "concurrent", "secondment"];
const EMPLOYMENT_STATUSES = ["active", "terminated"];
const ACCOUNT_STATUSES = ["active", "suspended"];
const REMOTE_WORK_VALUES = ["true", "false"];
const PREFECTURE_CODES = PREFECTURES.map(([code]) => code);
/** 文字列の値の条件で返す候補の最大数（API の PersonFacets.TextLimit）。 */
const TEXT_LIMIT = 50;
const LAST = "￿";

type PersonFacet =
  | "legal_entities"
  | "employment_types"
  | "job_types"
  | "sites"
  | "site_types"
  | "prefectures"
  | "remote_work"
  | "employment_statuses"
  | "account_statuses";
type AssignmentPart = "levels" | "assignment_kinds" | "positions";
const PERSON_FACETS: PersonFacet[] = [
  "legal_entities", "employment_types", "job_types", "sites", "site_types", "prefectures", "remote_work", "employment_statuses", "account_statuses",
];
/** 文字列の値（正規化表記で照合する）の条件。それ以外はコード値・ID の完全一致。 */
const TEXT_FACETS = new Set<PersonFacet>(["employment_types", "job_types"]);

/** 見える項目だけを残した、照合用の値。 */
interface Visible {
  person: DemoPerson;
  values: Record<PersonFacet, string | null>;
  hireDate: string | null;
  assignments: DemoAssignment[] | null;
  kana: string | null;
  /** 並べ替えの拠点の位置（法人のコード・拠点コード）。拠点が見えない・無ければ null。 */
  siteKey: string | null;
  siteName: string | null;
}

const kindOf = (a: DemoAssignment): AssignmentKind => (a.secondment !== "none" ? "secondment" : a.type === "primary" ? "primary" : "concurrent");

export function findPeople(dir: DemoDirectory, q: FindPeopleQuery): PersonSearchPage {
  dir.checkAsOf(q.asOf);
  const f = q.filters;
  const query = f.query?.trim() || null;
  if (query) {
    const problem = validateQuery(query);
    if (problem) throw new ApiError(400, problem, "urn:jinji:problem:invalid-request");
  }
  if (q.facets && q.cursor) throw new ApiError(400, "件数（facets）は最初のページ（cursor なし）でだけ求められます。", "urn:jinji:problem:invalid-request");
  if (f.hiredFrom && f.hiredTo && f.hiredFrom > f.hiredTo) {
    throw new ApiError(400, "入社年月日の範囲は、開始を終了以前にしてください。", "urn:jinji:problem:invalid-request");
  }
  const reading = query ? readingKey(query) : null;
  const queryReading = reading && reading.length >= READING_MIN_MATCH_LENGTH ? reading : null;
  const asOf = q.asOf;

  const set = (values: string[] | undefined, key = (v: string) => simpleSearchKey(v)) => new Set((values ?? []).map(key));
  const personCriteria: Record<PersonFacet, Set<string>> = {
    legal_entities: new Set(f.legalEntities ?? []),
    employment_types: set(f.employmentTypes),
    job_types: set(f.jobTypes),
    sites: new Set(f.sites ?? []),
    site_types: new Set(f.siteTypes ?? []),
    prefectures: new Set(f.prefectures ?? []),
    remote_work: new Set(f.remoteWork === true ? ["true"] : f.remoteWork === false ? ["false"] : []),
    employment_statuses: new Set(f.employmentStatuses ?? []),
    account_statuses: new Set(f.accountStatuses ?? []),
  };
  const exact = (facet: PersonFacet) => !TEXT_FACETS.has(facet);
  const levels = new Set(f.levels ?? []);
  const kinds = new Set(f.assignmentKinds ?? []);
  const positions = set(f.positions);
  const hasAssignmentCriteria = Boolean(f.orgId) || levels.size > 0 || kinds.size > 0 || positions.size > 0;

  // 組織の絞り込み（配下を含むなら、その時点の配下の組織すべて）
  let orgTargets: Set<string> | null = null;
  if (f.orgId) {
    orgTargets = new Set([f.orgId]);
    if (f.includeDescendants) {
      for (const o of dir.orgsAt(asOf)) {
        if (dir.ancestors(o.id).some((a) => a.id === f.orgId)) orgTargets.add(o.id);
      }
    }
  }

  const levelOf = (a: DemoAssignment): string | null => {
    const org = dir.orgUnit(a.org_unit_id);
    if (!org?.level_key) return null;
    return dir.data.level_schemes[org.legal_entity_id]?.some((l) => l.key === org.level_key) ? org.level_key : null;
  };
  const assignmentMatches = (a: DemoAssignment, ignore: AssignmentPart | null) => {
    if (orgTargets && !orgTargets.has(a.org_unit_id)) return false;
    if (ignore !== "levels" && levels.size > 0 && !levels.has(levelOf(a) ?? "")) return false;
    if (ignore !== "assignment_kinds" && kinds.size > 0 && !kinds.has(kindOf(a))) return false;
    if (ignore !== "positions" && positions.size > 0 && !(a.position_title && positions.has(simpleSearchKey(a.position_title)))) return false;
    return true;
  };

  const visibleOf = (p: DemoPerson): Visible => {
    const visible = new Set<PersonField>(dir.visibleFields(p));
    const pick = <T,>(field: PersonField, value: T): T | null => (visible.has(field) ? value : null);
    const suspended = p.suspensions.some((s) => s.from <= asOf && (s.until === null || asOf < s.until));
    const kana = pick("family_name_kana", p.family_name_kana);
    const site = visible.has("site") ? dir.personSite(p, asOf) : null;
    const remote = pick("remote_work", p.remote_work);
    return {
      person: p,
      values: {
        legal_entities: pick("legal_entity", p.legal_entity_id),
        employment_types: pick("employment_type", p.employment_type),
        job_types: pick("job_type", p.job_type),
        sites: site?.id ?? null,
        site_types: site?.type ?? null,
        prefectures: site?.prefecture_code ?? null,
        remote_work: remote === null ? null : String(remote),
        employment_statuses: pick("employment_status", p.termination_date !== null && p.termination_date < asOf ? "terminated" : "active"),
        account_statuses: pick("account_status", suspended ? "suspended" : "active"),
      },
      hireDate: pick("hire_date", p.hire_date),
      assignments: visible.has("assignments") ? dir.assignmentsAt(p, asOf) : null,
      kana: kana ? `${kana}${pick("given_name_kana", p.given_name_kana) ?? ""}` : null,
      siteKey: site ? `${dir.legalEntity(site.legal_entity_id)?.code ?? ""}\u001f${site.code}\u001f${site.id}` : null,
      siteName: site?.name ?? null,
    };
  };

  const matchesQuery = (v: Visible) => {
    if (!query) return true;
    const visible = new Set<PersonField>(dir.visibleFields(v.person));
    const p = v.person;
    const pick = (field: PersonField, value: string | null) => (visible.has(field) ? value : null);
    const latin = pick("family_name_latin", p.family_name_latin) ? [`${p.given_name_latin ?? ""}${p.family_name_latin ?? ""}`, `${p.family_name_latin ?? ""}${p.given_name_latin ?? ""}`] : [];
    if (
      matchesAny(query, [
        pick("display_name", p.display_name),
        pick("family_name", p.family_name) && pick("given_name", p.given_name) ? `${p.family_name}${p.given_name}` : null,
        v.kana,
        ...latin,
        pick("employee_number", p.employee_number),
        pick("work_email", p.work_email),
      ])
    ) {
      return true;
    }
    return queryReading !== null && [v.kana, ...latin].some((c) => readingKey(c)?.includes(queryReading));
  };

  const counts = {
    person: Object.fromEntries(PERSON_FACETS.map((k) => [k, new Map<string, number>()])) as Record<PersonFacet, Map<string, number>>,
    levels: new Map<string, number>(),
    assignment_kinds: new Map<string, number>(),
    positions: new Map<string, number>(),
  };
  const texts = new Map<string, string>();
  const siteNames = new Map<string, string>();
  const bump = (m: Map<string, number>, key: string) => m.set(key, (m.get(key) ?? 0) + 1);
  const countKey = (facet: PersonFacet, value: string) => {
    if (exact(facet)) return value;
    const key = simpleSearchKey(value);
    if (!texts.has(key)) texts.set(key, value.trim());
    return key;
  };

  const matched: Visible[] = [];
  for (const p of dir.data.people) {
    if (!dir.isListed(p)) continue;
    // 入社前の人（その時点の在籍者でない人）は API と同じく投影しない
    if (p.hire_date > asOf && dir.assignmentsAt(p, asOf).length === 0) continue;
    const v = visibleOf(p);
    if (!matchesQuery(v)) continue;
    if ((f.hiredFrom || f.hiredTo) && (!v.hireDate || (f.hiredFrom && v.hireDate < f.hiredFrom) || (f.hiredTo && v.hireDate > f.hiredTo))) continue;
    const failed = PERSON_FACETS.filter((facet) => {
      const criteria = personCriteria[facet];
      if (criteria.size === 0) return false;
      const value = v.values[facet];
      return value === null || !criteria.has(exact(facet) ? value : simpleSearchKey(value));
    });
    const assignments = v.assignments ?? [];
    const assignmentsMatch = !hasAssignmentCriteria || assignments.some((a) => assignmentMatches(a, null));
    if (failed.length === 0 && assignmentsMatch) matched.push(v);
    if (!q.facets || q.cursor) continue;
    if (assignmentsMatch && failed.length <= 1) {
      for (const facet of failed.length === 0 ? PERSON_FACETS : failed) {
        const value = v.values[facet];
        if (value === null) continue;
        bump(counts.person[facet], countKey(facet, value));
        if (facet === "sites" && v.siteName) siteNames.set(value, v.siteName);
      }
    }
    if (failed.length === 0 && v.assignments) {
      const parts: Array<[AssignmentPart, (a: DemoAssignment) => string | null]> = [
        ["levels", levelOf],
        ["assignment_kinds", kindOf],
        ["positions", (a) => (a.position_title ? simpleSearchKey(a.position_title) : null)],
      ];
      for (const [part, valueOf] of parts) {
        const values = new Set(assignments.filter((a) => assignmentMatches(a, part)).map(valueOf).filter((x): x is string => x !== null));
        for (const value of values) bump(counts[part], value);
      }
      for (const a of assignments) if (a.position_title) texts.set(simpleSearchKey(a.position_title), texts.get(simpleSearchKey(a.position_title)) ?? a.position_title);
    }
  }

  // 並べ替え（見えない値は最後、同じ値は ID の順）
  const order = orgOrder(dir, asOf);
  const keyOf = (v: Visible): [string, string, string] => {
    const kana = v.kana ? simpleSearchKey(v.kana) : LAST;
    switch (q.sort) {
      case "kana":
        return [kana, "", v.person.id];
      case "org": {
        const a = v.assignments ?? [];
        const home = a.find((x) => x.type === "primary" && x.secondment === "none") ?? a.find((x) => x.type === "primary") ?? a[0];
        const index = home ? order.get(home.org_unit_id) : undefined;
        return [index === undefined ? LAST : String(index).padStart(6, "0"), kana, v.person.id];
      }
      case "hire_date":
        return [v.hireDate ?? LAST, kana, v.person.id];
      case "site":
        return [v.siteKey ?? LAST, kana, v.person.id];
      case "remote_work": {
        const remote = v.values.remote_work;
        return [remote === "true" ? "0" : remote === "false" ? "1" : LAST, kana, v.person.id];
      }
      case "hire_date_desc":
        return [v.hireDate ? String(99999999 - Number(v.hireDate.replaceAll("-", ""))).padStart(8, "0") : LAST, kana, v.person.id];
      default:
        return ["", "", v.person.id];
    }
  };
  const keyed = matched.map((v) => ({ v, k: keyOf(v) }));
  const cmp = (x: string, y: string) => (x < y ? -1 : x > y ? 1 : 0);
  keyed.sort((a, b) => cmp(a.k[0], b.k[0]) || cmp(a.k[1], b.k[1]) || cmp(a.k[2], b.k[2]));

  const limit = Math.min(Math.max(q.limit ?? 50, 1), 100);
  const start = q.cursor ? Number.parseInt(q.cursor, 10) || 0 : 0;
  const slice = keyed.slice(start, start + limit);
  const next = start + limit < keyed.length ? String(start + limit) : null;
  const items = slice.map(({ v }) => dir.project(v.person, asOf));
  if (!q.facets || q.cursor) return { items, nextCursor: next, total: null, facets: null };

  const byCount = (a: FacetValue, b: FacetValue) => b.count - a.count || (a.label < b.label ? -1 : a.label > b.label ? 1 : 0);
  const textValues = (m: Map<string, number>): FacetValue[] =>
    [...m].map(([key, count]) => ({ value: texts.get(key) ?? key, label: texts.get(key) ?? key, count })).sort(byCount).slice(0, TEXT_LIMIT);
  const fixed = (m: Map<string, number>, codes: string[]): FacetValue[] =>
    codes.filter((c) => m.has(c)).map((c) => ({ value: c, label: c, count: m.get(c)! }));
  const levelInfo = new Map<string, { rank: number; names: string[] }>();
  for (const scheme of Object.values(dir.data.level_schemes)) {
    for (const l of scheme) {
      const info = levelInfo.get(l.key) ?? { rank: l.rank, names: [] };
      info.rank = Math.max(info.rank, l.rank);
      if (!info.names.includes(l.name)) info.names.push(l.name);
      levelInfo.set(l.key, info);
    }
  }
  const facets: PersonFacets = {
    legal_entities: [...counts.person.legal_entities]
      .map(([id, count]) => ({ value: id, label: dir.legalEntity(id)?.name ?? id, count }))
      .sort(byCount),
    levels: [...counts.levels]
      .map(([key, count]) => ({ key, count, info: levelInfo.get(key) }))
      .sort((a, b) => (a.info?.rank ?? 99) - (b.info?.rank ?? 99) || cmp(a.key, b.key))
      .map(({ key, count, info }) => ({ value: key, label: info?.names.join("・") ?? key, count })),
    assignment_kinds: fixed(counts.assignment_kinds, ASSIGNMENT_KINDS),
    positions: textValues(counts.positions),
    employment_types: textValues(counts.person.employment_types),
    job_types: textValues(counts.person.job_types),
    sites: [...counts.person.sites]
      .map(([id, count]) => ({ value: id, label: siteNames.get(id) ?? id, count }))
      .sort(byCount)
      .slice(0, TEXT_LIMIT),
    site_types: fixed(counts.person.site_types, [...SITE_TYPES]),
    prefectures: PREFECTURE_CODES.filter((c) => counts.person.prefectures.has(c)).map((c) => ({
      value: c,
      label: PREFECTURES.find(([code]) => code === c)![1],
      count: counts.person.prefectures.get(c)!,
    })),
    remote_work: fixed(counts.person.remote_work, REMOTE_WORK_VALUES),
    employment_statuses: fixed(counts.person.employment_statuses, EMPLOYMENT_STATUSES),
    account_statuses: fixed(counts.person.account_statuses, ACCOUNT_STATUSES),
  };
  return { items, nextCursor: next, total: keyed.length, facets };
}

/** 組織図の順（法人のコード・上位から深さ優先・組織コード）の位置。 */
function orgOrder(dir: DemoDirectory, asOf: string): Map<string, number> {
  const orgs = dir.orgsAt(asOf);
  const ids = new Set(orgs.map((o) => o.id));
  const children = new Map<string | null, typeof orgs>();
  for (const o of orgs) {
    const parent = o.parent_id && ids.has(o.parent_id) ? o.parent_id : null;
    children.set(parent, [...(children.get(parent) ?? []), o]);
  }
  const order = new Map<string, number>();
  const entityCode = (id: string) => dir.legalEntity(id)?.code ?? "";
  const visit = (list: typeof orgs) => {
    for (const o of [...list].sort((a, b) => (a.code < b.code ? -1 : a.code > b.code ? 1 : 0))) {
      if (order.has(o.id)) continue;
      order.set(o.id, order.size);
      visit(children.get(o.id) ?? []);
    }
  };
  const roots = [...(children.get(null) ?? [])].sort((a, b) => entityCode(a.legal_entity_id).localeCompare(entityCode(b.legal_entity_id)));
  const byEntity = new Map<string, typeof orgs>();
  for (const r of roots) byEntity.set(r.legal_entity_id, [...(byEntity.get(r.legal_entity_id) ?? []), r]);
  for (const list of byEntity.values()) visit(list);
  return order;
}
