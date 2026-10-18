// 「拠点で色分け」「拠点で分ける」の切り替えと凡例（組織図・3D で共通）。切り替えは URL（color=site・split=site）に持ち、
// 組織図と 3D の間で引き継ぐ。2 つは組み合わせられる（分けた区画の見出し・枠に拠点の色を使う）。
import { useCallback } from "react";
import { useRoute } from "../../lib/router";
import { SITE_COLOR_VARS, SITE_OTHER_VAR, type SiteColoring } from "../../lib/siteColors";

/** 拠点で色分けしているか（URL の color=site）と、その切り替え。 */
export function useSiteColorMode(): [boolean, (on: boolean) => void] {
  const { route, navigate } = useRoute();
  const on = route.params.get("color") === "site";
  const set = useCallback((next: boolean) => navigate({ params: { color: next ? "site" : null } }, true), [navigate]);
  return [on, set];
}

/** 拠点で分けているか（URL の split=site）と、その切り替え。 */
export function useSiteSplitMode(): [boolean, (on: boolean) => void] {
  const { route, navigate } = useRoute();
  const on = route.params.get("split") === "site";
  const set = useCallback((next: boolean) => navigate({ params: { split: next ? "site" : null } }, true), [navigate]);
  return [on, set];
}

/** 「拠点で分ける」の切り替え。className は「拠点で色分け」と同じ。 */
export function SiteSplitToggle({ on, onChange, className = "rounded-sm px-1.5" }: { on: boolean; onChange: (on: boolean) => void; className?: string }) {
  return (
    <button
      type="button"
      aria-pressed={on}
      onClick={() => onChange(!on)}
      title="拠点ごとに区画を分け、その拠点の組織だけで木を描きます。上位の組織が別の拠点にある組織は、区画の中で根になり「上位」を添えます"
      className={`pointer-events-auto inline-flex items-center gap-1 border ${className} ${on ? "border-accent bg-accent text-accent-fg" : "border-border hover:bg-surface-2"}`}
    >
      <svg aria-hidden="true" viewBox="0 0 12 12" className="size-3" fill="none" stroke="currentColor" strokeWidth="1.1">
        <rect x="0.75" y="1.5" width="4.5" height="9" rx="1" />
        <rect x="6.75" y="1.5" width="4.5" height="9" rx="1" strokeDasharray="1.6 1.1" />
      </svg>
      拠点で分ける
    </button>
  );
}

/** 「拠点で色分け」の切り替え。className は大きさ・角の丸み（既定は 3D の凡例の箱の中の小さな形）。 */
export function SiteColorToggle({ on, onChange, className = "rounded-sm px-1.5" }: { on: boolean; onChange: (on: boolean) => void; className?: string }) {
  return (
    <button
      type="button"
      aria-pressed={on}
      onClick={() => onChange(!on)}
      title={on ? "色分けの間は兼務・出向の線を出しません。色は人数の多い 3 拠点まで" : undefined}
      className={`pointer-events-auto inline-flex items-center gap-1 border ${className} ${on ? "border-accent bg-accent text-accent-fg" : "border-border hover:bg-surface-2"}`}
    >
      <svg aria-hidden="true" viewBox="0 0 12 12" className="size-3" fill="currentColor">
        <rect x="1" y="1" width="4.5" height="4.5" rx="1" />
        <rect x="6.5" y="1" width="4.5" height="4.5" rx="1" opacity="0.65" />
        <rect x="1" y="6.5" width="4.5" height="4.5" rx="1" opacity="0.45" />
        <rect x="6.5" y="6.5" width="4.5" height="4.5" rx="1" opacity="0.25" />
      </svg>
      拠点で色分け
    </button>
  );
}

function Swatch({ color }: { color: string }) {
  return <span aria-hidden="true" className="inline-block size-2.5 shrink-0 rounded-[3px]" style={{ background: color }} />;
}

/** 凡例。色を付けた拠点（名前と人数）と「その他」。人数は見える人だけの集計（主務）。 */
export function SiteLegend({ coloring, className = "" }: { coloring: SiteColoring; className?: string }) {
  const unit = coloring.byHeadcount ? "名" : " 組織";
  const otherWeight = coloring.other.reduce((n, e) => n + e.weight, 0);
  if (coloring.colored.length === 0) return <span className={`text-muted ${className}`}>拠点を持つ組織がありません</span>;
  return (
    <ul aria-label="拠点の色の凡例" className={`m-0 flex list-none flex-wrap items-center gap-x-3 gap-y-0.5 p-0 ${className}`}>
      {coloring.colored.map((e) => (
        <li key={e.site.id} className="inline-flex items-center gap-1">
          <Swatch color={SITE_COLOR_VARS[e.slot ?? 0]!} />
          <span className="text-fg">{e.site.name}</span>
          <span className="tabular text-muted">
            {e.weight}
            {unit}
          </span>
        </li>
      ))}
      {coloring.other.length > 0 && (
        <li className="inline-flex items-center gap-1" title={coloring.other.map((e) => e.site.name).join("・")}>
          <Swatch color={SITE_OTHER_VAR} />
          <span className="text-fg">その他 {coloring.other.length} 拠点</span>
          <span className="tabular text-muted">
            {otherWeight}
            {unit}
          </span>
        </li>
      )}
    </ul>
  );
}
