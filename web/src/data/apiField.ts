// 直接登録・グループ・素案の API の取得元（/api/v1/field・/groups・/plans）。状態を変える要求には CSRF のヘッダーを付ける。
import { buildUrl, createCsrfSender, type ApiClient } from "../api/client";
import type { FieldAdapter, PlanChange, PlanInput } from "./field";

type Body = Record<string, unknown>;

/** 変更を API の形にする（値は文字列か null）。 */
function changes(list: PlanChange[]) {
  return list.map((c) => ({ kind: c.kind, target_id: c.target_id, values: c.values as Record<string, string> }));
}

function planBody(input: PlanInput) {
  return { title: input.title, note: input.note, base_date: input.base_date, effective_date: input.effective_date, changes: changes(input.changes) };
}

export function createApiField(client: ApiClient, baseUrl = ""): FieldAdapter {
  const withCsrf = createCsrfSender(client);
  // パスの型は生成した schema で決まるが、引数の組み立てを短く書くため、ここだけ緩く呼ぶ
  const post = <T,>(path: string, pathParams: Record<string, string> = {}, body: Body | undefined = {}): Promise<T> =>
    withCsrf((headers) => (client.post as unknown as (p: string, o: object, h: Record<string, string>) => Promise<T>)(path, { path: pathParams, body }, headers));
  const get = <T,>(path: string, pathParams: Record<string, string> = {}, query: Record<string, string | undefined> = {}): Promise<T> =>
    (client.get as unknown as (p: string, o: object) => Promise<T>)(path, { path: pathParams, query });

  return {
    modes: () => get("/api/v1/field/modes"),
    settings: () => get("/api/v1/field/settings"),
    proposeMode: (domain, mode, note) => post("/api/v1/field/settings", {}, { domain, mode, note }),
    decideMode: (id, approve) => post(`/api/v1/field/settings/{settingId}/${approve ? "approve" : "reject"}`, { settingId: id }),
    grants: () => get("/api/v1/field/grants"),
    grant: (i) => post("/api/v1/field/grants", {}, { org_unit_id: i.orgUnitId, person_id: i.personId, from: i.from, until: i.until }),
    revokeGrant: (id) => post("/api/v1/field/grants/{grantId}/revoke", { grantId: id }),
    scope: () => get("/api/v1/field/scope"),

    registrations: (q) => get("/api/v1/field/registrations", {}, { mine: q.mine ? "true" : undefined }),
    registration: (id) => get("/api/v1/field/registrations/{registrationId}", { registrationId: id }),
    register: (i) => post("/api/v1/field/registrations", {}, { effective_date: i.effective_date, note: i.note, changes: changes(i.changes) }),
    transition: (id, action) => post(`/api/v1/field/registrations/{registrationId}/${action}`, { registrationId: id }),

    groups: () => get("/api/v1/groups"),
    group: (id) => get("/api/v1/groups/{groupId}", { groupId: id }),
    createGroup: (i) => post("/api/v1/groups", {}, { name: i.name, kind: i.kind, owner_org_unit_id: i.owner_org_unit_id, visibility: i.visibility }),
    addMember: (groupId, personId, role) => post("/api/v1/groups/{groupId}/members", { groupId }, { person_id: personId, role }),
    removeMember: (groupId, personId) => post("/api/v1/groups/{groupId}/members/{personId}/remove", { groupId, personId }),

    plans: () => get("/api/v1/plans"),
    plan: (id) => get("/api/v1/plans/{planId}", { planId: id }),
    createPlan: (i) => post("/api/v1/plans", {}, planBody(i)),
    updatePlan: (id, i) => post("/api/v1/plans/{planId}/update", { planId: id }, planBody(i)),
    deletePlan: async (id) => {
      await post("/api/v1/plans/{planId}/delete", { planId: id });
    },
    sharePlan: (id, people) => post("/api/v1/plans/{planId}/share", { planId: id }, { person_ids: people }),
    simulate: (id, no) =>
      no === null
        ? get("/api/v1/plans/{planId}/simulation", { planId: id })
        : get("/api/v1/plans/{planId}/versions/{versionNo}/simulation", { planId: id, versionNo: String(no) }),
    versions: (id) => get("/api/v1/plans/{planId}/versions", { planId: id }),
    freeze: (id) => post("/api/v1/plans/{planId}/versions", { planId: id }),
    recordApproval: async (id, no, approvedOn, reference) => {
      await post("/api/v1/plans/{planId}/versions/{versionNo}/approval", { planId: id, versionNo: String(no) }, { approved_on: approvedOn, reference });
    },
    registerPlan: (id, no) => post("/api/v1/plans/{planId}/register", { planId: id }, { version_no: no, indexes: null }),
    changesCsv: async (id, no) => ({
      // Cookie のセッション（同一オリジン）なので、リンクの GET でそのまま落とせる。ファイル名は Content-Disposition に任せる
      href:
        no === null
          ? buildUrl(baseUrl, "/api/v1/plans/{planId}/changes.csv", { planId: id }, {})
          : buildUrl(baseUrl, "/api/v1/plans/{planId}/versions/{versionNo}/changes.csv", { planId: id, versionNo: String(no) }, {}),
      fileName: null,
    }),
  };
}
