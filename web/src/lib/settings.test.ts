import { describe, expect, it } from "vitest";
import { formatHash, parseHash } from "./router";
import {
  chartDirectionParam,
  DEFAULT_SETTINGS,
  floorOrgColorParam,
  parseSettings,
  resolveChartDirection,
  resolveFloorOrgColor,
  serializeSetting,
  SETTING_KEYS,
} from "./settings";

describe("表示の設定の保存", () => {
  it("何も保存していなければ既定値（組織で色分けはオン・組織図は上から下）", () => {
    expect(parseSettings(() => null)).toEqual({ floorOrgColor: true, chartDirection: "tb" });
    expect(DEFAULT_SETTINGS.floorOrgColor).toBe(true);
  });

  it("保存した値を読み、知らない値は既定値にする", () => {
    const stored: Record<string, string> = { [SETTING_KEYS.floorOrgColor]: "off", [SETTING_KEYS.chartDirection]: "lr" };
    expect(parseSettings((k) => stored[k] ?? null)).toEqual({ floorOrgColor: false, chartDirection: "lr" });
    expect(parseSettings(() => "壊れた値")).toEqual({ floorOrgColor: true, chartDirection: "tb" });
  });

  it("既定値は保存しない（null）、それ以外は文字列にする", () => {
    expect(serializeSetting("floorOrgColor", true)).toBeNull();
    expect(serializeSetting("floorOrgColor", false)).toBe("off");
    expect(serializeSetting("chartDirection", "tb")).toBeNull();
    expect(serializeSetting("chartDirection", "lr")).toBe("lr");
    // 書いた値を読み直すと同じ設定に戻る
    const stored: Record<string, string | null> = { [SETTING_KEYS.floorOrgColor]: serializeSetting("floorOrgColor", false), [SETTING_KEYS.chartDirection]: serializeSetting("chartDirection", "lr") };
    expect(parseSettings((k) => stored[k] ?? null)).toEqual({ floorOrgColor: false, chartDirection: "lr" });
  });
});

describe("URL のクエリが設定より優先する", () => {
  it("組織で色分け: color=org・color=none は設定より優先し、書かなければ設定に従う", () => {
    expect(resolveFloorOrgColor(null, true)).toBe(true);
    expect(resolveFloorOrgColor(null, false)).toBe(false);
    expect(resolveFloorOrgColor("none", true)).toBe(false);
    expect(resolveFloorOrgColor("org", false)).toBe(true);
    // 組織モードの color=site は建物・フロアモードでは意味を持たない
    expect(resolveFloorOrgColor("site", false)).toBe(false);
  });

  it("組織で色分けの切り替え: 設定と同じなら URL に書かず、違えば明示する", () => {
    expect(floorOrgColorParam(true, true)).toBeNull();
    expect(floorOrgColorParam(false, true)).toBe("none");
    expect(floorOrgColorParam(true, false)).toBe("org");
    expect(floorOrgColorParam(false, false)).toBeNull();
  });

  it("組織図の向き: dir は設定より優先し、切り替えは設定と同じなら書かない", () => {
    expect(resolveChartDirection(null, "lr")).toBe("lr");
    expect(resolveChartDirection("tb", "lr")).toBe("tb");
    expect(resolveChartDirection("lr", "tb")).toBe("lr");
    expect(resolveChartDirection("xx", "tb")).toBe("tb");
    expect(chartDirectionParam("lr", "lr")).toBeNull();
    expect(chartDirectionParam("tb", "lr")).toBe("tb");
    expect(chartDirectionParam("lr", "tb")).toBe("lr");
  });
});

describe("設定の画面の URL", () => {
  it("#/settings/<項目> の 2 段目を読み書きし、ほかの画面では 2 段目を持たない", () => {
    const r = parseHash("#/settings/licenses?source=demo-large");
    expect(r.view).toBe("settings");
    expect(r.sub).toBe("licenses");
    expect(formatHash(r)).toBe("#/settings/licenses?source=demo-large");
    expect(parseHash("#/settings").sub).toBeNull();
    expect(parseHash("#/org-chart/x").view).toBe("org-chart");
    expect(parseHash("#/org-chart/x").sub).toBeNull();
  });
});
