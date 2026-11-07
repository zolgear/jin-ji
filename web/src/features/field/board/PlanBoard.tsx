// 編集のボード: 組織図の札に所属する人を並べ、人を別の札へドラッグして異動、札のつまみをドラッグして上位組織の付け替えをする。
// 新設・変更・廃止した組織と、動かした人は印を付ける（動かした人は元の札にも薄く残す）。
// ドラッグできない環境（キーボード・読み上げ）では、人や札を選んで横の欄から同じ操作ができる（BoardSidebar）。
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { useAdapter } from "../../../data/adapterContext";
import type { PlanChange } from "../../../data/field";
import { useQuery } from "../../../data/query";
import type { Person } from "../../../data/types";
import { personName } from "../../../components/ui";
import { DragProvider, useBoardDrag, type DragPayload } from "./drag";
import { isWithin, movePerson, updateUnit, type BoardUnit } from "./model";
import { primaryOf, type BoardData } from "./useBoard";

export type BoardSelection = { kind: "person"; personId: string; name: string; from: { org: string | null; title: string | null } } | { kind: "unit"; unitId: string };

const MEMBER_LIMIT = 8;
const ZOOM_MIN = 0.3;
const ZOOM_MAX = 1.5;
const ZOOM_STEP = 1.2;

const STATE_LABEL: Record<BoardUnit["state"], string> = { unchanged: "", created: "新設", changed: "変更", abolished: "廃止" };

interface BoardProps {
  data: BoardData;
  changes: PlanChange[];
  /** 無ければ見るだけ（今の組織を見ているときなど）。 */
  onChange?: (next: PlanChange[]) => void;
  selection: BoardSelection | null;
  onSelect: (s: BoardSelection | null) => void;
  /** 横の欄（ドラッグの元にもなるので、同じドラッグの範囲に置く）。 */
  aside?: ReactNode;
  /** 図の上に重ねる操作（変更後・今の組織の切り替えなど）。 */
  overlay?: ReactNode;
  /** この組織から下だけを描く（無い・見つからなければ全体）。 */
  root?: string | null;
}

export function PlanBoard(props: BoardProps) {
  const { data, changes, onChange } = props;
  const scroller = useRef<HTMLDivElement>(null);
  const canDrop = useCallback(
    (p: DragPayload, unitId: string) => {
      if (!onChange) return false;
      const target = data.tree.byId.get(unitId);
      if (!target || target.state === "abolished") return false;
      if (p.kind === "person") return (data.moves.get(p.personId)?.to ?? p.from.org) !== unitId;
      return target.id !== data.tree.byId.get(p.unitId)?.parentId && !isWithin(data.tree, unitId, p.unitId);
    },
    [data, onChange],
  );
  const onDrop = useCallback(
    (p: DragPayload, unitId: string) => {
      if (!onChange) return;
      if (p.kind === "person") onChange(movePerson(changes, p.personId, unitId, p.from));
      else onChange(updateUnit(changes, data.tree, p.unitId, "parent_org_unit_id", unitId));
    },
    [changes, data.tree, onChange],
  );
  return (
    <DragProvider scroller={scroller} canDrop={canDrop} onDrop={onDrop}>
      <div className="flex size-full flex-col lg:flex-row">
        <div className="relative h-[65dvh] min-w-0 shrink-0 lg:h-auto lg:flex-1">
          <Canvas {...props} scroller={scroller} />
          {props.overlay}
        </div>
        {props.aside && <aside className="min-h-0 shrink-0 border-t border-border bg-surface lg:w-80 lg:overflow-y-auto lg:border-t-0 lg:border-l">{props.aside}</aside>}
      </div>
    </DragProvider>
  );
}

function Canvas({ data, changes, onChange, selection, onSelect, scroller, root }: BoardProps & { scroller: React.RefObject<HTMLDivElement | null> }) {
  const [zoom, setZoomState] = useState(0.85);
  // 拡大・縮小の中心（図の上の点を、画面の同じ位置に保つ）
  const anchor = useRef<{ x: number; y: number; cx: number; cy: number } | null>(null);
  const setZoom = useCallback(
    (next: (z: number) => number, at?: { x: number; y: number }) => {
      const el = scroller.current;
      setZoomState((z) => {
        const n = Math.min(ZOOM_MAX, Math.max(ZOOM_MIN, next(z)));
        if (el && n !== z) {
          const x = at?.x ?? el.clientWidth / 2;
          const y = at?.y ?? el.clientHeight / 2;
          anchor.current = { x, y, cx: (el.scrollLeft + x) / z, cy: (el.scrollTop + y) / z };
        }
        return n;
      });
    },
    [scroller],
  );
  useLayoutEffect(() => {
    const el = scroller.current;
    const a = anchor.current;
    if (!el || !a) return;
    anchor.current = null;
    el.scrollTo(a.cx * zoom - a.x, a.cy * zoom - a.y);
  }, [zoom, scroller]);
  // ホイールで拡大・縮小（組織図の画面と同じ）。React の onWheel は passive なので自分で付ける
  useEffect(() => {
    const el = scroller.current;
    if (!el) return;
    const wheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = el.getBoundingClientRect();
      const factor = Math.exp(-Math.max(-100, Math.min(100, e.deltaMode === 1 ? e.deltaY * 33 : e.deltaY)) / 500);
      setZoom((z) => z * factor, { x: e.clientX - r.left, y: e.clientY - r.top });
    };
    el.addEventListener("wheel", wheel, { passive: false });
    return () => el.removeEventListener("wheel", wheel);
  }, [scroller, setZoom]);
  const [open, setOpen] = useState<Map<string, boolean>>(new Map());
  // 変更のある組織とその祖先は開く（自分で閉じたものは閉じたまま）
  const touched = useMemo(() => {
    const set = new Set<string>();
    const mark = (id: string | null | undefined) => {
      for (let cur = id ?? null, guard = 0; cur && guard < 1000; guard++) {
        set.add(cur);
        cur = data.tree.byId.get(cur)?.parentId ?? null;
      }
    };
    for (const c of changes) {
      mark(data.tree.byId.get(c.target_id ?? "")?.parentId);
      if (c.kind === "assignment") mark(data.tree.byId.get(c.values.org_unit_id ?? "")?.parentId);
    }
    for (const m of data.moved.values()) mark(data.tree.byId.get(m.org ?? "")?.parentId);
    return set;
  }, [changes, data]);
  const depthOf = useCallback(
    (id: string) => {
      let d = 0;
      for (let cur = data.tree.byId.get(id)?.parentId ?? null; cur && d < 1000; d++) cur = data.tree.byId.get(cur)?.parentId ?? null;
      return d;
    },
    [data.tree],
  );
  const top = useMemo(() => (root ? (data.tree.byId.get(root) ?? null) : null), [root, data.tree]);
  const topDepth = useMemo(() => (top ? depthOf(top.id) : 0), [top, depthOf]);
  const isOpen = useCallback((id: string) => open.get(id) ?? (depthOf(id) - topDepth < 1 || touched.has(id)), [open, depthOf, topDepth, touched]);
  const toggle = useCallback((id: string) => setOpen((m) => new Map(m).set(id, !isOpen(id))), [isOpen]);

  // 札の外を押してドラッグすると、図を動かす
  const pan = (e: ReactPointerEvent<HTMLDivElement>) => {
    if (e.button !== 0 || (e.target as HTMLElement).closest("[data-drop-unit],button,input,select,a")) return;
    const el = scroller.current;
    if (!el) return;
    const start = { x: e.clientX, y: e.clientY, left: el.scrollLeft, top: el.scrollTop };
    const move = (ev: PointerEvent) => el.scrollTo(start.left - (ev.clientX - start.x), start.top - (ev.clientY - start.y));
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  };

  const roots = top ? [top] : (data.tree.children.get(null) ?? []);
  return (
    <div className="relative size-full">
      <div ref={scroller} onPointerDown={pan} className="size-full cursor-grab overflow-auto bg-bg [background-image:radial-gradient(var(--border)_1px,transparent_1px)] [background-size:20px_20px]">
        <div className="plan-tree inline-flex min-w-full justify-center px-8 pt-16 pb-16" style={{ zoom }}>
          <ul className="row root">
            {roots.map((u) => (
              <Branch key={u.id} unit={u} data={data} changes={changes} onChange={onChange} selection={selection} onSelect={onSelect} isOpen={isOpen} toggle={toggle} />
            ))}
          </ul>
        </div>
      </div>
      <div className="absolute bottom-3 left-3 flex overflow-hidden rounded-md border border-border bg-surface shadow-sm">
        <button type="button" aria-label="縮小" disabled={zoom <= ZOOM_MIN} onClick={() => setZoom((z) => z / ZOOM_STEP)} className="h-8 w-8 hover:bg-surface-2 disabled:opacity-40">
          −
        </button>
        <span className="flex h-8 w-12 items-center justify-center border-x border-border text-xs tabular">{Math.round(zoom * 100)}%</span>
        <button type="button" aria-label="拡大" disabled={zoom >= ZOOM_MAX} onClick={() => setZoom((z) => z * ZOOM_STEP)} className="h-8 w-8 hover:bg-surface-2 disabled:opacity-40">
          ＋
        </button>
      </div>
    </div>
  );
}

interface BranchProps {
  unit: BoardUnit;
  data: BoardData;
  changes: PlanChange[];
  onChange?: (next: PlanChange[]) => void;
  selection: BoardSelection | null;
  onSelect: (s: BoardSelection | null) => void;
  isOpen: (id: string) => boolean;
  toggle: (id: string) => void;
}

function Branch(props: BranchProps) {
  const { unit, data, isOpen } = props;
  const kids = data.tree.children.get(unit.id) ?? [];
  const opened = kids.length > 0 && isOpen(unit.id);
  // 配下がすべて末端（開いた配下が無い）なら縦に積む
  const stacked = opened && kids.every((k) => !(data.tree.children.get(k.id)?.length && isOpen(k.id)));
  return (
    <li className={stacked ? "has-stack" : undefined}>
      <UnitCard {...props} kids={kids.length} opened={opened} />
      {opened && (
        <ul className={stacked ? "stack" : "row"}>
          {kids.map((k) => (
            <Branch key={k.id} {...props} unit={k} />
          ))}
        </ul>
      )}
    </li>
  );
}

interface Chip {
  personId: string;
  name: string;
  title: string | null;
  from: { org: string | null; title: string | null };
  /** in: 変更で入ってくる人。out: 変更で出ていく人（元の札に薄く残す）。 */
  mark: "none" | "in" | "out" | "title";
  /** in なら元の組織、out なら行き先。 */
  other: string | null;
}

function UnitCard({ unit, data, onChange, selection, onSelect, toggle, kids, opened }: BranchProps & { kids: number; opened: boolean }) {
  const { adapter } = useAdapter();
  const drag = useBoardDrag();
  const [all, setAll] = useState(false);
  const members = useQuery(adapter, unit.state === "created" ? null : `board-members:${unit.id}:${data.baseDate}`, () =>
    adapter.listOrgMembers(unit.id, { asOf: data.baseDate, limit: 100 }),
  );
  const chips = useMemo(() => {
    const list: Chip[] = [];
    for (const p of (members.data?.items ?? []) as Person[]) {
      const a = primaryOf(p, unit.id);
      if (!a) continue;
      const from = { org: unit.id, title: a.position_title };
      const move = data.moves.get(p.id);
      if (move && move.to !== unit.id) list.push({ personId: p.id, name: personName(p), title: a.position_title, from, mark: "out", other: move.to });
      else if (move && move.title !== undefined) list.push({ personId: p.id, name: personName(p), title: move.title, from, mark: "title", other: null });
      else list.push({ personId: p.id, name: personName(p), title: a.position_title, from, mark: "none", other: null });
    }
    for (const m of data.moves.values()) {
      if (m.to !== unit.id) continue;
      const info = data.moved.get(m.personId);
      if (info?.org === unit.id) continue;
      list.push({
        personId: m.personId,
        name: info?.name ?? "…",
        title: m.title !== undefined ? m.title : (info?.title ?? null),
        from: { org: info?.org ?? null, title: info?.title ?? null },
        mark: "in",
        other: info?.org ?? null,
      });
    }
    const rank = (c: Chip) => (c.mark === "in" ? 0 : c.personId === unit.headPersonId ? 1 : c.mark === "out" ? 4 : c.title ? 2 : 3);
    return list.sort((a, b) => rank(a) - rank(b) || a.name.localeCompare(b.name, "ja"));
  }, [members.data, data.moves, data.moved, unit.id, unit.headPersonId]);

  const present = chips.filter((c) => c.mark !== "out").length;
  const truncated = Boolean(members.data?.nextCursor);
  const baseCount = chips.filter((c) => c.mark !== "in").length;
  const delta = present - baseCount;
  const shown = all ? chips : chips.slice(0, MEMBER_LIMIT);
  const selected = selection?.kind === "unit" && selection.unitId === unit.id;
  const over = drag.over === unit.id;
  const editable = Boolean(onChange) && unit.state !== "abolished";
  const tone =
    unit.state === "created"
      ? "border-accent bg-accent-weak"
      : unit.state === "changed"
        ? "border-warn-fg bg-surface"
        : unit.state === "abolished"
          ? "border-danger border-dashed bg-surface opacity-60"
          : "border-border bg-surface";
  const nameOf = (id: string | null) => (id ? (data.tree.byId.get(id)?.name ?? "—") : "—");
  const parentChanged = unit.changed.includes("parent_org_unit_id");

  return (
    <div
      data-drop-unit={unit.id}
      className={`plan-card relative flex w-60 flex-col rounded-md border text-xs shadow-sm transition-shadow ${tone} ${over ? "ring-2 ring-accent ring-offset-2 ring-offset-bg" : ""} ${selected ? "outline-2 outline-offset-2 outline-accent" : ""}`}
    >
      <div className="flex min-w-0 items-center gap-1 border-b border-border/70 px-2 py-1.5">
        {editable && (
          <button
            type="button"
            aria-label={`${unit.name}を動かす（上位組織を付け替える）`}
            title="ドラッグして上位組織を付け替え"
            onPointerDown={(e) => drag.begin(e, { kind: "unit", unitId: unit.id, name: unit.name })}
            onClick={() => onSelect({ kind: "unit", unitId: unit.id })}
            className="-ml-1 flex h-6 w-4 shrink-0 cursor-grab touch-none items-center justify-center rounded-sm text-muted hover:bg-surface-2"
          >
            <svg aria-hidden="true" viewBox="0 0 6 12" className="h-3 w-1.5 fill-current">
              {[2, 6, 10].flatMap((y) => [<circle key={`a${y}`} cx="1" cy={y} r="1" />, <circle key={`b${y}`} cx="5" cy={y} r="1" />])}
            </svg>
          </button>
        )}
        {unit.levelName && <span className="shrink-0 rounded-sm bg-surface-2 px-1 text-[10px] leading-4 text-muted">{unit.levelName}</span>}
        <button
          type="button"
          onClick={() => onSelect({ kind: "unit", unitId: unit.id })}
          className={`min-w-0 truncate text-left text-[13px] font-bold hover:underline ${unit.state === "abolished" ? "line-through" : ""}`}
          title={unit.name}
        >
          {unit.name}
        </button>
        {STATE_LABEL[unit.state] && (
          <span
            className={`shrink-0 rounded-sm px-1 text-[10px] font-bold leading-4 ${unit.state === "created" ? "bg-accent text-accent-fg" : unit.state === "abolished" ? "bg-danger text-white" : "bg-warn-bg text-warn-fg"}`}
          >
            {STATE_LABEL[unit.state]}
          </span>
        )}
        <span className="ml-auto shrink-0 tabular text-muted" title="主務で所属する人">
          <span className="font-bold text-fg">
            {present}
            {truncated ? "+" : ""}
          </span>
          名
          {delta !== 0 && <span className="ml-0.5 font-bold text-accent">{delta > 0 ? `+${delta}` : `−${-delta}`}</span>}
        </span>
      </div>
      {(unit.changed.length > 0 || unit.headPersonId) && (
        <div className="flex flex-col gap-0.5 border-b border-border/70 px-2 py-1 text-muted">
          {unit.headPersonId && (
            <span className="truncate">
              組織長 <span className={unit.changed.includes("head_person_id") ? "font-bold text-warn-fg" : "text-fg"}>{data.names.get(unit.headPersonId) ?? "…"}</span>
            </span>
          )}
          {unit.changed.includes("name") && unit.base && <span className="truncate">旧名称 {unit.base.name}</span>}
          {parentChanged && unit.base && (
            <span className="truncate">
              上位 {nameOf(unit.base.parent_id)} → <span className="font-bold text-warn-fg">{nameOf(unit.parentId)}</span>
            </span>
          )}
        </div>
      )}
      <ul className="m-0 flex list-none flex-col gap-px p-1">
        {members.loading && chips.length === 0 && <li className="px-1.5 py-1 text-muted">…</li>}
        {shown.map((c) => (
          <PersonChip key={`${c.personId}:${c.mark}`} chip={c} unit={unit} data={data} editable={editable} selection={selection} onSelect={onSelect} />
        ))}
        {!members.loading && chips.length === 0 && <li className="px-1.5 py-1 text-muted">所属する人はいません</li>}
      </ul>
      {(chips.length > MEMBER_LIMIT || kids > 0 || editable) && (
        <div className="flex items-center gap-1 border-t border-border/70 px-1 py-0.5">
          {chips.length > MEMBER_LIMIT && (
            <button type="button" onClick={() => setAll(!all)} className="rounded-sm px-1.5 py-0.5 text-muted hover:bg-surface-2">
              {all ? "たたむ" : `ほか ${chips.length - MEMBER_LIMIT} 名`}
            </button>
          )}
          {kids > 0 && (
            <button type="button" aria-expanded={opened} onClick={() => toggle(unit.id)} className="ml-auto rounded-sm px-1.5 py-0.5 text-muted hover:bg-surface-2">
              配下 {kids} {opened ? "▴" : "▾"}
            </button>
          )}
        </div>
      )}
    </div>
  );
}

function PersonChip({
  chip,
  unit,
  data,
  editable,
  selection,
  onSelect,
}: {
  chip: Chip;
  unit: BoardUnit;
  data: BoardData;
  editable: boolean;
  selection: BoardSelection | null;
  onSelect: (s: BoardSelection | null) => void;
}) {
  const drag = useBoardDrag();
  const out = chip.mark === "out";
  const selected = selection?.kind === "person" && selection.personId === chip.personId;
  const isHead = chip.personId === unit.headPersonId;
  const other = chip.other ? (data.tree.byId.get(chip.other)?.name ?? "—") : null;
  const payload: DragPayload = { kind: "person", personId: chip.personId, name: chip.name, from: chip.from };
  return (
    <li>
      <button
        type="button"
        onPointerDown={editable && !out ? (e) => drag.begin(e, payload) : undefined}
        onClick={() => onSelect({ kind: "person", personId: chip.personId, name: chip.name, from: chip.from })}
        className={`flex w-full min-w-0 items-baseline gap-1.5 rounded-sm px-1.5 py-1 text-left ${editable && !out ? "cursor-grab touch-none" : ""} ${
          selected ? "bg-accent text-accent-fg" : chip.mark === "in" ? "bg-accent-weak hover:bg-accent-weak/70" : out ? "text-muted hover:bg-surface-2" : "hover:bg-surface-2"
        }`}
        title={out ? `${other}へ` : chip.mark === "in" ? `${other ?? "—"}から` : undefined}
      >
        <span className={`shrink-0 font-bold ${out ? "font-normal line-through" : ""}`}>{chip.name}</span>
        <span className={`min-w-0 truncate ${selected ? "" : chip.mark === "title" ? "font-bold text-warn-fg" : "text-muted"}`}>
          {out ? `→ ${other}` : chip.mark === "in" ? `${chip.title ? `${chip.title}・` : ""}${other ?? "—"}から` : (chip.title ?? (isHead ? "組織長" : ""))}
        </span>
      </button>
    </li>
  );
}
