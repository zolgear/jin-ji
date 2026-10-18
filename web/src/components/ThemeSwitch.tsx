// 配色（自動 / ライト / ダーク）の切り替え。size="md" は設定の箱の中で使う大きさ（幅いっぱい・押しやすい高さ）。
import { useThemeChoice, type ThemeChoice } from "../lib/theme";

export function ThemeSwitch({ size = "sm" }: { size?: "sm" | "md" }) {
  const { choice, setChoice } = useThemeChoice();
  const options: Array<[ThemeChoice, string]> = [
    ["system", "自動"],
    ["light", "ライト"],
    ["dark", "ダーク"],
  ];
  const md = size === "md";
  return (
    <fieldset className={`m-0 flex overflow-hidden border border-border p-0 ${md ? "w-full rounded-md text-sm" : "rounded-sm text-xs"}`}>
      <legend className="sr-only">配色</legend>
      {options.map(([value, label]) => (
        <button
          key={value}
          type="button"
          aria-pressed={choice === value}
          onClick={() => setChoice(value)}
          className={`${md ? "h-8 flex-1" : "px-1.5 py-0.5 sm:px-2"} ${choice === value ? "bg-accent text-accent-fg" : "hover:bg-surface-2"}`}
        >
          {label}
        </button>
      ))}
    </fieldset>
  );
}
