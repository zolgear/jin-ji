// 3D 表示のカメラの合わせ方（three の計算だけ。描画に依らないのでテストできる）。
import * as THREE from "three";
import type { OrgLink } from "../../data/types";
import { boundingSphere, heightOf, type Placed3D, type Scene3D } from "./layout3d";

/**
 * カメラを点の集まりに合わせる位置。球で囲むと平たい木では余白が大きすぎるため、
 * 視線の向き dir から見た各点の横・縦のずれを画角と比べ、すべてが収まる最小の距離を求める。
 * モジュールは外接円の縁の 4 点と上下で近似する。margin は画面の端に残す割合。
 */
export function fitView(
  camera: THREE.PerspectiveCamera,
  points: Placed3D[],
  dir: THREE.Vector3,
  /** 画面の上から何割までに収めるか（スマホで下に詳細のシートが重なるとき 1 未満）。 */
  visible = 1,
  margin = 1.1,
  /** 画面の上の何割を空けるか（図の上に重なる要素で図の上端を隠さないため。今は使っていない）。 */
  topInset = 0,
  /** これより近づかない（収める点が少ないときに寄りすぎないように）。 */
  minDistance = 0,
): { target: THREE.Vector3; position: THREE.Vector3; distance: number } {
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
  distance = Math.max(distance * margin, minDistance);
  // 見えている範囲（上に重なる要素と下のシートの間）の中心が target に来るように、注視点を上下にずらす
  const aim = target.clone().sub(up.clone().multiplyScalar((1 - topInset - visible) * distance * tanFull));
  return { target: aim, position: aim.clone().add(back.multiplyScalar(distance)), distance };
}

/**
 * 組織を選んだときに、これより近づかない距離。配下の無い組織（チーム）は収める点が自分と上位だけになり、
 * そのまま合わせるとモジュールが画面いっぱいになるため、周りの組織が見える距離を保つ。全体の大きさに応じて少し変える。
 */
export function minFocusDistance(scene: Scene3D): number {
  return Math.min(64, Math.max(34, scene.extent * 0.3));
}

/** 上位の組織まで収めるのは、収めても距離がこの倍率までのとき（それ以上離れるなら、画面の外でよい）。 */
const PARENT_REACH = 1.8;
/** 兼務・出向の相手先まで収めるのは、収めても距離がこの倍率までのとき（上位より厳しく。相手先は図のどこにでもあり得る）。 */
const LINK_REACH = 1.35;

/**
 * 組織を選んだときのカメラ。「その組織と配下すべて」を収め、minFocusDistance より近づかない。
 * 上位 1 段と、兼務・出向の相手先・拠点をまたぐ上位や配下（別の島）は、収めても遠ざかりすぎないときだけ含める:
 * - 相手先が図の反対側にあると、その組織だけ急に全体が見えるほど引いてしまい、選ぶたびに倍率が大きく変わる
 * - 平面の配置で組織が多いと、上位の組織は内側の輪の遠くにあり、収めるとほとんど寄らない
 * まず上位を含めるかを決め、その上で相手先を含めるかを決める。
 */
export function fitFocus(
  camera: THREE.PerspectiveCamera,
  scene: Scene3D,
  byId: ReadonlyMap<string, Placed3D>,
  focus: { id: string; links: readonly OrgLink[]; upperPairs: ReadonlyArray<readonly [string, string]> },
  dir: THREE.Vector3,
  visible = 1,
): { target: THREE.Vector3; position: THREE.Vector3; distance: number } {
  const self = byId.get(focus.id)!;
  const points = [self];
  // 配下はすべて含める（強調している線が画面の外へ伸びないように）
  const walk = (n: Placed3D["node"]) =>
    n.children.forEach((c) => {
      const pc = byId.get(c.id);
      if (pc) points.push(pc);
      walk(c);
    });
  walk(self.node);
  const min = minFocusDistance(scene);
  const fit = (extra: Placed3D[]) => fitView(camera, [...points, ...extra], dir, visible, 1.1, 0, min);
  const base = fit([]);
  const parent = self.node.parentId ? byId.get(self.node.parentId) : undefined;
  const others: Placed3D[] = [];
  focus.links.forEach((l) => {
    const other = byId.get(l.from === focus.id ? l.to : l.from);
    if (other) others.push(other);
  });
  focus.upperPairs.forEach((pair) => {
    for (const id of pair) {
      const other = byId.get(id);
      if (other) others.push(other);
    }
  });
  let chosen = base;
  let extra: Placed3D[] = [];
  if (parent) {
    const withParent = fit([parent]);
    if (withParent.distance <= base.distance * PARENT_REACH) {
      chosen = withParent;
      extra = [parent];
    }
  }
  if (others.length > 0) {
    const wide = fit([...extra, ...others]);
    if (wide.distance <= chosen.distance * LINK_REACH) chosen = wide;
  }
  return chosen;
}
