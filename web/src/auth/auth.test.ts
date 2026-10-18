import { describe, expect, it } from "vitest";
import { ApiError } from "../data/types";
import { createDemoAuthAdapter } from "../demo/demoAuthAdapter";
import { createApiAuthAdapter, CSRF_HEADER, PROBLEM } from "./apiAuthAdapter";
import { FIELD_LABELS, MCP_TOOLS } from "./labels";
import { authErrorMessage, loginRedirectError, passwordRules, waitPhrase } from "./messages";
import { authPath } from "./paths";
import { loginUrl, providerLoginUrl, safeReturnUrl } from "./returnUrl";
import { canUseAdmin, isHrAdmin, type Delegation, type Session } from "./types";
import { delegationClientName } from "./pages/AccountPage";
// サーバーの名前の定義（C#）。項目・ツールの名前の突き合わせに使う
import personFieldsCs from "../../../src/JinJi.Policy/PersonFields.cs?raw";
import mcpToolNamesCs from "../../../src/JinJi.Application/Access/McpToolNames.cs?raw";
import { fromBase64Url, parseCreationOptions, parseRequestOptions, toBase64Url } from "./webauthn";

type Call = { method: string; url: string; headers: Record<string, string>; body: unknown };

/** URL とメソッドごとに応答を返す fetch。応答は配列なら順に返す。 */
function fakeFetch(routes: Record<string, Array<{ status: number; body?: unknown; headers?: Record<string, string> }>>) {
  const calls: Call[] = [];
  const fn = async (input: RequestInfo | URL, init?: RequestInit) => {
    const method = init?.method ?? "GET";
    const url = String(input);
    calls.push({
      method,
      url,
      headers: Object.fromEntries(new Headers(init?.headers)),
      body: typeof init?.body === "string" ? JSON.parse(init.body) : undefined,
    });
    const queue = routes[`${method} ${url}`];
    const r = queue && queue.length > 1 ? queue.shift()! : queue?.[0];
    if (!r) return new Response(JSON.stringify({ title: "Not Found" }), { status: 404 });
    return new Response(r.status === 204 ? null : JSON.stringify(r.body ?? {}), {
      status: r.status,
      headers: { "Content-Type": "application/json", ...r.headers },
    });
  };
  return { fn: fn as typeof fetch, calls };
}

const session: Session = {
  kind: "person",
  person_id: "0192f000-0000-7000-8000-000000000001",
  account_id: null,
  session_id: "0192f000-0000-7000-8000-000000000002",
  provider: "entra",
  methods: ["fed"],
  roles: [],
};

describe("safeReturnUrl", () => {
  it("同じオリジンのパスだけを通す（サーバーの SafeReturnUrl と同じ）", () => {
    expect(safeReturnUrl("/consent?request=jjar_abc")).toBe("/consent?request=jjar_abc");
    expect(safeReturnUrl("/#/org-chart?as_of=2026-04-01")).toBe("/#/org-chart?as_of=2026-04-01");
    for (const bad of [null, "", "https://evil.example", "//evil.example", "/\\evil", "/a\\b", "/auth/login/entra", "/x\u0000", "a/b", "/" + "a".repeat(2048)]) {
      expect(safeReturnUrl(bad)).toBe("/");
    }
  });

  it("ログイン画面自身には戻らない", () => {
    expect(safeReturnUrl("/login?return_url=/")).toBe("/");
    expect(safeReturnUrl("/login")).toBe("/");
    expect(safeReturnUrl("/loginx")).toBe("/loginx");
  });

  it("ログイン画面と外部 IdP の URL に戻り先を付ける", () => {
    expect(loginUrl("/consent?request=a b")).toBe("/login?return_url=%2Fconsent%3Frequest%3Da%20b");
    expect(loginUrl("/")).toBe("/login");
    expect(providerLoginUrl("/auth/login/entra", "/admin")).toBe("/auth/login/entra?return_url=%2Fadmin");
    expect(providerLoginUrl("/auth/login/entra", "https://evil.example")).toBe("/auth/login/entra?return_url=%2F");
  });
});

describe("authPath", () => {
  it("認証の画面のパスだけを認める（末尾の / は無視）", () => {
    expect(authPath("/login")).toBe("/login");
    expect(authPath("/account-recovery/")).toBe("/account-recovery");
    expect(authPath("/")).toBeNull();
    expect(authPath("/login/extra")).toBeNull();
  });
});

describe("createApiAuthAdapter", () => {
  it("状態を変える要求に CSRF のトークンを付け、ログインの応答のトークンに差し替える", async () => {
    const { fn, calls } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t1", header_name: CSRF_HEADER } }],
      "POST /auth/login/password": [{ status: 200, body: { status: "signed_in", session, csrf_token: "t2" } }],
      "POST /auth/logout": [{ status: 204 }],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    const result = await auth.loginPassword("yamada", "pw");
    expect(result.status).toBe("signed_in");
    await auth.logout();
    const login = calls.find((c) => c.url === "/auth/login/password")!;
    expect(login.headers["x-jinji-csrf"]).toBe("t1");
    expect(login.body).toEqual({ user_name: "yamada", password: "pw" });
    expect(calls.find((c) => c.url === "/auth/logout")!.headers["x-jinji-csrf"]).toBe("t2");
    expect(calls.filter((c) => c.url === "/auth/csrf")).toHaveLength(1);
  });

  it("CSRF で拒否されたら 1 回だけトークンを取り直して再送する", async () => {
    const { fn, calls } = fakeFetch({
      "GET /auth/csrf": [
        { status: 200, body: { token: "old", header_name: CSRF_HEADER } },
        { status: 200, body: { token: "new", header_name: CSRF_HEADER } },
      ],
      "POST /oauth/requests/r1/decision": [
        { status: 403, body: { title: "要求を確認できません", type: PROBLEM.csrf } },
        { status: 200, body: { redirect_to: "http://127.0.0.1:5000/cb?code=x" } },
      ],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    expect(await auth.decideConsent("r1", true)).toBe("http://127.0.0.1:5000/cb?code=x");
    const posts = calls.filter((c) => c.method === "POST");
    expect(posts.map((c) => c.headers["x-jinji-csrf"])).toEqual(["old", "new"]);
    expect(posts[1]!.body).toEqual({ approve: true });
  });

  it("ログインしていなければ me は null、ログインが使えない環境では providers は null", async () => {
    const { fn } = fakeFetch({
      "GET /auth/me": [{ status: 401, body: { type: PROBLEM.loginFailed } }],
      "GET /auth/csrf": [{ status: 404, body: { type: PROBLEM.unavailable } }],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    expect(await auth.me()).toBeNull();
    expect(await auth.providers()).toBeNull();
  });

  it("429 の Retry-After を読み、失敗は ApiError で返す", async () => {
    const { fn } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t", header_name: CSRF_HEADER } }],
      "POST /auth/login/password": [{ status: 429, body: { title: "試行が多すぎます" }, headers: { "Retry-After": "120" } }],
    });
    const error = await createApiAuthAdapter({ fetch: fn })
      .loginPassword("a", "b")
      .catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).retryAfter).toBe(120);
  });

  it("パスキーは options → ブラウザの操作 → 検証の順に呼ぶ", async () => {
    const { fn, calls } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t", header_name: CSRF_HEADER } }],
      "POST /auth/login/passkey/options": [{ status: 200, body: { challenge: "AAEC", rpId: "example.com" } }],
      "POST /auth/login/passkey": [{ status: 200, body: { status: "signed_in", session, csrf_token: "t2" } }],
    });
    const seen: unknown[] = [];
    const auth = createApiAuthAdapter({
      fetch: fn,
      passkeys: {
        create: async () => ({}),
        get: async (options) => {
          seen.push(options);
          return { id: "cred", type: "public-key" };
        },
      },
    });
    await auth.loginPasskey();
    expect(seen).toEqual([{ challenge: "AAEC", rpId: "example.com" }]);
    expect(calls.find((c) => c.url === "/auth/login/passkey")!.body).toEqual({ credential: { id: "cred", type: "public-key" }, name: null });
  });

  it("クライアントの MCP ツールの変更は、変えない項目を null で送る", async () => {
    const { fn, calls } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t", header_name: CSRF_HEADER } }],
      "POST /auth/admin/oauth/clients/c1/update": [{ status: 200, body: {} }],
      "POST /auth/admin/oauth/clients/c1/suspend": [{ status: 200, body: {} }],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    await auth.updateClient("c1", { mcpTools: ["search_people"] });
    await auth.setClientStatus("c1", "suspend");
    const body = calls.find((c) => c.url.endsWith("/update"))!.body as Record<string, unknown>;
    expect(body.mcp_tools).toEqual(["search_people"]);
    expect(body.scopes).toBeNull();
    expect(body.fields).toBeNull();
    expect(calls.some((c) => c.url === "/auth/admin/oauth/clients/c1/suspend")).toBe(true);
  });
});

describe("先行提供（API アダプタ）", () => {
  it("一覧は GET（client で絞る）、起案・承認・却下は CSRF のトークンを付けた POST", async () => {
    const { fn, calls } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t", header_name: CSRF_HEADER } }],
      "GET /auth/admin/oauth/disclosure-leads?client=c1": [{ status: 200, body: [] }],
      "POST /auth/admin/oauth/clients/c1/disclosure-leads": [{ status: 200, body: {} }],
      "POST /auth/admin/oauth/disclosure-leads/l1/approve": [{ status: 200, body: {} }],
      "POST /auth/admin/oauth/disclosure-leads/l1/reject": [{ status: 200, body: {} }],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    expect(await auth.listDisclosureLeads("c1")).toEqual([]);
    await auth.proposeDisclosureLead("c1", 14, "理由");
    await auth.decideDisclosureLead("l1", true);
    await auth.decideDisclosureLead("l1", false);
    expect(calls.find((c) => c.method === "GET" && c.url.includes("disclosure-leads"))!.headers["x-jinji-csrf"]).toBeUndefined();
    expect(calls.find((c) => c.url.endsWith("/clients/c1/disclosure-leads"))!.body).toEqual({ lead_days: 14, note: "理由" });
    const posts = calls.filter((c) => c.method === "POST");
    expect(posts.map((c) => c.url)).toEqual([
      "/auth/admin/oauth/clients/c1/disclosure-leads",
      "/auth/admin/oauth/disclosure-leads/l1/approve",
      "/auth/admin/oauth/disclosure-leads/l1/reject",
    ]);
    expect(posts.every((c) => c.headers["x-jinji-csrf"] === "t")).toBe(true);
  });
});

describe("先行提供（デモのアダプタ）", () => {
  const setup = async () => {
    const auth = createDemoAuthAdapter();
    const clients = await auth.listClients();
    const attendance = clients.find((c) => c.grant_types.includes("client_credentials"))!;
    return { auth, attendance };
  };

  it("別の人事管理者の起案は承認でき、承認するとクライアントの日数が変わる", async () => {
    const { auth, attendance } = await setup();
    expect(attendance.disclosure_lead_days).toBe(0);
    const lead = (await auth.listDisclosureLeads(attendance.id))[0]!;
    expect(lead.status).toBe("proposed");
    const decided = await auth.decideDisclosureLead(lead.id, true);
    expect(decided.status).toBe("approved");
    const after = (await auth.listClients()).find((c) => c.id === attendance.id)!;
    expect(after.disclosure_lead_days).toBe(lead.lead_days);
  });

  it("自分の起案は承認も却下もできない（403）。承認待ちがあるうちの再起案は 409", async () => {
    const { auth, attendance } = await setup();
    const other = (await auth.listDisclosureLeads(attendance.id))[0]!;
    await auth.decideDisclosureLead(other.id, false);
    const mine = await auth.proposeDisclosureLead(attendance.id, 30, null);
    for (const approve of [true, false]) {
      const e = await auth.decideDisclosureLead(mine.id, approve).catch((x: unknown) => x);
      expect((e as ApiError).status).toBe(403);
    }
    const dup = await auth.proposeDisclosureLead(attendance.id, 7, null).catch((x: unknown) => x);
    expect((dup as ApiError).status).toBe(409);
  });

  it("0〜90 の範囲外・現在値と同じ・社内システム以外は拒否する", async () => {
    const { auth, attendance } = await setup();
    const other = (await auth.listDisclosureLeads(attendance.id))[0]!;
    await auth.decideDisclosureLead(other.id, false);
    for (const days of [-1, 91, 1.5, 0]) {
      const e = await auth.proposeDisclosureLead(attendance.id, days, null).catch((x: unknown) => x);
      expect((e as ApiError).status).toBe(400);
    }
    const agent = (await auth.listClients()).find((c) => !c.grant_types.includes("client_credentials"))!;
    const e = await auth.proposeDisclosureLead(agent.id, 7, null).catch((x: unknown) => x);
    expect((e as ApiError).status).toBe(400);
  });
});

describe("Webhook（API アダプタ）", () => {
  it("一覧・送信の一覧は GET、登録・変更・鍵のローテーション・再送・ping は CSRF のトークンを付けた POST。指定しない項目は null で送る", async () => {
    const { fn, calls } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t", header_name: CSRF_HEADER } }],
      "GET /auth/admin/oauth/webhooks": [{ status: 200, body: [] }],
      "GET /auth/admin/oauth/webhooks/w1/deliveries?status=dead&limit=20": [{ status: 200, body: [] }],
      "GET /auth/admin/oauth/webhooks/w1/deliveries": [{ status: 200, body: [] }],
      "POST /auth/admin/oauth/clients/c1/webhook": [{ status: 200, body: { webhook_id: "w1", secret_id: "s1", secret: "whsec_x" } }],
      "POST /auth/admin/oauth/webhooks/w1/update": [{ status: 200, body: {} }],
      "POST /auth/admin/oauth/webhooks/w1/rotate-secret": [{ status: 200, body: { webhook_id: "w1", secret_id: "s2", secret: "whsec_y" } }],
      "POST /auth/admin/oauth/webhooks/w1/redeliver": [{ status: 200, body: { redelivered: 2 } }],
      "POST /auth/admin/oauth/webhooks/w1/ping": [{ status: 200, body: { delivery_id: "d9" } }],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    expect(await auth.listWebhooks()).toEqual([]);
    expect(await auth.listWebhookDeliveries("w1", "dead", 20)).toEqual([]);
    expect(await auth.listWebhookDeliveries("w1")).toEqual([]);
    expect((await auth.registerWebhook("c1", "https://hooks.example.com/jinji", { summaryThreshold: 500 })).secret).toBe("whsec_x");
    await auth.updateWebhook("w1", { status: "disabled" });
    expect((await auth.rotateWebhookSecret("w1", 0)).secret).toBe("whsec_y");
    await auth.rotateWebhookSecret("w1");
    expect(await auth.redeliverWebhook("w1")).toBe(2);
    await auth.redeliverWebhook("w1", "d1");
    expect(await auth.pingWebhook("w1")).toBe("d9");

    const gets = calls.filter((c) => c.method === "GET" && c.url.includes("webhooks"));
    expect(gets.every((c) => c.headers["x-jinji-csrf"] === undefined)).toBe(true);
    const posts = calls.filter((c) => c.method === "POST");
    expect(posts.every((c) => c.headers["x-jinji-csrf"] === "t")).toBe(true);
    expect(posts.map((c) => c.body)).toEqual([
      { url: "https://hooks.example.com/jinji", max_events_per_delivery: null, summary_threshold: 500, min_interval_seconds: null },
      { url: null, status: "disabled", max_events_per_delivery: null, summary_threshold: null, min_interval_seconds: null },
      { overlap_hours: 0 },
      { overlap_hours: null },
      { delivery_id: null },
      { delivery_id: "d1" },
      undefined,
    ]);
  });

  it("dead でない送信の再送（409）は理由の文を出す", async () => {
    const { fn } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t", header_name: CSRF_HEADER } }],
      "POST /auth/admin/oauth/webhooks/w1/redeliver": [{ status: 409, body: { title: "dead の送信だけを再送できます（送信待ち・成功した送信は再送しません）。" } }],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    const e = await auth.redeliverWebhook("w1", "d1").catch((x: unknown) => x);
    expect((e as ApiError).status).toBe(409);
    expect(authErrorMessage(e, "admin")).toContain("dead の送信だけ");
  });
});

describe("Webhook（デモのアダプタ）", () => {
  const setup = async () => {
    const auth = createDemoAuthAdapter();
    const webhooks = await auth.listWebhooks();
    const clients = await auth.listClients();
    const attendance = webhooks.find((w) => w.client_id === "attendance")!;
    return { auth, webhooks, clients, attendance };
  };

  it("勤怠システムの Webhook は送信待ち・dead・成功の件数と、ローテーション中の旧鍵を持つ", async () => {
    const { auth, attendance } = await setup();
    expect(attendance.pending_count).toBe(1);
    expect(attendance.dead_count).toBe(1);
    expect(attendance.succeeded_count).toBeGreaterThan(0);
    expect(attendance.secrets.filter((s) => s.expires_at === null)).toHaveLength(1);
    expect(attendance.secrets.some((s) => s.expires_at !== null)).toBe(true);
    const all = await auth.listWebhookDeliveries(attendance.id);
    expect(new Set(all.map((d) => d.kind))).toEqual(new Set(["changes", "summary", "ping"]));
    const pending = await auth.listWebhookDeliveries(attendance.id, "pending");
    expect(pending.every((d) => d.status === "pending")).toBe(true);
    expect(pending[0]!.last_error).toBe("状態コード 503");
    // 本文は種別と ID だけ（氏名は載せない）
    for (const d of all) expect(JSON.parse(d.body)).not.toHaveProperty("events.0.display_name");
  });

  it("dead は個別に・まとめて再送でき、dead でない送信の指定は 409", async () => {
    const { auth, attendance } = await setup();
    const [dead] = await auth.listWebhookDeliveries(attendance.id, "dead");
    const [succeeded] = await auth.listWebhookDeliveries(attendance.id, "succeeded");
    const e = await auth.redeliverWebhook(attendance.id, succeeded!.id).catch((x: unknown) => x);
    expect((e as ApiError).status).toBe(409);
    expect(await auth.redeliverWebhook(attendance.id, dead!.id)).toBe(1);
    expect(await auth.redeliverWebhook(attendance.id)).toBe(0);
    const after = (await auth.listWebhooks()).find((w) => w.id === attendance.id)!;
    expect(after.dead_count).toBe(0);
    expect(after.pending_count).toBe(2);
  });

  it("登録は whsec_ の鍵を返し、https 以外・登録できないクライアント・2 つ目は拒否する", async () => {
    const { auth, clients, webhooks } = await setup();
    const payroll = clients.find((c) => c.client_id === "payroll")!;
    expect(webhooks.some((w) => w.client_ref === payroll.id)).toBe(false);
    const insecure = await auth.registerWebhook(payroll.id, "http://payroll.example.co.jp/hooks", {}).catch((x: unknown) => x);
    expect((insecure as ApiError).status).toBe(400);
    const agent = clients.find((c) => !c.grant_types.includes("client_credentials"))!;
    const notReceiver = await auth.registerWebhook(agent.id, "https://agent.example.com/hooks", {}).catch((x: unknown) => x);
    expect((notReceiver as ApiError).status).toBe(400);
    const badSettings = await auth.registerWebhook(payroll.id, "https://payroll.example.co.jp/hooks", { maxEventsPerDelivery: 200, summaryThreshold: 100 }).catch((x: unknown) => x);
    expect((badSettings as ApiError).status).toBe(400);

    const issued = await auth.registerWebhook(payroll.id, "https://payroll.example.co.jp/hooks", {});
    expect(issued.secret).toMatch(/^whsec_/);
    const registered = (await auth.listWebhooks()).find((w) => w.id === issued.webhook_id)!;
    expect(registered).toMatchObject({ client_id: "payroll", status: "active", max_events_per_delivery: 100, summary_threshold: 1000, min_interval_seconds: 10 });
    const dup = await auth.registerWebhook(payroll.id, "https://payroll.example.co.jp/other", {}).catch((x: unknown) => x);
    expect((dup as ApiError).status).toBe(409);
  });

  it("停止・再開と設定の変更、鍵のローテーション（旧鍵は重ねる時間だけ残る）、ping", async () => {
    const { auth, attendance } = await setup();
    expect((await auth.updateWebhook(attendance.id, { status: "disabled" })).status).toBe("disabled");
    const changed = await auth.updateWebhook(attendance.id, { status: "active", url: "https://attendance.example.co.jp/v2/hooks", minIntervalSeconds: 60 });
    expect(changed).toMatchObject({ status: "active", url: "https://attendance.example.co.jp/v2/hooks", min_interval_seconds: 60, max_events_per_delivery: 100 });
    const bad = await auth.updateWebhook(attendance.id, { url: "ftp://example.com" }).catch((x: unknown) => x);
    expect((bad as ApiError).status).toBe(400);

    const rotated = await auth.rotateWebhookSecret(attendance.id);
    expect(rotated.secret).toMatch(/^whsec_/);
    let secrets = (await auth.listWebhooks()).find((w) => w.id === attendance.id)!.secrets;
    expect(secrets.find((s) => s.expires_at === null)!.id).toBe(rotated.secret_id);
    expect(secrets.filter((s) => s.expires_at !== null).length).toBeGreaterThan(0);
    await auth.rotateWebhookSecret(attendance.id, 0);
    secrets = (await auth.listWebhooks()).find((w) => w.id === attendance.id)!.secrets;
    expect(secrets.map((s) => s.id)).not.toContain(rotated.secret_id);
    const tooLong = await auth.rotateWebhookSecret(attendance.id, 169).catch((x: unknown) => x);
    expect((tooLong as ApiError).status).toBe(400);

    const pingId = await auth.pingWebhook(attendance.id);
    const [latest] = await auth.listWebhookDeliveries(attendance.id);
    expect(latest).toMatchObject({ id: pingId, kind: "ping" });
  });
});

describe("createApiAuthAdapter（アカウント・委任・ローカルアカウントの管理）", () => {
  it("パスキーの削除は資格情報 ID をパスに入れ、CSRF のトークンを付ける。最後の 1 つの拒否（409）は理由の文を出す", async () => {
    const { fn, calls } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t", header_name: CSRF_HEADER } }],
      "GET /auth/account/passkeys": [{ status: 200, body: [{ id: "a-b_c", name: "PC", created_at: "2026-10-01T00:00:00Z", is_backed_up: false }] }],
      "POST /auth/account/passkeys/a-b_c/delete": [
        { status: 204 },
        { status: 409, body: { title: "削除できません", detail: "最後のパスキーは削除できません。", type: PROBLEM.lastFactor } },
      ],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    expect((await auth.listPasskeys()).map((p) => p.id)).toEqual(["a-b_c"]);
    await auth.deletePasskey("a-b_c");
    const error = await auth.deletePasskey("a-b_c").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).problemType).toBe(PROBLEM.lastFactor);
    expect(authErrorMessage(error, "account")).toBe("最後のパスキーは削除できません。");
    expect(calls.filter((c) => c.method === "POST").every((c) => c.headers["x-jinji-csrf"] === "t")).toBe(true);
  });

  it("委任の一覧は、トークン発行を使わない環境（/oauth が 404）では null。取り消しは CSRF のトークンを付けて POST する", async () => {
    const delegation: Delegation = {
      id: "f1",
      client_id: "https://agent.example/client.json",
      client_name: null,
      registration: "cimd",
      resource: "mcp",
      scopes: ["jinji.read"],
      granted_at: "2026-10-01T00:00:00Z",
      last_used_at: null,
      expires_at: "2026-10-31T00:00:00Z",
    };
    const { fn, calls } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t", header_name: CSRF_HEADER } }],
      "GET /oauth/delegations": [{ status: 200, body: [delegation] }],
      "POST /oauth/delegations/f1/revoke": [{ status: 204 }],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    const list = await auth.listDelegations();
    expect(list).toHaveLength(1);
    expect(delegationClientName(list![0]!)).toBe("agent.example");
    expect(delegationClientName({ ...delegation, client_name: "アシスタント" })).toBe("アシスタント");
    expect(delegationClientName({ ...delegation, registration: "registered", client_id: "knowledge-agent" })).toBe("knowledge-agent");
    await auth.revokeDelegation("f1");
    expect(calls.find((c) => c.url === "/oauth/delegations/f1/revoke")!.headers["x-jinji-csrf"]).toBe("t");

    const none = createApiAuthAdapter({ fetch: fakeFetch({}).fn });
    expect(await none.listDelegations()).toBeNull();
  });

  it("ローカルアカウントの一覧はユーザー名で絞り（空なら query を付けない）、無効化・再有効化は CSRF のトークンを付けて POST する", async () => {
    const { fn, calls } = fakeFetch({
      "GET /auth/csrf": [{ status: 200, body: { token: "t", header_name: CSRF_HEADER } }],
      "GET /auth/admin/local-accounts?query=yamada": [{ status: 200, body: [] }],
      "GET /auth/admin/local-accounts": [{ status: 200, body: [] }],
      "POST /auth/admin/local-accounts/a1/disable": [{ status: 200, body: { sessions_ended: 2, delegations_revoked: 1 } }],
      "POST /auth/admin/local-accounts/a1/enable": [{ status: 204 }],
    });
    const auth = createApiAuthAdapter({ fetch: fn });
    await auth.listLocalAccounts(" yamada ");
    await auth.listLocalAccounts("  ");
    expect(await auth.disableAccount("a1")).toEqual({ sessions_ended: 2, delegations_revoked: 1 });
    await auth.enableAccount("a1");
    expect(calls.filter((c) => c.method === "GET" && c.url.startsWith("/auth/admin")).map((c) => c.url)).toEqual([
      "/auth/admin/local-accounts?query=yamada",
      "/auth/admin/local-accounts",
    ]);
    expect(calls.filter((c) => c.method === "POST").map((c) => c.headers["x-jinji-csrf"])).toEqual(["t", "t"]);
  });
});

describe("messages", () => {
  it("ログインの失敗の文は理由を区別しない（アカウントの有無を推測させない）", () => {
    const failed = new ApiError(401, "ログインできません", PROBLEM.loginFailed);
    const message = authErrorMessage(failed, "login");
    expect(message).toContain("ユーザー名またはパスワードが正しくないか");
    expect(message).not.toMatch(/存在しません|ロック|退職/);
    expect(authErrorMessage(new ApiError(401, "x"), "two-factor")).toContain("確認コード");
  });

  it("試行の上限は待つ時間を示す", () => {
    expect(authErrorMessage(new ApiError(429, "x", null, 90), "login")).toContain("2 分ほど");
    expect(waitPhrase(30)).toBe("30 秒ほど");
    expect(waitPhrase(null)).toBe("しばらく");
  });

  it("パスキーの取り消しはブラウザの例外から言い分ける", () => {
    expect(authErrorMessage(new DOMException("x", "NotAllowedError"), "passkey-login")).toContain("取り消された");
  });

  it("外部 IdP から戻った誤りを日本語にする", () => {
    expect(loginRedirectError(null)).toBeNull();
    expect(loginRedirectError("unbound")).toContain("結び付けられていません");
    expect(loginRedirectError("inactive")).toBe(loginRedirectError("rejected"));
    expect(loginRedirectError("failed")).toContain("完了できませんでした");
  });

  it("パスワードの条件（長さと文字種）", () => {
    expect(passwordRules("Abcdefghij1!").every((r) => r.ok)).toBe(true);
    expect(passwordRules("abcdefghij1!").find((r) => r.label.includes("大文字"))!.ok).toBe(false);
    expect(passwordRules("Abc1!").find((r) => r.label.includes("文字以上"))!.ok).toBe(false);
  });
});

describe("roles", () => {
  it("認証の管理は人事管理者の人と緊急用アカウントだけ", () => {
    expect(canUseAdmin({ ...session, roles: ["hr_admin"] })).toBe(true);
    expect(canUseAdmin({ ...session, kind: "break_glass", person_id: null })).toBe(true);
    expect(canUseAdmin(session)).toBe(false);
    expect(isHrAdmin({ ...session, kind: "break_glass", person_id: null, roles: ["hr_admin"] })).toBe(false);
  });
});

describe("webauthn", () => {
  it("base64url を往復する", () => {
    const bytes = new Uint8Array([0, 1, 2, 250, 251, 252, 253, 254, 255]);
    const encoded = toBase64Url(bytes);
    expect(encoded).not.toMatch(/[+/=]/);
    expect(new Uint8Array(fromBase64Url(encoded))).toEqual(bytes);
  });

  it("options の base64url の項目をバイト列にする", () => {
    const create = parseCreationOptions({
      challenge: "AAEC",
      rp: { id: "example.com", name: "jin-ji" },
      user: { id: "AQID", name: "u", displayName: "u" },
      excludeCredentials: [{ type: "public-key", id: "BAUG" }],
      pubKeyCredParams: [{ type: "public-key", alg: -7 }],
    });
    expect(new Uint8Array(create.challenge as ArrayBuffer)).toEqual(new Uint8Array([0, 1, 2]));
    expect(new Uint8Array(create.user.id as ArrayBuffer)).toEqual(new Uint8Array([1, 2, 3]));
    expect(new Uint8Array(create.excludeCredentials![0]!.id as ArrayBuffer)).toEqual(new Uint8Array([4, 5, 6]));
    const get = parseRequestOptions({ challenge: "AAEC", rpId: "example.com" });
    expect(get.allowCredentials).toBeUndefined();
    expect(get.rpId).toBe("example.com");
  });
});

describe("labels", () => {
  const constants = (source: string) => [...source.matchAll(/public const string \w+ = "([a-z_]+)";/g)].map((m) => m[1]!);

  it("項目の名前はサーバーの PersonFields と揃っている", () => {
    expect(Object.keys(FIELD_LABELS).sort()).toEqual(constants(personFieldsCs).sort());
  });

  it("MCP ツールの名前はサーバーの McpToolNames と揃っている", () => {
    expect(MCP_TOOLS.map((t) => t.name).sort()).toEqual(constants(mcpToolNamesCs).sort());
  });
});
