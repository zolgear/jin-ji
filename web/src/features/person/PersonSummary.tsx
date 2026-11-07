// 人の要点（フリガナ・所属〔兼務・出向を含む〕・社員番号・雇用区分・拠点）と、全項目を開く「詳細」のダイアログ。
// 組織の変更の編集・3D 表示の浮かべる箱で使う（画面を移らずに全項目を見られる）。
import { useCallback, useRef } from "react";
import { usePerson } from "../../app/hooks";
import { Badge, personKana, personName } from "../../components/ui";
import { employmentTypeLabel } from "../../lib/labels";
import { assignmentKind, PersonDetail } from "./PersonPanel";

/**
 * 選んだ人の要点（フリガナ・所属〔兼務・出向を含む〕・社員番号・雇用区分・拠点）。すべての項目は「詳細」で箱の上に開く（画面を移らない）。
 * 見えるのは閲覧者の公開範囲の項目だけ。report が false なら、詳細に値の誤りの報告を出さない。
 */
export function PersonSummary({ personId, asOf, report = true }: { personId: string; asOf: string; report?: boolean }) {
  const person = usePerson(personId, asOf);
  const dialog = useRef<HTMLDialogElement | null>(null);
  // 箱の外（背景）を押したら閉じる（背景を押すと、対象は <dialog> そのものになる）
  const attachDialog = useCallback((d: HTMLDialogElement | null) => {
    dialog.current = d;
    if (!d) return;
    const onClick = (e: MouseEvent) => e.target === d && d.close();
    d.addEventListener("click", onClick);
    return () => d.removeEventListener("click", onClick);
  }, []);
  const p = person.data;
  if (!p) return null;
  const kana = personKana(p);
  const assignments = [...(p.assignments ?? [])].sort((a, b) => (a.type === "primary" ? -1 : 0) - (b.type === "primary" ? -1 : 0));
  return (
    <section aria-label="人の詳細" className="flex flex-col gap-2 border-t border-border/60 pt-3">
      <div className="flex items-center gap-2">
        {kana && <span className="min-w-0 flex-1 truncate text-xs text-muted">{kana}</span>}
        <button type="button" onClick={() => dialog.current?.showModal()} className="ml-auto shrink-0 rounded-sm border border-border bg-surface px-2 py-0.5 text-xs hover:bg-surface-2">
          詳細
        </button>
      </div>
      {assignments.length > 0 && (
        <ul className="m-0 flex list-none flex-col gap-1 p-0">
          {assignments.map((a) => {
            const kind = assignmentKind(a);
            return (
              <li key={`${a.org_unit_id}:${a.type}:${a.valid_from}`} className="flex flex-wrap items-center gap-1.5 text-xs">
                <Badge tone={kind.tone}>{kind.label}</Badge>
                <span className="font-bold">{a.org_unit_name}</span>
                {a.position_title && <span className="text-muted">{a.position_title}</span>}
              </li>
            );
          })}
        </ul>
      )}
      {/* 社員番号・雇用区分・拠点（見えない・無い項目は出さない） */}
      <dl className="m-0 grid grid-cols-[4.5em_1fr] gap-x-2 gap-y-0.5 text-xs">
        {p.employee_number && (
          <>
            <dt className="text-muted">社員番号</dt>
            <dd className="m-0 tabular">{p.employee_number}</dd>
          </>
        )}
        {p.employment_type && (
          <>
            <dt className="text-muted">雇用区分</dt>
            <dd className="m-0">{employmentTypeLabel(p.employment_type)}</dd>
          </>
        )}
        {p.site && (
          <>
            <dt className="text-muted">拠点</dt>
            <dd className="m-0">{p.site.name}</dd>
          </>
        )}
      </dl>
      <dialog
        ref={attachDialog}
        aria-label={`${personName(p)}の詳細`}
        className="m-auto max-h-[85dvh] w-[min(32rem,calc(100vw-16px))] overflow-y-auto rounded-lg border border-border bg-surface p-4 text-sm text-fg shadow-2xl backdrop:bg-black/40"
      >
        <div className="mb-2 flex justify-end">
          <button type="button" onClick={() => dialog.current?.close()} className="rounded-sm border border-border px-2 py-0.5 text-xs hover:bg-surface-2">
            閉じる
          </button>
        </div>
        <PersonDetail person={p} report={report} />
      </dialog>
    </section>
  );
}
