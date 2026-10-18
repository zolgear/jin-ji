// ライト / ダーク。既定は OS の設定（prefers-color-scheme）に従い、明示した場合だけ data-theme を付ける。
import { useCallback, useEffect, useState } from "react";

export type ThemeChoice = "system" | "light" | "dark";
const KEY = "jinji.theme";

function read(): ThemeChoice {
  try {
    const v = localStorage.getItem(KEY);
    return v === "light" || v === "dark" ? v : "system";
  } catch {
    return "system";
  }
}

export function applyTheme(choice: ThemeChoice) {
  const root = document.documentElement;
  if (choice === "system") root.removeAttribute("data-theme");
  else root.setAttribute("data-theme", choice);
}

/** 起動時に一度呼ぶ（描画前に適用してちらつきを抑える）。URL の ?theme= でも上書きできる（スクリーンショット用）。 */
export function initTheme() {
  const fromUrl = new URLSearchParams(window.location.search).get("theme");
  applyTheme(fromUrl === "light" || fromUrl === "dark" ? fromUrl : read());
}

/** 実際に効いているテーマ（3D の色の切り替えなどに使う）。 */
function resolvedTheme(): "light" | "dark" {
  const attr = document.documentElement.getAttribute("data-theme");
  if (attr === "light" || attr === "dark") return attr;
  return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

export function useResolvedTheme(): "light" | "dark" {
  const [theme, setTheme] = useState(resolvedTheme);
  useEffect(() => {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const update = () => setTheme(resolvedTheme());
    const observer = new MutationObserver(update);
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });
    media.addEventListener("change", update);
    return () => {
      observer.disconnect();
      media.removeEventListener("change", update);
    };
  }, []);
  return theme;
}

export function useThemeChoice() {
  const [choice, setChoice] = useState<ThemeChoice>(() => {
    const attr = document.documentElement.getAttribute("data-theme");
    return attr === "light" || attr === "dark" ? attr : "system";
  });
  const update = useCallback((next: ThemeChoice) => {
    setChoice(next);
    applyTheme(next);
    try {
      if (next === "system") localStorage.removeItem(KEY);
      else localStorage.setItem(KEY, next);
    } catch {
      // 保存できなくても表示は切り替える
    }
  }, []);
  return { choice, setChoice: update };
}
