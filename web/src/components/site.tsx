// 拠点とリモートワークの有無の表示部品。値は API の投影をそのまま出す（見えるかどうかの判定は呼び出し側の FieldValue）。
// リモートワークは「対象か否か」だけを扱い、理由・頻度は持たない（ADR-0002）。印は色に頼らず、アイコンと文字で示す。
import type { SiteRef } from "../data/types";
import { siteTypeLabel } from "../lib/labels";

/** 拠点のアイコン（建物）。 */
export function SiteIcon({ className = "size-3.5" }: { className?: string }) {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className={`${className} shrink-0`} fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinejoin="round">
      <path d="M2.5 14V5.5L7 3v11M7 14V2h6.5v12M1.5 14h13M9 4.5h1.5M9 7h1.5M9 9.5h1.5M4.2 7.5h1M4.2 10h1" />
    </svg>
  );
}

/** リモートワークのアイコン（家とノート PC）。 */
export function RemoteIcon({ className = "size-3.5" }: { className?: string }) {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className={`${className} shrink-0`} fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round">
      <path d="M1.8 7.2 8 2l6.2 5.2M3.5 6v7.5h9V6M5.8 13.5V10h4.4v3.5" />
    </svg>
  );
}

/** 拠点の種別・都道府県（「工場・神奈川県」）。 */
export function siteDetail(site: Pick<SiteRef, "type" | "prefecture_name">): string {
  return [siteTypeLabel(site.type), site.prefecture_name].filter(Boolean).join("・");
}

/**
 * 拠点の名前と種別・都道府県。inheritedNote は引き継いだ拠点の注記（人: 「主務の組織の拠点」、組織: 「上位組織から」）、
 * ownNote は引き継いでいない拠点の注記（人: 所属組織と違う拠点）。
 */
export function SiteValue({ site, inheritedNote, ownNote }: { site: SiteRef; inheritedNote?: string; ownNote?: string }) {
  const detail = siteDetail(site);
  return (
    <span className="inline-flex min-w-0 flex-wrap items-baseline gap-x-1.5">
      <span className="font-bold">{site.name}</span>
      {detail && <span className="text-xs text-muted">{detail}</span>}
      {site.inherited && inheritedNote && <span className="text-xs text-muted">（{inheritedNote}）</span>}
      {!site.inherited && ownNote && <span className="text-xs text-muted">（{ownNote}）</span>}
    </span>
  );
}

/** リモートワークの有無（アイコン + 文字）。 */
export function RemoteWorkValue({ value }: { value: boolean }) {
  return value ? (
    <span className="inline-flex items-center gap-1">
      <RemoteIcon />
      リモートワーク対象
    </span>
  ) : (
    <span className="inline-flex items-center gap-1 text-muted">
      <RemoteIcon className="size-3.5 opacity-60" />
      対象外
    </span>
  );
}

/** 名刺・一覧の小さな印（リモートワークの対象の人だけ。アイコン + 短い文字、読み上げは正式な名前）。 */
export function RemoteWorkBadge() {
  return (
    <span className="inline-flex shrink-0 items-center gap-1 rounded-sm border border-border px-1.5 text-[11px] leading-[18px] font-bold text-fg" title="リモートワーク対象">
      <RemoteIcon className="size-3" />
      <span aria-hidden="true">リモート</span>
      <span className="sr-only">リモートワーク対象</span>
    </span>
  );
}
