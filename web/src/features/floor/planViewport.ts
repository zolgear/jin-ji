// 2D の平面図の表示範囲（SVG の viewBox）のズーム・パン（純粋な関数）。単位は配置図の mm。
// SVG の y は下向きなので、配置図の y（奥が正）は描くときに反転する（toSvgY）。

export interface Viewport {
  /** 表示の中心（配置図の mm。y は SVG の向き＝反転後）。 */
  cx: number;
  cy: number;
  /** 表示する幅・高さ（mm）。 */
  w: number;
  h: number;
}

export interface PlanBounds {
  minX: number;
  minY: number;
  maxX: number;
  maxY: number;
}

/** 配置図の y を SVG の y にする（外接矩形の上端を 0 に）。 */
export const toSvgY = (y: number, bounds: PlanBounds) => bounds.maxY - y;

/** 全体が収まる表示範囲（aspect は表示領域の幅 / 高さ。pad は余白の割合）。 */
export function fitViewport(bounds: PlanBounds, aspect: number, pad = 0.04): Viewport {
  const bw = Math.max(1, bounds.maxX - bounds.minX);
  const bh = Math.max(1, bounds.maxY - bounds.minY);
  let w = bw * (1 + pad * 2);
  let h = bh * (1 + pad * 2);
  const a = aspect > 0 && Number.isFinite(aspect) ? aspect : 1;
  if (w / h > a) h = w / a;
  else w = h * a;
  return { cx: bounds.minX + bw / 2, cy: bh / 2, w, h };
}

/** 画面の点（表示領域の中の 0〜1 の割合 fx・fy）を中心に factor 倍する（factor > 1 で拡大）。 */
export function zoomAt(v: Viewport, factor: number, fx: number, fy: number, limits: { minW: number; maxW: number }): Viewport {
  const w = Math.min(limits.maxW, Math.max(limits.minW, v.w / factor));
  const k = w / v.w;
  const h = v.h * k;
  // 指した点の配置図の座標を保つ
  const px = v.cx - v.w / 2 + fx * v.w;
  const py = v.cy - v.h / 2 + fy * v.h;
  return { cx: px - (fx - 0.5) * w, cy: py - (fy - 0.5) * h, w, h };
}

/** 画面の px だけ動かす（viewWidthPx は表示領域の幅の px）。 */
export function panBy(v: Viewport, dxPx: number, dyPx: number, viewWidthPx: number): Viewport {
  const k = v.w / Math.max(1, viewWidthPx);
  return { ...v, cx: v.cx - dxPx * k, cy: v.cy - dyPx * k };
}

/** 点（配置図の mm、SVG の向き）を中心に、幅 w で見る。 */
export function centerOn(v: Viewport, x: number, y: number, w = v.w): Viewport {
  const k = w / v.w;
  return { cx: x, cy: y, w, h: v.h * k };
}

export const viewBoxOf = (v: Viewport) => `${(v.cx - v.w / 2).toFixed(0)} ${(v.cy - v.h / 2).toFixed(0)} ${v.w.toFixed(0)} ${v.h.toFixed(0)}`;
