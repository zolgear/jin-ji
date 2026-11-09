// 簡単な xlsx（Office Open XML のブック）の読み書き。依存を足さず（ADR-0001・ADR-0003: Excel のライブラリは API・画面に入れない）、
// ブラウザの標準機能（DecompressionStream）だけで、方眼紙のような表（文字と数・罫線と塗り・セル結合）を扱う。
// - 書く: シートごとの行、セルの見た目（決まった数種類）、セル結合、列の幅・行の高さ、固定する行と列。ZIP は無圧縮で書く
// - 読む: 共有文字列・インラインの文字・数・真偽を値として読み、あわせてセル結合と、セルが在るか・塗られているかを返す。
//   数式（キャッシュの値は読む）・日付の解釈・色の種類はしない
// 読むのは利用者が選んだファイルで、値は文字として返すだけ（式の評価や外部の参照はしない）。

export type CellValue = string | number | null;

/** セルの見た目。header は太字の見出し、grid は方眼（細い罫線）、shade は灰色の塗り、box* は枠（濃い罫線・中央寄せ・縮小して表示）、ruler は目盛り。 */
export type CellStyle = "header" | "grid" | "shade" | "box" | "boxBlue" | "boxAmber" | "boxGray" | "ruler" | "wrap";

/** セル結合の範囲（0 始まり。両端を含む）。 */
export interface MergeRange {
  r1: number;
  c1: number;
  r2: number;
  c2: number;
}

export interface SheetData {
  name: string;
  rows: CellValue[][];
  /** セルの見た目（rows と同じ並び）。見た目のあるセルは空でも書く。 */
  styles?: Array<Array<CellStyle | null | undefined>>;
  merges?: MergeRange[];
  /** 列の幅（文字数の目安）。widths に無い列は defaultWidth。 */
  widths?: number[];
  defaultWidth?: number;
  /** 行の高さ（ポイント）。 */
  rowHeight?: number;
  /** 固定する行と列の数（既定は 1 行目だけ）。 */
  freeze?: { rows: number; cols: number };
}

export interface ReadSheet {
  name: string;
  /** 値（文字。空のセルは null）。 */
  rows: Array<Array<string | null>>;
  merges: MergeRange[];
  /** 在るセル（値か見た目のあるセル）の "行,列"。 */
  present: Set<string>;
  /** 塗りのある（白以外の）セルの "行,列"。 */
  shaded: Set<string>;
}

const MAX_ROWS = 2000;
const MAX_COLS = 500;

// ---------------------------------------------------------------------------
// 書く
// ---------------------------------------------------------------------------

const enc = new TextEncoder();

function escapeXml(s: string): string {
  // XML 1.0 で使えない制御文字は除く
  // eslint-disable-next-line no-control-regex
  return s.replace(/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/g, "").replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

export function columnName(index: number): string {
  let n = index + 1;
  let s = "";
  while (n > 0) {
    const m = (n - 1) % 26;
    s = String.fromCharCode(65 + m) + s;
    n = Math.floor((n - 1) / 26);
  }
  return s;
}

// styles.xml の cellXfs の並び（0 は既定）
const STYLE_INDEX: Record<CellStyle, number> = { header: 1, grid: 2, shade: 3, box: 4, boxBlue: 5, boxAmber: 6, boxGray: 7, ruler: 8, wrap: 9 };
const STYLES_XML =
  '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">' +
  '<fonts count="4"><font><sz val="11"/><name val="Yu Gothic"/></font><font><b/><sz val="11"/><name val="Yu Gothic"/></font>' +
  '<font><sz val="9"/><name val="Yu Gothic"/></font><font><sz val="8"/><color rgb="FF808080"/><name val="Yu Gothic"/></font></fonts>' +
  '<fills count="7"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill>' +
  ["FFE8EEF5", "FFBFBFBF", "FFDDEBF7", "FFFFF2CC", "FFE7E6E6"].map((c) => `<fill><patternFill patternType="solid"><fgColor rgb="${c}"/></patternFill></fill>`).join("") +
  "</fills>" +
  '<borders count="3"><border/>' +
  ["FFD9D9D9", "FF404040"].map((c) => `<border>${["left", "right", "top", "bottom"].map((e) => `<${e} style="thin"><color rgb="${c}"/></${e}>`).join("")}<diagonal/></border>`).join("") +
  "</borders>" +
  '<cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="10"><xf/>' +
  '<xf fontId="1" fillId="2" applyFont="1" applyFill="1"/>' +
  '<xf borderId="1" applyBorder="1"/>' +
  '<xf fillId="3" borderId="1" applyFill="1" applyBorder="1"/>' +
  [0, 4, 5, 6]
    .map((fill) => `<xf fontId="2" fillId="${fill}" borderId="2" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1"><alignment horizontal="center" vertical="center" shrinkToFit="1"/></xf>`)
    .join("") +
  '<xf fontId="3" applyFont="1"/>' +
  '<xf applyAlignment="1"><alignment vertical="top" wrapText="1"/></xf>' +
  "</cellXfs></styleSheet>";

function sheetXml(sheet: SheetData): string {
  const width = Math.max(sheet.widths?.length ?? 0, ...sheet.rows.map((r) => r.length), ...(sheet.styles ?? []).map((r) => r.length));
  const colDefs: string[] = [];
  for (let i = 0; i < width; i++) {
    const w = sheet.widths?.[i] ?? sheet.defaultWidth;
    if (w !== undefined) colDefs.push(`<col min="${i + 1}" max="${i + 1}" width="${w}" customWidth="1"/>`);
  }
  const cols = colDefs.length ? `<cols>${colDefs.join("")}</cols>` : "";
  const height = sheet.rowHeight === undefined ? "" : ` ht="${sheet.rowHeight}" customHeight="1"`;
  const freeze = sheet.freeze ?? { rows: 1, cols: 0 };
  const rowCount = Math.max(sheet.rows.length, sheet.styles?.length ?? 0);
  const rows: string[] = [];
  for (let r = 0; r < rowCount; r++) {
    const values = sheet.rows[r] ?? [];
    const styles = sheet.styles?.[r] ?? [];
    const cells: string[] = [];
    for (let c = 0; c < Math.max(values.length, styles.length); c++) {
      const v = values[c] ?? null;
      const styleName = styles[c] ?? (r === 0 && !sheet.styles ? "header" : null);
      if ((v === null || v === "") && !styleName) continue;
      const ref = `${columnName(c)}${r + 1}`;
      const style = styleName ? ` s="${STYLE_INDEX[styleName]}"` : "";
      if (v === null || v === "") cells.push(`<c r="${ref}"${style}/>`);
      else if (typeof v === "number" && Number.isFinite(v)) cells.push(`<c r="${ref}"${style}><v>${v}</v></c>`);
      else cells.push(`<c r="${ref}"${style} t="inlineStr"><is><t xml:space="preserve">${escapeXml(String(v))}</t></is></c>`);
    }
    rows.push(`<row r="${r + 1}"${height}>${cells.join("")}</row>`);
  }
  const ref = (r: number, c: number) => `${columnName(c)}${r + 1}`;
  const merges = sheet.merges?.length ? `<mergeCells count="${sheet.merges.length}">${sheet.merges.map((m) => `<mergeCell ref="${ref(m.r1, m.c1)}:${ref(m.r2, m.c2)}"/>`).join("")}</mergeCells>` : "";
  const topLeft = ref(freeze.rows, freeze.cols);
  const pane =
    freeze.rows || freeze.cols
      ? `<pane${freeze.cols ? ` xSplit="${freeze.cols}"` : ""}${freeze.rows ? ` ySplit="${freeze.rows}"` : ""} topLeftCell="${topLeft}" activePane="${freeze.rows && freeze.cols ? "bottomRight" : freeze.rows ? "bottomLeft" : "topRight"}" state="frozen"/>`
      : "";
  return (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
    '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">' +
    `<sheetViews><sheetView workbookViewId="0">${pane}</sheetView></sheetViews>` +
    (sheet.rowHeight === undefined ? "" : `<sheetFormatPr defaultRowHeight="${sheet.rowHeight}" customHeight="1"/>`) +
    `${cols}<sheetData>${rows.join("")}</sheetData>${merges}</worksheet>`
  );
}

/** シートの並びから xlsx のバイト列を作る。 */
export function writeXlsx(sheets: SheetData[]): Uint8Array {
  const files: Array<[string, string]> = [
    [
      "[Content_Types].xml",
      '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">' +
        '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/>' +
        '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>' +
        '<Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>' +
        sheets.map((_, i) => `<Override PartName="/xl/worksheets/sheet${i + 1}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>`).join("") +
        "</Types>",
    ],
    [
      "_rels/.rels",
      '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' +
        '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>',
    ],
    [
      "xl/workbook.xml",
      '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>' +
        sheets.map((s, i) => `<sheet name="${escapeXml(s.name)}" sheetId="${i + 1}" r:id="rId${i + 1}"/>`).join("") +
        "</sheets></workbook>",
    ],
    [
      "xl/_rels/workbook.xml.rels",
      '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' +
        sheets.map((_, i) => `<Relationship Id="rId${i + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet${i + 1}.xml"/>`).join("") +
        `<Relationship Id="rId${sheets.length + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>`,
    ],
    ["xl/styles.xml", STYLES_XML],
    ...sheets.map((s, i): [string, string] => [`xl/worksheets/sheet${i + 1}.xml`, sheetXml(s)]),
  ];
  return zipStore(files.map(([name, text]) => [name, enc.encode(text)]));
}

let crcTable: Uint32Array | null = null;
function crc32(data: Uint8Array): number {
  if (!crcTable) {
    crcTable = new Uint32Array(256);
    for (let n = 0; n < 256; n++) {
      let c = n;
      for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
      crcTable[n] = c >>> 0;
    }
  }
  let crc = 0xffffffff;
  for (const b of data) crc = crcTable[(crc ^ b) & 0xff]! ^ (crc >>> 8);
  return (crc ^ 0xffffffff) >>> 0;
}

/** 無圧縮の ZIP（xlsx はこれで開ける）。 */
function zipStore(entries: Array<[string, Uint8Array]>): Uint8Array {
  const parts: Uint8Array[] = [];
  const central: Uint8Array[] = [];
  let offset = 0;
  for (const [name, data] of entries) {
    const nameBytes = enc.encode(name);
    const crc = crc32(data);
    const local = new Uint8Array(30 + nameBytes.length);
    const lv = new DataView(local.buffer);
    lv.setUint32(0, 0x04034b50, true);
    lv.setUint16(4, 20, true);
    lv.setUint16(6, 0x0800, true); // 名前は UTF-8
    lv.setUint32(14, crc, true);
    lv.setUint32(18, data.length, true);
    lv.setUint32(22, data.length, true);
    lv.setUint16(26, nameBytes.length, true);
    local.set(nameBytes, 30);
    const head = new Uint8Array(46 + nameBytes.length);
    const cv = new DataView(head.buffer);
    cv.setUint32(0, 0x02014b50, true);
    cv.setUint16(4, 20, true);
    cv.setUint16(6, 20, true);
    cv.setUint16(8, 0x0800, true);
    cv.setUint32(16, crc, true);
    cv.setUint32(20, data.length, true);
    cv.setUint32(24, data.length, true);
    cv.setUint16(28, nameBytes.length, true);
    cv.setUint32(42, offset, true);
    head.set(nameBytes, 46);
    parts.push(local, data);
    central.push(head);
    offset += local.length + data.length;
  }
  const centralSize = central.reduce((n, c) => n + c.length, 0);
  const end = new Uint8Array(22);
  const ev = new DataView(end.buffer);
  ev.setUint32(0, 0x06054b50, true);
  ev.setUint16(8, entries.length, true);
  ev.setUint16(10, entries.length, true);
  ev.setUint32(12, centralSize, true);
  ev.setUint32(16, offset, true);
  const all = [...parts, ...central, end];
  const out = new Uint8Array(all.reduce((n, p) => n + p.length, 0));
  let at = 0;
  for (const p of all) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}

// ---------------------------------------------------------------------------
// 読む
// ---------------------------------------------------------------------------

const dec = new TextDecoder();

async function inflateRaw(data: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([data as BlobPart]).stream().pipeThrough(new DecompressionStream("deflate-raw"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

/** ZIP の中のファイル（名前 → 中身）。圧縮は無圧縮と deflate だけ。 */
async function unzip(bytes: Uint8Array, maxTotal: number): Promise<Map<string, Uint8Array>> {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  let eocd = -1;
  for (let i = bytes.length - 22; i >= Math.max(0, bytes.length - 22 - 65535); i--) {
    if (view.getUint32(i, true) === 0x06054b50) {
      eocd = i;
      break;
    }
  }
  if (eocd < 0) throw new Error("Excel（xlsx）のファイルとして読めません。");
  const count = view.getUint16(eocd + 10, true);
  let at = view.getUint32(eocd + 16, true);
  const out = new Map<string, Uint8Array>();
  let total = 0;
  for (let i = 0; i < count; i++) {
    if (view.getUint32(at, true) !== 0x02014b50) throw new Error("Excel（xlsx）のファイルとして読めません。");
    const method = view.getUint16(at + 10, true);
    const compressed = view.getUint32(at + 20, true);
    const size = view.getUint32(at + 24, true);
    const nameLength = view.getUint16(at + 28, true);
    const extra = view.getUint16(at + 30, true);
    const comment = view.getUint16(at + 32, true);
    const local = view.getUint32(at + 42, true);
    const name = dec.decode(bytes.subarray(at + 46, at + 46 + nameLength));
    at += 46 + nameLength + extra + comment;
    if (!/^(xl\/|\[Content_Types\])/.test(name)) continue;
    total += size;
    if (total > maxTotal) throw new Error("ファイルが大きすぎます。");
    const start = local + 30 + view.getUint16(local + 26, true) + view.getUint16(local + 28, true);
    const raw = bytes.subarray(start, start + compressed);
    if (method === 0) out.set(name, raw);
    else if (method === 8) out.set(name, await inflateRaw(raw));
    else throw new Error("この圧縮の形式の Excel は読めません。");
  }
  return out;
}

function decodeXml(s: string): string {
  return s
    .replace(/&#x([0-9a-fA-F]+);/g, (_, h: string) => String.fromCodePoint(parseInt(h, 16)))
    .replace(/&#(\d+);/g, (_, d: string) => String.fromCodePoint(Number(d)))
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&");
}

/** 要素の中の <t> の文字をつなぐ（ふりがな <rPh> は除く）。 */
function textOf(xml: string): string {
  const body = xml.replace(/<rPh\b[\s\S]*?<\/rPh>/g, "");
  return [...body.matchAll(/<t\b[^>]*>([\s\S]*?)<\/t>/g)].map((m) => decodeXml(m[1]!)).join("");
}

function attr(tag: string, name: string): string | null {
  const m = new RegExp(`\\s${name}="([^"]*)"`).exec(tag);
  return m ? decodeXml(m[1]!) : null;
}

function columnIndex(ref: string): number {
  const letters = /^[A-Z]+/.exec(ref)?.[0] ?? "A";
  let n = 0;
  for (const ch of letters) n = n * 26 + (ch.charCodeAt(0) - 64);
  return n - 1;
}

function cellAt(ref: string): { r: number; c: number } | null {
  const m = /^\$?([A-Z]+)\$?(\d+)$/.exec(ref);
  return m ? { r: Number(m[2]) - 1, c: columnIndex(m[1]!) } : null;
}

/** styles.xml から、cellXfs の並びごとに塗りがある（白以外）かを返す。 */
function shadedStyles(xml: string | null): boolean[] {
  if (!xml) return [];
  const fills = [...(/<fills\b[^>]*>([\s\S]*?)<\/fills>/.exec(xml)?.[1] ?? "").matchAll(/<fill>([\s\S]*?)<\/fill>|<fill\/>/g)].map((m) => {
    const body = m[1] ?? "";
    const pattern = /<patternFill\b[^>]*>/.exec(body)?.[0];
    const type = pattern ? attr(pattern, "patternType") : null;
    if (/<gradientFill\b/.test(body)) return true;
    if (!type || type === "none") return false;
    const fg = /<fgColor\b[^>]*>/.exec(body)?.[0] ?? "";
    // 白（rgb の FFFFFF・テーマの背景 1・既定の色番号 9）は塗りとみなさない
    const rgb = attr(fg, "rgb")?.toUpperCase();
    if (rgb && /^(FF)?FFFFFF$/.test(rgb)) return false;
    if (attr(fg, "theme") === "0" && !attr(fg, "tint")) return false;
    if (attr(fg, "indexed") === "9") return false;
    return true;
  });
  const xfs = /<cellXfs\b[^>]*>([\s\S]*?)<\/cellXfs>/.exec(xml)?.[1] ?? "";
  return [...xfs.matchAll(/<xf\b[^>]*>/g)].map((m) => fills[Number(attr(m[0], "fillId") ?? 0)] ?? false);
}

/** xlsx のシートを、名前と値の表（文字。空のセルは null）・セル結合・在るセルと塗りのあるセルで読む。 */
export async function readXlsx(bytes: Uint8Array, options: { maxBytes?: number } = {}): Promise<ReadSheet[]> {
  const files = await unzip(bytes, options.maxBytes ?? 50 * 1024 * 1024);
  // 要素の名前空間の接頭辞（<x:sheet> など。Excel 以外のツールが付ける）は外して読む
  const text = (name: string) => {
    const f = files.get(name);
    return f ? dec.decode(f).replace(/<(\/?)[A-Za-z_][\w.-]*:(?=[A-Za-z_])/g, "<$1") : null;
  };
  const workbook = text("xl/workbook.xml");
  if (!workbook) throw new Error("Excel（xlsx）のファイルとして読めません。");
  const rels = new Map<string, string>();
  for (const m of (text("xl/_rels/workbook.xml.rels") ?? "").matchAll(/<Relationship\b[^>]*>/g)) {
    const id = attr(m[0], "Id");
    const target = attr(m[0], "Target");
    if (id && target) rels.set(id, target.startsWith("/") ? target.slice(1) : `xl/${target}`);
  }
  const shared = [...(text("xl/sharedStrings.xml") ?? "").matchAll(/<si\b[^>]*>([\s\S]*?)<\/si>/g)].map((m) => textOf(m[1]!));
  const shadedStyle = shadedStyles(text("xl/styles.xml"));
  const sheets: ReadSheet[] = [];
  for (const m of workbook.matchAll(/<sheet\b[^>]*\/?>/g)) {
    const name = attr(m[0], "name") ?? "";
    const id = attr(m[0], "r:id");
    const xml = id && rels.get(id) ? text(rels.get(id)!) : null;
    if (!xml) continue;
    const rows: Array<Array<string | null>> = [];
    const present = new Set<string>();
    const shaded = new Set<string>();
    for (const row of xml.matchAll(/<row\b([^>]*?)(?:\/>|>([\s\S]*?)<\/row>)/g)) {
      const r = Number(attr(row[0].slice(0, row[0].indexOf(">") + 1), "r") ?? rows.length + 1) - 1;
      if (r >= MAX_ROWS) break;
      const cells: Array<string | null> = [];
      for (const c of (row[2] ?? "").matchAll(/<c\b([^>]*?)(?:\/>|>([\s\S]*?)<\/c>)/g)) {
        const head = c[0].slice(0, c[0].indexOf(">") + 1);
        const ref = attr(head, "r");
        const col = ref ? columnIndex(ref) : cells.length;
        if (col >= MAX_COLS) continue;
        const type = attr(head, "t");
        const inner = c[2] ?? "";
        const v = /<v\b[^>]*>([\s\S]*?)<\/v>/.exec(inner)?.[1];
        let value: string | null = null;
        if (type === "s") value = v !== undefined ? (shared[Number(v)] ?? null) : null;
        else if (type === "inlineStr") value = textOf(inner);
        else if (type === "b") value = v === "1" ? "TRUE" : v === "0" ? "FALSE" : null;
        else if (v !== undefined) value = decodeXml(v);
        while (cells.length < col) cells.push(null);
        cells[col] = value === "" ? null : value;
        present.add(`${r},${col}`);
        if (shadedStyle[Number(attr(head, "s") ?? 0)]) shaded.add(`${r},${col}`);
      }
      while (rows.length < r) rows.push([]);
      rows[r] = cells;
    }
    const merges: MergeRange[] = [];
    for (const mc of xml.matchAll(/<mergeCell\b[^>]*>/g)) {
      const [a, b] = (attr(mc[0], "ref") ?? "").split(":");
      const from = a ? cellAt(a) : null;
      const to = b ? cellAt(b) : from;
      if (from && to) merges.push({ r1: Math.min(from.r, to.r), c1: Math.min(from.c, to.c), r2: Math.max(from.r, to.r), c2: Math.max(from.c, to.c) });
    }
    sheets.push({ name, rows, merges, present, shaded });
  }
  return sheets;
}
