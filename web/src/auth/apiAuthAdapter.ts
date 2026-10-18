// 認証まわりの API アダプタ（/auth・/oauth の JSON API。同一オリジン、Cookie のセッション）。
// 状態を変える要求（POST）には X-JinJi-CSRF ヘッダーに CSRF の要求トークンを付ける（§6.4）。
// トークンは GET /auth/csrf で取り、ログインの応答で新しい閲覧者のトークンに差し替える。CSRF で拒否されたら 1 回だけ取り直して再送する。
import { createApiClient, CSRF_HEADER, type ApiClientOptions } from "../api/client";
import type { Schemas } from "../api/schema.gen";
import { ApiError } from "../data/types";
import type { AuthAdapter, ClientUpdate, LoginResult, PersonSummary, WebhookSettings, WebhookUpdate } from "./types";
import { createPasskey, getPasskey } from "./webauthn";

export { CSRF_HEADER };
export const PROBLEM = {
  csrf: "urn:jinji:problem:csrf",
  loginFailed: "urn:jinji:problem:login-failed",
  mfaRequired: "urn:jinji:problem:mfa-required",
  unavailable: "urn:jinji:problem:unavailable",
  invalidLink: "urn:jinji:problem:invalid-link",
  lastFactor: "urn:jinji:problem:last-factor",
} as const;

type Person = Schemas["PersonResponse"];

function summarize(p: Person): PersonSummary {
  const name = p.display_name ?? ([p.family_name, p.given_name].filter(Boolean).join(" ") || "（氏名非公開）");
  const primary = p.assignments?.find((a) => a.type === "primary");
  const detail = [p.employee_number, primary?.org_unit_name].filter(Boolean).join("・");
  return { id: p.id, name, detail: detail || null };
}

export interface ApiAuthAdapterOptions extends ApiClientOptions {
  /** パスキーの操作（テストで差し替える）。 */
  passkeys?: { create: typeof createPasskey; get: typeof getPasskey };
}

export function createApiAuthAdapter(options: ApiAuthAdapterOptions = {}): AuthAdapter {
  const client = createApiClient(options);
  const passkeys = options.passkeys ?? { create: createPasskey, get: getPasskey };
  let csrf: string | null = null;

  async function csrfToken(): Promise<string> {
    if (csrf === null) {
      csrf = (await client.get("/auth/csrf")).token;
    }
    return csrf;
  }

  /** CSRF のヘッダーを付けて送る。CSRF で拒否されたら（トークンの期限切れ・閲覧者の変化）1 回だけ取り直す。 */
  async function withCsrf<T>(send: (headers: Record<string, string>) => Promise<T>): Promise<T> {
    try {
      return await send({ [CSRF_HEADER]: await csrfToken() });
    } catch (e) {
      if (e instanceof ApiError && e.status === 403 && e.problemType === PROBLEM.csrf) {
        csrf = null;
        return send({ [CSRF_HEADER]: await csrfToken() });
      }
      throw e;
    }
  }

  function afterLogin(result: LoginResult): LoginResult {
    if (result.csrf_token) csrf = result.csrf_token;
    return result;
  }

  return {
    kind: "api",

    async providers() {
      try {
        // ログインが使えない環境（公開 URL が https でない）では /auth/csrf が 404
        await csrfToken();
      } catch (e) {
        if (e instanceof ApiError && e.status === 404) return null;
        throw e;
      }
      return client.get("/auth/providers");
    },

    async me() {
      try {
        return await client.get("/auth/me");
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) return null;
        throw e;
      }
    },

    async logout() {
      await withCsrf((h) => client.post("/auth/logout", {}, h));
      csrf = null;
    },

    async loginPassword(userName, password) {
      return afterLogin(await withCsrf((h) => client.post("/auth/login/password", { body: { user_name: userName, password } }, h)));
    },

    async loginTwoFactor({ code, recoveryCode }) {
      return afterLogin(
        await withCsrf((h) => client.post("/auth/login/two-factor", { body: { code: code ?? null, recovery_code: recoveryCode ?? null } }, h)),
      );
    },

    async loginPasskey() {
      const optionsJson = (await withCsrf((h) => client.post("/auth/login/passkey/options", {}, h))) as Record<string, unknown>;
      const credential = await passkeys.get(optionsJson);
      return afterLogin(await withCsrf((h) => client.post("/auth/login/passkey", { body: { credential, name: null } }, h)));
    },

    async registerPasskey(name) {
      const optionsJson = (await withCsrf((h) => client.post("/auth/account/passkeys/options", {}, h))) as Record<string, unknown>;
      const credential = await passkeys.create(optionsJson);
      await withCsrf((h) => client.post("/auth/account/passkeys", { body: { credential, name: name || null } }, h));
    },

    async setupTwoFactor() {
      return withCsrf((h) => client.post("/auth/account/two-factor/setup", {}, h));
    },

    async enableTwoFactor(code) {
      return (await withCsrf((h) => client.post("/auth/account/two-factor/enable", { body: { code } }, h))).recovery_codes;
    },

    async listPasskeys() {
      return client.get("/auth/account/passkeys");
    },

    async deletePasskey(id) {
      await withCsrf((h) => client.post("/auth/account/passkeys/{credentialId}/delete", { path: { credentialId: id } }, h));
    },

    async listDelegations() {
      try {
        return await client.get("/oauth/delegations");
      } catch (e) {
        // トークン発行（JinJi:Auth:Server:Issuer）を設定していない環境では /oauth が無い
        if (e instanceof ApiError && e.status === 404) return null;
        throw e;
      }
    },

    async revokeDelegation(id) {
      await withCsrf((h) => client.post("/oauth/delegations/{familyId}/revoke", { path: { familyId: id } }, h));
    },

    async displayName(personId) {
      try {
        return summarize(await client.get("/api/v1/people/{id}", { path: { id: personId }, query: {} })).name;
      } catch {
        return null;
      }
    },

    async resetPassword(token, newPassword) {
      await withCsrf((h) => client.post("/auth/reset/password", { body: { token, new_password: newPassword } }, h));
    },

    async resetPasskey(token, name) {
      const optionsJson = (await withCsrf((h) => client.post("/auth/reset/passkey/options", { body: { token } }, h))) as Record<string, unknown>;
      const credential = await passkeys.create(optionsJson);
      await withCsrf((h) => client.post("/auth/reset/passkey", { body: { token, credential, name: name || null } }, h));
    },

    async getConsent(requestId) {
      return client.get("/oauth/requests/{requestId}", { path: { requestId } });
    },

    async decideConsent(requestId, approve) {
      return (await withCsrf((h) => client.post("/oauth/requests/{requestId}/decision", { path: { requestId }, body: { approve } }, h))).redirect_to;
    },

    async listUnboundLogins(includeResolved) {
      return client.get("/auth/admin/unbound-logins", { query: { include_resolved: includeResolved || undefined } });
    },

    async bindLogin(unboundLoginId, personId) {
      return withCsrf((h) =>
        client.post("/auth/admin/login-bindings", { body: { person_id: personId, unbound_login_id: unboundLoginId, provider: null, subject: null } }, h),
      );
    },

    async revokeBinding(bindingId) {
      await withCsrf((h) => client.post("/auth/admin/login-bindings/{bindingId}/revoke", { path: { bindingId } }, h));
    },

    async listLocalAccounts(query) {
      return client.get("/auth/admin/local-accounts", { query: { query: query.trim() || undefined } });
    },

    async disableAccount(accountId) {
      return withCsrf((h) => client.post("/auth/admin/local-accounts/{accountId}/disable", { path: { accountId } }, h));
    },

    async enableAccount(accountId) {
      await withCsrf((h) => client.post("/auth/admin/local-accounts/{accountId}/enable", { path: { accountId } }, h));
    },

    async unlockAccount(accountId) {
      await withCsrf((h) => client.post("/auth/admin/local-accounts/{accountId}/unlock", { path: { accountId } }, h));
    },

    async issueResetLink(accountId, purpose) {
      return withCsrf((h) => client.post("/auth/admin/local-accounts/{accountId}/reset-links", { path: { accountId }, body: { purpose } }, h));
    },

    async searchPeople(query) {
      const r = await client.get("/api/v1/people", { query: { query, limit: "20" } });
      return r.items.map(summarize);
    },

    async peopleByIds(ids) {
      if (ids.length === 0) return [];
      const r = await client.get("/api/v1/people", { query: { ids: [...new Set(ids)].slice(0, 100).join(",") } });
      return r.items.map(summarize);
    },

    async listClients(status) {
      return client.get("/auth/admin/oauth/clients", { query: { status } });
    },

    async setClientStatus(id, action) {
      return withCsrf((h) => client.post("/auth/admin/oauth/clients/{clientRef}/{action}", { path: { clientRef: id, action } }, h));
    },

    async updateClient(id, update: ClientUpdate) {
      const body: Schemas["UpdateClientRequest"] = {
        display_name: null,
        redirect_uris: null,
        resources: null,
        scopes: null,
        fields: null,
        mcp_tools: update.mcpTools ?? null,
        rate_limit_per_client: null,
        clear_rate_limit: null,
      };
      return withCsrf((h) => client.post("/auth/admin/oauth/clients/{clientRef}/update", { path: { clientRef: id }, body }, h));
    },

    async listDisclosureLeads(clientId) {
      return client.get("/auth/admin/oauth/disclosure-leads", { query: { client: clientId } });
    },

    async proposeDisclosureLead(clientId, leadDays, note) {
      const body: Schemas["ProposeDisclosureLeadRequest"] = { lead_days: leadDays, note };
      return withCsrf((h) => client.post("/auth/admin/oauth/clients/{clientRef}/disclosure-leads", { path: { clientRef: clientId }, body }, h));
    },

    async decideDisclosureLead(leadId, approve) {
      return withCsrf((h) =>
        client.post("/auth/admin/oauth/disclosure-leads/{leadId}/{action}", { path: { leadId, action: approve ? "approve" : "reject" } }, h),
      );
    },

    async listCimdDomains() {
      return client.get("/auth/admin/oauth/cimd-domains");
    },

    async addCimdDomain(domain) {
      await withCsrf((h) => client.post("/auth/admin/oauth/cimd-domains", { body: { domain } }, h));
    },

    async removeCimdDomain(domain) {
      await withCsrf((h) => client.post("/auth/admin/oauth/cimd-domains/{domain}/remove", { path: { domain } }, h));
    },

    async listWebhooks() {
      return client.get("/auth/admin/oauth/webhooks");
    },

    async registerWebhook(clientId, url, settings: WebhookSettings) {
      const body: Schemas["RegisterWebhookRequest"] = {
        url,
        max_events_per_delivery: settings.maxEventsPerDelivery ?? null,
        summary_threshold: settings.summaryThreshold ?? null,
        min_interval_seconds: settings.minIntervalSeconds ?? null,
      };
      return withCsrf((h) => client.post("/auth/admin/oauth/clients/{clientRef}/webhook", { path: { clientRef: clientId }, body }, h));
    },

    async updateWebhook(webhookId, update: WebhookUpdate) {
      const body: Schemas["UpdateWebhookRequest"] = {
        url: update.url ?? null,
        status: update.status ?? null,
        max_events_per_delivery: update.maxEventsPerDelivery ?? null,
        summary_threshold: update.summaryThreshold ?? null,
        min_interval_seconds: update.minIntervalSeconds ?? null,
      };
      return withCsrf((h) => client.post("/auth/admin/oauth/webhooks/{webhookId}/update", { path: { webhookId }, body }, h));
    },

    async rotateWebhookSecret(webhookId, overlapHours) {
      const body: Schemas["RotateWebhookSecretRequest"] = { overlap_hours: overlapHours ?? null };
      return withCsrf((h) => client.post("/auth/admin/oauth/webhooks/{webhookId}/rotate-secret", { path: { webhookId }, body }, h));
    },

    async listWebhookDeliveries(webhookId, status, limit) {
      // クエリは文字列で組み立てる（生成した型の limit は number だが、URL の組み立ては文字列だけを扱う）
      const query = { status, limit: limit === undefined ? undefined : String(limit) } as unknown as { status?: string; limit?: number };
      return client.get("/auth/admin/oauth/webhooks/{webhookId}/deliveries", { path: { webhookId }, query });
    },

    async redeliverWebhook(webhookId, deliveryId) {
      const body: Schemas["RedeliverWebhookRequest"] = { delivery_id: deliveryId ?? null };
      return (await withCsrf((h) => client.post("/auth/admin/oauth/webhooks/{webhookId}/redeliver", { path: { webhookId }, body }, h))).redelivered;
    },

    async pingWebhook(webhookId) {
      return (await withCsrf((h) => client.post("/auth/admin/oauth/webhooks/{webhookId}/ping", { path: { webhookId } }, h))).delivery_id;
    },
  };
}
