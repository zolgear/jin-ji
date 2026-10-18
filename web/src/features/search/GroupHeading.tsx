// 検索の結果のグループの見出し（拠点・リモートワークの有無）。人数は件数（facets）から取った全体の人数。
import { RemoteIcon, SiteIcon, siteDetail } from "../../components/site";
import type { ResultGroup } from "./grouping";

/** level は見出しの段（カードの区切りは h3。表の中では見出し要素にしない＝null）。 */
export function GroupHeading({ group, level }: { group: ResultGroup; level: 3 | null }) {
  const Tag = level === 3 ? "h3" : "span";
  const detail = group.site ? siteDetail(group.site) : null;
  const icon =
    group.kind === "site" ? <SiteIcon className="size-4 text-muted" /> : group.kind === "remote" ? <RemoteIcon className={`size-4 ${group.remote ? "" : "text-muted opacity-60"}`} /> : null;
  return (
    <span className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5">
      <Tag className="m-0 inline-flex min-w-0 items-center gap-1.5 self-center text-sm font-bold">
        {icon}
        <span className={group.kind === "rest" ? "text-muted" : ""}>{group.title}</span>
      </Tag>
      {detail && <span className="text-xs text-muted">{detail}</span>}
      <span className="tabular text-xs text-muted">
        {group.total !== null ? (
          <>
            <span className="font-bold text-fg">{group.total.toLocaleString("ja-JP")}</span> 人
            {group.people.length < group.total && <span>（表示中 {group.people.length.toLocaleString("ja-JP")}）</span>}
          </>
        ) : (
          <>表示中 {group.people.length.toLocaleString("ja-JP")} 人</>
        )}
      </span>
    </span>
  );
}
