// 建物・フロアモードの URL の状態と取得のフック。
// URL: #/overview-3d?mode=floor&site=&building=&floor=&plan=1&person=&color=none&presence=0（as_of はタイムマシンと共有。仕様 §5.5）
import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import { useAdapter } from "../../data/adapterContext";
import { fetchCached, useQuery } from "../../data/query";
import type { Building, FloorLayout } from "../../data/types";
import { useRoute } from "../../lib/router";

export interface FloorRoute {
  floorMode: boolean;
  siteId: string | null;
  buildingId: string | null;
  floorId: string | null;
  plan: boolean;
  personId: string | null;
  /** 組織で色分け（既定）か単色か。 */
  monochrome: boolean;
  /** URL の presence（"0" なら当日の所在を重ねない）。 */
  presenceParam: string | null;
}

/** 組織モードの引数（建物・フロアモードでは使わない）。 */
const ORG_MODE_KEYS = ["org", "split", "color"];
/** 建物・フロアモードの引数（組織モードに戻すと消す）。 */
export const FLOOR_MODE_KEYS = ["mode", "site", "building", "floor", "plan", "color", "presence"];

export function useFloorRoute() {
  const { route, navigate } = useRoute();
  const p = route.params;
  const state: FloorRoute = {
    floorMode: p.get("mode") === "floor",
    siteId: p.get("site"),
    buildingId: p.get("building"),
    floorId: p.get("floor"),
    plan: p.get("plan") === "1",
    personId: p.get("person"),
    monochrome: p.get("color") === "none",
    presenceParam: p.get("presence"),
  };
  const set = useCallback((change: Record<string, string | null>, replace = true) => navigate({ params: change }, replace), [navigate]);
  /** 組織 / 建物・フロアの切り替え。 */
  const setMode = useCallback(
    (floor: boolean) => {
      const change: Record<string, string | null> = {};
      for (const k of floor ? ORG_MODE_KEYS : FLOOR_MODE_KEYS) change[k] = null;
      if (floor) change.mode = "floor";
      navigate({ params: change });
    },
    [navigate],
  );
  return { ...state, set, setMode };
}

export function useBuildings(siteId: string | null, asOf: string) {
  const { adapter } = useAdapter();
  // 今日の空き席数は予約で変わるので、当日の所在の版もキーに含める
  const version = usePresenceVersion();
  return useQuery(adapter, siteId ? `buildings:${siteId}:${asOf}:${version}` : null, () => adapter.workplace.listBuildings(siteId!, asOf));
}

export function useFloorLayout(floorId: string | null, asOf: string) {
  const { adapter } = useAdapter();
  return useQuery(adapter, floorId ? `layout:${floorId}:${asOf}` : null, () => adapter.workplace.getFloorLayout(floorId!, asOf));
}

export function useFloorOccupants(floorId: string | null, asOf: string) {
  const { adapter } = useAdapter();
  return useQuery(adapter, floorId ? `occupants:${floorId}:${asOf}` : null, () => adapter.workplace.listFloorOccupants(floorId!, asOf));
}

export function usePresenceSettings() {
  const { adapter } = useAdapter();
  return useQuery(adapter, "presence-settings", () => adapter.workplace.getPresenceSettings());
}

// ---- 当日の所在の取り直し ----
// 当日の所在は API が no-store で返す（ETag で使い回さない）。画面の中では予約・取り消しのたびに版を上げ、キーを変えて取り直す。

let presenceVersion = 0;
const listeners = new Set<() => void>();

/** 予約・取り消しの後に呼ぶ（当日の所在と、空き席数を含む階の一覧を取り直す）。 */
export function bumpPresenceVersion(): void {
  presenceVersion++;
  listeners.forEach((l) => l());
}

export function usePresenceVersion(): number {
  return useSyncExternalStore(
    (cb) => {
      listeners.add(cb);
      return () => listeners.delete(cb);
    },
    () => presenceVersion,
    () => presenceVersion,
  );
}

export function useFloorPresence(floorId: string | null, date: string | null) {
  const { adapter } = useAdapter();
  const version = usePresenceVersion();
  return useQuery(adapter, floorId && date ? `presence:${floorId}:${date}:${version}` : null, () => adapter.workplace.getFloorPresence(floorId!, date!));
}

export function useMyPresence(enabled: boolean) {
  const { adapter } = useAdapter();
  const version = usePresenceVersion();
  return useQuery(adapter, enabled ? `my-presence:${version}` : null, () => adapter.workplace.listMyPresence());
}

/** 階の ID から、建物の一覧の中の建物と階。 */
export function findFloor(buildings: readonly Building[] | undefined, floorId: string | null) {
  for (const b of buildings ?? []) {
    const f = b.floors.find((x) => x.id === floorId);
    if (f) return { building: b, floor: f };
  }
  return null;
}

/** 複数の階の配置図（建物の段でほかの階の点を描くため）。取得できた分から返す。 */
export function useFloorLayouts(floorIds: readonly string[], asOf: string): ReadonlyMap<string, FloorLayout> {
  const { adapter } = useAdapter();
  const key = floorIds.join(",");
  const [state, setState] = useState<{ key: string; map: Map<string, FloorLayout> }>({ key: "", map: new Map() });
  useEffect(() => {
    if (!key) return;
    let cancelled = false;
    const ids = key.split(",");
    Promise.allSettled(ids.map((id) => fetchCached(adapter, `layout:${id}:${asOf}`, () => adapter.workplace.getFloorLayout(id, asOf)).promise)).then((results) => {
      if (cancelled) return;
      const map = new Map<string, FloorLayout>();
      results.forEach((r, i) => {
        if (r.status === "fulfilled") map.set(ids[i]!, r.value);
      });
      setState({ key: `${asOf}|${key}`, map });
    });
    return () => {
      cancelled = true;
    };
  }, [adapter, key, asOf]);
  return state.map;
}
