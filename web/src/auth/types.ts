// 認証まわりの画面（ログイン・同意・アカウント・リンク・管理）が使う取得元の共通インターフェース。
// 実装は API アダプタ（/auth・/oauth の JSON API。Cookie のセッション + CSRF のヘッダー）とデモアダプタ（架空のデータ）の 2 つ。
// 型の元は OpenAPI から生成した schema.gen.ts。
import type { Schemas } from "../api/schema.gen";

export type LoginMethods = Schemas["LoginMethodsResponse"];
export type LoginProvider = Schemas["LoginProviderResponse"];
export type Session = Schemas["SessionResponse"];
export type LoginResult = Schemas["LoginResponse"];
export type TwoFactorSetup = Schemas["TwoFactorSetupResponse"];
export type Consent = Schemas["ConsentResponse"];
export type UnboundLogin = Schemas["UnboundLoginResponse"];
export type LoginBinding = Schemas["LoginBindingResponse"];
export type ResetLink = Schemas["ResetLinkResponse"];
export type OAuthClient = Schemas["OAuthClientResponse"];
export type DisclosureLead = Schemas["DisclosureLeadResponse"];
export type CimdDomains = Schemas["CimdDomainsResponse"];
export type LocalAccount = Schemas["LocalAccountResponse"];
export type DisabledAccount = Schemas["DisableAccountResponse"];
export type Passkey = Schemas["PasskeyResponse"];
export type Delegation = Schemas["DelegationResponse"];
export type Webhook = Schemas["WebhookResponse"];
export type WebhookDelivery = Schemas["WebhookDeliveryResponse"];
export type WebhookSecretIssued = Schemas["WebhookSecretResponse"];

export type ClientStatus = OAuthClient["status"];
export type ClientAction = "suspend" | "activate" | "approve" | "reject";
export type ResetPurpose = ResetLink["purpose"];
export type WebhookStatus = Webhook["status"];
export type WebhookDeliveryStatus = WebhookDelivery["status"];

/** 紐付けの候補の表示に使う人の要約（人事管理者だけが引ける。緊急用アカウントは人事情報を見られない）。 */
export interface PersonSummary {
  id: string;
  name: string;
  detail: string | null;
}

/** Webhook の送信の設定（指定しない項目は登録では既定、変更では今のまま）。 */
export interface WebhookSettings {
  /** 1 回に送る件数（1〜1000。既定 100）。 */
  maxEventsPerDelivery?: number;
  /** 要約（changes.summary）に切り替える件数（1 回に送る件数以上 100,000 以下。既定 1000）。 */
  summaryThreshold?: number;
  /** 送信の間隔（1〜3600 秒。既定 10）。 */
  minIntervalSeconds?: number;
}

/** Webhook の登録の変更（指定した項目だけ変える）。 */
export interface WebhookUpdate extends WebhookSettings {
  url?: string;
  status?: WebhookStatus;
}

/** クライアントの登録の変更（指定した項目だけ変える）。 */
export interface ClientUpdate {
  mcpTools?: string[];
}

export interface AuthAdapter {
  readonly kind: "api" | "demo";

  // ---- ログイン ----
  /** 使えるログイン方式。ログインが設定されていない環境（https でない等）では null。 */
  providers(): Promise<LoginMethods | null>;
  /** ログイン中のセッション。ログインしていなければ null。 */
  me(): Promise<Session | null>;
  logout(): Promise<void>;
  loginPassword(userName: string, password: string): Promise<LoginResult>;
  loginTwoFactor(input: { code?: string; recoveryCode?: string }): Promise<LoginResult>;
  /** パスキーでログインする（options の取得 → navigator.credentials.get → 検証）。 */
  loginPasskey(): Promise<LoginResult>;

  // ---- ログイン中の本人のローカルアカウント ----
  registerPasskey(name: string): Promise<void>;
  setupTwoFactor(): Promise<TwoFactorSetup>;
  /** TOTP を有効にし、リカバリコード（10 個）を返す。 */
  enableTwoFactor(code: string): Promise<string[]>;
  /** 登録したパスキー。 */
  listPasskeys(): Promise<Passkey[]>;
  /** パスキーを削除する（最後の 1 つでログインの手段か管理系のロールの多要素が無くなる場合は 409）。 */
  deletePasskey(id: string): Promise<void>;

  // ---- ログイン中の本人の委任（AI エージェントへの許可） ----
  /** 本人の生きている委任。トークン発行（認可サーバー）を使わない環境では null。 */
  listDelegations(): Promise<Delegation[] | null>;
  /** 委任を取り消す（そのクライアントのトークンは次の要求から使えない）。 */
  revokeDelegation(id: string): Promise<void>;
  /** 本人の表示名（人の詳細から。読めなければ null）。 */
  displayName(personId: string): Promise<string | null>;

  // ---- 管理者が発行したリンク ----
  resetPassword(token: string, newPassword: string): Promise<void>;
  resetPasskey(token: string, name: string): Promise<void>;

  // ---- 同意画面 ----
  getConsent(requestId: string): Promise<Consent>;
  /** 同意・拒否して、遷移する先（クライアントの redirect_uri）を返す。 */
  decideConsent(requestId: string, approve: boolean): Promise<string>;

  // ---- 認証の管理（人事管理者・緊急用アカウント） ----
  listUnboundLogins(includeResolved: boolean): Promise<UnboundLogin[]>;
  bindLogin(unboundLoginId: string, personId: string): Promise<LoginBinding>;
  revokeBinding(bindingId: string): Promise<void>;
  /** ローカルアカウントの一覧（query はユーザー名の部分一致）。 */
  listLocalAccounts(query: string): Promise<LocalAccount[]>;
  /** 無効にする（セッションを終え、人のアカウントならその人の委任をすべて失効する）。 */
  disableAccount(accountId: string): Promise<DisabledAccount>;
  enableAccount(accountId: string): Promise<void>;
  unlockAccount(accountId: string): Promise<void>;
  issueResetLink(accountId: string, purpose: ResetPurpose): Promise<ResetLink>;
  /** 人の検索（紐付けの相手を探す。人事管理者だけ）。 */
  searchPeople(query: string): Promise<PersonSummary[]>;
  /** 人の一括取得（紐付けの候補の表示。人事管理者だけ。見えない ID は省かれる）。 */
  peopleByIds(ids: string[]): Promise<PersonSummary[]>;

  // ---- トークン発行のクライアントの管理（人事管理者だけ） ----
  listClients(status?: ClientStatus): Promise<OAuthClient[]>;
  setClientStatus(id: string, action: ClientAction): Promise<OAuthClient>;
  updateClient(id: string, update: ClientUpdate): Promise<OAuthClient>;
  /** 先行提供の日数の起案の一覧（新しい順）。clientId で社内システムを絞れる。 */
  listDisclosureLeads(clientId?: string): Promise<DisclosureLead[]>;
  /** 先行提供の日数（0〜90）を起案する。別の人事管理者の承認で有効になる。承認待ちがあるとき・現在値と同じとき・社内システム以外は拒否。 */
  proposeDisclosureLead(clientId: string, leadDays: number, note: string | null): Promise<DisclosureLead>;
  /** 起案を承認・却下する（起案者本人は不可＝職務分掌）。承認すると前の値は置き換わる。 */
  decideDisclosureLead(leadId: string, approve: boolean): Promise<DisclosureLead>;
  listCimdDomains(): Promise<CimdDomains>;
  addCimdDomain(domain: string): Promise<void>;
  removeCimdDomain(domain: string): Promise<void>;

  // ---- 社内システムへの Webhook（人事管理者だけ） ----
  /** 登録済みの Webhook（件数・最終の成功と失敗・署名の鍵の期限）。 */
  listWebhooks(): Promise<Webhook[]>;
  /** 社内システム（client credentials・REST API・jinji.read）に登録する（クライアントごとに 1 つ。2 つ目は 409）。鍵はこの応答でだけ返る。 */
  registerWebhook(clientId: string, url: string, settings: WebhookSettings): Promise<WebhookSecretIssued>;
  /** 送信先・状態（停止・再開）・設定を変える。 */
  updateWebhook(webhookId: string, update: WebhookUpdate): Promise<Webhook>;
  /** 署名の鍵を入れ替える。旧鍵でも overlapHours（0〜168。省略で 24）の間は署名を続ける。新しい鍵はこの応答でだけ返る。 */
  rotateWebhookSecret(webhookId: string, overlapHours?: number): Promise<WebhookSecretIssued>;
  /** 送信の一覧（新しい順）。status で絞れる。 */
  listWebhookDeliveries(webhookId: string, status?: WebhookDeliveryStatus, limit?: number): Promise<WebhookDelivery[]>;
  /** dead の送信を再送する（deliveryId を省くと dead をすべて。dead でない送信の指定は 409）。再送した件数を返す。 */
  redeliverWebhook(webhookId: string, deliveryId?: string): Promise<number>;
  /** 送信の確かめ（人事データを持たない ping を送る）。送信の ID を返す。 */
  pingWebhook(webhookId: string): Promise<string>;
}

/** 人事管理者のロール（role_grant の hr_admin）。 */
export const HR_ADMIN = "hr_admin";

export function isHrAdmin(session: Session | null): boolean {
  return session?.kind === "person" && session.roles.includes(HR_ADMIN);
}

/** 認証の管理（/admin）を使えるか（人事管理者の人か緊急用アカウント）。 */
export function canUseAdmin(session: Session | null): boolean {
  return session?.kind === "break_glass" || isHrAdmin(session);
}
