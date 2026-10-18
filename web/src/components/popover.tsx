// 小さな浮き出しの箱（メニュー・ヘルプ）。ブラウザの Popover API（popover="auto"）を使い、
// 外を押す・Esc で閉じる・最前面に出す・閉じたら押したボタンへフォーカスを戻す、をブラウザに任せる（依存を足さない）。
// 位置は開く直前（beforetoggle）に、押したボタンの位置から決める（CSS の anchor positioning はまだ全ブラウザでは使えないため）。
import { useEffect, useId, useRef, type ReactNode } from "react";

/** 箱を出す向き。"right" はボタンの右（下端を揃えて上へ伸ばす）。右に入らないとき（スマホの下のタブ）は上か下に出す。 */
export type PopoverSide = "right" | "bottom";

const GAP = 8;

function place(panel: HTMLElement, anchor: HTMLElement, side: PopoverSide, width: number) {
  const r = anchor.getBoundingClientRect();
  // レールの中のボタンは、レールの右端から出す（data-popover-edge を付けた祖先の右端）
  const edge = anchor.closest("[data-popover-edge]")?.getBoundingClientRect().right ?? r.right;
  const vw = window.innerWidth;
  const vh = window.innerHeight;
  const s = panel.style;
  s.position = "fixed";
  s.margin = "0";
  s.inset = "auto";
  if (side === "right" && edge + GAP + width <= vw - GAP) {
    s.left = `${edge + GAP}px`;
    s.bottom = `${Math.max(GAP, vh - r.bottom)}px`;
    s.maxHeight = `${r.bottom - GAP}px`;
    return;
  }
  // 横は画面の内側に収める
  s.left = `${Math.min(Math.max(GAP, r.left), Math.max(GAP, vw - width - GAP))}px`;
  if (r.top > vh / 2) {
    s.bottom = `${vh - r.top + GAP}px`;
    s.maxHeight = `${r.top - GAP * 2}px`;
  } else {
    s.top = `${r.bottom + GAP / 2}px`;
    s.maxHeight = `${vh - r.bottom - GAP * 2}px`;
  }
}

export interface PopoverProps {
  /** 開くボタンの読み上げる名前（ツールチップにも使う）。 */
  label: string;
  /** 開くボタンの中身（アイコンなど）。 */
  trigger: ReactNode;
  /** 開くボタンのクラス。 */
  triggerClassName: string;
  children: ReactNode;
  side?: PopoverSide;
  /** 箱の幅（px）。位置の計算にも使う。 */
  width?: number;
  /** 箱の id（外から aria-controls で指したいとき）。 */
  id?: string;
  /** ボタンの横に出すツールチップ（アイコンだけのボタン用）。 */
  tooltip?: ReactNode;
}

/** ボタンと、押すと開く箱。 */
export function Popover({ label, trigger, triggerClassName, children, side = "bottom", width = 288, id, tooltip }: PopoverProps) {
  const autoId = useId();
  const panelId = id ?? `${autoId}-popover`;
  const panel = useRef<HTMLDivElement>(null);
  const anchor = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    const el = panel.current;
    if (!el) return;
    const onBefore = (e: Event) => {
      if ((e as ToggleEvent).newState === "open" && anchor.current) place(el, anchor.current, side, width);
    };
    el.addEventListener("beforetoggle", onBefore);
    return () => el.removeEventListener("beforetoggle", onBefore);
  }, [side, width]);

  return (
    <>
      <button
        ref={anchor}
        type="button"
        popoverTarget={panelId}
        aria-label={label}
        aria-controls={panelId}
        className={`group relative ${triggerClassName}`}
      >
        {trigger}
        {tooltip}
      </button>
      <div
        ref={panel}
        id={panelId}
        popover="auto"
        style={{ width }}
        className="max-w-[calc(100vw-16px)] overflow-y-auto rounded-lg text-left font-normal whitespace-normal border border-border bg-surface p-0 text-sm text-fg shadow-xl"
      >
        {children}
      </div>
    </>
  );
}

function HelpIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round">
      <circle cx="8" cy="8" r="6.3" />
      <path d="M6.2 6.3a1.9 1.9 0 0 1 3.7.5c0 1.3-1.9 1.6-1.9 2.8" />
      <circle cx="8" cy="11.6" r="0.4" fill="currentColor" />
    </svg>
  );
}

function InfoIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round">
      <circle cx="8" cy="8" r="6.3" />
      <path d="M8 7.4v3.6" />
      <circle cx="8" cy="5.2" r="0.4" fill="currentColor" />
    </svg>
  );
}

/**
 * 「?」のボタン（trigger で差し替えられる）と説明の箱。画面に常に出しておくほどではない説明を入れる。
 * icon="info" は「i」のボタン。表の見出しの横など、小さく添える補足に使う（size を小さくする）。
 */
export function HelpTip({
  label,
  children,
  width = 300,
  trigger,
  icon = "help",
}: {
  label: string;
  children: ReactNode;
  width?: number;
  trigger?: ReactNode;
  icon?: "help" | "info";
}) {
  return (
    <Popover
      label={label}
      width={width}
      trigger={trigger ?? (icon === "info" ? <InfoIcon /> : <HelpIcon />)}
      triggerClassName={
        trigger
          ? "inline-flex shrink-0 items-center rounded-sm"
          : icon === "info"
            ? "inline-flex size-6 shrink-0 items-center justify-center rounded-md text-muted hover:bg-surface-2 hover:text-fg"
            : "inline-flex size-8 shrink-0 items-center justify-center rounded-md text-muted hover:bg-surface-2 hover:text-fg"
      }
    >
      <div className="space-y-2 px-4 py-3 text-[13px] leading-relaxed">{children}</div>
    </Popover>
  );
}
