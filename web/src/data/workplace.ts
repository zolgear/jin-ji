// 建物・階・配置図・席の割り当てと、当日の所在（自己申告の座席予約）の型と取得元のインターフェース。
// 仕様は docs/design/building-floor-mode.md（§2 データモデル・§3 権限・§6 API）。
//
// - 座標は階ごとのローカル座標で、整数のミリメートル（原点は図面の左下、x は右、y は奥）
// - 席の状態は閲覧者から見た 3 つだけ（人・空席・使えない席。§3.2）。閲覧者に見えない人の席は「使えない席」として届く
// - 当日の所在は今日以降の予約だけを扱い、予約が無いことに意味を持たせない（「不在」の値を作らない。§3.4）
//
// OpenAPI（schema.gen.ts）の再生成の前に、設計書の §6 のパスとフィールドで先に型を置いている。
// バックエンドの最終形が決まったら、schema.gen.ts の型に置き換える。
import type { Person } from "./types";

/** 階の種類（表示とまとめ方だけに使う固定の少数。§2.2）。 */
export type FloorKind = "office" | "factory" | "warehouse" | "lobby" | "service" | "other";
/** エリア（ゾーン）の種類（§2.3）。 */
export type ZoneKind = "office" | "free_address" | "production_line" | "storage" | "common" | "restricted";
/** 固定の部分（コア・壁で囲った部屋・吹き抜け）。 */
export type FixtureKind = "core" | "room" | "void";
/** 席の種類（固定席・フリーアドレスの席・持ち場）。 */
export type SeatKind = "fixed" | "free" | "station";
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
  /** 今日の空き席数（当日の所在を重ねられるときだけ。予約・割り当てで埋まっていない席）。それ以外は null。 */
  today_vacant_count: number | null;
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
  /** このゾーンをホームエリアにしている組織（その日に有効なもの。組織の情報なので全社公開）。 */
  home_org_units: Array<{ id: string; name: string }>;
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
  /** 主務の組織（・上位組織）のホームエリアを引き継いだもの（フリーアドレス）。 */
  inherited: boolean;
}

/** 人の席（人の項目 seat）。 */
export interface PersonSeat {
  seat_id: string;
  code: string;
  floor_id: string;
  x_mm: number;
  y_mm: number;
}

/** 人の今日の予約（人の項目 presence。予約が無い・見えないときは項目ごと省かれる）。 */
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

/** 席・階・今日の予約の項目を持つ人（OpenAPI の再生成までの拡張）。 */
export type PersonWithWorkplace = Person & {
  workplace?: PersonWorkplace | null;
  seat?: PersonSeat | null;
  presence?: PersonPresence | null;
};

export function workplaceOf(person: Person): { workplace: PersonWorkplace | null; seat: PersonSeat | null; presence: PersonPresence | null } {
  const p = person as PersonWithWorkplace;
  return { workplace: p.workplace ?? null, seat: p.seat ?? null, presence: p.presence ?? null };
}

/** 当日の所在の機能の設定（機能が無効なら取得元が null を返す）。 */
export interface PresenceSettings {
  /** 利用目的の文（社内に公表した文）。 */
  purpose: string;
  /** 予約できる先の日数（今日を含まない。既定 14）。 */
  days_ahead: number;
  /** 予約の単位。 */
  unit: "seat" | "zone" | "both";
}

/** 階の予約（閲覧者に presence が見える人の分だけ）。 */
export interface FloorReservation {
  person_id: string;
  display_name: string | null;
  org_unit_id: string | null;
  org_unit_name: string | null;
  /** 席番号は seat も見えるときだけ（null ならエリアだけ）。 */
  seat_id: string | null;
  zone_key: string | null;
}

/** その日の階の予約の状態（GET /floors/{id}/presence?date=）。 */
export interface FloorPresence {
  floor_id: string;
  date: string;
  /** 見えない人の予約で埋まった席（使えない席として表示する。予約があることを目立たせない）。 */
  unavailable_seat_ids: string[];
  /** 閲覧者に見える人の予約。 */
  reservations: FloorReservation[];
  /** その日の空き席数（予約・割り当てで埋まっていない席）。 */
  vacant_count: number;
}

/** 本人の予約の 1 件（GET /me/presence の items）。 */
export interface MyReservation {
  date: string;
  site_id: string;
  building_id: string;
  building_name: string;
  floor_id: string;
  floor_label: string;
  zone_key: string | null;
  zone_name: string | null;
  seat_id: string | null;
  seat_code: string | null;
}

/** 予約の対象（席かエリアのどちらか）。 */
export type ReservationTarget = { floorId: string; seatId: string } | { floorId: string; zoneKey: string };

/**
 * 建物・フロアモードの取得元。API アダプタとデモアダプタが同じ形で実装する（DirectoryAdapter.workplace）。
 * 時点（asOf）の規則は人の情報と同じ。当日の所在は時点を受けない（タイムマシンの対象外）。
 */
export interface WorkplaceAdapter {
  /** 拠点の建物と階の一覧。今日を表示しているときだけ today_vacant_count が入る。 */
  listBuildings(siteId: string, asOf: string): Promise<Building[]>;
  getBuilding(id: string, asOf: string): Promise<Building>;
  /** その日の配置図（席の状態を含む。人は含まない）。 */
  getFloorLayout(floorId: string, asOf: string): Promise<FloorLayout>;
  /** 階の人と席の対応（すべてのページを読む）。 */
  listFloorOccupants(floorId: string, asOf: string): Promise<FloorOccupant[]>;
  /** 当日の所在の設定。機能が無効なら null。 */
  getPresenceSettings(): Promise<PresenceSettings | null>;
  /** その日（今日から予約できる先の日数まで）の階の予約。機能が無効なら null。 */
  getFloorPresence(floorId: string, date: string): Promise<FloorPresence | null>;
  /** 本人の予約（今日以降と、保持の間の過去の日）。 */
  listMyPresence(): Promise<MyReservation[]>;
  /** 本人の予約・変更（1 人 1 日 1 件）。取り合いは 409。 */
  reserve(date: string, target: ReservationTarget): Promise<MyReservation>;
  /** 本人の予約の取り消し（記録を残さない）。 */
  cancel(date: string): Promise<void>;
}
