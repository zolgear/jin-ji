// jin-ji の配置図の形式 v1（JSON）。取込と配置図の直接登録（POST /field/layouts。0026）が受け付ける形で、
// 画面では今の配置図の書き出しと、登録する JSON の読み込み（形式と数の確かめ）に使う。形の検査（外形の内側か・定員など）は API が行う。
import type { FloorLayout, Building, PointMm, LayoutSeat, FixtureKind, ZoneKind, FloorKind } from "./workplace";

export const LAYOUT_FORMAT = "jinji-floor-layout/v1";

export interface LayoutDocumentSeat {
  code: string;
  x_mm: number;
  y_mm: number;
  rotation_deg?: number;
  desk_type?: LayoutSeat["desk_type"];
  kind?: LayoutSeat["seat_kind"];
  capacity?: number;
  features?: string[];
  zone?: string | null;
  out_of_service?: boolean;
}

export interface LayoutDocumentFloor {
  label: string;
  level_order?: number;
  kind?: FloorKind;
  layout?: {
    valid_from?: string;
    grid_mm?: number;
    outline: PointMm[];
    fixtures?: Array<{ kind: FixtureKind; name?: string | null; polygon: PointMm[] }>;
    zones?: Array<{ name: string; kind?: ZoneKind; polygon: PointMm[] }>;
    seats?: LayoutDocumentSeat[];
  };
}

export interface LayoutDocument {
  format: typeof LAYOUT_FORMAT;
  site_code: string;
  buildings: Array<{ code: string; name?: string; sort_order?: number; floors: LayoutDocumentFloor[] }>;
}

/** 建物の今の配置図を、形式 v1 にする（配置図の無い階は階だけ）。 */
export function toLayoutDocument(siteCode: string, building: Building, layouts: ReadonlyMap<string, FloorLayout>): LayoutDocument {
  return {
    format: LAYOUT_FORMAT,
    site_code: siteCode,
    buildings: [
      {
        code: building.code,
        name: building.name,
        floors: building.floors.map((f) => {
          const layout = layouts.get(f.id);
          const floor: LayoutDocumentFloor = { label: f.label, level_order: f.level_order, kind: f.kind };
          if (!layout?.layout_id) return floor;
          const zoneName = new Map(layout.zones.map((z) => [z.zone_key, z.name]));
          floor.layout = {
            grid_mm: layout.grid_mm,
            outline: layout.outline,
            fixtures: layout.fixtures.map((x) => ({ kind: x.kind, name: x.name, polygon: x.polygon })),
            zones: layout.zones.map((z) => ({ name: z.name, kind: z.kind, polygon: z.polygon })),
            seats: layout.seats.map((s) => ({
              code: s.code,
              x_mm: s.x_mm,
              y_mm: s.y_mm,
              rotation_deg: s.rotation_deg,
              desk_type: s.desk_type,
              kind: s.seat_kind,
              capacity: s.capacity,
              features: [...s.features],
              zone: s.zone_key ? (zoneName.get(s.zone_key) ?? null) : null,
              // 使えない席の印は書かない（応答は閲覧者に見えない人の席と区別しない）。印の無い席は登録で今の値を引き継ぐ
            })),
          };
          return floor;
        }),
      },
    ],
  };
}

/** 読み込んだ JSON の形式を確かめ、建物・階・席の数を返す。形式が違えば Error。 */
export function readLayoutDocument(text: string): { document: LayoutDocument; buildings: number; floors: number; seats: number } {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Error("JSON として読めません。");
  }
  const doc = parsed as Partial<LayoutDocument> | null;
  if (!doc || typeof doc !== "object" || doc.format !== LAYOUT_FORMAT) throw new Error(`format が ${LAYOUT_FORMAT} ではありません。`);
  if (!Array.isArray(doc.buildings) || doc.buildings.length === 0) throw new Error("建物（buildings）がありません。");
  const floors = doc.buildings.flatMap((b) => (Array.isArray(b?.floors) ? b.floors : []));
  const seats = floors.reduce((n, f) => n + (Array.isArray(f?.layout?.seats) ? f.layout.seats.length : 0), 0);
  return { document: doc as LayoutDocument, buildings: doc.buildings.length, floors: floors.length, seats };
}
