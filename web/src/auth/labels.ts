// 認証まわりの画面に出す名前（項目・スコープ・MCP ツール・認証の方法）。
// 項目の名前は JinJi.Policy.PersonFields、ツールの名前は JinJi.Application.Access.McpToolNames と揃える（labels.test.ts で突き合わせる）。

/** 人の項目（クライアントの項目の上限・公開範囲で使う名前）。 */
export const FIELD_LABELS: Record<string, string> = {
  employee_number: "社員番号",
  legal_entity: "所属法人",
  display_name: "業務上の氏名",
  family_name: "姓",
  given_name: "名",
  family_name_kana: "姓（フリガナ）",
  given_name_kana: "名（フリガナ）",
  family_name_latin: "姓（ローマ字）",
  given_name_latin: "名（ローマ字）",
  work_email: "社用メール",
  extension_number: "内線番号",
  work_mobile_phone: "社用携帯",
  site: "拠点",
  remote_work: "リモートワークの有無",
  workplace: "階・エリア（建物・階・ゾーン）",
  seat: "席（席番号・位置・階）",
  presence: "今日の所在（外部から届いたもの。「*」には含まれない）",
  employment_type: "雇用区分",
  job_type: "職種",
  hire_date: "入社日",
  termination_date: "退職日",
  employment_status: "在籍状態",
  account_status: "アカウント状態",
  account_suspension: "一時停止の期間",
  assignments: "所属・役職（主務・兼務・出向）",
  managers: "直属の上長",
  personnel_actions: "発令",
  groups: "グループ",
  name_history: "氏名の履歴（人事管理者だけ）",
};

export function fieldLabel(name: string): string {
  return FIELD_LABELS[name] ?? name;
}

/** スコープ。title は短い名前、description は同意画面で意味を伝える文。 */
export const SCOPE_INFO: Record<string, { title: string; description: string }> = {
  "jinji.read": {
    title: "人事情報の参照",
    description: "あなたの権限で見られる人・組織の情報（氏名・所属・連絡先など）を読み取ります。",
  },
  "jinji.propose": {
    title: "取込の確認待ちへの提案・値の誤りの報告",
    description:
      "取込で確認が必要になった項目に解決案を書き添え、あなたの名義で値の誤りを報告（修正依頼）します。公開される人事情報は変わらず、人事の担当者が確かめて人事パッケージで直したときだけ反映されます。",
  },
};

export function scopeInfo(scope: string): { title: string; description: string } {
  return SCOPE_INFO[scope] ?? { title: scope, description: "jin-ji が知らない権限です。" };
}

export interface McpToolInfo {
  name: string;
  label: string;
  kind: "read" | "propose";
}

/** MCP ツール（McpToolNames）。 */
export const MCP_TOOLS: McpToolInfo[] = [
  { name: "search_people", label: "人の検索・組織の所属者の一覧", kind: "read" },
  { name: "get_person", label: "1 人の詳細（上長も）", kind: "read" },
  { name: "get_people", label: "人を ID でまとめて取得", kind: "read" },
  { name: "get_approval_route", label: "承認ルート（申請を誰が承認するか）", kind: "read" },
  { name: "get_org", label: "組織と上位・直下・組織長", kind: "read" },
  { name: "get_org_tree", label: "組織図の部分木", kind: "read" },
  { name: "search_orgs", label: "組織の検索", kind: "read" },
  { name: "list_sites", label: "拠点の一覧と拠点ごとの人数", kind: "read" },
  { name: "list_floors", label: "建物・階と席の埋まり方", kind: "read" },
  { name: "find_vacant_seats", label: "空席の検索（割り当ての無い席。予約の空きではない）", kind: "read" },
  { name: "list_floor_occupants", label: "階の在籍者と席", kind: "read" },
  { name: "get_changes", label: "変更フィード", kind: "read" },
  { name: "get_directory_info", label: "業務日付・組織の段・時点の目盛り", kind: "read" },
  { name: "list_review_proposals", label: "取込の要確認の根拠", kind: "read" },
  { name: "list_corrections", label: "修正依頼の一覧（委任元に見える範囲）", kind: "read" },
  { name: "list_field_registrations", label: "組織・人の直接登録の一覧", kind: "read" },
  { name: "list_groups", label: "チーム・プロジェクトのグループの一覧", kind: "read" },
  { name: "get_group", label: "グループとメンバー", kind: "read" },
  { name: "suggest_review_resolution", label: "要確認への解決の推定（作業領域だけに書く）", kind: "propose" },
  { name: "report_correction", label: "値の誤りの報告（修正依頼。公開データは変えない）", kind: "propose" },
  { name: "draft_field_registration", label: "組織・人の変更の下書き（反映は委任元の確定）", kind: "propose" },
];

export function mcpToolLabel(name: string): string {
  if (name === "*") return "すべてのツール";
  return MCP_TOOLS.find((t) => t.name === name)?.label ?? name;
}

/** 認証の方法（セッションの methods。RFC 8176 の amr に倣う）。 */
export const METHOD_LABELS: Record<string, string> = {
  pwd: "パスワード",
  otp: "認証アプリの確認コード",
  rc: "リカバリコード",
  hwk: "パスキー",
  fed: "外部の認証サービス",
};

export function methodLabel(code: string): string {
  return METHOD_LABELS[code] ?? code;
}

export const CLIENT_STATUS_LABELS: Record<string, string> = {
  active: "有効",
  pending_approval: "承認待ち",
  suspended: "停止中",
  rejected: "却下",
};

export const REGISTRATION_LABELS: Record<string, string> = {
  registered: "事前登録",
  cimd: "CIMD（自己申告の文書）",
};

export const RESOURCE_LABELS: Record<string, string> = {
  api: "REST API",
  mcp: "MCP（AI エージェント）",
};

/** Webhook の状態（クライアントの「停止」と同じ語にそろえる）。 */
export const WEBHOOK_STATUS_LABELS: Record<string, string> = {
  active: "有効",
  disabled: "停止中",
};

/** Webhook の送信の状態。dead は再試行の上限に達して止めたもの（管理画面から再送する）。 */
export const WEBHOOK_DELIVERY_STATUS_LABELS: Record<string, string> = {
  pending: "送信待ち",
  sending: "送信中",
  succeeded: "成功",
  dead: "dead（再試行の上限）",
};

/** Webhook の送信の種類（本文の type）。 */
export const WEBHOOK_DELIVERY_KIND_LABELS: Record<string, string> = {
  changes: "変更",
  summary: "要約",
  ping: "送信の確かめ",
};

/** 認証の管理（/admin?tab=）のタブ。設定の画面の「管理者設定」からも入口として並べる。 */
export type AdminTab = "unbound" | "accounts" | "clients" | "webhooks" | "domains";

export const ADMIN_TAB_LABELS: Record<AdminTab, string> = {
  unbound: "未紐付けのログイン",
  accounts: "ローカルアカウント",
  clients: "API クライアント",
  webhooks: "Webhook",
  domains: "CIMD の許可ドメイン",
};
