import { describe, expect, it } from "vitest";
import { ApiError } from "../data/types";
import demoData from "./demo-data.json";
import { DemoDirectory } from "./directory";
import { generateLargeDataset } from "./largeDataset";
import type { DemoDataset, DemoPerson } from "./model";

const data = demoData as DemoDataset;
const dir = new DemoDirectory(data);
const total = (asOf: string) => Object.values(dir.stats(asOf).headcount).reduce((s, h) => s + h.primary, 0);

describe("DemoDirectory（サンプル）", () => {
  it("2026-04-01 の一斉異動の前後で所属の人数が変わる", () => {
    const before = dir.stats("2026-03-31").headcount;
    const after = dir.stats("2026-04-01").headcount;
    const changed = Object.keys({ ...before, ...after }).filter((id) => (before[id]?.primary ?? 0) !== (after[id]?.primary ?? 0));
    expect(changed.length).toBeGreaterThan(5);
    // 新卒の入社で主務の総数が増える
    expect(total("2026-04-01")).toBeGreaterThan(total("2026-03-31"));
  });

  it("廃止された組織は廃止日まで組織ツリーにあり、翌日から無い", () => {
    const abolished = data.org_units.find((o) => o.valid_until === "2025-04-01")!;
    expect(dir.orgTree("2025-03-31").units.some((u) => u.id === abolished.id)).toBe(true);
    expect(dir.orgTree("2025-04-01").units.some((u) => u.id === abolished.id)).toBe(false);
  });

  it("未来の時点は 403（一般の閲覧者には内示を見せない）", () => {
    expect(() => dir.orgTree("2026-11-01")).toThrow(ApiError);
    try {
      dir.orgTree("2026-11-01");
    } catch (e) {
      expect((e as ApiError).status).toBe(403);
    }
  });

  it("見えない項目は省略され visible_fields に入らない（社用携帯）", () => {
    const p = dir.getPerson(data.people[0]!.id, data.meta.business_date);
    expect(p.visible_fields).not.toContain("work_mobile_phone");
    expect(p.visible_fields).not.toContain("name_history");
    expect(p.work_mobile_phone).toBeNull();
    expect(p.visible_fields).toContain("display_name");
  });

  it("組織単位の制限: 内部監査部（A020）の人は内線・拠点・リモートワークが見えない", () => {
    const audit = data.org_units.find((o) => o.code === "A020")!;
    const member = data.people.find((p) => p.assignments.some((a) => a.org_unit_id === audit.id && a.valid_until === null))!;
    const view = dir.getPerson(member.id, data.meta.business_date);
    expect(view.visible_fields).not.toContain("extension_number");
    expect(view.visible_fields).not.toContain("site");
    expect(view.visible_fields).not.toContain("remote_work");
    expect(view.extension_number).toBeNull();
    expect(view.site).toBeNull();
    expect(view.remote_work).toBeNull();
  });

  it("拠点: 人ごとの拠点 → 主務の組織（無ければ上位組織）の拠点の順に解決し、引き継いだかを返す", () => {
    const today = data.meta.business_date;
    const own = data.people.find((p) => p.site_id && dir.isListed(p) && dir.visibleFields(p).includes("site"))!;
    const ownView = dir.getPerson(own.id, today);
    expect(ownView.site?.id).toBe(own.site_id);
    expect(ownView.site?.inherited).toBe(false);
    const inherit = data.people.find((p) => !p.site_id && dir.isListed(p) && dir.visibleFields(p).includes("site") && p.termination_date === null)!;
    const view = dir.getPerson(inherit.id, today);
    const home = view.assignments!.find((a) => a.type === "primary")!;
    const tree = dir.orgTree(today);
    const unit = tree.units.find((u) => u.id === home.org_unit_id)!;
    expect(view.site?.id).toBe(unit.site?.id);
    expect(view.site?.inherited).toBe(true);
    expect(view.site?.prefecture_name).toMatch(/[都道府県]$/);
    // 組織: 最上位は自分の拠点、配下は上位組織から引き継ぐ
    const child = tree.units.find((u) => u.parent_id && u.site)!;
    expect(child.site?.inherited).toBe(true);
    const root = tree.units.find((u) => !u.parent_id && u.site)!;
    expect(root.site?.inherited).toBe(false);
  });

  it("拠点の一覧: 法人のコード・拠点コードの順。人数は見える人だけ（内部監査部の人は数えない）。閉鎖済みは出ない", () => {
    const today = data.meta.business_date;
    const sites = dir.sites({ asOf: today });
    expect(sites.map((s) => `${s.legal_entity.code}:${s.code}`)).toEqual([...sites.map((s) => `${s.legal_entity.code}:${s.code}`)].sort());
    expect(sites.some((s) => s.name === "川崎倉庫")).toBe(false);
    expect(dir.sites({ asOf: "2025-03-31" }).some((s) => s.name === "川崎倉庫")).toBe(true);
    const hq = sites.find((s) => s.name === "東京本社")!;
    const visible = data.people.filter((p) => dir.isListed(p) && dir.assignmentsAt(p, today).length > 0 && dir.visibleFields(p).includes("site"));
    const atHq = visible.filter((p) => dir.personSite(p, today)?.id === hq.id);
    expect(hq.member_count).toBe(atHq.length);
    expect(hq.remote_work_count).toBe(atHq.filter((p) => p.remote_work).length);
    expect(hq.org_units.length).toBeGreaterThan(0);
    expect(dir.site(hq.id, today)).toEqual(hq);
    expect(dir.sites({ asOf: today, siteTypes: ["logistics"] }).every((s) => s.type === "logistics")).toBe(true);
    expect(dir.sites({ asOf: today, prefectures: ["27"] }).map((s) => s.prefecture_name)).toEqual(["大阪府", "大阪府"]);
    expect(dir.sites({ asOf: today, query: "おおさか" }).length + dir.sites({ asOf: today, query: "大阪" }).length).toBeGreaterThan(0);
    expect(() => dir.site("no-such-site", today)).toThrow(ApiError);
  });

  it("将来の終了日・一時停止の終了予定日は返さない", () => {
    const withEnd: DemoPerson = {
      ...data.people[0]!,
      id: "test-person",
      assignments: [{ ...data.people[0]!.assignments[0]!, valid_from: "2020-01-01", valid_until: "2027-01-01" }],
      suspensions: [{ from: "2026-09-01", until: "2026-12-01" }],
    };
    const d = new DemoDirectory({ ...data, people: [...data.people, withEnd] });
    const view = d.getPerson("test-person", data.meta.business_date);
    expect(view.assignments?.[0]?.valid_until).toBeNull();
    expect(view.account_status).toBe("suspended");
    expect(view.account_suspension).toEqual({ from: "2026-09-01", until: null });
  });

  it("退職後の公開期間（90 日）を過ぎた人は 404", () => {
    const retired = data.people.find((p) => p.termination_date && p.termination_date < "2026-01-01")!;
    expect(() => dir.getPerson(retired.id, data.meta.business_date)).toThrow(/見つかりません/);
    const recent = data.people.find((p) => p.termination_date && p.termination_date > "2026-07-15")!;
    const view = dir.getPerson(recent.id, data.meta.business_date);
    expect(view.employment_status).toBe("terminated");
    expect(view.termination_date).toBe(recent.termination_date);
  });

  it("検索はカタカナとひらがな、全角半角の違いを無視する", () => {
    const target = data.people.find((p) => p.family_name_kana === "スギヤマ")!;
    const hits = dir.search("すぎやま", data.meta.business_date);
    expect(hits.some((p) => p.id === target.id)).toBe(true);
    expect(() => dir.search("す", data.meta.business_date)).toThrow(ApiError);
    expect(() => dir.search("山*", data.meta.business_date)).toThrow(/ワイルドカード/);
  });

  it("上長は所属組織から上にたどった最初の組織長（本人を除く）", () => {
    const org = data.org_units.find((o) => o.head_person_id && o.parent_id)!;
    const head = data.people.find((p) => p.id === org.head_person_id)!;
    const managers = dir.getManagers(head.id, data.meta.business_date);
    expect(managers.every((m) => m.id !== head.id)).toBe(true);
  });

  it("人の一括取得: 指定の順で、見えない・存在しない ID は省く", () => {
    const [a, b] = data.people.filter((p) => dir.isListed(p));
    const got = dir.peopleByIds([b!.id, "no-such-id", a!.id, b!.id], data.meta.business_date);
    expect(got.map((p) => p.id)).toEqual([b!.id, a!.id]);
  });

  it("組織の検索: 完全一致・前方一致・部分一致の順で、limit を超えたら hasMore", () => {
    const r = dir.searchOrgs("営業", data.meta.business_date, 2);
    expect(r.items).toHaveLength(2);
    expect(r.hasMore).toBe(true);
    expect(r.items[0]!.name.startsWith("営業")).toBe(true);
  });

  it("兼務・出向の線を組織の組で集計する", () => {
    const links = dir.stats(data.meta.business_date).links;
    expect(links.some((l) => l.kind === "concurrent")).toBe(true);
    expect(links.some((l) => l.kind === "secondment")).toBe(true);
  });
});

describe("合成 1 万人", () => {
  it("組織 1,000 超・1 万人を数百ミリ秒で集計できる", () => {
    const bigData = generateLargeDataset();
    const retired = bigData.people.filter((p) => p.termination_date).length;
    const big = new DemoDirectory(bigData);
    const started = performance.now();
    const stats = big.stats(big.businessDate);
    const tree = big.orgTree(big.businessDate);
    expect(tree.units.length).toBeGreaterThan(1000);
    expect(Object.values(stats.headcount).reduce((s, h) => s + h.primary, 0)).toBe(10000 - retired);
    expect(performance.now() - started).toBeLessThan(3000);
  });
});
