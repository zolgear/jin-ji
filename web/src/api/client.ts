// OpenAPI から生成した paths の型で引数と応答を縛る、最小の fetch ラッパー。
// 同一オリジンの /api/v1 だけに接続する（CSP connect-src 'self'）。
import { ApiError } from "../data/types";
import type { paths } from "./schema.gen";

type GetPaths = { [P in keyof paths]: paths[P] extends { get: unknown } ? P : never }[keyof paths];
type GetOp<P extends GetPaths> = paths[P] extends { get: infer O } ? O : never;
type PathParams<P extends GetPaths> = GetOp<P> extends { path: infer T } ? T : never;
type QueryParams<P extends GetPaths> = GetOp<P> extends { query: infer T } ? T : never;
type Response<P extends GetPaths> = GetOp<P> extends { response: infer T } ? T : never;

type PostPaths = { [P in keyof paths]: paths[P] extends { post: unknown } ? P : never }[keyof paths];
type PostOp<P extends PostPaths> = paths[P] extends { post: infer O } ? O : never;
type PostPathParams<P extends PostPaths> = PostOp<P> extends { path: infer T } ? T : never;
type PostBody<P extends PostPaths> = PostOp<P> extends { body: infer T } ? T : never;
type PostResponse<P extends PostPaths> = PostOp<P> extends { response: infer T } ? T : never;

/** アクセストークンの取得。UI は Cookie のセッション（BFF）なので、既定は null（ヘッダーを付けない）。 */
export type TokenProvider = () => Promise<string | null>;

export interface ApiClientOptions {
  baseUrl?: string;
  getToken?: TokenProvider;
  fetch?: typeof fetch;
}

/** URL を組み立てる。クエリの配列は同じ名前を繰り返す（例: level=a&level=b）。undefined・空文字は省く。 */
export function buildUrl(
  baseUrl: string,
  path: string,
  pathParams: Record<string, string>,
  query: Record<string, string | readonly string[] | undefined>,
): string {
  const resolved = path.replace(/\{([^}]+)\}/g, (_, name: string) => {
    const value = pathParams[name];
    if (value === undefined) {
      throw new Error(`パスの引数 ${name} がありません`);
    }
    return encodeURIComponent(value);
  });
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    for (const v of typeof value === "string" ? [value] : (value ?? [])) {
      if (v !== "") search.append(key, v);
    }
  }
  const qs = search.toString();
  return `${baseUrl}${resolved}${qs ? `?${qs}` : ""}`;
}

export function createApiClient(options: ApiClientOptions = {}) {
  const baseUrl = options.baseUrl ?? "";
  const getToken = options.getToken ?? (async () => null);
  const doFetch = options.fetch ?? ((input: RequestInfo | URL, init?: RequestInit) => fetch(input, init));

  async function send(url: string, init: RequestInit): Promise<globalThis.Response> {
    const headers = new Headers(init.headers);
    headers.set("Accept", "application/json");
    const token = await getToken();
    if (token) {
      headers.set("Authorization", `Bearer ${token}`);
    }
    const response = await doFetch(url, { ...init, headers, credentials: "same-origin" });
    if (!response.ok) {
      throw await toApiError(response);
    }
    return response;
  }

  async function get<P extends GetPaths>(
    path: P,
    params: { path?: PathParams<P>; query?: QueryParams<P> } = {},
    signal?: AbortSignal,
  ): Promise<Response<P>> {
    const url = buildUrl(
      baseUrl,
      path,
      (params.path ?? {}) as Record<string, string>,
      (params.query ?? {}) as Record<string, string | readonly string[] | undefined>,
    );
    const response = await send(url, { signal });
    return (await response.json()) as Response<P>;
  }

  /**
   * 状態を変える要求（POST）。本文は JSON。204 なら undefined を返す。
   * headers は CSRF のトークン（X-JinJi-CSRF）など、呼び出し側が足すヘッダー。
   */
  async function post<P extends PostPaths>(
    path: P,
    params: { path?: PostPathParams<P>; body?: PostBody<P> } = {},
    headers: Record<string, string> = {},
    signal?: AbortSignal,
  ): Promise<PostResponse<P>> {
    const url = buildUrl(baseUrl, path, (params.path ?? {}) as Record<string, string>, {});
    const init: RequestInit = { method: "POST", headers: { ...headers }, signal };
    if (params.body !== undefined) {
      (init.headers as Record<string, string>)["Content-Type"] = "application/json";
      init.body = JSON.stringify(params.body);
    }
    const response = await send(url, init);
    if (response.status === 204 || response.headers.get("Content-Length") === "0") {
      return undefined as PostResponse<P>;
    }
    return (await response.json()) as PostResponse<P>;
  }

  return { get, post };
}

/** 失敗の応答を ApiError にする（Problem Details なら detail と type、429 なら Retry-After の秒数も）。 */
export async function toApiError(response: globalThis.Response): Promise<ApiError> {
  let detail = response.statusText;
  let type: string | null = null;
  try {
    const problem = (await response.json()) as { title?: string; detail?: string; type?: string };
    detail = problem.detail ?? problem.title ?? detail;
    type = problem.type ?? null;
  } catch {
    // Problem Details でない応答は statusText のまま
  }
  const retry = Number.parseInt(response.headers.get("Retry-After") ?? "", 10);
  return new ApiError(response.status, detail, type, Number.isFinite(retry) && retry >= 0 ? retry : null);
}

export type ApiClient = ReturnType<typeof createApiClient>;

/** CSRF の要求トークンを載せるヘッダー（§6.4。Cookie のセッションで状態を変える要求に要る）。 */
export const CSRF_HEADER = "X-JinJi-CSRF";
const CSRF_PROBLEM = "urn:jinji:problem:csrf";

/**
 * 状態を変える要求に CSRF のヘッダーを付けて送る関数を作る。トークンは GET /auth/csrf で取り、覚えておく。
 * CSRF で拒否されたら（トークンの期限切れ・閲覧者の変化）1 回だけ取り直して再送する。
 */
export function createCsrfSender(client: ApiClient) {
  let token: string | null = null;
  const current = async () => (token ??= (await client.get("/auth/csrf")).token);
  return async function withCsrf<T>(send: (headers: Record<string, string>) => Promise<T>): Promise<T> {
    try {
      return await send({ [CSRF_HEADER]: await current() });
    } catch (e) {
      if (e instanceof ApiError && e.status === 403 && e.problemType === CSRF_PROBLEM) {
        token = null;
        return send({ [CSRF_HEADER]: await current() });
      }
      throw e;
    }
  };
}
