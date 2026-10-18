// デモの「API 相当」の判定と投影。jin-ji の PersonProjector・OrgDirectoryService の規則（概要 §5.6）を、
// 一般の閲覧者（人事管理者でない社員）から見た場合に限って簡略に真似る。本番の判定は API 側にしかない。
import { addDays, inPeriod } from "../lib/date";
import { matchesAny, simpleSearchKey, validateQuery } from "../lib/search";
import { prefectureName } from "../lib/labels";
import { ApiError, type Assignment, type OrgLink, type OrgStats, type OrgTree, type Person, type Site, type SiteQuery, type SiteRef } from "../data/types";
import type { DemoDataset, DemoOrgUnit, DemoPerson, DemoSite } from "./model";

/** 退職後に一般公開を続ける日数（JinJi:Directory:RetiredPublicDays の既定）。 */
export const RETIRED_PUBLIC_DAYS = 90;

/** PersonResponse の項目名（visible_fields に入る名前）。 */
export const PERSON_FIELDS = [
  "employee_number", "legal_entity", "display_name", "family_name", "given_name", "family_name_kana", "given_name_kana",
  "family_name_latin", "given_name_latin", "work_email", "extension_number", "work_mobile_phone", "site",
  "remote_work", "employment_type", "job_type", "hire_date", "termination_date", "employment_status", "account_status",
  "account_suspension", "assignments", "managers", "personnel_actions", "name_history",
] as const;
export type PersonField = (typeof PERSON_FIELDS)[number];

/** デモの閲覧者に当てはまる公開範囲（概要 §5.2 の例に倣った架空のポリシー）。 */
export interface DemoPolicy {
  /** 誰についても見えない項目（例: 社用携帯は部内のみ → デモの閲覧者は部外）。 */
  hiddenFields: PersonField[];
  /** 組織単位の制限: この組織コード（配下を含む）に所属する人の、これらの項目は見えない。 */
  orgRestrictions: Array<{ orgCode: string; fields: PersonField[] }>;
}

export const DEFAULT_DEMO_POLICY: DemoPolicy = {
  // 社用携帯は部内のみ・発令の履歴と氏名の履歴は人事管理者のみ、という想定
  hiddenFields: ["work_mobile_phone", "personnel_actions", "name_history"],
  // 内部監査部（A020）の人の内線・拠点・リモートワークの有無は部内のみ、という想定
  orgRestrictions: [{ orgCode: "A020", fields: ["extension_number", "site", "remote_work"] }],
};

export interface PersonListResult {
  items: Person[];
  nextCursor: string | null;
}

export class DemoDirectory {
  readonly data: DemoDataset;
  readonly businessDate: string;
  private readonly policy: DemoPolicy;
  private readonly orgById = new Map<string, DemoOrgUnit>();
  private readonly personById = new Map<string, DemoPerson>();
  private readonly siteById = new Map<string, DemoSite>();
  private readonly entityById: Map<string, DemoDataset["legal_entities"][number]>;
  private readonly visibleCache = new Map<string, PersonField[]>();

  constructor(data: DemoDataset, policy: DemoPolicy = DEFAULT_DEMO_POLICY) {
    this.data = data;
    this.policy = policy;
    this.businessDate = data.meta.business_date;
    for (const o of data.org_units) this.orgById.set(o.id, o);
    for (const p of data.people) this.personById.set(p.id, p);
    for (const s of data.sites) this.siteById.set(s.id, s);
    this.entityById = new Map(data.legal_entities.map((e) => [e.id, e]));
  }

  /** 組織（期間に関わらず）。 */
  orgUnit(id: string): DemoOrgUnit | undefined {
    return this.orgById.get(id);
  }

  /** 法人。 */
  legalEntity(id: string): DemoDataset["legal_entities"][number] | undefined {
    return this.entityById.get(id);
  }

  /** その時点に有効な拠点。 */
  siteAt(id: string, asOf: string): DemoSite | undefined {
    const site = this.siteById.get(id);
    return site && inPeriod(asOf, site.valid_from, site.valid_until) ? site : undefined;
  }

  private siteRef(site: DemoSite, inherited: boolean): SiteRef {
    return {
      id: site.id,
      legal_entity_id: site.legal_entity_id,
      code: site.code,
      name: site.name,
      type: site.type,
      prefecture_code: site.prefecture_code,
      prefecture_name: prefectureName(site.prefecture_code),
      inherited,
    };
  }

  /** 組織の拠点: 自分の拠点、無ければ上位組織の拠点（inherited = true）。拠点がその時点で無ければ null。 */
  orgSite(orgId: string, asOf: string): SiteRef | null {
    for (const org of this.ancestors(orgId)) {
      if (org.site_id) {
        const site = this.siteAt(org.site_id, asOf);
        return site ? this.siteRef(site, org.id !== orgId) : null;
      }
    }
    return null;
  }

  /**
   * 人の拠点（API の OrgSnapshot.SiteOf と同じ規則）: 人ごとの拠点があればその拠点、無ければ主務（出向でない主務を優先）の組織の拠点
   * （inherited = true）。人ごとの拠点がその時点で無ければ null（組織の拠点には戻さない）。
   */
  personSite(person: DemoPerson, asOf: string): SiteRef | null {
    if (person.site_id) {
      const site = this.siteAt(person.site_id, asOf);
      return site ? this.siteRef(site, false) : null;
    }
    const current = this.assignmentsAt(person, asOf);
    const home = current.find((a) => a.type === "primary" && a.secondment === "none") ?? current.find((a) => a.type === "primary");
    const site = home ? this.orgSite(home.org_unit_id, asOf) : null;
    return site ? { ...site, inherited: true } : null;
  }

  /** 未来の時点は人事管理者だけ（デモの閲覧者は一般社員なので 403）。 */
  checkAsOf(asOf: string): void {
    if (asOf > this.businessDate) {
      throw new ApiError(403, "未来の時点は人事管理者だけが指定できます。", "urn:jinji:problem:forbidden");
    }
  }

  /** 退職後の公開期間を過ぎた人は、一覧・参照に出さない（時点に関わらず今日で判定）。 */
  isListed(person: DemoPerson): boolean {
    return !(person.termination_date && addDays(person.termination_date, RETIRED_PUBLIC_DAYS) < this.businessDate);
  }

  orgsAt(asOf: string): DemoOrgUnit[] {
    return this.data.org_units.filter((o) => inPeriod(asOf, o.valid_from, o.valid_until));
  }

  /** その時点に有効な所属（その時点に組織があるものだけ）。 */
  assignmentsAt(person: DemoPerson, asOf: string) {
    return person.assignments.filter((a) => inPeriod(asOf, a.valid_from, a.valid_until) && this.orgById.has(a.org_unit_id));
  }

  /** 組織の祖先（自分を含む）。 */
  ancestors(orgId: string): DemoOrgUnit[] {
    const out: DemoOrgUnit[] = [];
    let current = this.orgById.get(orgId);
    const seen = new Set<string>();
    while (current && !seen.has(current.id)) {
      seen.add(current.id);
      out.push(current);
      current = current.parent_id ? this.orgById.get(current.parent_id) : undefined;
    }
    return out;
  }

  /** その時点の組織長（見える人で、その時点に所属がある人だけ）。 */
  headAt(org: DemoOrgUnit, asOf: string): string | null {
    if (!org.head_person_id) return null;
    const head = this.personById.get(org.head_person_id);
    if (!head || !this.isListed(head) || this.assignmentsAt(head, asOf).length === 0) return null;
    return head.id;
  }

  orgTree(asOf: string): OrgTree {
    this.checkAsOf(asOf);
    const units = this.orgsAt(asOf).map((o) => {
      const entity = this.entityById.get(o.legal_entity_id);
      const level = o.level_key ? this.data.level_schemes[o.legal_entity_id]?.find((l) => l.key === o.level_key) : undefined;
      return {
        id: o.id,
        legal_entity: { id: o.legal_entity_id, code: entity?.code ?? null, name: entity?.name ?? null },
        code: o.code,
        name: o.name,
        short_name: o.short_name,
        level: level ? { key: level.key, name: level.name, rank: level.rank } : null,
        parent_id: o.parent_id,
        head_person_id: this.headAt(o, asOf),
        site: this.orgSite(o.id, asOf),
      };
    });
    return { as_of: asOf, units };
  }

  /** 閲覧者に見える項目。組織単位の制限は今日の所属で評価する（概要 §5.6）。今日の所属だけで決まるので人ごとに覚える。 */
  visibleFields(person: DemoPerson): PersonField[] {
    let fields = this.visibleCache.get(person.id);
    if (!fields) {
      fields = this.computeVisibleFields(person);
      this.visibleCache.set(person.id, fields);
    }
    return fields;
  }

  private computeVisibleFields(person: DemoPerson): PersonField[] {
    const hidden = new Set<PersonField>(this.policy.hiddenFields);
    const todays = this.assignmentsAt(person, this.businessDate);
    for (const rule of this.policy.orgRestrictions) {
      const applies = todays.some((a) => this.ancestors(a.org_unit_id).some((o) => o.code === rule.orgCode));
      if (applies) rule.fields.forEach((f) => hidden.add(f));
    }
    return PERSON_FIELDS.filter((f) => !hidden.has(f)).sort();
  }

  /** 上長: 所属組織から上にたどり、本人以外の組織長が最初に見つかった組織の組織長（所属ごとの和集合）。 */
  managerIds(person: DemoPerson, asOf: string): string[] {
    const ids = new Set<string>();
    for (const a of this.assignmentsAt(person, asOf)) {
      for (const org of this.ancestors(a.org_unit_id)) {
        const head = this.headAt(org, asOf);
        if (head && head !== person.id) {
          ids.add(head);
          break;
        }
      }
    }
    return [...ids];
  }

  /** PersonResponse 相当の投影。見えない項目・値の無い項目は null（API と同じ）。 */
  project(person: DemoPerson, asOf: string): Person {
    const visible = new Set(this.visibleFields(person));
    const today = this.businessDate;
    const entity = this.entityById.get(person.legal_entity_id);
    const suspension = person.suspensions.find((s) => inPeriod(asOf, s.from, s.until));
    const terminated = person.termination_date !== null && person.termination_date < asOf;
    const assignments = this.assignmentsAt(person, asOf).map((a): Assignment => {
      const org = this.orgById.get(a.org_unit_id)!;
      return {
        org_unit_id: a.org_unit_id,
        org_unit_code: org.code,
        org_unit_name: org.name,
        legal_entity_id: org.legal_entity_id,
        type: a.type,
        position_title: a.position_title,
        secondment: a.secondment,
        valid_from: a.valid_from,
        // 将来の予定を推測させる終了日は返さない
        valid_until: a.valid_until && a.valid_until <= today ? a.valid_until : null,
      };
    });
    const values: Partial<Person> = {
      employee_number: person.employee_number,
      legal_entity: entity ? { id: entity.id, code: entity.code, name: entity.name } : null,
      display_name: person.display_name,
      family_name: person.family_name,
      given_name: person.given_name,
      family_name_kana: person.family_name_kana,
      given_name_kana: person.given_name_kana,
      family_name_latin: person.family_name_latin,
      given_name_latin: person.given_name_latin,
      work_email: person.work_email,
      extension_number: person.extension_number,
      work_mobile_phone: person.work_mobile_phone,
      site: visible.has("site") ? this.personSite(person, asOf) : null,
      remote_work: person.remote_work,
      employment_type: person.employment_type,
      job_type: person.job_type,
      hire_date: person.hire_date,
      // 退職年月日は退職済みになるまで返さない
      termination_date: person.termination_date && person.termination_date < today ? person.termination_date : null,
      employment_status: terminated ? "terminated" : "active",
      account_status: suspension ? "suspended" : "active",
      // 一時停止の終了予定日は返さない（終わった期間の終了日だけ）
      account_suspension: suspension
        ? { from: suspension.from, until: suspension.until && suspension.until <= today ? suspension.until : null }
        : null,
      assignments,
      managers: this.managerIds(person, asOf),
    };
    // API と同じく、すべての項目を明示的に返す（見えない項目・値の無い項目は null。見えるかどうかは visible_fields で区別する）
    const out: Record<string, unknown> = { id: person.id, visible_fields: [...visible] };
    for (const field of PERSON_FIELDS) {
      const value = values[field];
      out[field] = visible.has(field) && value !== undefined ? value : null;
    }
    return out as Person;
  }

  getPerson(id: string, asOf: string): Person {
    this.checkAsOf(asOf);
    const person = this.personById.get(id);
    if (!person || !this.isListed(person)) {
      throw new ApiError(404, "見つかりません。", "urn:jinji:problem:not-found");
    }
    return this.project(person, asOf);
  }

  /** ID の一括取得（GET /people?ids=）。重複を除いて最大 100 件、指定の順。存在しない・見えない ID は含めない。 */
  peopleByIds(ids: string[], asOf: string): Person[] {
    this.checkAsOf(asOf);
    const unique = [...new Set(ids)];
    if (unique.length > 100) {
      throw new ApiError(400, "ids は 100 件までです。", "urn:jinji:problem:bad-request");
    }
    return unique
      .map((id) => this.personById.get(id))
      .filter((p): p is DemoPerson => p !== undefined && this.isListed(p))
      .map((p) => this.project(p, asOf));
  }

  /** 組織の検索（GET /orgs?query=）。組織名・略称・組織コード。完全一致・前方一致・部分一致の順。 */
  searchOrgs(query: string, asOf: string, limit = 30): { items: OrgTree["units"]; hasMore: boolean } {
    const problem = validateQuery(query);
    if (problem) {
      throw new ApiError(400, problem, "urn:jinji:problem:bad-request");
    }
    const key = simpleSearchKey(query);
    const rank = (u: OrgTree["units"][number]): number => {
      const keys = [u.name, u.short_name, u.code].map(simpleSearchKey);
      if (keys.some((k) => k === key)) return 0;
      if (keys.some((k) => k.startsWith(key))) return 1;
      if (keys.some((k) => k.includes(key))) return 2;
      return 3;
    };
    const hits = this.orgTree(asOf)
      .units.map((u) => ({ u, r: rank(u) }))
      .filter((x) => x.r < 3)
      .sort((a, b) => a.r - b.r || a.u.code.localeCompare(b.u.code));
    const n = Math.min(Math.max(limit, 1), 100);
    return { items: hits.slice(0, n).map((x) => x.u), hasMore: hits.length > n };
  }

  getManagers(id: string, asOf: string): Person[] {
    const person = this.personById.get(id);
    this.getPerson(id, asOf);
    return this.managerIds(person!, asOf)
      .map((m) => this.personById.get(m)!)
      .map((m) => this.project(m, asOf));
  }

  /** 組織の所属者（主務・兼務・出向）。組織長、役職者、社員番号の順。 */
  members(orgId: string, asOf: string, includeDescendants: boolean): DemoPerson[] {
    this.checkAsOf(asOf);
    if (!this.orgsAt(asOf).some((o) => o.id === orgId)) {
      throw new ApiError(404, "見つかりません。", "urn:jinji:problem:not-found");
    }
    const target = new Set<string>([orgId]);
    if (includeDescendants) {
      for (const o of this.orgsAt(asOf)) {
        if (this.ancestors(o.id).some((a) => a.id === orgId)) target.add(o.id);
      }
    }
    const org = this.orgById.get(orgId)!;
    const head = this.headAt(org, asOf);
    const rows = this.data.people
      .filter((p) => this.isListed(p))
      .map((p) => ({ p, a: this.assignmentsAt(p, asOf).filter((a) => target.has(a.org_unit_id)) }))
      .filter((r) => r.a.length > 0);
    rows.sort((x, y) => {
      const rank = (r: (typeof rows)[number]) => (r.p.id === head ? 0 : r.a.some((a) => a.position_title) ? 1 : 2);
      return rank(x) - rank(y) || x.p.employee_number.localeCompare(y.p.employee_number);
    });
    return rows.map((r) => r.p);
  }

  /** 簡易検索（見える項目だけで照合する）。 */
  search(query: string, asOf: string): DemoPerson[] {
    this.checkAsOf(asOf);
    const problem = validateQuery(query);
    if (problem) {
      throw new ApiError(400, problem, "urn:jinji:problem:bad-request");
    }
    return this.data.people.filter((p) => {
      if (!this.isListed(p)) return false;
      const visible = new Set(this.visibleFields(p));
      const pick = (field: PersonField, value: string | null) => (visible.has(field) ? value : null);
      return matchesAny(query, [
        pick("display_name", p.display_name),
        pick("family_name", p.family_name) && pick("given_name", p.given_name) ? `${p.family_name}${p.given_name}` : null,
        pick("family_name_kana", p.family_name_kana) && `${p.family_name_kana ?? ""}${p.given_name_kana ?? ""}`,
        pick("family_name_latin", p.family_name_latin) && `${p.given_name_latin ?? ""} ${p.family_name_latin ?? ""}`,
        pick("family_name_latin", p.family_name_latin) && `${p.family_name_latin ?? ""} ${p.given_name_latin ?? ""}`,
        pick("employee_number", p.employee_number),
        pick("work_email", p.work_email),
      ]);
    });
  }

  /** 組織ごとの人数と、兼務・出向の線（組織の組で集計）。 */
  stats(asOf: string): OrgStats {
    this.checkAsOf(asOf);
    const headcount: OrgStats["headcount"] = {};
    const links = new Map<string, OrgLink>();
    const bump = (orgId: string, key: "primary" | "concurrent" | "secondment") => {
      const h = (headcount[orgId] ??= { primary: 0, concurrent: 0, secondment: 0 });
      h[key]++;
    };
    for (const p of this.data.people) {
      if (!this.isListed(p)) continue;
      const current = this.assignmentsAt(p, asOf);
      const primary = current.find((a) => a.type === "primary");
      for (const a of current) {
        const kind = a.secondment !== "none" ? "secondment" : a.type === "primary" ? "primary" : "concurrent";
        bump(a.org_unit_id, kind);
        if (kind !== "primary" && primary) {
          const key = `${primary.org_unit_id}>${a.org_unit_id}>${kind}`;
          const link = links.get(key) ?? { from: primary.org_unit_id, to: a.org_unit_id, kind, count: 0 };
          link.count++;
          links.set(key, link);
        }
      }
    }
    return { asOf, headcount, links: [...links.values()] };
  }

  /**
   * 拠点の一覧（GET /sites）と詳細（GET /sites/{id}）。法人のコード・拠点コードの順。
   * 人数は一覧に出る人のうち、拠点が見える人だけ（リモートワークの人数は、その有無も見える人だけ）。
   */
  sites(q: SiteQuery): Site[] {
    this.checkAsOf(q.asOf);
    const query = q.query?.trim() ?? "";
    if (query) {
      const problem = validateQuery(query);
      if (problem) throw new ApiError(400, problem, "urn:jinji:problem:bad-request");
    }
    const counts = this.siteCounts(q.asOf);
    const key = query ? simpleSearchKey(query) : null;
    const entityCode = (id: string) => this.entityById.get(id)?.code ?? "";
    return this.data.sites
      .filter((s) => inPeriod(q.asOf, s.valid_from, s.valid_until))
      .filter((s) => !q.legalEntities?.length || q.legalEntities.includes(s.legal_entity_id))
      .filter((s) => !q.siteTypes?.length || q.siteTypes.includes(s.type))
      .filter((s) => !q.prefectures?.length || (s.prefecture_code !== null && q.prefectures.includes(s.prefecture_code)))
      .filter((s) => !key || simpleSearchKey(s.name).includes(key) || simpleSearchKey(s.code).includes(key))
      .sort((a, b) => entityCode(a.legal_entity_id).localeCompare(entityCode(b.legal_entity_id)) || a.code.localeCompare(b.code))
      .map((s) => this.siteResponse(s, q.asOf, counts));
  }

  site(id: string, asOf: string): Site {
    this.checkAsOf(asOf);
    const site = this.siteAt(id, asOf);
    if (!site) throw new ApiError(404, "見つかりません。", "urn:jinji:problem:not-found");
    return this.siteResponse(site, asOf, this.siteCounts(asOf));
  }

  private siteCounts(asOf: string): Map<string, { members: number; remote: number }> {
    const counts = new Map<string, { members: number; remote: number }>();
    for (const p of this.data.people) {
      if (!this.isListed(p) || this.assignmentsAt(p, asOf).length === 0) continue;
      const visible = this.visibleFields(p);
      if (!visible.includes("site")) continue;
      const site = this.personSite(p, asOf);
      if (!site) continue;
      const c = counts.get(site.id) ?? { members: 0, remote: 0 };
      c.members++;
      if (visible.includes("remote_work") && p.remote_work === true) c.remote++;
      counts.set(site.id, c);
    }
    return counts;
  }

  private siteResponse(site: DemoSite, asOf: string, counts: Map<string, { members: number; remote: number }>): Site {
    const entity = this.entityById.get(site.legal_entity_id);
    return {
      id: site.id,
      legal_entity: { id: site.legal_entity_id, code: entity?.code ?? null, name: entity?.name ?? null },
      code: site.code,
      name: site.name,
      type: site.type,
      prefecture_code: site.prefecture_code,
      prefecture_name: prefectureName(site.prefecture_code),
      location: site.location,
      member_count: counts.get(site.id)?.members ?? 0,
      remote_work_count: counts.get(site.id)?.remote ?? 0,
      // この拠点を自分の拠点として持つ組織（引き継いだ組織は含めない。デモは組織コードの順に簡略化）
      org_units: this.orgsAt(asOf)
        .filter((o) => o.site_id === site.id)
        .sort((a, b) => a.code.localeCompare(b.code))
        .map((o) => ({ id: o.id, code: o.code, name: o.name })),
    };
  }

  page(people: DemoPerson[], asOf: string, cursor: string | null | undefined, limit: number): PersonListResult {
    const start = cursor ? Number.parseInt(cursor, 10) || 0 : 0;
    const slice = people.slice(start, start + limit);
    const next = start + limit < people.length ? String(start + limit) : null;
    return { items: slice.map((p) => this.project(p, asOf)), nextCursor: next };
  }
}
