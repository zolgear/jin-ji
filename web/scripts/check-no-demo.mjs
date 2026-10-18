// 本番ビルドの成果物にデモアダプタ・デモデータが含まれていないことを確かめる。
//   node scripts/check-no-demo.mjs <dist>
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";

// デモデータとデモアダプタにだけ現れる文字列
const MARKERS = [
  "jin-ji UI のデモ用データ",
  "kazuya.sugiyama@example.com",
  "デモアダプタ（架空データ）",
  "株式会社サンプル商事",
  // デモの認証（demoAuthAdapter）
  "社内ナレッジエージェント",
];

function* files(dir) {
  for (const name of readdirSync(dir)) {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) yield* files(path);
    else if (/\.(js|html|css|json|map)$/.test(name)) yield path;
  }
}

const dist = process.argv[2] ?? "dist";
const found = [];
for (const path of files(dist)) {
  const text = readFileSync(path, "utf8");
  for (const marker of MARKERS) {
    if (text.includes(marker)) found.push(`${path}: ${marker}`);
  }
}
if (found.length > 0) {
  console.error("本番ビルドにデモ用のコード・データが含まれています:");
  for (const f of found) console.error(`  ${f}`);
  process.exit(1);
}
console.log(`${dist} にデモ用のコード・データは含まれていません。`);
