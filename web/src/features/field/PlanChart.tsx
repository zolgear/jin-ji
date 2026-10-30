// 素案の組織図（字下げの木を SVG で描く）と提案資料。提案資料は、今の組織図と素案の組織図を並べ、変更の一覧と人数の増減を添える。
// 印刷（ブラウザの印刷で PDF）と SVG の保存ができる。新しい依存は足さない（ADR-0001）。
import { useEffect, useMemo, useRef, type RefObject } from "react";
import { createPortal } from "react-dom";
import { Button } from "../../auth/ui";
import type { PlanSimulation, PlanUnit, PlanVersion } from "../../data/field";
import type { OrgUnit } from "../../data/types";
import { orderUnits } from "./shared";

export interface ChartNode {
  id: string;
  name: string;
  code: string;
  parentId: string | null;
  state: PlanUnit["state"];
  count: number;
  /** 増減（素案の図だけ）。 */
  delta?: number;
}

const ROW = 26;
const INDENT = 18;
const BOX = 250;

const STATE_STYLE: Record<PlanUnit["state"], { stroke: string; fill: string; label: string }> = {
  unchanged: { stroke: "var(--border)", fill: "var(--surface)", label: "" },
  created: { stroke: "var(--accent)", fill: "var(--accent-weak)", label: "新設" },
  changed: { stroke: "var(--warn-fg)", fill: "var(--warn-bg)", label: "変更" },
  abolished: { stroke: "var(--danger)", fill: "var(--surface)", label: "廃止" },
};

/** 字下げの木の組織図（SVG）。行ごとに組織名・状態・人数を描く。 */
export function PlanChart({ title, nodes, svgRef }: { title: string; nodes: ChartNode[]; svgRef?: RefObject<SVGSVGElement | null> }) {
  const rows = useMemo(() => {
    const asUnits = nodes.map((n) => ({ id: n.id, code: n.code, parent_id: n.parentId }) as unknown as OrgUnit);
    const byId = new Map(nodes.map((n) => [n.id, n]));
    return orderUnits(asUnits).map(({ unit, depth }) => ({ node: byId.get(unit.id)!, depth }));
  }, [nodes]);
  const maxDepth = rows.reduce((m, r) => Math.max(m, r.depth), 0);
  const width = BOX + maxDepth * INDENT + 80;
  const height = (rows.length + 1) * ROW + 8;
  const index = new Map(rows.map((r, i) => [r.node.id, i]));
  return (
    <svg ref={svgRef} xmlns="http://www.w3.org/2000/svg" aria-label={title} width={width} height={height} viewBox={`0 0 ${width} ${height}`} className="h-auto max-w-full">
      <text x={4} y={16} fontSize={13} fontWeight={700} fill="var(--fg)">
        {title}
      </text>
      {rows.map(({ node, depth }, i) => {
        const y = (i + 1) * ROW + 4;
        const x = 4 + depth * INDENT;
        const s = STATE_STYLE[node.state];
        const parent = node.parentId !== null ? index.get(node.parentId) : undefined;
        return (
          <g key={node.id}>
            {parent !== undefined && (
              <path
                d={`M${x - INDENT + 6} ${(parent + 1) * ROW + 4 + ROW - 6}V${y + 10}H${x}`}
                fill="none"
                stroke="var(--border)"
                strokeWidth={1}
              />
            )}
            <rect x={x} y={y} width={BOX} height={ROW - 6} rx={3} fill={s.fill} stroke={s.stroke} strokeWidth={node.state === "unchanged" ? 1 : 1.6} />
            <text x={x + 6} y={y + 14} fontSize={12} fill="var(--fg)" textDecoration={node.state === "abolished" ? "line-through" : undefined}>
              {node.name}
              {s.label ? `（${s.label}）` : ""}
            </text>
            <text x={x + BOX + 6} y={y + 14} fontSize={12} fill="var(--fg)" className="tabular">
              {node.count}
              {node.delta ? `（${node.delta > 0 ? "+" : ""}${node.delta}）` : ""}
            </text>
          </g>
        );
      })}
    </svg>
  );
}

/** 今の組織図（基準日の組織と今の人数）。廃止・変更する組織に印を付ける。 */
export function beforeNodes(units: OrgUnit[], sim: PlanSimulation): ChartNode[] {
  const byId = new Map(sim.units.map((u) => [u.id, u]));
  return units.map((u) => {
    const s = byId.get(u.id);
    return {
      id: u.id,
      name: u.name,
      code: u.code,
      parentId: u.parent_id,
      state: s?.state === "abolished" ? "abolished" : s?.state === "changed" ? "changed" : "unchanged",
      count: s?.before ?? 0,
    };
  });
}

/** 素案を適用した組織図（廃止した組織を除く。増減を添える）。 */
export function afterNodes(sim: PlanSimulation): ChartNode[] {
  return sim.units
    .filter((u) => u.state !== "abolished")
    .map((u) => ({ id: u.id, name: u.name, code: u.code, parentId: u.parent_id, state: u.state, count: u.after, delta: u.after - u.before }));
}

/** SVG を保存する（画面の配色の CSS 変数を、その時点の値に置き換えて単体で開けるようにする）。 */
export function downloadSvg(svg: SVGSVGElement | null, fileName: string) {
  if (!svg) return;
  const style = getComputedStyle(document.documentElement);
  const text = new XMLSerializer().serializeToString(svg).replace(/var\((--[a-z0-9-]+)\)/g, (_, name: string) => style.getPropertyValue(name).trim() || "#888");
  const href = URL.createObjectURL(new Blob([text], { type: "image/svg+xml" }));
  const a = document.createElement("a");
  a.href = href;
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(href), 1000);
}

/** 提案資料（印刷用の全面の表示）。開いている間は body に印をつけ、印刷では提案資料だけを出す。 */
export function ProposalDocument({
  title,
  version,
  baseUnits,
  simulation,
  changes,
  onCsv,
  onClose,
}: {
  title: string;
  version: PlanVersion | null;
  baseUnits: OrgUnit[];
  simulation: PlanSimulation;
  changes: string[];
  onCsv: () => void;
  onClose: () => void;
}) {
  const beforeRef = useRef<SVGSVGElement>(null);
  const afterRef = useRef<SVGSVGElement>(null);
  useEffect(() => {
    document.body.classList.add("printing");
    const onKey = (e: KeyboardEvent) => e.key === "Escape" && onClose();
    window.addEventListener("keydown", onKey);
    return () => {
      document.body.classList.remove("printing");
      window.removeEventListener("keydown", onKey);
    };
  }, [onClose]);
  const diff = simulation.units.filter((u) => u.before !== u.after || u.state !== "unchanged");
  return createPortal(
    <dialog open aria-modal="true" aria-label="提案資料" className="print-doc fixed inset-0 z-50 m-0 size-full max-h-none max-w-none overflow-y-auto border-0 bg-bg p-6 text-fg print:static print:p-0">
      <div className="mb-4 flex flex-wrap items-center gap-2 print:hidden">
        <Button variant="primary" onClick={() => window.print()}>
          印刷・PDF
        </Button>
        <Button onClick={() => downloadSvg(beforeRef.current, "jinji-org-before.svg")}>今の組織図の SVG</Button>
        <Button onClick={() => downloadSvg(afterRef.current, "jinji-org-after.svg")}>素案の組織図の SVG</Button>
        <Button onClick={onCsv}>変更の一覧の CSV</Button>
        <Button variant="ghost" className="ml-auto" onClick={onClose}>
          閉じる
        </Button>
      </div>
      <header className="mb-4">
        <h2 className="m-0 text-xl font-bold">{title}</h2>
        <p className="m-0 text-sm text-muted">
          基準日 {simulation.base_date}
          {version && `・申請版 ${version.version_no}・予定の有効日 ${version.effective_date}`}
          {version?.approved_on && `・承認 ${version.approved_on}${version.approval_reference ? `（${version.approval_reference}）` : ""}`}
        </p>
      </header>
      <section className="mb-6 grid gap-4 lg:grid-cols-2 print:grid-cols-2">
        <div className="overflow-x-auto rounded-md border border-border p-2">
          <PlanChart title="今の組織" nodes={beforeNodes(baseUnits, simulation)} svgRef={beforeRef} />
        </div>
        <div className="overflow-x-auto rounded-md border border-border p-2">
          <PlanChart title="素案の組織" nodes={afterNodes(simulation)} svgRef={afterRef} />
        </div>
      </section>
      <section className="mb-6 break-inside-avoid">
        <h3 className="mb-2 text-base font-bold">変更の一覧</h3>
        <ol className="m-0 pl-6 text-sm">
          {changes.map((c, i) => (
            <li key={i}>{c}</li>
          ))}
        </ol>
      </section>
      <section className="break-inside-avoid">
        <h3 className="mb-2 text-base font-bold">組織ごとの人数</h3>
        <table className="w-full max-w-2xl border-collapse text-sm">
          <thead>
            <tr className="border-b border-border text-left">
              <th className="py-1">組織</th>
              <th className="py-1 text-right">今</th>
              <th className="py-1 text-right">素案</th>
              <th className="py-1 text-right">増減</th>
            </tr>
          </thead>
          <tbody>
            {diff.map((u) => (
              <tr key={u.id} className="border-b border-border">
                <td className="py-1">
                  {u.name}
                  {u.state !== "unchanged" && <span className="ml-1 text-xs text-muted">（{STATE_STYLE[u.state].label}）</span>}
                </td>
                <td className="py-1 text-right tabular">{u.before}</td>
                <td className="py-1 text-right tabular">{u.after}</td>
                <td className="py-1 text-right tabular">{u.after - u.before > 0 ? `+${u.after - u.before}` : u.after - u.before}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>
      {simulation.warnings.length > 0 && (
        <section className="mt-6 print:hidden">
          <h3 className="mb-2 text-base font-bold">確かめること</h3>
          <ul className="m-0 pl-6 text-sm text-warn-fg">
            {simulation.warnings.map((w, i) => (
              <li key={i}>{w.index >= 0 ? `変更 ${w.index + 1}: ` : ""}{w.message}</li>
            ))}
          </ul>
        </section>
      )}
    </dialog>,
    document.body,
  );
}
