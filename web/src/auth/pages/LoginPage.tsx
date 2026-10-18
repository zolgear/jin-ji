// ログイン（/login）。有効な方式（GET /auth/providers）だけを出す: 外部 IdP への遷移・パスキー・パスワード（→ 確認コード）。
// ログインできたら戻り先（?return_url=。同じオリジンのパスだけ）へ。緊急用アカウントは認証の管理（/admin）へ。
// 失敗の文はアカウントの有無・ロック・退職を区別しない（サーバーと同じ）。
import { useEffect, useRef, useState, type FormEvent } from "react";
import { withSource, type AuthSource } from "../authAdapter";
import { authErrorMessage, loginRedirectError } from "../messages";
import { providerLoginUrl, safeReturnUrl } from "../returnUrl";
import type { AuthAdapter, LoginMethods, LoginResult } from "../types";
import { AuthFrame, Button, Icon, LinkButton, Loading, Notice, Panel, TextField } from "../ui";
import { webAuthnSupported } from "../webauthn";

type Step = { kind: "start" } | { kind: "two-factor"; recovery: boolean };

export function LoginPage({ adapter, source }: { adapter: AuthAdapter; source: AuthSource }) {
  const params = new URLSearchParams(window.location.search);
  const returnUrl = safeReturnUrl(params.get("return_url"));
  const redirectError = loginRedirectError(params.get("error"));
  const [methods, setMethods] = useState<LoginMethods | null | undefined>(undefined);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [step, setStep] = useState<Step>({ kind: "start" });
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [busy, setBusy] = useState<"password" | "two-factor" | "passkey" | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const codeRef = useRef<HTMLInputElement>(null);
  const passkeyAvailable = adapter.kind === "demo" || webAuthnSupported();

  useEffect(() => {
    adapter.providers().then(setMethods, (e: unknown) => setLoadError(authErrorMessage(e, "login")));
  }, [adapter]);

  useEffect(() => {
    if (step.kind === "two-factor") codeRef.current?.focus();
  }, [step]);

  const finish = (result: LoginResult) => {
    if (result.status === "two_factor_required") {
      setStep({ kind: "two-factor", recovery: false });
      setPassword("");
      return;
    }
    setDone(true);
    // 緊急用アカウントは人事情報を見られないので、認証の管理へ
    const next = result.session?.kind === "break_glass" ? "/admin" : returnUrl;
    window.location.assign(withSource(next, source));
  };

  const run = async (kind: NonNullable<typeof busy>, action: () => Promise<LoginResult>, context: "login" | "two-factor" | "passkey-login") => {
    setBusy(kind);
    setError(null);
    try {
      finish(await action());
    } catch (e) {
      setError(authErrorMessage(e, context));
      if (context === "two-factor") {
        setCode("");
        codeRef.current?.focus();
      }
    } finally {
      setBusy(null);
    }
  };

  const onPassword = (e: FormEvent) => {
    e.preventDefault();
    if (!userName.trim() || !password) {
      setError("ユーザー名とパスワードを入力してください。");
      return;
    }
    void run("password", () => adapter.loginPassword(userName.trim(), password), "login");
  };

  const onTwoFactor = (e: FormEvent) => {
    e.preventDefault();
    if (step.kind !== "two-factor") return;
    const value = code.replace(/\s/g, "");
    if (!step.recovery && !/^\d{6}$/.test(value)) {
      setError("確認コードは 6 桁の数字で入力してください。");
      return;
    }
    if (step.recovery && value.length === 0) {
      setError("リカバリコードを入力してください。");
      return;
    }
    void run("two-factor", () => adapter.loginTwoFactor(step.recovery ? { recoveryCode: value } : { code: value }), "two-factor");
  };

  const hasAny = methods && (methods.providers.length > 0 || methods.password || (methods.passkey && passkeyAvailable));

  return (
    <AuthFrame
      title="ログイン"
      aside={
        <p className="m-0">
          パスワードを忘れた・パスキーのデバイスをなくしたなど、ログインできないときは人事の管理者に再設定のリンクの発行を依頼してください。jin-ji からメールは送りません。
        </p>
      }
    >
      <div className="mb-6">
        <h1 className="m-0 text-2xl font-bold">ログイン</h1>
        <p className="mt-1 mb-0 text-sm text-muted">人事情報を見るには、会社のアカウントでログインしてください。</p>
      </div>

      {redirectError && step.kind === "start" && <Notice tone="error" className="mb-4">{redirectError}</Notice>}
      {adapter.kind === "demo" && (
        <Notice tone="warn" className="mb-4" title="デモ（架空のデータ）">
          ユーザー名 <kbd className="font-mono">demo</kbd>・パスワードは任意・確認コード <kbd className="font-mono">123456</kbd> でログインできます。
        </Notice>
      )}

      {methods === undefined && !loadError ? (
        <Loading label="ログイン方法を確認中" />
      ) : loadError ? (
        <Notice tone="error">{loadError}</Notice>
      ) : !hasAny ? (
        <Notice tone="warn" title="この環境ではログインできません">
          ログインの方法が設定されていません。システムの管理者にお問い合わせください。
        </Notice>
      ) : done ? (
        <Loading label="ログインしました。移動しています" />
      ) : step.kind === "two-factor" ? (
        <Panel aria-labelledby="two-factor-title">
          <h2 id="two-factor-title" className="m-0 flex items-center gap-2 text-base font-bold">
            <Icon name="shield" className="size-5 text-accent" />
            {step.recovery ? "リカバリコードの入力" : "確認コードの入力"}
          </h2>
          <p className="mt-1 mb-4 text-sm text-muted">
            {step.recovery
              ? "認証アプリを使えないときは、設定のときに保存したリカバリコードを 1 つ入力してください（1 つのコードは 1 回だけ使えます）。"
              : "スマートフォンの認証アプリに表示されている 6 桁の確認コードを入力してください。"}
          </p>
          <form onSubmit={onTwoFactor} noValidate className="flex flex-col gap-4">
            <TextField
              ref={codeRef}
              label={step.recovery ? "リカバリコード" : "確認コード（6 桁）"}
              value={code}
              onChange={(e) => setCode(e.target.value)}
              inputMode={step.recovery ? "text" : "numeric"}
              autoComplete="one-time-code"
              maxLength={step.recovery ? 32 : 8}
              className=""
              error={error}
              spellCheck={false}
            />
            <Button type="submit" variant="primary" busy={busy === "two-factor"} full>
              確認してログイン
            </Button>
          </form>
          <div className="mt-4 flex flex-wrap justify-between gap-2 text-sm">
            <Button
              variant="ghost"
              className="px-0"
              onClick={() => {
                setStep({ kind: "two-factor", recovery: !step.recovery });
                setCode("");
                setError(null);
              }}
            >
              {step.recovery ? "確認コードを使う" : "リカバリコードを使う"}
            </Button>
            <Button
              variant="ghost"
              className="px-0"
              onClick={() => {
                setStep({ kind: "start" });
                setCode("");
                setError(null);
              }}
            >
              最初からやり直す
            </Button>
          </div>
        </Panel>
      ) : (
        <div className="flex flex-col gap-4">
          {error && <Notice tone="error">{error}</Notice>}

          {methods!.providers.length > 0 && (
            <Panel aria-labelledby="sso-title">
              <h2 id="sso-title" className="m-0 mb-3 text-sm font-bold text-muted">
                会社のアカウント
              </h2>
              <ul className="m-0 flex list-none flex-col gap-2 p-0">
                {methods!.providers.map((p) => (
                  <li key={p.name}>
                    <LinkButton href={providerLoginUrl(p.login_url, returnUrl)} variant={methods!.providers[0] === p ? "primary" : "secondary"} full className="justify-between text-left">
                      <span className="min-w-0">{p.display_name} でログイン</span>
                      <Icon name="arrow" className="size-4 shrink-0" />
                    </LinkButton>
                  </li>
                ))}
              </ul>
            </Panel>
          )}

          {(methods!.password || (methods!.passkey && passkeyAvailable)) && (
            <Panel aria-labelledby="local-title">
              <h2 id="local-title" className="m-0 mb-3 text-sm font-bold text-muted">
                jin-ji のアカウント
              </h2>
              {methods!.passkey && passkeyAvailable && (
                <Button
                  variant={methods!.providers.length === 0 ? "primary" : "secondary"}
                  full
                  busy={busy === "passkey"}
                  disabled={busy !== null && busy !== "passkey"}
                  onClick={() => void run("passkey", () => adapter.loginPasskey(), "passkey-login")}
                >
                  <Icon name="key" />
                  パスキーでログイン
                </Button>
              )}
              {methods!.passkey && passkeyAvailable && methods!.password && (
                <div className="my-4 flex items-center gap-3 text-xs text-muted" aria-hidden="true">
                  <span className="h-px flex-1 bg-border" />
                  またはパスワード
                  <span className="h-px flex-1 bg-border" />
                </div>
              )}
              {methods!.password && (
                <form onSubmit={onPassword} noValidate className="flex flex-col gap-4">
                  <TextField
                    label="ユーザー名"
                    value={userName}
                    onChange={(e) => setUserName(e.target.value)}
                    autoComplete="username"
                    autoCapitalize="none"
                    spellCheck={false}
                    required
                  />
                  <TextField
                    label="パスワード"
                    type="password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="current-password"
                    required
                  />
                  <Button type="submit" variant={methods!.passkey && passkeyAvailable ? "secondary" : "primary"} busy={busy === "password"} disabled={busy !== null && busy !== "password"} full>
                    ログイン
                  </Button>
                </form>
              )}
            </Panel>
          )}
        </div>
      )}
    </AuthFrame>
  );
}
