// 組織図のノード（組織の札）。
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { memo } from "react";
import { LineKey } from "../../components/ui";
import type { OrgNode } from "../../lib/orgTree";
import type { CrossSiteUpper } from "../../lib/siteSplit";
import { CROSS_HANDLE_RATIO, STACK_TRUNK, type ChartDirection } from "./layout";

export interface OrgCardData extends Record<string, unknown> {
  node: OrgNode;
  headName: string | null | undefined;
  hiddenChildren: number;
  open: boolean;
  /** 前の時点からの主務の人数の増減（集計が無い・変化が無ければ 0）。 */
  delta: number;
  statsAvailable: boolean;
  exiting: boolean;
  onToggle: (id: string) => void;
  onSelect: (id: string) => void;
  /** 指している（ホバー・フォーカス）組織。兼務・出向の線を出すのに使う。 */
  onHover: (id: string | null) => void;
  /** 拠点で色分けしているか（組織長の行の代わりに拠点の行を出す）。 */
  siteMode: boolean;
  /** 拠点の色（CSS の色。拠点が無ければ null）。 */
  siteColor: string | null;
  /** 組織図の向き（線の出入り口の辺が変わる）。 */
  direction: ChartDirection;
  /** 拠点で分けたとき、区画の根の本来の上位組織（別の拠点）。組織長・拠点の行の代わりに「上位」の行を出す。 */
  upper?: CrossSiteUpper | null;
}

const HANDLE_CLASS = "!size-1 !min-w-0 !border-0 !bg-transparent";
const CROSS_KINDS = ["concurrent", "secondment"] as const;

/**
 * 線の出入り口（見えない Handle）。
 * - 組織の上下の線: 左から右では左から入り右から出る。上から下では上から入り下から出る
 * - 縦に積んだ子への線（上から下だけ）: 親の下の辺の左寄り（幹）から出て、子の左から入る
 * - 兼務・出向の線: 左から右では右の辺、上から下では上の辺。種類ごとに位置を分け、組織の上下の線（辺の中央）とも重ねない
 */
function Handles({ direction, type }: { direction: ChartDirection; type: "source" | "target" }) {
  const tb = direction === "tb";
  const crossSide = tb ? Position.Top : Position.Right;
  const tree = type === "target" ? (tb ? Position.Top : Position.Left) : tb ? Position.Bottom : Position.Right;
  return (
    <>
      <Handle type={type} position={tree} className={HANDLE_CLASS} isConnectable={false} />
      {tb &&
        (type === "target" ? (
          <Handle id="stack-in" type="target" position={Position.Left} className={HANDLE_CLASS} isConnectable={false} />
        ) : (
          <Handle id="stack-out" type="source" position={Position.Bottom} style={{ left: STACK_TRUNK }} className={HANDLE_CLASS} isConnectable={false} />
        ))}
      {CROSS_KINDS.map((k) => (
        <Handle
          key={k}
          id={`cross-${type === "target" ? "in" : "out"}-${k}`}
          type={type}
          position={crossSide}
          style={tb ? { left: `${CROSS_HANDLE_RATIO[k] * 100}%` } : { top: `${CROSS_HANDLE_RATIO[k] * 100}%` }}
          className={HANDLE_CLASS}
          isConnectable={false}
        />
      ))}
    </>
  );
}

export type OrgFlowNode = Node<OrgCardData, "org">;

function Counts({ node, statsAvailable }: { node: OrgNode; statsAvailable: boolean }) {
  if (!statsAvailable || !node.headcount) {
    return <span className="text-muted">人数: 集計なし</span>;
  }
  const h = node.headcount;
  return (
    <span className="tabular flex flex-wrap gap-x-2">
      {node.children.length > 0 ? (
        // 配下がある組織は「所属 22名（直 1名）」。末端の組織は直属と同じなので「所属 N名」だけにする
        <span title={`配下を含めて ${node.totalPrimary}名が所属（うちこの組織に直接 ${h.primary}名）`}>
          所属 <span className="font-bold">{node.totalPrimary}</span>名<span className="text-muted">（直 {h.primary}名）</span>
        </span>
      ) : (
        <span>
          所属 <span className="font-bold">{h.primary}</span>名
        </span>
      )}
      {h.concurrent > 0 && (
        <span className="inline-flex items-center gap-0.5" title={`この組織で兼務している人（受け入れ。主務は他の組織） ${h.concurrent}名`}>
          <LineKey kind="concurrent" width={12} />
          兼務 {h.concurrent}
        </span>
      )}
      {h.secondment > 0 && (
        <span className="inline-flex items-center gap-0.5" title={`この組織に出向している人（受け入れ） ${h.secondment}名`}>
          <LineKey kind="secondment" width={12} />
          出向 {h.secondment}
        </span>
      )}
    </span>
  );
}

function OrgNodeCardImpl({ data }: NodeProps<OrgFlowNode>) {
  const { node, headName, hiddenChildren, open, delta, statsAvailable, exiting, onToggle, onSelect, onHover, siteMode, siteColor, direction, upper } = data;
  const isEntity = node.kind === "legal_entity";
  return (
    <div
      className={`org-card relative flex h-[76px] w-[232px] flex-col justify-between rounded-md border py-1.5 pr-2.5 text-xs shadow-sm ${siteMode && !isEntity ? "pl-3.5" : "pl-2.5"} ${
        isEntity ? "border-accent bg-accent text-accent-fg" : "border-border bg-surface text-fg"
      }`}
      style={{ pointerEvents: exiting ? "none" : undefined }}
      onFocus={() => onHover(node.id)}
      onBlur={() => onHover(null)}
    >
      {siteMode && siteColor && (
        // 拠点の色の帯（色だけに頼らず、下の行に拠点名を出す）
        <span aria-hidden="true" className="absolute inset-y-0 left-0 w-1.5 rounded-l-md" style={{ background: siteColor }} />
      )}
      <Handles direction={direction} type="target" />
      <div className="flex min-w-0 items-center gap-1.5">
        {node.levelName && (
          <span className="shrink-0 rounded-sm bg-surface-2 px-1 text-[10px] leading-4 text-muted">{node.levelName}</span>
        )}
        <button
          type="button"
          onClick={(e) => {
            e.stopPropagation();
            onSelect(node.id);
          }}
          className={`nodrag min-w-0 truncate text-left text-[13px] font-bold hover:underline ${isEntity ? "" : "text-fg"}`}
          title={node.name}
        >
          {node.name}
        </button>
        {delta !== 0 && (
          <span
            // 増減は良し悪しではないので、状態の色（警告など）は使わず、記号と符号で示す
            className={`tabular ml-auto shrink-0 rounded-sm border px-1 text-[10px] font-bold ${isEntity ? "border-accent-fg/50" : "border-border bg-surface-2 text-fg"}`}
            title={`前に表示した日付から、所属が ${Math.abs(delta)} 名${delta > 0 ? "増" : "減"}`}
          >
            <span aria-hidden="true">{delta > 0 ? "▲" : "▼"}</span>
            {delta > 0 ? `+${delta}` : `−${-delta}`}
          </span>
        )}
      </div>
      {isEntity ? (
        <div className="opacity-90">{node.code ? `法人コード ${node.code}` : "法人"}</div>
      ) : upper ? (
        // 拠点をまたぐ親子を見失わないように、本来の上位組織と、その拠点を示す（押すと上位を選ぶ）
        <div className="flex min-w-0 items-center gap-1 text-muted">
          <span className="shrink-0">上位</span>
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              onSelect(upper.id);
            }}
            className="nodrag min-w-0 truncate text-left text-fg hover:underline"
            title={`本来の上位組織: ${upper.name}（${upper.siteName ?? "拠点なし"}）`}
          >
            {upper.name}
          </button>
          <span className="shrink-0 truncate text-[10px]">（{upper.siteName ?? "拠点なし"}）</span>
        </div>
      ) : siteMode ? (
        <div className="flex min-w-0 items-center gap-1 text-muted">
          <span className="shrink-0">拠点</span>
          {node.site ? (
            <>
              <span aria-hidden="true" className="size-2.5 shrink-0 rounded-[3px]" style={{ background: siteColor ?? "transparent" }} />
              <span className="truncate text-fg" title={node.site.name}>
                {node.site.name}
              </span>
              {node.site.inherited && <span className="shrink-0 text-[10px]">（上位から）</span>}
            </>
          ) : (
            <span className="text-muted">— 未設定</span>
          )}
        </div>
      ) : (
        <div className="flex min-w-0 items-center gap-1 text-muted">
          <span className="shrink-0">組織長</span>
          <span className="truncate text-fg">
            {node.headPersonId ? (headName ?? "…") : <span className="text-muted">— 未設定または非公開</span>}
          </span>
        </div>
      )}
      <div className={`flex items-center justify-between gap-1 ${isEntity ? "opacity-90" : ""}`}>
        {isEntity ? <span>直属の組織 {node.children.length}</span> : <Counts node={node} statsAvailable={statsAvailable} />}
        {node.children.length > 0 && (
          <button
            type="button"
            onClick={(e) => {
              e.stopPropagation();
              onToggle(node.id);
            }}
            aria-expanded={open}
            aria-label={open ? `${node.name}の配下を閉じる（${node.children.length} 組織）` : `${node.name}の配下の組織 ${hiddenChildren} を開く`}
            title={open ? "配下を閉じる" : `配下の組織 ${hiddenChildren} を開く`}
            // 開閉で同じ位置・同じ高さ（20px）にし、「＋ N」と「−」を対にする
            className={`nodrag tabular -my-0.5 inline-flex h-5 shrink-0 items-center justify-center gap-0.5 rounded-sm border text-[11px] leading-4 ${
              open ? "w-5" : "min-w-5 px-1"
            } ${
              isEntity ? "border-accent-fg/50 hover:bg-accent-fg/15" : "border-border hover:bg-surface-2"
            }`}
          >
            <svg aria-hidden="true" viewBox="0 0 24 24" className="size-3.5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
              {open ? <path d="M6 12h12" /> : <path d="M6 12h12M12 6v12" />}
            </svg>
            {!open && hiddenChildren}
          </button>
        )}
      </div>
      <Handles direction={direction} type="source" />
    </div>
  );
}

export const OrgNodeCard = memo(OrgNodeCardImpl);
