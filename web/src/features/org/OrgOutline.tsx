// 組織の一覧（入れ子のリスト）。3D 表示のキーボード・スクリーンリーダー向けの代替として使う。
import { useMemo } from "react";
import { useOrgStats, useOrgTree } from "../../app/hooks";
import { useNav } from "../../app/nav";
import { Spinner } from "../../components/ui";
import { buildForest, type OrgNode } from "../../lib/orgTree";

function Item({ node, onSelect }: { node: OrgNode; onSelect: (id: string) => void }) {
  return (
    <li>
      {node.kind === "org" ? (
        <button type="button" onClick={() => onSelect(node.id)} className="flex w-full items-baseline gap-1.5 py-0.5 text-left hover:underline">
          <span>{node.name}</span>
          {node.headcount && <span className="tabular text-xs text-muted">{node.headcount.primary}名</span>}
        </button>
      ) : (
        <div className="py-0.5 font-bold">{node.name}</div>
      )}
      {node.children.length > 0 && (
        <ul className="m-0 list-none border-l border-border pl-3">
          {node.children.map((c) => (
            <Item key={c.id} node={c} onSelect={onSelect} />
          ))}
        </ul>
      )}
    </li>
  );
}

export function OrgOutline() {
  const { asOf, selectOrg } = useNav();
  const tree = useOrgTree(asOf);
  const stats = useOrgStats(asOf);
  const forest = useMemo(() => (tree.data ? buildForest(tree.data, stats.data ?? null) : null), [tree.data, stats.data]);
  if (!forest) return <Spinner />;
  return (
    <nav aria-label="組織の一覧" className="text-sm">
      <h2 className="mb-1 text-xs font-bold text-muted">組織の一覧</h2>
      <p className="mb-2 text-xs text-muted">組織を選ぶと、3D 表示がその組織に寄り、所属者を表示します。</p>
      <ul className="m-0 list-none p-0">
        {forest.roots.map((r) => (
          <Item key={r.id} node={r} onSelect={selectOrg} />
        ))}
      </ul>
    </nav>
  );
}
