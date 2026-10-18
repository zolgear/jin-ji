// 組織図と 3D の上端の操作の帯（共通の部品）。同じ順序・同じ高さ（32px のボタン）・同じ見た目にそろえる:
//   [表示の形（組織図だけ）] [向き（組織図だけ）] [拠点で分ける][拠点で色分け] 拠点の凡例 [兼務][出向] … 右寄せ: [全体の操作] [図の見方]
// 図の上に重ねると、スマホ幅では図の上端を隠すので、図の外（上）に置く。スマホ幅では折り返さずに横へ送れるようにして、図の高さを残す。
import type { ReactNode } from "react";
import { LineKey } from "../../components/ui";
import type { SiteColoring } from "../../lib/siteColors";
import { SiteColorToggle, SiteLegend, SiteSplitToggle } from "./SiteLegend";

/** 帯の外枠。 */
export function ChartToolbar({ children }: { children: ReactNode }) {
  // relative: 中の sr-only（position: absolute）の見出しの基準をこの帯にする。基準が帯の外（図の section）だと、横へ送った先の
  // 見出しが帯の横スクロールの外に出て、ページ全体の幅を広げる（スマホでは画面が横にずれ、下からのシートも左が切れる）
  return (
    <div className="relative flex shrink-0 items-center gap-x-4 gap-y-2 overflow-x-auto border-b border-border bg-surface px-4 py-2.5 text-[13px] whitespace-nowrap max-md:[&>*]:shrink-0 md:flex-wrap md:px-5">
      {children}
    </div>
  );
}

/** 帯の右寄せの部分（全体の操作と「図の見方」）。 */
export function ChartToolbarEnd({ children }: { children: ReactNode }) {
  return <span className="ml-auto flex items-center gap-2">{children}</span>;
}

/** 帯のボタン（「すべて開く」「全体を表示」など）。 */
export function ToolbarButton({ onClick, children, title }: { onClick: () => void; children: ReactNode; title?: string }) {
  return (
    <button type="button" title={title} className="h-8 rounded-md border border-border px-3 hover:bg-surface-2" onClick={onClick}>
      {children}
    </button>
  );
}

/** 「拠点で分ける」「拠点で色分け」の切り替えと、色分けしているときの凡例。 */
export function SiteModeControls({
  split,
  onSplit,
  color,
  onColor,
  coloring,
}: {
  split: boolean;
  onSplit: (on: boolean) => void;
  color: boolean;
  onColor: (on: boolean) => void;
  coloring: SiteColoring | null;
}) {
  return (
    <>
      <span className="flex items-center gap-1.5">
        <SiteSplitToggle on={split} onChange={onSplit} className="h-8 rounded-md px-3" />
        <SiteColorToggle on={color} onChange={onColor} className="h-8 rounded-md px-3" />
      </span>
      {color && coloring && <SiteLegend coloring={coloring} className="gap-x-4" />}
    </>
  );
}

/** 兼務・出向の線の種類の切り替え（チェックボックス）。children には種類のほかの操作（線の出し方など）を続けられる。 */
export function CrossKindFilter({
  concurrent,
  onConcurrent,
  secondment,
  onSecondment,
  available,
  hidden = false,
  children,
}: {
  concurrent: boolean;
  onConcurrent: (on: boolean) => void;
  secondment: boolean;
  onSecondment: (on: boolean) => void;
  /** 集計があるか（無ければ操作できない）。 */
  available: boolean;
  /** 拠点で色分けしている間は出さない（色の意味が重なり、線を出さないため）。 */
  hidden?: boolean;
  children?: ReactNode;
}) {
  return (
    <fieldset className={`m-0 ${hidden ? "hidden" : "flex"} flex-wrap items-center gap-x-4 gap-y-1 border-0 p-0 ${available ? "" : "opacity-50"}`}>
      <legend className="sr-only">兼務・出向の線</legend>
      <label className="flex items-center gap-1.5">
        <input type="checkbox" checked={concurrent} disabled={!available} onChange={(e) => onConcurrent(e.target.checked)} />
        <LineKey kind="concurrent" />
        兼務
      </label>
      <label className="flex items-center gap-1.5">
        <input type="checkbox" checked={secondment} disabled={!available} onChange={(e) => onSecondment(e.target.checked)} />
        <LineKey kind="secondment" />
        出向
      </label>
      {children}
    </fieldset>
  );
}

/** 集計が無いときの印。 */
export function NoStatsBadge({ title }: { title: string }) {
  return (
    <span className="rounded-sm bg-private-bg px-2 text-xs leading-5 text-private-fg" title={title}>
      人数の集計なし
    </span>
  );
}
