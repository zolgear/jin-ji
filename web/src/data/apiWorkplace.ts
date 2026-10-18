// 建物・フロアモードの REST API（設計書 docs/design/building-floor-mode.md §6.1 のパス）。
// OpenAPI（schema.gen.ts）の再生成の前なので、型付きのクライアントではなく、同じ規則（同一オリジン・Accept・
// Cookie のセッション・Problem Details のエラー）の小さな送信関数で呼ぶ。再生成の後は client.get に置き換える。
// 今日の所在の応答は Cache-Control: no-store（API 側）。提供元が無い導入先では 404 を返す。
import { buildUrl, toApiError, type ApiClientOptions } from "../api/client";
import { ApiError } from "./types";
import type {
  Building,
  FloorLayout,
  FloorOccupant,
  FloorPresence,
  WorkplaceAdapter,
} from "./workplace";

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
      const r = await get<{ items: Building[] }>("/api/v1/sites/{id}/buildings", { id: siteId }, { as_of: asOfParam(asOf) });
      return r.items;
    },
    async getBuilding(id, asOf) {
      return get<Building>("/api/v1/buildings/{id}", { id }, { as_of: asOfParam(asOf) });
    },
    async getFloorLayout(floorId, asOf) {
      return get<FloorLayout>("/api/v1/floors/{id}/layout", { id: floorId }, { as_of: asOfParam(asOf) });
    },
    async getFloorPresence(floorId, date) {
      try {
        return await get<FloorPresence>("/api/v1/floors/{id}/presence", { id: floorId }, { date });
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) return null;
        throw e;
      }
    },
    async listFloorOccupants(floorId, asOf) {
      const out: FloorOccupant[] = [];
      let cursor: string | null = null;
      for (let i = 0; i < OCCUPANT_MAX_PAGES; i++) {
        const r: { items: FloorOccupant[]; next_cursor: string | null } = await get("/api/v1/floors/{id}/occupants", { id: floorId }, {
          as_of: asOfParam(asOf),
          cursor: cursor ?? undefined,
          limit: String(OCCUPANT_PAGE),
        });
        out.push(...r.items);
        cursor = r.next_cursor;
        if (!cursor) break;
      }
      return out;
    },
  };
}
