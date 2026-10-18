import { describe, expect, it } from "vitest";
import { formatDate, formatDateShort, parseDateInput, toWareki } from "./date";

describe("和暦", () => {
  it("元号の境目を正しく扱い、1 年は元年と書く", () => {
    expect(toWareki("2019-04-30")).toEqual({ era: "平成", year: 31 });
    expect(toWareki("2019-05-01")).toEqual({ era: "令和", year: 1 });
    expect(toWareki("1989-01-07")).toEqual({ era: "昭和", year: 64 });
    expect(toWareki("1989-01-08")).toEqual({ era: "平成", year: 1 });
    expect(formatDate("2019-05-01", "wareki")).toBe("令和元年5月1日");
    expect(formatDate("2026-10-01", "wareki")).toBe("令和8年10月1日");
    expect(formatDate("2026-10-01", "seireki")).toBe("2026年10月1日");
    expect(formatDateShort("2026-04-01", "wareki")).toBe("R8.04.01");
    expect(formatDateShort("2026-04-01")).toBe("2026/04/01");
  });

  it("明治 6 年より前は西暦で表示する", () => {
    expect(formatDate("1870-01-01", "wareki")).toBe("1870年1月1日");
  });
});

describe("parseDateInput", () => {
  it.each([
    ["2026-04-01", "2026-04-01"],
    ["2026/4/1", "2026-04-01"],
    ["２０２６／４／１", "2026-04-01"],
    ["20260401", "2026-04-01"],
    ["2026年4月1日", "2026-04-01"],
    ["令和8年4月1日", "2026-04-01"],
    ["令和元年5月1日", "2019-05-01"],
    ["R8.4.1", "2026-04-01"],
    ["h31/4/30", "2019-04-30"],
  ])("%s → %s", (input, expected) => {
    expect(parseDateInput(input)).toBe(expected);
  });

  it.each(["", "2026/2/30", "令和1年4月30日", "平成32年1月1日", "10/01/2026", "あした"])("%s は読めない", (input) => {
    expect(parseDateInput(input)).toBeNull();
  });
});
