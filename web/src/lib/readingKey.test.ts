import { describe, expect, it } from "vitest";
import { READING_MIN_MATCH_LENGTH, readingKey } from "./readingKey";
import vectors from "./readingKey.vectors.json";

// テストベクタは API 側の ReadingKeyTests（tests/JinJi.IntegrationTests）と共有する
const cases: Array<{ input: string; expected: string | null }> = vectors;

describe("readingKey", () => {
  it.each(cases.map((v) => [v.input, v.expected] as const))("共有のテストベクタ: %j → %j", (input, expected) => {
    expect(readingKey(input)).toBe(expected);
  });

  it("null・undefined は null", () => {
    expect(readingKey(null)).toBeNull();
    expect(readingKey(undefined)).toBeNull();
  });

  it("結果は英小文字だけで、もう一度通しても変わらない（冪等）", () => {
    for (const { input } of cases) {
      const key = readingKey(input);
      if (key === null) continue;
      expect(key).toMatch(/^[a-z]+$/);
      expect(readingKey(key)).toBe(key);
    }
  });

  it("英字とかなの任意の並びでも冪等", () => {
    // 畳み込みで新しくできた綴り（例: oh の後ろの子音の重なり）も正規化されることを確かめる
    const alphabet = "abcdefghijklmnopqrstuvwxyzあいうかきしちつっゃゅょぁぃーん";
    let seed = 12345;
    const next = () => (seed = (seed * 1103515245 + 12345) % 2147483648);
    for (let n = 0; n < 2000; n++) {
      let text = "";
      const length = 1 + (next() % 10);
      for (let i = 0; i < length; i++) text += alphabet[next() % alphabet.length];
      const key = readingKey(text);
      if (key === null) continue;
      expect(key).toMatch(/^[a-z]+$/);
      expect(readingKey(key), text).toBe(key);
    }
  });

  it("照合に使う最小の長さは 3", () => {
    expect(READING_MIN_MATCH_LENGTH).toBe(3);
  });
});
