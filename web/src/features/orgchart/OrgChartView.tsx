// 組織図（React Flow）。法人ごとの組織ツリーを描き、兼務・出向は組織の間の線で区別する。
// 向きは「上から下」（既定。親が上、配下がその下に横一列）と「左から右」を切り替えられる。切り替えは URL（dir=lr）に持つ。
// 時点を切り替えると、配置・人数の変化を補間して遷移する（prefers-reduced-motion では即時）。
//
// 兼務・出向の線は、既定では「指している（ホバー・フォーカス）か選んでいる組織」に関わるものだけを出す。
// すべてを常に出すと、弧が札の右側で束になって交差し、人数のラベルも重なって読めないため。
// すべての線を見たいときは「すべて」に切り替える（ラベルはそのときも指している組織の分だけ）。
// 同じ内容は「一覧表」でも読める（色や図に頼らない代替）。
//
// 「拠点で分ける」（URL の split=site。lib/siteSplit.ts）では、拠点ごとの区画の枠（SiteGroupNode）の中に、その拠点の組織だけで木を描く。
// 上位の組織が別の拠点にある組織は区画の中で根になり、札に「上位」を添える。拠点をまたぐ親子の線は、
// 常に出すと区画をまたいで図を横切るので、指している・選んでいる組織に関わるものだけを細い破線で出す。
import {
  Background,
  BackgroundVariant,
  Controls,
  MarkerType,
  MiniMap,
  ReactFlow,
  ReactFlowProvider,
  getViewportForBounds,
  useReactFlow,
  useUpdateNodeInternals,
  type Edge,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";
import { useReducedMotion } from "motion/react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useOrgStats, useOrgTree, usePersonNames } from "../../app/hooks";
import { useNav } from "../../app/nav";
import { HelpTip } from "../../components/popover";
import { ErrorNote, Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { describeError, type OrgStats } from "../../data/types";
import { ancestorsOf, buildForest, type OrgForest } from "../../lib/orgTree";
import { assignSiteColors, siteColorVar } from "../../lib/siteColors";
import { splitBySite } from "../../lib/siteSplit";
import { useResolvedTheme } from "../../lib/theme";
import { useRoute } from "../../lib/router";
import {
  DEFAULT_DIRECTION,
  NODE_HEIGHT,
  NODE_WIDTH,
  isOpen,
  labelPlacement,
  layoutChart,
  layoutSplitChart,
  type ChartDirection,
  type ChartLayout,
  type PlacedGroup,
} from "./layout";
import { CrossEdge } from "./CrossEdge";
import { OrgNodeCard, type OrgFlowNode } from "./OrgNodeCard";
import { OrgTable } from "./OrgTable";
import { useSiteColorMode, useSiteSplitMode } from "./SiteLegend";
import { ChartToolbar, ChartToolbarEnd, CrossKindFilter, NoStatsBadge, SiteModeControls, ToolbarButton } from "./ChartToolbar";
import { SiteGroupNode, type SiteGroupFlowNode } from "./SiteGroupNode";
import { useTween } from "./useTween";

const nodeTypes = { org: OrgNodeCard, siteGroup: SiteGroupNode };

type LinkMode = "focus" | "all" | "none";
const edgeTypes = { cross: CrossEdge };

/** 組織図の向き（URL の dir。既定の上から下は URL に書かない）と、その切り替え。拠点の色分けと同じく、画面を移っても引き継ぐ。 */
function useChartDirection(): [ChartDirection, (next: ChartDirection) => void] {
  const { route, navigate } = useRoute();
  const direction: ChartDirection = route.params.get("dir") === "lr" ? "lr" : DEFAULT_DIRECTION;
  const set = useCallback(
    (next: ChartDirection) => navigate({ params: { dir: next === DEFAULT_DIRECTION ? null : next } }, true),
    [navigate],
  );
  return [direction, set];
}

/** 向きの切り替えのアイコン（札の並び方の略図）。 */
function DirectionIcon({ direction }: { direction: ChartDirection }) {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4 shrink-0" fill="none" stroke="currentColor" strokeWidth="1.3">
      {direction === "tb" ? (
        <>
          <rect x="5.5" y="1.5" width="5" height="3.5" rx="0.8" />
          <path d="M8 5v2.5M3.25 7.5h9.5M3.25 7.5V10M12.75 7.5V10M8 7.5V10" />
          <rect x="1" y="10.5" width="4.5" height="3.5" rx="0.8" />
          <rect x="5.75" y="10.5" width="4.5" height="3.5" rx="0.8" />
          <rect x="10.5" y="10.5" width="4.5" height="3.5" rx="0.8" />
        </>
      ) : (
        <>
          <rect x="1" y="6.25" width="4.5" height="3.5" rx="0.8" />
          <path d="M5.5 8H8M8 3.25V12.75M8 3.25h2.5M8 8h2.5M8 12.75h2.5" />
          <rect x="10.5" y="1.5" width="4.5" height="3.5" rx="0.8" />
          <rect x="10.5" y="6.25" width="4.5" height="3.5" rx="0.8" />
          <rect x="10.5" y="11" width="4.5" height="3.5" rx="0.8" />
        </>
      )}
    </svg>
  );
}

const DIRECTIONS = [
  ["tb", "上から下", "親の組織の下に、配下の組織を横一列に並べます（配下がすべて末端の組織なら縦に積みます）"],
  ["lr", "左から右", "親の組織の右に、配下の組織を縦に並べます"],
] as const;

/** 札の並び（と区画の枠）の外接矩形。 */
function boundsOf(layout: ChartLayout) {
  let [x0, y0, x1, y1] = [Infinity, Infinity, -Infinity, -Infinity];
  for (const n of layout.nodes) {
    x0 = Math.min(x0, n.x);
    x1 = Math.max(x1, n.x + NODE_WIDTH);
    y0 = Math.min(y0, n.y - NODE_HEIGHT / 2);
    y1 = Math.max(y1, n.y + NODE_HEIGHT / 2);
  }
  for (const g of layout.groups) {
    x0 = Math.min(x0, g.x);
    x1 = Math.max(x1, g.x + g.width);
    y0 = Math.min(y0, g.y);
    y1 = Math.max(y1, g.y + g.height);
  }
  return { x: x0, y: y0, width: x1 - x0, height: y1 - y0 };
}

/**
 * 前に表示した時点からの、組織ごとの主務の人数（配下を含む）の増減。閉じた組織でも配下の変化が分かるように配下を含める。
 * 取得元が変わったら数え直す。
 */
function useDeltas(forest: OrgForest | null, stats: OrgStats | null | undefined, adapterKey: object) {
  const [track, setTrack] = useState<{ owner: object; asOf: string | null; totals: Map<string, number>; deltas: Record<string, number> }>({
    owner: adapterKey,
    asOf: null,
    totals: new Map(),
    deltas: {},
  });
  // 描画中に前の値から導く（effect で setState しない）
  if (track.owner !== adapterKey) {
    setTrack({ owner: adapterKey, asOf: null, totals: new Map(), deltas: {} });
  } else if (forest && stats && stats.asOf !== track.asOf) {
    const totals = new Map([...forest.byId.values()].map((n) => [n.id, n.totalPrimary ?? 0]));
    const deltas: Record<string, number> = {};
    if (track.asOf !== null) {
      for (const id of new Set([...track.totals.keys(), ...totals.keys()])) {
        const d = (totals.get(id) ?? 0) - (track.totals.get(id) ?? 0);
        if (d !== 0) deltas[id] = d;
      }
    }
    setTrack({ owner: adapterKey, asOf: stats.asOf, totals, deltas });
  }
  return track.deltas;
}

function Chart() {
  const { adapter } = useAdapter();
  const { asOf, orgId, selectOrg } = useNav();
  const theme = useResolvedTheme();
  const reduced = useReducedMotion();
  const tree = useOrgTree(asOf);
  const stats = useOrgStats(asOf);
  const [expanded, setExpanded] = useState<Map<string, boolean>>(new Map());
  const [showConcurrent, setShowConcurrent] = useState(true);
  const [showSecondment, setShowSecondment] = useState(true);
  const [linkMode, setLinkMode] = useState<LinkMode>("focus");
  const [display, setDisplay] = useState<"chart" | "table">("chart");
  const [direction, setDirection] = useChartDirection();
  const [hoveredId, setHoveredId] = useState<string | null>(null);
  // 線を出す対象: 指している組織を優先し、無ければ選んでいる組織
  const activeId = hoveredId ?? orgId;
  const flow = useReactFlow();
  const forest = useMemo(() => (tree.data ? buildForest(tree.data, stats.data ?? null) : null), [tree.data, stats.data]);
  // 拠点で分ける（区画の中だけで組み直した木を描く。開閉・祖先を開く・線の寄せ先はこの木の上で決める）
  const [splitMode, setSplitMode] = useSiteSplitMode();
  const split = useMemo(() => (forest && splitMode ? splitBySite(forest) : null), [forest, splitMode]);
  const viewForest = split?.forest ?? forest;
  // 増減は、組織ツリーと集計の両方が今の時点のものになってから数える。配下を含めた人数は区画の中で数え直すので、
  // 分ける・分けないを切り替えたら数え直す（切り替えの前後を比べない）
  const fresh = !tree.stale && !stats.stale && !tree.loading && !stats.loading;
  const deltaOwner = useMemo(() => ({ adapter, split: splitMode }), [adapter, splitMode]);
  const deltas = useDeltas(fresh ? viewForest : null, stats.data, deltaOwner);
  const statsAvailable = stats.data !== null && stats.data !== undefined;
  // 拠点で色分け（色の意味が所属の種類と重なるので、その間は兼務・出向の線を出さない）
  const [siteMode, setSiteMode] = useSiteColorMode();
  const coloring = useMemo(() => {
    if (!forest || !siteMode) return null;
    const codes = new Map(forest.roots.map((r) => [r.legalEntityId, r.code]));
    return assignSiteColors(forest, (id) => codes.get(id) ?? "");
  }, [forest, siteMode]);

  const links = useMemo(
    () =>
      (stats.data?.links ?? []).filter((l) => (l.kind === "concurrent" ? showConcurrent : showSecondment)),
    [stats.data, showConcurrent, showSecondment],
  );
  const layout = useMemo(
    () => (split ? layoutSplitChart(split, expanded, links, direction) : forest ? layoutChart(forest, expanded, links, direction) : null),
    [split, forest, expanded, links, direction],
  );

  // 組織が選ばれたら（検索・3D から来た場合を含む）、祖先を開いて中央に寄せる
  useEffect(() => {
    if (!viewForest || !orgId || !viewForest.byId.has(orgId)) return;
    const closed = ancestorsOf(viewForest, orgId).filter((a) => !isOpen(a, expanded));
    if (closed.length > 0) {
      setExpanded((m) => {
        const next = new Map(m);
        closed.forEach((a) => next.set(a.id, true));
        return next;
      });
    }
  }, [viewForest, orgId, expanded]);

  const placed = layout?.nodes.find((n) => n.node.id === orgId);
  useEffect(() => {
    if (!placed) return;
    void flow.setCenter(placed.x + NODE_WIDTH / 2, placed.y, { zoom: Math.max(flow.getZoom(), 0.85), duration: reduced ? 0 : 400 });
    // 選んだときだけ寄せる（配置の補間のたびには動かさない）
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [orgId, placed !== undefined]);

  const headIds = useMemo(
    () => (layout ? layout.nodes.map((n) => n.node.headPersonId).filter((v): v is string => v !== null) : []),
    [layout],
  );
  const headNames = usePersonNames(headIds, asOf);

  const selectNode = useCallback((id: string) => selectOrg(id.startsWith("le:") ? null : id), [selectOrg]);

  const toggle = useCallback(
    (id: string) => {
      setExpanded((m) => {
        const node = viewForest?.byId.get(id);
        if (!node) return m;
        const next = new Map(m);
        next.set(id, !isOpen(node, m));
        return next;
      });
    },
    [viewForest],
  );

  const target = useMemo(
    () =>
      layout?.nodes.map((p) => ({ id: p.node.id, x: p.x, y: p.y - NODE_HEIGHT / 2, data: p })) ?? [],
    [layout],
  );
  const parentOf = useCallback((id: string) => viewForest?.byId.get(id)?.parentId ?? null, [viewForest]);
  const frames = useTween(target, parentOf);
  // 区画の枠も同じ時間で位置と大きさを補間する（分ける・分けないの切り替え、開閉で区画が広がるとき）
  const groupTarget = useMemo(
    () => layout?.groups.map((g) => ({ id: `g:${g.partition.key}`, x: g.x, y: g.y, w: g.width, h: g.height, data: g })) ?? [],
    [layout],
  );
  const noParent = useCallback(() => null, []);
  const groupFrames = useTween<PlacedGroup>(groupTarget, noParent);
  const showEntity = (forest?.roots.length ?? 0) > 1;

  const nodes: Array<OrgFlowNode | SiteGroupFlowNode> = useMemo(() => {
    const groups: SiteGroupFlowNode[] = groupFrames.map((f) => ({
      id: f.id,
      type: "siteGroup",
      position: { x: f.x, y: f.y },
      width: f.w,
      height: f.h,
      measured: { width: f.w, height: f.h },
      draggable: false,
      connectable: false,
      selectable: false,
      focusable: false,
      // 札と線の後ろに敷く
      zIndex: -1,
      style: { opacity: f.opacity },
      ariaLabel: undefined,
      data: {
        partition: f.data.partition,
        color: coloring && f.data.partition.site ? siteColorVar(coloring, f.data.partition.site.id) : null,
        showEntity,
        width: f.w ?? f.data.width,
        height: f.h ?? f.data.height,
      },
    }));
    const cards: OrgFlowNode[] = frames.map((f) => ({
        id: f.id,
        type: "org",
        position: { x: f.x, y: f.y },
        // 札の大きさは固定なので、計測を待たずに寸法を渡す（制御された nodes でも計測待ちで非表示にならないように）
        width: NODE_WIDTH,
        height: NODE_HEIGHT,
        measured: { width: NODE_WIDTH, height: NODE_HEIGHT },
        draggable: false,
        connectable: false,
        selectable: !f.exiting,
        selected: f.id === orgId,
        style: { opacity: f.opacity },
        ariaLabel: `${f.data.node.name}${f.data.node.levelName ? `（${f.data.node.levelName}）` : ""}`,
        data: {
          node: f.data.node,
          headName: f.data.node.headPersonId ? headNames.get(f.data.node.headPersonId) : null,
          hiddenChildren: f.data.hiddenChildren,
          open: isOpen(f.data.node, expanded),
          delta: deltas[f.id] ?? 0,
          statsAvailable,
          exiting: f.exiting,
          onToggle: toggle,
          onSelect: selectNode,
          onHover: setHoveredId,
          // 拠点で分けているときは、拠点は区画の見出しと枠の色で分かるので、札の拠点の行と色の帯は出さない（組織長の行に戻す）
          siteMode: siteMode && !split,
          siteColor: coloring && !split ? siteColorVar(coloring, f.data.node.site?.id) : null,
          direction,
          upper: split?.upperOf.get(f.id) ?? null,
        },
      }));
    return [...groups, ...cards];
  }, [frames, groupFrames, orgId, headNames, expanded, deltas, statsAvailable, toggle, selectNode, siteMode, coloring, direction, split, showEntity]);

  // 向きを切り替えたら、札の線の出入り口の位置を測り直す（札の大きさは変わらないので、自動では測り直されない）
  const updateNodeInternals = useUpdateNodeInternals();
  const measuredDirection = useRef(direction);
  useEffect(() => {
    if (measuredDirection.current === direction) return;
    measuredDirection.current = direction;
    updateNodeInternals(nodes.map((n) => n.id));
  }, [direction, nodes, updateNodeInternals]);

  const edges: Edge[] = useMemo(() => {
    if (!layout) return [];
    const present = new Set(frames.filter((f) => !f.exiting).map((f) => f.id));
    const treeEdges: Edge[] = layout.treeEdges
      .filter((e) => present.has(e.from) && present.has(e.to))
      .map((e) => ({
        id: e.id,
        source: e.from,
        target: e.to,
        // 縦に積んだ子へは、親の下の幹から子の左へ入る
        ...(e.stacked ? { sourceHandle: "stack-out", targetHandle: "stack-in" } : {}),
        type: "smoothstep",
        selectable: false,
        focusable: false,
      }));
    // 拠点をまたぐ親子（区画の根と本来の上位）。指している・選んでいる組織に関わるものだけを細い破線で出す。
    // 上位が閉じた枝の中にあれば、見えている最も近い祖先（区画の中）から引く
    const upperEdges: Edge[] = [];
    if (split && activeId) {
      const visibleAncestor = (id: string) => {
        let current = split.forest.byId.get(id);
        while (current && !present.has(current.id)) current = current.parentId ? split.forest.byId.get(current.parentId) : undefined;
        return current?.id ?? null;
      };
      for (const [rootId, upper] of split.upperOf) {
        if (!present.has(rootId)) continue;
        const from = visibleAncestor(upper.id);
        if (!from || (rootId !== activeId && from !== activeId && upper.id !== activeId)) continue;
        upperEdges.push({
          id: `u:${upper.id}>${rootId}`,
          source: from,
          target: rootId,
          type: "default",
          className: "edge-upper",
          selectable: false,
          focusable: false,
          zIndex: 0,
        });
      }
    }
    const nameOf = (id: string) => forest?.byId.get(id)?.name ?? "";
    // 同じ相手との行きと帰り（兼務先と兼務元など）は、ラベルが相手の札の同じ位置に重なるので 1 つにまとめる
    const partnerLabels = new Map<string, string[]>();
    if (activeId) {
      for (const e of layout.crossEdges) {
        if (e.from !== activeId && e.to !== activeId) continue;
        const partner = e.from === activeId ? e.to : e.from;
        const word = e.kind === "concurrent" ? "兼務" : "出向";
        const key = `${e.kind}:${partner}`;
        const list = partnerLabels.get(key) ?? [];
        // 先（送り出し）を前に置く
        const text = `${word}${e.from === activeId ? "先" : "元"} ${e.count}名`;
        if (e.from === activeId) list.unshift(text);
        else list.push(text);
        partnerLabels.set(key, list);
      }
    }
    const labelled = new Set<string>();
    const placedById = new Map(layout.nodes.map((n) => [n.node.id, n]));
    const cross: Edge[] =
      linkMode === "none" || siteMode
        ? []
        : layout.crossEdges
            .filter((e) => present.has(e.from) && present.has(e.to))
            .filter((e) => linkMode === "all" || e.from === activeId || e.to === activeId)
            .map((e) => {
              const touches = e.from === activeId || e.to === activeId;
              const word = e.kind === "concurrent" ? "兼務" : "出向";
              // ラベルは相手の組織の側に置く（active が主務の側なら相手は兼務先・出向先）
              const partnerKey = `${e.kind}:${e.from === activeId ? e.to : e.from}`;
              const first = touches && !labelled.has(partnerKey);
              if (first) labelled.add(partnerKey);
              const labelAt = !first ? null : e.from === activeId ? "target" : "source";
              const label = (partnerLabels.get(partnerKey) ?? []).join("・");
              const partnerPlaced = labelAt ? placedById.get(labelAt === "target" ? e.to : e.from) : undefined;
              const placement = partnerPlaced
                ? labelPlacement(partnerPlaced, e.kind, label, layout.nodes, layout.direction)
                : { side: "right" as const, dx: 0, dy: 0 };
              const color = e.kind === "concurrent" ? "var(--concurrent)" : "var(--secondment)";
              return {
                id: e.id,
                source: e.from,
                target: e.to,
                sourceHandle: `cross-out-${e.kind}`,
                targetHandle: `cross-in-${e.kind}`,
                type: "cross",
                className: e.kind === "concurrent" ? "edge-concurrent" : "edge-secondment",
                markerEnd: { type: MarkerType.ArrowClosed, color, width: 14, height: 14 },
                data: {
                  kind: e.kind,
                  count: e.count,
                  labelAt,
                  label,
                  placement,
                  direction: layout.direction,
                  // 閉じた組織の線は配下の分をまとめている（詳細パネルの「配下を含む」と同じ数）
                  description: `${nameOf(e.from)}${e.aggregated ? "（配下を含む）" : ""}に主務がある ${e.count}名が、${nameOf(e.to)}${e.aggregated ? "（配下を含む）" : ""}に${word}`,
                },
                selectable: false,
                focusable: false,
                // 上から下では、弧が上の段の札（法人など）を横切るので、札の下に描いて札の文字を隠さない
                zIndex: layout.direction === "tb" ? 0 : 1,
                // 「すべて」で組織を指しているときは、その組織に関わらない線を薄くする
                style: { opacity: !activeId || touches ? 1 : 0.18 },
              } satisfies Edge;
            });
    return [...treeEdges, ...upperEdges, ...cross];
  }, [layout, frames, activeId, linkMode, forest, siteMode, split]);

  // 初回・取得元の切り替え・向きの切り替えのときだけ全体を表示する（開閉のたびには動かさない）。
  // 向きを切り替えたときは配置が補間の途中なので、描いている札ではなく新しい配置から表示の範囲を決める。
  // 組織を選んでいれば、全体ではなくその組織を中央に寄せる
  const fitted = useRef<{ adapter: object; direction: ChartDirection; split: boolean } | null>(null);
  const paneRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!layout || layout.nodes.length === 0) return;
    const last = fitted.current;
    if (last && last.adapter === adapter && last.direction === direction && last.split === splitMode) return;
    fitted.current = { adapter, direction, split: splitMode };
    const selected = layout.nodes.find((n) => n.node.id === orgId);
    if (!last || last.adapter !== adapter) {
      // 選んだ組織があれば、上の effect が中央に寄せるので全体の表示で上書きしない
      if (!selected) requestAnimationFrame(() => void flow.fitView({ padding: 0.15, duration: 0, minZoom: 0.45, maxZoom: 1 }));
      return;
    }
    const duration = reduced ? 0 : 400;
    if (selected) {
      void flow.setCenter(selected.x + NODE_WIDTH / 2, selected.y, { zoom: flow.getZoom(), duration });
      return;
    }
    const el = paneRef.current;
    if (!el || el.clientWidth === 0) return;
    void flow.setViewport(getViewportForBounds(boundsOf(layout), el.clientWidth, el.clientHeight, 0.45, 1, 0.15), { duration });
  }, [layout, adapter, direction, splitMode, orgId, flow, reduced]);

  if (tree.error && !tree.data) {
    return (
      <div className="p-4">
        <ErrorNote>組織ツリーを取得できませんでした: {describeError(tree.error)}</ErrorNote>
      </div>
    );
  }

  return (
    <div className="flex size-full flex-col">
      {/* 操作の帯（3D と共通の部品。ChartToolbar.tsx）。説明の文は「図の見方」（?）の箱に入れる */}
      <ChartToolbar>
        <fieldset className="m-0 flex overflow-hidden rounded-md border border-border p-0">
          <legend className="sr-only">表示の形</legend>
          {(
            [
              ["chart", "組織図"],
              ["table", "一覧表"],
            ] as const
          ).map(([v, label]) => (
            <button
              key={v}
              type="button"
              aria-pressed={display === v}
              onClick={() => setDisplay(v)}
              className={`h-8 px-3 ${display === v ? "bg-accent font-bold text-accent-fg" : "hover:bg-surface-2"}`}
            >
              {label}
            </button>
          ))}
        </fieldset>
        {display === "chart" && (
          <>
            <fieldset className="m-0 flex overflow-hidden rounded-md border border-border p-0">
              <legend className="sr-only">組織図の向き</legend>
              {DIRECTIONS.map(([v, label, title]) => (
                <button
                  key={v}
                  type="button"
                  aria-pressed={direction === v}
                  onClick={() => setDirection(v)}
                  title={title}
                  className={`inline-flex h-8 items-center gap-1.5 px-3 ${direction === v ? "bg-accent font-bold text-accent-fg" : "hover:bg-surface-2"}`}
                >
                  <DirectionIcon direction={v} />
                  {label}
                </button>
              ))}
            </fieldset>
            <SiteModeControls split={splitMode} onSplit={setSplitMode} color={siteMode} onColor={setSiteMode} coloring={coloring} />
            <CrossKindFilter
              concurrent={showConcurrent}
              onConcurrent={setShowConcurrent}
              secondment={showSecondment}
              onSecondment={setShowSecondment}
              available={statsAvailable}
              hidden={siteMode}
            >
              <label className="flex items-center gap-1.5">
                <span className="text-muted">線</span>
                <select
                  value={linkMode}
                  disabled={!statsAvailable}
                  onChange={(e) => setLinkMode(e.target.value as LinkMode)}
                  className="h-8 rounded-md border border-border bg-surface px-2"
                >
                  <option value="focus">指した・選んだ組織の分</option>
                  <option value="all">すべて</option>
                  <option value="none">表示しない</option>
                </select>
              </label>
            </CrossKindFilter>
          </>
        )}
        {(tree.loading || stats.loading) && <Spinner />}
        {!statsAvailable && !stats.loading && <NoStatsBadge title="集計の API が未提供のため、人数と兼務・出向の線を表示していません" />}
        {display === "chart" && (
          <ChartToolbarEnd>
            <ToolbarButton
              onClick={() => {
                if (!forest) return;
                setExpanded(new Map([...forest.byId.keys()].map((id) => [id, true])));
              }}
            >
              すべて開く
            </ToolbarButton>
            <ToolbarButton onClick={() => setExpanded(new Map())}>既定に戻す</ToolbarButton>
            <HelpTip label="図の見方">
              <p className="m-0 flex items-center gap-2">
                <svg aria-hidden="true" width="18" height="8" className="line-key">
                  <path d="M2 4h14" stroke="var(--edge)" strokeWidth="1.6" />
                </svg>
                灰色の線は組織の上下です。
              </p>
              <p className="m-0">「上から下」では親の組織の下に配下を横一列に並べ、配下がすべて末端の組織なら縦に積みます。「左から右」では右へ並べます。</p>
              <p className="m-0">組織を指すか選ぶと、兼務・出向の相手先を色の線で示します（「線」で、すべて表示・表示しないに切り替えられます）。</p>
              <p className="m-0">組織を閉じているときは、その線に配下の組織の分をまとめて描きます（配下どうしの分は線にしません）。</p>
              <p className="m-0">拠点で色分けしている間は、兼務・出向の線を出しません（色の意味が重なるため）。色は人数の多い 3 拠点までで、ほかは灰色の「その他」です。</p>
              <p className="m-0">
                「拠点で分ける」では、拠点ごとの枠の中に、その拠点の組織だけで木を描きます（法人の順、その中で人数の多い順）。上位の組織が別の拠点にある組織は枠の中で一番上に置き、札に「上位」を示します。指すか選ぶと、本来の上位との間を細い破線で結びます。配下を含めた人数は、その拠点の中の配下だけで数えます。
              </p>
              {!statsAvailable && <p className="m-0 text-muted">人数と兼務・出向の線は、集計の API が未提供のため表示していません。</p>}
            </HelpTip>
          </ChartToolbarEnd>
        )}
      </ChartToolbar>
      <div className="relative min-h-0 flex-1">
        {display === "table" && forest && (
          <OrgTable forest={forest} links={stats.data?.links ?? []} statsAvailable={statsAvailable} selectedId={orgId} onSelect={selectNode} />
        )}
        <div ref={paneRef} className={display === "table" ? "hidden" : "size-full"}>
          <ReactFlow
            nodes={nodes}
            edges={edges}
            nodeTypes={nodeTypes}
            edgeTypes={edgeTypes}
            colorMode={theme}
            minZoom={0.15}
            maxZoom={1.8}
            nodesDraggable={false}
            nodesConnectable={false}
            elementsSelectable
            onlyRenderVisibleElements
            onNodeClick={(_, node) => selectNode(node.id)}
            onNodeMouseEnter={(_, node) => setHoveredId(node.id)}
            onNodeMouseLeave={() => setHoveredId(null)}
            onPaneClick={() => undefined}
            aria-label="組織図"
            proOptions={{ hideAttribution: false }}
          >
            <Background variant={BackgroundVariant.Dots} gap={20} size={1} color="var(--border)" />
            <Controls showInteractive={false} position="bottom-left" aria-label="表示の拡大・縮小" />
            <MiniMap
              pannable
              zoomable
              className="!hidden md:!block"
              nodeColor={(n) => {
                if (String(n.id).startsWith("le:")) return "var(--accent)";
                if (String(n.id).startsWith("g:")) return "color-mix(in srgb, var(--surface-2) 60%, transparent)";
                if (!coloring) return "var(--surface-2)";
                return siteColorVar(coloring, forest?.byId.get(String(n.id))?.site?.id) ?? "var(--surface-2)";
              }}
              nodeStrokeColor="var(--border)"
              maskColor="color-mix(in srgb, var(--bg) 70%, transparent)"
              ariaLabel="組織図の全体図"
            />
          </ReactFlow>
        </div>
      </div>
    </div>
  );
}

export function OrgChartView() {
  return (
    <ReactFlowProvider>
      <Chart />
    </ReactFlowProvider>
  );
}
