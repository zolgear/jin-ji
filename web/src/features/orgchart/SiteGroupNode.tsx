// 「拠点で分ける」の区画の枠（組織図の札の後ろに敷く薄い面と見出し）。操作を受けない（図の移動・札の選択を妨げない）。
import type { Node, NodeProps } from "@xyflow/react";
import { memo } from "react";
import { siteTypeLabel } from "../../lib/labels";
import { partitionName, type SitePartition } from "../../lib/siteSplit";

export interface SiteGroupData extends Record<string, unknown> {
  partition: SitePartition;
  /** 拠点の色（色分けしているときだけ。CSS の色）。 */
  color: string | null;
  /** 法人名を出すか（法人が複数あるときだけ）。 */
  showEntity: boolean;
  width: number;
  height: number;
}

export type SiteGroupFlowNode = Node<SiteGroupData, "siteGroup">;

function SiteGroupNodeImpl({ data }: NodeProps<SiteGroupFlowNode>) {
  const { partition, color, showEntity, width, height } = data;
  const type = partition.site ? siteTypeLabel(partition.site.type) : null;
  return (
    <div
      className="site-group relative rounded-lg border"
      style={{
        width,
        height,
        // 色分けしているときは、枠と面を拠点の色にごく薄く寄せる（札の色の帯と同じ色）
        borderColor: color ? `color-mix(in srgb, ${color} 45%, var(--border))` : undefined,
        background: color ? `color-mix(in srgb, ${color} 5%, var(--site-group-bg))` : undefined,
      }}
    >
      {/* 見出し: 1 行目に拠点名と種別、2 行目に法人（複数のときだけ）と人数。狭い区画（左から右・根が 1 つ）でも拠点名を切らない */}
      <div className="absolute top-2.5 right-4 left-4 flex min-w-0 flex-col gap-0.5 text-xs">
        <div className="flex min-w-0 items-center gap-1.5">
          {color && <span aria-hidden="true" className="size-2.5 shrink-0 rounded-[3px]" style={{ background: color }} />}
          <span className="truncate text-[15px] leading-5 font-bold text-fg" title={partitionName(partition)}>
            {partitionName(partition)}
          </span>
          {type && <span className="shrink-0 rounded-sm bg-surface-2 px-1 text-[10px] leading-4 text-muted">{type}</span>}
        </div>
        <div className="tabular flex min-w-0 items-center gap-1 text-[11px] leading-4 text-muted">
          {showEntity && (
            <span className="min-w-0 truncate" title={partition.legalEntityName}>
              {partition.legalEntityName} ·
            </span>
          )}
          <span className="shrink-0">
            {partition.headcount !== null && (
              <>
                <span className="font-bold text-fg">{partition.headcount}</span>名 ·{" "}
              </>
            )}
            {partition.orgCount} 組織
          </span>
        </div>
      </div>
    </div>
  );
}

export const SiteGroupNode = memo(SiteGroupNodeImpl);
