// 3D 表示（組織モード）のスキン「電脳空間」。設定の「3D 表示のスキン」で選ぶ（URL の skin=cyber でも）。ダークの配色のときだけ使い、
// ライトでは標準の見た目のまま。標準の見た目に足すもの:
// - 外装: 半透明の暗い外装。溝を細い光の線にし、輪郭をうっすら光らせる。上面の板は自ら光る
// - 空間: 深い青の空と地平の光の帯、細かい塵、法人の上へ伸びる光の柱、外周に浮かぶ薄い立方体と板
// - 直線と四角: 段を収める四角い枠（四隅の鉤と柱）、床の四角い区画、段の円盤の四角い点の行列、選んだ組織の四角い照準
// - 明るい線と板だけの薄いにじみ（ブルーム）
// 黄（signal）は選択の印だけに使い、表示灯は白にする。
import { useFrame, useThree } from "@react-three/fiber";
import { useEffect, useMemo } from "react";
import * as THREE from "three";
import { EffectComposer } from "three/examples/jsm/postprocessing/EffectComposer.js";
import { OutputPass } from "three/examples/jsm/postprocessing/OutputPass.js";
import { RenderPass } from "three/examples/jsm/postprocessing/RenderPass.js";
import { UnrealBloomPass } from "three/examples/jsm/postprocessing/UnrealBloomPass.js";
import type { Skin3dSetting } from "../../lib/settings";
import { PALETTE, type Cyber, type Palette } from "./common3d";
import { heightOf, type Scene3D } from "./layout3d";

const CYBER_DARK: Palette = {
  ...PALETTE.dark,
  bg: "#06111f",
  primary: "#3fc6ee",
  deemph: "#253142",
  entity: "#9fd6e4",
  edge: "#4b97ab",
  dot: "#e4fbff",
  wire: "#58b4d0",
  grid: "#1c4a68",
  shell: "#14222b",
  shellDeemph: "#0f151d",
  shellEntity: "#24404c",
  lamp: "#e4fbff",
  lampOff: "#253142",
  light: { ambient: 0.5, key: 1.5, env: 0.6 },
  cyber: { sky: "#030916", horizon: "#1c6aa0", seam: "#9af1ff", rim: "#43cfe8", rimPower: 0.9, glass: 0.5, plateGlow: 0.35, dust: 0.5, bloom: 0.32 },
};

/** スキンとテーマから 3D の色。電脳空間はダークだけ（ライトは標準のまま）。 */
export function skinPalette(skin: Skin3dSetting, theme: "light" | "dark"): Palette {
  return skin === "cyber" && theme === "dark" ? CYBER_DARK : PALETTE[theme];
}

/** 外装: 溝を細い光の線にし、輪郭（視線に対して寝た面）をうっすら光らせる。glass < 1 では輪郭と溝だけ濃い半透明。 */
export function cyberShellShader(cyber: Cyber) {
  return (shader: THREE.WebGLProgramParametersWithUniforms) => {
    shader.uniforms.uSeam = { value: new THREE.Color(cyber.seam) };
    shader.uniforms.uRim = { value: new THREE.Color(cyber.rim) };
    shader.uniforms.uRimPower = { value: cyber.rimPower };
    shader.vertexShader = shader.vertexShader
      .replace("#include <common>", "#include <common>\nvarying float vSeam;")
      // 溝＝黒鉛色の帯のうち、台座より上のもの
      .replace("#include <begin_vertex>", "#include <begin_vertex>\nvSeam = ( color.r < 0.5 && position.y > 0.3 ) ? 1.0 : 0.0;");
    shader.fragmentShader = shader.fragmentShader
      .replace("#include <common>", "#include <common>\nvarying float vSeam;\nuniform vec3 uSeam;\nuniform vec3 uRim;\nuniform float uRimPower;")
      .replace(
        "#include <opaque_fragment>",
        `{
          float rimF = pow( 1.0 - saturate( dot( normalize( vViewPosition ), normal ) ), 2.5 ) * uRimPower;
          outgoingLight = mix( outgoingLight, uRim, saturate( rimF ) );
          outgoingLight = mix( outgoingLight, uSeam, vSeam );
          diffuseColor.a = mix( diffuseColor.a, 1.0, saturate( max( rimF * 1.4, vSeam ) ) );
        }
        #include <opaque_fragment>`,
      );
  };
}

/** 上面の板: データの色を自ら光らせる（陰に入っても色が沈まない）。 */
export function cyberPlateShader(cyber: Cyber) {
  return (shader: THREE.WebGLProgramParametersWithUniforms) => {
    shader.uniforms.uGlow = { value: cyber.plateGlow };
    shader.fragmentShader = shader.fragmentShader
      .replace("#include <common>", "#include <common>\nuniform float uGlow;")
      .replace("#include <opaque_fragment>", "outgoingLight += diffuseColor.rgb * uGlow;\n#include <opaque_fragment>");
  };
}

const BACKDROP_VERTEX = /* glsl */ `
varying vec3 vDir;
varying vec2 vScreen;
void main() {
  vScreen = position.xy;
  vec4 far = inverse( projectionMatrix * viewMatrix ) * vec4( position.xy, 1.0, 1.0 );
  vDir = far.xyz / far.w - cameraPosition;
  gl_Position = vec4( position.xy, 1.0, 1.0 );
}
`;

const BACKDROP_FRAGMENT = /* glsl */ `
uniform vec3 uBg;
uniform vec3 uSky;
uniform vec3 uHorizon;
varying vec3 vDir;
varying vec2 vScreen;
void main() {
  float h = normalize( vDir ).y;
  // 地平より下は地の色（床の霧と同じ）。地平に細い光の帯、上は空の色へ
  vec3 c = mix( uBg, uSky, smoothstep( 0.0, 0.45, h ) );
  float band = exp( -pow( ( h - 0.015 ) / 0.07, 2.0 ) );
  c = mix( c, uHorizon, band * 0.9 );
  // 画面の隅をわずかに落とす
  c *= 1.0 - 0.22 * smoothstep( 0.55, 1.35, length( vScreen ) );
  gl_FragColor = vec4( c, 1.0 );
  #include <colorspace_fragment>
}
`;

/** 背景: 地平の細い光の帯と、上へ向かって沈む空（画面いっぱいの板 1 枚）。 */
export function Backdrop({ palette }: { palette: Palette }) {
  const cyber = palette.cyber!;
  const material = useMemo(
    () =>
      new THREE.ShaderMaterial({
        vertexShader: BACKDROP_VERTEX,
        fragmentShader: BACKDROP_FRAGMENT,
        depthTest: false,
        depthWrite: false,
        uniforms: {
          uBg: { value: new THREE.Color(palette.bg) },
          uSky: { value: new THREE.Color(cyber.sky) },
          uHorizon: { value: new THREE.Color(cyber.horizon) },
        },
      }),
    [palette, cyber],
  );
  useEffect(() => () => material.dispose(), [material]);
  return (
    <mesh material={material} frustumCulled={false} renderOrder={-100} raycast={() => null}>
      <planeGeometry args={[2, 2]} />
    </mesh>
  );
}

/** 決まった並びの擬似乱数（描くたびに同じ配置にする）。 */
function lcg(seed: number) {
  let s = seed >>> 0;
  return () => {
    s = (Math.imul(s, 1664525) + 1013904223) >>> 0;
    return s / 4294967296;
  };
}

/** 空間に浮く細かい塵（1〜2 px の点。奥行きの手がかり）。 */
export function DataDust({ scene, palette }: { scene: Scene3D; palette: Palette }) {
  const cyber = palette.cyber!;
  const geometry = useMemo(() => {
    const rand = lcg(20261111);
    const n = 700;
    const pos = new Float32Array(n * 3);
    const top = Math.max(...scene.nodes.map((p) => p.y)) + 30;
    for (let i = 0; i < n; i++) {
      const r = Math.sqrt(rand()) * scene.extent * 1.3;
      const a = rand() * Math.PI * 2;
      pos[i * 3] = scene.center[0] + r * Math.cos(a);
      pos[i * 3 + 1] = scene.floorY + rand() * (top - scene.floorY);
      pos[i * 3 + 2] = scene.center[2] + r * Math.sin(a);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.BufferAttribute(pos, 3));
    return g;
  }, [scene]);
  useEffect(() => () => geometry.dispose(), [geometry]);
  return (
    <points geometry={geometry} raycast={() => null}>
      <pointsMaterial color={cyber.rim} size={1.6} sizeAttenuation={false} transparent opacity={cyber.dust} depthWrite={false} />
    </points>
  );
}

/** 法人の塔の上へ伸びる細い光の柱（上へ行くほど地の色に溶ける）。 */
export function Pillars({ scene, palette }: { scene: Scene3D; palette: Palette }) {
  const cyber = palette.cyber!;
  const geometry = useMemo(() => {
    const v: number[] = [];
    const c: number[] = [];
    const from = new THREE.Color(cyber.rim);
    const to = new THREE.Color(cyber.sky);
    const len = Math.max(30, scene.extent * 0.6);
    for (const p of scene.nodes) {
      if (p.node.kind !== "legal_entity") continue;
      const y = p.y + heightOf(p);
      v.push(p.x, y, p.z, p.x, y + len, p.z);
      c.push(from.r, from.g, from.b, to.r, to.g, to.b);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.Float32BufferAttribute(v, 3));
    g.setAttribute("color", new THREE.Float32BufferAttribute(c, 3));
    return g;
  }, [scene, cyber]);
  useEffect(() => () => geometry.dispose(), [geometry]);
  return (
    <lineSegments geometry={geometry} raycast={() => null}>
      <lineBasicMaterial vertexColors fog={false} />
    </lineSegments>
  );
}

const DISC_VERTEX = /* glsl */ `
varying vec2 vUv;
varying vec3 vWorld;
void main() {
  vUv = uv;
  vec4 w = modelMatrix * vec4( position, 1.0 );
  vWorld = w.xyz;
  gl_Position = projectionMatrix * viewMatrix * w;
}
`;

const DISC_FRAGMENT = /* glsl */ `
uniform vec3 uColor;
uniform float uOpacity;
varying vec2 vUv;
varying vec3 vWorld;
void main() {
  float r = length( vUv - 0.5 ) * 2.0;
  // 四角い点の行列（世界座標に固定）。縁へ近いほど濃く、中心は空ける
  vec2 c = vWorld.xz / 2.2;
  vec2 f = abs( fract( c ) - 0.5 );
  float d = max( f.x, f.y ) * 1.25 / max( length( fwidth( c ) ), 1e-4 );
  float dotA = 1.0 - smoothstep( 0.5, 1.3, d );
  float rim = smoothstep( 0.45, 1.0, r );
  float a = ( 0.03 + dotA * ( 0.03 + 0.22 * rim * rim ) ) * uOpacity;
  gl_FragColor = vec4( uColor, a );
  #include <colorspace_fragment>
}
`;

/** 段の円盤の面: 薄い塗りの代わりに、細かい点の行列。 */
export function DotDisc({ x, y, z, radius, color, opacity }: { x: number; y: number; z: number; radius: number; color: string; opacity: number }) {
  const material = useMemo(
    () =>
      new THREE.ShaderMaterial({
        vertexShader: DISC_VERTEX,
        fragmentShader: DISC_FRAGMENT,
        transparent: true,
        depthWrite: false,
        side: THREE.DoubleSide,
        uniforms: { uColor: { value: new THREE.Color() }, uOpacity: { value: 1 } },
      }),
    [],
  );
  useEffect(() => () => material.dispose(), [material]);
  useEffect(() => setDiscUniforms(material, color, opacity), [material, color, opacity]);
  return (
    <mesh position={[x, y, z]} rotation={[-Math.PI / 2, 0, 0]} material={material} raycast={() => null}>
      <circleGeometry args={[radius, 96]} />
    </mesh>
  );
}

// three のオブジェクトの変更（フックの外の関数で行う）
function setDiscUniforms(material: THREE.ShaderMaterial, color: string, opacity: number) {
  (material.uniforms.uColor!.value as THREE.Color).set(color);
  material.uniforms.uOpacity!.value = opacity;
}

/** 薄いにじみ（明るい線と板だけが少しにじむ）。描画を引き取る（useFrame の優先度 1）。 */
export function Bloom({ strength }: { strength: number }) {
  const { gl, scene, camera, size } = useThree();
  const composer = useMemo(() => {
    const target = new THREE.WebGLRenderTarget(1, 1, { type: THREE.HalfFloatType, samples: 4 });
    const c = new EffectComposer(gl, target);
    c.addPass(new RenderPass(scene, camera));
    c.addPass(new UnrealBloomPass(new THREE.Vector2(1, 1), strength, 0.5, 0.8));
    c.addPass(new OutputPass());
    return c;
  }, [gl, scene, camera, strength]);
  useEffect(() => () => composer.dispose(), [composer]);
  useEffect(() => {
    // 高精細の画面でも 1.5 倍まで（にじみの処理は画素の数に比例して重い。名前の札は HTML なので鮮明なまま）
    composer.setPixelRatio(Math.min(gl.getPixelRatio(), 1.5));
    composer.setSize(size.width, size.height);
  }, [composer, gl, size]);
  useFrame(() => composer.render(), 1);
  return null;
}

/** 四隅の鉤（L 字）を足す。half＝中心から辺まで、len＝鉤の腕の長さ。 */
function brackets(v: number[], x: number, y: number, z: number, half: number, len: number) {
  for (const sx of [-1, 1]) {
    for (const sz of [-1, 1]) {
      const cx = x + sx * half;
      const cz = z + sz * half;
      v.push(cx, y, cz, cx - sx * len, y, cz, cx, y, cz, cx, y, cz - sz * len);
    }
  }
}

/** 四角の輪郭を足す。 */
function squareLine(v: number[], x: number, y: number, z: number, half: number) {
  const c: Array<[number, number]> = [[-1, -1], [1, -1], [1, 1], [-1, 1]];
  c.forEach(([ax, az], i) => {
    const [bx, bz] = c[(i + 1) % 4]!;
    v.push(x + ax * half, y, z + az * half, x + bx * half, y, z + bz * half);
  });
}

/** 同じ中心に重なる段のまとまり（法人・島ごと）。half は段を囲む四角の、中心から辺まで。 */
export interface Stack {
  x: number;
  z: number;
  half: number;
  top: number;
}

const stackKey = (x: number, z: number) => `${Math.round(x * 10)}:${Math.round(z * 10)}`;

export function stacksOf(scene: Scene3D): Map<string, Stack> {
  const stacks = new Map<string, Stack>();
  for (const l of scene.layers) {
    const half = l.radius * 1.05 + 1;
    const p = stacks.get(stackKey(l.x, l.z));
    if (!p) stacks.set(stackKey(l.x, l.z), { x: l.x, z: l.z, half, top: l.y });
    else {
      p.half = Math.max(p.half, half);
      p.top = Math.max(p.top, l.y);
    }
  }
  return stacks;
}

/**
 * 直線と四角の語彙（回る輪・円盤の曲線に対して、動かない角を置く）。法人（島）ごとに、いちばん広い段に合わせた同じ大きさの四角を
 * 各段の高さに重ね、四隅を床からの細い柱でつなぐ（円盤を収める棚）。床には一回り大きい四角い区画。
 * - 段の四角: 細い輪郭と、濃い四隅の鉤・辺の中央の短い目盛り
 * - 選んだ組織に関係しない段の鉤は薄くする
 */
export function Frames({ scene, palette, activeLayers }: { scene: Scene3D; palette: Palette; activeLayers: Set<string> | null }) {
  const { strong, faint, plot } = useMemo(() => {
    const on: number[] = [];
    const off: number[] = [];
    const floor: number[] = [];
    const stacks = stacksOf(scene);
    for (const l of scene.layers) {
      const half = stacks.get(stackKey(l.x, l.z))!.half;
      const v = !activeLayers || activeLayers.has(`${l.entityIndex}:${l.depth}`) ? on : off;
      brackets(v, l.x, l.y, l.z, half, Math.max(1.4, half * 0.12));
      const t = Math.max(0.6, half * 0.04);
      v.push(l.x - half, l.y, l.z, l.x - half - t, l.y, l.z, l.x + half, l.y, l.z, l.x + half + t, l.y, l.z);
      v.push(l.x, l.y, l.z - half, l.x, l.y, l.z - half - t, l.x, l.y, l.z + half, l.x, l.y, l.z + half + t);
      squareLine(off, l.x, l.y, l.z, half);
    }
    const y = scene.floorY + 0.03;
    for (const p of stacks.values()) {
      const half = p.half;
      // 四隅の柱（床からいちばん上の段まで）
      for (const sx of [-1, 1]) for (const sz of [-1, 1]) off.push(p.x + sx * half, y, p.z + sz * half, p.x + sx * half, p.top, p.z + sz * half);
      squareLine(floor, p.x, y, p.z, half);
      const outer = half + Math.max(2, half * 0.08);
      brackets(floor, p.x, y, p.z, outer, outer * 0.1);
      for (const sx of [-1, 1]) for (const sz of [-1, 1]) squareLine(floor, p.x + sx * half, y, p.z + sz * half, 0.6);
    }
    const geo = (v: number[]) => {
      const g = new THREE.BufferGeometry();
      g.setAttribute("position", new THREE.Float32BufferAttribute(v, 3));
      return g;
    };
    return { strong: geo(on), faint: geo(off), plot: geo(floor) };
  }, [scene, activeLayers]);
  useEffect(
    () => () => {
      strong.dispose();
      faint.dispose();
      plot.dispose();
    },
    [strong, faint, plot],
  );
  const cyber = palette.cyber!;
  return (
    <group>
      <lineSegments geometry={strong} raycast={() => null}>
        <lineBasicMaterial color={cyber.rim} transparent opacity={0.9} />
      </lineSegments>
      <lineSegments geometry={faint} raycast={() => null}>
        <lineBasicMaterial color={cyber.rim} transparent opacity={0.2} />
      </lineSegments>
      <lineSegments geometry={plot} raycast={() => null}>
        <lineBasicMaterial color={palette.wire} transparent opacity={0.65} />
      </lineSegments>
    </group>
  );
}

/** 選んだ組織を囲む四角い照準（四隅の鉤。縞の輪の外側）。 */
export function Reticle({ x, y, z, reach, palette }: { x: number; y: number; z: number; reach: number; palette: Palette }) {
  const geometry = useMemo(() => {
    const v: number[] = [];
    const half = reach * 1.25 + 1.1;
    brackets(v, 0, 0, 0, half, half * 0.3);
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.Float32BufferAttribute(v, 3));
    return g;
  }, [reach]);
  useEffect(() => () => geometry.dispose(), [geometry]);
  return (
    <lineSegments geometry={geometry} position={[x, y, z]} raycast={() => null} renderOrder={2}>
      <lineBasicMaterial color={palette.lamp ?? palette.signal} fog={false} toneMapped={false} />
    </lineSegments>
  );
}

const CUBE_VERTEX = /* glsl */ `
varying vec2 vUv;
varying vec3 vTint;
void main() {
  vUv = uv;
  vTint = instanceColor;
  gl_Position = projectionMatrix * viewMatrix * modelMatrix * instanceMatrix * vec4( position, 1.0 );
}
`;

const CUBE_FRAGMENT = /* glsl */ `
varying vec2 vUv;
varying vec3 vTint;
void main() {
  // 面はごく薄く、辺だけ細く明るく（画面上の太さをそろえる）
  vec2 d = min( vUv, 1.0 - vUv ) / max( fwidth( vUv ), vec2( 1e-5 ) );
  float e = 1.0 - smoothstep( 0.7, 1.7, min( d.x, d.y ) );
  float a = max( e * 0.85, 0.04 );
  gl_FragColor = vec4( vTint * a, 1.0 );
  #include <colorspace_fragment>
}
`;

/**
 * 空間に浮かぶ立方体のかたまりと薄い板（辺だけが細く光る。面はほぼ透明）。軸にそろえて置き、回さない。
 * 組織の図に重ならないよう、外周の輪の帯に置く。描画呼び出しは 1 回（InstancedMesh）。
 */
export function FloatingCubes({ scene, palette }: { scene: Scene3D; palette: Palette }) {
  const cyber = palette.cyber!;
  const geometry = useMemo(() => new THREE.BoxGeometry(1, 1, 1), []);
  const material = useMemo(
    () =>
      new THREE.ShaderMaterial({
        vertexShader: CUBE_VERTEX,
        fragmentShader: CUBE_FRAGMENT,
        blending: THREE.AdditiveBlending,
        transparent: true,
        depthWrite: false,
        side: THREE.DoubleSide,
      }),
    [],
  );
  const mesh = useMemo(() => {
    const rand = lcg(7141);
    const items: Array<{ x: number; y: number; z: number; sx: number; sy: number; sz: number; b: number; white: boolean }> = [];
    const top = Math.max(...scene.nodes.map((p) => p.y)) + 18;
    const stacks = [...stacksOf(scene).values()];
    // 組織の図に重ねない（段を囲む四角の枠と、その少し外には置かない）
    const place = (): [number, number, number] => {
      let x = 0;
      let z = 0;
      for (let attempt = 0; attempt < 24; attempt++) {
        const r = scene.extent * (0.5 + rand() * 0.9);
        const a = rand() * Math.PI * 2;
        x = scene.center[0] + r * Math.cos(a);
        z = scene.center[2] + r * Math.sin(a);
        const px = x;
        const pz = z;
        if (!stacks.some((st) => Math.abs(px - st.x) < st.half + 9 && Math.abs(pz - st.z) < st.half + 9)) break;
      }
      return [x, scene.floorY + 2 + rand() * (top - scene.floorY) * 0.8, z];
    };
    // 立方体のかたまり（格子にそろえた数個）
    for (let c = 0; c < 30; c++) {
      const [cx, cy, cz] = place();
      const s = 0.7 + rand() * 1.9;
      const n = 3 + Math.floor(rand() * 6);
      const b = 0.08 + rand() * 0.14;
      for (let i = 0; i < n; i++) {
        const k = () => Math.round((rand() - 0.5) * 4) * s;
        const big = rand() < 0.2 ? 2 : 1;
        items.push({ x: cx + k(), y: cy + k(), z: cz + k(), sx: s * 0.86 * big, sy: s * 0.86 * big, sz: s * 0.86 * big, b, white: false });
      }
    }
    // 薄い板（縦が多く、少し横）
    for (let i = 0; i < 36; i++) {
      const [x, y, z] = place();
      const w = 3 + rand() * 9;
      const h = 2 + rand() * 6;
      const t = rand();
      const size: [number, number, number] = t < 0.4 ? [w, h, 0.05] : t < 0.8 ? [0.05, h, w] : [w, 0.05, h];
      items.push({ x, y, z, sx: size[0], sy: size[1], sz: size[2], b: 0.07 + rand() * 0.11, white: false });
    }
    const m = new THREE.InstancedMesh(geometry, material, items.length);
    const mat = new THREE.Matrix4();
    const tint = new THREE.Color();
    items.forEach((it, i) => {
      mat.makeScale(it.sx, it.sy, it.sz).setPosition(it.x, it.y, it.z);
      m.setMatrixAt(i, mat);
      tint.set(it.white ? "#ffffff" : cyber.rim).multiplyScalar(it.b);
      m.setColorAt(i, tint);
    });
    m.raycast = () => undefined;
    m.frustumCulled = false;
    return m;
  }, [scene, cyber, geometry, material]);
  useEffect(
    () => () => {
      geometry.dispose();
      material.dispose();
    },
    [geometry, material],
  );
  useEffect(() => () => void mesh.dispose(), [mesh]);
  return <primitive object={mesh} />;
}
