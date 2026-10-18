// 認証の取得元の選択。本番ビルドは API アダプタだけ。デモ用ビルドでは ?source=api のときだけ API、それ以外はデモ（架空）。
import { useEffect, useState } from "react";
import { DEMO_AVAILABLE } from "../data/adapterContext";
import { createApiAuthAdapter } from "./apiAuthAdapter";
import type { AuthAdapter } from "./types";

export type AuthSource = "api" | "demo";

/** 認証の画面（/login など。パスで開く）の取得元。URL のクエリ source で選ぶ。 */
export function authSourceFromLocation(search: string = window.location.search): AuthSource {
  if (!DEMO_AVAILABLE) return "api";
  return new URLSearchParams(search).get("source") === "api" ? "api" : "demo";
}

/** 認証の画面の URL に取得元を引き継ぐ（デモ用ビルドで API を選んでいるときだけ source=api を足す）。 */
export function withSource(url: string, source: AuthSource): string {
  if (!DEMO_AVAILABLE || source !== "api") return url;
  const [path = "", hash = ""] = url.split("#", 2);
  const sep = path.includes("?") ? "&" : "?";
  return `${path}${sep}source=api${hash ? `#${hash}` : ""}`;
}

export async function loadAuthAdapter(source: AuthSource): Promise<AuthAdapter> {
  if (__JINJI_DEMO__ && source === "demo") {
    const { createDemoAuthAdapter } = await import("../demo/demoAuthAdapter");
    return createDemoAuthAdapter();
  }
  return createApiAuthAdapter();
}

/** 取得元を読み込む（読み込み中は null）。 */
export function useAuthAdapter(source: AuthSource): AuthAdapter | null {
  const [adapter, setAdapter] = useState<{ source: AuthSource; adapter: AuthAdapter } | null>(null);
  useEffect(() => {
    let cancelled = false;
    void loadAuthAdapter(source).then((a) => {
      if (!cancelled) setAdapter({ source, adapter: a });
    });
    return () => {
      cancelled = true;
    };
  }, [source]);
  return adapter?.source === source ? adapter.adapter : null;
}
