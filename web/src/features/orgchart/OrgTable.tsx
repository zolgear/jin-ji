// 組織図の表での代替（スクリーンリーダー・キーボード・印刷・色の見分けにくい人向け）。
// 図と同じ時点・同じ集計を、組織ごとの行で示す。兼務・出向の相手先も文字で列挙する。
import { useMemo } from "react";
import { HelpTip } from "../../components/popover";
import { LineKey } from "../../components/ui";
import type { OrgLink } from "../../data/types";
import { flatten, type OrgForest } from "../../lib/orgTree";

interface Props {
  forest: OrgForest;
  links: OrgLink[];
  statsAvailable: boolean;
  selectedId: string | null;
  onSelect: (id: string) => void;
}

export function OrgTable({ forest, links, statsAvailable, selectedId, onSelect }: Props) {
  const rows = useMemo(() => flatten(forest), [forest]);
  // 組織ごとの相手先（自分が主務の側 = 送り出し、相手が主務の側 = 受け入れ）
  const partners = useMemo(() => {
    const map = new Map<string, Array<{ kind: OrgLink["kind"]; dir: "out" | "in"; other: string; count: number }>>();
    const push = (id: string, v: { kind: OrgLink["kind"]; dir: "out" | "in"; other: string; count: number }) => {
      const list = map.get(id) ?? [];
      list.push(v);
      map.set(id, list);
    };
    for (const l of links) {
      push(l.from, { kind: l.kind, dir: "out", other: l.to, count: l.count });
      push(l.to, { kind: l.kind, dir: "in", other: l.from, count: l.count });
    }
    return map;
  }, [links]);

  const name = (id: string) => forest.byId.get(id)?.name ?? "（表示範囲外の組織）";

  return (
    <div className="size-full overflow-auto px-4 pt-3 pb-4">
      <table className="w-full min-w-[820px] border-collapse text-xs">
        <caption className="mb-2 text-left text-sm font-bold">
          <span className="flex items-center gap-0.5">
            組織ごとの人数と兼務・出向の相手先
            <HelpTip label="表の見方" icon="info" width={300}>
              <p className="m-0">
                <b>直属</b>: この組織に主務がある人。<b>所属（配下を含む）</b>: 配下の組織の分も合わせた主務の人
              </p>
              <p className="m-0">
                <b>兼務・出向の受け入れ</b>: この組織で兼務・出向している人（主務は他の組織）。配下の組織の分は含めません
              </p>
              <p className="m-0">
                <b>兼務先・出向先</b>: この組織に主務がある人の兼務先・出向先
              </p>
              <p className="m-0">
                <b>兼務元・出向元</b>: この組織で兼務・出向している人の主務の組織
              </p>
            </HelpTip>
          </span>
        </caption>
        <thead>
          <tr className="border-b border-border text-left text-muted">
            <th scope="col" className="py-1 pr-2 font-bold">組織</th>
            <th scope="col" className="py-1 pr-2 font-bold">段</th>
            <th scope="col" className="py-1 pr-2 font-bold">拠点</th>
            <th scope="col" className="tabular py-1 pr-4 text-right font-bold">直属</th>
            <th scope="col" className="tabular py-1 pr-4 text-right font-bold">所属（配下を含む）</th>
            <th scope="col" className="tabular py-1 pr-4 text-right font-bold">兼務の受け入れ</th>
            <th scope="col" className="tabular py-1 pr-4 text-right font-bold">出向の受け入れ</th>
            <th scope="col" className="py-1 font-bold">兼務・出向の相手先（人数）</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((n) => {
            const isEntity = n.kind === "legal_entity";
            const list = partners.get(n.id) ?? [];
            return (
              <tr key={n.id} aria-selected={n.id === selectedId || undefined} className={`border-b border-border/60 align-top ${n.id === selectedId ? "bg-accent-weak" : ""}`}>
                <th scope="row" className="py-1 pr-2 text-left font-normal" style={{ paddingLeft: `${n.depth * 14}px` }}>
                  {isEntity ? (
                    <span className="font-bold">{n.name}</span>
                  ) : (
                    <button type="button" className="text-left text-accent hover:underline" onClick={() => onSelect(n.id)}>
                      {n.name}
                    </button>
                  )}
                </th>
                <td className="py-1 pr-2 text-muted">{isEntity ? "法人" : (n.levelName ?? "—")}</td>
                <td className="py-1 pr-2">
                  {n.site ? (
                    <>
                      {n.site.name}
                      {n.site.inherited && <span className="ml-1 text-muted">（上位から）</span>}
                    </>
                  ) : (
                    <span className="text-muted">—</span>
                  )}
                </td>
                <td className="tabular py-1 pr-4 text-right">{statsAvailable && !isEntity ? n.headcount?.primary : "—"}</td>
                <td className="tabular py-1 pr-4 text-right">{statsAvailable ? n.totalPrimary : "—"}</td>
                <td className="tabular py-1 pr-4 text-right">{statsAvailable && !isEntity ? n.headcount?.concurrent || "" : "—"}</td>
                <td className="tabular py-1 pr-4 text-right">{statsAvailable && !isEntity ? n.headcount?.secondment || "" : "—"}</td>
                <td className="py-1">
                  {list.length > 0 && (
                    <ul className="m-0 flex list-none flex-col gap-0.5 p-0">
                      {list.map((p) => (
                        <li key={`${p.kind}:${p.dir}:${p.other}`} className="flex items-center gap-1">
                          <LineKey kind={p.kind} width={14} />
                          <span>
                            {p.kind === "concurrent" ? (p.dir === "out" ? "兼務先" : "兼務元") : p.dir === "out" ? "出向先" : "出向元"}
                          </span>
                          <button type="button" className="text-accent hover:underline" onClick={() => onSelect(p.other)}>
                            {name(p.other)}
                          </button>
                          <span className="tabular text-muted">{p.count}名</span>
                        </li>
                      ))}
                    </ul>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}
