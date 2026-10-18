// 日付（yyyy-MM-dd の文字列）の扱い。業務日付は日本時間（UTC+9）の日付（概要 §5.6）。

const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

export function isIsoDate(value: string): boolean {
  if (!DATE_RE.test(value)) return false;
  const d = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === value;
}

/** 日本時間の今日。 */
export function businessDateJst(now: Date = new Date()): string {
  return new Date(now.getTime() + 9 * 60 * 60 * 1000).toISOString().slice(0, 10);
}

export function addDays(date: string, days: number): string {
  const d = new Date(`${date}T00:00:00Z`);
  d.setUTCDate(d.getUTCDate() + days);
  return d.toISOString().slice(0, 10);
}

/** [from, until) に date が入るか（until が null なら無期限）。 */
export function inPeriod(date: string, from: string, until: string | null | undefined): boolean {
  return from <= date && (until == null || date < until);
}

/** 2026-04-01 → 2026年4月1日（常に西暦。画面の表示は useFormatDate で暦の設定に従う） */
export function formatDateJa(date: string | null | undefined): string {
  if (!date || !isIsoDate(date)) return "";
  const [y, m, d] = date.split("-").map(Number);
  return `${y}年${m}月${d}日`;
}

/** 表示の暦。内部の値は常に西暦の yyyy-MM-dd で、表示だけを切り替える。 */
export type DateStyle = "seireki" | "wareki";

/** 元号（開始日の新しい順）。明治 5 年以前は太陰太陽暦のため、明治 6 年（1873-01-01）より前は西暦で表示する。 */
const ERAS: ReadonlyArray<{ name: string; initial: string; start: string }> = [
  { name: "令和", initial: "R", start: "2019-05-01" },
  { name: "平成", initial: "H", start: "1989-01-08" },
  { name: "昭和", initial: "S", start: "1926-12-25" },
  { name: "大正", initial: "T", start: "1912-07-30" },
  { name: "明治", initial: "M", start: "1868-10-23" },
];
const GREGORIAN_ADOPTED = "1873-01-01";

/** 2019-05-01 → { era: "令和", year: 1 }。元号の範囲外は null。 */
export function toWareki(date: string): { era: string; year: number } | null {
  if (!isIsoDate(date) || date < GREGORIAN_ADOPTED) return null;
  const era = ERAS.find((e) => date >= e.start);
  if (!era) return null;
  return { era: era.name, year: Number(date.slice(0, 4)) - Number(era.start.slice(0, 4)) + 1 };
}

/** 2026-04-01 → 2026年4月1日（西暦）/ 令和8年4月1日（和暦）。和暦の 1 年は「元年」。 */
export function formatDate(date: string | null | undefined, style: DateStyle = "seireki"): string {
  if (!date || !isIsoDate(date)) return "";
  const [y, m, d] = date.split("-").map(Number);
  if (style === "wareki") {
    const w = toWareki(date);
    if (w) return `${w.era}${w.year === 1 ? "元" : w.year}年${m}月${d}日`;
  }
  return `${y}年${m}月${d}日`;
}

/** 入力欄に出す短い形: 2026/04/01（西暦）/ R8.04.01（和暦）。 */
export function formatDateShort(date: string, style: DateStyle = "seireki"): string {
  if (!isIsoDate(date)) return "";
  const [y, m, d] = date.split("-");
  if (style === "wareki") {
    const w = toWareki(date);
    const era = w ? ERAS.find((e) => e.name === w.era) : undefined;
    if (w && era) return `${era.initial}${w.year}.${m}.${d}`;
  }
  return `${y}/${m}/${d}`;
}

const pad2 = (n: number) => String(n).padStart(2, "0");

/**
 * 利用者が入力した日付を yyyy-MM-dd にする。読めなければ null。
 * 受け付ける形: 2026-04-01・2026/4/1・2026.4.1・20260401・2026年4月1日・令和8年4月1日・令和元年5月1日・R8/4/1・R8.4.1・r8.4.1。
 * 全角の数字・記号も受け付ける。
 */
export function parseDateInput(input: string): string | null {
  const s = input
    .normalize("NFKC")
    .trim()
    .replace(/\s+/g, "")
    .replace(/元年/, "1年");
  let year: number;
  let month: number;
  let day: number;
  const compact = /^(\d{4})(\d{2})(\d{2})$/.exec(s);
  const western = /^(\d{4})[-/.年](\d{1,2})[-/.月](\d{1,2})日?$/.exec(s);
  const japanese = /^(令和|平成|昭和|大正|明治|[RHSTMrhstm])(\d{1,2})[-/.年](\d{1,2})[-/.月](\d{1,2})日?$/.exec(s);
  if (compact) {
    [year, month, day] = [Number(compact[1]), Number(compact[2]), Number(compact[3])];
  } else if (western) {
    [year, month, day] = [Number(western[1]), Number(western[2]), Number(western[3])];
  } else if (japanese) {
    const key = japanese[1]!;
    const era = ERAS.find((e) => e.name === key || e.initial === key.toUpperCase());
    if (!era) return null;
    year = Number(era.start.slice(0, 4)) + Number(japanese[2]) - 1;
    [month, day] = [Number(japanese[3]), Number(japanese[4])];
    const iso = `${year}-${pad2(month)}-${pad2(day)}`;
    // 元号の範囲外（例: 平成32年）は受け付けない
    if (!isIsoDate(iso) || toWareki(iso)?.era !== era.name) return null;
    return iso;
  } else {
    return null;
  }
  const iso = `${String(year).padStart(4, "0")}-${pad2(month)}-${pad2(day)}`;
  return isIsoDate(iso) ? iso : null;
}
