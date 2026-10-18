import { lazy, Suspense, useEffect, useRef } from "react";
import { DemoBadge, NavRail, VIEW_LABEL } from "./app/NavRail";
import { NavProvider, useNav } from "./app/nav";
import { TimeMachine } from "./components/TimeMachine";
import { SessionGate } from "./auth/session";
import { Spinner } from "./components/ui";
import { AdapterProvider, DEMO_AVAILABLE, useAdapter, type AdapterChoice } from "./data/adapterContext";
import { CorrectionsView } from "./features/corrections/CorrectionsView";
import { OrgOutline } from "./features/org/OrgOutline";
import { OrgPanel } from "./features/org/OrgPanel";
import { OrgChartView } from "./features/orgchart/OrgChartView";
import { PersonPanel } from "./features/person/PersonPanel";
import { SearchView } from "./features/search/SearchView";
import { SettingsView } from "./features/settings/SettingsView";
import { parseHash } from "./lib/router";
import { useFloorRoute } from "./features/floor/floorHooks";

// three.js は大きいので 3D の画面を開いたときに読む
const Overview3D = lazy(() => import("./features/overview3d/Overview3D").then((m) => ({ default: m.Overview3D })));

function SidePanel() {
  const { view, orgId, personId, openPerson, selectOrg } = useNav();
  const { floorMode } = useFloorRoute();
  const panelRef = useRef<HTMLElement>(null);
  // 3D の組織モードで何も選んでいないときは組織の一覧を出す（建物・フロアモードでは階の一覧が図の中にある）
  const open = view !== "settings" && Boolean(personId || orgId || (view === "overview-3d" && !floorMode));
  const close = () => {
    if (personId) openPerson(null);
    else selectOrg(null);
  };
  useEffect(() => {
    if (!personId && !orgId) return;
    const onKey = (e: KeyboardEvent) => {
      // 箱（設定・ヘルプ・タイムマシンなど）が開いているときの Esc は、その箱を閉じるだけにする
      if (e.key === "Escape" && !document.querySelector(":popover-open, dialog[open]")) close();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  });
  if (!open) return null;
  // 3D で何も選んでいないときは、組織の一覧（キーボード操作の代替）を出す
  const outlineOnly = !personId && !orgId;
  return (
    <aside
      ref={panelRef}
      aria-label={personId ? "人の詳細" : orgId ? "組織の詳細" : "組織の一覧"}
      className={`z-20 overflow-y-auto border-border bg-surface p-4 lg:static lg:block lg:w-[400px] lg:shrink-0 lg:border-l ${
        outlineOnly ? "hidden" : `fixed inset-0 top-auto ${personId ? "max-h-[75dvh]" : "max-h-[55dvh]"} rounded-t-lg border-t shadow-2xl lg:max-h-none lg:rounded-none lg:shadow-none`
      }`}
    >
      {personId ? (
        <PersonPanel key={personId} personId={personId} onClose={() => openPerson(null)} />
      ) : orgId ? (
        <OrgPanel key={orgId} orgId={orgId} onClose={() => selectOrg(null)} />
      ) : (
        <OrgOutline />
      )}
    </aside>
  );
}

/** 上の帯: 画面の名前・スマホではデモの札・右端にタイムマシン（修正依頼の画面は日付を持たない）。
 *  タイムマシンは過去を見るためのまれな機能なので、主役の画面名から離して右端に置く。 */
function TopBar() {
  const { view } = useNav();
  // 修正依頼と設定は、タイムマシンの日付を持たない
  const dated = view !== "corrections" && view !== "settings";
  return (
    <header className="flex min-h-14 shrink-0 items-center gap-x-4 gap-y-2 border-b border-border bg-surface px-4 py-2 md:px-5">
      {/* スマホでは下のタブが現在地を示すので、日付のある画面では名前を読み上げだけにして幅を空ける */}
      <h1 className={`m-0 shrink-0 text-base font-bold ${dated ? "max-sm:sr-only" : ""}`}>{VIEW_LABEL[view]}</h1>
      <DemoBadge className={`md:hidden ${dated ? "" : "ml-auto"}`} />
      {dated && <TimeMachine className="ml-auto" />}
    </header>
  );
}

function Shell() {
  const { view } = useNav();
  return (
    <div className="flex h-dvh flex-col md:flex-row">
      <a href="#main" className="sr-only focus:not-sr-only focus:absolute focus:z-50 focus:bg-surface focus:p-2">
        本文へ移動
      </a>
      {/* 画面の切り替えと設定: md 以上は左のレール、スマホは下のタブ（order で下へ） */}
      <NavRail />
      <div className="flex min-h-0 min-w-0 flex-1 flex-col">
        <TopBar />
        <main id="main" className="relative flex min-h-0 flex-1">
          <section aria-label={VIEW_LABEL[view]} className="relative min-w-0 flex-1">
            {view === "org-chart" && <OrgChartView />}
            {view === "overview-3d" && (
              <Suspense fallback={<div className="p-4"><Spinner label="3D 表示を準備中" /></div>}>
                <Overview3D />
              </Suspense>
            )}
            {view === "search" && <SearchView />}
            {view === "corrections" && <CorrectionsView />}
            {view === "settings" && <SettingsView />}
          </section>
          <SidePanel />
        </main>
      </div>
    </div>
  );
}

function initialChoice(): AdapterChoice {
  const source = parseHash(window.location.hash).params.get("source");
  if (DEMO_AVAILABLE) {
    return source === "api" || source === "demo-large" ? source : "demo";
  }
  return "api";
}

/** API のときはログインを確かめてから画面を出す（デモはログインなしで動く）。 */
function Gate() {
  const { adapter } = useAdapter();
  return (
    <SessionGate source={adapter.kind}>
      <NavProvider>
        <Shell />
      </NavProvider>
    </SessionGate>
  );
}

export function App() {
  return (
    <AdapterProvider initial={initialChoice()}>
      <Gate />
    </AdapterProvider>
  );
}
