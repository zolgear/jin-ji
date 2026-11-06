// 編集のボードの横の欄: 選んだ人・組織の操作（ドラッグの代わりにもなる）、人を探して組織図へ連れてくる、変更の一覧（1 件ずつ取り消せる）。
import { useEffect, useId, useMemo, useState } from "react";
import { Button } from "../../../auth/ui";
import { personName } from "../../../components/ui";
import { useAdapter } from "../../../data/adapterContext";
import { CHANGE_KIND_LABEL, type Group, type PlanChange } from "../../../data/field";
import { ChangeAdder, describeChange, inputClass, newUuid, useNamers } from "../shared";
import { useBoardDrag } from "./drag";
import { abolishUnit, createUnit, isWithin, movePerson, nextCode, undoMove, updateUnit, type BoardTree } from "./model";
import type { BoardSelection } from "./PlanBoard";
import { primaryOf, type BoardData } from "./useBoard";

interface SidebarProps {
  data: BoardData;
  changes: PlanChange[];
  onChange?: (next: PlanChange[]) => void;
  selection: BoardSelection | null;
  onSelect: (s: BoardSelection | null) => void;
  groups: Group[];
  allowGroups: boolean;
}

/** 組織を木の順に並べた選択肢（廃止する組織と、除く組織の配下は出さない）。 */
function unitOptions(tree: BoardTree, exclude?: string) {
  const out: Array<{ id: string; label: string }> = [];
  const walk = (parent: string | null, depth: number) => {
    for (const u of tree.children.get(parent) ?? []) {
      if (exclude && isWithin(tree, u.id, exclude)) continue;
      if (u.state !== "abolished") out.push({ id: u.id, label: `${"　".repeat(depth)}${u.name}${u.state === "created" ? "（新設）" : ""}` });
      walk(u.id, depth + 1);
    }
  };
  walk(null, 0);
  return out;
}

export function BoardSidebar(props: SidebarProps) {
  const { data, changes, onChange, groups, allowGroups } = props;
  const created = useMemo(() => data.tree.units.filter((u) => u.state === "created").map((u) => ({ id: u.id, name: u.name })), [data.tree]);
  const names = useNamers(data.baseDate, changes, groups, created);
  return (
    <div className="flex flex-col gap-4 p-3 text-sm">
      <Selected {...props} />
      {onChange && <FindPerson {...props} />}
      <section aria-label="変更" className="flex flex-col gap-1.5">
        <h3 className="m-0 text-xs font-bold text-muted">変更 {changes.length} 件</h3>
        {changes.length === 0 ? (
          <p className="m-0 text-muted">まだありません</p>
        ) : (
          <ol className="m-0 flex list-none flex-col gap-1 p-0">
            {changes.map((c, i) => (
              <li key={`${i}-${c.kind}-${c.target_id}`} className="flex items-start gap-1.5 rounded-sm border border-border bg-surface px-2 py-1 text-xs">
                <span className="shrink-0 rounded-sm bg-surface-2 px-1">{CHANGE_KIND_LABEL[c.kind]}</span>
                <span className="min-w-0 flex-1 break-words">{describeChange(c, names)}</span>
                {onChange && (
                  <button type="button" aria-label="この変更を取り消す" onClick={() => onChange(changes.filter((_, j) => j !== i))} className="shrink-0 rounded-sm px-1 text-muted hover:bg-surface-2 hover:text-fg">
                    ×
                  </button>
                )}
              </li>
            ))}
          </ol>
        )}
      </section>
      {onChange && (
        <details className="rounded-md border border-border">
          <summary className="cursor-pointer px-3 py-2 text-xs font-bold text-muted">{allowGroups ? "連絡先・グループの変更" : "連絡先の変更"}</summary>
          <div className="p-2">
            <ChangeAdder asOf={data.baseDate} created={created} groups={groups} allowGroups={allowGroups} only={allowGroups ? ["contact", "group_join"] : ["contact"]} onAdd={(c) => onChange([...changes, c])} />
          </div>
        </details>
      )}
    </div>
  );
}

function Selected({ data, changes, onChange, selection, onSelect }: SidebarProps) {
  if (!selection) return null;
  if (selection.kind === "person") return <SelectedPerson key={selection.personId} selection={selection} data={data} changes={changes} onChange={onChange} onSelect={onSelect} />;
  const unit = data.tree.byId.get(selection.unitId);
  if (!unit) return null;
  return <SelectedUnit key={unit.id} unitId={unit.id} data={data} changes={changes} onChange={onChange} onSelect={onSelect} />;
}

function SelectedPerson({
  selection,
  data,
  changes,
  onChange,
  onSelect,
}: {
  selection: Extract<BoardSelection, { kind: "person" }>;
  data: BoardData;
  changes: PlanChange[];
  onChange?: (next: PlanChange[]) => void;
  onSelect: (s: BoardSelection | null) => void;
}) {
  const drag = useBoardDrag();
  const move = data.moves.get(selection.personId);
  const to = move?.to ?? selection.from.org;
  const title = move?.title !== undefined ? move.title : selection.from.title;
  const [titleText, setTitleText] = useState(title ?? "");
  const id = useId();
  const nameOf = (u: string | null) => (u ? (data.tree.byId.get(u)?.name ?? "—") : "—");
  const destination = to ? data.tree.byId.get(to) : undefined;
  const options = unitOptions(data.tree);
  const apply = (next: PlanChange[]) => onChange?.(next);
  return (
    <section aria-label="選んだ人" className="flex flex-col gap-2 rounded-md border border-accent/50 bg-surface p-3">
      <div className="flex items-center gap-2">
        <button
          type="button"
          onPointerDown={onChange ? (e) => drag.begin(e, { kind: "person", personId: selection.personId, name: selection.name, from: selection.from }) : undefined}
          className={`min-w-0 truncate rounded-sm border border-border bg-surface-2 px-2 py-0.5 font-bold ${onChange ? "cursor-grab touch-none" : ""}`}
          title={onChange ? "組織図の組織へドラッグして異動" : undefined}
        >
          {selection.name}
        </button>
        <button type="button" aria-label="選ぶのをやめる" onClick={() => onSelect(null)} className="ml-auto rounded-sm px-1 text-muted hover:bg-surface-2">
          ×
        </button>
      </div>
      <dl className="m-0 grid grid-cols-[3em_1fr] gap-x-2 gap-y-0.5 text-xs">
        <dt className="text-muted">今</dt>
        <dd className="m-0">
          {nameOf(selection.from.org)}
          {selection.from.title ? `・${selection.from.title}` : ""}
        </dd>
        {move && (
          <>
            <dt className="text-muted">変更後</dt>
            <dd className="m-0 font-bold">
              {nameOf(to)}
              {title ? `・${title}` : ""}
            </dd>
          </>
        )}
      </dl>
      {onChange && (
        <>
          <label htmlFor={id} className="flex flex-col gap-1 text-xs">
            <span className="text-muted">移す先</span>
            <select
              id={id}
              className={inputClass}
              value={to ?? ""}
              onChange={(e) => e.target.value && apply(movePerson(changes, selection.personId, e.target.value, selection.from))}
            >
              {!to && <option value="">選んでください</option>}
              {options.map((o) => (
                <option key={o.id} value={o.id}>
                  {o.label}
                </option>
              ))}
            </select>
          </label>
          <label className="flex flex-col gap-1 text-xs">
            <span className="text-muted">役職</span>
            <input
              className={inputClass}
              value={titleText}
              maxLength={100}
              onChange={(e) => setTitleText(e.target.value)}
              onBlur={() => to && (titleText.trim() || null) !== (title ?? null) && apply(movePerson(changes, selection.personId, to, selection.from, titleText.trim() || null))}
              onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
            />
          </label>
          <div className="flex flex-wrap gap-1">
            {destination && destination.headPersonId !== selection.personId && destination.state !== "abolished" && (
              <Button onClick={() => apply(updateUnit(changes, data.tree, destination.id, "head_person_id", selection.personId))}>{destination.name}の組織長にする</Button>
            )}
            {move && (
              <Button variant="ghost" onClick={() => (apply(undoMove(changes, selection.personId)), setTitleText(selection.from.title ?? ""))}>
                元に戻す
              </Button>
            )}
          </div>
        </>
      )}
    </section>
  );
}

function SelectedUnit({
  unitId,
  data,
  changes,
  onChange,
  onSelect,
}: {
  unitId: string;
  data: BoardData;
  changes: PlanChange[];
  onChange?: (next: PlanChange[]) => void;
  onSelect: (s: BoardSelection | null) => void;
}) {
  const unit = data.tree.byId.get(unitId)!;
  const [name, setName] = useState(unit.name);
  const [childName, setChildName] = useState("");
  const parentId = useId();
  const options = unitOptions(data.tree, unit.id);
  const apply = (next: PlanChange[]) => onChange?.(next);
  const editable = Boolean(onChange) && unit.state !== "abolished";
  const addChild = () => {
    const id = newUuid();
    apply(createUnit(changes, id, unit.id, childName.trim(), nextCode(data.tree, unit.id)));
    setChildName("");
    onSelect({ kind: "unit", unitId: id });
  };
  return (
    <section aria-label="選んだ組織" className="flex flex-col gap-2 rounded-md border border-accent/50 bg-surface p-3">
      <div className="flex items-center gap-2">
        <span className="min-w-0 truncate font-bold">{unit.name}</span>
        <button type="button" aria-label="選ぶのをやめる" onClick={() => onSelect(null)} className="ml-auto rounded-sm px-1 text-muted hover:bg-surface-2">
          ×
        </button>
      </div>
      {editable && (
        <>
          <label className="flex flex-col gap-1 text-xs">
            <span className="text-muted">名称</span>
            <input
              className={inputClass}
              value={name}
              maxLength={200}
              onChange={(e) => setName(e.target.value)}
              onBlur={() => name.trim() && name.trim() !== unit.name && apply(updateUnit(changes, data.tree, unit.id, "name", name.trim()))}
              onKeyDown={(e) => e.key === "Enter" && (e.target as HTMLInputElement).blur()}
            />
          </label>
          {unit.parentId && (
            <label htmlFor={parentId} className="flex flex-col gap-1 text-xs">
              <span className="text-muted">上位組織</span>
              <select id={parentId} className={inputClass} value={unit.parentId} onChange={(e) => apply(updateUnit(changes, data.tree, unit.id, "parent_org_unit_id", e.target.value))}>
                {options.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.label}
                  </option>
                ))}
              </select>
            </label>
          )}
          <div className="flex items-end gap-1">
            <label className="flex min-w-0 flex-1 flex-col gap-1 text-xs">
              <span className="text-muted">配下に新設</span>
              <input className={inputClass} value={childName} maxLength={200} placeholder="組織の名称" onChange={(e) => setChildName(e.target.value)} onKeyDown={(e) => e.key === "Enter" && childName.trim() && addChild()} />
            </label>
            <Button disabled={!childName.trim()} onClick={addChild}>
              新設
            </Button>
          </div>
        </>
      )}
      {onChange && (
        <div className="flex flex-wrap gap-1">
          {unit.state === "abolished" ? (
            <Button onClick={() => apply(abolishUnit(changes, data.tree, unit.id, true))}>廃止を取り消す</Button>
          ) : unit.state === "created" ? (
            <Button variant="danger" onClick={() => (apply(abolishUnit(changes, data.tree, unit.id)), onSelect(null))}>
              新設を取り消す
            </Button>
          ) : (
            unit.parentId && (
              <Button variant="danger" onClick={() => apply(abolishUnit(changes, data.tree, unit.id))}>
                廃止する
              </Button>
            )
          )}
        </div>
      )}
    </section>
  );
}

/** 人を探す。選ぶと「選んだ人」に出て、そこから組織図へドラッグするか、移す先を選べる。 */
function FindPerson({ data, onSelect }: SidebarProps) {
  const { adapter } = useAdapter();
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<Array<{ id: string; name: string; org: string | null; orgName: string; title: string | null }>>([]);
  const id = useId();
  useEffect(() => {
    if (!query.trim()) return;
    let cancelled = false;
    const timer = setTimeout(() => {
      adapter.searchPeople(query.trim(), { asOf: data.baseDate, limit: 8 }).then(
        (page) => {
          if (cancelled) return;
          setResults(
            page.items.map((p) => {
              const a = primaryOf(p);
              return { id: p.id, name: personName(p), org: a?.org_unit_id ?? null, orgName: a?.org_unit_name ?? "", title: a?.position_title ?? null };
            }),
          );
        },
        () => !cancelled && setResults([]),
      );
    }, 200);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [adapter, data.baseDate, query]);
  return (
    <section aria-label="人を探す" className="flex flex-col gap-1">
      <label htmlFor={id} className="text-xs font-bold text-muted">
        人を探す
      </label>
      <input id={id} type="search" className={inputClass} value={query} onChange={(e) => setQuery(e.target.value)} placeholder="氏名" autoComplete="off" />
      {query.trim() && results.length > 0 && (
        <ul className="m-0 max-h-56 list-none overflow-y-auto rounded-md border border-border bg-surface p-0">
          {results.map((r) => (
            <li key={r.id}>
              <button
                type="button"
                className="flex w-full items-baseline gap-2 px-3 py-1.5 text-left hover:bg-surface-2"
                onClick={() => {
                  onSelect({ kind: "person", personId: r.id, name: r.name, from: { org: r.org, title: r.title } });
                  setQuery("");
                  setResults([]);
                }}
              >
                <span>{r.name}</span>
                <span className="truncate text-xs text-muted">{r.orgName}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
