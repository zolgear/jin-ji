// 図（組織の変更の編集・3D 表示）の上に浮かべる箱。入力欄の外は少し透かし、後ろの図が見えるようにする。PC は右上、スマホは下に出す。
import type { ReactNode } from "react";
import { CloseButton } from "./ui";

const FRAME = "absolute inset-x-3 bottom-3 z-10 flex max-h-[55%] flex-col overflow-hidden rounded-lg border border-border bg-surface/75 text-sm shadow-xl backdrop-blur-sm sm:inset-x-auto sm:bottom-auto";

/**
 * title があれば見出しと × を付ける（無ければ中身が自分で見出しと閉じるボタンを持つ）。
 * top は PC での上端（図の上に操作の帯を浮かべているときは下げる）。side は寄せる側（PC は右に詳細・左に一覧。スマホは詳細を下、一覧を上に出して重ねない）。
 */
export function FloatingCard({
  label,
  title,
  onClose,
  children,
  wide = false,
  top = "sm:top-16 sm:max-h-[calc(100%-5rem)]",
  side = "right",
}: {
  label: string;
  title?: ReactNode;
  onClose?: () => void;
  children: ReactNode;
  wide?: boolean;
  top?: string;
  side?: "left" | "right";
}) {
  return (
    <section aria-label={label} className={`${FRAME} ${top} ${side === "left" ? "max-sm:top-3 max-sm:bottom-auto max-sm:max-h-[40%] sm:left-3" : "sm:right-3"} ${wide ? "sm:w-96" : "sm:w-80"}`}>
      {title !== undefined && (
        <div className="flex items-center gap-2 border-b border-border/60 px-3 py-2">
          <div className="min-w-0 flex-1 font-bold">{title}</div>
          {onClose && <CloseButton onClick={onClose} />}
        </div>
      )}
      <div className="flex min-h-0 flex-col gap-3 overflow-y-auto p-3">{children}</div>
    </section>
  );
}
