// 直接登録・グループ・素案のデモ（架空データ。端末のメモリだけに持ち、再読み込みで消える）。
// デモでは全領域の管理元を jin-ji（field）にし、利用者は全社の範囲を持つ。直接登録は記録だけで、デモの組織・人のデータは変えない。
// 素案のシミュレーションは、API の PlanSimulator と同じ考え方（基準日の組織に変更を重ねる）を簡単にしたもの。
import type {
  FieldAdapter,
  FieldGrant,
  FieldRegistration,
  FieldSetting,
  GroupDetail,
  Plan,
  PlanChange,
  PlanInput,
  PlanSimulation,
  PlanUnit,
  PlanVersion,
} from "../data/field";
import { ApiError } from "../data/types";
import { demoMe } from "./demoViewer";
import type { DemoDirectory } from "./directory";

let counter = 0;
const newId = () => {
  counter += 1;
  return `00000000-0000-7000-8000-${counter.toString(16).padStart(12, "0")}`;
};

const DOMAINS = ["org_structure", "org_site", "assignment", "person_site", "person_contact"] as const;

/** 基準日の組織に素案の変更を重ねる（人は ID と数だけ）。 */
export function simulateDemo(dir: DemoDirectory, baseDate: string, changes: PlanChange[]): PlanSimulation {
  const tree = dir.orgTree(baseDate);
  const units = new Map(
    tree.units.map((u) => [
      u.id,
      {
        id: u.id,
        code: u.code,
        name: u.name,
        short_name: u.short_name,
        level_key: u.level?.key ?? null,
        parent_id: u.parent_id,
        head_person_id: u.head_person_id,
        site_id: u.site?.id ?? null,
        state: "unchanged" as PlanUnit["state"],
      },
    ]),
  );
  const primary = new Map<string, { unit: string; title: string | null }>();
  for (const p of dir.data.people) {
    const a = dir.assignmentsAt(p, baseDate).find((x) => x.type === "primary");
    if (a) primary.set(p.id, { unit: a.org_unit_id, title: a.position_title ?? null });
  }
  const before = new Map<string, number>();
  for (const v of primary.values()) before.set(v.unit, (before.get(v.unit) ?? 0) + 1);
  const after = new Map(primary);
  const sim: PlanSimulation = { base_date: baseDate, units: [], moves: [], person_changes: [], org_changes: [], group_changes: [], warnings: [] };

  changes.forEach((c, i) => {
    if (c.kind !== "org_create") return;
    const parent = c.values.parent_org_unit_id;
    if (!c.target_id || !parent || !units.has(parent) || !c.values.name) {
      sim.warnings.push({ index: i, message: "新設の組織に、名称と上位組織が要ります。" });
      return;
    }
    units.set(c.target_id, {
      id: c.target_id,
      code: c.values.code ?? "",
      name: c.values.name,
      short_name: c.values.short_name ?? null,
      level_key: c.values.level_key ?? null,
      parent_id: parent,
      head_person_id: c.values.head_person_id ?? null,
      site_id: null,
      state: "created",
    });
  });
  changes.forEach((c, i) => {
    const unit = c.target_id ? units.get(c.target_id) : undefined;
    switch (c.kind) {
      case "org_create":
        return;
      case "org_update":
        if (!unit) break;
        for (const [key, value] of Object.entries(c.values)) {
          const old =
            key === "name" ? unit.name : key === "parent_org_unit_id" ? unit.parent_id : key === "head_person_id" ? unit.head_person_id : key === "short_name" ? unit.short_name : null;
          if (key === "name" && value) unit.name = value;
          else if (key === "parent_org_unit_id" && value && units.has(value)) unit.parent_id = value;
          else if (key === "head_person_id") unit.head_person_id = value;
          else if (key === "short_name") unit.short_name = value;
          sim.org_changes.push({ index: i, target_id: unit.id, field: key, old, new: value });
        }
        if (unit.state === "unchanged") unit.state = "changed";
        return;
      case "org_abolish":
        if (!unit) break;
        unit.state = "abolished";
        sim.org_changes.push({ index: i, target_id: unit.id, field: "abolish", old: null, new: null });
        return;
      case "assignment": {
        const to = c.values.org_unit_id;
        if (!c.target_id || !to || !units.has(to)) break;
        const from = after.get(c.target_id);
        const title = "position_title" in c.values ? (c.values.position_title ?? null) : (from?.title ?? null);
        after.set(c.target_id, { unit: to, title });
        sim.moves.push({ index: i, person_id: c.target_id, from_unit_id: from?.unit ?? null, to_unit_id: to, from_title: from?.title ?? null, to_title: title });
        return;
      }
      case "person":
        if (!c.target_id) break;
        for (const [key, value] of Object.entries(c.values)) sim.person_changes.push({ index: i, target_id: c.target_id, field: key, old: null, new: value });
        return;
      case "group_join":
      case "group_leave":
        if (!c.target_id || !c.values.person_id) break;
        sim.group_changes.push({
          index: i,
          group_id: c.target_id,
          person_id: c.values.person_id,
          join: c.kind === "group_join",
          role: c.kind === "group_join" ? ((c.values.role as "owner" | "lead" | "member" | null) ?? "member") : null,
        });
        return;
    }
    sim.warnings.push({ index: i, message: "対象が見つからないか、適用できない変更です。" });
  });
  const counts = new Map<string, number>();
  for (const v of after.values()) counts.set(v.unit, (counts.get(v.unit) ?? 0) + 1);
  sim.units = [...units.values()]
    .map((u) => ({
      ...u,
      before: u.state === "created" ? 0 : (before.get(u.id) ?? 0),
      after: u.state === "abolished" ? 0 : (counts.get(u.id) ?? 0),
    }))
    .sort((a, b) => a.code.localeCompare(b.code));
  for (const u of sim.units.filter((x) => x.state === "abolished")) {
    if ((counts.get(u.id) ?? 0) > 0) sim.warnings.push({ index: -1, message: `廃止する組織 ${u.code} に所属者が残っています。` });
  }
  return sim;
}

export function createDemoField(dir: DemoDirectory, later: <T>(fn: () => T) => Promise<T>): FieldAdapter {
  const me = demoMe(dir.data).id;
  const now = () => new Date().toISOString();
  const settings: FieldSetting[] = [];
  const grants: FieldGrant[] = [];
  const registrations: FieldRegistration[] = [];
  const plans = new Map<string, Plan>();
  const versions = new Map<string, PlanVersion[]>();
  const groups = new Map<string, GroupDetail>();

  // 架空のグループを 2 つ（全社公開）
  const sample = dir.data.people.filter((p) => dir.isListed(p)).slice(0, 6);
  for (const [name, kind, members] of [
    ["新製品の立ち上げ", "project", sample.slice(0, 4)],
    ["社内勉強会", "team", sample.slice(2, 6)],
  ] as const) {
    const id = newId();
    groups.set(id, {
      group: { id, name, kind, owner_org_unit_id: null, visibility: "all", managed: false, valid_from: dir.businessDate, valid_until: null, member_count: members.length },
      members: members.map((p, i) => ({ person_id: p.id, role: i === 0 ? "owner" : "member", from: dir.businessDate, until: null })),
    });
  }

  const notFound = () => new ApiError(404, "見つかりません。");
  const findPlan = (id: string) => plans.get(id) ?? (() => { throw notFound(); })();
  const savePlan = (id: string, input: PlanInput, base?: Plan): Plan => {
    const plan: Plan = {
      id,
      title: input.title,
      note: input.note,
      base_date: input.base_date,
      effective_date: input.effective_date,
      changes: input.changes.map((c) => ({ kind: c.kind, target_id: c.target_id, values: c.values as Record<string, string> })),
      owner: me,
      shared_with: base?.shared_with ?? [],
      created_at: base?.created_at ?? now(),
      updated_at: now(),
    };
    plans.set(id, plan);
    return plan;
  };
  const toChanges = (list: Plan["changes"]): PlanChange[] =>
    list.map((c) => ({ kind: c.kind ?? "assignment", target_id: c.target_id ?? null, values: (c.values ?? {}) as Record<string, string | null> }));
  const touch = (detail: GroupDetail) => {
    detail.group.member_count = detail.members.length;
    return structuredClone(detail);
  };

  return {
    modes: () => later(() => DOMAINS.map((domain) => ({ domain, mode: "field" as const }))),
    settings: () => later(() => [...settings]),
    proposeMode: (domain, mode, note) =>
      later(() => {
        const s: FieldSetting = { id: newId(), domain, mode, status: "proposed", note, proposed_by: me, proposed_at: now(), decided_by: null, decided_at: null };
        settings.unshift(s);
        return s;
      }),
    decideMode: (id, approve) =>
      later(() => {
        const s = settings.find((x) => x.id === id) ?? (() => { throw notFound(); })();
        Object.assign(s, { status: approve ? "approved" : "rejected", decided_by: me, decided_at: now() });
        return s;
      }),
    grants: () => later(() => [...grants]),
    grant: (i) =>
      later(() => {
        const g: FieldGrant = { id: newId(), org_unit_id: i.orgUnitId, person_id: i.personId, from: i.from, until: i.until, granted_by: me, granted_at: now(), revoked_by: null, revoked_at: null };
        grants.unshift(g);
        return g;
      }),
    revokeGrant: (id) =>
      later(() => {
        const g = grants.find((x) => x.id === id) ?? (() => { throw notFound(); })();
        Object.assign(g, { revoked_by: me, revoked_at: now() });
        return g;
      }),
    scope: () => later(() => ({ manages_all: true, org_unit_ids: [], parent_scope_org_unit_ids: [], grantable_org_unit_ids: [] })),

    registrations: () => later(() => [...registrations]),
    registration: (id) => later(() => registrations.find((r) => r.id === id) ?? (() => { throw notFound(); })()),
    register: (i) =>
      later(() => {
        const r: FieldRegistration = {
          id: newId(),
          status: "applied",
          effective_date: i.effective_date,
          note: i.note,
          registered_by: me,
          registered_via: "user",
          created_at: now(),
          expires_at: now(),
          org_unit_ids: [],
          person_ids: i.changes.filter((c) => c.kind === "assignment" || c.kind === "person").map((c) => c.target_id!).filter(Boolean),
          consent_org_unit_ids: [],
          consented_by: null,
          consented_at: null,
          applied_by: me,
          applied_at: now(),
          withdrawn_by: null,
          withdrawn_at: null,
          changes: i.changes.map((c) => ({
            id: newId(),
            target_kind: c.kind === "assignment" ? "assignment" : c.kind === "person" ? "person_version" : c.kind === "org_create" ? "org_unit" : "org_unit_version",
            operation: c.kind === "org_create" ? "create" : c.kind === "org_abolish" ? "abolish" : "change",
            target_id: c.target_id,
            status: "applied",
            payload: {},
            diff: Object.fromEntries(Object.entries(c.values).map(([k, v]) => [k, { old: null, new: v }])),
          })),
        };
        registrations.unshift(r);
        return r;
      }),
    transition: (id, action) =>
      later(() => {
        const r = registrations.find((x) => x.id === id) ?? (() => { throw notFound(); })();
        if (action === "withdraw") Object.assign(r, { status: "withdrawn", withdrawn_by: me, withdrawn_at: now() });
        else if (action === "discard") r.status = "discarded";
        else r.status = "applied";
        return r;
      }),

    groups: () => later(() => [...groups.values()].map((g) => structuredClone(g.group))),
    group: (id) => later(() => structuredClone(groups.get(id) ?? (() => { throw notFound(); })())),
    createGroup: (i) =>
      later(() => {
        const id = newId();
        const detail: GroupDetail = {
          group: { id, name: i.name, kind: i.kind, owner_org_unit_id: i.owner_org_unit_id, visibility: i.visibility, managed: false, valid_from: dir.businessDate, valid_until: null, member_count: 1 },
          members: [{ person_id: me, role: "owner", from: dir.businessDate, until: null }],
        };
        groups.set(id, detail);
        return touch(detail);
      }),
    addMember: (groupId, personId, role) =>
      later(() => {
        const detail = groups.get(groupId) ?? (() => { throw notFound(); })();
        if (detail.members.some((m) => m.person_id === personId)) throw new ApiError(409, "この人は既にメンバーです。");
        detail.members.push({ person_id: personId, role, from: dir.businessDate, until: null });
        return touch(detail);
      }),
    removeMember: (groupId, personId) =>
      later(() => {
        const detail = groups.get(groupId) ?? (() => { throw notFound(); })();
        detail.members = detail.members.filter((m) => m.person_id !== personId);
        return touch(detail);
      }),

    plans: () => later(() => [...plans.values()].sort((a, b) => b.updated_at.localeCompare(a.updated_at))),
    plan: (id) => later(() => findPlan(id)),
    createPlan: (i) => later(() => savePlan(newId(), i)),
    updatePlan: (id, i) => later(() => savePlan(id, i, findPlan(id))),
    deletePlan: (id) =>
      later(() => {
        plans.delete(id);
        versions.delete(id);
      }),
    sharePlan: (id, people) =>
      later(() => {
        const plan = findPlan(id);
        plan.shared_with = people;
        return plan;
      }),
    simulate: (id, no) =>
      later(() => {
        const plan = findPlan(id);
        const version = no === null ? null : versions.get(id)?.find((v) => v.version_no === no);
        if (no !== null && !version) throw notFound();
        return simulateDemo(dir, (version ?? plan).base_date, toChanges((version ?? plan).changes));
      }),
    versions: (id) => later(() => [...(versions.get(id) ?? [])]),
    freeze: (id) =>
      later(() => {
        const plan = findPlan(id);
        const list = versions.get(id) ?? [];
        const v: PlanVersion = {
          id: newId(),
          plan_id: id,
          version_no: list.length + 1,
          title: plan.title,
          note: plan.note,
          base_date: plan.base_date,
          effective_date: plan.effective_date,
          changes: structuredClone(plan.changes),
          created_by: me,
          created_at: now(),
          approved_on: null,
          approval_reference: null,
          approval_recorded_by: null,
          approval_recorded_by_client: null,
        };
        versions.set(id, [...list, v]);
        return v;
      }),
    recordApproval: (id, no, approvedOn, reference) =>
      later(() => {
        const v = versions.get(id)?.find((x) => x.version_no === no) ?? (() => { throw notFound(); })();
        if (v.approved_on) throw new ApiError(409, "承認の結果は記録済みです。");
        Object.assign(v, { approved_on: approvedOn, approval_reference: reference, approval_recorded_by: me });
      }),
    registerPlan: async (id, no) => {
      const plan = findPlan(id);
      const version = no === null ? null : versions.get(id)?.find((v) => v.version_no === no);
      if (no !== null && !version) throw notFound();
      if (version && !version.approved_on) throw new ApiError(409, "承認の結果を記録した申請版だけを登録できます。");
      const list = toChanges((version ?? plan).changes);
      const field = list.filter((c) => c.kind !== "group_join" && c.kind !== "group_leave");
      const registration = field.length > 0 ? await registerFromPlan(field, (version ?? plan).effective_date) : null;
      return { registration, group_changes: list.length - field.length };
    },
    changesCsv: (id, no) =>
      later(() => {
        const plan = findPlan(id);
        const version = no === null ? null : versions.get(id)?.find((v) => v.version_no === no);
        const sim = simulateDemo(dir, (version ?? plan).base_date, toChanges((version ?? plan).changes));
        const units = new Map(sim.units.map((u) => [u.id, u]));
        const name = (pid: string) => {
          const p = dir.data.people.find((x) => x.id === pid);
          return p ? `${p.employee_number} ${p.display_name}` : pid;
        };
        const unit = (uid: string | null) => (uid && units.get(uid) ? `${units.get(uid)!.code} ${units.get(uid)!.name}` : (uid ?? ""));
        const q = (v: string) => `"${(/^[=+\-@\t\r]/.test(v) ? `'${v}` : v).replaceAll('"', '""')}"`;
        const rows = [
          "番号,種類,対象,項目,現在の値,素案の値,有効日",
          ...sim.moves.map((m) => [String(m.index + 1), "異動", name(m.person_id), "所属", unit(m.from_unit_id), unit(m.to_unit_id), (version ?? plan).effective_date].map(q).join(",")),
          ...sim.org_changes.map((c) => [String(c.index + 1), "組織", unit(c.target_id), c.field, c.old ?? "", c.new ?? "", (version ?? plan).effective_date].map(q).join(",")),
        ];
        const blob = new Blob(["﻿" + rows.join("\r\n") + "\r\n"], { type: "text/csv;charset=utf-8" });
        const href = URL.createObjectURL(blob);
        return { href, fileName: `jinji-plan-demo${no ? `-v${no}` : ""}.csv`, dispose: () => URL.revokeObjectURL(href) };
      }),
  };

  // 素案から直接登録にする（デモは記録だけ）
  function registerFromPlan(field: PlanChange[], effective: string): Promise<FieldRegistration> {
    return later(() => {
      const r: FieldRegistration = {
        id: newId(),
        status: "applied",
        effective_date: effective,
        note: "素案から",
        registered_by: me,
        registered_via: "user",
        created_at: now(),
        expires_at: now(),
        org_unit_ids: [],
        person_ids: field.filter((c) => c.kind === "assignment" && c.target_id).map((c) => c.target_id!),
        consent_org_unit_ids: [],
        consented_by: null,
        consented_at: null,
        applied_by: me,
        applied_at: now(),
        withdrawn_by: null,
        withdrawn_at: null,
        changes: null,
      };
      registrations.unshift(r);
      return r;
    });
  }
}
