// 修正依頼のレポートの CSV（デモ用。API の JinJi.Application の CorrectionReport と同じ列・同じ規則）。
// UTF-8 BOM・CRLF・RFC 4180 の引用。数式として解釈される先頭の文字（= + - @ タブ CR）には ' を前置する。
import { correctionFieldLabel, ORIGIN_LABEL, statusLabelLong, type Correction, type CorrectionPerson, type CorrectionStatusFilter } from "../data/corrections";

export const CORRECTION_REPORT_HEADERS = [
  "状態", "経過日数", "社員番号", "氏名", "法人", "主務の組織", "項目", "理由・内容", "正しい値の提案",
  "起票の経路", "賛同数", "取込のファイル", "取込の行番号", "起票日", "種別", "項目のコード", "依頼 ID", "閉じた日", "対応のメモ",
] as const;

/** 1 つの値の引用と、数式の注入の対策。 */
export function csvCell(value: string): string {
  let v = value;
  if (/^[=+\-@\t\r]/.test(v)) v = `'${v}`;
  return /[",\r\n]/.test(v) || v.startsWith(" ") || v.endsWith(" ") ? `"${v.replaceAll('"', '""')}"` : v;
}

/** 日時を日本時間の日付（yyyy-MM-dd）にする。 */
export function jstDate(iso: string | null): string {
  if (!iso) return "";
  const t = new Date(iso).getTime();
  if (Number.isNaN(t)) return "";
  return new Date(t + 9 * 3600_000).toISOString().slice(0, 10);
}

export function correctionReportRows(items: Correction[], people: CorrectionPerson[]): string[][] {
  const byId = new Map(people.map((p) => [p.id, p]));
  return items.map((c) => {
    const person = c.person_id ? byId.get(c.person_id) : undefined;
    const field = c.field ?? c.source_field;
    return [
      statusLabelLong(c.status),
      c.status === "open" ? String(c.age_days) : "",
      person?.employee_number ?? "",
      person?.display_name ?? "",
      person?.legal_entity_name ?? "",
      person?.primary_org_name ?? "",
      field ? correctionFieldLabel(field) : "",
      c.reason ?? c.message,
      c.suggested_value ?? "",
      ORIGIN_LABEL[c.origin],
      String(c.endorsements),
      c.file_name ?? "",
      c.source_row_number === null ? "" : String(c.source_row_number),
      jstDate(c.created_at),
      c.issue,
      field ?? "",
      c.id,
      jstDate(c.closed_at),
      c.closed_note ?? "",
    ];
  });
}

/** CSV の本文（先頭に BOM。行末は CRLF）。 */
export function correctionReportCsv(items: Correction[], people: CorrectionPerson[]): string {
  const lines = [[...CORRECTION_REPORT_HEADERS], ...correctionReportRows(items, people)].map((row) => row.map(csvCell).join(","));
  return `﻿${lines.map((l) => `${l}\r\n`).join("")}`;
}

/** ダウンロードのファイル名（API と同じ: jinji-corrections-<状態>-<業務日付>.csv）。 */
export function correctionReportFileName(status: CorrectionStatusFilter, businessDate: string): string {
  return `jinji-corrections-${status}-${businessDate.replaceAll("-", "")}.csv`;
}
