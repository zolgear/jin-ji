// 組織の詳細と所属者の一覧。所属者は API の /orgs/{id}/members（ページ送り）で取る。
import { useEffect, useMemo, useRef, useState } from "react";
import { useOrgStats, useOrgTree, usePersonNames } from "../../app/hooks";
import { useNav } from "../../app/nav";
import { HelpTip } from "../../components/popover";
import { SiteIcon, SiteValue } from "../../components/site";
import { Badge, ErrorNote, LineKey, personKana, personName, Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { describeError, type OrgLink, type Person } from "../../data/types";
import { ancestorsOf, buildForest, crossSummary, type CrossCounts, type OrgForest } from "../../lib/orgTree";

const PAGE = 50;

function useMembers(orgId: string, asOf: string, includeDescendants: boolean) {
  const { adapter } = useAdapter();
  const scope = `${orgId}|${asOf}|${includeDescendants}`;
  const [request, setRequest] = useState<{ scope: string; cursor: string | null }>({ scope, cursor: null });
  const cursor = request.scope === scope ? request.cursor : null;
  const [state, setState] = useState<{ items: Person[]; next: string | null; loading: boolean; error: unknown }>({
    items: [],
    next: null,
    loading: true,
    error: undefined,
  });
  useEffect(() => {
    let cancelled = false;
    setState((s) => ({ items: cursor ? s.items : [], next: null, loading: true, error: undefined }));
    adapter.listOrgMembers(orgId, { asOf, includeDescendants, cursor, limit: PAGE }).then(
      (r) => {
        if (!cancelled) setState((s) => ({ items: cursor ? [...s.items, ...r.items] : r.items, next: r.nextCursor, loading: false, error: undefined }));
      },
      (error: unknown) => {
        if (!cancelled) setState((s) => ({ ...s, loading: false, error }));
      },
    );
    return () => {
      cancelled = true;
    };
    // scope に orgId・asOf・includeDescendants を含む
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [adapter, scope, cursor]);
  return { ...state, cursor: state.next, loadMore: () => state.next && setRequest({ scope, cursor: state.next }) };
}

function MemberRow({ person, orgId, isHead }: { person: Person; orgId: string; isHead: boolean }) {
  const { openPerson } = useNav();
  const here = person.assignments?.filter((a) => a.org_unit_id === orgId) ?? [];
  const elsewhere = here.length === 0 ? person.assignments?.find((a) => a.type === "primary") : undefined;
  const title = here.find((a) => a.position_title)?.position_title ?? null;
  const kana = personKana(person);
  return (
    <li>
      <button
        type="button"
        onClick={() => openPerson(person.id)}
        className="flex w-full items-center gap-2 border-b border-border/70 px-1 py-1.5 text-left hover:bg-surface-2"
      >
        <span className="min-w-0 flex-1">
          <span className="flex flex-wrap items-center gap-1.5">
            <span className="font-bold">{personName(person)}</span>
            {title && <span className="text-xs">{title}</span>}
            {isHead && <Badge tone="accent">組織長</Badge>}
            {here.some((a) => a.type === "concurrent" && a.secondment === "none") && <Badge tone="concurrent">兼務</Badge>}
            {here.some((a) => a.secondment !== "none") && <Badge tone="secondment">出向</Badge>}
            {person.account_status === "suspended" && <Badge tone="warn">一時停止中</Badge>}
          </span>
          {(kana || elsewhere) && (
            <span className="block truncate text-xs text-muted">
              {kana}
              {elsewhere ? `${kana ? "・" : ""}${elsewhere.org_unit_name}` : ""}
            </span>
          )}
        </span>
        <span aria-hidden="true" className="text-muted">
          ›
        </span>
      </button>
    </li>
  );
}

const CROSS_ROWS: Array<{ key: keyof CrossCounts; kind: "concurrent" | "secondment"; label: string }> = [
  { key: "concurrentIn", kind: "concurrent", label: "兼務で受け入れ" },
  { key: "concurrentOut", kind: "concurrent", label: "兼務で送り出し" },
  { key: "secondmentIn", kind: "secondment", label: "出向で受け入れ" },
  { key: "secondmentOut", kind: "secondment", label: "出向で送り出し" },
];

/**
 * 兼務・出向の人数。「この組織に直接」と「配下を含む」を分けて示す。説明は見出しの横の「i」に収める。
 */
function CrossTable({ forest, links, orgId, hasChildren }: { forest: OrgForest; links: OrgLink[]; orgId: string; hasChildren: boolean }) {
  const summary = useMemo(() => crossSummary(forest, links, orgId), [forest, links, orgId]);
  const cell = (n: number) => (
    <td className={`tabular py-1 pl-2 text-right ${n === 0 ? "text-muted" : "font-bold"}`}>{n}名</td>
  );
  const { internal } = summary;
  return (
    <section aria-labelledby="org-cross" className="text-xs">
      <table className="w-full border-collapse">
        <caption className="mb-1 text-left font-bold text-muted">
          <span className="flex items-center gap-0.5">
            <span id="org-cross">兼務・出向</span>
            <HelpTip label="兼務・出向の人数について" icon="info" width={280}>
              <p className="m-0">
                <b>受け入れ</b>: 主務は他の組織で、この組織でも兼務・出向している人
              </p>
              <p className="m-0">
                <b>送り出し</b>: この組織が主務で、他の組織で兼務・出向している人
              </p>
              <p className="m-0">
                <b>この組織に直接</b>: この組織だけの人数
              </p>
              {hasChildren && (
                <p className="m-0">
                  <b>配下を含む</b>: 配下の組織も合わせた人数。配下の組織どうしの分は数えず、外の組織との間だけ数えます
                  {internal.concurrent + internal.secondment > 0 &&
                    `（配下どうし: ${[internal.concurrent > 0 ? `兼務 ${internal.concurrent}名` : "", internal.secondment > 0 ? `出向 ${internal.secondment}名` : ""].filter(Boolean).join("・")}）`}
                  。
                </p>
              )}
            </HelpTip>
          </span>
        </caption>
        <thead>
          <tr className="border-b border-border text-muted">
            <th scope="col" className="py-1 text-left font-normal">
              <span className="sr-only">種類</span>
            </th>
            <th scope="col" className="py-1 pl-2 text-right font-normal">この組織に直接</th>
            {hasChildren && (
              <th scope="col" className="py-1 pl-2 text-right font-normal">
                配下を含む
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          {CROSS_ROWS.map((r) => (
            <tr key={r.key} className="border-b border-border/60">
              <th scope="row" className="py-1 text-left font-normal">
                <span className="flex items-center gap-1">
                  <LineKey kind={r.kind} width={14} />
                  {r.label}
                </span>
              </th>
              {cell(summary.direct[r.key])}
              {hasChildren && cell(summary.subtree[r.key])}
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

export function OrgPanel({ orgId, onClose }: { orgId: string; onClose: () => void }) {
  const { asOf, selectOrg, openPerson, showSitePeople } = useNav();
  const tree = useOrgTree(asOf);
  const stats = useOrgStats(asOf);
  const [includeDescendants, setIncludeDescendants] = useState(false);
  const forest = useMemo(() => (tree.data ? buildForest(tree.data, stats.data ?? null) : null), [tree.data, stats.data]);
  const node = forest?.byId.get(orgId);
  const members = useMembers(orgId, asOf, includeDescendants);
  const heads = usePersonNames(node?.headPersonId ? [node.headPersonId] : [], asOf);
  const heading = useRef<HTMLHeadingElement>(null);
  // 開いたら見出しへ移る（組織が変わると key で作り直される）
  useEffect(() => {
    heading.current?.focus();
  }, []);

  if (tree.data && !node) {
    return (
      <div className="flex flex-col gap-3">
        <ErrorNote>表示している日付にはこの組織がありません（廃止・統合された可能性があります）。</ErrorNote>
        <button type="button" onClick={onClose} className="self-start rounded-sm border border-border px-2 py-0.5 text-xs hover:bg-surface-2">
          閉じる
        </button>
      </div>
    );
  }
  if (!node || !forest) return <Spinner />;
  const path = ancestorsOf(forest, node.id).reverse();
  const headName = node.headPersonId ? heads.get(node.headPersonId) : null;

  return (
    <div className="flex flex-col gap-3">
      <div className="flex items-start justify-between gap-2">
        <div className="min-w-0">
          <nav aria-label="上位組織" className="text-xs text-muted">
            {path.map((a, i) => (
              <span key={a.id}>
                {i > 0 && <span aria-hidden="true"> › </span>}
                {a.kind === "org" ? (
                  <button type="button" className="hover:underline" onClick={() => selectOrg(a.id)}>
                    {a.name}
                  </button>
                ) : (
                  a.name
                )}
              </span>
            ))}
          </nav>
          <h2 ref={heading} tabIndex={-1} className="text-xl font-bold outline-none">
            {node.name}
          </h2>
          <div className="flex flex-wrap gap-x-3 text-xs text-muted">
            {node.levelName && <span>段: {node.levelName}</span>}
            <span>組織コード: {node.code || "未設定"}</span>
          </div>
        </div>
        <button type="button" onClick={onClose} className="shrink-0 rounded-sm border border-border px-2 py-0.5 text-xs hover:bg-surface-2">
          閉じる
        </button>
      </div>

      <dl className="m-0 grid grid-cols-2 gap-2 text-xs">
        <div className="col-span-2 rounded-sm border border-border px-2 py-1.5">
          <dt className="text-muted">組織長</dt>
          <dd className="m-0 text-sm font-bold">
            {node.headPersonId ? (
              <button type="button" className="text-accent hover:underline" onClick={() => openPerson(node.headPersonId!)}>
                {headName ?? "…"}
              </button>
            ) : (
              <span className="font-normal text-muted">— 未設定または非公開</span>
            )}
          </dd>
        </div>
        <div className="col-span-2 rounded-sm border border-border px-2 py-1.5">
          <dt className="flex items-center gap-1 text-muted">
            <SiteIcon className="size-3" />
            拠点
          </dt>
          <dd className="m-0 text-sm">
            {node.site ? (
              <span className="flex flex-wrap items-baseline justify-between gap-x-2 gap-y-0.5">
                <SiteValue site={node.site} inheritedNote="上位組織から引き継ぎ" />
                <button type="button" className="text-xs text-accent hover:underline" onClick={() => showSitePeople(node.site!.id)}>
                  この拠点の人を見る
                </button>
              </span>
            ) : (
              <span className="text-muted">— 未設定</span>
            )}
          </dd>
        </div>
        {node.headcount && (
          <>
            <div className="rounded-sm border border-border px-2 py-1.5">
              <dt className="text-muted">直属</dt>
              <dd className="tabular m-0 text-sm font-bold">{node.headcount.primary}名</dd>
            </div>
            <div className="rounded-sm border border-border px-2 py-1.5">
              <dt className="text-muted">所属（配下を含む）</dt>
              <dd className="tabular m-0 text-sm font-bold">{node.totalPrimary}名</dd>
            </div>
          </>
        )}
      </dl>

      {node.headcount && stats.data && <CrossTable forest={forest} links={stats.data.links} orgId={node.id} hasChildren={node.children.length > 0} />}

      {node.children.length > 0 && (
        <details className="text-xs">
          <summary className="cursor-pointer text-muted">配下の組織（{node.children.length}）</summary>
          <ul className="m-0 mt-1 list-none p-0">
            {node.children.map((c) => (
              <li key={c.id}>
                <button type="button" className="py-0.5 text-accent hover:underline" onClick={() => selectOrg(c.id)}>
                  {c.name}
                </button>
              </li>
            ))}
          </ul>
        </details>
      )}

      <section aria-labelledby="org-members">
        <div className="flex items-center justify-between gap-2">
          <h3 id="org-members" className="text-xs font-bold text-muted">
            所属者
          </h3>
          {node.children.length > 0 && (
            <label className="flex items-center gap-1 text-xs">
              <input type="checkbox" checked={includeDescendants} onChange={(e) => setIncludeDescendants(e.target.checked)} />
              配下の組織を含む
            </label>
          )}
        </div>
        {members.error ? (
          <ErrorNote>{describeError(members.error)}</ErrorNote>
        ) : (
          <>
            <ul className="m-0 list-none p-0" aria-busy={members.loading}>
              {members.items.map((p) => (
                <MemberRow key={p.id} person={p} orgId={orgId} isHead={p.id === node.headPersonId} />
              ))}
            </ul>
            {members.loading && <Spinner />}
            {!members.loading && members.items.length === 0 && <p className="text-muted">表示している日付の所属者はいません（または見える人がいません）。</p>}
            {members.cursor && !members.loading && (
              <button type="button" onClick={members.loadMore} className="mt-2 w-full rounded-sm border border-border py-1 text-xs hover:bg-surface-2">
                さらに読み込む
              </button>
            )}
          </>
        )}
      </section>
    </div>
  );
}
