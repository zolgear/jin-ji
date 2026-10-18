// 名刺の形の人のカード（日本の名刺 91×55mm の比率）。法人・組織・役職・氏名・フリガナ・ローマ字・連絡先・拠点を載せる。
// リモートワークの対象の人には印（アイコン + 文字）を付ける。対象外・非公開の人には付けない（一覧の表では区別して出す）。
// 値は API の投影をそのまま出し、見えない項目は「非公開」、値の無い項目は「—」（人の詳細と同じ）。空欄にして「無い」と誤解させない。
// カード全体（氏名のボタンを広げたもの）で人の詳細を開き、メール・内線は個別にコピーできる。
import { useEffect, useRef, useState, type ReactNode } from "react";
import { RemoteWorkBadge, siteDetail } from "../../components/site";
import { Badge, EmptyValue, isVisible, personKana, personName, PrivateValue } from "../../components/ui";
import type { Assignment, Person } from "../../data/types";
import { useFormatDate } from "../../lib/dateStyle";

/** カードの所属の要約（主務を先に。兼務・出向の数）。 */
export function assignmentSummary(person: Person): { home: Assignment | null; concurrent: number; secondment: number } {
  const list = person.assignments ?? [];
  const home =
    list.find((a) => a.type === "primary" && a.secondment === "none") ?? list.find((a) => a.type === "primary") ?? list[0] ?? null;
  return {
    home,
    concurrent: list.filter((a) => a !== home && a.type === "concurrent" && a.secondment === "none").length,
    secondment: list.filter((a) => a.secondment !== "none").length,
  };
}

/** ローマ字の氏名（名 姓。人の詳細と同じ表記）。 */
export function latinName(person: Person): string | null {
  const name = [person.given_name_latin, person.family_name_latin].filter(Boolean).join(" ");
  return name || null;
}

/** 頭文字（顔写真の代わり）。氏名が見えなければ null。 */
function initialOf(person: Person): string | null {
  const source = person.family_name || person.display_name;
  return source ? [...source.trim()][0] ?? null : null;
}

/** 項目のアイコン。short があれば短い名前を画面にも出す（アイコンだけでは分かりにくい内線・携帯）。 */
function Icon({ d, label, short }: { d: string; label: string; short?: string }) {
  return (
    <>
      <svg aria-hidden="true" viewBox="0 0 16 16" className="size-3.5 shrink-0 text-muted" fill="none" stroke="currentColor" strokeWidth="1.3" strokeLinecap="round" strokeLinejoin="round">
        <path d={d} />
      </svg>
      {short ? (
        <span className="shrink-0 text-[11px] text-muted">
          <span aria-hidden="true">{short}</span>
          <span className="sr-only">{label}</span>
        </span>
      ) : (
        <span className="sr-only">{label}</span>
      )}
    </>
  );
}

const ICONS = {
  mail: "M2 4h12v8H2zM2.5 4.5 8 9l5.5-4.5",
  phone: "M5.5 2h-2A1.5 1.5 0 0 0 2 3.5C2 9 7 14 12.5 14a1.5 1.5 0 0 0 1.5-1.5v-2l-3-1-1.3 1.3A8 8 0 0 1 4.7 6.3L6 5z",
  mobile: "M5 1.5h6a.5.5 0 0 1 .5.5v12a.5.5 0 0 1-.5.5H5a.5.5 0 0 1-.5-.5V2a.5.5 0 0 1 .5-.5zM4.5 3.5h7M4.5 11.5h7M7.3 13h1.4",
  calendar: "M2.5 3.5h11v10h-11zM2.5 6.5h11M5.5 2v3M10.5 2v3",
  place: "M8 14.5s4.5-4.3 4.5-8a4.5 4.5 0 0 0-9 0c0 3.7 4.5 8 4.5 8zM8 8a1.6 1.6 0 1 0 0-3.2A1.6 1.6 0 0 0 8 8z",
};

/** 値をクリップボードにコピーするボタン。結果は読み上げ用の領域とボタンの見た目で知らせる。 */
export function CopyButton({ value, label, onCopied }: { value: string; label: string; onCopied: (message: string) => void }) {
  const [done, setDone] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => () => {
    if (timer.current) clearTimeout(timer.current);
  }, []);
  return (
    <button
      type="button"
      title={`${label}をコピー`}
      aria-label={`${label}をコピー`}
      onClick={async (e) => {
        e.stopPropagation();
        try {
          await navigator.clipboard.writeText(value);
          setDone(true);
          onCopied(`${label}をコピーしました`);
          if (timer.current) clearTimeout(timer.current);
          timer.current = setTimeout(() => setDone(false), 1600);
        } catch {
          onCopied(`${label}をコピーできませんでした（ブラウザの設定で許可されていません）`);
        }
      }}
      className="relative z-10 inline-flex size-6 shrink-0 items-center justify-center rounded-sm text-muted before:absolute before:-inset-2 before:content-[''] hover:bg-surface-2 hover:text-fg"
    >
      <svg aria-hidden="true" viewBox="0 0 16 16" className="size-3.5" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round">
        {done ? <path d="M3 8.5 6.5 12 13 4.5" /> : <path d="M5.5 5.5h7v8h-7zM3.5 10.5v-8h7" />}
      </svg>
    </button>
  );
}

/** 連絡先の 1 行。見えない項目は「非公開」、値が無ければ「—」。 */
function ContactRow({ icon, label, short, person, field, children }: { icon: string; label: string; short?: string; person: Person; field: string; children: ReactNode }) {
  const visible = isVisible(person, field);
  const empty = children === null || children === undefined || children === false || children === "";
  return (
    <div className="flex min-w-0 items-center gap-1.5" title={label}>
      <Icon d={icon} label={label} short={short} />
      {!visible ? <PrivateValue /> : empty ? <EmptyValue /> : children}
    </div>
  );
}

export function PersonCard({
  person,
  selected,
  onOpen,
  onCopied,
  showHireDate = false,
}: {
  person: Person;
  selected: boolean;
  onOpen: () => void;
  onCopied: (message: string) => void;
  /** 入社年月日の順に並べているときは、並びの基準の値として入社年月日も出す。 */
  showHireDate?: boolean;
}) {
  const formatDate = useFormatDate();
  const { home, concurrent, secondment } = assignmentSummary(person);
  const kana = personKana(person);
  const latin = latinName(person);
  const initial = initialOf(person);
  const suspended = person.account_status === "suspended";
  const terminated = person.employment_status === "terminated";
  const assignmentsVisible = isVisible(person, "assignments");

  return (
    <article
      className={`group relative flex aspect-[91/55] min-w-0 flex-col gap-2 rounded-lg border p-4 ${terminated ? "bg-surface-2" : "bg-surface"} shadow-[0_1px_2px_rgb(0_0_0/0.06)] transition-shadow hover:shadow-[0_4px_14px_rgb(0_0_0/0.10)] has-[.card-open:focus-visible]:outline-2 has-[.card-open:focus-visible]:outline-offset-2 has-[.card-open:focus-visible]:outline-[var(--focus)] ${
        selected ? "border-accent ring-1 ring-accent" : "border-border"
      }`}
    >
      {/* 上段: 法人・組織・役職（名刺の肩書き）と頭文字 */}
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1 leading-snug">
          <div className="truncate text-[11px] text-muted">
            {isVisible(person, "legal_entity") ? (person.legal_entity?.name ?? <EmptyValue />) : <PrivateValue />}
          </div>
          {!assignmentsVisible ? (
            <div className="text-xs">
              <PrivateValue />
            </div>
          ) : home ? (
            <>
              <div className="truncate text-xs text-fg">{home.org_unit_name}</div>
              {home.position_title && <div className="truncate text-xs font-bold text-fg">{home.position_title}</div>}
            </>
          ) : (
            <div className="text-xs text-muted">所属なし</div>
          )}
        </div>
        <span
          aria-hidden="true"
          className="flex size-9 shrink-0 items-center justify-center rounded-full border border-border bg-surface-2 text-base font-bold text-muted"
        >
          {initial ?? (
            <svg viewBox="0 0 16 16" className="size-5" fill="currentColor">
              <path d="M8 8a3 3 0 1 0 0-6 3 3 0 0 0 0 6Zm-5 6c0-2.8 2.2-4.5 5-4.5s5 1.7 5 4.5H3Z" />
            </svg>
          )}
        </span>
      </div>

      {/* 中段: 氏名（大きく）・フリガナ・ローマ字 */}
      <div className="min-w-0">
        <div className="truncate text-[11px] leading-tight tracking-wider text-muted">{kana ?? " "}</div>
        <h3 className="m-0 flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5 text-xl leading-tight font-bold">
          <button
            type="button"
            onClick={onOpen}
            aria-current={selected ? "true" : undefined}
            className="card-open min-w-0 truncate text-left outline-none after:absolute after:inset-0 after:rounded-lg after:content-['']"
          >
            {personName(person)}
          </button>
        </h3>
        <div className="flex min-h-[18px] flex-wrap items-center gap-1 text-[11px] leading-tight text-muted">
          {latin && <span className="truncate tracking-wide">{latin}</span>}
          {concurrent > 0 && <Badge tone="concurrent">兼務 {concurrent}</Badge>}
          {secondment > 0 && <Badge tone="secondment">出向</Badge>}
          {person.remote_work === true && <RemoteWorkBadge />}
          {suspended && <Badge tone="warn">一時停止中</Badge>}
          {terminated && <Badge tone="danger">退職</Badge>}
        </div>
      </div>

      {/* 下段: 連絡先と拠点 */}
      <div className="mt-auto grid min-w-0 gap-0.5 border-t border-border/70 pt-2 text-xs">
        <ContactRow icon={ICONS.mail} label="社用メール" person={person} field="work_email">
          {person.work_email && (
            <span className="flex min-w-0 items-center gap-0.5">
              <a href={`mailto:${person.work_email}`} className="relative z-10 min-w-0 truncate text-accent hover:underline">
                {person.work_email}
              </a>
              <CopyButton value={person.work_email} label="社用メール" onCopied={onCopied} />
            </span>
          )}
        </ContactRow>
        <div className="grid grid-cols-2 gap-2">
          <ContactRow icon={ICONS.phone} label="内線" short="内線" person={person} field="extension_number">
            {person.extension_number && (
              <span className="flex items-center gap-0.5">
                <span className="tabular">{person.extension_number}</span>
                <CopyButton value={person.extension_number} label="内線" onCopied={onCopied} />
              </span>
            )}
          </ContactRow>
          <ContactRow icon={ICONS.mobile} label="社用携帯" short="携帯" person={person} field="work_mobile_phone">
            {person.work_mobile_phone && <span className="tabular truncate">{person.work_mobile_phone}</span>}
          </ContactRow>
        </div>
        <div className={showHireDate ? "grid grid-cols-2 gap-2" : ""}>
          <ContactRow icon={ICONS.place} label="拠点" person={person} field="site">
            {person.site && (
              <span className="min-w-0 truncate" title={`${person.site.name}（${siteDetail(person.site)}）`}>
                {person.site.name}
                {!showHireDate && person.site.prefecture_name && <span className="ml-1.5 text-[11px] text-muted">{person.site.prefecture_name}</span>}
              </span>
            )}
          </ContactRow>
          {showHireDate && (
            <ContactRow icon={ICONS.calendar} label="入社年月日" short="入社" person={person} field="hire_date">
              {person.hire_date && <span className="tabular truncate">{formatDate(person.hire_date)}</span>}
            </ContactRow>
          )}
        </div>
      </div>
    </article>
  );
}

/** 読み込み中のカード（形だけ）。 */
export function PersonCardSkeleton() {
  return (
    <div aria-hidden="true" className="flex aspect-[91/55] flex-col gap-3 rounded-lg border border-border bg-surface p-4">
      <div className="flex gap-3">
        <div className="flex-1 space-y-1.5">
          <div className="h-2.5 w-1/3 animate-pulse rounded-sm bg-surface-2" />
          <div className="h-2.5 w-2/3 animate-pulse rounded-sm bg-surface-2" />
        </div>
        <div className="size-10 animate-pulse rounded-full bg-surface-2" />
      </div>
      <div className="space-y-1.5">
        <div className="h-2 w-1/4 animate-pulse rounded-sm bg-surface-2" />
        <div className="h-5 w-1/2 animate-pulse rounded-sm bg-surface-2" />
      </div>
      <div className="mt-auto space-y-1.5 border-t border-border/70 pt-2">
        <div className="h-2.5 w-3/4 animate-pulse rounded-sm bg-surface-2" />
        <div className="h-2.5 w-1/2 animate-pulse rounded-sm bg-surface-2" />
      </div>
    </div>
  );
}
