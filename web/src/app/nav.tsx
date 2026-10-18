// 画面の状態（時点・選択中の組織と人・画面）を URL に持ち、各画面から使う。
import { createContext, useContext, useMemo, type ReactNode } from "react";
import { useAdapter } from "../data/adapterContext";
import { isIsoDate } from "../lib/date";
import { useRoute, type View } from "../lib/router";

export interface Nav {
  view: View;
  asOf: string;
  orgId: string | null;
  personId: string | null;
  goView: (view: View) => void;
  setAsOf: (asOf: string) => void;
  selectOrg: (orgId: string | null) => void;
  openPerson: (personId: string | null) => void;
  /** 他の画面から組織を選んで組織図・3D に移る。 */
  showOrgIn: (view: View, orgId: string) => void;
  /** 拠点の人を、検索の画面で拠点ごとにまとめて見る。 */
  showSitePeople: (siteId: string) => void;
}

const NavContext = createContext<Nav | null>(null);

export function NavProvider({ children }: { children: ReactNode }) {
  const { adapter } = useAdapter();
  const { route, navigate } = useRoute();
  const rawAsOf = route.params.get("as_of");
  const asOf = rawAsOf && isIsoDate(rawAsOf) ? rawAsOf : adapter.businessDate;
  const orgId = route.params.get("org");
  const personId = route.params.get("person");

  const nav = useMemo<Nav>(
    () => ({
      view: route.view,
      asOf,
      orgId,
      personId,
      goView: (view) => navigate({ view }),
      setAsOf: (value) => navigate({ params: { as_of: value === adapter.businessDate ? null : value } }, true),
      selectOrg: (id) => navigate({ params: { org: id, person: null } }),
      openPerson: (id) => navigate({ params: { person: id } }),
      showOrgIn: (view, id) => navigate({ view, params: { org: id, person: null } }),
      // 引数の名前は検索の画面の URL（features/search/searchState.ts の site・group）
      showSitePeople: (id) => navigate({ view: "search", params: { site: id, group: "site", org: null, person: null } }),
    }),
    [route.view, asOf, orgId, personId, navigate, adapter.businessDate],
  );
  return <NavContext.Provider value={nav}>{children}</NavContext.Provider>;
}

export function useNav(): Nav {
  const nav = useContext(NavContext);
  if (!nav) throw new Error("NavProvider の外で useNav が呼ばれました");
  return nav;
}
