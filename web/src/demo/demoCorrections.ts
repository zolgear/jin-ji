// 修正依頼のデモ（架空データ。メモリ上だけで、再読み込みで戻る）。CorrectionAdapter を API と同じ規則で簡略に真似る:
// - 一覧・参照: 修正担当者・人事管理者はすべて、それ以外は自分が起票・賛同した依頼と自分についての依頼だけ
// - 伏せる: 修正担当者・人事管理者以外には、起票者・対応した人を伏せ、賛同しただけの依頼の理由・値の提案・メモも伏せる
// - 起票: 見える人の見える項目だけ。同じ人・項目の未対応の依頼があれば賛同、自分の起票・賛同があれば何もしない。24 時間の上限
// - 理由・メモ・値の提案は保持しない情報（生年月日・住所・電話番号）らしき記述を拒否する（JinJi.Domain の SensitiveValueDetector と同じ規則）
// 本番の判定は API 側にしかない。
import {
  acceptsSuggestedValue,
  CORRECTION_FIELDS,
  correctionFieldLabel,
  DEFAULT_NEW_WITHIN_DAYS,
  DEFAULT_STALE_AFTER_DAYS,
  MAX_REASON_LENGTH,
  MAX_SUGGESTED_VALUE_LENGTH,
  suggestionKind,
  type CloseCorrectionInput,
  type Correction,
  type CorrectionAdapter,
  type CorrectionPage,
  type CorrectionPerson,
  type CorrectionQuery,
  type CorrectionStatusFilter,
  type CorrectionSummary,
  type CreateCorrectionInput,
  type CreateCorrectionResult,
} from "../data/corrections";
import { ApiError } from "../data/types";
import { addDays } from "../lib/date";
import { correctionReportCsv, correctionReportFileName } from "./correctionCsv";
import type { DemoDirectory } from "./directory";
import { demoMe, demoRoles, demoViewerKind } from "./demoViewer";

/** メモリ上の依頼（伏せる前の元）。 */
interface DemoCorrection {
  id: string;
  status: Correction["status"];
  origin: Correction["origin"];
  issue: Correction["issue"];
  message: string;
  person_id: string | null;
  field: string | null;
  source_field: string | null;
  reason: string | null;
  suggested_value: string | null;
  batch_id: string | null;
  file_name: string | null;
  source_row_number: number | null;
  reported_by: string | null;
  client_id: string | null;
  endorsers: string[];
  created_at: string;
  closed_at: string | null;
  closed_kind: Correction["closed_kind"];
  closed_by: string | null;
  closed_note: string | null;
}

export interface DemoCorrectionViewer {
  personId: string;
  roles: string[];
}

/** 1 人あたり 24 時間に起票・賛同できる件数（API の既定と同じ）。 */
export const DEMO_MAX_REPORTS_PER_DAY = 20;

const BIRTH = /生年月日|誕生日|[0-9]{1,3}\s*歳|[0-9]{1,4}\s*[年./-]\s*[0-9]{1,2}\s*[月./-]\s*[0-9]{1,2}\s*日?\s*(生まれ|生)/;
const ADDRESS = /(北海道|東京都|京都府|大阪府|.{2,3}県)\s*\S{1,6}?[市区町村郡]\S{0,12}?[0-9]+\s*[-－丁目番]\s*[0-9]+/;
const POSTAL = /〒\s*[0-9]{3}|(^|[^0-9])[0-9]{3}-[0-9]{4}($|[^0-9])/;
const PHONE = /(^|[^0-9])0[0-9]{1,4}[-(]\s*[0-9]{1,4}[-)]\s*[0-9]{3,4}($|[^0-9])|\+81[-\s]?[0-9]/;

/** 保持しない情報らしき記述の種別（空なら問題なし）。 */
export function detectSensitive(value: string): string[] {
  const text = value.replace(/[​-‍⁠﻿]/g, "").normalize("NFKC");
  const kinds: string[] = [];
  if (BIRTH.test(text)) kinds.push("生年月日");
  if (ADDRESS.test(text) || POSTAL.test(text)) kinds.push("住所");
  if (PHONE.test(text)) kinds.push("電話番号");
  return kinds;
}

/** 自由記述を整える（制御文字を空白に、前後の空白を除く）。 */
function normalize(value: string | null | undefined): string {
  // eslint-disable-next-line no-control-regex -- 制御文字を空白にする（API と同じ整え方）
  return (value ?? "").replace(/[\u0000-\u001F\u007F]/g, " ").trim();
}

function bad(message: string): never {
  throw new ApiError(400, message, "urn:jinji:problem:bad-request");
}

function rejectSensitive(text: string, name: string) {
  const kinds = detectSensitive(text);
  if (kinds.length > 0) {
    bad(
      `${name} に jin-ji が保持しない情報（${kinds.join("・")}）らしき記述があります。その部分を除いて書き直してください（内線・社用携帯の正しい番号は suggested_value に書けます）。`,
    );
  }
}

export function cleanFreeText(value: string | null | undefined, name: string, required: boolean): string {
  const text = normalize(value);
  if (!text) return required ? bad(`${name} を書いてください。`) : "";
  if (text.length > MAX_REASON_LENGTH) bad(`${name} は ${MAX_REASON_LENGTH} 文字以内で書いてください。`);
  rejectSensitive(text, name);
  return text;
}

export function cleanSuggestedValue(field: string, value: string | null | undefined): string | null {
  const text = normalize(value);
  if (!text) return null;
  const name = "suggested_value";
  if (!acceptsSuggestedValue(field)) bad(`項目 ${field}（${correctionFieldLabel(field)}）には値の提案を付けられません。正しい内容は理由に書いてください。`);
  if (text.length > MAX_SUGGESTED_VALUE_LENGTH) bad(`${name} は ${MAX_SUGGESTED_VALUE_LENGTH} 文字以内で指定してください。`);
  switch (suggestionKind(field)) {
    case "email":
      if (!/^[^@\s]{1,64}@[^@\s]+\.[^@\s]+$/.test(text)) bad(`${name} はメールアドレスの形で指定してください。`);
      rejectSensitive(text, name);
      break;
    case "phone":
      if (!/^[0-9+()\- ]{2,30}$/.test(text)) bad(`${name} は電話番号の形（数字と + - ( ) 空白）で指定してください。`);
      break;
    case "date":
      if (!/^\d{4}-\d{2}-\d{2}$/.test(text) || Number.isNaN(Date.parse(`${text}T00:00:00Z`)) || new Date(`${text}T00:00:00Z`).toISOString().slice(0, 10) !== text) {
        bad(`${name} は日付（yyyy-MM-dd）で指定してください。`);
      }
      break;
    case "code":
      if (!/^[A-Za-z0-9-]{1,32}$/.test(text)) bad(`${name} は英数字と - だけで指定してください。`);
      break;
    default:
      rejectSensitive(text, name);
  }
  return text;
}

function uuid(n: number): string {
  return `0193a1c0-0000-7000-8000-${String(n).padStart(12, "0")}`;
}

/** 業務日付の days 日前の日本時間の時刻。 */
function at(businessDate: string, daysAgo: number, time = "10:00:00"): string {
  return `${addDays(businessDate, -daysAgo)}T${time}+09:00`;
}

/** 日本時間の日付の差（日数）。 */
function daysBetween(fromIso: string, today: string): number {
  const from = new Date(new Date(fromIso).getTime() + 9 * 3600_000).toISOString().slice(0, 10);
  return Math.max(0, Math.round((Date.parse(`${today}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86_400_000));
}

/** 架空の修正依頼（取込由来・人・AI エージェント。未対応・対応済み・対応不要・解消・置き換え。長く放置されたものを含む）。 */
function seed(dir: DemoDirectory, meId: string): DemoCorrection[] {
  const today = dir.businessDate;
  const people = dir.data.people.filter((p) => dir.isListed(p) && p.id !== meId && p.termination_date === null);
  const pick = (i: number) => people[i % people.length]!;
  const [a, b, c, d, e, f, g, h, k] = [pick(7), pick(23), pick(41), pick(58), pick(77), pick(96), pick(115), pick(134), pick(152)];
  const batch = uuid(9001);
  const file = "jinji_export_20260801.csv";
  const base = {
    source_field: null,
    reason: null,
    suggested_value: null,
    batch_id: null,
    file_name: null,
    source_row_number: null,
    reported_by: null,
    client_id: null,
    endorsers: [] as string[],
    closed_at: null,
    closed_kind: null,
    closed_by: null,
    closed_note: null,
  };
  return [
    {
      ...base,
      id: uuid(1),
      status: "open",
      origin: "ingest",
      issue: "invalid_value",
      message: "取込項目「メール」の値がメールアドレスの形ではないため、取り込みませんでした。",
      person_id: a.id,
      field: "work_email",
      source_field: "メール",
      batch_id: batch,
      file_name: file,
      source_row_number: 128,
      created_at: at(today, 61, "06:12:00"),
    },
    {
      ...base,
      id: uuid(2),
      status: "open",
      origin: "ingest",
      issue: "suspected_employee_number_typo",
      message: "社員番号が前回の取込の別の人の番号と 1 文字違いです。入力の誤りでないか確かめてください。",
      person_id: b.id,
      field: "employee_number",
      source_field: "社員番号",
      batch_id: batch,
      file_name: file,
      source_row_number: 211,
      created_at: at(today, 61, "06:12:00"),
    },
    {
      ...base,
      id: uuid(3),
      status: "open",
      origin: "user",
      issue: "reported_value",
      message: "jin-ji の利用者が、項目「拠点」の値の誤りを報告しました。",
      person_id: c.id,
      field: "site",
      reason: "10 月から大阪支社に常駐しています。座席表も大阪支社になっています。",
      suggested_value: "大阪支社",
      reported_by: d.id,
      endorsers: [e.id, f.id],
      created_at: at(today, 3, "14:05:00"),
    },
    {
      ...base,
      id: uuid(4),
      status: "open",
      origin: "agent",
      issue: "reported_value",
      message: "AI エージェントが委任元の名義で、項目「内線」の値の誤りを報告しました。",
      person_id: e.id,
      field: "extension_number",
      reason: "内線表（2026 年 9 月版）の番号と違います。",
      suggested_value: "4321",
      reported_by: g.id,
      client_id: "https://agent.example.com/oauth/client.json",
      created_at: at(today, 1, "09:41:00"),
    },
    {
      ...base,
      id: uuid(5),
      status: "open",
      origin: "user",
      issue: "reported_value",
      message: "jin-ji の利用者が、項目「名（ローマ字）」の値の誤りを報告しました。",
      person_id: meId,
      field: "given_name_latin",
      reason: "ローマ字の綴りが社内の名刺・メールの署名と違います。",
      reported_by: h.id,
      created_at: at(today, 9, "17:20:00"),
    },
    {
      ...base,
      id: uuid(6),
      status: "open",
      origin: "user",
      issue: "reported_value",
      message: "jin-ji の利用者が、項目「所属・役職」の値の誤りを報告しました。",
      person_id: f.id,
      field: "assignments",
      reason: "9/1 付で兼務が解かれたはずですが、組織図にまだ兼務先が表示されています。",
      reported_by: meId,
      endorsers: [k.id],
      created_at: at(today, 16, "11:02:00"),
    },
    {
      ...base,
      id: uuid(7),
      status: "resolved",
      origin: "user",
      issue: "reported_value",
      message: "jin-ji の利用者が、項目「職種」の値の誤りを報告しました。",
      person_id: g.id,
      field: "job_type",
      reason: "4 月の異動で職種が企画職に変わっています。",
      suggested_value: "企画職",
      reported_by: h.id,
      endorsers: [meId],
      created_at: at(today, 30, "10:00:00"),
      closed_at: at(today, 20, "15:30:00"),
      closed_kind: "person",
      closed_by: meId,
      closed_note: "人事パッケージで修正しました。次の取込で反映されます。",
    },
    {
      ...base,
      id: uuid(8),
      status: "auto_resolved",
      origin: "ingest",
      issue: "missing_required",
      message: "取込項目「入社日」が空でした。",
      person_id: h.id,
      field: "hire_date",
      source_field: "入社日",
      batch_id: batch,
      file_name: file,
      source_row_number: 77,
      created_at: at(today, 61, "06:12:00"),
      closed_at: at(today, 31, "06:15:00"),
      closed_kind: "auto",
    },
    {
      ...base,
      id: uuid(9),
      status: "dismissed",
      origin: "user",
      issue: "reported_value",
      message: "jin-ji の利用者が、項目「上長」の値の誤りを報告しました。",
      person_id: k.id,
      field: "managers",
      reason: "上長が前の部長のままです。",
      reported_by: a.id,
      endorsers: [meId],
      created_at: at(today, 12, "13:45:00"),
      closed_at: at(today, 10, "09:10:00"),
      closed_kind: "person",
      closed_by: meId,
      closed_note: "発令日（10/1）より前のため、現在の表示で正しいことを確かめました。",
    },
    {
      ...base,
      id: uuid(10),
      status: "superseded",
      origin: "ingest",
      issue: "unknown_org",
      message: "取込項目「所属コード」の組織が見つかりませんでした。",
      person_id: d.id,
      field: "assignments",
      source_field: "所属コード",
      batch_id: uuid(9000),
      file_name: "jinji_export_20260701.csv",
      source_row_number: 302,
      created_at: at(today, 92, "06:10:00"),
      closed_at: at(today, 61, "06:12:00"),
      closed_kind: "auto",
    },
  ];
}

export function createDemoCorrections(
  dir: DemoDirectory,
  viewerOf: () => DemoCorrectionViewer = () => ({ personId: demoMe(dir.data).id, roles: demoRoles(demoViewerKind()) }),
  now: () => Date = () => new Date(`${dir.businessDate}T10:00:00+09:00`),
): CorrectionAdapter {
  const today = dir.businessDate;
  const items: DemoCorrection[] = seed(dir, viewerOf().personId);
  /** 起票・賛同の記録（24 時間の上限の判定）。 */
  const reports: Array<{ by: string; at: number }> = [];
  let next = 100;

  const later = <T,>(fn: () => T): Promise<T> =>
    new Promise((resolve, reject) => {
      queueMicrotask(() => {
        try {
          resolve(fn());
        } catch (e) {
          reject(e);
        }
      });
    });

  const handles = (v: DemoCorrectionViewer) => v.roles.includes("hr_corrector") || v.roles.includes("hr_admin");

  const visibleTo = (v: DemoCorrectionViewer, r: DemoCorrection) =>
    handles(v) || r.reported_by === v.personId || r.endorsers.includes(v.personId) || r.person_id === v.personId;

  /** 閲覧者に見せてよい形にする（API の CorrectionRequestService.Mask と同じ規則）。 */
  const mask = (v: DemoCorrectionViewer, r: DemoCorrection): Correction => {
    const handler = handles(v);
    const reporter = r.reported_by === v.personId;
    const about = r.person_id === v.personId;
    const content = handler || reporter || about || r.origin === "ingest";
    return {
      id: r.id,
      status: r.status,
      origin: r.origin,
      issue: r.issue,
      message: r.message,
      person_id: r.person_id,
      field: r.field,
      source_field: r.source_field,
      reason: content ? r.reason : null,
      suggested_value: content ? r.suggested_value : null,
      batch_id: r.batch_id,
      file_name: r.file_name,
      source_row_number: r.source_row_number,
      reported_by_person_id: handler || reporter ? r.reported_by : null,
      reported_via_client_id: handler || reporter ? r.client_id : null,
      reported_by_me: reporter,
      endorsed_by_me: r.endorsers.includes(v.personId),
      about_me: about,
      endorsements: r.endorsers.length,
      created_at: r.created_at,
      age_days: daysBetween(r.created_at, today),
      closed_at: r.closed_at,
      closed_kind: r.closed_kind,
      closed_by_person_id: handler ? r.closed_by : null,
      closed_note: content ? r.closed_note : null,
    };
  };

  /** 対象の人の要約（閲覧者に見える項目だけ）。 */
  const summarize = (personId: string): CorrectionPerson | null => {
    let p;
    try {
      p = dir.getPerson(personId, today);
    } catch {
      return null;
    }
    const name = p.display_name ?? ([p.family_name, p.given_name].filter(Boolean).join(" ") || null);
    const primary = p.assignments?.find((a) => a.type === "primary");
    return {
      id: p.id,
      display_name: name,
      employee_number: p.employee_number,
      legal_entity_name: p.legal_entity?.name ?? null,
      primary_org_name: primary?.org_unit_name ?? null,
    };
  };

  const matches = (filter: CorrectionStatusFilter, r: DemoCorrection) =>
    filter === "all" || (filter === "open" ? r.status === "open" : r.status !== "open");

  const select = (v: DemoCorrectionViewer, filter: CorrectionStatusFilter, personId?: string | null) =>
    items.filter((r) => visibleTo(v, r) && matches(filter, r) && (!personId || r.person_id === personId)).sort((x, y) => x.id.localeCompare(y.id));

  const peopleOf = (rows: Correction[]) =>
    [...new Set(rows.map((r) => r.person_id).filter((id): id is string => id !== null))]
      .map(summarize)
      .filter((p): p is CorrectionPerson => p !== null)
      .sort((x, y) => x.id.localeCompare(y.id));

  const requireHandler = (v: DemoCorrectionViewer, what: string) => {
    if (!handles(v)) throw new ApiError(403, `${what}は修正担当者と人事管理者だけです。`, "urn:jinji:problem:forbidden");
  };

  return {
    list: (q: CorrectionQuery) =>
      later((): CorrectionPage => {
        const v = viewerOf();
        const rows = select(v, q.status, q.personId);
        const start = q.cursor ? Number.parseInt(q.cursor, 10) : 0;
        if (!Number.isFinite(start) || start < 0) bad("cursor が不正です。");
        const limit = Math.min(Math.max(q.limit ?? 50, 1), 100);
        const page = rows.slice(start, start + limit).map((r) => mask(v, r));
        return { items: page, people: peopleOf(page), nextCursor: start + limit < rows.length ? String(start + limit) : null };
      }),

    summary: () =>
      later((): CorrectionSummary => {
        const v = viewerOf();
        requireHandler(v, "修正依頼の件数を見られるの");
        const open = items.filter((r) => r.status === "open").map((r) => mask(v, r));
        const oldest = open.map((r) => r.created_at).sort()[0] ?? null;
        return {
          open: open.length,
          new: open.filter((r) => r.age_days < DEFAULT_NEW_WITHIN_DAYS).length,
          stale: open.filter((r) => r.age_days >= DEFAULT_STALE_AFTER_DAYS).length,
          new_within_days: DEFAULT_NEW_WITHIN_DAYS,
          stale_after_days: DEFAULT_STALE_AFTER_DAYS,
          oldest_created_at: oldest,
        };
      }),

    create: (input: CreateCorrectionInput) =>
      later((): CreateCorrectionResult => {
        const v = viewerOf();
        if (!CORRECTION_FIELDS.some((f) => f.field === input.field)) bad("field は jin-ji が保持する人の項目から指定してください。");
        const reason = cleanFreeText(input.reason, "reason", true);
        const suggested = cleanSuggestedValue(input.field, input.suggestedValue);
        const person = dir.getPerson(input.personId, today); // 見えなければ 404
        if (!person.visible_fields.includes(input.field)) {
          bad(`項目 ${input.field} は見えない項目なので報告できません（見えている項目だけを報告できます）。`);
        }
        const t = now().getTime();
        if (reports.filter((r) => r.by === v.personId && r.at > t - 86_400_000).length >= DEMO_MAX_REPORTS_PER_DAY) {
          throw new ApiError(429, `修正依頼の起票・賛同は 1 人あたり 24 時間に ${DEMO_MAX_REPORTS_PER_DAY} 件までです。時間をおいてください。`, "urn:jinji:problem:rate-limited", 3600);
        }
        const open = items.find((r) => r.status === "open" && r.person_id === input.personId && r.field === input.field && r.origin !== "ingest");
        if (open && (open.reported_by === v.personId || open.endorsers.includes(v.personId))) {
          return { outcome: "already_reported", correction: mask(v, open) };
        }
        reports.push({ by: v.personId, at: t });
        if (open) {
          open.endorsers.push(v.personId);
          return { outcome: "endorsed", correction: mask(v, open) };
        }
        const label = correctionFieldLabel(input.field);
        const created: DemoCorrection = {
          id: uuid(next++),
          status: "open",
          origin: "user",
          issue: "reported_value",
          message: `jin-ji の利用者が、項目「${label}」の値の誤りを報告しました。`,
          person_id: input.personId,
          field: input.field,
          source_field: null,
          reason,
          suggested_value: suggested,
          batch_id: null,
          file_name: null,
          source_row_number: null,
          reported_by: v.personId,
          client_id: null,
          endorsers: [],
          created_at: now().toISOString(),
          closed_at: null,
          closed_kind: null,
          closed_by: null,
          closed_note: null,
        };
        items.push(created);
        return { outcome: "created", correction: mask(v, created) };
      }),

    close: (id: string, input: CloseCorrectionInput) =>
      later((): Correction => {
        const v = viewerOf();
        requireHandler(v, "修正依頼を閉じられるの");
        const r = items.find((x) => x.id === id);
        if (!r) throw new ApiError(404, "見つかりません。", "urn:jinji:problem:not-found");
        if (input.status !== "resolved" && input.status !== "dismissed") bad("status は resolved / dismissed で指定してください。");
        const note = cleanFreeText(input.note, "note", false);
        if (r.status !== "open") throw new ApiError(409, "この修正依頼は既に閉じられています。", "urn:jinji:problem:conflict");
        r.status = input.status;
        r.closed_at = now().toISOString();
        r.closed_kind = "person";
        r.closed_by = v.personId;
        r.closed_note = note || null;
        return mask(v, r);
      }),

    report: (status: CorrectionStatusFilter) =>
      later(() => {
        const v = viewerOf();
        requireHandler(v, "修正依頼のレポートを出せるの");
        const rows = select(v, status).map((r) => mask(v, r));
        const csv = correctionReportCsv(rows, peopleOf(rows));
        const href = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
        return { href, fileName: correctionReportFileName(status, today), dispose: () => URL.revokeObjectURL(href) };
      }),
  };
}
