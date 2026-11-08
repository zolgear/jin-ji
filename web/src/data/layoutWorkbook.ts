// 配置図の Excel のひな形（現場・総務が Excel で直して登録する形）。jin-ji の配置図の形式 v1（layoutDocument.ts）と行き来する。
// シートは「建物・階」「席」「エリア」「固定の部分」と「記入のしかた」。種類の列は日本語の選択肢で書き（コードでも読める）、
// 多角形は「x,y x,y …」（ミリメートル、空白・改行・; 区切り）。形の検査（外形の内側か・定員など）は登録で API が行う。
import type { CellValue, SheetData } from "../lib/xlsx";
import { LAYOUT_FORMAT, type LayoutDocument, type LayoutDocumentFloor, type LayoutDocumentSeat } from "./layoutDocument";
import type { FixtureKind, FloorKind, LayoutSeat, PointMm, ZoneKind } from "./workplace";

export const FLOOR_KIND_LABEL: Record<FloorKind, string> = { office: "事務所", factory: "工場", warehouse: "倉庫", lobby: "ロビー", service: "機械室・駐車場", other: "その他" };
export const ZONE_KIND_LABEL: Record<ZoneKind, string> = { office: "事務所", production_line: "製造ライン", storage: "保管", common: "共用", restricted: "立入制限" };
export const FIXTURE_KIND_LABEL: Record<FixtureKind, string> = { core: "コア", room: "部屋", void: "吹き抜け" };
export const DESK_TYPE_LABEL: Record<LayoutSeat["desk_type"], string> = {
  desk_1000: "机 1000",
  desk_1200: "机 1200",
  desk_1400: "机 1400",
  desk_1600: "机 1600",
  desk_1800: "机 1800",
  station: "持ち場",
  counter: "カウンター",
  booth: "ブース",
};
export const SEAT_KIND_LABEL: Record<LayoutSeat["seat_kind"], string> = { fixed: "固定席", station: "持ち場" };
export const FEATURE_LABEL: Record<LayoutSeat["features"][number], string> = {
  monitor: "モニター",
  dual_monitor: "モニター 2 台",
  standing: "スタンディング",
  phone_booth: "電話ブース",
  docking_station: "ドッキングステーション",
  locker: "ロッカー",
  quiet: "静かな席",
};

const SHEET = { floors: "建物・階", seats: "席", zones: "エリア", fixtures: "固定の部分", guide: "記入のしかた" } as const;
const FLOOR_COLUMNS = ["拠点コード", "建物コード", "建物名", "階", "並び順", "階の種類", "格子(mm)", "外形"] as const;
const SEAT_COLUMNS = ["建物コード", "階", "席番号", "X(mm)", "Y(mm)", "向き(度)", "机の型", "席の種類", "定員", "設備", "エリア", "使えない"] as const;
const ZONE_COLUMNS = ["建物コード", "階", "名前", "種類", "多角形"] as const;
const FIXTURE_COLUMNS = ["建物コード", "階", "種類", "名前", "多角形"] as const;
const YES = ["はい", "○", "〇", "true", "1", "yes"];
const NO = ["いいえ", "×", "false", "0", "no"];

const polygonText = (p: PointMm[]) => p.map(([x, y]) => `${x},${y}`).join(" ");

/** 配置図（形式 v1）を Excel のシートにする。document が null なら見出しと記入のしかただけのひな形。 */
export function toLayoutSheets(document: LayoutDocument | null): SheetData[] {
  const floors: CellValue[][] = [[...FLOOR_COLUMNS]];
  const seats: CellValue[][] = [[...SEAT_COLUMNS]];
  const zones: CellValue[][] = [[...ZONE_COLUMNS]];
  const fixtures: CellValue[][] = [[...FIXTURE_COLUMNS]];
  for (const b of document?.buildings ?? []) {
    for (const f of b.floors) {
      const l = f.layout;
      floors.push([document!.site_code, b.code, b.name ?? null, f.label, f.level_order ?? null, f.kind ? FLOOR_KIND_LABEL[f.kind] : null, l?.grid_mm ?? null, l ? polygonText(l.outline) : null]);
      for (const s of l?.seats ?? []) {
        seats.push([
          b.code,
          f.label,
          s.code,
          s.x_mm,
          s.y_mm,
          s.rotation_deg ?? 0,
          s.desk_type ? DESK_TYPE_LABEL[s.desk_type] : null,
          s.kind ? SEAT_KIND_LABEL[s.kind] : null,
          s.capacity ?? 1,
          (s.features ?? []).map((x) => FEATURE_LABEL[x as keyof typeof FEATURE_LABEL] ?? x).join("、") || null,
          s.zone ?? null,
          s.out_of_service === undefined ? null : s.out_of_service ? "はい" : "いいえ",
        ]);
      }
      for (const z of l?.zones ?? []) zones.push([b.code, f.label, z.name, z.kind ? ZONE_KIND_LABEL[z.kind] : null, polygonText(z.polygon)]);
      for (const x of l?.fixtures ?? []) fixtures.push([b.code, f.label, FIXTURE_KIND_LABEL[x.kind], x.name ?? null, polygonText(x.polygon)]);
    }
  }
  return [
    { name: SHEET.floors, rows: floors, widths: [12, 12, 16, 8, 8, 14, 10, 50], choices: { 5: Object.values(FLOOR_KIND_LABEL) } },
    {
      name: SHEET.seats,
      rows: seats,
      widths: [12, 8, 12, 10, 10, 9, 12, 10, 6, 24, 14, 9],
      choices: { 6: Object.values(DESK_TYPE_LABEL), 7: Object.values(SEAT_KIND_LABEL), 11: ["はい", "いいえ"] },
    },
    { name: SHEET.zones, rows: zones, widths: [12, 8, 16, 12, 60], choices: { 3: Object.values(ZONE_KIND_LABEL) } },
    { name: SHEET.fixtures, rows: fixtures, widths: [12, 8, 12, 16, 60], choices: { 2: Object.values(FIXTURE_KIND_LABEL) } },
    { name: SHEET.guide, rows: guideRows(), widths: [14, 14, 90] },
  ];
}

function guideRows(): CellValue[][] {
  const kinds = (m: Record<string, string>) => Object.values(m).join("・");
  return [
    ["シート", "列", "書き方"],
    [SHEET.floors, "", "1 行に 1 つの階。拠点コードは jin-ji の拠点のコード（ファイルの中で 1 つ）。建物コード・階の表示名で今の建物・階と突き合わせ、無ければ新しく作ります"],
    [SHEET.floors, "並び順", "地上 n 階は n × 10、地下 n 階は -n × 10、中二階は間の値（1F と 2F の間なら 15）"],
    [SHEET.floors, "階の種類", kinds(FLOOR_KIND_LABEL)],
    [SHEET.floors, "外形", "階の外形の頂点を「x,y」で、空白か改行で区切って順に書きます（単位はミリメートル、原点は図面の左下、x は右、y は奥）。空なら階だけを登録します"],
    [SHEET.seats, "", "1 行に 1 席。建物コード・階は「建物・階」の行と同じ値。席番号が今と同じ席は同じ席として、人の席の割り当てが続きます"],
    [SHEET.seats, "X(mm)・Y(mm)", "席の位置（外形の内側）"],
    [SHEET.seats, "向き(度)", "0〜345 の 15 度刻み"],
    [SHEET.seats, "机の型", kinds(DESK_TYPE_LABEL)],
    [SHEET.seats, "席の種類", `${kinds(SEAT_KIND_LABEL)}。定員 2 以上は持ち場だけ`],
    [SHEET.seats, "設備", `${kinds(FEATURE_LABEL)}（「、」で区切る）`],
    [SHEET.seats, "エリア", "「エリア」シートの名前"],
    [SHEET.seats, "使えない", "保守中・予備の席は「はい」。空なら今の値のまま"],
    [SHEET.zones, "種類", kinds(ZONE_KIND_LABEL)],
    [SHEET.zones, "多角形", "外形と同じ書き方（外形の内側）"],
    [SHEET.fixtures, "種類", kinds(FIXTURE_KIND_LABEL)],
    ["", "", "行を消すと、その席・エリアは配置図から外れます。登録する前に、登録する日を選びます"],
  ];
}

const norm = (s: string | null | undefined) => (s ?? "").normalize("NFKC").trim();

function lookup<K extends string>(labels: Record<K, string>, value: string): K | null {
  const v = norm(value);
  for (const [code, label] of Object.entries(labels) as Array<[K, string]>) if (v === code || v === norm(label)) return code;
  return null;
}

/** 読んだシートを配置図（形式 v1）にする。読めない値は problems に「シート 行: 理由」で返す（1 件でもあれば登録しない）。 */
export function fromLayoutSheets(sheets: Array<{ name: string; rows: Array<Array<string | null>> }>): { document: LayoutDocument | null; problems: string[] } {
  const problems: string[] = [];
  const byName = new Map(sheets.map((s) => [norm(s.name), s.rows]));
  const floorsSheet = byName.get(SHEET.floors);
  if (!floorsSheet) return { document: null, problems: [`「${SHEET.floors}」のシートがありません（ひな形を使ってください）。`] };

  // 見出しの名前で列を探す（列の並べ替え・追加に耐える）
  const table = (sheetName: string, columns: readonly string[]) => {
    const rows = byName.get(sheetName) ?? [];
    const header = (rows[0] ?? []).map((h) => norm(h));
    const index = columns.map((c) => header.indexOf(norm(c)));
    const missing = columns.filter((_, i) => index[i]! < 0);
    if (rows.length > 0 && missing.length > 0) problems.push(`${sheetName}: 見出しの列がありません（${missing.join("・")}）`);
    return rows
      .map((row, i) => ({ line: i + 1, get: (c: string) => norm(row[index[columns.indexOf(c)]!] ?? null) || null }))
      .slice(1)
      .filter((r) => columns.some((c) => r.get(c) !== null));
  };
  const at = (sheet: string, line: number) => `${sheet} ${line} 行目`;
  const int = (sheet: string, line: number, column: string, value: string | null, required: boolean): number | undefined => {
    if (value === null) {
      if (required) problems.push(`${at(sheet, line)}: ${column}がありません`);
      return undefined;
    }
    const n = Number(value);
    if (!Number.isInteger(n)) {
      problems.push(`${at(sheet, line)}: ${column}は整数で書いてください（${value}）`);
      return undefined;
    }
    return n;
  };
  const polygon = (sheet: string, line: number, column: string, value: string | null): PointMm[] | undefined => {
    if (value === null) return undefined;
    const points = value.split(/[\s;；]+/).filter(Boolean).map((p) => p.split(/[,，、]/).map(Number));
    if (points.length < 3 || points.some((p) => p.length !== 2 || !p.every(Number.isInteger))) {
      problems.push(`${at(sheet, line)}: ${column}は「x,y」を空白で区切って 3 点以上書いてください`);
      return undefined;
    }
    return points as PointMm[];
  };
  const choice = <K extends string>(sheet: string, line: number, column: string, labels: Record<K, string>, value: string | null): K | undefined => {
    if (value === null) return undefined;
    const code = lookup(labels, value);
    if (!code) problems.push(`${at(sheet, line)}: ${column}は ${Object.values(labels).join("・")} のどれかにしてください（${value}）`);
    return code ?? undefined;
  };

  const siteCodes = new Set<string>();
  const buildings = new Map<string, { code: string; name: string | undefined; floors: Map<string, LayoutDocumentFloor> }>();
  const floorOf = (sheet: string, line: number, building: string | null, label: string | null) => {
    if (!building || !label) {
      problems.push(`${at(sheet, line)}: 建物コードと階がありません`);
      return null;
    }
    const f = buildings.get(building)?.floors.get(label);
    if (!f) {
      problems.push(`${at(sheet, line)}: 建物 ${building} の階 ${label} が「${SHEET.floors}」にありません`);
      return null;
    }
    if (!f.layout) {
      problems.push(`${at(sheet, line)}: 建物 ${building} の階 ${label} に外形がありません`);
      return null;
    }
    return f;
  };

  for (const r of table(SHEET.floors, FLOOR_COLUMNS)) {
    const site = r.get("拠点コード");
    const code = r.get("建物コード");
    const label = r.get("階");
    if (!site || !code || !label) {
      problems.push(`${at(SHEET.floors, r.line)}: 拠点コード・建物コード・階がありません`);
      continue;
    }
    siteCodes.add(site);
    const building = buildings.get(code) ?? { code, name: undefined as string | undefined, floors: new Map<string, LayoutDocumentFloor>() };
    buildings.set(code, building);
    building.name ??= r.get("建物名") ?? undefined;
    if (building.floors.has(label)) {
      problems.push(`${at(SHEET.floors, r.line)}: 建物 ${code} の階 ${label} が重なっています`);
      continue;
    }
    const floor: LayoutDocumentFloor = { label };
    const order = int(SHEET.floors, r.line, "並び順", r.get("並び順"), false);
    if (order !== undefined) floor.level_order = order;
    const kind = choice(SHEET.floors, r.line, "階の種類", FLOOR_KIND_LABEL, r.get("階の種類"));
    if (kind) floor.kind = kind;
    const outline = polygon(SHEET.floors, r.line, "外形", r.get("外形"));
    if (outline) {
      floor.layout = { outline, fixtures: [], zones: [], seats: [] };
      const grid = int(SHEET.floors, r.line, "格子(mm)", r.get("格子(mm)"), false);
      if (grid !== undefined) floor.layout.grid_mm = grid;
    }
    building.floors.set(label, floor);
  }
  if (siteCodes.size > 1) problems.push(`${SHEET.floors}: 拠点コードは 1 つにしてください（${[...siteCodes].join("・")}）`);

  for (const r of table(SHEET.seats, SEAT_COLUMNS)) {
    const floor = floorOf(SHEET.seats, r.line, r.get("建物コード"), r.get("階"));
    const code = r.get("席番号");
    if (!code) {
      problems.push(`${at(SHEET.seats, r.line)}: 席番号がありません`);
      continue;
    }
    const x = int(SHEET.seats, r.line, "X(mm)", r.get("X(mm)"), true);
    const y = int(SHEET.seats, r.line, "Y(mm)", r.get("Y(mm)"), true);
    if (!floor || x === undefined || y === undefined) continue;
    const seat: LayoutDocumentSeat = { code, x_mm: x, y_mm: y };
    const rotation = int(SHEET.seats, r.line, "向き(度)", r.get("向き(度)"), false);
    if (rotation !== undefined) seat.rotation_deg = rotation;
    const desk = choice(SHEET.seats, r.line, "机の型", DESK_TYPE_LABEL, r.get("机の型"));
    if (desk) seat.desk_type = desk;
    const kind = choice(SHEET.seats, r.line, "席の種類", SEAT_KIND_LABEL, r.get("席の種類"));
    if (kind) seat.kind = kind;
    const capacity = int(SHEET.seats, r.line, "定員", r.get("定員"), false);
    if (capacity !== undefined) seat.capacity = capacity;
    const features = r.get("設備");
    if (features) {
      seat.features = features
        .split(/[、,，\n]+/)
        .map((f) => f.trim())
        .filter(Boolean)
        .map((f) => choice(SHEET.seats, r.line, "設備", FEATURE_LABEL, f))
        .filter((f): f is LayoutSeat["features"][number] => f !== undefined);
    }
    const zone = r.get("エリア");
    if (zone) seat.zone = zone;
    const out = r.get("使えない");
    if (out !== null) {
      const v = out.toLowerCase();
      if (YES.includes(v)) seat.out_of_service = true;
      else if (NO.includes(v)) seat.out_of_service = false;
      else problems.push(`${at(SHEET.seats, r.line)}: 使えないは「はい」「いいえ」か空にしてください（${out}）`);
    }
    floor.layout!.seats!.push(seat);
  }

  for (const r of table(SHEET.zones, ZONE_COLUMNS)) {
    const floor = floorOf(SHEET.zones, r.line, r.get("建物コード"), r.get("階"));
    const name = r.get("名前");
    const p = polygon(SHEET.zones, r.line, "多角形", r.get("多角形"));
    if (!name) problems.push(`${at(SHEET.zones, r.line)}: 名前がありません`);
    if (!floor || !name || !p) continue;
    const kind = choice(SHEET.zones, r.line, "種類", ZONE_KIND_LABEL, r.get("種類"));
    floor.layout!.zones!.push({ name, ...(kind ? { kind } : {}), polygon: p });
  }

  for (const r of table(SHEET.fixtures, FIXTURE_COLUMNS)) {
    const floor = floorOf(SHEET.fixtures, r.line, r.get("建物コード"), r.get("階"));
    const kind = choice(SHEET.fixtures, r.line, "種類", FIXTURE_KIND_LABEL, r.get("種類"));
    const p = polygon(SHEET.fixtures, r.line, "多角形", r.get("多角形"));
    if (r.get("種類") === null) problems.push(`${at(SHEET.fixtures, r.line)}: 種類がありません`);
    if (!floor || !kind || !p) continue;
    floor.layout!.fixtures!.push({ kind, name: r.get("名前"), polygon: p });
  }

  if (buildings.size === 0 && problems.length === 0) problems.push(`「${SHEET.floors}」に階の行がありません。`);
  const document: LayoutDocument | null =
    problems.length > 0
      ? null
      : {
          format: LAYOUT_FORMAT,
          site_code: [...siteCodes][0]!,
          buildings: [...buildings.values()].map((b) => ({ code: b.code, ...(b.name ? { name: b.name } : {}), floors: [...b.floors.values()] })),
        };
  return { document, problems };
}
