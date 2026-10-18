// 建物・フロアモードの表示の組み立て（three・React に依存しない純粋な関数）。仕様 docs/design/building-floor-mode.md §3.2・§5。
//
// - 席の状態は閲覧者から見た 3 つだけ（人・空席・使えない席）。空席は「割り当ての無い席」の意味だけ
//   - 席を扱うのは人の決まった席・持ち場（有効期間付きの割り当て）だけ。タイムマシンで見た日の割り当てがその日の所在
//   - 外部から届いた今日の所在を重ねても増やさない（§5.7）。見える人の所在 → 人（「今日」の印）、見えない人の所在で埋まった席 → 使えない席
//   - 所在が無い人の固定席は、割り当てのまま人を出す（「不在」の印は付けない）
// - 色は「組織で色分け」: その階で席の多い組織（既定は部の段）3 つまでを categorical の 3 枠にし、残りは灰の「その他」
//   （lib/siteColors.ts と同じ all-pairs の制約）。色だけに頼らず、エリアの札に組織名を文字で出す
import type { FloorLayout, FloorOccupant, FloorPresence, FloorPresenceItem, LayoutSeat, LayoutZone, OrgTree, PointMm } from "../../data/types";

/** 色の枠の数（all-pairs で検証を通る categorical の先頭 3 枠。lib/siteColors.ts と同じ）。 */
export const ORG_COLOR_SLOTS = 3;
/** CSS の色（枠の順。--site-1〜3 は dataviz の categorical の 1〜3 枠と同じ値）。 */
export const ORG_COLOR_VARS = ["var(--site-1)", "var(--site-2)", "var(--site-3)"] as const;
export const ORG_OTHER_VAR = "var(--site-other)";

/** 閲覧者から見た席の状態（§3.2）。 */
export type SeatState = "person" | "vacant" | "unavailable";

/** 色分けのまとまり（既定は部）。 */
export interface OrgGroup {
  id: string;
  name: string;
  code: string;
}

export interface SeatPerson {
  personId: string;
  name: string | null;
  orgId: string | null;
  orgName: string | null;
  group: OrgGroup | null;
  /** 今日の所在（外部から届いたもの）による人か。 */
  today: boolean;
}

export interface SeatView {
  seat: LayoutSeat;
  state: SeatState;
  /** 席にいる人（その日の割り当てと、今日の所在の人）。持ち場は定員まで複数。 */
  people: SeatPerson[];
  /** 色の枠（0〜2）。null は「その他」、undefined は色なし（空席・使えない席）。 */
  slot: number | null | undefined;
  /** 今日の所在による人がいるか（「今日」の印）。 */
  today: boolean;
}

export interface ZoneView {
  zone: LayoutZone;
  /** 札の名前（席の半分以上を占める組織名。無ければエリアの名前）。 */
  label: string;
  seats: number;
  used: number;
  vacant: number;
  /** エリアの色の枠（エリアの席で多い組織の枠）。 */
  slot: number | null | undefined;
  /** 札を置く位置（多角形の左上の角）。 */
  anchor: PointMm;
}

export interface LegendEntry {
  group: OrgGroup;
  slot: number | null;
  seats: number;
}

export interface FloorCounts {
  seats: number;
  used: number;
  vacant: number;
  unavailable: number;
}

export interface FloorView {
  layout: FloorLayout;
  seats: SeatView[];
  zones: ZoneView[];
  /** 色を付けた組織（枠の順）と「その他」。 */
  legend: LegendEntry[];
  counts: FloorCounts;
  /** 今日の所在を重ねているか。 */
  presenceShown: boolean;
  /** 席を持たない所在（見える人の分。エリアだけ）。 */
  zoneOnly: FloorPresenceItem[];
  bounds: { minX: number; minY: number; maxX: number; maxY: number };
}

/**
 * 組織の ID → 色分けのまとまり（段 department の祖先、無ければ最上位から 2 段目、それも無ければ組織そのもの）。
 * 組織ツリーに無い組織は null。
 */
export function groupResolver(tree: OrgTree | undefined): (orgId: string | null) => OrgGroup | null {
  const byId = new Map((tree?.units ?? []).map((u) => [u.id, u]));
  const memo = new Map<string, OrgGroup | null>();
  return (orgId) => {
    if (!orgId) return null;
    if (memo.has(orgId)) return memo.get(orgId)!;
    const chain: NonNullable<ReturnType<typeof byId.get>>[] = [];
    let cur = byId.get(orgId);
    const seen = new Set<string>();
    while (cur && !seen.has(cur.id)) {
      seen.add(cur.id);
      chain.unshift(cur);
      cur = cur.parent_id ? byId.get(cur.parent_id) : undefined;
    }
    const unit = chain.find((u) => u.level?.key === "department") ?? chain[1] ?? chain[0];
    const group = unit ? { id: unit.id, name: unit.name, code: unit.code } : null;
    memo.set(orgId, group);
    return group;
  };
}

/** 多角形の外接矩形。 */
export function boundsOf(points: readonly PointMm[]): { minX: number; minY: number; maxX: number; maxY: number } {
  let minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
  for (const [x, y] of points) {
    minX = Math.min(minX, x);
    minY = Math.min(minY, y);
    maxX = Math.max(maxX, x);
    maxY = Math.max(maxY, y);
  }
  return Number.isFinite(minX) ? { minX, minY, maxX, maxY } : { minX: 0, minY: 0, maxX: 1, maxY: 1 };
}

/** 点が多角形の内側か（偶奇の規則）。 */
export function pointInPolygon([x, y]: PointMm, polygon: readonly PointMm[]): boolean {
  let inside = false;
  for (let i = 0, j = polygon.length - 1; i < polygon.length; j = i++) {
    const [xi, yi] = polygon[i]!;
    const [xj, yj] = polygon[j]!;
    if (yi > y !== yj > y && x < ((xj - xi) * (y - yi)) / (yj - yi) + xi) inside = !inside;
  }
  return inside;
}

export interface FloorViewInput {
  layout: FloorLayout;
  occupants: readonly FloorOccupant[];
  /** 今日の所在（今日を表示していて、提供元があるときだけ渡す。それ以外は null）。 */
  presence: FloorPresence | null;
  groupOf: (orgId: string | null) => OrgGroup | null;
  /** 色分けをしない（単色。選んだ人だけ色）。 */
  monochrome?: boolean;
}

/** 配置図・在籍者・今日の所在から、階の表示を組む。 */
export function buildFloorView({ layout, occupants, presence, groupOf, monochrome = false }: FloorViewInput): FloorView {
  const bySeat = new Map<string, SeatPerson[]>();
  const person = (o: { person_id: string; display_name: string | null; org_unit_id: string | null; org_unit_name: string | null }, today: boolean): SeatPerson => ({
    personId: o.person_id,
    name: o.display_name,
    orgId: o.org_unit_id,
    orgName: o.org_unit_name,
    group: groupOf(o.org_unit_id),
    today,
  });
  for (const o of occupants) {
    if (!o.seat_id) continue;
    const list = bySeat.get(o.seat_id) ?? [];
    list.push(person(o, false));
    bySeat.set(o.seat_id, list);
  }

  const unavailableToday = new Set(presence?.unavailable_seat_ids ?? []);
  const zoneOnly: FloorPresenceItem[] = [];
  if (presence) {
    for (const r of presence.items) {
      if (!r.seat_id) {
        zoneOnly.push(r);
        continue;
      }
      const list = bySeat.get(r.seat_id) ?? [];
      const own = list.find((p) => p.personId === r.person_id);
      // 割り当ての席に来ている人は、割り当ての人に「今日」の印を付ける
      if (own) own.today = true;
      else list.push(person(r, true));
      bySeat.set(r.seat_id, list);
    }
  }

  // 色の枠: 席の多い組織 3 つまで（コードの順に枠を割り当て、順位の入れ替わりで塗り替えない）
  const weight = new Map<string, { group: OrgGroup; seats: number }>();
  for (const people of bySeat.values()) {
    const g = people[0]?.group;
    if (!g) continue;
    const e = weight.get(g.id) ?? { group: g, seats: 0 };
    e.seats++;
    weight.set(g.id, e);
  }
  const ranked = [...weight.values()].sort((a, b) => b.seats - a.seats || a.group.code.localeCompare(b.group.code));
  const colored = monochrome ? [] : ranked.slice(0, ORG_COLOR_SLOTS).sort((a, b) => a.group.code.localeCompare(b.group.code));
  const slotOf = new Map<string, number | null>(ranked.map((e) => [e.group.id, null]));
  colored.forEach((e, i) => slotOf.set(e.group.id, i));
  const legend: LegendEntry[] = [
    ...colored.map((e, i) => ({ group: e.group, slot: i, seats: e.seats })),
    ...ranked.filter((e) => slotOf.get(e.group.id) === null).map((e) => ({ group: e.group, slot: null, seats: e.seats })),
  ];

  const counts: FloorCounts = { seats: 0, used: 0, vacant: 0, unavailable: 0 };
  const seats: SeatView[] = layout.seats.map((seat) => {
    const people = bySeat.get(seat.seat_id) ?? [];
    let state: SeatState;
    if (seat.status === "unavailable") state = "unavailable";
    else if (people.length > 0 || seat.status === "occupied") state = "person";
    else state = unavailableToday.has(seat.seat_id) ? "unavailable" : "vacant";
    counts.seats++;
    if (state === "person") counts.used++;
    else if (state === "vacant") counts.vacant++;
    else counts.unavailable++;
    const g = people[0]?.group;
    const slot = state !== "person" ? undefined : g ? (slotOf.get(g.id) ?? null) : null;
    return { seat, state, people, slot: monochrome && state === "person" ? null : slot, today: people.some((p) => p.today) };
  });

  const zones: ZoneView[] = layout.zones.map((zone) => {
    const mine = seats.filter((s) => s.seat.zone_key === zone.zone_key);
    let slot: number | null | undefined;
    if (!monochrome) {
      const tally = new Map<number | null, number>();
      for (const s of mine) if (s.slot !== undefined) tally.set(s.slot, (tally.get(s.slot) ?? 0) + 1);
      const top = [...tally.entries()].sort((a, b) => b[1] - a[1])[0];
      slot = top ? top[0] : undefined;
    }
    const b = boundsOf(zone.polygon);
    // 札の名前: 席の半分以上を占める組織（固定席の島）。無ければエリアの名前
    let label: string;
    {
      const tally = new Map<string, { name: string; n: number }>();
      for (const s of mine) {
        const g = s.people[0]?.group;
        if (!g) continue;
        const t = tally.get(g.id) ?? { name: g.name, n: 0 };
        t.n++;
        tally.set(g.id, t);
      }
      const top = [...tally.values()].sort((x, y) => y.n - x.n)[0];
      const used = mine.filter((s) => s.state === "person").length;
      label = top && top.n * 2 >= used ? top.name : zone.name;
    }
    return {
      zone,
      label,
      seats: mine.length,
      used: mine.filter((s) => s.state === "person").length,
      vacant: mine.filter((s) => s.state === "vacant").length,
      slot,
      anchor: [b.minX, b.maxY],
    };
  });

  return { layout, seats, zones, legend, counts, presenceShown: presence !== null, zoneOnly, bounds: boundsOf(layout.outline) };
}

/** 席の CSS の色（色なしは null）。 */
export function slotColorVar(slot: number | null | undefined): string | null {
  return slot === undefined ? null : slot === null ? ORG_OTHER_VAR : ORG_COLOR_VARS[slot]!;
}

/** 人の席（見つからなければ undefined）。 */
export function seatOfPerson(view: FloorView, personId: string | null): SeatView | undefined {
  if (!personId) return undefined;
  return view.seats.find((s) => s.people.some((p) => p.personId === personId));
}

/**
 * 今日の所在を重ねるか（§5.7）: 提供元があって、今日を表示しているときだけ。過去・将来の日には出さない。
 * presenceParam は URL の presence（"0" なら重ねない）。
 */
export function presenceOverlayOn(asOf: string, businessDate: string, available: boolean, presenceParam: string | null): boolean {
  return available && asOf === businessDate && presenceParam !== "0";
}

/** 席の向きから、人（椅子）の位置（机の中心から向きの反対へ off mm）。rotation 0 は +y（奥）を向く。 */
export function chairPosition(seat: Pick<LayoutSeat, "x_mm" | "y_mm" | "rotation_deg" | "seat_kind">, off = 650): PointMm {
  if (seat.seat_kind === "station") return [seat.x_mm, seat.y_mm];
  const a = (seat.rotation_deg / 180) * Math.PI;
  // 向き（+y を 0° とし、時計回り）
  const fx = Math.sin(a);
  const fy = Math.cos(a);
  return [Math.round(seat.x_mm - fx * off), Math.round(seat.y_mm - fy * off)];
}

/** 机の大きさ（mm。既定の型から）。 */
export function deskSize(deskType: string): { w: number; d: number } {
  switch (deskType) {
    case "desk_1400":
      return { w: 1400, d: 700 };
    case "desk_1600":
      return { w: 1600, d: 700 };
    case "counter":
      return { w: 1200, d: 500 };
    case "station":
      return { w: 1600, d: 900 };
    default:
      return { w: 1200, d: 700 };
  }
}
