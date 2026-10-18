import { describe, expect, it } from "vitest";
import { buildUrl } from "../api/client";
import { createApiAdapter, loadApiAdapter } from "./apiAdapter";
import { ApiError } from "./types";

function fakeFetch(responses: Record<string, { status: number; body: unknown }>) {
  const calls: Array<{ url: string; headers: Record<string, string> }> = [];
  const fn = async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    calls.push({ url, headers: Object.fromEntries(new Headers(init?.headers)) });
    const r = responses[url] ?? { status: 404, body: { title: "Not Found", type: "urn:jinji:problem:not-found" } };
    return new Response(JSON.stringify(r.body), { status: r.status, headers: { "Content-Type": "application/json" } });
  };
  return { fn: fn as typeof fetch, calls };
}

// 2026-10-12T00:00:00+09:00
const now = () => new Date("2026-10-11T15:00:00Z");

describe("createApiAdapter", () => {
  it("業務日付は日本時間。今日の時点は as_of を省略する", async () => {
    const { fn, calls } = fakeFetch({ "/api/v1/orgs/tree": { status: 200, body: { as_of: "2026-10-12", units: [] } } });
    const adapter = createApiAdapter({ fetch: fn, now });
    expect(adapter.businessDate).toBe("2026-10-12");
    await adapter.getOrgTree("2026-10-12");
    expect(calls[0]!.url).toBe("/api/v1/orgs/tree");
  });

  it("過去の時点は as_of を付け、トークンがあれば Bearer で送る", async () => {
    const { fn, calls } = fakeFetch({
      "/api/v1/people?query=%E5%B1%B1%E6%9C%AC&as_of=2026-04-01&limit=50": { status: 200, body: { items: [], next_cursor: null } },
    });
    const adapter = createApiAdapter({ fetch: fn, now, getToken: async () => "t0ken" });
    await adapter.searchPeople("山本", { asOf: "2026-04-01" });
    expect(calls[0]!.headers.authorization).toBe("Bearer t0ken");
  });

  it("人の検索: 複数の値は同じ名前を繰り返し、件数は最初のページだけ求める。既定の並び（id）・空の条件は送らない", async () => {
    const body = { items: [], next_cursor: null, total: 0, facets: null };
    const { fn, calls } = fakeFetch({
      "/api/v1/people?query=%E3%82%84%E3%81%BE&org=o1&include_descendants=true&level=section&site=s1&site=s2&site_type=factory&prefecture=13&remote_work=false&hired_from=2016-04-01&sort=kana&facets=true&limit=48":
        { status: 200, body },
      "/api/v1/people?cursor=c1&limit=48": { status: 200, body: { ...body, total: null } },
    });
    const adapter = createApiAdapter({ fetch: fn, now });
    const page = await adapter.findPeople({
      asOf: "2026-10-12",
      filters: { query: "やま", orgId: "o1", includeDescendants: true, levels: ["section"], sites: ["s1", "s2"], siteTypes: ["factory"], prefectures: ["13"], remoteWork: false, hiredFrom: "2016-04-01", positions: [] },
      sort: "kana",
      facets: true,
      limit: 48,
    });
    expect(page.total).toBe(0);
    await adapter.findPeople({ asOf: "2026-10-12", filters: {}, sort: "id", facets: true, cursor: "c1", limit: 48 });
    expect(calls).toHaveLength(2);
  });

  it("拠点ごと・リモートワークの並びは sort に渡し、リモートワークの true も送る", async () => {
    const body = { items: [], next_cursor: null, total: null, facets: null };
    const { fn, calls } = fakeFetch({
      "/api/v1/people?remote_work=true&sort=site&limit=48": { status: 200, body },
      "/api/v1/people?sort=remote_work&limit=48": { status: 200, body },
    });
    const adapter = createApiAdapter({ fetch: fn, now });
    await adapter.findPeople({ asOf: "2026-10-12", filters: { remoteWork: true }, sort: "site", facets: false, limit: 48 });
    await adapter.findPeople({ asOf: "2026-10-12", filters: { remoteWork: null }, sort: "remote_work", facets: false, limit: 48 });
    expect(calls.map((c) => c.url)).toEqual(["/api/v1/people?remote_work=true&sort=site&limit=48", "/api/v1/people?sort=remote_work&limit=48"]);
  });

  it("拠点の一覧・詳細: 条件は同じ名前を繰り返し、今日の時点は as_of を省略する", async () => {
    const site = {
      id: "s1",
      legal_entity: { id: "le1", code: "P", name: "株式会社サンプル" },
      code: "S010",
      name: "東京本社",
      type: "head_office",
      prefecture_code: "13",
      prefecture_name: "東京都",
      location: "千代田区丸の内9-9-1",
      member_count: 10,
      remote_work_count: 2,
      org_units: [],
    };
    const { fn } = fakeFetch({
      "/api/v1/sites?query=%E6%9D%B1%E4%BA%AC&site_type=head_office&site_type=branch&prefecture=13": { status: 200, body: { as_of: "2026-10-12", items: [site] } },
      "/api/v1/sites/s1?as_of=2026-04-01": { status: 200, body: site },
    });
    const adapter = createApiAdapter({ fetch: fn, now });
    const list = await adapter.listSites({ asOf: "2026-10-12", query: " 東京 ", siteTypes: ["head_office", "branch"], prefectures: ["13"] });
    expect(list).toEqual([site]);
    expect((await adapter.getSite("s1", "2026-04-01")).remote_work_count).toBe(2);
  });

  it("API の null をそのまま渡す（値を補ったり推測したりしない）", async () => {
    const id = "0192f000-0000-7000-8000-000000000001";
    const { fn } = fakeFetch({
      [`/api/v1/people/${id}`]: { status: 200, body: { id, visible_fields: ["display_name"], display_name: "山本 蓮", work_email: null, assignments: null } },
    });
    const p = await createApiAdapter({ fetch: fn, now }).getPerson(id, "2026-10-12");
    expect(p.work_email).toBeNull();
    expect(p.assignments).toBeNull();
  });

  it("meta の業務日付・目盛り・未来の時点の可否を使う。読めなければ端末の日本時間の今日", async () => {
    const { fn } = fakeFetch({
      "/api/v1/meta": {
        status: 200,
        body: {
          business_date: "2026-10-11",
          time_zone: "Asia/Tokyo",
          can_view_future: false,
          legal_entities: [],
          default_levels: [],
          milestones: [
            { date: "2026-04-01", kinds: ["org_change", "mass_action"] },
            { date: "2025-10-01", kinds: ["mass_action"] },
          ],
        },
      },
    });
    const a = await loadApiAdapter({ fetch: fn });
    expect(a.businessDate).toBe("2026-10-11");
    expect(a.canViewFuture).toBe(false);
    expect(a.milestones).toEqual([
      { date: "2025-10-01", label: "一斉異動" },
      { date: "2026-04-01", label: "組織改編・一斉異動" },
    ]);
    const fallback = await loadApiAdapter({ fetch: fakeFetch({ "/api/v1/meta": { status: 401, body: { title: "Unauthorized" } } }).fn });
    expect(fallback.milestones).toEqual([]);
  });

  it("組織の集計を画面の形にする", async () => {
    const { fn } = fakeFetch({
      "/api/v1/orgs/stats": {
        status: 200,
        body: {
          as_of: "2026-10-12",
          units: [{ org_unit_id: "A", primary: 3, primary_total: 5, concurrent: 1, secondment: 0 }],
          links: [{ from_org_unit_id: "B", to_org_unit_id: "A", kind: "concurrent", count: 1 }],
        },
      },
    });
    const stats = await createApiAdapter({ fetch: fn, now }).getOrgStats("2026-10-12");
    expect(stats?.headcount.A).toEqual({ primary: 3, concurrent: 1, secondment: 0 });
    expect(stats?.links).toEqual([{ from: "B", to: "A", kind: "concurrent", count: 1 }]);
  });

  it("人の一括取得は重複を除いて 100 件ずつ", async () => {
    const ids = Array.from({ length: 150 }, (_, i) => `p${i}`);
    const calls: string[] = [];
    const fn = (async (input: RequestInfo | URL) => {
      const url = new URL(String(input), "http://x");
      calls.push(url.searchParams.get("ids") ?? "");
      const items = (url.searchParams.get("ids") ?? "").split(",").map((id) => ({ id }));
      return new Response(JSON.stringify({ items, next_cursor: null }), { status: 200 });
    }) as typeof fetch;
    const people = await createApiAdapter({ fetch: fn, now }).getPeopleByIds([...ids, "p0"], "2026-10-12");
    expect(calls).toHaveLength(2);
    expect(calls[0]!.split(",")).toHaveLength(100);
    expect(people).toHaveLength(150);
  });

  it("組織の検索", async () => {
    const { fn } = fakeFetch({
      "/api/v1/orgs?query=%E5%96%B6%E6%A5%AD&limit=30": { status: 200, body: { as_of: "2026-10-12", items: [], has_more: true } },
    });
    expect(await createApiAdapter({ fetch: fn, now }).searchOrgs("営業", { asOf: "2026-10-12" })).toEqual({ items: [], hasMore: true });
  });

  it("Problem Details を ApiError にする", async () => {
    const { fn } = fakeFetch({ "/api/v1/orgs/x": { status: 404, body: { title: "Not Found", type: "urn:jinji:problem:not-found" } } });
    const adapter = createApiAdapter({ fetch: fn, now });
    await expect(adapter.getOrgTree("2026-01-01")).rejects.toBeInstanceOf(ApiError);
  });

});

describe("修正依頼（API アダプタ）", () => {
  function recorder(handler: (method: string, url: string) => { status: number; body: unknown }) {
    const calls: Array<{ method: string; url: string; headers: Record<string, string>; body: unknown }> = [];
    const fn = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      const url = String(input);
      calls.push({ method, url, headers: Object.fromEntries(new Headers(init?.headers)), body: init?.body ? JSON.parse(String(init.body)) : null });
      const r = handler(method, url);
      return new Response(JSON.stringify(r.body), { status: r.status, headers: { "Content-Type": "application/json" } });
    }) as typeof fetch;
    return { fn, calls };
  }

  it("一覧は status・personId・limit を付け、next_cursor を渡す", async () => {
    const { fn, calls } = recorder(() => ({ status: 200, body: { items: [], people: [], next_cursor: "c2" } }));
    const page = await createApiAdapter({ fetch: fn, now }).corrections.list({ status: "all", personId: "p1", cursor: "c1" });
    expect(calls[0]!.url).toBe("/api/v1/corrections?status=all&person_id=p1&cursor=c1&limit=50");
    expect(page.nextCursor).toBe("c2");
  });

  it("起票と閉じるには CSRF のヘッダーを付け、CSRF で拒否されたら 1 回だけ取り直す", async () => {
    let tokens = 0;
    let rejected = false;
    const { fn, calls } = recorder((method, url) => {
      if (url === "/auth/csrf") return { status: 200, body: { token: `t${++tokens}`, header_name: "X-JinJi-CSRF" } };
      if (method === "POST" && !rejected) {
        rejected = true;
        return { status: 403, body: { title: "要求を確認できません", type: "urn:jinji:problem:csrf" } };
      }
      if (url.endsWith("/close")) return { status: 200, body: { id: "c1", status: "resolved" } };
      return { status: 200, body: { outcome: "created", correction: { id: "c1" } } };
    });
    const corrections = createApiAdapter({ fetch: fn, now }).corrections;
    const r = await corrections.create({ personId: "p1", field: "site", reason: "違います", suggestedValue: "" });
    expect(r.outcome).toBe("created");
    const posts = calls.filter((c) => c.method === "POST");
    expect(posts.map((c) => c.headers["x-jinji-csrf"])).toEqual(["t1", "t2"]);
    expect(posts[1]!.body).toEqual({ person_id: "p1", field: "site", reason: "違います", suggested_value: null });
    await corrections.close("c1", { status: "resolved", note: "直しました" });
    const close = calls.at(-1)!;
    expect(close.url).toBe("/api/v1/corrections/c1/close");
    expect(close.headers["x-jinji-csrf"]).toBe("t2");
    expect(close.body).toEqual({ status: "resolved", note: "直しました" });
  });

  it("CSV は同一オリジンのリンク（ファイル名は Content-Disposition に任せる）", async () => {
    const { fn, calls } = recorder(() => ({ status: 500, body: {} }));
    const file = await createApiAdapter({ fetch: fn, now }).corrections.report("closed");
    expect(file).toEqual({ href: "/api/v1/corrections/report.csv?status=closed", fileName: null });
    expect(calls).toHaveLength(0);
  });
});

describe("buildUrl", () => {
  it("パスの引数をエンコードし、空の引数は付けない", () => {
    expect(buildUrl("", "/api/v1/people/{id}", { id: "a/b" }, { as_of: undefined, q: "" })).toBe("/api/v1/people/a%2Fb");
  });
});

describe("建物・フロアモードの API（設計書 §6.1 のパス）", () => {
  it("建物の一覧は as_of を付け、配置図・在籍者は階ごと。在籍者はページを続けて読む", async () => {
    const { fn, calls } = fakeFetch({
      "/api/v1/sites/s1/buildings?as_of=2026-04-01": { status: 200, body: { items: [] } },
      "/api/v1/floors/f1/layout": { status: 200, body: { floor_id: "f1" } },
      "/api/v1/floors/f1/occupants?limit=100": { status: 200, body: { items: [{ person_id: "p1" }], next_cursor: "c2" } },
      "/api/v1/floors/f1/occupants?cursor=c2&limit=100": { status: 200, body: { items: [{ person_id: "p2" }], next_cursor: null } },
    });
    const adapter = createApiAdapter({ fetch: fn, now });
    expect(await adapter.workplace.listBuildings("s1", "2026-04-01")).toEqual([]);
    await adapter.workplace.getFloorLayout("f1", "2026-10-12");
    const occupants = await adapter.workplace.listFloorOccupants("f1", "2026-10-12");
    expect(occupants.map((o) => o.person_id)).toEqual(["p1", "p2"]);
    expect(calls).toHaveLength(4);
  });

  it("今日の所在の提供元が無い導入先（404）では、階の所在は null", async () => {
    const { fn } = fakeFetch({});
    const adapter = createApiAdapter({ fetch: fn, now });
    expect(await adapter.workplace.getFloorPresence("f1", "2026-10-12")).toBeNull();
  });
});
