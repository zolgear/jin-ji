// 修正依頼の画面・通知・起票で共有する部品（変更の知らせ・件数の取得・状態の札）。
import { useEffect, useState, type ReactNode } from "react";
import { useSession } from "../../auth/session";
import { Icon } from "../../auth/ui";
import { handlesCorrections, STATUS_LABEL, STATUS_NOTE, type CorrectionStatus, type CorrectionSummary } from "../../data/corrections";
import { useAdapter } from "../../data/adapterContext";

// ---- 変更の知らせ（起票・対応のあとに、件数のバッジと一覧を読み直す） ----

type ChangeKind = "created" | "closed";
const listeners = new Set<(kind: ChangeKind) => void>();

export function notifyCorrectionsChanged(kind: ChangeKind): void {
  listeners.forEach((l) => l(kind));
}

/** 起票・対応を知らせる（戻り値で解除）。 */
export function onCorrectionsChanged(listener: (kind: ChangeKind) => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** 閲覧者が修正担当者・人事管理者か（ボタン・通知の有無だけに使う。権限の判定は API が行う）。 */
export function useHandlesCorrections(): boolean {
  return handlesCorrections(useSession()?.session);
}

/** 修正担当者・人事管理者だけ、未対応の件数を取る（5 分ごと・起票と対応のあとに読み直す）。それ以外は null。 */
export function useCorrectionSummary(): CorrectionSummary | null {
  const { adapter } = useAdapter();
  const handler = useHandlesCorrections();
  const [summary, setSummary] = useState<CorrectionSummary | null>(null);
  useEffect(() => {
    if (!handler) return;
    let cancelled = false;
    const load = () =>
      adapter.corrections.summary().then(
        (s) => {
          if (!cancelled) setSummary(s);
        },
        () => {
          // 件数が取れなくても画面は使える（バッジを出さないだけ）
          if (!cancelled) setSummary(null);
        },
      );
    void load();
    const timer = setInterval(() => void load(), 5 * 60_000);
    const off = onCorrectionsChanged(() => void load());
    return () => {
      cancelled = true;
      clearInterval(timer);
      off();
    };
  }, [adapter, handler]);
  return handler ? summary : null;
}

/** 件数の読み上げ・ツールチップの文（人事データの値は含めない）。 */
export function summaryLabel(s: CorrectionSummary): string {
  return `修正依頼 未対応 ${s.open} 件（新しい依頼 ${s.new} 件、${s.stale_after_days} 日以上 ${s.stale} 件）`;
}

// ---- 状態の札 ----

type Tone = "warn" | "ok" | "muted" | "accent";

const TONE_CLASS: Record<Tone, string> = {
  warn: "border-warn-fg/30 bg-warn-bg text-warn-fg",
  ok: "border-accent/40 bg-accent-weak text-accent",
  accent: "border-accent/40 bg-surface text-accent",
  muted: "border-border bg-surface-2 text-muted",
};

/** 小さな札（色だけでなくアイコンと文字で示す）。 */
export function Tag({ tone, icon, children, title }: { tone: Tone; icon?: "warn" | "ok" | "info" | "error"; children: ReactNode; title?: string }) {
  return (
    <span title={title} className={`inline-flex shrink-0 items-center gap-1 rounded-sm border px-1.5 text-[11px] leading-[20px] font-bold ${TONE_CLASS[tone]}`}>
      {icon && <Icon name={icon} className="size-3" />}
      {children}
    </span>
  );
}

const STATUS_TONE: Record<CorrectionStatus, { tone: Tone; icon: "warn" | "ok" | "info" }> = {
  open: { tone: "warn", icon: "warn" },
  resolved: { tone: "ok", icon: "ok" },
  dismissed: { tone: "muted", icon: "info" },
  auto_resolved: { tone: "ok", icon: "ok" },
  superseded: { tone: "muted", icon: "info" },
};

export function StatusTag({ status }: { status: CorrectionStatus }) {
  const t = STATUS_TONE[status];
  const note = STATUS_NOTE[status];
  return (
    <Tag tone={t.tone} icon={t.icon} title={note}>
      {STATUS_LABEL[status]}
      {note && <span className="sr-only">（{note}）</span>}
    </Tag>
  );
}
