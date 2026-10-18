// 3D 組織俯瞰（React Three Fiber）。
//
// この画面で読み取らせること（form）:
// - 組織の「形」: 法人ごとに、どの深さにどれだけ組織が広がっているか（上の段ほど上位。段＝組織の深さ）
// - 組織の「規模」: モジュールの底面の面積＝主務の人数。近づくと人を点で表す（組織ごとに最大 120）
// - 選んだ組織の「つながり」: 上位・配下の線を強調し、兼務・出向の相手先を弧で結ぶ（相手先には人数のラベル）
// 数値を正確に比べるのは組織図の「表」・右の組織一覧・所属者パネルの役目で、3D は全体の形と規模感の把握に使う。
//
// 見た目（白基調のワイヤーフレームの電脳空間に、工業製品風のモジュールを置く）:
// - 空間: 細い格子の床（遠くへ薄れる）・段ごとのワイヤーの円盤（目盛り付きの輪）・法人の軸線・距離のフォグ・
//   控えめな走査線（CSS の重ね。prefers-reduced-motion では止める）。ダークはグラファイトの地に白いワイヤー（自動反転ではなく値を選ぶ）
// - 組織: 段ごとに底面の形を変えたモジュール（shapes3d.ts・modules3d.ts。法人＝十二角の塔、1 段目＝八角、2 段目＝六角、3 段目＝円、
//   4 段目以降＝角を落とした四角）。形は違っても底面の面積を揃え、面積＝主務の人数を保つ。白い外装・黒鉛色の台座と溝・上面の板・表示灯
// - テクスチャ（textures/。画像生成 AI で生成したグレースケールのタイル）は外装の凹凸・粗さと板の微細な凹凸にだけ使い、色はマテリアルで付ける。
//   読み込めないときはテクスチャ無しの形と色だけで成り立つ
//
// 色（dataviz の categorical 3 枠。src/index.css と同じ値）: 主務の人数＝青、兼務＝橙（破線）、出向＝緑（点線）。データの色は上面の板に付ける。
// 組織を選んだときは、関係する組織だけを青のまま残し、それ以外を灰色にして地の色へ寄せて薄くする（段の輪・島も。「選んだ組織に関係しないものを薄くする」）。選んだ組織は黄と黒鉛色の縞の輪で囲む（色に頼らない印）。
// 法人の根はデータではないので文字色の灰。
// アクセントの黄（--signal）は選択・ホバー・表示灯・段の目印の線にだけ使い、データ・状態の色には使わない。
// 白地では 3:1 に届かないので、黄は必ず黒鉛色と組にし（縞・表示灯の台）、文字には使わない。
// 「拠点で色分け」では、上面の板を拠点の色（categorical の 1〜3 枠。人数の多い 3 拠点まで、残りは灰色の「その他」。lib/siteColors.ts）にし、
// 色の意味が重なるので兼務・出向の弧は出さない。拠点名は指したときの説明と凡例に文字で出す。
// 「拠点で分ける」（URL の split=site。2D と共有）では、拠点の区画ごとの島に分けて床の上に離して並べる（layout3dSplit）。
// 島の表現は控えめに: 床の上の細い輪（色分けの間は拠点の色）と、拠点名・人数の札だけ。段の高さは島をまたいで共通。
// 拠点をまたぐ親子は、選んだ組織に関わるものだけを細い破線で結ぶ。切り替え・時点の変更では、モジュールが前の位置から滑らかに移る
// （prefers-reduced-motion では即時）。
//
// 1 万人規模でも破綻しないように:
// - 組織は段ごとに部品（外装・板・表示灯）を 1 つずつの InstancedMesh にし、ジオメトリは段ごとに共有（描画呼び出しは十数回）。
//   指す・選ぶの判定は、低い多角形の円柱 1 つの InstancedMesh（描かない）で行う
// - テクスチャは 3 枚（512px 以下・ミップマップ）。ポストプロセスは使わない
// - 人は組織ごとに点の数を上限で切り、超えた分は底面の大きさで表す（集約）
// - 人の点は近づいたときだけ描く（LOD）
// - ラベルは HTML で重ね、優先度の高い順に置いて、重なるものは出さない（毎フレームの投影と矩形の衝突判定）
// - frameloop="demand" で、操作や遷移があるときだけ描き直す（走査線は CSS なので描き直しを起こさない）
import { Canvas, useFrame, useThree, type ThreeEvent } from "@react-three/fiber";
import { useReducedMotion } from "motion/react";
import { Component, useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from "react";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { RoomEnvironment } from "three/examples/jsm/environments/RoomEnvironment.js";
import { Line2 } from "three/examples/jsm/lines/Line2.js";
import { LineGeometry } from "three/examples/jsm/lines/LineGeometry.js";
import { LineMaterial } from "three/examples/jsm/lines/LineMaterial.js";
import { useOrgStats, useOrgTree } from "../../app/hooks";
import { useNav } from "../../app/nav";
import { HelpTip } from "../../components/popover";
import { ErrorNote, LineKey, Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { describeError, type OrgLink } from "../../data/types";
import { buildForest } from "../../lib/orgTree";
import { assignSiteColors, type SiteColoring } from "../../lib/siteColors";
import { partitionName, splitBySite, type SiteSplit } from "../../lib/siteSplit";
import { ChartToolbar, ChartToolbarEnd, CrossKindFilter, NoStatsBadge, SiteModeControls, ToolbarButton } from "../orgchart/ChartToolbar";
import { useSiteColorMode, useSiteSplitMode } from "../orgchart/SiteLegend";
import { useResolvedTheme } from "../../lib/theme";
import { boundingSphere, heightOf, layout3d, layout3dSplit, personDots, type Island3D, type Layer3D, type Placed3D, type Scene3D } from "./layout3d";
import { buildModules, hazardRing } from "./modules3d";
import { TIER_COUNT, TIER_NAMES, TIER_SHAPES } from "./shapes3d";
import beadUrl from "./textures/bead.webp";
import hairlineUrl from "./textures/hairline.webp";
import panelUrl from "./textures/panel.webp";

const PALETTE = {
  light: {
    bg: "#f4f6f8",
    primary: "#2a78d6",
    deemph: "#c3c9d1",
    entity: "#556274",
    edge: "#8a96a6",
    concurrent: "#eb6834",
    secondment: "#1baf7a",
    dot: "#ffffff",
    // 拠点で色分け（src/index.css の --site-1〜3・--site-other と同じ値）
    site: ["#2a78d6", "#eb6834", "#1baf7a"],
    siteOther: "#8a96a6",
    // 空間（ワイヤー・床の格子）
    wire: "#9aa5b2",
    grid: "#c9d0d8",
    // モジュールの外装（白基調）。退かせた組織は地に寄せ、法人は黒鉛色
    shell: "#eef1f4",
    shellDeemph: "#e6e9ed",
    shellEntity: "#59636f",
    // アクセント（src/index.css の --signal・--signal-ink）
    signal: "#ffd400",
    signalInk: "#1d232a",
    lampOff: "#5b6470",
    light: { ambient: 0.5, key: 1.35, env: 0.5 },
  },
  dark: {
    bg: "#0f141a",
    primary: "#3987e5",
    deemph: "#3a4553",
    entity: "#a0acba",
    edge: "#6b7889",
    concurrent: "#d95926",
    secondment: "#199e70",
    dot: "#e5eaf0",
    site: ["#3987e5", "#d95926", "#199e70"],
    siteOther: "#6b7889",
    wire: "#7d8a99",
    grid: "#34404c",
    shell: "#b9c1ca",
    shellDeemph: "#3b4450",
    shellEntity: "#6f7b88",
    signal: "#ffd400",
    signalInk: "#11161c",
    lampOff: "#3a4553",
    light: { ambient: 0.35, key: 1.3, env: 0.35 },
  },
} as const;
type Palette = (typeof PALETTE)[keyof typeof PALETTE];

/** 最初のカメラの向き（注視点からカメラへ）。斜め上 約 30° から見下ろす。 */
const VIEW_DIR = new THREE.Vector3(0, 0.58, 1).normalize();
/**
 * 縦長の画面（スマホ）での向き。法人は x 方向に並ぶので、x 軸に沿って斜め上から見ると、
 * 奥の法人が画面の上、手前の法人が下に並び、横幅に縛られて小さくなるのを避けられる。
 */
const VIEW_DIR_PORTRAIT = new THREE.Vector3(0.9, 0.85, 0.45).normalize();
const viewDir = (aspect: number) => (aspect < 0.85 ? VIEW_DIR_PORTRAIT : VIEW_DIR);

/** WebGL が使えない環境（古い端末・ヘッドレス）での代替表示。 */
class WebGlBoundary extends Component<{ children: ReactNode }, { failed: boolean }> {
  override state = { failed: false };
  static getDerivedStateFromError() {
    return { failed: true };
  }
  override render() {
    if (this.state.failed) {
      return (
        <div className="p-4">
          <ErrorNote>3D 表示を初期化できませんでした（WebGL が使えない環境です）。組織図または右の組織一覧をお使いください。</ErrorNote>
        </div>
      );
    }
    return this.props.children;
  }
}

function useOrbitControls(reduced: boolean) {
  const { camera, gl, invalidate } = useThree();
  const [controls, setControls] = useState<OrbitControls | null>(null);
  useEffect(() => {
    const c = new OrbitControls(camera, gl.domElement);
    c.enableDamping = !reduced;
    c.dampingFactor = 0.12;
    c.screenSpacePanning = true;
    // ホイールの拡大・縮小はカーソルの位置へ向ける。組織を選ぶと、選んだ組織が画面の中心に無い
    // （相手先まで収めるため上の方に来る）ので、中心へ寄せると選んだ組織が画面の外へ逃げてしまう
    c.zoomToCursor = true;
    // 真下・真上からは見せない（段の重なりが読めなくなる）
    c.minPolarAngle = Math.PI * 0.08;
    c.maxPolarAngle = Math.PI * 0.62;
    const onChange = () => invalidate();
    c.addEventListener("change", onChange);
    // OrbitControls は DOM にリスナーを付ける外部のオブジェクトなので、effect で作って state に置く
    // oxlint-disable-next-line react/set-state-in-effect
    setControls(c);
    return () => {
      c.removeEventListener("change", onChange);
      c.dispose();
    };
  }, [camera, gl, invalidate, reduced]);
  useFrame(() => {
    // 減衰の間は update が change を出して次のフレームを要求する
    controls?.update();
  });
  return controls;
}

/** 選んだ組織に関係する組織（自分・上位・配下・兼務出向の相手先）。 */
interface Focus {
  id: string;
  related: Set<string>;
  edgeChildren: Set<string>;
  links: OrgLink[];
  /** 拠点をまたぐ親子（拠点で分けたとき）: [上位, 区画の根] の組のうち、選んだ組織に関わるもの。 */
  upperPairs: Array<[string, string]>;
}

function isDescendant(byId: Map<string, Placed3D>, id: string, ancestor: string): boolean {
  let p = byId.get(id)?.node.parentId ?? null;
  while (p) {
    if (p === ancestor) return true;
    p = byId.get(p)?.node.parentId ?? null;
  }
  return false;
}

function focusOf(byId: Map<string, Placed3D>, id: string | null, links: OrgLink[], split: SiteSplit | null): Focus | null {
  if (!id || !byId.has(id)) return null;
  const related = new Set<string>([id]);
  const edgeChildren = new Set<string>([id]);
  // 上位
  let p = byId.get(id)?.node.parentId ?? null;
  let child = id;
  while (p) {
    related.add(p);
    edgeChildren.add(child);
    child = p;
    p = byId.get(p)?.node.parentId ?? null;
  }
  // 配下
  const walk = (nid: string) => {
    const n = byId.get(nid)?.node;
    n?.children.forEach((c) => {
      related.add(c.id);
      edgeChildren.add(c.id);
      walk(c.id);
    });
  };
  walk(id);
  // 兼務・出向: 配下を含めた組織（subtree）と外の組織の間のものを、選んだ組織と相手先の組に寄せて集計する
  // （組織図で閉じた組織の線を祖先に寄せるのと同じ考え方。配下の内側どうしの線は出さない）
  const inside = (x: string) => x === id || (edgeChildren.has(x) && isDescendant(byId, x, id));
  const merged = new Map<string, OrgLink>();
  for (const l of links) {
    if (!byId.has(l.from) || !byId.has(l.to)) continue;
    const fromIn = inside(l.from);
    const toIn = inside(l.to);
    if (fromIn === toIn) continue;
    const from = fromIn ? id : l.from;
    const to = toIn ? id : l.to;
    const key = `${l.kind}:${from}>${to}`;
    const m = merged.get(key) ?? { from, to, kind: l.kind, count: 0 };
    m.count += l.count;
    merged.set(key, m);
  }
  const mine = [...merged.values()];
  mine.forEach((l) => related.add(l.from === id ? l.to : l.from));
  // 拠点をまたぐ親子: 選んだ組織が区画の根ならその本来の上位、選んだ組織が上位なら別の拠点にある直下の組織
  const upperPairs: Array<[string, string]> = [];
  for (const [root, upper] of split?.upperOf ?? []) {
    if ((root === id || upper.id === id) && byId.has(root) && byId.has(upper.id)) {
      upperPairs.push([upper.id, root]);
      related.add(root);
      related.add(upper.id);
    }
  }
  return { id, related, edgeChildren, links: mine, upperPairs };
}

// ---- 選んだ組織に関係しないものを薄くする ----
//
// 組織を選んだら、関係する組織（自分・上位の系列・配下・兼務出向の相手先・拠点をまたぐ上位/配下）以外のモジュール・親子の線・
// 段の輪・段の札・島を薄くする。薄さは「地の色へ寄せる」で表す（シェーダーで最後の色をフォグの色＝地の色へ混ぜる）。
// 本当の半透明にすると、同じ InstancedMesh の中の描く順で奥の組織が手前の薄い組織に隠れてちらつくため、不透明のまま色だけ寄せる。
// 濃さは頂点（モジュールはインスタンス）ごとの aFade（1 = 通常、FADED = 薄い）で持ち、FADE_SECONDS で滑らかに変える
// （マテリアルは作り直さない。動きを減らす設定では即時）。

/** 関係しないものの濃さ。 */
const FADED = 0.16;
/** 濃さの変化の時間（秒。おおよそ 95% に届くまで）。 */
const FADE_SECONDS = 0.25;

/**
 * マテリアルに aFade（頂点またはインスタンスの属性）による「地の色へ寄せる」を足す。フォグを使うマテリアルだけ（地の色＝フォグの色）。
 * aFade を持たないジオメトリに使うと既定値 0（すべて地の色）になるので、必ず属性を付ける。
 */
function addFade<M extends THREE.Material>(m: M, key: string, extra?: (shader: THREE.WebGLProgramParametersWithUniforms) => void): M {
  m.onBeforeCompile = (shader) => {
    extra?.(shader);
    shader.vertexShader = shader.vertexShader
      .replace("#include <common>", "#include <common>\nattribute float aFade;\nvarying float vFade;")
      // begin_vertex は輪の回転（spinningLineMaterial）が置き換えるので、フォグの頂点処理の後ろで渡す
      .replace("#include <fog_vertex>", "#include <fog_vertex>\nvFade = aFade;");
    shader.fragmentShader = shader.fragmentShader
      .replace("#include <common>", "#include <common>\nvarying float vFade;")
      .replace("#include <fog_fragment>", "#include <fog_fragment>\n#ifdef USE_FOG\ngl_FragColor.rgb = mix( fogColor, gl_FragColor.rgb, vFade );\n#endif");
  };
  m.customProgramCacheKey = () => key;
  return m;
}

/**
 * current を target へ近づける（指数的に。FADE_SECONDS でおよそ届く）。変えたら true。
 * frameloop="demand" では、しばらく描いていないと最初の dt が大きいので、1 回の歩みを 1/30 秒までにする。
 */
function stepToward(current: Float32Array, target: Float32Array, dt: number, reduced: boolean): boolean {
  const k = reduced ? 1 : 1 - Math.exp(-(Math.min(dt, 1 / 30) * 3) / FADE_SECONDS);
  let changed = false;
  const n = Math.min(current.length, target.length);
  for (let i = 0; i < n; i++) {
    const d = target[i]! - current[i]!;
    if (d === 0) continue;
    current[i] = Math.abs(d) < 0.004 ? target[i]! : current[i]! + d * k;
    changed = true;
  }
  return changed;
}

/** 属性の値を target へ近づけ、変えたら GPU へ送り直す印を付ける（three のオブジェクトの変更なので、フックの外の関数で行う）。 */
function stepAttribute(attr: THREE.BufferAttribute, target: Float32Array, dt: number, reduced: boolean): boolean {
  if (!stepToward(attr.array as Float32Array, target, dt, reduced)) return false;
  attr.needsUpdate = true;
  return true;
}

/** 選んだ組織に関係する段（"島または法人の番号:深さ"）と島（番号）。選んでいなければ null（すべて通常の濃さ）。 */
interface Active {
  layers: Set<string>;
  islands: Set<number>;
}

function activeOf(focus: Focus | null, byId: Map<string, Placed3D>): Active | null {
  if (!focus) return null;
  const layers = new Set<string>();
  const islands = new Set<number>();
  for (const id of focus.related) {
    const p = byId.get(id);
    if (!p) continue;
    layers.add(`${p.entityIndex}:${p.node.depth}`);
    islands.add(p.entityIndex);
  }
  return { layers, islands };
}

// ---- 配置の切り替えの補間 ----

/** 補間の時間（秒）。2D の組織図（useTween）と同じくらい。 */
const MORPH_SECONDS = 0.8;

/** 配置の切り替え（拠点で分ける・時点の変更）の補間の状態。t は 0〜1（1 で終わり）。 */
interface Morph {
  from: Map<string, [number, number, number]>;
  t: number;
  active: boolean;
}

const morphEase = (t: number) => 1 - (1 - t) ** 3;

/** 補間の途中の組織の位置（前の配置に無い組織は最初から今の位置）。 */
function morphPos(m: Morph, p: Placed3D, out: THREE.Vector3): THREE.Vector3 {
  out.set(p.x, p.y, p.z);
  if (!m.active) return out;
  const f = m.from.get(p.node.id);
  if (!f) return out;
  const e = morphEase(m.t);
  return out.set(f[0] + (p.x - f[0]) * e, f[1] + (p.y - f[1]) * e, f[2] + (p.z - f[2]) * e);
}

/** scene が変わったら、前の配置の位置から補間を始める。部品は useFrame で morph.current を読んで位置を書き直す。 */
function useSceneMorph(scene: Scene3D, reduced: boolean): RefObject<Morph> {
  const morph = useRef<Morph>({ from: new Map(), t: 1, active: false });
  const previous = useRef<Scene3D | null>(null);
  const started = useRef(0);
  const { clock, invalidate } = useThree();
  useLayoutEffect(() => {
    const before = previous.current;
    previous.current = scene;
    if (!before || before === scene || reduced) {
      morph.current = { from: new Map(), t: 1, active: false };
      return;
    }
    morph.current = { from: new Map(before.nodes.map((p) => [p.node.id, [p.x, p.y, p.z]])), t: 0, active: true };
    started.current = clock.getElapsedTime();
    invalidate();
  }, [scene, reduced, clock, invalidate]);
  useFrame(() => {
    const m = morph.current;
    if (!m.active) return;
    m.t = Math.min(1, (clock.getElapsedTime() - started.current) / MORPH_SECONDS);
    if (m.t >= 1) m.active = false;
    invalidate();
  });
  return morph;
}

/** テクスチャ（グレースケールのタイル。色はマテリアルで付ける）。読み込めなければ null のまま、形と色だけで描く。 */
interface ModuleTextures {
  panel: THREE.Texture | null;
  hairline: THREE.Texture | null;
  bead: THREE.Texture | null;
}

function useModuleTextures(): ModuleTextures {
  const { gl, invalidate } = useThree();
  const [textures, setTextures] = useState<ModuleTextures>({ panel: null, hairline: null, bead: null });
  useEffect(() => {
    let alive = true;
    const loader = new THREE.TextureLoader();
    const loaded: THREE.Texture[] = [];
    const anisotropy = Math.min(4, gl.capabilities.getMaxAnisotropy());
    const load = (key: keyof ModuleTextures, url: string) =>
      loader.load(
        url,
        (t) => {
          // 鏡像で敷き詰めて継ぎ目を消す。データとして使う（色空間の変換をしない）
          t.wrapS = t.wrapT = THREE.MirroredRepeatWrapping;
          t.colorSpace = THREE.NoColorSpace;
          t.anisotropy = anisotropy;
          t.needsUpdate = true;
          loaded.push(t);
          if (!alive) {
            t.dispose();
            return;
          }
          setTextures((prev) => ({ ...prev, [key]: t }));
          invalidate();
        },
        undefined,
        // 読み込めなくても形と色だけで成り立つので、何もしない
        () => undefined,
      );
    load("panel", panelUrl);
    load("hairline", hairlineUrl);
    load("bead", beadUrl);
    return () => {
      alive = false;
      loaded.forEach((t) => t.dispose());
    };
  }, [gl, invalidate]);
  return textures;
}

/** 段ごとのモジュールの部品のマテリアル（全段で共有）。 */
/** マテリアルにテクスチャを付ける（null なら外す）。three のオブジェクトの変更なので、フックの外の関数で行う。 */
function applyTextures(materials: { shell: THREE.MeshStandardMaterial; plate: THREE.MeshStandardMaterial }, textures: ModuleTextures) {
  const { shell, plate } = materials;
  shell.bumpMap = textures.panel;
  shell.bumpScale = 1.4;
  shell.roughnessMap = textures.hairline;
  plate.bumpMap = textures.bead;
  plate.bumpScale = 0.6;
  shell.needsUpdate = true;
  plate.needsUpdate = true;
}

function useModuleMaterials(textures: ModuleTextures) {
  const materials = useMemo(
    () => ({
      // 白い外装（粉体塗装の金属）。台座・溝は頂点色で黒鉛色、組織ごとの色（退かせる・法人）はインスタンスの色
      // 外装・板・表示灯は、選んだ組織に関係しないものを地の色へ寄せる（aFade。インスタンスごと）
      shell: addFade(new THREE.MeshStandardMaterial({ color: "#ffffff", vertexColors: true, roughness: 0.42, metalness: 0.18 }), "fade-shell"),
      // 上面の板（データの色はインスタンスの色）
      plate: addFade(new THREE.MeshStandardMaterial({ color: "#ffffff", roughness: 0.55, metalness: 0.05 }), "fade-plate"),
      // 表示灯（光って見えるように陰影・トーンマッピングを付けない）
      lamp: addFade(new THREE.MeshBasicMaterial({ color: "#ffffff", toneMapped: false }), "fade-lamp"),
      // 指す・選ぶの判定だけに使う（色も深度も書かない）
      pick: new THREE.MeshBasicMaterial({ colorWrite: false, depthWrite: false }),
    }),
    [],
  );
  useEffect(() => applyTextures(materials, textures), [materials, textures]);
  useEffect(() => () => Object.values(materials).forEach((m) => m.dispose()), [materials]);
  return materials;
}

function Orgs({ scene, palette, focus, hoveredId, coloring, morph, onSelect, onHover }: {
  scene: Scene3D;
  morph: RefObject<Morph>;
  coloring: SiteColoring | null;
  palette: Palette;
  focus: Focus | null;
  hoveredId: string | null;
  onSelect: (p: Placed3D) => void;
  onHover: (p: Placed3D | null, e?: ThreeEvent<PointerEvent>) => void;
}) {
  const textures = useModuleTextures();
  const materials = useModuleMaterials(textures);
  const modules = useMemo(() => buildModules(), []);
  // 指す・選ぶの判定用の円柱（底を原点に置く）
  const pickGeometry = useMemo(() => new THREE.CylinderGeometry(1, 1, 1, 12).translate(0, 0.5, 0), []);
  useEffect(
    () => () => {
      modules.forEach((m) => Object.values(m).forEach((g: THREE.BufferGeometry) => g.dispose()));
      pickGeometry.dispose();
    },
    [modules, pickGeometry],
  );
  // 段ごとの組織（scene.nodes の添字）
  const byTier = useMemo(() => {
    const lists: number[][] = Array.from({ length: TIER_COUNT }, () => []);
    scene.nodes.forEach((p, i) => lists[p.tier]!.push(i));
    return lists;
  }, [scene]);
  const parts = useRef<Array<{ shell: THREE.InstancedMesh | null; plate: THREE.InstancedMesh | null; lamp: THREE.InstancedMesh | null }>>(
    Array.from({ length: TIER_COUNT }, () => ({ shell: null, plate: null, lamp: null })),
  );
  const pick = useRef<THREE.InstancedMesh>(null);
  const { invalidate } = useThree();
  const reduced = useReducedMotion() ?? false;

  // 濃さ（段ごとに 1 つのインスタンスの属性を、外装・板・表示灯のジオメトリで共有する）。組織の数が変わったら作り直す
  const fade = useMemo(
    () =>
      byTier.map((list) => ({
        attr: new THREE.InstancedBufferAttribute(new Float32Array(Math.max(1, list.length)).fill(1), 1),
        target: new Float32Array(Math.max(1, list.length)).fill(1),
      })),
    [byTier],
  );
  useLayoutEffect(() => {
    modules.forEach((g, t) => {
      for (const geometry of [g.shell, g.plate, g.lamp]) geometry.setAttribute("aFade", fade[t]!.attr);
    });
  }, [modules, fade]);
  useLayoutEffect(() => {
    byTier.forEach((list, t) => {
      const target = fade[t]!.target;
      list.forEach((ni, k) => {
        const id = scene.nodes[ni]!.node.id;
        target[k] = !focus || focus.related.has(id) ? 1 : FADED;
      });
    });
    invalidate();
  }, [scene, byTier, fade, focus, invalidate]);
  useFrame((_, dt) => {
    let moving = false;
    for (const f of fade) {
      if (stepAttribute(f.attr, f.target, dt, reduced)) moving = true;
    }
    if (moving) invalidate();
  });

  // 位置と大きさ（scene が変わったときだけ）
  useLayoutEffect(() => {
    const m = new THREE.Matrix4();
    const q = new THREE.Quaternion();
    const pos = new THREE.Vector3();
    const scl = new THREE.Vector3();
    byTier.forEach((list, t) => {
      const meshes = parts.current[t]!;
      list.forEach((ni, k) => {
        const p = scene.nodes[ni]!;
        m.compose(pos.set(p.x, p.y, p.z), q, scl.set(p.radius, heightOf(p), p.radius));
        for (const mesh of [meshes.shell, meshes.plate, meshes.lamp]) mesh?.setMatrixAt(k, m);
      });
      for (const mesh of [meshes.shell, meshes.plate, meshes.lamp]) {
        if (!mesh) continue;
        mesh.count = list.length;
        mesh.instanceMatrix.needsUpdate = true;
        mesh.computeBoundingSphere();
      }
    });
    const pm = pick.current;
    if (pm) {
      scene.nodes.forEach((p, i) => {
        m.compose(pos.set(p.x, p.y, p.z), q, scl.set(p.reach, heightOf(p), p.reach));
        pm.setMatrixAt(i, m);
      });
      pm.count = scene.nodes.length;
      pm.instanceMatrix.needsUpdate = true;
      pm.computeBoundingSphere();
    }
    invalidate();
  }, [scene, byTier, invalidate]);

  // 補間の途中は、外装・板・表示灯の位置を毎フレーム書き直す（指す・選ぶの判定は最後の位置のまま）。
  // applied は最後に当てた補間の位置（新しい配置が始まると t が 1 から 0 付近へ戻るので、当て直しが起きる）
  const applied = useRef(1);
  useFrame(() => {
    const m = morph.current;
    if (!m.active && applied.current === 1) return;
    const t = m.active ? m.t : 1;
    if (t === applied.current) return;
    applied.current = t;
    const mat = new THREE.Matrix4();
    const q = new THREE.Quaternion();
    const pos = new THREE.Vector3();
    const scl = new THREE.Vector3();
    byTier.forEach((list, tier) => {
      const meshes = parts.current[tier]!;
      list.forEach((ni, k) => {
        const p = scene.nodes[ni]!;
        mat.compose(morphPos(m, p, pos), q, scl.set(p.radius, heightOf(p), p.radius));
        for (const mesh of [meshes.shell, meshes.plate, meshes.lamp]) mesh?.setMatrixAt(k, mat);
      });
      for (const mesh of [meshes.shell, meshes.plate, meshes.lamp]) {
        if (!mesh) continue;
        mesh.instanceMatrix.needsUpdate = true;
        if (t === 1) mesh.computeBoundingSphere();
      }
    });
  });

  // 色（選択・ホバー・色分け・テーマが変わったとき）
  useLayoutEffect(() => {
    const color = new THREE.Color();
    const white = new THREE.Color("#ffffff");
    const shell = new THREE.Color();
    const lamp = new THREE.Color();
    byTier.forEach((list, t) => {
      const meshes = parts.current[t]!;
      list.forEach((ni, k) => {
        const p = scene.nodes[ni]!;
        const entity = p.node.kind === "legal_entity";
        const away = focus !== null && !focus.related.has(p.node.id);
        const hovered = p.node.id === hoveredId;
        const selected = focus?.id === p.node.id;
        // 上面の板: データの色
        if (away) color.set(palette.deemph);
        else if (entity) color.set(palette.entity);
        else if (coloring) {
          // 拠点で色分け: 色を付けた拠点・その他（灰）・拠点なし（退いた灰）
          const slot = p.node.site ? coloring.slotOf.get(p.node.site.id) : undefined;
          color.set(slot === undefined ? palette.deemph : slot === null ? palette.siteOther : palette.site[slot]!);
        } else color.set(palette.primary);
        if (hovered) color.lerp(white, 0.3);
        meshes.plate?.setColorAt(k, color);
        // 外装: 白。退かせた組織は地へ寄せ、法人は黒鉛色
        shell.set(entity ? palette.shellEntity : away ? palette.shellDeemph : palette.shell);
        meshes.shell?.setColorAt(k, shell);
        // 表示灯: 黄。退かせた組織は消し、選択・ホバーは明るく
        lamp.set(away ? palette.lampOff : palette.signal);
        if (selected || hovered) lamp.lerp(white, 0.45);
        meshes.lamp?.setColorAt(k, lamp);
      });
      for (const mesh of [meshes.shell, meshes.plate, meshes.lamp]) {
        if (mesh?.instanceColor) mesh.instanceColor.needsUpdate = true;
      }
    });
    invalidate();
  }, [scene, byTier, palette, focus, hoveredId, coloring, invalidate]);

  const capacity = (t: number) => Math.max(1, byTier[t]!.length);
  return (
    <group>
      {modules.map((g, t) => (
        // 段の組織の数が変わったら作り直す（InstancedMesh の容量は作るときに決まる）
        <group key={`${t}:${capacity(t)}`}>
          <instancedMesh ref={(m) => void (parts.current[t]!.shell = m)} args={[g.shell, materials.shell, capacity(t)]} raycast={() => null} />
          <instancedMesh ref={(m) => void (parts.current[t]!.plate = m)} args={[g.plate, materials.plate, capacity(t)]} raycast={() => null} />
          <instancedMesh ref={(m) => void (parts.current[t]!.lamp = m)} args={[g.lamp, materials.lamp, capacity(t)]} raycast={() => null} />
        </group>
      ))}
      <instancedMesh
        key={scene.nodes.length}
        ref={pick}
        args={[pickGeometry, materials.pick, Math.max(1, scene.nodes.length)]}
        onClick={(e) => {
          e.stopPropagation();
          const p = e.instanceId !== undefined ? scene.nodes[e.instanceId] : undefined;
          if (p) onSelect(p);
        }}
        onPointerMove={(e) => {
          e.stopPropagation();
          const p = e.instanceId !== undefined ? scene.nodes[e.instanceId] : undefined;
          onHover(p ?? null, e);
        }}
        onPointerOut={() => onHover(null)}
      />
    </group>
  );
}

/**
 * 選んだ組織の印: 黄と黒鉛色の斜めの縞の輪（色に頼らず模様と形で分かる）。
 * 動きを減らす設定でなければ、選んだときに黄の輪が一度だけ外へ広がって消える（どこが選ばれたかを目で追えるように）。
 */
function SelectionMarker({ placed, palette, reduced }: { placed: Placed3D | null; palette: Palette; reduced: boolean }) {
  const geometry = useMemo(() => {
    if (!placed) return null;
    const inner = placed.reach + 0.25;
    const width = Math.max(0.45, placed.reach * 0.14);
    return hazardRing(inner, inner + width, width * 1.5, new THREE.Color(palette.signal), new THREE.Color(palette.signalInk));
  }, [placed, palette]);
  useEffect(() => () => geometry?.dispose(), [geometry]);
  const ping = useRef<THREE.Mesh>(null);
  const started = useRef<number | null>(null);
  const { invalidate, clock } = useThree();
  useEffect(() => {
    started.current = placed && !reduced ? clock.getElapsedTime() : null;
    invalidate();
  }, [placed, reduced, clock, invalidate]);
  useFrame(() => {
    const mesh = ping.current;
    if (!mesh || !placed) return;
    if (started.current === null) {
      mesh.visible = false;
      return;
    }
    const t = (clock.getElapsedTime() - started.current) / 1.1;
    if (t >= 1) {
      mesh.visible = false;
      started.current = null;
      invalidate();
      return;
    }
    const ease = 1 - (1 - t) ** 3;
    const r = placed.reach + 0.3 + ease * Math.max(3, placed.reach * 1.5);
    mesh.visible = true;
    mesh.scale.set(r, r, 1);
    (mesh.material as THREE.MeshBasicMaterial).opacity = 0.85 * (1 - t);
    invalidate();
  });
  if (!placed || !geometry) return null;
  const y = placed.y + heightOf(placed) + 0.04;
  return (
    <group position={[placed.x, y, placed.z]}>
      <mesh geometry={geometry} raycast={() => null} renderOrder={2}>
        <meshBasicMaterial vertexColors side={THREE.DoubleSide} toneMapped={false} />
      </mesh>
      <mesh ref={ping} rotation={[-Math.PI / 2, 0, 0]} visible={false} raycast={() => null} renderOrder={3}>
        <ringGeometry args={[0.97, 1, 96]} />
        <meshBasicMaterial color={palette.signal} transparent depthWrite={false} side={THREE.DoubleSide} toneMapped={false} />
      </mesh>
    </group>
  );
}

/** 指している組織の足元の細い輪（黄の線と黒鉛色の縁。説明の札と合わせて、どれを指しているかを示す）。 */
function HoverRing({ placed, palette }: { placed: Placed3D | null; palette: Palette }) {
  if (!placed) return null;
  const r = placed.reach;
  return (
    <group position={[placed.x, placed.y + 0.02, placed.z]} rotation={[-Math.PI / 2, 0, 0]}>
      <mesh raycast={() => null}>
        <ringGeometry args={[r + 0.12, r + 0.24, 72]} />
        <meshBasicMaterial color={palette.signal} side={THREE.DoubleSide} toneMapped={false} />
      </mesh>
      <mesh raycast={() => null}>
        <ringGeometry args={[r + 0.24, r + 0.3, 72]} />
        <meshBasicMaterial color={palette.signalInk} side={THREE.DoubleSide} />
      </mesh>
    </group>
  );
}

function Edges({ scene, palette, focus, morph, byId }: { scene: Scene3D; palette: Palette; focus: Focus | null; morph: RefObject<Morph>; byId: Map<string, Placed3D> }) {
  const geometry = useMemo(() => {
    const g = new THREE.BufferGeometry();
    // 補間の途中は位置を書き換えるので、scene の配列を写して持つ
    g.setAttribute("position", new THREE.BufferAttribute(new Float32Array(scene.segments), 3));
    g.setAttribute("color", new THREE.BufferAttribute(new Float32Array(scene.segments.length), 3));
    return g;
  }, [scene]);
  const { invalidate } = useThree();
  const reduced = useReducedMotion() ?? false;
  // 色の目標（選んだ組織が変わったら、今の色からこの色へ滑らかに変える）
  const target = useRef<{ geometry: THREE.BufferGeometry | null; colors: Float32Array }>({ geometry: null, colors: new Float32Array(0) });
  useLayoutEffect(() => {
    const colors = geometry.getAttribute("color") as THREE.BufferAttribute;
    const goal = new Float32Array(colors.array.length);
    // 組織が多いと親子の線が束になって図を覆うので、数に応じて背景へ寄せる（1,000 組織でおよそ半分）
    const crowd = Math.min(0.55, Math.max(0, (scene.nodes.length - 150) / 2000));
    const strong = new THREE.Color(palette.edge).lerp(new THREE.Color(palette.bg), crowd);
    const focused = new THREE.Color(palette.edge);
    // 関係の無い線は地の色へ大きく寄せて薄くする（モジュールの薄さと揃える）
    const faint = new THREE.Color(palette.edge).lerp(new THREE.Color(palette.bg), 1 - FADED);
    scene.segmentChildIds.forEach((id, i) => {
      const c = !focus ? strong : focus.edgeChildren.has(id) ? focused : faint;
      goal.set([c.r, c.g, c.b, c.r, c.g, c.b], i * 6);
    });
    // 新しい配置（ジオメトリ）では、最初から目標の色にする
    if (target.current.geometry !== geometry) {
      (colors.array as Float32Array).set(goal);
      colors.needsUpdate = true;
    }
    target.current = { geometry, colors: goal };
    invalidate();
  }, [geometry, scene, palette, focus, invalidate]);
  useFrame((_, dt) => {
    const colors = geometry.getAttribute("color") as THREE.BufferAttribute;
    if (target.current.geometry !== geometry) return;
    if (stepAttribute(colors, target.current.colors, dt, reduced)) invalidate();
  });
  useEffect(() => () => geometry.dispose(), [geometry]);
  // 補間の途中は、親子の線の端を組織の位置に合わせて動かす
  const applied = useRef(1);
  useFrame(() => {
    const m = morph.current;
    const t = m.active ? m.t : 1;
    if (t === applied.current) return;
    applied.current = t;
    const position = geometry.getAttribute("position") as THREE.BufferAttribute;
    const a = new THREE.Vector3();
    const b = new THREE.Vector3();
    scene.segmentChildIds.forEach((id, i) => {
      const child = byId.get(id);
      const parent = child?.node.parentId ? byId.get(child.node.parentId) : undefined;
      if (!child || !parent) return;
      morphPos(m, parent, a);
      morphPos(m, child, b);
      position.setXYZ(i * 2, a.x, a.y, a.z);
      position.setXYZ(i * 2 + 1, b.x, b.y + heightOf(child), b.z);
    });
    position.needsUpdate = true;
  });
  return (
    <lineSegments geometry={geometry} raycast={() => null}>
      <lineBasicMaterial vertexColors />
    </lineSegments>
  );
}

// ---- 段の輪の回転（ゆっくり） ----
//
// 段の円盤の外周の輪（目盛り付き）と内側の破線の輪を、段の中心を軸に逆向きにゆっくり回す（拠点の島の床の輪も同じ）。
// 組織のモジュール・親子の線・ラベル・段の目印（ラベルの位置）は回さない。prefers-reduced-motion では回さない。
// 負荷を抑えるため:
// - 回すのは頂点シェーダー（LineBasicMaterial に onBeforeCompile で回転を足す）。輪の頂点に「回転の中心」と「角速度」を持たせ、
//   時刻の uniform だけを毎フレーム書き換える。すべての段の輪は 1 つの LineSegments のまま（描画呼び出しは増えない。
//   ジオメトリの作り直し・React の再レンダーもしない）
// - frameloop="demand" のまま、回している間だけ毎秒 SPIN_FPS 回の描き直しを要求する（1 周 90 秒前後なので、
//   毎秒 15 回でも 1 回の動きは 0.3° 未満で滑らかに見える。60 回描くより低性能な GPU でも負荷が 1/4 で済む）

/** 回している間の描き直しの回数（毎秒）。 */
const SPIN_FPS = 15;

/** 輪の 1 周の秒数（段が深いほど少しだけ遅く。外周と内側で少し変える）。 */
const spinPeriod = (depth: number, inner: boolean) => (inner ? 118 : 96) + depth * 14;
/** 角速度（rad/s）。外周は正、内側の破線は逆向き。 */
const spinSpeed = (depth: number, inner: boolean) => ((inner ? -1 : 1) * Math.PI * 2) / spinPeriod(depth, inner);

/** 回転の時刻（秒）。シェーダーで共有する uniform（SpinClock が書き換える）。 */
const SPIN_TIME = { value: 0 };

/** 輪の線のマテリアル: 頂点の aCenter（回転の中心）を軸に、aSpin（rad/s）× 時刻だけ水平に回す。 */
function spinningLineMaterial(params: THREE.LineBasicMaterialParameters): THREE.LineBasicMaterial {
  // 選んだ組織に関係しない段・島の輪は薄くする（aFade。頂点ごと）
  return addFade(new THREE.LineBasicMaterial(params), "spinning-line", (shader) => {
    shader.uniforms.uSpinTime = SPIN_TIME;
    shader.vertexShader = shader.vertexShader
      .replace("#include <common>", "#include <common>\nattribute vec3 aCenter;\nattribute float aSpin;\nuniform float uSpinTime;")
      .replace(
        "#include <begin_vertex>",
        `vec3 transformed = vec3( position );
        {
          float sa = aSpin * uSpinTime;
          vec2 d = transformed.xz - aCenter.xz;
          transformed.xz = aCenter.xz + vec2( cos( sa ) * d.x - sin( sa ) * d.y, sin( sa ) * d.x + cos( sa ) * d.y );
        }`,
      );
  });
}

/** 回る輪の線を集める（位置・回転の中心・角速度・色を頂点ごとに持つ）。 */
class SpinLines {
  readonly position: number[] = [];
  readonly center: number[] = [];
  readonly spin: number[] = [];
  readonly color: number[] = [];
  /** 線ごとの薄くする単位（段・島）の鍵。 */
  readonly keys: string[] = [];
  seg(x1: number, y1: number, z1: number, x2: number, y2: number, z2: number, cx: number, cz: number, speed: number, key: string, c?: THREE.Color) {
    this.position.push(x1, y1, z1, x2, y2, z2);
    this.keys.push(key);
    this.center.push(cx, y1, cz, cx, y2, cz);
    this.spin.push(speed, speed);
    if (c) this.color.push(c.r, c.g, c.b, c.r, c.g, c.b);
  }
  build(): THREE.BufferGeometry {
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.Float32BufferAttribute(this.position, 3));
    g.setAttribute("aCenter", new THREE.Float32BufferAttribute(this.center, 3));
    g.setAttribute("aSpin", new THREE.Float32BufferAttribute(this.spin, 1));
    if (this.color.length > 0) g.setAttribute("color", new THREE.Float32BufferAttribute(this.color, 3));
    g.setAttribute("aFade", new THREE.Float32BufferAttribute(new Float32Array(this.keys.length * 2).fill(1), 1));
    return g;
  }
}

/** 輪の線の濃さを、選んだ組織に関係する段・島（isActive）に合わせて滑らかに変える。 */
function useLineFade(geometry: THREE.BufferGeometry, keys: readonly string[], isActive: ((key: string) => boolean) | null) {
  const { invalidate } = useThree();
  const reduced = useReducedMotion() ?? false;
  const target = useMemo(() => new Float32Array(keys.length * 2).fill(1), [keys]);
  useLayoutEffect(() => {
    keys.forEach((k, i) => {
      const v = !isActive || isActive(k) ? 1 : FADED;
      target[i * 2] = v;
      target[i * 2 + 1] = v;
    });
    invalidate();
  }, [keys, isActive, target, invalidate]);
  useFrame((_, dt) => {
    const attr = geometry.getAttribute("aFade") as THREE.BufferAttribute | undefined;
    if (!attr) return;
    if (stepAttribute(attr, target, dt, reduced)) invalidate();
  });
}

/** マテリアルの不透明度を変える（three のオブジェクトの変更なので、フックの外の関数で行う）。 */
function setOpacity(m: THREE.Material, opacity: number) {
  m.opacity = opacity;
}

/** 輪の回転の時刻を進める。動きを減らす設定では止めたまま（時刻 0 = 回さない）。 */
function SpinClock({ reduced }: { reduced: boolean }) {
  const { invalidate } = useThree();
  useEffect(() => {
    SPIN_TIME.value = 0;
    if (reduced) {
      invalidate();
      return;
    }
    const started = performance.now();
    const id = window.setInterval(() => {
      // 裏のタブでは描かない（setInterval も間引かれるが、描き直しの要求自体をしない）
      if (document.hidden) return;
      SPIN_TIME.value = (performance.now() - started) / 1000;
      invalidate();
    }, 1000 / SPIN_FPS);
    return () => window.clearInterval(id);
  }, [reduced, invalidate]);
  return null;
}

/**
 * 段の札の付け根の、輪の縁の上の角度（ラジアン。0 = +x、π/2 = 手前の +z）。
 * 段ごとに左右に振り分け、深くなるほど手前へずらす（縦に同じ位置に並ばず、札どうし・下の段の輪と重なりにくい）。
 * 最初の向き（手前の斜め上から見る）では、左右の縁から外へ札が出る。
 */
function layerLabelAngle(depth: number): number {
  const step = Math.floor((depth - 1) / 2);
  const toward = 0.18 + 0.32 * step;
  return depth % 2 === 1 ? Math.PI - toward : toward;
}

/** 段の札の引き出し線の長さ（輪の縁から外へ）。 */
const leaderLength = (radius: number) => Math.max(2.5, radius * 0.1);

/** 内側の破線の輪の切れ目（120° ごとに 12°）。輪が一様だと回っているのが分からないので、外周の長い目盛り（90° ごと）と合わせて回転の手がかりにする。 */
const innerGap = (a: number) => ((a + Math.PI * 2) % ((Math.PI * 2) / 3)) < (12 / 180) * Math.PI;

/**
 * 段のワイヤーの円盤: 外周の輪・内側の破線の輪・目盛り（5° ごと、30° ごとに長く、90° ごとにさらに長く）と、法人の中心を貫く軸線。
 * すべての段の輪を 1 つの LineSegments にまとめる（回る。上の「段の輪の回転」）。段の札と引き出し線は HTML の重ね（回らない）。
 * 組織を選んでいるときは、関係する組織の無い段の輪と面を薄くする。
 */
function Layers({ scene, palette, active }: { scene: Scene3D; palette: Palette; active: Active | null }) {
  const { rings, keys, lines, floorY } = useMemo(() => {
    const v: number[] = [];
    const ring = new SpinLines();
    const seg = (x1: number, y1: number, z1: number, x2: number, y2: number, z2: number) => v.push(x1, y1, z1, x2, y2, z2);
    const at = (l: Layer3D, r: number, a: number): [number, number] => [l.x + r * Math.cos(a), l.z + r * Math.sin(a)];
    for (const l of scene.layers) {
      const key = `${l.entityIndex}:${l.depth}`;
      const outer = spinSpeed(l.depth, false);
      const inner = spinSpeed(l.depth, true);
      const n = 144;
      for (let i = 0; i < n; i++) {
        const a0 = (i / n) * Math.PI * 2;
        const a1 = ((i + 1) / n) * Math.PI * 2;
        const [x0, z0] = at(l, l.radius, a0);
        const [x1, z1] = at(l, l.radius, a1);
        ring.seg(x0, l.y, z0, x1, l.y, z1, l.x, l.z, outer, key);
        // 内側の破線の輪（外周と逆向きに回る）
        if (i % 2 === 0 && !innerGap(a0)) {
          const [u0, w0] = at(l, l.radius - 0.7, a0);
          const [u1, w1] = at(l, l.radius - 0.7, a1);
          ring.seg(u0, l.y, w0, u1, l.y, w1, l.x, l.z, inner, key);
        }
      }
      // 目盛り（外向き）
      for (let d = 0; d < 360; d += 5) {
        const a = (d / 180) * Math.PI;
        const len = d % 90 === 0 ? 1.5 : d % 30 === 0 ? 0.9 : 0.35;
        const [x0, z0] = at(l, l.radius, a);
        const [x1, z1] = at(l, l.radius + len, a);
        ring.seg(x0, l.y, z0, x1, l.y, z1, l.x, l.z, outer, key);
      }
    }
    const floor = scene.floorY;
    // 法人の中心を貫く軸線（塔の上から床まで）
    for (const p of scene.nodes) {
      if (p.node.kind !== "legal_entity") continue;
      seg(p.x, p.y, p.z, p.x, floor, p.z);
    }
    const lg = new THREE.BufferGeometry();
    lg.setAttribute("position", new THREE.Float32BufferAttribute(v, 3));
    return { rings: ring.build(), keys: ring.keys, lines: lg, floorY: floor };
  }, [scene]);
  useEffect(
    () => () => {
      rings.dispose();
      lines.dispose();
    },
    [rings, lines],
  );
  const ringMaterial = useMemo(() => spinningLineMaterial({ transparent: true, opacity: 0.75 }), []);
  useEffect(() => () => ringMaterial.dispose(), [ringMaterial]);
  useLayoutEffect(() => void ringMaterial.color.set(palette.wire), [ringMaterial, palette]);
  const isActive = useMemo(() => (active ? (k: string) => active.layers.has(k) : null), [active]);
  useLineFade(rings, keys, isActive);
  return (
    <group>
      {scene.layers.map((l) => (
        // ごく薄い面（段の重なりを読みやすくする）。関係する組織の無い段はさらに薄く
        <mesh key={`${l.entityIndex}:${l.depth}`} position={[l.x, l.y - 0.02, l.z]} rotation={[-Math.PI / 2, 0, 0]} raycast={() => null}>
          <circleGeometry args={[l.radius, 96]} />
          <meshBasicMaterial
            color={palette.wire}
            transparent
            opacity={!active || active.layers.has(`${l.entityIndex}:${l.depth}`) ? 0.05 : 0.015}
            side={THREE.DoubleSide}
            depthWrite={false}
          />
        </mesh>
      ))}
      <lineSegments geometry={rings} material={ringMaterial} raycast={() => null} />
      <lineSegments geometry={lines} raycast={() => null}>
        <lineBasicMaterial color={palette.wire} transparent opacity={0.75} />
      </lineSegments>
      <Floor scene={scene} y={floorY} palette={palette} />
    </group>
  );
}

/** 島の色（色分けしているときの拠点の色。HTML の札用の CSS の色）。 */
function islandColorCss(coloring: SiteColoring | null, island: Island3D): string | undefined {
  const site = island.partition.site;
  if (!coloring || !site) return undefined;
  const slot = coloring.slotOf.get(site.id);
  return slot === undefined ? undefined : slot === null ? "var(--site-other)" : `var(--site-${slot + 1})`;
}

/** 島の線の色（three の色）。色分けしていなければワイヤーの色。 */
function islandColor(coloring: SiteColoring | null, island: Island3D, palette: Palette): string {
  const site = island.partition.site;
  const slot = coloring && site ? coloring.slotOf.get(site.id) : undefined;
  return slot === undefined ? palette.wire : slot === null ? palette.siteOther : palette.site[slot]!;
}

/** 島の軸線の上端（島の一番上の段より少し上。島の札をここに置く）。 */
const islandTop = (i: Island3D) => i.topY + 2.2;

/**
 * 拠点の島（床の上）: 細い外周の輪と、内側の破線の輪・4 方向の短い目盛り（段の円盤と同じ語彙）、島の中心を貫く細い軸線。
 * 面は塗らない（床の薄い円の面・四隅の鉤の案と比べ、段の円盤の語彙のままの輪がいちばん静かで、白基調の空間になじんだ）。
 * 色分けしているときは輪を拠点の色にする（軸線は地の色のまま）。組織を選んでいるときは、関係する組織の無い島を薄くする。
 */
function Islands({ scene, palette, coloring, active }: { scene: Scene3D; palette: Palette; coloring: SiteColoring | null; active: Active | null }) {
  // すべての島の輪と軸線を 2 つの LineSegments（外周・目盛り・軸線と、内側の破線）にまとめる。島の色は頂点の色
  const { outer, inner } = useMemo(() => {
    const y = scene.floorY + 0.02;
    const out = new SpinLines();
    const dash = new SpinLines();
    const c = new THREE.Color();
    const wire = new THREE.Color(palette.wire);
    for (const island of scene.islands) {
      const key = `isl:${island.index}`;
      c.set(islandColor(coloring, island, palette));
      const r = island.radius;
      const at = (rr: number, a: number): [number, number] => [island.x + rr * Math.cos(a), island.z + rr * Math.sin(a)];
      // 島の中心を貫く細い軸線（床から島の札まで。通常の表示の法人の軸線と同じ語彙。色分けの間も地の色。回さない）
      out.seg(island.x, y, island.z, island.x, islandTop(island), island.z, island.x, island.z, 0, key, wire);
      // 島の輪は床の上の段（深さ 0）として回す
      const n = 160;
      for (let i = 0; i < n; i++) {
        const a0 = (i / n) * Math.PI * 2;
        const [x0, z0] = at(r, a0);
        const [x1, z1] = at(r, ((i + 1) / n) * Math.PI * 2);
        out.seg(x0, y, z0, x1, y, z1, island.x, island.z, spinSpeed(0, false), key, c);
        if (i % 3 === 0 && !innerGap(a0)) {
          const [u0, w0] = at(r - 0.9, a0);
          const [u1, w1] = at(r - 0.9, ((i + 1.4) / n) * Math.PI * 2);
          dash.seg(u0, y, w0, u1, y, w1, island.x, island.z, spinSpeed(0, true), key, c);
        }
      }
      // 4 方向の短い目盛り（外向き）
      for (let k = 0; k < 4; k++) {
        const a = (k / 4) * Math.PI * 2 + Math.PI / 2;
        const [x0, z0] = at(r, a);
        const [x1, z1] = at(r + 1.4, a);
        out.seg(x0, y, z0, x1, y, z1, island.x, island.z, spinSpeed(0, false), key, c);
      }
    }
    return { outer: { geometry: out.build(), keys: out.keys }, inner: { geometry: dash.build(), keys: dash.keys } };
  }, [scene, coloring, palette]);
  useEffect(
    () => () => {
      outer.geometry.dispose();
      inner.geometry.dispose();
    },
    [outer, inner],
  );
  const materials = useMemo(
    () => ({
      outer: spinningLineMaterial({ vertexColors: true, transparent: true }),
      inner: spinningLineMaterial({ vertexColors: true, transparent: true, opacity: 0.35 }),
    }),
    [],
  );
  useEffect(() => () => Object.values(materials).forEach((m) => m.dispose()), [materials]);
  useLayoutEffect(() => setOpacity(materials.outer, coloring ? 0.5 : 0.7), [materials, coloring]);
  const isActive = useMemo(() => (active ? (k: string) => active.islands.has(Number(k.slice(4))) : null), [active]);
  useLineFade(outer.geometry, outer.keys, isActive);
  useLineFade(inner.geometry, inner.keys, isActive);
  return (
    <group>
      <lineSegments geometry={outer.geometry} material={materials.outer} raycast={() => null} />
      <lineSegments geometry={inner.geometry} material={materials.inner} raycast={() => null} />
    </group>
  );
}

/** 拠点をまたぐ親子（選んだ組織に関わる分）。上位のモジュールの底から、別の島の根の上面へ、細い破線で結ぶ。 */
function UpperLinks({ pairs, byId, palette }: { pairs: Array<[string, string]>; byId: Map<string, Placed3D>; palette: Palette }) {
  const { invalidate, size } = useThree();
  const lines = useMemo(
    () =>
      pairs.map(([upperId, rootId]) => {
        const a = byId.get(upperId)!;
        const b = byId.get(rootId)!;
        const p1 = new THREE.Vector3(a.x, a.y, a.z);
        const p2 = new THREE.Vector3(b.x, b.y + heightOf(b), b.z);
        // 低い弧（親子の線より少し上を通る）。高く持ち上げると兼務・出向の弧と紛れる
        const mid = p1.clone().add(p2).multiplyScalar(0.5);
        mid.y = Math.max(p1.y, p2.y) + Math.min(4, p1.distanceTo(p2) * 0.08);
        const pts = new THREE.QuadraticBezierCurve3(p1, mid, p2).getPoints(40);
        const geometry = new LineGeometry();
        geometry.setPositions(pts.flatMap((p) => [p.x, p.y, p.z]));
        const material = new LineMaterial({ color: palette.edge, linewidth: 1.4, dashed: true, dashSize: 0.6, gapSize: 0.5, worldUnits: false });
        const line = new Line2(geometry, material);
        line.computeLineDistances();
        line.raycast = () => undefined;
        return line;
      }),
    [pairs, byId, palette],
  );
  useEffect(() => {
    lines.forEach((l) => (l.material as LineMaterial).resolution.set(size.width, size.height));
    invalidate();
  }, [lines, size, invalidate]);
  useEffect(
    () => () =>
      lines.forEach((l) => {
        l.geometry.dispose();
        (l.material as LineMaterial).dispose();
      }),
    [lines],
  );
  return (
    <group>
      {lines.map((l) => (
        <primitive key={l.uuid} object={l} />
      ))}
    </group>
  );
}

const FLOOR_VERTEX = /* glsl */ `
varying vec3 vWorld;
void main() {
  vec4 w = modelMatrix * vec4(position, 1.0);
  vWorld = w.xyz;
  gl_Position = projectionMatrix * viewMatrix * w;
}
`;

const FLOOR_FRAGMENT = /* glsl */ `
uniform vec3 uColor;
uniform vec2 uCenter;
uniform float uRadius;
uniform float uCell;
varying vec3 vWorld;
float gridLine(vec2 p, float cell, float width) {
  vec2 c = p / cell;
  vec2 d = abs(fract(c - 0.5) - 0.5) / max(fwidth(c), vec2(1e-4));
  return 1.0 - clamp(min(d.x, d.y) / width, 0.0, 1.0);
}
void main() {
  float minor = gridLine(vWorld.xz, uCell, 0.8);
  float major = gridLine(vWorld.xz, uCell * 5.0, 1.2);
  // 中心から離れるほど薄れる（遠方へ溶ける）
  float fade = 1.0 - smoothstep(uRadius * 0.15, uRadius, length(vWorld.xz - uCenter));
  float a = max(minor * 0.22, major * 0.55) * fade;
  if (a < 0.01) discard;
  gl_FragColor = vec4(uColor, a);
  #include <colorspace_fragment>
}
`;

function setFloorUniforms(material: THREE.ShaderMaterial, palette: Palette, scene: Scene3D, size: number) {
  (material.uniforms.uColor!.value as THREE.Color).set(palette.grid);
  (material.uniforms.uCenter!.value as THREE.Vector2).set(scene.center[0], scene.center[2]);
  material.uniforms.uRadius!.value = size / 2;
  // 格子の目は全体の大きさに合わせる（1 万人でも細かすぎない）
  material.uniforms.uCell!.value = Math.max(4, Math.round(scene.extent / 24));
}

/** 床の格子（シェーダーで線を引く。テクスチャ無し・描画呼び出し 1 回）。 */
function Floor({ scene, y, palette }: { scene: Scene3D; y: number; palette: Palette }) {
  const material = useMemo(
    () =>
      new THREE.ShaderMaterial({
        vertexShader: FLOOR_VERTEX,
        fragmentShader: FLOOR_FRAGMENT,
        transparent: true,
        depthWrite: false,
        side: THREE.DoubleSide,
        uniforms: {
          uColor: { value: new THREE.Color() },
          uCenter: { value: new THREE.Vector2() },
          uRadius: { value: 100 },
          uCell: { value: 2 },
        },
      }),
    [],
  );
  useEffect(() => () => material.dispose(), [material]);
  const size = scene.extent * 2.4 + 60;
  useLayoutEffect(() => setFloorUniforms(material, palette, scene, size), [material, palette, scene, size]);
  return (
    <mesh position={[scene.center[0], y, scene.center[2]]} rotation={[-Math.PI / 2, 0, 0]} material={material} raycast={() => null} renderOrder={-1}>
      <planeGeometry args={[size, size]} />
    </mesh>
  );
}

// three のシーンの変更（フックの外の関数で行う）
function setEnvironment(gl: THREE.WebGLRenderer, scene: THREE.Scene) {
  const pmrem = new THREE.PMREMGenerator(gl);
  const room = new RoomEnvironment();
  const target = pmrem.fromScene(room, 0.04);
  scene.environment = target.texture;
  room.dispose();
  pmrem.dispose();
  return () => {
    scene.environment = null;
    target.dispose();
  };
}

function setFog(scene: THREE.Scene, palette: Palette) {
  scene.environmentIntensity = palette.light.env;
  scene.fog = new THREE.Fog(palette.bg, 50, 400);
  return () => {
    scene.fog = null;
  };
}

function fitFog(scene: THREE.Scene, distance: number) {
  const fog = scene.fog;
  if (!(fog instanceof THREE.Fog)) return;
  fog.near = distance * 1.1;
  fog.far = distance * 3.4;
}

/** 環境光（RoomEnvironment を PMREM にしたもの。金属の外装の映り込み）と距離のフォグ。 */
function Atmosphere({ palette, controls }: { palette: Palette; controls: OrbitControls | null }) {
  const { gl, scene, camera } = useThree();
  useEffect(() => setEnvironment(gl, scene), [gl, scene]);
  useEffect(() => setFog(scene, palette), [scene, palette]);
  // フォグは注視点までの距離に合わせて、手前はくっきり、奥の法人・床の端だけを地に溶かす
  useFrame(() => {
    if (controls) fitFog(scene, camera.position.distanceTo(controls.target));
  });
  return null;
}

/** 兼務・出向の弧（選んだ組織の分だけ）。太さを画面の px で指定できる Line2 で、兼務は破線、出向は点線。 */
function CrossArcs({ focus, byId, palette }: { focus: Focus | null; byId: Map<string, Placed3D>; palette: Palette }) {
  const { invalidate, size } = useThree();
  const lines = useMemo(() => {
    if (!focus) return [];
    return focus.links.map((l) => {
      const a = byId.get(l.from)!;
      const b = byId.get(l.to)!;
      const p1 = new THREE.Vector3(a.x, a.y + heightOf(a), a.z);
      const p2 = new THREE.Vector3(b.x, b.y + heightOf(b), b.z);
      const dist = p1.distanceTo(p2);
      const mid = p1.clone().add(p2).multiplyScalar(0.5);
      mid.y = Math.max(p1.y, p2.y) + Math.max(3, dist * 0.35);
      const pts = new THREE.QuadraticBezierCurve3(p1, mid, p2).getPoints(48);
      const geometry = new LineGeometry();
      geometry.setPositions(pts.flatMap((p) => [p.x, p.y, p.z]));
      const concurrent = l.kind === "concurrent";
      const material = new LineMaterial({
        color: concurrent ? palette.concurrent : palette.secondment,
        linewidth: concurrent ? 2.5 : 3,
        dashed: true,
        dashSize: concurrent ? 0.9 : 0.08,
        gapSize: concurrent ? 0.45 : 0.5,
        worldUnits: false,
      });
      const line = new Line2(geometry, material);
      line.computeLineDistances();
      line.raycast = () => undefined;
      return line;
    });
  }, [focus, byId, palette]);
  useEffect(() => {
    lines.forEach((l) => (l.material as LineMaterial).resolution.set(size.width, size.height));
    invalidate();
  }, [lines, size, invalidate]);
  useEffect(
    () => () =>
      lines.forEach((l) => {
        l.geometry.dispose();
        (l.material as LineMaterial).dispose();
      }),
    [lines],
  );
  return (
    <group>
      {lines.map((l) => (
        <primitive key={l.uuid} object={l} />
      ))}
    </group>
  );
}

function PersonDots({ scene, palette, controls, morph }: { scene: Scene3D; palette: Palette; controls: OrbitControls | null; morph: RefObject<Morph> }) {
  const ref = useRef<THREE.InstancedMesh>(null);
  const dots = useMemo(() => personDots(scene.nodes), [scene]);
  const count = dots.length / 3;
  const geometry = useMemo(() => new THREE.CylinderGeometry(0.1, 0.1, 0.04, 8), []);
  const material = useMemo(() => new THREE.MeshBasicMaterial(), []);
  const { camera, invalidate } = useThree();
  useEffect(() => () => {
    geometry.dispose();
    material.dispose();
  }, [geometry, material]);
  useLayoutEffect(() => {
    material.color.set(palette.dot);
    const mesh = ref.current;
    if (!mesh) return;
    const m = new THREE.Matrix4();
    for (let i = 0; i < count; i++) {
      m.makeTranslation(dots[i * 3]!, dots[i * 3 + 1]!, dots[i * 3 + 2]!);
      mesh.setMatrixAt(i, m);
    }
    mesh.count = count;
    mesh.instanceMatrix.needsUpdate = true;
    mesh.computeBoundingSphere();
    invalidate();
  }, [dots, count, palette, material, invalidate]);
  // LOD: カメラが注視点に近いときだけ人の点を描く
  useFrame(() => {
    const mesh = ref.current;
    if (!mesh || !controls) return;
    // 配置の補間の途中は出さない（点は最後の位置にあるため）
    const near = !morph.current.active && camera.position.distanceTo(controls.target) < Math.max(36, scene.extent * 0.5);
    if (mesh.visible !== near) {
      mesh.visible = near;
      invalidate();
    }
  });
  if (count === 0) return null;
  return <instancedMesh ref={ref} args={[geometry, material, count]} raycast={() => null} />;
}

/**
 * カメラを点の集まりに合わせる位置。球で囲むと平たい木では余白が大きすぎるため、
 * 視線の向き dir から見た各点の横・縦のずれを画角と比べ、すべてが収まる最小の距離を求める。
 * モジュールは外接円の縁の 4 点と上下で近似する。margin は画面の端に残す割合。
 */
function fitView(
  camera: THREE.PerspectiveCamera,
  points: Placed3D[],
  dir: THREE.Vector3,
  /** 画面の上から何割までに収めるか（スマホで下に詳細のシートが重なるとき 1 未満）。 */
  visible = 1,
  margin = 1.1,
  /** 画面の上の何割を空けるか（図の上に重なる要素で図の上端を隠さないため。今は使っていない）。 */
  topInset = 0,
): { target: THREE.Vector3; position: THREE.Vector3 } {
  const sphere = boundingSphere(points, 1);
  const center = new THREE.Vector3(...sphere.center);
  const back = dir.clone().normalize();
  const right = new THREE.Vector3(0, 1, 0).cross(back).normalize();
  const up = back.clone().cross(right).normalize();
  const tanFull = Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2);
  const tanH = tanFull * camera.aspect;
  const tanV = tanFull * Math.max(0.2, visible - topInset);
  // 中心を画面の中心に置いたとき、各点が収まる距離。横・縦の偏りを見て中心も少し動かす
  let minR = Infinity, maxR = -Infinity, minU = Infinity, maxU = -Infinity;
  const corners: THREE.Vector3[] = [];
  for (const p of points) {
    const h = heightOf(p);
    for (const [dx, dz] of [[p.reach, 0], [-p.reach, 0], [0, p.reach], [0, -p.reach]] as const) {
      for (const y of [p.y, p.y + h + 1.2]) {
        const c = new THREE.Vector3(p.x + dx, y, p.z + dz);
        corners.push(c);
        const d = c.clone().sub(center);
        minR = Math.min(minR, d.dot(right)); maxR = Math.max(maxR, d.dot(right));
        minU = Math.min(minU, d.dot(up)); maxU = Math.max(maxU, d.dot(up));
      }
    }
  }
  const target = center.clone().add(right.clone().multiplyScalar((minR + maxR) / 2)).add(up.clone().multiplyScalar((minU + maxU) / 2));
  let distance = 4;
  for (const c of corners) {
    const d = c.clone().sub(target);
    const toward = d.dot(back);
    distance = Math.max(distance, Math.abs(d.dot(right)) / tanH + toward, Math.abs(d.dot(up)) / tanV + toward);
  }
  distance *= margin;
  // 見えている範囲（上に重なる要素と下のシートの間）の中心が target に来るように、注視点を上下にずらす
  const aim = target.clone().sub(up.clone().multiplyScalar((1 - topInset - visible) * distance * tanFull));
  return { target: aim, position: aim.clone().add(back.multiplyScalar(distance)) };
}

/**
 * 全体を表示するときに収める点。拠点で分けたときは、島の段の輪（モジュールより外へ広がる）も収めるように、
 * 島ごとに島の大きさの仮の点を足す。
 */
function wholeScene(scene: Scene3D): Placed3D[] {
  if (scene.islands.length === 0) return scene.nodes;
  const top = new Map<number, number>();
  for (const l of scene.layers) top.set(l.entityIndex, Math.max(top.get(l.entityIndex) ?? -Infinity, l.y));
  const extra = scene.islands.map(
    (i): Placed3D => ({
      node: i.partition.roots[0]!,
      x: i.x,
      y: top.get(i.index) ?? i.topY,
      z: i.z,
      radius: i.radius,
      reach: i.radius,
      tier: 0,
      entityIndex: i.index,
    }),
  );
  return [...scene.nodes, ...extra];
}

/** カメラの移動: 最初は全体、組織を選んだら「その組織・上位 1 段・配下・相手先」が収まるように寄せる。 */
function CameraRig({ scene, byId, focus, fitRequest, controls, reduced, layoutKey }: {
  /** 配置の種類（拠点で分けるか）。変わったら、全体か選んだ組織に合わせ直す。 */
  layoutKey: string;
  scene: Scene3D;
  byId: Map<string, Placed3D>;
  focus: Focus | null;
  fitRequest: number;
  controls: OrbitControls | null;
  reduced: boolean;
}) {
  const { camera, invalidate, size } = useThree();
  const goal = useRef<{ target: THREE.Vector3; position: THREE.Vector3 } | null>(null);
  const initialized = useRef(false);

  // 寄せている途中でも、人がマウス・指で動かし始めたらそちらを優先する（目標へ引き戻さない）
  useEffect(() => {
    if (!controls) return;
    const cancel = () => {
      goal.current = null;
    };
    controls.addEventListener("start", cancel);
    return () => controls.removeEventListener("start", cancel);
  }, [controls]);

  const moveTo = (points: Placed3D[], dir: THREE.Vector3, instant: boolean) => {
    if (!controls) return;
    // lg（1024px）未満では詳細のパネルが画面の下 55% に重なる（App.tsx の SidePanel）
    const sheet = focus && window.innerWidth < 1024 ? 0.42 : 1;
    const { target, position } = fitView(camera as THREE.PerspectiveCamera, points, dir, sheet, 1.1);
    if (instant) {
      controls.target.copy(target);
      camera.position.copy(position);
      controls.update();
      invalidate();
    } else {
      goal.current = { target, position };
      invalidate();
    }
  };

  // 最初の表示と「全体を表示」
  useEffect(() => {
    if (!controls || size.width === 0) return;
    const first = !initialized.current;
    initialized.current = true;
    if (first && focus) return; // 最初から組織が選ばれているときは下の effect に任せる
    moveTo(wholeScene(scene), viewDir(size.width / Math.max(1, size.height)), first || reduced);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [controls, fitRequest, size.width > 0]);

  // 組織を選んだら寄せる。選択を外したら全体に戻す。配置の種類が変わったら（拠点で分ける）、全体か選んだ組織に合わせ直す
  const focusId = focus?.id ?? null;
  const hadFocus = useRef(false);
  const lastLayout = useRef(layoutKey);
  useEffect(() => {
    if (!controls || size.width === 0) return;
    const relayout = lastLayout.current !== layoutKey;
    lastLayout.current = layoutKey;
    if (!focus) {
      if (hadFocus.current || relayout) {
        // 切り替えでは今の見下ろす向きを保つ（急に回り込まない）。縦長の画面の向きは最初の表示と同じ
        moveTo(wholeScene(scene), viewDir(size.width / Math.max(1, size.height)), reduced);
      }
      hadFocus.current = false;
      return;
    }
    hadFocus.current = true;
    const self = byId.get(focus.id)!;
    const parent = self.node.parentId ? byId.get(self.node.parentId) : undefined;
    const points = [self, ...(parent ? [parent] : [])];
    // 配下はすべて含める（強調している線が画面の外へ伸びないように）
    const walk = (n: Placed3D["node"]) =>
      n.children.forEach((c) => {
        const pc = byId.get(c.id);
        if (pc) points.push(pc);
        walk(c);
      });
    walk(self.node);
    focus.links.forEach((l) => {
      const other = byId.get(l.from === focus.id ? l.to : l.from);
      if (other) points.push(other);
    });
    // 拠点をまたぐ上位・配下（別の島）も収める
    focus.upperPairs.forEach(([upper, root]) => {
      for (const id of [upper, root]) {
        const other = byId.get(id);
        if (other) points.push(other);
      }
    });
    // 今の向きを保ちつつ、見下ろす角度は 25° 以上にする
    const dir = camera.position.clone().sub(controls.target).normalize();
    if (dir.y < 0.42) {
      dir.y = 0.42;
      dir.normalize();
    }
    moveTo(points, dir, reduced);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusId, controls, size.width > 0, layoutKey]);

  useFrame((_, delta) => {
    if (!goal.current || !controls) return;
    const k = 1 - Math.exp(-delta * 4.5);
    controls.target.lerp(goal.current.target, k);
    camera.position.lerp(goal.current.position, k);
    controls.update();
    if (camera.position.distanceTo(goal.current.position) < 0.05) goal.current = null;
    invalidate();
  });
  return null;
}

// ---- HTML のラベル ----

interface LabelSpec {
  id: string;
  kind: "entity" | "org" | "layer" | "island";
  text: string;
  /** 選んだ組織に関係しない段・島の札（薄くする）。 */
  dim?: boolean;
  /** 段の札の引き出し線: 輪の縁の付け根（from）と、輪の中心（pivot。札を中心から離れる側へ置くのに使う）。札は anchor（線の先）に置く。 */
  leader?: { from: [number, number, number]; pivot: [number, number, number] };
  /** 島の札の人数と色（色分けしているときの拠点の色）。 */
  detail?: string;
  color?: string;
  /** 2 行目（兼務・出向の相手先の人数など）。 */
  sub?: { kind: OrgLink["kind"]; text: string };
  emphasis?: "selected" | "hovered";
  priority: number;
  anchor: [number, number, number];
}

/**
 * ラベルの配置。毎フレーム（カメラが動いたときだけ描き直される）アンカーを画面に投影し、
 * 優先度の高い順に置いて、既に置いたラベルと重なるもの・画面外・カメラの後ろは隠す。
 */
function LabelProjector({ specs, overlay, morph }: {
  specs: LabelSpec[];
  overlay: RefObject<HTMLDivElement | null>;
  morph: RefObject<Morph>;
}) {
  const { camera, size, invalidate } = useThree();
  const sorted = useMemo(() => [...specs].sort((a, b) => b.priority - a.priority), [specs]);
  // 寸法は内容ごとに覚える（内容が変わったら測り直す）
  const sizes = useRef(new Map<string, { w: number; h: number }>());
  const v = useMemo(() => new THREE.Vector3(), []);
  const latest = useRef<LabelSpec[]>([]);
  useEffect(() => {
    // ラベルが変わったら次のフレームで置き直す
    latest.current = sorted;
    invalidate();
  }, [sorted, invalidate]);
  // カメラ・大きさ・ラベルが前のフレームと同じなら置き直さない（輪の回転で描き直しが続くときに、DOM の書き換えを省く）
  const last = useRef<{ matrix: THREE.Matrix4; w: number; h: number; specs: LabelSpec[] | null; moving: boolean }>({
    matrix: new THREE.Matrix4(),
    w: 0,
    h: 0,
    specs: null,
    moving: false,
  });
  const p = useMemo(() => new THREE.Vector3(), []);
  useFrame(() => {
    const root = overlay.current;
    if (!root) return;
    const prev = last.current;
    camera.updateMatrixWorld();
    if (
      prev.specs === latest.current &&
      prev.w === size.width &&
      prev.h === size.height &&
      prev.moving === morph.current.active &&
      prev.matrix.equals(camera.matrixWorld)
    ) {
      return;
    }
    last.current = { matrix: prev.matrix.copy(camera.matrixWorld), w: size.width, h: size.height, specs: latest.current, moving: morph.current.active };
    const els = new Map<string, HTMLElement>();
    root.querySelectorAll<HTMLElement>("[data-label-id]").forEach((el) => els.set(el.dataset.labelId!, el));
    const leaders = new Map<string, SVGGElement>();
    root.querySelectorAll<SVGGElement>("[data-leader-id]").forEach((el) => leaders.set(el.dataset.leaderId!, el));
    const toScreen = (xyz: [number, number, number]): [number, number] => {
      p.set(...xyz).project(camera);
      return [((p.x + 1) / 2) * size.width, ((1 - p.y) / 2) * size.height];
    };
    const placed: Array<[number, number, number, number]> = [];
    const pad = 3;
    // 配置の補間の途中はラベルを出さない（アンカーは最後の位置にあるため）
    const moving = morph.current.active;
    for (const spec of latest.current) {
      const el = els.get(spec.id);
      if (!el) continue;
      const leader = leaders.get(spec.id);
      if (moving) {
        el.style.visibility = "hidden";
        if (leader) leader.style.visibility = "hidden";
        continue;
      }
      const key = `${spec.id}|${spec.text}|${spec.sub?.text ?? ""}|${spec.emphasis ?? ""}`;
      let dim = sizes.current.get(key);
      if (!dim || dim.w === 0) {
        dim = { w: el.offsetWidth, h: el.offsetHeight };
        sizes.current.set(key, dim);
      }
      v.set(...spec.anchor).project(camera);
      const sx = ((v.x + 1) / 2) * size.width;
      const sy = ((1 - v.y) / 2) * size.height;
      // 段の札は引き出し線の先の、輪の中心から離れる側（画面の上で）へ、組織のラベルはアンカーの上へ置く
      // アンカーが画面内なら、はみ出す分は画面の内側へ寄せる（端の組織の名前も読めるように）
      const anchorIn = sx >= 0 && sx <= size.width && sy >= 0 && sy <= size.height;
      const outward = spec.leader ? toScreen(spec.leader.pivot)[0] <= sx : true;
      const rawLeft = spec.kind === "layer" ? (outward ? sx + 4 : sx - dim.w - 4) : sx - dim.w / 2;
      const left = Math.min(Math.max(rawLeft, 2), size.width - dim.w - 2);
      const top = spec.kind === "layer" ? sy - dim.h / 2 : sy - dim.h - 6;
      const visible =
        v.z < 1 &&
        v.z > -1 &&
        anchorIn &&
        top >= 2 &&
        top + dim.h <= size.height - 2 &&
        !placed.some(([l, t, r, b]) => left < r + pad && left + dim.w + pad > l && top < b + pad && top + dim.h + pad > t);
      if (visible) {
        placed.push([left, top, left + dim.w, top + dim.h]);
        el.style.transform = `translate(${Math.round(left)}px, ${Math.round(top)}px)`;
        el.style.visibility = "visible";
        if (leader && spec.leader) {
          const [fx, fy] = toScreen(spec.leader.from);
          const line = leader.firstElementChild as SVGLineElement | null;
          const dot = leader.lastElementChild as SVGCircleElement | null;
          line?.setAttribute("x1", fx.toFixed(1));
          line?.setAttribute("y1", fy.toFixed(1));
          line?.setAttribute("x2", sx.toFixed(1));
          line?.setAttribute("y2", sy.toFixed(1));
          dot?.setAttribute("cx", fx.toFixed(1));
          dot?.setAttribute("cy", fy.toFixed(1));
          leader.style.visibility = "visible";
        }
      } else {
        el.style.visibility = "hidden";
        if (leader) leader.style.visibility = "hidden";
      }
    }
  });
  return null;
}

function LabelOverlay({ specs, overlay }: { specs: LabelSpec[]; overlay: RefObject<HTMLDivElement | null> }) {
  return (
    <div ref={overlay} className="pointer-events-none absolute inset-0 overflow-hidden" aria-hidden="true">
      {/* 段の札の引き出し線（輪の縁の付け根の点から札まで。細い中間の灰色） */}
      <svg className="absolute inset-0 size-full overflow-visible">
        {specs
          .filter((s) => s.leader)
          .map((s) => (
            <g key={s.id} data-leader-id={s.id} style={{ visibility: "hidden" }} className={`transition-opacity duration-250 motion-reduce:transition-none ${s.dim ? "opacity-20" : ""}`}>
              <line stroke="var(--edge)" strokeWidth="0.8" />
              <circle r="1.6" fill="var(--edge)" />
            </g>
          ))}
      </svg>
      {specs.map((s) =>
        s.kind === "island" ? (
          <div
            key={s.id}
            data-label-id={s.id}
            className={`label3d rounded-sm border border-l-2 border-border bg-surface/95 py-0.5 pr-2 pl-1.5 leading-4 shadow-sm transition-opacity duration-250 motion-reduce:transition-none ${s.dim ? "opacity-25" : ""}`}
            style={{ visibility: "hidden", borderLeftColor: s.color ?? "var(--muted)" }}
          >
            <span className="text-[13px] font-bold text-fg">{s.text}</span>
            {s.detail && <span className="tabular ml-1.5 text-[11px] text-muted">{s.detail}</span>}
          </div>
        ) : s.kind === "layer" ? (
          <div key={s.id} data-label-id={s.id} className={`label3d rounded-sm bg-bg/85 px-1 py-px text-[11px] leading-4 text-muted transition-opacity duration-250 motion-reduce:transition-none ${s.dim ? "opacity-25" : ""}`} style={{ visibility: "hidden" }}>
            {s.text}
          </div>
        ) : (
          <div
            key={s.id}
            data-label-id={s.id}
            className={`label3d rounded-sm border bg-surface/95 px-1.5 py-px leading-4 shadow-sm ${
              s.kind === "entity" ? "border-border text-[13px] font-bold" : "text-[11px]"
            } ${s.emphasis === "selected" ? "border-fg border-l-4 border-l-signal font-bold" : s.emphasis === "hovered" ? "border-muted border-l-4 border-l-signal" : "border-border"} text-fg`}
            style={{ visibility: "hidden" }}
          >
            {s.text}
            {s.sub && (
              <span className="flex items-center gap-1 text-[10px] text-fg">
                <LineKey kind={s.sub.kind} width={12} />
                <span className="tabular">{s.sub.text}</span>
              </span>
            )}
          </div>
        ),
      )}
    </div>
  );
}

function buildLabels(scene: Scene3D, focus: Focus | null, hoveredId: string | null, islandCss: (i: Island3D) => string | undefined, byId: Map<string, Placed3D>): LabelSpec[] {
  const active = activeOf(focus, byId);
  const specs: LabelSpec[] = [];
  const top = (p: Placed3D): [number, number, number] => [p.x, p.y + heightOf(p) + 0.1, p.z];
  const partnerSub = new Map<string, LabelSpec["sub"]>();
  if (focus) {
    for (const l of focus.links) {
      const other = l.from === focus.id ? l.to : l.from;
      const word = l.kind === "concurrent" ? "兼務" : "出向";
      const prev = partnerSub.get(other);
      const text = `${word}${l.from === focus.id ? "先" : "元"} ${l.count}名`;
      partnerSub.set(other, prev ? { kind: prev.kind, text: `${prev.text}・${text}` } : { kind: l.kind, text });
    }
  }
  const candidates: LabelSpec[] = [];
  for (const p of scene.nodes) {
    const id = p.node.id;
    if (p.node.kind === "legal_entity") {
      specs.push({ id, kind: "entity", text: p.node.name, priority: 10000, anchor: top(p) });
      continue;
    }
    const total = p.node.totalPrimary ?? 0;
    let priority = 1000 + Math.min(total, 5000) / 10 - p.node.depth * 100;
    let emphasis: LabelSpec["emphasis"];
    if (focus?.id === id) {
      priority = 9500;
      emphasis = "selected";
    } else if (hoveredId === id) {
      priority = 9000;
      emphasis = "hovered";
    } else if (partnerSub.has(id)) {
      priority = 8000;
    } else if (focus && focus.related.has(id)) {
      priority += 3000;
    } else if (focus) {
      // 選んだ組織に関係しない組織の名前は出さない（退かせたモジュールに名前が残ると、どれが関係するのか読めなくなる）
      continue;
    }
    candidates.push({ id, kind: "org", text: p.node.name, sub: partnerSub.get(id), emphasis, priority, anchor: top(p) });
  }
  candidates.sort((a, b) => b.priority - a.priority);
  // 拠点で分けたときは島の札が増えるので、組織の名前は少なめにする
  specs.push(...candidates.slice(0, scene.islands.length > 0 ? 60 : 160));
  // 島の札は拠点名と人数だけ（種別・法人・組織の数は 2D の区画の見出しに任せ、図の上の文字を減らす）
  for (const i of scene.islands) {
    const p = i.partition;
    specs.push({
      id: `island:${p.key}`,
      kind: "island",
      text: partitionName(p),
      detail: p.headcount !== null ? `${p.headcount}名` : `${p.orgCount}組織`,
      color: islandCss(i),
      dim: active !== null && !active.islands.has(i.index),
      priority: 9700 - i.index,
      anchor: [i.x, islandTop(i), i.z],
    });
  }
  // 段の札: 輪の縁の付け根から外へ細い引き出し線を出し、その先に「3段目 課 25組織」。付け根の角度は段ごとにずらす。
  // 拠点で分けたときは島ごとに付けるが、真ん中の島（人数の多い島）を優先し、ほかの島の札は組織の名前より後に置く（重なれば出さない）
  for (const l of scene.layers) {
    const angle = layerLabelAngle(l.depth);
    const cos = Math.cos(angle);
    const sin = Math.sin(angle);
    const tip = l.radius + leaderLength(l.radius);
    specs.push({
      id: `layer:${l.entityIndex}:${l.depth}`,
      kind: "layer",
      text: [`${l.depth}段目`, l.label, `${l.orgCount}組織`].filter(Boolean).join(" "),
      dim: active !== null && !active.layers.has(`${l.entityIndex}:${l.depth}`),
      priority: scene.islands.length > 0 && l.entityIndex !== 0 ? 500 - l.depth : 6000 - l.depth,
      anchor: [l.x + tip * cos, l.y, l.z + tip * sin],
      leader: { from: [l.x + l.radius * cos, l.y, l.z + l.radius * sin], pivot: [l.x, l.y, l.z] },
    });
  }
  return specs;
}

function SceneContent({ scene, byId, palette, focus, hoveredId, coloring, fitRequest, onSelect, onHover, labels, overlay, layoutKey }: {
  scene: Scene3D;
  layoutKey: string;
  coloring: SiteColoring | null;
  byId: Map<string, Placed3D>;
  palette: Palette;
  focus: Focus | null;
  hoveredId: string | null;
  fitRequest: number;
  onSelect: (p: Placed3D) => void;
  onHover: (p: Placed3D | null, e?: ThreeEvent<PointerEvent>) => void;
  labels: LabelSpec[];
  overlay: RefObject<HTMLDivElement | null>;
}) {
  const reduced = useReducedMotion() ?? false;
  const controls = useOrbitControls(reduced);
  const morph = useSceneMorph(scene, reduced);
  const active = useMemo(() => activeOf(focus, byId), [focus, byId]);
  const selected = focus ? (byId.get(focus.id) ?? null) : null;
  const hovered = hoveredId ? (byId.get(hoveredId) ?? null) : null;
  return (
    <>
      <color attach="background" args={[palette.bg]} />
      <Atmosphere palette={palette} controls={controls} />
      <SpinClock reduced={reduced} />
      <hemisphereLight args={["#ffffff", palette.grid, palette.light.ambient]} />
      <directionalLight position={[40, 80, 30]} intensity={palette.light.key} />
      <directionalLight position={[-60, 30, -40]} intensity={palette.light.key * 0.25} />
      <Layers scene={scene} palette={palette} active={active} />
      {scene.islands.length > 0 && <Islands scene={scene} palette={palette} coloring={coloring} active={active} />}
      <Edges scene={scene} palette={palette} focus={focus} morph={morph} byId={byId} />
      {focus && focus.upperPairs.length > 0 && <UpperLinks pairs={focus.upperPairs} byId={byId} palette={palette} />}
      <Orgs scene={scene} palette={palette} focus={focus} hoveredId={hoveredId} coloring={coloring} morph={morph} onSelect={onSelect} onHover={onHover} />
      <SelectionMarker placed={selected} palette={palette} reduced={reduced} />
      {hovered && hovered !== selected && <HoverRing placed={hovered} palette={palette} />}
      {!coloring && <CrossArcs focus={focus} byId={byId} palette={palette} />}
      <PersonDots scene={scene} palette={palette} controls={controls} morph={morph} />
      <LabelProjector specs={labels} overlay={overlay} morph={morph} />
      <CameraRig scene={scene} byId={byId} focus={focus} fitRequest={fitRequest} controls={controls} reduced={reduced} layoutKey={layoutKey} />
    </>
  );
}

/** 「図の見方」のモジュールの見本（白い外装・黒鉛色の台座・上面の板にデータの色）。 */
function ModuleIcon() {
  return (
    <svg aria-hidden="true" width="18" height="14" viewBox="0 0 18 14" className="line-key">
      <path d="M1.5 6v4.5c0 1.6 3.4 2.9 7.5 2.9s7.5-1.3 7.5-2.9V6" fill="var(--signal-ink)" />
      <path d="M2.2 5v4c0 1.4 3 2.5 6.8 2.5s6.8-1.1 6.8-2.5V5" fill="var(--surface)" stroke="var(--muted)" strokeWidth="0.6" />
      <ellipse cx="9" cy="5" rx="6.8" ry="2.6" fill="var(--surface)" stroke="var(--muted)" strokeWidth="0.6" />
      <ellipse cx="9" cy="5" rx="5" ry="1.8" fill="var(--series-primary)" />
    </svg>
  );
}

/** 段ごとの底面の形の見本（shapes3d.ts の形をそのまま描く）。 */
function TierShapeIcon({ tier }: { tier: number }) {
  const pts = TIER_SHAPES[tier]!.map(([x, z]) => `${(7 + x * 5.2).toFixed(2)},${(7 + z * 5.2).toFixed(2)}`).join(" ");
  return (
    <svg aria-hidden="true" width="14" height="14" viewBox="0 0 14 14" className="line-key">
      <polygon points={pts} fill="var(--surface)" stroke="var(--fg)" strokeWidth="1" strokeLinejoin="round" />
    </svg>
  );
}

/** 選択の印（黄と黒鉛色の縞の輪）の見本。 */
function HazardIcon() {
  return (
    <svg aria-hidden="true" width="18" height="12" viewBox="0 0 18 12" className="line-key">
      <defs>
        <pattern id="hazard3d" width="4" height="4" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
          <rect width="4" height="4" fill="var(--signal-ink)" />
          <rect width="2" height="4" fill="var(--signal)" />
        </pattern>
      </defs>
      <ellipse cx="9" cy="6" rx="7.5" ry="4.5" fill="none" stroke="url(#hazard3d)" strokeWidth="2.6" />
    </svg>
  );
}

function LayersIcon() {
  return (
    <svg aria-hidden="true" width="18" height="12" viewBox="0 0 18 12" className="line-key" fill="none" stroke="var(--muted)" strokeWidth="1.2">
      <ellipse cx="9" cy="2.5" rx="4" ry="1.5" />
      <ellipse cx="9" cy="6" rx="6" ry="2" />
      <ellipse cx="9" cy="9.5" rx="8" ry="2" />
    </svg>
  );
}

/** 「図の見方」の中身。読み方を短い一覧にまとめる（画面には常に出さない）。 */
function HowToRead({ stats }: { stats: boolean }) {
  return (
    <>
      <p className="m-0 flex items-center gap-2">
        <LayersIcon />
        上の段ほど上位の組織です（法人 → 1段目 → 2段目 …）。各段の札の名前は、その段でいちばん多い組織の種類です。
      </p>
      <div>
        <p className="m-0">段ごとに底面の形が違います。</p>
        <ul className="m-0 mt-1 flex list-none flex-wrap gap-x-3 gap-y-1 p-0">
          {Array.from({ length: TIER_COUNT }, (_, t) => (
            <li key={t} className="inline-flex items-center gap-1">
              <TierShapeIcon tier={t} />
              {TIER_NAMES[t]}
            </li>
          ))}
        </ul>
      </div>
      {stats ? (
        <p className="m-0 flex items-start gap-2">
          <ModuleIcon />
          <span>底面の広さが、その組織に所属する人数です（形が違っても広さで比べられます）。近づくと 1 人を 1 つの点で表します（1 組織に 120 人まで）。</span>
        </p>
      ) : (
        <p className="m-0 text-muted">人数の集計 API が未提供のため、人数は表していません。</p>
      )}
      <p className="m-0 flex items-start gap-2">
        <HazardIcon />
        <span>選んだ組織は黄と黒の縞の輪で囲みます。関係のない組織・段・島は薄く表示します。</span>
      </p>
      {stats && <p className="m-0">組織を選ぶと、兼務・出向の相手先を色の線で結びます。</p>}
      <p className="m-0">「拠点で色分け」では、上面の色が組織の拠点です（人数の多い 3 拠点まで。ほかは灰色）。色分けの間は兼務・出向の線を出しません。</p>
      <p className="m-0">
        「拠点で分ける」では、拠点ごとの島に分けて並べます。段の高さはどの島も同じです。上位の組織が別の拠点にある組織を選ぶと、上位の組織と細い破線で結びます。
      </p>
      <p className="m-0 text-muted">ドラッグで回転、ホイール・ピンチで拡大縮小、右ドラッグ・2 本指で移動します。人数の表は組織図の「一覧表」にあります。</p>
    </>
  );
}

export function Overview3D() {
  const { adapter } = useAdapter();
  const { asOf, orgId, selectOrg } = useNav();
  const tree = useOrgTree(asOf);
  const stats = useOrgStats(asOf);
  const theme = useResolvedTheme();
  const palette = PALETTE[theme];
  const forest = useMemo(() => (tree.data ? buildForest(tree.data, stats.data ?? null) : null), [tree.data, stats.data]);
  const [splitMode, setSplitMode] = useSiteSplitMode();
  const split = useMemo(() => (forest && splitMode ? splitBySite(forest) : null), [forest, splitMode]);
  const scene = useMemo(() => (split ? layout3dSplit(split) : forest ? layout3d(forest) : null), [forest, split]);
  const byId = useMemo(() => new Map((scene?.nodes ?? []).map((p) => [p.node.id, p])), [scene]);
  const [hover, setHover] = useState<{ p: Placed3D; x: number; y: number; width: number } | null>(null);
  const [fitRequest, setFitRequest] = useState(0);
  const container = useRef<HTMLDivElement>(null);
  const overlay = useRef<HTMLDivElement>(null);
  // 兼務・出向の弧の種類（2D の組織図と同じ切り替え。画面ごとの状態）
  const [showConcurrent, setShowConcurrent] = useState(true);
  const [showSecondment, setShowSecondment] = useState(true);
  const links = useMemo(
    () => (stats.data?.links ?? []).filter((l) => (l.kind === "concurrent" ? showConcurrent : showSecondment)),
    [stats.data, showConcurrent, showSecondment],
  );
  const focus = useMemo(() => (scene ? focusOf(byId, orgId, links, split) : null), [scene, byId, orgId, links, split]);
  const hoveredId = hover?.p.node.id ?? null;
  const [siteMode, setSiteMode] = useSiteColorMode();
  const coloring = useMemo(() => {
    if (!forest || !siteMode) return null;
    const codes = new Map(forest.roots.map((r) => [r.legalEntityId, r.code]));
    return assignSiteColors(forest, (id) => codes.get(id) ?? "");
  }, [forest, siteMode]);
  const labels = useMemo(
    () => (scene ? buildLabels(scene, focus, hoveredId, (i) => islandColorCss(coloring, i), byId) : []),
    [scene, focus, hoveredId, coloring, byId],
  );
  const people = useMemo(
    () => (forest ? [...forest.byId.values()].reduce((s, n) => s + (n.headcount?.primary ?? 0), 0) : 0),
    [forest],
  );

  if (tree.error && !tree.data) {
    return (
      <div className="p-4">
        <ErrorNote>組織ツリーを取得できませんでした: {describeError(tree.error)}</ErrorNote>
      </div>
    );
  }
  if (!scene) {
    return (
      <div className="p-4">
        <Spinner />
      </div>
    );
  }
  const h = hover?.p.node.headcount;
  const orgCount = scene.nodes.filter((n) => n.node.kind === "org").length;
  const statsAvailable = stats.data !== null && stats.data !== undefined;
  return (
    <div className="flex size-full flex-col">
      {/* 操作の帯（2D の組織図と共通の部品・同じ順序）。図に重なる箱は置かず、読み方の説明は「図の見方」（?）の中に入れる */}
      <ChartToolbar>
        <SiteModeControls split={splitMode} onSplit={setSplitMode} color={siteMode} onColor={setSiteMode} coloring={coloring} />
        <CrossKindFilter
          concurrent={showConcurrent}
          onConcurrent={setShowConcurrent}
          secondment={showSecondment}
          onSecondment={setShowSecondment}
          available={statsAvailable}
          hidden={siteMode}
        />
        <span className="tabular text-muted">
          組織 <span className="font-bold text-fg">{orgCount}</span>
          {statsAvailable && (
            <>
              ・<span className="font-bold text-fg">{people}</span>名
            </>
          )}
        </span>
        {(tree.loading || stats.loading) && <Spinner />}
        {!statsAvailable && !stats.loading && <NoStatsBadge title="集計の API が未提供のため、人数と兼務・出向の線を表示していません" />}
        <ChartToolbarEnd>
          <ToolbarButton onClick={() => (orgId ? selectOrg(null) : setFitRequest((n) => n + 1))}>全体を表示</ToolbarButton>
          <HelpTip label="図の見方" width={340}>
            <HowToRead stats={statsAvailable} />
          </HelpTip>
        </ChartToolbarEnd>
      </ChartToolbar>
    <div ref={container} className="relative min-h-0 flex-1" style={{ cursor: hover ? "pointer" : "grab" }}>
      <p id="overview3d-help" className="sr-only">
        3D 組織俯瞰。上の段ほど上位の組織で、段ごとに底面の形が違い（法人は十二角、1 段目は八角、2 段目は六角、3 段目は円、4 段目以降は角を落とした四角）、底面の面積が主務の人数です。マウスのドラッグまたは 1 本指で回転、ホイールまたはピンチで拡大縮小、右ドラッグまたは 2
        本指で移動。組織を選ぶとその組織に寄って黄と黒の縞の輪で囲み、兼務・出向の相手先を弧で結びます。キーボードでは右の組織の一覧から選べます。人数の表は組織図の「表」にあります。
      </p>
      <WebGlBoundary>
        <Canvas
          key={adapter.label}
          frameloop="demand"
          dpr={[1, 2]}
          camera={{ fov: 40, near: 0.5, far: 8000, position: [0, 60, 120] }}
          gl={{ antialias: true, toneMapping: THREE.NeutralToneMapping }}
          aria-describedby="overview3d-help"
        >
          <SceneContent
            scene={scene}
            byId={byId}
            palette={palette}
            focus={focus}
            hoveredId={hoveredId}
            coloring={coloring}
            fitRequest={fitRequest}
            layoutKey={splitMode ? "split" : "entity"}
            labels={labels}
            overlay={overlay}
            onSelect={(p) => selectOrg(p.node.kind === "legal_entity" ? null : p.node.id)}
            onHover={(p, e) => {
              if (!p || !e) {
                setHover(null);
                return;
              }
              const rect = container.current?.getBoundingClientRect();
              setHover({ p, x: e.nativeEvent.clientX - (rect?.left ?? 0), y: e.nativeEvent.clientY - (rect?.top ?? 0), width: rect?.width ?? 9999 });
            }}
          />
        </Canvas>
      </WebGlBoundary>
      {/* 走査線（CSS。WebGL の描き直しを起こさない。動きを減らす設定では止まる） */}
      <div className="scan3d pointer-events-none absolute inset-0" aria-hidden="true" />
      <LabelOverlay specs={labels} overlay={overlay} />
      {hover && (
        <div
          className="pointer-events-none absolute z-10 min-w-[10em] rounded-sm border border-t-2 border-border border-t-signal bg-surface px-2 py-1.5 text-xs shadow"
          style={{ left: Math.min(hover.x + 14, hover.width - 200), top: hover.y + 14 }}
          aria-hidden="true"
        >
          <div className="text-muted">{hover.p.node.kind === "legal_entity" ? "法人" : (hover.p.node.levelName ?? "組織")}</div>
          <div className="font-bold">{hover.p.node.name}</div>
          {hover.p.node.site && (
            <div className="text-muted">
              拠点 <span className="text-fg">{hover.p.node.site.name}</span>
              {hover.p.node.site.inherited ? "（上位から）" : ""}
            </div>
          )}
          {split?.upperOf.get(hover.p.node.id) && (
            <div className="text-muted">
              上位 <span className="text-fg">{split.upperOf.get(hover.p.node.id)!.name}</span>（{split.upperOf.get(hover.p.node.id)!.siteName ?? "拠点なし"}）
            </div>
          )}
          {h && (
            <dl className="tabular m-0 mt-1 grid grid-cols-[auto_auto] gap-x-3">
              <dt className="flex items-center gap-1 text-muted">
                <LineKey kind="primary" width={12} />
                主務
              </dt>
              <dd className="m-0 text-right font-bold">{h.primary}名</dd>
              {hover.p.node.children.length > 0 && (
                <>
                  <dt className="pl-4 text-muted">配下計</dt>
                  <dd className="m-0 text-right font-bold">{hover.p.node.totalPrimary}名</dd>
                </>
              )}
              {h.concurrent > 0 && (
                <>
                  <dt className="flex items-center gap-1 text-muted">
                    <LineKey kind="concurrent" width={12} />
                    兼務で受け入れ
                  </dt>
                  <dd className="m-0 text-right font-bold">{h.concurrent}名</dd>
                </>
              )}
              {h.secondment > 0 && (
                <>
                  <dt className="flex items-center gap-1 text-muted">
                    <LineKey kind="secondment" width={12} />
                    出向で受け入れ
                  </dt>
                  <dd className="m-0 text-right font-bold">{h.secondment}名</dd>
                </>
              )}
            </dl>
          )}
        </div>
      )}
    </div>
    </div>
  );
}
