// 検索の条件パネル（デスクトップは左の列、スマホはシートの中）。値の候補と件数は API の facets をそのまま出す。
// 件数は閲覧者に見える人・見える値だけで数えた値（API の規則）。選んだのに候補に無い値（件数 0）も外せるように残す。
import { useEffect, useId, useMemo, useState, type ReactNode } from "react";
import { useOrgTree } from "../../app/hooks";
import { useAdapter } from "../../data/adapterContext";
import { useQuery } from "../../data/query";
import type { FacetValue, PersonFacets, PersonFilters } from "../../data/types";
import { DateField } from "../../components/DateField";
import { LineKey, type LinkKind } from "../../components/ui";
import { useFormatDate } from "../../lib/dateStyle";
import { ACCOUNT_STATUS_LABEL, ASSIGNMENT_KIND_LABEL, EMPLOYMENT_STATUS_LABEL, employmentTypeLabel, prefectureName, siteTypeLabel } from "../../lib/labels";
import { validateQuery } from "../../lib/search";
import { matchingTenure, TENURE_PRESETS, tenureToHired, toggleValue, type ListFilterKey } from "./searchState";

/** 所属の種類の表示名（組織図と同じ線の見本を添える。色だけに頼らない）。 */
function kindLabel(v: FacetValue): ReactNode {
  return (
    <span className="inline-flex items-center gap-1.5">
      <LineKey kind={v.value as LinkKind} width={14} />
      {ASSIGNMENT_KIND_LABEL[v.value] ?? v.label}
    </span>
  );
}

/** 最初に見せる候補の数（残りは「すべて表示」）。 */
const COLLAPSED = 6;

function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}

function Section({ title, selected, children }: { title: string; selected: number; children: React.ReactNode }) {
  return (
    <details open className="group border-b border-border py-2.5 last:border-b-0">
      <summary className="flex cursor-pointer list-none items-center gap-2 rounded-sm py-0.5 pointer-coarse:min-h-11 text-xs font-bold text-muted select-none [&::-webkit-details-marker]:hidden">
        <svg aria-hidden="true" viewBox="0 0 12 12" className="size-3 transition-transform group-open:rotate-90" fill="currentColor">
          <path d="M4 2.5 8 6l-4 3.5z" />
        </svg>
        <span className="text-fg">{title}</span>
        {selected > 0 && (
          <span className="rounded-full bg-accent px-1.5 text-[11px] leading-[16px] text-accent-fg tabular">
            {selected}
            <span className="sr-only">件を選択中</span>
          </span>
        )}
      </summary>
      <div className="mt-2">{children}</div>
    </details>
  );
}

/** 値の候補のチェックボックスの一覧。 */
function FacetList({
  name,
  values,
  selected,
  labelOf,
  onToggle,
  hideEmpty = false,
}: {
  hideEmpty?: boolean;
  name: string;
  values: FacetValue[];
  selected: string[];
  labelOf?: (v: FacetValue) => ReactNode;
  onToggle: (value: string) => void;
}) {
  const [expanded, setExpanded] = useState(false);
  // 選んだ値は候補に無くても（件数 0）出す
  const merged = useMemo(() => {
    const known = new Set(values.map((v) => v.value));
    return [...values, ...selected.filter((s) => !known.has(s)).map((s) => ({ value: s, label: s, count: 0 }))];
  }, [values, selected]);
  if (merged.length === 0) {
    return hideEmpty ? null : <p className="m-0 text-xs text-muted">選べる値はありません。</p>;
  }
  const shown = expanded ? merged : merged.slice(0, COLLAPSED);
  return (
    <>
      <ul className="m-0 flex list-none flex-col gap-0.5 p-0">
        {shown.map((v) => {
          const checked = selected.includes(v.value);
          const label = labelOf ? labelOf(v) : v.label;
          return (
            <li key={v.value}>
              <label className={`flex cursor-pointer items-center gap-2 rounded-sm px-1 py-0.5 text-sm hover:bg-surface-2 pointer-coarse:min-h-11 ${v.count === 0 && !checked ? "text-muted" : ""}`}>
                <input type="checkbox" name={name} checked={checked} onChange={() => onToggle(v.value)} className="size-4 shrink-0 accent-[var(--accent)]" />
                <span className="min-w-0 flex-1 truncate">{label}</span>
                <span className="tabular text-xs text-muted">
                  {v.count.toLocaleString("ja-JP")}
                  <span className="sr-only">人</span>
                </span>
              </label>
            </li>
          );
        })}
      </ul>
      {merged.length > COLLAPSED && (
        <button type="button" onClick={() => setExpanded(!expanded)} aria-expanded={expanded} className="mt-1 px-1 text-xs text-accent hover:underline pointer-coarse:min-h-11">
          {expanded ? "少なく表示" : `すべて表示（${merged.length}）`}
        </button>
      )}
    </>
  );
}

/** 組織の絞り込み（組織名・コードで探して選ぶ）。 */
function OrgFilter({ filters, asOf, onChange }: { filters: PersonFilters; asOf: string; onChange: (next: PersonFilters) => void }) {
  const { adapter } = useAdapter();
  const tree = useOrgTree(asOf);
  const inputId = useId();
  const [text, setText] = useState("");
  const query = useDebounced(text.trim(), 250);
  const problem = query ? validateQuery(query) : null;
  const results = useQuery(adapter, query && !problem && !filters.orgId ? `org-search:${query}:${asOf}:8` : null, () => adapter.searchOrgs(query, { asOf, limit: 8 }));
  const selected = filters.orgId ? tree.data?.units.find((u) => u.id === filters.orgId) : undefined;

  if (filters.orgId) {
    return (
      <div className="flex flex-col gap-1.5">
        <div className="flex items-start gap-2 rounded-sm border border-border bg-surface-2 px-2 py-1.5">
          <span className="min-w-0 flex-1 text-sm">
            <span className="font-bold">{selected?.name ?? (tree.loading ? "…" : "（表示している日付に無い組織）")}</span>
            {selected && (
              <span className="block text-[11px] text-muted">
                {selected.legal_entity.name}
                {selected.level ? `・${selected.level.name}` : ""}
              </span>
            )}
          </span>
          <button type="button" onClick={() => onChange({ ...filters, orgId: null })} className="shrink-0 text-xs text-accent hover:underline">
            解除
          </button>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={filters.includeDescendants !== false}
            onChange={(e) => onChange({ ...filters, includeDescendants: e.target.checked })}
            className="size-4 accent-[var(--accent)]"
          />
          配下の組織を含む
        </label>
      </div>
    );
  }

  return (
    <div>
      <label htmlFor={inputId} className="sr-only">
        組織を名前・コードで探す
      </label>
      <input
        id={inputId}
        type="search"
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder="組織名・コードで探す"
        autoComplete="off"
        className="w-full rounded-sm border border-border bg-surface px-2 py-1 text-sm"
      />
      {problem && text.trim().length > 0 && <p className="m-0 mt-1 text-xs text-muted">{problem}</p>}
      {results.data && (
        <ul className="m-0 mt-1 flex list-none flex-col p-0" aria-label="組織の候補">
          {results.data.items.length === 0 && <li className="px-1 text-xs text-muted">該当する組織はありません。</li>}
          {results.data.items.map((u) => (
            <li key={u.id}>
              <button
                type="button"
                onClick={() => {
                  setText("");
                  onChange({ ...filters, orgId: u.id, includeDescendants: true });
                }}
                className="w-full rounded-sm px-1 py-0.5 text-left text-sm hover:bg-surface-2"
              >
                {u.name}
                <span className="ml-1.5 text-[11px] text-muted">
                  {u.legal_entity.name}
                  {u.level ? `・${u.level.name}` : ""}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

/** リモートワークの有無（どちらか 1 つ。件数つき）。見えない人は数えない（「非公開」の候補は作らない）。 */
function RemoteFilter({ filters, values, onChange }: { filters: PersonFilters; values: FacetValue[]; onChange: (next: PersonFilters) => void }) {
  const name = useId();
  const count = (v: string) => values.find((x) => x.value === v)?.count ?? 0;
  const current = filters.remoteWork === true ? "true" : filters.remoteWork === false ? "false" : "";
  const options: Array<[string, string, number | null]> = [
    ["", "指定しない", null],
    ["true", "対象", count("true")],
    ["false", "対象外", count("false")],
  ];
  return (
    <fieldset className="m-0 border-0 p-0">
      <legend className="sr-only">リモートワークの有無</legend>
      <ul className="m-0 flex list-none flex-col gap-0.5 p-0">
        {options.map(([value, label, n]) => (
          <li key={value || "any"}>
            <label className={`flex cursor-pointer items-center gap-2 rounded-sm px-1 py-0.5 text-sm hover:bg-surface-2 pointer-coarse:min-h-11 ${n === 0 && current !== value ? "text-muted" : ""}`}>
              <input
                type="radio"
                name={name}
                checked={current === value}
                onChange={() => onChange({ ...filters, remoteWork: value === "" ? null : value === "true" })}
                className="size-4 shrink-0 accent-[var(--accent)]"
              />
              <span className="min-w-0 flex-1 truncate">{label}</span>
              {n !== null && (
                <span className="tabular text-xs text-muted">
                  {n.toLocaleString("ja-JP")}
                  <span className="sr-only">人</span>
                </span>
              )}
            </label>
          </li>
        ))}
      </ul>
    </fieldset>
  );
}

/** 入社年月日の範囲（勤続年数の目安からも選べる）。 */
function HireFilter({ filters, onChange }: { filters: PersonFilters; onChange: (next: PersonFilters) => void }) {
  const { adapter } = useAdapter();
  const formatDate = useFormatDate();
  const current = matchingTenure(filters, adapter.businessDate);
  return (
    <div className="flex flex-col gap-2">
      <fieldset className="m-0 border-0 p-0">
        <legend className="mb-1 text-xs text-muted">勤続年数（{formatDate(adapter.businessDate)}現在）</legend>
        <div className="flex flex-wrap gap-1">
          {TENURE_PRESETS.map((p) => {
            const active = current?.id === p.id;
            return (
              <button
                key={p.id}
                type="button"
                aria-pressed={active}
                onClick={() => onChange({ ...filters, ...(active ? { hiredFrom: null, hiredTo: null } : tenureToHired(p, adapter.businessDate)) })}
                className={`rounded-full border px-2 py-0.5 text-xs pointer-coarse:min-h-11 pointer-coarse:px-3 ${active ? "border-accent bg-accent text-accent-fg" : "border-border hover:bg-surface-2"}`}
              >
                {p.label}
              </button>
            );
          })}
        </div>
      </fieldset>
      <fieldset className="m-0 border-0 p-0">
        <legend className="mb-1 text-xs text-muted">入社年月日（和暦も可。空にすると指定なし）</legend>
        <div className="grid grid-cols-[auto_1fr] items-center gap-x-2 gap-y-1.5 text-xs">
          <span aria-hidden="true" className="text-muted">から</span>
          <DateField label="入社年月日（から）" labelVisible="never" value={filters.hiredFrom ?? null} max={filters.hiredTo ?? undefined} clearable onChange={(v) => onChange({ ...filters, hiredFrom: v })} widthClass="w-[9em]" />
          <span aria-hidden="true" className="text-muted">まで</span>
          <DateField label="入社年月日（まで）" labelVisible="never" value={filters.hiredTo ?? null} min={filters.hiredFrom ?? undefined} clearable onChange={(v) => onChange({ ...filters, hiredTo: v })} widthClass="w-[9em]" />
        </div>
      </fieldset>
    </div>
  );
}

export function FilterPanel({
  filters,
  facets,
  asOf,
  onChange,
  entityName,
  siteName,
}: {
  filters: PersonFilters;
  facets: PersonFacets | null;
  asOf: string;
  onChange: (next: PersonFilters) => void;
  /** 法人の ID から名前（選んだのに候補に無い法人の表示用）。 */
  entityName: (id: string) => string;
  /** 拠点の ID から名前（選んだのに候補に無い拠点の表示用）。 */
  siteName: (id: string) => string;
}) {
  const toggle = (key: ListFilterKey) => (value: string) => onChange(toggleValue(filters, key, value));
  const sel = (key: ListFilterKey) => (filters[key] as string[] | undefined) ?? [];
  const empty: FacetValue[] = [];
  const f = facets;
  // 在籍状態・アカウント状態は、見える人がいるとき（件数がある・選択中）だけ出す
  const showStatuses = (f?.employment_statuses.length ?? 0) > 0 || sel("employmentStatuses").length > 0;
  const showAccounts = (f?.account_statuses.length ?? 0) > 0 || sel("accountStatuses").length > 0;

  const nothing = f !== null && Object.values(f).every((values) => values.length === 0);
  return (
    <div className="flex flex-col">
      {nothing && <p className="m-0 mt-2 rounded-sm bg-surface-2 px-2 py-1.5 text-xs text-muted">今の条件に一致する人がいないため、選べる値がありません。条件を外すと候補が出ます。</p>}
      <Section title="組織" selected={(filters.orgId ? 1 : 0) + sel("levels").length}>
        <OrgFilter filters={filters} asOf={asOf} onChange={onChange} />
        <p className="m-0 mt-3 mb-1 text-xs text-muted">組織の段</p>
        <FacetList hideEmpty={nothing} name="level" values={f?.levels ?? empty} selected={sel("levels")} onToggle={toggle("levels")} />
      </Section>
      <Section title="主務・兼務・出向" selected={sel("assignmentKinds").length}>
        <FacetList hideEmpty={nothing}
          name="assignment_kind"
          values={f?.assignment_kinds ?? empty}
          selected={sel("assignmentKinds")}
          labelOf={kindLabel}
          onToggle={toggle("assignmentKinds")}
        />
      </Section>
      <Section title="役職" selected={sel("positions").length}>
        <FacetList hideEmpty={nothing} name="position" values={f?.positions ?? empty} selected={sel("positions")} onToggle={toggle("positions")} />
      </Section>
      <Section title="法人" selected={sel("legalEntities").length}>
        <FacetList hideEmpty={nothing}
          name="legal_entity"
          values={f?.legal_entities ?? empty}
          selected={sel("legalEntities")}
          labelOf={(v) => (v.label === v.value ? entityName(v.value) : v.label)}
          onToggle={toggle("legalEntities")}
        />
      </Section>
      <Section title="雇用区分" selected={sel("employmentTypes").length}>
        <FacetList hideEmpty={nothing}
          name="employment_type"
          values={f?.employment_types ?? empty}
          selected={sel("employmentTypes")}
          labelOf={(v) => employmentTypeLabel(v.value)}
          onToggle={toggle("employmentTypes")}
        />
      </Section>
      <Section title="職種" selected={sel("jobTypes").length}>
        <FacetList hideEmpty={nothing} name="job_type" values={f?.job_types ?? empty} selected={sel("jobTypes")} onToggle={toggle("jobTypes")} />
      </Section>
      <Section title="拠点" selected={sel("sites").length + sel("siteTypes").length + sel("prefectures").length}>
        <FacetList
          hideEmpty={nothing}
          name="site"
          values={f?.sites ?? empty}
          selected={sel("sites")}
          labelOf={(v) => (v.label === v.value ? siteName(v.value) : v.label)}
          onToggle={toggle("sites")}
        />
        <p className="m-0 mt-3 mb-1 text-xs text-muted">拠点の種別</p>
        <FacetList hideEmpty={nothing} name="site_type" values={f?.site_types ?? empty} selected={sel("siteTypes")} labelOf={(v) => siteTypeLabel(v.value)} onToggle={toggle("siteTypes")} />
        <p className="m-0 mt-3 mb-1 text-xs text-muted">都道府県</p>
        <FacetList
          hideEmpty={nothing}
          name="prefecture"
          values={f?.prefectures ?? empty}
          selected={sel("prefectures")}
          labelOf={(v) => (v.label === v.value ? (prefectureName(v.value) ?? v.value) : v.label)}
          onToggle={toggle("prefectures")}
        />
      </Section>
      <Section title="リモートワーク" selected={filters.remoteWork === true || filters.remoteWork === false ? 1 : 0}>
        <RemoteFilter filters={filters} values={f?.remote_work ?? empty} onChange={onChange} />
      </Section>
      <Section title="入社年月日・勤続" selected={filters.hiredFrom || filters.hiredTo ? 1 : 0}>
        <HireFilter filters={filters} onChange={onChange} />
      </Section>
      {showStatuses && (
        <Section title="在籍状態" selected={sel("employmentStatuses").length}>
          <FacetList hideEmpty={nothing}
            name="employment_status"
            values={f?.employment_statuses ?? empty}
            selected={sel("employmentStatuses")}
            labelOf={(v) => EMPLOYMENT_STATUS_LABEL[v.value] ?? v.label}
            onToggle={toggle("employmentStatuses")}
          />
        </Section>
      )}
      {showAccounts && (
        <Section title="アカウント" selected={sel("accountStatuses").length}>
          <FacetList hideEmpty={nothing}
            name="account_status"
            values={f?.account_statuses ?? empty}
            selected={sel("accountStatuses")}
            labelOf={(v) => ACCOUNT_STATUS_LABEL[v.value] ?? v.label}
            onToggle={toggle("accountStatuses")}
          />
        </Section>
      )}
      <p className="m-0 mt-3 text-[11px] leading-relaxed text-muted">
        件数は、あなたに見える人の、見える項目の値だけで数えています（非公開の項目の人は、その条件の件数に含みません）。
      </p>
    </div>
  );
}
