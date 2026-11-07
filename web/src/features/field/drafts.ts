// 編集中の変更（保存・反映の前）を覚えておく。タブや画面を移っても消えないよう sessionStorage に置く（読めなければメモリだけ）。
import type { PlanInput } from "../../data/field";

const KEY = "jinji.changes.drafts";
const memory = new Map<string, PlanInput>();
let loaded = false;

function load() {
  if (loaded) return;
  loaded = true;
  try {
    const raw = sessionStorage.getItem(KEY);
    if (raw) for (const [k, v] of Object.entries(JSON.parse(raw) as Record<string, PlanInput>)) memory.set(k, v);
  } catch {
    // 読めなければ覚えていないものとして動く
  }
}

/** 新しい編集は "new"、保存した計画はその ID。 */
export function loadDraft(key: string): PlanInput | null {
  load();
  return memory.get(key) ?? null;
}

export function hasDraft(key: string): boolean {
  load();
  return memory.has(key);
}

export function saveDraft(key: string, draft: PlanInput | null) {
  load();
  if (draft) memory.set(key, draft);
  else if (!memory.delete(key)) return;
  try {
    sessionStorage.setItem(KEY, JSON.stringify(Object.fromEntries(memory)));
  } catch {
    // 覚えられなくても編集は続けられる
  }
}
