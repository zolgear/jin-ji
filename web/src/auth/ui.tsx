// 認証まわりの画面の共通部品（枠・ボタン・入力欄・お知らせ・コピー・確認付きのボタン）。
// 色は index.css の意味のトークンだけを使い、状態は色だけでなくアイコンと文字で示す。
import { useEffect, useId, useRef, useState, type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type Ref } from "react";
import { ThemeSwitch } from "../components/ThemeSwitch";

/** 認証の画面の枠（上の帯・本文・下の注記）。width は本文の最大幅。 */
export function AuthFrame({
  title,
  width = "md",
  children,
  aside,
  nav,
}: {
  title: string;
  width?: "md" | "lg" | "xl" | "wide";
  children: ReactNode;
  aside?: ReactNode;
  nav?: ReactNode;
}) {
  useEffect(() => {
    document.title = `${title} - jin-ji`;
    return () => {
      document.title = "jin-ji";
    };
  }, [title]);
  const max = { md: "max-w-md", lg: "max-w-xl", xl: "max-w-3xl", wide: "max-w-6xl" }[width];
  return (
    <div className="flex min-h-dvh flex-col bg-bg">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:z-50 focus:bg-surface focus:p-2">
        本文へ移動
      </a>
      <header className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-border bg-surface px-4 py-2">
        <a href="/" className="flex items-baseline gap-2 no-underline">
          <span className="text-base font-bold tracking-wide text-fg">jin-ji</span>
          <span className="text-xs text-muted">人事情報</span>
        </a>
        {nav}
        <div className="ml-auto">
          <ThemeSwitch />
        </div>
      </header>
      <main id="main" className={`mx-auto w-full ${max} flex-1 px-4 py-8 sm:py-12`}>
        {children}
      </main>
      {aside && <footer className={`mx-auto w-full ${max} px-4 pb-8 text-xs text-muted`}>{aside}</footer>}
    </div>
  );
}

/** 白い面の箱。 */
export function Panel({ children, className = "", as: Tag = "section", ...rest }: { children: ReactNode; className?: string; as?: "section" | "div" } & Record<string, unknown>) {
  return (
    <Tag className={`rounded-lg border border-border bg-surface p-5 sm:p-6 ${className}`} {...rest}>
      {children}
    </Tag>
  );
}

type Variant = "primary" | "secondary" | "danger" | "ghost";

const VARIANTS: Record<Variant, string> = {
  primary: "border-accent bg-accent text-accent-fg hover:brightness-110 disabled:opacity-60",
  secondary: "border-border bg-surface text-fg hover:bg-surface-2 disabled:opacity-60",
  danger: "border-danger/60 bg-surface text-danger hover:bg-surface-2 disabled:opacity-60",
  ghost: "border-transparent bg-transparent text-accent underline-offset-2 hover:underline disabled:opacity-60",
};

export function Button({
  variant = "secondary",
  busy = false,
  full = false,
  className = "",
  children,
  disabled,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: Variant; busy?: boolean; full?: boolean; ref?: Ref<HTMLButtonElement> }) {
  return (
    <button
      type="button"
      {...rest}
      disabled={disabled || busy}
      aria-busy={busy || undefined}
      className={`inline-flex min-h-10 items-center justify-center gap-2 rounded-md border px-4 py-2 text-sm font-bold transition-[filter,background-color] disabled:cursor-not-allowed ${
        full ? "w-full" : ""
      } ${VARIANTS[variant]} ${className}`}
    >
      {busy && <span aria-hidden="true" className="size-3.5 animate-spin rounded-full border-2 border-current border-t-transparent" />}
      {children}
    </button>
  );
}

/** ボタンと同じ見た目のリンク（外部 IdP への遷移など、トップレベルの遷移に使う）。 */
export function LinkButton({
  href,
  variant = "secondary",
  full = false,
  className = "justify-center",
  children,
}: {
  href: string;
  variant?: Variant;
  full?: boolean;
  className?: string;
  children: ReactNode;
}) {
  return (
    <a
      href={href}
      className={`inline-flex min-h-10 items-center gap-2 rounded-md border px-4 py-2 text-sm font-bold no-underline ${full ? "w-full" : ""} ${VARIANTS[variant]} ${className}`}
    >
      {children}
    </a>
  );
}

/** ラベル付きの入力欄。hint は下に出す説明、error はその欄の誤り。 */
export function TextField({
  label,
  hint,
  error,
  className = "",
  ref,
  ...input
}: InputHTMLAttributes<HTMLInputElement> & { label: string; hint?: ReactNode; error?: string | null; ref?: Ref<HTMLInputElement> }) {
  const id = useId();
  const describedBy = [input["aria-describedby"], hint ? `${id}-hint` : null, error ? `${id}-error` : null].filter(Boolean).join(" ") || undefined;
  return (
    <div className={className}>
      <label htmlFor={id} className="mb-1 block text-sm font-bold">
        {label}
      </label>
      <input
        id={id}
        ref={ref}
        {...input}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy}
        className={`w-full rounded-md border bg-surface px-3 py-2 text-base ${error ? "border-danger" : "border-border"} ${input.readOnly ? "bg-surface-2" : ""}`}
      />
      {hint && (
        <p id={`${id}-hint`} className="mt-1 text-xs text-muted">
          {hint}
        </p>
      )}
      {error && (
        <p id={`${id}-error`} className="mt-1 flex items-start gap-1 text-xs text-danger">
          <Icon name="error" className="mt-0.5 size-3.5 shrink-0" />
          {error}
        </p>
      )}
    </div>
  );
}

type IconName = "info" | "warn" | "error" | "ok" | "key" | "lock" | "shield" | "arrow" | "copy" | "external";

export function Icon({ name, className = "size-4" }: { name: IconName; className?: string }) {
  const common = { "aria-hidden": true, viewBox: "0 0 20 20", className, fill: "none", stroke: "currentColor", strokeWidth: 1.7, strokeLinecap: "round", strokeLinejoin: "round" } as const;
  switch (name) {
    case "info":
      return (
        <svg {...common}>
          <circle cx="10" cy="10" r="7.5" />
          <path d="M10 9v5M10 6.2v.1" />
        </svg>
      );
    case "warn":
      return (
        <svg {...common}>
          <path d="M10 2.8 18 16.5H2L10 2.8Z" />
          <path d="M10 8v4M10 14.4v.1" />
        </svg>
      );
    case "error":
      return (
        <svg {...common}>
          <circle cx="10" cy="10" r="7.5" />
          <path d="m7.2 7.2 5.6 5.6m0-5.6-5.6 5.6" />
        </svg>
      );
    case "ok":
      return (
        <svg {...common}>
          <circle cx="10" cy="10" r="7.5" />
          <path d="m6.5 10.2 2.4 2.4 4.7-5" />
        </svg>
      );
    case "key":
      return (
        <svg {...common}>
          <circle cx="7" cy="10" r="3.5" />
          <path d="M10.5 10H18m-2.5 0v3m-2.5-3v2" />
        </svg>
      );
    case "lock":
      return (
        <svg {...common}>
          <rect x="4" y="8.5" width="12" height="8.5" rx="1.5" />
          <path d="M6.8 8.5V6.3a3.2 3.2 0 0 1 6.4 0v2.2" />
        </svg>
      );
    case "shield":
      return (
        <svg {...common}>
          <path d="M10 2.5 16 5v4.6c0 3.6-2.5 6.6-6 7.9-3.5-1.3-6-4.3-6-7.9V5l6-2.5Z" />
        </svg>
      );
    case "arrow":
      return (
        <svg {...common}>
          <path d="M4 10h11m-4-4 4 4-4 4" />
        </svg>
      );
    case "copy":
      return (
        <svg {...common}>
          <rect x="7" y="7" width="9.5" height="9.5" rx="1.5" />
          <path d="M13 7V4.8A1.3 1.3 0 0 0 11.7 3.5H4.8A1.3 1.3 0 0 0 3.5 4.8v6.9A1.3 1.3 0 0 0 4.8 13H7" />
        </svg>
      );
    case "external":
      return (
        <svg {...common}>
          <path d="M11 3.5h5.5V9M16.5 3.5 9 11M14 12v3.2a1.3 1.3 0 0 1-1.3 1.3H4.8a1.3 1.3 0 0 1-1.3-1.3V7.3A1.3 1.3 0 0 1 4.8 6H8" />
        </svg>
      );
  }
}

type Tone = "info" | "warn" | "error" | "ok";

const NOTICE: Record<Tone, string> = {
  info: "border-border bg-surface-2 text-fg",
  warn: "border-warn-fg/30 bg-warn-bg text-warn-fg",
  error: "border-danger/50 bg-surface text-danger",
  ok: "border-accent/40 bg-accent-weak text-fg",
};

const NOTICE_LABEL: Record<Tone, string> = { info: "お知らせ", warn: "注意", error: "エラー", ok: "完了" };

/** お知らせ。error は role=alert で読み上げる。title は太字の見出し。 */
export function Notice({ tone = "info", title, children, className = "" }: { tone?: Tone; title?: ReactNode; children?: ReactNode; className?: string }) {
  return (
    <div role={tone === "error" ? "alert" : tone === "ok" ? "status" : undefined} className={`flex gap-2.5 rounded-md border px-3 py-2.5 text-sm ${NOTICE[tone]} ${className}`}>
      <Icon name={tone === "ok" ? "ok" : tone} className={`mt-0.5 size-4 shrink-0 ${tone === "ok" ? "text-accent" : ""}`} />
      <div className="min-w-0 flex-1">
        <span className="sr-only">{NOTICE_LABEL[tone]}: </span>
        {title && <p className="m-0 font-bold">{title}</p>}
        {children && <div className={title ? "mt-0.5" : ""}>{children}</div>}
      </div>
    </div>
  );
}

/** 値をクリップボードにコピーするボタン。結果は読み上げる。 */
export function CopyButton({ value, label = "コピー" }: { value: string; label?: string }) {
  const [state, setState] = useState<"idle" | "done" | "failed">("idle");
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => {
    if (timer.current) clearTimeout(timer.current);
  }, []);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setState("done");
    } catch {
      setState("failed");
    }
    if (timer.current) clearTimeout(timer.current);
    timer.current = setTimeout(() => setState("idle"), 2500);
  };
  return (
    <button
      type="button"
      onClick={() => void copy()}
      className="inline-flex shrink-0 items-center gap-1 rounded-sm border border-border bg-surface px-2 py-1 text-xs font-bold hover:bg-surface-2"
    >
      <Icon name={state === "done" ? "ok" : "copy"} className="size-3.5" />
      <span aria-live="polite">{state === "done" ? "コピーしました" : state === "failed" ? "コピーできません（選択してコピー）" : label}</span>
    </button>
  );
}

/** 値の表示とコピー（シークレット・URL など。等幅で折り返す）。 */
export function CopyField({ label, value, mono = true }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <div className="mb-1 flex items-center justify-between gap-2">
        <span className="text-xs font-bold text-muted">{label}</span>
        <CopyButton value={value} />
      </div>
      <output className={`block rounded-md border border-border bg-surface-2 px-3 py-2 text-sm break-all select-all ${mono ? "font-mono" : ""}`}>{value}</output>
    </div>
  );
}

/**
 * 確認が要る操作のボタン（停止・却下・取り消しなど）。1 回目で確認の文と「実行」「やめる」を出し、2 回目で実行する。
 * window.confirm は使わない（読み上げ・スタイルを揃えるため）。
 */
export function ConfirmButton({
  label,
  confirmLabel,
  message,
  onConfirm,
  variant = "secondary",
  busy = false,
}: {
  label: string;
  confirmLabel?: string;
  message: ReactNode;
  onConfirm: () => void;
  variant?: Variant;
  busy?: boolean;
}) {
  const [asking, setAsking] = useState(false);
  const confirmRef = useRef<HTMLButtonElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    if (asking) confirmRef.current?.focus();
  }, [asking]);
  if (!asking) {
    return (
      <Button ref={triggerRef} variant={variant} busy={busy} onClick={() => setAsking(true)} className="min-h-8 px-3 py-1 text-xs">
        {label}
      </Button>
    );
  }
  return (
    <fieldset className="m-0 flex w-full min-w-0 flex-wrap items-center gap-2 rounded-md border border-border bg-surface-2 px-3 py-2 text-xs">
      <legend className="sr-only">{label}の確認</legend>
      <span className="min-w-0 flex-1">{message}</span>
      <Button
        ref={confirmRef}
        variant={variant === "danger" ? "danger" : "primary"}
        className="min-h-8 px-3 py-1 text-xs"
        onClick={() => {
          setAsking(false);
          onConfirm();
        }}
      >
        {confirmLabel ?? label}
      </Button>
      <Button
        className="min-h-8 px-3 py-1 text-xs"
        onClick={() => {
          setAsking(false);
          setTimeout(() => triggerRef.current?.focus(), 0);
        }}
      >
        やめる
      </Button>
    </fieldset>
  );
}

/** 読み込み中の表示（画面全体）。 */
export function Loading({ label = "読み込み中" }: { label?: string }) {
  return (
    <output aria-live="polite" className="flex items-center justify-center gap-2 py-16 text-muted">
      <span aria-hidden="true" className="size-4 animate-spin rounded-full border-2 border-border border-t-accent" />
      {label}…
    </output>
  );
}

/** 日時（日本時間）。 */
export function formatDateTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return new Intl.DateTimeFormat("ja-JP", {
    timeZone: "Asia/Tokyo",
    year: "numeric",
    month: "numeric",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  }).format(d);
}

export function formatTime(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return new Intl.DateTimeFormat("ja-JP", { timeZone: "Asia/Tokyo", hour: "2-digit", minute: "2-digit" }).format(d);
}
