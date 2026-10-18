// 取得結果の小さなキャッシュと React フック。
// TanStack Query は使わない（画面が少なく、要件はキャッシュ・重複排除・前の値の保持だけ。依存を増やさないため）。
import { useEffect, useState } from "react";

type Entry<T = unknown> = { promise: Promise<T>; value?: T; error?: unknown; settled: boolean };

const caches = new WeakMap<object, Map<string, Entry>>();
const MAX_ENTRIES = 300;

function cacheFor(owner: object): Map<string, Entry> {
  let cache = caches.get(owner);
  if (!cache) {
    cache = new Map();
    caches.set(owner, cache);
  }
  return cache;
}

/** owner（アダプタ）ごとに、キーの結果を 1 回だけ取得して共有する。失敗はキャッシュしない。 */
export function fetchCached<T>(owner: object, key: string, load: () => Promise<T>): Entry<T> {
  const cache = cacheFor(owner);
  const hit = cache.get(key);
  if (hit) {
    return hit as Entry<T>;
  }
  const entry: Entry<T> = { promise: Promise.resolve(undefined as T), settled: false };
  entry.promise = load().then(
    (value) => {
      entry.value = value;
      entry.settled = true;
      return value;
    },
    (error: unknown) => {
      entry.error = error;
      entry.settled = true;
      cache.delete(key);
      throw error;
    },
  );
  cache.set(key, entry as Entry);
  if (cache.size > MAX_ENTRIES) {
    const oldest = cache.keys().next().value;
    if (oldest !== undefined) cache.delete(oldest);
  }
  return entry;
}

export interface QueryState<T> {
  data: T | undefined;
  error: unknown;
  loading: boolean;
  /** data が今のキーではなく前のキーの値か（時点を切り替えた直後など）。 */
  stale: boolean;
}

/**
 * key が変わるたびに load を呼ぶ。key が null なら何もしない。
 * 新しい値が届くまで前の値を返す（組織図の遷移アニメーションのため）。
 */
export function useQuery<T>(owner: object, key: string | null, load: () => Promise<T>): QueryState<T> {
  const [state, setState] = useState<{ key: string | null; data: T | undefined; error: unknown; loading: boolean }>(() => {
    if (key === null) return { key, data: undefined, error: undefined, loading: false };
    const entry = cacheFor(owner).get(key);
    return entry?.settled && entry.error === undefined
      ? { key, data: entry.value as T, error: undefined, loading: false }
      : { key: null, data: undefined, error: undefined, loading: true };
  });

  useEffect(() => {
    if (key === null) {
      setState({ key: null, data: undefined, error: undefined, loading: false });
      return;
    }
    let cancelled = false;
    const entry = fetchCached(owner, key, load);
    if (entry.settled && entry.error === undefined) {
      setState({ key, data: entry.value, error: undefined, loading: false });
      return;
    }
    setState((s) => ({ ...s, loading: true, error: undefined }));
    entry.promise.then(
      (data) => {
        if (!cancelled) setState({ key, data, error: undefined, loading: false });
      },
      (error: unknown) => {
        if (!cancelled) setState((s) => ({ ...s, key, error, loading: false }));
      },
    );
    return () => {
      cancelled = true;
    };
    // load は key と owner で決まる前提（呼び出し側で key に引数をすべて含める）
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [owner, key]);

  return { data: state.data, error: state.error, loading: state.loading, stale: state.key !== key };
}
