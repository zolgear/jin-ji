// 修正依頼の画面（#/corrections）。
// - 修正担当者・人事管理者: すべての依頼。行ごとに「対応済み」「対応不要」（メモつき・確認してから送る）と、CSV のダウンロード
// - それ以外の人: 自分が起票・賛同した依頼と、自分についての依頼（操作なし）
// 修正依頼は人事パッケージの担当者への情報提供で、対応しても jin-ji の表示は変わらない（人事パッケージで直され、次の取込で反映される）。
// 理由・メモ・message は利用者・機械が書いた文なので、テキストとして表示する（リンクにしない）。
import { useCallback, useEffect, useId, useRef, useState } from "react";
import { useNav } from "../../app/nav";
import { Button, Icon, Notice } from "../../auth/ui";
import { HelpTip } from "../../components/popover";
import { Spinner } from "../../components/ui";
import {
  correctionFieldLabel,
  describeCorrectionError,
  DEFAULT_STALE_AFTER_DAYS,
  FILTER_LABEL,
  MAX_REASON_LENGTH,
  ORIGIN_LABEL,
  shortId,
  type Correction,
  type CorrectionPerson,
  type CorrectionStatusFilter,
  type CorrectionSummary,
} from "../../data/corrections";
import { useAdapter } from "../../data/adapterContext";
import { useFormatDate } from "../../lib/dateStyle";
import { useRoute } from "../../lib/router";
import { notifyCorrectionsChanged, onCorrectionsChanged, StatusTag, Tag, useCorrectionSummary, useHandlesCorrections } from "./shared";

const FILTERS: CorrectionStatusFilter[] = ["open", "closed", "all"];
const PAGE_SIZE = 50;

/** 日時を日本時間の日付（yyyy-MM-dd）にする（日付の表示は利用者の暦の設定で整形する）。 */
function jstDate(iso: string): string {
  const t = new Date(iso).getTime();
  return Number.isNaN(t) ? iso.slice(0, 10) : new Date(t + 9 * 3600_000).toISOString().slice(0, 10);
}

type Flash = { tone: "ok" | "error"; text: string } | null;

function SummaryBand({ summary }: { summary: CorrectionSummary }) {
  const formatDate = useFormatDate();
  const items: Array<{ label: string; value: number; tone?: "warn" }> = [
    { label: "未対応", value: summary.open },
    { label: `新しい依頼（${summary.new_within_days} 日以内）`, value: summary.new },
    { label: `${summary.stale_after_days} 日以上 未対応`, value: summary.stale, tone: summary.stale > 0 ? "warn" : undefined },
  ];
  return (
    <section aria-label="未対応の件数" className="rounded-md border border-border bg-surface px-4 py-3">
      <dl className="m-0 grid grid-cols-3 gap-3">
        {items.map((i) => (
          <div key={i.label} className="min-w-0">
            <dt className="text-xs text-muted">{i.label}</dt>
            <dd className={`m-0 flex items-center gap-1 text-xl font-bold tabular ${i.tone === "warn" ? "text-warn-fg" : ""}`}>
              {i.tone === "warn" && <Icon name="warn" className="size-4" />}
              {i.value}
              <span className="text-xs font-normal text-muted">件</span>
            </dd>
          </div>
        ))}
      </dl>
      {summary.oldest_created_at && (
        <p className="m-0 mt-1 text-xs text-muted">最も古い未対応の起票: {formatDate(jstDate(summary.oldest_created_at))}</p>
      )}
    </section>
  );
}

/** 閉じる操作（メモの入力と確認）。 */
function CloseForm({
  correction,
  kind,
  onDone,
  onCancel,
}: {
  correction: Correction;
  kind: "resolved" | "dismissed";
  onDone: (updated: Correction, text: string) => void;
  onCancel: () => void;
}) {
  const { adapter } = useAdapter();
  const id = useId();
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const ref = useRef<HTMLTextAreaElement>(null);
  useEffect(() => ref.current?.focus(), []);
  const label = kind === "resolved" ? "対応済み" : "対応不要";
  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      const updated = await adapter.corrections.close(correction.id, { status: kind, note: note.trim() || null });
      onDone(updated, `${label}にしました。`);
    } catch (e) {
      setError(describeCorrectionError(e));
      setBusy(false);
    }
  };
  return (
    <fieldset className="m-0 mt-3 flex min-w-0 flex-col gap-2 rounded-md border border-border bg-surface-2 p-3">
      <legend className="sr-only">{label}にする</legend>
      <p className="m-0 text-sm font-bold">{label}にしますか</p>
      <p className="m-0 text-xs text-muted">
        {kind === "resolved"
          ? "人事パッケージで直したことを記録します。jin-ji の表示は次の取込で変わります。"
          : "直す必要がないと判断したことを記録します。"}
        起票した人には、状態と対応のメモが見えます。
      </p>
      <div>
        <label htmlFor={id} className="mb-1 block text-xs font-bold">
          対応のメモ（任意）
        </label>
        <textarea
          id={id}
          ref={ref}
          value={note}
          maxLength={MAX_REASON_LENGTH}
          rows={2}
          onChange={(e) => setNote(e.target.value)}
          aria-describedby={`${id}-count`}
          className="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm"
        />
        <p id={`${id}-count`} className="m-0 text-right text-xs text-muted tabular">
          {note.length} / {MAX_REASON_LENGTH} 文字
        </p>
      </div>
      {error && <Notice tone="error">{error}</Notice>}
      <div className="flex flex-wrap gap-2">
        <Button variant="primary" busy={busy} className="min-h-8 px-3 py-1 text-xs" onClick={() => void submit()}>
          {label}にする
        </Button>
        <Button className="min-h-8 px-3 py-1 text-xs" onClick={onCancel} disabled={busy}>
          やめる
        </Button>
      </div>
    </fieldset>
  );
}

function PersonCell({ correction, person }: { correction: Correction; person: CorrectionPerson | undefined }) {
  const { openPerson, personId } = useNav();
  if (!correction.person_id) return <span className="text-muted">（人の特定なし）</span>;
  const name = person?.display_name;
  const detail = [person?.employee_number, person?.legal_entity_name].filter(Boolean).join("・");
  return (
    <div className="min-w-0">
      {name ? (
        <button
          type="button"
          className="text-left font-bold text-accent hover:underline"
          aria-pressed={personId === correction.person_id}
          onClick={() => openPerson(correction.person_id)}
        >
          {name}
        </button>
      ) : (
        <span className="font-mono text-muted" title={correction.person_id}>
          ID {shortId(correction.person_id)}
        </span>
      )}
      {correction.about_me && (
        <span className="ml-1.5 align-middle">
          <Tag tone="accent">あなた</Tag>
        </span>
      )}
      {detail && <div className="text-xs text-muted tabular">{detail}</div>}
      {person?.primary_org_name && <div className="text-xs text-muted">{person.primary_org_name}</div>}
    </div>
  );
}

function CorrectionItem({
  correction,
  person,
  handler,
  staleAfterDays,
  onClosed,
}: {
  correction: Correction;
  person: CorrectionPerson | undefined;
  handler: boolean;
  staleAfterDays: number;
  onClosed: (updated: Correction, text: string) => void;
}) {
  const formatDate = useFormatDate();
  const [closing, setClosing] = useState<"resolved" | "dismissed" | null>(null);
  const c = correction;
  const open = c.status === "open";
  const stale = open && c.age_days >= staleAfterDays;
  const field = c.field ? correctionFieldLabel(c.field) : null;
  const text = c.reason ?? c.message;
  return (
    <article className={`rounded-md border bg-surface p-3 sm:p-4 ${stale ? "border-warn-fg/40" : "border-border"}`} aria-label={`${field ?? "項目なし"}の修正依頼`}>
      <div className="flex flex-wrap items-center gap-1.5">
        <StatusTag status={c.status} />
        {open && (
          <span className={`text-xs tabular ${stale ? "font-bold text-warn-fg" : "text-muted"}`}>
            {c.age_days === 0 ? "今日の起票" : `起票から ${c.age_days} 日`}
            {stale && "（長く未対応）"}
          </span>
        )}
        {c.reported_by_me && <Tag tone="accent">あなたが起票</Tag>}
        {c.endorsed_by_me && <Tag tone="accent">賛同済み</Tag>}
        <span className="ml-auto text-xs text-muted tabular">起票 {formatDate(jstDate(c.created_at))}</span>
      </div>

      <div className="mt-2 flex flex-col gap-3 md:flex-row md:items-start md:gap-6">
        <div className="min-w-0 flex-1">
          <div className="grid gap-x-6 gap-y-2 sm:grid-cols-[minmax(10em,14em)_1fr]">
            <PersonCell correction={c} person={person} />
            <div className="min-w-0">
              <p className="m-0 font-bold">
                {field ?? (c.source_field ? `取込項目「${c.source_field}」` : "（項目の特定なし）")}
                {field && c.source_field && <span className="ml-1 text-xs font-normal text-muted">取込項目「{c.source_field}」</span>}
              </p>
              <p className="m-0 whitespace-pre-wrap">{text}</p>
              {c.reason === null && c.origin !== "ingest" && (
                <p className="m-0 text-xs text-muted">理由と対応のメモは、起票した人・対象の人・担当者だけが見られます。</p>
              )}
              {c.suggested_value && (
                <p className="m-0 mt-1 text-sm">
                  <span className="text-xs text-muted">正しい値の提案: </span>
                  <span className="rounded-sm border border-border bg-surface-2 px-1.5 py-px whitespace-pre-wrap">{c.suggested_value}</span>
                </p>
              )}
            </div>
          </div>

          <dl className="m-0 mt-2 flex flex-wrap gap-x-4 gap-y-0.5 text-xs text-muted">
            <div className="flex gap-1">
              <dt>経路:</dt>
              <dd className="m-0">{ORIGIN_LABEL[c.origin]}</dd>
            </div>
            {c.origin !== "ingest" && (
              <div className="flex gap-1">
                <dt>賛同:</dt>
                <dd className="m-0 tabular">{c.endorsements} 人</dd>
              </div>
            )}
            {c.file_name && (
              <div className="flex min-w-0 gap-1">
                <dt>取込:</dt>
                <dd className="m-0 break-all">
                  <span className="font-mono">{c.file_name}</span>
                  {c.source_row_number !== null && ` の ${c.source_row_number} 行目`}
                </dd>
              </div>
            )}
            {c.closed_at && (
              <div className="flex gap-1">
                <dt>{c.closed_kind === "auto" ? "自動で閉じた日:" : "閉じた日:"}</dt>
                <dd className="m-0 tabular">{formatDate(jstDate(c.closed_at))}</dd>
              </div>
            )}
          </dl>
          {c.closed_note && (
            <p className="m-0 mt-2 rounded-sm border-l-2 border-border bg-surface-2 px-2 py-1 text-sm whitespace-pre-wrap">
              <span className="text-xs text-muted">対応のメモ: </span>
              {c.closed_note}
            </p>
          )}
        </div>
        {handler && open && !closing && (
          <div className="flex flex-wrap gap-2 md:w-36 md:shrink-0 md:flex-col">
            <Button variant="primary" className="min-h-8 px-3 py-1 text-xs" onClick={() => setClosing("resolved")}>
              対応済みにする
            </Button>
            <Button className="min-h-8 px-3 py-1 text-xs" onClick={() => setClosing("dismissed")}>
              対応不要にする
            </Button>
          </div>
        )}
      </div>
      {closing && (
        <CloseForm
          correction={c}
          kind={closing}
          onCancel={() => setClosing(null)}
          onDone={(updated, message) => {
            setClosing(null);
            onClosed(updated, message);
          }}
        />
      )}
    </article>
  );
}

/** デモだけ: 閲覧者の役割を切り替える（端末に覚えて読み込み直す）。 */
function DemoViewerSwitch() {
  const id = useId();
  const [kind, setKind] = useState<"corrector" | "member" | null>(null);
  const [module, setModule] = useState<typeof import("../../demo/demoViewer") | null>(null);
  useEffect(() => {
    // 本番ビルドでは __JINJI_DEMO__ が false の定数になり、デモのモジュールごと取り除かれる
    if (__JINJI_DEMO__) {
      void import("../../demo/demoViewer").then((m) => {
        setModule(m);
        setKind(m.demoViewerKind());
      });
    }
  }, []);
  if (!module || !kind) return null;
  return (
    <div className="flex flex-wrap items-center gap-3 rounded-md border border-warn-fg/30 bg-warn-bg px-4 py-2.5 text-[13px] text-warn-fg">
      <label htmlFor={id} className="font-bold">
        デモの閲覧者
      </label>
      <select
        id={id}
        value={kind}
        title="切り替えると読み込み直し、デモの依頼の起票・対応は元に戻ります"
        aria-describedby={`${id}-note`}
        onChange={(e) => {
          module.setDemoViewerKind(e.target.value === "member" ? "member" : "corrector");
          window.location.reload();
        }}
        className="h-8 rounded-md border border-border bg-surface px-2 text-fg"
      >
        <option value="corrector">修正担当者・人事管理者（人事課）</option>
        <option value="member">一般の社員（ロールなし）</option>
      </select>
      {/* 注意書きは常には出さず、ツールチップと読み上げだけにする */}
      <span id={`${id}-note`} className="sr-only">
        切り替えると読み込み直し、デモの依頼の起票・対応は元に戻ります。
      </span>
    </div>
  );
}

export function CorrectionsView() {
  const { adapter } = useAdapter();
  const { route, navigate } = useRoute();
  const handler = useHandlesCorrections();
  const summary = useCorrectionSummary();
  const raw = route.params.get("status");
  const status: CorrectionStatusFilter = raw === "closed" || raw === "all" ? raw : "open";
  const [state, setState] = useState<{ key: string; items: Correction[]; people: Map<string, CorrectionPerson>; next: string | null } | null>(null);
  const [failure, setFailure] = useState<{ key: string; error: unknown } | null>(null);
  const [loadingMore, setLoadingMore] = useState(false);
  const [flash, setFlash] = useState<Flash>(null);
  const [downloading, setDownloading] = useState(false);
  const key = `${status}`;

  useEffect(() => {
    document.title = "修正依頼 - jin-ji";
    return () => {
      document.title = "jin-ji";
    };
  }, []);

  useEffect(() => {
    let cancelled = false;
    const load = () =>
      adapter.corrections.list({ status, limit: PAGE_SIZE }).then(
        (page) => {
          if (cancelled) return;
          setFailure(null);
          setState({ key, items: page.items, people: new Map(page.people.map((p) => [p.id, p])), next: page.nextCursor });
        },
        (e: unknown) => {
          if (!cancelled) setFailure({ key, error: e });
        },
      );
    void load();
    // 人の詳細から起票したら読み直す（対応したときは、その行だけを書き換える）
    const off = onCorrectionsChanged((kind) => {
      if (kind === "created") void load();
    });
    return () => {
      cancelled = true;
      off();
    };
  }, [adapter, status, key]);

  const loadMore = async () => {
    if (!state?.next) return;
    setLoadingMore(true);
    try {
      const page = await adapter.corrections.list({ status, cursor: state.next, limit: PAGE_SIZE });
      setState((s) =>
        s && s.key === key
          ? { key, items: [...s.items, ...page.items], people: new Map([...s.people, ...page.people.map((p) => [p.id, p] as const)]), next: page.nextCursor }
          : s,
      );
    } catch (e) {
      setFlash({ tone: "error", text: describeCorrectionError(e) });
    } finally {
      setLoadingMore(false);
    }
  };

  const onClosed = useCallback((updated: Correction, text: string) => {
    // 一覧は読み直さず、その行の状態だけを変える（閉じた行がその場で消えないように）
    setState((s) => (s ? { ...s, items: s.items.map((c) => (c.id === updated.id ? updated : c)) } : s));
    setFlash({ tone: "ok", text });
    notifyCorrectionsChanged("closed");
  }, []);

  const download = async () => {
    setDownloading(true);
    setFlash(null);
    try {
      const file = await adapter.corrections.report(status);
      const a = document.createElement("a");
      a.href = file.href;
      a.download = file.fileName ?? "";
      a.rel = "noopener";
      document.body.append(a);
      a.click();
      a.remove();
      if (file.dispose) setTimeout(file.dispose, 10_000);
    } catch (e) {
      setFlash({ tone: "error", text: describeCorrectionError(e) });
    } finally {
      setDownloading(false);
    }
  };

  const current = state && state.key === key ? state : null;
  const error = failure && failure.key === key ? failure.error : null;
  const staleAfterDays = summary?.stale_after_days ?? DEFAULT_STALE_AFTER_DAYS;

  return (
    <div className="h-full overflow-y-auto">
      <div className="mx-auto flex w-full max-w-5xl flex-col gap-5 px-4 py-5 md:px-6 md:py-6">
        {adapter.kind === "demo" && <DemoViewerSwitch />}
        {handler && summary && <SummaryBand summary={summary} />}

        <div>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <fieldset className="m-0 inline-flex min-w-0 rounded-md border border-border bg-surface p-0.5">
              <legend className="sr-only">状態で絞り込む</legend>
              {FILTERS.map((f) => (
                <button
                  key={f}
                  type="button"
                  aria-pressed={status === f}
                  onClick={() => navigate({ params: { status: f === "open" ? null : f } }, true)}
                  className={`rounded-sm px-3 py-1 text-sm ${status === f ? "bg-accent-weak font-bold text-accent" : "text-fg hover:bg-surface-2"}`}
                >
                  {FILTER_LABEL[f]}
                </button>
              ))}
            </fieldset>
            <span className="flex flex-wrap items-center gap-2">
              {/* 画面の説明は常に出さず、「?」の箱に入れる */}
              <HelpTip label="修正依頼について">
                {handler ? (
                  <>
                    <p className="m-0">jin-ji の値の誤りの報告と、取込で見つかった問題です。</p>
                    <p className="m-0">人事パッケージで直すと、次の取込で jin-ji に反映されます。直したら「対応済み」にしてください。</p>
                  </>
                ) : (
                  <>
                    <p className="m-0">あなたが起票・賛同した依頼と、あなたについての依頼です。修正依頼は人事パッケージの情報を直せる担当者に届きます。</p>
                    <p className="m-0">起票しても jin-ji の表示は変わりません。人事パッケージで直され、次の取込で反映されます。</p>
                  </>
                )}
              </HelpTip>
              {handler && (
                <Button busy={downloading} className="min-h-9 px-3 py-1.5 text-sm" onClick={() => void download()}>
                  <svg aria-hidden="true" viewBox="0 0 20 20" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round">
                    <path d="M10 3v10m-4-4 4 4 4-4M4 16.5h12" />
                  </svg>
                  CSV をダウンロード（{FILTER_LABEL[status]}）
                </Button>
              )}
            </span>
          </div>

          <div aria-live="polite" aria-atomic="true">
            {flash && (
              <Notice tone={flash.tone} className="mt-3">
                {flash.text}
              </Notice>
            )}
          </div>
        </div>

        {error ? (
          <Notice tone="error">{describeCorrectionError(error)}</Notice>
        ) : !current ? (
          <Spinner />
        ) : current.items.length === 0 ? (
          <div className="rounded-md border border-dashed border-border bg-surface px-4 py-8 text-center">
            <p className="m-0 font-bold">
              {status === "open" ? "未対応の修正依頼はありません。" : status === "closed" ? "閉じた修正依頼はありません。" : "修正依頼はありません。"}
            </p>
            <p className="m-0 mt-1 text-sm text-muted">値の誤りに気づいたら、人の詳細の「値の誤りを報告」から報告できます。</p>
          </div>
        ) : (
          <>
            <p className="m-0 text-xs text-muted">
              起票の古い順に表示しています（{current.items.length} 件{current.next ? "。続きがあります" : ""}）。
            </p>
            <ul className="m-0 flex list-none flex-col gap-3 p-0">
              {current.items.map((c) => (
                <li key={c.id}>
                  <CorrectionItem
                    correction={c}
                    person={c.person_id ? current.people.get(c.person_id) : undefined}
                    handler={handler}
                    staleAfterDays={staleAfterDays}
                    onClosed={onClosed}
                  />
                </li>
              ))}
            </ul>
            {current.next && (
              <div className="flex justify-center">
                <Button busy={loadingMore} onClick={() => void loadMore()}>
                  続きを読み込む
                </Button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
