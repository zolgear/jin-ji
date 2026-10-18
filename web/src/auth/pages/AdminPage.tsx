// 認証の管理（/admin?tab=）。UI でログインした人事管理者と、緊急用の管理者アカウント（認証の復旧だけ）が使う。
// - 未紐付けのログイン: 一覧（GET /auth/admin/unbound-logins）と紐付け・取り消し
// - ローカルアカウント: 一覧（GET /auth/admin/local-accounts。ユーザー名で絞る）と、ロックの解除・リセットリンクの発行・無効化と再有効化
// - API クライアント（人事管理者だけ）: 一覧・停止・再開・承認待ちの CIMD の承認と却下・MCP ツールの許可リスト
// - CIMD の許可ドメイン（人事管理者だけ）: 一覧・追加・削除
// - Webhook（人事管理者だけ）: 社内システムへの Webhook の一覧・登録・停止と再開・送信先と設定の変更・鍵のローテーション・送信の確かめ・送信の一覧と dead の再送
// 操作はすべてサーバー側で監査ログに残る。
import { useCallback, useEffect, useId, useRef, useState, type FormEvent, type KeyboardEvent, type ReactNode } from "react";
import { withSource, type AuthSource } from "../authAdapter";
import {
  CLIENT_STATUS_LABELS,
  fieldLabel,
  MCP_TOOLS,
  mcpToolLabel,
  REGISTRATION_LABELS,
  RESOURCE_LABELS,
  scopeInfo,
  WEBHOOK_DELIVERY_KIND_LABELS,
  WEBHOOK_DELIVERY_STATUS_LABELS,
  WEBHOOK_STATUS_LABELS,
} from "../labels";
import { authErrorMessage } from "../messages";
import { loginUrl } from "../returnUrl";
import {
  canUseAdmin,
  isHrAdmin,
  type AuthAdapter,
  type CimdDomains,
  type DisclosureLead,
  type ClientAction,
  type LocalAccount,
  type ClientStatus,
  type OAuthClient,
  type PersonSummary,
  type ResetLink,
  type Session,
  type UnboundLogin,
  type Webhook,
  type WebhookDelivery,
  type WebhookDeliveryStatus,
  type WebhookSecretIssued,
  type WebhookSettings,
  type WebhookUpdate,
} from "../types";
import { AuthFrame, Button, ConfirmButton, CopyField, Icon, Loading, Notice, Panel, TextField, formatDateTime } from "../ui";

type Tab = "unbound" | "accounts" | "clients" | "webhooks" | "domains";

const TAB_LABELS: Record<Tab, string> = {
  unbound: "未紐付けのログイン",
  accounts: "ローカルアカウント",
  clients: "API クライアント",
  webhooks: "Webhook",
  domains: "CIMD の許可ドメイン",
};

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

type Flash = { tone: "ok" | "error"; text: string } | null;

/** 状態の札（色だけでなくアイコンと文字で示す）。 */
function StatusTag({ tone, children }: { tone: "ok" | "warn" | "muted" | "danger"; children: ReactNode }) {
  const cls = {
    ok: "border-accent/40 bg-accent-weak text-accent",
    warn: "border-warn-fg/30 bg-warn-bg text-warn-fg",
    muted: "border-border bg-surface-2 text-muted",
    danger: "border-danger/40 bg-surface text-danger",
  }[tone];
  const icon = { ok: "ok", warn: "warn", muted: "info", danger: "error" } as const;
  return (
    <span className={`inline-flex shrink-0 items-center gap-1 rounded-sm border px-1.5 text-[11px] leading-[20px] font-bold ${cls}`}>
      <Icon name={icon[tone]} className="size-3" />
      {children}
    </span>
  );
}

function useFlash(): [Flash, (f: Flash) => void] {
  const [flash, setFlash] = useState<Flash>(null);
  return [flash, setFlash];
}

// ---- 未紐付けのログイン -------------------------------------------------------------------------------------

function BindForm({
  adapter,
  row,
  canSearch,
  candidates,
  onBound,
  onCancel,
}: {
  adapter: AuthAdapter;
  row: UnboundLogin;
  canSearch: boolean;
  candidates: PersonSummary[];
  onBound: (message: string) => void;
  onCancel: () => void;
}) {
  const groupName = useId();
  const [selected, setSelected] = useState<PersonSummary | null>(candidates.length === 1 ? candidates[0]! : null);
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<PersonSummary[] | null>(null);
  const [manualId, setManualId] = useState("");
  const [busy, setBusy] = useState<"search" | "bind" | null>(null);
  const [error, setError] = useState<string | null>(null);

  const search = async (e: FormEvent) => {
    e.preventDefault();
    if (query.trim().length < 2) {
      setError("2 文字以上で検索してください。");
      return;
    }
    setBusy("search");
    setError(null);
    try {
      setResults(await adapter.searchPeople(query.trim()));
    } catch (err) {
      setError(authErrorMessage(err, "admin"));
    } finally {
      setBusy(null);
    }
  };

  const personId = canSearch ? selected?.id : manualId.trim() || selected?.id;
  const bind = async () => {
    if (!personId || !UUID.test(personId)) {
      setError(canSearch ? "紐付ける人を選んでください。" : "人の ID（jin-ji ID）を正しい形式で入力してください。");
      return;
    }
    setBusy("bind");
    setError(null);
    try {
      await adapter.bindLogin(row.id, personId);
      onBound(`${row.display_name ?? row.email ?? row.subject} を${selected && selected.id === personId ? ` ${selected.name} さんに` : ""}紐付けました。次回から、このアカウントでログインできます。`);
    } catch (err) {
      setError(authErrorMessage(err, "admin"));
    } finally {
      setBusy(null);
    }
  };

  const options = [...candidates, ...(results ?? []).filter((r) => !candidates.some((c) => c.id === r.id))];

  return (
    <div className="mt-3 flex flex-col gap-3 rounded-md border border-border bg-surface-2 p-3">
      <p className="m-0 text-sm font-bold">紐付ける人を選ぶ</p>
      {canSearch && (
        <form onSubmit={search} className="flex flex-wrap items-end gap-2">
          <TextField
            label="氏名・社員番号で検索"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            type="search"
            className="min-w-48 flex-1"
            autoComplete="off"
          />
          <Button type="submit" busy={busy === "search"}>
            検索
          </Button>
        </form>
      )}
      {options.length > 0 ? (
        <fieldset className="m-0 border-0 p-0">
          <legend className="mb-1 text-xs text-muted">{results ? "候補と検索の結果" : "候補（社員番号・社用メールが一致した人）"}</legend>
          <ul className="m-0 flex list-none flex-col gap-1 p-0">
            {options.map((p) => (
              <li key={p.id}>
                <label className="flex cursor-pointer items-start gap-2 rounded-sm px-2 py-1 hover:bg-surface">
                  <input
                    type="radio"
                    name={groupName}
                    checked={selected?.id === p.id}
                    onChange={() => {
                      setSelected(p);
                      setManualId("");
                    }}
                    className="mt-1"
                  />
                  <span>
                    <span className="font-bold">{p.name}</span>
                    {p.detail && <span className="ml-2 text-xs text-muted">{p.detail}</span>}
                    {candidates.some((c) => c.id === p.id) && <span className="ml-2 text-xs text-accent">候補</span>}
                  </span>
                </label>
              </li>
            ))}
          </ul>
        </fieldset>
      ) : results ? (
        <p className="m-0 text-sm text-muted">該当する人はいません。</p>
      ) : null}
      {!canSearch && (
        <TextField
          label="人の ID（jin-ji ID）"
          hint="緊急用アカウントは人事情報を見られないため、人の ID を人事管理者から聞いて入力します。"
          value={manualId}
          onChange={(e) => {
            setManualId(e.target.value);
            setSelected(null);
          }}
          spellCheck={false}
          className="font-mono"
        />
      )}
      {error && <Notice tone="error">{error}</Notice>}
      <p className="m-0 text-xs text-muted">
        紐付けると、この外部アカウント（{row.provider}）でログインした人は、選んだ人として jin-ji を使えるようになります。本人であることを確かめてから紐付けてください。
      </p>
      <div className="flex flex-wrap gap-2">
        <Button variant="primary" busy={busy === "bind"} onClick={() => void bind()}>
          この人に紐付ける
        </Button>
        <Button onClick={onCancel}>やめる</Button>
      </div>
    </div>
  );
}

function UnboundTab({ adapter, hrAdmin }: { adapter: AuthAdapter; hrAdmin: boolean }) {
  const [includeResolved, setIncludeResolved] = useState(false);
  const [data, setData] = useState<{ version: number; rows: UnboundLogin[]; people: Map<string, PersonSummary> } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [flash, setFlash] = useFlash();

  // 取り直しは version を進める（効果の中で同期的に state を変えない）
  const [version, setVersion] = useState(0);
  const load = () => setVersion((v) => v + 1);

  useEffect(() => {
    let cancelled = false;
    const requested = version;
    (async () => {
      const list = await adapter.listUnboundLogins(includeResolved);
      const ids = [...new Set(list.flatMap((r) => r.candidate_person_ids))];
      const found = hrAdmin ? await adapter.peopleByIds(ids).catch(() => []) : [];
      if (cancelled) return;
      setError(null);
      setData({ version: requested, rows: list, people: new Map(found.map((p) => [p.id, p])) });
    })().catch((e: unknown) => {
      if (!cancelled) setError(authErrorMessage(e, "admin"));
    });
    return () => {
      cancelled = true;
    };
  }, [adapter, includeResolved, hrAdmin, version]);

  const rows = data?.rows ?? null;
  const people = data?.people ?? new Map<string, PersonSummary>();
  const revoke = async (bindingId: string) => {
    try {
      await adapter.revokeBinding(bindingId);
      setFlash({ tone: "ok", text: "紐付けを取り消しました。その紐付けでログイン中のセッションも終了しました。" });
      load();
    } catch (e) {
      setFlash({ tone: "error", text: authErrorMessage(e, "admin") });
    }
  };

  return (
    <section aria-labelledby="unbound-title" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 id="unbound-title" className="sr-only">
            未紐付けのログイン
          </h2>
          <p className="m-0 text-sm text-muted">
            会社のアカウントでサインインできたものの、jin-ji の社員情報と自動では結び付けられず、ログインを断ったものです。本人を確かめて紐付けます。
          </p>
        </div>
        <label className="flex items-center gap-2 text-sm">
          <input type="checkbox" checked={includeResolved} onChange={(e) => setIncludeResolved(e.target.checked)} />
          解決済みも表示
        </label>
      </div>
      {flash && <Notice tone={flash.tone}>{flash.text}</Notice>}
      {error ? (
        <Notice tone="error">{error}</Notice>
      ) : !rows ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Panel>
          <p className="m-0 text-muted">{includeResolved ? "記録はありません。" : "未解決のものはありません。"}</p>
        </Panel>
      ) : (
        <ul className="m-0 flex list-none flex-col gap-3 p-0">
          {rows.map((row) => {
            const candidates = row.candidate_person_ids.map((id) => people.get(id) ?? { id, name: hrAdmin ? "（表示できない人）" : `ID ${id}`, detail: null });
            const resolved = row.resolved_at !== null;
            return (
              <li key={row.id}>
                <Panel as="div" className="!p-4">
                  <div className="flex flex-wrap items-start justify-between gap-2">
                    <div className="min-w-0">
                      <p className="m-0 flex flex-wrap items-center gap-2">
                        <span className="text-base font-bold">{row.display_name ?? "（名前の情報なし）"}</span>
                        {resolved ? <StatusTag tone="ok">解決済み</StatusTag> : <StatusTag tone="warn">未解決</StatusTag>}
                      </p>
                      <p className="m-0 text-sm break-all text-muted">
                        {[row.email, row.employee_number ? `社員番号 ${row.employee_number}` : null].filter(Boolean).join("・") || "メール・社員番号の情報なし"}
                      </p>
                    </div>
                    {!resolved && open !== row.id && (
                      <Button variant="primary" className="min-h-8 px-3 py-1 text-xs" onClick={() => setOpen(row.id)}>
                        紐付ける
                      </Button>
                    )}
                    {resolved && row.resolved_binding_id && (
                      <div className="w-full sm:w-auto">
                        <ConfirmButton
                          label="紐付けを取り消す"
                          variant="danger"
                          message="取り消すと、このアカウントではログインできなくなり、ログイン中のセッションも終わります。"
                          confirmLabel="取り消す"
                          onConfirm={() => void revoke(row.resolved_binding_id!)}
                        />
                      </div>
                    )}
                  </div>
                  <dl className="mt-3 mb-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs sm:grid-cols-[auto_1fr_auto_1fr]">
                    <dt className="text-muted">ログイン方式</dt>
                    <dd className="m-0">{row.provider}</dd>
                    <dt className="text-muted">試行</dt>
                    <dd className="m-0 tabular">
                      {row.attempt_count} 回（最後 {formatDateTime(row.last_attempted_at)}）
                    </dd>
                    <dt className="text-muted">識別子</dt>
                    <dd className="m-0 font-mono break-all sm:col-span-3">{row.subject}</dd>
                    {resolved && (
                      <>
                        <dt className="text-muted">解決</dt>
                        <dd className="m-0 sm:col-span-3">{formatDateTime(row.resolved_at!)}</dd>
                      </>
                    )}
                    {!resolved && candidates.length > 0 && open !== row.id && (
                      <>
                        <dt className="text-muted">候補</dt>
                        <dd className="m-0 sm:col-span-3">{candidates.map((c) => c.name).join("、")}</dd>
                      </>
                    )}
                  </dl>
                  {open === row.id && (
                    <BindForm
                      adapter={adapter}
                      row={row}
                      canSearch={hrAdmin}
                      candidates={candidates}
                      onCancel={() => setOpen(null)}
                      onBound={(text) => {
                        setOpen(null);
                        setFlash({ tone: "ok", text });
                        load();
                      }}
                    />
                  )}
                </Panel>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

// ---- ローカルアカウント -------------------------------------------------------------------------------------

/** アカウントの状態の札。 */
function AccountTags({ account, now }: { account: LocalAccount; now: number }) {
  const locked = account.lockout_end !== null && Date.parse(account.lockout_end) > now;
  const mfa = account.passkey_count > 0 || account.two_factor_enabled;
  return (
    <span className="flex flex-wrap items-center gap-1">
      {account.disabled_at ? <StatusTag tone="danger">無効</StatusTag> : locked ? <StatusTag tone="warn">ロック中</StatusTag> : <StatusTag tone="ok">有効</StatusTag>}
      {account.kind === "break_glass" && <StatusTag tone="muted">緊急用</StatusTag>}
      {!account.has_password && account.passkey_count === 0 && <StatusTag tone="warn">資格情報が未設定</StatusTag>}
      {account.kind === "person" && !mfa && <StatusTag tone="muted">多要素なし</StatusTag>}
    </span>
  );
}

function AccountCard({
  account,
  person,
  now,
  adapter,
  onChanged,
}: {
  account: LocalAccount;
  person: PersonSummary | null;
  /** 一覧を読んだ時刻（ロック中かの判定。描画のたびに時計を読まない）。 */
  now: number;
  adapter: AuthAdapter;
  onChanged: (flash: Flash, link?: ResetLink) => void;
}) {
  const [busy, setBusy] = useState<"unlock" | "password" | "passkey" | "disable" | "enable" | null>(null);
  const breakGlass = account.kind === "break_glass";

  const run = async (kind: NonNullable<typeof busy>) => {
    setBusy(kind);
    try {
      if (kind === "unlock") {
        await adapter.unlockAccount(account.id);
        onChanged({ tone: "ok", text: `${account.user_name} のロックを解除しました。本人はすぐにログインを試せます。` });
      } else if (kind === "disable") {
        const r = await adapter.disableAccount(account.id);
        onChanged({
          tone: "ok",
          text: `${account.user_name} を無効にしました（終えたセッション ${r.sessions_ended} 件、取り消した AI エージェントへの許可 ${r.delegations_revoked} 件）。`,
        });
      } else if (kind === "enable") {
        await adapter.enableAccount(account.id);
        onChanged({ tone: "ok", text: `${account.user_name} を有効に戻しました。本人はもう一度ログインし、AI エージェントへの許可もやり直します。` });
      } else {
        onChanged(null, await adapter.issueResetLink(account.id, kind));
      }
    } catch (e) {
      onChanged({ tone: "error", text: authErrorMessage(e, "admin") });
    } finally {
      setBusy(null);
    }
  };

  return (
    <Panel as="div" className="!p-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="m-0 flex flex-wrap items-center gap-2">
            <span className="font-mono text-base font-bold break-all">{account.user_name}</span>
            <AccountTags account={account} now={now} />
          </p>
          <p className="m-0 text-sm break-all text-muted">
            {breakGlass ? "緊急用の管理者アカウント（人事情報は見られない）" : person ? [person.name, person.detail].filter(Boolean).join("・") : `人 ID ${account.person_id}`}
          </p>
        </div>
      </div>
      <dl className="mt-3 mb-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs sm:grid-cols-[auto_1fr_auto_1fr]">
        <dt className="text-muted">ログインの方法</dt>
        <dd className="m-0">
          {[account.has_password ? "パスワード" : null, account.two_factor_enabled ? "認証アプリ" : null, account.passkey_count > 0 ? `パスキー ${account.passkey_count} 個` : null]
            .filter(Boolean)
            .join("・") || "なし"}
        </dd>
        <dt className="text-muted">作成</dt>
        <dd className="m-0">{formatDateTime(account.created_at)}</dd>
        {account.disabled_at && (
          <>
            <dt className="text-muted">無効化</dt>
            <dd className="m-0">{formatDateTime(account.disabled_at)}</dd>
          </>
        )}
        {account.lockout_end && Date.parse(account.lockout_end) > now && (
          <>
            <dt className="text-muted">ロック</dt>
            <dd className="m-0">{formatDateTime(account.lockout_end)} まで</dd>
          </>
        )}
        <dt className="text-muted">アカウント ID</dt>
        <dd className="m-0 font-mono break-all sm:col-span-3">{account.id}</dd>
      </dl>
      <div className="mt-3 flex flex-wrap gap-2">
        {account.disabled_at ? (
          <Button className="min-h-8 px-3 py-1 text-xs" busy={busy === "enable"} onClick={() => void run("enable")}>
            有効に戻す
          </Button>
        ) : (
          <>
            <Button className="min-h-8 px-3 py-1 text-xs" busy={busy === "unlock"} onClick={() => void run("unlock")}>
              <Icon name="lock" />
              ロックを解除
            </Button>
            {!breakGlass && (
              <Button className="min-h-8 px-3 py-1 text-xs" busy={busy === "password"} onClick={() => void run("password")}>
                パスワードの再設定リンク
              </Button>
            )}
            <Button className="min-h-8 px-3 py-1 text-xs" busy={busy === "passkey"} onClick={() => void run("passkey")}>
              <Icon name="key" />
              パスキーの登録リンク
            </Button>
            <div className="w-full sm:w-auto">
              <ConfirmButton
                label="無効にする"
                variant="danger"
                busy={busy === "disable"}
                message={
                  breakGlass
                    ? "無効にすると、このアカウントではログインできなくなり、ログイン中のセッションも終わります。"
                    : "無効にすると、このアカウントではログインできなくなり、ログイン中のセッションと、この人の AI エージェントへの許可（会社のアカウントで許可したものも）がすべて取り消されます。"
                }
                confirmLabel="無効にする"
                onConfirm={() => void run("disable")}
              />
            </div>
          </>
        )}
      </div>
    </Panel>
  );
}

function AccountsTab({ adapter, hrAdmin }: { adapter: AuthAdapter; hrAdmin: boolean }) {
  const [query, setQuery] = useState("");
  const [applied, setApplied] = useState("");
  const [data, setData] = useState<{ version: number; rows: LocalAccount[]; people: Map<string, PersonSummary>; loadedAt: number } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [flash, setFlash] = useFlash();
  const [link, setLink] = useState<ResetLink | null>(null);
  const [version, setVersion] = useState(0);

  // 取り直しは version を進める（効果の中で同期的に state を変えない）
  useEffect(() => {
    let cancelled = false;
    const requested = version;
    (async () => {
      const rows = await adapter.listLocalAccounts(applied);
      const ids = [...new Set(rows.flatMap((r) => (r.person_id ? [r.person_id] : [])))];
      // 緊急用アカウントは人事情報を見られないので、人の名前は人事管理者のときだけ引く
      const found = hrAdmin ? await adapter.peopleByIds(ids).catch(() => []) : [];
      if (cancelled) return;
      setError(null);
      setData({ version: requested, rows, people: new Map(found.map((p) => [p.id, p])), loadedAt: Date.now() });
    })().catch((e: unknown) => {
      if (!cancelled) setError(authErrorMessage(e, "admin"));
    });
    return () => {
      cancelled = true;
    };
  }, [adapter, applied, hrAdmin, version]);

  const search = (e: FormEvent) => {
    e.preventDefault();
    setApplied(query.trim());
  };

  return (
    <section aria-labelledby="accounts-title" className="flex flex-col gap-4">
      <div>
        <h2 id="accounts-title" className="sr-only">
          ローカルアカウント
        </h2>
        <p className="m-0 text-sm text-muted">
          jin-ji のアカウント（パスワード・パスキー）と緊急用の管理者アカウント。ロックの解除、パスワードの再設定・パスキーの登録のリンクの発行、無効化と再有効化ができます。自分のアカウントには使えません。アカウントの作成は管理 CLI で行います。
        </p>
      </div>
      <form onSubmit={search} className="flex flex-col gap-2 sm:flex-row sm:items-end">
        <TextField
          label="ユーザー名で絞り込む"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          maxLength={64}
          spellCheck={false}
          autoComplete="off"
          className="flex-1"
        />
        <Button type="submit" className="sm:mb-5">
          絞り込む
        </Button>
      </form>
      {flash && <Notice tone={flash.tone}>{flash.text}</Notice>}
      {link && (
        <Panel className="flex flex-col gap-3" aria-live="polite">
          <Notice tone="ok" title={`${link.purpose === "passkey" ? "パスキーの登録" : "パスワードの再設定"}のリンクを発行しました`}>
            {formatDateTime(link.expires_at)} まで、1 回だけ使えます。同じアカウント・同じ用途のまだ使われていないリンクは無効になりました。
          </Notice>
          <Notice tone="warn" title="このリンクはこの画面でだけ表示されます">
            jin-ji はメールを送りません。本人であることを確かめたうえで、社内チャットの個別メッセージなど安全な経路で本人だけに渡してください。
          </Notice>
          {link.url ? <CopyField label="リンク" value={link.url} /> : <CopyField label="リンクの鍵（公開 URL が未設定のため URL を作れません）" value={link.token} />}
        </Panel>
      )}
      {error ? (
        <Notice tone="error">{error}</Notice>
      ) : !data ? (
        <Loading />
      ) : data.rows.length === 0 ? (
        <Panel>
          <p className="m-0 text-muted">{applied ? "一致するアカウントはありません。" : "ローカルアカウントはありません（管理 CLI の create-local-account で作ります）。"}</p>
        </Panel>
      ) : (
        <ul aria-label="ローカルアカウント" className="m-0 flex list-none flex-col gap-3 p-0">
          {data.rows.map((account) => (
            <li key={account.id}>
              <AccountCard
                account={account}
                person={account.person_id ? (data.people.get(account.person_id) ?? null) : null}
                now={data.loadedAt}
                adapter={adapter}
                onChanged={(next, issued) => {
                  setFlash(next);
                  setLink(issued ?? null);
                  if (next?.tone === "ok") setVersion((v) => v + 1);
                }}
              />
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}

// ---- API クライアント ---------------------------------------------------------------------------------------

const STATUS_TONE: Record<ClientStatus, "ok" | "warn" | "muted" | "danger"> = {
  active: "ok",
  pending_approval: "warn",
  suspended: "muted",
  rejected: "danger",
};

function ToolsEditor({ client, onSave, onCancel }: { client: OAuthClient; onSave: (tools: string[]) => Promise<void>; onCancel: () => void }) {
  const [all, setAll] = useState(client.mcp_tools.includes("*"));
  const [tools, setTools] = useState<Set<string>>(new Set(client.mcp_tools.filter((t) => t !== "*")));
  const [busy, setBusy] = useState(false);
  const toggle = (name: string) =>
    setTools((prev) => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name);
      else next.add(name);
      return next;
    });
  const save = async () => {
    setBusy(true);
    try {
      await onSave(all ? ["*"] : MCP_TOOLS.map((t) => t.name).filter((n) => tools.has(n)));
    } finally {
      setBusy(false);
    }
  };
  const proposeWithoutScope = !client.scopes.includes("jinji.propose") && (all || MCP_TOOLS.some((t) => t.kind === "propose" && tools.has(t.name)));
  return (
    <fieldset className="m-0 mt-3 flex flex-col gap-3 rounded-md border border-border bg-surface-2 p-3">
      <legend className="px-1 text-sm font-bold">MCP ツールの許可リスト</legend>
      <div className="flex items-start gap-2 text-sm">
        <input id={`${client.id}-all`} type="checkbox" checked={all} onChange={(e) => setAll(e.target.checked)} className="mt-1" aria-describedby={`${client.id}-all-hint`} />
        <div>
          <label htmlFor={`${client.id}-all`} className="font-bold">
            すべてのツール（*）
          </label>
          <p id={`${client.id}-all-hint`} className="m-0 text-xs text-muted">
            今後追加されるツールも自動で許可されます。
          </p>
        </div>
      </div>
      {(["read", "propose"] as const).map((kind) => (
        <div key={kind}>
          <p className="m-0 mb-1 text-xs font-bold text-muted">{kind === "read" ? "参照（jinji.read）" : "提案（jinji.propose）"}</p>
          <ul className="m-0 grid list-none gap-x-4 gap-y-0.5 p-0 sm:grid-cols-2">
            {MCP_TOOLS.filter((t) => t.kind === kind).map((t) => (
              <li key={t.name}>
                <label className={`flex items-start gap-2 text-sm ${all ? "text-muted" : ""}`}>
                  <input type="checkbox" disabled={all} checked={all || tools.has(t.name)} onChange={() => toggle(t.name)} className="mt-1" />
                  <span>
                    {t.label}
                    <span className="ml-1.5 font-mono text-[11px] text-muted">{t.name}</span>
                  </span>
                </label>
              </li>
            ))}
          </ul>
        </div>
      ))}
      {!all && tools.size === 0 && <Notice tone="warn">何も選ばないと、このクライアントはどのツールも使えません。</Notice>}
      {proposeWithoutScope && <Notice tone="info">このクライアントのスコープに jinji.propose が無いため、提案のツールは許可しても使えません。</Notice>}
      <div className="flex flex-wrap gap-2">
        <Button variant="primary" busy={busy} onClick={() => void save()}>
          保存
        </Button>
        <Button onClick={onCancel}>やめる</Button>
      </div>
    </fieldset>
  );
}

/** 先行提供（社内システムだけ）: 現在の日数・承認待ちの起案の承認／却下・新しい起案。 */
function DisclosureLeadSection({
  client,
  adapter,
  personId,
  onChanged,
}: {
  client: OAuthClient;
  adapter: AuthAdapter;
  personId: string | null;
  onChanged: (c: OAuthClient, message: string) => void;
}) {
  const [pending, setPending] = useState<DisclosureLead | null | undefined>(undefined);
  const [proposerName, setProposerName] = useState<string | null>(null);
  const [days, setDays] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState<"approve" | "reject" | "propose" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const hintId = useId();
  const name = client.display_name ?? client.client_id;

  // 取り直しは version を進める（効果の中で同期的に state を変えない）
  const [version, setVersion] = useState(0);
  const [loadedVersion, setLoadedVersion] = useState(-1);
  const load = () => setVersion((v) => v + 1);

  useEffect(() => {
    let cancelled = false;
    const requested = version;
    (async () => {
      const list = await adapter.listDisclosureLeads(client.id);
      const p = list.find((l) => l.status === "proposed") ?? null;
      const found = p && p.proposed_by_kind === "person" && p.proposed_by !== personId ? await adapter.peopleByIds([p.proposed_by]).catch(() => []) : [];
      if (cancelled) return;
      setPending(p);
      setProposerName(found[0]?.name ?? null);
      setLoadedVersion(requested);
    })().catch((err: unknown) => {
      if (cancelled) return;
      setPending(null);
      setLoadedVersion(requested);
      setError(authErrorMessage(err, "admin"));
    });
    return () => {
      cancelled = true;
    };
  }, [adapter, client.id, personId, version]);

  // 承認でクライアントの現在値が変わるので、一覧から取り直して親に渡す
  const refreshed = async (message: string) => {
    const next = (await adapter.listClients()).find((c) => c.id === client.id) ?? client;
    load();
    onChanged(next, message);
  };

  const decide = async (approve: boolean) => {
    setBusy(approve ? "approve" : "reject");
    setError(null);
    try {
      const r = await adapter.decideDisclosureLead(pending!.id, approve);
      await refreshed(approve ? `${name} の先行提供を ${r.lead_days} 日にしました。` : `${name} の先行提供の起案（${r.lead_days} 日）を却下しました。`);
    } catch (e) {
      setError(authErrorMessage(e, "admin"));
    } finally {
      setBusy(null);
    }
  };

  const propose = async (ev: FormEvent) => {
    ev.preventDefault();
    const n = Number(days);
    if (days.trim() === "" || !Number.isInteger(n) || n < 0 || n > 90) {
      setError("日数は 0〜90 の整数で指定してください。");
      return;
    }
    setBusy("propose");
    setError(null);
    try {
      const r = await adapter.proposeDisclosureLead(client.id, n, note.trim() || null);
      setDays("");
      setNote("");
      await refreshed(`${name} の先行提供を ${r.lead_days} 日にする起案をしました。別の人事管理者の承認で有効になります。`);
    } catch (e) {
      setError(authErrorMessage(e, "admin"));
    } finally {
      setBusy(null);
    }
  };

  const mine = pending != null && pending.proposed_by === personId;
  const stale = loadedVersion !== version; // 取り直し中は操作できない
  const current = client.disclosure_lead_days;

  return (
    <div className="mt-3 border-t border-border pt-3 text-xs">
      <p className="m-0 flex flex-wrap items-center gap-2">
        <span className="font-bold">先行提供</span>
        <span>{current === 0 ? "なし（発令日まで将来の予定を見せない）" : `有効日の ${current} 日前から`}</span>
      </p>
      {pending && (
        <div className="mt-2 rounded-md border border-border bg-surface-2 p-3">
          <p className="m-0 flex flex-wrap items-center gap-2">
            <StatusTag tone="warn">承認待ち</StatusTag>
            <span>{pending.lead_days === 0 ? "なし（0 日）にする起案" : `有効日の ${pending.lead_days} 日前からにする起案`}</span>
          </p>
          <p className="mt-1 mb-0 text-muted">
            起案: {mine ? "あなたの起案" : (proposerName ?? (pending.proposed_by_kind === "cli" ? "管理 CLI" : "別の人事管理者"))}・{formatDateTime(pending.proposed_at)}
            {pending.note && <>・理由: {pending.note}</>}
          </p>
          <div className="mt-2 flex flex-wrap gap-2">
            <Button variant="primary" className="min-h-8 px-3 py-1 text-xs" disabled={mine || stale} busy={busy === "approve"} onClick={() => void decide(true)}>
              承認
            </Button>
            <Button className="min-h-8 px-3 py-1 text-xs" disabled={mine || stale} busy={busy === "reject"} onClick={() => void decide(false)}>
              却下
            </Button>
          </div>
          {mine && <p className="mt-2 mb-0 text-muted">起案者は自分の起案を承認・却下できません（職務分掌）。別の人事管理者が承認します。</p>}
        </div>
      )}
      {pending === null && client.status !== "rejected" && (
        <form onSubmit={(e) => void propose(e)} className="mt-2 flex flex-col gap-2" aria-describedby={hintId}>
          <p id={hintId} className="m-0 text-muted">
            有効日の N 日前から、このシステムに将来の予定（内示・組織改編・異動）を見せます。人の委任（AI エージェント・UI）には効きません。
            内示の秘匿を緩める操作なので、別の人事管理者の承認で有効になります。すぐに止めるときはクライアントを停止します。
          </p>
          <div className="flex flex-col gap-2 sm:flex-row sm:items-start">
            <TextField label="日数（0〜90）" type="number" inputMode="numeric" min={0} max={90} step={1} value={days} onChange={(e) => setDays(e.target.value)} className="sm:w-40" />
            <TextField label="理由（任意）" value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} autoComplete="off" className="flex-1" />
            <Button type="submit" className="min-h-8 px-3 py-1 text-xs sm:mt-7" busy={busy === "propose"}>
              起案
            </Button>
          </div>
        </form>
      )}
      {error && (
        <Notice tone="error" className="mt-2">
          {error}
        </Notice>
      )}
    </div>
  );
}

function ClientCard({
  client,
  adapter,
  personId,
  onChanged,
}: {
  client: OAuthClient;
  adapter: AuthAdapter;
  personId: string | null;
  onChanged: (c: OAuthClient, message: string) => void;
}) {
  const [busy, setBusy] = useState<ClientAction | null>(null);
  const [editing, setEditing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const isMcp = client.resources.includes("mcp");

  const act = async (action: ClientAction) => {
    setBusy(action);
    setError(null);
    try {
      const next = await adapter.setClientStatus(client.id, action);
      const verb = { suspend: "停止しました", activate: "再開しました", approve: "承認しました", reject: "却下しました" }[action];
      onChanged(next, `${client.display_name ?? client.client_id} を${verb}。`);
    } catch (e) {
      setError(authErrorMessage(e, "admin"));
    } finally {
      setBusy(null);
    }
  };

  const saveTools = async (tools: string[]) => {
    setError(null);
    try {
      const next = await adapter.updateClient(client.id, { mcpTools: tools });
      setEditing(false);
      onChanged(next, `${client.display_name ?? client.client_id} の MCP ツールの許可リストを保存しました。`);
    } catch (e) {
      setError(authErrorMessage(e, "admin"));
    }
  };

  return (
    <Panel as="div" className="!p-4">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="m-0 flex flex-wrap items-center gap-2">
            <span className="text-base font-bold">{client.display_name ?? "（名前の申告なし）"}</span>
            <StatusTag tone={STATUS_TONE[client.status]}>{CLIENT_STATUS_LABELS[client.status]}</StatusTag>
            <span className="rounded-sm border border-border px-1.5 text-[11px] leading-[20px] text-muted">{REGISTRATION_LABELS[client.registration]}</span>
          </p>
          <p className="m-0 font-mono text-xs break-all text-muted">{client.client_id}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {client.status === "pending_approval" && (
            <>
              <Button variant="primary" className="min-h-8 px-3 py-1 text-xs" busy={busy === "approve"} onClick={() => void act("approve")}>
                承認
              </Button>
              <ConfirmButton
                label="却下"
                variant="danger"
                message="却下すると、このクライアントからの接続は拒否されたままになります（あとから承認には戻せません）。"
                busy={busy === "reject"}
                onConfirm={() => void act("reject")}
              />
            </>
          )}
          {client.status === "active" && (
            <ConfirmButton
              label="停止"
              variant="danger"
              message="停止すると、このクライアントのトークンは次の要求から使えなくなります（再開すれば元に戻ります）。"
              busy={busy === "suspend"}
              onConfirm={() => void act("suspend")}
            />
          )}
          {client.status === "suspended" && (
            <Button className="min-h-8 px-3 py-1 text-xs" busy={busy === "activate"} onClick={() => void act("activate")}>
              再開
            </Button>
          )}
        </div>
      </div>
      <dl className="mt-3 mb-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
        <dt className="text-muted">用途</dt>
        <dd className="m-0">
          {client.resources.map((r) => RESOURCE_LABELS[r] ?? r).join("・")}
          {client.grant_types.includes("client_credentials") && <span className="text-muted">（社内システム・client credentials）</span>}
        </dd>
        <dt className="text-muted">スコープ</dt>
        <dd className="m-0">{client.scopes.map((s) => scopeInfo(s).title).join("・") || "なし"}</dd>
        <dt className="text-muted">項目の上限</dt>
        <dd className="m-0">{client.fields.includes("*") ? "すべての項目" : client.fields.map(fieldLabel).join("・") || "なし"}</dd>
        {isMcp && (
          <>
            <dt className="text-muted">MCP ツール</dt>
            <dd className="m-0">
              {client.mcp_tools.length === 0 ? (
                <span className="text-warn-fg">なし（どのツールも使えません）</span>
              ) : (
                client.mcp_tools.map(mcpToolLabel).join("・")
              )}
              {!editing && client.status !== "rejected" && (
                <button type="button" onClick={() => setEditing(true)} className="ml-2 text-accent underline underline-offset-2">
                  変更
                </button>
              )}
            </dd>
          </>
        )}
        {client.redirect_uris.length > 0 && (
          <>
            <dt className="text-muted">戻り先</dt>
            <dd className="m-0 font-mono break-all">{client.redirect_uris.join(" ")}</dd>
          </>
        )}
        <dt className="text-muted">登録</dt>
        <dd className="m-0">{formatDateTime(client.created_at)}</dd>
      </dl>
      {editing && <ToolsEditor client={client} onSave={saveTools} onCancel={() => setEditing(false)} />}
      {client.grant_types.includes("client_credentials") && <DisclosureLeadSection client={client} adapter={adapter} personId={personId} onChanged={onChanged} />}
      {error && (
        <Notice tone="error" className="mt-3">
          {error}
        </Notice>
      )}
    </Panel>
  );
}

function ClientsTab({ adapter, personId }: { adapter: AuthAdapter; personId: string | null }) {
  const [filter, setFilter] = useState<ClientStatus | "">("");
  const [loaded, setLoaded] = useState<{ filter: ClientStatus | ""; list: OAuthClient[] } | null>(null);
  const clients = loaded?.filter === filter ? loaded.list : null;
  const setClients = (update: (list: OAuthClient[] | null) => OAuthClient[] | null) =>
    setLoaded((prev) => (prev ? { filter: prev.filter, list: update(prev.list) ?? [] } : prev));
  const [error, setError] = useState<string | null>(null);
  const [flash, setFlash] = useFlash();
  const selectId = useId();

  useEffect(() => {
    let cancelled = false;
    adapter.listClients(filter || undefined).then(
      (list) => {
        if (!cancelled) setLoaded({ filter, list });
      },
      (e: unknown) => {
        if (!cancelled) setError(authErrorMessage(e, "admin"));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [adapter, filter]);

  const changed = (next: OAuthClient, message: string) => {
    setClients((list) => (list ? list.map((c) => (c.id === next.id ? next : c)).filter((c) => !filter || c.status === filter) : list));
    setFlash({ tone: "ok", text: message });
  };

  const pending = clients?.filter((c) => c.status === "pending_approval") ?? [];
  const others = clients?.filter((c) => c.status !== "pending_approval") ?? [];

  return (
    <section aria-labelledby="clients-title" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h2 id="clients-title" className="sr-only">
            API クライアント
          </h2>
          <p className="m-0 text-sm text-muted">jin-ji のトークンで人事情報にアクセスするアプリ（AI エージェント・社内システム）。登録・鍵の管理は管理 CLI で行います。</p>
        </div>
        <div className="flex items-center gap-2 text-sm">
          <label htmlFor={selectId} className="text-muted">
            状態
          </label>
          <select id={selectId} value={filter} onChange={(e) => setFilter(e.target.value as ClientStatus | "")} className="rounded-md border border-border bg-surface px-2 py-1.5">
            <option value="">すべて</option>
            {(Object.keys(CLIENT_STATUS_LABELS) as ClientStatus[]).map((s) => (
              <option key={s} value={s}>
                {CLIENT_STATUS_LABELS[s]}
              </option>
            ))}
          </select>
        </div>
      </div>
      {flash && <Notice tone={flash.tone}>{flash.text}</Notice>}
      {error ? (
        <Notice tone="error">{error}</Notice>
      ) : !clients ? (
        <Loading />
      ) : clients.length === 0 ? (
        <Panel>
          <p className="m-0 text-muted">該当するクライアントはありません。</p>
        </Panel>
      ) : (
        <>
          {pending.length > 0 && (
            <div className="flex flex-col gap-3">
              <Notice tone="warn" title={`承認待ちの CIMD クライアント（${pending.length}）`}>
                許可ドメインの外のホストから、自分の情報（CIMD の文書）を示して接続しようとしたアプリです。承認するまで、利用者はこのアプリを許可できません。
                承認すると文書を取得して登録し、スコープ・項目・ツールは既定の値になります（ツールは既定で何も許しません）。
              </Notice>
              <ul className="m-0 flex list-none flex-col gap-3 p-0">
                {pending.map((c) => (
                  <li key={c.id}>
                    <ClientCard client={c} adapter={adapter} personId={personId} onChanged={changed} />
                  </li>
                ))}
              </ul>
            </div>
          )}
          {others.length > 0 && (
            <ul className="m-0 flex list-none flex-col gap-3 p-0">
              {others.map((c) => (
                <li key={c.id}>
                  <ClientCard client={c} adapter={adapter} personId={personId} onChanged={changed} />
                </li>
              ))}
            </ul>
          )}
        </>
      )}
    </section>
  );
}

// ---- 社内システムへの Webhook --------------------------------------------------------------------------------

const WEBHOOK_DELIVERY_TONE: Record<WebhookDeliveryStatus, "ok" | "warn" | "muted" | "danger"> = {
  pending: "warn",
  sending: "muted",
  succeeded: "ok",
  dead: "danger",
};

/** 旧鍵で署名を続ける時間の選択肢（0〜168 時間。既定 24）。 */
const OVERLAP_CHOICES = [
  { hours: 0, label: "重ねない（旧鍵はすぐ使わない）" },
  { hours: 1, label: "1 時間" },
  { hours: 24, label: "24 時間（既定）" },
  { hours: 72, label: "3 日" },
  { hours: 168, label: "7 日" },
];

/** 登録・変更の設定の入力（空欄は登録では既定、変更では今のまま）。整数でなければ null。 */
function parseSettings(values: { max: string; threshold: string; interval: string }): WebhookSettings | null {
  const read = (v: string): number | undefined | null => {
    if (v.trim() === "") return undefined;
    const n = Number(v);
    return Number.isInteger(n) ? n : null;
  };
  const max = read(values.max);
  const threshold = read(values.threshold);
  const interval = read(values.interval);
  if (max === null || threshold === null || interval === null) return null;
  return { maxEventsPerDelivery: max, summaryThreshold: threshold, minIntervalSeconds: interval };
}

/** 1 回の件数・要約に切り替える件数・送信の間隔の入力欄。 */
function WebhookSettingsFields({
  values,
  onChange,
  placeholders,
}: {
  values: { max: string; threshold: string; interval: string };
  onChange: (next: { max: string; threshold: string; interval: string }) => void;
  placeholders: { max: string; threshold: string; interval: string };
}) {
  return (
    <div className="grid gap-2 sm:grid-cols-3">
      <TextField
        label="1 回に送る件数（1〜1000）"
        type="number"
        inputMode="numeric"
        min={1}
        max={1000}
        step={1}
        value={values.max}
        placeholder={placeholders.max}
        onChange={(e) => onChange({ ...values, max: e.target.value })}
      />
      <TextField
        label="要約に切り替える件数"
        type="number"
        inputMode="numeric"
        min={1}
        max={100000}
        step={1}
        value={values.threshold}
        placeholder={placeholders.threshold}
        onChange={(e) => onChange({ ...values, threshold: e.target.value })}
        hint="1 回に送る件数以上。これを超える変更は件数だけの要約で送ります。"
      />
      <TextField
        label="送信の間隔（秒、1〜3600）"
        type="number"
        inputMode="numeric"
        min={1}
        max={3600}
        step={1}
        value={values.interval}
        placeholder={placeholders.interval}
        onChange={(e) => onChange({ ...values, interval: e.target.value })}
      />
    </div>
  );
}

/** 送信先と設定の変更。 */
function WebhookEditor({ webhook, onSave, onCancel }: { webhook: Webhook; onSave: (update: WebhookUpdate) => Promise<void>; onCancel: () => void }) {
  const [url, setUrl] = useState(webhook.url);
  const [values, setValues] = useState({
    max: String(webhook.max_events_per_delivery),
    threshold: String(webhook.summary_threshold),
    interval: String(webhook.min_interval_seconds),
  });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const save = async (e: FormEvent) => {
    e.preventDefault();
    const settings = parseSettings(values);
    if (!settings) {
      setError("件数と間隔は整数で入力してください。");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await onSave({ ...settings, url: url.trim() === webhook.url ? undefined : url.trim() });
    } catch (err) {
      setError(authErrorMessage(err, "admin"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => void save(e)} noValidate className="mt-3 flex flex-col gap-3 rounded-md border border-border bg-surface-2 p-3">
      <p className="m-0 text-sm font-bold">送信先と設定の変更</p>
      <TextField
        label="送信先の URL（https）"
        type="url"
        value={url}
        onChange={(e) => setUrl(e.target.value)}
        spellCheck={false}
        autoCapitalize="none"
        className="font-mono"
        hint="送信先を変えると、次の送信から新しい URL に送ります（送信待ちのものも）。署名の鍵は変わりません。"
      />
      <WebhookSettingsFields values={values} onChange={setValues} placeholders={{ max: "100", threshold: "1000", interval: "10" }} />
      {error && <Notice tone="error">{error}</Notice>}
      <div className="flex flex-wrap gap-2">
        <Button type="submit" variant="primary" busy={busy}>
          保存
        </Button>
        <Button onClick={onCancel}>やめる</Button>
      </div>
    </form>
  );
}

/** 鍵のローテーション（旧鍵で署名を続ける時間を選ぶ）。 */
function RotateForm({ onRotate, onCancel }: { onRotate: (overlapHours: number) => Promise<void>; onCancel: () => void }) {
  const [hours, setHours] = useState(24);
  const [busy, setBusy] = useState(false);
  const selectId = useId();
  const hintId = useId();

  const rotate = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    try {
      await onRotate(hours);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => void rotate(e)} className="mt-3 flex flex-col gap-3 rounded-md border border-border bg-surface-2 p-3" aria-describedby={hintId}>
      <p className="m-0 text-sm font-bold">署名の鍵のローテーション</p>
      <p id={hintId} className="m-0 text-xs text-muted">
        新しい鍵を作り、今の鍵（旧鍵）でも選んだ時間だけ署名を続けます（webhook-signature に両方の署名が並びます）。その間に受け手の設定を新しい鍵に切り替えてもらいます。
        鍵が漏れたときは「重ねない」を選びます。
      </p>
      <div className="flex flex-col gap-2 sm:flex-row sm:items-end">
        <div>
          <label htmlFor={selectId} className="mb-1 block text-sm font-bold">
            旧鍵で署名を続ける時間
          </label>
          <select id={selectId} value={hours} onChange={(e) => setHours(Number(e.target.value))} className="w-full rounded-md border border-border bg-surface px-3 py-2 text-base">
            {OVERLAP_CHOICES.map((c) => (
              <option key={c.hours} value={c.hours}>
                {c.label}
              </option>
            ))}
          </select>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button type="submit" variant="primary" busy={busy}>
            <Icon name="key" />
            鍵を入れ替える
          </Button>
          <Button onClick={onCancel}>やめる</Button>
        </div>
      </div>
    </form>
  );
}

/** 送信の一覧（状態で絞る。dead の行は個別に再送）。 */
function DeliveryList({ webhook, adapter, refresh, onChanged }: { webhook: Webhook; adapter: AuthAdapter; refresh: number; onChanged: (flash: Flash) => void }) {
  const [filter, setFilter] = useState<WebhookDeliveryStatus | "">("");
  const [loaded, setLoaded] = useState<{ key: string; rows: WebhookDelivery[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const selectId = useId();
  const key = `${filter}:${refresh}:${version}`;
  const rows = loaded?.key === key ? loaded.rows : null;

  // 取り直しは version（自分の再送）か refresh（カードの操作）を進める（効果の中で同期的に state を変えない）
  useEffect(() => {
    let cancelled = false;
    const requested = `${filter}:${refresh}:${version}`;
    adapter.listWebhookDeliveries(webhook.id, filter || undefined, 50).then(
      (list) => {
        if (cancelled) return;
        setError(null);
        setLoaded({ key: requested, rows: list });
      },
      (e: unknown) => {
        if (!cancelled) setError(authErrorMessage(e, "admin"));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [adapter, webhook.id, filter, refresh, version]);

  const redeliver = async (deliveryId: string) => {
    setBusy(deliveryId);
    try {
      await adapter.redeliverWebhook(webhook.id, deliveryId);
      setVersion((v) => v + 1);
      onChanged({ tone: "ok", text: "dead の送信を送信待ちに戻しました（同じイベント ID・本文で送ります）。" });
    } catch (e) {
      onChanged({ tone: "error", text: authErrorMessage(e, "admin") });
    } finally {
      setBusy(null);
    }
  };

  return (
    <div className="mt-3 border-t border-border pt-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="m-0 text-sm font-bold">送信の一覧（新しい順・50 件まで）</p>
        <div className="flex items-center gap-2 text-sm">
          <label htmlFor={selectId} className="text-muted">
            状態
          </label>
          <select
            id={selectId}
            value={filter}
            onChange={(e) => setFilter(e.target.value as WebhookDeliveryStatus | "")}
            className="rounded-md border border-border bg-surface px-2 py-1.5"
          >
            <option value="">すべて</option>
            {(Object.keys(WEBHOOK_DELIVERY_STATUS_LABELS) as WebhookDeliveryStatus[]).map((s) => (
              <option key={s} value={s}>
                {WEBHOOK_DELIVERY_STATUS_LABELS[s]}
              </option>
            ))}
          </select>
        </div>
      </div>
      {error ? (
        <Notice tone="error" className="mt-2">
          {error}
        </Notice>
      ) : !rows ? (
        <Loading />
      ) : rows.length === 0 ? (
        <p className="mt-2 mb-0 text-xs text-muted">該当する送信はありません。</p>
      ) : (
        <ul aria-label="送信の一覧" className="m-0 mt-2 flex list-none flex-col p-0">
          {rows.map((d) => (
            <li key={d.id} className="border-b border-border/70 py-2 text-xs last:border-b-0">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0 flex-1">
                  <p className="m-0 flex flex-wrap items-center gap-2">
                    <StatusTag tone={WEBHOOK_DELIVERY_TONE[d.status]}>{WEBHOOK_DELIVERY_STATUS_LABELS[d.status]}</StatusTag>
                    <span className="font-bold">{WEBHOOK_DELIVERY_KIND_LABELS[d.kind]}</span>
                    {d.kind !== "ping" && <span className="tabular">{d.event_count.toLocaleString("ja-JP")} 件</span>}
                    <span className="text-muted">試行 {d.attempts} 回</span>
                    {(d.last_http_status !== null || d.last_error) && (
                      <span className={d.status === "succeeded" ? "text-muted" : "text-danger"}>
                        {d.status === "succeeded" ? `状態コード ${d.last_http_status}` : (d.last_error ?? `状態コード ${d.last_http_status}`)}
                      </span>
                    )}
                  </p>
                  <p className="m-0 mt-0.5 font-mono break-all text-muted">{d.id}</p>
                  <p className="m-0 mt-0.5 text-muted">
                    {[
                      `作成 ${formatDateTime(d.created_at)}`,
                      d.last_attempt_at ? `最後の試行 ${formatDateTime(d.last_attempt_at)}` : null,
                      d.status === "pending" && d.attempts > 0 ? `次の試行 ${formatDateTime(d.next_attempt_at)}` : null,
                      d.succeeded_at ? `成功 ${formatDateTime(d.succeeded_at)}` : null,
                      d.dead_at ? `dead ${formatDateTime(d.dead_at)}` : null,
                      d.redelivered_at ? `再送の指示 ${formatDateTime(d.redelivered_at)}` : null,
                    ]
                      .filter(Boolean)
                      .join("・")}
                  </p>
                </div>
                {d.status === "dead" && (
                  <Button className="min-h-8 px-3 py-1 text-xs" busy={busy === d.id} onClick={() => void redeliver(d.id)}>
                    再送
                  </Button>
                )}
              </div>
              <details className="mt-1">
                <summary className="cursor-pointer text-accent">本文（種別と ID だけ）</summary>
                <pre className="m-0 mt-1 max-h-60 overflow-auto rounded-md border border-border bg-surface-2 p-2 font-mono text-[11px] leading-relaxed whitespace-pre-wrap break-all">
                  {d.body}
                </pre>
              </details>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function WebhookCard({
  webhook,
  adapter,
  onChanged,
}: {
  webhook: Webhook;
  adapter: AuthAdapter;
  onChanged: (flash: Flash, issued?: { name: string; secret: WebhookSecretIssued; kind: "register" | "rotate" }) => void;
}) {
  const [busy, setBusy] = useState<"disable" | "enable" | "ping" | "redeliver" | null>(null);
  const [mode, setMode] = useState<"edit" | "rotate" | null>(null);
  const [showDeliveries, setShowDeliveries] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const name = webhook.client_name ?? webhook.client_id;
  const current = webhook.secrets.find((s) => s.expires_at === null) ?? null;
  const previous = webhook.secrets.filter((s) => s.expires_at !== null);
  const clientInactive = webhook.client_status !== "active";

  const run = async (kind: NonNullable<typeof busy>) => {
    setBusy(kind);
    setError(null);
    try {
      if (kind === "disable" || kind === "enable") {
        await adapter.updateWebhook(webhook.id, { status: kind === "disable" ? "disabled" : "active" });
        onChanged({
          tone: "ok",
          text:
            kind === "disable"
              ? `${name} への Webhook を停止しました。変更は再開まで送信待ちとしてたまります（受け手は変更フィードでも取り直せます）。`
              : `${name} への Webhook を再開しました。たまった変更から順に送ります。`,
        });
      } else if (kind === "ping") {
        await adapter.pingWebhook(webhook.id);
        onChanged({ tone: "ok", text: `${name} に送信の確かめ（ping）を送りました。結果は送信の一覧で確かめます。` });
        setShowDeliveries(true);
      } else {
        const n = await adapter.redeliverWebhook(webhook.id);
        onChanged({ tone: "ok", text: `${name} の dead の送信 ${n} 件を送信待ちに戻しました（同じイベント ID・本文で送ります）。` });
      }
      setRefresh((r) => r + 1);
    } catch (e) {
      setError(authErrorMessage(e, "admin"));
    } finally {
      setBusy(null);
    }
  };

  const save = async (update: WebhookUpdate) => {
    await adapter.updateWebhook(webhook.id, update);
    setMode(null);
    onChanged({ tone: "ok", text: `${name} への Webhook の送信先と設定を保存しました。` });
  };

  const rotate = async (overlapHours: number) => {
    setError(null);
    try {
      const secret = await adapter.rotateWebhookSecret(webhook.id, overlapHours);
      setMode(null);
      onChanged(null, { name, secret, kind: "rotate" });
    } catch (e) {
      setError(authErrorMessage(e, "admin"));
    }
  };

  return (
    <Panel as="div" className={`!p-4 ${webhook.dead_count > 0 ? "!border-danger/50" : ""}`}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="m-0 flex flex-wrap items-center gap-2">
            <span className="text-base font-bold">{webhook.client_name ?? "（名前の申告なし）"}</span>
            <StatusTag tone={webhook.status === "active" ? "ok" : "muted"}>{WEBHOOK_STATUS_LABELS[webhook.status]}</StatusTag>
            {webhook.dead_count > 0 && <StatusTag tone="danger">dead {webhook.dead_count} 件</StatusTag>}
            {clientInactive && <StatusTag tone="warn">クライアントが{CLIENT_STATUS_LABELS[webhook.client_status]}</StatusTag>}
          </p>
          <p className="m-0 font-mono text-xs break-all text-muted">{webhook.client_id}</p>
        </div>
        <div className="flex flex-wrap gap-2">
          {webhook.status === "active" ? (
            <ConfirmButton
              label="停止"
              variant="danger"
              message="停止すると送信を止めます。変更は送信待ちとしてたまり、再開すると順に送ります。"
              busy={busy === "disable"}
              onConfirm={() => void run("disable")}
            />
          ) : (
            <Button className="min-h-8 px-3 py-1 text-xs" busy={busy === "enable"} onClick={() => void run("enable")}>
              再開
            </Button>
          )}
        </div>
      </div>
      {webhook.dead_count > 0 && (
        <Notice tone="warn" className="mt-3" title={`再試行の上限に達して止めた送信が ${webhook.dead_count} 件あります`}>
          <p className="m-0">受け手（{name}）の状態を確かめてから再送します。受け手は変更フィードからも取り直せます。</p>
          <div className="mt-2">
            <Button className="min-h-8 px-3 py-1 text-xs" busy={busy === "redeliver"} onClick={() => void run("redeliver")}>
              dead をすべて再送
            </Button>
          </div>
        </Notice>
      )}
      {clientInactive && (
        <Notice tone="info" className="mt-3">
          クライアントが{CLIENT_STATUS_LABELS[webhook.client_status]}のあいだは送信しません（API クライアントの画面で再開すると送ります）。
        </Notice>
      )}
      <dl className="mt-3 mb-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs sm:grid-cols-[auto_1fr_auto_1fr]">
        <dt className="text-muted">送信先</dt>
        <dd className="m-0 font-mono break-all sm:col-span-3">{webhook.url}</dd>
        <dt className="text-muted">件数</dt>
        <dd className="m-0 tabular sm:col-span-3">
          送信待ち {webhook.pending_count.toLocaleString("ja-JP")}・
          <span className={webhook.dead_count > 0 ? "font-bold text-danger" : ""}>dead {webhook.dead_count.toLocaleString("ja-JP")}</span>・成功{" "}
          {webhook.succeeded_count.toLocaleString("ja-JP")}
        </dd>
        <dt className="text-muted">最終の成功</dt>
        <dd className="m-0">{webhook.last_success_at ? formatDateTime(webhook.last_success_at) : "まだありません"}</dd>
        <dt className="text-muted">最終の失敗</dt>
        <dd className="m-0">
          {webhook.last_failure_at ? (
            <>
              {formatDateTime(webhook.last_failure_at)}
              {webhook.last_failure && <span className="text-danger">（{webhook.last_failure}）</span>}
            </>
          ) : (
            "ありません"
          )}
        </dd>
        <dt className="text-muted">最も古い送信待ち</dt>
        <dd className="m-0">{webhook.oldest_pending_at ? formatDateTime(webhook.oldest_pending_at) : "なし"}</dd>
        <dt className="text-muted">先行提供</dt>
        <dd className="m-0">{webhook.disclosure_lead_days > 0 ? `有効日の ${webhook.disclosure_lead_days} 日前から` : "なし"}</dd>
        <dt className="text-muted">設定</dt>
        <dd className="m-0 sm:col-span-3">
          1 回に {webhook.max_events_per_delivery.toLocaleString("ja-JP")} 件まで・{webhook.summary_threshold.toLocaleString("ja-JP")} 件を超えたら要約・
          {webhook.min_interval_seconds} 秒以上の間隔
        </dd>
        <dt className="text-muted">署名の鍵</dt>
        <dd className="m-0 sm:col-span-3">
          {current ? `今の鍵（${formatDateTime(current.created_at)} 作成）` : "なし"}
          {previous.map((s) => (
            <span key={s.id} className="block text-warn-fg">
              旧鍵（{formatDateTime(s.created_at)} 作成）も {formatDateTime(s.expires_at!)} まで署名に使います
            </span>
          ))}
        </dd>
        <dt className="text-muted">登録</dt>
        <dd className="m-0 sm:col-span-3">
          {formatDateTime(webhook.created_at)}
          {webhook.updated_at !== webhook.created_at && <span className="text-muted">（最終の変更 {formatDateTime(webhook.updated_at)}）</span>}
        </dd>
      </dl>
      <div className="mt-3 flex flex-wrap gap-2">
        <Button className="min-h-8 px-3 py-1 text-xs" onClick={() => setMode(mode === "edit" ? null : "edit")} aria-expanded={mode === "edit"}>
          送信先と設定を変更
        </Button>
        <Button className="min-h-8 px-3 py-1 text-xs" onClick={() => setMode(mode === "rotate" ? null : "rotate")} aria-expanded={mode === "rotate"}>
          <Icon name="key" />
          鍵のローテーション
        </Button>
        <Button className="min-h-8 px-3 py-1 text-xs" busy={busy === "ping"} onClick={() => void run("ping")}>
          送信の確かめ（ping）
        </Button>
        <Button className="min-h-8 px-3 py-1 text-xs" onClick={() => setShowDeliveries((v) => !v)} aria-expanded={showDeliveries}>
          {showDeliveries ? "送信の一覧を閉じる" : "送信の一覧"}
        </Button>
      </div>
      {mode === "edit" && <WebhookEditor webhook={webhook} onSave={save} onCancel={() => setMode(null)} />}
      {mode === "rotate" && <RotateForm onRotate={rotate} onCancel={() => setMode(null)} />}
      {error && (
        <Notice tone="error" className="mt-3">
          {error}
        </Notice>
      )}
      {showDeliveries && <DeliveryList webhook={webhook} adapter={adapter} refresh={refresh} onChanged={(f) => onChanged(f)} />}
    </Panel>
  );
}

/** Webhook を登録できるクライアント（client credentials・REST API・jinji.read を持ち、承認待ち・却下でなく、まだ Webhook の無いもの）。 */
function webhookCandidates(clients: OAuthClient[], webhooks: Webhook[]): OAuthClient[] {
  return clients.filter(
    (c) =>
      (c.status === "active" || c.status === "suspended") &&
      c.grant_types.includes("client_credentials") &&
      c.resources.includes("api") &&
      c.scopes.includes("jinji.read") &&
      !webhooks.some((w) => w.client_ref === c.id),
  );
}

/** 新規登録。 */
function RegisterWebhookForm({
  candidates,
  adapter,
  onRegistered,
}: {
  candidates: OAuthClient[];
  adapter: AuthAdapter;
  onRegistered: (name: string, secret: WebhookSecretIssued) => void;
}) {
  const [clientId, setClientId] = useState(candidates[0]?.id ?? "");
  const [url, setUrl] = useState("");
  const [values, setValues] = useState({ max: "", threshold: "", interval: "" });
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const selectId = useId();
  const selected = candidates.find((c) => c.id === clientId) ?? candidates[0] ?? null;

  const register = async (e: FormEvent) => {
    e.preventDefault();
    if (!selected) return;
    const settings = parseSettings(values);
    if (!/^https:\/\/\S+$/i.test(url.trim())) {
      setError("送信先の URL は https で入力してください。");
      return;
    }
    if (!settings) {
      setError("件数と間隔は整数で入力してください。");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      const secret = await adapter.registerWebhook(selected.id, url.trim(), settings);
      setUrl("");
      setValues({ max: "", threshold: "", interval: "" });
      onRegistered(selected.display_name ?? selected.client_id, secret);
    } catch (err) {
      setError(authErrorMessage(err, "admin"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel className="flex flex-col gap-3">
      <h3 className="m-0 text-base font-bold">Webhook を登録する</h3>
      {candidates.length === 0 ? (
        <p className="m-0 text-sm text-muted">
          登録できる社内システムはありません。対象は、client credentials で REST API を jinji.read のスコープで読めるクライアントのうち、まだ Webhook の無いものです（クライアントの登録は管理 CLI で行います）。
        </p>
      ) : (
        <form onSubmit={(e) => void register(e)} noValidate className="flex flex-col gap-3">
          <p className="m-0 text-xs text-muted">
            人・組織・拠点が変わったことを、社内システムに通知します。本文は種別と ID だけで、氏名などの値は載せません（受け手は自分のトークンで REST API・変更フィードから取り直します）。
            1 つの社内システムに 1 つ登録できます。
          </p>
          <div>
            <label htmlFor={selectId} className="mb-1 block text-sm font-bold">
              社内システム
            </label>
            <select id={selectId} value={selected?.id ?? ""} onChange={(e) => setClientId(e.target.value)} className="w-full rounded-md border border-border bg-surface px-3 py-2 text-base sm:w-auto">
              {candidates.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.display_name ?? c.client_id}（{c.client_id}）{c.status === "suspended" ? "・停止中" : ""}
                </option>
              ))}
            </select>
          </div>
          <TextField
            label="送信先の URL（https）"
            type="url"
            value={url}
            onChange={(e) => setUrl(e.target.value)}
            placeholder="https://attendance.example.co.jp/hooks/jinji"
            spellCheck={false}
            autoCapitalize="none"
            className="font-mono"
            hint="https だけ。社内の私設アドレス・localhost には送りません。"
          />
          <details>
            <summary className="cursor-pointer text-sm text-accent">送信の設定（任意。空欄は既定）</summary>
            <div className="mt-2">
              <WebhookSettingsFields values={values} onChange={setValues} placeholders={{ max: "既定 100", threshold: "既定 1000", interval: "既定 10" }} />
            </div>
          </details>
          {error && <Notice tone="error">{error}</Notice>}
          <div>
            <Button type="submit" variant="primary" busy={busy}>
              登録
            </Button>
          </div>
        </form>
      )}
    </Panel>
  );
}

function WebhooksTab({ adapter }: { adapter: AuthAdapter }) {
  const [data, setData] = useState<{ version: number; webhooks: Webhook[]; clients: OAuthClient[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [flash, setFlash] = useFlash();
  const [issued, setIssued] = useState<{ name: string; secret: WebhookSecretIssued; kind: "register" | "rotate" } | null>(null);
  const [version, setVersion] = useState(0);
  const issuedRef = useRef<HTMLDivElement>(null);

  // 鍵は画面の上に出すので、登録・ローテーションの直後はそこまで動かす（下の登録のフォームから操作しても見落とさない）
  useEffect(() => {
    if (issued) issuedRef.current?.scrollIntoView({ block: "start" });
  }, [issued]);

  // 取り直しは version を進める（効果の中で同期的に state を変えない）
  useEffect(() => {
    let cancelled = false;
    const requested = version;
    Promise.all([adapter.listWebhooks(), adapter.listClients()]).then(
      ([webhooks, clients]) => {
        if (cancelled) return;
        setError(null);
        setData({ version: requested, webhooks, clients });
      },
      (e: unknown) => {
        if (!cancelled) setError(authErrorMessage(e, "admin"));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [adapter, version]);

  const changed = (next: Flash, secret?: { name: string; secret: WebhookSecretIssued; kind: "register" | "rotate" }) => {
    setFlash(next);
    setIssued(secret ?? null);
    if (next?.tone !== "error") setVersion((v) => v + 1);
  };

  return (
    <section aria-labelledby="webhooks-title" className="flex flex-col gap-4">
      <div>
        <h2 id="webhooks-title" className="sr-only">
          Webhook
        </h2>
        <p className="m-0 text-sm text-muted">
          社内システムへの「変わった」の通知。本文は種別と ID だけで、受け手は自分のトークンで取り直します。失敗は間隔を延ばしながら再送し、上限に達したもの（dead）はここで確かめて再送します。
        </p>
      </div>
      {flash && <Notice tone={flash.tone}>{flash.text}</Notice>}
      {issued && (
        <div ref={issuedRef} className="scroll-mt-4">
          <Panel className="flex flex-col gap-3" aria-live="polite">
            <Notice tone="ok" title={issued.kind === "register" ? `${issued.name} に Webhook を登録しました` : `${issued.name} の署名の鍵を入れ替えました`}>
              {issued.kind === "register"
                ? "登録した時点より後の変更から通知します。送信の確かめ（ping）で、受け手が署名を検証できることを確かめてください。"
                : "受け手の設定を新しい鍵に切り替えてもらいます。旧鍵の期限まで、両方の鍵で署名します。"}
            </Notice>
            <Notice tone="warn" title="この鍵はこの画面でだけ表示されます">
              jin-ji は鍵をあとから表示できません。社内システムの担当者に安全な経路で渡してください。受け手は webhook-id・webhook-timestamp・本文から HMAC-SHA256 の署名を計算し、webhook-signature
              と照合します（Standard Webhooks の形式。手順は設計書 docs/architecture/overview.md の §9 を参照）。
            </Notice>
            <CopyField label="署名の鍵" value={issued.secret.secret} />
            <div>
              <Button className="min-h-8 px-3 py-1 text-xs" onClick={() => setIssued(null)}>
                控えたので閉じる
              </Button>
            </div>
          </Panel>
        </div>
      )}
      {error ? (
        <Notice tone="error">{error}</Notice>
      ) : !data ? (
        <Loading />
      ) : (
        <>
          {data.webhooks.length === 0 ? (
            <Panel>
              <p className="m-0 text-muted">登録済みの Webhook はありません。</p>
            </Panel>
          ) : (
            <ul aria-label="登録済みの Webhook" className="m-0 flex list-none flex-col gap-3 p-0">
              {data.webhooks.map((w) => (
                <li key={w.id}>
                  <WebhookCard webhook={w} adapter={adapter} onChanged={changed} />
                </li>
              ))}
            </ul>
          )}
          <RegisterWebhookForm
            key={data.webhooks.length}
            candidates={webhookCandidates(data.clients, data.webhooks)}
            adapter={adapter}
            onRegistered={(name, secret) => changed(null, { name, secret, kind: "register" })}
          />
        </>
      )}
    </section>
  );
}

// ---- CIMD の許可ドメイン ------------------------------------------------------------------------------------

const DOMAIN = /^(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$/i;

function DomainsTab({ adapter }: { adapter: AuthAdapter }) {
  const [domains, setDomains] = useState<CimdDomains | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [value, setValue] = useState("");
  const [inputError, setInputError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [flash, setFlash] = useFlash();

  const load = useCallback(() => {
    adapter.listCimdDomains().then(setDomains, (e: unknown) => setError(authErrorMessage(e, "admin")));
  }, [adapter]);
  useEffect(load, [load]);

  const add = async (e: FormEvent) => {
    e.preventDefault();
    const domain = value.trim().toLowerCase();
    if (!DOMAIN.test(domain)) {
      setInputError("ホスト名（例: agent.example.com）を入力してください。https:// やパスは付けません。");
      return;
    }
    setBusy(true);
    setInputError(null);
    try {
      await adapter.addCimdDomain(domain);
      setValue("");
      setFlash({ tone: "ok", text: `${domain} を許可しました。` });
      load();
    } catch (err) {
      setInputError(authErrorMessage(err, "admin"));
    } finally {
      setBusy(false);
    }
  };

  const remove = async (domain: string) => {
    try {
      await adapter.removeCimdDomain(domain);
      setFlash({ tone: "ok", text: `${domain} を許可ドメインから外しました。登録済みのクライアントは有効のままです。` });
      load();
    } catch (err) {
      setFlash({ tone: "error", text: authErrorMessage(err, "admin") });
    }
  };

  return (
    <section aria-labelledby="domains-title" className="flex flex-col gap-4">
      <div>
        <h2 id="domains-title" className="sr-only">
          CIMD の許可ドメイン
        </h2>
        <p className="m-0 text-sm text-muted">
          ここにあるホストのアプリは、最初の接続で自動的に登録されます（管理者の承認は不要。ツールは既定で何も許さない）。それ以外のホストは承認待ちになります。
        </p>
      </div>
      {flash && <Notice tone={flash.tone}>{flash.text}</Notice>}
      {error ? (
        <Notice tone="error">{error}</Notice>
      ) : !domains ? (
        <Loading />
      ) : (
        <Panel className="flex flex-col gap-4">
          <ul className="m-0 flex list-none flex-col p-0" aria-label="許可ドメイン">
            {domains.configured.map((d) => (
              <li key={`c-${d}`} className="flex flex-wrap items-center justify-between gap-2 border-b border-border/70 py-2 last:border-b-0">
                <span className="font-mono">{d}</span>
                <span className="text-xs text-muted">設定ファイル（ここでは外せません）</span>
              </li>
            ))}
            {domains.added.map((d) => (
              <li key={`a-${d}`} className="flex flex-wrap items-center justify-between gap-2 border-b border-border/70 py-2 last:border-b-0">
                <span className="font-mono">{d}</span>
                <div className="w-full sm:w-auto">
                  <ConfirmButton
                    label="外す"
                    variant="danger"
                    message={`${d} を外します。このドメインから登録済みのクライアントは有効のままです（止めるならクライアントを停止）。`}
                    onConfirm={() => void remove(d)}
                  />
                </div>
              </li>
            ))}
            {domains.configured.length + domains.added.length === 0 && <li className="py-2 text-muted">許可ドメインはありません（CIMD のクライアントはすべて承認待ちになります）。</li>}
          </ul>
          <form onSubmit={add} noValidate className="flex flex-wrap items-start gap-2 border-t border-border pt-4">
            <TextField
              label="ドメインを追加"
              value={value}
              onChange={(e) => setValue(e.target.value)}
              placeholder="agent.example.com"
              spellCheck={false}
              autoCapitalize="none"
              error={inputError}
              hint="ホスト名の完全一致で照合します（サブドメインは含みません）。"
              className="min-w-56 flex-1 font-mono"
            />
            <Button type="submit" busy={busy} className="sm:mt-7">
              追加
            </Button>
          </form>
        </Panel>
      )}
    </section>
  );
}

// ---- 画面 ---------------------------------------------------------------------------------------------------

export function AdminPage({ adapter, source }: { adapter: AuthAdapter; source: AuthSource }) {
  const [session, setSession] = useState<Session | null | undefined>(undefined);
  const [error, setError] = useState<string | null>(null);
  const requested = new URLSearchParams(window.location.search).get("tab");
  const [tab, setTab] = useState<Tab>(requested && requested in TAB_LABELS ? (requested as Tab) : "unbound");

  useEffect(() => {
    adapter.me().then(
      (s) => {
        if (!s) {
          window.location.assign(withSource(loginUrl(), source));
          return;
        }
        setSession(s);
      },
      (e: unknown) => setError(authErrorMessage(e, "admin")),
    );
  }, [adapter, source]);

  const hrAdmin = isHrAdmin(session ?? null);
  const tabs: Tab[] = hrAdmin ? ["unbound", "accounts", "clients", "webhooks", "domains"] : ["unbound", "accounts"];
  const current = tabs.includes(tab) ? tab : "unbound";

  // スマホ幅ではタブが横にあふれるので、選んでいるタブを見える位置に寄せる（/admin?tab= で開いたときも）
  const ready = session != null;
  useEffect(() => {
    if (ready) document.getElementById(`tab-${current}`)?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [ready, current]);

  const select = (next: Tab) => {
    setTab(next);
    const url = new URL(window.location.href);
    url.searchParams.set("tab", next);
    window.history.replaceState(null, "", url.pathname + url.search);
  };

  const onTabKey = (e: KeyboardEvent, index: number) => {
    const delta = e.key === "ArrowRight" ? 1 : e.key === "ArrowLeft" ? -1 : 0;
    if (e.key === "Home" || e.key === "End" || delta !== 0) {
      e.preventDefault();
      const nextIndex = e.key === "Home" ? 0 : e.key === "End" ? tabs.length - 1 : (index + delta + tabs.length) % tabs.length;
      select(tabs[nextIndex]!);
      document.getElementById(`tab-${tabs[nextIndex]}`)?.focus();
    }
  };

  return (
    <AuthFrame
      title="認証の管理"
      width="xl"
      nav={
        <nav aria-label="画面" className="flex gap-1 text-sm">
          {session?.kind === "person" && (
            <a href={withSource("/", source)} className="rounded-sm px-3 py-1 text-fg hover:bg-surface-2">
              人事情報へ戻る
            </a>
          )}
          <a href={withSource("/account", source)} className="rounded-sm px-3 py-1 text-fg hover:bg-surface-2">
            アカウント
          </a>
        </nav>
      }
    >
      {session === undefined && !error ? (
        <Loading />
      ) : !session ? (
        <Notice tone="error">{error}</Notice>
      ) : !canUseAdmin(session) ? (
        <Notice tone="warn" title="この画面は使えません">
          認証の管理は、人事管理者と緊急用の管理者アカウントだけが使えます。
        </Notice>
      ) : (
        <div className="flex flex-col gap-5">
          <div>
            <h1 className="m-0 text-2xl font-bold">認証の管理</h1>
            <p className="mt-1 mb-0 text-sm text-muted">
              {session.kind === "break_glass"
                ? "緊急用の管理者アカウントでログインしています。認証の復旧（紐付け・ロックの解除・リンクの発行・アカウントの無効化）だけを行えます。"
                : "ログインの紐付け・アカウントの復旧と、API・MCP のクライアントの管理。操作はすべて監査ログに残ります。"}
            </p>
          </div>
          <div role="tablist" aria-label="管理の項目" className="-mx-4 flex gap-1 overflow-x-auto border-b border-border px-4">
            {tabs.map((t, i) => (
              <button
                key={t}
                id={`tab-${t}`}
                type="button"
                role="tab"
                aria-selected={current === t}
                aria-controls={`panel-${t}`}
                tabIndex={current === t ? 0 : -1}
                onClick={() => select(t)}
                onKeyDown={(e) => onTabKey(e, i)}
                className={`-mb-px shrink-0 border-b-2 px-3 py-2 text-sm whitespace-nowrap ${
                  current === t ? "border-accent font-bold text-accent" : "border-transparent text-muted hover:text-fg"
                }`}
              >
                {TAB_LABELS[t]}
              </button>
            ))}
          </div>
          <div role="tabpanel" id={`panel-${current}`} aria-labelledby={`tab-${current}`}>
            {current === "unbound" && <UnboundTab adapter={adapter} hrAdmin={hrAdmin} />}
            {current === "accounts" && <AccountsTab adapter={adapter} hrAdmin={hrAdmin} />}
            {current === "clients" && <ClientsTab adapter={adapter} personId={session?.kind === "person" ? session.person_id : null} />}
            {current === "webhooks" && <WebhooksTab adapter={adapter} />}
            {current === "domains" && <DomainsTab adapter={adapter} />}
          </div>
        </div>
      )}
    </AuthFrame>
  );
}
