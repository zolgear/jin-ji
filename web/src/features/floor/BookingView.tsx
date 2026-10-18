// 本人の「今日の席」の画面（当日の所在＝自己申告の座席予約。仕様 §2.6・§3.4・§5.7）。3D 表示とは別の画面。
// URL: #/floor-booking?floor=<id>&date=2026-10-20（date は今日から予約できる先の日数まで。タイムマシンの as_of とは別の引数）
//
// - 2D の平面図の上で空席（フリーアドレスの席）を選んで予約する。席の単位を許していなければエリアを選ぶ
// - 自分の予約の一覧（今日以降）と取り消し。予約は本人だけが入れ、取り消しは記録を残さない
// - 予約の画面にも「予約が無いことは不在を意味しない」と利用目的の文を出す（i の中。画面に常時の説明文は出さない）
// - 機能が無効な導入先では、この画面を出さない（レールにも出さない）
import { useMemo, useState } from "react";
import { useOrgTree } from "../../app/hooks";
import { HelpTip } from "../../components/popover";
import { ErrorNote, Spinner } from "../../components/ui";
import { useAdapter } from "../../data/adapterContext";
import { useQuery } from "../../data/query";
import { describeError, type MyReservation } from "../../data/types";
import { addDays, isIsoDate } from "../../lib/date";
import { useFormatDate } from "../../lib/dateStyle";
import { useRoute } from "../../lib/router";
import { bumpPresenceVersion, useBuildings, useFloorLayout, useFloorOccupants, useFloorPresence, useMyPresence, usePresenceSettings } from "./floorHooks";
import { buildFloorView, groupResolver, type SeatView } from "./floorModel";
import { FloorPlan2D } from "./FloorPlan2D";
import { PRESENCE_NOTE } from "./FloorParts";

const WEEKDAY = ["日", "月", "火", "水", "木", "金", "土"];
const selectClass = "h-8 max-w-[12em] rounded-md border border-border bg-surface px-2 text-[13px]";

function shortDate(date: string): string {
  const d = new Date(`${date}T00:00:00Z`);
  return `${d.getUTCMonth() + 1}/${d.getUTCDate()}（${WEEKDAY[d.getUTCDay()]}）`;
}

export function BookingView() {
  const { adapter } = useAdapter();
  const { route, navigate } = useRoute();
  const formatDate = useFormatDate();
  const today = adapter.businessDate;
  const settings = usePresenceSettings();
  const enabled = Boolean(settings.data);
  const daysAhead = settings.data?.days_ahead ?? 14;
  const mine = useMyPresence(enabled);
  const myList = useMemo(() => mine.data ?? [], [mine.data]);
  const rawDate = route.params.get("date");
  const date = rawDate && isIsoDate(rawDate) && rawDate >= today && rawDate <= addDays(today, daysAhead) ? rawDate : today;
  const myOnDate = myList.find((r) => r.date === date) ?? null;
  const anchor = myOnDate ?? myList[0] ?? null;

  // 拠点・建物・階: URL の floor か、自分の予約の階
  const sites = useQuery(adapter, `sites:${today}`, () => adapter.listSites({ asOf: today }));
  const [siteChoice, setSiteChoice] = useState<string | null>(null);
  const siteList = sites.data ?? [];
  const siteId = siteChoice ?? anchor?.site_id ?? (siteList.find((s) => s.type === "head_office") ?? siteList[0])?.id ?? null;
  const buildings = useBuildings(siteId, today);
  const floors = (buildings.data ?? []).flatMap((b) => b.floors.filter((f) => f.seat_count > 0).map((f) => ({ b, f })));
  const floorParam = route.params.get("floor");
  const floorId = floors.find((x) => x.f.id === floorParam)?.f.id ?? (anchor && floors.some((x) => x.f.id === anchor.floor_id) ? anchor.floor_id : floors[0]?.f.id) ?? null;
  const current = floors.find((x) => x.f.id === floorId);

  // 平面図は今日の配置図・席の割り当てに、選んだ日の予約を重ねる（将来の割り当ては内示に関わるので見ない）
  const layout = useFloorLayout(enabled ? floorId : null, today);
  const occupants = useFloorOccupants(enabled ? floorId : null, today);
  const presence = useFloorPresence(enabled ? floorId : null, date);
  const tree = useOrgTree(today);
  const groupOf = useMemo(() => groupResolver(tree.data), [tree.data]);
  const view = useMemo(
    () => (layout.data && occupants.data && presence.data !== undefined ? buildFloorView({ layout: layout.data, occupants: occupants.data, presence: presence.data, groupOf }) : null),
    [layout.data, occupants.data, presence.data, groupOf],
  );
  const freeZones = (layout.data?.zones ?? []).filter((z) => z.kind === "free_address");

  const [pending, setPending] = useState<SeatView | null>(null);
  const [zoneChoice, setZoneChoice] = useState<string>("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const unit = settings.data?.unit ?? "both";

  const set = (change: Record<string, string | null>) => navigate({ params: change }, true);
  const run = async (fn: () => Promise<unknown>, ok: string) => {
    setBusy(true);
    setMessage(null);
    try {
      await fn();
      bumpPresenceVersion();
      setMessage({ tone: "ok", text: ok });
      setPending(null);
    } catch (e) {
      setMessage({ tone: "error", text: describeError(e) });
    } finally {
      setBusy(false);
    }
  };
  const reserveSeat = (s: SeatView) =>
    run(() => adapter.workplace.reserve(date, { floorId: floorId!, seatId: s.seat.seat_id }), `${shortDate(date)}に ${s.seat.code} を予約しました。`);
  const cancel = (r: MyReservation) => run(() => adapter.workplace.cancel(r.date), `${shortDate(r.date)}の予約を取り消しました。`);

  if (settings.loading && !settings.data) {
    return (
      <div className="p-4">
        <Spinner />
      </div>
    );
  }
  if (!enabled) {
    return <p className="p-6 text-muted">この導入先では、当日の所在（席の予約）の機能は有効になっていません。</p>;
  }
  const dates: string[] = [];
  for (let k = 0; k <= daysAhead; k++) dates.push(addDays(today, k));

  return (
    <div className="flex size-full flex-col overflow-hidden lg:flex-row">
      <div className="flex min-h-0 min-w-0 flex-1 flex-col">
        {/* 日付と階の選択（2D 組織図・3D の帯と同じ体裁） */}
        <div className="flex shrink-0 items-center gap-x-3 gap-y-2 overflow-x-auto border-b border-border bg-surface px-4 py-2.5 text-[13px] whitespace-nowrap max-md:[&>*]:shrink-0 md:flex-wrap md:px-5">
          <select aria-label="予約する日" className="h-8 rounded-md border border-border bg-surface px-2 text-[13px]" value={date} onChange={(e) => set({ date: e.target.value === today ? null : e.target.value })}>
            {dates.map((d) => (
              <option key={d} value={d}>
                {shortDate(d)}
                {d === today ? " 今日" : ""}
                {myList.some((r) => r.date === d) ? "（予約あり）" : ""}
              </option>
            ))}
          </select>
          <select aria-label="拠点" className={selectClass} value={siteId ?? ""} onChange={(e) => {
            setSiteChoice(e.target.value);
            set({ floor: null });
          }}>
            {siteList.map((s) => (
              <option key={s.id} value={s.id}>
                {s.name}
              </option>
            ))}
          </select>
          <select aria-label="階" className={selectClass} value={floorId ?? ""} onChange={(e) => set({ floor: e.target.value })}>
            {floors.map(({ b, f }) => (
              <option key={f.id} value={f.id}>
                {b.name} {f.label}
              </option>
            ))}
          </select>
          {presence.data && <span className="tabular text-muted">空き <span className="font-bold text-fg">{presence.data.vacant_count}</span> 席</span>}
          {(layout.loading || presence.loading) && <Spinner />}
          <span className="ml-auto">
            <HelpTip label="予約について" width={320}>
              <p className="m-0">{PRESENCE_NOTE}</p>
              {settings.data?.purpose && <p className="m-0">利用目的: {settings.data.purpose}</p>}
              <p className="m-0 text-muted">輪の太い空席（フリーアドレスの席）を押すと予約できます。1 日に 1 件で、予約を入れ直すと前の予約は置き換わります。予約の時刻は記録しません。</p>
            </HelpTip>
          </span>
        </div>
        <div className="relative min-h-0 flex-1">
          {layout.error || presence.error ? (
            <div className="p-4">
              <ErrorNote>{describeError(layout.error ?? presence.error)}</ErrorNote>
            </div>
          ) : view && current ? (
            <FloorPlan2D
              view={view}
              title={`${current.b.name} ${current.f.label}`}
              reservationLabel={date === today ? "今日" : "予約"}
              mySeatId={myOnDate?.seat_id ?? null}
              selectable={unit === "zone" ? () => false : (s) => s.state === "vacant" && s.seat.seat_kind === "free"}
              onSeat={(s) => setPending(s)}
              className="size-full"
            />
          ) : (
            <div className="p-4">{floors.length === 0 && buildings.data ? <p className="text-muted">この拠点には予約できる階がありません。</p> : <Spinner />}</div>
          )}
          {pending && (
            <section className="absolute inset-x-3 bottom-14 z-10 mx-auto flex max-w-md flex-wrap items-center gap-2 rounded-lg border border-border bg-surface px-3 py-2 shadow-lg md:bottom-3" aria-label="予約の確認">
              <span className="flex-1">
                <span className="font-bold">{shortDate(date)}</span> に <span className="tabular font-bold">{pending.seat.code}</span> を予約しますか
              </span>
              <button type="button" className="h-8 rounded-md border border-border px-3 hover:bg-surface-2" onClick={() => setPending(null)}>
                やめる
              </button>
              <button type="button" disabled={busy} className="h-8 rounded-md bg-accent px-3 font-bold text-accent-fg disabled:opacity-60" onClick={() => reserveSeat(pending)}>
                予約する
              </button>
            </section>
          )}
        </div>
      </div>
      {/* 自分の予約 */}
      <aside aria-labelledby="my-presence" className="shrink-0 overflow-y-auto border-t border-border bg-surface p-4 max-lg:max-h-[38dvh] lg:w-[340px] lg:border-t-0 lg:border-l">
        <h2 id="my-presence" className="m-0 mb-2 text-sm font-bold">
          自分の予約
        </h2>
        {message && (
          <p role={message.tone === "error" ? "alert" : "status"} className={`mb-2 rounded-sm border px-3 py-1.5 text-[13px] ${message.tone === "error" ? "border-danger/40 text-danger" : "border-border bg-accent-weak text-fg"}`}>
            {message.text}
          </p>
        )}
        {mine.loading && myList.length === 0 ? (
          <Spinner />
        ) : myList.length === 0 ? (
          <p className="m-0 text-[13px] text-muted">予約はありません。</p>
        ) : (
          <ul className="m-0 flex list-none flex-col gap-1.5 p-0">
            {myList.map((r) => (
              <li key={r.date} className={`flex items-center gap-2 rounded-md border px-2.5 py-1.5 text-[13px] ${r.date === date ? "border-accent" : "border-border"}`}>
                <button type="button" className="min-w-0 flex-1 text-left" onClick={() => set({ date: r.date === today ? null : r.date, floor: r.floor_id })}>
                  <span className="block font-bold">
                    {r.date === today ? "今日 " : ""}
                    {formatDate(r.date)}
                  </span>
                  <span className="block text-muted">
                    {r.building_name} {r.floor_label}
                    {r.seat_code ? <span className="tabular">・{r.seat_code}</span> : r.zone_name ? `・${r.zone_name}（席は決めない）` : ""}
                  </span>
                </button>
                <button type="button" disabled={busy} className="h-7 shrink-0 rounded-md border border-border px-2 text-xs hover:bg-surface-2 disabled:opacity-60" onClick={() => cancel(r)}>
                  取り消す
                </button>
              </li>
            ))}
          </ul>
        )}
        {unit !== "seat" && freeZones.length > 0 && (
          <div className="mt-4 flex flex-col gap-1.5 border-t border-border pt-3">
            <label htmlFor="zone-only" className="text-xs font-bold text-muted">
              席を決めずにエリアだけ予約する
            </label>
            <span className="flex gap-1.5">
              <select id="zone-only" className={`${selectClass} min-w-0 flex-1`} value={zoneChoice} onChange={(e) => setZoneChoice(e.target.value)}>
                <option value="">エリアを選ぶ</option>
                {freeZones.map((z) => (
                  <option key={z.zone_key} value={z.zone_key}>
                    {z.home_org_units.map((o) => o.name).join("・") || z.name}
                  </option>
                ))}
              </select>
              <button
                type="button"
                disabled={!zoneChoice || busy}
                className="h-8 shrink-0 rounded-md border border-border px-3 text-[13px] hover:bg-surface-2 disabled:opacity-50"
                onClick={() => run(() => adapter.workplace.reserve(date, { floorId: floorId!, zoneKey: zoneChoice }), `${shortDate(date)}にエリアを予約しました。`)}
              >
                予約する
              </button>
            </span>
          </div>
        )}
      </aside>
    </div>
  );
}
