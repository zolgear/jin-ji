// 人の詳細からの起票（「値の誤りを報告」）。項目は閲覧者に見える報告できる項目だけ。値の提案は受け付ける項目のときだけ出す。
// 起票しても jin-ji の表示は変わらない（人事パッケージの担当者への情報提供。直されたら次の取込で反映される）。
import { useEffect, useId, useRef, useState, type FormEvent } from "react";
import { Button, Notice } from "../../auth/ui";
import {
  acceptsSuggestedValue,
  describeCorrectionError,
  MAX_REASON_LENGTH,
  MAX_SUGGESTED_VALUE_LENGTH,
  OUTCOME_MESSAGE,
  reportableFields,
  SUGGESTION_HINT,
  suggestionKind,
  type CorrectionField,
} from "../../data/corrections";
import { useAdapter } from "../../data/adapterContext";
import type { Person } from "../../data/types";
import { formatHash, parseHash } from "../../lib/router";
import { notifyCorrectionsChanged } from "./shared";

type Result = { tone: "ok" | "error"; text: string; link: boolean } | null;

/** 修正依頼の一覧へのリンク（取得元の指定だけを引き継ぐ）。 */
function correctionsHref(): string {
  const source = parseHash(window.location.hash).params.get("source");
  return formatHash({ view: "corrections", params: new URLSearchParams(source ? { source } : {}) });
}

export function ReportCorrection({ person }: { person: Person }) {
  const { adapter } = useAdapter();
  const id = useId();
  const fields = reportableFields(person.visible_fields);
  const [open, setOpen] = useState(false);
  const [field, setField] = useState<CorrectionField | "">("");
  const [reason, setReason] = useState("");
  const [suggested, setSuggested] = useState("");
  const [errors, setErrors] = useState<{ field?: string; reason?: string }>({});
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<Result>(null);
  const selectRef = useRef<HTMLSelectElement>(null);
  const openerRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (open) selectRef.current?.focus();
  }, [open]);

  if (fields.length === 0) return null;

  const kind = field ? suggestionKind(field) : "none";
  const showSuggested = field !== "" && acceptsSuggestedValue(field);

  const reset = () => {
    setField("");
    setReason("");
    setSuggested("");
    setErrors({});
  };

  const cancel = () => {
    setOpen(false);
    reset();
    setTimeout(() => openerRef.current?.focus(), 0);
  };

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const next: typeof errors = {};
    if (!field) next.field = "報告する項目を選んでください。";
    if (!reason.trim()) next.reason = "誤りの内容・理由を書いてください。";
    setErrors(next);
    if (!field || next.reason) return;
    setBusy(true);
    setResult(null);
    try {
      const r = await adapter.corrections.create({
        personId: person.id,
        field,
        reason: reason.trim(),
        suggestedValue: showSuggested && suggested.trim() ? suggested.trim() : null,
      });
      setResult({ tone: "ok", text: OUTCOME_MESSAGE[r.outcome], link: true });
      setOpen(false);
      reset();
      if (r.outcome !== "already_reported") notifyCorrectionsChanged("created");
      setTimeout(() => openerRef.current?.focus(), 0);
    } catch (err) {
      setResult({ tone: "error", text: describeCorrectionError(err), link: false });
    } finally {
      setBusy(false);
    }
  };

  return (
    <section aria-labelledby={`${id}-title`} className="flex flex-col gap-2 border-t border-border pt-3">
      <h3 id={`${id}-title`} className="m-0 text-xs font-bold text-muted">
        値の誤りの報告
      </h3>
      <div aria-live="polite" aria-atomic="true">
        {result?.tone === "ok" && (
          <Notice tone="ok">
            {result.text}
            {result.link && (
              <>
                {" "}
                jin-ji の表示は、人事パッケージで直されて次の取込で反映されるまで変わりません。{" "}
                <a href={correctionsHref()} className="text-accent underline">
                  修正依頼の一覧
                </a>
              </>
            )}
          </Notice>
        )}
      </div>
      {!open && (
        <div>
          <Button ref={openerRef} className="min-h-8 px-3 py-1 text-xs" aria-expanded={false} aria-controls={`${id}-form`} onClick={() => {
            setResult(null);
            setOpen(true);
          }}>
            値の誤りを報告
          </Button>
        </div>
      )}
      {open && (
        <form id={`${id}-form`} onSubmit={(e) => void submit(e)} noValidate className="flex flex-col gap-3 rounded-md border border-border bg-surface-2 p-3">
          <p className="m-0 text-xs text-muted">
            報告は、人事パッケージの情報を直せる担当者に届きます。起票しても jin-ji の表示は変わりません。人事パッケージで直され、次の取込で反映されます。
          </p>
          <div>
            <label htmlFor={`${id}-field`} className="mb-1 block text-sm font-bold">
              項目
            </label>
            <select
              id={`${id}-field`}
              ref={selectRef}
              value={field}
              onChange={(e) => setField(e.target.value as CorrectionField | "")}
              aria-invalid={errors.field ? true : undefined}
              aria-describedby={errors.field ? `${id}-field-error` : undefined}
              className={`w-full rounded-md border bg-surface px-2 py-2 text-sm ${errors.field ? "border-danger" : "border-border"}`}
            >
              <option value="">選んでください</option>
              {fields.map((f) => (
                <option key={f.field} value={f.field}>
                  {f.label}
                </option>
              ))}
            </select>
            {errors.field && (
              <p id={`${id}-field-error`} className="m-0 mt-1 text-xs text-danger">
                {errors.field}
              </p>
            )}
          </div>
          <div>
            <label htmlFor={`${id}-reason`} className="mb-1 block text-sm font-bold">
              誤りの内容・理由
            </label>
            <textarea
              id={`${id}-reason`}
              value={reason}
              maxLength={MAX_REASON_LENGTH}
              rows={3}
              onChange={(e) => setReason(e.target.value)}
              aria-invalid={errors.reason ? true : undefined}
              aria-describedby={`${id}-reason-hint${errors.reason ? ` ${id}-reason-error` : ""}`}
              className={`w-full rounded-md border bg-surface px-3 py-2 text-sm ${errors.reason ? "border-danger" : "border-border"}`}
            />
            <div className="flex items-start justify-between gap-2">
              <p id={`${id}-reason-hint`} className="m-0 text-xs text-muted">
                生年月日・住所・私用の電話番号は書かないでください（jin-ji では保持しません）。
              </p>
              <span className="shrink-0 text-xs text-muted tabular">
                {reason.length} / {MAX_REASON_LENGTH}
              </span>
            </div>
            {errors.reason && (
              <p id={`${id}-reason-error`} className="m-0 mt-1 text-xs text-danger">
                {errors.reason}
              </p>
            )}
          </div>
          {showSuggested ? (
            <div>
              <label htmlFor={`${id}-suggested`} className="mb-1 block text-sm font-bold">
                正しい値（任意）
              </label>
              <input
                id={`${id}-suggested`}
                value={suggested}
                maxLength={MAX_SUGGESTED_VALUE_LENGTH}
                onChange={(e) => setSuggested(e.target.value)}
                inputMode={kind === "phone" ? "tel" : kind === "email" ? "email" : undefined}
                autoComplete="off"
                aria-describedby={SUGGESTION_HINT[kind] ? `${id}-suggested-hint` : undefined}
                className="w-full rounded-md border border-border bg-surface px-3 py-2 text-sm"
              />
              {SUGGESTION_HINT[kind] && (
                <p id={`${id}-suggested-hint`} className="m-0 mt-1 text-xs text-muted">
                  {SUGGESTION_HINT[kind]}
                </p>
              )}
            </div>
          ) : (
            field && <p className="m-0 text-xs text-muted">この項目は値の提案を受け付けません。正しい内容は理由に書いてください。</p>
          )}
          {result?.tone === "error" && <Notice tone="error">{result.text}</Notice>}
          <div className="flex flex-wrap gap-2">
            <Button type="submit" variant="primary" busy={busy} className="min-h-8 px-3 py-1 text-xs">
              報告する
            </Button>
            <Button className="min-h-8 px-3 py-1 text-xs" onClick={cancel} disabled={busy}>
              やめる
            </Button>
          </div>
        </form>
      )}
    </section>
  );
}
