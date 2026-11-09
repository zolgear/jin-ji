import { describe, expect, it } from "vitest";
import { columnName, readXlsx, writeXlsx } from "./xlsx";

async function deflateRaw(data: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([data as BlobPart]).stream().pipeThrough(new CompressionStream("deflate-raw"));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

/** 値を [バイト数, 値] の並びで、リトルエンディアンで足す。 */
function put(out: number[], fields: Array<[2 | 4, number]>) {
  for (const [size, v] of fields) for (let i = 0; i < size; i++) out.push(Number((BigInt(v) >> BigInt(8 * i)) & 0xffn));
}

/** 書いた無圧縮の ZIP を、Excel が保存するときと同じ deflate の ZIP に作り直す（中央ディレクトリを読む道の確認用）。 */
async function recompress(bytes: Uint8Array): Promise<Uint8Array> {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const out: number[] = [];
  const central: number[] = [];
  let at = 0;
  let count = 0;
  while (view.getUint32(at, true) === 0x04034b50) {
    const crc = view.getUint32(at + 14, true);
    const size = view.getUint32(at + 18, true);
    const nameLength = view.getUint16(at + 26, true);
    const name = bytes.subarray(at + 30, at + 30 + nameLength);
    const packed = await deflateRaw(bytes.subarray(at + 30 + nameLength, at + 30 + nameLength + size));
    const offset = out.length;
    put(out, [[4, 0x04034b50], [2, 20], [2, 0x0800], [2, 8], [2, 0], [2, 0], [4, crc], [4, packed.length], [4, size], [2, nameLength], [2, 0]]);
    out.push(...name, ...packed);
    put(central, [[4, 0x02014b50], [2, 20], [2, 20], [2, 0x0800], [2, 8], [2, 0], [2, 0], [4, crc], [4, packed.length], [4, size], [2, nameLength], [2, 0], [2, 0], [2, 0], [2, 0], [4, 0], [4, offset]]);
    central.push(...name);
    at += 30 + nameLength + size;
    count++;
  }
  const start = out.length;
  out.push(...central);
  put(out, [[4, 0x06054b50], [2, 0], [2, 0], [2, count], [2, count], [4, central.length], [4, start], [2, 0]]);
  return new Uint8Array(out);
}

describe("xlsx の読み書き", () => {
  it("列の名前は A・Z・AA と続く", () => {
    expect([0, 25, 26, 27].map(columnName)).toEqual(["A", "Z", "AA", "AB"]);
  });

  it("書いたシートを、無圧縮でも deflate でも同じ値で読める（記号・改行を含む文字、数、空のセル）", async () => {
    const bytes = writeXlsx([
      { name: "席", rows: [["席番号", "X(mm)", "メモ"], ["A-01", 1200, "a&b <c> \"d\"\n次の行"], ["A-02", null, null]] },
      { name: "空", rows: [] },
    ]);
    for (const file of [bytes, await recompress(bytes)]) {
      const sheets = await readXlsx(file);
      expect(sheets.map((s) => s.name)).toEqual(["席", "空"]);
      expect(sheets[0]!.rows).toEqual([["席番号", "X(mm)", "メモ"], ["A-01", "1200", "a&b <c> \"d\"\n次の行"], ["A-02"]]);
    }
  });

  it("セル結合と、見た目だけの空のセル・塗りのあるセルを読める", async () => {
    const bytes = writeXlsx([
      {
        name: "3F",
        rows: [[null, "0m"], [null, "A01"]],
        styles: [[null, "ruler"], ["shade", "boxBlue", "boxBlue", "grid"]],
        merges: [{ r1: 1, c1: 1, r2: 1, c2: 2 }],
        defaultWidth: 2.7,
        rowHeight: 18,
        freeze: { rows: 1, cols: 1 },
      },
    ]);
    for (const file of [bytes, await recompress(bytes)]) {
      const [sheet] = await readXlsx(file);
      expect(sheet!.merges).toEqual([{ r1: 1, c1: 1, r2: 1, c2: 2 }]);
      expect([...sheet!.present].sort()).toEqual(["0,1", "1,0", "1,1", "1,2", "1,3"]);
      // 枠の淡い色も塗りとして返す（床かどうかは呼ぶ側が枠を先に除いて決める）
      expect([...sheet!.shaded].sort()).toEqual(["1,0", "1,1", "1,2"]);
    }
  });

  it("xlsx でないファイルは読まない", async () => {
    await expect(readXlsx(new TextEncoder().encode("not a zip"))).rejects.toThrow("xlsx");
  });
});
