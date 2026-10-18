// デモの建物・階・配置図・席の割り当てを、デモの人員（350 人・1 万人）から決定的に作る。
// 完全な架空データで、乱数は固定シード。仕様は docs/design/building-floor-mode.md（§2 のデータモデルを簡略に真似る）。
//
// - 建物を持つのは 3 拠点（東京本社の本館、横浜工場の工場棟と事務棟、千葉物流センター）。どちらのデモにも同じ名前の拠点がある
// - 人の席は「部」（組織の段の department、無ければ最上位の組織）ごとのエリアにまとめる。所属の期間（主務）ごとに席を割り当て、
//   期間が重ならない人どうしで同じ席を順に使う（定員 1 の席は同時に 1 人。タイムマシンで過去の席の配置も見られる）
// - 働き方: 工場の製造の部はラインの持ち場（定員 3）、物流センターの物流の部は入出荷の持ち場（定員 4）、ほかは（営業・総務も）固定席
// - 配置図の改訂は 1 つ（2000-01-01 から）。部が無くなったエリアは、その後は割り当ての無い席のまま残る
// - 扱うのは人の決まった席・持ち場の割り当て（有効期間付き）だけ。席の取り合いに当たるものは扱わない（連携先の範疇）
// - 今日の所在は、外部のシステムから届く想定のサンプル（固定シード、デモの「今日」だけ）。席にいる人の一部と、空いている席に座る少数
import { addDays, inPeriod } from "../lib/date";
import type { DemoDataset, DemoOrgUnit, DemoPerson } from "./model";
import type { FixtureKind, FloorKind, PointMm, SeatKind, ZoneKind } from "../data/workplace";

// ---- 型（API の応答ではなく、時点で引くための元データ） ----

export interface DemoBuilding {
  id: string;
  site_id: string;
  code: string;
  name: string;
  sort_order: number;
}

export interface DemoFloor {
  id: string;
  building_id: string;
  label: string;
  level_order: number;
  kind: FloorKind;
}

export interface DemoZone {
  zone_key: string;
  name: string;
  kind: ZoneKind;
  polygon: PointMm[];
}

export interface DemoSeat {
  seat_id: string;
  code: string;
  zone_key: string | null;
  x_mm: number;
  y_mm: number;
  rotation_deg: number;
  desk_type: string;
  seat_kind: SeatKind;
  capacity: number;
  features: string[];
  out_of_service: boolean;
}

export interface DemoLayout {
  layout_id: string;
  floor_id: string;
  valid_from: string;
  outline: PointMm[];
  fixtures: Array<{ kind: FixtureKind; name: string | null; polygon: PointMm[] }>;
  grid_mm: number;
  zones: DemoZone[];
  seats: DemoSeat[];
}

/** 人 → 席 またはゾーン（有効期間付き）。 */
export interface DemoSeatAssignment {
  person_id: string;
  floor_id: string;
  seat_id: string | null;
  zone_key: string | null;
  role: "primary" | "secondary";
  /** 定員 1 の席か。 */
  exclusive: boolean;
  valid_from: string;
  valid_until: string | null;
}

/** 外部から届いた今日の所在（1 人 1 件。時刻・履歴を持たない）。 */
export interface DemoPresence {
  person_id: string;
  floor_id: string;
  seat_id: string | null;
  zone_key: string | null;
}

export interface DemoWorkplace {
  buildings: DemoBuilding[];
  floors: DemoFloor[];
  /** 階の ID → 配置図（改訂は 1 つ）。 */
  layouts: Map<string, DemoLayout>;
  assignments: DemoSeatAssignment[];
  /** 今日の所在のサンプル（業務日付の分だけ）。 */
  presence: DemoPresence[];
}

// ---- 乱数・小さな道具 ----

function mulberry32(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const rect = (x0: number, y0: number, x1: number, y1: number): PointMm[] => [
  [x0, y0],
  [x1, y0],
  [x1, y1],
  [x0, y1],
];

/** エリアの記号（A, B, … Z, AA, AB …）。 */
function zoneLetter(i: number): string {
  return i < 26 ? String.fromCharCode(65 + i) : zoneLetter(Math.floor(i / 26) - 1) + String.fromCharCode(65 + (i % 26));
}

const pad2 = (n: number) => String(n).padStart(2, "0");

/** 階の表示名と並び順（地上 n 階 = n × 10、地下 = 負）。 */
const floorLabel = (level: number) => (level < 0 ? `B${-level}` : `${level}F`);

// ---- 拠点ごとの建物の構成 ----

type BuildingPlan = "tower" | "factory" | "annex" | "logistics";

interface SitePlan {
  site: string;
  buildings: Array<{ code: string; name: string; plan: BuildingPlan }>;
}

/** 建物を持つ拠点（どちらのデモにも同じ名前の拠点がある）。 */
export const WORKPLACE_SITES: readonly SitePlan[] = [
  { site: "東京本社", buildings: [{ code: "MAIN", name: "本館", plan: "tower" }] },
  {
    site: "横浜工場",
    buildings: [
      { code: "PLANT", name: "工場棟", plan: "factory" },
      { code: "OFFICE", name: "事務棟", plan: "annex" },
    ],
  },
  { site: "千葉物流センター", buildings: [{ code: "DC", name: "物流センター", plan: "logistics" }] },
];

/** 部の働き方。 */
type WorkStyle = "fixed" | "line" | "dock";

/** 部ごとの席の需要。 */
interface DeptDemand {
  dept: DemoOrgUnit;
  style: WorkStyle;
  /** 所属の期間（主務）。席の割り当ての単位。 */
  intervals: Array<{ person: DemoPerson; from: string; until: string | null; slot: number }>;
  /** 同時に席を使う人数の最大（= 割り当てた枠の数）。 */
  peak: number;
}

/** 生成した席の置き場所（部ごと）。 */
interface PlacedZone {
  floorId: string;
  zone: DemoZone;
  seats: DemoSeat[];
}

// ---- 席の幾何（mm） ----

const DESK_W = 1200;
const PAIR_H = 3600; // 向かい合わせの 2 列（椅子・通路を含む）
const ZONE_HEAD = 1400; // エリアの上の札の帯
const ZONE_GAP = 600;
const MARGIN = 2000;
const CORE_W = 6000;
const ROOMS_H = 5000;
const STATION_PITCH = 2400;
const LINE_H = 5000;
const LINE_GAP = 1500;
const STATIONS_PER_LINE = 18;
/** 持ち場の定員（交替で同じ持ち場を使う人を重ねて割り当てる。誰がいつ入るかは持たない）。ラインは 3、入出荷は 4。 */
const capacityOf = (style: WorkStyle) => (style === "dock" ? 4 : 3);
/** これより少ない人数の部は、共用のエリアにまとめる。 */
const SMALL_DEPT = 5;

interface OfficeGeom {
  /** 1 列の机の数。 */
  dpr: number;
  /** 階の外形の幅・奥行き。 */
  width: number;
  depth: number;
  /** 列（左・右）の x の始まりと幅。 */
  columns: Array<{ x0: number; w: number; top: number; bottom: number }>;
  core: [number, number, number, number];
  rooms: Array<[number, number, number, number]>;
}

/** 決まった幅に入る机の数（列）。 */
const maxDprFor = (width: number) => Math.floor((Math.floor((width - CORE_W - 4 * MARGIN) / 2) - 2000) / DESK_W);

function officeGeom(dpr: number, depth: number, fixedWidth?: number): OfficeGeom {
  const colW = dpr * DESK_W + 2000;
  const natural = 2 * colW + CORE_W + 4 * MARGIN;
  const width = Math.max(natural, fixedWidth ?? 0);
  const extra = Math.floor((width - natural) / 2);
  const leftX = MARGIN + extra;
  const coreX = leftX + colW + MARGIN;
  const rightX = coreX + CORE_W + MARGIN;
  const top = depth - MARGIN;
  const roomTop = MARGIN + ROOMS_H;
  const roomW = Math.floor(colW / 3);
  const coreHalf = Math.min(4500, Math.floor(depth / 2) - MARGIN - 2000);
  return {
    dpr,
    width,
    depth,
    columns: [
      { x0: leftX, w: colW, top, bottom: MARGIN },
      { x0: rightX, w: colW, top, bottom: roomTop + 1000 },
    ],
    core: [coreX, Math.floor(depth / 2) - coreHalf, coreX + CORE_W, Math.floor(depth / 2) + coreHalf],
    rooms: [0, 1, 2].map((i) => [rightX + i * roomW, MARGIN, rightX + (i + 1) * roomW - 300, roomTop] as [number, number, number, number]),
  };
}

/** 列にまだ入る組（向かい合わせの 2 列）の数。 */
const pairsFitting = (height: number) => Math.max(0, Math.floor((height - ZONE_HEAD - ZONE_GAP) / PAIR_H));

interface ZoneRequest {
  demand: DeptDemand;
  /** 席の数（組の数に切り上げる前）。 */
  seats: number;
}

/** 事務の階の詰め方（列を上から詰め、入らない分は次の列・次の階へ分ける）。返り値は階ごとのエリアの置き場所。 */
function packOffice(requests: ZoneRequest[], geom: OfficeGeom, floorsTarget: number) {
  type Piece = { req: ZoneRequest; column: number; y1: number; pairs: number };
  const perPair = geom.dpr * 2;
  const totalPairs = requests.reduce((s, r) => s + Math.max(1, Math.ceil(r.seats / perPair)), 0);
  const limit = Math.ceil(totalPairs / Math.max(1, floorsTarget));
  const floors: Piece[][] = [[]];
  let column = 0;
  let y = geom.columns[0]!.top;
  let used = 0;
  const newFloor = () => {
    floors.push([]);
    column = 0;
    used = 0;
    y = geom.columns[0]!.top;
  };
  const nextColumn = () => {
    column++;
    if (column >= geom.columns.length) newFloor();
    else y = geom.columns[column]!.top;
  };
  for (const req of requests) {
    let rest = Math.max(1, Math.ceil(req.seats / perPair));
    while (rest > 0) {
      // 階の目安の組の数に届いたら、次の階へ（どの階にもエリアが行き渡るように）
      if (used >= limit && floors.at(-1)!.length > 0) {
        newFloor();
        continue;
      }
      const col = geom.columns[column]!;
      const fit = pairsFitting(y - col.bottom);
      if (fit === 0) {
        nextColumn();
        continue;
      }
      const pairs = Math.min(rest, fit, Math.max(1, limit - used));
      floors.at(-1)!.push({ req, column, y1: y, pairs });
      y -= ZONE_HEAD + pairs * PAIR_H + ZONE_GAP;
      rest -= pairs;
      used += pairs;
    }
  }
  return floors;
}

/** 事務の階のエリアと席（組ごとに向かい合わせの 2 列）。 */
function officeZone(
  floorId: string,
  label: string,
  index: number,
  piece: { req: ZoneRequest; column: number; y1: number; pairs: number },
  geom: OfficeGeom,
): PlacedZone {
  const col = geom.columns[piece.column]!;
  const y0 = piece.y1 - ZONE_HEAD - piece.pairs * PAIR_H;
  const letter = zoneLetter(index);
  const zone: DemoZone = {
    zone_key: `${floorId}-z${letter}`,
    name: `${label} ${letter}エリア`,
    kind: "office",
    polygon: rect(col.x0, y0, col.x0 + col.w, piece.y1),
  };
  const seats: DemoSeat[] = [];
  let n = 0;
  for (let p = 0; p < piece.pairs; p++) {
    const pairTop = piece.y1 - ZONE_HEAD - p * PAIR_H;
    for (const [dy, rot] of [
      [-1150, 180],
      [-1850, 0],
    ] as const) {
      for (let j = 0; j < geom.dpr; j++) {
        n++;
        const code = `${label}-${letter}${pad2(n)}`;
        seats.push({
          seat_id: `${floorId}-${code}`,
          code,
          zone_key: zone.zone_key,
          x_mm: col.x0 + 1000 + j * DESK_W + DESK_W / 2,
          y_mm: pairTop + dy,
          rotation_deg: rot,
          desk_type: "desk_1200",
          seat_kind: "fixed",
          capacity: 1,
          features: j % 4 === 0 ? ["dual_monitor"] : ["monitor"],
          out_of_service: false,
        });
      }
    }
  }
  return { floorId, zone, seats };
}

/** ライン・入出荷の持ち場の帯（持ち場を等間隔に並べる）。 */
function stationZone(floorId: string, label: string, index: number, name: string, kind: ZoneKind, x0: number, x1: number, y1: number, capacity: number): PlacedZone {
  const y0 = y1 - LINE_H;
  const zone: DemoZone = { zone_key: `${floorId}-l${index + 1}`, name, kind, polygon: rect(x0, y0, x1, y1) };
  const seats: DemoSeat[] = [];
  for (let k = 0; k < STATIONS_PER_LINE; k++) {
    const code = `${label}-L${index + 1}-${pad2(k + 1)}`;
    seats.push({
      seat_id: `${floorId}-${code}`,
      code,
      zone_key: zone.zone_key,
      x_mm: x0 + 1500 + k * STATION_PITCH,
      y_mm: y0 + LINE_H / 2,
      rotation_deg: 0,
      desk_type: "station",
      seat_kind: "station",
      capacity,
      features: [],
      out_of_service: false,
    });
  }
  return { floorId, zone, seats };
}

// ---- 生成 ----

export interface WorkplaceOptions {
  seed?: number;
}

/** 部（段 department の祖先。無ければ最上位の組織）。 */
export function deptResolver(orgById: Map<string, DemoOrgUnit>) {
  const memo = new Map<string, DemoOrgUnit>();
  return (orgId: string): DemoOrgUnit | null => {
    const hit = memo.get(orgId);
    if (hit) return hit;
    let cur = orgById.get(orgId);
    let top: DemoOrgUnit | undefined;
    const seen = new Set<string>();
    while (cur && !seen.has(cur.id)) {
      seen.add(cur.id);
      if (cur.level_key === "department") {
        memo.set(orgId, cur);
        return cur;
      }
      top = cur;
      cur = cur.parent_id ? orgById.get(cur.parent_id) : undefined;
    }
    if (top) memo.set(orgId, top);
    return top ?? null;
  };
}

/** 組織の拠点（自分か祖先の拠点。期間は見ない）。 */
function orgSiteResolver(orgById: Map<string, DemoOrgUnit>) {
  return (orgId: string): string | null => {
    let cur = orgById.get(orgId);
    const seen = new Set<string>();
    while (cur && !seen.has(cur.id)) {
      seen.add(cur.id);
      if (cur.site_id) return cur.site_id;
      cur = cur.parent_id ? orgById.get(cur.parent_id) : undefined;
    }
    return null;
  };
}

/** 部の働き方（部の名前と、所属する人の職種の多数から決める）。 */
function workStyleOf(dept: DemoOrgUnit, people: DemoPerson[], plans: Set<BuildingPlan>): WorkStyle {
  const jobs = new Map<string, number>();
  for (const p of people) if (p.job_type) jobs.set(p.job_type, (jobs.get(p.job_type) ?? 0) + 1);
  const major = [...jobs.entries()].sort((a, b) => b[1] - a[1])[0]?.[0] ?? "";
  if (plans.has("factory") && (/製造|生産/.test(dept.name) || major === "製造")) return "line";
  if (plans.has("logistics") && (/物流|倉庫|配送/.test(dept.name) || major === "物流")) return "dock";
  return "fixed";
}

/** 期間の重ならない人どうしで枠を使い回す（開始の早い順に、空いた最小の枠へ）。返り値は枠の数。 */
function allocateSlots(intervals: DeptDemand["intervals"]): number {
  intervals.sort((a, b) => a.from.localeCompare(b.from) || a.person.employee_number.localeCompare(b.person.employee_number));
  const ends: Array<string | null> = [];
  for (const iv of intervals) {
    let slot = ends.findIndex((end) => end !== null && end <= iv.from);
    if (slot < 0) {
      slot = ends.length;
      ends.push(iv.until);
    } else ends[slot] = iv.until;
    iv.slot = slot;
  }
  return ends.length;
}

export function generateWorkplace(data: DemoDataset, options: WorkplaceOptions = {}): DemoWorkplace {
  const rng = mulberry32(options.seed ?? 20261018);
  const orgById = new Map(data.org_units.map((o) => [o.id, o]));
  const deptOf = deptResolver(orgById);
  const orgSite = orgSiteResolver(orgById);
  const siteByName = new Map(data.sites.map((s) => [s.name, s]));

  const buildings: DemoBuilding[] = [];
  const floors: DemoFloor[] = [];
  const layouts = new Map<string, DemoLayout>();
  const assignments: DemoSeatAssignment[] = [];

  // 人の所属の期間（主務）を、拠点 × 部ごとにまとめる
  const plansBySite = new Map<string, SitePlan>();
  for (const plan of WORKPLACE_SITES) {
    const site = siteByName.get(plan.site);
    if (site) plansBySite.set(site.id, plan);
  }
  const demands = new Map<string, Map<string, DeptDemand>>();
  for (const person of data.people) {
    const end = person.termination_date ? addDays(person.termination_date, 1) : null;
    const pieces: Array<{ dept: DemoOrgUnit; siteId: string; from: string; until: string | null }> = [];
    const primaries = person.assignments.filter((a) => a.type === "primary").sort((a, b) => a.valid_from.localeCompare(b.valid_from));
    for (const a of primaries) {
      const siteId = person.site_id ?? orgSite(a.org_unit_id);
      const dept = deptOf(a.org_unit_id);
      if (!siteId || !dept || !plansBySite.has(siteId)) continue;
      const from = a.valid_from > person.hire_date ? a.valid_from : person.hire_date;
      const until = end && (a.valid_until === null || end < a.valid_until) ? end : a.valid_until;
      if (until !== null && until <= from) continue;
      const last = pieces.at(-1);
      // 同じ部・同じ拠点で続く期間は 1 つにまとめる（課の異動では席を変えない）
      if (last && last.dept.id === dept.id && last.siteId === siteId && last.until === from) last.until = until;
      else pieces.push({ dept, siteId, from, until });
    }
    for (const piece of pieces) {
      const bySite = demands.get(piece.siteId) ?? new Map<string, DeptDemand>();
      demands.set(piece.siteId, bySite);
      const d = bySite.get(piece.dept.id) ?? { dept: piece.dept, style: "fixed" as WorkStyle, intervals: [], peak: 0 };
      d.intervals.push({ person, from: piece.from, until: piece.until, slot: -1 });
      bySite.set(piece.dept.id, d);
    }
  }

  // 拠点ごとに建物・階・配置図を作る
  const placedByDept = new Map<string, PlacedZone[]>();
  for (const [siteId, plan] of plansBySite) {
    const site = data.sites.find((s) => s.id === siteId)!;
    const siteDemands = [...(demands.get(siteId)?.values() ?? [])].sort((a, b) => a.dept.code.localeCompare(b.dept.code));
    const planKinds = new Set(plan.buildings.map((b) => b.plan));
    for (const d of siteDemands) {
      d.style = workStyleOf(d.dept, [...new Set(d.intervals.map((i) => i.person))], planKinds);
      d.peak = allocateSlots(d.intervals);
    }
    // 人数の少ない部（人ごとの拠点でこの拠点にいる人など）は、1 つの共用の固定席のエリアにまとめる
    const small = siteDemands.filter((d) => d.peak < SMALL_DEPT);
    if (small.length > 0) {
      const shared: DeptDemand = {
        dept: { ...small[0]!.dept, id: `shared-${site.code}`, code: "~shared", name: "共用", parent_id: null, valid_from: "2000-01-01", valid_until: null },
        style: "fixed",
        intervals: small.flatMap((d) => d.intervals.map((iv) => ({ ...iv, slot: -1 }))),
        peak: 0,
      };
      shared.peak = allocateSlots(shared.intervals);
      siteDemands.splice(0, siteDemands.length, ...siteDemands.filter((d) => d.peak >= SMALL_DEPT), shared);
    }
    plan.buildings.forEach((b, bi) => {
      const building: DemoBuilding = { id: `bld-${site.code}-${b.code}`, site_id: siteId, code: b.code, name: b.name, sort_order: bi };
      buildings.push(building);
      const inBuilding = siteDemands.filter((d) => {
        if (b.plan === "factory") return d.style === "line";
        if (b.plan === "annex") return d.style !== "line";
        return true;
      });
      const addFloor = (level: number, kind: FloorKind): DemoFloor => {
        const label = floorLabel(level);
        const floor: DemoFloor = { id: `${building.id}-${label}`, building_id: building.id, label, level_order: level * 10, kind };
        floors.push(floor);
        return floor;
      };
      const addLayout = (floor: DemoFloor, width: number, depth: number, fixtures: DemoLayout["fixtures"], placed: PlacedZone[]) => {
        layouts.set(floor.id, {
          layout_id: `${floor.id}-r1`,
          floor_id: floor.id,
          valid_from: "2000-01-01",
          outline: rect(0, 0, width, depth),
          fixtures,
          grid_mm: 100,
          zones: placed.map((p) => p.zone),
          seats: placed.flatMap((p) => p.seats),
        });
      };
      const seatDemand = (d: DeptDemand) => d.peak + Math.max(1, Math.ceil(d.peak * 0.12));

      // 事務の階（本館の 2F〜、事務棟の 1F〜、物流センターの倉庫の上）
      const officeFloorsFor = (
        reqs: ZoneRequest[],
        startLevel: number,
        minFloors: number,
        maxFloors: number,
        depth: number,
        fixedWidth?: number,
      ): { width: number; depth: number } => {
        const total = reqs.reduce((s, r) => s + r.seats, 0);
        const target = Math.min(maxFloors, Math.max(minFloors, Math.ceil(total / 230)));
        // 机の数（列）と奥行きの組から、目安の階数に収まる最も小さい外形を選ぶ（階がエリアで埋まるように）
        let geom = officeGeom(fixedWidth ? maxDprFor(fixedWidth) : 6, depth, fixedWidth);
        let packed = packOffice(reqs, geom, target);
        if (!fixedWidth) {
          let best: { geom: OfficeGeom; packed: ReturnType<typeof packOffice>; area: number } | null = null;
          for (let dpr = 6; dpr <= 20; dpr += 2) {
            for (let d = 16000; d <= depth; d += 2000) {
              const g = officeGeom(dpr, d);
              const p = packOffice(reqs, g, target);
              const area = g.width * g.depth;
              if (p.length <= target && (!best || area < best.area)) best = { geom: g, packed: p, area };
            }
          }
          if (best) {
            geom = best.geom;
            packed = best.packed;
          } else {
            geom = officeGeom(20, depth);
            packed = packOffice(reqs, geom, target);
          }
        }
        while (packed.length < minFloors) packed.push([]);
        packed.forEach((pieces, i) => {
          const floor = addFloor(startLevel + i, "office");
          const placed = pieces.map((piece, k) => officeZone(floor.id, floor.label, k, piece, geom));
          pieces.forEach((piece, k) => {
            const list = placedByDept.get(`${siteId}|${piece.req.demand.dept.id}`) ?? [];
            list.push(placed[k]!);
            placedByDept.set(`${siteId}|${piece.req.demand.dept.id}`, list);
          });
          const fixtures: DemoLayout["fixtures"] = [
            { kind: "core", name: "EV・階段", polygon: rect(...geom.core) },
            ...geom.rooms.map((r, k) => ({ kind: "room" as const, name: `会議室 ${String.fromCharCode(65 + k)}`, polygon: rect(...r) })),
          ];
          addLayout(floor, geom.width, geom.depth, fixtures, placed);
        });
        return { width: geom.width, depth: geom.depth };
      };

      // 持ち場の階（工場のライン・物流の入出荷）。1 階に 3〜4 本の帯
      const stationFloorsFor = (deptList: DeptDemand[], kind: "factory" | "warehouse", startLevel: number, minFloors: number) => {
        const width = 60000;
        const depth = kind === "factory" ? 30000 : 40000;
        const perFloor = 4;
        const lines: DeptDemand[] = [];
        for (const d of deptList) {
          const stations = Math.ceil(d.peak / capacityOf(d.style)) + 1;
          for (let k = 0; k < Math.max(1, Math.ceil(stations / STATIONS_PER_LINE)); k++) lines.push(d);
        }
        const count = Math.max(minFloors, Math.ceil(lines.length / perFloor));
        const per = Math.ceil(lines.length / count);
        for (let f = 0; f < count; f++) {
          const floor = addFloor(startLevel + f, kind);
          const mine = lines.slice(f * per, (f + 1) * per);
          const placed: PlacedZone[] = [];
          const lineX1 = width - 12000;
          let y = kind === "factory" ? depth - MARGIN : 28000;
          mine.forEach((d, k) => {
            const n = f * per + k + 1;
            const name = kind === "factory" ? `第${n}ライン` : `入出荷 ${n}`;
            const z = stationZone(floor.id, floor.label, k, name, kind === "factory" ? "production_line" : "storage", MARGIN, lineX1, y, capacityOf(d.style));
            placed.push(z);
            const list = placedByDept.get(`${siteId}|${d.dept.id}`) ?? [];
            list.push(z);
            placedByDept.set(`${siteId}|${d.dept.id}`, list);
            y -= LINE_H + LINE_GAP;
          });
          const fixtures: DemoLayout["fixtures"] = [{ kind: "core", name: "EV・階段", polygon: rect(width - 10000, MARGIN, width - MARGIN, MARGIN + 9000) }];
          if (kind === "warehouse") {
            // 保管の棚（細い枠の並び）
            for (let r = 0; r < 6; r++) fixtures.push({ kind: "room", name: null, polygon: rect(MARGIN + r * 7600, 30500, MARGIN + r * 7600 + 5200, depth - MARGIN - 600) });
          }
          const extraZones: PlacedZone[] =
            kind === "factory"
              ? [{ floorId: floor.id, zone: { zone_key: `${floor.id}-zs`, name: "部材置き場", kind: "storage", polygon: rect(width - 10000, MARGIN + 10000, width - MARGIN, depth - MARGIN) }, seats: [] }]
              : [{ floorId: floor.id, zone: { zone_key: `${floor.id}-zs`, name: "保管エリア", kind: "storage", polygon: rect(MARGIN, 29900, width - 12000, depth - MARGIN) }, seats: [] }];
          addLayout(floor, width, depth, fixtures, [...placed, ...extraZones]);
        }
        return { width, depth };
      };

      const officeReqs = (list: DeptDemand[]) => list.filter((d) => d.style === "fixed").map((d) => ({ demand: d, seats: seatDemand(d) }));
      if (b.plan === "tower") {
        // 地下 1 階（駐車場・機械室）と 1 階（受付）は席を持たない
        const reqs = officeReqs(inBuilding);
        const floorCountBefore = floors.length;
        const size = officeFloorsFor(reqs, 2, 4, 12, 30000);
        const b1 = addFloor(-1, "service");
        const f1 = addFloor(1, "lobby");
        // 並びを B1・1F・2F… にする（地上の事務の階より先に作った扱い）
        floors.splice(floors.length - 2, 2);
        floors.splice(floorCountBefore, 0, b1, f1);
        const w = size.width;
        const d = size.depth;
        const core = rect(Math.floor(w / 2) - CORE_W / 2, Math.floor(d / 2) - 4000, Math.floor(w / 2) + CORE_W / 2, Math.floor(d / 2) + 4000);
        addLayout(b1, w, d, [{ kind: "core", name: "EV・階段", polygon: core }], [
          { floorId: b1.id, zone: { zone_key: `${b1.id}-zp`, name: "駐車場", kind: "common", polygon: rect(MARGIN, MARGIN, Math.floor(w / 2) - CORE_W / 2 - MARGIN, d - MARGIN) }, seats: [] },
          { floorId: b1.id, zone: { zone_key: `${b1.id}-zm`, name: "機械室", kind: "restricted", polygon: rect(Math.floor(w / 2) + CORE_W / 2 + MARGIN, MARGIN, w - MARGIN, d - MARGIN) }, seats: [] },
        ]);
        const roomW = Math.floor((w / 2 - CORE_W / 2 - 2 * MARGIN) / 3);
        addLayout(
          f1,
          w,
          d,
          [
            { kind: "core", name: "EV・階段", polygon: core },
            ...[0, 1, 2].map((k) => ({
              kind: "room" as const,
              name: `来客室 ${k + 1}`,
              polygon: rect(Math.floor(w / 2) + CORE_W / 2 + MARGIN + k * roomW, MARGIN, Math.floor(w / 2) + CORE_W / 2 + MARGIN + (k + 1) * roomW - 400, MARGIN + 7000),
            })),
          ],
          [{ floorId: f1.id, zone: { zone_key: `${f1.id}-zl`, name: "受付・ロビー", kind: "common", polygon: rect(MARGIN, MARGIN, Math.floor(w / 2) - CORE_W / 2 - MARGIN, d - MARGIN) }, seats: [] }],
        );
      } else if (b.plan === "annex") {
        officeFloorsFor(officeReqs(inBuilding), 1, 1, 8, 30000);
      } else if (b.plan === "factory") {
        stationFloorsFor(inBuilding, "factory", 1, 2);
      } else {
        const docks = inBuilding.filter((d) => d.style === "dock");
        const size = stationFloorsFor(docks, "warehouse", 1, 1);
        const top = floors.filter((f) => f.building_id === building.id).length;
        officeFloorsFor(officeReqs(inBuilding), top + 1, 1, 6, size.depth, size.width);
      }
    });

    // 割り当て
    for (const d of siteDemands) {
      const placed = placedByDept.get(`${siteId}|${d.dept.id}`) ?? [];
      if (placed.length === 0) continue;
      const seats = placed.flatMap((p) => p.seats.map((s) => ({ seat: s, floorId: p.floorId })));
      if (d.style === "fixed") {
        // 予備の席の一部を「使えない席」（保守中・予備）にする
        seats.forEach((s, i) => {
          if (i >= d.peak && rng() < 0.18) s.seat.out_of_service = true;
        });
      }
      for (const iv of d.intervals) {
        const index = d.style === "fixed" ? iv.slot : Math.floor(iv.slot / capacityOf(d.style));
        const target = seats[index];
        if (!target || target.seat.out_of_service) continue;
        assignments.push({
          person_id: iv.person.id,
          floor_id: target.floorId,
          seat_id: target.seat.seat_id,
          zone_key: null,
          role: "primary",
          exclusive: target.seat.capacity === 1,
          valid_from: iv.from,
          valid_until: iv.until,
        });
      }
    }
  }

  // ---- 外部から届いた今日の所在（サンプル。業務日付だけ） ----
  const today = data.meta.business_date;
  const assignedToday = new Set<string>();
  for (const a of assignments) if (a.seat_id && inPeriod(today, a.valid_from, a.valid_until)) assignedToday.add(a.seat_id);
  const freeByZone = new Map<string, DemoSeat[]>();
  for (const layout of layouts.values()) {
    for (const seat of layout.seats) {
      if (seat.seat_kind !== "fixed" || !seat.zone_key || seat.out_of_service || assignedToday.has(seat.seat_id)) continue;
      const list = freeByZone.get(seat.zone_key) ?? [];
      list.push(seat);
      freeByZone.set(seat.zone_key, list);
    }
  }
  const presence: DemoPresence[] = [];
  const listed = new Set(data.people.filter((p) => p.termination_date === null || p.termination_date >= today).map((p) => p.id));
  for (const a of assignments) {
    if (!a.seat_id || !a.exclusive || !listed.has(a.person_id) || !inPeriod(today, a.valid_from, a.valid_until)) continue;
    const roll = rng();
    const own = layouts.get(a.floor_id)?.seats.find((s) => s.seat_id === a.seat_id);
    if (roll < 0.5) presence.push({ person_id: a.person_id, floor_id: a.floor_id, seat_id: a.seat_id, zone_key: own?.zone_key ?? null });
    else if (roll < 0.58 && own?.zone_key) {
      // 割り当ての無い席に座る（同じエリアの空き）
      const pool = freeByZone.get(own.zone_key) ?? [];
      const seat = pool.splice(Math.floor(rng() * pool.length), 1)[0];
      if (seat) presence.push({ person_id: a.person_id, floor_id: a.floor_id, seat_id: seat.seat_id, zone_key: own.zone_key });
    } else if (roll < 0.62 && own?.zone_key) presence.push({ person_id: a.person_id, floor_id: a.floor_id, seat_id: null, zone_key: own.zone_key });
  }

  return {
    buildings,
    floors,
    layouts,
    assignments,
    presence,
  };
}
