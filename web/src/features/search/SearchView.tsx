// 人の多角的な検索。自由語（氏名・フリガナ・ローマ字・社員番号・社用メール。API の正規化と読みの照合）と、組織・段・主務兼務出向・役職・法人・
// 雇用区分・職種・拠点（種別・都道府県）・リモートワークの有無・入社年月日（勤続年数）・在籍状態・アカウント状態の絞り込み。
// 結果は名刺の形のカードか一覧（表）で、拠点ごと・リモートワークの有無ごとにまとめて見せられる（グループ化）。
// - 条件・並び順・表示の形・グループ化は URL（#/search?...）に持つ（共有・戻る操作）。時点は画面共通の as_of
// - グループ化は API の並び（sort=site / remote_work）と組み合わせ、ページをまたいでも区切りが続くようにする（grouping.ts）
// - 件数（facets）は最初のページと一緒に取る（API の規則で、見える人の見える値だけを数える）。続きはカーソルで 1 ページずつ読む
// - 1 ページは 48 人（API の上限 100 の内側。返却人数の制限に数えられる）。下端に近づくと次を読み、ボタンでも読める
import { motion, useReducedMotion } from "motion/react";
import { useCallback, useEffect, useId, useMemo, useRef, useState } from "react";
import { useOrgTree } from "../../app/hooks";
import { useNav } from "../../app/nav";
import { ErrorNote, Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { useQuery } from "../../data/query";
import { describeError, type Person, type PersonFilters, type PersonSearchPage, type PersonSort } from "../../data/types";
import { useFormatDate } from "../../lib/dateStyle";
import { ACCOUNT_STATUS_LABEL, ASSIGNMENT_KIND_LABEL, EMPLOYMENT_STATUS_LABEL, employmentTypeLabel, prefectureName, siteTypeLabel } from "../../lib/labels";
import { formatHash, useRoute } from "../../lib/router";
import { validateQuery } from "../../lib/search";
import { FilterPanel } from "./FilterPanel";
import { GroupHeading } from "./GroupHeading";
import { groupPeople } from "./grouping";
import { PeopleTable } from "./PeopleTable";
import { PersonCard, PersonCardSkeleton } from "./PersonCard";
import {
  countFilters,
  effectiveSort,
  isEmptySearch,
  matchingTenure,
  parseSearchState,
  writeSearchState,
  type Grouping,
  type Layout,
  type ListFilterKey,
  type SearchState,
} from "./searchState";

const PAGE_SIZE = 48;

const SORT_LABEL: Record<PersonSort, string> = {
  kana: "フリガナ順",
  org: "組織順",
  hire_date: "入社日の古い順",
  hire_date_desc: "入社日の新しい順",
  id: "登録順",
  site: "拠点順",
  remote_work: "リモートワーク対象が先",
};

const GROUP_LABEL: Record<Grouping, string> = { none: "なし", site: "拠点", remote_work: "リモートワーク" };

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

/** 検索の条件を URL から読み、書き換える。 */
function useSearchState(): [SearchState, (next: SearchState, replace?: boolean) => void] {
  const { route } = useRoute();
  const raw = route.params.toString();
  // URL の文字列が同じなら同じオブジェクトを返す（取得のキー・effect の依存を安定させる）
  // eslint-disable-next-line react-hooks/exhaustive-deps
  const state = useMemo(() => parseSearchState(route.params), [raw]);
  const update = useCallback((next: SearchState, replace = false) => {
    const current = new URLSearchParams(window.location.hash.split("?")[1] ?? "");
    const hash = formatHash({ view: "search", params: writeSearchState(current, next) });
    if (hash === window.location.hash) return;
    if (replace) {
      window.history.replaceState(null, "", hash);
      window.dispatchEvent(new HashChangeEvent("hashchange"));
    } else {
      window.location.hash = hash;
    }
  }, []);
  return [state, update];
}

/** 検索の結果（最初のページは件数つきでキャッシュし、続きはこの画面の中で足す）。 */
function useSearchResults(filters: PersonFilters, sort: PersonSort, asOf: string, retry: number) {
  const { adapter } = useAdapter();
  const problem = filters.query?.trim() ? validateQuery(filters.query.trim()) : null;
  const key = problem ? null : `find:${asOf}:${sort}:${retry}:${JSON.stringify(filters)}`;
  const first = useQuery<PersonSearchPage>(adapter, key, () => adapter.findPeople({ asOf, filters, sort, facets: true, limit: PAGE_SIZE }));
  const [more, setMore] = useState<{ key: string | null; items: Person[]; cursor: string | null; loading: boolean; error: unknown }>({
    key: null,
    items: [],
    cursor: null,
    loading: false,
    error: undefined,
  });
  const firstCursor = first.data?.nextCursor ?? null;
  const current = useMemo(
    () => (more.key === key && !first.stale ? more : { key, items: [] as Person[], cursor: firstCursor, loading: false, error: undefined as unknown }),
    [more, key, first.stale, firstCursor],
  );
  const loadMore = useCallback(() => {
    if (current.loading || !current.cursor || first.stale) return;
    const cursor = current.cursor;
    setMore({ ...current, loading: true, error: undefined });
    adapter.findPeople({ asOf, filters, sort, facets: false, cursor, limit: PAGE_SIZE }).then(
      (page) => setMore((m) => (m.key === key ? { key, items: [...m.items, ...page.items], cursor: page.nextCursor, loading: false, error: undefined } : m)),
      (error: unknown) => setMore((m) => (m.key === key ? { ...m, loading: false, error } : m)),
    );
  }, [adapter, asOf, filters, sort, key, current, first.stale]);
  const items = first.data ? [...first.data.items, ...current.items] : [];
  return { problem, first, items, hasMore: Boolean(current.cursor), loadingMore: current.loading, moreError: current.error, loadMore };
}

function SearchIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round">
      <circle cx="7" cy="7" r="4.5" />
      <path d="m10.5 10.5 3.5 3.5" />
    </svg>
  );
}

function FilterIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round">
      <path d="M2 4h12M4.5 8h7M7 12h2" />
    </svg>
  );
}

/** 表示の形の切り替え（カード / 一覧）。 */
function LayoutSwitch({ layout, onChange }: { layout: Layout; onChange: (layout: Layout) => void }) {
  const options: Array<[Layout, string, string]> = [
    ["cards", "カード", "M2 2.5h5v5H2zM9 2.5h5v5H9zM2 9.5h5v5H2zM9 9.5h5v5H9z"],
    ["table", "一覧", "M2 3h12M2 6.5h12M2 10h12M2 13.5h12"],
  ];
  return (
    <fieldset className="m-0 flex overflow-hidden rounded-sm border border-border p-0 text-xs">
      <legend className="sr-only">表示の形</legend>
      {options.map(([value, label, d]) => (
        <button
          key={value}
          type="button"
          aria-pressed={layout === value}
          onClick={() => onChange(value)}
          className={`inline-flex items-center gap-1 px-2 py-1 pointer-coarse:min-h-11 pointer-coarse:px-3 ${layout === value ? "bg-accent text-accent-fg" : "hover:bg-surface-2"}`}
        >
          <svg aria-hidden="true" viewBox="0 0 16 16" className="size-3.5" fill="none" stroke="currentColor" strokeWidth="1.4">
            <path d={d} />
          </svg>
          {label}
        </button>
      ))}
    </fieldset>
  );
}

interface Chip {
  id: string;
  label: string;
  remove: () => PersonFilters;
}

/** グループ化の切り替え（なし / 拠点 / リモートワーク）。 */
function GroupSwitch({ group, onChange }: { group: Grouping; onChange: (group: Grouping) => void }) {
  return (
    <fieldset className="m-0 flex items-center gap-1.5 border-0 p-0 text-xs">
      <legend className="sr-only">グループ化</legend>
      <span aria-hidden="true" className="text-muted max-sm:hidden">
        グループ化
      </span>
      <span className="flex overflow-hidden rounded-sm border border-border">
        {(["none", "site", "remote_work"] as const).map((g) => (
          <button
            key={g}
            type="button"
            aria-pressed={group === g}
            onClick={() => onChange(g)}
            className={`px-2 py-1 pointer-coarse:min-h-11 pointer-coarse:px-3 ${g !== "none" ? "border-l border-border" : ""} ${group === g ? "bg-accent text-accent-fg" : "hover:bg-surface-2"}`}
          >
            {g === "none" ? <span className="sm:hidden">まとめない</span> : null}
            <span className={g === "none" ? "max-sm:hidden" : ""}>{GROUP_LABEL[g]}</span>
            {g !== "none" && <span className="sr-only">ごとにまとめる</span>}
          </button>
        ))}
      </span>
    </fieldset>
  );
}

/** 選んだ条件のチップ（外す操作つき）。 */
function useChips(
  filters: PersonFilters,
  names: { entity: (id: string) => string; org: (id: string) => string; level: (key: string) => string; site: (id: string) => string },
): Chip[] {
  const { adapter } = useAdapter();
  const formatDate = useFormatDate();
  const chips: Chip[] = [];
  if (filters.orgId) {
    chips.push({
      id: "org",
      label: `組織: ${names.org(filters.orgId)}${filters.includeDescendants === false ? "（直属のみ）" : "（配下を含む）"}`,
      remove: () => ({ ...filters, orgId: null }),
    });
  }
  const lists: Array<[ListFilterKey, string, (v: string) => string]> = [
    ["levels", "段", names.level],
    ["assignmentKinds", "所属", (v) => ASSIGNMENT_KIND_LABEL[v] ?? v],
    ["positions", "役職", (v) => v],
    ["legalEntities", "法人", names.entity],
    ["employmentTypes", "雇用区分", employmentTypeLabel],
    ["jobTypes", "職種", (v) => v],
    ["sites", "拠点", names.site],
    ["siteTypes", "拠点の種別", siteTypeLabel],
    ["prefectures", "都道府県", (v) => prefectureName(v) ?? v],
    ["employmentStatuses", "在籍", (v) => EMPLOYMENT_STATUS_LABEL[v] ?? v],
    ["accountStatuses", "アカウント", (v) => ACCOUNT_STATUS_LABEL[v] ?? v],
  ];
  for (const [key, title, label] of lists) {
    for (const value of (filters[key] as string[] | undefined) ?? []) {
      chips.push({
        id: `${key}:${value}`,
        label: `${title}: ${label(value)}`,
        remove: () => ({ ...filters, [key]: ((filters[key] as string[]) ?? []).filter((v) => v !== value) }),
      });
    }
  }
  if (filters.remoteWork === true || filters.remoteWork === false) {
    chips.push({
      id: "remote",
      label: `リモートワーク: ${filters.remoteWork ? "対象" : "対象外"}`,
      remove: () => ({ ...filters, remoteWork: null }),
    });
  }
  if (filters.hiredFrom || filters.hiredTo) {
    const tenure = matchingTenure(filters, adapter.businessDate);
    chips.push({
      id: "hired",
      label: tenure
        ? `勤続: ${tenure.label}`
        : filters.hiredFrom && filters.hiredTo
          ? `入社: ${formatDate(filters.hiredFrom)}〜${formatDate(filters.hiredTo)}`
          : filters.hiredFrom
            ? `入社: ${formatDate(filters.hiredFrom)}以降`
            : `入社: ${formatDate(filters.hiredTo)}以前`,
      remove: () => ({ ...filters, hiredFrom: null, hiredTo: null }),
    });
  }
  return chips;
}

export function SearchView() {
  const { adapter } = useAdapter();
  const { asOf, openPerson, personId, showOrgIn } = useNav();
  const [state, setState] = useSearchState();
  const { filters, sort, layout, group } = state;
  const reduceMotion = useReducedMotion();
  const inputId = useId();
  const sortId = useId();
  const sheetRef = useRef<HTMLDialogElement>(null);
  const [announce, setAnnounce] = useState("");
  const [retry, setRetry] = useState(0);
  const tree = useOrgTree(asOf);

  // 検索語は入力中は手元に持ち、少し止まってから URL に書く（戻る操作の履歴を増やさない）
  const urlQuery = filters.query ?? "";
  const [text, setText] = useState(urlQuery);
  const [lastUrlQuery, setLastUrlQuery] = useState(urlQuery);
  if (urlQuery !== lastUrlQuery) {
    // 戻る操作・チップの解除などで URL が変わったら入力欄も合わせる
    setLastUrlQuery(urlQuery);
    setText(urlQuery);
  }
  const debounced = useDebounced(text.trim(), 300);
  useEffect(() => {
    if (debounced !== urlQuery.trim()) setState({ ...state, filters: { ...filters, query: debounced } }, true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debounced]);

  // シートを開いている間は履歴を増やさない（スマホの戻る操作で条件が 1 つずつ戻らないように）
  const setFilters = (next: PersonFilters) => setState({ ...state, filters: next }, sheetRef.current?.open ?? false);
  const results = useSearchResults(filters, effectiveSort(state), asOf, retry);
  const { first, items } = results;
  const page = first.data;
  const total = page?.total ?? null;

  // 件数が変わったら読み上げる（入力のたびではなく、結果が届いたとき）
  const resultKey = JSON.stringify([filters, sort, group, asOf]);
  useEffect(() => {
    if (total !== null && !first.stale) setAnnounce(`${total.toLocaleString("ja-JP")} 人が見つかりました`);
  }, [total, first.stale, resultKey]);

  // 名前の引き当て（チップ・候補の表示用。組織の情報は全社公開）
  const names = useMemo(() => {
    const units = tree.data?.units ?? [];
    const entity = new Map<string, string>();
    for (const u of units) if (u.legal_entity.id && u.legal_entity.name) entity.set(u.legal_entity.id, u.legal_entity.name);
    for (const e of page?.facets?.legal_entities ?? []) entity.set(e.value, e.label);
    const levels = new Map((page?.facets?.levels ?? []).map((l) => [l.value, l.label]));
    for (const u of units) if (u.level && !levels.has(u.level.key)) levels.set(u.level.key, u.level.name);
    // 拠点の名前（件数の候補と組織の拠点から。拠点の情報は全社公開）
    const sites = new Map<string, string>();
    for (const u of units) if (u.site) sites.set(u.site.id, u.site.name);
    for (const v of page?.facets?.sites ?? []) sites.set(v.value, v.label);
    return {
      site: (id: string) => sites.get(id) ?? "（表示している日付に無い拠点）",
      entity: (id: string) => entity.get(id) ?? "（不明な法人）",
      org: (id: string) => units.find((u) => u.id === id)?.name ?? "（表示している日付に無い組織）",
      level: (key: string) => levels.get(key) ?? key,
    };
  }, [tree.data, page?.facets]);
  const chips = useChips(filters, names);
  const filterCount = countFilters(filters);

  // 自由語に当たる組織の候補（その組織で絞り込める）
  const q = filters.query?.trim() ?? "";
  const orgHits = useQuery(adapter, q && !results.problem ? `org-search:${q}:${asOf}:5` : null, () => adapter.searchOrgs(q, { asOf, limit: 5 }));

  // 下端に近づいたら次のページを読む
  const sentinel = useRef<HTMLDivElement>(null);
  const { hasMore, loadingMore, loadMore } = results;
  useEffect(() => {
    const el = sentinel.current;
    if (!el || !hasMore || typeof IntersectionObserver === "undefined") return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((e) => e.isIntersecting)) loadMore();
    }, { rootMargin: "400px" });
    observer.observe(el);
    return () => observer.disconnect();
  }, [hasMore, loadingMore, loadMore]);

  const openSheet = () => sheetRef.current?.showModal();
  const closeSheet = () => sheetRef.current?.close();
  const clearAll = () => setState({ ...state, filters: { query: filters.query } });
  const busy = first.loading || first.stale;

  const panel = <FilterPanel filters={filters} facets={page?.facets ?? null} asOf={asOf} onChange={setFilters} entityName={names.entity} siteName={names.site} />;
  const groups = groupPeople(items, group, page?.facets ?? null, total);
  // 読み込んだ順の位置（カードの出現の遅れを、ページの中の位置で決める）
  const position = new Map(items.map((p, i) => [p.id, i]));

  return (
    <div className="flex h-full min-h-0">
      {/* デスクトップ: 左の列に条件 */}
      <aside aria-label="絞り込みの条件" className="hidden w-72 shrink-0 overflow-y-auto border-r border-border bg-surface px-4 py-3 lg:block">
        <h2 className="m-0 mb-1 text-sm font-bold">絞り込み</h2>
        {panel}
      </aside>

      <div className="min-w-0 flex-1 overflow-y-auto">
        <div className="mx-auto flex w-full max-w-[1200px] flex-col gap-3 p-4">
          {/* 検索語 */}
          <div className="flex flex-col gap-1">
            <label htmlFor={inputId} className="text-xs font-bold text-muted">
              氏名・フリガナ・ローマ字・社員番号・メールで探す
            </label>
            <div className="relative">
              <span className="pointer-events-none absolute top-1/2 left-3 -translate-y-1/2 text-muted">
                <SearchIcon />
              </span>
              <input
                id={inputId}
                type="search"
                value={text}
                // スマホでは開いた途端にキーボードで結果が隠れないよう、幅の広い画面だけ入力欄に移る
                autoFocus={typeof window !== "undefined" && window.matchMedia?.("(min-width: 1024px)").matches}
                onChange={(e) => setText(e.target.value)}
                placeholder="例: やまもと・yamamoto・E0123"
                autoComplete="off"
                aria-describedby={`${inputId}-hint`}
                className="w-full rounded-md border border-border bg-surface py-2 pr-3 pl-9 text-base shadow-[inset_0_1px_2px_rgb(0_0_0/0.04)]"
              />
            </div>
            <p id={`${inputId}-hint`} className={`m-0 text-xs text-muted ${results.problem ? "" : "max-sm:hidden"}`}>
              {results.problem ? (
                <span className="text-warn-fg">{results.problem}</span>
              ) : adapter.kind === "demo" ? (
                "ひらがな・カタカナ・半角・ローマ字の綴り（Sato / Satou / Satoh）の違いを同じとみなします。デモでは異体字の正規化は簡易です。"
              ) : (
                "異体字・かなの揺れ・ローマ字の綴りの違いは API 側で正規化して照合します。見える項目だけで照合します。"
              )}
            </p>
          </div>

          {/* 自由語に当たる組織 */}
          {orgHits.data && orgHits.data.items.length > 0 && (
            <section aria-labelledby="search-orgs" className="flex flex-wrap items-center gap-1.5 text-xs">
              <h2 id="search-orgs" className="m-0 mr-1 text-xs font-bold text-muted">
                組織の候補
              </h2>
              {orgHits.data.items.map((u) => (
                <span key={u.id} className="inline-flex items-center overflow-hidden rounded-full border border-border bg-surface">
                  <button
                    type="button"
                    onClick={() => setState({ ...state, filters: { ...filters, query: "", orgId: u.id, includeDescendants: true } })}
                    className="px-2 py-0.5 hover:bg-surface-2"
                    title="この組織（配下を含む）の人に絞り込む"
                  >
                    {u.name}
                    <span className="sr-only">で絞り込む</span>
                  </button>
                  <button
                    type="button"
                    onClick={() => showOrgIn("org-chart", u.id)}
                    className="border-l border-border px-1.5 py-0.5 text-muted hover:bg-surface-2 hover:text-fg"
                    aria-label={`${u.name}を組織図で見る`}
                    title="組織図で見る"
                  >
                    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-3" fill="none" stroke="currentColor" strokeWidth="1.5">
                      <path d="M6 3h7v7M13 3 5 11M3 6v7h7" />
                    </svg>
                  </button>
                </span>
              ))}
            </section>
          )}

          {/* 件数・並べ替え・表示の形 */}
          <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
            <button
              type="button"
              onClick={openSheet}
              className="inline-flex items-center gap-1.5 rounded-sm border border-border bg-surface px-2.5 py-1 text-sm hover:bg-surface-2 pointer-coarse:min-h-11 lg:hidden"
            >
              <FilterIcon />
              絞り込み
              {filterCount > 0 && <span className="rounded-full bg-accent px-1.5 text-[11px] leading-[16px] text-accent-fg tabular">{filterCount}</span>}
            </button>
            <h2 className="sr-only">検索の結果</h2>
            <p className="m-0 text-sm">
              {total !== null ? (
                <>
                  <span className="tabular text-lg font-bold">{total.toLocaleString("ja-JP")}</span>
                  <span className="ml-0.5">人</span>
                  <span className="ml-1 text-xs text-muted">（あなたに見える人）</span>
                  {busy && <span className="ml-2 text-xs text-muted">更新中…</span>}
                </>
              ) : busy ? (
                <span className="text-muted">検索中…</span>
              ) : null}
            </p>
            <div className="ml-auto flex items-center gap-2 max-sm:w-full">
              <label htmlFor={sortId} className="text-xs text-muted max-sm:sr-only">
                並べ替え
              </label>
              {group === "none" ? (
                <select
                  id={sortId}
                  value={sort}
                  onChange={(e) => setState({ ...state, sort: e.target.value as PersonSort })}
                  className="rounded-sm border border-border bg-surface px-1.5 py-1 text-sm max-sm:min-w-0 max-sm:flex-1 pointer-coarse:min-h-11"
                >
                  {(["kana", "org", "hire_date", "hire_date_desc"] as PersonSort[]).map((s) => (
                    <option key={s} value={s}>
                      {SORT_LABEL[s]}
                    </option>
                  ))}
                </select>
              ) : (
                // グループ化している間は、グループの順（拠点順・リモートワーク対象が先）で、グループの中はフリガナ順
                <select id={sortId} disabled value="grouped" className="rounded-sm border border-border bg-surface-2 px-1.5 py-1 text-sm text-muted max-sm:min-w-0 max-sm:flex-1 pointer-coarse:min-h-11">
                  <option value="grouped">グループの中はフリガナ順</option>
                </select>
              )}
              <LayoutSwitch layout={layout} onChange={(next) => setState({ ...state, layout: next })} />
            </div>
            <div className="flex w-full justify-end max-sm:justify-start">
              <GroupSwitch group={group} onChange={(next) => setState({ ...state, group: next })} />
            </div>
          </div>

          {/* 選んだ条件 */}
          {chips.length > 0 && (
            <fieldset className="m-0 flex flex-wrap items-center gap-1.5 border-0 p-0">
              <legend className="sr-only">選んだ条件（押すと外す）</legend>
              {chips.map((c) => (
                <button
                  key={c.id}
                  type="button"
                  onClick={() => setFilters(c.remove())}
                  aria-label={`条件を外す: ${c.label}`}
                  className="inline-flex max-w-full items-center gap-1 rounded-full border border-accent/40 bg-accent-weak py-0.5 pr-1.5 pl-2.5 text-xs pointer-coarse:min-h-11 pointer-coarse:px-3 text-accent hover:bg-accent hover:text-accent-fg"
                >
                  <span className="truncate">{c.label}</span>
                  <svg aria-hidden="true" viewBox="0 0 12 12" className="size-3 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.6">
                    <path d="M3 3l6 6m0-6L3 9" />
                  </svg>
                </button>
              ))}
              {chips.length > 1 && (
                <button type="button" onClick={clearAll} className="px-1 text-xs text-accent underline-offset-2 hover:underline">
                  すべて解除
                </button>
              )}
            </fieldset>
          )}

          <output aria-live="polite" className="sr-only">
            {announce}
          </output>

          {/* 結果 */}
          <section aria-label="検索の結果" aria-busy={busy}>
            {results.problem ? null : first.error ? (
              <div className="flex flex-col items-start gap-2">
                <ErrorNote>{describeError(first.error)}</ErrorNote>
                <button type="button" onClick={() => setRetry((n) => n + 1)} className="rounded-sm border border-border px-2 py-0.5 text-sm hover:bg-surface-2">
                  もう一度試す
                </button>
              </div>
            ) : !page ? (
              <ul className="m-0 grid list-none grid-cols-[repeat(auto-fill,minmax(min(100%,300px),1fr))] gap-3 p-0" aria-label="読み込み中">
                {Array.from({ length: 6 }, (_, i) => (
                  <li key={i}>
                    <PersonCardSkeleton />
                  </li>
                ))}
              </ul>
            ) : items.length === 0 ? (
              <div className="rounded-lg border border-dashed border-border bg-surface px-6 py-10 text-center">
                <p className="m-0 text-base font-bold">条件に一致する人はいません</p>
                <p className="m-0 mt-1 text-sm text-muted">
                  {isEmptySearch(filters)
                    ? "この日付で見られる人がいません。タイムマシンで日付を変えてみてください。"
                    : "条件を減らすか、別の語で探してください。見えない項目（非公開）では照合しません。"}
                </p>
                <div className="mt-3 flex flex-wrap justify-center gap-2">
                  {q && (
                    <button type="button" onClick={() => setText("")} className="rounded-sm border border-border px-3 py-1 text-sm hover:bg-surface-2">
                      検索語「{q}」を消す
                    </button>
                  )}
                  {chips.length > 0 && (
                    <button type="button" onClick={clearAll} className="rounded-sm border border-border px-3 py-1 text-sm hover:bg-surface-2">
                      条件をすべて解除
                    </button>
                  )}
                </div>
              </div>
            ) : layout === "table" ? (
              <div className={busy ? "opacity-60 transition-opacity" : "transition-opacity"}>
                <PeopleTable groups={groups} selectedId={personId} onOpen={openPerson} onCopied={setAnnounce} />
              </div>
            ) : (
              <div className={`flex flex-col gap-4 ${busy ? "opacity-60 transition-opacity" : "transition-opacity"}`}>
                {groups.map((g) => (
                  <section key={g.key} aria-label={g.kind === "all" ? undefined : g.title} className="flex flex-col gap-2">
                    {g.kind !== "all" && (
                      // 区切りの見出しはスクロールしても上に残す（どのグループを見ているか分かるように）
                      <div className="sticky top-0 z-[5] -mx-4 border-b border-border bg-bg/95 px-4 py-1.5 backdrop-blur-sm">
                        <GroupHeading group={g} level={3} />
                      </div>
                    )}
                    <ul className="m-0 grid list-none grid-cols-[repeat(auto-fill,minmax(min(100%,300px),1fr))] gap-3 p-0">
                      {g.people.map((p) => {
                        const i = position.get(p.id) ?? 0;
                        return (
                          <motion.li
                            key={p.id}
                            initial={reduceMotion ? false : { opacity: 0, y: 6 }}
                            animate={{ opacity: 1, y: 0 }}
                            transition={{ duration: 0.18, delay: reduceMotion ? 0 : Math.min(i % PAGE_SIZE, 12) * 0.015, ease: "easeOut" }}
                            className="min-w-0"
                          >
                            <PersonCard
                              person={p}
                              selected={p.id === personId}
                              onOpen={() => openPerson(p.id)}
                              onCopied={setAnnounce}
                              showHireDate={group === "none" && (sort === "hire_date" || sort === "hire_date_desc")}
                            />
                          </motion.li>
                        );
                      })}
                    </ul>
                  </section>
                ))}
              </div>
            )}

            {page && items.length > 0 && (
              <div ref={sentinel} className="mt-4 flex flex-col items-center gap-2 pb-6 text-sm">
                {results.moreError ? (
                  <>
                    <ErrorNote>{describeError(results.moreError)}</ErrorNote>
                    <button type="button" onClick={loadMore} className="rounded-sm border border-border px-3 py-1 hover:bg-surface-2">
                      もう一度読む
                    </button>
                  </>
                ) : loadingMore ? (
                  <Spinner label="続きを読み込み中" />
                ) : hasMore ? (
                  <button type="button" onClick={loadMore} className="rounded-sm border border-border bg-surface px-3 py-1 hover:bg-surface-2">
                    さらに表示
                    {total !== null && <span className="ml-1 text-xs text-muted tabular">（{items.length.toLocaleString("ja-JP")} / {total.toLocaleString("ja-JP")} 人）</span>}
                  </button>
                ) : (
                  <p className="m-0 text-xs text-muted">{items.length.toLocaleString("ja-JP")} 人をすべて表示しました。</p>
                )}
              </div>
            )}
          </section>
        </div>
      </div>

      {/* スマホ: 条件はシート（ネイティブの dialog。Esc・閉じるボタンで閉じ、開いている間は背後を操作させない） */}
      <dialog
        ref={sheetRef}
        aria-labelledby={`${inputId}-sheet`}
        className="m-0 mt-auto max-h-[85dvh] w-full max-w-none rounded-t-xl border-t border-border bg-surface p-0 text-fg shadow-2xl backdrop:bg-black/60 lg:hidden"
      >
        <div className="flex max-h-[85dvh] flex-col">
          <div className="flex items-center justify-between border-b border-border px-4 py-2.5">
            <h2 id={`${inputId}-sheet`} className="m-0 text-base font-bold">
              絞り込み
            </h2>
            <button type="button" onClick={closeSheet} className="rounded-sm border border-border px-2 py-0.5 text-sm hover:bg-surface-2 pointer-coarse:min-h-11 pointer-coarse:px-3">
              閉じる
            </button>
          </div>
          <div className="min-h-0 flex-1 overflow-y-auto px-4 py-1">{panel}</div>
          <div className="flex items-center gap-2 border-t border-border px-4 py-2.5">
            {filterCount > 0 && (
              <button type="button" onClick={clearAll} className="text-sm text-accent hover:underline">
                すべて解除
              </button>
            )}
            <button type="button" onClick={closeSheet} className="ml-auto rounded-sm bg-accent px-4 py-1.5 text-sm font-bold text-accent-fg">
              {total !== null ? `${total.toLocaleString("ja-JP")} 人を表示` : "結果を表示"}
            </button>
          </div>
        </div>
      </dialog>
    </div>
  );
}
