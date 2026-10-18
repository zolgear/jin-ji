// 3D 表示（組織モード・建物・フロアモード）で共有する部品: 色（PALETTE）・視点の操作・環境光。
// 白基調のワイヤーフレームの空間に工業製品風の質感を置く見た目を、両方のモードでそろえる。
import { useFrame, useThree } from "@react-three/fiber";
import { useEffect, useLayoutEffect, useMemo, useState } from "react";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { RoomEnvironment } from "three/examples/jsm/environments/RoomEnvironment.js";

export const PALETTE = {
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
export type Palette = (typeof PALETTE)[keyof typeof PALETTE];

/** 視点の操作（ドラッグで回転・ホイールとピンチで拡大縮小・右ドラッグと 2 本指で移動）。 */
export function useOrbitControls(reduced: boolean) {
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


// three のシーンの変更（フックの外の関数で行う）
export function setEnvironment(gl: THREE.WebGLRenderer, scene: THREE.Scene) {
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

/** 床の格子（シェーダーで線を引く。テクスチャ無し・描画呼び出し 1 回）。中心から離れるほど薄れる。cell は格子の目（m）。 */
export function GridFloor({ center, y, size, cell, palette }: { center: [number, number]; y: number; size: number; cell: number; palette: Palette }) {
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
  useLayoutEffect(() => setGridUniforms(material, palette, center, size, cell), [material, palette, center, size, cell]);
  return (
    <mesh position={[center[0], y, center[1]]} rotation={[-Math.PI / 2, 0, 0]} material={material} raycast={() => null} renderOrder={-1}>
      <planeGeometry args={[size, size]} />
    </mesh>
  );
}

function setGridUniforms(material: THREE.ShaderMaterial, palette: Palette, center: [number, number], size: number, cell: number) {
  (material.uniforms.uColor!.value as THREE.Color).set(palette.grid);
  (material.uniforms.uCenter!.value as THREE.Vector2).set(center[0], center[1]);
  material.uniforms.uRadius!.value = size / 2;
  material.uniforms.uCell!.value = cell;
}
