// 建物・フロアモードの 3D（案 B: 分解アクソメ。仕様 §5.1〜§5.3）。
//
// 見た目は組織モードと同じ白基調のワイヤーフレームの空間（格子の床・距離のフォグ・環境光・工業製品風の白い外装）にそろえる:
// - 階を大きく離して積み、四隅を細い縦の破線で揃える。選んだ階だけ机・人の形（組織の色）・空席の輪・使えない席の淡い斜線を描き、
//   ほかの階は埋まり具合を灰の点で示す（埋まっている席は濃い灰、空いている席は淡い灰）
// - 線は細い縦の破線と輪郭だけ。直角の配線・発光・光の脈動・粒子は使わない。選択は控えめなアクセント色（--accent と同じ値）の細い輪
// - 動きは階・人を選んだときのカメラの移動だけ（prefers-reduced-motion では瞬時）
// 性能（§5.3）: 選んだ階の机・人・空席の輪・使えない席・今日の印はそれぞれ 1 つの InstancedMesh。ほかの階の点は全体で 2 つの InstancedMesh。
// 端末の性能が低いときはほかの階の点を描かず、札の数字だけにする（LOD）。frameloop="demand"・ポストプロセスなし。
import { Canvas, useFrame, useThree, type ThreeEvent } from "@react-three/fiber";
import { useReducedMotion } from "motion/react";
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type RefObject } from "react";
import * as THREE from "three";
import type { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { mergeGeometries } from "three/examples/jsm/utils/BufferGeometryUtils.js";
import type { Building, FloorLayout } from "../../data/types";
import { GridFloor, setEnvironment, useOrbitControls, type Palette } from "../overview3d/common3d";
import { chairPosition, deskSize, type FloorView, type SeatView } from "./floorModel";
import type { StackedFloor } from "./floorLayout3d";
import { SeatTooltip } from "./FloorPlan2D";

/** 選択の色（src/index.css の --accent と同じ値。控えめな濃い青 / ダークでは淡い青）。 */
const ACCENT = { light: "#1f4e8c", dark: "#8db8f2" } as const;
/** 人・空席の輪などの黒鉛色（--fg に近い値）。 */
const INK = { light: "#2b3440", dark: "#d5dce4" } as const;
/**
 * スラブの色（通常・選んだ階・薄くした階）とほかの階の点（人のいる席・空いている席）。ライトは組織モードの外装の白、
 * ダークは外装の明るい灰だとスラブの面が広くて重いので、地に近いグラファイトにする（自動反転ではなく値を選ぶ）。
 */
const SLAB = {
  light: { normal: "#eef1f4", selected: "#f6f8fa", dim: "#e6e9ed", used: "#8a96a6", free: "#c3c9d1" },
  dark: { normal: "#323c48", selected: "#3d4856", dim: "#252d37", used: "#c3ccd6", free: "#4d5866" },
} as const;

export interface SceneBuilding {
  building: Building;
  /** 外形の左下（m）の x と、奥の端の z（建物は z〜z + depth に入る）。 */
  x: number;
  z: number;
  width: number;
  depth: number;
  stack: StackedFloor[];
}

export interface FloorLabel {
  text: string;
  detail: string;
}

export interface FloorScene3DProps {
  theme: "light" | "dark";
  palette: Palette;
  buildings: SceneBuilding[];
  /** 表示する階の配置図（ほかの階の点に使う。人は含まない）。 */
  layouts: ReadonlyMap<string, FloorLayout>;
  /** 選んだ階の表示（無ければ建物・拠点の段）。 */
  detail: FloorView | null;
  selectedFloorId: string | null;
  selectedPersonId: string | null;
  floorLabels: ReadonlyMap<string, FloorLabel>;
  /** 建物の札（拠点の段）。 */
  buildingLabels: ReadonlyMap<string, FloorLabel>;
  lowEnd: boolean;
  fitRequest: number;
  /** スマホで下に詳細のシートが重なるか。 */
  sheet: boolean;
  onSelectFloor: (floorId: string) => void;
  onSelectBuilding: (buildingId: string) => void;
  onSelectPerson: (personId: string) => void;
}

// ---- 座標 ----

/** 建物の中の配置図の mm を、世界の m にする（配置図の奥＝ y の大きい方を、画面の奥＝ z の小さい方に）。 */
function worldOf(b: SceneBuilding, xMm: number, yMm: number): [number, number] {
  return [b.x + xMm / 1000, b.z + b.depth - yMm / 1000];
}

function floorOf(buildings: SceneBuilding[], floorId: string | null): { b: SceneBuilding; f: StackedFloor } | null {
  for (const b of buildings) {
    const f = b.stack.find((s) => s.id === floorId);
    if (f) return { b, f };
  }
  return null;
}

// ---- 部品 ----

/** 階のスラブ（白い外装の板）と輪郭。選んだ階以外は地に寄せて薄くする。 */
function Slab({ b, f, palette, theme, selected, dim, above, onClick, onHover }: {
  b: SceneBuilding;
  f: StackedFloor;
  palette: Palette;
  theme: "light" | "dark";
  selected: boolean;
  dim: boolean;
  /** 選んだ階より上の階（選んだ階の席が透けて見えるよう、ごく薄くする）。 */
  above: boolean;
  onClick: () => void;
  onHover: (on: boolean) => void;
}) {
  const thickness = f.compressed ? 0.12 : 0.22;
  const edges = useMemo(() => new THREE.EdgesGeometry(new THREE.BoxGeometry(b.width, thickness, b.depth)), [b.width, b.depth, thickness]);
  useEffect(() => () => edges.dispose(), [edges]);
  const shell = selected ? SLAB[theme].selected : dim ? SLAB[theme].dim : SLAB[theme].normal;
  return (
    <group position={[b.x + b.width / 2, f.y - thickness / 2, b.z + b.depth / 2]}>
      <mesh
        onClick={(e) => {
          e.stopPropagation();
          onClick();
        }}
        onPointerOver={(e) => {
          e.stopPropagation();
          onHover(true);
        }}
        onPointerOut={() => onHover(false)}
      >
        <boxGeometry args={[b.width, thickness, b.depth]} />
        <meshStandardMaterial color={shell} roughness={0.5} metalness={0.08} transparent={dim} opacity={above ? 0.3 : dim ? 0.82 : 1} depthWrite={!above} />
      </mesh>
      <lineSegments geometry={edges} raycast={() => null}>
        <lineBasicMaterial color={selected ? INK[theme] : palette.wire} transparent opacity={selected ? 0.9 : dim ? 0.45 : 0.75} />
      </lineSegments>
    </group>
  );
}

/** 四隅を揃える細い縦の破線（最下階から最上階まで）。 */
function CornerLines({ b, palette }: { b: SceneBuilding; palette: Palette }) {
  const geometry = useMemo(() => {
    const top = b.stack.at(-1)?.y ?? 0;
    const bottom = (b.stack[0]?.y ?? 0) - 0.22;
    const pts: number[] = [];
    for (const [x, z] of [
      [b.x, b.z],
      [b.x + b.width, b.z],
      [b.x, b.z + b.depth],
      [b.x + b.width, b.z + b.depth],
    ]) {
      pts.push(x!, bottom, z!, x!, top, z!);
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.Float32BufferAttribute(pts, 3));
    return g;
  }, [b]);
  const ref = useRef<THREE.LineSegments>(null);
  // 破線の長さ（ジオメトリが変わるたびに測り直す）
  useLayoutEffect(() => {
    ref.current?.computeLineDistances();
  });
  useEffect(() => () => geometry.dispose(), [geometry]);
  return (
    <lineSegments ref={ref} geometry={geometry} raycast={() => null}>
      <lineDashedMaterial color={palette.wire} dashSize={0.5} gapSize={0.45} transparent opacity={0.7} />
    </lineSegments>
  );
}

/** 拠点の段の建物の外形（ごく薄い箱と輪郭。建物の高さ＝階数）。 */
function BuildingShell({ b, palette }: { b: SceneBuilding; palette: Palette }) {
  const height = Math.max(1, (b.stack.at(-1)?.y ?? 0) - (b.stack[0]?.y ?? 0) + 3);
  const y0 = (b.stack[0]?.y ?? 0) - 0.2;
  const edges = useMemo(() => new THREE.EdgesGeometry(new THREE.BoxGeometry(b.width, height, b.depth)), [b.width, b.depth, height]);
  useEffect(() => () => edges.dispose(), [edges]);
  return (
    <group position={[b.x + b.width / 2, y0 + height / 2, b.z + b.depth / 2]}>
      <mesh raycast={() => null}>
        <boxGeometry args={[b.width, height, b.depth]} />
        <meshStandardMaterial color={palette.shell} transparent opacity={0.1} depthWrite={false} roughness={0.6} />
      </mesh>
      <lineSegments geometry={edges} raycast={() => null}>
        <lineBasicMaterial color={palette.wire} transparent opacity={0.6} />
      </lineSegments>
    </group>
  );
}

/** コア（EV・階段）の低い箱。どの階にも描く（建物の形が読めるように）。 */
function Cores({ buildings, layouts, palette, theme, selectedFloorId }: { buildings: SceneBuilding[]; layouts: ReadonlyMap<string, FloorLayout>; palette: Palette; theme: "light" | "dark"; selectedFloorId: string | null }) {
  const boxes = useMemo(() => {
    const out: Array<{ key: string; pos: [number, number, number]; size: [number, number, number]; dim: boolean }> = [];
    for (const b of buildings) {
      for (const f of b.stack) {
        const layout = layouts.get(f.id);
        if (!layout) continue;
        layout.fixtures.forEach((fx, i) => {
          if (fx.kind !== "core") return;
          const xs = fx.polygon.map((p) => p[0]);
          const ys = fx.polygon.map((p) => p[1]);
          const [x0, z1] = worldOf(b, Math.min(...xs), Math.min(...ys));
          const [x1, z0] = worldOf(b, Math.max(...xs), Math.max(...ys));
          const h = f.compressed ? 0.5 : 1.1;
          out.push({ key: `${f.id}:${i}`, pos: [(x0 + x1) / 2, f.y + h / 2, (z0 + z1) / 2], size: [x1 - x0, h, z1 - z0], dim: selectedFloorId !== null && f.id !== selectedFloorId });
        });
      }
    }
    return out;
  }, [buildings, layouts, selectedFloorId]);
  return (
    <group>
      {boxes.map((c) => (
        <mesh key={c.key} position={c.pos} raycast={() => null}>
          <boxGeometry args={c.size} />
          <meshStandardMaterial color={theme === "dark" ? (c.dim ? "#2c3540" : "#4a5563") : c.dim ? palette.shellDeemph : palette.shell} roughness={0.45} metalness={0.12} />
        </mesh>
      ))}
    </group>
  );
}

/** ほかの階の灰の点（埋まっている席は濃い灰、空いている席は淡い灰）。2 つの InstancedMesh。 */
function OtherFloorDots({ buildings, layouts, theme, selectedFloorId }: { buildings: SceneBuilding[]; layouts: ReadonlyMap<string, FloorLayout>; theme: "light" | "dark"; selectedFloorId: string | null }) {
  const { used, free } = useMemo(() => {
    const u: number[] = [];
    const v: number[] = [];
    for (const b of buildings) {
      for (const f of b.stack) {
        if (f.id === selectedFloorId) continue;
        for (const s of layouts.get(f.id)?.seats ?? []) {
          const [x, z] = worldOf(b, s.x_mm, s.y_mm);
          (s.status === "occupied" ? u : v).push(x, f.y + 0.03, z);
        }
      }
    }
    return { used: u, free: v };
  }, [buildings, layouts, selectedFloorId]);
  const geometry = useMemo(() => new THREE.CylinderGeometry(0.3, 0.3, 0.06, 10), []);
  useEffect(() => () => geometry.dispose(), [geometry]);
  return (
    <>
      <Dots positions={used} geometry={geometry} color={SLAB[theme].used} />
      <Dots positions={free} geometry={geometry} color={SLAB[theme].free} />
    </>
  );
}

function Dots({ positions, geometry, color }: { positions: number[]; geometry: THREE.BufferGeometry; color: string }) {
  const ref = useRef<THREE.InstancedMesh>(null);
  const count = positions.length / 3;
  const { invalidate } = useThree();
  useLayoutEffect(() => {
    const mesh = ref.current;
    if (!mesh) return;
    const m = new THREE.Matrix4();
    for (let i = 0; i < count; i++) {
      m.makeTranslation(positions[i * 3]!, positions[i * 3 + 1]!, positions[i * 3 + 2]!);
      mesh.setMatrixAt(i, m);
    }
    mesh.count = count;
    mesh.instanceMatrix.needsUpdate = true;
    mesh.computeBoundingSphere();
    invalidate();
  }, [positions, count, invalidate]);
  if (count === 0) return null;
  return (
    <instancedMesh key={count} ref={ref} args={[geometry, undefined, count]} raycast={() => null}>
      <meshBasicMaterial color={color} />
    </instancedMesh>
  );
}

/** 斜線のテクスチャ（使えない席。canvas で作る 1 枚）。 */
function useHatchTexture(theme: "light" | "dark") {
  const texture = useMemo(() => {
    const c = document.createElement("canvas");
    c.width = c.height = 32;
    const g = c.getContext("2d");
    if (g) {
      g.fillStyle = theme === "dark" ? "#2a333e" : "#eef1f4";
      g.fillRect(0, 0, 32, 32);
      g.strokeStyle = theme === "dark" ? "#8a96a6" : "#7d8a99";
      g.lineWidth = 5;
      for (let k = -32; k <= 64; k += 11) {
        g.beginPath();
        g.moveTo(k, 0);
        g.lineTo(k + 32, 32);
        g.stroke();
      }
    }
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    return t;
  }, [theme]);
  useEffect(() => () => texture.dispose(), [texture]);
  return texture;
}

/** 人の形（胴の円柱と頭の球を 1 つのジオメトリに）。 */
function personGeometry(): THREE.BufferGeometry {
  const body = new THREE.CylinderGeometry(0.17, 0.22, 0.78, 12).translate(0, 0.39, 0);
  const head = new THREE.SphereGeometry(0.16, 12, 8).translate(0, 0.98, 0);
  const merged = mergeGeometries([body.toNonIndexed(), head.toNonIndexed()]) ?? body;
  body.dispose();
  head.dispose();
  return merged;
}

interface DetailIndex {
  /** 机の InstancedMesh の添字 → 席。 */
  desks: SeatView[];
}

/** 選んだ階の詳細: エリアの面と枠・部屋の輪郭・机・人・空席の輪・使えない席・今日の印。 */
function FloorDetail({ b, f, view, palette, theme, onHover, onClickSeat }: {
  b: SceneBuilding;
  f: StackedFloor;
  view: FloorView;
  palette: Palette;
  theme: "light" | "dark";
  onHover: (seat: SeatView | null, e?: ThreeEvent<PointerEvent>) => void;
  onClickSeat: (seat: SeatView) => void;
}) {
  const { invalidate } = useThree();
  const hatch = useHatchTexture(theme);
  const geoms = useMemo(
    () => ({
      desk: new THREE.BoxGeometry(1, 1, 1).translate(0, 0.5, 0),
      person: personGeometry(),
      ring: new THREE.RingGeometry(0.2, 0.27, 24).rotateX(-Math.PI / 2),
      square: new THREE.PlaneGeometry(0.62, 0.62).rotateX(-Math.PI / 2),
      mark: new THREE.OctahedronGeometry(0.13),
    }),
    [],
  );
  useEffect(() => () => Object.values(geoms).forEach((g) => g.dispose()), [geoms]);
  const slotColor = (slot: number | null | undefined) => (slot === undefined || slot === null ? palette.siteOther : palette.site[slot]!);

  // エリアの面（組織の色をごく薄く）と枠（フリーアドレスは破線）、部屋の輪郭
  const zoneShapes = useMemo(() => {
    return view.zones.map((z) => {
      // ShapeGeometry は xy 平面。x 軸まわりに 90° 回すと (x, y) が世界の (x, z) になる
      const shape = new THREE.Shape(z.zone.polygon.map(([x, y]) => new THREE.Vector2(...worldOf(b, x, y))));
      const fill = new THREE.ShapeGeometry(shape).rotateX(Math.PI / 2);
      const pts: number[] = [];
      z.zone.polygon.forEach(([x, y], i) => {
        const [nx, ny] = z.zone.polygon[(i + 1) % z.zone.polygon.length]!;
        const [ax, az] = worldOf(b, x, y);
        const [bx, bz] = worldOf(b, nx, ny);
        pts.push(ax, f.y + 0.025, az, bx, f.y + 0.025, bz);
      });
      const line = new THREE.BufferGeometry();
      line.setAttribute("position", new THREE.Float32BufferAttribute(pts, 3));
      return { z, fill, line };
    });
  }, [view, b, f.y]);
  useEffect(
    () => () =>
      zoneShapes.forEach((s) => {
        s.fill.dispose();
        s.line.dispose();
      }),
    [zoneShapes],
  );
  const rooms = useMemo(() => {
    const pts: number[] = [];
    for (const fx of view.layout.fixtures) {
      if (fx.kind === "core") continue;
      fx.polygon.forEach(([x, y], i) => {
        const [nx, ny] = fx.polygon[(i + 1) % fx.polygon.length]!;
        const [ax, az] = worldOf(b, x, y);
        const [bx, bz] = worldOf(b, nx, ny);
        pts.push(ax, f.y + 0.02, az, bx, f.y + 0.02, bz);
        pts.push(ax, f.y + 0.6, az, bx, f.y + 0.6, bz);
      });
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.Float32BufferAttribute(pts, 3));
    return g;
  }, [view, b, f.y]);
  useEffect(() => () => rooms.dispose(), [rooms]);

  const refs = {
    desk: useRef<THREE.InstancedMesh>(null),
    person: useRef<THREE.InstancedMesh>(null),
    ring: useRef<THREE.InstancedMesh>(null),
    square: useRef<THREE.InstancedMesh>(null),
    mark: useRef<THREE.InstancedMesh>(null),
  };
  const counts = useMemo(() => {
    let people = 0, vacant = 0, unavailable = 0, today = 0;
    for (const s of view.seats) {
      if (s.state === "person") people += s.seat.seat_kind === "station" ? Math.max(1, Math.min(s.seat.capacity, s.people.length)) : 1;
      else if (s.state === "vacant") vacant++;
      else unavailable++;
      if (s.today) today++;
    }
    return { desks: view.seats.length, people, vacant, unavailable, today };
  }, [view]);
  const index = useRef<DetailIndex>({ desks: [] });

  useLayoutEffect(() => {
    const m = new THREE.Matrix4();
    const q = new THREE.Quaternion();
    const up = new THREE.Vector3(0, 1, 0);
    const pos = new THREE.Vector3();
    const scl = new THREE.Vector3();
    const color = new THREE.Color();
    const n = { desk: 0, person: 0, ring: 0, square: 0, mark: 0 };
    index.current.desks = [];
    for (const s of view.seats) {
      const [x, z] = worldOf(b, s.seat.x_mm, s.seat.y_mm);
      const { w, d } = deskSize(s.seat.desk_type);
      const station = s.seat.seat_kind === "station";
      q.setFromAxisAngle(up, (-s.seat.rotation_deg / 180) * Math.PI);
      m.compose(pos.set(x, f.y, z), q, scl.set(w / 1000 - 0.06, station ? 0.9 : 0.72, d / 1000));
      refs.desk.current?.setMatrixAt(n.desk++, m);
      index.current.desks.push(s);
      const [cx, cyMm] = chairPosition(s.seat, 750);
      const [px, pz] = worldOf(b, cx, cyMm);
      if (s.state === "person") {
        const list = station ? s.people.slice(0, s.seat.capacity) : s.people.slice(0, 1);
        const shown = list.length > 0 ? list : [null];
        shown.forEach((_, i) => {
          // 持ち場の人は持ち場の手前に横に並べる
          const ox = station ? (i - (shown.length - 1) / 2) * 0.55 : 0;
          const oz = station ? 0.9 : 0;
          m.compose(pos.set(px + ox, f.y, pz + oz), q.identity(), scl.set(1, 1, 1));
          refs.person.current?.setMatrixAt(n.person, m);
          refs.person.current?.setColorAt(n.person, color.set(slotColor(s.slot)));
          n.person++;
        });
        if (s.today) {
          m.compose(pos.set(px, f.y + 1.42, pz), q.identity(), scl.set(1, 1.3, 1));
          refs.mark.current?.setMatrixAt(n.mark++, m);
        }
      } else if (s.state === "vacant") {
        m.compose(pos.set(px, f.y + 0.03, pz), q.identity(), scl.set(1, 1, 1));
        refs.ring.current?.setMatrixAt(n.ring++, m);
      } else {
        m.compose(pos.set(px, f.y + 0.03, pz), q.identity(), scl.set(1, 1, 1));
        refs.square.current?.setMatrixAt(n.square++, m);
      }
    }
    for (const [k, r] of Object.entries(refs)) {
      const mesh = r.current;
      if (!mesh) continue;
      mesh.count = n[k as keyof typeof n];
      mesh.instanceMatrix.needsUpdate = true;
      if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
      mesh.computeBoundingSphere();
    }
    invalidate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view, b, f.y, palette, counts, invalidate]);

  const cap = (k: number) => Math.max(1, k);
  return (
    <group>
      {zoneShapes.map(({ z, fill, line }) => {
        const color = z.slot === undefined ? palette.wire : slotColor(z.slot);
        const muted = z.zone.kind === "common" || z.zone.kind === "storage" || z.zone.kind === "restricted";
        return (
          <group key={z.zone.zone_key}>
            <mesh geometry={fill} position={[0, f.y + 0.012, 0]} raycast={() => null} renderOrder={1}>
              <meshBasicMaterial color={color} transparent opacity={z.slot === undefined ? (muted ? 0.07 : 0.04) : 0.13} depthWrite={false} side={THREE.DoubleSide} />
            </mesh>
            <ZoneOutline geometry={line} color={color} dashed={z.zone.kind === "free_address"} />
          </group>
        );
      })}
      <lineSegments geometry={rooms} raycast={() => null}>
        <lineBasicMaterial color={palette.wire} transparent opacity={0.8} />
      </lineSegments>
      <instancedMesh
        key={`d${counts.desks}`}
        ref={refs.desk}
        args={[geoms.desk, undefined, cap(counts.desks)]}
        onPointerMove={(e) => {
          e.stopPropagation();
          const s = e.instanceId !== undefined ? index.current.desks[e.instanceId] : undefined;
          onHover(s ?? null, e);
        }}
        onPointerOut={() => onHover(null)}
        onClick={(e) => {
          e.stopPropagation();
          const s = e.instanceId !== undefined ? index.current.desks[e.instanceId] : undefined;
          if (s) onClickSeat(s);
        }}
      >
        <meshStandardMaterial color={palette.shell} roughness={0.42} metalness={0.16} />
      </instancedMesh>
      <instancedMesh key={`p${counts.people}`} ref={refs.person} args={[geoms.person, undefined, cap(counts.people)]} raycast={() => null}>
        <meshStandardMaterial roughness={0.55} metalness={0.05} />
      </instancedMesh>
      <instancedMesh key={`r${counts.vacant}`} ref={refs.ring} args={[geoms.ring, undefined, cap(counts.vacant)]} raycast={() => null}>
        <meshBasicMaterial color={INK[theme]} side={THREE.DoubleSide} />
      </instancedMesh>
      <instancedMesh key={`u${counts.unavailable}`} ref={refs.square} args={[geoms.square, undefined, cap(counts.unavailable)]} raycast={() => null}>
        <meshBasicMaterial map={hatch} side={THREE.DoubleSide} />
      </instancedMesh>
      <instancedMesh key={`m${counts.today}`} ref={refs.mark} args={[geoms.mark, undefined, cap(counts.today)]} raycast={() => null}>
        <meshStandardMaterial color={INK[theme]} roughness={0.4} metalness={0.2} />
      </instancedMesh>
    </group>
  );
}

function ZoneOutline({ geometry, color, dashed }: { geometry: THREE.BufferGeometry; color: string; dashed: boolean }) {
  const ref = useRef<THREE.LineSegments>(null);
  useLayoutEffect(() => {
    if (dashed) ref.current?.computeLineDistances();
  });
  return (
    <lineSegments ref={ref} geometry={geometry} raycast={() => null}>
      {dashed ? <lineDashedMaterial color={color} dashSize={0.6} gapSize={0.4} transparent opacity={0.8} /> : <lineBasicMaterial color={color} transparent opacity={0.65} />}
    </lineSegments>
  );
}

/** 選んだ人の印: 控えめなアクセント色の細い輪と、細い縦の引き出し線（札は HTML）。 */
function PersonMarker({ at, color }: { at: [number, number, number]; color: string }) {
  const line = useMemo(() => {
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", new THREE.Float32BufferAttribute([at[0], at[1] + 1.25, at[2], at[0], at[1] + 3.2, at[2]], 3));
    return g;
  }, [at]);
  useEffect(() => () => line.dispose(), [line]);
  return (
    <group>
      <mesh position={[at[0], at[1] + 0.04, at[2]]} rotation={[-Math.PI / 2, 0, 0]} raycast={() => null}>
        <ringGeometry args={[0.5, 0.6, 48]} />
        <meshBasicMaterial color={color} side={THREE.DoubleSide} />
      </mesh>
      <lineSegments geometry={line} raycast={() => null}>
        <lineBasicMaterial color={color} />
      </lineSegments>
    </group>
  );
}

// ---- 環境・カメラ ----

function Atmosphere({ palette, controls }: { palette: Palette; controls: OrbitControls | null }) {
  const { gl, scene, camera } = useThree();
  useEffect(() => setEnvironment(gl, scene), [gl, scene]);
  useEffect(() => applyFog(scene, palette), [scene, palette]);
  useFrame(() => {
    if (controls) fitFog(scene, camera.position.distanceTo(controls.target));
  });
  return null;
}

// three のシーンの変更（フックの外の関数で行う）
function applyFog(scene: THREE.Scene, palette: Palette) {
  scene.environmentIntensity = palette.light.env;
  scene.fog = new THREE.Fog(palette.bg, 60, 500);
  return () => {
    scene.fog = null;
  };
}

function fitFog(scene: THREE.Scene, distance: number) {
  const fog = scene.fog;
  if (!(fog instanceof THREE.Fog)) return;
  fog.near = distance * 1.2;
  fog.far = distance * 3.6;
}

/** 最初の向き（注視点からカメラへ）。手前・やや右の斜め上から見下ろす（分解アクソメの見え方）。 */
const VIEW_DIR = new THREE.Vector3(0.42, 0.62, 1).normalize();
const VIEW_DIR_PORTRAIT = new THREE.Vector3(0.3, 0.75, 1).normalize();

/** 点の集まりが画面に収まるカメラの位置（visible は画面の上から何割に収めるか）。 */
function fitPoints(camera: THREE.PerspectiveCamera, points: THREE.Vector3[], dir: THREE.Vector3, visible = 1, margin = 1.12) {
  const center = new THREE.Vector3();
  points.forEach((p) => center.add(p));
  center.multiplyScalar(1 / Math.max(1, points.length));
  const back = dir.clone().normalize();
  const right = new THREE.Vector3(0, 1, 0).cross(back).normalize();
  const up = back.clone().cross(right).normalize();
  const tanFull = Math.tan(THREE.MathUtils.degToRad(camera.fov) / 2);
  const tanH = tanFull * camera.aspect;
  const tanV = tanFull * Math.max(0.2, visible);
  let minR = Infinity, maxR = -Infinity, minU = Infinity, maxU = -Infinity;
  for (const p of points) {
    const d = p.clone().sub(center);
    minR = Math.min(minR, d.dot(right));
    maxR = Math.max(maxR, d.dot(right));
    minU = Math.min(minU, d.dot(up));
    maxU = Math.max(maxU, d.dot(up));
  }
  const target = center.clone().add(right.clone().multiplyScalar((minR + maxR) / 2)).add(up.clone().multiplyScalar((minU + maxU) / 2));
  let distance = 6;
  for (const p of points) {
    const d = p.clone().sub(target);
    const toward = d.dot(back);
    distance = Math.max(distance, Math.abs(d.dot(right)) / tanH + toward, Math.abs(d.dot(up)) / tanV + toward);
  }
  distance *= margin;
  const aim = target.clone().sub(up.clone().multiplyScalar((1 - visible) * distance * tanFull));
  return { target: aim, position: aim.clone().add(back.multiplyScalar(distance)) };
}

function boxCorners(x0: number, x1: number, y0: number, y1: number, z0: number, z1: number): THREE.Vector3[] {
  const out: THREE.Vector3[] = [];
  for (const x of [x0, x1]) for (const y of [y0, y1]) for (const z of [z0, z1]) out.push(new THREE.Vector3(x, y, z));
  return out;
}

function CameraRig({ points, focusKey, fitRequest, controls, reduced, sheet }: {
  points: THREE.Vector3[];
  focusKey: string;
  fitRequest: number;
  controls: OrbitControls | null;
  reduced: boolean;
  sheet: boolean;
}) {
  const { camera, invalidate, size } = useThree();
  const goal = useRef<{ target: THREE.Vector3; position: THREE.Vector3 } | null>(null);
  const first = useRef(true);
  useEffect(() => {
    if (!controls) return;
    const cancel = () => {
      goal.current = null;
    };
    controls.addEventListener("start", cancel);
    return () => controls.removeEventListener("start", cancel);
  }, [controls]);
  useEffect(() => {
    if (!controls || size.width === 0 || points.length === 0) return;
    const aspect = size.width / Math.max(1, size.height);
    const dir = first.current ? (aspect < 0.85 ? VIEW_DIR_PORTRAIT : VIEW_DIR) : camera.position.clone().sub(controls.target).normalize();
    // 見下ろす角度は 25° 以上・65° 以下に保つ
    dir.y = Math.min(0.9, Math.max(0.42, dir.y));
    dir.normalize();
    const fit = fitPoints(camera as THREE.PerspectiveCamera, points, dir, sheet ? 0.45 : 1);
    if (first.current || reduced) {
      controls.target.copy(fit.target);
      camera.position.copy(fit.position);
      controls.update();
      goal.current = null;
    } else goal.current = fit;
    first.current = false;
    invalidate();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [focusKey, fitRequest, controls, size.width > 0, sheet]);
  useFrame((_, delta) => {
    if (!goal.current || !controls) return;
    const k = 1 - Math.exp(-Math.min(delta, 1 / 20) * 4.5);
    controls.target.lerp(goal.current.target, k);
    camera.position.lerp(goal.current.position, k);
    controls.update();
    if (camera.position.distanceTo(goal.current.position) < 0.05) goal.current = null;
    invalidate();
  });
  return null;
}

// ---- 札（HTML の重ね） ----

interface LabelSpec {
  id: string;
  kind: "floor" | "zone" | "building" | "person";
  text: string;
  detail?: string;
  emphasis?: boolean;
  dim?: boolean;
  priority: number;
  anchor: [number, number, number];
}

function LabelProjector({ specs, overlay }: { specs: LabelSpec[]; overlay: RefObject<HTMLDivElement | null> }) {
  const { camera, size, invalidate } = useThree();
  const sorted = useMemo(() => [...specs].sort((a, b) => b.priority - a.priority), [specs]);
  const latest = useRef(sorted);
  const last = useRef<{ m: THREE.Matrix4; w: number; h: number; specs: LabelSpec[] | null }>({ m: new THREE.Matrix4(), w: 0, h: 0, specs: null });
  const v = useMemo(() => new THREE.Vector3(), []);
  useEffect(() => {
    latest.current = sorted;
    invalidate();
  }, [sorted, invalidate]);
  useFrame(() => {
    const root = overlay.current;
    if (!root) return;
    camera.updateMatrixWorld();
    const prev = last.current;
    if (prev.specs === latest.current && prev.w === size.width && prev.h === size.height && prev.m.equals(camera.matrixWorld)) return;
    last.current = { m: prev.m.copy(camera.matrixWorld), w: size.width, h: size.height, specs: latest.current };
    const els = new Map<string, HTMLElement>();
    root.querySelectorAll<HTMLElement>("[data-label-id]").forEach((el) => els.set(el.dataset.labelId!, el));
    const placed: Array<[number, number, number, number]> = [];
    for (const spec of latest.current) {
      const el = els.get(spec.id);
      if (!el) continue;
      const w = el.offsetWidth;
      const h = el.offsetHeight;
      v.set(...spec.anchor).project(camera);
      const sx = ((v.x + 1) / 2) * size.width;
      const sy = ((1 - v.y) / 2) * size.height;
      // 階の札は外形の左手前の角の左に、エリア・人の札は角・頭の上に置く
      const left = spec.kind === "floor" ? sx - w - 8 : spec.kind === "person" ? sx - 6 : sx;
      const top = spec.kind === "floor" ? sy - h / 2 : sy - h - 2;
      const inView = v.z < 1 && v.z > -1 && sx >= -40 && sx <= size.width + 40 && sy >= 0 && sy <= size.height;
      const l = Math.min(Math.max(left, 2), size.width - w - 2);
      const ok = inView && top >= 2 && top + h <= size.height - 2 && !placed.some(([a, b2, c, d]) => l < c + 3 && l + w + 3 > a && top < d + 3 && top + h + 3 > b2);
      if (ok) {
        placed.push([l, top, l + w, top + h]);
        el.style.transform = `translate(${Math.round(l)}px, ${Math.round(top)}px)`;
        el.style.visibility = "visible";
      } else el.style.visibility = "hidden";
    }
  });
  return null;
}

function LabelOverlay({ specs, overlay }: { specs: LabelSpec[]; overlay: RefObject<HTMLDivElement | null> }) {
  return (
    <div ref={overlay} className="pointer-events-none absolute inset-0 overflow-hidden" aria-hidden="true">
      {specs.map((s) => (
        <div
          key={s.id}
          data-label-id={s.id}
          style={{ visibility: "hidden" }}
          className={`label3d rounded-sm border bg-surface/95 px-1.5 py-px leading-4 shadow-sm ${
            s.kind === "person"
              ? "border-border border-l-4 border-l-accent text-[12px] font-bold"
              : s.kind === "floor"
                ? `text-[12px] ${s.emphasis ? "border-fg border-l-4 border-l-accent font-bold" : "border-border"} ${s.dim ? "opacity-70" : ""}`
                : s.kind === "building"
                  ? "border-border text-[13px] font-bold"
                  : "border-border text-[11px]"
          } text-fg`}
        >
          {s.text}
          {s.detail && <span className="tabular ml-1.5 font-normal text-muted">{s.detail}</span>}
        </div>
      ))}
    </div>
  );
}

// ---- 全体 ----

function SceneContent(props: FloorScene3DProps & { overlay: RefObject<HTMLDivElement | null>; labels: LabelSpec[]; onHoverSeat: (s: SeatView | null, e?: ThreeEvent<PointerEvent>) => void; onHoverSlab: (on: boolean) => void }) {
  const { palette, theme, buildings, layouts, detail, selectedFloorId, selectedPersonId, lowEnd, fitRequest, sheet } = props;
  const reduced = useReducedMotion() ?? false;
  const controls = useOrbitControls(reduced);
  const selected = floorOf(buildings, selectedFloorId);
  const personSeat = detail && selectedPersonId ? detail.seats.find((s) => s.people.some((p) => p.personId === selectedPersonId)) : undefined;
  const personAt = useMemo<[number, number, number] | null>(() => {
    if (!selected || !personSeat) return null;
    const [cx, cy] = chairPosition(personSeat.seat, 750);
    const [x, z] = worldOf(selected.b, cx, cy);
    return [x, selected.f.y, z];
  }, [selected, personSeat]);

  // カメラで収める点: 人 → その席の周り、階 → その階と上下の階、建物 → 建物全体、拠点 → すべての建物
  const { points, focusKey } = useMemo(() => {
    if (personAt) {
      return { points: boxCorners(personAt[0] - 9, personAt[0] + 9, personAt[1], personAt[1] + 3, personAt[2] - 7, personAt[2] + 7), focusKey: `p:${selectedPersonId}:${selectedFloorId}` };
    }
    if (selected) {
      // 選んだ階に寄る（上の階の下面まで少し含めて、階の高さの見当が付くように）
      const above = selected.b.stack[selected.f.index + 1];
      const top = above ? selected.f.y + (above.y - selected.f.y) * 0.45 : selected.f.y + 3;
      return {
        points: boxCorners(selected.b.x, selected.b.x + selected.b.width, selected.f.y - 0.5, top, selected.b.z, selected.b.z + selected.b.depth),
        focusKey: `f:${selectedFloorId}`,
      };
    }
    const pts = buildings.flatMap((b) => boxCorners(b.x, b.x + b.width, (b.stack[0]?.y ?? 0) - 0.3, (b.stack.at(-1)?.y ?? 0) + 1.5, b.z, b.z + b.depth));
    return { points: pts, focusKey: `b:${buildings.map((b) => b.building.id).join(",")}` };
  }, [personAt, selected, buildings, selectedPersonId, selectedFloorId]);

  const extent = useMemo(() => {
    const xs = buildings.flatMap((b) => [b.x, b.x + b.width]);
    const zs = buildings.flatMap((b) => [b.z, b.z + b.depth]);
    const minY = Math.min(...buildings.map((b) => b.stack[0]?.y ?? 0));
    return { cx: (Math.min(...xs) + Math.max(...xs)) / 2, cz: (Math.min(...zs) + Math.max(...zs)) / 2, size: Math.max(Math.max(...xs) - Math.min(...xs), Math.max(...zs) - Math.min(...zs)), minY };
  }, [buildings]);

  const accent = ACCENT[theme];
  return (
    <>
      <color attach="background" args={[palette.bg]} />
      <Atmosphere palette={palette} controls={controls} />
      <hemisphereLight args={["#ffffff", palette.grid, palette.light.ambient]} />
      <directionalLight position={[40, 80, 30]} intensity={palette.light.key} />
      <directionalLight position={[-60, 30, -40]} intensity={palette.light.key * 0.25} />
      <GridFloor center={[extent.cx, extent.cz]} y={extent.minY - 2.5} size={extent.size * 3 + 80} cell={Math.max(2, Math.round(extent.size / 20))} palette={palette} />
      {buildings.map((b) => (
        <group key={b.building.id}>
          {b.stack.map((f) => (
            <Slab
              key={f.id}
              b={b}
              f={f}
              palette={palette}
              theme={theme}
              selected={f.id === selectedFloorId}
              dim={selectedFloorId !== null && f.id !== selectedFloorId}
              above={selected !== null && selected.b === b && f.index > selected.f.index}
              onHover={props.onHoverSlab}
              onClick={() => (props.buildings.length > 1 && !selected ? props.onSelectBuilding(b.building.id) : f.id !== selectedFloorId && props.onSelectFloor(f.id))}
            />
          ))}
          <CornerLines b={b} palette={palette} />
        </group>
      ))}
      <Cores buildings={buildings} layouts={layouts} palette={palette} theme={theme} selectedFloorId={selectedFloorId} />
      {buildings.length > 1 && !selected && buildings.map((b) => <BuildingShell key={b.building.id} b={b} palette={palette} />)}
      {!lowEnd && <OtherFloorDots buildings={buildings} layouts={layouts} theme={theme} selectedFloorId={selectedFloorId} />}
      {selected && detail && (
        <FloorDetail
          b={selected.b}
          f={selected.f}
          view={detail}
          palette={palette}
          theme={theme}
          onHover={props.onHoverSeat}
          onClickSeat={(s) => {
            const p = s.people[0];
            if (p) props.onSelectPerson(p.personId);
          }}
        />
      )}
      {personAt && <PersonMarker at={personAt} color={accent} />}
      <LabelProjector specs={props.labels} overlay={props.overlay} />
      <CameraRig points={points} focusKey={focusKey} fitRequest={fitRequest} controls={controls} reduced={reduced} sheet={sheet} />
    </>
  );
}

function buildLabels(props: FloorScene3DProps): LabelSpec[] {
  const { buildings, detail, selectedFloorId, selectedPersonId, floorLabels, buildingLabels } = props;
  const specs: LabelSpec[] = [];
  const selected = floorOf(buildings, selectedFloorId);
  for (const b of buildings) {
    const overview = buildings.length > 1 && !selected;
    if (overview) {
      const l = buildingLabels.get(b.building.id);
      const top = b.stack.at(-1)?.y ?? 0;
      specs.push({ id: `b:${b.building.id}`, kind: "building", text: l?.text ?? b.building.name, detail: l?.detail, priority: 9000, anchor: [b.x + b.width / 2, top + 1.6, b.z + b.depth / 2] });
      continue;
    }
    for (const f of b.stack) {
      const l = floorLabels.get(f.id);
      if (!l) continue;
      const emphasis = f.id === selectedFloorId;
      // 詰めた階の札は数字を省き、優先度を下げる（重なれば出さない）
      specs.push({
        id: `f:${f.id}`,
        kind: "floor",
        text: l.text,
        detail: f.compressed ? undefined : l.detail,
        emphasis,
        dim: selectedFloorId !== null && !emphasis,
        priority: emphasis ? 9500 : f.compressed ? 3000 - f.index : 7000 - f.index,
        anchor: [b.x, f.y, b.z + b.depth],
      });
    }
  }
  if (selected && detail) {
    for (const z of detail.zones) {
      if (z.seats === 0 && z.zone.kind !== "common" && z.zone.kind !== "storage") continue;
      const [x, zz] = worldOf(selected.b, z.anchor[0] + 300, z.anchor[1] - 300);
      const free = z.zone.kind === "free_address";
      const detailText = z.seats === 0 ? undefined : free ? `${z.seats}席` : `${z.used}/${z.seats}`;
      specs.push({ id: `z:${z.zone.zone_key}`, kind: "zone", text: free && z.label !== z.zone.name ? `${z.label}（フリーアドレス）` : z.label, detail: detailText, priority: 5000 + z.seats, anchor: [x, selected.f.y + 0.4, zz] });
    }
    const seat = selectedPersonId ? detail.seats.find((s) => s.people.some((p) => p.personId === selectedPersonId)) : undefined;
    if (seat) {
      const p = seat.people.find((x) => x.personId === selectedPersonId)!;
      const [cx, cy] = chairPosition(seat.seat, 750);
      const [x, z] = worldOf(selected.b, cx, cy);
      specs.push({ id: `p:${p.personId}`, kind: "person", text: p.name ?? "（氏名非公開）", detail: seat.seat.code, priority: 10000, anchor: [x, selected.f.y + 3.2, z] });
    }
  }
  return specs;
}

export function FloorScene3D(props: FloorScene3DProps) {
  const overlay = useRef<HTMLDivElement>(null);
  const container = useRef<HTMLDivElement>(null);
  const [hover, setHover] = useState<{ s: SeatView; x: number; y: number; w: number } | null>(null);
  const [slabHover, setSlabHover] = useState(false);
  const labels = useMemo(() => buildLabels(props), [props]);
  return (
    <div ref={container} className="relative size-full" style={{ cursor: hover || slabHover ? "pointer" : "grab" }}>
      <Canvas
        frameloop="demand"
        dpr={[1, 2]}
        camera={{ fov: 36, near: 0.3, far: 4000, position: [40, 60, 120] }}
        gl={{ antialias: true, toneMapping: THREE.NeutralToneMapping }}
        aria-describedby="floor3d-help"
      >
        <SceneContent
          {...props}
          overlay={overlay}
          labels={labels}
          onHoverSlab={(on) => setSlabHover(on && props.selectedFloorId === null ? true : on)}
          onHoverSeat={(s, e) => {
            if (!s || !e) {
              setHover(null);
              return;
            }
            const r = container.current?.getBoundingClientRect();
            setHover({ s, x: e.nativeEvent.clientX - (r?.left ?? 0), y: e.nativeEvent.clientY - (r?.top ?? 0), w: r?.width ?? 9999 });
          }}
        />
      </Canvas>
      <LabelOverlay specs={labels} overlay={overlay} />
      {hover && (
        <div
          className="pointer-events-none absolute z-10 min-w-[9em] rounded-sm border border-t-2 border-border border-t-accent bg-surface px-2 py-1.5 text-xs shadow"
          style={{ left: Math.min(hover.x + 14, hover.w - 190), top: hover.y + 14 }}
          aria-hidden="true"
        >
          <SeatTooltip s={hover.s} />
        </div>
      )}
    </div>
  );
}
