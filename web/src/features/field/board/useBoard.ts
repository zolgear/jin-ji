// 素案ボードのデータ: 基準日の組織ツリー、変更を重ねた木、異動する人の名前と元の所属。
import { useMemo } from "react";
import { usePersonNames } from "../../../app/hooks";
import { personName } from "../../../components/ui";
import { useAdapter } from "../../../data/adapterContext";
import type { PlanChange } from "../../../data/field";
import { useQuery } from "../../../data/query";
import type { Person } from "../../../data/types";
import { applyOrgChanges, movesOf, type BoardMove, type BoardTree } from "./model";

/** 異動する人の、基準日の名前・主務の組織・役職。 */
export interface MovedPerson {
  name: string;
  org: string | null;
  title: string | null;
}

export interface BoardData {
  baseDate: string;
  loading: boolean;
  error: unknown;
  /** 基準日の組織。 */
  base: BoardTree;
  /** 素案を重ねた組織。 */
  tree: BoardTree;
  moves: Map<string, BoardMove>;
  moved: Map<string, MovedPerson>;
  /** 組織長などの名前（見えない人は null）。 */
  names: Map<string, string | null>;
}

export function primaryOf(p: Person, unitId?: string) {
  return p.assignments?.find((a) => a.type === "primary" && (!unitId || a.org_unit_id === unitId)) ?? null;
}

export function useBoard(baseDate: string, changes: PlanChange[]): BoardData {
  const { adapter } = useAdapter();
  // 組織図の画面と同じキー（取得をまとめる）
  const treeQuery = useQuery(adapter, `tree:${baseDate}`, () => adapter.getOrgTree(baseDate));
  const units = useMemo(() => treeQuery.data?.units ?? [], [treeQuery.data]);
  const base = useMemo(() => applyOrgChanges(units, []), [units]);
  const tree = useMemo(() => applyOrgChanges(units, changes), [units, changes]);
  const moves = useMemo(() => movesOf(changes), [changes]);

  const movedKey = [...moves.keys()].sort().join(",");
  const movedQuery = useQuery(adapter, movedKey ? `board-moved:${baseDate}:${movedKey}` : null, async () => {
    const ids = movedKey.split(",");
    const people: Person[] = [];
    for (let i = 0; i < ids.length; i += 100) people.push(...(await adapter.getPeopleByIds(ids.slice(i, i + 100), baseDate)));
    return people;
  });
  const moved = useMemo(() => {
    const map = new Map<string, MovedPerson>();
    for (const p of movedQuery.data ?? []) {
      const a = primaryOf(p);
      map.set(p.id, { name: personName(p), org: a?.org_unit_id ?? null, title: a?.position_title ?? null });
    }
    return map;
  }, [movedQuery.data]);

  const headIds = useMemo(() => tree.units.map((u) => u.headPersonId).filter((v): v is string => v !== null), [tree]);
  const names = usePersonNames(headIds, baseDate);

  return { baseDate, loading: treeQuery.loading, error: treeQuery.error, base, tree, moves, moved, names };
}
