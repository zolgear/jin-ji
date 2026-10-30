// 管理のタブ: 領域ごとの管理元（起案 → 別の人の承認）と、組織の担当の指名（ADR-0013 決定 2・3）。
// 管理元の起案・承認は人事管理者・組織管理者だけ、担当の指名は組織長としての範囲を持つ人（API が判定する）。
import { useCallback, useState } from "react";
import { Button, Notice } from "../../auth/ui";
import { HelpTip } from "../../components/popover";
import { Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { DOMAIN_LABEL, MODE_LABEL, type FieldDomain, type FieldGrant, type FieldManagementMode, type FieldMode, type FieldSetting } from "../../data/field";
import { useQuery } from "../../data/query";
import { describeError } from "../../data/types";
import { inputClass, OrgSelect, PersonPicker, useNamers } from "./shared";

export function AdminTab({ canManage }: { canManage: boolean }) {
  const { adapter } = useAdapter();
  const field = adapter.field;
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const [version, setVersion] = useState(() => Date.now());
  const reload = useCallback(async () => setVersion(Date.now()), []);
  const query = useQuery(field, `field-admin:${version}`, () => Promise.all([field.modes(), field.settings(), field.grants()]));
  const [modes, settings, grants] = query.data ?? [null, [] as FieldSetting[], [] as FieldGrant[]];
  const run = async (action: () => Promise<unknown>, ok: string) => {
    setMessage(null);
    try {
      await action();
      setMessage({ tone: "ok", text: ok });
      await reload();
    } catch (e) {
      setMessage({ tone: "error", text: describeError(e) });
    }
  };
  if (!modes) return query.error !== undefined ? <Notice tone="error">{describeError(query.error)}</Notice> : <Spinner />;
  const pending = settings.filter((s) => s.status === "proposed");
  return (
    <div className="flex flex-col gap-6">
      {message && <Notice tone={message.tone === "ok" ? "ok" : "error"}>{message.text}</Notice>}
      <section aria-label="管理元" className="flex flex-col gap-2">
        <h3 className="m-0 flex items-center gap-1 text-sm font-bold">
          管理元
          <HelpTip label="管理元の説明">
            人事パッケージ: 取込だけで変わります。jin-ji（現場）: 範囲を持つ人の登録だけで変わり、取込の差分は「人事パッケージとの差」になります。両方: どちらでも変わります。変えるには、起案した人とは別の人の承認が要ります。
          </HelpTip>
        </h3>
        <table className="w-full max-w-2xl border-collapse text-sm">
          <tbody>
            {modes.map((m) => (
              <ModeRow key={m.domain} mode={m} canManage={canManage} onPropose={(mode) => run(() => field.proposeMode(m.domain, mode, null), "起案しました。")} />
            ))}
          </tbody>
        </table>
        {pending.map((s) => (
          <div key={s.id} className="flex flex-wrap items-center gap-2 rounded-md border border-warn-fg/30 bg-warn-bg px-3 py-2 text-sm">
            <span>
              起案: {DOMAIN_LABEL[s.domain]} を {MODE_LABEL[s.mode]} に
            </span>
            {canManage && (
              <span className="ml-auto flex gap-1">
                <Button onClick={() => void run(() => field.decideMode(s.id, true), "承認しました。")}>承認</Button>
                <Button variant="danger" onClick={() => void run(() => field.decideMode(s.id, false), "却下しました。")}>
                  却下
                </Button>
              </span>
            )}
          </div>
        ))}
      </section>
      <Grants grants={grants} onRun={run} />
    </div>
  );
}

function ModeRow({ mode, canManage, onPropose }: { mode: FieldMode; canManage: boolean; onPropose: (mode: FieldManagementMode) => void }) {
  const [next, setNext] = useState<FieldManagementMode>(mode.mode);
  return (
    <tr className="border-b border-border">
      <th scope="row" className="py-2 pr-3 text-left font-normal">
        {DOMAIN_LABEL[mode.domain as FieldDomain]}
      </th>
      <td className="py-2 pr-3 font-bold">{MODE_LABEL[mode.mode]}</td>
      {canManage && (
        <td className="py-2">
          <span className="flex gap-1">
            <select className={inputClass} value={next} onChange={(e) => setNext(e.target.value as FieldManagementMode)} aria-label={`${DOMAIN_LABEL[mode.domain]}の新しい管理元`}>
              {(Object.keys(MODE_LABEL) as FieldManagementMode[]).map((m) => (
                <option key={m} value={m}>
                  {MODE_LABEL[m]}
                </option>
              ))}
            </select>
            <Button disabled={next === mode.mode} onClick={() => onPropose(next)}>
              起案
            </Button>
          </span>
        </td>
      )}
    </tr>
  );
}

function Grants({ grants, onRun }: { grants: FieldGrant[]; onRun: (action: () => Promise<unknown>, ok: string) => Promise<void> }) {
  const { adapter } = useAdapter();
  const [org, setOrg] = useState("");
  const [person, setPerson] = useState("");
  const [until, setUntil] = useState(() => {
    const d = new Date(`${adapter.businessDate}T00:00:00Z`);
    d.setUTCDate(d.getUTCDate() + 90);
    return d.toISOString().slice(0, 10);
  });
  const active = grants.filter((g) => !g.revoked_at);
  const names = useNamers(
    adapter.businessDate,
    active.map((g) => ({ kind: "org_update" as const, target_id: g.org_unit_id, values: { head_person_id: g.person_id } })),
    [],
  );
  return (
    <section aria-label="担当の指名" className="flex flex-col gap-2">
      <h3 className="m-0 flex items-center gap-1 text-sm font-bold">
        担当の指名
        <HelpTip label="担当の指名の説明">組織長は、自分の組織の登録を任せる担当（庶務など）を期間付きで指名できます。担当はさらに別の人を指名できません。</HelpTip>
      </h3>
      <ul className="m-0 flex list-none flex-col gap-1 p-0">
        {active.map((g) => (
          <li key={g.id} className="flex items-center gap-2 rounded-sm border border-border px-3 py-1.5 text-sm">
            <span className="flex-1">
              {names.person(g.person_id)}・{names.org(g.org_unit_id)}
            </span>
            <span className="text-xs text-muted">
              {g.from} 〜 {g.until}
            </span>
            <Button variant="ghost" onClick={() => void onRun(() => adapter.field.revokeGrant(g.id), "取り消しました。")}>
              取り消す
            </Button>
          </li>
        ))}
      </ul>
      <div className="grid gap-2 sm:grid-cols-[1fr_1fr_auto_auto] sm:items-end">
        <OrgSelect label="組織" asOf={adapter.businessDate} value={org} onChange={setOrg} />
        <PersonPicker label="担当にする人" asOf={adapter.businessDate} value={person} onChange={(id) => setPerson(id)} />
        <label className="flex flex-col gap-1 text-sm">
          <span className="text-muted">期間の終わり</span>
          <input type="date" className={inputClass} value={until} onChange={(e) => setUntil(e.target.value)} />
        </label>
        <Button
          disabled={!org || !person}
          onClick={() => void onRun(() => adapter.field.grant({ orgUnitId: org, personId: person, from: adapter.businessDate, until }), "指名しました。")}
        >
          指名
        </Button>
      </div>
    </section>
  );
}
