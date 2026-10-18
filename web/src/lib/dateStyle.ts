// 日付の表示の暦（西暦 / 和暦）の設定。利用者ごとに localStorage に覚える（読めなくても既定の西暦で動く）。
import { useCallback, useSyncExternalStore } from "react";
import { formatDate, type DateStyle } from "./date";

const KEY = "jinji.dateStyle";
const listeners = new Set<() => void>();

function read(): DateStyle {
  try {
    return localStorage.getItem(KEY) === "wareki" ? "wareki" : "seireki";
  } catch {
    return "seireki";
  }
}

let current: DateStyle = typeof window === "undefined" ? "seireki" : read();

export function setDateStyle(style: DateStyle) {
  current = style;
  try {
    if (style === "seireki") localStorage.removeItem(KEY);
    else localStorage.setItem(KEY, style);
  } catch {
    // 保存できなくても表示は切り替える
  }
  listeners.forEach((l) => l());
}

function subscribe(listener: () => void) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function useDateStyle(): DateStyle {
  return useSyncExternalStore(subscribe, () => current, () => "seireki");
}

/** 今の暦で日付を整形する関数。 */
export function useFormatDate(): (date: string | null | undefined) => string {
  const style = useDateStyle();
  return useCallback((date: string | null | undefined) => formatDate(date, style), [style]);
}
