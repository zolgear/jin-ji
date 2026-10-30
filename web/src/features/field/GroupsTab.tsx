// グループのタブ: チーム・プロジェクトなど規程外の単位の一覧・作成・メンバー（ADR-0014）。
// 変えられるのはオーナー・所管組織の範囲を持つ人・組織管理者・人事管理者（API が判定する）。メンバーは自分を外せる。
import { useEffect, useState } from "react";
import { Button, Notice } from "../../auth/ui";
import { Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { GROUP_KIND_LABEL, GROUP_ROLE_LABEL, type Group, type GroupDetail, type GroupKind, type GroupRole } from "../../data/field";
import { describeError } from "../../data/types";
import { inputClass, PersonPicker, usePeopleNames } from "./shared";

export function GroupsTab({ groups, onChanged }: { groups: Group[] | null; onChanged: () => Promise<void> }) {
  const [selected, setSelected] = useState<string | null>(null);
  return (
    <div className="grid gap-4 lg:grid-cols-[260px_1fr]">
      <nav aria-label="グループの一覧" className="flex flex-col gap-2">
        <NewGroup onCreated={async (id) => (await onChanged(), setSelected(id))} />
        {groups === null ? (
          <Spinner />
        ) : (
          <ul className="m-0 flex list-none flex-col gap-1 p-0">
            {groups.map((g) => (
              <li key={g.id}>
                <button
                  type="button"
                  aria-current={g.id === selected ? "true" : undefined}
                  onClick={() => setSelected(g.id)}
                  className="w-full rounded-md border border-border px-3 py-2 text-left text-sm hover:bg-surface-2 aria-[current=true]:border-accent aria-[current=true]:bg-accent-weak"
                >
                  <span className="block font-bold">{g.name}</span>
                  <span className="text-xs text-muted">
                    {GROUP_KIND_LABEL[g.kind]}・{g.member_count} 人{g.visibility === "members" ? "・メンバーだけ" : ""}
                    {g.managed ? "・管理対象" : ""}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </nav>
      {selected ? <GroupPanel key={selected} id={selected} onChanged={onChanged} /> : <p className="m-0 text-sm text-muted">グループを選ぶか、新しく作ってください。</p>}
    </div>
  );
}

function NewGroup({ onCreated }: { onCreated: (id: string) => Promise<void> }) {
  const { adapter } = useAdapter();
  const [name, setName] = useState("");
  const [kind, setKind] = useState<GroupKind>("team");
  const [membersOnly, setMembersOnly] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const create = async () => {
    setError(null);
    try {
      const detail = await adapter.field.createGroup({ name: name.trim(), kind, owner_org_unit_id: null, visibility: membersOnly ? "members" : "all" });
      setName("");
      await onCreated(detail.group.id);
    } catch (e) {
      setError(describeError(e));
    }
  };
  return (
    <details className="rounded-md border border-border p-2 text-sm">
      <summary className="cursor-pointer font-bold">新しいグループ</summary>
      <div className="mt-2 flex flex-col gap-2">
        <input className={inputClass} placeholder="名前" value={name} onChange={(e) => setName(e.target.value)} maxLength={200} aria-label="グループの名前" />
        <select className={inputClass} value={kind} onChange={(e) => setKind(e.target.value as GroupKind)} aria-label="種別">
          {(Object.keys(GROUP_KIND_LABEL) as GroupKind[]).map((k) => (
            <option key={k} value={k}>
              {GROUP_KIND_LABEL[k]}
            </option>
          ))}
        </select>
        <label className="flex items-center gap-1">
          <input type="checkbox" checked={membersOnly} onChange={(e) => setMembersOnly(e.target.checked)} />
          メンバーだけに見せる
        </label>
        {error && <Notice tone="error">{error}</Notice>}
        <Button variant="primary" disabled={!name.trim()} onClick={() => void create()}>
          作る
        </Button>
      </div>
    </details>
  );
}

function GroupPanel({ id, onChanged }: { id: string; onChanged: () => Promise<void> }) {
  const { adapter } = useAdapter();
  const [detail, setDetail] = useState<GroupDetail | null>(null);
  const [person, setPerson] = useState("");
  const [role, setRole] = useState<GroupRole>("member");
  const [error, setError] = useState<string | null>(null);
  const names = usePeopleNames(detail?.members.map((m) => m.person_id) ?? [], adapter.businessDate);
  useEffect(() => {
    let cancelled = false;
    adapter.field.group(id).then(
      (d) => !cancelled && setDetail(d),
      (e: unknown) => !cancelled && setError(describeError(e)),
    );
    return () => {
      cancelled = true;
    };
  }, [adapter, id]);
  const run = async (action: () => Promise<GroupDetail>) => {
    setError(null);
    try {
      setDetail(await action());
      await onChanged();
    } catch (e) {
      setError(describeError(e));
    }
  };
  if (!detail) return error ? <Notice tone="error">{error}</Notice> : <Spinner />;
  return (
    <section aria-label={detail.group.name} className="flex flex-col gap-3">
      <h3 className="m-0 text-base font-bold">{detail.group.name}</h3>
      <p className="m-0 text-sm text-muted">
        {GROUP_KIND_LABEL[detail.group.kind]}・{detail.group.visibility === "members" ? "メンバーだけに公開" : "全社に公開"}
        {detail.group.managed ? "・アクセス権の管理の対象" : ""}
      </p>
      <ul className="m-0 flex list-none flex-col gap-1 p-0">
        {detail.members.map((m) => (
          <li key={m.person_id} className="flex items-center gap-2 rounded-sm border border-border px-3 py-1.5 text-sm">
            <span className="flex-1">{names.get(m.person_id) ?? "—"}</span>
            <span className="text-xs text-muted">{GROUP_ROLE_LABEL[m.role]}</span>
            <Button variant="ghost" onClick={() => void run(() => adapter.field.removeMember(id, m.person_id))}>
              外す
            </Button>
          </li>
        ))}
      </ul>
      <div className="flex flex-wrap items-end gap-2">
        <div className="min-w-48 flex-1">
          <PersonPicker label="加える人" asOf={adapter.businessDate} value={person} onChange={(p) => setPerson(p)} />
        </div>
        <select className={inputClass} value={role} onChange={(e) => setRole(e.target.value as GroupRole)} aria-label="役割">
          {(Object.keys(GROUP_ROLE_LABEL) as GroupRole[]).map((r) => (
            <option key={r} value={r}>
              {GROUP_ROLE_LABEL[r]}
            </option>
          ))}
        </select>
        <Button disabled={!person} onClick={() => void run(async () => (await adapter.field.addMember(id, person, role)) as GroupDetail).then(() => setPerson(""))}>
          加える
        </Button>
      </div>
      {error && <Notice tone="error">{error}</Notice>}
    </section>
  );
}
