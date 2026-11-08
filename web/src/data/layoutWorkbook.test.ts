import { describe, expect, it } from "vitest";
import { readXlsx, writeXlsx } from "../lib/xlsx";
import { LAYOUT_FORMAT, type LayoutDocument } from "./layoutDocument";
import { fromLayoutSheets, toLayoutSheets } from "./layoutWorkbook";

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
              [20000, 0],
              [20000, 10000],
              [0, 10000],
            ],
            fixtures: [{ kind: "core", name: "EV・階段", polygon: [[8000, 3000], [12000, 3000], [12000, 7000], [8000, 7000]] }],
            zones: [{ name: "西", kind: "office", polygon: [[0, 0], [8000, 0], [8000, 10000], [0, 10000]] }],
            seats: [
              { code: "A01", x_mm: 1000, y_mm: 2000, rotation_deg: 90, desk_type: "desk_1200", kind: "fixed", capacity: 1, features: ["dual_monitor", "quiet"], zone: "西" },
              { code: "S01", x_mm: 3000, y_mm: 2000, rotation_deg: 0, desk_type: "station", kind: "station", capacity: 3, features: [], zone: null },
            ],
          },
        },
        { label: "B1", level_order: -10, kind: "service" },
      ],
    },
  ],
};

const sheetsOf = async (d: LayoutDocument | null) => readXlsx(writeXlsx(toLayoutSheets(d)));

describe("配置図の Excel", () => {
  it("書き出した Excel を読むと同じ配置図になる（種類は日本語で書き、使えない席の印は書かない）", async () => {
    const sheets = await sheetsOf(doc);
    expect(sheets.map((s) => s.name)).toEqual(["建物・階", "席", "エリア", "固定の部分", "記入のしかた"]);
    expect(sheets[1]!.rows[1]).toEqual(["MAIN", "3F", "A01", "1000", "2000", "90", "机 1200", "固定席", "1", "モニター 2 台、静かな席", "西"]);

    const read = fromLayoutSheets(sheets);
    expect(read.problems).toEqual([]);
    const floor = read.document!.buildings[0]!.floors[0]!;
    // 空のエリア・設備は書かない（省いた値は空と同じ）
    const { zone: _zone, features: _features, ...station } = doc.buildings[0]!.floors[0]!.layout!.seats![1]!;
    expect(floor.layout!.seats).toEqual([doc.buildings[0]!.floors[0]!.layout!.seats![0], station]);
    expect(floor.layout!.zones).toEqual(doc.buildings[0]!.floors[0]!.layout!.zones);
    expect(floor.layout!.fixtures).toEqual(doc.buildings[0]!.floors[0]!.layout!.fixtures);
    expect(read.document!.buildings[0]!.floors[1]).toEqual({ label: "B1", level_order: -10, kind: "service" });
  });

  it("ひな形は見出しだけで、そのまま読むと階が無い理由を返す", async () => {
    const sheets = await sheetsOf(null);
    expect(sheets[0]!.rows).toEqual([["拠点コード", "建物コード", "建物名", "階", "並び順", "階の種類", "格子(mm)", "外形"]]);
    expect(fromLayoutSheets(sheets).problems).toEqual(["「建物・階」に階の行がありません。"]);
  });

  it("コードでも全角でも読み、読めない値・無い階・外形の無い階の席はシートと行で知らせる", () => {
    const read = fromLayoutSheets([
      {
        name: "建物・階",
        rows: [
          ["拠点コード", "建物コード", "建物名", "階", "並び順", "階の種類", "格子(mm)", "外形"],
          ["TKY", "MAIN", "本館", "3F", "３０", "office", null, "0,0 1000,0 1000,1000"],
          ["TKY", "MAIN", "本館", "4F", "40", "事務所", null, null],
        ],
      },
      {
        name: "席",
        rows: [
          ["建物コード", "階", "席番号", "X(mm)", "Y(mm)", "向き(度)", "机の型", "席の種類", "定員", "設備", "エリア", "使えない"],
          ["MAIN", "3F", "A01", "１００", "100", null, "desk_1400", "持ち場", null, null, null, "はい"],
          ["MAIN", "3F", "A02", "1.5", "100", null, null, null, null, null, null, null],
          ["MAIN", "3F", "A05", "1", "1", null, "机 9999", null, null, null, null, null],
          ["MAIN", "5F", "A03", "1", "1", null, null, null, null, null, null, null],
          ["MAIN", "4F", "A04", "1", "1", null, null, null, null, null, null, null],
        ],
      },
    ]);
    expect(read.document).toBeNull();
    expect(read.problems).toEqual([
      "席 3 行目: X(mm)は整数で書いてください（1.5）",
      "席 4 行目: 机の型は 机 1000・机 1200・机 1400・机 1600・机 1800・持ち場・カウンター・ブース のどれかにしてください（机 9999）",
      "席 5 行目: 建物 MAIN の階 5F が「建物・階」にありません",
      "席 6 行目: 建物 MAIN の階 4F に外形がありません",
    ]);
  });
});
