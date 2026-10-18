// ログインの後に戻る先。サーバーの AuthEndpoints.SafeReturnUrl と同じ規則（同じオリジンのパスだけ。オープンリダイレクトの対策）に、
// ログイン画面自身へは戻らない（行き来を繰り返さない）を足す。外れたら "/"。
/** 制御文字（C0・DEL・C1）を含むか。 */
function hasControl(value: string): boolean {
  for (const ch of value) {
    const c = ch.codePointAt(0)!;
    if (c < 0x20 || (c >= 0x7f && c <= 0x9f)) return true;
  }
  return false;
}

export function safeReturnUrl(value: string | null | undefined): string {
  if (
    !value ||
    value.length > 2048 ||
    value[0] !== "/" ||
    value.startsWith("//") ||
    value.includes("\\") ||
    hasControl(value) ||
    /^\/auth\//i.test(value) ||
    /^\/login(?:[/?#]|$)/i.test(value)
  ) {
    return "/";
  }
  return value;
}

/** 今の画面（パス・クエリ・ハッシュ）を戻り先にしたログイン画面の URL。 */
export function loginUrl(returnTo: string = window.location.pathname + window.location.search + window.location.hash): string {
  const safe = safeReturnUrl(returnTo);
  return safe === "/" ? "/login" : `/login?return_url=${encodeURIComponent(safe)}`;
}

/** 外部 IdP へ行く URL（GET /auth/login/{provider}?return_url=）。 */
export function providerLoginUrl(loginUrlFromServer: string, returnTo: string): string {
  const safe = safeReturnUrl(returnTo);
  const sep = loginUrlFromServer.includes("?") ? "&" : "?";
  return `${loginUrlFromServer}${sep}return_url=${encodeURIComponent(safe)}`;
}
