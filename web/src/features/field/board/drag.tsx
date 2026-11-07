// 編集のボードのドラッグ＆ドロップ。HTML の drag & drop はタッチで動かないので、ポインタイベントで作る。
// 落とし先は data-drop-unit を持つ要素（組織の札）で、ポインタの下の要素から探す（拡大・縮小やスクロールに関係なく決まる）。
// 札の中の人（data-drop-person）の上なら、その人の前か後ろ（上半分か下半分か）も渡す（組織の中の並び順）。
// 少し動かすまではドラッグにしない（押して離しただけなら、呼ぶ側の onClick がそのまま働く）。
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";

export type DragPayload =
  | { kind: "person"; personId: string; name: string; from: { org: string | null; title: string | null } }
  | { kind: "unit"; unitId: string; name: string };

/** 札の中の落とす位置（この人の前か後ろ）。 */
export interface DropAt {
  personId: string;
  before: boolean;
}

interface DragState {
  payload: DragPayload;
  x: number;
  y: number;
  over: string | null;
  at: DropAt | null;
}

interface DragApi {
  /** 押したときに呼ぶ。動かしたらドラッグを始める。 */
  begin: (e: ReactPointerEvent, payload: DragPayload) => void;
  dragging: DragPayload | null;
  over: string | null;
  overAt: DropAt | null;
}

const DragContext = createContext<DragApi | null>(null);

export function useBoardDrag(): DragApi {
  const api = useContext(DragContext);
  if (!api) throw new Error("DragProvider の外で useBoardDrag が呼ばれました");
  return api;
}

const THRESHOLD = 5;
const EDGE = 48;

function targetAt(x: number, y: number): { unit: string; at: DropAt | null } | null {
  const el = document.elementFromPoint(x, y);
  const unit = el?.closest<HTMLElement>("[data-drop-unit]")?.dataset.dropUnit;
  if (!unit) return null;
  const chip = el?.closest<HTMLElement>("[data-drop-person]");
  if (!chip?.dataset.dropPerson) return { unit, at: null };
  const r = chip.getBoundingClientRect();
  return { unit, at: { personId: chip.dataset.dropPerson, before: y < r.top + r.height / 2 } };
}

export function DragProvider({
  children,
  scroller,
  canDrop,
  onDrop,
}: {
  children: ReactNode;
  /** ふちに寄せたときにスクロールさせる要素。 */
  scroller: React.RefObject<HTMLElement | null>;
  canDrop: (payload: DragPayload, unitId: string, at: DropAt | null) => boolean;
  onDrop: (payload: DragPayload, unitId: string, at: DropAt | null) => void;
}) {
  const [state, setState] = useState<DragState | null>(null);
  const handlers = useRef({ canDrop, onDrop });
  useEffect(() => {
    handlers.current = { canDrop, onDrop };
  }, [canDrop, onDrop]);

  const begin = useCallback(
    (e: ReactPointerEvent, payload: DragPayload) => {
      if (e.button !== 0) return;
      const start = { x: e.clientX, y: e.clientY };
      let active = false;
      const move = (ev: PointerEvent) => {
        if (!active && Math.hypot(ev.clientX - start.x, ev.clientY - start.y) < THRESHOLD) return;
        if (!active) {
          active = true;
          document.body.classList.add("board-dragging");
        }
        ev.preventDefault();
        const t = targetAt(ev.clientX, ev.clientY);
        const ok = t !== null && handlers.current.canDrop(payload, t.unit, t.at);
        setState({ payload, x: ev.clientX, y: ev.clientY, over: ok ? t.unit : null, at: ok ? t.at : null });
        const box = scroller.current?.getBoundingClientRect();
        if (box && scroller.current) {
          const dx = ev.clientX < box.left + EDGE ? -16 : ev.clientX > box.right - EDGE ? 16 : 0;
          const dy = ev.clientY < box.top + EDGE ? -16 : ev.clientY > box.bottom - EDGE ? 16 : 0;
          if (dx || dy) scroller.current.scrollBy(dx, dy);
        }
      };
      const end = (ev: PointerEvent) => {
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", end);
        window.removeEventListener("pointercancel", cancel);
        if (!active) return;
        document.body.classList.remove("board-dragging");
        // ドラッグの後に続くクリックで、札や人を選ばない
        const swallow = (c: MouseEvent) => (c.stopPropagation(), c.preventDefault());
        window.addEventListener("click", swallow, { capture: true, once: true });
        setTimeout(() => window.removeEventListener("click", swallow, { capture: true }), 0);
        const t = targetAt(ev.clientX, ev.clientY);
        setState(null);
        if (t && handlers.current.canDrop(payload, t.unit, t.at)) handlers.current.onDrop(payload, t.unit, t.at);
      };
      const cancel = () => {
        window.removeEventListener("pointermove", move);
        window.removeEventListener("pointerup", end);
        window.removeEventListener("pointercancel", cancel);
        document.body.classList.remove("board-dragging");
        setState(null);
      };
      window.addEventListener("pointermove", move, { passive: false });
      window.addEventListener("pointerup", end);
      window.addEventListener("pointercancel", cancel);
    },
    [scroller],
  );

  const api = useMemo<DragApi>(() => ({ begin, dragging: state?.payload ?? null, over: state?.over ?? null, overAt: state?.at ?? null }), [begin, state]);
  return (
    <DragContext.Provider value={api}>
      {children}
      {state &&
        createPortal(
          <div
            aria-hidden="true"
            className="pointer-events-none fixed z-50 rounded-md border border-accent bg-surface px-2 py-1 text-xs font-bold text-fg shadow-lg"
            style={{ left: state.x + 12, top: state.y + 8 }}
          >
            {state.payload.name}
          </div>,
          document.body,
        )}
    </DragContext.Provider>
  );
}
