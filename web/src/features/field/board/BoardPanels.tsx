// 編集のボードの上に浮かべる箱: 選んだ人・組織の操作（ドラッグの代わりにもなる）、変更の一覧（1 件ずつ取り消せる）、
// 組織・人を探して図をそこへ動かす検索。箱は入力欄の外を少し透かし、後ろの組織図が見えるようにする。
import { useCallback, useEffect, useId, useMemo, useRef, useState, type ReactNode } from "react";
import { Button } from "../../../auth/ui";
import { Badge, personKana, personName } from "../../../components/ui";
import { usePerson } from "../../../app/hooks";
import { useAdapter } from "../../../data/adapterContext";
import { employmentTypeLabel } from "../../../lib/labels";
import { assignmentKind, PersonDetail } from "../../person/PersonPanel";
import { CHANGE_KIND_LABEL, type Group, type PlanChange } from "../../../data/field";
import { ChangeAdder, describeChange, inputClass, newUuid, useNamers } from "../shared";
import { useBoardDrag } from "./drag";
import { abolishUnit, createUnit, isWithin, movePerson, nextCode, undoMove, updateUnit, type BoardTree } from "./model";
import { useMoveInUnit, type BoardSelection } from "./PlanBoard";
import { primaryOf, type BoardData } from "./useBoard";

interface PanelProps {
  data: BoardData;
  changes: PlanChange[];
  onChange?: (next: PlanChange[]) => void;
  selection: BoardSelection | null;
  onSelect: (s: BoardSelection | null) => void;
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

/** 図の上に浮かべる箱。PC は右上、スマホは下に出す。 */
export function FloatingCard({ label, title, onClose, children, wide = false }: { label: string; title: ReactNode; onClose: () => void; children: ReactNode; wide?: boolean }) {
  return (
    <section
      aria-label={label}
      className={`absolute inset-x-3 bottom-3 z-10 flex max-h-[55%] flex-col overflow-hidden rounded-lg border border-border bg-surface/75 text-sm shadow-xl backdrop-blur-sm sm:inset-x-auto sm:top-16 sm:right-3 sm:bottom-auto sm:max-h-[calc(100%-5rem)] ${wide ? "sm:w-96" : "sm:w-80"}`}
    >
      <div className="flex items-center gap-2 border-b border-border/60 px-3 py-2">
        <div className="min-w-0 flex-1 font-bold">{title}</div>
        <button type="button" aria-label="閉じる" onClick={onClose} className="shrink-0 rounded-sm px-1.5 text-muted hover:bg-surface-2 hover:text-fg">
          ×
        </button>
      </div>
      <div className="flex min-h-0 flex-col gap-3 overflow-y-auto p-3">{children}</div>
    </section>
  );
}

/** 変更の一覧と、連絡先・グループの変更の追加。 */
export function ChangesList({ data, changes, onChange, groups }: Omit<PanelProps, "selection" | "onSelect"> & { groups: Group[] }) {
  const created = useMemo(() => data.tree.units.filter((u) => u.state === "created").map((u) => ({ id: u.id, name: u.name })), [data.tree]);
  const names = useNamers(data.baseDate, changes, groups, created);
  return (
    <>
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
      {onChange && (
        <details className="rounded-md border border-border bg-surface">
          <summary className="cursor-pointer px-3 py-2 text-xs font-bold text-muted">連絡先・グループの変更</summary>
          <div className="p-2">
            <ChangeAdder asOf={data.baseDate} created={created} groups={groups} allowGroups only={["contact", "group_join"]} onAdd={(c) => onChange([...changes, c])} />
          </div>
        </details>
      )}
    </>
  );
}

/** 選んだ人・組織の箱。 */
export function SelectionCard(props: PanelProps) {
  const { data, selection, onSelect } = props;
  if (!selection) return null;
  if (selection.kind === "person")
    return (
      <SelectedPerson key={selection.personId} selection={selection} data={data} changes={props.changes} onChange={props.onChange} onSelect={onSelect} />
    );
  const unit = data.tree.byId.get(selection.unitId);
  if (!unit) return null;
  return <SelectedUnit key={unit.id} unitId={unit.id} data={data} changes={props.changes} onChange={props.onChange} onSelect={onSelect} />;
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
  // 組織の中の並び（ドラッグの代わり）
  const shift = useMoveInUnit(changes, to, selection.personId);
  return (
    <FloatingCard
      label="選んだ人"
      onClose={() => onSelect(null)}
      title={
        <button
          type="button"
          onPointerDown={onChange ? (e) => drag.begin(e, { kind: "person", personId: selection.personId, name: selection.name, from: selection.from }) : undefined}
          className={`min-w-0 truncate rounded-sm border border-border bg-surface-2 px-2 py-0.5 font-bold ${onChange ? "cursor-grab touch-none" : ""}`}
          title={onChange ? "組織図の組織へドラッグして異動" : undefined}
        >
          {selection.name}
        </button>
      }
    >
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
            {shift && (
              <span className="flex">
                <Button variant="ghost" aria-label="組織の中で上へ" title="組織の中で上へ" onClick={() => apply(shift(-1))}>
                  ↑
                </Button>
                <Button variant="ghost" aria-label="組織の中で下へ" title="組織の中で下へ" onClick={() => apply(shift(1))}>
                  ↓
                </Button>
              </span>
            )}
            {move && (
              <Button variant="ghost" onClick={() => (apply(undoMove(changes, selection.personId)), setTitleText(selection.from.title ?? ""))}>
                元に戻す
              </Button>
            )}
          </div>
        </>
      )}
      <PersonDetails personId={selection.personId} asOf={data.baseDate} />
    </FloatingCard>
  );
}

/**
 * 選んだ人の要点（フリガナ・所属〔兼務・出向を含む〕・社員番号・雇用区分・拠点）。すべての項目は「詳細」で箱の上に開く（画面を移らない）。
 * 見えるのは閲覧者の公開範囲の項目だけ。
 */
function PersonDetails({ personId, asOf }: { personId: string; asOf: string }) {
  const person = usePerson(personId, asOf);
  const dialog = useRef<HTMLDialogElement | null>(null);
  // 箱の外（背景）を押したら閉じる（背景を押すと、対象は <dialog> そのものになる）
  const attachDialog = useCallback((d: HTMLDialogElement | null) => {
    dialog.current = d;
    if (!d) return;
    const onClick = (e: MouseEvent) => e.target === d && d.close();
    d.addEventListener("click", onClick);
    return () => d.removeEventListener("click", onClick);
  }, []);
  const p = person.data;
  if (!p) return null;
  const kana = personKana(p);
  const assignments = [...(p.assignments ?? [])].sort((a, b) => (a.type === "primary" ? -1 : 0) - (b.type === "primary" ? -1 : 0));
  return (
    <section aria-label="人の詳細" className="flex flex-col gap-2 border-t border-border/60 pt-3">
      <div className="flex items-center gap-2">
        {kana && <span className="min-w-0 flex-1 truncate text-xs text-muted">{kana}</span>}
        <button type="button" onClick={() => dialog.current?.showModal()} className="ml-auto shrink-0 rounded-sm border border-border bg-surface px-2 py-0.5 text-xs hover:bg-surface-2">
          詳細
        </button>
      </div>
      {assignments.length > 0 && (
        <ul className="m-0 flex list-none flex-col gap-1 p-0">
          {assignments.map((a) => {
            const kind = assignmentKind(a);
            return (
              <li key={`${a.org_unit_id}:${a.type}:${a.valid_from}`} className="flex flex-wrap items-center gap-1.5 text-xs">
                <Badge tone={kind.tone}>{kind.label}</Badge>
                <span className="font-bold">{a.org_unit_name}</span>
                {a.position_title && <span className="text-muted">{a.position_title}</span>}
              </li>
            );
          })}
        </ul>
      )}
      {/* 社員番号・雇用区分・拠点（見えない・無い項目は出さない） */}
      <dl className="m-0 grid grid-cols-[4.5em_1fr] gap-x-2 gap-y-0.5 text-xs">
        {p.employee_number && (
          <>
            <dt className="text-muted">社員番号</dt>
            <dd className="m-0 tabular">{p.employee_number}</dd>
          </>
        )}
        {p.employment_type && (
          <>
            <dt className="text-muted">雇用区分</dt>
            <dd className="m-0">{employmentTypeLabel(p.employment_type)}</dd>
          </>
        )}
        {p.site && (
          <>
            <dt className="text-muted">拠点</dt>
            <dd className="m-0">{p.site.name}</dd>
          </>
        )}
      </dl>
      <dialog
        ref={attachDialog}
        aria-label={`${personName(p)}の詳細`}
        className="m-auto max-h-[85dvh] w-[min(32rem,calc(100vw-16px))] overflow-y-auto rounded-lg border border-border bg-surface p-4 text-sm text-fg shadow-2xl backdrop:bg-black/40"
      >
        <div className="mb-2 flex justify-end">
          <button type="button" onClick={() => dialog.current?.close()} className="rounded-sm border border-border px-2 py-0.5 text-xs hover:bg-surface-2">
            閉じる
          </button>
        </div>
        <PersonDetail person={p} report={false} />
      </dialog>
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
    <FloatingCard label="選んだ組織" onClose={() => onSelect(null)} title={<span className="block truncate">{unit.name}</span>}>
      <dl className="m-0 grid grid-cols-[4em_1fr] gap-x-2 gap-y-0.5 text-xs">
        <dt className="text-muted">組織長</dt>
        <dd className={`m-0 ${unit.changed.includes("head_person_id") ? "font-bold text-warn-fg" : ""}`}>{unit.headPersonId ? (data.names.get(unit.headPersonId) ?? "…") : "なし"}</dd>
        {unit.levelName && (
          <>
            <dt className="text-muted">段</dt>
            <dd className="m-0">{unit.levelName}</dd>
          </>
        )}
      </dl>
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
    </FloatingCard>
  );
}

type SearchHit =
  | { kind: "unit"; id: string; name: string; sub: string }
  | { kind: "person"; id: string; name: string; sub: string; org: string | null; title: string | null };

/** 組織（名称・コード）と人（氏名）をまとめて探す。選ぶと、その札まで図を動かして選ぶ。 */
export function BoardSearch({ data, onPick }: { data: BoardData; onPick: (s: BoardSelection) => void }) {
  const { adapter } = useAdapter();
  const [query, setQuery] = useState("");
  const [people, setPeople] = useState<SearchHit[]>([]);
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  const listId = useId();
  const q = query.trim();
  const units = useMemo<SearchHit[]>(() => {
    if (!q) return [];
    const needle = q.toLowerCase();
    return data.tree.units
      .filter((u) => u.name.toLowerCase().includes(needle) || u.code.toLowerCase().includes(needle))
      .slice(0, 6)
      .map((u) => ({ kind: "unit", id: u.id, name: u.name, sub: [u.levelName, u.parentId ? data.tree.byId.get(u.parentId)?.name : null].filter(Boolean).join("・") }));
  }, [q, data.tree]);
  useEffect(() => {
    if (!q) return;
    let cancelled = false;
    const timer = setTimeout(() => {
      adapter.searchPeople(q, { asOf: data.baseDate, limit: 6 }).then(
        (page) => {
          if (cancelled) return;
          setPeople(
            page.items.map((p) => {
              const a = primaryOf(p);
              return { kind: "person", id: p.id, name: personName(p), sub: a?.org_unit_name ?? "", org: a?.org_unit_id ?? null, title: a?.position_title ?? null };
            }),
          );
        },
        () => !cancelled && setPeople([]),
      );
    }, 200);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [adapter, data.baseDate, q]);
  // 外を押したら候補を閉じる
  useEffect(() => {
    if (!open) return;
    const down = (e: PointerEvent) => !box.current?.contains(e.target as Node) && setOpen(false);
    window.addEventListener("pointerdown", down);
    return () => window.removeEventListener("pointerdown", down);
  }, [open]);
  const hits = q ? [...units, ...people] : [];
  const pick = (h: SearchHit) => {
    onPick(h.kind === "unit" ? { kind: "unit", unitId: h.id } : { kind: "person", personId: h.id, name: h.name, from: { org: h.org, title: h.title } });
    setQuery("");
    setPeople([]);
    setOpen(false);
  };
  return (
    <div ref={box} className="relative">
      <input
        type="search"
        aria-label="組織・人を探す"
        aria-controls={listId}
        className={`${inputClass} h-10 w-48 sm:w-64`}
        value={query}
        placeholder="組織・人を探す"
        autoComplete="off"
        onChange={(e) => (setQuery(e.target.value), setOpen(true))}
        onFocus={() => setOpen(true)}
        onKeyDown={(e) => {
          if (e.key === "Enter" && hits[0]) pick(hits[0]);
          if (e.key === "Escape") setOpen(false);
        }}
      />
      {open && hits.length > 0 && (
        <ul id={listId} className="absolute top-full left-0 z-20 m-0 mt-1 max-h-80 w-72 list-none overflow-y-auto rounded-md border border-border bg-surface p-0 shadow-xl">
          {hits.map((h) => (
            <li key={`${h.kind}:${h.id}`}>
              <button type="button" className="flex w-full items-baseline gap-2 px-3 py-1.5 text-left hover:bg-surface-2" onClick={() => pick(h)}>
                <span className="shrink-0 rounded-sm bg-surface-2 px-1 text-xs text-muted">{h.kind === "unit" ? "組織" : "人"}</span>
                <span className="shrink-0">{h.name}</span>
                <span className="truncate text-xs text-muted">{h.sub}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
