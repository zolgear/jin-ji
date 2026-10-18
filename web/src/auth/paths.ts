// 認証まわりの画面のパス。サーバー（OAuthEndpoints の /login・/consent、リンクの /account-recovery）と揃える。
export const AUTH_PATHS = ["/login", "/consent", "/account", "/account-recovery", "/admin"] as const;
export type AuthPath = (typeof AUTH_PATHS)[number];

/** パスが認証の画面なら、そのパス（末尾の / は無視）。それ以外は null。 */
export function authPath(pathname: string): AuthPath | null {
  const normalized = pathname.length > 1 ? pathname.replace(/\/+$/, "") : pathname;
  return (AUTH_PATHS as readonly string[]).includes(normalized) ? (normalized as AuthPath) : null;
}
