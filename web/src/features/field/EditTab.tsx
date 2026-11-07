// 編集のタブ: 組織図の上で人・組織をドラッグして変更を組み立て、目的に応じて「計画として保存」か「変更を反映」する。
// 横の固定の欄は持たず、選んだ人・組織や変更の一覧・メモ・申請と共有は図の上に浮かべる箱で開く（図を広く使う）。
// 開いている計画は URL の plan に、組織図で選んだ組織から下だけを描くときはその組織を root に持つ（無ければ新しい編集・全体）。保存・反映の前の変更は drafts に覚え、タブを移っても消さない。
// 計画は公開されない作業領域で、保存しても公開されている組織・人の情報は変わらない（ADR-0013 決定 8・9）。
// 反映は範囲の中の直接登録（部をまたぐ異動は相手側の同意で反映）。申請版・承認の記録・共有・提案資料は、図の上の帯から行う。
import { useCallback, useEffect, useId, useMemo, useState, type ReactNode } from "react";
import { Button, Notice } from "../../auth/ui";
import { HelpTip, Popover } from "../../components/popover";
import { Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import type { FieldDownload, Group, Plan, PlanChange, PlanInput, PlanSimulation, PlanVersion } from "../../data/field";
import { useQuery } from "../../data/query";
import { describeError } from "../../data/types";
import { useRoute } from "../../lib/router";
import { FloatingCard } from "../../components/FloatingCard";
import { BoardSearch, ChangesList, SelectionCard } from "./board/BoardPanels";
import { PlanBoard, type BoardSelection } from "./board/PlanBoard";
import { isWithin } from "./board/model";
import { useBoard } from "./board/useBoard";
import { loadDraft, saveDraft } from "./drafts";
import { ProposalDocument } from "./PlanChart";
import { describeChange, inputClass, PersonPicker, useNamers, useOrgUnits } from "./shared";

export function toChanges(list: Plan["changes"]): PlanChange[] {
  return list.map((c) => ({ kind: c.kind ?? "assignment", target_id: c.target_id ?? null, values: (c.values ?? {}) as Record<string, string | null> }));
}

/** CSV を落とす（API は同一オリジンのリンク、デモは Blob の URL）。 */
function download(file: FieldDownload) {
  const a = document.createElement("a");
  a.href = file.href;
  if (file.fileName) a.download = file.fileName;
  a.click();
  setTimeout(() => file.dispose?.(), 1000);
}

/** 反映（直接登録）で扱えない変更。グループへの加入・脱退は計画でだけ扱う。 */
const NOT_REFLECTABLE = new Set<PlanChange["kind"]>(["group_join", "group_leave"]);

export function EditTab({ groups, plans, onPlansChanged }: { groups: Group[]; plans: Plan[] | null; onPlansChanged: () => Promise<void> }) {
  const { route, navigate } = useRoute();
  const raw = route.params.get("plan");
  const selected = raw && raw !== "new" ? raw : null;
  const plan = plans?.find((p) => p.id === selected) ?? null;
  // 新しい編集を初めて保存したら URL を計画の ID にするが、編集の状態は作り直さない
  const [adopted, setAdopted] = useState<string | null>(null);
  // 反映・取りやめのあとに白紙から始め直す
  const [round, setRound] = useState(0);
  const [flash, setFlash] = useState<string | null>(null);

  if (selected && selected !== adopted && !plan) return plans === null ? <Spinner /> : <Notice tone="error" className="m-4">計画が見つかりません。</Notice>;
  const fresh = () => {
    if (selected) navigate({ params: { plan: null } });
    setAdopted(null);
    setRound((n) => n + 1);
  };
  return (
    <Editor
      key={selected && selected !== adopted ? selected : `new:${round}`}
      plan={plan}
      groups={groups}
      flash={flash}
      onFlash={setFlash}
      onNew={fresh}
      onSaved={async (id) => {
        await onPlansChanged();
        if (id !== selected) {
          setAdopted(id);
          navigate({ params: { plan: id } }, true);
        }
      }}
      onReflected={(text) => {
        setFlash(text);
        fresh();
      }}
      onDeleted={async () => {
        await onPlansChanged();
        fresh();
      }}
    />
  );
}

/** 図の上の操作の帯のボタン（Popover の開くボタンにも同じ見た目を使う）。 */
function toolClass(primary: boolean, disabled: boolean) {
  return `h-8 rounded-sm px-2 text-sm sm:px-3 font-bold whitespace-nowrap ${primary ? "bg-accent text-accent-fg hover:brightness-110" : "hover:bg-surface-2"} ${
    disabled ? "pointer-events-none opacity-40" : ""
  }`;
}

function ToolButton({
  primary = false,
  pressed,
  disabled = false,
  onClick,
  children,
}: {
  primary?: boolean;
  pressed?: boolean;
  disabled?: boolean;
  onClick: () => void;
  children: ReactNode;
}) {
  return (
    <button type="button" aria-pressed={pressed} disabled={disabled} onClick={onClick} className={`${toolClass(primary, false)} disabled:opacity-40 aria-pressed:bg-accent-weak aria-pressed:text-accent`}>
      {children}
    </button>
  );
}

function closePanel(id: string) {
  document.getElementById(id)?.hidePopover();
}

function blank(date: string): PlanInput {
  return { title: "", note: null, base_date: date, effective_date: date, changes: [] };
}

function Editor({
  plan,
  groups,
  flash,
  onFlash,
  onNew,
  onSaved,
  onReflected,
  onDeleted,
}: {
  plan: Plan | null;
  groups: Group[];
  flash: string | null;
  onFlash: (text: string | null) => void;
  onNew: () => void;
  onSaved: (id: string) => Promise<void>;
  onReflected: (text: string) => void;
  onDeleted: () => Promise<void>;
}) {
  const { adapter } = useAdapter();
  const field = adapter.field;
  const { route, navigate } = useRoute();
  const root = route.params.get("root");
  const panelId = useId();
  const savePanel = `${panelId}-save`;
  const reflectPanel = `${panelId}-reflect`;
  const draftKey = plan?.id ?? "new";
  const [draft, setDraft] = useState<PlanInput>(
    () =>
      loadDraft(draftKey) ??
      (plan ? { title: plan.title, note: plan.note, base_date: plan.base_date, effective_date: plan.effective_date, changes: toChanges(plan.changes) } : blank(adapter.businessDate)),
  );
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(flash ? { tone: "ok", text: flash } : null);
  const [selection, setSelection] = useState<BoardSelection | null>(null);
  // 図の上に浮かべる箱: 選んだ人・組織（選んでいる間）か、変更の一覧・メモ・申請と共有のどれか 1 つ
  const [panel, setPanel] = useState<"changes" | "memo" | "plan" | null>(null);
  const [focus, setFocus] = useState<{ unitId: string; n: number } | null>(null);
  const select = (s: BoardSelection | null) => {
    setSelection(s);
    if (s) setPanel(null);
  };
  const togglePanel = (p: "changes" | "memo" | "plan") => {
    setSelection(null);
    setPanel((cur) => (cur === p ? null : p));
  };
  // 見ているもの: 変更後（編集できる）・今の組織・申請版（見るだけ）
  const [viewing, setViewing] = useState<{ label: string; changes: PlanChange[] } | null>(null);
  const [proposal, setProposal] = useState<{ sim: PlanSimulation; version: PlanVersion | null } | null>(null);
  const shownChanges = viewing?.changes ?? draft.changes;
  const board = useBoard(draft.base_date, shownChanges);
  const created = useMemo(() => board.tree.units.filter((u) => u.state === "created").map((u) => ({ id: u.id, name: u.name })), [board.tree]);
  const names = useNamers(draft.base_date, draft.changes, groups, created);
  const baseUnits = useOrgUnits(draft.base_date).map((x) => x.unit);
  /** 探した組織・人を選び、その札まで図を動かす（絞った範囲の外なら全体に戻す）。 */
  const pick = (s: BoardSelection) => {
    select(s);
    const unitId = s.kind === "unit" ? s.unitId : (board.moves.get(s.personId)?.to ?? s.from.org);
    if (!unitId) return;
    if (root && !isWithin(board.tree, unitId, root)) navigate({ params: { root: null } }, true);
    setFocus((f) => ({ unitId, n: (f?.n ?? 0) + 1 }));
  };
  const dirty = plan
    ? JSON.stringify(toChanges(plan.changes)) !== JSON.stringify(draft.changes) ||
      plan.title !== draft.title ||
      plan.base_date !== draft.base_date ||
      plan.effective_date !== draft.effective_date ||
      (plan.note ?? null) !== draft.note
    : draft.changes.length > 0 || draft.title.trim() !== "" || (draft.note ?? "") !== "";
  const setChanges = viewing ? undefined : (changes: PlanChange[]) => setDraft((d) => ({ ...d, changes }));

  useEffect(() => {
    saveDraft(draftKey, dirty ? draft : null);
  }, [draftKey, draft, dirty]);
  useEffect(() => {
    if (flash) onFlash(null);
  }, [flash, onFlash]);

  const [versionsKey, setVersionsKey] = useState(() => Date.now());
  const loadVersions = useCallback(async () => setVersionsKey(Date.now()), []);
  const versionsQuery = useQuery(field, plan ? `plan-versions:${plan.id}:${versionsKey}` : null, () => field.versions(plan!.id));
  const versions = versionsQuery.data ?? [];

  const run = async (action: () => Promise<void>, ok?: string) => {
    setBusy(true);
    setMessage(null);
    try {
      await action();
      if (ok) setMessage({ tone: "ok", text: ok });
    } catch (e) {
      setMessage({ tone: "error", text: describeError(e) });
    } finally {
      setBusy(false);
    }
  };
  const title = draft.title.trim() || "無題の計画";
  /** 計画として保存して ID を返す（変えていなければそのまま）。 */
  const persist = async () => {
    if (plan && !dirty) return plan.id;
    const input = { ...draft, title };
    const saved = plan ? await field.updatePlan(plan.id, input) : await field.createPlan(input);
    if (!plan) saveDraft("new", null);
    await onSaved(saved.id);
    return saved.id;
  };
  const openProposal = (version: PlanVersion | null) =>
    run(async () => {
      const id = await persist();
      setProposal({ sim: await field.simulate(id, version?.version_no ?? null), version });
    });
  const reflectable = draft.changes.length > 0 && !draft.changes.some((c) => NOT_REFLECTABLE.has(c.kind));
  const reflect = () =>
    run(async () => {
      const r = await field.register({ effective_date: draft.effective_date, note: draft.note?.trim() || null, changes: draft.changes });
      saveDraft(draftKey, null);
      onReflected(r.status === "pending_consent" ? "反映しました。相手側の組織の同意待ちの分があります。" : r.status === "draft" ? "下書きにしました。" : "反映しました。");
    });
  const startNew = () => {
    if (!plan && dirty && !window.confirm("編集中の変更を消して、新しく始めますか。")) return;
    if (!plan) saveDraft("new", null);
    onNew();
  };

  return (
    <div className="flex h-full min-h-0 flex-col">
      {/* 題名・基準日・有効日は計画として保存してから出す（それまでは図を広く使う） */}
      {plan && (
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-border bg-surface px-3 py-1.5 text-sm">
          <input
            aria-label="計画の題名"
            className={`${inputClass} min-w-40 flex-1 font-bold`}
            value={draft.title}
            placeholder="無題の計画"
            onChange={(e) => setDraft({ ...draft, title: e.target.value })}
            maxLength={200}
          />
          <label className="flex items-center gap-1">
            <span className="text-xs text-muted">基準日</span>
            <input type="date" className={inputClass} value={draft.base_date} onChange={(e) => setDraft({ ...draft, base_date: e.target.value })} />
            <HelpTip label="基準日の説明">この日の組織と所属の上に、変更を重ねて見ます。</HelpTip>
          </label>
          <label className="flex items-center gap-1">
            <span className="text-xs text-muted">有効日</span>
            <input type="date" className={inputClass} value={draft.effective_date} onChange={(e) => setDraft({ ...draft, effective_date: e.target.value })} />
          </label>
        </div>
      )}
      <div className="min-h-0 flex-1">
        <PlanBoard
          data={board}
          changes={shownChanges}
          onChange={setChanges}
          selection={selection}
          onSelect={select}
          root={root}
          overlay={
            <>
              <div className="pointer-events-none absolute inset-x-3 top-3 flex flex-wrap items-start justify-between gap-2 [&>*]:pointer-events-auto">
                <div className="flex flex-wrap items-center gap-2">
                  <fieldset className="m-0 flex overflow-hidden rounded-md border border-border bg-surface p-0 text-sm shadow-sm">
                    <legend className="sr-only">表示</legend>
                    <button type="button" aria-pressed={!viewing} onClick={() => setViewing(null)} className="h-8 px-3 hover:bg-surface-2 aria-pressed:bg-accent aria-pressed:font-bold aria-pressed:text-accent-fg">
                      変更後
                    </button>
                    <button
                      type="button"
                      aria-pressed={viewing?.changes.length === 0}
                      onClick={() => setViewing({ label: "今の組織", changes: [] })}
                      className="h-8 px-3 hover:bg-surface-2 aria-pressed:bg-accent aria-pressed:font-bold aria-pressed:text-accent-fg"
                    >
                      今の組織
                    </button>
                  </fieldset>
                  {root && board.tree.byId.has(root) && (
                    <span className="flex h-8 items-center gap-1 rounded-md border border-border bg-surface pl-3 text-sm shadow-sm">
                      <span className="font-bold">{board.tree.byId.get(root)!.name}</span>
                      <span className="text-muted">から下</span>
                      <button type="button" aria-label="組織全体を表示" title="組織全体を表示" onClick={() => navigate({ params: { root: null } }, true)} className="h-full px-2 text-muted hover:bg-surface-2 hover:text-fg">
                        ×
                      </button>
                    </span>
                  )}
                  {viewing && viewing.changes.length > 0 && <span className="rounded-md border border-border bg-surface px-2 py-1 text-xs font-bold shadow-sm">{viewing.label}</span>}
                  <BoardSearch data={board} onPick={pick} />
                </div>
                <div className="flex flex-wrap items-center justify-end gap-2">
                <div className="flex items-center gap-0.5 rounded-md border border-border bg-surface p-1 shadow-sm">
                  <ToolButton pressed={panel === "changes" && !selection} onClick={() => togglePanel("changes")}>
                    変更 {shownChanges.length}
                  </ToolButton>
                  <ToolButton pressed={panel === "memo" && !selection} onClick={() => togglePanel("memo")}>
                    メモ{draft.note ? " ●" : ""}
                  </ToolButton>
                  {plan && (
                    <ToolButton pressed={panel === "plan" && !selection} onClick={() => togglePanel("plan")}>
                      申請・共有
                    </ToolButton>
                  )}
                </div>
                <div className="flex items-center gap-0.5 rounded-md border border-border bg-surface p-1 shadow-sm">
                  {plan ? (
                    <ToolButton primary disabled={busy || !dirty} onClick={() => run(async () => void (await persist()), "計画を保存しました。")}>
                      保存
                    </ToolButton>
                  ) : (
                    <Popover id={savePanel} label="計画として保存" trigger="計画として保存" triggerClassName={toolClass(true, !dirty)} width={300}>
                      <div className="flex flex-col gap-2 p-3">
                        <label className="flex flex-col gap-1 text-xs">
                          <span className="text-muted">題名</span>
                          <input className={`${inputClass} font-bold`} value={draft.title} placeholder="無題の計画" onChange={(e) => setDraft({ ...draft, title: e.target.value })} maxLength={200} />
                        </label>
                        <Button
                          variant="primary"
                          busy={busy}
                          disabled={!dirty}
                          onClick={() =>
                            run(async () => {
                              closePanel(savePanel);
                              await persist();
                            }, "計画を保存しました。")
                          }
                        >
                          保存
                        </Button>
                      </div>
                    </Popover>
                  )}
                  <Popover id={reflectPanel} label="変更を反映" trigger="変更を反映" triggerClassName={toolClass(false, !reflectable)} width={300}>
                    <div className="flex flex-col gap-2 p-3">
                      <label className="flex flex-col gap-1 text-xs">
                        <span className="flex items-center gap-1 text-muted">
                          有効日
                          <HelpTip label="反映の説明">
                            範囲（組織長・指名された担当・組織管理者）の中の変更は、有効日にそのまま反映されます。部をまたぐ異動は、相手側の組織の範囲を持つ人の同意で反映されます。有効日の前なら「反映の記録」から取り消せます。グループへの加入・脱退は計画でだけ扱います。
                          </HelpTip>
                        </span>
                        <input type="date" className={inputClass} value={draft.effective_date} min={adapter.businessDate} onChange={(e) => setDraft({ ...draft, effective_date: e.target.value })} />
                      </label>
                      <Button
                        variant="primary"
                        busy={busy}
                        disabled={!reflectable || draft.effective_date < adapter.businessDate}
                        onClick={() => {
                          closePanel(reflectPanel);
                          reflect();
                        }}
                      >
                        {draft.changes.length} 件を反映する
                      </Button>
                    </div>
                  </Popover>
                  <ToolButton disabled={busy || draft.changes.length === 0} onClick={() => openProposal(null)}>
                    提案資料
                  </ToolButton>
                  <ToolButton disabled={busy} onClick={startNew}>
                    新規
                  </ToolButton>
                </div>
                </div>
              </div>
              {(message || board.error !== undefined) && (
                <div className="absolute bottom-14 left-1/2 flex w-[min(32rem,calc(100%-24px))] -translate-x-1/2 items-start gap-1 shadow-lg">
                  <Notice tone={message?.tone === "ok" ? "ok" : "error"} className="flex-1">
                    {message?.text ?? describeError(board.error)}
                  </Notice>
                  {message && (
                    <button type="button" aria-label="閉じる" onClick={() => setMessage(null)} className="absolute top-1 right-1 h-7 w-7 rounded-sm text-muted hover:bg-surface-2">
                      ×
                    </button>
                  )}
                </div>
              )}
            </>
          }
          focus={focus}
          onBackground={() => {
            setSelection(null);
            setPanel(null);
          }}
          floating={
            selection ? (
              <SelectionCard data={board} changes={shownChanges} onChange={setChanges} selection={selection} onSelect={select} />
            ) : panel === "changes" ? (
              <FloatingCard label="変更" title={`変更 ${shownChanges.length} 件`} onClose={() => setPanel(null)}>
                <ChangesList data={board} changes={shownChanges} onChange={setChanges} groups={groups} />
              </FloatingCard>
            ) : panel === "memo" ? (
              <FloatingCard label="メモ" title="メモ" onClose={() => setPanel(null)}>
                <textarea aria-label="メモ" className={inputClass} rows={6} value={draft.note ?? ""} onChange={(e) => setDraft({ ...draft, note: e.target.value || null })} maxLength={2000} />
              </FloatingCard>
            ) : panel === "plan" && plan ? (
              <FloatingCard label="計画" title={title} onClose={() => setPanel(null)}>
                {(
                  <section aria-label="申請版" className="flex flex-col gap-2">
                    <h3 className="m-0 flex items-center gap-1 text-xs font-bold text-muted">
                      申請版
                      <HelpTip label="申請版の説明">
                        申請版は計画を固定したものです。社内のワークフローで申請し、承認されたら承認日と申請番号を記録してから反映します。
                      </HelpTip>
                    </h3>
                    <div>
                      <Button
                        busy={busy}
                        disabled={draft.changes.length === 0}
                        onClick={() =>
                          run(async () => {
                            await persist();
                            await field.freeze(plan.id);
                            await loadVersions();
                          }, "申請版にしました。")
                        }
                      >
                        申請版にする
                      </Button>
                    </div>
                    {versions.map((v) => (
                      <VersionRow
                        key={v.id}
                        plan={plan}
                        version={v}
                        busy={busy}
                        onSimulate={() => setViewing({ label: `申請版 ${v.version_no}`, changes: toChanges(v.changes) })}
                        onProposal={() => openProposal(v)}
                        onChanged={loadVersions}
                        run={run}
                      />
                    ))}
                  </section>
                )}
                {<ShareBox plan={plan} asOf={draft.base_date} onShared={() => onSaved(plan.id)} />}
                {(
                  <div className="flex flex-wrap gap-1">
                    <Button variant="ghost" busy={busy} onClick={() => run(async () => download(await field.changesCsv(await persist(), null)))}>
                      CSV
                    </Button>
                    <Button
                      variant="danger"
                      busy={busy}
                      onClick={() =>
                        run(async () => {
                          if (!window.confirm("この計画を消しますか（申請版も消えます）。")) return;
                          await field.deletePlan(plan.id);
                          saveDraft(plan.id, null);
                          await onDeleted();
                        })
                      }
                    >
                      計画を削除
                    </Button>
                  </div>
                )}
              </FloatingCard>
            ) : null
          }
        />
      </div>

      {proposal && (
        <ProposalDocument
          title={title}
          version={proposal.version}
          baseUnits={baseUnits}
          simulation={proposal.sim}
          changes={(proposal.version ? toChanges(proposal.version.changes) : draft.changes).map((c) => describeChange(c, names))}
          onCsv={() => plan && void field.changesCsv(plan.id, proposal.version?.version_no ?? null).then(download)}
          onClose={() => setProposal(null)}
        />
      )}
    </div>
  );
}

function VersionRow({
  plan,
  version,
  busy,
  onSimulate,
  onProposal,
  onChanged,
  run,
}: {
  plan: Plan;
  version: PlanVersion;
  busy: boolean;
  onSimulate: () => void;
  onProposal: () => void;
  onChanged: () => Promise<void>;
  run: (action: () => Promise<void>, ok?: string) => Promise<void>;
}) {
  const { adapter } = useAdapter();
  const [approvedOn, setApprovedOn] = useState(adapter.businessDate);
  const [reference, setReference] = useState("");
  return (
    <div className="flex flex-col gap-2 rounded-md border border-border p-3 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-bold">版 {version.version_no}</span>
        <span className="text-muted">{version.changes.length} 件・有効日 {version.effective_date}</span>
        {version.approved_on ? (
          <span className="rounded-sm border border-accent/40 bg-accent-weak px-1.5 text-xs font-bold text-accent">
            承認 {version.approved_on}
            {version.approval_reference ? `（${version.approval_reference}）` : ""}
          </span>
        ) : (
          <span className="text-xs text-muted">未承認</span>
        )}
        <span className="ml-auto flex flex-wrap gap-1">
          <Button variant="ghost" onClick={onSimulate}>
            組織図
          </Button>
          <Button variant="ghost" onClick={onProposal}>
            提案資料
          </Button>
        </span>
      </div>
      {version.approved_on ? (
        <div>
          <Button
            variant="primary"
            busy={busy}
            onClick={() =>
              run(async () => {
                if (!window.confirm("承認された申請版の変更を、あなたの範囲で反映します。よろしいですか。")) return;
                await adapter.field.registerPlan(plan.id, version.version_no);
              }, "反映しました。相手側の組織の同意が要る分は、反映の記録のタブで状態を確かめられます。")
            }
          >
            反映する
          </Button>
        </div>
      ) : (
        <div className="flex flex-wrap items-end gap-2">
          <label className="flex flex-col gap-1">
            <span className="text-xs text-muted">承認日</span>
            <input type="date" className={inputClass} value={approvedOn} onChange={(e) => setApprovedOn(e.target.value)} />
          </label>
          <label className="flex flex-col gap-1">
            <span className="text-xs text-muted">申請番号</span>
            <input className={inputClass} value={reference} onChange={(e) => setReference(e.target.value)} maxLength={100} />
          </label>
          <Button
            busy={busy}
            onClick={() =>
              run(async () => {
                await adapter.field.recordApproval(plan.id, version.version_no, approvedOn, reference.trim() || null);
                await onChanged();
              }, "承認を記録しました。")
            }
          >
            承認を記録
          </Button>
        </div>
      )}
    </div>
  );
}

function ShareBox({ plan, asOf, onShared }: { plan: Plan; asOf: string; onShared: () => Promise<void> }) {
  const { adapter } = useAdapter();
  const [person, setPerson] = useState("");
  const [error, setError] = useState<string | null>(null);
  const names = useNamers(asOf, plan.shared_with.map((id) => ({ kind: "assignment" as const, target_id: id, values: {} })), []);
  const share = async (ids: string[]) => {
    setError(null);
    try {
      await adapter.field.sharePlan(plan.id, ids);
      setPerson("");
      await onShared();
    } catch (e) {
      setError(describeError(e));
    }
  };
  return (
    <details className="rounded-md border border-border p-3 text-sm">
      <summary className="cursor-pointer font-bold">共有（{plan.shared_with.length} 人）</summary>
      <div className="mt-2 flex flex-col gap-2">
        <ul className="m-0 flex list-none flex-wrap gap-1 p-0">
          {plan.shared_with.map((id) => (
            <li key={id} className="flex items-center gap-1 rounded-sm bg-surface-2 px-2">
              {names.person(id)}
              <Button variant="ghost" onClick={() => void share(plan.shared_with.filter((x) => x !== id))}>
                外す
              </Button>
            </li>
          ))}
        </ul>
        <div className="flex items-end gap-2">
          <div className="min-w-0 flex-1">
            <PersonPicker label="共有する人" asOf={asOf} value={person} onChange={(id) => setPerson(id)} />
          </div>
          <Button disabled={!person} onClick={() => void share([...plan.shared_with, person])}>
            共有する
          </Button>
        </div>
        {error && <Notice tone="error">{error}</Notice>}
      </div>
    </details>
  );
}
