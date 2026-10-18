import { describe, expect, it } from "vitest";
import { countFilters, effectiveSort, isEmptySearch, matchingTenure, parseSearchState, tenureToHired, TENURE_PRESETS, toggleValue, writeSearchState, yearsBefore } from "./searchState";

describe("検索の条件と URL", () => {
  it("条件・並び順・表示の形を URL に書いて読み戻せる（複数の値は同じ名前を繰り返す）。他の引数は残す", () => {
    const base = new URLSearchParams({ as_of: "2026-04-01", person: "p1", source: "demo" });
    const state = {
      filters: {
        query: "やまもと",
        orgId: "o1",
        includeDescendants: false,
        legalEntities: ["e1"],
        levels: ["section"],
        assignmentKinds: ["concurrent" as const, "secondment" as const],
        positions: ["課長"],
        employmentTypes: ["regular"],
        jobTypes: ["営業"],
        sites: ["s1", "s2"],
        siteTypes: ["factory"],
        prefectures: ["13", "27"],
        remoteWork: false,
        hiredFrom: "2016-04-01",
        hiredTo: null,
        employmentStatuses: ["active"],
        accountStatuses: ["suspended"],
      },
      sort: "org" as const,
      layout: "table" as const,
      group: "site" as const,
    };
    const params = writeSearchState(base, state);
    expect(params.getAll("site")).toEqual(["s1", "s2"]);
    expect(params.get("remote")).toBe("0");
    expect(params.get("group")).toBe("site");
    expect(params.get("as_of")).toBe("2026-04-01");
    expect(params.get("person")).toBe("p1");
    expect(parseSearchState(params)).toEqual(state);
  });

  it("既定（フリガナ順・カード・配下を含む）は URL に書かない。知らない値・不正な日付は捨てる", () => {
    const empty = writeSearchState(new URLSearchParams(), parseSearchState(new URLSearchParams()));
    expect(empty.toString()).toBe("");
    const parsed = parseSearchState(new URLSearchParams("kind=main&kind=primary&hf=2026-13-01&sort=name&es=retired&in=o1"));
    expect(parsed.filters.assignmentKinds).toEqual(["primary"]);
    expect(parsed.filters.hiredFrom).toBeNull();
    expect(parsed.filters.employmentStatuses).toEqual([]);
    expect(parsed.filters.includeDescendants).toBe(true);
    expect(parsed.sort).toBe("kana");
    expect(parsed.group).toBe("none");
    const odd = parseSearchState(new URLSearchParams("group=dept&remote=yes&stype=castle&pref=48&pref=13&sort=site"));
    expect(odd.group).toBe("none");
    expect(odd.filters.remoteWork).toBeNull();
    expect(odd.filters.siteTypes).toEqual([]);
    expect(odd.filters.prefectures).toEqual(["13"]);
    // 拠点・リモートワークの並びはグループ化で使うもので、並び順の引数としては受け付けない
    expect(odd.sort).toBe("kana");
  });

  it("グループ化は URL に残り、API にはグループの並びを渡す（選んだ並び順はグループ化を外すと戻る）", () => {
    const state = parseSearchState(new URLSearchParams("group=remote_work&sort=hire_date&remote=1"));
    expect(state.group).toBe("remote_work");
    expect(effectiveSort(state)).toBe("remote_work");
    expect(effectiveSort({ ...state, group: "site" })).toBe("site");
    expect(effectiveSort({ ...state, group: "none" })).toBe("hire_date");
    expect(writeSearchState(new URLSearchParams(), state).toString()).toBe("remote=1&sort=hire_date&group=remote_work");
    expect(countFilters(state.filters)).toBe(1);
  });

  it("値の切り替え・条件の数・空の検索", () => {
    let f = toggleValue({}, "jobTypes", "営業");
    expect(f.jobTypes).toEqual(["営業"]);
    f = toggleValue(f, "jobTypes", "営業");
    expect(f.jobTypes).toEqual([]);
    expect(isEmptySearch({ query: "  " })).toBe(true);
    expect(countFilters({ orgId: "o", hiredFrom: "2020-01-01", hiredTo: "2021-01-01", positions: ["a", "b"] })).toBe(4);
  });

  it("勤続年数の目安を入社年月日の範囲にし、範囲から目安を引き戻せる（境界は業務日付から数える）", () => {
    const today = "2026-10-01";
    expect(tenureToHired(TENURE_PRESETS.find((p) => p.id === "lt1")!, today)).toEqual({ hiredFrom: "2025-10-02", hiredTo: null });
    expect(tenureToHired(TENURE_PRESETS.find((p) => p.id === "3to10")!, today)).toEqual({ hiredFrom: "2016-10-02", hiredTo: "2023-10-01" });
    expect(tenureToHired(TENURE_PRESETS.find((p) => p.id === "ge20")!, today)).toEqual({ hiredFrom: null, hiredTo: "2006-10-01" });
    for (const p of TENURE_PRESETS) expect(matchingTenure(tenureToHired(p, today), today)?.id).toBe(p.id);
    expect(yearsBefore("2028-02-29", 1)).toBe("2027-02-28");
  });
});
