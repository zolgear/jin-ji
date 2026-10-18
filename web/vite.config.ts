import { defineConfig, type Plugin } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { licensesPlugin } from "./scripts/licenses.mjs";

const projectDir = fileURLToPath(new URL(".", import.meta.url));
/** 画面の「jin-ji について」に出す版（web/package.json の version。リポジトリに他の版の定義は無い）。 */
const version: string = JSON.parse(readFileSync(new URL("./package.json", import.meta.url), "utf8")).version;

/** パッケージではない同梱物（同梱しているライブラリのライセンスの一覧の後ろに載せる）。 */
const LICENSE_EXTRAS = [
  {
    name: "3D 表示のテクスチャ",
    license: "Apache-2.0",
    note: "画像生成 AI で生成した jin-ji の自前の素材です（src/features/overview3d/textures/）。jin-ji と同じ Apache License 2.0 で配布します。",
  },
];

/**
 * 本番の SPA に付ける CSP（ADR-0001: connect-src 'self'、外部 CDN 不使用）。
 * 本来は配信側（ASP.NET Core / リバースプロキシ）の応答ヘッダーで付ける。meta でも付けておき、ヘッダーが無い配信でも外部へ出ないようにする。
 * frame-ancestors は meta では効かないので、配信側のヘッダーで付けること。
 * 開発サーバーは HMR のインラインスクリプトと WebSocket を使うため付けない。
 */
const CONTENT_SECURITY_POLICY = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self'",
  "img-src 'self' data: blob:",
  "font-src 'self'",
  "connect-src 'self'",
  "worker-src 'self' blob:",
  "object-src 'none'",
  "base-uri 'self'",
  "form-action 'self'",
].join("; ");

function contentSecurityPolicy(): Plugin {
  return {
    name: "jinji-csp",
    apply: "build",
    transformIndexHtml(html) {
      return html.replace(
        "<!-- CSP -->",
        `<meta http-equiv="Content-Security-Policy" content="${CONTENT_SECURITY_POLICY}" />`,
      );
    },
  };
}

/**
 * API（api の役割）が SPA を配信するときの応答ヘッダーの CSP（src/JinJi.Host/Web/WebUi.cs の ContentSecurityPolicy と同じ）。
 * `preview`（vite preview）でも同じヘッダーを付け、ヘッドレス撮影で CSP 違反が無いことを確かめる（scripts/screenshots.mjs）。
 * style-src に 'unsafe-inline' は無いので、<style> を差し込むコードを入れないこと（要素の style は CSSOM 経由なので対象外）。
 */
const SERVED_CONTENT_SECURITY_POLICY =
  "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; " +
  "worker-src 'self' blob:; manifest-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

/** @fontsource の CSS から woff（woff2 の代替）を外し、配布物に woff2 だけを含める。 */
function woff2Only(): Plugin {
  return {
    name: "jinji-woff2-only",
    enforce: "pre",
    transform(code, id) {
      if (!/@fontsource[\\/].+\.css$/.test(id)) {
        return null;
      }
      return code.replace(/,\s*url\([^)]+\.woff\)\s*format\('woff'\)/g, "");
    },
  };
}

export default defineConfig(({ mode, command }) => {
  // デモアダプタとデモデータは、mode=demo のビルドと開発サーバーにだけ含める。
  // 本番ビルド（mode=production）では __JINJI_DEMO__ が false の定数になり、動的 import ごと取り除かれる。
  const demo = mode === "demo" || (command === "serve" && mode !== "production");
  return {
    plugins: [woff2Only(), react(), tailwindcss(), contentSecurityPolicy(), licensesPlugin({ projectDir, extras: LICENSE_EXTRAS })],
    define: {
      __JINJI_DEMO__: JSON.stringify(demo),
      __JINJI_VERSION__: JSON.stringify(version),
    },
    build: {
      target: "es2023",
      sourcemap: false,
      chunkSizeWarningLimit: 1200,
      assetsInlineLimit: 0,
    },
    preview: {
      host: "0.0.0.0",
      strictPort: true,
      headers: { "Content-Security-Policy": SERVED_CONTENT_SECURITY_POLICY },
    },
    server: {
      // コンテナ内では 0.0.0.0 で待ち受け、公開は scripts/pnpm.sh が 127.0.0.1 だけに絞る
      host: "0.0.0.0",
      strictPort: true,
    },
    test: {
      environment: "node",
      include: ["src/**/*.test.ts", "scripts/**/*.test.mjs"],
    },
  };
});
