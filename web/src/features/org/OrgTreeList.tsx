// 組織のツリー（入れ子の一覧）。3D 表示の図の上に浮かべ、階層をたどって組織を選ぶ（名前を知らない組織を探す・キーボードの代わり）。
// 選んだ組織の上位は開き、選んだ組織を強調してその行まで送る。法人と 1 段目は最初から開く。
import { useEffect, useMemo, useRef, useState } from "react";
import { useOrgStats, useOrgTree } from "../../app/hooks";
import { useNav } from "../../app/nav";
import { Spinner } from "../../components/ui";
import { buildForest, type OrgNode } from "../../lib/orgTree";

export function OrgTreeList({ selectedId, onSelect }: { selectedId: string | null; onSelect: (node: OrgNode) => void }) {
  const { asOf } = useNav();
  const tree = useOrgTree(asOf);
  const stats = useOrgStats(asOf);
  const forest = useMemo(() => (tree.data ? buildForest(tree.data, stats.data ?? null) : null), [tree.data, stats.data]);
  // 自分で開け閉めした組織（無ければ既定: 深さ 1 まで・選んだ組織の上位）
  const [toggled, setToggled] = useState<Map<string, boolean>>(new Map());
  const ancestors = useMemo(() => {
    const set = new Set<string>();
    for (let cur = selectedId ? (forest?.byId.get(selectedId)?.parentId ?? null) : null, guard = 0; cur && guard < 1000; guard++) {
      set.add(cur);
      cur = forest?.byId.get(cur)?.parentId ?? null;
    }
    return set;
  }, [forest, selectedId]);
  const list = useRef<HTMLUListElement>(null);
  useEffect(() => {
    if (!selectedId) return;
    list.current?.querySelector(`[data-tree-id="${CSS.escape(selectedId)}"]`)?.scrollIntoView({ block: "nearest" });
  }, [selectedId]);
  if (!forest) return <Spinner />;
  const isOpen = (n: OrgNode) => toggled.get(n.id) ?? (n.depth < 1 || ancestors.has(n.id));
  const toggle = (n: OrgNode) => setToggled((m) => new Map(m).set(n.id, !isOpen(n)));
  const item = (node: OrgNode) => {
    const open = isOpen(node);
    const selected = node.id === selectedId;
    return (
      <li key={node.id}>
        <div data-tree-id={node.id} className={`flex items-center gap-0.5 rounded-sm ${selected ? "bg-accent text-accent-fg" : ""}`}>
          {node.children.length > 0 ? (
            <button
              type="button"
              aria-expanded={open}
              aria-label={`${node.name}の配下を${open ? "閉じる" : "開く"}`}
              onClick={() => toggle(node)}
              className={`flex size-5 shrink-0 items-center justify-center rounded-sm text-xs ${selected ? "" : "text-muted hover:bg-surface-2"}`}
            >
              {open ? "▾" : "▸"}
            </button>
          ) : (
            <span className="size-5 shrink-0" />
          )}
          <button
            type="button"
            aria-current={selected || undefined}
            onClick={() => onSelect(node)}
            className={`flex min-w-0 flex-1 items-baseline gap-1.5 rounded-sm px-1 py-0.5 text-left ${selected ? "font-bold" : "hover:bg-surface-2"} ${node.kind === "legal_entity" ? "font-bold" : ""}`}
          >
            <span className="truncate">{node.name}</span>
            {node.totalPrimary !== null && <span className={`ml-auto shrink-0 text-xs tabular ${selected ? "" : "text-muted"}`}>{node.totalPrimary}名</span>}
          </button>
        </div>
        {open && node.children.length > 0 && <ul className="m-0 ml-2.5 list-none border-l border-border pl-1.5">{node.children.map(item)}</ul>}
      </li>
    );
  };
  return (
    <ul ref={list} aria-label="組織のツリー" className="m-0 list-none p-0">
      {forest.roots.map(item)}
    </ul>
  );
}
