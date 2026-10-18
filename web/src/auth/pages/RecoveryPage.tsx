// 管理者が発行したリンクの着地画面（/account-recovery?purpose=password|passkey#token=...）。
// 鍵は URL のフラグメントで届く（サーバーのログ・Referer に残さない）。読み取ったらすぐにアドレス欄・履歴から消し、メモリにだけ持つ。
import { useState, type FormEvent } from "react";
import { withSource, type AuthSource } from "../authAdapter";
import { authErrorMessage, PASSWORD_MIN_LENGTH, passwordRules } from "../messages";
import type { AuthAdapter } from "../types";
import { AuthFrame, Button, Icon, LinkButton, Notice, Panel, TextField } from "../ui";
import { webAuthnSupported } from "../webauthn";

let taken: string | null | undefined;

/** フラグメントの token を読み、アドレス欄から消す（ページの読み込みごとに 1 回だけ読む。StrictMode の二重の呼び出しでも同じ値）。 */
function takeToken(): string | null {
  if (taken === undefined) {
    taken = new URLSearchParams(window.location.hash.replace(/^#/, "")).get("token");
    if (taken) {
      window.history.replaceState(null, "", window.location.pathname + window.location.search);
    }
  }
  return taken;
}

function PasswordForm({ adapter, token, onDone }: { adapter: AuthAdapter; token: string; onDone: () => void }) {
  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [touched, setTouched] = useState(false);
  const rules = passwordRules(password);
  const mismatch = confirm.length > 0 && confirm !== password;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setTouched(true);
    if (rules.some((r) => !r.ok)) {
      setError("パスワードが条件を満たしていません。下の条件を確かめてください。");
      return;
    }
    if (password !== confirm) {
      setError("確認のパスワードが一致しません。");
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await adapter.resetPassword(token, password);
      onDone();
    } catch (err) {
      setError(authErrorMessage(err, "reset"));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={submit} noValidate className="flex flex-col gap-4">
      {error && <Notice tone="error">{error}</Notice>}
      <TextField
        label="新しいパスワード"
        type="password"
        autoComplete="new-password"
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        aria-describedby="password-rules"
        required
      />
      <ul id="password-rules" aria-label="パスワードの条件" className="m-0 -mt-2 grid list-none gap-x-4 gap-y-0.5 p-0 text-xs sm:grid-cols-2">
        {rules.map((r) => (
          <li key={r.label} className={`flex items-center gap-1.5 ${r.ok ? "text-fg" : touched ? "text-danger" : "text-muted"}`}>
            <Icon name={r.ok ? "ok" : touched ? "error" : "info"} className="size-3.5 shrink-0" />
            {r.label}
            <span className="sr-only">{r.ok ? "（満たしています）" : "（未達）"}</span>
          </li>
        ))}
      </ul>
      <TextField
        label="新しいパスワード（確認）"
        type="password"
        autoComplete="new-password"
        value={confirm}
        onChange={(e) => setConfirm(e.target.value)}
        error={mismatch ? "上のパスワードと一致しません。" : null}
        required
      />
      <Button type="submit" variant="primary" busy={busy} full>
        パスワードを設定する
      </Button>
      <p className="m-0 text-xs text-muted">
        設定すると、このアカウントのロックは解除され、ほかの端末でログイン中のセッションは終了します。{PASSWORD_MIN_LENGTH} 文字以上の長い文（例: 単語をいくつか組み合わせたもの）がおすすめです。
      </p>
    </form>
  );
}

function PasskeyForm({ adapter, token, onDone }: { adapter: AuthAdapter; token: string; onDone: () => void }) {
  const [name, setName] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const supported = adapter.kind === "demo" || webAuthnSupported();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await adapter.resetPasskey(token, name.trim());
      onDone();
    } catch (err) {
      setError(authErrorMessage(err, "reset"));
    } finally {
      setBusy(false);
    }
  };

  if (!supported) {
    return <Notice tone="warn">このブラウザはパスキーに対応していません。パスキーを使えるデバイスの最新のブラウザで、このリンクを開いてください。</Notice>;
  }
  return (
    <form onSubmit={submit} className="flex flex-col gap-4">
      {error && <Notice tone="error">{error}</Notice>}
      <TextField
        label="パスキーの名前（任意）"
        hint="あとで見分けられるように。例: 仕事用ノート PC"
        value={name}
        onChange={(e) => setName(e.target.value)}
        maxLength={200}
      />
      <Button type="submit" variant="primary" busy={busy} full>
        <Icon name="key" />
        このデバイスでパスキーを登録
      </Button>
      <p className="m-0 text-xs text-muted">ボタンを押すと、デバイスの指紋・顔認証・PIN の確認が表示されます。</p>
    </form>
  );
}

export function RecoveryPage({ adapter, source }: { adapter: AuthAdapter; source: AuthSource }) {
  const purpose = new URLSearchParams(window.location.search).get("purpose") === "passkey" ? "passkey" : "password";
  const [token] = useState(takeToken);
  const [done, setDone] = useState(false);
  const title = purpose === "passkey" ? "パスキーの登録" : "パスワードの設定";

  return (
    <AuthFrame
      title={title}
      aside={<p className="m-0">このリンクは 1 回だけ使えます。心当たりがない場合は、何もせずに人事の管理者へ連絡してください。</p>}
    >
      <div className="mb-6">
        <h1 className="m-0 text-2xl font-bold">{title}</h1>
        <p className="mt-1 mb-0 text-sm text-muted">
          {purpose === "passkey" ? "管理者が発行したリンクで、ログインに使うパスキーを登録します。" : "管理者が発行したリンクで、jin-ji のアカウントのパスワードを設定します。"}
        </p>
      </div>
      {!token ? (
        <Notice tone="error" title="リンクが正しくありません">
          リンクの一部が欠けているか、すでに開いたリンクです。届いたリンクをそのまま開き直すか、管理者に新しいリンクの発行を依頼してください。
        </Notice>
      ) : done ? (
        <Panel className="flex flex-col gap-4">
          <Notice tone="ok" title={purpose === "passkey" ? "パスキーを登録しました" : "パスワードを設定しました"}>
            ログイン画面から、{purpose === "passkey" ? "登録したパスキー" : "新しいパスワード"}でログインしてください。
          </Notice>
          <LinkButton href={withSource("/login", source)} variant="primary" full>
            ログイン画面へ
          </LinkButton>
        </Panel>
      ) : (
        <Panel>
          {purpose === "passkey" ? (
            <PasskeyForm adapter={adapter} token={token} onDone={() => setDone(true)} />
          ) : (
            <PasswordForm adapter={adapter} token={token} onDone={() => setDone(true)} />
          )}
        </Panel>
      )}
    </AuthFrame>
  );
}
