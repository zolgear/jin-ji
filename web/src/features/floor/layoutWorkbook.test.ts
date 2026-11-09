import { describe, expect, it } from "vitest";
import { readXlsx, writeXlsx, type ReadSheet } from "../../lib/xlsx";
import { LAYOUT_FORMAT, type LayoutDocument } from "../../data/layoutDocument";
import { classify, fromLayoutSheets, toLayoutSheets } from "./layoutWorkbook";

const doc: LayoutDocument = {
  format: LAYOUT_FORMAT,
  site_code: "TKY",
  buildings: [
    {
      code: "MAIN",
      name: "本館",
      floors: [
        {
          label: "3F",
          level_order: 30,
          kind: "office",
          layout: {
            grid_mm: 100,
            outline: [
              [0, 0],
              [12000, 0],
              [12000, 6000],
              [0, 6000],
            ],
            fixtures: [{ kind: "core", name: "EV", polygon: [[9000, 3000], [10800, 3000], [10800, 4800], [9000, 4800]] }],
            zones: [{ name: "西", kind: "office", polygon: [[0, 0], [6000, 0], [6000, 6000], [0, 6000]] }],
            seats: [
              { code: "A01", x_mm: 1250, y_mm: 1000, rotation_deg: 180, desk_type: "desk_1200", kind: "fixed", capacity: 1, features: ["dual_monitor"], zone: "西" },
              { code: "A02", x_mm: 2450, y_mm: 1000, rotation_deg: 0, desk_type: "desk_1200", kind: "fixed", capacity: 1, features: [], zone: "西" },
            ],
          },
        },
        { label: "B1", level_order: -10, kind: "service" },
      ],
    },
  ],
};

const read = async (d: LayoutDocument) => readXlsx(writeXlsx(toLayoutSheets(d)));

/** 読んだシートのセルを書き換える（Excel で直した体）。 */
function edit(sheet: ReadSheet, r: number, c: number, value: string | null, opts: { shade?: boolean } = {}) {
  (sheet.rows[r] ??= [])[c] = value;
  sheet.present.add(`${r},${c}`);
  if (opts.shade) sheet.shaded.add(`${r},${c}`);
  else sheet.shaded.delete(`${r},${c}`);
}

describe("配置図の Excel 方眼紙", () => {
  it("名前から種類を決める（先頭の種類で明示できる）", () => {
    expect(classify("Ａ０１")).toEqual({ kind: "seat", code: "A01" });
    expect(classify("EV")).toEqual({ kind: "fixture", fixture: "core", name: "EV" });
    expect(classify("階段")).toEqual({ kind: "fixture", fixture: "core", name: "階段" });
    expect(classify("吹き抜け")).toEqual({ kind: "fixture", fixture: "void", name: null });
    expect(classify("会議室A")).toEqual({ kind: "fixture", fixture: "room", name: "会議室A" });
    expect(classify("席 窓際1")).toEqual({ kind: "seat", code: "窓際1" });
    expect(classify("部屋 101")).toEqual({ kind: "fixture", fixture: "room", name: "101" });
  });

  it("1 階 1 シートの方眼で、机は結合した枠、外は灰色。そのまま読むと今の配置図と同じ", async () => {
    const sheets = await read(doc);
    expect(sheets.map((s) => s.name)).toEqual(["3F", "B1", "記入のしかた"]);
    const s3 = sheets[0]!;
    // 原点は -3m（外のマスを 1 つ残す）。目盛りは 3m ごと
    expect(s3.rows[0]![1]).toBe("-3m");
    expect(s3.rows[0]![6]).toBe("0m");
    // A01（x 650〜1850・y 650〜1350）は 2 マス横の枠
    const a01 = s3.merges.find((m) => s3.rows[m.r1]?.[m.c1] === "A01")!;
    expect(a01.c2 - a01.c1 + 1).toBe(2);
    expect(a01.r2 - a01.r1 + 1).toBe(1);
    expect(s3.shaded.has("1,1")).toBe(true);

    const result = fromLayoutSheets(sheets, doc);
    expect(result.problems).toEqual([]);
    const floors = result.document!.buildings[0]!.floors;
    // 手を付けていない配置図の無い階（B1）は送らない
    expect(floors.map((f) => f.label)).toEqual(["3F"]);
    const layout = floors[0]!.layout!;
    const before = doc.buildings[0]!.floors[0]!.layout!;
    expect(layout.outline).toEqual(before.outline);
    expect(layout.seats).toEqual(before.seats);
    expect(layout.fixtures).toEqual(before.fixtures);
    expect(layout.zones).toEqual(before.zones);
  });

  it("枠を動かした席は位置を方眼に合わせ、縦の枠は縦向き、新しい名前は新しい席・部屋になる", async () => {
    const sheets = await read(doc);
    const s3 = sheets[0]!;
    const a02 = s3.merges.find((m) => s3.rows[m.r1]?.[m.c1] === "A02")!;
    // A02 を縦 2 マスにする
    s3.merges.splice(s3.merges.indexOf(a02), 1, { r1: a02.r1 - 1, c1: a02.c1, r2: a02.r1, c2: a02.c1 });
    edit(s3, a02.r1, a02.c1, null);
    edit(s3, a02.r1 - 1, a02.c1, "A02");
    // 会議室（3×2）と新しい席
    s3.merges.push({ r1: 3, c1: 14, r2: 4, c2: 16 }, { r1: 8, c1: 14, r2: 8, c2: 15 });
    edit(s3, 3, 14, "会議室A");
    edit(s3, 8, 14, "Ｂ０１");

    const result = fromLayoutSheets(sheets, doc);
    expect(result.problems).toEqual([]);
    const layout = result.document!.buildings[0]!.floors[0]!.layout!;
    const a02seat = layout.seats!.find((s) => s.code === "A02")!;
    expect(a02seat).toMatchObject({ rotation_deg: 90, desk_type: "desk_1200", zone: "西" });
    expect(layout.seats!.find((s) => s.code === "A01")).toEqual(doc.buildings[0]!.floors[0]!.layout!.seats![0]);
    // B01: O〜P 列（x 4800〜6000）・9 行目（y 1800〜2400）
    expect(layout.seats!.find((s) => s.code === "B01")).toEqual({ code: "B01", x_mm: 5400, y_mm: 2100, rotation_deg: 0, desk_type: "desk_1200", kind: "fixed", capacity: 1, features: [], zone: "西" });
    expect(layout.fixtures).toContainEqual({ kind: "room", name: "会議室A", polygon: [[4800, 4200], [6600, 4200], [6600, 5400], [4800, 5400]] });
  });

  it("灰色に塗ったマスで外形が欠け、新しいシートは新しい階になる", async () => {
    const sheets = await read(doc);
    const s3 = sheets[0]!;
    // 右上の角（x 10800〜12000・y 4800〜6000）を外にする
    for (const [r, c] of [[2, 24], [2, 25], [3, 24], [3, 25]] as const) edit(s3, r, c, null, { shade: true });
    // 空の階を写したシートに席を 1 つ置く
    const s4: ReadSheet = { ...sheets[1]!, name: "4F", rows: sheets[1]!.rows.map((r) => [...r]) };
    edit(s4, 2, 2, "C01");
    const result = fromLayoutSheets([...sheets, s4], doc);
    expect(result.problems).toEqual([]);
    const floors = result.document!.buildings[0]!.floors;
    expect(floors[0]!.layout!.outline).toEqual([
      [0, 6000],
      [10800, 6000],
      [10800, 4800],
      [12000, 4800],
      [12000, 0],
      [0, 0],
    ]);
    expect(floors[1]).toMatchObject({ label: "4F", level_order: 40, kind: "office" });
    expect(floors[1]!.layout!.outline).toEqual([
      [0, 18000],
      [30000, 18000],
      [30000, 0],
      [0, 0],
    ]);
  });

  it("離れた床・名前の無い枠・同じ席番号はシートとセルで知らせる", async () => {
    const sheets = await read(doc);
    const s3 = sheets[0]!;
    edit(s3, 1, 30, null); // 外の先に床のマス
    s3.merges.push({ r1: 9, c1: 14, r2: 9, c2: 15 });
    edit(s3, 6, 18, "A01");
    const result = fromLayoutSheets(sheets, doc);
    expect(result.document).toBeNull();
    expect(result.problems).toEqual(["3F: 床（罫線のある白いマス）が離れています。ひとつながりにしてください"]);

    s3.present.delete("1,30");
    expect(fromLayoutSheets(sheets, doc).problems).toEqual(["3F の O10: 枠に名前がありません", "3F の H11: 席番号 A01 が 2 つあります"]);
  });
});
