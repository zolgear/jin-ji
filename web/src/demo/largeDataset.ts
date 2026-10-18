// 描画の負荷確認用の合成データ（1 万人規模）。完全な架空データで、乱数は固定シード。
import type { DemoDataset, DemoOrgUnit, DemoPerson, DemoSite } from "./model";

function mulberry32(seed: number) {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const FAMILY = [
  ["佐藤", "サトウ", "sato"], ["鈴木", "スズキ", "suzuki"], ["高橋", "タカハシ", "takahashi"], ["田中", "タナカ", "tanaka"],
  ["伊藤", "イトウ", "ito"], ["渡辺", "ワタナベ", "watanabe"], ["山本", "ヤマモト", "yamamoto"], ["中村", "ナカムラ", "nakamura"],
  ["小林", "コバヤシ", "kobayashi"], ["加藤", "カトウ", "kato"], ["吉田", "ヨシダ", "yoshida"], ["山田", "ヤマダ", "yamada"],
  ["佐々木", "ササキ", "sasaki"], ["山口", "ヤマグチ", "yamaguchi"], ["松本", "マツモト", "matsumoto"], ["井上", "イノウエ", "inoue"],
  ["木村", "キムラ", "kimura"], ["林", "ハヤシ", "hayashi"], ["斎藤", "サイトウ", "saito"], ["清水", "シミズ", "shimizu"],
  ["山崎", "ヤマザキ", "yamazaki"], ["森", "モリ", "mori"], ["池田", "イケダ", "ikeda"], ["橋本", "ハシモト", "hashimoto"],
  ["阿部", "アベ", "abe"], ["石川", "イシカワ", "ishikawa"], ["山下", "ヤマシタ", "yamashita"], ["中島", "ナカジマ", "nakajima"],
  ["前田", "マエダ", "maeda"], ["藤田", "フジタ", "fujita"], ["小川", "オガワ", "ogawa"], ["後藤", "ゴトウ", "goto"],
  ["岡田", "オカダ", "okada"], ["長谷川", "ハセガワ", "hasegawa"], ["村上", "ムラカミ", "murakami"], ["近藤", "コンドウ", "kondo"],
  ["石井", "イシイ", "ishii"], ["遠藤", "エンドウ", "endo"], ["青木", "アオキ", "aoki"], ["坂本", "サカモト", "sakamoto"],
] as const;
const GIVEN = [
  ["翔", "ショウ", "sho"], ["陽菜", "ヒナ", "hina"], ["蓮", "レン", "ren"], ["結衣", "ユイ", "yui"], ["大輝", "ダイキ", "daiki"],
  ["美咲", "ミサキ", "misaki"], ["健", "ケン", "ken"], ["彩", "アヤ", "aya"], ["誠", "マコト", "makoto"], ["葵", "アオイ", "aoi"],
  ["拓海", "タクミ", "takumi"], ["さくら", "サクラ", "sakura"], ["大和", "ヤマト", "yamato"], ["優花", "ユウカ", "yuka"], ["悠斗", "ユウト", "yuto"],
  ["莉子", "リコ", "riko"], ["颯太", "ソウタ", "sota"], ["真央", "マオ", "mao"], ["隆", "タカシ", "takashi"], ["裕子", "ユウコ", "yuko"],
  ["直樹", "ナオキ", "naoki"], ["恵", "メグミ", "megumi"], ["浩二", "コウジ", "koji"], ["由美", "ユミ", "yumi"], ["剛", "ツヨシ", "tsuyoshi"],
  ["香織", "カオリ", "kaori"], ["哲也", "テツヤ", "tetsuya"], ["麻衣", "マイ", "mai"], ["亮", "リョウ", "ryo"], ["千尋", "チヒロ", "chihiro"],
  ["博", "ヒロシ", "hiroshi"], ["智子", "トモコ", "tomoko"], ["光", "ヒカル", "hikaru"], ["奈々", "ナナ", "nana"], ["慎也", "シンヤ", "shinya"],
  ["花", "ハナ", "hana"], ["勇気", "ユウキ", "yuki"], ["瞳", "ヒトミ", "hitomi"], ["悟", "サトル", "satoru"], ["紗希", "サキ", "saki"],
] as const;

// 拠点（架空。所在地の番地は 9-9-n）。種別・都道府県が散らばるように 29 拠点
const SITES: ReadonlyArray<readonly [code: string, name: string, type: DemoSite["type"], prefecture: string, city: string]> = [
  ["S01", "東京本社", "head_office", "13", "千代田区"],
  ["S02", "大阪支社", "branch", "27", "大阪市北区"],
  ["S03", "名古屋支店", "branch", "23", "名古屋市中村区"],
  ["S04", "福岡支店", "branch", "40", "福岡市博多区"],
  ["S05", "札幌支店", "branch", "01", "札幌市中央区"],
  ["S06", "仙台支店", "branch", "04", "仙台市青葉区"],
  ["S07", "広島支店", "branch", "34", "広島市中区"],
  ["S10", "横浜工場", "factory", "14", "横浜市鶴見区"],
  ["S11", "静岡工場", "factory", "22", "静岡市清水区"],
  ["S12", "群馬工場", "factory", "10", "太田市"],
  ["S13", "北九州工場", "factory", "40", "北九州市若松区"],
  ["S20", "筑波研究所", "laboratory", "08", "つくば市"],
  ["S21", "京都研究所", "laboratory", "26", "京都市下京区"],
  ["S30", "千葉物流センター", "logistics", "12", "千葉市美浜区"],
  ["S31", "埼玉物流センター", "logistics", "11", "川口市"],
  ["S32", "神戸物流センター", "logistics", "28", "神戸市東灘区"],
  ["S40", "金沢営業所", "sales_office", "17", "金沢市"],
  ["S41", "新潟営業所", "sales_office", "15", "新潟市中央区"],
  ["S42", "岡山営業所", "sales_office", "33", "岡山市北区"],
  ["S43", "高松営業所", "sales_office", "37", "高松市"],
  ["S44", "熊本営業所", "sales_office", "43", "熊本市中央区"],
  ["S45", "鹿児島営業所", "sales_office", "46", "鹿児島市"],
  ["S46", "那覇営業所", "sales_office", "47", "那覇市"],
  ["S47", "盛岡営業所", "sales_office", "03", "盛岡市"],
  ["S48", "長野営業所", "sales_office", "20", "長野市"],
  ["S50", "銀座店", "store", "13", "中央区"],
  ["S51", "梅田店", "store", "27", "大阪市北区"],
  ["S52", "天神店", "store", "40", "福岡市中央区"],
  ["S90", "幕張研修センター", "other", "12", "千葉市美浜区"],
];
// 本部（最上位の組織）の拠点と、部ごとの拠点（null は本部の拠点を引き継ぐ）
const DIVISION_SITES = ["S01", "S20", "S10", "S30", "S01", "S01", "S02", "S21"] as const;
const DEPARTMENT_SITES: ReadonlyArray<ReadonlyArray<string | null>> = [
  [null, "S02", "S03", "S04", "S05", "S06"],
  [null, null, "S21", null, null, "S07"],
  [null, "S11", "S12", "S13", null, null],
  [null, "S31", "S32", null, null, null],
  [null, null, null, null, null, null],
  [null, null, null, null, null, "S90"],
  [null, "S07", "S42", "S43", null, null],
  [null, null, "S20", null, null, null],
];
// 営業本部（第 1 本部）の課: 5 番目の課は営業所、第 1 部の 2〜4 番目の課は店舗
const SALES_OFFICES = ["S40", "S41", "S44", "S45", "S46", "S47", "S48"] as const;
const STORES = ["S50", "S51", "S52"] as const;
/** 職種ごとのリモートワークの対象の割合（有無だけ。理由は持たない）。 */
const REMOTE_RATE: Record<string, number> = { 営業: 0.15, 技術: 0.3, 製造: 0.03, 物流: 0.03, 企画: 0.3, 管理: 0.25, 事務: 0.2 };
// 本部ごとの主な職種
const JOB_TYPES = ["営業", "技術", "製造", "物流", "企画", "管理", "事務"] as const;
const DIVISION_JOBS = ["営業", "技術", "製造", "物流", "企画", "管理", "事務", "技術"] as const;

// 雇用区分（DB のコード値）と割合（%）
const EMPLOYMENT_BANDS: ReadonlyArray<readonly [string, number]> = [
  ["regular", 78], ["contract", 9], ["entrusted", 4], ["part_time", 4], ["dispatched", 3], ["other", 2],
];

const FIRST_HIRE_YEAR = 1985;
const CONCURRENT_FROM = "2024-04-01";
const HEAD_TITLE: Record<string, string> = { division: "本部長", department: "部長", section: "課長", team: "チームリーダー" };
const LEVEL_RANK: Record<string, number> = { division: 1, department: 2, section: 3, team: 4 };

const DAY_MS = 86_400_000;
const toDay = (date: string) => Date.parse(`${date}T00:00:00Z`) / DAY_MS;
const fromDay = (day: number) => new Date(day * DAY_MS).toISOString().slice(0, 10);

const pick = <T,>(rng: () => number, list: readonly T[]): T => list[Math.floor(rng() * list.length)]!;

/** 1 法人・4 段（本部 > 部 > 課 > チーム）で、およそ people 人の合成データを作る。 */
export function generateLargeDataset(people = 10000, seed = 20261012, businessDate = "2026-10-01"): DemoDataset {
  const rng = mulberry32(seed);
  // 拠点・リモートワークは別の乱数列で決める（既存の値の並びを変えないため）
  const siteRng = mulberry32(seed ^ 0x5173);
  const entityId = "00000000-0000-8000-8000-00000000e001";
  const orgs: DemoOrgUnit[] = [];
  let seq = 0;
  const id = (prefix: string) => `${prefix}-0000-8000-8000-${(++seq).toString(16).padStart(12, "0")}`;
  const sites: DemoSite[] = SITES.map(([code, name, type, prefecture, city], i) => ({
    id: `00000000-0000-8000-a000-${(i + 1).toString(16).padStart(12, "0")}`,
    legal_entity_id: entityId,
    code,
    name,
    type,
    prefecture_code: prefecture,
    location: `${city}9-9-${i + 1}`,
    valid_from: `${FIRST_HIRE_YEAR}-01-01`,
    valid_until: null,
  }));
  const siteOf = (code: string | null) => (code ? sites.find((x) => x.code === code)!.id : null);
  const make = (name: string, level: string, parent: string | null, code: string, siteCode: string | null = null): DemoOrgUnit => {
    const org: DemoOrgUnit = {
      id: id("00000000"),
      legal_entity_id: entityId,
      code,
      name,
      short_name: null,
      level_key: level,
      parent_id: parent,
      head_person_id: null,
      site_id: siteOf(siteCode),
      valid_from: `${FIRST_HIRE_YEAR}-01-01`,
      valid_until: null,
    };
    orgs.push(org);
    return org;
  };
  const leaves: DemoOrgUnit[] = [];
  for (let d = 1; d <= 8; d++) {
    const div = make(`第${d}本部`, "division", null, `D${d}`, DIVISION_SITES[d - 1]!);
    for (let b = 1; b <= 6; b++) {
      const dep = make(`第${d}-${b}部`, "department", div.id, `D${d}${b}`, DEPARTMENT_SITES[d - 1]![b - 1]!);
      for (let s = 1; s <= 5; s++) {
        const sectionSite = d !== 1 ? null : s === 5 ? SALES_OFFICES[(b - 1) % SALES_OFFICES.length]! : b === 1 && s >= 2 && s <= 4 ? STORES[s - 2]! : null;
        const sec = make(`第${d}-${b}-${s}課`, "section", dep.id, `D${d}${b}${s}`, sectionSite);
        for (let t = 1; t <= 4; t++) {
          leaves.push(make(`第${d}-${b}-${s}-${t}チーム`, "team", sec.id, `D${d}${b}${s}${t}`));
        }
      }
    }
  }
  const bdDay = toDay(businessDate);
  const firstDay = toDay(`${FIRST_HIRE_YEAR}-01-01`);
  const businessYear = Number(businessDate.slice(0, 4));
  const hireDateOf = (): string => {
    if (rng() < 0.6) {
      // 4 月 1 日入社。業務日付より先なら前年にする
      let year = FIRST_HIRE_YEAR + Math.floor(rng() * (businessYear - FIRST_HIRE_YEAR + 1));
      if (`${year}-04-01` > businessDate) year--;
      return `${year}-04-01`;
    }
    return fromDay(firstDay + Math.floor(rng() * (bdDay - firstDay + 1)));
  };
  const divisionIndex = new Map<string, number>();
  for (const o of orgs) if (o.level_key === "division") divisionIndex.set(o.id, divisionIndex.size);
  const orgById = new Map(orgs.map((o) => [o.id, o]));
  const divisionOf = (o: DemoOrgUnit): number => {
    let cur = o;
    while (cur.parent_id) cur = orgById.get(cur.parent_id)!;
    return divisionIndex.get(cur.id)!;
  };

  const persons: DemoPerson[] = [];
  for (let i = 0; i < people; i++) {
    const [fam, famKana, famLatin] = pick(rng, FAMILY);
    const [giv, givKana, givLatin] = pick(rng, GIVEN);
    const org = pick(rng, leaves);
    const div = divisionOf(org);
    const pid = `00000000-0000-8000-9000-${(i + 1).toString(16).padStart(12, "0")}`;
    const hire = hireDateOf();
    // 拠点: 9 割は主務の組織の拠点を引き継ぎ、残りは人ごとの拠点（別の乱数列）
    const ownSite = siteRng() < 0.06 ? pick(siteRng, sites).id : null;
    // 雇用区分
    let er = Math.floor(rng() * 100);
    let employment = "regular";
    for (const [code, width] of EMPLOYMENT_BANDS) {
      if (er < width) {
        employment = code;
        break;
      }
      er -= width;
    }
    // 職種: 本部の主な職種が 8 割、残りは他の職種
    const mainJob = DIVISION_JOBS[div]!;
    const job = rng() < 0.8 ? mainJob : pick(rng, JOB_TYPES.filter((j) => j !== mainJob));
    const remote = siteRng() < (REMOTE_RATE[job] ?? 0.1);
    const mobile = rng() < 0.4 ? `090-0000-${String(Math.floor(rng() * 10000)).padStart(4, "0")}` : null;
    // 退職者（約 0.5%）: 業務日付の 1〜60 日前。入社がそれより後なら退職者にしない
    let termination: string | null = null;
    if (rng() < 0.005) {
      const t = fromDay(bdDay - 1 - Math.floor(rng() * 60));
      if (t >= hire) termination = t;
    }
    const end = termination ? fromDay(toDay(termination) + 1) : null;
    // 一時停止（約 0.5%）: 業務日付の 1〜200 日前から。退職者・入社前にはしない
    const suspensions: DemoPerson["suspensions"] = [];
    if (rng() < 0.005 && !termination) {
      const from = fromDay(bdDay - 1 - Math.floor(rng() * 200));
      if (from >= hire) suspensions.push({ from, until: null });
    }
    persons.push({
      id: pid,
      employee_number: String(i + 1).padStart(6, "0"),
      legal_entity_id: entityId,
      family_name: fam,
      given_name: giv,
      family_name_kana: famKana,
      given_name_kana: givKana,
      family_name_latin: famLatin[0]!.toUpperCase() + famLatin.slice(1),
      given_name_latin: givLatin[0]!.toUpperCase() + givLatin.slice(1),
      display_name: `${fam} ${giv}`,
      work_email: `${givLatin}.${famLatin}.${i + 1}@example.jp`,
      extension_number: String(1000 + (i % 9000)),
      work_mobile_phone: mobile,
      site_id: ownSite,
      remote_work: remote,
      employment_type: employment,
      job_type: job,
      hire_date: hire,
      termination_date: termination,
      assignments: [{ org_unit_id: org.id, type: "primary", position_title: null, secondment: "none", valid_from: hire, valid_until: end }],
      suspensions,
    });
  }
  // 兼務（約 3%）・出向（約 1%）。兼務・出向の線と詳細パネルの数を、1 万人規模でも確かめられるように。
  // 開始は max(入社日, 2024-04-01)
  const sections = orgs.filter((o) => o.level_key === "section");
  for (const p of persons) {
    const r = rng();
    if (r >= 0.04) continue;
    const secondment = r < 0.01;
    const target = secondment ? pick(rng, leaves) : pick(rng, rng() < 0.5 ? sections : leaves);
    if (target.id === p.assignments[0]!.org_unit_id) continue;
    p.assignments.push({
      org_unit_id: target.id,
      type: "concurrent",
      position_title: null,
      secondment: secondment ? "outbound" : "none",
      valid_from: p.hire_date > CONCURRENT_FROM ? p.hire_date : CONCURRENT_FROM,
      valid_until: p.assignments[0]!.valid_until,
    });
  }
  // 各組織の先頭の（在籍中の）所属者を組織長にする（上位組織は配下の長から）。
  // 複数の組織の長を兼ねる人は、最上位の段の役職にする
  const children = new Map<string, DemoOrgUnit[]>();
  for (const o of orgs) if (o.parent_id) children.set(o.parent_id, [...(children.get(o.parent_id) ?? []), o]);
  const byOrg = new Map<string, DemoPerson[]>();
  for (const p of persons) {
    if (p.termination_date) continue;
    const o = p.assignments[0]!.org_unit_id;
    const list = byOrg.get(o);
    if (list) list.push(p);
    else byOrg.set(o, [p]);
  }
  const headTitle = new Map<string, { rank: number; title: string }>();
  for (const org of [...orgs].reverse()) {
    const members = byOrg.get(org.id) ?? (children.get(org.id) ?? []).flatMap((c) => byOrg.get(c.id) ?? []);
    const head = members[0];
    if (!head) continue;
    org.head_person_id = head.id;
    if (!byOrg.has(org.id)) byOrg.set(org.id, [head]);
    const rank = LEVEL_RANK[org.level_key ?? ""] ?? 4;
    const cur = headTitle.get(head.id);
    if (!cur || rank < cur.rank) headTitle.set(head.id, { rank, title: HEAD_TITLE[org.level_key ?? ""] ?? "チームリーダー" });
  }
  for (const p of persons) {
    const head = headTitle.get(p.id);
    if (head) p.assignments[0]!.position_title = head.title;
    else if (rng() < 0.05) p.assignments[0]!.position_title = "主任";
  }
  return {
    meta: {
      description: "描画の負荷確認用の合成データ（架空）",
      business_date: businessDate,
      derivations: [
        `合成: 組織 ${orgs.length}、人 ${people}（固定シードの乱数で決定的）`,
        "入社日は 1985 年〜業務日付（6 割は 4 月 1 日入社）。雇用区分・職種・役職・勤務用携帯は本部ごとの偏りを持たせた架空の値",
        `拠点 ${sites.length}（本部・部・一部の課が持ち、配下は引き継ぐ）。約 6% の人は人ごとの拠点。リモートワークは職種ごとの割合（3〜30%）`,
        "退職者（約 0.5%、業務日付の 1〜60 日前）と一時停止（約 0.5%）を含む。所属は入社日から始まり、退職者は退職日の翌日に終わる",
      ],
      milestones: [{ date: businessDate, label: "基準日" }],
    },
    legal_entities: [{ id: entityId, code: "S", name: "合成データ株式会社", short_name: "合成データ" }],
    level_schemes: {
      [entityId]: [
        { key: "division", name: "本部", rank: 1 },
        { key: "department", name: "部", rank: 2 },
        { key: "section", name: "課", rank: 3 },
        { key: "team", name: "チーム", rank: 4 },
      ],
    },
    sites,
    org_units: orgs,
    people: persons,
  };
}
