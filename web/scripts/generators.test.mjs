// 生成物（API の型・デモデータ）がコミット済みのファイルと一致することを確かめる。
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { generate, schemaToTs } from "./gen-api-types.mjs";
import { buildDemoData } from "./build-demo-data.mjs";

const root = new URL("..", import.meta.url).pathname;

describe("gen-api-types", () => {
  it("src/api/schema.gen.ts が openapi/v1.json から再生成した結果と一致する", () => {
    const doc = JSON.parse(readFileSync(`${root}openapi/v1.json`, "utf8"));
    expect(readFileSync(`${root}src/api/schema.gen.ts`, "utf8")).toBe(generate(doc));
  });

  it("null を含む type の配列・oneOf・$ref を型にする", () => {
    expect(schemaToTs({ type: ["null", "string"] })).toBe("string | null");
    expect(schemaToTs({ oneOf: [{ type: "null" }, { $ref: "#/components/schemas/X" }] })).toBe('components["schemas"]["X"] | null');
    expect(schemaToTs({ type: ["null", "array"], items: { type: "string" } })).toBe("string[] | null");
  });

  it("扱えない構文は黙って unknown にせず失敗する", () => {
    expect(() => schemaToTs({ allOf: [] })).toThrow(/未対応/);
    expect(() => schemaToTs({ $ref: "other.json#/x" })).toThrow(/未対応/);
  });
});

describe("build-demo-data", () => {
  const data = buildDemoData(`${root}../samples/messy-corp`);

  it("src/demo/demo-data.json が samples/messy-corp から再生成した結果と一致する", () => {
    const committed = JSON.parse(readFileSync(`${root}src/demo/demo-data.json`, "utf8"));
    expect(committed).toEqual(JSON.parse(JSON.stringify(data)));
  });

  it("保持しない項目（ADR-0002）を含まない", () => {
    const text = JSON.stringify(data);
    for (const forbidden of ["birth", "gender", "address", "生年月日", "性別", "住所", "扶養", "等級", "号俸", "休職理由", "評価"]) {
      expect(text).not.toContain(forbidden);
    }
    const keys = new Set(data.people.flatMap((p) => Object.keys(p)));
    expect([...keys].sort()).toEqual([
      "assignments", "display_name", "employee_number", "employment_type", "extension_number", "family_name", "family_name_kana",
      "family_name_latin", "given_name", "given_name_kana", "given_name_latin", "hire_date", "id", "job_type", "legal_entity_id",
      "remote_work", "site_id", "suspensions", "termination_date", "work_email", "work_mobile_phone",
    ]);
  });

  it("拠点: 組織は最上位だけが拠点を持ち、人の拠点は主務の組織の拠点と違う人だけが持つ", () => {
    const siteIds = new Set(data.sites.map((s) => s.id));
    expect(data.sites.length).toBeGreaterThanOrEqual(7);
    for (const o of data.org_units) {
      if (o.parent_id) expect(o.site_id).toBeNull();
      if (o.site_id) expect(siteIds.has(o.site_id)).toBe(true);
    }
    const own = data.people.filter((p) => p.site_id);
    expect(own.length).toBeGreaterThan(0);
    for (const p of own) expect(siteIds.has(p.site_id)).toBe(true);
    expect(own.length).toBeLessThan(data.people.length / 4);
    expect(data.people.some((p) => p.remote_work === true)).toBe(true);
    // 拠点の所在地は会社の情報で、番地まで持つが個人の住所ではない（人には所在地を持たせない）
    expect(data.people.some((p) => "location" in p)).toBe(false);
  });

  it("未来日付の辞令（内示）を含まない", () => {
    const dates = data.people.flatMap((p) => p.assignments.map((a) => a.valid_from));
    expect(dates.every((d) => d <= data.meta.business_date)).toBe(true);
  });
});
