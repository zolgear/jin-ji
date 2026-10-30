// 素案のタブ: 一覧・作成・変更の組み立て・シミュレーション（今と素案の組織図）・申請版・承認の記録・登録・提案資料。
// 素案は公開されない作業領域で、保存しても公開されている組織・人の情報は変わらない（ADR-0013 決定 8・9）。
import { useCallback, useMemo, useState } from "react";
import { Button, Notice } from "../../auth/ui";
import { HelpTip } from "../../components/popover";
import { Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import type { FieldDownload, Group, Plan, PlanChange, PlanInput, PlanSimulation, PlanVersion } from "../../data/field";
import { useQuery } from "../../data/query";
import { describeError } from "../../data/types";
import { afterNodes, beforeNodes, PlanChart, ProposalDocument } from "./PlanChart";
import { ChangeAdder, ChangeList, describeChange, inputClass, PersonPicker, useNamers, useOrgUnits } from "./shared";

function toChanges(list: Plan["changes"]): PlanChange[] {
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

export function PlansTab({ groups }: { groups: Group[] }) {
  const { adapter } = useAdapter();
  const field = adapter.field;
  const [selected, setSelected] = useState<string | "new" | null>(null);
  const [version, setVersion] = useState(() => Date.now());
  const reload = useCallback(async () => setVersion(Date.now()), []);
  const query = useQuery(field, `plans:${version}`, () => field.plans());
  const plans = query.data ?? null;
  const error = query.error ? describeError(query.error) : null;

  const plan = plans?.find((p) => p.id === selected) ?? null;
  return (
    <div className="grid gap-4 lg:grid-cols-[260px_1fr]">
      <nav aria-label="素案の一覧" className="flex flex-col gap-2">
        <Button variant="primary" onClick={() => setSelected("new")}>
          新しい素案
        </Button>
        {error && <Notice tone="error">{error}</Notice>}
        {plans === null ? (
          <Spinner />
        ) : (
          <ul className="m-0 flex list-none flex-col gap-1 p-0">
            {plans.map((p) => (
              <li key={p.id}>
                <button
                  type="button"
                  aria-current={p.id === selected ? "true" : undefined}
                  onClick={() => setSelected(p.id)}
                  className="w-full rounded-md border border-border px-3 py-2 text-left text-sm hover:bg-surface-2 aria-[current=true]:border-accent aria-[current=true]:bg-accent-weak"
                >
                  <span className="block font-bold">{p.title}</span>
                  <span className="text-xs text-muted">
                    {p.effective_date}・{p.changes.length} 件
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </nav>
      {selected === null ? (
        <p className="m-0 text-sm text-muted">素案を選ぶか、新しく作ってください。</p>
      ) : selected !== "new" && !plan ? (
        <Spinner />
      ) : (
        <PlanEditor
          key={selected}
          plan={plan}
          groups={groups}
          onSaved={async (id) => {
            await reload();
            setSelected(id);
          }}
          onDeleted={async () => {
            await reload();
            setSelected(null);
          }}
        />
      )}
    </div>
  );
}

function PlanEditor({ plan, groups, onSaved, onDeleted }: { plan: Plan | null; groups: Group[]; onSaved: (id: string) => Promise<void>; onDeleted: () => Promise<void> }) {
  const { adapter } = useAdapter();
  const field = adapter.field;
  const [draft, setDraft] = useState<PlanInput>(() =>
    plan
      ? { title: plan.title, note: plan.note, base_date: plan.base_date, effective_date: plan.effective_date, changes: toChanges(plan.changes) }
      : { title: "", note: null, base_date: adapter.businessDate, effective_date: adapter.businessDate, changes: [] },
  );
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const [simulation, setSimulation] = useState<{ sim: PlanSimulation; version: PlanVersion | null } | null>(null);
  const [proposal, setProposal] = useState<{ sim: PlanSimulation; version: PlanVersion | null } | null>(null);
  const created = useMemo(
    () => draft.changes.filter((c) => c.kind === "org_create" && c.target_id).map((c) => ({ id: c.target_id!, name: c.values.name ?? "" })),
    [draft.changes],
  );
  const names = useNamers(draft.base_date, draft.changes, groups, created);
  const baseUnits = useOrgUnits(simulation?.sim.base_date ?? draft.base_date).map((x) => x.unit);
  const dirty = !plan || JSON.stringify(toChanges(plan.changes)) !== JSON.stringify(draft.changes) || plan.title !== draft.title || plan.base_date !== draft.base_date || plan.effective_date !== draft.effective_date || (plan.note ?? null) !== draft.note;

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

  const save = () =>
    run(async () => {
      const saved = plan ? await field.updatePlan(plan.id, draft) : await field.createPlan(draft);
      await onSaved(saved.id);
    }, "保存しました。");

  const simulate = (version: PlanVersion | null) =>
    run(async () => {
      if (!plan) return;
      setSimulation({ sim: await field.simulate(plan.id, version?.version_no ?? null), version });
    });

  return (
    <section aria-label="素案" className="flex min-w-0 flex-col gap-4">
      <div className="grid gap-3 sm:grid-cols-2 sm:items-end">
        <label className="flex flex-col gap-1 text-sm sm:col-span-2">
          <span className="text-muted">題名</span>
          <input className={inputClass} value={draft.title} onChange={(e) => setDraft({ ...draft, title: e.target.value })} maxLength={200} />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          <span className="flex items-center gap-1 text-muted">
            基準日
            <HelpTip label="基準日の説明">この日の組織と所属の上に、素案の変更を重ねて見ます。</HelpTip>
          </span>
          <input type="date" className={inputClass} value={draft.base_date} onChange={(e) => setDraft({ ...draft, base_date: e.target.value })} />
        </label>
        <label className="flex flex-col gap-1 text-sm">
          <span className="text-muted">予定の有効日</span>
          <input type="date" className={inputClass} value={draft.effective_date} onChange={(e) => setDraft({ ...draft, effective_date: e.target.value })} />
        </label>
        <label className="flex flex-col gap-1 text-sm sm:col-span-2">
          <span className="text-muted">メモ</span>
          <textarea className={inputClass} rows={2} value={draft.note ?? ""} onChange={(e) => setDraft({ ...draft, note: e.target.value || null })} maxLength={2000} />
        </label>
      </div>

      <div className="flex flex-col gap-2">
        <h3 className="m-0 text-sm font-bold">変更</h3>
        <ChangeList
          changes={draft.changes}
          describe={(c) => describeChange(c, names)}
          onRemove={(i) => setDraft({ ...draft, changes: draft.changes.filter((_, j) => j !== i) })}
        />
        <ChangeAdder asOf={draft.base_date} created={created} groups={groups} allowGroups onAdd={(c) => setDraft({ ...draft, changes: [...draft.changes, c] })} />
      </div>

      {message && <Notice tone={message.tone === "ok" ? "ok" : "error"}>{message.text}</Notice>}
      <div className="flex flex-wrap gap-2">
        <Button variant="primary" busy={busy} disabled={!draft.title.trim() || !dirty} onClick={save}>
          保存
        </Button>
        {plan && (
          <>
            <Button busy={busy} disabled={dirty} onClick={() => simulate(null)}>
              組織図で見る
            </Button>
            <Button
              busy={busy}
              disabled={dirty || draft.changes.length === 0}
              onClick={() =>
                run(async () => {
                  await field.freeze(plan.id);
                  await loadVersions();
                }, "申請版にしました。")
              }
            >
              申請版にする
            </Button>
            <Button busy={busy} disabled={dirty} onClick={() => run(async () => download(await field.changesCsv(plan.id, null)))}>
              CSV
            </Button>
            <Button
              variant="danger"
              busy={busy}
              onClick={() =>
                run(async () => {
                  if (!window.confirm("この素案を消しますか（申請版も消えます）。")) return;
                  await field.deletePlan(plan.id);
                  await onDeleted();
                })
              }
            >
              削除
            </Button>
          </>
        )}
      </div>

      {plan && <ShareBox plan={plan} asOf={draft.base_date} onShared={() => onSaved(plan.id)} />}

      {simulation && (
        <section aria-label="素案を適用した組織" className="flex flex-col gap-3">
          <div className="flex items-center gap-2">
            <h3 className="m-0 text-sm font-bold">{simulation.version ? `申請版 ${simulation.version.version_no} の組織` : "素案を適用した組織"}</h3>
            <Button variant="ghost" onClick={() => setProposal(simulation)}>
              提案資料を開く
            </Button>
          </div>
          {simulation.sim.warnings.length > 0 && (
            <Notice tone="warn">
              {simulation.sim.warnings.map((w) => (w.index >= 0 ? `変更 ${w.index + 1}: ${w.message}` : w.message)).join(" / ")}
            </Notice>
          )}
          <div className="grid gap-3 xl:grid-cols-2">
            <div className="overflow-x-auto rounded-md border border-border p-2">
              <PlanChart title="今の組織" nodes={beforeNodes(baseUnits, simulation.sim)} />
            </div>
            <div className="overflow-x-auto rounded-md border border-border p-2">
              <PlanChart title="素案の組織" nodes={afterNodes(simulation.sim)} />
            </div>
          </div>
        </section>
      )}

      {plan && versions.length > 0 && (
        <section aria-label="申請版" className="flex flex-col gap-2">
          <h3 className="m-0 flex items-center gap-1 text-sm font-bold">
            申請版
            <HelpTip label="申請版の説明">
              申請版は素案を固定したものです。社内のワークフローで申請し、承認されたら承認日と申請番号を記録してから登録します。
            </HelpTip>
          </h3>
          {versions.map((v) => (
            <VersionRow
              key={v.id}
              plan={plan}
              version={v}
              busy={busy}
              onSimulate={() => simulate(v)}
              onProposal={() =>
                run(async () => {
                  setProposal({ sim: await field.simulate(plan.id, v.version_no), version: v });
                })
              }
              onChanged={loadVersions}
              run={run}
            />
          ))}
        </section>
      )}

      {proposal && plan && (
        <ProposalDocument
          title={plan.title}
          version={proposal.version}
          baseUnits={baseUnits}
          simulation={proposal.sim}
          changes={toChanges(proposal.version?.changes ?? plan.changes).map((c) => describeChange(c, names))}
          onCsv={() => void field.changesCsv(plan.id, proposal.version?.version_no ?? null).then(download)}
          onClose={() => setProposal(null)}
        />
      )}
    </section>
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
                if (!window.confirm("承認された申請版の変更を、あなたの範囲で登録します。よろしいですか。")) return;
                await adapter.field.registerPlan(plan.id, version.version_no);
              }, "登録しました。相手側の組織の同意が要る分は、登録のタブで状態を確かめられます。")
            }
          >
            登録する
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
