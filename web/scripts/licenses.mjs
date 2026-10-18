// 同梱しているライブラリのライセンスの一覧（licenses.json）を作る。依存は足さず、node_modules の package.json と LICENSE を読む。
//
// - ビルド（vite build）では、バンドルに実際に入ったモジュール（各チャンクの moduleIds）から、それを含むパッケージを集める。
//   推移的な依存も、バンドルに入ったものだけが載る（型定義・ビルド用の道具は載らない）
// - 開発サーバーでは、package.json の dependencies から推移的にたどった一覧で代わりにする（バンドルを作らないため）
// - 成果物の licenses.json は設定画面の「同梱しているライブラリのライセンス」が読む（デモ版にも入る）
//
// vite.config.ts から licensesPlugin() を使う。純粋な部分（パスからパッケージの場所・著作権の行の取り出し）は licenses.test.mjs で確かめる。
import { existsSync, readdirSync, readFileSync, realpathSync } from "node:fs";
import { dirname, join, sep } from "node:path";

/** 出力するファイルの名前（配信のルートに置く）。 */
export const LICENSES_FILE = "licenses.json";

const LICENSE_FILE = /^(licen[cs]e|copying)(\.(md|txt|markdown))?$/i;
const NOTICE_FILE = /^notice(\.(md|txt))?$/i;

/**
 * モジュールの ID（絶対パス）から、それを含むパッケージの場所（node_modules/<名前> まで）。node_modules の外なら null。
 * pnpm の .pnpm/<名前@版>/node_modules/<名前>/... の形にも対応する（最後の node_modules を使う）。
 */
export function packageRootOf(id) {
  const path = id.replace(/\\/g, "/").replace(/[?#].*$/, "").replace(/^\0/, "");
  const marker = "/node_modules/";
  const at = path.lastIndexOf(marker);
  if (at < 0) return null;
  const rest = path.slice(at + marker.length).split("/");
  const name = rest[0]?.startsWith("@") ? `${rest[0]}/${rest[1] ?? ""}` : rest[0];
  if (!name || name.endsWith("/") || name === ".pnpm") return null;
  return path.slice(0, at + marker.length) + name;
}

/**
 * ビルドの道具が差し込む仮想モジュール（\0vite/preload-helper.js・\0rolldown/runtime.js など）の ID から、
 * そのコードの出どころのパッケージの名前。該当しなければ null。
 */
export function virtualPackageOf(id) {
  const m = /^\0(vite|rolldown)\//.exec(id);
  return m ? m[1] : null;
}

/** MIT のパッケージに本文が無いときの定型文（MIT License の条文。著作権の行は package.json の author から）。 */
export function mitText(copyright) {
  return [
    "MIT License",
    "",
    ...copyright,
    "",
    "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:",
    "",
    "The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.",
    "",
    "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.",
  ].join("\n");
}

/** ライセンス本文から著作権表示の行（Copyright ・(c)・©）を取り出す。重複は除く。 */
export function extractCopyright(text) {
  const lines = new Set();
  for (const raw of text.split(/\r?\n/)) {
    const line = raw.trim();
    // 条文の途中の「copyright statement(s)」「Copyright notice」などは表示ではないので除く（表示は大文字で始まる）
    if (/^(Copyright\b|COPYRIGHT\b|\([cC]\)|©)/.test(line) && !/^copyright\s+(notices?|holders?|owners?|statements?|law|license)\b/i.test(line) && !/\[yyyy\]|\[name of copyright owner\]/i.test(line)) {
      lines.add(line);
    }
  }
  return [...lines];
}

/** package.json の license（文字列・古い形の { type }・licenses の配列）を SPDX の式の文字列にする。 */
export function licenseOf(pkg) {
  if (typeof pkg.license === "string") return pkg.license;
  if (pkg.license && typeof pkg.license.type === "string") return pkg.license.type;
  if (Array.isArray(pkg.licenses)) return pkg.licenses.map((l) => (typeof l === "string" ? l : l.type)).filter(Boolean).join(" OR ");
  return "UNKNOWN";
}

function authorOf(pkg) {
  const a = pkg.author;
  if (!a) return null;
  return typeof a === "string" ? a : (a.name ?? null);
}

function readText(dir, pattern) {
  const name = readdirSync(dir).find((n) => pattern.test(n));
  return name ? readFileSync(join(dir, name), "utf8").trim() : null;
}

/** パッケージの場所から、一覧の 1 件（名前・版・ライセンス・著作権表示・本文）。 */
export function readPackage(root) {
  const pkg = JSON.parse(readFileSync(join(root, "package.json"), "utf8"));
  const text = readText(root, LICENSE_FILE);
  const notice = readText(root, NOTICE_FILE);
  const found = extractCopyright([text, notice].filter(Boolean).join("\n"));
  const author = authorOf(pkg);
  const copyright = found.length > 0 ? found : author ? [`Copyright (c) ${author}`] : [];
  const license = licenseOf(pkg);
  const body = [text, notice].filter(Boolean).join("\n\n") || null;
  // 本文を同梱していないパッケージ（MIT のもの）は定型文で補い、補ったことを印で残す（画面で断り書きを出す）
  const generated = !body && license === "MIT";
  return {
    name: pkg.name,
    version: pkg.version,
    license,
    copyright,
    repository: repositoryOf(pkg),
    text: generated ? mitText(copyright) : body,
    textGenerated: generated,
  };
}

function repositoryOf(pkg) {
  const r = typeof pkg.repository === "string" ? pkg.repository : pkg.repository?.url;
  if (!r) return pkg.homepage ?? null;
  if (/^github:/.test(r) || /^[\w.-]+\/[\w.-]+$/.test(r)) return `https://github.com/${r.replace(/^github:/, "")}`;
  return r.replace(/^git\+/, "").replace(/^git:\/\//, "https://").replace(/\.git$/, "");
}

/**
 * パッケージの名前で上へたどって探す（Node の解決と同じく、近い node_modules から）。
 * pnpm では依存は実体のパス（.pnpm/<名前@版>/node_modules/）の兄弟にある。
 */
function findPackageDir(fromDir, name) {
  let dir = fromDir;
  for (;;) {
    const candidate = join(dir, "node_modules", name);
    if (existsSync(join(candidate, "package.json"))) return realpathSync(candidate);
    const parent = dirname(dir);
    if (parent === dir) return null;
    dir = parent;
  }
}

/** dependencies を推移的にたどったパッケージの場所（開発サーバー用。ビルドではバンドルの中身から集める）。 */
export function dependencyRoots(projectDir) {
  const seen = new Set();
  const visit = (dir) => {
    const pkg = JSON.parse(readFileSync(join(dir, "package.json"), "utf8"));
    for (const name of Object.keys(pkg.dependencies ?? {})) {
      const found = findPackageDir(dir, name);
      if (!found || seen.has(found)) continue;
      seen.add(found);
      visit(found);
    }
  };
  visit(projectDir);
  return [...seen];
}

/** パッケージの場所の一覧から、名前・版の順に並べた一覧（同じ名前と版は 1 件）。 */
export function collectLicenses(roots) {
  const byKey = new Map();
  for (const root of roots) {
    if (!existsSync(join(root, "package.json"))) continue;
    const entry = readPackage(root);
    // パッケージの中の入れ子の package.json（"type" だけのものなど）は名前を持たないので、上の階層で数える
    if (!entry.name) continue;
    byKey.set(`${entry.name}@${entry.version}`, entry);
  }
  return [...byKey.values()].sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version));
}

/** バンドルのモジュールの ID の一覧から、パッケージの場所（重複なし）。仮想モジュールは projectDir から出どころのパッケージを探す。 */
export function rootsOfModules(ids, projectDir) {
  const roots = new Set();
  for (const id of ids) {
    const virtual = virtualPackageOf(id);
    if (virtual) {
      // rolldown は vite の依存なので、vite の場所からも探す
      const vite = findPackageDir(projectDir, "vite");
      const dir = findPackageDir(projectDir, virtual) ?? (vite ? findPackageDir(vite, virtual) : null);
      if (dir) roots.add(dir);
      continue;
    }
    const root = packageRootOf(id);
    if (root) roots.add(root.split("/").join(sep));
  }
  return [...roots];
}

/**
 * Vite のプラグイン。build では licenses.json を成果物に出し、開発サーバーでは同じ URL で返す。
 * extras はパッケージではない同梱物（3D のテクスチャなど）で、一覧の後ろに付ける。
 * @param {{ projectDir: string, extras?: Array<{ name: string, license: string, note: string }> }} options
 */
export function licensesPlugin({ projectDir, extras = [] }) {
  const document = (packages) => JSON.stringify({ packages, extras }, null, 2);
  return {
    name: "jinji-licenses",
    configureServer(server) {
      server.middlewares.use(`/${LICENSES_FILE}`, (_req, res) => {
        res.setHeader("Content-Type", "application/json; charset=utf-8");
        res.end(document(collectLicenses(dependencyRoots(projectDir))));
      });
    },
    generateBundle(_options, bundle) {
      const ids = [];
      for (const chunk of Object.values(bundle)) {
        if (chunk.type === "chunk") ids.push(...chunk.moduleIds);
      }
      // CSS（フォントの @font-face など）はチャンクの moduleIds に残らないことがあるので、モジュールのグラフからも拾う
      for (const id of this.getModuleIds()) {
        if (/\.css(\?|$)/.test(id)) ids.push(id);
      }
      this.emitFile({ type: "asset", fileName: LICENSES_FILE, source: document(collectLicenses(rootsOfModules(ids, projectDir))) });
    },
  };
}
