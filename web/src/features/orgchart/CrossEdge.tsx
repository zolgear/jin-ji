// 兼務・出向の線。左から右では両端とも札の右側から出入りし、右へ膨らむ弧で描く（同じ列の組織同士でも札と重ならない）。
// 上から下では両端とも札の上の辺から出入りし、上へ膨らむ弧で描く（同じ段の組織同士でも札と重ならない）。
// ラベルは線の中ほどではなく「相手の組織」の札のすぐそば（layout.ts の labelPlacement）に置く。線の中ほどに置くと、弧が束になったときにラベルが重なるため。
// ラベルの文字は文字の色（--fg）で書き、線種の見本を添える（文字をデータの色にしない）。
import { BaseEdge, EdgeLabelRenderer, type Edge, type EdgeProps } from "@xyflow/react";
import { LineKey } from "../../components/ui";
import type { ChartDirection, LabelPlacement } from "./layout";

export interface CrossEdgeData extends Record<string, unknown> {
  kind: "concurrent" | "secondment";
  count: number;
  /** ラベルを置く端（相手の組織の側）。null ならラベルを出さない。 */
  labelAt: "source" | "target" | null;
  /** ラベルの文字（例: 兼務先 2名）。 */
  label: string;
  /** ラベルの置き場所（線の出入り口からのずれ。layout.ts の labelPlacement）。 */
  placement: LabelPlacement;
  direction: ChartDirection;
  /** 読み上げ・ツールチップ用の説明。 */
  description: string;
}

/** ラベルの基準点に対する寄せ方（右: 左端を合わせ上下中央、左: 右端を合わせ上下中央、上: 左端と下端を合わせる）。 */
const LABEL_ANCHOR: Record<LabelPlacement["side"], string> = {
  right: "translateY(-50%)",
  left: "translate(-100%, -50%)",
  above: "translateY(-100%)",
};

export type CrossFlowEdge = Edge<CrossEdgeData, "cross">;

export function crossEdgePath(
  sx: number,
  sy: number,
  tx: number,
  ty: number,
  direction: ChartDirection = "lr",
): { path: string; labelX: number; labelY: number } {
  if (direction === "tb") {
    const bulge = 36 + 0.22 * Math.abs(tx - sx) + Math.max(0, sy - ty) * 0.1;
    const c1y = Math.min(sy, ty) - bulge;
    return { path: `M ${sx},${sy} C ${sx},${c1y} ${tx},${c1y} ${tx},${ty}`, labelX: (sx + tx) / 2, labelY: 0.125 * sy + 0.75 * c1y + 0.125 * ty };
  }
  const bulge = 36 + 0.22 * Math.abs(ty - sy) + Math.max(0, sx - tx) * 0.1;
  const c1x = Math.max(sx, tx) + bulge;
  const path = `M ${sx},${sy} C ${c1x},${sy} ${c1x},${ty} ${tx},${ty}`;
  // 3 次ベジェの t=0.5 の点
  return { path, labelX: 0.125 * sx + 0.75 * c1x + 0.125 * tx, labelY: (sy + ty) / 2 };
}

export function CrossEdge({ id, sourceX, sourceY, targetX, targetY, data, style, markerEnd }: EdgeProps<CrossFlowEdge>) {
  const { path } = crossEdgePath(sourceX, sourceY, targetX, targetY, data?.direction);
  const at = data?.labelAt;
  const x = at === "source" ? sourceX : targetX;
  const y = at === "source" ? sourceY : targetY;
  return (
    <>
      <BaseEdge id={id} path={path} style={style} markerEnd={markerEnd} />
      {data && at && (
        <EdgeLabelRenderer>
          <div
            className="nodrag nopan pointer-events-auto absolute flex items-center gap-1 whitespace-nowrap rounded-sm border border-border bg-surface px-1.5 text-[11px] leading-[18px] text-fg shadow-sm"
            style={{
              transform: `translate(${x + data.placement.dx}px, ${y + data.placement.dy}px) ${LABEL_ANCHOR[data.placement.side]}`,
              zIndex: 5,
            }}
            title={data.description}
          >
            <LineKey kind={data.kind} width={14} />
            <span className="tabular">{data.label}</span>
            <span className="sr-only">{data.description}</span>
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  );
}
