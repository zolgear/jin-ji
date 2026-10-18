// 建物・フロアモードの REST API（設計書 docs/design/building-floor-mode.md §6.1 のパス）。
// OpenAPI（schema.gen.ts）の再生成の前なので、型付きのクライアントではなく、同じ規則（同一オリジン・Accept・
// Cookie のセッション・Problem Details のエラー）の小さな送信関数で呼ぶ。再生成の後は client.get に置き換える。
// 当日の所在の応答は Cache-Control: no-store（API 側）。取得の結果もキャッシュの寿命を短くする（features/floor の取得フック）。
import { buildUrl, createCsrfSender, toApiError, type ApiClient, type ApiClientOptions } from "../api/client";
import { ApiError } from "./types";
import type {
  Building,
  FloorLayout,
  FloorOccupant,
  FloorPresence,
  MyReservation,
  PresenceSettings,
  ReservationTarget,
  WorkplaceAdapter,
} from "./workplace";

/** 階の在籍者の 1 ページの上限（API の上限と同じ）。 */
const OCCUPANT_PAGE = 100;
/** 在籍者を読むページの上限（1 階 3,000 席まで。§2.3）。 */
const OCCUPANT_MAX_PAGES = 40;

type Query = Record<string, string | undefined>;

export function createApiWorkplace(client: ApiClient, options: ApiClientOptions, asOfParam: (asOf: string) => string | undefined): WorkplaceAdapter {
  const baseUrl = options.baseUrl ?? "";
  const getToken = options.getToken ?? (async () => null);
  const doFetch = options.fetch ?? ((input: RequestInfo | URL, init?: RequestInit) => fetch(input, init));
  const withCsrf = createCsrfSender(client);

  async function send<T>(method: string, path: string, pathParams: Record<string, string>, query: Query, body?: unknown, extra: Record<string, string> = {}): Promise<T> {
    const headers = new Headers(extra);
    headers.set("Accept", "application/json");
    const token = await getToken();
    if (token) headers.set("Authorization", `Bearer ${token}`);
    const init: RequestInit = { method, headers, credentials: "same-origin" };
    if (body !== undefined) {
      headers.set("Content-Type", "application/json");
      init.body = JSON.stringify(body);
    }
    const response = await doFetch(buildUrl(baseUrl, path, pathParams, query), init);
    if (!response.ok) throw await toApiError(response);
    if (response.status === 204 || response.headers.get("Content-Length") === "0") return undefined as T;
    return (await response.json()) as T;
  }
  const get = <T,>(path: string, pathParams: Record<string, string> = {}, query: Query = {}) => send<T>("GET", path, pathParams, query);
  /** 機能が無効な導入先では当日の所在のエンドポイントが 404 を返す。 */
  const orNull = async <T,>(load: () => Promise<T>): Promise<T | null> => {
    try {
      return await load();
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) return null;
      throw e;
    }
  };

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
    async getPresenceSettings() {
      const r = await orNull(() => get<{ settings: PresenceSettings }>("/api/v1/me/presence"));
      return r?.settings ?? null;
    },
    async getFloorPresence(floorId, date) {
      return orNull(() => get<FloorPresence>("/api/v1/floors/{id}/presence", { id: floorId }, { date }));
    },
    async listMyPresence() {
      const r = await orNull(() => get<{ items: MyReservation[] }>("/api/v1/me/presence"));
      return r?.items ?? [];
    },
    async reserve(date, target: ReservationTarget) {
      const body = "seatId" in target ? { floor_id: target.floorId, seat_id: target.seatId } : { floor_id: target.floorId, zone_key: target.zoneKey };
      return withCsrf((headers) => send<MyReservation>("PUT", "/api/v1/me/presence/{date}", { date }, {}, body, headers));
    },
    async cancel(date) {
      await withCsrf((headers) => send<void>("DELETE", "/api/v1/me/presence/{date}", { date }, {}, undefined, headers));
    },
  };
}
