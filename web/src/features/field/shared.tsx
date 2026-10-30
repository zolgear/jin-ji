// 組織の変更の画面（素案・登録・グループ・管理）で共有する部品: 組織の選択・人の選択・名前の解決・変更の追加と説明。
import { useEffect, useId, useMemo, useState } from "react";
import { Button } from "../../auth/ui";
import { useAdapter } from "../../data/adapterContext";
import { CHANGE_KIND_LABEL, GROUP_ROLE_LABEL, VALUE_LABEL, type Group, type GroupRole, type PlanChange } from "../../data/field";
import { useQuery } from "../../data/query";
import type { OrgUnit } from "../../data/types";
import { personName } from "../../components/ui";

/** UUID（v4）。crypto.randomUUID は HTTPS でないページ（LAN の確認用サーバーなど）では使えないので、getRandomValues で作る。 */
export function newUuid(): string {
  const b = crypto.getRandomValues(new Uint8Array(16));
  b[6] = (b[6]! & 0x0f) | 0x40;
  b[8] = (b[8]! & 0x3f) | 0x80;
  const h = [...b].map((x) => x.toString(16).padStart(2, "0")).join("");
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

export const inputClass = "min-h-10 rounded-md border border-border bg-surface px-3 py-2 text-sm text-fg";

/** 組織を組織図の順（親 → 子）に並べ、深さを付ける。 */
export function orderUnits(units: OrgUnit[]): Array<{ unit: OrgUnit; depth: number }> {
  const children = new Map<string | null, OrgUnit[]>();
  const ids = new Set(units.map((u) => u.id));
  for (const u of units) {
    const parent = u.parent_id && ids.has(u.parent_id) ? u.parent_id : null;
    children.set(parent, [...(children.get(parent) ?? []), u]);
  }
  for (const list of children.values()) list.sort((a, b) => a.code.localeCompare(b.code));
  const out: Array<{ unit: OrgUnit; depth: number }> = [];
  const walk = (parent: string | null, depth: number) => {
    for (const u of children.get(parent) ?? []) {
      out.push({ unit: u, depth });
      walk(u.id, depth + 1);
    }
  };
  walk(null, 0);
  return out;
}

/** その日の組織（組織図の順）。 */
export function useOrgUnits(asOf: string) {
  const { adapter } = useAdapter();
  const tree = useQuery(adapter, `field-orgs:${asOf}`, () => adapter.getOrgTree(asOf));
  return useMemo(() => orderUnits(tree.data?.units ?? []), [tree.data]);
}

export function OrgSelect({
  label,
  asOf,
  value,
  onChange,
  extra = [],
}: {
  label: string;
  asOf: string;
  value: string;
  onChange: (id: string) => void;
  /** 同じ素案で新設する組織（選択肢の最後に足す）。 */
  extra?: Array<{ id: string; name: string }>;
}) {
  const units = useOrgUnits(asOf);
  const id = useId();
  return (
    <label htmlFor={id} className="flex flex-col gap-1 text-sm">
      <span className="text-muted">{label}</span>
      <select id={id} className={inputClass} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">選んでください</option>
        {units.map(({ unit, depth }) => (
          <option key={unit.id} value={unit.id}>
            {`${"　".repeat(depth)}${unit.name}（${unit.code}）`}
          </option>
        ))}
        {extra.map((u) => (
          <option key={u.id} value={u.id}>
            {`＋ ${u.name}（新設）`}
          </option>
        ))}
      </select>
    </label>
  );
}

/** 名前で人を探して選ぶ。 */
export function PersonPicker({ label, asOf, value, onChange }: { label: string; asOf: string; value: string; onChange: (id: string, name: string) => void }) {
  const { adapter } = useAdapter();
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<Array<{ id: string; name: string; sub: string }>>([]);
  const [chosen, setChosen] = useState<string | null>(null);
  const id = useId();
  useEffect(() => {
    if (query.trim().length < 1) return;
    let cancelled = false;
    const timer = setTimeout(() => {
      adapter.searchPeople(query.trim(), { asOf, limit: 8 }).then(
        (page) => {
          if (!cancelled) {
            setResults(
              page.items.map((p) => ({
                id: p.id,
                name: personName(p),
                sub: p.assignments?.find((a) => a.type === "primary")?.org_unit_name ?? "",
              })),
            );
          }
        },
        () => !cancelled && setResults([]),
      );
    }, 200);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [adapter, asOf, query]);
  return (
    <div className="flex flex-col gap-1 text-sm">
      <label htmlFor={id} className="text-muted">
        {label}
      </label>
      {value && chosen ? (
        <div className="flex items-center gap-2">
          <span className="font-bold">{chosen}</span>
          <Button variant="ghost" onClick={() => (setChosen(null), onChange("", ""))}>
            変える
          </Button>
        </div>
      ) : (
        <>
          <input id={id} className={inputClass} value={query} onChange={(e) => setQuery(e.target.value)} placeholder="氏名で検索" autoComplete="off" />
          {query.trim() && results.length > 0 && (
            <ul className="m-0 max-h-48 list-none overflow-y-auto rounded-md border border-border p-0">
              {results.map((r) => (
                <li key={r.id}>
                  <button
                    type="button"
                    className="flex w-full items-baseline gap-2 px-3 py-1.5 text-left hover:bg-surface-2"
                    onClick={() => {
                      setChosen(r.name);
                      setResults([]);
                      setQuery("");
                      onChange(r.id, r.name);
                    }}
                  >
                    <span>{r.name}</span>
                    <span className="text-xs text-muted">{r.sub}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </div>
  );
}

/** 人の ID → 表示名（閲覧者に見える人だけ。見えない人は「—」）。 */
export function usePeopleNames(ids: string[], asOf: string): Map<string, string> {
  const { adapter } = useAdapter();
  const key = [...new Set(ids)].sort().join(",");
  const result = useQuery(adapter, key ? `field-names:${asOf}:${key}` : null, async () => {
    const list = key.split(",");
    const names = new Map<string, string>();
    for (let i = 0; i < list.length; i += 100) {
      for (const p of await adapter.getPeopleByIds(list.slice(i, i + 100), asOf)) names.set(p.id, personName(p));
    }
    return names;
  });
  return result.data ?? new Map();
}

/** 変更の説明（組織・人は名前で）。 */
export function describeChange(
  change: PlanChange,
  names: { org: (id: string | null | undefined) => string; person: (id: string | null | undefined) => string; group?: (id: string | null | undefined) => string },
): string {
  const v = change.values;
  switch (change.kind) {
    case "assignment":
      return `${names.person(change.target_id)} → ${names.org(v.org_unit_id)}${v.position_title ? `（${v.position_title}）` : ""}`;
    case "person":
      return `${names.person(change.target_id)}: ${Object.entries(v)
        .map(([k, x]) => `${VALUE_LABEL[k] ?? k} ${x ?? "なし"}`)
        .join("・")}`;
    case "org_create":
      return `${v.name ?? ""}（${v.code ?? ""}）を ${names.org(v.parent_org_unit_id)} の下に新設`;
    case "org_update":
      return `${names.org(change.target_id)}: ${Object.entries(v)
        .map(([k, x]) => `${VALUE_LABEL[k] ?? k} → ${k === "parent_org_unit_id" ? names.org(x) : k === "head_person_id" ? names.person(x) : (x ?? "なし")}`)
        .join("・")}`;
    case "org_abolish":
      return `${names.org(change.target_id)} を廃止`;
    case "group_join":
      return `${names.person(v.person_id)} を ${names.group?.(change.target_id) ?? "グループ"} に加える（${GROUP_ROLE_LABEL[(v.role as GroupRole) ?? "member"] ?? ""}）`;
    case "group_leave":
      return `${names.person(v.person_id)} を ${names.group?.(change.target_id) ?? "グループ"} から外す`;
  }
}

type AdderKind = "assignment" | "org_create" | "org_rename" | "org_parent" | "org_head" | "org_abolish" | "contact" | "group_join";

const ADDER_LABEL: Record<AdderKind, string> = {
  assignment: "異動・役職",
  org_create: "組織の新設",
  org_rename: "組織の名称",
  org_parent: "上位組織の付け替え",
  org_head: "組織長",
  org_abolish: "組織の廃止",
  contact: "内線・社用携帯",
  group_join: "グループに加える",
};

/** 変更を 1 つ組み立てて足す。 */
export function ChangeAdder({
  asOf,
  created,
  groups,
  allowGroups,
  onAdd,
}: {
  asOf: string;
  /** 同じ素案で新設する組織。 */
  created: Array<{ id: string; name: string }>;
  groups: Group[];
  allowGroups: boolean;
  onAdd: (change: PlanChange) => void;
}) {
  const [kind, setKind] = useState<AdderKind>("assignment");
  const [person, setPerson] = useState("");
  const [org, setOrg] = useState("");
  const [other, setOther] = useState("");
  const [text, setText] = useState("");
  const [code, setCode] = useState("");
  const [group, setGroup] = useState("");
  const [role, setRole] = useState<GroupRole>("member");
  const reset = () => {
    setPerson("");
    setOrg("");
    setOther("");
    setText("");
    setCode("");
  };
  const kinds = (Object.keys(ADDER_LABEL) as AdderKind[]).filter((k) => allowGroups || k !== "group_join");

  const build = (): PlanChange | null => {
    switch (kind) {
      case "assignment":
        return person && org ? { kind: "assignment", target_id: person, values: text ? { org_unit_id: org, position_title: text } : { org_unit_id: org } } : null;
      case "org_create":
        return text && org ? { kind: "org_create", target_id: newUuid(), values: { name: text, code: code || text, parent_org_unit_id: org } } : null;
      case "org_rename":
        return org && text ? { kind: "org_update", target_id: org, values: { name: text } } : null;
      case "org_parent":
        return org && other ? { kind: "org_update", target_id: org, values: { parent_org_unit_id: other } } : null;
      case "org_head":
        return org && person ? { kind: "org_update", target_id: org, values: { head_person_id: person } } : null;
      case "org_abolish":
        return org ? { kind: "org_abolish", target_id: org, values: {} } : null;
      case "contact":
        return person && (text || code) ? { kind: "person", target_id: person, values: { ...(text ? { extension_number: text } : {}), ...(code ? { work_mobile_phone: code } : {}) } } : null;
      case "group_join":
        return group && person ? { kind: "group_join", target_id: group, values: { person_id: person, role } } : null;
    }
  };
  const change = build();
  const selectId = useId();

  return (
    <div className="flex flex-col gap-3 rounded-md border border-border p-3">
      <label htmlFor={selectId} className="flex flex-col gap-1 text-sm">
        <span className="text-muted">変更の種類</span>
        <select id={selectId} className={inputClass} value={kind} onChange={(e) => (setKind(e.target.value as AdderKind), reset())}>
          {kinds.map((k) => (
            <option key={k} value={k}>
              {ADDER_LABEL[k]}
            </option>
          ))}
        </select>
      </label>
      <div className="grid gap-3 sm:grid-cols-2">
        {(kind === "assignment" || kind === "org_head" || kind === "contact" || kind === "group_join") && (
          <PersonPicker key={kind} label={kind === "org_head" ? "組織長にする人" : "人"} asOf={asOf} value={person} onChange={(id) => setPerson(id)} />
        )}
        {kind !== "contact" && kind !== "group_join" && (
          <OrgSelect
            label={kind === "assignment" ? "異動先" : kind === "org_create" ? "上位組織" : "組織"}
            asOf={asOf}
            value={org}
            onChange={setOrg}
            extra={kind === "assignment" || kind === "org_create" ? created : []}
          />
        )}
        {kind === "org_parent" && <OrgSelect label="新しい上位組織" asOf={asOf} value={other} onChange={setOther} extra={created} />}
        {(kind === "assignment" || kind === "org_create" || kind === "org_rename" || kind === "contact") && (
          <label className="flex flex-col gap-1 text-sm">
            <span className="text-muted">{kind === "assignment" ? "役職（任意）" : kind === "contact" ? "内線" : "名称"}</span>
            <input className={inputClass} value={text} onChange={(e) => setText(e.target.value)} />
          </label>
        )}
        {(kind === "org_create" || kind === "contact") && (
          <label className="flex flex-col gap-1 text-sm">
            <span className="text-muted">{kind === "org_create" ? "組織コード" : "社用携帯"}</span>
            <input className={inputClass} value={code} onChange={(e) => setCode(e.target.value)} />
          </label>
        )}
        {kind === "group_join" && (
          <>
            <label className="flex flex-col gap-1 text-sm">
              <span className="text-muted">グループ</span>
              <select className={inputClass} value={group} onChange={(e) => setGroup(e.target.value)}>
                <option value="">選んでください</option>
                {groups.map((g) => (
                  <option key={g.id} value={g.id}>
                    {g.name}
                  </option>
                ))}
              </select>
            </label>
            <label className="flex flex-col gap-1 text-sm">
              <span className="text-muted">役割</span>
              <select className={inputClass} value={role} onChange={(e) => setRole(e.target.value as GroupRole)}>
                {(Object.keys(GROUP_ROLE_LABEL) as GroupRole[]).map((r) => (
                  <option key={r} value={r}>
                    {GROUP_ROLE_LABEL[r]}
                  </option>
                ))}
              </select>
            </label>
          </>
        )}
      </div>
      <div>
        <Button
          variant="primary"
          disabled={!change}
          onClick={() => {
            if (change) {
              onAdd(change);
              reset();
            }
          }}
        >
          変更を足す
        </Button>
      </div>
    </div>
  );
}

/** 変更の一覧（説明と削除）。 */
export function ChangeList({
  changes,
  describe,
  onRemove,
}: {
  changes: PlanChange[];
  describe: (change: PlanChange) => string;
  onRemove?: (index: number) => void;
}) {
  if (changes.length === 0) return <p className="m-0 text-sm text-muted">変更はまだありません。</p>;
  return (
    <ol className="m-0 flex list-none flex-col gap-1 p-0">
      {changes.map((c, i) => (
        <li key={`${i}-${c.kind}-${c.target_id}`} className="flex items-center gap-2 rounded-sm border border-border px-3 py-1.5 text-sm">
          <span className="w-6 shrink-0 text-xs text-muted tabular">{i + 1}</span>
          <span className="shrink-0 rounded-sm bg-surface-2 px-1.5 text-xs">{CHANGE_KIND_LABEL[c.kind]}</span>
          <span className="min-w-0 flex-1 break-words">{describe(c)}</span>
          {onRemove && (
            <Button variant="ghost" onClick={() => onRemove(i)}>
              削除
            </Button>
          )}
        </li>
      ))}
    </ol>
  );
}

/** 組織・人・グループの名前の引き方をまとめる。 */
export function useNamers(asOf: string, changes: PlanChange[], groups: Group[], extraUnits: Array<{ id: string; name: string }> = []) {
  const units = useOrgUnits(asOf);
  const personIds = changes.flatMap((c) => [
    ...(c.kind === "assignment" || c.kind === "person" ? [c.target_id ?? ""] : []),
    c.values.head_person_id ?? "",
    c.values.person_id ?? "",
  ]).filter(Boolean);
  const people = usePeopleNames(personIds, asOf);
  return useMemo(() => {
    const orgNames = new Map(units.map(({ unit }) => [unit.id, unit.name]));
    for (const u of extraUnits) orgNames.set(u.id, `${u.name}（新設）`);
    for (const c of changes) if (c.kind === "org_create" && c.target_id) orgNames.set(c.target_id, `${c.values.name ?? ""}（新設）`);
    const groupNames = new Map(groups.map((g) => [g.id, g.name]));
    return {
      org: (id: string | null | undefined) => (id ? (orgNames.get(id) ?? "不明な組織") : "—"),
      person: (id: string | null | undefined) => (id ? (people.get(id) ?? "—") : "—"),
      group: (id: string | null | undefined) => (id ? (groupNames.get(id) ?? "グループ") : "—"),
    };
  }, [units, people, groups, changes, extraUnits]);
}
