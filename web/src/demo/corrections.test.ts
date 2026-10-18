import { describe, expect, it } from "vitest";
import { ApiError } from "../data/types";
import { correctionReportCsv, csvCell, CORRECTION_REPORT_HEADERS } from "./correctionCsv";
import demoData from "./demo-data.json";
import { createDemoCorrections, detectSensitive, type DemoCorrectionViewer } from "./demoCorrections";
import { demoMe } from "./demoViewer";
import { DemoDirectory } from "./directory";
import type { DemoDataset } from "./model";

const data = demoData as DemoDataset;
const me = demoMe(data);
const corrector: DemoCorrectionViewer = { personId: me.id, roles: ["hr_corrector"] };
const member: DemoCorrectionViewer = { personId: me.id, roles: [] };
/** 依頼に何も関わっていない一般の社員。 */
const stranger = data.people.find((p) => p.id !== me.id && p.termination_date === null && p.family_name !== me.family_name)!;

function setup() {
  let viewer = corrector;
  const dir = new DemoDirectory(data);
  const adapter = createDemoCorrections(dir, () => viewer);
  return { adapter, dir, as: (v: DemoCorrectionViewer) => (viewer = v) };
}

async function rejects(p: Promise<unknown>): Promise<ApiError> {
  try {
    await p;
  } catch (e) {
    expect(e).toBeInstanceOf(ApiError);
    return e as ApiError;
  }
  throw new Error("失敗するはずの操作が成功しました");
}

/** 報告できる見える人（自分以外・依頼がまだ無い人）。 */
function target(dir: DemoDirectory) {
  return data.people.find((p) => p.id !== me.id && p.termination_date === null && dir.isListed(p) && p.family_name === "山本")!;
}

describe("修正依頼のデモ", () => {
  it("修正担当者はすべての依頼を見られ、取込由来・人・AI エージェントと各状態を含む", async () => {
    const { adapter } = setup();
    const all = await adapter.list({ status: "all", limit: 100 });
    expect(new Set(all.items.map((c) => c.origin))).toEqual(new Set(["ingest", "user", "agent"]));
    expect(new Set(all.items.map((c) => c.status))).toEqual(new Set(["open", "resolved", "dismissed", "auto_resolved", "superseded"]));
    const open = await adapter.list({ status: "open" });
    expect(open.items.every((c) => c.status === "open")).toBe(true);
    const closed = await adapter.list({ status: "closed" });
    expect(closed.items.every((c) => c.status !== "open")).toBe(true);
    expect(open.items.length + closed.items.length).toBe(all.items.length);
    // 対象の人の要約が付く
    expect(all.people.length).toBeGreaterThan(0);
    expect(all.people.every((p) => all.items.some((c) => c.person_id === p.id))).toBe(true);
  });

  it("件数: 長く放置された依頼と新しい依頼を数える。一般の社員には 403", async () => {
    const { adapter, as } = setup();
    const s = await adapter.summary();
    expect(s.open).toBeGreaterThan(0);
    expect(s.stale).toBeGreaterThan(0);
    expect(s.new).toBeGreaterThan(0);
    expect(s.oldest_created_at).not.toBeNull();
    as(member);
    expect((await rejects(adapter.summary())).status).toBe(403);
  });

  it("一般の社員は自分が起票・賛同した依頼と自分についての依頼だけを見る。起票者と賛同しただけの依頼の理由は伏せる", async () => {
    const { adapter, as } = setup();
    as(member);
    const page = await adapter.list({ status: "all" });
    expect(page.items.length).toBeGreaterThan(0);
    for (const c of page.items) {
      expect(c.reported_by_me || c.endorsed_by_me || c.about_me).toBe(true);
      expect(c.closed_by_person_id).toBeNull();
      if (!c.reported_by_me) expect(c.reported_by_person_id).toBeNull();
      if (!c.reported_by_me && !c.about_me && c.origin !== "ingest") expect(c.reason).toBeNull();
    }
    as({ personId: stranger.id, roles: [] });
    expect((await adapter.list({ status: "all" })).items).toHaveLength(0);
  });

  it("起票 → 別の人は賛同 → 同じ人の再送は既に報告済み。起票しても人の値は変わらない", async () => {
    const { adapter, dir, as } = setup();
    const p = target(dir);
    const before = dir.getPerson(p.id, dir.businessDate).site;
    as(member);
    const created = await adapter.create({ personId: p.id, field: "site", reason: "大阪支社に常駐しています。", suggestedValue: "大阪支社" });
    expect(created.outcome).toBe("created");
    expect(created.correction.reported_by_me).toBe(true);
    expect(created.correction.suggested_value).toBe("大阪支社");
    expect(dir.getPerson(p.id, dir.businessDate).site).toEqual(before);

    const again = await adapter.create({ personId: p.id, field: "site", reason: "もう一度" });
    expect(again.outcome).toBe("already_reported");

    as({ personId: stranger.id, roles: [] });
    const endorsed = await adapter.create({ personId: p.id, field: "site", reason: "私もそう思います。" });
    expect(endorsed.outcome).toBe("endorsed");
    expect(endorsed.correction.id).toBe(created.correction.id);
    expect(endorsed.correction.endorsements).toBe(1);
    expect(endorsed.correction.endorsed_by_me).toBe(true);
    // 賛同しただけの人には理由を見せない
    expect(endorsed.correction.reason).toBeNull();
    // 賛同した人の一覧に出る
    expect((await adapter.list({ status: "open" })).items.map((c) => c.id)).toContain(created.correction.id);
  });

  it("起票の検査: 値の提案を受け付けない項目・形の誤り・保持しない情報・見えない項目・見えない人", async () => {
    const { adapter, dir } = setup();
    const p = target(dir);
    const e1 = await rejects(adapter.create({ personId: p.id, field: "assignments", reason: "兼務が残っています。", suggestedValue: "営業部" }));
    expect(e1.status).toBe(400);
    expect(e1.message).toContain("値の提案を付けられません");
    expect((await rejects(adapter.create({ personId: p.id, field: "hire_date", reason: "違います", suggestedValue: "2026/04/01" }))).message).toContain("yyyy-MM-dd");
    expect((await rejects(adapter.create({ personId: p.id, field: "work_email", reason: "違います", suggestedValue: "not-an-email" }))).status).toBe(400);
    const sensitive = await rejects(adapter.create({ personId: p.id, field: "site", reason: "自宅は東京都港区芝公園4-2-8 です" }));
    expect(sensitive.message).toContain("住所");
    expect((await rejects(adapter.create({ personId: p.id, field: "site", reason: "  " }))).message).toContain("reason を書いてください");
    expect((await rejects(adapter.create({ personId: p.id, field: "site", reason: "あ".repeat(501) }))).message).toContain("500 文字");
    // 社用携帯はデモの閲覧者には見えない項目
    expect((await rejects(adapter.create({ personId: p.id, field: "work_mobile_phone", reason: "違います" }))).message).toContain("見えない項目");
    expect((await rejects(adapter.create({ personId: "00000000-0000-7000-8000-000000000000", field: "site", reason: "違います" }))).status).toBe(404);
  });

  it("24 時間の上限を超えたら 429（Retry-After つき）", async () => {
    const { adapter, dir, as } = setup();
    as(member);
    const people = data.people.filter((p) => p.id !== me.id && p.termination_date === null && dir.isListed(p)).slice(200, 221);
    for (const p of people.slice(0, 20)) {
      expect((await adapter.create({ personId: p.id, field: "job_type", reason: "職種が違います。" })).outcome).toBe("created");
    }
    const e = await rejects(adapter.create({ personId: people[20]!.id, field: "job_type", reason: "職種が違います。" }));
    expect(e.status).toBe(429);
    expect(e.retryAfter).toBe(3600);
  });

  it("閉じる: 修正担当者だけ。閉じた依頼は 409。メモを残す", async () => {
    const { adapter, as } = setup();
    const open = (await adapter.list({ status: "open" })).items[0]!;
    as(member);
    expect((await rejects(adapter.close(open.id, { status: "resolved" }))).status).toBe(403);
    as(corrector);
    const closed = await adapter.close(open.id, { status: "resolved", note: "人事パッケージで修正しました。" });
    expect(closed.status).toBe("resolved");
    expect(closed.closed_kind).toBe("person");
    expect(closed.closed_note).toBe("人事パッケージで修正しました。");
    expect((await rejects(adapter.close(open.id, { status: "dismissed" }))).status).toBe(409);
    expect((await adapter.list({ status: "open" })).items.map((c) => c.id)).not.toContain(open.id);
    const after = await adapter.summary();
    expect(after.open).toBe((await adapter.list({ status: "open", limit: 100 })).items.length);
  });

  it("続きの読み込み（cursor）", async () => {
    const { adapter } = setup();
    const first = await adapter.list({ status: "all", limit: 4 });
    expect(first.items).toHaveLength(4);
    expect(first.nextCursor).not.toBeNull();
    const second = await adapter.list({ status: "all", limit: 4, cursor: first.nextCursor });
    expect(second.items.map((c) => c.id)).not.toContain(first.items[0]!.id);
  });

  it("CSV は修正担当者だけ（一般の社員は 403）", async () => {
    const { adapter, as } = setup();
    as(member);
    expect((await rejects(adapter.report("open"))).status).toBe(403);
  });
});

describe("修正依頼の CSV", () => {
  it("数式として解釈される先頭の文字に ' を前置し、RFC 4180 で引用する", () => {
    expect(csvCell("=SUM(A1:A2)")).toBe("'=SUM(A1:A2)");
    expect(csvCell("+81")).toBe("'+81");
    expect(csvCell("-1")).toBe("'-1");
    expect(csvCell("@cmd")).toBe("'@cmd");
    expect(csvCell("a,b")).toBe('"a,b"');
    expect(csvCell('彼は"本社"と')).toBe('"彼は""本社""と"');
    expect(csvCell("1行目\n2行目")).toBe('"1行目\n2行目"');
    expect(csvCell(" 前後の空白 ")).toBe('" 前後の空白 "');
    expect(csvCell("=a,b")).toBe("\"'=a,b\"");
    expect(csvCell("普通の値")).toBe("普通の値");
  });

  it("UTF-8 BOM・CRLF・API と同じ列", async () => {
    const { adapter } = setup();
    const page = await adapter.list({ status: "all", limit: 100 });
    const csv = correctionReportCsv(page.items, page.people);
    expect(csv.startsWith("﻿")).toBe(true);
    const lines = csv.slice(1).split("\r\n");
    expect(lines.at(-1)).toBe("");
    expect(lines[0]).toBe(CORRECTION_REPORT_HEADERS.join(","));
    expect(lines).toHaveLength(page.items.length + 2);
    // 閉じた依頼の経過日数は空・解消の状態は補足つき
    const auto = page.items.find((c) => c.status === "auto_resolved")!;
    const row = lines.find((l) => l.includes(auto.id))!;
    expect(row.startsWith("解消（取り込み直しで値が変わった）,,")).toBe(true);
  });
});

describe("保持しない情報の検出（API の SensitiveValueDetector と同じ規則）", () => {
  it("生年月日・住所・電話番号らしき記述を見つけ、日付だけは見逃す", () => {
    expect(detectSensitive("1990年4月1日生まれです")).toContain("生年月日");
    expect(detectSensitive("〒105-0011")).toContain("住所");
    expect(detectSensitive("私用は 090-1234-5678 です")).toContain("電話番号");
    expect(detectSensitive("2025/03/31 廃止の組織が残っています")).toEqual([]);
    expect(detectSensitive("内線 4321")).toEqual([]);
  });
});
