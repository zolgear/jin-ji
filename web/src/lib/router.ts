// URL のハッシュ（#/org-chart?as_of=...&person=...）で画面と状態を持つ最小のルーター。
// 画面が少ないので TanStack Router / React Router は入れない。ハッシュなので配信側に SPA の振り分け設定が要らない。
import { useCallback, useSyncExternalStore } from "react";

// 設定の画面だけは、項目をパスの 2 段目に持つ（#/settings/display）。
export type View = "org-chart" | "overview-3d" | "search" | "changes" | "corrections" | "settings";
export const VIEWS: View[] = ["org-chart", "overview-3d", "search", "changes", "corrections", "settings"];

export interface Route {
  view: View;
  params: URLSearchParams;
  /** パスの 2 段目（設定の画面の項目）。無ければ null。 */
  sub?: string | null;
}

export function parseHash(hash: string): Route {
  const raw = hash.replace(/^#\/?/, "");
  const [path = "", query = ""] = raw.split("?");
  const [head = "", ...rest] = path.split("/");
  const view = (VIEWS as string[]).includes(head) ? (head as View) : "org-chart";
  const sub = view === "settings" && rest.length > 0 && rest[0] ? rest.join("/") : null;
  return { view, params: new URLSearchParams(query), sub };
}

export function formatHash(route: Route): string {
  const qs = route.params.toString();
  return `#/${route.view}${route.sub ? `/${route.sub}` : ""}${qs ? `?${qs}` : ""}`;
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
      // 画面を変えたら、2 段目（設定の項目）は引き継がない
      const view = change.view ?? current.view;
      const next = formatHash({ view, params, sub: view === current.view ? current.sub : null });
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
