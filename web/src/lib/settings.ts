// 利用者ごとの表示の既定（設定画面の「表示」）。localStorage に覚え、読めない・書けないときは既定値で動く（theme.ts・dateStyle.ts と同じ流儀）。
// 既定値と同じ値は保存しない（消す）。画面の URL のクエリで値を明示したときは、URL が設定より優先する（共有したリンクの見え方を保つ）。
//
// - 組織で色分け（建物・フロアモード）: 既定はオン。URL は color=org（オン）・color=none（オフ）。書かなければ設定に従う
// - 組織図の向き: 既定は上から下（tb）。URL は dir=tb・dir=lr。書かなければ設定に従う
import { useSyncExternalStore } from "react";

export type ChartDirectionSetting = "tb" | "lr";

export interface DisplaySettings {
  /** 建物・フロアモードの「組織で色分け」の既定。 */
  floorOrgColor: boolean;
  /** 組織図の向きの既定。 */
  chartDirection: ChartDirectionSetting;
}

export const DEFAULT_SETTINGS: DisplaySettings = { floorOrgColor: true, chartDirection: "tb" };

export const SETTING_KEYS = { floorOrgColor: "jinji.floorOrgColor", chartDirection: "jinji.chartDirection" } as const;

/** 保存した文字列から設定の値（知らない値は既定値）。 */
export function parseSettings(get: (key: string) => string | null): DisplaySettings {
  return {
    floorOrgColor: get(SETTING_KEYS.floorOrgColor) !== "off",
    chartDirection: get(SETTING_KEYS.chartDirection) === "lr" ? "lr" : "tb",
  };
}

/** 設定の値を保存する文字列（既定値なら null。保存せずに消す）。 */
export function serializeSetting<K extends keyof DisplaySettings>(key: K, value: DisplaySettings[K]): string | null {
  if (value === DEFAULT_SETTINGS[key]) return null;
  if (key === "floorOrgColor") return value ? "on" : "off";
  return String(value);
}

/** 建物・フロアモードの組織で色分けをするか（URL の color が設定より優先）。 */
export function resolveFloorOrgColor(param: string | null, setting: boolean): boolean {
  if (param === "org") return true;
  if (param === "none") return false;
  return setting;
}

/** 組織で色分けを切り替えたときの URL の color（設定と同じなら書かない）。 */
export function floorOrgColorParam(next: boolean, setting: boolean): string | null {
  if (next === setting) return null;
  return next ? "org" : "none";
}

/** 組織図の向き（URL の dir が設定より優先）。 */
export function resolveChartDirection(param: string | null, setting: ChartDirectionSetting): ChartDirectionSetting {
  return param === "lr" || param === "tb" ? param : setting;
}

/** 向きを切り替えたときの URL の dir（設定と同じなら書かない）。 */
export function chartDirectionParam(next: ChartDirectionSetting, setting: ChartDirectionSetting): string | null {
  return next === setting ? null : next;
}

// ---- 保存と購読（画面の各所から同じ値を見る） ----

function safeGet(key: string): string | null {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

const listeners = new Set<() => void>();
let current: DisplaySettings = typeof window === "undefined" ? DEFAULT_SETTINGS : parseSettings(safeGet);

export function setSetting<K extends keyof DisplaySettings>(key: K, value: DisplaySettings[K]) {
  current = { ...current, [key]: value };
  const raw = serializeSetting(key, value);
  try {
    if (raw === null) localStorage.removeItem(SETTING_KEYS[key]);
    else localStorage.setItem(SETTING_KEYS[key], raw);
  } catch {
    // 保存できなくても、開いている間は切り替える
  }
  listeners.forEach((l) => l());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useSettings(): DisplaySettings {
  return useSyncExternalStore(subscribe, () => current, () => DEFAULT_SETTINGS);
}
