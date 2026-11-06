// 反映の記録のタブ: 編集から反映した変更（直接登録）の一覧（自分の・同意した・自分の範囲の）と、確定・同意・取り消し・破棄。
// 範囲・管理元の判定は API が行う（範囲の外は 403）。AI エージェントの下書きは、名義の人が確定したときだけ反映される。
import { useCallback, useState } from "react";
import { Button, Notice } from "../../auth/ui";
import { Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { REGISTRATION_STATUS_LABEL, type FieldRegistration, type RegistrationAction } from "../../data/field";
import { useQuery } from "../../data/query";
import { describeError } from "../../data/types";

const ACTIONS: Partial<Record<FieldRegistration["status"], Array<{ action: RegistrationAction; label: string }>>> = {
  draft: [
    { action: "confirm", label: "確定" },
    { action: "discard", label: "破棄" },
  ],
  pending_consent: [
    { action: "consent", label: "同意" },
    { action: "discard", label: "破棄" },
  ],
  applied: [{ action: "withdraw", label: "取り消し" }],
};

export function RegistrationsTab() {
  const { adapter } = useAdapter();
  const field = adapter.field;
  const [mine, setMine] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const [version, setVersion] = useState(() => Date.now());
  const reload = useCallback(async () => setVersion(Date.now()), []);
  const query = useQuery(field, `registrations:${mine}:${version}`, () => field.registrations({ mine }));
  const list = query.data ?? null;

  const act = async (r: FieldRegistration, action: RegistrationAction, label: string) => {
    if (!window.confirm(`この変更を${label}しますか。`)) return;
    setBusy(r.id);
    setMessage(null);
    try {
      await field.transition(r.id, action);
      setMessage({ tone: "ok", text: `${label}しました。` });
      await reload();
    } catch (e) {
      setMessage({ tone: "error", text: describeError(e) });
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="flex flex-col gap-4 p-4">
      <section aria-label="反映の記録" className="flex flex-col gap-2">
        <div className="flex items-center gap-3">
          <h3 className="m-0 text-sm font-bold">反映の記録</h3>
          <label className="flex items-center gap-1 text-sm">
            <input type="checkbox" checked={mine} onChange={(e) => setMine(e.target.checked)} />
            自分の分だけ
          </label>
        </div>
        {message && <Notice tone={message.tone === "ok" ? "ok" : "error"}>{message.text}</Notice>}
        {query.error !== undefined && <Notice tone="error">{describeError(query.error)}</Notice>}
        {list === null ? (
          <Spinner />
        ) : list.length === 0 ? (
          <p className="m-0 text-sm text-muted">反映した変更はまだありません。</p>
        ) : (
          <ul className="m-0 flex list-none flex-col gap-2 p-0">
            {list.map((r) => (
              <li key={r.id} className="flex flex-wrap items-center gap-2 rounded-md border border-border px-3 py-2 text-sm">
                <span className="rounded-sm bg-surface-2 px-1.5 text-xs font-bold">{REGISTRATION_STATUS_LABEL[r.status]}</span>
                <span>有効日 {r.effective_date}</span>
                <span className="text-muted">{r.person_ids.length} 人</span>
                {r.registered_via === "agent" && <span className="text-xs text-muted">AI エージェントの下書き</span>}
                {r.note && <span className="min-w-0 flex-1 truncate text-muted">{r.note}</span>}
                <span className="ml-auto flex gap-1">
                  {(ACTIONS[r.status] ?? [])
                    .filter((a) => a.action !== "withdraw" || r.effective_date > adapter.businessDate)
                    .map((a) => (
                      <Button key={a.action} variant={a.action === "discard" || a.action === "withdraw" ? "danger" : "secondary"} busy={busy === r.id} onClick={() => void act(r, a.action, a.label)}>
                        {a.label}
                      </Button>
                    ))}
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
