// 検索の画面の状態（条件・並び順・表示の形）と URL（#/search?...）の相互変換。条件を URL に持つので、共有・戻る操作で同じ検索に戻れる。
// 値はコード値・ID・文字列のまま持ち、表示名は画面で付ける。複数の値は同じ名前を繰り返す（le=a&le=b）。
import type { AssignmentKind, PersonFilters, PersonSort } from "../../data/types";
import { addDays, isIsoDate } from "../../lib/date";
import { SITE_TYPES } from "../../lib/labels";

export type Layout = "cards" | "table";

/** 結果のグループ化（拠点ごと・リモートワークの有無ごと）。 */
export type Grouping = "none" | "site" | "remote_work";

export interface SearchState {
  filters: PersonFilters;
  sort: PersonSort;
  layout: Layout;
  group: Grouping;
}

/** URL の引数の名前（画面の他の引数 org・person・as_of・source と重ならないように短い名前にする）。 */
const KEYS = {
  query: "q",
  org: "in",
  /** 配下を含まない（既定は含む）。 */
  directOnly: "direct",
  legalEntities: "le",
  levels: "lv",
  assignmentKinds: "kind",
  positions: "pos",
  employmentTypes: "et",
  jobTypes: "jt",
  sites: "site",
  siteTypes: "stype",
  prefectures: "pref",
  /** リモートワークの対象か（1 / 0）。 */
  remoteWork: "remote",
  hiredFrom: "hf",
  hiredTo: "ht",
  employmentStatuses: "es",
  accountStatuses: "acc",
  sort: "sort",
  layout: "layout",
  group: "group",
} as const;

/** 検索の画面だけが使う引数（他の画面に移るときに消す）。 */
export const SEARCH_PARAM_KEYS: readonly string[] = Object.values(KEYS);

/** 画面で選べる並び順（拠点・リモートワークの並びはグループ化で使う）。 */
const SORTS: PersonSort[] = ["kana", "org", "hire_date", "hire_date_desc", "id"];
const KINDS: AssignmentKind[] = ["primary", "concurrent", "secondment"];
/** 1 つの条件の値の数の上限（API の PersonSearch.MaxValuesPerFilter）。 */
export const MAX_VALUES = 20;
/** 画面の既定の並び順。 */
export const DEFAULT_SORT: PersonSort = "kana";

const list = (params: URLSearchParams, key: string) => [...new Set(params.getAll(key).filter((v) => v !== ""))].slice(0, MAX_VALUES);

export function parseSearchState(params: URLSearchParams): SearchState {
  const date = (key: string) => {
    const v = params.get(key);
    return v && isIsoDate(v) ? v : null;
  };
  const sort = params.get(KEYS.sort) as PersonSort | null;
  const group = params.get(KEYS.group);
  return {
    filters: {
      query: params.get(KEYS.query) ?? "",
      orgId: params.get(KEYS.org) || null,
      includeDescendants: params.get(KEYS.directOnly) !== "1",
      legalEntities: list(params, KEYS.legalEntities),
      levels: list(params, KEYS.levels),
      assignmentKinds: list(params, KEYS.assignmentKinds).filter((k): k is AssignmentKind => (KINDS as string[]).includes(k)),
      positions: list(params, KEYS.positions),
      employmentTypes: list(params, KEYS.employmentTypes),
      jobTypes: list(params, KEYS.jobTypes),
      sites: list(params, KEYS.sites),
      siteTypes: list(params, KEYS.siteTypes).filter((v) => (SITE_TYPES as readonly string[]).includes(v)),
      prefectures: list(params, KEYS.prefectures).filter((v) => /^(0[1-9]|[1-3][0-9]|4[0-7])$/.test(v)),
      remoteWork: params.get(KEYS.remoteWork) === "1" ? true : params.get(KEYS.remoteWork) === "0" ? false : null,
      hiredFrom: date(KEYS.hiredFrom),
      hiredTo: date(KEYS.hiredTo),
      employmentStatuses: list(params, KEYS.employmentStatuses).filter((v) => v === "active" || v === "terminated"),
      accountStatuses: list(params, KEYS.accountStatuses).filter((v) => v === "active" || v === "suspended"),
    },
    sort: sort && SORTS.includes(sort) ? sort : DEFAULT_SORT,
    layout: params.get(KEYS.layout) === "table" ? "table" : "cards",
    group: group === "site" || group === "remote_work" ? group : "none",
  };
}

/** 状態を URL の引数に書く。検索の画面の引数以外（時点・選択中の人・取得元）はそのまま残す。 */
export function writeSearchState(base: URLSearchParams, state: SearchState): URLSearchParams {
  const params = new URLSearchParams(base);
  for (const key of SEARCH_PARAM_KEYS) params.delete(key);
  const f = state.filters;
  const put = (key: string, value: string | null | undefined) => {
    if (value) params.set(key, value);
  };
  const putAll = (key: string, values: readonly string[] | undefined) => {
    for (const v of values ?? []) params.append(key, v);
  };
  put(KEYS.query, f.query?.trim());
  put(KEYS.org, f.orgId);
  if (f.orgId && f.includeDescendants === false) params.set(KEYS.directOnly, "1");
  putAll(KEYS.legalEntities, f.legalEntities);
  putAll(KEYS.levels, f.levels);
  putAll(KEYS.assignmentKinds, f.assignmentKinds);
  putAll(KEYS.positions, f.positions);
  putAll(KEYS.employmentTypes, f.employmentTypes);
  putAll(KEYS.jobTypes, f.jobTypes);
  putAll(KEYS.sites, f.sites);
  putAll(KEYS.siteTypes, f.siteTypes);
  putAll(KEYS.prefectures, f.prefectures);
  if (f.remoteWork === true || f.remoteWork === false) params.set(KEYS.remoteWork, f.remoteWork ? "1" : "0");
  put(KEYS.hiredFrom, f.hiredFrom);
  put(KEYS.hiredTo, f.hiredTo);
  putAll(KEYS.employmentStatuses, f.employmentStatuses);
  putAll(KEYS.accountStatuses, f.accountStatuses);
  if (state.sort !== DEFAULT_SORT) params.set(KEYS.sort, state.sort);
  if (state.layout !== "cards") params.set(KEYS.layout, state.layout);
  if (state.group !== "none") params.set(KEYS.group, state.group);
  return params;
}

/**
 * API に渡す並び順。グループ化しているときはグループの並び（拠点の順・リモートワークの対象が先。同じグループの中はフリガナ順）にして、
 * ページをまたいでも同じグループがひと続きになるようにする。画面で選んだ並び順は URL に残し、グループ化を外すと戻る。
 */
export function effectiveSort(state: Pick<SearchState, "sort" | "group">): PersonSort {
  return state.group === "none" ? state.sort : state.group;
}

/** 選択式の条件（値の一覧を持つ条件）の名前。 */
export type ListFilterKey =
  | "legalEntities"
  | "levels"
  | "assignmentKinds"
  | "positions"
  | "employmentTypes"
  | "jobTypes"
  | "sites"
  | "siteTypes"
  | "prefectures"
  | "employmentStatuses"
  | "accountStatuses";

/** 値の選択を切り替えた条件を返す。 */
export function toggleValue(filters: PersonFilters, key: ListFilterKey, value: string): PersonFilters {
  const current = (filters[key] ?? []) as string[];
  const next = current.includes(value) ? current.filter((v) => v !== value) : [...current, value].slice(0, MAX_VALUES);
  return { ...filters, [key]: next };
}

/** 検索語以外の条件があるか（絞り込みの数を数える）。 */
export function countFilters(f: PersonFilters): number {
  const lists: ListFilterKey[] = [
    "legalEntities", "levels", "assignmentKinds", "positions", "employmentTypes", "jobTypes", "sites", "siteTypes", "prefectures", "employmentStatuses", "accountStatuses",
  ];
  return (
    lists.reduce((n, k) => n + ((f[k] as string[] | undefined)?.length ?? 0), 0) +
    (f.orgId ? 1 : 0) +
    (f.hiredFrom || f.hiredTo ? 1 : 0) +
    (f.remoteWork === true || f.remoteWork === false ? 1 : 0)
  );
}

/** 条件が何も無いか（全員の一覧）。 */
export function isEmptySearch(f: PersonFilters): boolean {
  return !f.query?.trim() && countFilters(f) === 0;
}

/** 勤続年数の目安（入社年月日の範囲に直す）。年数は業務日付から数える。 */
export interface TenurePreset {
  id: string;
  label: string;
  /** 勤続 min 年以上（null なら下限なし）。 */
  min: number | null;
  /** 勤続 max 年未満（null なら上限なし）。 */
  max: number | null;
}

export const TENURE_PRESETS: TenurePreset[] = [
  { id: "lt1", label: "1 年未満", min: null, max: 1 },
  { id: "1to3", label: "1〜3 年", min: 1, max: 3 },
  { id: "3to10", label: "3〜10 年", min: 3, max: 10 },
  { id: "10to20", label: "10〜20 年", min: 10, max: 20 },
  { id: "ge20", label: "20 年以上", min: 20, max: null },
];

/** 業務日付から n 年前の日付（2/29 は 2/28 に寄せる）。 */
export function yearsBefore(date: string, years: number): string {
  const [y, m, d] = date.split("-").map(Number) as [number, number, number];
  const target = `${String(y - years).padStart(4, "0")}-${String(m).padStart(2, "0")}-${String(d).padStart(2, "0")}`;
  return isIsoDate(target) ? target : `${String(y - years).padStart(4, "0")}-02-28`;
}

/**
 * 勤続年数の範囲を入社年月日の範囲にする。勤続 min 年以上 = 入社が min 年前の日以前、勤続 max 年未満 = 入社が max 年前の日より後。
 */
export function tenureToHired(preset: TenurePreset, businessDate: string): { hiredFrom: string | null; hiredTo: string | null } {
  return {
    hiredFrom: preset.max === null ? null : addDays(yearsBefore(businessDate, preset.max), 1),
    hiredTo: preset.min === null ? null : yearsBefore(businessDate, preset.min),
  };
}

/** 今の入社年月日の範囲が勤続年数の目安と一致すれば、その目安。 */
export function matchingTenure(f: PersonFilters, businessDate: string): TenurePreset | null {
  return TENURE_PRESETS.find((p) => {
    const r = tenureToHired(p, businessDate);
    return r.hiredFrom === (f.hiredFrom ?? null) && r.hiredTo === (f.hiredTo ?? null);
  }) ?? null;
}
