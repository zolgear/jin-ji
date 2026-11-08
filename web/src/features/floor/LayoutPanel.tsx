// フロアの帯の「配置図」: 建物の今の配置図の JSON（jin-ji の配置図の形式 v1）の書き出しと、JSON を選んでの直接登録（施設管理者・人事管理者。0026）。
// 登録は管理元の設定（配置図: 承認なし / 承認あり）に従い、すぐに反映するか承認待ちになる。形の検査は API が行う。
import { useId, useState } from "react";
import { Button, Notice } from "../../auth/ui";
import { HelpTip, Popover } from "../../components/popover";
import { useAdapter } from "../../data/adapterContext";
import { readLayoutDocument, toLayoutDocument } from "../../data/layoutDocument";
import { describeError, type Building, type FloorLayout } from "../../data/types";
import { inputClass } from "../field/shared";

const triggerClass = "h-8 shrink-0 rounded-md border border-border px-3 whitespace-nowrap hover:bg-surface-2";

export function LayoutPanel({ building, siteCode, asOf, onRegistered }: { building: Building | null; siteCode: string | null; asOf: string; onRegistered: () => void }) {
  const { adapter } = useAdapter();
  const panelId = useId();
  const fileId = useId();
  const [file, setFile] = useState<{ name: string; summary: string; document: Record<string, unknown> } | null>(null);
  const [date, setDate] = useState(adapter.businessDate);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(null);

  const exportJson = async () => {
    if (!building || !siteCode) return;
    setMessage(null);
    try {
      const layouts = new Map<string, FloorLayout>();
      for (const f of building.floors) if (f.layout_id) layouts.set(f.id, await adapter.workplace.getFloorLayout(f.id, asOf));
      const text = JSON.stringify(toLayoutDocument(siteCode, building, layouts), null, 2);
      const href = URL.createObjectURL(new Blob([text], { type: "application/json" }));
      const a = document.createElement("a");
      a.href = href;
      a.download = `${siteCode}-${building.code}-layout.json`;
      a.click();
      URL.revokeObjectURL(href);
    } catch (e) {
      setMessage({ tone: "error", text: describeError(e) });
    }
  };

  const pick = async (input: HTMLInputElement) => {
    setMessage(null);
    const chosen = input.files?.[0];
    input.value = "";
    if (!chosen) return;
    try {
      const read = readLayoutDocument(await chosen.text());
      setFile({ name: chosen.name, summary: `${read.buildings} 棟・${read.floors} 階・${read.seats} 席`, document: read.document as unknown as Record<string, unknown> });
    } catch (e) {
      setFile(null);
      setMessage({ tone: "error", text: e instanceof Error ? e.message : String(e) });
    }
  };

  const register = async () => {
    if (!file) return;
    setBusy(true);
    setMessage(null);
    try {
      const r = await adapter.field.registerLayout({ effective_date: date, note: null, document: file.document });
      setFile(null);
      setMessage({ tone: "ok", text: r.status === "pending_consent" ? "承認待ちにしました。" : `反映しました（${date} から）。` });
      onRegistered();
    } catch (e) {
      setMessage({ tone: "error", text: describeError(e) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <Popover id={panelId} label="配置図" trigger="配置図" triggerClassName={triggerClass} width={320}>
      <div className="flex flex-col gap-3 p-3">
        <div className="flex items-center gap-1 text-xs font-bold">
          配置図
          <HelpTip label="配置図の説明" width={320}>
            建物・階・机とエリアの置き場所を、取込と同じ JSON（jinji-floor-layout/v1）で登録します。書き出した JSON を直して登録できます。席番号が同じ席は同じ席として、人の席の割り当てが続きます。設定で「承認あり」にすると、登録した人と別の施設管理者・人事管理者が「組織の変更」の反映の記録で承認したときに反映します。
          </HelpTip>
        </div>
        <Button disabled={!building || !siteCode} onClick={() => void exportJson()}>
          {building ? `${building.name}の JSON を書き出す` : "建物を選ぶと書き出せます"}
        </Button>
        <div className="flex flex-col gap-2 border-t border-border pt-3">
          <label htmlFor={fileId} className="flex cursor-pointer flex-col gap-1 text-xs">
            <span className="text-muted">登録する JSON</span>
            <input id={fileId} type="file" accept="application/json,.json" className="text-sm" onChange={(e) => void pick(e.currentTarget)} />
          </label>
          {file && (
            <p className="m-0 text-xs">
              <span className="font-bold">{file.name}</span> <span className="text-muted">{file.summary}</span>
            </p>
          )}
          <label className="flex flex-col gap-1 text-xs">
            <span className="text-muted">切り替える日</span>
            <input type="date" className={inputClass} value={date} min={adapter.businessDate} onChange={(e) => setDate(e.target.value)} />
          </label>
          <Button variant="primary" busy={busy} disabled={!file || date < adapter.businessDate} onClick={() => void register()}>
            登録する
          </Button>
        </div>
        {message && <Notice tone={message.tone === "ok" ? "ok" : "error"}>{message.text}</Notice>}
      </div>
    </Popover>
  );
}
