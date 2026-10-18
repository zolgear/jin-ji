// 日付の入力欄（時点の欄・検索の入社年月日で共有）。
//
// ブラウザの <input type="date"> の表示は使わない。表示がブラウザの言語設定に従い、
// 日本語の画面でも「10/01/2026」のような米国式になることがあるため。入力は自前で解釈し（西暦・和暦・全角を受け付ける）、
// 表示は利用者が選んだ暦（西暦 / 和暦）にする。内部の値は常に西暦の yyyy-MM-dd。
// variant="heading" は、日付そのものを見出しの体裁（大きな太字・「2026年10月1日」の長い形）で出し、押すとその場で入力できる
// （時点のバーで、見出しと入力欄に同じ日付を 2 回出さないため）。
import { useId, useRef, useState } from "react";
import { formatDateShort, isIsoDate, parseDateInput } from "../lib/date";
import { useDateStyle, useFormatDate } from "../lib/dateStyle";

function CalendarIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.4">
      <rect x="2" y="3" width="12" height="11" rx="1.5" />
      <path d="M2 6.5h12M5.5 1.5v3M10.5 1.5v3" />
    </svg>
  );
}

export interface DateFieldProps {
  /** yyyy-MM-dd。clearable なら null（未指定）も取る。 */
  value: string | null;
  onChange: (iso: string | null) => void;
  /** 入力欄の名前（読み上げる名前。labelVisible でなければ画面には出さない）。 */
  label: string;
  /** 名前を画面に出すか（"sm" は幅が sm 以上のときだけ）。 */
  labelVisible?: "always" | "sm" | "never";
  min?: string;
  max?: string;
  /** 空にして確定すると null にできる。 */
  clearable?: boolean;
  /** 欄の幅（Tailwind のクラス）。 */
  widthClass?: string;
  /** "heading": 見出しの体裁（枠は指したとき・入力中だけ出す。値は長い形で表示し、入力中も長い形のまま編集できる）。 */
  variant?: "field" | "heading";
}

/** 日付の入力欄。確定（Enter・フォーカスを外す）したときに解釈し、読めない・範囲外ならその場で知らせる。 */
export function DateField({ value, onChange, label, labelVisible = "sm", min, max, clearable = false, widthClass, variant = "field" }: DateFieldProps) {
  const style = useDateStyle();
  const formatDate = useFormatDate();
  const id = useId();
  const hintId = useId();
  const picker = useRef<HTMLInputElement>(null);
  const heading = variant === "heading";
  // 見出しの体裁では「2026年10月1日」「令和8年10月1日」の長い形（parseDateInput はこの形も読める）
  const shown = value ? (heading ? formatDate(value) : formatDateShort(value, style)) : "";
  // 編集中の文字列（null のときは値を整形して出す）
  const [draft, setDraft] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const commit = () => {
    if (draft === null) return;
    if (clearable && draft.trim() === "") {
      setError(null);
      setDraft(null);
      if (value !== null) onChange(null);
      return;
    }
    const iso = parseDateInput(draft);
    if (!iso) {
      setError("日付として読めません（例: 2026/4/1、令和8年4月1日、R8.4.1）");
      return;
    }
    if (max && iso > max) {
      setError(`${formatDate(max)}より後は指定できません`);
      return;
    }
    if (min && iso < min) {
      setError(`${formatDate(min)}より前は指定できません`);
      return;
    }
    setError(null);
    setDraft(null);
    if (iso !== value) onChange(iso);
  };

  const labelTone = heading ? "text-xs font-bold text-muted" : "text-xs text-muted";
  const labelClass = labelVisible === "always" ? labelTone : labelVisible === "sm" ? `sr-only ${labelTone} sm:not-sr-only` : "sr-only";
  const width = widthClass ?? (heading ? "" : "w-[8.5em]");
  const placeholder = heading ? (style === "wareki" ? "令和8年4月1日" : "2026年4月1日") : style === "wareki" ? "R8.04.01" : "2026/04/01";
  // 見出しの体裁では、欄の幅を中身の長さに合わせる（日付の後ろに空きを作らず、カレンダーのボタンを日付のすぐ横に置く）。
  // 数字・記号は全角の約 0.7 文字分として数える
  const headingWidth = heading && !widthClass ? `${[...((draft ?? shown) || placeholder)].reduce((w, c) => w + (c.charCodeAt(0) < 0x80 ? 0.68 : 1), 0) + 1.1}em` : undefined;
  const inputClass = heading
    ? `tabular ${width} rounded-sm border px-1 py-0 text-base font-bold underline decoration-muted decoration-dotted underline-offset-4 focus:no-underline ${error ? "border-danger" : "border-transparent hover:border-border focus:border-border"} bg-transparent hover:bg-surface focus:bg-surface`
    : `tabular ${width} rounded-sm border bg-surface px-2 py-0.5 ${error ? "border-danger" : "border-border"}`;

  return (
    <div className="relative flex items-center gap-1">
      <label htmlFor={id} className={labelClass}>
        {label}
      </label>
      <input
        id={id}
        type="text"
        inputMode="text"
        autoComplete="off"
        spellCheck={false}
        value={draft ?? shown}
        placeholder={placeholder}
        style={headingWidth ? { width: headingWidth } : undefined}
        aria-invalid={error ? true : undefined}
        aria-describedby={hintId}
        onFocus={(e) => e.currentTarget.select()}
        onChange={(e) => {
          setDraft(e.target.value);
          setError(null);
        }}
        onBlur={commit}
        onKeyDown={(e) => {
          if (e.key === "Enter") commit();
          if (e.key === "Escape") {
            setDraft(null);
            setError(null);
          }
        }}
        className={inputClass}
      />
      {/* 暦の一覧から選びたい人向け。ブラウザの日付選択を開くだけで、値の表示には使わない */}
      <button
        type="button"
        title="カレンダーから選ぶ"
        aria-label={`${label}をカレンダーから選ぶ`}
        onClick={() => {
          const el = picker.current;
          if (!el) return;
          try {
            el.showPicker();
          } catch {
            el.focus();
          }
        }}
        className={`inline-flex size-7 shrink-0 items-center justify-center rounded-sm text-muted hover:bg-surface-2 hover:text-fg ${heading ? "" : "border border-border"}`}
      >
        <CalendarIcon />
      </button>
      <input
        ref={picker}
        type="date"
        tabIndex={-1}
        aria-hidden="true"
        value={value ?? ""}
        min={min}
        max={max}
        onChange={(e) => {
          const v = e.target.value;
          if (isIsoDate(v) && (!max || v <= max) && (!min || v >= min)) {
            setDraft(null);
            setError(null);
            onChange(v);
          }
        }}
        className="pointer-events-none absolute right-0 bottom-0 size-px opacity-0"
      />
      <span id={hintId} role={error ? "alert" : undefined} className={error ? "absolute top-full left-0 z-30 mt-1 w-max max-w-[80vw] rounded-sm border border-danger/40 bg-surface px-2 py-1 text-xs text-danger shadow" : "sr-only"}>
        {error ?? `西暦（2026/4/1）・和暦（令和8年4月1日、R8.4.1）で入力できます${clearable ? "。空にすると指定なし" : ""}`}
      </span>
    </div>
  );
}
