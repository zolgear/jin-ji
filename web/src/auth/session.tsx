// 人事情報の画面（組織図・3D・検索）のログインの確認と、利用者メニュー。
// API のときは GET /auth/me で確かめ、ログインしていなければログイン画面へ（戻り先は今の画面）。緊急用アカウントは人事情報を見られないので認証の管理へ。
// デモ（架空データ）ではログインを求めない（今までどおり動く）。メニューはデモの利用者で出す。
import { createContext, useContext, useEffect, useState, type ReactNode } from "react";
import { Popover, type PopoverSide } from "../components/popover";
import { Spinner } from "../components/ui";
import { loadAuthAdapter, withSource, type AuthSource } from "./authAdapter";
import { loginUrl } from "./returnUrl";
import { canUseAdmin, type AuthAdapter, type Session } from "./types";

interface SessionState {
  source: AuthSource;
  adapter: AuthAdapter;
  session: Session;
}

const SessionContext = createContext<SessionState | null>(null);

export function useSession(): SessionState | null {
  return useContext(SessionContext);
}

export function SessionGate({ source, children }: { source: AuthSource; children: ReactNode }) {
  const [state, setState] = useState<{ source: AuthSource; value: SessionState | null } | null>(null);

  useEffect(() => {
    let cancelled = false;
    void (async () => {
      let value: SessionState | null = null;
      try {
        const adapter = await loadAuthAdapter(source);
        const session = await adapter.me();
        if (source === "api" && !session) {
          window.location.assign(withSource(loginUrl(), source));
          return;
        }
        if (source === "api" && session?.kind === "break_glass") {
          window.location.assign(withSource("/admin", source));
          return;
        }
        value = session ? { source, adapter, session } : null;
      } catch {
        // 確かめられない（ネットワーク・サーバーの誤り）ときは画面を出し、各画面の取得がそれぞれ誤りを示す
        value = null;
      }
      if (!cancelled) setState({ source, value });
    })();
    return () => {
      cancelled = true;
    };
  }, [source]);

  if (!state || state.source !== source) {
    return (
      <div className="flex h-dvh items-center justify-center bg-bg">
        <Spinner label="ログインを確認中" />
      </div>
    );
  }
  return <SessionContext.Provider value={state.value}>{children}</SessionContext.Provider>;
}

/**
 * 利用者メニュー（名前・アカウント・認証の管理・ログアウト）。開くボタンの形は置き場所（画面の左のレール・スマホの下のタブ）が決める。
 * 開閉・外を押して閉じる・Esc は components/popover.tsx（ブラウザの Popover API）に任せる。
 */
export function UserMenu({
  renderTrigger,
  triggerClassName,
  side = "bottom",
}: {
  renderTrigger: (name: string | null) => ReactNode;
  triggerClassName: string;
  side?: PopoverSide;
}) {
  const state = useSession();
  const [name, setName] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    if (state?.session.person_id) void state.adapter.displayName(state.session.person_id).then(setName);
  }, [state]);

  if (!state) return null;
  const { source, adapter, session } = state;
  const label = name ?? "ログイン中";

  const logout = async () => {
    setBusy(true);
    try {
      await adapter.logout();
    } finally {
      window.location.assign(withSource("/login", source));
    }
  };

  return (
    <Popover id="user-menu" label={`利用者メニュー（${label}）`} side={side} width={240} trigger={renderTrigger(name)} triggerClassName={triggerClassName}>
      <p className="m-0 border-b border-border px-4 py-3 text-sm font-bold">
        {label}
        {source === "demo" && <span className="block text-xs font-normal text-muted">デモの利用者（架空）</span>}
      </p>
      <ul className="m-0 list-none p-1.5">
        <li>
          <a href={withSource("/account", source)} className="block rounded-md px-3 py-2 text-fg no-underline hover:bg-surface-2">
            アカウント
          </a>
        </li>
        {canUseAdmin(session) && (
          <li>
            <a href={withSource("/admin", source)} className="block rounded-md px-3 py-2 text-fg no-underline hover:bg-surface-2">
              認証の管理
            </a>
          </li>
        )}
        <li>
          <button type="button" disabled={busy} onClick={() => void logout()} className="block w-full rounded-md px-3 py-2 text-left hover:bg-surface-2">
            ログアウト
          </button>
        </li>
      </ul>
    </Popover>
  );
}
