import { describe, expect, it } from "vitest";
import { generateLargeDataset } from "./largeDataset";

const data = generateLargeDataset();
const { people, org_units: orgs } = data;
const count = (pick: (p: (typeof people)[number]) => string | null) => {
  const m = new Map<string | null, number>();
  for (const p of people) m.set(pick(p), (m.get(pick(p)) ?? 0) + 1);
  return m;
};

describe("合成データの性質", () => {
  it("同じ引数なら同じ結果になる", () => {
    expect(generateLargeDataset(500)).toEqual(generateLargeDataset(500));
    expect(generateLargeDataset(500, 1)).not.toEqual(generateLargeDataset(500, 2));
  });

  it("入社日は 1985 年〜業務日付の範囲で、6 割ほどが 4 月 1 日入社", () => {
    for (const p of people) {
      expect(p.hire_date >= "1985-01-01" && p.hire_date <= data.meta.business_date).toBe(true);
    }
    const april = people.filter((p) => p.hire_date.endsWith("-04-01")).length / people.length;
    expect(april).toBeGreaterThan(0.55);
    expect(april).toBeLessThan(0.7);
  });

  it("所属の開始は入社日以後で、兼務の開始は max(入社日, 2024-04-01)", () => {
    for (const p of people) {
      for (const a of p.assignments) {
        expect(a.valid_from >= p.hire_date).toBe(true);
        if (a.type === "concurrent") expect(a.valid_from).toBe(p.hire_date > "2024-04-01" ? p.hire_date : "2024-04-01");
      }
    }
  });

  it("退職者の所属は退職日の翌日に終わり、退職日は業務日付の 1〜60 日前", () => {
    const retired = people.filter((p) => p.termination_date);
    expect(retired.length).toBeGreaterThan(10);
    expect(retired.length / people.length).toBeLessThan(0.01);
    const day = (d: string) => Date.parse(`${d}T00:00:00Z`) / 86_400_000;
    for (const p of retired) {
      const diff = day(data.meta.business_date) - day(p.termination_date!);
      expect(diff >= 1 && diff <= 60).toBe(true);
      for (const a of p.assignments) expect(day(a.valid_until!)).toBe(day(p.termination_date!) + 1);
      expect(p.suspensions).toEqual([]);
    }
    for (const p of people.filter((q) => !q.termination_date)) {
      for (const a of p.assignments) expect(a.valid_until).toBeNull();
    }
  });

  it("一時停止は業務日付の 1〜200 日前から、終了なし", () => {
    const suspended = people.filter((p) => p.suspensions.length > 0);
    expect(suspended.length).toBeGreaterThan(10);
    for (const p of suspended) {
      expect(p.suspensions[0]!.until).toBeNull();
      expect(p.suspensions[0]!.from >= p.hire_date && p.suspensions[0]!.from < data.meta.business_date).toBe(true);
    }
  });

  it("雇用区分・職種・勤務用携帯・役職の分布", () => {
    const employment = count((p) => p.employment_type);
    expect(new Set(employment.keys())).toEqual(new Set(["regular", "contract", "entrusted", "part_time", "dispatched", "other"]));
    expect(employment.get("regular")! / people.length).toBeGreaterThan(0.75);
    expect(employment.get("regular")! / people.length).toBeLessThan(0.81);
    expect(count((p) => p.job_type).size).toBe(7);
    const mobiles = people.filter((p) => p.work_mobile_phone);
    expect(mobiles.length / people.length).toBeGreaterThan(0.35);
    expect(mobiles.length / people.length).toBeLessThan(0.45);
    for (const p of mobiles) expect(p.work_mobile_phone).toMatch(/^090-0000-\d{4}$/);
    const titles = count((p) => p.assignments[0]!.position_title);
    expect(titles.get("主任")!).toBeGreaterThan(300);
    expect(titles.get("本部長")).toBeGreaterThan(0);
    expect(titles.get("チームリーダー")).toBeGreaterThan(0);
  });

  it("拠点は 20〜40 で種別・都道府県が散らばり、本部は必ず拠点を持つ。人ごとの拠点は 1 割未満、リモートワークは職種で偏る", () => {
    const { sites } = data;
    expect(sites.length).toBeGreaterThanOrEqual(20);
    expect(sites.length).toBeLessThanOrEqual(40);
    expect(new Set(sites.map((s) => s.type)).size).toBe(8);
    expect(new Set(sites.map((s) => s.prefecture_code)).size).toBeGreaterThan(20);
    const used = new Set([...orgs.map((o) => o.site_id), ...people.map((p) => p.site_id)].filter(Boolean));
    expect(used.size).toBe(sites.length);
    for (const o of orgs.filter((x) => x.level_key === "division")) expect(o.site_id).not.toBeNull();
    const own = people.filter((p) => p.site_id).length / people.length;
    expect(own).toBeGreaterThan(0.03);
    expect(own).toBeLessThan(0.1);
    const rate = (job: string) => {
      const list = people.filter((p) => p.job_type === job);
      return list.filter((p) => p.remote_work).length / list.length;
    };
    expect(rate("技術")).toBeGreaterThan(rate("製造") * 3);
  });

  it("組織長は在籍中で、複数の組織の長は最上位の段の役職になる", () => {
    const byId = new Map(people.map((p) => [p.id, p]));
    const division = orgs.find((o) => o.level_key === "division")!;
    const head = byId.get(division.head_person_id!)!;
    expect(head.termination_date).toBeNull();
    expect(head.assignments[0]!.position_title).toBe("本部長");
  });
});
