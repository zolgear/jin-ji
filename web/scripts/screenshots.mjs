// 各画面のスクリーンショットを撮る（ライト / ダーク × デスクトップ幅 / スマホ幅）。
// ホストにブラウザを入れず、公式イメージ mcr.microsoft.com/playwright のコンテナ内で実行する（README の「Web UI」）。
//
//   node scripts/screenshots.mjs <開発サーバーの URL> [出力ディレクトリ]
//
// 画面のコンソールのエラー（CSP 違反を含む）を集め、あれば終了コード 1 にする。
import { mkdirSync, readFileSync } from "node:fs";
import { chromium } from "playwright-core";

const base = process.argv[2] ?? "http://127.0.0.1:28000";
const outDir = process.argv[3] ?? "screenshots";
mkdirSync(outDir, { recursive: true });

// デモデータから、見せたい状態の人を選ぶ（一時停止中の人・内部監査部の人）
const demo = JSON.parse(readFileSync(new URL("../src/demo/demo-data.json", import.meta.url), "utf8"));
const today = demo.meta.business_date;
const audit = demo.org_units.find((o) => o.code === "A020");
const suspended = demo.people.find((p) => p.suspensions.some((s) => s.from <= today && s.until === null));
const auditMember = demo.people.find((p) => p.assignments.some((a) => a.org_unit_id === audit.id && a.valid_until === null && a.type === "primary"));
const sales = demo.org_units.find((o) => o.name === "営業本部");
// 修正依頼の起票フォームを開く人（拠点が見える人＝内部監査部でない人。山本の 1 人目）
const reportTarget = demo.people.find((p) => p.family_name === "山本" && p.termination_date === null && !p.assignments.some((a) => a.org_unit_id === audit.id));
// 拠点（検索の絞り込み・組織の詳細）と、人ごとの拠点を持つ人（主務の組織の拠点と違う人）
const siteId = (name) => demo.sites.find((s) => s.name === name).id;
const ownSitePerson = demo.people.find((p) => p.site_id && p.termination_date === null && p.remote_work && !p.assignments.some((a) => a.org_unit_id === audit.id));
const factoryOrg = demo.org_units.find((o) => o.parent_id === null && o.site_id === siteId("横浜工場"));
const reportHash = `#/search?q=${encodeURIComponent(reportTarget.family_name_kana)}&person=${reportTarget.id}`;

/** 人の詳細の「値の誤りを報告」を開いて入力する（送信はしない）。 */
async function fillReport(page, field, reason, suggested) {
  await page.click("button:has-text('値の誤りを報告')");
  await page.selectOption("select[id$='-field']", field);
  await page.fill("textarea", reason);
  if (suggested) await page.fill("input[id$='-suggested']", suggested);
  await page.locator("form button[type='submit']").scrollIntoViewIfNeeded();
}

const concurrentPerson = demo.people.find((p) => p.assignments.some((a) => a.type === "concurrent" && a.secondment === "none"));

const viewports = {
  desktop: { width: 1440, height: 900 },
  mobile: { width: 390, height: 844 },
};

const shots = [
  { name: "org-chart", hash: "#/org-chart", wait: ".react-flow__node" },
  { name: "org-chart-2026-03-31", hash: "#/org-chart?as_of=2026-03-31", wait: ".react-flow__node", only: "desktop" },
  { name: "org-chart-2026-04-01", hash: "#/org-chart?as_of=2026-04-01", wait: ".react-flow__node", only: "desktop", previous: "#/org-chart?as_of=2026-03-31" },
  // 組織図の向き（既定は上から下。左から右は dir=lr）と、すべて開いたとき（上から下では末端の組織を縦に積む）
  { name: "org-chart-left-to-right", hash: "#/org-chart?dir=lr", wait: ".react-flow__node" },
  { name: "org-chart-open-all", hash: "#/org-chart", wait: ".react-flow__node", only: "desktop", action: (page) => page.click("button:has-text('すべて開く')") },
  { name: "org-chart-left-to-right-open-all", hash: "#/org-chart?dir=lr", wait: ".react-flow__node", only: "desktop", action: (page) => page.click("button:has-text('すべて開く')") },
  { name: "org-chart-selected", hash: `#/org-chart?org=${sales.id}`, wait: ".react-flow__node" },
  { name: "org-chart-left-to-right-selected", hash: `#/org-chart?dir=lr&org=${sales.id}`, wait: ".react-flow__node", only: "desktop" },
  // 表での代替と、和暦の表示（暦の設定は localStorage）
  { name: "org-chart-table", hash: `#/org-chart?org=${sales.id}`, wait: ".react-flow__node", action: (page) => page.click("button:has-text('一覧表')") },
  { name: "org-chart-links-all", hash: `#/org-chart?org=${sales.id}`, wait: ".react-flow__node", only: "desktop", action: (page) => page.locator("select:has(option[value='focus'])").selectOption("all") },
  // タイムマシン（日付の切り替えの箱）を開いた状態
  { name: "org-chart-time-machine", hash: "#/org-chart?as_of=2026-04-01", wait: ".react-flow__node", action: async (page) => {
    await page.click("button[aria-label^='タイムマシン']");
    await page.waitForSelector("dialog[open]");
  } },
  { name: "org-chart-wareki", hash: "#/org-chart?as_of=2026-04-01", wait: ".react-flow__node", only: "desktop", storage: { "jinji.dateStyle": "wareki" } },
  { name: "overview-3d", hash: "#/overview-3d", wait: "canvas", settle: 3500 },
  { name: "overview-3d-selected", hash: `#/overview-3d?org=${sales.id}`, wait: "canvas", settle: 4500 },
  { name: "overview-3d-large", hash: "#/overview-3d?source=demo-large", wait: "canvas", settle: 6000, only: "desktop" },
  { name: "person-suspended", hash: `#/org-chart?person=${suspended.id}`, wait: "text=人の詳細" },
  { name: "person-private-fields", hash: `#/org-chart?person=${auditMember.id}`, wait: "text=人の詳細" },
  { name: "person-concurrent", hash: `#/search?q=${encodeURIComponent(concurrentPerson.family_name_kana)}&person=${concurrentPerson.id}`, wait: "text=人の詳細" },
  // 人の多角的な検索（名刺のカード・一覧・条件のシート・0 件・1 万人）
  { name: "search", hash: "#/search", wait: "article" },
  { name: "search-filtered", hash: `#/search?site=${siteId("横浜工場")}&kind=primary&hf=2016-10-02&sort=org`, wait: "article" },
  // 拠点ごと・リモートワークの有無ごとのグループ化（API の sort=site / remote_work と組み合わせる）
  { name: "search-group-site", hash: "#/search?group=site", wait: "article" },
  { name: "search-group-site-scrolled", hash: "#/search?group=site", wait: "article", only: "desktop", action: async (page) => {
    // 結果の列だけをスクロールする（区切りの見出しが上に残るのを見せる）
    await page.evaluate(() => {
      const heading = [...document.querySelectorAll("h3")].find((h) => h.textContent.includes("東京本社"));
      const scroller = heading.closest(".overflow-y-auto");
      scroller.scrollTop += heading.getBoundingClientRect().top - scroller.getBoundingClientRect().top + 600;
    });
  } },
  { name: "search-group-remote", hash: `#/search?group=remote_work&site=${siteId("東京本社")}`, wait: "article" },
  { name: "search-group-table", hash: "#/search?group=site&layout=table&remote=1", wait: "table", only: "desktop" },
  { name: "search-sheet-site", hash: "#/search?group=site", wait: "article", only: "mobile", action: async (page) => {
    await page.click("button:has-text('絞り込み')");
    await page.waitForSelector("dialog[open]");
    await page.locator("dialog summary:has-text('リモートワーク')").scrollIntoViewIfNeeded();
  } },
  { name: "person-own-site", hash: `#/search?q=${encodeURIComponent(ownSitePerson.family_name_kana)}&person=${ownSitePerson.id}`, wait: "text=人の詳細" },
  { name: "org-site", hash: `#/org-chart?org=${factoryOrg.id}`, wait: "text=この拠点の人を見る" },
  { name: "org-chart-sites", hash: "#/org-chart?color=site", wait: ".react-flow__node" },
  { name: "org-chart-sites-open", hash: "#/org-chart?color=site", wait: ".react-flow__node", only: "desktop", action: (page) => page.click("button:has-text('すべて開く')") },
  { name: "overview-3d-sites", hash: "#/overview-3d?color=site", wait: "canvas", settle: 3500 },
  { name: "overview-3d-sites-large", hash: "#/overview-3d?source=demo-large&color=site", wait: "canvas", settle: 6000, only: "desktop" },
  // 拠点で分ける（split=site。色分けと組み合わせられる）。1 万人のデモには上位が別の拠点にある組織がある（札の「上位」を押して上位を選ぶ）
  { name: "org-chart-split", hash: "#/org-chart?split=site", wait: ".react-flow__node" },
  { name: "org-chart-split-left-to-right", hash: "#/org-chart?split=site&dir=lr", wait: ".react-flow__node", only: "desktop" },
  { name: "org-chart-split-sites-open", hash: "#/org-chart?split=site&color=site", wait: ".react-flow__node", only: "desktop", action: (page) => page.click("button:has-text('すべて開く')") },
  { name: "org-chart-split-selected", hash: `#/org-chart?split=site&org=${sales.id}`, wait: ".react-flow__node" },
  { name: "org-chart-split-large-upper", hash: "#/org-chart?source=demo-large&split=site", wait: ".react-flow__node", only: "desktop", action: (page) => page.locator(".org-card:has-text('上位') button").first().click() },
  { name: "overview-3d-split", hash: "#/overview-3d?split=site", wait: "canvas", settle: 3500 },
  { name: "overview-3d-split-sites-large", hash: "#/overview-3d?source=demo-large&split=site&color=site", wait: "canvas", settle: 6000, only: "desktop" },
  { name: "overview-3d-help", hash: "#/overview-3d", wait: "canvas", settle: 3500, action: (page) => page.click("button[aria-label='図の見方']") },
  { name: "search-query", hash: `#/search?q=${encodeURIComponent("yamamoto")}&person=${reportTarget.id}`, wait: "text=人の詳細" },
  { name: "search-table", hash: `#/search?layout=table&jt=${encodeURIComponent("営業")}`, wait: "table" },
  { name: "search-sheet", hash: `#/search?jt=${encodeURIComponent("営業")}`, wait: "article", only: "mobile", action: async (page) => {
    await page.click("button:has-text('絞り込み')");
    await page.waitForSelector("dialog[open]");
  } },
  { name: "search-empty", hash: `#/search?q=${encodeURIComponent("該当なしの語")}`, wait: "text=条件に一致する人はいません", only: "desktop" },
  { name: "search-large", hash: "#/search?source=demo-large&group=site&remote=1", wait: "article", only: "desktop", settle: 2000 },
  // 修正依頼（デモの閲覧者は修正担当者・人事管理者。一般の社員の見え方は storage で切り替える）
  { name: "corrections", hash: "#/corrections", wait: "text=起票の古い順" },
  { name: "corrections-close", hash: "#/corrections", wait: "text=起票の古い順", only: "desktop", action: async (page) => {
    await page.click("button:has-text('対応済みにする') >> nth=0");
    await page.fill("textarea", "人事パッケージで修正しました。次の取込で反映されます。");
  } },
  // CSV のダウンロード（デモは Blob。配信時の CSP の下で落とせて、BOM 付きの UTF-8 であることを確かめる）
  { name: "corrections-all", hash: "#/corrections?status=all", wait: "text=起票の古い順", only: "desktop", action: async (page) => {
    const [download] = await Promise.all([page.waitForEvent("download"), page.click("button:has-text('CSV をダウンロード')")]);
    const text = readFileSync(await download.path(), "utf8");
    if (!/^jinji-corrections-all-\d{8}\.csv$/.test(download.suggestedFilename()) || !text.startsWith("\uFEFF状態,経過日数,")) {
      throw new Error(`CSV のダウンロードが想定と違います: ${download.suggestedFilename()}`);
    }
    console.log(`CSV: ${download.suggestedFilename()}（${text.split("\r\n").length - 2} 行）`);
  } },
  { name: "corrections-member", hash: "#/corrections", wait: "text=起票の古い順", storage: { "jinji.demo.viewer": "member" } },
  { name: "corrections-member-closed", hash: "#/corrections?status=closed", wait: "text=起票の古い順", only: "desktop", storage: { "jinji.demo.viewer": "member" } },
  { name: "person-report", hash: reportHash, wait: "text=人の詳細", action: (page) => fillReport(page, "site", "10 月から大阪支社に常駐しています。座席表も大阪支社です。", "大阪支社") },
  { name: "person-report-sent", hash: reportHash, wait: "text=人の詳細", only: "desktop", action: async (page) => {
    await fillReport(page, "site", "10 月から大阪支社に常駐しています。座席表も大阪支社です。", "大阪支社");
    await page.click("form button[type='submit']");
    await page.waitForSelector("text=修正依頼を起票しました");
  } },
  { name: "person-report-error", hash: reportHash, wait: "text=人の詳細", only: "mobile", action: async (page) => {
    await fillReport(page, "assignments", "自宅は東京都港区芝公園4-2-8 です");
    await page.click("form button[type='submit']");
    await page.waitForSelector("text=保持しない情報");
  } },
  // 認証まわりの画面（パスで開く。デモの認証の架空のデータ）
  { name: "user-menu", hash: "#/org-chart", wait: ".react-flow__node", action: (page) => page.click("button[aria-controls='user-menu']") },
  { name: "settings", hash: "#/org-chart", wait: ".react-flow__node", action: (page) => page.click("button[aria-label='設定']") },
  { name: "login", path: `/login?return_url=${encodeURIComponent("/#/search")}`, wait: "text=jin-ji のアカウント" },
  { name: "login-unbound", path: "/login?error=unbound", wait: "text=jin-ji のアカウント", only: "mobile" },
  {
    name: "login-two-factor",
    path: "/login",
    wait: "text=jin-ji のアカウント",
    action: async (page) => {
      await page.fill("input[autocomplete='username']", "demo");
      await page.fill("input[autocomplete='current-password']", "demo-password");
      await page.click("form button[type='submit']");
      await page.waitForSelector("text=確認コードの入力");
    },
  },
  { name: "consent-loopback", path: "/consent?request=demo", wait: "text=許可すると、このアプリができること" },
  { name: "consent-cimd", path: "/consent?request=cimd", wait: "text=許可すると、このアプリができること" },
  { name: "account", path: "/account", wait: "text=認証アプリ（確認コード）" },
  { name: "account-totp", path: "/account", wait: "text=認証アプリ（確認コード）", action: async (page) => {
    await page.click("text=設定を始める");
    await page.waitForSelector("text=セットアップキー（共有鍵）");
  } },
  { name: "recovery-password", path: "/account-recovery?purpose=password#token=demo", wait: "text=新しいパスワード（確認）" },
  { name: "admin-unbound", path: "/admin?tab=unbound", wait: "text=未解決" },
  { name: "admin-bind", path: "/admin?tab=unbound", wait: "text=未解決", only: "desktop", action: async (page) => {
    await page.click("button:has-text('紐付ける') >> nth=1");
    await page.waitForSelector("text=紐付ける人を選ぶ");
  } },
  { name: "admin-accounts", path: "/admin?tab=accounts", wait: "text=アカウント ID", only: "desktop" },
  { name: "admin-clients", path: "/admin?tab=clients", wait: "text=承認待ちの CIMD クライアント" },
  { name: "admin-clients-tools", path: "/admin?tab=clients", wait: "text=承認待ちの CIMD クライアント", only: "desktop", action: async (page) => {
    await page.click("dd button:has-text('変更') >> nth=0");
    await page.waitForSelector("text=MCP ツールの許可リスト");
  } },
  { name: "admin-domains", path: "/admin?tab=domains", wait: "text=ドメインを追加" },
  // 社内システムへの Webhook（dead の警告・停止中・新規登録。送信の一覧を開いた状態、登録の直後の鍵の表示）
  { name: "admin-webhooks", path: "/admin?tab=webhooks", wait: "text=Webhook を登録する" },
  { name: "admin-webhooks-deliveries", path: "/admin?tab=webhooks", wait: "text=Webhook を登録する", only: "desktop", action: async (page) => {
    await page.click("button:has-text('送信の一覧') >> nth=0");
    await page.waitForSelector("text=送信の一覧（新しい順");
    await page.click("summary:has-text('本文') >> nth=0");
    // 送信の一覧を画面の上に寄せる
    await page.evaluate(() => {
      const title = [...document.querySelectorAll("p")].find((p) => p.textContent.startsWith("送信の一覧（新しい順"));
      window.scrollBy(0, title.getBoundingClientRect().top - 16);
    });
  } },
  { name: "admin-webhooks-secret", path: "/admin?tab=webhooks", wait: "text=Webhook を登録する", only: "desktop", action: async (page) => {
    await page.fill("input[type='url']", "https://payroll.example.co.jp/hooks/jinji");
    await page.click("form button[type='submit']:has-text('登録')");
    await page.waitForSelector("text=この鍵はこの画面でだけ表示されます");
  } },
];

// JINJI_SHOTS（正規表現）で撮る画面を絞れる（例: JINJI_SHOTS='^(login|consent|account|admin|recovery|user)'）
const only = process.env.JINJI_SHOTS ? new RegExp(process.env.JINJI_SHOTS) : null;

const browser = await chromium.launch({
  args: ["--use-angle=swiftshader", "--enable-unsafe-swiftshader", "--ignore-gpu-blocklist"],
  chromiumSandbox: false,
});
const problems = [];
// CSP 違反（securitypolicyviolation）の件数。preview（API と同じ CSP ヘッダー）に向けて撮ると、配信時の CSP の下で動くかを確かめられる
let cspViolations = 0;
let cspHeader = null;
try {
  for (const theme of ["light", "dark"]) {
    for (const [device, viewport] of Object.entries(viewports)) {
      const context = await browser.newContext({
        viewport,
        deviceScaleFactor: 1,
        colorScheme: theme,
        reducedMotion: "reduce",
        locale: "ja-JP",
        timezoneId: "Asia/Tokyo",
        isMobile: device === "mobile",
        hasTouch: device === "mobile",
      });
      for (const shot of shots) {
        if (shot.only && shot.only !== device) continue;
        if (only && !only.test(shot.name)) continue;
        const page = await context.newPage();
        await page.addInitScript(() => {
          document.addEventListener("securitypolicyviolation", (e) => {
            console.error(`CSP 違反: ${e.violatedDirective} ${e.blockedURI}`);
          });
        });
        page.on("console", (m) => {
          if (m.type() === "error" && m.text().startsWith("CSP 違反")) cspViolations++;
        });
        page.on("response", (r) => {
          if (r.request().resourceType() === "document") cspHeader ??= r.headers()["content-security-policy"] ?? "";
        });
        if (shot.storage) {
          await page.addInitScript((entries) => {
            for (const [k, v] of Object.entries(entries)) localStorage.setItem(k, v);
          }, shot.storage);
        }
        page.on("console", (m) => {
          if (m.type() === "error") problems.push(`${shot.name}/${theme}/${device}: ${m.text()}`);
        });
        page.on("pageerror", (e) => problems.push(`${shot.name}/${theme}/${device}: ${e.message}`));
        if (shot.previous) {
          // 前の時点を開いてから切り替える（増減の表示を出すため）
          await page.goto(`${base}/${shot.previous}`);
          await page.waitForSelector(shot.wait, { timeout: 30000 });
          await page.waitForTimeout(800);
          await page.evaluate((h) => {
            window.location.hash = h;
          }, shot.hash);
        } else if (shot.path) {
          await page.goto(`${base}${shot.path}`);
        } else {
          await page.goto(`${base}/${shot.hash}`);
        }
        await page.waitForSelector(shot.wait, { timeout: 60000 });
        await page.waitForTimeout(shot.settle ?? 1200);
        if (shot.action) {
          await shot.action(page);
          await page.waitForTimeout(600);
        }
        const file = `${outDir}/${shot.name}-${theme}-${device}.png`;
        await page.screenshot({ path: file });
        // 設定を次の画面に持ち越さない
        if (shot.storage) await page.evaluate(() => localStorage.clear());
        console.log(file);
        await page.close();
      }
      await context.close();
    }
  }
} finally {
  await browser.close();
}

console.log(`CSP ヘッダー: ${cspHeader || "（なし。開発サーバー）"}`);
console.log(`CSP 違反: ${cspViolations} 件`);
if (problems.length > 0) {
  console.error("画面のエラー:");
  for (const p of [...new Set(problems)]) console.error(`  ${p}`);
  process.exit(1);
}
