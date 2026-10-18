// 検索の結果の一覧（表）。カードと同じ値を、列で見比べやすい形にする。見えない項目は「非公開」、値の無い項目は「—」。
import type { ReactNode } from "react";
import { RemoteWorkValue, siteDetail } from "../../components/site";
import { Badge, FieldValue, personKana, personName } from "../../components/ui";
import type { Person } from "../../data/types";
import { useFormatDate } from "../../lib/dateStyle";
import { employmentTypeLabel } from "../../lib/labels";
import { GroupHeading } from "./GroupHeading";
import type { ResultGroup } from "./grouping";
import { assignmentSummary, CopyButton } from "./PersonCard";

function Th({ children, className = "" }: { children: ReactNode; className?: string }) {
  return (
    <th scope="col" className={`sticky top-0 ${className.includes("z-[") ? "" : "z-[1]"} border-b border-border bg-surface-2 px-2 py-1.5 text-left text-xs font-bold whitespace-nowrap text-muted ${className}`}>
      {children}
    </th>
  );
}

const COLUMNS = 9;

export function PeopleTable({
  groups,
  selectedId,
  onOpen,
  onCopied,
}: {
  /** 結果（グループ化しないときは kind = "all" の 1 つ）。グループの見出しは行の区切りとして出す。 */
  groups: ResultGroup[];
  selectedId: string | null;
  onOpen: (id: string) => void;
  onCopied: (message: string) => void;
}) {
  return (
    <div className="max-h-[70dvh] max-w-full overflow-auto rounded-lg border border-border bg-surface">
      <table className="w-full min-w-[56rem] border-collapse text-sm">
        <caption className="sr-only">検索の結果（氏名のボタンで人の詳細を開く）</caption>
        <thead>
          <tr>
            <Th className="left-0 z-[3]">氏名</Th>
            <Th>組織・役職</Th>
            <Th>法人</Th>
            <Th>雇用区分</Th>
            <Th>職種</Th>
            <Th>拠点</Th>
            <Th>リモートワーク</Th>
            <Th>内線</Th>
            <Th>入社年月日</Th>
          </tr>
        </thead>
        {groups.map((g) => (
          <tbody key={g.key}>
            {g.kind !== "all" && (
              <tr>
                <th scope="colgroup" colSpan={COLUMNS} className="sticky top-[30px] z-[1] border-y border-border bg-surface px-2 py-1.5 text-left font-normal">
                  <GroupHeading group={g} level={null} />
                </th>
              </tr>
            )}
            {g.people.map((p) => <Row key={p.id} p={p} selected={p.id === selectedId} onOpen={onOpen} onCopied={onCopied} />)}
          </tbody>
        ))}
      </table>
    </div>
  );
}

function Row({ p, selected, onOpen, onCopied }: { p: Person; selected: boolean; onOpen: (id: string) => void; onCopied: (message: string) => void }) {
  const formatDate = useFormatDate();
  const { home, concurrent, secondment } = assignmentSummary(p);
  const kana = personKana(p);
            return (
              <tr className={`border-b border-border/70 last:border-b-0 ${selected ? "bg-accent-weak" : "hover:bg-surface-2"}`}>
                <td className={`sticky left-0 z-[2] border-r border-border/70 px-2 py-1.5 align-top whitespace-nowrap ${selected ? "bg-accent-weak" : "bg-surface"}`}>
                  {kana && <div className="text-[11px] leading-tight text-muted">{kana}</div>}
                  <button
                    type="button"
                    onClick={() => onOpen(p.id)}
                    aria-current={selected ? "true" : undefined}
                    className="text-left font-bold text-accent hover:underline"
                  >
                    {personName(p)}
                  </button>
                  <div className="mt-0.5 flex flex-wrap gap-1">
                    {concurrent > 0 && <Badge tone="concurrent">兼務 {concurrent}</Badge>}
                    {secondment > 0 && <Badge tone="secondment">出向</Badge>}
                    {p.account_status === "suspended" && <Badge tone="warn">一時停止中</Badge>}
                    {p.employment_status === "terminated" && <Badge tone="danger">退職</Badge>}
                  </div>
                </td>
                <td className="px-2 py-1.5 align-top">
                  <FieldValue person={p} field="assignments">
                    {home && (
                      <span>
                        {home.org_unit_name}
                        {home.position_title && <span className="ml-1 text-xs font-bold">{home.position_title}</span>}
                      </span>
                    )}
                  </FieldValue>
                </td>
                <td className="px-2 py-1.5 align-top text-xs">
                  <FieldValue person={p} field="legal_entity">{p.legal_entity?.name}</FieldValue>
                </td>
                <td className="px-2 py-1.5 align-top text-xs whitespace-nowrap">
                  <FieldValue person={p} field="employment_type">{employmentTypeLabel(p.employment_type)}</FieldValue>
                </td>
                <td className="px-2 py-1.5 align-top text-xs">
                  <FieldValue person={p} field="job_type">{p.job_type}</FieldValue>
                </td>
                <td className="px-2 py-1.5 align-top text-xs">
                  <FieldValue person={p} field="site">
                    {p.site && (
                      <span>
                        {p.site.name}
                        <span className="block text-[11px] text-muted">{siteDetail(p.site)}</span>
                      </span>
                    )}
                  </FieldValue>
                </td>
                <td className="px-2 py-1.5 align-top text-xs whitespace-nowrap">
                  <FieldValue person={p} field="remote_work">{p.remote_work !== null && <RemoteWorkValue value={p.remote_work} />}</FieldValue>
                </td>
                <td className="px-2 py-1.5 align-top text-xs whitespace-nowrap">
                  <FieldValue person={p} field="extension_number">
                    {p.extension_number && (
                      <span className="inline-flex items-center gap-0.5">
                        <span className="tabular">{p.extension_number}</span>
                        <CopyButton value={p.extension_number} label="内線" onCopied={onCopied} />
                      </span>
                    )}
                  </FieldValue>
                </td>
                <td className="tabular px-2 py-1.5 align-top text-xs whitespace-nowrap">
                  <FieldValue person={p} field="hire_date">{p.hire_date && formatDate(p.hire_date)}</FieldValue>
                </td>
              </tr>
            );
}
