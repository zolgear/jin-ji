// タイムマシン（表示する日付 as_of の切り替え）。上の帯には今の日付のボタンだけを置き、押すと箱（ネイティブの <dialog>）で
// 日付の入力（DateField）・目盛り（組織改編・一斉異動の日）のスライダー・「今日に戻る」・西暦 / 和暦を出す。
// 日付を変えるのはたまにで、常に出しておくと画面の上を広く取るため。代わりに、今日以外を見ているときはボタンを警告の色にして
// 「今日に戻る」を箱の外にも出す（過去・将来を見ていることに気付けるように）。
// 業務日付より後（内示）は指定できない（人事管理者以外には API が 403 を返すため）。
import { useEffect, useId, useRef } from "react";
import { useNav } from "../app/nav";
import { useAdapter } from "../data/adapterContext";
import { type DateStyle } from "../lib/date";
import { setDateStyle, useDateStyle, useFormatDate } from "../lib/dateStyle";
import { DateField } from "./DateField";
import { CloseIcon } from "./ui";

function ClockIcon({ className = "size-4" }: { className?: string }) {
  // 時計と、戻る向きの矢印
  return (
    <svg aria-hidden="true" viewBox="0 0 20 20" className={`${className} shrink-0`} fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
      <path d="M3.3 7.2A7 7 0 1 1 3 10.8" />
      <path d="M2.6 3.8v3.6h3.6M10 6.2V10l2.6 1.6" />
    </svg>
  );
}

function EraSwitch() {
  const style = useDateStyle();
  const options: Array<[DateStyle, string]> = [
    ["seireki", "西暦"],
    ["wareki", "和暦"],
  ];
  return (
    <fieldset className="m-0 flex overflow-hidden rounded-md border border-border p-0 text-sm">
      <legend className="sr-only">日付の表示</legend>
      {options.map(([value, label]) => (
        <button
          key={value}
          type="button"
          aria-pressed={style === value}
          onClick={() => setDateStyle(value)}
          className={`h-8 px-3 ${style === value ? "bg-accent text-accent-fg" : "hover:bg-surface-2"}`}
        >
          {label}
        </button>
      ))}
    </fieldset>
  );
}

export function TimeMachine({ className = "" }: { className?: string }) {
  const { adapter } = useAdapter();
  const { asOf, setAsOf } = useNav();
  const formatDate = useFormatDate();
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  const slider = useRef<HTMLInputElement>(null);
  // 業務日付より後（内示）は人事管理者だけ（API の meta.can_view_future）
  const maxDate = adapter.canViewFuture ? "9999-12-31" : adapter.businessDate;
  const milestones = adapter.milestones.filter((m) => m.date <= maxDate);
  const index = milestones.findIndex((m) => m.date === asOf);
  // 目盛りに無い日付のときは、その日以前で最も近い目盛りにつまみを置く
  const nearest = index >= 0 ? index : Math.max(0, milestones.findLastIndex((m) => m.date <= asOf));
  const current = milestones[index];
  // 目盛りの名前に含まれる日付（例: 一斉異動（2026-04-01））も、選んだ暦で書く
  const labelOf = (label: string) => label.replace(/\d{4}-\d{2}-\d{2}/g, (d) => formatDate(d));
  const isToday = asOf === adapter.businessDate;
  const when = isToday ? "today" : asOf > adapter.businessDate ? "future" : "past";
  const stateLabel = { today: "今日", past: "過去を表示中", future: "将来を表示中（内示を含む）" }[when];

  const open = () => {
    const d = dialog.current;
    const t = trigger.current;
    if (!d || !t) return;
    // sm 以上はボタンの下に、箱の右端をボタンの右端に揃えて出す（ボタンは帯の右端にあるため。画面からははみ出させない）。スマホは下からのシート（クラスの max-sm: が受け持つ）
    if (window.matchMedia("(min-width: 640px)").matches) {
      const r = t.getBoundingClientRect();
      d.style.top = `${r.bottom + 8}px`;
      const w = Math.min(440, window.innerWidth - 16);
      d.style.left = `${Math.max(8, Math.min(r.right - w, window.innerWidth - w - 8))}px`;
    } else {
      d.style.top = "";
      d.style.left = "";
    }
    d.showModal();
    // 目盛りに寄せる（矢印キーで日付を送れる）。目盛りが無ければ日付の入力欄
    (slider.current ?? d.querySelector<HTMLInputElement>("input[type='text']"))?.focus();
  };
  const close = () => dialog.current?.close();
  // 箱の外（背景）を押したら閉じる（背景を押すと、対象は <dialog> そのものになる）
  useEffect(() => {
    const d = dialog.current;
    if (!d) return;
    const onClick = (e: MouseEvent) => {
      if (e.target === d) d.close();
    };
    d.addEventListener("click", onClick);
    return () => d.removeEventListener("click", onClick);
  }, []);
  const backToToday = () => {
    setAsOf(adapter.businessDate);
    // 押したボタンは消えるので、フォーカスをタイムマシンのボタンに移す（読み上げで日付が今日に戻ったと分かる）
    requestAnimationFrame(() => trigger.current?.focus());
  };

  const tone =
    when === "today"
      ? "border-border bg-surface text-fg hover:bg-surface-2"
      : "border-warn-fg/40 bg-warn-bg text-warn-fg hover:brightness-[0.97]";

  return (
    <div className={`flex min-w-0 items-center gap-2 ${className}`}>
      <button
        ref={trigger}
        type="button"
        onClick={open}
        aria-haspopup="dialog"
        aria-controls={`${id}-dialog`}
        aria-label={`タイムマシン: ${formatDate(asOf)}（${stateLabel}）。日付を変える`}
        title="タイムマシン: 表示する日付を変える"
        className={`inline-flex h-9 min-w-0 items-center gap-2 rounded-md border px-3 text-sm ${tone}`}
      >
        <ClockIcon />
        <span className="hidden text-xs font-bold text-muted sm:inline">タイムマシン</span>
        <span className="tabular truncate font-bold">{formatDate(asOf)}</span>
        {when !== "today" && (
          <span className="hidden shrink-0 rounded-sm bg-warn-fg px-1.5 text-[11px] leading-[18px] font-bold text-warn-bg sm:inline">
            {when === "past" ? "過去" : "将来"}
          </span>
        )}
        <svg aria-hidden="true" viewBox="0 0 12 12" className="size-2.5 shrink-0 opacity-70" fill="currentColor">
          <path d="M2 4h8L6 8.5 2 4Z" />
        </svg>
      </button>
      {when !== "today" && (
        <button
          type="button"
          onClick={backToToday}
          className="inline-flex h-9 shrink-0 items-center gap-1.5 rounded-md bg-accent px-3 text-sm font-bold text-accent-fg hover:opacity-90"
        >
          今日に戻る
        </button>
      )}

      <dialog
        ref={dialog}
        id={`${id}-dialog`}
        aria-labelledby={`${id}-title`}
        onClose={() => trigger.current?.focus()}
        className="w-[440px] max-w-[calc(100vw-16px)] overflow-visible rounded-xl border border-border bg-surface p-0 text-fg shadow-2xl backdrop:bg-black/25 max-sm:mt-auto max-sm:mb-0 max-sm:w-full max-sm:max-w-none max-sm:rounded-b-none max-sm:backdrop:bg-black/50 sm:m-0 sm:[inset:auto]"
      >
        <div className="flex flex-col gap-5 p-5 max-sm:pb-[calc(1.25rem+env(safe-area-inset-bottom))]">
          <div className="flex items-start justify-between gap-3">
            <div>
              <h2 id={`${id}-title`} className="m-0 flex items-center gap-2 text-base font-bold">
                <ClockIcon className="size-5 text-accent" />
                タイムマシン
              </h2>
              <p className="m-0 mt-1 text-xs text-muted">選んだ日の組織と所属を表示します</p>
            </div>
            <button type="button" onClick={close} aria-label="閉じる" title="閉じる" className="-mt-1 -mr-2 inline-flex size-8 items-center justify-center rounded-md text-muted hover:bg-surface-2 hover:text-fg">
              <CloseIcon />
            </button>
          </div>

          {/* 目盛り・入力で日付が変わったことを読み上げる（入力欄の値の変化は読み上げられないため） */}
          <span className="sr-only" aria-live="polite">
            {`${formatDate(asOf)}${current ? ` ${labelOf(current.label)}` : ""}`}
          </span>

          <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
            {/* 日付は 1 回だけ出す: 見出しの体裁の入力欄（押すとその場で入力、横のボタンでカレンダー） */}
            <DateField value={asOf} max={maxDate} onChange={(v) => v && setAsOf(v)} label="表示する日付" variant="heading" labelVisible="never" />
            {current && <span className="rounded-sm bg-accent-weak px-2 text-xs leading-5 font-bold text-accent">{labelOf(current.label)}</span>}
            {when !== "today" && <span className="rounded-sm bg-warn-bg px-2 text-xs leading-5 text-warn-fg">{stateLabel}</span>}
          </div>

          {milestones.length > 1 && (
            <div className="flex flex-col gap-1.5">
              <label htmlFor={`${id}-slider`} className="text-xs font-bold text-muted">
                組織改編・一斉異動の日
              </label>
              <input
                ref={slider}
                id={`${id}-slider`}
                type="range"
                min={0}
                max={milestones.length - 1}
                step={1}
                value={nearest}
                list={`${id}-ticks`}
                aria-valuetext={`${formatDate(milestones[nearest]?.date)} ${labelOf(milestones[nearest]?.label ?? "")}`}
                onChange={(e) => {
                  const m = milestones[Number(e.target.value)];
                  if (m) setAsOf(m.date);
                }}
                className="w-full accent-[var(--accent)]"
              />
              <datalist id={`${id}-ticks`}>
                {milestones.map((m, i) => (
                  <option key={m.date} value={i}>
                    {labelOf(m.label)}
                  </option>
                ))}
              </datalist>
              <div aria-hidden="true" className="tabular flex justify-between text-[11px] text-muted">
                <span>{formatDate(milestones[0]?.date)}</span>
                <span>{formatDate(milestones[milestones.length - 1]?.date)}</span>
              </div>
            </div>
          )}

          <div className="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-4">
            <button
              type="button"
              onClick={() => setAsOf(adapter.businessDate)}
              disabled={isToday}
              className="inline-flex h-8 items-center rounded-md border border-border px-3 text-sm hover:bg-surface-2 disabled:opacity-50 disabled:hover:bg-transparent"
            >
              今日に戻る
            </button>
            <div className="flex items-center gap-2">
              <span className="text-xs text-muted">表示</span>
              <EraSwitch />
            </div>
          </div>
        </div>
      </dialog>
    </div>
  );
}
