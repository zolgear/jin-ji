// 認証まわりの画面（パスで開く）。API のサーバーは拡張子の無いパスに index.html を返すので、/login などをそのまま開ける。
// 人事情報の画面（組織図・3D・検索）は従来どおりハッシュ（/#/org-chart）で持つ。
import { LoginPage } from "./pages/LoginPage";
import { ConsentPage } from "./pages/ConsentPage";
import { AccountPage } from "./pages/AccountPage";
import { RecoveryPage } from "./pages/RecoveryPage";
import { AdminPage } from "./pages/AdminPage";
import { authSourceFromLocation, useAuthAdapter } from "./authAdapter";
import type { AuthPath } from "./paths";
import { Loading } from "./ui";

export default function AuthApp({ path }: { path: AuthPath }) {
  const source = authSourceFromLocation();
  const adapter = useAuthAdapter(source);
  if (!adapter) {
    return (
      <div className="min-h-dvh bg-bg">
        <Loading />
      </div>
    );
  }
  switch (path) {
    case "/login":
      return <LoginPage adapter={adapter} source={source} />;
    case "/consent":
      return <ConsentPage adapter={adapter} source={source} />;
    case "/account":
      return <AccountPage adapter={adapter} source={source} />;
    case "/account-recovery":
      return <RecoveryPage adapter={adapter} source={source} />;
    case "/admin":
      return <AdminPage adapter={adapter} source={source} />;
  }
}
