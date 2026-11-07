// 組織（名称・コード）と人（氏名）をまとめて探す検索の欄。組織の変更の編集・3D 表示の上の帯に置く。
import { useEffect, useId, useMemo, useRef, useState } from "react";
import { personName } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";

const inputClass = "min-h-10 rounded-md border border-border bg-surface px-3 py-2 text-sm text-fg";

export type SearchHit =
  | { kind: "unit"; id: string; name: string; sub: string }
  | { kind: "person"; id: string; name: string; sub: string; org: string | null; title: string | null };

/** 探す対象の組織（id・名称・コード・候補に添える説明）。 */
export interface SearchUnit {
  id: string;
  name: string;
  code: string;
  sub: string;
}

/** 組織（名称・コード）と人（氏名）をまとめて探す。選んだものを onPick に渡す（図をそこへ動かすのは呼ぶ側）。 */
export function OrgPersonSearch({ units: all, asOf, onPick }: { units: SearchUnit[]; asOf: string; onPick: (hit: SearchHit) => void }) {
  const { adapter } = useAdapter();
  const [query, setQuery] = useState("");
  const [people, setPeople] = useState<SearchHit[]>([]);
  const [open, setOpen] = useState(false);
  const box = useRef<HTMLDivElement>(null);
  const listId = useId();
  const q = query.trim();
  const units = useMemo<SearchHit[]>(() => {
    if (!q) return [];
    const needle = q.toLowerCase();
    return all
      .filter((u) => u.name.toLowerCase().includes(needle) || u.code.toLowerCase().includes(needle))
      .slice(0, 6)
      .map((u) => ({ kind: "unit", id: u.id, name: u.name, sub: u.sub }));
  }, [q, all]);
  useEffect(() => {
    if (!q) return;
    let cancelled = false;
    const timer = setTimeout(() => {
      adapter.searchPeople(q, { asOf: asOf, limit: 6 }).then(
        (page) => {
          if (cancelled) return;
          setPeople(
            page.items.map((p) => {
              const a = p.assignments?.find((x) => x.type === "primary") ?? null;
              return { kind: "person", id: p.id, name: personName(p), sub: a?.org_unit_name ?? "", org: a?.org_unit_id ?? null, title: a?.position_title ?? null };
            }),
          );
        },
        () => !cancelled && setPeople([]),
      );
    }, 200);
    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [adapter, asOf, q]);
  // 外を押したら候補を閉じる
  useEffect(() => {
    if (!open) return;
    const down = (e: PointerEvent) => !box.current?.contains(e.target as Node) && setOpen(false);
    window.addEventListener("pointerdown", down);
    return () => window.removeEventListener("pointerdown", down);
  }, [open]);
  const hits = q ? [...units, ...people] : [];
  const pick = (h: SearchHit) => {
    onPick(h);
    setQuery("");
    setPeople([]);
    setOpen(false);
  };
  return (
    <div ref={box} className="relative">
      <input
        type="search"
        aria-label="組織・人を探す"
        aria-controls={listId}
        className={`${inputClass} h-10 w-48 sm:w-64`}
        value={query}
        placeholder="組織・人を探す"
        autoComplete="off"
        onChange={(e) => (setQuery(e.target.value), setOpen(true))}
        onFocus={() => setOpen(true)}
        onKeyDown={(e) => {
          if (e.key === "Enter" && hits[0]) pick(hits[0]);
          if (e.key === "Escape") setOpen(false);
        }}
      />
      {open && hits.length > 0 && (
        <ul id={listId} className="absolute top-full left-0 z-20 m-0 mt-1 max-h-80 w-72 list-none overflow-y-auto rounded-md border border-border bg-surface p-0 shadow-xl">
          {hits.map((h) => (
            <li key={`${h.kind}:${h.id}`}>
              <button type="button" className="flex w-full items-baseline gap-2 px-3 py-1.5 text-left hover:bg-surface-2" onClick={() => pick(h)}>
                <span className="shrink-0 rounded-sm bg-surface-2 px-1 text-xs text-muted">{h.kind === "unit" ? "組織" : "人"}</span>
                <span className="shrink-0">{h.name}</span>
                <span className="truncate text-xs text-muted">{h.sub}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
