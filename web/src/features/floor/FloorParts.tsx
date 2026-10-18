// 建物・フロアモードの小さな部品: モードの切り替え（組織 / 建物・フロア）・「図の見方」の中身・席の状態の見本。
import type { ReactNode } from "react";

/** 当日の所在の注記（凡例と予約の画面に出す。仕様 §5.7）。 */
export const PRESENCE_NOTE = "当日の所在は本人の予約です。予約が無いことは、不在・在宅・休みを意味しません。";

/** 3D 表示のモードの切り替え（組織 / 建物・フロア）。2D 組織図・3D の帯と同じ高さの 2 つのボタン。 */
export function ModeSwitch({ floor, onChange }: { floor: boolean; onChange: (floor: boolean) => void }) {
  const items: Array<[boolean, string]> = [
    [false, "組織"],
    [true, "建物・フロア"],
  ];
  return (
    <fieldset className="m-0 flex shrink-0 overflow-hidden rounded-md border border-border p-0">
      <legend className="sr-only">表示するもの</legend>
      {items.map(([value, label]) => (
        <button
          key={label}
          type="button"
          aria-pressed={floor === value}
          onClick={() => floor !== value && onChange(value)}
          className={`h-8 px-3 ${floor === value ? "bg-fg font-bold text-bg" : "hover:bg-surface-2"}`}
        >
          {label}
        </button>
      ))}
    </fieldset>
  );
}

/** 席の状態の見本（平面図と同じ形）。 */
export function SeatKey({ kind }: { kind: "person" | "vacant" | "unavailable" | "today" | "selected" | "free" }) {
  return (
    <svg aria-hidden="true" width="18" height="14" viewBox="0 0 18 14" className="line-key">
      <defs>
        <pattern id="seatkey-hatch" width="3" height="3" patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
          <rect width="3" height="3" fill="var(--surface)" />
          <rect width="1.2" height="3" fill="var(--edge)" />
        </pattern>
      </defs>
      {kind === "person" && <circle cx="9" cy="7" r="4.2" fill="var(--site-1)" />}
      {kind === "vacant" && <circle cx="9" cy="7" r="4" fill="var(--surface)" stroke="var(--fg)" strokeWidth="1" />}
      {kind === "unavailable" && <rect x="4.5" y="2.5" width="9" height="9" rx="1" fill="url(#seatkey-hatch)" stroke="var(--edge)" strokeWidth="0.8" />}
      {kind === "today" && (
        <>
          <circle cx="8" cy="8" r="4" fill="var(--site-1)" />
          <path d="M13 1.5l2.6 2.6-2.6 2.6-2.6-2.6Z" fill="var(--fg)" />
        </>
      )}
      {kind === "selected" && (
        <>
          <circle cx="9" cy="7" r="3.2" fill="var(--site-1)" />
          <circle cx="9" cy="7" r="5.8" fill="none" stroke="var(--accent)" strokeWidth="1.6" />
        </>
      )}
      {kind === "free" && <rect x="2" y="3" width="14" height="8" rx="1" fill="none" stroke="var(--edge)" strokeWidth="1" strokeDasharray="2.5 2" />}
    </svg>
  );
}

function Item({ icon, children }: { icon: ReactNode; children: ReactNode }) {
  return (
    <p className="m-0 flex items-start gap-2">
      {icon}
      <span>{children}</span>
    </p>
  );
}

/** 「図の見方」の中身（画面には常に出さない）。 */
export function FloorHowToRead({ presenceEnabled, presenceShown, purpose }: { presenceEnabled: boolean; presenceShown: boolean; purpose: string | null }) {
  return (
    <>
      <p className="m-0">階を離して積んでいます。選んだ階だけ席を描き、ほかの階は埋まり具合を灰の点で示します（濃い点が人のいる席）。</p>
      <p className="m-0">階の札と左上の一覧の数字は「割り当て / 席数」です。人数は、あなたが見られる人だけで数えます。</p>
      <Item icon={<SeatKey kind="person" />}>人のいる席。色は組織です（席の多い 3 つまで。ほかは灰色）。エリアの札に組織名を出します。</Item>
      <Item icon={<SeatKey kind="vacant" />}>空席</Item>
      <Item icon={<SeatKey kind="unavailable" />}>使えない席（保守中・予備の席など）</Item>
      <Item icon={<SeatKey kind="free" />}>破線のエリアはフリーアドレスです。</Item>
      <Item icon={<SeatKey kind="selected" />}>選んだ人の席</Item>
      {presenceEnabled &&
        (presenceShown ? (
          <>
            <Item icon={<SeatKey kind="today" />}>今日の予約で来る人。札の「空き」は今日の空き席数です。</Item>
            <p className="m-0 text-muted">{PRESENCE_NOTE}</p>
            {purpose && <p className="m-0 text-muted">利用目的: {purpose}</p>}
          </>
        ) : (
          <p className="m-0 text-muted">当日の所在は今日の分だけを表示します。</p>
        ))}
      <p className="m-0 text-muted">ドラッグで回転、ホイール・ピンチで拡大縮小、右ドラッグ・2 本指で移動します。席の一覧の表は「平面図」の下にあります。</p>
    </>
  );
}
