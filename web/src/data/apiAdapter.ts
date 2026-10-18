// jin-ji の REST API（/api/v1）を使うアダプタ。
// API はすべての項目を明示的に返す（見えない項目・値の無い項目は null。見えるかどうかは visible_fields で区別する）ので、
// UI 側で値を補ったり、見えない項目を推測したりはしない。
import { buildUrl, createApiClient, createCsrfSender, type ApiClient, type ApiClientOptions } from "../api/client";
import type { Schemas } from "../api/schema.gen";
import { businessDateJst } from "../lib/date";
import { createApiWorkplace } from "./apiWorkplace";
import type { CorrectionAdapter } from "./corrections";
import type { DirectoryAdapter, Milestone, OrgStats, Person, PersonPage } from "./types";

/** API の上限（PersonSearch.MaxLimit・ids の件数・組織検索の limit）。 */
export const API_MAX_LIMIT = 100;

type Meta = Schemas["MetaResponse"];

const MILESTONE_KIND_LABEL: Record<string, string> = { org_change: "組織改編", mass_action: "一斉異動" };

/** 目盛り（GET /meta の milestones）を画面の目盛りにする。 */
export function toMilestones(meta: Pick<Meta, "milestones">): Milestone[] {
  return meta.milestones
    .map((m) => ({ date: m.date, label: m.kinds.map((k) => MILESTONE_KIND_LABEL[k] ?? k).join("・") }))
    .sort((a, b) => a.date.localeCompare(b.date));
}

/** GET /orgs/stats の応答を画面の集計にする。 */
export function toOrgStats(r: Schemas["OrgStatsResponse"]): OrgStats {
  const headcount: OrgStats["headcount"] = {};
  for (const u of r.units) {
    headcount[u.org_unit_id] = { primary: u.primary, concurrent: u.concurrent, secondment: u.secondment };
  }
  return {
    asOf: r.as_of,
    headcount,
    links: r.links.map((l) => ({ from: l.from_org_unit_id, to: l.to_org_unit_id, kind: l.kind, count: l.count })),
  };
}

export interface ApiAdapterOptions extends ApiClientOptions {
  now?: () => Date;
  /** GET /meta の応答（業務日付・目盛り・未来の時点を見られるか）。無ければ端末の時計の日本時間の今日・目盛りなし。 */
  meta?: Pick<Meta, "business_date" | "milestones" | "can_view_future"> | null;
}

export function createApiAdapter(options: ApiAdapterOptions = {}): DirectoryAdapter {
  const client = createApiClient(options);
  const meta = options.meta ?? null;
  const businessDate = meta?.business_date ?? businessDateJst(options.now?.() ?? new Date());
  // 今日の時点は as_of を省略する（端末の時計のずれで「未来の時点」として 403 にならないように）
  const asOfParam = (asOf: string) => (asOf === businessDate ? undefined : asOf);
  const page = (r: { items: Person[]; next_cursor: string | null }): PersonPage => ({ items: r.items, nextCursor: r.next_cursor });

  return {
    kind: "api",
    label: "jin-ji API",
    businessDate,
    milestones: meta ? toMilestones(meta) : [],
    canViewFuture: meta?.can_view_future ?? false,
    corrections: createApiCorrections(client, options.baseUrl ?? ""),
    workplace: createApiWorkplace(options, asOfParam),
    async getOrgTree(asOf) {
      return client.get("/api/v1/orgs/tree", { query: { as_of: asOfParam(asOf) } });
    },
    async getOrgStats(asOf) {
      // 見える人だけを数えた集計（人は返さない。集計の種別 org_stats で回数を制限される）
      return toOrgStats(await client.get("/api/v1/orgs/stats", { query: { as_of: asOfParam(asOf) } }));
    },
    async listOrgMembers(orgId, query) {
      const r = await client.get("/api/v1/orgs/{id}/members", {
        path: { id: orgId },
        query: {
          as_of: asOfParam(query.asOf),
          include_descendants: query.includeDescendants ? "true" : undefined,
          cursor: query.cursor ?? undefined,
          limit: String(Math.min(query.limit ?? 50, API_MAX_LIMIT)),
        },
      });
      return page(r);
    },
    async getPerson(id, asOf) {
      return client.get("/api/v1/people/{id}", { path: { id }, query: { as_of: asOfParam(asOf) } });
    },
    async getManagers(id, asOf) {
      const r = await client.get("/api/v1/people/{id}/manager", { path: { id }, query: { as_of: asOfParam(asOf) } });
      return r.managers;
    },
    async getPeopleByIds(ids, asOf) {
      const unique = [...new Set(ids)];
      const out: Person[] = [];
      // 100 件ずつ（API の上限）
      for (let i = 0; i < unique.length; i += API_MAX_LIMIT) {
        const chunk = unique.slice(i, i + API_MAX_LIMIT);
        const r = await client.get("/api/v1/people", { query: { ids: chunk.join(","), as_of: asOfParam(asOf) } });
        out.push(...r.items);
      }
      return out;
    },
    async searchOrgs(query, search) {
      const r = await client.get("/api/v1/orgs", {
        query: { query, as_of: asOfParam(search.asOf), limit: String(Math.min(search.limit ?? 30, API_MAX_LIMIT)) },
      });
      return { items: r.items, hasMore: r.has_more };
    },
    async searchPeople(query, search) {
      const r = await client.get("/api/v1/people", {
        query: {
          query,
          as_of: asOfParam(search.asOf),
          cursor: search.cursor ?? undefined,
          limit: String(Math.min(search.limit ?? 50, API_MAX_LIMIT)),
        },
      });
      return page(r);
    },
    async findPeople(q) {
      const f = q.filters;
      const r = await client.get("/api/v1/people", {
        query: {
          query: f.query || undefined,
          org: f.orgId ?? undefined,
          include_descendants: f.orgId && f.includeDescendants ? "true" : undefined,
          legal_entity: f.legalEntities,
          level: f.levels,
          assignment_kind: f.assignmentKinds,
          position: f.positions,
          employment_type: f.employmentTypes,
          job_type: f.jobTypes,
          site: f.sites,
          site_type: f.siteTypes,
          prefecture: f.prefectures,
          remote_work: f.remoteWork === true ? "true" : f.remoteWork === false ? "false" : undefined,
          hired_from: f.hiredFrom ?? undefined,
          hired_to: f.hiredTo ?? undefined,
          employment_status: f.employmentStatuses,
          account_status: f.accountStatuses,
          sort: q.sort === "id" ? undefined : q.sort,
          facets: q.facets && !q.cursor ? "true" : undefined,
          as_of: asOfParam(q.asOf),
          cursor: q.cursor ?? undefined,
          limit: String(Math.min(q.limit ?? 50, API_MAX_LIMIT)),
        },
      });
      return { items: r.items, nextCursor: r.next_cursor, total: r.total, facets: r.facets };
    },
    async listSites(q) {
      const r = await client.get("/api/v1/sites", {
        query: {
          query: q.query?.trim() || undefined,
          legal_entity: q.legalEntities,
          site_type: q.siteTypes,
          prefecture: q.prefectures,
          as_of: asOfParam(q.asOf),
        },
      });
      return r.items;
    },
    async getSite(id, asOf) {
      return client.get("/api/v1/sites/{id}", { path: { id }, query: { as_of: asOfParam(asOf) } });
    },
  };
}

/** 修正依頼（/api/v1/corrections）。状態を変える要求には CSRF のヘッダーを付ける。 */
export function createApiCorrections(client: ApiClient, baseUrl = ""): CorrectionAdapter {
  const withCsrf = createCsrfSender(client);
  return {
    async list(q) {
      const r = await client.get("/api/v1/corrections", {
        query: {
          status: q.status,
          person_id: q.personId ?? undefined,
          cursor: q.cursor ?? undefined,
          limit: String(Math.min(q.limit ?? 50, API_MAX_LIMIT)),
        },
      });
      return { items: r.items, people: r.people, nextCursor: r.next_cursor };
    },
    async summary() {
      return client.get("/api/v1/corrections/summary");
    },
    async create(input) {
      return withCsrf((headers) =>
        client.post(
          "/api/v1/corrections",
          { body: { person_id: input.personId, field: input.field, reason: input.reason, suggested_value: input.suggestedValue || null } },
          headers,
        ),
      );
    },
    async close(id, input) {
      return withCsrf((headers) =>
        client.post("/api/v1/corrections/{correctionId}/close", { path: { correctionId: id }, body: { status: input.status, note: input.note || null } }, headers),
      );
    },
    async report(status) {
      // Cookie のセッション（SameSite=Lax・同一オリジン）なので、リンクの GET でそのまま落とせる。ファイル名は Content-Disposition に任せる
      return { href: buildUrl(baseUrl, "/api/v1/corrections/report.csv", {}, { status }), fileName: null };
    },
  };
}

/**
 * GET /meta を読んでからアダプタを作る。読めない（認証の未実装で 401 など）ときは、
 * 端末の時計の日本時間の今日を業務日付とし、目盛りなしで続ける（各画面の取得がそれぞれエラーを示す）。
 */
export async function loadApiAdapter(options: ApiClientOptions = {}): Promise<DirectoryAdapter> {
  let meta: Meta | null = null;
  try {
    meta = await createApiClient(options).get("/api/v1/meta");
  } catch {
    meta = null;
  }
  return createApiAdapter({ ...options, meta });
}
