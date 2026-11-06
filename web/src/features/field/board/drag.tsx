// 素案ボードのドラッグ＆ドロップ。HTML の drag & drop はタッチで動かないので、ポインタイベントで作る。
// 落とし先は data-drop-unit を持つ要素（組織の札）で、ポインタの下の要素から探す（拡大・縮小やスクロールに関係なく決まる）。
// 少し動かすまではドラッグにしない（押して離しただけなら、呼ぶ側の onClick がそのまま働く）。
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent, type ReactNode } from "react";
import { createPortal } from "react-dom";

export type DragPayload =
  | { kind: "person"; personId: string; name: string; from: { org: string | null; title: string | null } }
  | { kind: "unit"; unitId: string; name: string };

interface DragState {
  payload: DragPayload;
  x: number;
  y: number;
  over: string | null;
}

interface DragApi {
  /** 押したときに呼ぶ。動かしたらドラッグを始める。 */
  begin: (e: ReactPointerEvent, payload: DragPayload) => void;
  dragging: DragPayload | null;
  over: string | null;
}

const DragContext = createContext<DragApi | null>(null);

export function useBoardDrag(): DragApi {
  const api = useContext(DragContext);
  if (!api) throw new Error("DragProvider の外で useBoardDrag が呼ばれました");
  return api;
}

const THRESHOLD = 5;
const EDGE = 48;

function targetAt(x: number, y: number): string | null {
  const el = document.elementFromPoint(x, y);
  return el?.closest<HTMLElement>("[data-drop-unit]")?.dataset.dropUnit ?? null;
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
  canDrop: (payload: DragPayload, unitId: string) => boolean;
  onDrop: (payload: DragPayload, unitId: string) => void;
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
        const id = targetAt(ev.clientX, ev.clientY);
        const over = id && handlers.current.canDrop(payload, id) ? id : null;
        setState({ payload, x: ev.clientX, y: ev.clientY, over });
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
        const id = targetAt(ev.clientX, ev.clientY);
        setState(null);
        if (id && handlers.current.canDrop(payload, id)) handlers.current.onDrop(payload, id);
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

  const api = useMemo<DragApi>(() => ({ begin, dragging: state?.payload ?? null, over: state?.over ?? null }), [begin, state]);
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
