// 現場・IT 部門の直接登録・グループ・素案（/api/v1/field・/groups・/plans。ADR-0013・0014）の型と取得元のインターフェース。
// 範囲・管理元・見える範囲の判定は取得元（API）が行う。UI はボタンの有無を決めるだけで、権限の判定には使わない。
// 素案のメモ・グループの名前などは利用者が書いた文なので、画面ではテキストとして表示する（HTML として解釈しない）。
import type { Schemas } from "../api/schema.gen";

export type FieldMode = Schemas["FieldModeResponse"];
export type FieldDomain = FieldMode["domain"];
export type FieldManagementMode = FieldMode["mode"];
export type FieldSetting = Schemas["FieldSettingResponse"];
export type FieldGrant = Schemas["FieldGrantResponse"];
export type FieldScope = Schemas["FieldScopeResponse"];
export type FieldRegistration = Schemas["FieldRegistrationResponse"];
export type FieldRegistrationStatus = FieldRegistration["status"];
export type FieldRegistrationChange = Schemas["FieldRegistrationChangeResponse"];
export type Group = Schemas["GroupResponse"];
export type GroupDetail = Schemas["GroupDetailResponse"];
export type GroupKind = Group["kind"];
export type GroupRole = Schemas["GroupMemberResponse"]["role"];
export type Plan = Schemas["PlanResponse"];
export type PlanVersion = Schemas["PlanVersionResponse"];
export type PlanSimulation = Schemas["PlanSimulationResponse"];
export type PlanUnit = Schemas["PlanUnitResponse"];
export type PlanRegistrationResult = Schemas["PlanRegistrationResponse"];

/** 素案・直接登録の 1 つの変更（kind・target_id・values）。 */
export interface PlanChange {
  kind: "assignment" | "person" | "org_update" | "org_create" | "org_abolish" | "group_join" | "group_leave";
  target_id: string | null;
  values: Record<string, string | null>;
}

export interface PlanInput {
  title: string;
  note: string | null;
  base_date: string;
  effective_date: string;
  changes: PlanChange[];
}

export interface RegistrationInput {
  effective_date: string;
  note: string | null;
  changes: PlanChange[];
}

export interface GroupInput {
  name: string;
  kind: GroupKind;
  owner_org_unit_id: string | null;
  visibility: "all" | "members";
}

/** 直接登録の状態を進める操作。 */
export type RegistrationAction = "confirm" | "consent" | "withdraw" | "discard";

/** CSV のダウンロード（API は同一オリジンの GET、デモは Blob の URL）。 */
export interface FieldDownload {
  href: string;
  fileName: string | null;
  dispose?: () => void;
}

/** 取得元。API アダプタとデモアダプタが同じ形で実装する（DirectoryAdapter.field）。 */
export interface FieldAdapter {
  modes(): Promise<FieldMode[]>;
  settings(): Promise<FieldSetting[]>;
  proposeMode(domain: FieldDomain, mode: FieldManagementMode, note: string | null): Promise<FieldSetting>;
  decideMode(id: string, approve: boolean): Promise<FieldSetting>;
  grants(): Promise<FieldGrant[]>;
  grant(input: { orgUnitId: string; personId: string; from: string; until: string }): Promise<FieldGrant>;
  revokeGrant(id: string): Promise<FieldGrant>;
  scope(): Promise<FieldScope>;

  registrations(query: { mine: boolean }): Promise<FieldRegistration[]>;
  registration(id: string): Promise<FieldRegistration>;
  register(input: RegistrationInput): Promise<FieldRegistration>;
  transition(id: string, action: RegistrationAction): Promise<FieldRegistration>;

  groups(): Promise<Group[]>;
  group(id: string): Promise<GroupDetail>;
  createGroup(input: GroupInput): Promise<GroupDetail>;
  addMember(groupId: string, personId: string, role: GroupRole): Promise<GroupDetail>;
  removeMember(groupId: string, personId: string): Promise<GroupDetail>;

  plans(): Promise<Plan[]>;
  plan(id: string): Promise<Plan>;
  createPlan(input: PlanInput): Promise<Plan>;
  updatePlan(id: string, input: PlanInput): Promise<Plan>;
  deletePlan(id: string): Promise<void>;
  sharePlan(id: string, personIds: string[]): Promise<Plan>;
  simulate(id: string, versionNo: number | null): Promise<PlanSimulation>;
  versions(id: string): Promise<PlanVersion[]>;
  freeze(id: string): Promise<PlanVersion>;
  recordApproval(id: string, versionNo: number, approvedOn: string, reference: string | null): Promise<void>;
  registerPlan(id: string, versionNo: number | null): Promise<PlanRegistrationResult>;
  changesCsv(id: string, versionNo: number | null): Promise<FieldDownload>;
}

export const DOMAIN_LABEL: Record<FieldDomain, string> = {
  org_structure: "組織の新設・改廃・名称・組織長",
  org_site: "組織の拠点",
  assignment: "異動・役職",
  person_site: "人の拠点・リモートワーク",
  person_contact: "内線・社用携帯",
};

export const MODE_LABEL: Record<FieldManagementMode, string> = { hr: "人事パッケージ", field: "jin-ji（現場）", both: "両方" };

export const REGISTRATION_STATUS_LABEL: Record<FieldRegistrationStatus, string> = {
  draft: "下書き",
  pending_consent: "同意待ち",
  applied: "反映済み",
  withdrawn: "取り消し",
  discarded: "破棄",
  expired: "期限切れ",
};

export const GROUP_KIND_LABEL: Record<GroupKind, string> = { team: "チーム", project: "プロジェクト", committee: "委員会", other: "その他" };

export const GROUP_ROLE_LABEL: Record<GroupRole, string> = { owner: "オーナー", lead: "リーダー", member: "メンバー" };

export const CHANGE_KIND_LABEL: Record<PlanChange["kind"], string> = {
  assignment: "異動・役職",
  person: "拠点・連絡先",
  org_update: "組織の変更",
  org_create: "組織の新設",
  org_abolish: "組織の廃止",
  group_join: "グループに加入",
  group_leave: "グループから脱退",
};

export const VALUE_LABEL: Record<string, string> = {
  org_unit_id: "所属",
  position_title: "役職",
  site_id: "拠点",
  remote_work: "リモートワーク",
  extension_number: "内線",
  work_mobile_phone: "社用携帯",
  code: "コード",
  name: "名称",
  short_name: "略称",
  level_key: "段",
  parent_org_unit_id: "上位組織",
  head_person_id: "組織長",
  person_id: "人",
  role: "役割",
  abolish: "廃止",
};

/** 組織管理者・人事管理者（全社の範囲）か。ボタンの有無にだけ使う。 */
export function managesAllOrgs(session: { kind: string; roles: string[] } | null | undefined): boolean {
  return session?.kind === "person" && (session.roles.includes("org_admin") || session.roles.includes("hr_admin"));
}
