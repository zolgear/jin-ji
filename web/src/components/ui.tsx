// 小さな共通部品。Radix は使っていない（今の画面はネイティブ要素で足りるため。モーダル・メニューが要るときに必要なものだけ足す）。
import type { ReactNode } from "react";
import type { Person } from "../data/types";

/** 非公開（閲覧者の公開範囲の外）の表示。値を推測させる情報は出さない。 */
export function PrivateValue() {
  return (
    <span className="inline-flex items-center gap-1 rounded-sm bg-private-bg px-1.5 py-px text-xs text-private-fg">
      <svg aria-hidden="true" viewBox="0 0 16 16" className="size-3" fill="currentColor">
        <path d="M5 7V5a3 3 0 1 1 6 0v2h.5A1.5 1.5 0 0 1 13 8.5v5a1.5 1.5 0 0 1-1.5 1.5h-7A1.5 1.5 0 0 1 3 13.5v-5A1.5 1.5 0 0 1 4.5 7H5Zm1.5 0h3V5a1.5 1.5 0 0 0-3 0v2Z" />
      </svg>
      非公開
    </span>
  );
}

/** 見えるが値が無い項目の表示。 */
export function EmptyValue() {
  return (
    <span className="text-muted">
      <span aria-hidden="true">—</span>
      <span className="sr-only">未登録</span>
    </span>
  );
}

export function isVisible(person: Person, field: string): boolean {
  return person.visible_fields.includes(field);
}

/** 項目の値。見えない項目は「非公開」、値が無ければ「—」。 */
export function FieldValue({ person, field, children }: { person: Person; field: string; children?: ReactNode }) {
  if (!isVisible(person, field)) return <PrivateValue />;
  if (children === null || children === undefined || children === "" || children === false) return <EmptyValue />;
  return <>{children}</>;
}

export type LinkKind = "primary" | "concurrent" | "secondment";

/** 所属の種類ごとの線の見本の描き方（色 + 線種。色だけに頼らない）。 */
export const LINK_STYLE: Record<LinkKind, { color: string; dash: string | undefined; width: number; label: string }> = {
  primary: { color: "var(--series-primary)", dash: undefined, width: 2.5, label: "主務" },
  concurrent: { color: "var(--concurrent)", dash: "6 3", width: 2.5, label: "兼務" },
  secondment: { color: "var(--secondment)", dash: "0.5 4", width: 3, label: "出向" },
};

/** 線の種類の見本（凡例・人数の横に置く印）。文字はデータの色にしない。 */
export function LineKey({ kind, width = 18 }: { kind: LinkKind; width?: number }) {
  const s = LINK_STYLE[kind];
  return (
    <svg aria-hidden="true" width={width} height="8" className="line-key">
      <path d={`M2 4h${width - 4}`} stroke={s.color} strokeWidth={s.width} strokeDasharray={s.dash} strokeLinecap="round" />
    </svg>
  );
}

type Tone = "neutral" | "accent" | "warn" | "danger" | LinkKind;

const TONES: Record<Tone, string> = {
  neutral: "border-border text-muted",
  accent: "border-accent/40 bg-accent-weak text-accent",
  primary: "border-border text-fg",
  concurrent: "border-border text-fg",
  secondment: "border-border text-fg",
  warn: "border-warn-fg/30 bg-warn-bg text-warn-fg",
  danger: "border-danger/40 text-danger",
};

/** 状態のアイコン（状態は色だけで示さない）。 */
function StatusIcon({ tone }: { tone: "warn" | "danger" }) {
  return tone === "warn" ? (
    // 一時停止（II）
    <svg aria-hidden="true" viewBox="0 0 12 12" className="size-2.5" fill="currentColor">
      <rect x="2" y="1.5" width="2.6" height="9" rx="0.6" />
      <rect x="7.4" y="1.5" width="2.6" height="9" rx="0.6" />
    </svg>
  ) : (
    <svg aria-hidden="true" viewBox="0 0 12 12" className="size-2.5" fill="none" stroke="currentColor" strokeWidth="1.8">
      <path d="M3 3l6 6m0-6L3 9" />
    </svg>
  );
}

export function Badge({ tone = "neutral", children }: { tone?: Tone; children: ReactNode }) {
  const isLink = tone === "primary" || tone === "concurrent" || tone === "secondment";
  return (
    <span className={`inline-flex shrink-0 items-center gap-1 rounded-sm border px-1.5 text-[11px] leading-[18px] font-bold ${TONES[tone]}`}>
      {isLink && <LineKey kind={tone} width={14} />}
      {(tone === "warn" || tone === "danger") && <StatusIcon tone={tone} />}
      {children}
    </span>
  );
}

/** 人の表示名（業務上の氏名）。表示名が見えなければ姓名、どちらも見えなければ「氏名非公開」。 */
export function personName(person: Person): string {
  if (person.display_name) return person.display_name;
  const name = [person.family_name, person.given_name].filter(Boolean).join(" ");
  return name || "（氏名非公開）";
}

export function personKana(person: Person): string | null {
  const kana = [person.family_name_kana, person.given_name_kana].filter(Boolean).join(" ");
  return kana || null;
}

export function Spinner({ label = "読み込み中" }: { label?: string }) {
  return (
    <output aria-live="polite" className="inline-flex items-center gap-2 text-muted">
      <span aria-hidden="true" className="size-3 animate-spin rounded-full border-2 border-border border-t-accent" />
      {label}…
    </output>
  );
}

export function ErrorNote({ children }: { children: ReactNode }) {
  return (
    <p role="alert" className="rounded-sm border border-danger/40 bg-surface px-3 py-2 text-danger">
      {children}
    </p>
  );
}

export function IconButton({ label, onClick, children }: { label: string; onClick: () => void; children: ReactNode }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label}
      title={label}
      className="inline-flex size-8 items-center justify-center rounded-sm text-muted hover:bg-surface-2 hover:text-fg"
    >
      {children}
    </button>
  );
}

export function CloseIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.8">
      <path d="M3.5 3.5l9 9m0-9l-9 9" />
    </svg>
  );
}
