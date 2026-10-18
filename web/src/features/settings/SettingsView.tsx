// 設定の画面（#/settings/<項目>）。左に目次（ユーザー設定・管理者設定・jin-ji について）、右に選んだ項目の中身を出す。
// スマホ幅では目次と中身を 1 枚ずつ出す（#/settings は目次、#/settings/<項目> は中身と「← 戻る」）。
// 表示の既定は利用者ごとに localStorage に覚える（lib/settings.ts・lib/theme.ts・lib/dateStyle.ts）。管理の機能は /admin にあり、ここは入口だけ。
import { useEffect, useState, type ReactNode } from "react";
import { previousAppHash } from "../../app/nav";
import { withSource } from "../../auth/authAdapter";
import { ADMIN_TAB_LABELS, type AdminTab } from "../../auth/labels";
import { useSession } from "../../auth/session";
import { canUseAdmin, isHrAdmin } from "../../auth/types";
import { HelpTip } from "../../components/popover";
import { ErrorNote, Spinner } from "../../components/ui";
import { DEMO_AVAILABLE, useAdapter, type AdapterChoice } from "../../data/adapterContext";
import type { DateStyle } from "../../lib/date";
import { setDateStyle, useDateStyle } from "../../lib/dateStyle";
import { formatHash, parseHash, useRoute } from "../../lib/router";
import { setSetting, useSettings, type ChartDirectionSetting } from "../../lib/settings";
import { useThemeChoice, type ThemeChoice } from "../../lib/theme";

const REPOSITORY_URL = "https://github.com/zolgear/jin-ji";
const LICENSE_URL = `${REPOSITORY_URL}/blob/main/LICENSE`;

type Section = "display" | "data" | "admin" | "about" | "licenses";

const SECTION_LABEL: Record<Section, string> = {
  display: "表示",
  data: "データ",
  admin: "認証の管理",
  about: "jin-ji について",
  licenses: "同梱しているライブラリのライセンス",
};

/** 目次に並べる項目（使えるものだけ）。ライブラリのライセンスは「jin-ji について」の下の階層なので目次には出さない。 */
function useSections(): { user: Section[]; admin: Section[] } {
  const session = useSession();
  return {
    // データの切り替えはデモ版のビルドだけ（本番のビルドは API だけ）
    user: DEMO_AVAILABLE ? ["display", "data"] : ["display"],
    admin: canUseAdmin(session?.session ?? null) ? ["admin"] : [],
  };
}

/** 取得元（source）だけを残した URL の引数。 */
function sourceParams(params: URLSearchParams): URLSearchParams {
  const source = params.get("source");
  return new URLSearchParams(source ? { source } : {});
}

// ---- 部品 ----

/** 押して選ぶ切り替え（タイムマシンの西暦 / 和暦と同じ形）。 */
function Segmented<T extends string>({ label, value, options, onChange }: { label: string; value: T; options: Array<[T, string]>; onChange: (v: T) => void }) {
  return (
    <fieldset className="m-0 flex shrink-0 overflow-hidden rounded-md border border-border p-0 text-sm">
      <legend className="sr-only">{label}</legend>
      {options.map(([v, text]) => (
        <button
          key={v}
          type="button"
          aria-pressed={value === v}
          onClick={() => onChange(v)}
          className={`h-8 px-3 ${value === v ? "bg-accent font-bold text-accent-fg" : "hover:bg-surface-2"}`}
        >
          {text}
        </button>
      ))}
    </fieldset>
  );
}

/** オン・オフのスイッチ。 */
function Switch({ label, checked, onChange }: { label: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex h-6 w-11 shrink-0 items-center rounded-full border transition-colors ${checked ? "border-accent bg-accent" : "border-border bg-surface-2"}`}
    >
      <span aria-hidden="true" className={`absolute top-1/2 size-[18px] -translate-y-1/2 rounded-full shadow-sm transition-[left] ${checked ? "left-[22px] bg-accent-fg" : "left-[2px] bg-muted"}`} />
    </button>
  );
}

/** 設定の 1 行（左に名前と補足の i、右に操作）。スマホ幅では縦に積む（inline はスイッチなど小さな操作で、スマホ幅でも横に並べる）。 */
function Row({ label, help, inline = false, children }: { label: string; help?: ReactNode; inline?: boolean; children: ReactNode }) {
  return (
    <div className={`flex gap-2 px-4 py-3.5 sm:flex-row sm:items-center sm:justify-between sm:gap-6 ${inline ? "flex-row items-center justify-between" : "flex-col items-start"}`}>
      <span className="flex items-center gap-1 text-sm font-bold">
        {label}
        {help && (
          <HelpTip label={`${label}について`} icon="info">
            {help}
          </HelpTip>
        )}
      </span>
      {children}
    </div>
  );
}

/** 行をまとめる枠。 */
function Card({ title, children }: { title?: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-2">
      {title && <h3 className="m-0 text-xs font-bold text-muted">{title}</h3>}
      <div className="divide-y divide-border rounded-lg border border-border bg-surface">{children}</div>
    </section>
  );
}

function ExternalIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-3.5 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round">
      <path d="M9.5 2.5h4v4M13.5 2.5 7.5 8.5M12 9.5v3a1 1 0 0 1-1 1H3.5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h3" />
    </svg>
  );
}

function ChevronIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4 shrink-0 text-muted" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
      <path d="m6 3.5 4.5 4.5L6 12.5" />
    </svg>
  );
}

/** 外のサイトへのリンク（新しいタブで開く）。 */
function ExternalLink({ href, children }: { href: string; children: ReactNode }) {
  return (
    <a href={href} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1 text-accent">
      {children}
      <ExternalIcon />
      <span className="sr-only">（新しいタブで開く）</span>
    </a>
  );
}

// ---- 項目の中身 ----

function DisplaySection() {
  const { choice, setChoice } = useThemeChoice();
  const dateStyle = useDateStyle();
  const settings = useSettings();
  return (
    <div className="flex flex-col gap-6">
      <Card title="画面">
        <Row label="配色">
          <Segmented<ThemeChoice>
            label="配色"
            value={choice}
            onChange={setChoice}
            options={[
              ["system", "自動"],
              ["light", "ライト"],
              ["dark", "ダーク"],
            ]}
          />
        </Row>
        <Row label="日付の表示" help={<p className="m-0">タイムマシンの「西暦 / 和暦」と同じ設定です。</p>}>
          <Segmented<DateStyle>
            label="日付の表示"
            value={dateStyle}
            onChange={setDateStyle}
            options={[
              ["seireki", "西暦"],
              ["wareki", "和暦"],
            ]}
          />
        </Row>
      </Card>
      <Card title="図を開いたときの既定">
        <Row label="組織図の向き" help={<p className="m-0">組織図を開いたときの向きです。向きを指定したリンクから開いたときは、リンクの向きで表示します。</p>}>
          <Segmented<ChartDirectionSetting>
            label="組織図の向き"
            value={settings.chartDirection}
            onChange={(v) => setSetting("chartDirection", v)}
            options={[
              ["tb", "上から下"],
              ["lr", "左から右"],
            ]}
          />
        </Row>
        <Row
          label="建物・フロアを組織で色分け"
          inline
          help={<p className="m-0">3D 表示の建物・フロアモードで、席を組織（部）ごとの色で塗ります。色分けを指定したリンクから開いたときは、リンクの指定で表示します。</p>}
        >
          <Switch label="建物・フロアを組織で色分け" checked={settings.floorOrgColor} onChange={(v) => setSetting("floorOrgColor", v)} />
        </Row>
      </Card>
    </div>
  );
}

/** データの取得元（デモ 350 人・デモ 1 万人・API）。デモ版のビルドだけで出す。 */
function DataSection() {
  const { choice, setChoice } = useAdapter();
  return (
    <Card>
      <Row label="データの取得元" help={<p className="m-0">デモは架空のサンプルデータです。切り替えると、選んでいる組織・人と日付は元に戻ります。</p>}>
        <select
          aria-label="データ"
          value={choice}
          onChange={(e) => {
            const next = e.target.value as AdapterChoice;
            // 取得元を変えたら選択と日付を戻す（設定の項目は残す）
            const current = parseHash(window.location.hash);
            window.history.replaceState(null, "", formatHash({ view: current.view, sub: current.sub, params: new URLSearchParams({ source: next }) }));
            window.dispatchEvent(new HashChangeEvent("hashchange"));
            setChoice(next);
          }}
          className="h-8 rounded-md border border-border bg-surface px-2 text-sm max-sm:w-full sm:w-48"
        >
          <option value="demo">デモ 350 人</option>
          <option value="demo-large">デモ 1 万人</option>
          <option value="api">jin-ji API</option>
        </select>
      </Row>
    </Card>
  );
}

/** 認証の管理（/admin）への入口。人事管理者はすべてのタブ、緊急用アカウントは認証の復旧のタブだけ（AdminPage と同じ）。 */
function AdminSection() {
  const state = useSession();
  if (!state || !canUseAdmin(state.session)) return null;
  const tabs: AdminTab[] = isHrAdmin(state.session) ? ["unbound", "accounts", "clients", "webhooks", "domains"] : ["unbound", "accounts"];
  return (
    <Card>
      {tabs.map((t) => (
        <a key={t} href={withSource(`/admin?tab=${t}`, state.source)} className="flex items-center justify-between gap-4 px-4 py-3.5 text-sm font-bold text-fg no-underline hover:bg-surface-2">
          {ADMIN_TAB_LABELS[t]}
          <ChevronIcon />
        </a>
      ))}
    </Card>
  );
}

function AboutSection({ licensesHref }: { licensesHref: string }) {
  return (
    <div className="flex flex-col gap-6">
      <Card>
        <div className="flex items-center gap-3 px-4 py-4">
          <span aria-hidden="true" className="flex size-10 shrink-0 items-center justify-center rounded-lg bg-accent text-base leading-none font-bold text-accent-fg">
            J<sup className="ml-px align-super text-[10px]">2</sup>
          </span>
          <span className="flex flex-col">
            <span className="text-base font-bold">jin-ji</span>
            <span className="text-xs text-muted">人事情報</span>
          </span>
        </div>
        <dl className="m-0 divide-y divide-border text-sm">
          <div className="px-4 py-3">
            <dt className="sr-only">著作権</dt>
            <dd className="m-0">Copyright 2026 Toru Suzuki</dd>
          </div>
          <div className="flex gap-2 px-4 py-3">
            <dt className="text-muted">バージョン:</dt>
            <dd className="m-0 tabular">{__JINJI_VERSION__}</dd>
          </div>
          <div className="flex gap-2 px-4 py-3">
            <dt className="text-muted">ライセンス:</dt>
            <dd className="m-0">
              <ExternalLink href={LICENSE_URL}>Apache License 2.0</ExternalLink>
            </dd>
          </div>
          <div className="px-4 py-3">
            <dt className="sr-only">ソースコード</dt>
            <dd className="m-0">
              <ExternalLink href={REPOSITORY_URL}>GitHub リポジトリ</ExternalLink>
            </dd>
          </div>
        </dl>
        <a href={licensesHref} className="flex items-center justify-between gap-4 px-4 py-3.5 text-sm text-fg no-underline hover:bg-surface-2">
          同梱しているライブラリのライセンス
          <ChevronIcon />
        </a>
      </Card>
    </div>
  );
}

// ---- 同梱しているライブラリのライセンス（ビルド時に scripts/licenses.mjs が作る licenses.json） ----

interface LicensePackage {
  name: string;
  version: string;
  license: string;
  copyright: string[];
  repository: string | null;
  text: string | null;
  textGenerated: boolean;
}

interface LicenseExtra {
  name: string;
  license: string;
  note: string;
}

interface LicenseDocument {
  packages: LicensePackage[];
  extras: LicenseExtra[];
}

const LICENSES_URL = `${import.meta.env.BASE_URL}licenses.json`;

function useLicenses() {
  const [state, setState] = useState<{ data: LicenseDocument | null; error: string | null }>({ data: null, error: null });
  useEffect(() => {
    let cancelled = false;
    fetch(LICENSES_URL)
      .then((r) => {
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        return r.json() as Promise<LicenseDocument>;
      })
      .then(
        (data) => !cancelled && setState({ data, error: null }),
        (e: unknown) => !cancelled && setState({ data: null, error: String(e) }),
      );
    return () => {
      cancelled = true;
    };
  }, []);
  return state;
}

function LicenseBadge({ children }: { children: ReactNode }) {
  return <span className="shrink-0 rounded-sm border border-border px-1.5 text-xs leading-5 text-muted">{children}</span>;
}

function LicensesSection() {
  const { data, error } = useLicenses();
  if (error) return <ErrorNote>ライセンスの一覧を読み込めませんでした（{error}）</ErrorNote>;
  if (!data) return <Spinner />;
  return (
    <div className="flex flex-col gap-6">
      <Card title={`ライブラリ（${data.packages.length} 件）`}>
        {data.packages.map((p) => (
          <details key={`${p.name}@${p.version}`} className="group">
            <summary className="flex cursor-pointer list-none items-center gap-3 px-4 py-3 text-sm hover:bg-surface-2 [&::-webkit-details-marker]:hidden">
              <span className="min-w-0 flex-1 truncate">
                <span className="font-bold">{p.name}</span> <span className="text-muted tabular">{p.version}</span>
              </span>
              <LicenseBadge>{p.license}</LicenseBadge>
              <span className="transition-transform group-open:rotate-90">
                <ChevronIcon />
              </span>
            </summary>
            <div className="flex flex-col gap-2 px-4 pb-4 text-[13px]">
              {p.copyright.length > 0 && (
                <ul className="m-0 list-none p-0">
                  {p.copyright.map((c) => (
                    <li key={c}>{c}</li>
                  ))}
                </ul>
              )}
              {p.repository && /^https:\/\//.test(p.repository) && (
                <span>
                  <ExternalLink href={p.repository}>ソースコード</ExternalLink>
                </span>
              )}
              {p.textGenerated && <p className="m-0 text-muted">パッケージにライセンスの本文が含まれていないため、MIT License の条文に著作権表示を入れて載せています。</p>}
              {p.text ? (
                <pre className="m-0 max-h-80 overflow-auto rounded-md bg-surface-2 p-3 font-mono text-xs leading-relaxed whitespace-pre-wrap">{p.text}</pre>
              ) : (
                <p className="m-0 text-muted">ライセンスの本文はパッケージに含まれていません。</p>
              )}
            </div>
          </details>
        ))}
      </Card>
      {data.extras.length > 0 && (
        <Card title="ライブラリ以外の素材">
          {data.extras.map((x) => (
            <div key={x.name} className="flex flex-col gap-1 px-4 py-3 text-sm">
              <span className="flex items-center gap-3">
                <span className="flex-1 font-bold">{x.name}</span>
                <LicenseBadge>{x.license}</LicenseBadge>
              </span>
              <span className="text-[13px] text-muted">{x.note}</span>
            </div>
          ))}
        </Card>
      )}
    </div>
  );
}

// ---- 画面 ----

function BackIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
      <path d="M13 8H3.5M7.5 3.5 3 8l4.5 4.5" />
    </svg>
  );
}

const BACK_LINK = "inline-flex items-center gap-1.5 self-start rounded-md px-2 py-1 -ml-2 text-sm text-muted no-underline hover:bg-surface-2 hover:text-fg";

export function SettingsView() {
  const { route } = useRoute();
  const sections = useSections();
  const available: Section[] = [...sections.user, ...sections.admin, "about", "licenses"];
  const requested = route.sub as Section | null | undefined;
  const chosen: Section | null = requested && available.includes(requested) ? requested : null;
  // md 以上では、項目を選んでいなければ「表示」を出す
  const section: Section = chosen ?? "display";
  const params = sourceParams(route.params);
  const href = (s: Section | null) => formatHash({ view: "settings", sub: s, params });
  const current = section === "licenses" ? "about" : section;

  const navItem = (s: Section) => (
    <li key={s}>
      <a
        href={href(s)}
        aria-current={current === s ? "page" : undefined}
        className={`flex items-center justify-between gap-2 rounded-md px-3 py-2 text-sm no-underline md:py-1.5 ${
          current === s ? "md:bg-accent-weak md:font-bold md:text-accent" : ""
        } text-fg hover:bg-surface-2`}
      >
        {SECTION_LABEL[s]}
        <span className="md:hidden">
          <ChevronIcon />
        </span>
      </a>
    </li>
  );
  const group = (title: string, items: Section[]) =>
    items.length > 0 && (
      <div className="flex flex-col gap-1">
        <h2 className="m-0 px-3 text-xs font-bold text-muted">{title}</h2>
        <ul className="m-0 flex list-none flex-col gap-0.5 p-0">{items.map(navItem)}</ul>
      </div>
    );

  // 「← 戻る」: スマホ幅では目次へ、md 以上では設定を開く前の画面へ（ライブラリのライセンスは「jin-ji について」へ）
  const appBack = previousAppHash() ?? formatHash({ view: "org-chart", params });
  const back =
    section === "licenses" ? (
      <a href={href("about")} className={BACK_LINK}>
        <BackIcon />
        戻る
      </a>
    ) : (
      <>
        <a href={href(null)} className={`${BACK_LINK} md:hidden`}>
          <BackIcon />
          戻る
        </a>
        <a href={appBack} className={`${BACK_LINK} max-md:hidden`}>
          <BackIcon />
          戻る
        </a>
      </>
    );

  return (
    <div className="flex size-full min-h-0">
      <nav aria-label="設定の項目" className={`shrink-0 flex-col gap-5 overflow-y-auto border-border bg-surface p-3 max-md:w-full md:flex md:w-60 md:border-r md:py-5 ${chosen ? "hidden" : "flex"}`}>
        {group("ユーザー設定", sections.user)}
        {group("管理者設定", sections.admin)}
        <ul className="m-0 list-none border-t border-border p-0 pt-3 md:mt-auto">{navItem("about")}</ul>
      </nav>
      <div className={`min-w-0 flex-1 overflow-y-auto ${chosen ? "block" : "max-md:hidden"}`}>
        <div className="flex max-w-3xl flex-col gap-4 px-4 py-5 md:px-8 md:py-6">
          {back}
          <h2 className="m-0 text-lg font-bold">{SECTION_LABEL[section]}</h2>
          {section === "display" && <DisplaySection />}
          {section === "data" && <DataSection />}
          {section === "admin" && <AdminSection />}
          {section === "about" && <AboutSection licensesHref={href("licenses")} />}
          {section === "licenses" && <LicensesSection />}
        </div>
      </div>
    </div>
  );
}
