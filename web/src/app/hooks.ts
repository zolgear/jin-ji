// 画面で共有する取得フック。キーに引数をすべて含める（キャッシュは アダプタ × キー）。
import { useEffect, useState } from "react";
import { useAdapter } from "../data/adapterContext";
import { fetchCached, useQuery } from "../data/query";
import type { Person } from "../data/types";
import { personName } from "../components/ui";

export function useOrgTree(asOf: string) {
  const { adapter } = useAdapter();
  return useQuery(adapter, `tree:${asOf}`, () => adapter.getOrgTree(asOf));
}

export function useOrgStats(asOf: string) {
  const { adapter } = useAdapter();
  return useQuery(adapter, `stats:${asOf}`, () => adapter.getOrgStats(asOf));
}

export function usePerson(id: string | null, asOf: string) {
  const { adapter } = useAdapter();
  return useQuery(adapter, id ? `person:${id}:${asOf}` : null, () => adapter.getPerson(id!, asOf));
}

/**
 * 複数の人の表示名をまとめて引く（組織長の名前など）。API の一括取得（GET /people?ids=、100 件ずつ）を使う。
 * 見えない人・取得できない人は null。ID の組ごとにキャッシュする。
 */
export function usePersonNames(ids: string[], asOf: string): Map<string, string | null> {
  const { adapter } = useAdapter();
  const [state, setState] = useState<{ key: string; names: Map<string, string | null> }>({ key: "", names: new Map() });
  const key = [...new Set(ids)].sort().join(",");
  useEffect(() => {
    if (!key) return;
    let cancelled = false;
    const unique = key.split(",");
    fetchCached<Person[]>(adapter, `people:${asOf}:${key}`, () => adapter.getPeopleByIds(unique, asOf)).promise.then(
      (people) => {
        if (cancelled) return;
        const found = new Map(people.map((p) => [p.id, personName(p)]));
        setState({ key: `${asOf}|${key}`, names: new Map(unique.map((id) => [id, found.get(id) ?? null])) });
      },
      () => {
        if (!cancelled) setState({ key: `${asOf}|${key}`, names: new Map(unique.map((id) => [id, null])) });
      },
    );
    return () => {
      cancelled = true;
    };
  }, [adapter, key, asOf]);
  // 取得中は前の結果を使う（名前が「…」に戻ってちらつかないように）
  return state.names;
}
