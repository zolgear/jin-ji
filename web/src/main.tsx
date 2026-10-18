import "@fontsource/biz-udpgothic/400.css";
import "@fontsource/biz-udpgothic/700.css";
import "./index.css";
import { lazy, StrictMode, Suspense } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import { authPath } from "./auth/paths";
import { initTheme } from "./lib/theme";

// 認証まわりの画面（/login・/consent・/account・/account-recovery・/admin）はパスで開く。人事情報の画面はハッシュ（/#/...）
const AuthApp = lazy(() => import("./auth/AuthApp"));

initTheme();

const path = authPath(window.location.pathname);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {path ? (
      <Suspense fallback={null}>
        <AuthApp path={path} />
      </Suspense>
    ) : (
      <App />
    )}
  </StrictMode>,
);
