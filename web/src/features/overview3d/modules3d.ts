// 3D 俯瞰のモジュールのジオメトリ（段ごとに 1 組を作り、その段の組織すべてで InstancedMesh として共有する）。
//
// 単位の形（底面の面積 π・高さ 1）で作り、インスタンスの行列で x, z を radius 倍、y を高さ倍にする。
// 1 つのモジュールは 3 つの部品に分ける（部品ごとに 1 つの InstancedMesh。段 × 3 の描画呼び出し）:
// - shell: 台座・胴・溝・上面の縁（白基調の外装。台座と溝は頂点色で黒鉛色にする）
// - plate: 上面の板（データの色＝主務の青・拠点の色・退かせる灰）
// - lamp: 上面の縁の手前に置く小さな表示灯（アクセントの黄）
import * as THREE from "three";
import { PLATE_LEVEL, PLATE_SCALE, TIER_COUNT, TIER_SHAPES, TIER_SMOOTH, radiusAt, type Vec2 } from "./shapes3d";

/** 断面の点: s＝底面の形に対する倍率、y＝高さ（0〜1）、dark＝この点までの帯を黒鉛色にするか。 */
type ProfilePoint = readonly [s: number, y: number, dark?: boolean];

const D = true;

/** 上面の縁と板の凹み（全段で共通）。 */
const CROWN: ProfilePoint[] = [
  [0.88, 1.0],
  [PLATE_SCALE, 1.0],
  [PLATE_SCALE, PLATE_LEVEL, D],
];

/**
 * 段ごとの断面（下から上へ）。共通の文法: 黒鉛色の台座 → 白い胴 → 溝（パネルの継ぎ目）→ 面取りした上面の縁 → 凹んだ板。
 * 段ごとに台座の段数・溝の数・胴の絞りを変えて、同じ系列の別の製品に見せる。
 */
const PROFILES: readonly ProfilePoint[][] = [
  // 法人: 十二角の塔。二段の台座と 3 本の溝（リブ）
  [
    [1.0, 0], [1.0, 0.1, D], [0.96, 0.12, D], [0.96, 0.2, D], [0.92, 0.22, D],
    [0.92, 0.36], [0.89, 0.37, D], [0.89, 0.41, D], [0.92, 0.42, D],
    [0.92, 0.56], [0.89, 0.57, D], [0.89, 0.61, D], [0.92, 0.62, D],
    [0.92, 0.76], [0.89, 0.77, D], [0.89, 0.81, D], [0.92, 0.82, D],
    [0.92, 0.9], [0.88, 0.96], [0.86, 1.0], ...CROWN.slice(1),
  ],
  // 1 段目（本部）: 八角。二段の台座と溝 1 本
  [
    [1.0, 0], [1.0, 0.12, D], [0.96, 0.14, D], [0.96, 0.24, D], [0.92, 0.26, D],
    [0.92, 0.66], [0.89, 0.68, D], [0.89, 0.74, D], [0.92, 0.76, D],
    [0.92, 0.88], ...CROWN,
  ],
  // 2 段目（部）: 六角。台座と溝 1 本
  [
    [1.0, 0], [1.0, 0.16, D], [0.93, 0.2, D],
    [0.93, 0.68], [0.9, 0.7, D], [0.9, 0.76, D], [0.93, 0.78, D],
    [0.93, 0.88], ...CROWN,
  ],
  // 3 段目（課）: 円。旋盤で削ったような 2 本の溝
  [
    [1.0, 0], [1.0, 0.16, D], [0.93, 0.2, D],
    [0.93, 0.46], [0.9, 0.48, D], [0.9, 0.52, D], [0.93, 0.54, D],
    [0.93, 0.68], [0.9, 0.7, D], [0.9, 0.74, D], [0.93, 0.76, D],
    [0.93, 0.88], ...CROWN,
  ],
  // 4 段目以降（チーム）: 角を落とした四角。低い台座だけの簡素な形
  [
    [1.0, 0], [1.0, 0.14, D], [0.94, 0.18, D],
    [0.94, 0.86], ...CROWN,
  ],
];

/** 表示灯の向き（x 軸から z 軸への角度）。最初のカメラ（+z の斜め上）から見える手前のやや右。 */
const LAMP_ANGLE = Math.PI * 0.36;

/** 黒鉛色の部品の明るさ（頂点色。外装の色に掛ける）。 */
const DARK_TONE = 0.035;

interface Builder {
  positions: number[];
  normals: number[];
  uvs: number[];
  colors: number[];
  indices: number[];
}

function newBuilder(): Builder {
  return { positions: [], normals: [], uvs: [], colors: [], indices: [] };
}

function perimeter(pts: readonly Vec2[]): number[] {
  // 各頂点までの周長の割合（u 座標）
  const lens = [0];
  for (let i = 0; i < pts.length; i++) {
    const [x1, y1] = pts[i]!;
    const [x2, y2] = pts[(i + 1) % pts.length]!;
    lens.push(lens[i]! + Math.hypot(x2 - x1, y2 - y1));
  }
  const total = lens[lens.length - 1]!;
  return lens.map((l) => l / total);
}

/** 断面の 2 点の間の帯（形の各辺ごとに四角形）。 */
function band(b: Builder, pts: readonly Vec2[], smooth: boolean, p0: ProfilePoint, p1: ProfilePoint, uFrac: number[]) {
  const [s0, y0] = p0;
  const [s1, y1, dark] = p1;
  const tone = dark ? DARK_TONE : 1;
  const ds = s1 - s0;
  const dy = y1 - y0;
  // 側面のテクスチャは周に 6 回、高さ方向は 1 回（高さ 1 の単位で）
  const U = 6;
  for (let i = 0; i < pts.length; i++) {
    const j = (i + 1) % pts.length;
    const a = pts[i]!;
    const c = pts[j]!;
    const quad: [[number, number, number], [number, number, number], [number, number, number], [number, number, number]] = [
      [a[0] * s0, y0, a[1] * s0],
      [c[0] * s0, y0, c[1] * s0],
      [c[0] * s1, y1, c[1] * s1],
      [a[0] * s1, y1, a[1] * s1],
    ];
    // 外向きの目安（辺の中点の向き × 断面の傾き）
    const mx = (a[0] + c[0]) / 2;
    const mz = (a[1] + c[1]) / 2;
    const ml = Math.hypot(mx, mz) || 1;
    const hint = new THREE.Vector3((mx / ml) * dy, -ds * ml, (mz / ml) * dy);
    const v0 = new THREE.Vector3(...quad[0]);
    let n = new THREE.Vector3(...quad[1]).sub(v0).cross(new THREE.Vector3(...quad[3]).sub(v0));
    if (n.lengthSq() < 1e-12) n = new THREE.Vector3(...quad[2]).sub(v0).cross(new THREE.Vector3(...quad[3]).sub(v0));
    n.normalize();
    const flip = n.dot(hint) < 0;
    if (flip) n.negate();
    const base = b.positions.length / 3;
    quad.forEach((q, k) => {
      b.positions.push(...q);
      if (smooth) {
        // 円: 頂点ごとに放射方向の法線（断面の傾きを加える）
        const p = k === 0 || k === 3 ? a : c;
        const l = Math.hypot(p[0], p[1]) || 1;
        const v = new THREE.Vector3((p[0] / l) * dy, -ds * l, (p[1] / l) * dy).normalize();
        b.normals.push(v.x, v.y, v.z);
      } else {
        b.normals.push(n.x, n.y, n.z);
      }
      const u = (k === 0 || k === 3 ? uFrac[i]! : uFrac[i + 1]!) * U;
      // 水平な帯（上面の縁）は平面の座標で
      const horizontal = Math.abs(dy) < 1e-6;
      b.uvs.push(horizontal ? q[0] * 0.5 : u, horizontal ? q[2] * 0.5 : (k < 2 ? y0 : y1) * 0.5);
      b.colors.push(tone, tone, tone);
    });
    if (flip) b.indices.push(base, base + 3, base + 1, base + 1, base + 3, base + 2);
    else b.indices.push(base, base + 1, base + 3, base + 1, base + 2, base + 3);
  }
}

/** 水平な多角形（扇形に三角形を張る）。up＝上向きの面か。 */
function cap(b: Builder, pts: readonly Vec2[], s: number, y: number, up: boolean, tone: number) {
  const base = b.positions.length / 3;
  b.positions.push(0, y, 0);
  b.normals.push(0, up ? 1 : -1, 0);
  b.uvs.push(0.5, 0.5);
  b.colors.push(tone, tone, tone);
  for (const [x, z] of pts) {
    b.positions.push(x * s, y, z * s);
    b.normals.push(0, up ? 1 : -1, 0);
    b.uvs.push(0.5 + x * s * 0.5, 0.5 + z * s * 0.5);
    b.colors.push(tone, tone, tone);
  }
  const n = pts.length;
  for (let i = 0; i < n; i++) {
    const a = base + 1 + i;
    const c = base + 1 + ((i + 1) % n);
    // 上から見て反時計回り（x→z の正の向きは上から見ると時計回りなので、上向きの面は逆順）
    if (up) b.indices.push(base, c, a);
    else b.indices.push(base, a, c);
  }
}

function toGeometry(b: Builder, withColors: boolean): THREE.BufferGeometry {
  const g = new THREE.BufferGeometry();
  g.setAttribute("position", new THREE.Float32BufferAttribute(b.positions, 3));
  g.setAttribute("normal", new THREE.Float32BufferAttribute(b.normals, 3));
  g.setAttribute("uv", new THREE.Float32BufferAttribute(b.uvs, 2));
  if (withColors) g.setAttribute("color", new THREE.Float32BufferAttribute(b.colors, 3));
  g.setIndex(b.indices);
  g.computeBoundingSphere();
  return g;
}

export interface ModuleGeometry {
  shell: THREE.BufferGeometry;
  plate: THREE.BufferGeometry;
  lamp: THREE.BufferGeometry;
}

/** 段 tier のモジュールのジオメトリ。 */
export function buildModule(tier: number): ModuleGeometry {
  const pts = TIER_SHAPES[tier]!;
  const smooth = TIER_SMOOTH[tier]!;
  const profile = PROFILES[tier]!;
  const uFrac = perimeter(pts);

  const shell = newBuilder();
  for (let k = 0; k + 1 < profile.length; k++) band(shell, pts, smooth, profile[k]!, profile[k + 1]!, uFrac);
  cap(shell, pts, 1, 0, false, DARK_TONE);

  const plate = newBuilder();
  const last = profile[profile.length - 1]!;
  cap(plate, pts, last[0], last[1], true, 1);

  // 表示灯: 上面の縁（板と面取りの間）に載せた細長い箱
  const lamp = new THREE.BoxGeometry(0.06, 0.1, 0.32);
  const rim = radiusAt(tier, LAMP_ANGLE) * ((PLATE_SCALE + 0.88) / 2);
  lamp.rotateY(-LAMP_ANGLE);
  lamp.translate(Math.cos(LAMP_ANGLE) * rim, 1.0 + 0.05, Math.sin(LAMP_ANGLE) * rim);
  lamp.deleteAttribute("uv");

  return { shell: toGeometry(shell, true), plate: toGeometry(plate, false), lamp };
}

/** すべての段のモジュール。 */
export function buildModules(): ModuleGeometry[] {
  return Array.from({ length: TIER_COUNT }, (_, t) => buildModule(t));
}

/**
 * 黄と黒鉛色の斜めの縞の輪（選んだ組織の印。色に頼らず、縞の模様と輪の形で分かる）。
 * 縞の幅がおよそ stripe になるように、周の長さから縞の数を決める。
 */
export function hazardRing(inner: number, outer: number, stripe: number, yellow: THREE.Color, dark: THREE.Color): THREE.BufferGeometry {
  const n = Math.max(12, Math.round((Math.PI * 2 * outer) / stripe / 2) * 2);
  const positions: number[] = [];
  const colors: number[] = [];
  const skew = (Math.PI * 2) / n / 2; // 内側を半分ずらして斜めにする
  for (let i = 0; i < n; i++) {
    const a0 = (i / n) * Math.PI * 2;
    const a1 = ((i + 1) / n) * Math.PI * 2;
    const c = i % 2 === 0 ? yellow : dark;
    const o0: [number, number, number] = [Math.cos(a0) * outer, 0, Math.sin(a0) * outer];
    const o1: [number, number, number] = [Math.cos(a1) * outer, 0, Math.sin(a1) * outer];
    const i0: [number, number, number] = [Math.cos(a0 + skew) * inner, 0, Math.sin(a0 + skew) * inner];
    const i1: [number, number, number] = [Math.cos(a1 + skew) * inner, 0, Math.sin(a1 + skew) * inner];
    // 上から見えるように（上から見て反時計回り）
    for (const v of [o0, i1, o1, o0, i0, i1]) {
      positions.push(...v);
      colors.push(c.r, c.g, c.b);
    }
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
  g.setAttribute("color", new THREE.Float32BufferAttribute(colors, 3));
  return g;
}
