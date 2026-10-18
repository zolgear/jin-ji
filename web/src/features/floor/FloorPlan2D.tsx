// 2D の簡易フロアマップ（SVG を React で直接描く。仕様 §5.4）。xyflow はノードと辺のためのもので、座標が固定の平面図には使わない。
//
// 要素: 外形（実線）・コア（斜線）・部屋（細線）・エリア（組織の色の淡い面と細い枠）・机（細線の矩形）・
// 人（組織の色の点）・空席（輪）・使えない席（淡い斜線）・選んだ人（控えめなアクセント色の輪と札）・今日の所在の人（点の右上の小さな菱形）。
// 1 m の格子をごく薄く。状態は形で区別し、色だけに頼らない。ズーム・パンは viewBox で行う（ホイール・ドラッグ・ピンチ・ボタン）。
// アクセシビリティ: 同じ内容の表（席番号・状態・人・組織）を下に置き、キーボードで席を順に辿れる。
import { useEffect, useId, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { chairPosition, deskSize, slotColorVar, type FloorView, type SeatView } from "./floorModel";
import { centerOn, fitViewport, panBy, toSvgY, viewBoxOf, zoomAt, type Viewport } from "./planViewport";

const STATE_LABEL = { person: "人", vacant: "空席", unavailable: "使えない席" } as const;

export interface FloorPlan2DProps {
  view: FloorView;
  /** 選んだ人（その人の席に印を付け、開いたときに寄せる）。 */
  selectedPersonId?: string | null;
  /** 席を押したとき（人の席・空席など。判定は呼び出し側）。 */
  onSeat?: (seat: SeatView) => void;
  /** 表の見出し（読み上げ）。 */
  title: string;
  className?: string;
}

function ZoomIcon({ kind }: { kind: "in" | "out" | "fit" }) {
  return (
    <svg aria-hidden="true" viewBox="0 0 16 16" className="size-4" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round">
      {kind === "fit" ? (
        <path d="M2.5 6V2.5H6M10 2.5h3.5V6M13.5 10v3.5H10M6 13.5H2.5V10" />
      ) : (
        <>
          <path d="M3.5 8h9" />
          {kind === "in" && <path d="M8 3.5v9" />}
        </>
      )}
    </svg>
  );
}

const btn = "inline-flex size-8 items-center justify-center rounded-md border border-border bg-surface text-fg shadow-sm hover:bg-surface-2";

export function FloorPlan2D({ view, selectedPersonId = null, onSeat, title, className = "" }: FloorPlan2DProps) {
  const uid = useId().replace(/:/g, "");
  const box = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 800, h: 500 });
  const b = view.bounds;
  const fit = useMemo(() => fitViewport(b, size.w / Math.max(1, size.h)), [b, size.w, size.h]);
  const [vp, setVp] = useState<Viewport | null>(null);
  const viewport = vp ?? fit;
  const selected = useMemo(() => view.seats.find((s) => s.people.some((p) => p.personId === selectedPersonId)), [view, selectedPersonId]);
  const [hover, setHover] = useState<{ seat: SeatView; x: number; y: number } | null>(null);

  // 表示領域の大きさ（縦横比を合わせる）
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const ro = new ResizeObserver(([e]) => {
      const r = e!.contentRect;
      if (r.width > 0 && r.height > 0) setSize({ w: r.width, h: r.height });
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // 階・選んだ人が変わったら合わせ直す（選んだ人がいれば、その席に寄せる）
  const selectedKey = selected?.seat.seat_id ?? "";
  useEffect(() => {
    if (selected) {
      const [x, y] = chairPosition(selected.seat);
      setVp(centerOn(fit, x, toSvgY(y, b), Math.min(fit.w, 18000)));
    } else setVp(null);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [view.layout.floor_id, selectedKey, fit.w > 0]);

  // ホイールで拡大縮小（ページのスクロールを止めるため、受け身でないリスナーで付ける）
  const limits = useMemo(() => ({ minW: 3000, maxW: fit.w * 1.6 }), [fit.w]);
  useEffect(() => {
    const el = box.current;
    if (!el) return;
    const onWheel = (e: WheelEvent) => {
      e.preventDefault();
      const r = el.getBoundingClientRect();
      const factor = Math.exp(-e.deltaY * 0.0018);
      setVp((cur) => zoomAt(cur ?? fit, factor, (e.clientX - r.left) / r.width, (e.clientY - r.top) / r.height, limits));
    };
    el.addEventListener("wheel", onWheel, { passive: false });
    return () => el.removeEventListener("wheel", onWheel);
  }, [fit, limits]);

  // ドラッグで移動、2 本指で拡大縮小
  const pointers = useRef(new Map<number, { x: number; y: number }>());
  const moved = useRef(0);
  const pinch = useRef<{ d: number } | null>(null);
  const onPointerDown = (e: ReactPointerEvent) => {
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    moved.current = 0;
    if (pointers.current.size === 2) {
      const [p, q] = [...pointers.current.values()];
      pinch.current = { d: Math.hypot(p!.x - q!.x, p!.y - q!.y) };
    }
  };
  const onPointerMove = (e: ReactPointerEvent) => {
    const prev = pointers.current.get(e.pointerId);
    if (!prev) return;
    const r = box.current!.getBoundingClientRect();
    pointers.current.set(e.pointerId, { x: e.clientX, y: e.clientY });
    if (pointers.current.size === 2 && pinch.current) {
      const [p, q] = [...pointers.current.values()];
      const d = Math.hypot(p!.x - q!.x, p!.y - q!.y);
      const cx = ((p!.x + q!.x) / 2 - r.left) / r.width;
      const cy = ((p!.y + q!.y) / 2 - r.top) / r.height;
      setVp((cur) => zoomAt(cur ?? fit, d / Math.max(1, pinch.current!.d), cx, cy, limits));
      pinch.current = { d };
      moved.current += 10;
      return;
    }
    const dx = e.clientX - prev.x;
    const dy = e.clientY - prev.y;
    moved.current += Math.abs(dx) + Math.abs(dy);
    if (moved.current > 4) {
      // 動かし始めてから指・ポインタを捕まえる（押しただけのときは席のクリックとして届ける）
      if (!box.current!.hasPointerCapture?.(e.pointerId)) box.current!.setPointerCapture?.(e.pointerId);
      setVp((cur) => panBy(cur ?? fit, dx, dy, r.width));
    }
  };
  const onPointerUp = (e: ReactPointerEvent) => {
    pointers.current.delete(e.pointerId);
    if (pointers.current.size < 2) pinch.current = null;
  };
  const zoom = (factor: number) => setVp((cur) => zoomAt(cur ?? fit, factor, 0.5, 0.5, limits));

  const Y = (y: number) => toSvgY(y, b);
  // 線の太さは画面の px で揃える（vector-effect）。文字は mm の大きさで、縮小しすぎたら札を省く
  const pxPerMm = size.w / viewport.w;
  const showZoneText = pxPerMm > 0.006;
  // 札の文字は、縮小したときも読めるように画面の 8.5px 以上に保つ（配置図の mm で 640〜900）
  const zoneFont = Math.min(900, Math.max(640, 8.5 / Math.max(pxPerMm, 1e-6)));
  const clickable = (s: SeatView) => s.state === "person";
  const seatTarget = (s: SeatView) => {
    if (moved.current > 6) return;
    if (clickable(s)) onSeat?.(s);
  };

  return (
    <div className={`flex min-h-0 flex-col ${className}`}>
      <div
        ref={box}
        className="relative min-h-0 flex-1 touch-none overflow-hidden bg-bg select-none"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={onPointerUp}
        onPointerCancel={onPointerUp}
        onPointerLeave={() => setHover(null)}
      >
        <svg aria-labelledby={`${uid}-title`} viewBox={viewBoxOf(viewport)} className="absolute inset-0 size-full" preserveAspectRatio="xMidYMid meet">
          <title id={`${uid}-title`}>{`${title}の平面図`}</title>
          <defs>
            <pattern id={`${uid}-grid`} width="1000" height="1000" patternUnits="userSpaceOnUse" x={b.minX} y="0">
              <path d="M1000 0H0V1000" fill="none" stroke="var(--border)" strokeWidth="1" vectorEffect="non-scaling-stroke" opacity="0.55" />
            </pattern>
            <pattern id={`${uid}-hatch`} width="500" height="500" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
              <rect width="500" height="500" fill="var(--surface-2)" />
              <path d="M0 0V500" stroke="var(--edge)" strokeWidth="1" vectorEffect="non-scaling-stroke" opacity="0.7" />
            </pattern>
            <pattern id={`${uid}-hatch-seat`} width="160" height="160" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
              <rect width="160" height="160" fill="var(--surface)" />
              <path d="M0 0V160" stroke="var(--edge)" strokeWidth="60" opacity="0.6" />
            </pattern>
          </defs>
          {/* 外形と格子 */}
          <path d={polyPath(view.layout.outline, Y)} fill="var(--surface)" />
          <path d={polyPath(view.layout.outline, Y)} fill={`url(#${uid}-grid)`} />
          {/* エリア */}
          {view.zones.map((z) => {
            const color = slotColorVar(z.slot);
            const muted = z.zone.kind === "common" || z.zone.kind === "storage" || z.zone.kind === "restricted";
            return (
              <g key={z.zone.zone_key}>
                <path
                  d={polyPath(z.zone.polygon, Y)}
                  fill={color ?? (muted ? "var(--surface-2)" : "var(--bg)")}
                  fillOpacity={color ? 0.08 : muted ? 0.7 : 0.6}
                  stroke={color ?? "var(--edge)"}
                  strokeOpacity={color ? 0.55 : 0.6}
                  strokeWidth="1"
                  vectorEffect="non-scaling-stroke"
                />
                {showZoneText && (
                  <text x={z.anchor[0] + 400} y={Y(z.anchor[1]) + 300 + zoneFont} fontSize={zoneFont} fill="var(--fg)" className="font-bold">
                    {z.label}
                    {z.seats > 0 && (
                      <tspan fill="var(--muted)" fontWeight="normal" dx="300">
                        {`${z.used}/${z.seats}席`}
                      </tspan>
                    )}
                  </text>
                )}
              </g>
            );
          })}
          {/* 固定の部分 */}
          {view.layout.fixtures.map((f, i) => {
            const bb = f.polygon;
            const cx = bb.reduce((s, p) => s + p[0], 0) / bb.length;
            const cy = bb.reduce((s, p) => s + p[1], 0) / bb.length;
            return (
              <g key={i}>
                <path
                  d={polyPath(f.polygon, Y)}
                  fill={f.kind === "core" ? `url(#${uid}-hatch)` : f.kind === "void" ? "none" : "var(--surface)"}
                  stroke="var(--edge)"
                  strokeWidth="1"
                  strokeDasharray={f.kind === "void" ? "4 3" : undefined}
                  vectorEffect="non-scaling-stroke"
                />
                {f.name && showZoneText && (
                  <text x={cx} y={Y(cy)} fontSize="520" textAnchor="middle" dominantBaseline="middle" fill="var(--muted)">
                    {f.name}
                  </text>
                )}
              </g>
            );
          })}
          <path d={polyPath(view.layout.outline, Y)} fill="none" stroke="var(--fg)" strokeWidth="1.6" vectorEffect="non-scaling-stroke" />
          {/* 机と席 */}
          {view.seats.map((s) => (
            <SeatGlyph
              key={s.seat.seat_id}
              s={s}
              Y={Y}
              hatch={`url(#${uid}-hatch-seat)`}
              clickable={clickable(s) && Boolean(onSeat)}
              onClick={() => seatTarget(s)}
              onHover={(e) => {
                const r = box.current?.getBoundingClientRect();
                setHover(e && r ? { seat: s, x: e.clientX - r.left, y: e.clientY - r.top } : null);
              }}
            />
          ))}
          {/* 選んだ人の印（控えめなアクセント色の輪と札） */}
          {selected && <SelectedMark s={selected} Y={Y} personId={selectedPersonId} />}
        </svg>
        <div className="pointer-events-none absolute right-2 bottom-2 flex flex-col gap-1.5">
          <div className="pointer-events-auto flex flex-col gap-1">
            <button type="button" className={btn} aria-label="拡大" title="拡大" onClick={() => zoom(1.4)}>
              <ZoomIcon kind="in" />
            </button>
            <button type="button" className={btn} aria-label="縮小" title="縮小" onClick={() => zoom(1 / 1.4)}>
              <ZoomIcon kind="out" />
            </button>
            <button type="button" className={btn} aria-label="全体を表示" title="全体を表示" onClick={() => setVp(null)}>
              <ZoomIcon kind="fit" />
            </button>
          </div>
        </div>
        {hover && (
          <div
            className="pointer-events-none absolute z-10 min-w-[9em] rounded-sm border border-t-2 border-border border-t-accent bg-surface px-2 py-1.5 text-xs shadow"
            style={{ left: Math.min(hover.x + 14, size.w - 190), top: Math.min(hover.y + 14, size.h - 80) }}
            aria-hidden="true"
          >
            <SeatTooltip s={hover.seat} />
          </div>
        )}
      </div>
      <SeatTable view={view} title={title} onSeat={onSeat} clickable={clickable} />
    </div>
  );
}

function polyPath(points: ReadonlyArray<readonly [number, number]>, Y: (y: number) => number): string {
  return points.map(([x, y], i) => `${i === 0 ? "M" : "L"}${x} ${Y(y)}`).join("") + "Z";
}

/** 席の説明（指したとき）。 */
export function SeatTooltip({ s }: { s: SeatView }) {
  return (
    <>
      <div className="tabular text-muted">{s.seat.code}</div>
      {s.state === "person" ? (
        s.people.length > 0 ? (
          s.people.map((p) => (
            <div key={p.personId}>
              <span className="font-bold">{p.name ?? "（氏名非公開）"}</span>
              {p.today && <span className="ml-1 rounded-sm border border-border px-1 text-[10px]">今日</span>}
              {p.orgName && <div className="text-muted">{p.orgName}</div>}
            </div>
          ))
        ) : (
          <div className="font-bold">使用中</div>
        )
      ) : (
        <div className="font-bold">{STATE_LABEL[s.state]}</div>
      )}
      {s.seat.capacity > 1 && <div className="text-muted">持ち場（定員 {s.seat.capacity}）</div>}
    </>
  );
}

const PERSON_R = 260;

function SeatGlyph({ s, Y, hatch, clickable, onClick, onHover }: {
  s: SeatView;
  Y: (y: number) => number;
  hatch: string;
  clickable: boolean;
  onClick: () => void;
  onHover: (e: { clientX: number; clientY: number } | null) => void;
}) {
  const { w, d } = deskSize(s.seat.desk_type);
  const x = s.seat.x_mm;
  const y = Y(s.seat.y_mm);
  const station = s.seat.seat_kind === "station";
  const [px, pyRaw] = chairPosition(s.seat);
  const py = Y(pyRaw);
  const color = slotColorVar(s.slot) ?? "var(--site-other)";
  return (
    <g
      onClick={onClick}
      onPointerEnter={(e) => onHover(e)}
      onPointerMove={(e) => onHover(e)}
      onPointerLeave={() => onHover(null)}
      style={{ cursor: clickable ? "pointer" : "default" }}
    >
      {/* 机（向きに合わせて回す） */}
      <rect
        x={x - w / 2}
        y={y - d / 2}
        width={w - 60}
        height={d}
        rx={station ? 200 : 40}
        transform={`rotate(${s.seat.rotation_deg} ${x} ${y})`}
        fill="var(--surface)"
        stroke="var(--edge)"
        strokeWidth="0.8"
        vectorEffect="non-scaling-stroke"
      />
      {s.state === "person" &&
        (station ? (
          // 持ち場: 定員までの小さな点を横に並べる
          s.people.slice(0, s.seat.capacity).map((p, i, list) => (
            <circle key={p.personId} cx={x + (i - (list.length - 1) / 2) * 420} cy={y} r={190} fill={color} />
          ))
        ) : (
          <circle cx={px} cy={py} r={PERSON_R} fill={color} />
        ))}
      {s.state === "person" && station && s.people.length === 0 && <circle cx={x} cy={y} r={190} fill={color} />}
      {s.state === "vacant" && <circle cx={px} cy={py} r={PERSON_R - 20} fill="var(--surface)" stroke="var(--fg)" strokeWidth="1" vectorEffect="non-scaling-stroke" />}
      {s.state === "unavailable" && (
        <rect x={px - 260} y={py - 260} width={520} height={520} rx={60} fill={hatch} stroke="var(--edge)" strokeWidth="0.8" vectorEffect="non-scaling-stroke" opacity="0.9" />
      )}
      {/* 今日の所在の人: 点の右上の小さな菱形（形で示す） */}
      {s.today && (
        <path d={`M${px + 300} ${py - 520}l170 170l-170 170l-170 -170Z`} fill="var(--fg)" stroke="var(--surface)" strokeWidth="1" vectorEffect="non-scaling-stroke" />
      )}
    </g>
  );
}

function SelectedMark({ s, Y, personId }: { s: SeatView; Y: (y: number) => number; personId: string | null }) {
  const [px, pyRaw] = chairPosition(s.seat);
  const py = Y(pyRaw);
  const p = s.people.find((x) => x.personId === personId);
  const text = `${p?.name ?? ""} ${s.seat.code}`.trim();
  const w = Math.max(3200, text.length * 560);
  return (
    <g pointerEvents="none">
      <circle cx={px} cy={py} r={520} fill="none" stroke="var(--accent)" strokeWidth="2.4" vectorEffect="non-scaling-stroke" />
      <circle cx={px} cy={py} r={640} fill="none" stroke="var(--surface)" strokeWidth="1.5" vectorEffect="non-scaling-stroke" />
      <g transform={`translate(${px - 400} ${py - 2100})`}>
        <rect width={w} height={1150} rx={120} fill="var(--surface)" stroke="var(--border)" strokeWidth="1" vectorEffect="non-scaling-stroke" />
        <rect width={220} height={1150} rx={60} fill="var(--accent)" />
        <text x={480} y={780} fontSize="640" fill="var(--fg)" className="font-bold">
          {text}
        </text>
      </g>
    </g>
  );
}

/** 平面図と同じ内容の表（キーボード・読み上げでの代替）。 */
function SeatTable({ view, title, onSeat, clickable }: {
  view: FloorView;
  title: string;
  onSeat?: (s: SeatView) => void;
  clickable: (s: SeatView) => boolean;
}) {
  return (
    <details className="shrink-0 border-t border-border bg-surface text-sm">
      <summary className="cursor-pointer px-4 py-2 text-[13px] text-muted hover:text-fg">席の一覧（表）</summary>
      <div className="max-h-[40dvh] overflow-auto px-4 pb-3">
        <table className="w-full border-collapse text-left text-[13px]">
          <caption className="sr-only">{title}の席の一覧</caption>
          <thead>
            <tr className="border-b border-border text-xs text-muted">
              <th className="py-1 pr-3 font-normal">席番号</th>
              <th className="py-1 pr-3 font-normal">状態</th>
              <th className="py-1 pr-3 font-normal">人</th>
              <th className="py-1 font-normal">組織</th>
            </tr>
          </thead>
          <tbody>
            {view.seats.map((s) => {
              const people = s.people.length > 0 ? s.people : [null];
              return people.map((p, i) => (
                <tr key={`${s.seat.seat_id}:${p?.personId ?? i}`} className="border-b border-border/60">
                  <td className="tabular py-1 pr-3">
                    {i === 0 && onSeat && clickable(s) ? (
                      <button type="button" className="text-accent hover:underline" onClick={() => onSeat(s)}>
                        {s.seat.code}
                      </button>
                    ) : i === 0 ? (
                      s.seat.code
                    ) : null}
                  </td>
                  <td className="py-1 pr-3">
                    {STATE_LABEL[s.state]}
                    {p?.today && <span className="ml-1 rounded-sm border border-border px-1 text-[11px]">今日</span>}
                  </td>
                  <td className="py-1 pr-3">{p ? (p.name ?? "（氏名非公開）") : ""}</td>
                  <td className="py-1 text-muted">{p?.orgName ?? ""}</td>
                </tr>
              ));
            })}
          </tbody>
        </table>
      </div>
    </details>
  );
}
