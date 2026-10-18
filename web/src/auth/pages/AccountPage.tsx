// アカウント（/account）。ログイン中のセッションの情報、ローカルアカウントのパスキーの登録・一覧・削除と認証アプリ（TOTP）の設定、
// AI エージェント（MCP クライアントなど）への許可（委任）の一覧と取り消し、ログアウト。
// TOTP の QR コードは外部ライブラリを入れずに作れないため出さず、otpauth の URI と共有鍵を表示する（認証アプリに手入力・貼り付けできる）。
import { useEffect, useState, type FormEvent } from "react";
import { withSource, type AuthSource } from "../authAdapter";
import { methodLabel, REGISTRATION_LABELS, RESOURCE_LABELS, scopeInfo } from "../labels";
import { authErrorMessage } from "../messages";
import { loginUrl } from "../returnUrl";
import { canUseAdmin, type AuthAdapter, type Delegation, type Passkey, type Session, type TwoFactorSetup } from "../types";
import { AuthFrame, Button, ConfirmButton, CopyButton, CopyField, Icon, Loading, Notice, Panel, TextField, formatDateTime } from "../ui";
import { webAuthnSupported } from "../webauthn";

/** 共有鍵を 4 文字ずつに区切る（手入力しやすく）。 */
export function groupKey(key: string): string {
  return key.replace(/\s/g, "").replace(/(.{4})/g, "$1 ").trim();
}

function PasskeySection({ adapter }: { adapter: AuthAdapter }) {
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ tone: "ok" | "error"; text: string } | null>(null);
  const [list, setList] = useState<{ version: number; passkeys: Passkey[] } | null>(null);
  const [listError, setListError] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const supported = adapter.kind === "demo" || webAuthnSupported();

  useEffect(() => {
    let cancelled = false;
    adapter.listPasskeys().then(
      (passkeys) => {
        if (cancelled) return;
        setListError(null);
        setList({ version, passkeys });
      },
      (e: unknown) => {
        if (!cancelled) setListError(authErrorMessage(e, "account"));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [adapter, version]);

  const register = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setMessage(null);
    try {
      await adapter.registerPasskey(name.trim());
      setMessage({ tone: "ok", text: "パスキーを登録しました。次回から、このデバイスのパスキーでログインできます。" });
      setName("");
      setVersion((v) => v + 1);
    } catch (err) {
      setMessage({ tone: "error", text: authErrorMessage(err, "account") });
    } finally {
      setBusy(false);
    }
  };

  const remove = async (passkey: Passkey) => {
    setMessage(null);
    try {
      await adapter.deletePasskey(passkey.id);
      setMessage({ tone: "ok", text: `パスキー「${passkey.name ?? "名前なし"}」を削除しました。そのパスキーではログインできなくなります。` });
      setVersion((v) => v + 1);
    } catch (err) {
      setMessage({ tone: "error", text: authErrorMessage(err, "account") });
    }
  };

  return (
    <Panel aria-labelledby="passkey-title">
      <h2 id="passkey-title" className="m-0 flex items-center gap-2 text-base font-bold">
        <Icon name="key" className="size-5 text-accent" />
        パスキー
      </h2>
      <p className="mt-1 mb-4 text-sm text-muted">
        パソコンやスマートフォンの指紋・顔認証・PIN でログインできるようにします。パスワードより安全で、管理者の権限を持つ人はパスキーか認証アプリの確認コードが必要です。
      </p>
      {listError ? (
        <Notice tone="error" className="mb-4">
          {listError}
        </Notice>
      ) : !list ? (
        <p className="mt-0 mb-4 text-sm text-muted">登録済みのパスキーを読み込み中…</p>
      ) : list.passkeys.length === 0 ? (
        <p className="mt-0 mb-4 text-sm text-muted">登録済みのパスキーはありません。</p>
      ) : (
        <ul aria-label="登録済みのパスキー" className="m-0 mb-4 flex list-none flex-col gap-2 p-0">
          {list.passkeys.map((p) => (
            <li key={p.id} className="flex flex-wrap items-center justify-between gap-2 rounded-md border border-border px-3 py-2">
              <div className="min-w-0">
                <p className="m-0 font-bold break-all">{p.name ?? "（名前なし）"}</p>
                <p className="m-0 text-xs text-muted">
                  {formatDateTime(p.created_at)} に登録{p.is_backed_up ? "・同期されるパスキー（他のデバイスでも使えます）" : ""}
                </p>
              </div>
              <div className="w-full sm:w-auto">
                <ConfirmButton
                  label="削除"
                  variant="danger"
                  message="削除すると、このパスキーではログインできなくなります。"
                  confirmLabel="削除する"
                  onConfirm={() => void remove(p)}
                />
              </div>
            </li>
          ))}
        </ul>
      )}
      {!supported ? (
        <Notice tone="warn">このブラウザはパスキーに対応していません。最新のブラウザで開いてください。</Notice>
      ) : (
        <form onSubmit={register} className="flex flex-col gap-3 sm:flex-row sm:items-end">
          <TextField
            label="パスキーの名前（任意）"
            hint="あとで見分けられるように。例: 仕事用ノート PC"
            value={name}
            onChange={(e) => setName(e.target.value)}
            maxLength={200}
            className="flex-1"
          />
          <Button type="submit" variant="primary" busy={busy} className="sm:mb-5">
            このデバイスで登録
          </Button>
        </form>
      )}
      {message && (
        <Notice tone={message.tone} className="mt-3">
          {message.text}
        </Notice>
      )}
    </Panel>
  );
}

/** 委任のクライアントの表示名（名前が無ければ client_id。CIMD は URL のホスト）。 */
export function delegationClientName(d: Delegation): string {
  if (d.client_name) return d.client_name;
  if (d.registration === "cimd") {
    try {
      return new URL(d.client_id).host;
    } catch {
      return d.client_id;
    }
  }
  return d.client_id;
}

function DelegationsSection({ adapter }: { adapter: AuthAdapter }) {
  // rows が undefined: 読み込み中、null: トークン発行を使わない環境（項目を出さない）
  const [data, setData] = useState<{ version: number; rows: Delegation[] | null } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [version, setVersion] = useState(0);

  useEffect(() => {
    let cancelled = false;
    const requested = version;
    adapter.listDelegations().then(
      (list) => {
        if (cancelled) return;
        setError(null);
        setData({ version: requested, rows: list });
      },
      (e: unknown) => {
        if (!cancelled) setError(authErrorMessage(e, "account"));
      },
    );
    return () => {
      cancelled = true;
    };
  }, [adapter, version]);

  const revoke = async (d: Delegation) => {
    setMessage(null);
    setError(null);
    try {
      await adapter.revokeDelegation(d.id);
      setMessage(`「${delegationClientName(d)}」への許可を取り消しました。このアプリは次の要求から jin-ji の情報を読めません。`);
      setVersion((v) => v + 1);
    } catch (e) {
      setError(authErrorMessage(e, "account"));
    }
  };

  const rows = data ? data.rows : undefined;
  if (rows === null) return null;

  return (
    <Panel aria-labelledby="delegation-title">
      <h2 id="delegation-title" className="m-0 flex items-center gap-2 text-base font-bold">
        <Icon name="shield" className="size-5 text-accent" />
        AI エージェントへの許可
      </h2>
      <p className="mt-1 mb-4 text-sm text-muted">
        同意画面で許可したアプリ（AI エージェント・MCP クライアントなど）です。アプリは、あなたが jin-ji で見られる範囲の情報を、あなたの代わりに読みます。使わなくなったものは取り消してください。
      </p>
      {message && (
        <Notice tone="ok" className="mb-3">
          {message}
        </Notice>
      )}
      {error && (
        <Notice tone="error" className="mb-3">
          {error}
        </Notice>
      )}
      {rows === undefined ? (
        !error && <p className="m-0 text-sm text-muted">読み込み中…</p>
      ) : rows.length === 0 ? (
        <p className="m-0 text-sm text-muted">許可しているアプリはありません。</p>
      ) : (
        <ul aria-label="許可しているアプリ" className="m-0 flex list-none flex-col gap-2 p-0">
          {rows.map((d) => (
            <li key={d.id} className="flex flex-col gap-2 rounded-md border border-border px-3 py-2">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <p className="m-0 font-bold break-all">{delegationClientName(d)}</p>
                  <p className="m-0 text-xs break-all text-muted">
                    {REGISTRATION_LABELS[d.registration] ?? d.registration}・{RESOURCE_LABELS[d.resource] ?? d.resource}
                  </p>
                </div>
                <div className="w-full sm:w-auto">
                  <ConfirmButton
                    label="取り消す"
                    variant="danger"
                    message="取り消すと、このアプリは次の要求から jin-ji の情報を読めなくなります。使い続けるには、アプリからもう一度接続して許可し直します。"
                    confirmLabel="取り消す"
                    onConfirm={() => void revoke(d)}
                  />
                </div>
              </div>
              <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-xs">
                <dt className="text-muted">できること</dt>
                <dd className="m-0">{d.scopes.map((s) => scopeInfo(s).title).join("・")}</dd>
                <dt className="text-muted">許可</dt>
                <dd className="m-0">{formatDateTime(d.granted_at)}</dd>
                <dt className="text-muted">最後の利用</dt>
                <dd className="m-0">{d.last_used_at ? formatDateTime(d.last_used_at) : "なし"}</dd>
                <dt className="text-muted">期限</dt>
                <dd className="m-0">{formatDateTime(d.expires_at)}（期限を過ぎたら、アプリから許可し直します）</dd>
              </dl>
            </li>
          ))}
        </ul>
      )}
    </Panel>
  );
}

function TwoFactorSection({ adapter }: { adapter: AuthAdapter }) {
  const [setup, setSetup] = useState<TwoFactorSetup | null>(null);
  const [codes, setCodes] = useState<string[] | null>(null);
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState<"setup" | "enable" | null>(null);
  const [error, setError] = useState<string | null>(null);

  const start = async () => {
    setBusy("setup");
    setError(null);
    try {
      setSetup(await adapter.setupTwoFactor());
      setCodes(null);
      setCode("");
    } catch (e) {
      setError(authErrorMessage(e, "account"));
    } finally {
      setBusy(null);
    }
  };

  const enable = async (e: FormEvent) => {
    e.preventDefault();
    const value = code.replace(/\s/g, "");
    if (!/^\d{6}$/.test(value)) {
      setError("確認コードは 6 桁の数字で入力してください。");
      return;
    }
    setBusy("enable");
    setError(null);
    try {
      setCodes(await adapter.enableTwoFactor(value));
      setSetup(null);
    } catch (err) {
      setError(authErrorMessage(err, "account"));
    } finally {
      setBusy(null);
    }
  };

  return (
    <Panel aria-labelledby="totp-title">
      <h2 id="totp-title" className="m-0 flex items-center gap-2 text-base font-bold">
        <Icon name="shield" className="size-5 text-accent" />
        認証アプリ（確認コード）
      </h2>
      <p className="mt-1 mb-4 text-sm text-muted">
        パスワードに加えて、スマートフォンの認証アプリ（Microsoft Authenticator・Google Authenticator など）に表示される 6 桁の確認コードでログインします。
      </p>

      {codes ? (
        <div className="flex flex-col gap-3">
          <Notice tone="ok" title="認証アプリの確認コードを有効にしました" />
          <Notice tone="warn" title="リカバリコードを今すぐ保存してください">
            認証アプリを使えなくなったときに、確認コードの代わりに 1 つずつ使えます。<strong>この画面を閉じると二度と表示されません。</strong>
            パスワード管理ツールなど、安全な場所に保存してください。
          </Notice>
          <div>
            <div className="mb-1 flex items-center justify-between gap-2">
              <span className="text-xs font-bold text-muted">リカバリコード（{codes.length} 個）</span>
              <CopyButton value={codes.join("\n")} label="すべてコピー" />
            </div>
            <ol className="m-0 grid list-none grid-cols-2 gap-x-6 gap-y-1 rounded-md border border-border bg-surface-2 px-4 py-3 font-mono text-sm sm:grid-cols-5">
              {codes.map((c) => (
                <li key={c} className="select-all">
                  {c}
                </li>
              ))}
            </ol>
          </div>
        </div>
      ) : setup ? (
        <form onSubmit={enable} className="flex flex-col gap-4" noValidate>
          <ol className="m-0 flex list-decimal flex-col gap-3 pl-5 text-sm">
            <li>
              認証アプリで「アカウントを追加」→「セットアップキーを入力」を選び、次の鍵を入力します（QR コードの代わり）。
              <div className="mt-2">
                <CopyField label="セットアップキー（共有鍵）" value={groupKey(setup.shared_key)} />
              </div>
              <details className="mt-2">
                <summary className="cursor-pointer text-xs text-accent">URI で追加する（パスワード管理ツールなど）</summary>
                <div className="mt-2">
                  <CopyField label="otpauth の URI" value={setup.authenticator_uri} />
                </div>
              </details>
            </li>
            <li>アプリに表示された 6 桁の確認コードを入力して、設定を確かめます。</li>
          </ol>
          <TextField
            label="確認コード（6 桁）"
            value={code}
            onChange={(e) => setCode(e.target.value)}
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={8}
            error={error}
            className="sm:max-w-60"
          />
          <div className="flex flex-wrap gap-2">
            <Button type="submit" variant="primary" busy={busy === "enable"}>
              確かめて有効にする
            </Button>
            <Button onClick={() => setSetup(null)}>やめる</Button>
          </div>
        </form>
      ) : (
        <div className="flex flex-col gap-3">
          <Notice tone="info">
            すでに設定している場合、やり直すと今の認証アプリの確認コードは使えなくなります（新しい鍵で設定し直すまで、ログインには確認コードの代わりにリカバリコードかパスキーを使います）。
          </Notice>
          {error && <Notice tone="error">{error}</Notice>}
          <div>
            <Button variant="primary" busy={busy === "setup"} onClick={() => void start()}>
              設定を始める
            </Button>
          </div>
        </div>
      )}
    </Panel>
  );
}

export function AccountPage({ adapter, source }: { adapter: AuthAdapter; source: AuthSource }) {
  const [session, setSession] = useState<Session | null | undefined>(undefined);
  const [name, setName] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loggingOut, setLoggingOut] = useState(false);

  useEffect(() => {
    adapter.me().then(
      (s) => {
        if (!s) {
          window.location.assign(withSource(loginUrl("/account"), source));
          return;
        }
        setSession(s);
        if (s.person_id) void adapter.displayName(s.person_id).then(setName);
      },
      (e: unknown) => setError(authErrorMessage(e, "account")),
    );
  }, [adapter, source]);

  const logout = async () => {
    setLoggingOut(true);
    try {
      await adapter.logout();
      window.location.assign(withSource("/login", source));
    } catch (e) {
      setError(authErrorMessage(e, "account"));
      setLoggingOut(false);
    }
  };

  const local = session?.provider === "local";
  const breakGlass = session?.kind === "break_glass";

  return (
    <AuthFrame
      title="アカウント"
      width="xl"
      nav={
        <nav aria-label="画面" className="flex gap-1 text-sm">
          {!breakGlass && (
            <a href={withSource("/", source)} className="rounded-sm px-3 py-1 text-fg hover:bg-surface-2">
              人事情報へ戻る
            </a>
          )}
          {canUseAdmin(session ?? null) && (
            <a href={withSource("/admin", source)} className="rounded-sm px-3 py-1 text-fg hover:bg-surface-2">
              認証の管理
            </a>
          )}
        </nav>
      }
    >
      {session === undefined && !error ? (
        <Loading />
      ) : !session ? (
        <Notice tone="error">{error}</Notice>
      ) : (
        <div className="flex flex-col gap-4">
          <div className="flex flex-wrap items-end justify-between gap-3">
            <div>
              <h1 className="m-0 text-2xl font-bold">アカウント</h1>
              <p className="mt-1 mb-0 text-sm text-muted">ログインの方法の管理とログアウト</p>
            </div>
            <Button variant="secondary" busy={loggingOut} onClick={() => void logout()}>
              ログアウト
            </Button>
          </div>
          {error && <Notice tone="error">{error}</Notice>}

          <Panel aria-labelledby="session-title">
            <h2 id="session-title" className="m-0 mb-3 text-base font-bold">
              ログイン中
            </h2>
            <dl className="m-0 grid grid-cols-[auto_1fr] gap-x-6 gap-y-2 text-sm">
              <dt className="text-muted">利用者</dt>
              <dd className="m-0 font-bold">{breakGlass ? "緊急用の管理者アカウント" : (name ?? "（氏名を表示できません）")}</dd>
              <dt className="text-muted">ログインの方法</dt>
              <dd className="m-0">
                {local ? "jin-ji のアカウント" : `外部の認証サービス（${session.provider}）`}
                {session.methods.length > 0 && <span className="text-muted">（{session.methods.map(methodLabel).join("・")}）</span>}
              </dd>
              {session.roles.length > 0 && (
                <>
                  <dt className="text-muted">権限</dt>
                  <dd className="m-0">{session.roles.map((r) => (r === "hr_admin" ? "人事管理者" : r)).join("・")}</dd>
                </>
              )}
            </dl>
          </Panel>

          {breakGlass ? (
            <Notice tone="info" title="緊急用の管理者アカウント">
              このアカウントはパスキーだけでログインし、人事情報は見られません。パスキーの追加は、別の管理者が発行する登録リンクで行います。
            </Notice>
          ) : local ? (
            <>
              <PasskeySection adapter={adapter} />
              <TwoFactorSection adapter={adapter} />
            </>
          ) : (
            <Notice tone="info" title="パスワード・多要素認証の設定">
              このアカウントは外部の認証サービスでログインしています。パスワードや多要素認証は、その認証サービス側（会社のアカウントの設定）で管理されます。
            </Notice>
          )}
          {!breakGlass && <DelegationsSection adapter={adapter} />}
        </div>
      )}
    </AuthFrame>
  );
}
