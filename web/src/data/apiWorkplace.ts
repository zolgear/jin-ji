// 建物・フロアモードの REST API（設計書 docs/design/building-floor-mode.md §6.1 のパス）。
// 応答の型は生成型（schema.gen.ts）。画面の内部モデル（配置図・在籍者・今日の所在。data/workplace.ts）への変換はここで行う。
// 送信は既存のクライアントと同じ規則（同一オリジン・Accept・Cookie のセッション・Problem Details のエラー）。
// 今日の所在の応答は Cache-Control: no-store（API 側）。提供元が無い導入先では 404（presence-unavailable）を返す。
import { buildUrl, toApiError, type ApiClientOptions } from "../api/client";
import type { Schemas } from "../api/schema.gen";
import { ApiError } from "./types";
import type {
  FloorLayout,
  FloorOccupant,
  FloorPresence,
  PointMm,
  WorkplaceAdapter,
} from "./workplace";

/** 提供元が無い導入先の今日の所在（404 の problem type）。 */
export const PRESENCE_UNAVAILABLE = "urn:jinji:problem:presence-unavailable";

const toPoints = (polygon: number[][]): PointMm[] => polygon.map((p) => [p[0]!, p[1]!]);

/** GET /floors/{id}/layout の応答を画面の配置図にする。配置図が無い階は空の配置図。 */
export function toFloorLayout(r: Schemas["FloorLayoutResponse"]): FloorLayout {
  const l = r.layout;
  return {
    floor_id: r.floor.id,
    as_of: r.as_of,
    layout_id: l?.id ?? "",
    valid_from: l?.valid_from ?? "",
    outline: l ? toPoints(l.outline) : [],
    fixtures: (l?.fixtures ?? []).map((f) => ({ kind: f.kind, name: f.name, polygon: toPoints(f.polygon) })),
    grid_mm: l?.grid_mm ?? 1000,
    zones: (l?.zones ?? []).map((z) => ({ zone_key: z.zone_key, name: z.name, kind: z.kind, polygon: toPoints(z.polygon) })),
    seats: (l?.seats ?? []).map(({ kind, ...seat }) => ({ ...seat, seat_kind: kind })),
  };
}

/** 人の応答から、人の ID・表示名・主務の組織（画面の色分けに使う）を取り出す。 */
function whoOf(person: Schemas["PersonResponse"]) {
  const primary = person.assignments?.find((a) => a.type === "primary");
  return {
    person_id: person.id,
    display_name: person.display_name,
    org_unit_id: primary?.org_unit_id ?? null,
    org_unit_name: primary?.org_unit_name ?? null,
  };
}

/** 在籍者 1 件（person は人の応答の形）を画面の在籍者にする。 */
export function toOccupant(o: Schemas["FloorOccupantResponse"]): FloorOccupant {
  return { ...whoOf(o.person), seat_id: o.seat_id, zone_key: o.zone_key, role: o.role };
}

/** GET /floors/{id}/presence の応答を画面の今日の所在にする（使えない席は seats の status から導く）。 */
export function toFloorPresence(r: Schemas["FloorPresenceResponse"]): FloorPresence {
  return {
    floor_id: r.floor.id,
    date: r.date,
    unavailable_seat_ids: r.seats.filter((s) => s.status === "unavailable").map((s) => s.seat_id),
    items: r.items.map((i) => ({ ...whoOf(i.person), seat_id: i.seat_id, zone_key: i.zone_key })),
  };
}

/** 階の在籍者の 1 ページの上限（API の上限と同じ）。 */
const OCCUPANT_PAGE = 100;
/** 在籍者を読むページの上限（1 階 3,000 席まで。§2.3）。 */
const OCCUPANT_MAX_PAGES = 40;

type Query = Record<string, string | undefined>;

export function createApiWorkplace(options: ApiClientOptions, asOfParam: (asOf: string) => string | undefined): WorkplaceAdapter {
  const baseUrl = options.baseUrl ?? "";
  const getToken = options.getToken ?? (async () => null);
  const doFetch = options.fetch ?? ((input: RequestInfo | URL, init?: RequestInit) => fetch(input, init));

  async function get<T>(path: string, pathParams: Record<string, string> = {}, query: Query = {}): Promise<T> {
    const headers = new Headers({ Accept: "application/json" });
    const token = await getToken();
    if (token) headers.set("Authorization", `Bearer ${token}`);
    const response = await doFetch(buildUrl(baseUrl, path, pathParams, query), { method: "GET", headers, credentials: "same-origin" });
    if (!response.ok) throw await toApiError(response);
    return (await response.json()) as T;
  }

  return {
    async listBuildings(siteId, asOf) {
      const r = await get<Schemas["BuildingListResponse"]>("/api/v1/sites/{id}/buildings", { id: siteId }, { as_of: asOfParam(asOf) });
      return r.items;
    },
    async getBuilding(id, asOf) {
      const r = await get<Schemas["BuildingDetailResponse"]>("/api/v1/buildings/{id}", { id }, { as_of: asOfParam(asOf) });
      return r.building;
    },
    async getFloorLayout(floorId, asOf) {
      return toFloorLayout(await get<Schemas["FloorLayoutResponse"]>("/api/v1/floors/{id}/layout", { id: floorId }, { as_of: asOfParam(asOf) }));
    },
    // 今日だけ（as_of は受け付けない）。date は呼び出し側の「今日」で、API には送らない
    async getFloorPresence(floorId) {
      try {
        return toFloorPresence(await get<Schemas["FloorPresenceResponse"]>("/api/v1/floors/{id}/presence", { id: floorId }));
      } catch (e) {
        // 提供元が無い導入先は所在の重ねを出さない（階が無いなどの 404 は通常のエラー）
        if (e instanceof ApiError && e.status === 404 && e.problemType === PRESENCE_UNAVAILABLE) return null;
        throw e;
      }
    },
    async listFloorOccupants(floorId, asOf) {
      const out: FloorOccupant[] = [];
      let cursor: string | null = null;
      for (let i = 0; i < OCCUPANT_MAX_PAGES; i++) {
        const r: Schemas["FloorOccupantListResponse"] = await get("/api/v1/floors/{id}/occupants", { id: floorId }, {
          as_of: asOfParam(asOf),
          cursor: cursor ?? undefined,
          limit: String(OCCUPANT_PAGE),
        });
        out.push(...r.items.map(toOccupant));
        cursor = r.next_cursor;
        if (!cursor) break;
      }
      return out;
    },
  };
}
