// 修正依頼（/api/v1/corrections）の型・表示名・取得元のインターフェース。
// 修正依頼は、人事パッケージの情報を直せる人（修正担当者 hr_corrector・人事管理者 hr_admin）への情報提供で、
// 起票しても jin-ji の公開データは変わらない（正は人事パッケージ。直されたら次の取込で反映される。概要 §7.1）。
// 理由・メモ・message は利用者・機械が書いた文なので、画面ではテキストとして表示する（HTML として解釈しない・リンクにしない）。
import type { Schemas } from "../api/schema.gen";
import { ApiError, describeError } from "./types";

export type Correction = Schemas["CorrectionResponse"];
export type CorrectionPerson = Schemas["CorrectionPersonResponse"];
export type CorrectionSummary = Schemas["CorrectionSummaryResponse"];
export type CorrectionStatus = Correction["status"];
export type CorrectionOrigin = Correction["origin"];
/** 報告できる項目（勤務地 work_location は廃止して拠点 site・リモートワーク remote_work に置き換えた。ADR-0002）。 */
export type CorrectionField = NonNullable<Schemas["CreateCorrectionRequest"]["field"]>;
export type CreateCorrectionResult = Schemas["CreateCorrectionResponse"];
export type CorrectionOutcome = CreateCorrectionResult["outcome"];

/** 一覧の絞り込み（open: 未対応 / closed: 閉じたもの / all: すべて）。 */
export type CorrectionStatusFilter = "open" | "closed" | "all";

export interface CorrectionQuery {
  status: CorrectionStatusFilter;
  personId?: string | null;
  cursor?: string | null;
  limit?: number;
}

export interface CorrectionPage {
  items: Correction[];
  /** 対象の人の要約（閲覧者に見える項目だけ。見えない項目は null）。 */
  people: CorrectionPerson[];
  nextCursor: string | null;
}

export interface CreateCorrectionInput {
  personId: string;
  field: CorrectionField;
  reason: string;
  suggestedValue?: string | null;
}

export interface CloseCorrectionInput {
  status: "resolved" | "dismissed";
  note?: string | null;
}

/** CSV のダウンロード。href をリンクで開く（API は同一オリジンの GET、デモは Blob の URL）。 */
export interface CorrectionReportDownload {
  href: string;
  /** 保存するファイル名（null なら応答の Content-Disposition に任せる）。 */
  fileName: string | null;
  /** ダウンロードを始めた後の後始末（Blob の URL の解放）。 */
  dispose?: () => void;
}

/**
 * 修正依頼の取得元。API アダプタとデモアダプタが同じ形で実装する（DirectoryAdapter.corrections）。
 * 見える範囲の判定は取得元（API）が行う。UI はロールでボタンの有無を決めるだけで、権限の判定には使わない。
 */
export interface CorrectionAdapter {
  list(query: CorrectionQuery): Promise<CorrectionPage>;
  /** 画面内の通知の件数（修正担当者・人事管理者だけ。それ以外は 403）。 */
  summary(): Promise<CorrectionSummary>;
  create(input: CreateCorrectionInput): Promise<CreateCorrectionResult>;
  /** 対応済み・対応不要にする（修正担当者・人事管理者の人だけ。閉じた依頼は 409）。 */
  close(id: string, input: CloseCorrectionInput): Promise<Correction>;
  /** CSV のレポート（修正担当者・人事管理者の人だけ）。 */
  report(status: CorrectionStatusFilter): Promise<CorrectionReportDownload>;
}

/** 修正担当者のロール（role_grant の hr_corrector）。 */
export const HR_CORRECTOR = "hr_corrector";

/** 修正依頼のすべてを見て対応できるか（修正担当者か人事管理者の人）。 */
export function handlesCorrections(session: { kind: string; roles: string[] } | null | undefined): boolean {
  return session?.kind === "person" && (session.roles.includes(HR_CORRECTOR) || session.roles.includes("hr_admin"));
}

/** 理由・対応のメモの最大の文字数。 */
export const MAX_REASON_LENGTH = 500;
/** 値の提案の最大の文字数。 */
export const MAX_SUGGESTED_VALUE_LENGTH = 200;
/** 「新しい依頼」「長く放置された依頼」の既定の日数（API の summary が返す値が優先）。 */
export const DEFAULT_NEW_WITHIN_DAYS = 7;
export const DEFAULT_STALE_AFTER_DAYS = 14;

/** 値の提案の形（none は提案を受け付けない。正しい内容は理由に書く）。 */
export type SuggestionKind = "none" | "text" | "email" | "phone" | "date" | "code";

/** 報告できる人の項目と表示名・値の提案の形（JinJi.Application の CorrectionFields と同じ。並びは画面の選択肢の順）。 */
export const CORRECTION_FIELDS: ReadonlyArray<{ field: CorrectionField; label: string; kind: SuggestionKind }> = [
  { field: "display_name", label: "表示名", kind: "text" },
  { field: "family_name", label: "姓", kind: "text" },
  { field: "given_name", label: "名", kind: "text" },
  { field: "family_name_kana", label: "姓（フリガナ）", kind: "text" },
  { field: "given_name_kana", label: "名（フリガナ）", kind: "text" },
  { field: "family_name_latin", label: "姓（ローマ字）", kind: "text" },
  { field: "given_name_latin", label: "名（ローマ字）", kind: "text" },
  { field: "work_email", label: "社用メール", kind: "email" },
  { field: "extension_number", label: "内線", kind: "phone" },
  { field: "work_mobile_phone", label: "社用携帯", kind: "phone" },
  { field: "site", label: "拠点", kind: "text" },
  { field: "remote_work", label: "リモートワーク", kind: "none" },
  { field: "assignments", label: "所属・役職", kind: "none" },
  { field: "managers", label: "上長", kind: "none" },
  { field: "employee_number", label: "社員番号", kind: "code" },
  { field: "legal_entity", label: "法人", kind: "none" },
  { field: "employment_type", label: "雇用区分", kind: "text" },
  { field: "job_type", label: "職種", kind: "text" },
  { field: "employment_status", label: "在籍状態", kind: "none" },
  { field: "hire_date", label: "入社年月日", kind: "date" },
  { field: "termination_date", label: "退職年月日", kind: "date" },
  { field: "account_status", label: "アカウント状態", kind: "none" },
  { field: "account_suspension", label: "一時停止の期間", kind: "none" },
  { field: "personnel_actions", label: "発令", kind: "none" },
];

const FIELD_BY_NAME = new Map(CORRECTION_FIELDS.map((f) => [f.field as string, f]));

/** 廃止した項目（既存の依頼の表示用。新しい起票には使えない）。 */
const RETIRED_FIELD_LABEL: Record<string, string> = { work_location: "勤務地（廃止。拠点に置き換え）" };

/** 項目の表示名（未知の項目・取込項目は名前のまま）。 */
export function correctionFieldLabel(field: string): string {
  return FIELD_BY_NAME.get(field)?.label ?? RETIRED_FIELD_LABEL[field] ?? field;
}

export function suggestionKind(field: string): SuggestionKind {
  return FIELD_BY_NAME.get(field)?.kind ?? "none";
}

/** 値の提案を受け付ける項目か。 */
export function acceptsSuggestedValue(field: string): boolean {
  return suggestionKind(field) !== "none";
}

/** 人の詳細から報告できる項目（閲覧者に見える項目だけ。選択肢の順）。 */
export function reportableFields(visibleFields: readonly string[]): Array<{ field: CorrectionField; label: string; kind: SuggestionKind }> {
  const visible = new Set(visibleFields);
  return CORRECTION_FIELDS.filter((f) => visible.has(f.field));
}

/** 値の提案の入力の補足（形の説明）。 */
export const SUGGESTION_HINT: Record<SuggestionKind, string> = {
  none: "",
  text: "",
  email: "メールアドレスの形で入力してください。",
  phone: "数字と + - ( ) 空白で入力してください。",
  date: "yyyy-MM-dd（例: 2026-04-01）で入力してください。",
  code: "英数字と - で入力してください。",
};

export const STATUS_LABEL: Record<CorrectionStatus, string> = {
  open: "未対応",
  resolved: "対応済み",
  dismissed: "対応不要",
  auto_resolved: "解消",
  superseded: "置き換え",
};

/** 状態の補足（解消・置き換えは短い名前だけでは意味が伝わらないので添える）。 */
export const STATUS_NOTE: Partial<Record<CorrectionStatus, string>> = {
  auto_resolved: "取り込み直しで値が変わった",
  superseded: "次の取込で改めて起票",
};

/** CSV の状態の列（API の CorrectionReport.StatusLabel と同じ）。 */
export function statusLabelLong(status: CorrectionStatus): string {
  const note = STATUS_NOTE[status];
  return note ? `${STATUS_LABEL[status]}（${note}）` : STATUS_LABEL[status];
}

export const ORIGIN_LABEL: Record<CorrectionOrigin, string> = {
  ingest: "取込の自動検出",
  user: "人",
  agent: "AI エージェント（委任元の名義）",
};

export const OUTCOME_MESSAGE: Record<CorrectionOutcome, string> = {
  created: "修正依頼を起票しました。",
  endorsed: "同じ項目の未対応の依頼があったので、賛同として数えました。",
  already_reported: "この項目は既に報告済みです（あなたの起票・賛同が数えられています）。",
};

export const FILTER_LABEL: Record<CorrectionStatusFilter, string> = { open: "未対応", closed: "閉じたもの", all: "すべて" };

/** 修正依頼の操作のエラーを利用者に見せる文にする（400 は API の detail をそのまま見せる）。 */
export function describeCorrectionError(error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 400:
        return error.message;
      case 403:
        return "この操作はできません（修正依頼の対応・件数・CSV は修正担当者と人事管理者だけが使えます）。";
      case 404:
        return "対象が見つからないか、参照できません。";
      case 409:
        return `既に閉じられています。一覧を読み込み直してください。${error.message ? `（${error.message}）` : ""}`;
      case 429: {
        const wait = error.retryAfter ? `約 ${Math.max(1, Math.ceil(error.retryAfter / 60))} 分後に` : "時間をおいて";
        return `起票の上限に達しました。${wait}もう一度お試しください。${error.message ? `（${error.message}）` : ""}`;
      }
    }
  }
  return describeError(error);
}

/** 人の ID の短縮（氏名が見えないときの表示）。 */
export function shortId(id: string): string {
  return id.length > 8 ? `${id.slice(0, 8)}…` : id;
}
