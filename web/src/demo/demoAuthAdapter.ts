// デモの認証（架空）。AuthAdapter を API アダプタと同じ形で実装する。
// mode=demo のビルドと開発サーバーにだけ含まれる（auth/authAdapter.ts の動的 import。本番ビルドでは取り除かれる）。
// - ログイン: ユーザー名 demo・パスワードは何でも → 確認コード 123456。パスキーは押すとログインできる
// - ログイン中の人は、デモデータの人事課の 1 人（人事管理者・修正担当者・ローカルアカウント。demoViewer.ts）
// - 管理の画面の未紐付けのログイン・ローカルアカウント・クライアント・許可ドメイン・Webhook、アカウントの画面のパスキー・委任は、
//   この中だけの架空のデータ（再読み込みで戻る）
import type { Schemas } from "../api/schema.gen";
import type {
  AuthAdapter,
  CimdDomains,
  Consent,
  Delegation,
  DisclosureLead,
  LocalAccount,
  OAuthClient,
  Passkey,
  PersonSummary,
  Session,
  UnboundLogin,
  Webhook,
  WebhookDelivery,
  WebhookSettings,
} from "../auth/types";
import { ApiError } from "../data/types";
import demoData from "./demo-data.json";
import { demoMe as demoViewer, demoRoles } from "./demoViewer";
import type { DemoDataset } from "./model";

const data = demoData as DemoDataset;
type DemoPerson = DemoDataset["people"][number];

const LOGIN_FAILED = "urn:jinji:problem:login-failed";

function personName(p: DemoPerson): string {
  return p.display_name || `${p.family_name} ${p.given_name}`;
}

function primaryOrgName(p: DemoPerson): string | null {
  const a = p.assignments.find((x) => x.type === "primary" && x.valid_until === null);
  return a ? (data.org_units.find((o) => o.id === a.org_unit_id)?.name ?? null) : null;
}

function summary(p: DemoPerson): PersonSummary {
  return { id: p.id, name: personName(p), detail: [p.employee_number, primaryOrgName(p)].filter(Boolean).join("・") || null };
}

function findByEmail(email: string): DemoPerson | undefined {
  return data.people.find((p) => p.work_email === email);
}

const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

const later = async <T,>(value: T, ms = 120): Promise<T> => {
  await wait(ms);
  return value;
};

function uuid(n: number): string {
  return `0192f6a0-0000-7000-8000-${String(n).padStart(12, "0")}`;
}

const NOW = "2026-10-12T09:30:00+09:00";

export function createDemoAuthAdapter(): AuthAdapter {
  const me = demoViewer(data);
  const session: Session = {
    kind: "person",
    person_id: me.id,
    account_id: uuid(900),
    session_id: uuid(901),
    provider: "local",
    methods: ["pwd", "otp"],
    // 既定は人事管理者・修正担当者。修正依頼の画面のデモの切り替えで一般の社員（ロールなし）にできる
    roles: demoRoles(),
  };

  const yokoyama = findByEmail("riko.yokoyama@example.com");
  const yamamotos = data.people.filter((p) => p.family_name === "山本").slice(0, 3);
  let unbound: UnboundLogin[] = [
    {
      id: uuid(1),
      provider: "entra",
      issuer: "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0",
      subject: "5f0c2b1e-8a7d-4c1e-9b3a-2d6f7e8a9b0c",
      tenant_id: "00000000-0000-0000-0000-000000000000",
      employee_number: null,
      email: yokoyama?.work_email ?? "riko.yokoyama@example.com",
      display_name: yokoyama ? personName(yokoyama) : "横山 莉子",
      candidate_person_ids: yokoyama ? [yokoyama.id] : [],
      attempt_count: 3,
      first_attempted_at: "2026-10-10T08:52:11+09:00",
      last_attempted_at: "2026-10-12T08:47:03+09:00",
      resolved_at: null,
      resolved_binding_id: null,
    },
    {
      id: uuid(2),
      provider: "entra",
      issuer: "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0",
      subject: "a1b2c3d4-0000-4000-8000-0000000000b2",
      tenant_id: "00000000-0000-0000-0000-000000000000",
      employee_number: null,
      email: "yamamoto@example.com",
      display_name: "山本",
      candidate_person_ids: yamamotos.map((p) => p.id),
      attempt_count: 1,
      first_attempted_at: "2026-10-11T19:05:40+09:00",
      last_attempted_at: "2026-10-11T19:05:40+09:00",
      resolved_at: null,
      resolved_binding_id: null,
    },
    {
      id: uuid(3),
      provider: "partner-oidc",
      issuer: "https://idp.partner.example.net",
      subject: "u-48213",
      tenant_id: null,
      employee_number: "P-0192",
      email: "h.sato@partner.example.net",
      display_name: "Sato Haruka",
      candidate_person_ids: [],
      attempt_count: 2,
      first_attempted_at: "2026-10-09T13:20:00+09:00",
      last_attempted_at: "2026-10-09T13:21:30+09:00",
      resolved_at: null,
      resolved_binding_id: null,
    },
    {
      id: uuid(4),
      provider: "entra",
      issuer: "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0",
      subject: "c3d4e5f6-0000-4000-8000-0000000000c4",
      tenant_id: "00000000-0000-0000-0000-000000000000",
      employee_number: null,
      email: "taro.example@example.com",
      display_name: "例 太郎",
      candidate_person_ids: [],
      attempt_count: 1,
      first_attempted_at: "2026-10-01T10:00:00+09:00",
      last_attempted_at: "2026-10-01T10:00:00+09:00",
      resolved_at: "2026-10-01T11:12:00+09:00",
      resolved_binding_id: uuid(40),
    },
  ];

  const base = {
    grant_types: ["authorization_code", "refresh_token"],
    rate_limit_per_client: null,
    metadata_expires_at: null,
    disclosure_lead_days: 0,
  };
  let clients: OAuthClient[] = [
    {
      ...base,
      id: uuid(11),
      client_id: "https://agent.example.com/oauth/client.json",
      registration: "cimd",
      status: "pending_approval",
      display_name: null,
      auth_method: "none",
      redirect_uris: [],
      resources: ["mcp"],
      scopes: ["jinji.read"],
      fields: ["*"],
      mcp_tools: [],
      created_at: "2026-10-12T08:15:00+09:00",
    },
    {
      ...base,
      id: uuid(12),
      client_id: "knowledge-agent",
      registration: "registered",
      status: "active",
      display_name: "社内ナレッジエージェント",
      auth_method: "none",
      redirect_uris: ["http://127.0.0.1/callback"],
      resources: ["mcp"],
      scopes: ["jinji.read"],
      fields: ["display_name", "family_name_kana", "given_name_kana", "work_email", "extension_number", "assignments", "managers"],
      mcp_tools: ["search_people", "get_person", "get_org", "search_orgs"],
      created_at: "2026-09-20T10:00:00+09:00",
    },
    {
      ...base,
      id: uuid(13),
      client_id: "https://assistant.example.org/mcp/client.json",
      registration: "cimd",
      status: "active",
      display_name: "アシスタント（例）",
      auth_method: "none",
      redirect_uris: ["https://assistant.example.org/oauth/callback"],
      resources: ["mcp"],
      scopes: ["jinji.read", "jinji.propose"],
      fields: ["*"],
      mcp_tools: ["*"],
      metadata_expires_at: "2026-10-12T10:30:00+09:00",
      created_at: "2026-09-28T14:02:00+09:00",
    },
    {
      ...base,
      id: uuid(14),
      client_id: "attendance",
      registration: "registered",
      status: "active",
      display_name: "勤怠システム",
      auth_method: "private_key_jwt",
      grant_types: ["client_credentials"],
      redirect_uris: [],
      resources: ["api"],
      scopes: ["jinji.read"],
      fields: ["employee_number", "display_name", "assignments", "managers", "employment_status"],
      mcp_tools: [],
      rate_limit_per_client: 600,
      created_at: "2026-08-01T09:00:00+09:00",
    },
    {
      ...base,
      id: uuid(16),
      client_id: "workflow",
      registration: "registered",
      status: "active",
      display_name: "ワークフローシステム",
      auth_method: "private_key_jwt",
      grant_types: ["client_credentials"],
      redirect_uris: [],
      resources: ["api"],
      scopes: ["jinji.read"],
      fields: ["employee_number", "display_name", "work_email", "assignments", "managers"],
      mcp_tools: [],
      created_at: "2026-08-20T09:00:00+09:00",
    },
    {
      ...base,
      id: uuid(17),
      client_id: "payroll",
      registration: "registered",
      status: "active",
      display_name: "給与計算システム",
      auth_method: "private_key_jwt",
      grant_types: ["client_credentials"],
      redirect_uris: [],
      resources: ["api"],
      scopes: ["jinji.read"],
      fields: ["employee_number", "display_name", "employment_type", "hire_date", "termination_date", "assignments"],
      mcp_tools: [],
      created_at: "2026-09-01T09:00:00+09:00",
    },
    {
      ...base,
      id: uuid(15),
      client_id: "legacy-sync",
      registration: "registered",
      status: "suspended",
      display_name: "旧連携ツール",
      auth_method: "none",
      redirect_uris: ["http://127.0.0.1/cb"],
      resources: ["mcp"],
      scopes: ["jinji.read"],
      fields: ["display_name", "assignments"],
      mcp_tools: ["search_people"],
      created_at: "2026-06-15T09:00:00+09:00",
    },
  ];

  const domains: CimdDomains = { configured: ["claude.ai", "chatgpt.com", "vscode.dev"], added: ["tools.example.net"] };

  const consents: Record<string, Consent> = {
    cimd: {
      client: {
        client_id: "https://assistant.example.org/mcp/client.json",
        name: "アシスタント（例）",
        registration: "cimd",
        loopback_only: false,
        fields: ["*"],
        mcp_tools: ["*"],
      },
      redirect_uri: "https://assistant.example.org/oauth/callback",
      redirect_host: "assistant.example.org",
      resource: "mcp",
      resource_uri: "https://jinji.example.co.jp/mcp",
      scopes: ["jinji.read", "jinji.propose"],
      expires_at: "2026-10-12T09:40:00+09:00",
    },
  };
  const defaultConsent: Consent = {
    client: {
      client_id: "knowledge-agent",
      name: "社内ナレッジエージェント",
      registration: "registered",
      loopback_only: true,
      fields: clients[1]!.fields,
      mcp_tools: clients[1]!.mcp_tools,
    },
    redirect_uri: "http://127.0.0.1:53682/callback",
    redirect_host: "127.0.0.1",
    resource: "mcp",
    resource_uri: "https://jinji.example.co.jp/mcp",
    scopes: ["jinji.read"],
    expires_at: "2026-10-12T09:40:00+09:00",
  };

  const others = data.people.filter((p) => p.id !== me.id).slice(0, 3);
  let accounts: LocalAccount[] = [
    {
      id: session.account_id!,
      kind: "person",
      person_id: me.id,
      user_name: "demo",
      has_password: true,
      two_factor_enabled: true,
      passkey_count: 2,
      lockout_end: null,
      disabled_at: null,
      created_at: "2026-04-01T09:00:00+09:00",
    },
    ...others.map(
      (p, i): LocalAccount => ({
        id: uuid(910 + i),
        kind: "person",
        person_id: p.id,
        user_name: `user${i + 1}`,
        has_password: true,
        two_factor_enabled: i === 0,
        passkey_count: i === 0 ? 1 : 0,
        lockout_end: i === 1 ? "2026-10-12T09:45:00+09:00" : null,
        disabled_at: i === 2 ? "2026-09-30T18:00:00+09:00" : null,
        created_at: "2026-05-10T09:00:00+09:00",
      }),
    ),
    {
      id: uuid(920),
      kind: "break_glass",
      person_id: null,
      user_name: "breakglass-1",
      has_password: false,
      two_factor_enabled: false,
      passkey_count: 1,
      lockout_end: null,
      disabled_at: null,
      created_at: "2026-04-01T08:00:00+09:00",
    },
  ];
  const findAccount = (id: string) => {
    const a = accounts.find((x) => x.id === id);
    if (!a) throw new ApiError(404, "見つかりません");
    if (a.id === session.account_id) throw new ApiError(403, "自分のアカウントには使えません（別の管理者が行います）。");
    return a;
  };

  let passkeys: Passkey[] = [
    { id: "ZGVtby1wYXNza2V5LTE", name: "仕事用ノート PC", created_at: "2026-04-02T10:00:00+09:00", is_backed_up: false },
    { id: "ZGVtby1wYXNza2V5LTI", name: "スマートフォン", created_at: "2026-06-18T12:30:00+09:00", is_backed_up: true },
  ];
  let delegations: Delegation[] = [
    {
      id: uuid(950),
      client_id: "https://assistant.example.org/mcp/client.json",
      client_name: "アシスタント（例）",
      registration: "cimd",
      resource: "mcp",
      scopes: ["jinji.read", "jinji.propose"],
      granted_at: "2026-10-05T14:10:00+09:00",
      last_used_at: "2026-10-12T09:12:00+09:00",
      expires_at: "2026-11-04T14:10:00+09:00",
    },
    {
      id: uuid(951),
      client_id: "knowledge-agent",
      client_name: "社内ナレッジエージェント",
      registration: "registered",
      resource: "mcp",
      scopes: ["jinji.read"],
      granted_at: "2026-09-21T09:00:00+09:00",
      last_used_at: null,
      expires_at: "2026-10-21T09:00:00+09:00",
    },
  ];

  let pendingTwoFactor = false;
  const signedIn = (): Schemas["LoginResponse"] => ({ status: "signed_in", session, csrf_token: "demo" });
  const failed = () => new ApiError(401, "ログインできません", LOGIN_FAILED);

  // 先行提供の起案。勤怠システムに、別の（架空の）人事管理者の起案が承認待ちで 1 件ある
  let leads: DisclosureLead[] = [
    {
      id: uuid(60),
      client_ref: uuid(14),
      lead_days: 14,
      status: "proposed",
      note: "月初の異動を勤怠の締めの前に反映するため",
      proposed_by_kind: "person",
      proposed_by: uuid(950),
      proposed_at: "2026-10-11T17:20:00+09:00",
      decided_by: null,
      decided_at: null,
      superseded_at: null,
    },
  ];

  // 社内システムへの Webhook。勤怠システムは再試行中・dead の送信を持ち、ワークフローシステムは停止中。給与計算システムはまだ登録が無い（新規登録の候補）
  type DemoWebhook = Omit<Webhook, "client_id" | "client_name" | "client_status" | "disclosure_lead_days" | "pending_count" | "dead_count" | "succeeded_count" | "oldest_pending_at"> & {
    /** 一覧に載せていない過去の成功の件数（件数の表示だけに足す）。 */
    succeeded_before: number;
  };
  let webhooks: DemoWebhook[] = [
    {
      id: uuid(80),
      client_ref: uuid(14),
      url: "https://attendance.example.co.jp/hooks/jinji",
      status: "active",
      max_events_per_delivery: 100,
      summary_threshold: 1000,
      min_interval_seconds: 10,
      last_success_at: "2026-10-12T08:05:12+09:00",
      last_failure_at: "2026-10-12T09:21:40+09:00",
      last_failure: "状態コード 503",
      secrets: [
        { id: uuid(90), created_at: "2026-10-11T18:00:00+09:00", expires_at: null },
        { id: uuid(89), created_at: "2026-08-01T09:10:00+09:00", expires_at: "2026-10-12T18:00:00+09:00" },
      ],
      created_at: "2026-08-01T09:10:00+09:00",
      updated_at: "2026-10-11T18:00:00+09:00",
      succeeded_before: 1281,
    },
    {
      id: uuid(81),
      client_ref: uuid(16),
      url: "https://workflow.example.co.jp/api/webhooks/jinji",
      status: "disabled",
      max_events_per_delivery: 50,
      summary_threshold: 500,
      min_interval_seconds: 30,
      last_success_at: "2026-10-09T17:42:03+09:00",
      last_failure_at: null,
      last_failure: null,
      secrets: [{ id: uuid(91), created_at: "2026-08-20T09:30:00+09:00", expires_at: null }],
      created_at: "2026-08-20T09:30:00+09:00",
      updated_at: "2026-10-10T09:00:00+09:00",
      succeeded_before: 214,
    },
  ];

  // 本文は種別と ID だけ（氏名などの値は載せない）。人の ID はデモデータの人から借りる
  const peopleIds = data.people.slice(10, 16).map((p) => p.id);
  const orgId = data.org_units[3]?.id ?? uuid(300);
  const changesBody = (id: string, createdAt: string, events: { type: string; id: string; effective_date: string }[]) =>
    JSON.stringify(
      { id, type: "changes", created_at: createdAt, client_id: "attendance", events: events.map((e) => ({ ...e, withdrawn: false })), changes_since: "c_000000000000a3f2" },
      null,
      2,
    );
  const delivery = (d: Partial<WebhookDelivery> & Pick<WebhookDelivery, "id" | "kind" | "status" | "created_at" | "body">): WebhookDelivery => ({
    event_count: 0,
    attempts: 0,
    next_attempt_at: d.created_at,
    last_attempt_at: null,
    last_http_status: null,
    last_error: null,
    succeeded_at: null,
    dead_at: null,
    redelivered_by: null,
    redelivered_at: null,
    ...d,
  });
  let deliveries: WebhookDelivery[] = [
    delivery({
      id: uuid(1001),
      kind: "changes",
      status: "pending",
      event_count: 3,
      attempts: 3,
      created_at: "2026-10-12T09:15:02+09:00",
      next_attempt_at: "2026-10-12T09:23:40+09:00",
      last_attempt_at: "2026-10-12T09:21:40+09:00",
      last_http_status: 503,
      last_error: "状態コード 503",
      body: changesBody(uuid(1001), "2026-10-12T09:15:02+09:00", [
        { type: "person", id: peopleIds[0]!, effective_date: "2026-11-01" },
        { type: "person", id: peopleIds[1]!, effective_date: "2026-11-01" },
        { type: "org_unit", id: orgId, effective_date: "2026-11-01" },
      ]),
    }),
    delivery({
      id: uuid(1002),
      kind: "summary",
      status: "succeeded",
      event_count: 4200,
      attempts: 1,
      created_at: "2026-10-12T08:05:10+09:00",
      last_attempt_at: "2026-10-12T08:05:12+09:00",
      last_http_status: 200,
      succeeded_at: "2026-10-12T08:05:12+09:00",
      body: JSON.stringify(
        {
          id: uuid(1002),
          type: "changes.summary",
          created_at: "2026-10-12T08:05:10+09:00",
          client_id: "attendance",
          summary: { total: 4200, person: 4000, org_unit: 199, site: 1, withdrawn: 0, first_effective_date: "2026-10-12", last_effective_date: "2026-10-12" },
          changes_since: "c_000000000000a3c1",
        },
        null,
        2,
      ),
    }),
    delivery({
      id: uuid(1003),
      kind: "changes",
      status: "dead",
      event_count: 2,
      attempts: 8,
      created_at: "2026-10-10T22:00:05+09:00",
      next_attempt_at: "2026-10-11T19:12:00+09:00",
      last_attempt_at: "2026-10-11T19:12:00+09:00",
      last_error: "接続できません",
      dead_at: "2026-10-11T19:12:00+09:00",
      body: changesBody(uuid(1003), "2026-10-10T22:00:05+09:00", [
        { type: "person", id: peopleIds[2]!, effective_date: "2026-10-11" },
        { type: "person", id: peopleIds[3]!, effective_date: "2026-10-11" },
      ]),
    }),
    delivery({
      id: uuid(1004),
      kind: "changes",
      status: "succeeded",
      event_count: 1,
      attempts: 2,
      created_at: "2026-10-10T09:00:03+09:00",
      last_attempt_at: "2026-10-10T09:00:40+09:00",
      last_http_status: 204,
      succeeded_at: "2026-10-10T09:00:40+09:00",
      body: changesBody(uuid(1004), "2026-10-10T09:00:03+09:00", [{ type: "person", id: peopleIds[4]!, effective_date: "2026-10-10" }]),
    }),
    delivery({
      id: uuid(1005),
      kind: "ping",
      status: "succeeded",
      attempts: 1,
      created_at: "2026-10-01T10:00:00+09:00",
      last_attempt_at: "2026-10-01T10:00:01+09:00",
      last_http_status: 200,
      succeeded_at: "2026-10-01T10:00:01+09:00",
      body: JSON.stringify({ id: uuid(1005), type: "ping", created_at: "2026-10-01T10:00:00+09:00", client_id: "attendance" }, null, 2),
    }),
  ];
  // 送信 → Webhook（送信の一覧は Webhook ごと）。ワークフローシステムは停止中なので、変更が送信待ちとしてたまっている
  const deliveryOwner = new Map<string, string>(deliveries.map((d) => [d.id, uuid(80)]));
  const held = delivery({
    id: uuid(1010),
    kind: "changes",
    status: "pending",
    event_count: 1,
    created_at: "2026-10-11T09:00:04+09:00",
    body: JSON.stringify(
      {
        id: uuid(1010),
        type: "changes",
        created_at: "2026-10-11T09:00:04+09:00",
        client_id: "workflow",
        events: [{ type: "person", id: peopleIds[5]!, effective_date: "2026-10-11", withdrawn: false }],
        changes_since: "c_000000000000a3d0",
      },
      null,
      2,
    ),
  });
  deliveries = [...deliveries, held];
  deliveryOwner.set(held.id, uuid(81));

  const findWebhook = (id: string) => {
    const w = webhooks.find((x) => x.id === id);
    if (!w) throw new ApiError(404, "Webhook の登録が見つかりません。");
    return w;
  };
  const toWebhook = (w: DemoWebhook): Webhook => {
    const c = findClient(w.client_ref);
    const mine = deliveries.filter((d) => deliveryOwner.get(d.id) === w.id);
    const pending = mine.filter((d) => d.status === "pending" || d.status === "sending");
    const { succeeded_before, ...rest } = w;
    return {
      ...rest,
      client_id: c.client_id,
      client_name: c.display_name,
      client_status: c.status,
      disclosure_lead_days: c.disclosure_lead_days,
      pending_count: pending.length,
      dead_count: mine.filter((d) => d.status === "dead").length,
      succeeded_count: succeeded_before + mine.filter((d) => d.status === "succeeded").length,
      oldest_pending_at: pending.map((d) => d.created_at).sort()[0] ?? null,
    };
  };
  let secretSeq = 0;
  const issueSecret = (webhookId: string) => {
    secretSeq += 1;
    const secretId = uuid(95 + secretSeq);
    // 架空の鍵（Standard Webhooks の whsec_ + base64 の 32 バイト）
    const secret = `whsec_ZGVtby1zZWNyZXQtJ${String(secretSeq).padStart(2, "0")}aW5Kajlx8KqM3rYv0n2Xw5sT1bE=`;
    return { webhook_id: webhookId, secret_id: secretId, secret };
  };
  const checkUrl = (url: string) => {
    let parsed: URL;
    try {
      parsed = new URL(url);
    } catch {
      throw new ApiError(400, "送信先の URL は https で指定してください。");
    }
    if (parsed.protocol !== "https:") throw new ApiError(400, "送信先の URL は https で指定してください。");
    if (parsed.username || parsed.password || parsed.hash) throw new ApiError(400, "送信先の URL に利用者情報・フラグメントは使えません。");
    if (parsed.hostname === "localhost" || parsed.hostname.endsWith(".localhost")) throw new ApiError(400, "送信先に localhost は使えません。");
  };
  const resolveSettings = (settings: WebhookSettings, current: { max: number; threshold: number; interval: number }) => {
    const max = settings.maxEventsPerDelivery ?? current.max;
    const threshold = settings.summaryThreshold ?? current.threshold;
    const interval = settings.minIntervalSeconds ?? current.interval;
    if (!Number.isInteger(max) || max < 1 || max > 1000) throw new ApiError(400, "1 回に送る件数は 1〜1000 で指定してください。");
    if (!Number.isInteger(threshold) || threshold < max || threshold > 100_000) {
      throw new ApiError(400, "要約に切り替える件数は、1 回に送る件数以上 100,000 以下で指定してください。");
    }
    if (!Number.isInteger(interval) || interval < 1 || interval > 3600) throw new ApiError(400, "送信の間隔は 1〜3600 秒で指定してください。");
    return { max_events_per_delivery: max, summary_threshold: threshold, min_interval_seconds: interval };
  };

  const findClient = (id: string) => {
    const c = clients.find((x) => x.id === id);
    if (!c) throw new ApiError(404, "見つかりません");
    return c;
  };
  const replaceClient = (next: OAuthClient) => {
    clients = clients.map((c) => (c.id === next.id ? next : c));
    return next;
  };

  return {
    kind: "demo",
    providers: () =>
      later({
        providers: [
          { name: "entra", display_name: "Microsoft Entra ID（社内アカウント）", type: "entra", login_url: "/auth/login/entra" },
          { name: "partner-oidc", display_name: "グループ会社の ID", type: "oidc", login_url: "/auth/login/partner-oidc" },
        ],
        password: true,
        passkey: true,
      }),
    me: () => later(session),
    logout: () => later(undefined),
    async loginPassword(userName, password) {
      await wait(300);
      if (userName.trim().toLowerCase() !== "demo" || password.length === 0) throw failed();
      pendingTwoFactor = true;
      return { status: "two_factor_required", session: null, csrf_token: null };
    },
    async loginTwoFactor({ code, recoveryCode }) {
      await wait(300);
      if (!pendingTwoFactor) throw failed();
      if (code === "123456" || (recoveryCode && recoveryCode.length >= 8)) return signedIn();
      throw failed();
    },
    async loginPasskey() {
      await wait(500);
      return signedIn();
    },
    registerPasskey: () => later(undefined, 500),
    setupTwoFactor: () =>
      later({
        shared_key: "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP",
        authenticator_uri: "otpauth://totp/jin-ji:demo?secret=JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP&issuer=jin-ji&digits=6",
      }),
    async enableTwoFactor(code) {
      await wait(200);
      if (!/^\d{6}$/.test(code)) throw new ApiError(400, "確認の符号が合いません。認証アプリの 6 桁の符号を入力してください。");
      return ["7KQ2-M9XP", "H4TD-2WCA", "R8NF-6YJE", "B3VL-Q5ZU", "D9GS-1KHM", "W2PX-8TRC", "N6JA-4FEQ", "C5UY-7LDB", "T1MK-3SVG", "F8ZE-9QNW"];
    },
    listPasskeys: () => later([...passkeys]),
    async deletePasskey(id) {
      await wait(200);
      if (!passkeys.some((p) => p.id === id)) throw new ApiError(404, "見つかりません");
      passkeys = passkeys.filter((p) => p.id !== id);
    },
    listDelegations: () => later([...delegations]),
    async revokeDelegation(id) {
      await wait(200);
      if (!delegations.some((d) => d.id === id)) throw new ApiError(404, "見つかりません");
      delegations = delegations.filter((d) => d.id !== id);
    },
    displayName: (personId) => later(personId === me.id ? personName(me) : null),
    async resetPassword(token) {
      await wait(300);
      if (!token) throw new ApiError(400, "リンクが無効か、期限が切れています。", "urn:jinji:problem:invalid-link");
    },
    resetPasskey: () => later(undefined, 500),
    async getConsent(requestId) {
      await wait(150);
      return consents[requestId] ?? defaultConsent;
    },
    // デモではクライアントへ戻らない（空の遷移先）
    decideConsent: () => later("", 300),
    listUnboundLogins: (includeResolved) => later(unbound.filter((u) => includeResolved || u.resolved_at === null)),
    async bindLogin(unboundLoginId, personId) {
      await wait(200);
      if (personId === me.id) throw new ApiError(403, "自分の人の紐付けは作成できません。");
      const row = unbound.find((u) => u.id === unboundLoginId);
      if (!row) throw new ApiError(404, "見つかりません");
      const bindingId = uuid(500 + unbound.indexOf(row));
      unbound = unbound.map((u) => (u.id === unboundLoginId ? { ...u, resolved_at: NOW, resolved_binding_id: bindingId } : u));
      return { id: bindingId, provider: row.provider, issuer: row.issuer, subject: row.subject, person_id: personId, basis: "manual", created_at: NOW };
    },
    async revokeBinding(bindingId) {
      await wait(200);
      unbound = unbound.map((u) => (u.resolved_binding_id === bindingId ? { ...u, resolved_at: null, resolved_binding_id: null } : u));
    },
    listLocalAccounts: (query) => later(accounts.filter((a) => a.user_name.toLowerCase().includes(query.trim().toLowerCase()))),
    async disableAccount(accountId) {
      await wait(200);
      const a = findAccount(accountId);
      if (a.disabled_at) throw new ApiError(409, "このアカウントは既に無効です。");
      accounts = accounts.map((x) => (x.id === accountId ? { ...x, disabled_at: NOW } : x));
      return { sessions_ended: 1, delegations_revoked: a.kind === "person" ? 1 : 0 };
    },
    async enableAccount(accountId) {
      await wait(200);
      const a = findAccount(accountId);
      if (!a.disabled_at) throw new ApiError(409, "このアカウントは無効になっていません。");
      accounts = accounts.map((x) => (x.id === accountId ? { ...x, disabled_at: null } : x));
    },
    async unlockAccount(accountId) {
      await wait(200);
      findAccount(accountId);
      accounts = accounts.map((x) => (x.id === accountId ? { ...x, lockout_end: null } : x));
    },
    async issueResetLink(accountId, purpose) {
      await wait(200);
      if (accountId === session.account_id) throw new ApiError(403, "自分のアカウントのリンクは発行できません。");
      const token = "demo-0000000000000000000000000000000000000000000";
      return {
        link_id: uuid(700),
        account_id: accountId,
        purpose,
        url: `${window.location.origin}/account-recovery?purpose=${purpose}#token=${token}`,
        token,
        expires_at: "2026-10-12T10:30:00+09:00",
      };
    },
    async searchPeople(query) {
      await wait(150);
      const q = query.trim();
      if (!q) return [];
      return data.people
        .filter((p) => [p.display_name, p.family_name, p.given_name, p.family_name_kana, p.given_name_kana, p.employee_number].some((v) => v?.includes(q)))
        .slice(0, 20)
        .map(summary);
    },
    peopleByIds: (ids) => later(data.people.filter((p) => ids.includes(p.id)).map(summary)),
    listClients: (status) => later(clients.filter((c) => !status || c.status === status)),
    async setClientStatus(id, action) {
      await wait(200);
      const c = findClient(id);
      const status = action === "suspend" ? "suspended" : action === "reject" ? "rejected" : "active";
      if ((action === "approve" || action === "reject") && c.status !== "pending_approval") {
        throw new ApiError(409, "承認待ちのクライアントだけを承認・却下できます。");
      }
      return replaceClient({ ...c, status });
    },
    async updateClient(id, update) {
      await wait(200);
      const c = findClient(id);
      return replaceClient({ ...c, mcp_tools: update.mcpTools ?? c.mcp_tools });
    },
    listDisclosureLeads: (clientId) => later(leads.filter((l) => !clientId || l.client_ref === clientId)),
    async proposeDisclosureLead(clientId, leadDays, note) {
      await wait(200);
      const c = findClient(clientId);
      if (!c.grant_types.includes("client_credentials")) throw new ApiError(400, "先行提供は社内システム（client credentials）だけに設定できます。");
      if (!Number.isInteger(leadDays) || leadDays < 0 || leadDays > 90) throw new ApiError(400, "日数は 0〜90 で指定してください。");
      if (leads.some((l) => l.client_ref === c.id && l.status === "proposed")) throw new ApiError(409, "承認待ちの起案があります。先にそれを承認または却下してください。");
      if (leadDays === c.disclosure_lead_days) throw new ApiError(400, "現在の値と同じです。");
      const lead: DisclosureLead = {
        id: uuid(61 + leads.length),
        client_ref: c.id,
        lead_days: leadDays,
        status: "proposed",
        note: note?.trim() || null,
        proposed_by_kind: "person",
        proposed_by: me.id,
        proposed_at: NOW,
        decided_by: null,
        decided_at: null,
        superseded_at: null,
      };
      leads = [lead, ...leads];
      return lead;
    },
    async decideDisclosureLead(leadId, approve) {
      await wait(200);
      const lead = leads.find((l) => l.id === leadId);
      if (!lead) throw new ApiError(404, "見つかりません");
      if (lead.status !== "proposed") throw new ApiError(409, "すでに決定済みの起案です。");
      if (lead.proposed_by === me.id) throw new ApiError(403, "起案者は自分の起案を承認・却下できません（別の人事管理者が行います）。");
      const decided: DisclosureLead = { ...lead, status: approve ? "approved" : "rejected", decided_by: me.id, decided_at: NOW };
      leads = leads.map((l) => {
        if (l.id === leadId) return decided;
        // 承認すると、同じクライアントの前の承認済みの値は置き換わる
        return approve && l.client_ref === lead.client_ref && l.status === "approved" ? { ...l, status: "superseded", superseded_at: NOW } : l;
      });
      if (approve) replaceClient({ ...findClient(lead.client_ref), disclosure_lead_days: lead.lead_days });
      return decided;
    },
    listCimdDomains: () => later({ configured: [...domains.configured], added: [...domains.added] }),
    async addCimdDomain(domain) {
      await wait(150);
      if (domains.configured.includes(domain) || domains.added.includes(domain)) throw new ApiError(409, "そのドメインはすでに許可されています。");
      domains.added = [...domains.added, domain].sort();
    },
    async removeCimdDomain(domain) {
      await wait(150);
      domains.added = domains.added.filter((d) => d !== domain);
    },
    listWebhooks: () => later(webhooks.map(toWebhook)),
    async registerWebhook(clientId, url, settings) {
      await wait(200);
      const c = findClient(clientId);
      if (c.status !== "active" && c.status !== "suspended") throw new ApiError(400, "承認待ち・却下したクライアントには登録できません。");
      if (!c.grant_types.includes("client_credentials") || !c.resources.includes("api") || !c.scopes.includes("jinji.read")) {
        throw new ApiError(
          400,
          "Webhook は、REST API を client credentials で jinji.read のスコープを使って読める社内システムにだけ登録できます（受け手は通知を受けて自分のトークンで取り直します）。",
        );
      }
      checkUrl(url);
      const resolved = resolveSettings(settings, { max: 100, threshold: 1000, interval: 10 });
      if (webhooks.some((w) => w.client_ref === c.id)) {
        throw new ApiError(409, "このクライアントには既に Webhook が登録されています（送信先の変更は登録の変更で行います）。");
      }
      const id = uuid(82 + webhooks.length);
      const issued = issueSecret(id);
      webhooks = [
        ...webhooks,
        {
          id,
          client_ref: c.id,
          url,
          status: "active",
          ...resolved,
          last_success_at: null,
          last_failure_at: null,
          last_failure: null,
          secrets: [{ id: issued.secret_id, created_at: NOW, expires_at: null }],
          created_at: NOW,
          updated_at: NOW,
          succeeded_before: 0,
        },
      ];
      return issued;
    },
    async updateWebhook(webhookId, update) {
      await wait(200);
      const w = findWebhook(webhookId);
      if (update.url !== undefined) checkUrl(update.url);
      const resolved = resolveSettings(update, { max: w.max_events_per_delivery, threshold: w.summary_threshold, interval: w.min_interval_seconds });
      const next: DemoWebhook = { ...w, ...resolved, url: update.url ?? w.url, status: update.status ?? w.status, updated_at: NOW };
      webhooks = webhooks.map((x) => (x.id === webhookId ? next : x));
      return toWebhook(next);
    },
    async rotateWebhookSecret(webhookId, overlapHours = 24) {
      await wait(200);
      const w = findWebhook(webhookId);
      if (!Number.isInteger(overlapHours) || overlapHours < 0 || overlapHours > 168) throw new ApiError(400, "旧鍵で署名を続ける期間は 0〜7 日で指定してください。");
      const issued = issueSecret(webhookId);
      const expires = new Date(Date.parse(NOW) + overlapHours * 3_600_000).toISOString();
      // 今の鍵に期限を付け（0 時間ならすぐ外れる）、新しい鍵を今の鍵にする
      const kept = w.secrets
        .map((x) => (x.expires_at === null ? { ...x, expires_at: expires } : x))
        .filter((x) => x.expires_at !== null && Date.parse(x.expires_at) > Date.parse(NOW));
      const next: DemoWebhook = { ...w, secrets: [{ id: issued.secret_id, created_at: NOW, expires_at: null }, ...kept], updated_at: NOW };
      webhooks = webhooks.map((x) => (x.id === webhookId ? next : x));
      return issued;
    },
    async listWebhookDeliveries(webhookId, status, limit = 50) {
      await wait(150);
      findWebhook(webhookId);
      if (!Number.isInteger(limit) || limit < 1 || limit > 200) throw new ApiError(400, "limit は 1〜200 で指定してください。");
      return deliveries
        .filter((d) => deliveryOwner.get(d.id) === webhookId && (!status || d.status === status))
        .sort((a, b) => b.created_at.localeCompare(a.created_at))
        .slice(0, limit);
    },
    async redeliverWebhook(webhookId, deliveryId) {
      await wait(200);
      findWebhook(webhookId);
      const targets = deliveries.filter((d) => deliveryOwner.get(d.id) === webhookId && d.status === "dead" && (!deliveryId || d.id === deliveryId));
      if (deliveryId && targets.length === 0) throw new ApiError(409, "dead の送信だけを再送できます（送信待ち・成功した送信は再送しません）。");
      deliveries = deliveries.map((d) =>
        targets.includes(d) ? { ...d, status: "pending", attempts: 0, next_attempt_at: NOW, dead_at: null, redelivered_by: me.id, redelivered_at: NOW } : d,
      );
      return targets.length;
    },
    async pingWebhook(webhookId) {
      await wait(200);
      const w = findWebhook(webhookId);
      const c = findClient(w.client_ref);
      const id = uuid(1100 + deliveries.length);
      // デモでは、送れる状態なら（有効・クライアントも有効）すぐに届いたことにする。停止中は再開まで送信待ちのまま
      const sendable = w.status === "active" && c.status === "active";
      deliveries = [
        delivery({
          id,
          kind: "ping",
          status: sendable ? "succeeded" : "pending",
          attempts: sendable ? 1 : 0,
          created_at: NOW,
          last_attempt_at: sendable ? NOW : null,
          last_http_status: sendable ? 200 : null,
          succeeded_at: sendable ? NOW : null,
          body: JSON.stringify({ id, type: "ping", created_at: NOW, client_id: c.client_id }, null, 2),
        }),
        ...deliveries,
      ];
      deliveryOwner.set(id, webhookId);
      if (sendable) webhooks = webhooks.map((x) => (x.id === webhookId ? { ...x, last_success_at: NOW } : x));
      return id;
    },
  };
}
