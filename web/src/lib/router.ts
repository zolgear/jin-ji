// URL のハッシュ（#/org-chart?as_of=...&person=...）で画面と状態を持つ最小のルーター。
// 画面が少ないので TanStack Router / React Router は入れない。ハッシュなので配信側に SPA の振り分け設定が要らない。
import { useCallback, useSyncExternalStore } from "react";

export type View = "org-chart" | "overview-3d" | "search" | "corrections";
export const VIEWS: View[] = ["org-chart", "overview-3d", "search", "corrections"];

export interface Route {
  view: View;
  params: URLSearchParams;
}

export function parseHash(hash: string): Route {
  const raw = hash.replace(/^#\/?/, "");
  const [path = "", query = ""] = raw.split("?");
  const view = (VIEWS as string[]).includes(path) ? (path as View) : "org-chart";
  return { view, params: new URLSearchParams(query) };
}

export function formatHash(route: Route): string {
  const qs = route.params.toString();
  return `#/${route.view}${qs ? `?${qs}` : ""}`;
}

function subscribe(callback: () => void) {
  window.addEventListener("hashchange", callback);
  return () => window.removeEventListener("hashchange", callback);
}

const getHash = () => window.location.hash;

export function useRoute() {
  const hash = useSyncExternalStore(subscribe, getHash, () => "");
  const route = parseHash(hash);

  /** 画面や引数を変える。null の引数は消す。replace なら履歴を増やさない。 */
  const navigate = useCallback(
    (change: { view?: View; params?: Record<string, string | null> }, replace = false) => {
      const current = parseHash(window.location.hash);
      const params = new URLSearchParams(current.params);
      for (const [k, v] of Object.entries(change.params ?? {})) {
        if (v === null) params.delete(k);
        else params.set(k, v);
      }
      const next = formatHash({ view: change.view ?? current.view, params });
      if (next === window.location.hash) return;
      if (replace) {
        window.history.replaceState(null, "", next);
        window.dispatchEvent(new HashChangeEvent("hashchange"));
      } else {
        window.location.hash = next;
      }
    },
    [],
  );

  return { route, navigate };
}
