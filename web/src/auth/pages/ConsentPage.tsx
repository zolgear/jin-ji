// 同意画面（/consent?request=）。AI エージェント（MCP クライアント）や社内のアプリに、ログイン中の人の権限で人事情報を渡してよいかを本人に尋ねる。
// 出すもの: クライアント名・登録の種類・戻り先のホスト・ループバックしか持たない場合の注意・要求スコープ・渡す範囲（項目の上限・MCP ツール）。
// 許可・拒否は POST /oauth/requests/{id}/decision（CSRF のヘッダー付き）で、応答の redirect_to（クライアントの redirect_uri）へ遷移する。
import { useEffect, useState } from "react";
import { ApiError } from "../../data/types";
import { withSource, type AuthSource } from "../authAdapter";
import { fieldLabel, MCP_TOOLS, mcpToolLabel, scopeInfo } from "../labels";
import { authErrorMessage } from "../messages";
import { loginUrl } from "../returnUrl";
import type { AuthAdapter, Consent } from "../types";
import { AuthFrame, Button, Icon, Loading, Notice, Panel, formatTime } from "../ui";

function clientHost(clientId: string): string | null {
  try {
    return new URL(clientId).host;
  } catch {
    return null;
  }
}

function FieldsList({ fields }: { fields: string[] }) {
  if (fields.includes("*")) {
    return <p className="m-0">あなたが <span className="whitespace-nowrap">jin-ji</span> で見られる<strong>すべての項目</strong>（氏名・所属・役職・連絡先・上長など）</p>;
  }
  if (fields.length === 0) {
    return <p className="m-0">項目はありません（このアプリには人の情報が渡りません）。</p>;
  }
  return (
    <ul className="m-0 flex list-none flex-wrap gap-1.5 p-0">
      {fields.map((f) => (
        <li key={f} className="rounded-sm border border-border bg-surface-2 px-2 py-0.5 text-xs">
          {fieldLabel(f)}
        </li>
      ))}
    </ul>
  );
}

function ToolsList({ tools }: { tools: string[] }) {
  if (tools.length === 0) {
    return <p className="m-0 text-muted">管理者がまだツールを許可していません。許可しても、今は人事情報を取得できません。</p>;
  }
  const names = tools.includes("*") ? MCP_TOOLS.map((t) => t.name) : tools;
  return (
    <ul className="m-0 grid list-none gap-x-4 gap-y-0.5 p-0 sm:grid-cols-2">
      {names.map((t) => (
        <li key={t} className="flex items-baseline gap-1.5 text-xs">
          <span aria-hidden="true" className="text-muted">・</span>
          {mcpToolLabel(t)}
        </li>
      ))}
    </ul>
  );
}

export function ConsentPage({ adapter, source }: { adapter: AuthAdapter; source: AuthSource }) {
  const requestId = new URLSearchParams(window.location.search).get("request") ?? "";
  const [consent, setConsent] = useState<Consent | null>(null);
  const [failure, setError] = useState<string | null>(null);
  const error = requestId ? failure : "許可の依頼が指定されていません。アプリから接続をやり直してください。";
  const [busy, setBusy] = useState<"approve" | "deny" | null>(null);
  const [decided, setDecided] = useState<{ approve: boolean; redirect: string } | null>(null);

  useEffect(() => {
    if (!requestId) return;
    adapter.getConsent(requestId).then(setConsent, (e: unknown) => {
      // ログインしていない（セッションの期限切れなど）ならログインして戻る
      if (e instanceof ApiError && e.status === 401) {
        window.location.assign(withSource(loginUrl(), source));
        return;
      }
      setError(authErrorMessage(e, "consent"));
    });
  }, [adapter, requestId, source]);

  const decide = async (approve: boolean) => {
    setBusy(approve ? "approve" : "deny");
    setError(null);
    try {
      const redirect = await adapter.decideConsent(requestId, approve);
      setDecided({ approve, redirect });
      if (redirect) window.location.assign(redirect);
    } catch (e) {
      setError(authErrorMessage(e, "consent"));
    } finally {
      setBusy(null);
    }
  };

  const isMcp = consent?.resource === "mcp";
  const host = consent ? clientHost(consent.client.client_id) : null;
  const propose = consent?.scopes.includes("jinji.propose") ?? false;

  return (
    <AuthFrame title="アクセスの許可" width="lg">
      {!consent && !error ? (
        <Loading label="許可の依頼を確認中" />
      ) : !consent ? (
        <Notice tone="error" title="許可の依頼を表示できません">
          {error}
        </Notice>
      ) : decided ? (
        <Panel>
          <Notice tone={decided.approve ? "ok" : "info"} title={decided.approve ? "許可しました" : "拒否しました"}>
            {decided.redirect
              ? `${consent.client.name} に戻っています…`
              : "デモのため、アプリへは戻りません。このタブを閉じてかまいません。"}
          </Notice>
        </Panel>
      ) : (
        <div className="flex flex-col gap-4">
          <div>
            <p className="m-0 text-sm text-muted">アクセスの許可</p>
            <h1 className="mt-1 mb-0 text-xl leading-snug font-bold sm:text-2xl">
              <span className="break-all">{consent.client.name}</span> が、あなたの権限で <span className="whitespace-nowrap">jin-ji</span> の人事情報を{propose ? "参照・提案" : "参照"}しようとしています
            </h1>
          </div>

          <Panel aria-labelledby="client-title" className="flex flex-col gap-3">
            <h2 id="client-title" className="sr-only">
              接続するアプリ
            </h2>
            <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
              <dt className="text-muted">アプリ</dt>
              <dd className="m-0 min-w-0">
                <span className="font-bold break-all">{consent.client.name}</span>
                <span className="mt-0.5 block text-xs text-muted">
                  {consent.client.registration === "registered"
                    ? "管理者が事前に登録したアプリです。"
                    : `アプリ自身が公開している情報（${host ?? consent.client.client_id}）で名乗っています。名前はアプリの申告で、jin-ji の管理者が確かめたものではありません。`}
                </span>
              </dd>
              <dt className="text-muted">戻り先</dt>
              <dd className="m-0 min-w-0">
                <span className="font-mono break-all">{consent.redirect_host}</span>
                <span className="mt-0.5 block text-xs text-muted">許可すると、アクセスの権利（コード）がこの宛先に渡ります。</span>
              </dd>
              <dt className="text-muted">使い方</dt>
              <dd className="m-0">{isMcp ? "AI エージェント（MCP）として人事情報を読み取ります" : "REST API で人事情報を読み取ります"}</dd>
            </dl>
          </Panel>

          {consent.client.loopback_only && (
            <Notice tone="warn" title="このパソコンで動くアプリです">
              戻り先が <span className="font-mono">{consent.redirect_host}</span>（このパソコン自身）のため、同じパソコンで動く別のアプリがこのアプリを名乗っている可能性があります。
              いま自分で接続を始めたのでなければ、<strong>拒否</strong>してください。
            </Notice>
          )}

          <Panel aria-labelledby="scope-title" className="flex flex-col gap-4">
            <h2 id="scope-title" className="m-0 text-base font-bold">
              許可すると、このアプリができること
            </h2>
            <ul className="m-0 flex list-none flex-col gap-3 p-0">
              {consent.scopes.map((s) => {
                const info = scopeInfo(s);
                return (
                  <li key={s} className="flex gap-2.5">
                    <Icon name={s === "jinji.propose" ? "shield" : "lock"} className="mt-0.5 size-4 shrink-0 text-accent" />
                    <div>
                      <p className="m-0 font-bold">{info.title}</p>
                      <p className="m-0 text-sm text-muted">{info.description}</p>
                    </div>
                  </li>
                );
              })}
            </ul>

            <div className="border-t border-border pt-4">
              <h3 className="m-0 mb-2 text-sm font-bold">渡す範囲</h3>
              <FieldsList fields={consent.client.fields} />
              <p className="mt-2 mb-0 text-xs text-muted">
                渡るのは、上のうち<strong>あなたが <span className="whitespace-nowrap">jin-ji</span> で見られる情報だけ</strong>です。あなたが見られない人・項目は渡りません。あなたが人事管理者の場合は、その権限で見られる情報も対象になります。
              </p>
            </div>

            {isMcp && (
              <div className="border-t border-border pt-4">
                <h3 className="m-0 mb-2 text-sm font-bold">使えるツール</h3>
                <ToolsList tools={consent.client.mcp_tools} />
              </div>
            )}
          </Panel>

          <Notice tone="info" title="許可する前に">
            <ul className="m-0 flex list-disc flex-col gap-1 pl-5">
              <li>
                アプリに渡った情報は、そのアプリ（{isMcp ? "AI エージェントと、その提供元のサービス" : "アプリの提供元"}）の中で処理・保存されることがあり、jin-ji から取り戻すことはできません。
              </li>
              <li>あなたが行った操作として記録されます。業務に必要なときだけ許可してください。</li>
              <li>
                許可は、あとから <span className="whitespace-nowrap">jin-ji</span> の「アカウント」画面（AI エージェントへの許可）でいつでも取り消せます。取り消すと、そのアプリは次の要求から情報を読めなくなります。
              </li>
            </ul>
          </Notice>

          {/* 許可・拒否は画面の下に固定し、どの幅でも読み込んだ時点で見えるようにする */}
          <div className="sticky bottom-0 -mx-4 flex flex-col gap-2 border-t border-border bg-surface px-4 py-3 shadow-[0_-4px_12px_rgb(0_0_0/0.06)] sm:mx-0 sm:rounded-t-lg sm:border-x">
            {error && <Notice tone="error">{error}</Notice>}
            <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
              <p className="m-0 min-w-0 flex-1 text-xs text-muted">
                <span className="break-all">{consent.client.name}</span> への許可（この依頼は {formatTime(consent.expires_at)} まで有効）
              </p>
              <div className="grid grid-cols-2 gap-2 sm:flex">
                <Button variant="secondary" onClick={() => void decide(false)} busy={busy === "deny"} disabled={busy !== null && busy !== "deny"}>
                  拒否する
                </Button>
                <Button variant="primary" onClick={() => void decide(true)} busy={busy === "approve"} disabled={busy !== null && busy !== "approve"}>
                  許可する
                </Button>
              </div>
            </div>
          </div>
        </div>
      )}
    </AuthFrame>
  );
}
