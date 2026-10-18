// 3D 表示の中の「建物・フロア」モード（URL の mode=floor。仕様 §5.2・§5.5）。
//
// 段: 拠点（建物を並べる）→ 建物（分解した階の積層）→ 階（選んだ階の詳細）→ 人を探す（その席へカメラが移る）。
// 2D の簡易フロアマップ（plan=1）は同じ画面で切り替える（3D と並べると、右の詳細のパネルと合わせて 3 列になり狭いため）。
// WebGL が使えない環境では、3D の代わりに 2D の平面図を出す。
// 席は人の決まった席・持ち場の割り当てだけ。タイムマシンで見た日の割り当てが、その日の所在になる。
// 外部から届いた今日の所在（§5.7）は、提供元があって今日を表示しているときだけ重ねる（過去・将来の日には出さない）。
import { Component, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useOrgTree, usePerson } from "../../app/hooks";
import { useNav } from "../../app/nav";
import { HelpTip } from "../../components/popover";
import { ErrorNote, Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { useQuery } from "../../data/query";
import { describeError, type Building, type FloorSummary, type Person } from "../../data/types";
import { workplaceOf } from "../../data/workplace";
import { PALETTE } from "../overview3d/common3d";
import { ChartToolbar, ChartToolbarEnd, ToolbarButton } from "../orgchart/ChartToolbar";
import { useResolvedTheme } from "../../lib/theme";
import { personName } from "../../components/ui";
import {
  useBuildings,
  useFloorLayout,
  useFloorLayouts,
  useFloorOccupants,
  useFloorPresence,
  useFloorRoute,
} from "./floorHooks";
import { isLowEndDevice, placeBuildings, stackFloors } from "./floorLayout3d";
import { buildFloorView, groupResolver, presenceOverlayOn, slotColorVar, type FloorView } from "./floorModel";
import { FloorPlan2D } from "./FloorPlan2D";
import { FloorScene3D, type FloorLabel, type SceneBuilding } from "./FloorScene3D";
import { FloorHowToRead, ModeSwitch, PRESENCE_NOTE } from "./FloorParts";

/** WebGL が使えないときに、代わりの表示（2D の平面図）を出す。 */
class WebGlFallback extends Component<{ fallback: ReactNode; children: ReactNode }, { failed: boolean }> {
  override state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  override render() {
    return this.state.failed ? this.props.fallback : this.props.children;
  }
}

const selectClass = "h-8 max-w-[11em] rounded-md border border-border bg-surface px-2 text-[13px]";

/** 階の数字（割り当て / 席数）。 */
function floorNumbers(f: FloorSummary): string {
  return f.seat_count === 0 ? "—" : `${f.occupied_count}/${f.seat_count}`;
}

/** 左上の階の一覧（3D と同じことを違う粒度で示す。押すとその階を選ぶ）。 */
function FloorList({ building, selectedId, onSelect }: { building: Building; selectedId: string | null; onSelect: (id: string) => void }) {
  const floors = [...building.floors].sort((a, b) => b.level_order - a.level_order);
  return (
    <nav aria-label={`${building.name}の階`} className="pointer-events-auto absolute top-3 left-3 z-10 hidden w-[200px] rounded-lg border border-border bg-surface/95 py-1.5 text-[12px] shadow-sm md:block">
      <p className="m-0 flex items-center justify-between px-3 pb-1 text-[11px] text-muted">
        <span className="font-bold text-fg">{building.name}の階</span>
        <span>割り当て / 席数</span>
      </p>
      <ul className="m-0 max-h-[55dvh] list-none overflow-y-auto p-0">
        {floors.map((f) => {
          const current = f.id === selectedId;
          const ratio = f.seat_count > 0 ? f.occupied_count / f.seat_count : 0;
          return (
            <li key={f.id}>
              <button
                type="button"
                aria-current={current ? "true" : undefined}
                onClick={() => onSelect(f.id)}
                className={`relative grid w-full grid-cols-[2.6em_1fr_auto] items-center gap-2 px-3 py-[3px] text-left hover:bg-surface-2 ${current ? "bg-accent-weak font-bold" : ""}`}
              >
                {current && <span aria-hidden="true" className="absolute top-0.5 bottom-0.5 left-0 w-[3px] rounded-r-full bg-accent" />}
                <span className="tabular">{f.label}</span>
                <span aria-hidden="true" className="h-[3px] rounded-full bg-border">
                  {f.seat_count > 0 && <span className="block h-full rounded-full bg-muted" style={{ width: `${Math.round(ratio * 100)}%` }} />}
                </span>
                <span className="tabular text-muted">{floorNumbers(f)}</span>
              </button>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}

/** 帯の色の凡例（色を付けた組織と「その他」）。 */
function OrgLegend({ view }: { view: FloorView }) {
  const colored = view.legend.filter((e) => e.slot !== null);
  const others = view.legend.length - colored.length;
  if (view.legend.length === 0) return null;
  return (
    <span className="flex items-center gap-x-3 text-[12px]" aria-label="色の凡例">
      {colored.map((e) => (
        <span key={e.group.id} className="inline-flex items-center gap-1">
          <span aria-hidden="true" className="size-2.5 rounded-full" style={{ background: slotColorVar(e.slot) ?? undefined }} />
          {e.group.name}
        </span>
      ))}
      {others > 0 && (
        <span className="inline-flex items-center gap-1 text-muted">
          <span aria-hidden="true" className="size-2.5 rounded-full" style={{ background: slotColorVar(null) ?? undefined }} />
          その他
        </span>
      )}
    </span>
  );
}

/** 人を探して、その席へ移る（見える人・見える項目だけで照合する API の検索）。 */
function PersonFinder({ onPick }: { onPick: (p: Person) => void }) {
  const { adapter } = useAdapter();
  const { asOf } = useNav();
  const [q, setQ] = useState("");
  const [open, setOpen] = useState(false);
  const [debounced, setDebounced] = useState("");
  useEffect(() => {
    const t = window.setTimeout(() => setDebounced(q.trim()), 250);
    return () => window.clearTimeout(t);
  }, [q]);
  const results = useQuery(adapter, debounced ? `floor-find:${asOf}:${debounced}` : null, () => adapter.searchPeople(debounced, { asOf, limit: 8 }));
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const close = (e: PointerEvent) => {
      if (!box.current?.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener("pointerdown", close);
    return () => document.removeEventListener("pointerdown", close);
  }, []);
  const items = results.data?.items ?? [];
  return (
    <div ref={box} className="relative">
      <input
        type="search"
        value={q}
        placeholder="人を探す"
        aria-label="人を探して席を表示"
        onChange={(e) => {
          setQ(e.target.value);
          setOpen(true);
        }}
        onFocus={() => setOpen(true)}
        onKeyDown={(e) => {
          if (e.key === "Escape") setOpen(false);
          if (e.key === "Enter" && items[0]) {
            onPick(items[0]);
            setOpen(false);
          }
        }}
        className="h-8 w-[10.5em] rounded-md border border-border bg-surface px-2 text-[13px]"
      />
      {open && debounced && (
        <ul className="fixed z-40 mt-1 max-h-[50dvh] w-[17em] list-none overflow-y-auto rounded-lg border border-border bg-surface p-1 text-[13px] whitespace-normal shadow-xl">
          {results.loading && items.length === 0 && (
            <li className="px-2 py-1.5">
              <Spinner />
            </li>
          )}
          {!results.loading && items.length === 0 && <li className="px-2 py-1.5 text-muted">見つかりません</li>}
          {items.map((p) => {
            const { workplace, seat } = workplaceOf(p);
            return (
              <li key={p.id}>
                <button
                  type="button"
                  onClick={() => {
                    onPick(p);
                    setOpen(false);
                  }}
                  className="flex w-full flex-col items-start rounded-md px-2 py-1 text-left hover:bg-surface-2"
                >
                  <span className="font-bold">{personName(p)}</span>
                  <span className="text-[11px] text-muted">
                    {workplace ? `${workplace.building_name} ${workplace.floor_label}${seat ? `・${seat.code}` : ""}` : "席の登録なし"}
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

export function FloorMode() {
  const { adapter } = useAdapter();
  const { asOf, openPerson } = useNav();
  const route = useFloorRoute();
  const theme = useResolvedTheme();
  const palette = PALETTE[theme];
  const tree = useOrgTree(asOf);
  const sites = useQuery(adapter, `sites:${asOf}`, () => adapter.listSites({ asOf }));
  const [fitRequest, setFitRequest] = useState(0);
  const [narrow, setNarrow] = useState(() => window.innerWidth < 1024);
  useEffect(() => {
    const onResize = () => setNarrow(window.innerWidth < 1024);
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, []);

  // 拠点: URL か、本社（無ければ最初の拠点）
  const siteList = sites.data ?? [];
  const siteId = route.siteId && siteList.some((s) => s.id === route.siteId) ? route.siteId : (siteList.find((s) => s.type === "head_office") ?? siteList[0])?.id ?? null;
  const buildings = useBuildings(siteId, asOf);
  const buildingList = useMemo(() => buildings.data ?? [], [buildings.data]);
  const building = buildingList.find((b) => b.id === route.buildingId) ?? (buildingList.length === 1 ? buildingList[0]! : null);
  const floor = building?.floors.find((f) => f.id === route.floorId) ?? null;
  const floorId = floor?.id ?? null;

  // 人を選んだら、その人の席の拠点・建物・階へ移る（人が変わったときだけ。その後に階を選び直せる）
  const person = usePerson(route.personId, asOf);
  const applied = useRef<string | null>(null);
  useEffect(() => {
    if (!route.personId || !person.data || applied.current === `${route.personId}|${asOf}`) return;
    applied.current = `${route.personId}|${asOf}`;
    const { workplace } = workplaceOf(person.data);
    if (workplace && workplace.floor_id !== route.floorId) {
      route.set({ site: workplace.site_id, building: workplace.building_id, floor: workplace.floor_id });
    }
  }, [route, person.data, asOf]);

  // 表示する建物（建物を選んでいなければ拠点のすべての建物）と、その階の配置図
  const shown = useMemo(() => (building ? [building] : buildingList), [building, buildingList]);
  const floorIds = useMemo(() => shown.flatMap((b) => b.floors.map((f) => f.id)), [shown]);
  const layouts = useFloorLayouts(floorIds, asOf);
  const layout = useFloorLayout(floorId, asOf);
  const occupants = useFloorOccupants(floorId, asOf);
  const isToday = asOf === adapter.businessDate;
  const presence = useFloorPresence(isToday ? floorId : null, isToday ? adapter.businessDate : null);
  const presenceOn = presenceOverlayOn(asOf, adapter.businessDate, Boolean(presence.data), route.presenceParam);
  const groupOf = useMemo(() => groupResolver(tree.data), [tree.data]);
  const view = useMemo(() => {
    if (!layout.data || layout.data.floor_id !== floorId || !occupants.data) return null;
    return buildFloorView({ layout: layout.data, occupants: occupants.data, presence: presenceOn ? (presence.data ?? null) : null, groupOf, monochrome: route.monochrome });
  }, [layout.data, occupants.data, presence.data, presenceOn, groupOf, route.monochrome, floorId]);

  const sceneBuildings = useMemo<SceneBuilding[]>(() => {
    const sizes = new Map<string, { width: number; depth: number }>();
    for (const b of shown) {
      const l = b.floors.map((f) => layouts.get(f.id)).find((x) => x);
      if (!l) continue;
      const xs = l.outline.map((p) => p[0]);
      const ys = l.outline.map((p) => p[1]);
      sizes.set(b.id, { width: (Math.max(...xs) - Math.min(...xs)) / 1000, depth: (Math.max(...ys) - Math.min(...ys)) / 1000 });
    }
    return placeBuildings(shown, sizes).map((p) => ({
      ...p,
      stack: stackFloors(p.building.floors, floorId, p.depth, !building && shown.length > 1),
    }));
  }, [shown, layouts, floorId, building]);

  const floorLabels = useMemo(() => {
    const m = new Map<string, FloorLabel>();
    for (const b of shown) for (const f of b.floors) m.set(f.id, { text: f.label, detail: floorNumbers(f) });
    return m;
  }, [shown]);
  const buildingLabels = useMemo(() => {
    const m = new Map<string, FloorLabel>();
    for (const b of shown) {
      const seats = b.floors.reduce((s, f) => s + f.seat_count, 0);
      m.set(b.id, { text: b.name, detail: `${b.floors.length}階・${seats}席` });
    }
    return m;
  }, [shown]);
  const lowEnd = useMemo(() => isLowEndDevice(navigator as Navigator & { deviceMemory?: number }, null), []);

  const selectFloor = (id: string | null) => route.set({ floor: id, building: building?.id ?? null }, false);
  const pickPerson = (p: Person) => {
    applied.current = null;
    openPerson(p.id);
  };
  const plan = route.plan;
  const sheet = narrow && Boolean(route.personId);

  const planView = view ? (
    <FloorPlan2D view={view} title={`${building?.name ?? ""} ${floor?.label ?? ""}`} selectedPersonId={route.personId} onSeat={(s) => s.people[0] && openPerson(s.people[0].personId)} className="size-full" />
  ) : (
    <div className="p-4 text-muted">{floorId ? <Spinner /> : "階を選ぶと平面図を表示します。"}</div>
  );

  const error = sites.error ?? buildings.error ?? layout.error;
  return (
    <div className="flex size-full flex-col">
      <ChartToolbar>
        <ModeSwitch floor onChange={route.setMode} />
        <span className="flex items-center gap-1.5">
          <select aria-label="拠点" className={selectClass} value={siteId ?? ""} onChange={(e) => route.set({ site: e.target.value, building: null, floor: null }, false)}>
            {siteList.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
          {buildingList.length > 0 && (
            <select aria-label="建物" className={selectClass} value={building?.id ?? ""} onChange={(e) => route.set({ building: e.target.value || null, floor: null }, false)}>
              {buildingList.length > 1 && <option value="">すべての建物</option>}
              {buildingList.map((b) => (
                <option key={b.id} value={b.id}>
                  {b.name}
                </option>
              ))}
            </select>
          )}
          {building && (
            <select aria-label="階" className={selectClass} value={floorId ?? ""} onChange={(e) => selectFloor(e.target.value || null)}>
              <option value="">すべての階</option>
              {[...building.floors].reverse().map((f) => (
                <option key={f.id} value={f.id}>
                  {f.label}
                  {f.seat_count > 0 ? `（${f.occupied_count}/${f.seat_count}）` : ""}
                </option>
              ))}
            </select>
          )}
        </span>
        <span className="flex items-center gap-1.5">
          <button
            type="button"
            aria-pressed={!route.monochrome}
            onClick={() => route.set({ color: route.monochrome ? null : "none" })}
            className={`inline-flex h-8 items-center gap-1 rounded-md border px-3 ${!route.monochrome ? "border-accent bg-accent text-accent-fg" : "border-border hover:bg-surface-2"}`}
          >
            組織で色分け
          </button>
          <button
            type="button"
            aria-pressed={plan}
            disabled={!floorId}
            onClick={() => route.set({ plan: plan ? null : "1" }, false)}
            className={`inline-flex h-8 items-center gap-1 rounded-md border px-3 disabled:opacity-50 ${plan ? "border-accent bg-accent text-accent-fg" : "border-border hover:bg-surface-2"}`}
          >
            平面図
          </button>
          {/* 今日の所在（今日を表示していて、提供元があるときだけ切り替えられる） */}
          {isToday && Boolean(presence.data) && (
            <button
              type="button"
              aria-pressed={presenceOn}
              onClick={() => route.set({ presence: presenceOn ? "0" : null })}
              className={`inline-flex h-8 items-center gap-1 rounded-md border px-3 ${presenceOn ? "border-accent bg-accent text-accent-fg" : "border-border hover:bg-surface-2"}`}
            >
              今日の所在
            </button>
          )}
        </span>
        {view && !route.monochrome && <OrgLegend view={view} />}
        <PersonFinder onPick={pickPerson} />
        {(buildings.loading || layout.loading || occupants.loading) && <Spinner />}
        <ChartToolbarEnd>
          <ToolbarButton
            onClick={() => {
              if (route.personId) openPerson(null);
              if (floorId) selectFloor(null);
              setFitRequest((n) => n + 1);
            }}
          >
            全体を表示
          </ToolbarButton>
          <HelpTip label="図の見方" width={340}>
            <FloorHowToRead presenceShown={presenceOn} />
          </HelpTip>
        </ChartToolbarEnd>
      </ChartToolbar>
      <div className="relative min-h-0 flex-1">
        <p id="floor3d-help" className="sr-only">
          建物・フロアの 3D 表示。階を離して積み、選んだ階の席を組織の色の人の形・空席の輪・使えない席の斜線で示します。ほかの階は埋まり具合を灰の点で示します。
          キーボードでは左上の階の一覧と、「平面図」の席の一覧（表）から辿れます。{presenceOn ? PRESENCE_NOTE : ""}
        </p>
        {error ? (
          <div className="p-4">
            <ErrorNote>建物・階の情報を取得できませんでした: {describeError(error)}</ErrorNote>
          </div>
        ) : buildings.data && buildingList.length === 0 ? (
          <p className="p-6 text-muted">この拠点には建物・階が登録されていません。</p>
        ) : plan ? (
          planView
        ) : sceneBuildings.length > 0 ? (
          <WebGlFallback
            fallback={
              <div className="flex size-full flex-col">
                <p className="m-0 border-b border-border bg-surface px-4 py-2 text-xs text-muted">3D 表示を使えない環境のため、平面図で表示しています。</p>
                {planView}
              </div>
            }
          >
            <FloorScene3D
              theme={theme}
              palette={palette}
              buildings={sceneBuildings}
              layouts={layouts}
              detail={view}
              selectedFloorId={floorId}
              selectedPersonId={route.personId}
              floorLabels={floorLabels}
              buildingLabels={buildingLabels}
              lowEnd={lowEnd}
              fitRequest={fitRequest}
              sheet={sheet}
              onSelectFloor={(id) => selectFloor(id)}
              onSelectBuilding={(id) => route.set({ building: id, floor: null }, false)}
              onSelectPerson={(id) => openPerson(id)}
            />
          </WebGlFallback>
        ) : (
          <div className="p-4">
            <Spinner />
          </div>
        )}
        {building && !plan && <FloorList building={building} selectedId={floorId} onSelect={(id) => selectFloor(id === floorId ? null : id)} />}
      </div>
    </div>
  );
}
