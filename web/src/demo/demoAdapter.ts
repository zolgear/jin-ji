// デモアダプタ（架空データ）。DirectoryAdapter を API アダプタと同じ形で実装する。
// このモジュールとデモデータは、mode=demo のビルドと開発サーバーにだけ含まれる（vite.config.ts の __JINJI_DEMO__）。
import type { DirectoryAdapter } from "../data/types";
import demoData from "./demo-data.json";
import { createDemoCorrections } from "./demoCorrections";
import { DemoDirectory } from "./directory";
import { generateLargeDataset } from "./largeDataset";
import { findPeople } from "./search";
import type { DemoDataset } from "./model";

export const DEMO_LABEL = "デモアダプタ（架空データ）";

export function createDemoAdapterFrom(data: DemoDataset, label = DEMO_LABEL): DirectoryAdapter {
  const dir = new DemoDirectory(data);
  // API と同じく非同期で返す（呼び出し側の作りを揃えるため）
  const later = <T,>(fn: () => T): Promise<T> =>
    new Promise((resolve, reject) => {
      queueMicrotask(() => {
        try {
          resolve(fn());
        } catch (e) {
          reject(e);
        }
      });
    });
  return {
    kind: "demo",
    label,
    businessDate: dir.businessDate,
    milestones: data.meta.milestones,
    canViewFuture: false,
    corrections: createDemoCorrections(dir),
    getOrgTree: (asOf) => later(() => dir.orgTree(asOf)),
    getOrgStats: (asOf) => later(() => dir.stats(asOf)),
    listOrgMembers: (orgId, q) =>
      later(() => dir.page(dir.members(orgId, q.asOf, q.includeDescendants ?? false), q.asOf, q.cursor, Math.min(q.limit ?? 50, 100))),
    getPerson: (id, asOf) => later(() => dir.getPerson(id, asOf)),
    getManagers: (id, asOf) => later(() => dir.getManagers(id, asOf)),
    getPeopleByIds: (ids, asOf) => later(() => dir.peopleByIds(ids, asOf)),
    searchOrgs: (query, q) => later(() => dir.searchOrgs(query, q.asOf, q.limit)),
    searchPeople: (query, q) => later(() => dir.page(dir.search(query, q.asOf), q.asOf, q.cursor, Math.min(q.limit ?? 50, 100))),
    findPeople: (q) => later(() => findPeople(dir, q)),
    listSites: (q) => later(() => dir.sites(q)),
    getSite: (id, asOf) => later(() => dir.site(id, asOf)),
  };
}

/** variant: "sample"（messy-corp 由来、350 人）/ "large"（合成 1 万人）。 */
export function createDemoAdapter(variant: "sample" | "large" = "sample"): DirectoryAdapter {
  if (variant === "large") {
    return createDemoAdapterFrom(generateLargeDataset(), `${DEMO_LABEL}・合成 1 万人`);
  }
  return createDemoAdapterFrom(demoData as DemoDataset);
}
