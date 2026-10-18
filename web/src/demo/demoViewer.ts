// デモの利用者（架空）。デモの認証（demoAuthAdapter）と修正依頼のデモ（demoCorrections）が同じ人・ロールを使う。
// 既定は人事課の 1 人で、人事管理者・修正担当者。修正依頼の画面のデモの切り替えで「一般の社員」の見え方にできる（端末に覚える）。
import type { DemoDataset, DemoPerson } from "./model";

export type DemoViewerKind = "corrector" | "member";

const KEY = "jinji.demo.viewer";

export function demoViewerKind(): DemoViewerKind {
  try {
    return globalThis.localStorage?.getItem(KEY) === "member" ? "member" : "corrector";
  } catch {
    return "corrector";
  }
}

export function setDemoViewerKind(kind: DemoViewerKind): void {
  try {
    if (kind === "member") localStorage.setItem(KEY, kind);
    else localStorage.removeItem(KEY);
  } catch {
    // 覚えられなくても既定で動く
  }
}

/** デモの利用者のロール。 */
export function demoRoles(kind: DemoViewerKind = demoViewerKind()): string[] {
  return kind === "member" ? [] : ["hr_admin", "hr_corrector"];
}

/** ログイン中の人（デモデータの人事課の最初の人。無ければ最初の人）。 */
export function demoMe(data: DemoDataset): DemoPerson {
  const hr = data.org_units.find((o) => o.name === "人事課");
  return (
    data.people.find((p) => p.assignments.some((a) => a.org_unit_id === hr?.id && a.type === "primary" && a.valid_until === null)) ??
    data.people[0]!
  );
}
