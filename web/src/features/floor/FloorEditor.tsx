// 建物・フロアの編集（URL の edit=1）: 平面図の上で人を席へドラッグして席替えを組み立て、「計画として保存」か「変更を反映」する。
// 変更は直接登録・計画の変更の種類 workplace（ADR-0013 決定 11）。組織の変更の画面と同じく、反映は範囲（人の主務の組織）の中だけ。
//
// - 人のいる席に落とすと入れ替える。左の箱に落とすと席を外す（席の決まっていない人として残る）
// - ほかの階・席の無い人は、左の箱で探してドラッグする（同じ編集のまま階を移って、別の階の席にも落とせる）
// - 保存・反映の前の変更は、階を移っても・画面を離れても消えないよう drafts に覚える（組織の変更の編集とは別の下書き）
// - ドラッグはポインタイベントで作る（タッチでも動く）。落とし先はポインタの下の data-drop-seat（席。"none" は左の箱）
// - ドラッグの代わりに、人を押して（キーボードでも）選んでから席を押しても動かせる
import { useCallback, useEffect, useId, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { createPortal } from "react-dom";
import { Button, Notice } from "../../auth/ui";
import { FloatingCard } from "../../components/FloatingCard";
import { HelpTip, Popover } from "../../components/popover";
import { CloseButton, personName, Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import type { PlanInput } from "../../data/field";
import { useQuery } from "../../data/query";
import { describeError, type FloorLayout, type FloorOccupant, type Person } from "../../data/types";
import { loadDraft, saveDraft } from "../field/drafts";
import { describeChange, inputClass, useNamers } from "../field/shared";
import { applySeatMoves, dropOnSeat, holdersOf, seatMovesOf, setSeat, type PersonInfo } from "./floorEdit";
import { buildFloorView, type OrgGroup } from "./floorModel";
import { FloorPlan2D } from "./FloorPlan2D";

const DRAFT_KEY = "floor";
const THRESHOLD = 5;

interface Dragging {
  personId: string;
  name: string;
  x: number;
  y: number;
  over: string | null;
}

/** 人の今の席（この階の席でなければ席の場所の文字）。 */
function seatText(p: Person): string {
  return p.seat ? `${p.seat.building_name} ${p.seat.floor_label}・${p.seat.code}` : p.workplace ? `${p.workplace.building_name} ${p.workplace.floor_label}` : "席なし";
}

function infoOf(p: Person): PersonInfo {
  const primary = p.assignments?.find((a) => a.type === "primary");
  return { name: personName(p), orgId: primary?.org_unit_id ?? null, orgName: primary?.org_unit_name ?? null };
}

export function FloorEditor({
  title,
  asOf,
  layout,
  occupants,
  groupOf,
  monochrome,
  onReflected,
}: {
  title: string;
  asOf: string;
  layout: FloorLayout;
  occupants: FloorOccupant[];
  groupOf: (orgId: string | null) => OrgGroup | null;
  monochrome: boolean;
  onReflected: () => void;
}) {
  const { adapter } = useAdapter();
  const field = adapter.field;
  const panelId = useId();
  const savePanel = `${panelId}-save`;
  const reflectPanel = `${panelId}-reflect`;
  const [draft, setDraft] = useState<PlanInput>(
    () => loadDraft(DRAFT_KEY) ?? { title: "", note: null, base_date: asOf, effective_date: asOf < adapter.businessDate ? adapter.businessDate : asOf, changes: [] },
  );
  const [planId, setPlanId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const [selectedSeat, setSelectedSeat] = useState<string | null>(null);
  // 左の箱は PC では最初から開く（スマホは図が狭いので閉じておく）
  const [panel, setPanel] = useState<"tray" | "changes" | null>(() => (window.innerWidth >= 640 ? "tray" : null));
  const [dragging, setDragging] = useState<Dragging | null>(null);
  // ドラッグの代わりに選んだ人（次に押した席へ動かす）
  const [picked, setPicked] = useState<string | null>(null);
  // 左の箱で探した人（ほかの階・席の無い人）の元の席と名前
  const [known, setKnown] = useState<Map<string, { original: string | null; info: PersonInfo }>>(() => new Map());

  useEffect(() => {
    saveDraft(DRAFT_KEY, draft.changes.length > 0 || draft.title.trim() !== "" ? draft : null);
  }, [draft]);

  const changes = draft.changes;
  const setChanges = useCallback((next: PlanInput["changes"]) => setDraft((d) => ({ ...d, changes: next })), []);
  const seatIds = useMemo(() => new Set(layout.seats.map((s) => s.seat_id)), [layout]);
  const seatCode = useMemo(() => new Map(layout.seats.map((s) => [s.seat_id, s.code])), [layout]);
  const moves = useMemo(() => seatMovesOf(changes), [changes]);
  const info = useMemo(() => new Map([...known].map(([id, k]) => [id, k.info])), [known]);
  const shown = useMemo(() => applySeatMoves(occupants, moves, seatIds, info), [occupants, moves, seatIds, info]);
  const originals = useMemo(() => {
    const m = new Map<string, string | null>();
    for (const [id, k] of known) m.set(id, k.original);
    for (const o of occupants) if (o.role === "primary") m.set(o.person_id, o.seat_id);
    return m;
  }, [occupants, known]);
  const view = useMemo(() => buildFloorView({ layout, occupants: shown, presence: null, groupOf, monochrome }), [layout, shown, groupOf, monochrome]);
  const changedSeatIds = useMemo(() => {
    const before = holdersOf(occupants);
    const after = holdersOf(shown);
    const ids = new Set<string>();
    for (const id of new Set([...before.keys(), ...after.keys()])) if ((before.get(id) ?? []).join() !== (after.get(id) ?? []).join()) ids.add(id);
    return ids;
  }, [occupants, shown]);
  const currentSeat = useCallback((personId: string) => {
    if (moves.has(personId)) {
      const to = moves.get(personId) ?? null;
      return to && seatIds.has(to) ? to : null;
    }
    return occupants.find((o) => o.person_id === personId && o.role === "primary")?.seat_id ?? null;
  }, [moves, seatIds, occupants]);
  const names = useNamers(asOf, changes, []);
  const describe = (c: PlanInput["changes"][number]) =>
    describeChange(c, { ...names, seat: (id) => (seatCode.has(id) ? `${seatCode.get(id)}（${title}）` : null) });
  const unseated = [...moves].filter(([, to]) => to === null).map(([id]) => id);

  // ---- ドラッグ ----------------------------------------------------------------------------------------------------
  const handlers = useRef({ drop: (_personId: string, _target: string) => {} });
  useEffect(() => {
    handlers.current.drop = (personId, target) => {
      if (target === "none") {
        setChanges(setSeat(changes, personId, null, originals.get(personId) ?? null));
        return;
      }
      const seat = layout.seats.find((s) => s.seat_id === target);
      if (!seat) return;
      const next = dropOnSeat(changes, shown, { personId, current: currentSeat(personId) }, seat, originals);
      if (next.length === changes.length && next.every((c, i) => c === changes[i])) {
        setMessage({ tone: "error", text: seat.status === "unavailable" ? `${seat.code} は使えない席です。` : `${seat.code} は定員まで埋まっています。` });
        return;
      }
      setChanges(next);
      setSelectedSeat(target);
    };
  });
  const begin = useCallback((e: ReactPointerEvent, personId: string, name: string) => {
    if (e.button !== 0) return;
    const start = { x: e.clientX, y: e.clientY };
    let active = false;
    const targetAt = (x: number, y: number) => document.elementFromPoint(x, y)?.closest<Element>("[data-drop-seat]")?.getAttribute("data-drop-seat") ?? null;
    const move = (ev: PointerEvent) => {
      if (!active && Math.hypot(ev.clientX - start.x, ev.clientY - start.y) < THRESHOLD) return;
      if (!active) {
        active = true;
        document.body.classList.add("board-dragging");
      }
      ev.preventDefault();
      setDragging({ personId, name, x: ev.clientX, y: ev.clientY, over: targetAt(ev.clientX, ev.clientY) });
    };
    const stop = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", end);
      window.removeEventListener("pointercancel", stop);
      document.body.classList.remove("board-dragging");
      setDragging(null);
    };
    const end = (ev: PointerEvent) => {
      const wasActive = active;
      stop();
      if (!wasActive) return;
      // ドラッグの後に続くクリックで、席を選ばない
      const swallow = (c: MouseEvent) => (c.stopPropagation(), c.preventDefault());
      window.addEventListener("click", swallow, { capture: true, once: true });
      setTimeout(() => window.removeEventListener("click", swallow, { capture: true }), 0);
      const target = targetAt(ev.clientX, ev.clientY);
      if (target) handlers.current.drop(personId, target);
    };
    window.addEventListener("pointermove", move, { passive: false });
    window.addEventListener("pointerup", end);
    window.addEventListener("pointercancel", stop);
  }, []);
  const remember = (p: Person) => {
    if (!known.has(p.id) && !occupants.some((o) => o.person_id === p.id)) {
      setKnown((m) => new Map(m).set(p.id, { original: p.seat?.seat_id ?? null, info: infoOf(p) }));
    }
  };

  // ---- 保存・反映 ---------------------------------------------------------------------------------------------------
  const run = async (action: () => Promise<void>, ok: string) => {
    setBusy(true);
    setMessage(null);
    try {
      await action();
      setMessage({ tone: "ok", text: ok });
    } catch (e) {
      setMessage({ tone: "error", text: describeError(e) });
    } finally {
      setBusy(false);
    }
  };
  const planTitle = draft.title.trim() || `${title}の席替え`;
  const save = () =>
    run(async () => {
      document.getElementById(savePanel)?.hidePopover();
      const input = { ...draft, title: planTitle, base_date: asOf };
      const saved = planId ? await field.updatePlan(planId, input) : await field.createPlan(input);
      setPlanId(saved.id);
    }, `計画「${planTitle}」を保存しました。組織の変更の画面の「計画」から開けます。`);
  const reflect = () =>
    run(async () => {
      document.getElementById(reflectPanel)?.hidePopover();
      await field.register({ effective_date: draft.effective_date, note: null, changes });
      setDraft((d) => ({ ...d, title: "", changes: [] }));
      setSelectedSeat(null);
      onReflected();
    }, `${changes.length} 件の席の変更を反映しました（有効日 ${draft.effective_date}）。`);

  const selected = selectedSeat ? view.seats.find((s) => s.seat.seat_id === selectedSeat) : undefined;
  const toolClass = (primary: boolean, disabled: boolean) =>
    `h-8 rounded-sm px-2 text-sm font-bold whitespace-nowrap sm:px-3 ${primary ? "bg-accent text-accent-fg hover:brightness-110" : "hover:bg-surface-2"} ${disabled ? "pointer-events-none opacity-40" : ""}`;

  return (
    // スマホでは操作の帯を図の上の段に置く（左上の箱と重ねない）。PC は図の上に浮かべる
    <div className="relative flex size-full flex-col">
      <div className="flex shrink-0 flex-wrap items-start justify-end gap-2 border-b border-border px-3 py-1.5 sm:pointer-events-none sm:absolute sm:inset-x-3 sm:top-3 sm:z-20 sm:border-0 sm:p-0 sm:[&>*]:pointer-events-auto">
        <div className="flex items-center gap-0.5 rounded-md border border-border bg-surface p-1 shadow-sm">
          <button type="button" aria-pressed={panel === "tray"} onClick={() => setPanel((p) => (p === "tray" ? null : "tray"))} className={`${toolClass(false, false)} aria-pressed:bg-accent-weak aria-pressed:text-accent`}>
            人を探す{unseated.length > 0 ? ` ${unseated.length}` : ""}
          </button>
          <button type="button" aria-pressed={panel === "changes"} onClick={() => { setPanel((p) => (p === "changes" ? null : "changes")); setSelectedSeat(null); }} className={`${toolClass(false, false)} aria-pressed:bg-accent-weak aria-pressed:text-accent`}>
            変更 {changes.length}
          </button>
        </div>
        <div className="flex items-center gap-0.5 rounded-md border border-border bg-surface p-1 shadow-sm">
          <Popover id={savePanel} label="計画として保存" trigger="計画として保存" triggerClassName={toolClass(true, changes.length === 0)} width={300}>
            <div className="flex flex-col gap-2 p-3">
              <label className="flex flex-col gap-1 text-xs">
                <span className="text-muted">題名</span>
                <input className={`${inputClass} font-bold`} value={draft.title} placeholder={`${title}の席替え`} onChange={(e) => setDraft({ ...draft, title: e.target.value })} maxLength={200} />
              </label>
              <Button variant="primary" busy={busy} disabled={changes.length === 0} onClick={save}>
                保存
              </Button>
            </div>
          </Popover>
          <Popover id={reflectPanel} label="変更を反映" trigger="変更を反映" triggerClassName={toolClass(false, changes.length === 0)} width={300}>
            <div className="flex flex-col gap-2 p-3">
              <label className="flex flex-col gap-1 text-xs">
                <span className="flex items-center gap-1 text-muted">
                  有効日
                  <HelpTip label="反映の説明">
                    席を変えられるのは、範囲（組織長・指名された担当・組織管理者）の中の人です。有効日の前なら「組織の変更」の反映の記録から取り消せます。
                  </HelpTip>
                </span>
                <input type="date" className={inputClass} value={draft.effective_date} min={adapter.businessDate} onChange={(e) => setDraft({ ...draft, effective_date: e.target.value })} />
              </label>
              <Button variant="primary" busy={busy} disabled={changes.length === 0 || draft.effective_date < adapter.businessDate} onClick={reflect}>
                {changes.length} 件を反映する
              </Button>
            </div>
          </Popover>
        </div>
      </div>

      <div className="relative min-h-0 flex-1">
        <FloorPlan2D
          view={view}
          title={title}
          className="size-full"
          onSeat={(s) => {
            if (picked) {
              handlers.current.drop(picked, s.seat.seat_id);
              setPicked(null);
              return;
            }
            setSelectedSeat(s.seat.seat_id);
            setPanel(null);
          }}
          edit={{
            onPersonDown: (e, _seat, p) => begin(e, p.personId, p.name ?? "（氏名非公開）"),
            overSeatId: dragging?.over && dragging.over !== "none" ? dragging.over : null,
            changedSeatIds,
            selectedSeatId: selectedSeat,
          }}
        />
        {panel === "tray" && (
          <FloatingCard label="人を探す" title="人を探す" side="left" top="sm:top-3 sm:max-h-[calc(100%-1.5rem)]" onClose={() => setPanel(null)}>
            <PersonTray
              asOf={asOf}
              unseated={unseated}
              names={names.person}
              picked={picked}
              onGrab={(e, id, name, p) => {
                if (p) remember(p);
                begin(e, id, name);
              }}
              onPick={(id, p) => {
                if (p) remember(p);
                setPicked((cur) => (cur === id ? null : id));
              }}
              dropping={dragging?.over === "none"}
            />
          </FloatingCard>
        )}
        {panel === "changes" && (
          <FloatingCard label="変更" title={`変更 ${changes.length} 件`} onClose={() => setPanel(null)} top="sm:top-16 sm:max-h-[calc(100%-5rem)]">
            {changes.length === 0 ? (
              <p className="m-0 text-muted">変更はありません。</p>
            ) : (
              <ul className="m-0 flex list-none flex-col gap-1 p-0">
                {changes.map((c, i) => (
                  <li key={`${c.target_id}:${i}`} className="flex items-start gap-2 border-b border-border/60 pb-1">
                    <span className="min-w-0 flex-1">{describe(c)}</span>
                    <Button variant="ghost" onClick={() => setChanges(changes.filter((x) => x !== c))}>
                      元に戻す
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </FloatingCard>
        )}
        {selected && (
          <FloatingCard label="席" title={<span className="tabular">{selected.seat.code}</span>} onClose={() => setSelectedSeat(null)}>
            {selected.people.length === 0 ? (
              <p className="m-0 text-muted">{selected.seat.status === "unavailable" ? "使えない席" : "空席"}</p>
            ) : (
              selected.people.map((p) => {
                const original = originals.get(p.personId) ?? null;
                const changed = moves.has(p.personId);
                return (
                  <div key={p.personId} className="flex flex-col gap-1">
                    <span className="font-bold">{p.name ?? "（氏名非公開）"}</span>
                    {p.orgName && <span className="text-xs text-muted">{p.orgName}</span>}
                    {changed && <span className="text-xs text-muted">元の席: {original ? (seatCode.get(original) ?? "ほかの階") : "なし"}</span>}
                    <span className="flex flex-wrap gap-1">
                      <Button variant="ghost" onClick={() => { setPicked(p.personId); setSelectedSeat(null); }}>
                        席を移す
                      </Button>
                      <Button variant="ghost" onClick={() => setChanges(setSeat(changes, p.personId, null, original))}>
                        席を外す
                      </Button>
                      {changed && (
                        <Button variant="ghost" onClick={() => setChanges(changes.filter((c) => !(c.kind === "workplace" && c.target_id === p.personId)))}>
                          元に戻す
                        </Button>
                      )}
                    </span>
                  </div>
                );
              })
            )}
          </FloatingCard>
        )}
        {picked && (
          <div className="absolute bottom-14 left-1/2 flex -translate-x-1/2 items-center gap-2 rounded-full border border-accent bg-surface py-1 pr-1 pl-3 text-sm shadow-lg">
            <span>
              <b>{names.person(picked) === "—" ? (known.get(picked)?.info.name ?? "—") : names.person(picked)}</b> → 席を選ぶ
            </span>
            <CloseButton label="やめる" onClick={() => setPicked(null)} />
          </div>
        )}
        {message && (
          <div className="absolute bottom-14 left-1/2 flex w-[min(32rem,calc(100%-24px))] -translate-x-1/2 items-start gap-1 shadow-lg">
            <Notice tone={message.tone} className="flex-1">
              {message.text}
            </Notice>
            <button type="button" aria-label="閉じる" onClick={() => setMessage(null)} className="absolute top-1 right-1 h-7 w-7 rounded-sm text-muted hover:bg-surface-2">
              ×
            </button>
          </div>
        )}
      </div>
      {dragging &&
        createPortal(
          <div aria-hidden="true" className="pointer-events-none fixed z-50 rounded-md border border-accent bg-surface px-2 py-1 text-xs font-bold text-fg shadow-lg" style={{ left: dragging.x + 12, top: dragging.y + 8 }}>
            {dragging.name}
          </div>,
          document.body,
        )}
    </div>
  );
}

/** 左の箱: 人を探してドラッグする（押して選び、席を押してもよい）。席を外した人も並べる。ここへ落とすと席を外す。 */
function PersonTray({
  asOf,
  unseated,
  names,
  picked,
  onGrab,
  onPick,
  dropping,
}: {
  asOf: string;
  unseated: string[];
  names: (id: string) => string;
  picked: string | null;
  onGrab: (e: ReactPointerEvent, id: string, name: string, person?: Person) => void;
  onPick: (id: string, person?: Person) => void;
  dropping: boolean;
}) {
  const { adapter } = useAdapter();
  const [q, setQ] = useState("");
  const [debounced, setDebounced] = useState("");
  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(q.trim()), 250);
    return () => window.clearTimeout(t);
  }, [q]);
  const results = useQuery(adapter, debounced ? `floor-edit-find:${asOf}:${debounced}` : null, () => adapter.searchPeople(debounced, { asOf, limit: 12 }));
  const chip = (id: string, name: string, sub: string | null, person?: Person) => (
    <button
      type="button"
      aria-pressed={picked === id}
      onPointerDown={(e) => onGrab(e, id, name, person)}
      onClick={() => onPick(id, person)}
      className="flex w-full cursor-grab touch-none flex-col items-start rounded-md border border-border bg-surface px-2 py-1 text-left select-none hover:border-accent aria-pressed:border-accent aria-pressed:bg-accent-weak"
    >
      <span className="font-bold">{name}</span>
      {sub && <span className="text-[11px] text-muted">{sub}</span>}
    </button>
  );
  return (
    <div data-drop-seat="none" className={`-m-1 flex flex-col gap-2 rounded-md p-1 ${dropping ? "outline-2 outline-accent outline-dashed" : ""}`}>
      <input type="search" value={q} placeholder="名前で探す" aria-label="席を決める人を探す" onChange={(e) => setQ(e.target.value)} className={inputClass} />
      {debounced && (
        <ul className="m-0 flex list-none flex-col gap-1 p-0">
          {results.loading && !results.data && (
            <li>
              <Spinner />
            </li>
          )}
          {results.data?.items.length === 0 && <li className="text-muted">見つかりません</li>}
          {results.data?.items.map((p) => (
            <li key={p.id}>{chip(p.id, personName(p), seatText(p), p)}</li>
          ))}
        </ul>
      )}
      {unseated.length > 0 && (
        <section aria-label="席を外した人" className="flex flex-col gap-1">
          <h3 className="m-0 text-xs font-bold text-muted">席を外した人</h3>
          <ul className="m-0 flex list-none flex-col gap-1 p-0">
            {unseated.map((id) => (
              <li key={id}>{chip(id, names(id), null)}</li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
