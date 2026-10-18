// 画面の切り替えと設定のレール。幅が md 以上では左にアイコンだけを縦に並べ（名前はツールチップと aria-label）、
// それより狭いとき（スマホ）は下のタブバーにして、アイコンの下に短い名前を出す（指で触る画面にはホバーが無いため）。
// 下の段（スマホでは右端）に、データの取得元・配色の設定と利用者メニューを置く。
import type { ReactNode } from "react";
import { UserMenu } from "../auth/session";
import { HelpTip, Popover } from "../components/popover";
import { ThemeSwitch } from "../components/ThemeSwitch";
import { DEMO_AVAILABLE, useAdapter, type AdapterChoice } from "../data/adapterContext";
import { summaryLabel, useCorrectionSummary } from "../features/corrections/shared";
import { SEARCH_PARAM_KEYS } from "../features/search/searchState";
import { useFormatDate } from "../lib/dateStyle";
import { formatHash, parseHash, useRoute, type View } from "../lib/router";
import { useNav } from "./nav";

export const VIEW_LABEL: Record<View, string> = { "org-chart": "組織図", "overview-3d": "3D 表示", search: "検索", corrections: "修正依頼" };

/** 画面を切り替えるときに引き継ぐ引数（修正依頼の画面は日付・選択を持たず、他の画面は修正依頼の絞り込みを、検索の画面以外は検索の条件を持たない）。 */
function paramsFor(view: View, current: URLSearchParams): URLSearchParams {
  const params = new URLSearchParams(current);
  if (view === "corrections") {
    for (const k of ["org", "person", "as_of", ...SEARCH_PARAM_KEYS]) params.delete(k);
    return params;
  }
  params.delete("status");
  if (view === "search") params.delete("org");
  else for (const k of SEARCH_PARAM_KEYS) params.delete(k);
  return params;
}

/** 取得元（source）だけを残した URL の引数（トップへのリンク用）。 */
function sourceOnly(params: URLSearchParams): URLSearchParams {
  const source = params.get("source");
  return new URLSearchParams(source ? { source } : {});
}

// ---- アイコン（24 の枠・線 1.6。依存を足さずインラインの SVG） ----

function Svg({ children }: { children: ReactNode }) {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" className="size-[22px] shrink-0" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round">
      {children}
    </svg>
  );
}

const VIEW_ICON: Record<View, ReactNode> = {
  // 組織の上下（上に 1 つ、下に 2 つの箱）
  "org-chart": (
    <Svg>
      <rect x="8.5" y="3" width="7" height="5" rx="1.2" />
      <rect x="3" y="16" width="7" height="5" rx="1.2" />
      <rect x="14" y="16" width="7" height="5" rx="1.2" />
      <path d="M12 8v4M6.5 16v-2.2c0-.5.4-.8.8-.8h9.4c.5 0 .8.3.8.8V16" />
    </Svg>
  ),
  // 立方体
  "overview-3d": (
    <Svg>
      <path d="M12 2.8 20 7.2v9.6L12 21.2 4 16.8V7.2L12 2.8Z" />
      <path d="M4 7.2 12 11.6l8-4.4M12 11.6v9.6" />
    </Svg>
  ),
  search: (
    <Svg>
      <circle cx="10.5" cy="10.5" r="6.2" />
      <path d="m15.2 15.2 5.3 5.3" />
    </Svg>
  ),
  // 受け箱（届いた依頼）
  corrections: (
    <Svg>
      <path d="M3.5 13.5 6 5.2c.2-.7.8-1.2 1.5-1.2h9c.7 0 1.3.5 1.5 1.2l2.5 8.3V18c0 1.1-.9 2-2 2h-13c-1.1 0-2-.9-2-2v-4.5Z" />
      <path d="M3.5 13.5h4.6l1.4 2.5h5l1.4-2.5h4.6" />
    </Svg>
  ),
};

function SettingsIcon() {
  // 歯車（8 つの歯と中央の穴）
  return (
    <Svg>
      <path d="M12.2 2.8h-.4a2 2 0 0 0-2 2v.2a2 2 0 0 1-1 1.7l-.4.2a2 2 0 0 1-2 0l-.2-.1a2 2 0 0 0-2.7.7l-.2.4a2 2 0 0 0 .7 2.7l.1.1a2 2 0 0 1 1 1.8v.5a2 2 0 0 1-1 1.7l-.1.1a2 2 0 0 0-.7 2.7l.2.4a2 2 0 0 0 2.7.7l.2-.1a2 2 0 0 1 2 0l.4.2a2 2 0 0 1 1 1.7v.2a2 2 0 0 0 2 2h.4a2 2 0 0 0 2-2v-.2a2 2 0 0 1 1-1.7l.4-.2a2 2 0 0 1 2 0l.2.1a2 2 0 0 0 2.7-.7l.2-.4a2 2 0 0 0-.7-2.7l-.1-.1a2 2 0 0 1-1-1.7v-.5a2 2 0 0 1 1-1.8l.1-.1a2 2 0 0 0 .7-2.7l-.2-.4a2 2 0 0 0-2.7-.7l-.2.1a2 2 0 0 1-2 0l-.4-.2a2 2 0 0 1-1-1.7V4.8a2 2 0 0 0-2-2Z" />
      <circle cx="12" cy="12" r="3" />
    </Svg>
  );
}

/** md 以上でアイコンの右に出すツールチップ（読み上げは aria-label が受け持つので隠す）。 */
function RailTooltip({ children }: { children: ReactNode }) {
  return (
    <span
      aria-hidden="true"
      className="pointer-events-none absolute top-1/2 left-full z-40 ml-3 hidden -translate-y-1/2 rounded-md bg-fg px-2.5 py-1 text-xs font-bold whitespace-nowrap text-bg opacity-0 shadow-md transition-opacity delay-150 group-hover:opacity-100 group-focus-visible:opacity-100 md:block"
    >
      {children}
    </span>
  );
}

/** スマホのタブバーでアイコンの下に出す短い名前。 */
function TabLabel({ children }: { children: ReactNode }) {
  return <span className="text-[10px] leading-none font-bold md:hidden">{children}</span>;
}

// レールの 1 つの項目の形（md 以上は 44px の正方形、スマホはタブの 1 マス）
const ITEM =
  "relative flex flex-1 flex-col items-center justify-center gap-1 rounded-lg py-1.5 no-underline md:size-11 md:flex-none md:gap-0 md:py-0";

/** 修正依頼の件数のバッジ（修正担当者・人事管理者だけ）。件数だけを出し、人事データの値は出さない。 */
function useCorrectionsNav(): { badge: ReactNode; label: string | undefined } {
  const summary = useCorrectionSummary();
  if (!summary || summary.open === 0) return { badge: null, label: summary ? summaryLabel(summary) : undefined };
  return {
    label: summaryLabel(summary),
    badge: (
      <span
        aria-hidden="true"
        className="absolute top-0.5 left-1/2 ml-1 inline-flex h-4 min-w-4 items-center justify-center gap-px rounded-full bg-accent px-1 text-[10px] leading-none font-bold text-accent-fg tabular ring-2 ring-surface md:top-0 md:right-0 md:left-auto md:ml-0"
      >
        {summary.stale > 0 && (
          <svg viewBox="0 0 12 12" className="size-2" fill="currentColor">
            <path d="M6 1 11.2 10.5H.8L6 1Zm-.6 3.6v2.8h1.2V4.6H5.4Zm0 3.6v1.2h1.2V8.2H5.4Z" />
          </svg>
        )}
        {summary.open > 99 ? "99+" : summary.open}
      </span>
    ),
  };
}

/** データの取得元（デモ 350 人・デモ 1 万人・API）。 */
function SourceSwitch() {
  const { choice, setChoice, adapter } = useAdapter();
  if (!DEMO_AVAILABLE) return <p className="m-0 text-sm">{adapter.label}</p>;
  return (
    <select
      aria-label="データ"
      value={choice}
      onChange={(e) => {
        const next = e.target.value as AdapterChoice;
        // 取得元を変えたら選択と日付を戻す
        window.history.replaceState(null, "", formatHash({ view: parseHash(window.location.hash).view, params: new URLSearchParams({ source: next }) }));
        window.dispatchEvent(new HashChangeEvent("hashchange"));
        setChoice(next);
      }}
      className="h-8 w-full rounded-md border border-border bg-surface px-2 text-sm"
    >
      <option value="demo">デモ 350 人</option>
      <option value="demo-large">デモ 1 万人</option>
      <option value="api">jin-ji API</option>
    </select>
  );
}

function Settings() {
  return (
    <Popover
      label="設定"
      side="right"
      width={260}
      trigger={
        <>
          <SettingsIcon />
          <TabLabel>設定</TabLabel>
        </>
      }
      tooltip={<RailTooltip>設定</RailTooltip>}
      triggerClassName={`${ITEM} text-muted hover:bg-surface-2 hover:text-fg`}
    >
      <div className="flex flex-col gap-4 p-4">
        <p className="m-0 text-sm font-bold">設定</p>
        <div className="flex flex-col gap-1.5">
          <span className="text-xs font-bold text-muted">データ</span>
          <SourceSwitch />
        </div>
        <div className="flex flex-col gap-1.5">
          <span className="text-xs font-bold text-muted">配色</span>
          <ThemeSwitch size="md" />
        </div>
      </div>
    </Popover>
  );
}

/** デモ（架空のデータ）であることの小さな札。押すと説明を出す。 */
export function DemoBadge({ className = "" }: { className?: string }) {
  const { adapter } = useAdapter();
  const formatDate = useFormatDate();
  if (adapter.kind !== "demo") return null;
  return (
    <span className={className}>
      <HelpTip label="デモ表示について" trigger={<span className="rounded-sm border border-warn-fg/30 bg-warn-bg px-1.5 text-[11px] leading-[18px] font-bold text-warn-fg">デモ</span>}>
        <p className="m-0 font-bold">架空のサンプルデータです</p>
        <p className="m-0">「今日」は {formatDate(adapter.businessDate)} として表示します。</p>
        <p className="m-0 text-muted">閲覧者は一般社員を想定しているため、社用携帯と一部組織の内線・拠点・リモートワークは「非公開」になります。</p>
      </HelpTip>
    </span>
  );
}

export function NavRail() {
  const { view } = useNav();
  const { route } = useRoute();
  const corrections = useCorrectionsNav();
  return (
    <div data-popover-edge className="order-last z-10 flex shrink-0 items-stretch border-t border-border bg-surface pb-[env(safe-area-inset-bottom)] md:order-none md:z-30 md:w-[68px] md:flex-col md:items-center md:gap-2 md:border-t-0 md:border-r md:py-3 md:pb-3">
      {/* 名前（トップへのリンク）。スマホでは上の帯の画面名で足りるので出さない */}
      <a
        href={formatHash({ view: "org-chart", params: sourceOnly(route.params) })}
        aria-label="jin-ji 人事情報（トップへ）"
        className="group relative hidden size-10 items-center justify-center rounded-lg bg-accent text-sm font-bold tracking-tight text-accent-fg no-underline md:flex"
      >
        <span aria-hidden="true" className="text-base leading-none">
          J<sup className="ml-px align-super text-[10px]">2</sup>
        </span>
        <RailTooltip>jin-ji 人事情報</RailTooltip>
      </a>
      <DemoBadge className="hidden md:block" />
      <nav aria-label="画面" className="flex min-w-0 flex-[4] md:mt-3 md:flex-none">
        <ul className="m-0 flex w-full list-none gap-0.5 p-1 md:flex-col md:gap-2 md:p-0">
          {(Object.keys(VIEW_LABEL) as View[]).map((v) => {
            const current = view === v;
            const label = (v === "corrections" ? corrections.label : undefined) ?? VIEW_LABEL[v];
            return (
              <li key={v} className="flex flex-1 md:flex-none">
                <a
                  href={formatHash({ view: v, params: paramsFor(v, route.params) })}
                  aria-current={current ? "page" : undefined}
                  aria-label={label}
                  className={`group ${ITEM} ${current ? "bg-accent-weak text-accent" : "text-muted hover:bg-surface-2 hover:text-fg"}`}
                >
                  {/* 現在地の印（md 以上は左端の縦線、スマホは上端の横線） */}
                  {current && (
                    <span aria-hidden="true" className="absolute top-0 left-1/2 h-[3px] w-6 -translate-x-1/2 rounded-b-full bg-accent md:top-1/2 md:-left-3 md:h-6 md:w-[3px] md:translate-x-0 md:-translate-y-1/2 md:rounded-r-full md:rounded-bl-none" />
                  )}
                  {VIEW_ICON[v]}
                  <TabLabel>{VIEW_LABEL[v]}</TabLabel>
                  {v === "corrections" && corrections.badge}
                  <RailTooltip>{VIEW_LABEL[v]}</RailTooltip>
                </a>
              </li>
            );
          })}
        </ul>
      </nav>
      <div className="flex flex-[2] gap-0.5 p-1 md:mt-auto md:flex-none md:flex-col md:items-center md:gap-2 md:p-0">
        <Settings />
        <UserMenu
          side="right"
          triggerClassName={`${ITEM} text-muted hover:bg-surface-2 hover:text-fg`}
          renderTrigger={(name) => (
            <>
              <span aria-hidden="true" className="flex size-[26px] items-center justify-center rounded-full border border-border bg-surface-2 text-xs font-bold text-fg">
                {name ? [...name][0] : "?"}
              </span>
              <TabLabel>アカウント</TabLabel>
              <RailTooltip>{name ?? "アカウント"}</RailTooltip>
            </>
          )}
        />
      </div>
    </div>
  );
}
