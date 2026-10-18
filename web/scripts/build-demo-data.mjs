// デモ用データ（UI のデモアダプタが読む静的 JSON）を、架空のサンプル samples/messy-corp から生成する。
//
//   node scripts/build-demo-data.mjs <samples/messy-corp のディレクトリ> <出力 .json>
//
// - 入力は ground_truth.json（正解データ。完全な架空データ）。内線だけは employees_hr_package.csv の
//   保持してよい列（ADR-0002）から補う。保持禁止列（生年月日・住所など）は読まない
// - 拠点は ground_truth の sites（開設日から有効）と closed_sites（閉鎖日の翌日まで。開設日は不明なので 2000-01-01）。
//   組織の拠点は最上位の組織だけが持ち（配下は引き継ぐ）、人の拠点は主務の組織の拠点と違う人（site_override）だけが持つ
// - ground_truth.json は基準日 2026-10-01 の「現在の状態」しか持たないので、時点指定（as_of）のデモのために
//   次の履歴を導出する。導出した値には meta.derivations に説明を残す
//   - 2026-04-01 の一斉異動（辞令の assign）で役職の無い人: 前所属を、人事パッケージが発令前のまま残している所属
//     （HR_STALE_ORG）から取る。分からない人は、同じ法人の別の末端組織を ID のハッシュで決定的に選ぶ（架空の前所属）
//   - 入社・退職・兼務・出向・休職（一時停止）は ground_truth の日付をそのまま有効期間にする
//   - 廃止済みの組織は廃止日の翌日まで有効、開始日は不明なので 2000-01-01 とする
// - 未来日付の辞令（内示）は含めない（一般の閲覧者には API が返さないため）
// - ID は ground_truth の ID から決定的に作る UUID（版 8）
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const BUSINESS_DATE = "2026-10-01";
const OPEN_START = "2000-01-01";

function uuidFor(key) {
  const hex = createHash("sha256").update(`jinji-demo:${key}`).digest("hex");
  const variant = ((parseInt(hex[16], 16) & 0x3) | 0x8).toString(16);
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-8${hex.slice(13, 16)}-${variant}${hex.slice(17, 20)}-${hex.slice(20, 32)}`;
}

function hashIndex(key, n) {
  const hex = createHash("sha256").update(key).digest("hex");
  return parseInt(hex.slice(0, 8), 16) % n;
}

function addDays(date, days) {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

function normalize(text) {
  return String(text ?? "").normalize("NFKC").replace(/\s+/g, "").trim();
}

/** 引用符付きの値を扱う最小の CSV パーサー（UTF-8）。 */
function parseCsv(text) {
  const rows = [];
  let row = [];
  let field = "";
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c === '"' && text[i + 1] === '"') { field += '"'; i++; }
      else if (c === '"') quoted = false;
      else field += c;
    } else if (c === '"') quoted = true;
    else if (c === ",") { row.push(field); field = ""; }
    else if (c === "\n") { row.push(field.replace(/\r$/, "")); rows.push(row); row = []; field = ""; }
    else field += c;
  }
  if (field !== "" || row.length > 0) { row.push(field); rows.push(row); }
  return rows;
}

/** 雇用区分（DB のコード値）。ID のハッシュ（0〜99）で、regular 78・contract 9・entrusted 4・part_time 4・dispatched 3・other 2 に振る。 */
const EMPLOYMENT_TYPE_BANDS = [
  ["regular", 78],
  ["contract", 9],
  ["entrusted", 4],
  ["part_time", 4],
  ["dispatched", 3],
  ["other", 2],
];

function employmentTypeFor(personId, hasInboundSecondment) {
  if (hasInboundSecondment) return "seconded_in";
  let n = hashIndex(`employment_type:${personId}`, 100);
  for (const [code, width] of EMPLOYMENT_TYPE_BANDS) {
    if (n < width) return code;
    n -= width;
  }
  return "regular";
}

/** 職種は主務の組織名のキーワードから決める（上から順に最初に当たったもの）。 */
const JOB_TYPE_KEYWORDS = [
  ["営業", ["営業"]],
  ["物流", ["物流", "倉庫", "配送", "センター"]],
  ["企画・管理", ["経理", "財務", "総務", "人事", "法務", "監査", "企画", "経営"]],
  ["技術", ["開発", "技術", "システム", "情報"]],
  ["製造", ["製造", "工場", "生産", "品質"]],
];

function jobTypeFor(orgName) {
  const name = normalize(orgName);
  for (const [job, words] of JOB_TYPE_KEYWORDS) {
    if (words.some((w) => name.includes(w))) return job;
  }
  return "事務";
}

const LEVELS = {
  P: [
    { key: "division", name: "本部", rank: 1 },
    { key: "department", name: "部", rank: 2 },
    { key: "section", name: "課", rank: 3 },
    { key: "team", name: "チーム", rank: 4 },
  ],
  L: [
    { key: "department", name: "部", rank: 1 },
    { key: "group", name: "グループ", rank: 2 },
  ],
};

export function buildDemoData(sampleDir) {
  const gt = JSON.parse(readFileSync(join(sampleDir, "ground_truth.json"), "utf8"));

  // 人事パッケージの保持してよい列（内線・所属部署）だけを読む
  const hrText = readFileSync(join(sampleDir, "employees_hr_package.csv"), "utf8").replace(/^﻿/, "");
  const [header, ...hrRows] = parseCsv(hrText).filter((r) => r.length > 1);
  const col = (name) => {
    const i = header.indexOf(name);
    if (i < 0) throw new Error(`列がありません: ${name}`);
    return i;
  };
  const extCol = col("内線");
  const deptCol = col("所属部署");
  const hrByPerson = new Map();
  for (const m of gt.mappings["employees_hr_package.csv"]) {
    const r = hrRows[m.row - 1];
    hrByPerson.set(m.person_id, {
      extension: normalize(r[extCol]) || null,
      dept: normalize(r[deptCol]),
    });
  }

  const orgByObservedName = new Map();
  for (const o of gt.orgs) {
    for (const name of [o.name, ...(o.observed_names ?? [])]) {
      orgByObservedName.set(`${o.company}:${normalize(name)}`, o.org_id);
    }
  }

  const companies = gt.companies.map((c) => ({ id: uuidFor(`le:${c.id}`), code: c.id, name: c.name, short_name: c.short }));
  const companyId = new Map(gt.companies.map((c) => [c.id, uuidFor(`le:${c.id}`)]));

  const orgNameById = new Map(gt.orgs.map((o) => [o.org_id, o.name]));

  const siteKey = (company, code) => `site:${company}:${code}`;
  const sites = [
    ...gt.sites.map((s) => ({
      id: uuidFor(siteKey(s.company, s.code)),
      legal_entity_id: companyId.get(s.company),
      code: s.code,
      name: s.name,
      type: s.type,
      prefecture_code: s.prefecture_code ?? null,
      location: s.location ?? null,
      valid_from: s.opened_on ?? OPEN_START,
      valid_until: null,
    })),
    // 閉鎖済みの拠点は正解データに種別・所在地が無い。種別は名前の「倉庫」から物流拠点（SiteCodes の表記揺れの照合と同じ）、所在地は不明とする
    ...(gt.closed_sites ?? []).map((s) => ({
      id: uuidFor(siteKey(s.company, s.code)),
      legal_entity_id: companyId.get(s.company),
      code: s.code,
      name: s.name,
      type: s.type ?? "logistics",
      prefecture_code: s.prefecture_code ?? null,
      location: s.location ?? null,
      valid_from: s.opened_on ?? OPEN_START,
      valid_until: addDays(s.closed_on, 1),
    })),
  ];
  // 拠点コードは法人をまたいで一意（出向先の法人の拠点で働く人がいるので、人の法人では引かない）
  const siteByCode = new Map([...gt.sites, ...(gt.closed_sites ?? [])].map((s) => [s.code, uuidFor(siteKey(s.company, s.code))]));
  const siteId = (code) => {
    if (!code) return null;
    const id = siteByCode.get(code);
    if (!id) throw new Error(`拠点がありません: ${code}`);
    return id;
  };

  const orgUnits = gt.orgs.map((o) => {
    const level = LEVELS[o.company].find((l) => l.rank === o.level) ?? null;
    return {
      id: uuidFor(o.org_id),
      legal_entity_id: companyId.get(o.company),
      code: o.code ?? "",
      name: o.name,
      short_name: null,
      level_key: level?.key ?? null,
      parent_id: o.parent_id ? uuidFor(o.parent_id) : null,
      head_person_id: o.head_person_id ? uuidFor(o.head_person_id) : null,
      site_id: siteId(o.site_code),
      valid_from: OPEN_START,
      valid_until: o.abolished_on ? addDays(o.abolished_on, 1) : null,
    };
  });

  const leafOrgs = (company) =>
    gt.orgs.filter((o) => o.company === company && o.active && o.child_org_ids.length === 0).map((o) => o.org_id);

  // 2026-04-01 の一斉異動
  const massAssign = new Map();
  for (const r of gt.mappings["jirei_2026.csv"]) {
    if (r.date === "2026-04-01" && r.order_type === "assign" && r.effective) {
      massAssign.set(r.person_id, r);
    }
  }

  let derivedFromHr = 0;
  let derivedByHash = 0;
  const people = gt.persons.map((p) => {
    const hr = hrByPerson.get(p.person_id) ?? { extension: null, dept: "" };
    const hired = p.hired_on;
    const end = p.retired_on ? addDays(p.retired_on, 1) : null;
    const assignments = [];
    const move = massAssign.get(p.person_id);
    const isMovedWithoutTitle = move && !move.title && hired < "2026-04-01";
    if (isMovedWithoutTitle) {
      const staleOrg = orgByObservedName.get(`${p.company}:${hr.dept}`);
      let prior;
      if (staleOrg && staleOrg !== p.home_org_id) {
        prior = staleOrg;
        derivedFromHr++;
      } else {
        const candidates = leafOrgs(p.company).filter((id) => id !== p.home_org_id);
        prior = candidates[hashIndex(p.person_id, candidates.length)];
        derivedByHash++;
      }
      assignments.push({ org_unit_id: uuidFor(prior), type: "primary", position_title: null, secondment: "none", valid_from: hired, valid_until: "2026-04-01" });
      assignments.push({ org_unit_id: uuidFor(p.home_org_id), type: "primary", position_title: p.title ?? null, secondment: "none", valid_from: "2026-04-01", valid_until: end });
    } else {
      assignments.push({ org_unit_id: uuidFor(p.home_org_id), type: "primary", position_title: p.title ?? null, secondment: "none", valid_from: hired, valid_until: end });
    }
    for (const c of p.concurrent ?? []) {
      assignments.push({ org_unit_id: uuidFor(c.org_id), type: "concurrent", position_title: c.title || null, secondment: "none", valid_from: c.since, valid_until: end });
    }
    if (p.secondment) {
      assignments.push({ org_unit_id: uuidFor(p.secondment.org_id), type: "concurrent", position_title: null, secondment: "outbound", valid_from: p.secondment.since, valid_until: end });
    }
    const suspensions = [];
    if (p.leave) suspensions.push({ from: p.leave.since, until: null });
    for (const h of p.leave_history ?? []) suspensions.push({ from: h.since, until: h.returned_on ?? null });

    const [givenLatin, familyLatin] = (p.email ?? "").split("@")[0].split(".");
    const cap = (s) => (s ? s[0].toUpperCase() + s.slice(1) : null);
    const name = p.business_name;
    return {
      id: uuidFor(p.person_id),
      employee_number: p.employee_no,
      legal_entity_id: companyId.get(p.company),
      family_name: name.family,
      given_name: name.given,
      family_name_kana: name.kana_family ?? null,
      given_name_kana: name.kana_given ?? null,
      family_name_latin: cap(familyLatin),
      given_name_latin: cap(givenLatin),
      display_name: `${name.family} ${name.given}`,
      work_email: p.email ?? null,
      extension_number: hr.extension,
      work_mobile_phone: null,
      // 主務の組織の拠点と同じ人は持たない（組織から引き継ぐ）
      site_id: p.site_override ? siteId(p.site_code) : null,
      remote_work: p.remote_work ?? null,
      employment_type: employmentTypeFor(p.person_id, assignments.some((a) => a.secondment === "inbound")),
      job_type: jobTypeFor(orgNameById.get(p.home_org_id)),
      hire_date: hired,
      termination_date: p.retired_on ?? null,
      assignments,
      suspensions,
    };
  });

  return {
    meta: {
      description: "jin-ji UI のデモ用データ。samples/messy-corp（完全な架空データ）から scripts/build-demo-data.mjs で生成。本番ビルドには含めない。",
      business_date: BUSINESS_DATE,
      derivations: [
        `2026-04-01 の一斉異動で役職の無い人の前所属: 人事パッケージの発令前の所属から ${derivedFromHr} 名、ID のハッシュで選んだ架空の前所属が ${derivedByHash} 名`,
        "ローマ字表記は社用メールのローカル部から導出",
        "廃止済みの組織の開始日は不明のため 2000-01-01",
        "雇用区分・職種は架空の導出（雇用区分は ID のハッシュ、出向受入の所属があれば seconded_in。職種は主務の組織名のキーワードから）",
        `拠点 ${gt.sites.length}（閉鎖済み ${(gt.closed_sites ?? []).length}）。人ごとの拠点は主務の組織の拠点と違う ${gt.persons.filter((p) => p.site_override).length} 名だけが持つ`,
        "2026-04-01 の兼務の終了（3 名）は終了した兼務先が正解データに無いため含めない",
      ],
      milestones: [
        { date: "2024-04-01", label: "2024 年度" },
        { date: "2025-04-01", label: "組織統合（2025-04）" },
        { date: "2025-10-01", label: "出向・兼務（2025-10）" },
        { date: "2026-03-31", label: "一斉異動の前日" },
        { date: "2026-04-01", label: "一斉異動（2026-04-01）" },
        { date: "2026-07-01", label: "出向（2026-07）" },
        { date: BUSINESS_DATE, label: "基準日" },
      ],
    },
    legal_entities: companies,
    level_schemes: Object.fromEntries(Object.entries(LEVELS).map(([code, levels]) => [companyId.get(code), levels])),
    sites,
    org_units: orgUnits,
    people,
  };
}

const isMain = process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href;
if (isMain) {
  const [sampleDir, output] = process.argv.slice(2);
  if (!sampleDir || !output) {
    console.error("使い方: node scripts/build-demo-data.mjs <samples/messy-corp> <出力 .json>");
    process.exit(2);
  }
  const data = buildDemoData(sampleDir);
  writeFileSync(output, `${JSON.stringify(data, null, 1)}\n`);
  console.log(`${output} を生成しました（組織 ${data.org_units.length}、人 ${data.people.length}）。`);
  for (const d of data.meta.derivations) console.log(`- ${d}`);
}
