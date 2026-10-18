// 認証まわりの画面に出す文。利用者を混乱させない日本語にし、アカウントの有無・ロック・退職などを推測させない
// （サーバーも失敗の理由を区別しない。§6.4「失敗の応答はユーザー名の有無・ロックアウト・退職・一時停止を区別しない」）。
import { ApiError } from "../data/types";
import { PROBLEM } from "./apiAuthAdapter";

/** 待つ時間の言い方（Retry-After の秒数から）。 */
export function waitPhrase(seconds: number | null): string {
  if (seconds === null || seconds <= 0) return "しばらく";
  if (seconds < 60) return `${seconds} 秒ほど`;
  return `${Math.ceil(seconds / 60)} 分ほど`;
}

/** パスキー（WebAuthn）の操作の失敗。DOMException の name で分ける。 */
function passkeyMessage(error: DOMException): string | null {
  switch (error.name) {
    case "NotAllowedError":
      return "パスキーの操作が取り消されたか、時間切れになりました。もう一度お試しください。";
    case "InvalidStateError":
      return "このデバイスのパスキーはすでに登録されています。";
    case "SecurityError":
      return "この接続ではパスキーを使えません（https の正しいアドレスで開いているか確かめてください）。";
    case "NotSupportedError":
      return "このブラウザまたはデバイスはパスキーに対応していません。";
    case "AbortError":
      return "パスキーの操作を中断しました。";
    default:
      return null;
  }
}

/** 文脈ごとの言い分け。 */
export type AuthContext = "login" | "two-factor" | "passkey-login" | "account" | "reset" | "consent" | "admin";

/** 画面に出すエラーの文。 */
export function authErrorMessage(error: unknown, context: AuthContext): string {
  if (error instanceof DOMException) {
    const m = passkeyMessage(error);
    if (m) return m;
  }
  if (error instanceof ApiError) {
    if (error.problemType === PROBLEM.csrf) {
      return "画面を開いてから時間がたったため、操作を確認できませんでした。ページを再読み込みしてからやり直してください。";
    }
    if (error.status === 429) {
      return `試行の回数が上限に達しました。${waitPhrase(error.retryAfter)}待ってからもう一度お試しください。`;
    }
    if (error.problemType === PROBLEM.mfaRequired) {
      return "このアカウントは管理者の権限を持つため、パスキー、またはパスワードと認証アプリの確認コードでログインしてください。どちらも未登録の場合は、別の管理者にパスキーの登録リンクの発行を依頼してください。";
    }
    if (error.problemType === PROBLEM.unavailable) {
      return "この方法は今の設定では使えません。別の方法でログインするか、管理者にお問い合わせください。";
    }
    if (error.problemType === PROBLEM.loginFailed || error.status === 401) {
      switch (context) {
        case "login":
          return "ユーザー名またはパスワードが正しくないか、このアカウントでは現在ログインできません。入力を確かめてもう一度お試しください。";
        case "two-factor":
          return "確認コードが正しくないか、有効期限が切れています。認証アプリに表示されている最新の 6 桁を入力してください。";
        case "passkey-login":
          return "パスキーでログインできませんでした。登録したパスキーを選んだか確かめてください。解決しない場合は別の方法でログインしてください。";
        default:
          return "ログインの有効期限が切れました。もう一度ログインしてください。";
      }
    }
    if (error.problemType === PROBLEM.invalidLink && context === "reset") {
      return error.message.startsWith("リンクが無効")
        ? "このリンクは無効か、有効期限が切れています。管理者に新しいリンクの発行を依頼してください。"
        : "パスワードが条件を満たしていません。下の条件を確かめてください。";
    }
    if (error.status === 404) {
      switch (context) {
        case "consent":
          return "この許可の依頼は見つからないか、有効期限（10 分）が切れています。アプリに戻って、接続をもう一度やり直してください。";
        case "reset":
          return "この方法は今の設定では使えません。管理者にお問い合わせください。";
        default:
          return "対象が見つかりません。画面を再読み込みしてください。";
      }
    }
    if (error.status === 403) {
      return context === "consent"
        ? "この依頼は、jin-ji にログインした本人だけが許可・拒否できます。"
        : `この操作を行う権限がありません。${error.message && !/^Forbidden$/i.test(error.message) ? `（${error.message}）` : ""}`;
    }
    if (error.status === 409 || error.status === 400) {
      return error.message;
    }
    if (error.status >= 500) {
      return "サーバーで問題が起きました。時間をおいてもう一度お試しください。";
    }
    return `処理できませんでした（${error.status}）。${error.message}`;
  }
  if (error instanceof TypeError) {
    return "サーバーに接続できませんでした。ネットワークの状態を確かめてください。";
  }
  return error instanceof Error ? error.message : String(error);
}

/** 外部 IdP のログインから戻ったときの /login?error= の文。 */
export function loginRedirectError(code: string | null): string | null {
  switch (code) {
    case null:
    case "":
      return null;
    case "unbound":
      return "サインインはできましたが、このアカウントはまだ jin-ji の社員情報と結び付けられていません。人事の管理者に連絡してください（管理者の画面に記録されています）。";
    case "inactive":
    case "rejected":
      return "このアカウントでは現在 jin-ji にログインできません。心当たりがない場合は人事の管理者に連絡してください。";
    default:
      return "外部の認証サービスでのログインを完了できませんでした。もう一度お試しください。";
  }
}

/** パスワードの条件（ASP.NET Core Identity の既定の文字種 + 最小の長さ。サーバーの PasswordMinLength の既定 12）。 */
export const PASSWORD_MIN_LENGTH = 12;

export interface PasswordRule {
  label: string;
  ok: boolean;
}

export function passwordRules(password: string, minLength = PASSWORD_MIN_LENGTH): PasswordRule[] {
  return [
    { label: `${minLength} 文字以上`, ok: [...password].length >= minLength },
    { label: "英大文字（A〜Z）を含む", ok: /[A-Z]/.test(password) },
    { label: "英小文字（a〜z）を含む", ok: /[a-z]/.test(password) },
    { label: "数字（0〜9）を含む", ok: /[0-9]/.test(password) },
    { label: "記号（! # % など）を含む", ok: /[^A-Za-z0-9]/.test(password) },
  ];
}
