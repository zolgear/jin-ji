// 同梱しているライブラリのライセンスの一覧（licenses.mjs）の純粋な部分と、node_modules からの読み取りを確かめる。
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { collectLicenses, dependencyRoots, extractCopyright, licenseOf, packageRootOf, rootsOfModules, virtualPackageOf } from "./licenses.mjs";

const root = new URL("..", import.meta.url).pathname;

describe("packageRootOf", () => {
  it("pnpm の実体のパスから、最後の node_modules の下のパッケージの場所を返す", () => {
    expect(packageRootOf("/w/node_modules/.pnpm/react@19.3.0/node_modules/react/cjs/react.production.js")).toBe("/w/node_modules/.pnpm/react@19.3.0/node_modules/react");
    expect(packageRootOf("/w/node_modules/@xyflow/react/dist/esm/index.js?v=1")).toBe("/w/node_modules/@xyflow/react");
    expect(packageRootOf("C:\\w\\node_modules\\three\\build\\three.module.js")).toBe("C:/w/node_modules/three");
  });

  it("node_modules の外（自前のコード）は null", () => {
    expect(packageRootOf("/w/src/App.tsx")).toBeNull();
    expect(packageRootOf("/w/node_modules/.pnpm/lock.yaml")).toBeNull();
  });
});

describe("virtualPackageOf", () => {
  it("ビルドの道具が差し込む仮想モジュールの出どころ", () => {
    expect(virtualPackageOf("\0vite/preload-helper.js")).toBe("vite");
    expect(virtualPackageOf("\0rolldown/runtime.js")).toBe("rolldown");
    expect(virtualPackageOf("\0other")).toBeNull();
  });
});

describe("extractCopyright", () => {
  it("Copyright・(c)・© の行を取り出し、重複と雛形の行を除く", () => {
    const text = [
      "MIT License",
      "",
      "Copyright (c) Meta Platforms, Inc. and affiliates.",
      "  Copyright (c) Meta Platforms, Inc. and affiliates.",
      "© 2020 Someone",
      "Copyright [yyyy] [name of copyright owner]",
      "Copyright notice ...",
      "copyright statement(s).",
      "Copyright holders may ...",
      "Permission is hereby granted",
    ].join("\n");
    expect(extractCopyright(text)).toEqual(["Copyright (c) Meta Platforms, Inc. and affiliates.", "© 2020 Someone"]);
  });
});

describe("licenseOf", () => {
  it("license の文字列・古い形のオブジェクト・licenses の配列", () => {
    expect(licenseOf({ license: "MIT" })).toBe("MIT");
    expect(licenseOf({ license: { type: "ISC" } })).toBe("ISC");
    expect(licenseOf({ licenses: [{ type: "MIT" }, "Apache-2.0"] })).toBe("MIT OR Apache-2.0");
    expect(licenseOf({})).toBe("UNKNOWN");
  });
});

describe("collectLicenses", () => {
  it("本文の無い MIT のパッケージは定型文で補い、印を付ける。名前の順に並べ、同じ版は 1 件にする", () => {
    const dir = mkdtempSync(join(tmpdir(), "jinji-licenses-"));
    const make = (name, pkg, license) => {
      const p = join(dir, "node_modules", name);
      mkdirSync(p, { recursive: true });
      writeFileSync(join(p, "package.json"), JSON.stringify({ name, version: "1.0.0", ...pkg }));
      if (license) writeFileSync(join(p, "LICENSE"), license);
      return p;
    };
    const b = make("b-lib", { license: "ISC" }, "ISC License\n\nCopyright (c) 2020 B\n");
    const a = make("a-lib", { license: "MIT", author: { name: "A Author" } });
    const list = collectLicenses([b, a, b]);
    expect(list.map((e) => e.name)).toEqual(["a-lib", "b-lib"]);
    expect(list[0]).toMatchObject({ license: "MIT", copyright: ["Copyright (c) A Author"], textGenerated: true });
    expect(list[0].text).toContain("Permission is hereby granted");
    expect(list[1]).toMatchObject({ license: "ISC", copyright: ["Copyright (c) 2020 B"], textGenerated: false });
  });

  it("web の dependencies をたどると、フォント（OFL）と React が入り、ビルド用の道具は入らない", () => {
    const list = collectLicenses(dependencyRoots(root));
    const names = list.map((e) => e.name);
    expect(names).toContain("react");
    expect(names).toContain("scheduler");
    expect(list.find((e) => e.name === "@fontsource/biz-udpgothic")?.license).toBe("OFL-1.1");
    expect(names).not.toContain("vite");
    expect(names).not.toContain("typescript");
    for (const e of list) expect(e.text, e.name).toBeTruthy();
  });

  it("バンドルのモジュールの ID から、仮想モジュールの出どころ（vite）を含めて集める", () => {
    const roots = rootsOfModules([`${root}node_modules/react/index.js`, "\0vite/preload-helper.js", `${root}src/App.tsx`], root);
    expect(collectLicenses(roots).map((e) => e.name)).toEqual(["react", "vite"]);
  });
});
