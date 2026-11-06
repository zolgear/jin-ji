// 登録のタブ: 直接登録の一覧（自分の登録・同意した登録・自分の範囲の登録）と、確定・同意・取り消し・破棄、新しい登録（素案と同じボードで組み立てる）。
// 範囲・管理元の判定は API が行う（範囲の外は 403）。AI エージェントの下書きは、名義の人が確定したときだけ反映される。
import { useCallback, useState } from "react";
import { Button, Notice } from "../../auth/ui";
import { HelpTip } from "../../components/popover";
import { Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { REGISTRATION_STATUS_LABEL, type FieldRegistration, type Group, type PlanChange, type RegistrationAction } from "../../data/field";
import { useQuery } from "../../data/query";
import { describeError } from "../../data/types";
import { BoardSidebar } from "./board/BoardSidebar";
import { PlanBoard, type BoardSelection } from "./board/PlanBoard";
import { useBoard } from "./board/useBoard";
import { inputClass } from "./shared";

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

export function RegistrationsTab({ groups }: { groups: Group[] }) {
  const { adapter } = useAdapter();
  const field = adapter.field;
  const [mine, setMine] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [composing, setComposing] = useState(false);

  const [version, setVersion] = useState(() => Date.now());
  const reload = useCallback(async () => setVersion(Date.now()), []);
  const query = useQuery(field, `registrations:${mine}:${version}`, () => field.registrations({ mine }));
  const list = query.data ?? null;

  const act = async (r: FieldRegistration, action: RegistrationAction, label: string) => {
    if (!window.confirm(`この登録を${label}しますか。`)) return;
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

  if (composing) {
    return (
      <NewRegistration
        groups={groups}
        onClose={() => setComposing(false)}
        onRegistered={async (text) => {
          setComposing(false);
          setMessage({ tone: "ok", text });
          await reload();
        }}
      />
    );
  }
  return (
    <div className="flex flex-col gap-4 p-4">
      <div>
        <Button variant="primary" onClick={() => setComposing(true)}>
          新しく登録する
        </Button>
      </div>
      <section aria-label="登録の一覧" className="flex flex-col gap-2">
        <div className="flex items-center gap-3">
          <h3 className="m-0 text-sm font-bold">登録</h3>
          <label className="flex items-center gap-1 text-sm">
            <input type="checkbox" checked={mine} onChange={(e) => setMine(e.target.checked)} />
            自分の登録だけ
          </label>
        </div>
        {message && <Notice tone={message.tone === "ok" ? "ok" : "error"}>{message.text}</Notice>}
        {query.error !== undefined && <Notice tone="error">{describeError(query.error)}</Notice>}
        {list === null ? (
          <Spinner />
        ) : list.length === 0 ? (
          <p className="m-0 text-sm text-muted">登録はまだありません。</p>
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

function NewRegistration({ groups, onClose, onRegistered }: { groups: Group[]; onClose: () => void; onRegistered: (text: string) => Promise<void> }) {
  const { adapter } = useAdapter();
  const [changes, setChanges] = useState<PlanChange[]>([]);
  const [date, setDate] = useState(adapter.businessDate);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selection, setSelection] = useState<BoardSelection | null>(null);
  const board = useBoard(date, changes);
  const register = async () => {
    setBusy(true);
    setError(null);
    try {
      const r = await adapter.field.register({ effective_date: date, note: note.trim() || null, changes });
      await onRegistered(r.status === "pending_consent" ? "相手側の組織の同意待ちにしました。" : r.status === "draft" ? "下書きにしました。" : "登録しました。");
    } catch (e) {
      setError(describeError(e));
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="flex flex-wrap items-end gap-x-3 gap-y-2 border-b border-border bg-surface px-3 py-2">
        <Button variant="ghost" onClick={onClose}>
          ← 一覧
        </Button>
        <label className="flex flex-col gap-0.5 text-xs">
          <span className="flex items-center gap-1 text-muted">
            有効日
            <HelpTip label="登録の説明">
              範囲（組織長・指名された担当・組織管理者）の中の変更は、承認を経ずにすぐ反映されます。部をまたぐ異動は、相手側の組織の範囲を持つ人の同意で反映されます。有効日の前なら取り消せます。
            </HelpTip>
          </span>
          <input type="date" className={inputClass} value={date} min={adapter.businessDate} onChange={(e) => setDate(e.target.value)} />
        </label>
        <label className="flex min-w-48 flex-1 flex-col gap-0.5 text-xs">
          <span className="text-muted">メモ</span>
          <input className={inputClass} value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} />
        </label>
        <Button variant="primary" busy={busy} disabled={changes.length === 0} onClick={() => void register()}>
          登録する
        </Button>
      </div>
      {error && (
        <Notice tone="error" className="m-2">
          {error}
        </Notice>
      )}
      <div className="min-h-0 flex-1">
        <PlanBoard
          data={board}
          changes={changes}
          onChange={setChanges}
          selection={selection}
          onSelect={setSelection}
          aside={<BoardSidebar data={board} changes={changes} onChange={setChanges} selection={selection} onSelect={setSelection} groups={groups} allowGroups={false} />}
        />
      </div>
    </div>
  );
}
