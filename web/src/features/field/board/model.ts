// 編集のボードの計算: 基準日の組織に変更を重ねた組織の木と、人の移動。変更の追加・取り消しは、同じ対象の変更に畳み込む
// （同じ人を 2 度動かしたら 1 件の異動、元の組織に戻したら変更を消す）。サーバーのシミュレーション（PlanSimulator）が正で、
// ここは画面で即座に見せるための近似。権限・範囲の判定はしない。
import type { PlanChange } from "../../../data/field";
import type { OrgUnit } from "../../../data/types";

export type UnitState = "unchanged" | "created" | "changed" | "abolished";

export interface BoardUnit {
  id: string;
  code: string;
  name: string;
  levelName: string | null;
  parentId: string | null;
  headPersonId: string | null;
  state: UnitState;
  /** 素案で変えた項目（name・parent_org_unit_id・head_person_id など）。 */
  changed: string[];
  /** 基準日の値（変えた項目の元の値を示すのに使う）。新設なら null。 */
  base: OrgUnit | null;
}

export interface BoardTree {
  units: BoardUnit[];
  byId: Map<string, BoardUnit>;
  children: Map<string | null, BoardUnit[]>;
}

/** 人の異動（主務の所属・役職）。 */
export interface BoardMove {
  personId: string;
  to: string;
  title: string | null | undefined;
}

/** 基準日の組織に、組織の変更（新設・名称・上位・組織長・廃止）を重ねる。 */
export function applyOrgChanges(base: OrgUnit[], changes: PlanChange[]): BoardTree {
  const units = new Map<string, BoardUnit>(
    base.map((u) => [
      u.id,
      {
        id: u.id,
        code: u.code,
        name: u.name,
        levelName: u.level?.name ?? null,
        parentId: u.parent_id,
        headPersonId: u.head_person_id,
        state: "unchanged" as UnitState,
        changed: [],
        base: u,
      },
    ]),
  );
  for (const c of changes) {
    const v = c.values;
    if (c.kind === "org_create" && c.target_id) {
      units.set(c.target_id, {
        id: c.target_id,
        code: v.code ?? "",
        name: v.name ?? "",
        levelName: null,
        parentId: v.parent_org_unit_id ?? null,
        headPersonId: v.head_person_id ?? null,
        state: "created",
        changed: [],
        base: null,
      });
    } else if (c.kind === "org_update" && c.target_id) {
      const u = units.get(c.target_id);
      if (!u) continue;
      if ("name" in v && v.name) u.name = v.name;
      if ("parent_org_unit_id" in v) u.parentId = v.parent_org_unit_id ?? null;
      if ("head_person_id" in v) u.headPersonId = v.head_person_id ?? null;
      if (u.state === "unchanged") u.state = "changed";
      u.changed = [...new Set([...u.changed, ...Object.keys(v)])];
    } else if (c.kind === "org_abolish" && c.target_id) {
      const u = units.get(c.target_id);
      if (u) u.state = "abolished";
    }
  }
  const list = [...units.values()];
  const children = new Map<string | null, BoardUnit[]>();
  for (const u of list) {
    const parent = u.parentId && units.has(u.parentId) ? u.parentId : null;
    children.set(parent, [...(children.get(parent) ?? []), u]);
  }
  for (const l of children.values()) l.sort((a, b) => (a.state === "created" ? 1 : 0) - (b.state === "created" ? 1 : 0) || a.code.localeCompare(b.code));
  return { units: list, byId: units, children };
}

/** 素案の異動（人ごとに最後のもの）。 */
export function movesOf(changes: PlanChange[]): Map<string, BoardMove> {
  const moves = new Map<string, BoardMove>();
  for (const c of changes) {
    if (c.kind !== "assignment" || !c.target_id || !c.values.org_unit_id) continue;
    moves.set(c.target_id, { personId: c.target_id, to: c.values.org_unit_id, title: "position_title" in c.values ? c.values.position_title : undefined });
  }
  return moves;
}

/** a が b の配下（b 自身を含む）か。 */
export function isWithin(tree: BoardTree, a: string, b: string): boolean {
  for (let cur: string | null = a, guard = 0; cur && guard < 1000; guard++) {
    if (cur === b) return true;
    cur = tree.byId.get(cur)?.parentId ?? null;
  }
  return false;
}

/**
 * 人を動かす（役職も変えるなら title）。元の組織・元の役職のままに戻したら異動の変更を消す。
 * from は基準日の主務の組織、fromTitle は基準日の役職。
 */
export function movePerson(
  changes: PlanChange[],
  personId: string,
  to: string,
  from: { org: string | null; title: string | null },
  title?: string | null,
): PlanChange[] {
  const existing = changes.find((c) => c.kind === "assignment" && c.target_id === personId);
  const nextTitle = title !== undefined ? title : existing && "position_title" in existing.values ? existing.values.position_title : undefined;
  const rest = changes.filter((c) => c !== existing);
  const sameOrg = to === from.org;
  const sameTitle = nextTitle === undefined || (nextTitle ?? null) === (from.title ?? null);
  if (sameOrg && sameTitle) return rest;
  const values: Record<string, string | null> = { org_unit_id: to };
  if (nextTitle !== undefined && !sameTitle) values.position_title = nextTitle;
  const change: PlanChange = { kind: "assignment", target_id: personId, values };
  return existing ? changes.map((c) => (c === existing ? change : c)) : [...changes, change];
}

/** 異動を取り消す。 */
export function undoMove(changes: PlanChange[], personId: string): PlanChange[] {
  return changes.filter((c) => !(c.kind === "assignment" && c.target_id === personId));
}

/**
 * 組織の項目（name・parent_org_unit_id・head_person_id）を変える。新設した組織なら新設の変更に書き込む。
 * 基準日の値に戻したら、その項目を変更から外す（項目が無くなれば変更ごと消す）。
 */
export function updateUnit(changes: PlanChange[], tree: BoardTree, unitId: string, key: "name" | "parent_org_unit_id" | "head_person_id", value: string | null): PlanChange[] {
  const created = changes.find((c) => c.kind === "org_create" && c.target_id === unitId);
  if (created) return changes.map((c) => (c === created ? { ...c, values: { ...c.values, [key]: value } } : c));
  const base = tree.byId.get(unitId)?.base;
  const baseValue = !base ? undefined : key === "name" ? base.name : key === "parent_org_unit_id" ? base.parent_id : base.head_person_id;
  const existing = changes.find((c) => c.kind === "org_update" && c.target_id === unitId);
  const values = { ...(existing?.values ?? {}) };
  if (value === (baseValue ?? null)) delete values[key];
  else values[key] = value;
  if (Object.keys(values).length === 0) return changes.filter((c) => c !== existing);
  const change: PlanChange = { kind: "org_update", target_id: unitId, values };
  return existing ? changes.map((c) => (c === existing ? change : c)) : [...changes, change];
}

/** 配下に組織を新設する（id は呼ぶ側が作る）。 */
export function createUnit(changes: PlanChange[], id: string, parentId: string, name: string, code: string): PlanChange[] {
  return [...changes, { kind: "org_create", target_id: id, values: { name, code, parent_org_unit_id: parentId } }];
}

/**
 * 組織を廃止する。新設した組織なら新設ごと消し、その組織への異動と、その下に新設した組織も消す。
 * 廃止を取り消すときは undo を true にする。
 */
export function abolishUnit(changes: PlanChange[], tree: BoardTree, unitId: string, undo = false): PlanChange[] {
  if (undo) return changes.filter((c) => !(c.kind === "org_abolish" && c.target_id === unitId));
  if (tree.byId.get(unitId)?.state === "created") {
    const removed = new Set(tree.units.filter((u) => u.state === "created" && isWithin(tree, u.id, unitId)).map((u) => u.id));
    return changes.filter(
      (c) =>
        !(c.kind === "org_create" && c.target_id && removed.has(c.target_id)) &&
        !(c.kind === "assignment" && c.values.org_unit_id && removed.has(c.values.org_unit_id)) &&
        !(c.kind === "org_update" && c.values.parent_org_unit_id && removed.has(c.values.parent_org_unit_id)),
    );
  }
  if (changes.some((c) => c.kind === "org_abolish" && c.target_id === unitId)) return changes;
  return [...changes, { kind: "org_abolish", target_id: unitId, values: {} }];
}

/** 新設する組織の仮のコード（同じ親の下で重ならない連番）。 */
export function nextCode(tree: BoardTree, parentId: string): string {
  const parent = tree.byId.get(parentId);
  const prefix = `${parent?.code ?? "NEW"}-N`;
  let n = 1;
  const codes = new Set(tree.units.map((u) => u.code));
  while (codes.has(`${prefix}${n}`)) n++;
  return `${prefix}${n}`;
}
