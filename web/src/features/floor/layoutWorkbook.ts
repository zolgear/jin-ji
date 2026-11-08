// 配置図の Excel 方眼紙（現場・総務が Excel でマスと線を埋めて直し、登録する形）。jin-ji の配置図の形式 v1（layoutDocument.ts）と行き来する。
// 1 階 1 シート（シート名は階の表示名）。1 マスは 600mm 四方で、1 行目と A 列は目盛り（m。A 列は行の下の辺）。
// 灰色に塗ったマス・何も無いマスは建物の外、罫線のある白いマスは床で、床のマスの輪郭を外形にする。
// セル結合した範囲（または名前だけを書いた 1 マス）が 1 つの枠で、書いた名前で種類を決める（classify）。
// 机の型と縦横は枠の大きさと形から決め、前後の向き・席の種類・設備は画面の編集で直す。エリアは Excel では扱わず今の配置図を引き継ぐ。
// 書き出したときと同じ枠（位置・大きさ・名前）と床は、今の位置・形・属性をそのまま引き継ぐ（往復しても配置図がずれない）。
import { columnName, type CellStyle, type CellValue, type MergeRange, type ReadSheet, type SheetData } from "../../lib/xlsx";
import type { LayoutDocument, LayoutDocumentFloor, LayoutDocumentSeat } from "../../data/layoutDocument";
import type { FixtureKind, LayoutSeat, PointMm } from "../../data/workplace";
import { deskSize, pointInPolygon } from "./floorModel";

export const CELL_MM = 600;
const RULER_EVERY = 5; // 3m ごと
const GUIDE_SHEET = "記入のしかた";
const BLANK = { cols: 50, rows: 30 };

type Layout = NonNullable<LayoutDocumentFloor["layout"]>;
type Fixture = NonNullable<Layout["fixtures"]>[number];

const FIXTURE_LABEL: Record<FixtureKind, string> = { core: "コア", room: "部屋", void: "吹き抜け" };
const PREFIX = /^(席|部屋|コア|吹き抜け)[\s:：]+(.+)$/su;
const CORE = /^(EV|ELV|WC|PS|EPS|DS)\d*$|エレベ|階段|トイレ|化粧室|便所|給湯|湯沸|コア|柱|シャフト/iu;
const VOID = /吹き抜|吹抜|ボイド|^void$/iu;
const SEAT_CODE = /^[A-Za-z0-9][A-Za-z0-9._-]*$/u;

const norm = (s: string | null | undefined) => (s ?? "").normalize("NFKC").trim();

type Classified = { kind: "seat"; code: string } | { kind: "fixture"; fixture: FixtureKind; name: string | null };

/** 枠の名前から種類を決める。先頭の「席」「部屋」「コア」「吹き抜け」で明示でき、無ければ英数字は席、EV・階段などはコア、吹き抜けは吹き抜け、ほかは部屋。 */
export function classify(text: string): Classified {
  const t = norm(text);
  const p = PREFIX.exec(t);
  if (p) {
    const rest = p[2]!.trim();
    if (p[1] === "席") return { kind: "seat", code: rest };
    const fixture = (Object.entries(FIXTURE_LABEL) as Array<[FixtureKind, string]>).find(([, label]) => label === p[1])![0];
    return { kind: "fixture", fixture, name: rest };
  }
  const fixture: FixtureKind | null = VOID.test(t) ? "void" : CORE.test(t) ? "core" : SEAT_CODE.test(t) ? null : "room";
  if (!fixture) return { kind: "seat", code: t };
  return { kind: "fixture", fixture, name: t === FIXTURE_LABEL[fixture] ? null : t };
}

const seatText = (code: string) => {
  const c = classify(code);
  return c.kind === "seat" && c.code === code ? code : `席 ${code}`;
};

const fixtureText = (f: Fixture) => {
  const text = f.name ?? FIXTURE_LABEL[f.kind];
  const c = classify(text);
  return c.kind === "fixture" && c.fixture === f.kind ? text : `${FIXTURE_LABEL[f.kind]} ${text}`;
};

/** 方眼の枠（方眼の行・列。0 は Excel の 2 行目・B 列。両端を含む）。 */
interface Frame {
  r1: number;
  c1: number;
  r2: number;
  c2: number;
  text: string;
  fixture?: Fixture;
  seat?: LayoutDocumentSeat;
}

/** 方眼（列 i は x0 + i×600 から右へ、行 j は yTop − j×600 から下へ 600mm）。 */
interface Grid {
  x0: number;
  yTop: number;
  cols: number;
  rows: number;
  /** 床のマス（"行,列"）。 */
  floor: Set<string>;
  frames: Frame[];
}

const key = (j: number, i: number) => `${j},${i}`;
const parseKey = (k: string) => k.split(",").map(Number) as [number, number];

/** 階の今の配置図を方眼にする（書き出しと、読んだ枠が今と同じかの突き合わせに使う）。 */
function gridOf(floor: LayoutDocumentFloor): Grid {
  const l = floor.layout;
  if (!l || l.outline.length === 0) {
    const all = new Set<string>();
    for (let j = 0; j < BLANK.rows; j++) for (let i = 0; i < BLANK.cols; i++) all.add(key(j, i));
    return { x0: 0, yTop: BLANK.rows * CELL_MM, cols: BLANK.cols, rows: BLANK.rows, floor: all, frames: [] };
  }
  const step = CELL_MM * RULER_EVERY;
  const xs = l.outline.map((p) => p[0]);
  const ys = l.outline.map((p) => p[1]);
  // 外形の周りに外のマスを 1 つ以上残し、原点を目盛りにそろえる
  const x0 = Math.floor((Math.min(...xs) - CELL_MM) / step) * step;
  const y0 = Math.floor((Math.min(...ys) - CELL_MM) / step) * step;
  const cols = Math.ceil((Math.max(...xs) + CELL_MM - x0) / CELL_MM);
  const rows = Math.ceil((Math.max(...ys) + CELL_MM - y0) / CELL_MM);
  const yTop = y0 + rows * CELL_MM;
  const grid: Grid = { x0, yTop, cols, rows, floor: new Set(), frames: [] };
  for (let j = 0; j < rows; j++) {
    for (let i = 0; i < cols; i++) if (pointInPolygon([x0 + (i + 0.5) * CELL_MM, yTop - (j + 0.5) * CELL_MM], l.outline)) grid.floor.add(key(j, i));
  }

  const used = new Set<string>();
  const free = (f: Pick<Frame, "r1" | "c1" | "r2" | "c2">) => {
    for (let j = f.r1; j <= f.r2; j++) for (let i = f.c1; i <= f.c2; i++) if (used.has(key(j, i))) return false;
    return true;
  };
  const clamp = (v: number, n: number) => Math.max(0, Math.min(n - 1, v));
  // 部屋などは辺をマスの境目に合わせ、机は中心と型の大きさ（マスの数）で置く（奥行き 700mm の机がどこでも 1 マスになる）
  const byEdges = (lo: number, hi: number, n: number): [number, number] => {
    let a = Math.round(lo);
    let b = Math.round(hi) - 1;
    if (b < a) a = b = Math.floor((lo + hi) / 2);
    return [clamp(a, n), clamp(b, n)];
  };
  const bySize = (center: number, size: number, n: number): [number, number] => {
    const count = Math.max(1, Math.round(size));
    const a = Math.round(center - count / 2);
    return [clamp(a, n), clamp(a + count - 1, n)];
  };
  const place = ([c1, c2]: [number, number], [r1, r2]: [number, number], frame: Omit<Frame, "r1" | "c1" | "r2" | "c2">) => {
    let at: Pick<Frame, "r1" | "c1" | "r2" | "c2"> | null = { r1, c1, r2, c2 };
    if (!free(at)) {
      // 重なるなら、真ん中にいちばん近い空いた 1 マスに置く
      at = null;
      const cj = Math.round((r1 + r2) / 2);
      const ci = Math.round((c1 + c2) / 2);
      for (let d = 0; !at && d < Math.max(rows, cols); d++) {
        for (let j = cj - d; !at && j <= cj + d; j++) {
          for (let i = ci - d; !at && i <= ci + d; i++) {
            if (j < 0 || i < 0 || j >= rows || i >= cols || Math.max(Math.abs(j - cj), Math.abs(i - ci)) !== d || used.has(key(j, i))) continue;
            at = { r1: j, c1: i, r2: j, c2: i };
          }
        }
      }
      if (!at) return;
    }
    for (let j = at.r1; j <= at.r2; j++) for (let i = at.c1; i <= at.c2; i++) used.add(key(j, i));
    grid.frames.push({ ...at, ...frame });
  };

  for (const f of l.fixtures ?? []) {
    const fx = f.polygon.map((p) => p[0]);
    const fy = f.polygon.map((p) => p[1]);
    place(byEdges((Math.min(...fx) - x0) / CELL_MM, (Math.max(...fx) - x0) / CELL_MM, cols), byEdges((yTop - Math.max(...fy)) / CELL_MM, (yTop - Math.min(...fy)) / CELL_MM, rows), {
      text: fixtureText(f),
      fixture: f,
    });
  }
  for (const s of l.seats ?? []) {
    const { w, d } = deskSize(s.desk_type ?? "desk_1200");
    const a = ((s.rotation_deg ?? 0) / 180) * Math.PI;
    const sx = Math.abs(w * Math.cos(a)) + Math.abs(d * Math.sin(a));
    const sy = Math.abs(w * Math.sin(a)) + Math.abs(d * Math.cos(a));
    place(bySize((s.x_mm - x0) / CELL_MM, sx / CELL_MM, cols), bySize((yTop - s.y_mm) / CELL_MM, sy / CELL_MM, rows), { text: seatText(s.code), seat: s });
  }
  return grid;
}

const sheetName = (label: string) => label.replace(/[[\]:*?/\\]/g, "_").slice(0, 31);

const FRAME_STYLE: Record<FixtureKind | "seat", CellStyle> = { seat: "boxBlue", room: "boxAmber", core: "boxGray", void: "boxGray" };

function floorSheet(floor: LayoutDocumentFloor): SheetData {
  const g = gridOf(floor);
  const rows: CellValue[][] = Array.from({ length: g.rows + 1 }, () => []);
  const styles: Array<Array<CellStyle | null>> = Array.from({ length: g.rows + 1 }, () => []);
  const step = CELL_MM * RULER_EVERY;
  for (let i = 0; i < g.cols; i++) {
    const x = g.x0 + i * CELL_MM;
    if (x % step === 0) {
      rows[0]![i + 1] = `${x / 1000}m`;
      styles[0]![i + 1] = "ruler";
    }
  }
  for (let j = 0; j < g.rows; j++) {
    const y = g.yTop - (j + 1) * CELL_MM;
    if (y % step === 0) {
      rows[j + 1]![0] = `${y / 1000}m`;
      styles[j + 1]![0] = "ruler";
    }
    for (let i = 0; i < g.cols; i++) styles[j + 1]![i + 1] = g.floor.has(key(j, i)) ? "grid" : "shade";
  }
  const merges: MergeRange[] = [];
  for (const f of g.frames) {
    const style = FRAME_STYLE[f.fixture ? f.fixture.kind : "seat"];
    for (let j = f.r1; j <= f.r2; j++) for (let i = f.c1; i <= f.c2; i++) styles[j + 1]![i + 1] = style;
    rows[f.r1 + 1]![f.c1 + 1] = f.text;
    if (f.r2 > f.r1 || f.c2 > f.c1) merges.push({ r1: f.r1 + 1, c1: f.c1 + 1, r2: f.r2 + 1, c2: f.c2 + 1 });
  }
  return { name: sheetName(floor.label), rows, styles, merges, widths: [5], defaultWidth: 2.7, rowHeight: 18, freeze: { rows: 1, cols: 1 } };
}

const GUIDE = [
  "1 マスは 600mm 四方です。1 行目と A 列の目盛りは m です。",
  "灰色のマスは建物の外、罫線のある白いマスは床です。床を広げるときは白いマスをコピーして貼り、外にするときは灰色に塗ります。床はひとつながりにします。",
  "席・部屋・コアなどは、マスを結合して罫線で囲み、名前を書きます。1 マスなら名前を書くだけです。",
  "名前が英数字（A01 など）なら席、EV・階段・トイレ・柱などはコア、吹き抜けは吹き抜け、それ以外（会議室A など）は部屋になります。「席 窓際1」「部屋 101」のように、先頭に種類を書いて決めることもできます。",
  "席の机は枠の大きさで決まります。横に 2 マスなら机 1200、3 マスなら机 1800、縦も 2 マス以上なら持ち場です。横長なら横向き、縦長なら縦向きです。前後の向き・設備などは画面で直します。",
  "席番号が今と同じ席は同じ席として、人の席の割り当てが続きます。書き出したときのままの枠は、位置・向き・設備も今のままです。枠を消した席は配置図から外れます。",
  "エリアは画面で編集します（Excel では今のものを引き継ぎます）。",
  "シートを足すと階が増えます（シート名が階の名前）。シートを消した階は今のままです。",
];

/** 建物の配置図（形式 v1。建物は 1 つ）を、1 階 1 シートの方眼紙と記入のしかたにする。 */
export function toLayoutSheets(document: LayoutDocument): SheetData[] {
  const floors = document.buildings[0]?.floors ?? [];
  return [
    ...(floors.length ? floors : [{ label: "1F" }]).map(floorSheet),
    { name: GUIDE_SHEET, rows: [["記入のしかた"], ...GUIDE.map((t) => [t])], styles: [["header"], ...GUIDE.map(() => ["wrap" as const])], widths: [100], freeze: { rows: 0, cols: 0 } },
  ];
}

/** 床のマスの外側の輪郭（方眼の頂点 [列, 行]。画面で時計回り）。床が離れていれば null。 */
function outlineOf(cells: Set<string>): Array<[number, number]> | null {
  const first = cells.values().next().value;
  if (first === undefined) return null;
  const seen = new Set([first]);
  const queue = [first];
  while (queue.length) {
    const [j, i] = parseKey(queue.pop()!);
    for (const n of [key(j - 1, i), key(j + 1, i), key(j, i - 1), key(j, i + 1)]) {
      if (cells.has(n) && !seen.has(n)) {
        seen.add(n);
        queue.push(n);
      }
    }
  }
  if (seen.size !== cells.size) return null;

  // 床を右手に見る向きの辺
  const out = new Map<string, Array<[number, number]>>();
  const edge = (a: [number, number], b: [number, number]) => {
    const k = `${a[0]},${a[1]}`;
    out.set(k, [...(out.get(k) ?? []), b]);
  };
  for (const c of cells) {
    const [j, i] = parseKey(c);
    if (!cells.has(key(j - 1, i))) edge([i, j], [i + 1, j]);
    if (!cells.has(key(j, i + 1))) edge([i + 1, j], [i + 1, j + 1]);
    if (!cells.has(key(j + 1, i))) edge([i + 1, j + 1], [i, j + 1]);
    if (!cells.has(key(j, i - 1))) edge([i, j + 1], [i, j]);
  }
  const loops: Array<Array<[number, number]>> = [];
  for (;;) {
    const start = [...out.entries()].find(([, v]) => v.length > 0);
    if (!start) break;
    const loop: Array<[number, number]> = [];
    let at = start[0].split(",").map(Number) as [number, number];
    let dir: [number, number] | null = null;
    for (;;) {
      const nexts = out.get(`${at[0]},${at[1]}`);
      if (!nexts?.length) break;
      // 斜めに接する角では、床の側へ曲がる辺を選ぶ
      let pick = 0;
      if (dir && nexts.length > 1) {
        const [dx, dy] = dir;
        const [ax, ay] = at;
        const turn = (b: [number, number]) => dx * (b[1] - ay) - dy * (b[0] - ax);
        pick = nexts.reduce((best, b, idx) => (turn(b) > turn(nexts[best]!) ? idx : best), 0);
      }
      const next = nexts.splice(pick, 1)[0]!;
      loop.push(at);
      dir = [next[0] - at[0], next[1] - at[1]];
      at = next;
    }
    loops.push(loop);
  }
  const area = (p: Array<[number, number]>) => Math.abs(p.reduce((s, [x, y], idx) => s + x * p[(idx + 1) % p.length]![1] - p[(idx + 1) % p.length]![0] * y, 0));
  const outer = loops.reduce((a, b) => (area(b) > area(a) ? b : a));
  // 一直線に並ぶ頂点を除く
  return outer.filter((p, idx) => {
    const prev = outer[(idx - 1 + outer.length) % outer.length]!;
    const next = outer[(idx + 1) % outer.length]!;
    return (p[0] - prev[0]) * (next[1] - p[1]) - (p[1] - prev[1]) * (next[0] - p[0]) !== 0;
  });
}

/** シートの目盛りから方眼の原点を読む（無ければ x は B 列を 0、y は床のいちばん下の行の下の辺を 0 とする）。 */
function originOf(sheet: ReadSheet, floor: Set<string>): { x0: number; yTop: number } {
  const meters = (v: string | null | undefined) => {
    const m = /^(-?\d+(?:\.\d+)?)\s*m?$/i.exec(norm(v));
    return m ? Number(m[1]) * 1000 : null;
  };
  let x0: number | null = null;
  const top = sheet.rows[0] ?? [];
  for (let c = 1; x0 === null && c < top.length; c++) {
    const x = meters(top[c]);
    if (x !== null) x0 = x - (c - 1) * CELL_MM;
  }
  let yTop: number | null = null;
  for (let r = 1; yTop === null && r < sheet.rows.length; r++) {
    const y = meters(sheet.rows[r]?.[0]);
    if (y !== null) yTop = y + r * CELL_MM;
  }
  if (yTop === null) yTop = (Math.max(-1, ...[...floor].map((k) => parseKey(k)[0])) + 1) * CELL_MM;
  return { x0: x0 ?? 0, yTop };
}

function levelOrderOf(label: string, others: number[]): number {
  const t = norm(label).toUpperCase();
  const above = /^(\d+)\s*(F|階)$/.exec(t);
  if (above) return Number(above[1]) * 10;
  const below = /^(?:B|地下)\s*(\d+)\s*(F|階)?$/.exec(t);
  if (below) return -Number(below[1]) * 10;
  return Math.max(0, ...others) + 10;
}

/**
 * 読んだ方眼紙を配置図（形式 v1）にする。current は書き出しに使った今の建物の配置図（建物は 1 つ）。
 * 読めない枠・床は problems に「シート名 の セル: 理由」で返す（1 件でもあれば登録しない）。
 */
export function fromLayoutSheets(sheets: ReadSheet[], current: LayoutDocument): { document: LayoutDocument | null; problems: string[] } {
  const building = current.buildings[0]!;
  const problems: string[] = [];
  const floors: LayoutDocumentFloor[] = [];
  const known = new Map(building.floors.map((f) => [norm(sheetName(f.label)), f]));
  const orders = building.floors.map((f) => f.level_order ?? 0);

  for (const sheet of sheets) {
    const name = norm(sheet.name);
    if (name === GUIDE_SHEET) continue;
    const before = known.get(name);
    const at = (r: number, c: number) => `${sheet.name} の ${columnName(c)}${r + 1}`;

    // 枠: 目盛り（1 行目・A 列）にかからないセル結合と、名前だけを書いた 1 マス
    const frames: Frame[] = [];
    const covered = new Set<string>();
    for (const m of sheet.merges) {
      if (m.r1 < 1 || m.c1 < 1) continue;
      let text = "";
      for (let r = m.r1; r <= m.r2; r++) {
        for (let c = m.c1; c <= m.c2; c++) {
          text ||= norm(sheet.rows[r]?.[c]);
          covered.add(key(r, c));
        }
      }
      frames.push({ r1: m.r1 - 1, c1: m.c1 - 1, r2: m.r2 - 1, c2: m.c2 - 1, text });
    }
    sheet.rows.forEach((row, r) =>
      row.forEach((v, c) => {
        if (r >= 1 && c >= 1 && norm(v) && !covered.has(key(r, c))) frames.push({ r1: r - 1, c1: c - 1, r2: r - 1, c2: c - 1, text: norm(v) });
      }),
    );

    // 床: 塗りの無い在るセルと、枠のマス
    const floor = new Set<string>();
    for (const k of sheet.present) {
      const [r, c] = parseKey(k);
      if (r >= 1 && c >= 1 && !sheet.shaded.has(k)) floor.add(key(r - 1, c - 1));
    }
    for (const f of frames) for (let j = f.r1; j <= f.r2; j++) for (let i = f.c1; i <= f.c2; i++) floor.add(key(j, i));
    if (floor.size === 0) {
      problems.push(`${sheet.name}: 床（罫線のある白いマス）がありません`);
      continue;
    }
    const { x0, yTop } = originOf(sheet, floor);
    const cellMm = (k: string, g: { x0: number; yTop: number }) => {
      const [j, i] = parseKey(k);
      return `${g.x0 + i * CELL_MM},${g.yTop - (j + 1) * CELL_MM}`;
    };
    const frameKey = (f: Frame, g: { x0: number; yTop: number }) =>
      `${g.x0 + f.c1 * CELL_MM},${g.yTop - (f.r2 + 1) * CELL_MM},${g.x0 + (f.c2 + 1) * CELL_MM},${g.yTop - f.r1 * CELL_MM}|${norm(f.text)}`;

    // 今の配置図を同じ方法で方眼にしたもの。同じ床・同じ枠は今の値を引き継ぐ
    const expected = gridOf(before ?? { label: name });
    const expectedFloor = new Set([...expected.floor].map((k) => cellMm(k, expected)));
    const sameFloor = floor.size === expectedFloor.size && [...floor].every((k) => expectedFloor.has(cellMm(k, { x0, yTop })));
    if (!before?.layout && sameFloor && frames.length === 0) continue; // 手を付けていない空の階
    const kept = new Map(expected.frames.map((f) => [frameKey(f, expected), f]));
    const beforeLayout = before?.layout;
    const beforeSeats = new Map((beforeLayout?.seats ?? []).map((s) => [s.code, s]));
    const beforeFrames = new Map(expected.frames.filter((f) => f.seat).map((f) => [f.seat!.code, f]));
    const zones = beforeLayout?.zones ?? [];

    let outline: PointMm[];
    if (sameFloor && beforeLayout) outline = beforeLayout.outline;
    else {
      const loop = outlineOf(floor);
      if (!loop) {
        problems.push(`${sheet.name}: 床（罫線のある白いマス）が離れています。ひとつながりにしてください`);
        continue;
      }
      outline = loop.map(([i, j]) => [x0 + i * CELL_MM, yTop - j * CELL_MM]);
    }

    const fixtures: Fixture[] = [];
    const seats: LayoutDocumentSeat[] = [];
    const codes = new Set<string>();
    for (const f of frames.sort((a, b) => a.r1 - b.r1 || a.c1 - b.c1)) {
      if (!f.text) {
        problems.push(`${at(f.r1 + 1, f.c1 + 1)}: 枠に名前がありません`);
        continue;
      }
      const c = classify(f.text);
      if (c.kind === "seat") {
        if (codes.has(c.code)) {
          problems.push(`${at(f.r1 + 1, f.c1 + 1)}: 席番号 ${c.code} が 2 つあります`);
          continue;
        }
        codes.add(c.code);
      }
      const same = kept.get(frameKey(f, { x0, yTop }));
      if (same?.fixture) {
        fixtures.push(same.fixture);
        continue;
      }
      if (same?.seat) {
        seats.push(same.seat);
        continue;
      }
      const xl = x0 + f.c1 * CELL_MM;
      const xr = x0 + (f.c2 + 1) * CELL_MM;
      const yt = yTop - f.r1 * CELL_MM;
      const yb = yTop - (f.r2 + 1) * CELL_MM;
      if (c.kind === "fixture") {
        fixtures.push({ kind: c.fixture, name: c.name, polygon: [[xl, yb], [xr, yb], [xr, yt], [xl, yt]] });
        continue;
      }
      const w = f.c2 - f.c1 + 1;
      const h = f.r2 - f.r1 + 1;
      const prev = beforeSeats.get(c.code);
      const prevFrame = beforeFrames.get(c.code);
      const sameSize = prevFrame !== undefined && prevFrame.c2 - prevFrame.c1 + 1 === w && prevFrame.r2 - prevFrame.r1 + 1 === h;
      const horizontal = w >= h;
      const desk: LayoutSeat["desk_type"] =
        sameSize && prev?.desk_type ? prev.desk_type : Math.min(w, h) >= 2 ? "station" : Math.max(w, h) >= 3 ? "desk_1800" : "desk_1200";
      const prevRotation = prev?.rotation_deg ?? 0;
      const rotation = prev && (prevRotation % 180 === 0) === horizontal ? prevRotation : horizontal ? 0 : 90;
      const center: PointMm = [(xl + xr) / 2, (yb + yt) / 2];
      const zone = zones.find((z) => pointInPolygon(center, z.polygon))?.name ?? (prev?.zone && zones.some((z) => z.name === prev.zone) ? prev.zone : null);
      const seat: LayoutDocumentSeat = {
        code: c.code,
        x_mm: center[0],
        y_mm: center[1],
        rotation_deg: rotation,
        desk_type: desk,
        kind: prev?.kind ?? (desk === "station" ? "station" : "fixed"),
        capacity: prev?.capacity ?? 1,
        features: prev?.features ?? [],
        zone,
      };
      if (prev?.out_of_service !== undefined) seat.out_of_service = prev.out_of_service;
      seats.push(seat);
    }

    // 引き継いだ要素は今の並びのまま、新しい要素は後ろに
    const order = <T,>(list: T[], prev: T[] = []) => list.sort((a, b) => (prev.indexOf(a) + 1 || Infinity) - (prev.indexOf(b) + 1 || Infinity));
    order(fixtures, beforeLayout?.fixtures);
    order(seats, beforeLayout?.seats);
    floors.push({
      label: before?.label ?? name,
      level_order: before?.level_order ?? levelOrderOf(name, orders),
      kind: before?.kind ?? "office",
      layout: { ...(beforeLayout?.grid_mm ? { grid_mm: beforeLayout.grid_mm } : {}), outline, fixtures, zones, seats },
    });
  }

  if (problems.length === 0 && floors.length === 0) problems.push("階のシートに、変えたところがありません。");
  if (problems.length > 0) return { document: null, problems };
  return { document: { ...current, buildings: [{ code: building.code, name: building.name, floors }] }, problems };
}
