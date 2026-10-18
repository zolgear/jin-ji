// 3D 俯瞰のモジュールの「形の文法」（three に依存しない幾何だけ。layout3d.ts と modules3d.ts が共有する）。
//
// 組織は段（法人・1〜4 段目）ごとに底面の形を変えた工業製品風のモジュールで表す:
//   法人＝十二角の塔、1 段目＝八角、2 段目＝六角、3 段目＝円、4 段目以降＝角を落とした四角
// 形が違っても「底面の面積＝主務の人数」を保つため、どの形も底面の面積を半径 1 の円と同じ π に揃える
// （radius は「底面と同じ面積の円の半径」。形の外接円は radius × reach で、場所取りに使う）。
// 断面（台座・胴・溝・上面の縁と板）の構成は全段で共通にし、段ごとに台座の段数・溝の数だけを変える。
import type { OrgNode } from "../../lib/orgTree";

export type Vec2 = readonly [number, number];

/** 段の数（0＝法人、1〜4＝組織の段）。 */
export const TIER_COUNT = 5;

/** 上面の板の高さ（モジュールの高さに対する割合）。人の点はこの上に置く。 */
export const PLATE_LEVEL = 0.955;
/** 上面の板の大きさ（底面の形に対する倍率）。 */
export const PLATE_SCALE = 0.78;

/** 組織の形の種類。 */
export function tierOf(node: Pick<OrgNode, "kind" | "depth">): number {
  if (node.kind === "legal_entity") return 0;
  return Math.max(1, Math.min(node.depth, TIER_COUNT - 1));
}

function regular(n: number, phase: number): Vec2[] {
  return Array.from({ length: n }, (_, i) => {
    const a = phase + (i / n) * Math.PI * 2;
    return [Math.cos(a), Math.sin(a)] as const;
  });
}

/** 正方形の角を c（辺の半分に対する割合）だけ落とした八角形。 */
function chamferedSquare(c: number): Vec2[] {
  const a = 1;
  const k = a * (1 - c);
  return [
    [a, -k], [a, k], [k, a], [-k, a], [-a, k], [-a, -k], [-k, -a], [k, -a],
  ];
}

/** 多角形の面積（反時計回りで正）。 */
export function polygonArea(points: readonly Vec2[]): number {
  let s = 0;
  for (let i = 0; i < points.length; i++) {
    const [x1, y1] = points[i]!;
    const [x2, y2] = points[(i + 1) % points.length]!;
    s += x1 * y2 - x2 * y1;
  }
  return s / 2;
}

/** 面積を π（半径 1 の円）に揃える。 */
function normalizeArea(points: Vec2[]): Vec2[] {
  const k = Math.sqrt(Math.PI / Math.abs(polygonArea(points)));
  return points.map(([x, y]) => [x * k, y * k] as const);
}

/**
 * 段ごとの底面の形（x, z の平面。面積は π）。円は 40 角形で近似する。
 * 多角形は手前（+z、最初のカメラの向き）に辺が来る向きにする（面の陰影で形が読みやすい）。
 */
export const TIER_SHAPES: readonly (readonly Vec2[])[] = [
  normalizeArea(regular(12, Math.PI / 12)),
  normalizeArea(regular(8, Math.PI / 8)),
  normalizeArea(regular(6, 0)),
  normalizeArea(regular(40, 0)),
  normalizeArea(chamferedSquare(0.32)),
];

/** 円として滑らかに陰影を付ける形か。 */
export const TIER_SMOOTH: readonly boolean[] = [false, false, false, true, false];

/** 段の名前（凡例）。 */
export const TIER_NAMES = ["法人", "1段目", "2段目", "3段目", "4段目以降"] as const;

/** 形の外接円の半径（面積 π の形の中心から最も遠い点）。 */
export function reachOf(tier: number): number {
  return Math.max(...TIER_SHAPES[tier]!.map(([x, y]) => Math.hypot(x, y)));
}

/** 形の内接円の半径（中心から最も近い辺）。 */
export function apothemOf(tier: number): number {
  const pts = TIER_SHAPES[tier]!;
  let m = Infinity;
  for (let i = 0; i < pts.length; i++) {
    const [x1, y1] = pts[i]!;
    const [x2, y2] = pts[(i + 1) % pts.length]!;
    // 原点から辺（直線）までの距離
    m = Math.min(m, Math.abs(x1 * y2 - x2 * y1) / Math.hypot(x2 - x1, y2 - y1));
  }
  return m;
}

/** 人の点を並べてよい半径（上面の板の内接円の 9 割。radius に掛ける）。 */
export function plateInnerOf(tier: number): number {
  return apothemOf(tier) * PLATE_SCALE * 0.9;
}

/** 形の中心から角度 angle（x 軸から z 軸へ）の向きに、縁までの距離。 */
export function radiusAt(tier: number, angle: number): number {
  const pts = TIER_SHAPES[tier]!;
  const dx = Math.cos(angle);
  const dy = Math.sin(angle);
  for (let i = 0; i < pts.length; i++) {
    const [x1, y1] = pts[i]!;
    const [x2, y2] = pts[(i + 1) % pts.length]!;
    // 原点からの半直線と辺の交点
    const ex = x2 - x1;
    const ey = y2 - y1;
    const den = dx * ey - dy * ex;
    if (Math.abs(den) < 1e-9) continue;
    const t = (x1 * ey - y1 * ex) / den;
    const u = (x1 * dy - y1 * dx) / den;
    if (t > 0 && u >= -1e-9 && u <= 1 + 1e-9) return t;
  }
  return 1;
}
