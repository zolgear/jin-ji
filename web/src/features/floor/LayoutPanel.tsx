// フロアの帯の「配置図」: 建物の今の配置図の Excel（ひな形を兼ねる）・JSON（jin-ji の配置図の形式 v1）の書き出しと、
// Excel か JSON を選んでの直接登録（施設管理者・人事管理者。0026）。Excel はブラウザで読み書きし、API には JSON で送る。
// 登録は管理元の設定（配置図: 承認なし / 承認あり）に従い、すぐに反映するか承認待ちになる。形の検査は API が行う。
import { useId, useState } from "react";
import { Button, Notice } from "../../auth/ui";
import { HelpTip, Popover } from "../../components/popover";
import { useAdapter } from "../../data/adapterContext";
import { readLayoutDocument, toLayoutDocument, type LayoutDocument } from "../../data/layoutDocument";
import { fromLayoutSheets, toLayoutSheets } from "../../data/layoutWorkbook";
import { readXlsx, writeXlsx } from "../../lib/xlsx";
import { describeError, type Building, type FloorLayout } from "../../data/types";
import { inputClass } from "../field/shared";

const triggerClass = "h-8 shrink-0 rounded-md border border-border px-3 whitespace-nowrap hover:bg-surface-2";
const linkClass = "self-start text-xs text-accent underline disabled:text-muted disabled:no-underline";

/** 選んだファイルを保存させる。 */
function save(bytes: Uint8Array | string, type: string, fileName: string) {
  const href = URL.createObjectURL(new Blob([bytes as BlobPart], { type }));
  const a = document.createElement("a");
  a.href = href;
  a.download = fileName;
  a.click();
  URL.revokeObjectURL(href);
}

function summarize(doc: LayoutDocument): string {
  const floors = doc.buildings.flatMap((b) => b.floors);
  const seats = floors.reduce((n, f) => n + (f.layout?.seats?.length ?? 0), 0);
  return `${doc.buildings.length} 棟・${floors.length} 階・${seats} 席`;
}

export function LayoutPanel({ building, siteCode, asOf, onRegistered }: { building: Building | null; siteCode: string | null; asOf: string; onRegistered: () => void }) {
  const { adapter } = useAdapter();
  const panelId = useId();
  const fileId = useId();
  const [file, setFile] = useState<{ name: string; summary: string; document: Record<string, unknown> } | null>(null);
  const [problems, setProblems] = useState<string[]>([]);
  const [date, setDate] = useState(adapter.businessDate);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(null);

  const current = async (): Promise<LayoutDocument | null> => {
    if (!building || !siteCode) return null;
    const layouts = new Map<string, FloorLayout>();
    for (const f of building.floors) if (f.layout_id) layouts.set(f.id, await adapter.workplace.getFloorLayout(f.id, asOf));
    return toLayoutDocument(siteCode, building, layouts);
  };

  const exportAs = async (kind: "xlsx" | "json") => {
    setMessage(null);
    try {
      const doc = await current();
      const base = doc ? `${siteCode}-${building!.code}-配置図` : "配置図のひな形";
      if (kind === "xlsx") save(writeXlsx(toLayoutSheets(doc)), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", `${base}.xlsx`);
      else if (doc) save(JSON.stringify(doc, null, 2), "application/json", `${base}.json`);
    } catch (e) {
      setMessage({ tone: "error", text: describeError(e) });
    }
  };

  const pick = async (input: HTMLInputElement) => {
    setMessage(null);
    setProblems([]);
    setFile(null);
    const chosen = input.files?.[0];
    input.value = "";
    if (!chosen) return;
    try {
      let doc: LayoutDocument;
      if (/\.json$/i.test(chosen.name)) {
        doc = readLayoutDocument(await chosen.text()).document;
      } else {
        const read = fromLayoutSheets(await readXlsx(new Uint8Array(await chosen.arrayBuffer())));
        if (!read.document) {
          setProblems(read.problems);
          return;
        }
        doc = read.document;
      }
      setFile({ name: chosen.name, summary: summarize(doc), document: doc as unknown as Record<string, unknown> });
    } catch (e) {
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
            建物・階・机とエリアの置き場所を Excel（取込と同じ JSON も可）で登録します。書き出した Excel を直して登録できます。書き方は Excel の「記入のしかた」のシートにあります。席番号が同じ席は同じ席として、人の席の割り当てが続きます。設定で「承認あり」にすると、登録した人と別の施設管理者・人事管理者が「組織の変更」の反映の記録で承認したときに反映します。
          </HelpTip>
        </div>
        <Button onClick={() => void exportAs("xlsx")}>{building ? `${building.name}の Excel を書き出す` : "Excel のひな形"}</Button>
        <button type="button" className={linkClass} disabled={!building || !siteCode} onClick={() => void exportAs("json")}>
          JSON で書き出す
        </button>
        <div className="flex flex-col gap-2 border-t border-border pt-3">
          <label htmlFor={fileId} className="flex cursor-pointer flex-col gap-1 text-xs">
            <span className="text-muted">登録する Excel・JSON</span>
            <input id={fileId} type="file" accept=".xlsx,.json,application/json,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" className="text-sm" onChange={(e) => void pick(e.currentTarget)} />
          </label>
          {problems.length > 0 && (
            <Notice tone="error">
              <ul className="m-0 flex max-h-40 list-none flex-col gap-0.5 overflow-y-auto p-0 text-xs">
                {problems.slice(0, 30).map((p) => (
                  <li key={p}>{p}</li>
                ))}
                {problems.length > 30 && <li>ほか {problems.length - 30} 件</li>}
              </ul>
            </Notice>
          )}
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
