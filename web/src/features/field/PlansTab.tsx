// 計画のタブ: 保存した計画の一覧。開くと編集のタブで続きを組み立てる（URL の plan）。
import { Button, Notice } from "../../auth/ui";
import { Spinner } from "../../components/ui";
import type { Plan } from "../../data/field";
import { describeError } from "../../data/types";
import { useRoute } from "../../lib/router";
import { hasDraft } from "./drafts";

export function PlansTab({ plans, error }: { plans: Plan[] | null; error: unknown }) {
  const { navigate } = useRoute();
  const open = (id: string | null) => navigate({ params: { tab: null, plan: id } });
  return (
    <div className="flex flex-col gap-3 p-4">
      <div>
        <Button variant="primary" onClick={() => open(null)}>
          新規
        </Button>
      </div>
      {error !== undefined && <Notice tone="error">{describeError(error)}</Notice>}
      {plans === null ? (
        <Spinner />
      ) : plans.length === 0 ? (
        <p className="m-0 text-sm text-muted">計画はまだありません。</p>
      ) : (
        <ul className="m-0 grid list-none gap-2 p-0 sm:grid-cols-2 xl:grid-cols-3">
          {plans.map((p) => (
            <li key={p.id}>
              <button
                type="button"
                onClick={() => open(p.id)}
                className="flex w-full flex-col gap-1 rounded-md border border-border bg-surface px-4 py-3 text-left hover:border-accent hover:bg-surface-2"
              >
                <span className="flex items-center gap-2 font-bold">
                  {p.title}
                  {hasDraft(p.id) && <span className="rounded-sm bg-warn-bg px-1.5 text-xs text-warn-fg">未保存の変更</span>}
                </span>
                <span className="text-xs text-muted">
                  有効日 {p.effective_date}・変更 {p.changes.length} 件
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
