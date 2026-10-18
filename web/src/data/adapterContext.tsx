// アダプタの選択と提供。本番ビルドは API アダプタだけ。デモ用ビルドでは画面から切り替えられる。
import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { loadApiAdapter } from "./apiAdapter";
import type { DirectoryAdapter } from "./types";

export type AdapterChoice = "api" | "demo" | "demo-large";

/** デモアダプタを使えるビルドか（本番ビルドでは false の定数になり、デモのコードは取り除かれる）。 */
export const DEMO_AVAILABLE: boolean = __JINJI_DEMO__;

export async function loadAdapter(choice: AdapterChoice): Promise<DirectoryAdapter> {
  if (__JINJI_DEMO__ && (choice === "demo" || choice === "demo-large")) {
    const { createDemoAdapter } = await import("../demo/demoAdapter");
    return createDemoAdapter(choice === "demo-large" ? "large" : "sample");
  }
  return loadApiAdapter();
}

interface AdapterContextValue {
  adapter: DirectoryAdapter;
  choice: AdapterChoice;
  setChoice: (choice: AdapterChoice) => void;
}

const AdapterContext = createContext<AdapterContextValue | null>(null);

export function AdapterProvider({ initial, children }: { initial: AdapterChoice; children: ReactNode }) {
  const [choice, setChoice] = useState<AdapterChoice>(initial);
  const [adapter, setAdapter] = useState<DirectoryAdapter | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    loadAdapter(choice).then(
      (a) => {
        if (!cancelled) setAdapter(a);
      },
      (e: unknown) => {
        if (!cancelled) setError(String(e));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [choice]);

  if (error) {
    return <p role="alert" className="p-6 text-danger">データの取得元を準備できませんでした: {error}</p>;
  }
  if (!adapter) {
    return <p className="p-6 text-muted" aria-live="polite">読み込み中…</p>;
  }
  return <AdapterContext.Provider value={{ adapter, choice, setChoice }}>{children}</AdapterContext.Provider>;
}

export function useAdapter(): AdapterContextValue {
  const value = useContext(AdapterContext);
  if (!value) {
    throw new Error("AdapterProvider の外で useAdapter が呼ばれました");
  }
  return value;
}
