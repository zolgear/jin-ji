// 建物・フロアモードの 3D の配置（three に依存しない純粋な関数）。仕様 §5.1 の案 B（分解アクソメ）・§5.3 の圧縮と LOD。
//
// - 単位は m（配置図の mm を 1/1000）。階の外形の左下を原点に、x は右、z は手前（配置図の y の奥を -z にする）
// - 階は大きく離して積む（分解）。階が多い建物（12 階超）は、選んだ階の前後 2 階だけを離し、ほかは詰めて積む（圧縮）
// - 拠点の段（建物を選んでいない）では、建物を横に並べ、各建物は詰めて積む
import type { Building } from "../../data/types";

/** 分解して離すときの階の間隔（奥行きに対する割合）。 */
const EXPLODE_RATIO = 0.34;
/** 圧縮した階の間隔（m）。 */
const COMPRESSED_GAP = 1.6;
/** 拠点の段で建物を詰めて積むときの階高（m。建物の高さ＝階数に見えるように）。 */
const STOREY_HEIGHT = 4.2;
/** 分解の間隔の下限（m）。 */
const MIN_GAP = 6;
/** これより階の多い建物は圧縮する。 */
export const COMPRESS_THRESHOLD = 12;
/** 選んだ階の上の隙間（分解の間隔に対する倍率）。 */
const SELECTED_ABOVE = 1.5;
/** 選んだ階の前後で離す階の数。 */
const KEEP_AROUND = 2;

export interface StackedFloor {
  id: string;
  index: number;
  /** スラブの上面の高さ（m）。 */
  y: number;
  /** 圧縮して詰めた階か（札を間引く・描き込みを減らす）。 */
  compressed: boolean;
}

/**
 * 階の積み方。floors は level_order の昇順。selectedId があれば、その前後を離す。
 * depthM は建物の奥行き（m）。overview（拠点の段）はすべて詰める。
 */
export function stackFloors(
  floors: ReadonlyArray<{ id: string; level_order: number }>,
  selectedId: string | null,
  depthM: number,
  overview = false,
): StackedFloor[] {
  const sorted = [...floors].sort((a, b) => a.level_order - b.level_order);
  const gap = Math.max(MIN_GAP, depthM * EXPLODE_RATIO);
  const sel = sorted.findIndex((f) => f.id === selectedId);
  const many = sorted.length > COMPRESS_THRESHOLD;
  const out: StackedFloor[] = [];
  let y = 0;
  sorted.forEach((f, i) => {
    let compressed = overview;
    if (!overview && many) compressed = sel < 0 ? false : Math.abs(i - sel) > KEEP_AROUND;
    if (i > 0) {
      const prev = out[i - 1]!;
      // 2 つの階の間の隙間は、どちらかが離す階なら分解の間隔（選んでいない多い建物は少し詰める）
      const wide = !compressed || !prev.compressed;
      let step = overview ? STOREY_HEIGHT : wide ? (many && sel < 0 ? gap * 0.55 : gap) : COMPRESSED_GAP;
      // 選んだ階の上は少し広く空ける（上の階のスラブで選んだ階の席が隠れないように）
      if (!overview && sel >= 0 && i === sel + 1) step = Math.max(step, gap * SELECTED_ABOVE);
      y += step;
    }
    out.push({ id: f.id, index: i, y, compressed });
  });
  return out;
}

export interface PlacedBuilding {
  building: Building;
  /** 建物の外形の左下（m）の x・z のずらし。 */
  x: number;
  z: number;
  width: number;
  depth: number;
}

/** 拠点の段: 建物を横に並べる（間に奥行きの半分の隙間）。sizes は建物ごとの外形（m）。 */
export function placeBuildings(buildings: readonly Building[], sizes: ReadonlyMap<string, { width: number; depth: number }>): PlacedBuilding[] {
  let x = 0;
  return buildings.map((b) => {
    const s = sizes.get(b.id) ?? { width: 30, depth: 20 };
    const placed = { building: b, x, z: 0, width: s.width, depth: s.depth };
    x += s.width + Math.max(12, s.depth * 0.6);
    return placed;
  });
}

/** 配置図の mm を 3D の m にする（y の奥を -z に）。 */
export const toWorld = (xMm: number, yMm: number): [number, number] => [xMm / 1000, -yMm / 1000];

/**
 * 端末の性能が低いか（LOD を下げる: 選んだ階以外の点を描かず、札の数字だけにする。仕様 §5.3）。
 * hardwareConcurrency・deviceMemory が小さい端末と、初回のフレームが極端に遅い端末を低いとみなす。
 */
export function isLowEndDevice(nav: { hardwareConcurrency?: number; deviceMemory?: number } | undefined, firstFrameMs: number | null): boolean {
  if (!nav) return false;
  if ((nav.hardwareConcurrency ?? 8) <= 2) return true;
  if ((nav.deviceMemory ?? 8) <= 2) return true;
  return firstFrameMs !== null && firstFrameMs > 400;
}
