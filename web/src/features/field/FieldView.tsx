// 組織の変更の画面（#/changes）: 素案（提案資料つき）・登録・グループ・管理のタブ（ADR-0013・0014）。タブは URL の tab に持つ。
import { useCallback, useState } from "react";
import { useSession } from "../../auth/session";
import { useAdapter } from "../../data/adapterContext";
import { managesAllOrgs, type Group } from "../../data/field";
import { useQuery } from "../../data/query";
import { useRoute } from "../../lib/router";
import { AdminTab } from "./AdminTab";
import { GroupsTab } from "./GroupsTab";
import { PlansTab } from "./PlansTab";
import { RegistrationsTab } from "./RegistrationsTab";

const TABS = [
  { id: "plans", label: "素案" },
  { id: "registrations", label: "登録" },
  { id: "groups", label: "グループ" },
  { id: "admin", label: "管理" },
] as const;

type TabId = (typeof TABS)[number]["id"];

export function FieldView() {
  const { adapter } = useAdapter();
  const { route, navigate } = useRoute();
  const session = useSession();
  const raw = route.params.get("tab");
  const tab: TabId = TABS.some((t) => t.id === raw) ? (raw as TabId) : "plans";
  const [groupsVersion, setGroupsVersion] = useState(() => Date.now());
  const reloadGroups = useCallback(async () => setGroupsVersion(Date.now()), []);
  const groupsQuery = useQuery(adapter.field, `groups:${groupsVersion}`, () => adapter.field.groups());
  const groups: Group[] | null = groupsQuery.data ?? (groupsQuery.error !== undefined ? [] : null);
  // デモは全社の範囲を持つ利用者として動く
  const canManage = adapter.kind === "demo" || managesAllOrgs(session?.session);

  return (
    <div className="flex h-full flex-col overflow-y-auto">
      <div role="tablist" aria-label="組織の変更" className="flex gap-1 border-b border-border px-4 pt-3">
        {TABS.map((t) => (
          <button
            key={t.id}
            type="button"
            role="tab"
            aria-selected={t.id === tab}
            onClick={() => navigate({ params: { tab: t.id === "plans" ? null : t.id } })}
            className="-mb-px rounded-t-md border border-transparent px-4 py-2 text-sm font-bold text-muted aria-selected:border-border aria-selected:border-b-surface aria-selected:bg-surface aria-selected:text-fg"
          >
            {t.label}
          </button>
        ))}
      </div>
      <div role="tabpanel" className="p-4">
        {tab === "plans" && <PlansTab groups={groups ?? []} />}
        {tab === "registrations" && <RegistrationsTab groups={groups ?? []} />}
        {tab === "groups" && <GroupsTab groups={groups} onChanged={reloadGroups} />}
        {tab === "admin" && <AdminTab canManage={canManage} />}
      </div>
    </div>
  );
}
