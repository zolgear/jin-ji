// 人の詳細。API の投影（PersonResponse）をそのまま表示し、見えない項目は「非公開」、値の無い項目は「—」とする。
// 内示・将来の終了日は API が返さない前提（返ってきても将来の日付は表示しない）。
import { useEffect, useRef } from "react";
import { useOrgTree, usePerson } from "../../app/hooks";
import { useNav } from "../../app/nav";
import { HelpTip } from "../../components/popover";
import { RemoteWorkValue, SiteValue } from "../../components/site";
import { Badge, ErrorNote, FieldValue, isVisible, personKana, personName, PrivateValue, Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { useQuery } from "../../data/query";
import { describeError, type Assignment, type Person } from "../../data/types";
import { workplaceOf } from "../../data/workplace";
import { useRoute } from "../../lib/router";
import { useFormatDate } from "../../lib/dateStyle";
import { EMPLOYMENT_STATUS_LABEL, employmentTypeLabel } from "../../lib/labels";
import { ReportCorrection } from "../corrections/ReportCorrection";

function assignmentKind(a: Assignment): { label: string; tone: "primary" | "concurrent" | "secondment" } {
  if (a.secondment === "outbound") return { label: "出向", tone: "secondment" };
  if (a.secondment === "inbound") return { label: "出向受入", tone: "secondment" };
  return a.type === "primary" ? { label: "主務", tone: "primary" } : { label: "兼務", tone: "concurrent" };
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="grid grid-cols-[7.5em_1fr] gap-2 border-b border-border/70 py-1.5 last:border-b-0">
      <dt className="text-muted">{label}</dt>
      <dd className="m-0 min-w-0">{children}</dd>
    </div>
  );
}

function Managers({ person }: { person: Person }) {
  const { adapter } = useAdapter();
  const { asOf, openPerson } = useNav();
  const visible = isVisible(person, "managers");
  const managers = useQuery(adapter, visible ? `managers:${person.id}:${asOf}` : null, () => adapter.getManagers(person.id, asOf));
  if (!visible) return <PrivateValue />;
  if (managers.loading && !managers.data) return <Spinner />;
  if (managers.error) return <span className="text-muted">{describeError(managers.error)}</span>;
  const list = managers.data ?? [];
  if (list.length === 0) return <span className="text-muted">—</span>;
  return (
    <ul className="m-0 flex list-none flex-col gap-0.5 p-0">
      {list.map((m) => (
        <li key={m.id}>
          <button type="button" className="text-accent hover:underline" onClick={() => openPerson(m.id)}>
            {personName(m)}
          </button>
        </li>
      ))}
    </ul>
  );
}

/** 席（建物・階・席番号）。押すと建物・フロアモードでその席を開く。 */
function SeatValue({ person }: { person: Person }) {
  const { navigate } = useRoute();
  const { workplace, seat } = workplaceOf(person);
  if (!workplace) return null;
  const where = `${workplace.building_name} ${workplace.floor_label}`;
  const detail = seat ? seat.code : workplace.zone_name;
  return (
    <span className="flex flex-col items-start gap-0.5">
      <span>
        {where}
        {detail && <span className="tabular">・{detail}</span>}
      </span>
      <button
        type="button"
        className="text-xs text-accent hover:underline"
        onClick={() =>
          navigate({
            view: "overview-3d",
            params: { mode: "floor", site: workplace.site_id, building: workplace.building_id, floor: workplace.floor_id, person: person.id, plan: null, org: null, split: null, color: null },
          })
        }
      >
        建物・フロアで席を開く
      </button>
    </span>
  );
}

/** 今日の所在（外部から届いたもの）。見られる場合だけ。無いときは行ごと出さない（「不在」と出さない）。 */
function PresenceRow({ person }: { person: Person }) {
  const { presence } = workplaceOf(person);
  if (!presence) return null;
  return (
    <Row label="今日の所在">
      <span className="inline-flex flex-wrap items-center gap-1.5">
        <Badge tone="neutral">今日</Badge>
        <span>
          {presence.building_name} {presence.floor_label}
          {presence.seat_code ? <span className="tabular">・{presence.seat_code}</span> : presence.zone_name ? `・${presence.zone_name}` : ""}
        </span>
      </span>
    </Row>
  );
}

export function PersonDetail({ person }: { person: Person }) {
  const { adapter } = useAdapter();
  const { asOf, showOrgIn, view } = useNav();
  const tree = useOrgTree(asOf);
  const orgExists = (id: string) => tree.data?.units.some((u) => u.id === id) ?? false;
  const formatDateJa = useFormatDate();
  const kana = personKana(person);
  const latin = [person.given_name_latin, person.family_name_latin].filter(Boolean).join(" ");
  const suspended = person.account_status === "suspended";
  const terminated = person.employment_status === "terminated";
  const today = adapter.businessDate;
  // 将来の日付は表示しない（API は返さない前提だが、念のため UI でも出さない）
  const pastOnly = (d: string | null | undefined) => (d && d <= today ? d : null);
  const assignments = [...(person.assignments ?? [])].sort((a, b) => (a.type === "primary" ? -1 : 0) - (b.type === "primary" ? -1 : 0));

  return (
    <div className="flex flex-col gap-4">
      <div>
        {kana && <div className="text-xs text-muted">{kana}</div>}
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-xl font-bold">{personName(person)}</span>
          {suspended && <Badge tone="warn">一時停止中</Badge>}
          {terminated && <Badge tone="danger">退職</Badge>}
          {assignments.some((a) => a.secondment !== "none") && <Badge tone="secondment">出向</Badge>}
        </div>
        {latin && <div className="text-xs text-muted">{latin}</div>}
        {!isVisible(person, "display_name") && !isVisible(person, "family_name") && (
          <p className="mt-1 text-xs text-muted">氏名は公開範囲の外です。</p>
        )}
      </div>

      {suspended && (
        <p className="rounded-sm border border-warn-fg/30 bg-warn-bg px-3 py-2 text-xs text-warn-fg">
          アカウントは一時停止中です
          {person.account_suspension?.from ? `（${formatDateJa(person.account_suspension.from)}から）` : ""}。
          理由と終了予定日は jin-ji では扱いません。組織図上の所属は維持されます。
        </p>
      )}

      <section aria-labelledby="person-assignments">
        <h3 id="person-assignments" className="mb-1 text-xs font-bold text-muted">
          所属
        </h3>
        {!isVisible(person, "assignments") ? (
          <PrivateValue />
        ) : assignments.length === 0 ? (
          <p className="text-muted">表示している日付の所属はありません。</p>
        ) : (
          <ul className="m-0 flex list-none flex-col gap-1.5 p-0">
            {assignments.map((a) => {
              const kind = assignmentKind(a);
              const until = pastOnly(a.valid_until);
              return (
                <li key={`${a.org_unit_id}:${a.type}:${a.valid_from}`} className="rounded-sm border border-border px-2 py-1.5">
                  <div className="flex flex-wrap items-center gap-1.5">
                    <Badge tone={kind.tone}>{kind.label}</Badge>
                    {orgExists(a.org_unit_id) ? (
                      <button
                        type="button"
                        className="font-bold text-accent hover:underline"
                        onClick={() => showOrgIn(view === "overview-3d" ? view : "org-chart", a.org_unit_id)}
                      >
                        {a.org_unit_name}
                      </button>
                    ) : (
                      <span className="font-bold">{a.org_unit_name}</span>
                    )}
                    {a.position_title && <span>{a.position_title}</span>}
                  </div>
                  <div className="tabular text-xs text-muted">
                    {formatDateJa(a.valid_from)}から{until ? ` ${formatDateJa(until)}の前日まで` : ""}
                  </div>
                </li>
              );
            })}
          </ul>
        )}
      </section>

      <section aria-labelledby="person-contact">
        <h3 id="person-contact" className="mb-1 text-xs font-bold text-muted">
          連絡先
        </h3>
        <dl className="m-0">
          <Row label="社用メール">
            <FieldValue person={person} field="work_email">
              {person.work_email && (
                <a className="text-accent hover:underline" href={`mailto:${person.work_email}`}>
                  {person.work_email}
                </a>
              )}
            </FieldValue>
          </Row>
          <Row label="内線">
            <FieldValue person={person} field="extension_number">
              {person.extension_number && <span className="tabular">{person.extension_number}</span>}
            </FieldValue>
          </Row>
          <Row label="社用携帯">
            <FieldValue person={person} field="work_mobile_phone">
              {person.work_mobile_phone && <span className="tabular">{person.work_mobile_phone}</span>}
            </FieldValue>
          </Row>
          <Row label="拠点">
            <FieldValue person={person} field="site">{person.site && <SiteValue site={person.site} inheritedNote="主務の組織の拠点" ownNote="所属組織とは別の拠点" />}</FieldValue>
          </Row>
          <Row label="席">
            <FieldValue person={person} field="workplace">
              {workplaceOf(person).workplace !== null && <SeatValue person={person} />}
            </FieldValue>
          </Row>
          {asOf === today && <PresenceRow person={person} />}
          <Row label="リモートワーク">
            <FieldValue person={person} field="remote_work">{person.remote_work !== null && <RemoteWorkValue value={person.remote_work} />}</FieldValue>
          </Row>
        </dl>
      </section>

      <section aria-labelledby="person-basic">
        <h3 id="person-basic" className="mb-1 text-xs font-bold text-muted">
          基本情報
        </h3>
        <dl className="m-0">
          <Row label="上長">
            <Managers person={person} />
          </Row>
          <Row label="社員番号">
            <FieldValue person={person} field="employee_number">
              {person.employee_number && <span className="tabular">{person.employee_number}</span>}
            </FieldValue>
          </Row>
          <Row label="雇用元の法人">
            <FieldValue person={person} field="legal_entity">{person.legal_entity?.name}</FieldValue>
          </Row>
          <Row label="雇用区分">
            <FieldValue person={person} field="employment_type">{employmentTypeLabel(person.employment_type)}</FieldValue>
          </Row>
          <Row label="職種">
            <FieldValue person={person} field="job_type">{person.job_type}</FieldValue>
          </Row>
          <Row label="在籍">
            <FieldValue person={person} field="employment_status">
              {person.employment_status && (EMPLOYMENT_STATUS_LABEL[person.employment_status] ?? person.employment_status)}
            </FieldValue>
          </Row>
          <Row label="入社年月日">
            <FieldValue person={person} field="hire_date">
              {person.hire_date && <span className="tabular">{formatDateJa(person.hire_date)}</span>}
            </FieldValue>
          </Row>
          {(person.termination_date || !isVisible(person, "termination_date")) && (
            <Row label="退職年月日">
              <FieldValue person={person} field="termination_date">
                {pastOnly(person.termination_date) && <span className="tabular">{formatDateJa(person.termination_date)}</span>}
              </FieldValue>
            </Row>
          )}
        </dl>
      </section>
      <div className="flex items-center gap-0.5 text-xs text-muted">
        「非公開」と「—」について
        <HelpTip label="「非公開」と「—」について" icon="info" width={280}>
          <p className="m-0">
            <b>非公開</b>: 閲覧者の公開範囲の外にある項目
          </p>
          <p className="m-0">
            <b>—</b>: 公開範囲内で、値が登録されていない項目
          </p>
        </HelpTip>
      </div>
      {/* 今日の時点の表示のときだけ報告できる（過去の時点の値は、今の人事パッケージの値と違って当然のため） */}
      {asOf === today ? (
        <ReportCorrection person={person} />
      ) : (
        <p className="border-t border-border pt-3 text-xs text-muted">値の誤りの報告は、タイムマシンで今日を表示しているときに行えます。</p>
      )}
    </div>
  );
}

export function PersonPanel({ personId, onClose }: { personId: string; onClose: () => void }) {
  const { asOf } = useNav();
  const person = usePerson(personId, asOf);
  const heading = useRef<HTMLHeadingElement>(null);
  // 開いたら見出しへ移る（人が変わると key で作り直される）
  useEffect(() => {
    heading.current?.focus();
  }, []);
  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-center justify-between gap-2">
        <h2 ref={heading} tabIndex={-1} className="text-sm font-bold text-muted outline-none">
          人の詳細
        </h2>
        <button type="button" onClick={onClose} className="rounded-sm border border-border px-2 py-0.5 text-xs hover:bg-surface-2">
          閉じる
        </button>
      </div>
      {person.error ? (
        <ErrorNote>{describeError(person.error)}</ErrorNote>
      ) : person.data ? (
        <PersonDetail person={person.data} />
      ) : (
        <Spinner />
      )}
    </div>
  );
}
