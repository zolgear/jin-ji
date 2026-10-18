// 建物・階・配置図・席の割り当てと、外部から届く今日の所在の型と取得元のインターフェース。
// 仕様は docs/design/building-floor-mode.md（§2 データモデル・§3 権限・§6 API）。
//
// - 座標は階ごとのローカル座標で、整数のミリメートル（原点は図面の左下、x は右、y は奥）
// - 席の状態は閲覧者から見た 3 つだけ（人・空席・使えない席。§3.2）。閲覧者に見えない人の席は「使えない席」として届く
// - 扱うのは人の決まった席・持ち場（有効期間付きの割り当て）だけ。タイムマシンで見た日の割り当てがその日の所在
// - 今日の所在は、連携しているシステムから届いたものを重ねるだけ（今日だけ。提供元が無い導入先では無効）。無いことに意味を持たせない
//
// OpenAPI（schema.gen.ts）の再生成の前に、設計書の §6 のパスとフィールドで先に型を置いている。
// バックエンドの最終形が決まったら、schema.gen.ts の型に置き換える。
import type { Person } from "./types";

/** 階の種類（表示とまとめ方だけに使う固定の少数。§2.2）。 */
export type FloorKind = "office" | "factory" | "warehouse" | "lobby" | "service" | "other";
/** エリア（ゾーン）の種類（§2.3）。 */
export type ZoneKind = "office" | "production_line" | "storage" | "common" | "restricted";
/** 固定の部分（コア・壁で囲った部屋・吹き抜け）。 */
export type FixtureKind = "core" | "room" | "void";
/** 席の種類（固定席・持ち場）。 */
export type SeatKind = "fixed" | "station";
/** 閲覧者から見た席の状態（§3.2 の 3 つ。occupied = 閲覧者に見える人の席）。 */
export type SeatStatus = "occupied" | "vacant" | "unavailable";

/** [x_mm, y_mm] の点。 */
export type PointMm = [number, number];

/** 階の一覧の 1 行（席数・割り当て数・空席数は閲覧者に見える人だけで数える）。 */
export interface FloorSummary {
  id: string;
  building_id: string;
  /** 表示名（B1・1F・M2F など）。 */
  label: string;
  /** 並び順（地上 n 階 = n × 10、地下 = 負、中二階は間の値）。 */
  level_order: number;
  kind: FloorKind;
  seat_count: number;
  /** 人がいる席の数（閲覧者に見える人の割り当て）。 */
  assigned_count: number;
  vacant_count: number;
  unavailable_count: number;
}

/** 建物と階の一覧（GET /sites/{id}/buildings の items、GET /buildings/{id}）。 */
export interface Building {
  id: string;
  site_id: string;
  code: string;
  name: string;
  sort_order: number;
  /** 階（level_order の昇順）。 */
  floors: FloorSummary[];
}

export interface LayoutFixture {
  kind: FixtureKind;
  /** 表示用の短い名前（「会議室 A」「EV・階段」）。 */
  name: string | null;
  polygon: PointMm[];
}

export interface LayoutZone {
  /** 改訂をまたいで同じゾーンを指す ID。 */
  zone_key: string;
  name: string;
  kind: ZoneKind;
  polygon: PointMm[];
}

export interface LayoutSeat {
  seat_id: string;
  /** 席番号（階の中で一意）。 */
  code: string;
  zone_key: string | null;
  x_mm: number;
  y_mm: number;
  rotation_deg: number;
  desk_type: string;
  seat_kind: SeatKind;
  capacity: number;
  features: string[];
  status: SeatStatus;
}

/** その日の配置図（GET /floors/{id}/layout）。人は含まない。 */
export interface FloorLayout {
  floor_id: string;
  as_of: string;
  /** 配置図の改訂の ID と有効になった日。 */
  layout_id: string;
  valid_from: string;
  outline: PointMm[];
  fixtures: LayoutFixture[];
  grid_mm: number;
  zones: LayoutZone[];
  seats: LayoutSeat[];
}

/** 階の人と席の対応（GET /floors/{id}/occupants の items。閲覧者に seat が見える人だけ）。 */
export interface FloorOccupant {
  person_id: string;
  display_name: string | null;
  /** 主務の組織（その日）。 */
  org_unit_id: string | null;
  org_unit_name: string | null;
  /** 席の割り当て（席か、ライン・エリアのどちらか一方）。 */
  seat_id: string | null;
  zone_key: string | null;
  role: "primary" | "secondary";
}

/** 人の所在の階・エリア（人の項目 workplace）。 */
export interface PersonWorkplace {
  site_id: string;
  building_id: string;
  building_name: string;
  floor_id: string;
  floor_label: string;
  zone_key: string | null;
  zone_name: string | null;
}

/** 人の席（人の項目 seat）。 */
export interface PersonSeat {
  seat_id: string;
  code: string;
  floor_id: string;
  x_mm: number;
  y_mm: number;
}

/** 人の今日の所在（人の項目 presence。外部から届いたもの。無い・見えないときは項目ごと省かれる）。 */
export interface PersonPresence {
  date: string;
  building_id: string;
  building_name: string;
  floor_id: string;
  floor_label: string;
  zone_key: string | null;
  zone_name: string | null;
  /** 席番号は項目 seat も見えるときだけ。 */
  seat_id: string | null;
  seat_code: string | null;
}

/** 席・階・今日の所在の項目を持つ人（OpenAPI の再生成までの拡張）。 */
export type PersonWithWorkplace = Person & {
  workplace?: PersonWorkplace | null;
  seat?: PersonSeat | null;
  presence?: PersonPresence | null;
};

export function workplaceOf(person: Person): { workplace: PersonWorkplace | null; seat: PersonSeat | null; presence: PersonPresence | null } {
  const p = person as PersonWithWorkplace;
  return { workplace: p.workplace ?? null, seat: p.seat ?? null, presence: p.presence ?? null };
}

/** 階の今日の所在 1 件（閲覧者に presence が見える人の分だけ）。 */
export interface FloorPresenceItem {
  person_id: string;
  display_name: string | null;
  org_unit_id: string | null;
  org_unit_name: string | null;
  /** 席番号は seat も見えるときだけ（null ならエリアだけ）。 */
  seat_id: string | null;
  zone_key: string | null;
}

/** 階の今日の所在（GET /floors/{id}/presence。外部から届いたもの。今日だけ）。 */
export interface FloorPresence {
  floor_id: string;
  date: string;
  /** 見えない人の所在で埋まった席（使えない席として表示する）。 */
  unavailable_seat_ids: string[];
  /** 閲覧者に見える人の所在。 */
  items: FloorPresenceItem[];
}

/**
 * 建物・フロアモードの取得元。API アダプタとデモアダプタが同じ形で実装する（DirectoryAdapter.workplace）。
 * 時点（asOf）の規則は人の情報と同じ。
 */
export interface WorkplaceAdapter {
  /** 拠点の建物と階の一覧。 */
  listBuildings(siteId: string, asOf: string): Promise<Building[]>;
  getBuilding(id: string, asOf: string): Promise<Building>;
  /** その日の配置図（席の状態を含む。人は含まない）。 */
  getFloorLayout(floorId: string, asOf: string): Promise<FloorLayout>;
  /** 階の人と席の対応（すべてのページを読む）。 */
  listFloorOccupants(floorId: string, asOf: string): Promise<FloorOccupant[]>;
  /** 階の今日の所在（外部から届いたもの）。提供元が無い導入先では null。 */
  getFloorPresence(floorId: string, date: string): Promise<FloorPresence | null>;
}
