// 建物・フロアの編集の計算（React に依存しない純粋な関数）。席の変更は直接登録・計画の変更の種類 workplace（ADR-0013 決定 11）。
//
// - 変更は 1 人 1 件（target_id は人、values.seat_id は行き先の席。null は席を外す）。元の席に戻したら変更を消す
// - 定員 1 の席に落とすと、その席の人と入れ替える（相手の行き先は落とした人の元の席。席が無かった人なら相手の席を外す）
// - 編集中の階の人の一覧（occupants）に変更を重ねて、平面図を描き直す。ほかの階から来る人・席の無い人は、名前と組織を渡して足す
import type { PlanChange } from "../../data/field";
import type { FloorOccupant, LayoutSeat } from "../../data/types";

/** 人の主の席の変更（seatId が null なら席を外す）。 */
export interface SeatMove {
  personId: string;
  seatId: string | null;
}

/** 変更の一覧のうち席の変更を、人ごとの行き先にする。 */
export function seatMovesOf(changes: readonly PlanChange[]): Map<string, string | null> {
  const moves = new Map<string, string | null>();
  for (const c of changes) if (c.kind === "workplace" && c.target_id) moves.set(c.target_id, c.values.seat_id ?? null);
  return moves;
}

/**
 * 人の席を決める。元の席（original。席が無ければ null。ほかの階の席でもその席の ID）と同じなら変更を消す。
 * ほかの種類の変更と、ほかの人の席の変更はそのまま残す。
 */
export function setSeat(changes: readonly PlanChange[], personId: string, seatId: string | null, original: string | null): PlanChange[] {
  const rest = changes.filter((c) => !(c.kind === "workplace" && c.target_id === personId));
  if (seatId === original) return rest;
  return [...rest, { kind: "workplace", target_id: personId, values: { seat_id: seatId } }];
}

/** 編集中の席の使い方（席 → 人）。occupants に変更を重ねた結果から作る。 */
export function holdersOf(occupants: readonly FloorOccupant[]): Map<string, string[]> {
  const bySeat = new Map<string, string[]>();
  for (const o of occupants) {
    if (o.role !== "primary" || !o.seat_id) continue;
    bySeat.set(o.seat_id, [...(bySeat.get(o.seat_id) ?? []), o.person_id]);
  }
  return bySeat;
}

/**
 * 人を席に落とす。定員 1 の席に別の人がいれば入れ替える（その人は落とした人の今の席へ。今の席が無ければ席を外す）。
 * 持ち場（定員 2 以上）は定員までなら足し、超えるなら変えない。
 * @param current 落とす前の、編集中の人の席（席が無い・ほかの階なら null）
 * @param originals 人ごとの元の席（変更を消す判定に使う）
 */
export function dropOnSeat(
  changes: readonly PlanChange[],
  occupants: readonly FloorOccupant[],
  person: { personId: string; current: string | null },
  seat: LayoutSeat,
  originals: ReadonlyMap<string, string | null>,
): PlanChange[] {
  if (person.current === seat.seat_id || seat.status === "unavailable") return [...changes];
  const holders = (holdersOf(occupants).get(seat.seat_id) ?? []).filter((id) => id !== person.personId);
  const original = (id: string) => originals.get(id) ?? null;
  if (seat.capacity > 1) {
    if (holders.length >= seat.capacity) return [...changes];
    return setSeat(changes, person.personId, seat.seat_id, original(person.personId));
  }
  let next = setSeat(changes, person.personId, seat.seat_id, original(person.personId));
  for (const other of holders) next = setSeat(next, other, person.current, original(other));
  return next;
}

/** 人ごとの名前と組織（ほかの階から来る人・席の無い人を図に足すため）。 */
export interface PersonInfo {
  name: string | null;
  orgId: string | null;
  orgName: string | null;
}

/**
 * 階の人の一覧に席の変更を重ねる。変わる人の主の席の行を外し、行き先がこの階の席なら足す。
 * @param seatIds この階の席
 * @param info ほかの階から来る人・席の無い人の名前と組織（今の一覧にいる人は一覧の値を使う）
 */
export function applySeatMoves(
  occupants: readonly FloorOccupant[],
  moves: ReadonlyMap<string, string | null>,
  seatIds: ReadonlySet<string>,
  info: ReadonlyMap<string, PersonInfo>,
): FloorOccupant[] {
  const known = new Map(occupants.map((o) => [o.person_id, o]));
  const result = occupants.filter((o) => !(o.role === "primary" && moves.has(o.person_id)));
  for (const [personId, seatId] of moves) {
    if (!seatId || !seatIds.has(seatId)) continue;
    const base = known.get(personId);
    const extra = info.get(personId);
    result.push({
      person_id: personId,
      display_name: base?.display_name ?? extra?.name ?? null,
      org_unit_id: base?.org_unit_id ?? extra?.orgId ?? null,
      org_unit_name: base?.org_unit_name ?? extra?.orgName ?? null,
      seat_id: seatId,
      zone_key: null,
      role: "primary",
    });
  }
  return result;
}
