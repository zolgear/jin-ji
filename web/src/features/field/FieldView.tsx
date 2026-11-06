// 組織の変更の画面（#/changes）: 編集（計画として保存・変更を反映）・計画・反映の記録・グループ・管理のタブ（ADR-0013・0014）。タブは URL の tab に持つ。
import { useCallback, useState } from "react";
import { useSession } from "../../auth/session";
import { useAdapter } from "../../data/adapterContext";
import { managesAllOrgs, type Group } from "../../data/field";
import { useQuery } from "../../data/query";
import { useRoute } from "../../lib/router";
import { AdminTab } from "./AdminTab";
import { GroupsTab } from "./GroupsTab";
import { EditTab } from "./EditTab";
import { PlansTab } from "./PlansTab";
import { RegistrationsTab } from "./RegistrationsTab";

const TABS = [
  { id: "edit", label: "編集" },
  { id: "plans", label: "計画" },
  { id: "registrations", label: "反映の記録" },
  { id: "groups", label: "グループ" },
  { id: "admin", label: "管理" },
] as const;

type TabId = (typeof TABS)[number]["id"];

export function FieldView() {
  const { adapter } = useAdapter();
  const { route, navigate } = useRoute();
  const session = useSession();
  const raw = route.params.get("tab");
  const tab: TabId = TABS.some((t) => t.id === raw) ? (raw as TabId) : "edit";
  const [groupsVersion, setGroupsVersion] = useState(() => Date.now());
  const reloadGroups = useCallback(async () => setGroupsVersion(Date.now()), []);
  const groupsQuery = useQuery(adapter.field, `groups:${groupsVersion}`, () => adapter.field.groups());
  const groups: Group[] | null = groupsQuery.data ?? (groupsQuery.error !== undefined ? [] : null);
  // デモは全社の範囲を持つ利用者として動く
  const [plansVersion, setPlansVersion] = useState(() => Date.now());
  const reloadPlans = useCallback(async () => setPlansVersion(Date.now()), []);
  const plansQuery = useQuery(adapter.field, `plans:${plansVersion}`, () => adapter.field.plans());
  const canManage = adapter.kind === "demo" || managesAllOrgs(session?.session);

  return (
    <div className="flex h-full flex-col">
      <div role="tablist" aria-label="組織の変更" className="flex gap-1 border-b border-border px-4 pt-3">
        {TABS.map((t) => (
          <button
            key={t.id}
            type="button"
            role="tab"
            aria-selected={t.id === tab}
            onClick={() => navigate({ params: { tab: t.id === "edit" ? null : t.id } })}
            className="-mb-px rounded-t-md border border-transparent px-4 py-2 text-sm font-bold text-muted aria-selected:border-border aria-selected:border-b-surface aria-selected:bg-surface aria-selected:text-fg"
          >
            {t.label}
          </button>
        ))}
      </div>
      {/* 編集は組織図の上で組み立てるので、余白を付けず画面の高さいっぱいに使う。編集中の変更はタブを移っても drafts に残る */}
      <div role="tabpanel" className="min-h-0 flex-1 overflow-y-auto">
        {tab === "edit" && <EditTab groups={groups ?? []} plans={plansQuery.data ?? (plansQuery.error !== undefined ? [] : null)} onPlansChanged={reloadPlans} />}
        {tab === "plans" && <PlansTab plans={plansQuery.data ?? null} error={plansQuery.error} />}
        {tab === "registrations" && <RegistrationsTab />}
        {tab === "groups" && (
          <div className="p-4">
            <GroupsTab groups={groups} onChanged={reloadGroups} />
          </div>
        )}
        {tab === "admin" && (
          <div className="p-4">
            <AdminTab canManage={canManage} />
          </div>
        )}
      </div>
    </div>
  );
}
