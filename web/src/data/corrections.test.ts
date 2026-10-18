import { describe, expect, it } from "vitest";
import {
  acceptsSuggestedValue,
  CORRECTION_FIELDS,
  correctionFieldLabel,
  describeCorrectionError,
  handlesCorrections,
  reportableFields,
  statusLabelLong,
} from "./corrections";
import { ApiError } from "./types";

describe("修正依頼の項目", () => {
  it("報告できる項目は API（CorrectionFields）と同じ 24 項目で、氏名の履歴は含まない", () => {
    expect(CORRECTION_FIELDS).toHaveLength(24);
    expect(new Set(CORRECTION_FIELDS.map((f) => f.field)).size).toBe(24);
    expect(CORRECTION_FIELDS.some((f) => (f.field as string) === "name_history")).toBe(false);
  });

  it("表示名", () => {
    expect(correctionFieldLabel("work_email")).toBe("社用メール");
    expect(correctionFieldLabel("extension_number")).toBe("内線");
    expect(correctionFieldLabel("hire_date")).toBe("入社年月日");
    expect(correctionFieldLabel("assignments")).toBe("所属・役職");
    expect(correctionFieldLabel("site")).toBe("拠点");
    expect(correctionFieldLabel("remote_work")).toBe("リモートワーク");
    // 廃止した項目は、既存の依頼の表示のためにだけ名前を持つ（新しい起票の選択肢には出ない）
    expect(correctionFieldLabel("work_location")).toBe("勤務地（廃止。拠点に置き換え）");
    expect(reportableFields(["work_location", "site"]).map((f) => f.field)).toEqual(["site"]);
    // 未知の項目は名前のまま
    expect(correctionFieldLabel("メール")).toBe("メール");
  });

  it("値の提案を受け付けるのは値が 1 つの項目だけ", () => {
    for (const f of ["display_name", "family_name_kana", "work_email", "extension_number", "work_mobile_phone", "site", "employment_type", "job_type", "hire_date", "termination_date", "employee_number"]) {
      expect(acceptsSuggestedValue(f)).toBe(true);
    }
    for (const f of ["remote_work", "work_location", "legal_entity", "employment_status", "account_status", "account_suspension", "assignments", "managers", "personnel_actions", "unknown"]) {
      expect(acceptsSuggestedValue(f)).toBe(false);
    }
  });

  it("報告できる項目は見える項目だけ", () => {
    const fields = reportableFields(["display_name", "work_email", "name_history", "managers"]).map((f) => f.field);
    expect(fields).toEqual(["display_name", "work_email", "managers"]);
  });

  it("状態の表示名（CSV の列は補足つき）", () => {
    expect(statusLabelLong("open")).toBe("未対応");
    expect(statusLabelLong("superseded")).toBe("置き換え（次の取込で改めて起票）");
  });
});

describe("修正依頼の権限とエラー", () => {
  it("修正担当者・人事管理者の人だけが対応できる", () => {
    expect(handlesCorrections({ kind: "person", roles: ["hr_corrector"] })).toBe(true);
    expect(handlesCorrections({ kind: "person", roles: ["hr_admin"] })).toBe(true);
    expect(handlesCorrections({ kind: "person", roles: [] })).toBe(false);
    expect(handlesCorrections({ kind: "break_glass", roles: ["hr_admin"] })).toBe(false);
    expect(handlesCorrections(null)).toBe(false);
  });

  it("400 は API の detail をそのまま見せ、429 は待つ時間を添える", () => {
    expect(describeCorrectionError(new ApiError(400, "reason に jin-ji が保持しない情報（住所）らしき記述があります。"))).toBe(
      "reason に jin-ji が保持しない情報（住所）らしき記述があります。",
    );
    expect(describeCorrectionError(new ApiError(429, "上限です", null, 3600))).toContain("約 60 分後");
    expect(describeCorrectionError(new ApiError(409, ""))).toContain("既に閉じられています");
  });
});
