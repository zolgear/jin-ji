# ADR-0004: 認証とトークン発行の実現方式

- ステータス: **承認**
- 日付: 2026-10-18
- 関連: [ADR-0001](0001-language-and-runtime.md)（サプライチェーン方針）、[ADR-0002](0002-data-scope.md)、[ADR-0006](0006-mcp-sdk.md)（MCP サーバー）、[アーキテクチャ概要 §6・§7](../architecture/overview.md#6-認証とトークン)

## 決定

**方式**

1. **人のログインは既存の IdP に委ね、API・MCP 用のトークン発行（認可サーバー）だけを jin-ji が自前の最小実装で持つ。** OpenIddict・Duende IdentityServer は使わない。対応するのは MCP 認可仕様（2026-07-28 版）と社内システム連携に要る範囲だけ: 認可コード + PKCE（S256 のみ）、リフレッシュトークン（回転）、client credentials（`private_key_jwt`）、Client ID Metadata Document（CIMD）、事前登録。動的クライアント登録（DCR）は持たず、DCR しか持たないクライアントは事前登録で対応する。
2. **jin-ji が発行するアクセストークンは参照トークン（不透明な乱数）とし、ハッシュを PostgreSQL に保存する。** 発行者と検証者が同じ jin-ji なので JWT にする必要がない。署名鍵の管理・`alg` の取り違え・失効の遅れという JWT 特有の危険を持ち込まない。
3. **人のログインは ASP.NET Core 標準の OpenID Connect ハンドラ**（Entra ID・汎用 OIDC 共通）とし、Microsoft.Identity.Web は使わない。Entra ID が発行したアプリ専用トークンは JwtBearer ハンドラで受け付ける（REST API のみ。MCP では受け付けない）。
4. **ローカルアカウントは ASP.NET Core Identity（共有フレームワーク）に、Npgsql で書いた自前のストアを差し込む。** パスワード・パスキー・TOTP・ロックアウトは Identity の標準機能を使う。
5. **EF Core は導入しない。** 認証用のテーブルも既存と同じ SQL マイグレーション・項目カタログ・DB ロールの規則で管理する。
6. 追加する NuGet 依存は `Microsoft.AspNetCore.Authentication.OpenIdConnect` と `Microsoft.AspNetCore.Authentication.JwtBearer` の 2 つ（推移的依存を含めて 10 件、すべて Microsoft 発行）だけとする。

**運用の既定**

7. **MCP をインターネットから届くようにするかは導入先が選び、既定は社内限定とする。** 公開する場合はリバースプロキシで MCP と `/oauth/*`・`/.well-known/*` だけを出す。
8. **CIMD の許可ドメインの初期値は Claude・ChatGPT・VS Code（`claude.ai`・`chatgpt.com`・`vscode.dev`）。** 許可ドメインに無いクライアントは拒否ではなく「管理者の承認待ち」とし、承認されるまで認可を進めない。
9. **メールアドレスによる自動の紐付けは一切しない。** 候補として示すだけで、紐付けは人事管理者が行う。
10. **緊急用の管理者アカウントを持つ。** person に紐付けない特別なアカウントで、権限は認証の復旧（IdP の設定・紐付け・ロックアウトの解除）に限り、人事情報は参照できない。パスキーを必須とし、使用は必ず監査ログに残す。
11. **パスワードリセットは管理者が発行するリセットリンクとする。** jin-ji はメール送信（SMTP 等の外向き通信）を持たない。
12. **トークン・セッションの寿命は下記「セキュリティの既定値」の初期値とする**（アクセストークン 10 分、リフレッシュトークン 無操作 7 日・最長 30 日、UI のセッション 無操作 8 時間・最長 12 時間）。
13. **本番運用の前に、自前実装のプロトコル部分について外部のセキュリティレビューを受ける。**

実装は 2 つの段階に分けて規則を定める。**段階 A** は人のログイン（外部 IdP・ローカルアカウント・UI のセッション。[概要 §6.4](../architecture/overview.md#64-人のログインの実装の規則adr-0004-の段階-a)）、**段階 B** はトークン発行（認可サーバー・参照トークン・CIMD・client credentials。[概要 §6.5](../architecture/overview.md#65-トークン発行の実装の規則adr-0004-の段階-b)）。

## 背景

前提:

- 導入先は主に M365 / Entra ID を持つ 200〜1,000 人（最大 1 万人）の日本企業。Keycloak 等の追加の認証基盤は前提にしない（概要 §6）
- ログイン方式は Entra ID・汎用 OIDC（Google、Keycloak、Okta 等）・ローカルアカウントで、同時に複数有効にできる
- 社内システムは client credentials（`private_key_jwt` 推奨）。Entra ID のアプリ登録・マネージド ID のトークンも受け付けたい
- AI エージェント（MCP）は OAuth 2.1 のユーザー委任（認可コード + PKCE）。エージェント単独の権限は持たせない
- Entra ID は MCP クライアントが前提にする動的なクライアント登録に対応しないので、Entra ID をそのまま MCP の認可サーバーにはできない。外部の認可サーバーを立てる構成も、運用の負担から採らない（概要 §6.2・§6.3）
- 依存は最小限・第一者優先（ADR-0001）

### MCP 認可仕様の現状（2026-10 時点で確認した事実）

| 項目 | 内容 |
| --- | --- |
| 最新版 | **2026-07-28**（`/specification/latest` が指す版） |
| 保護リソースメタデータ（RFC 9728） | MCP サーバーは **MUST** 実装。401 の `WWW-Authenticate` に `resource_metadata` を載せ、`scope` も **SHOULD** 載せる |
| 認可サーバーのメタデータ | RFC 8414 か OIDC Discovery の少なくとも一方を **MUST** 提供 |
| クライアント登録 | **CIMD を SHOULD**。**DCR は MAY で、2026-07-28 版で非推奨**（後方互換のためだけに残る）。クライアントの優先順は「事前登録 → CIMD → DCR」 |
| PKCE | S256。`code_challenge_methods_supported` に `S256` を載せる（載せないと ChatGPT は非対応扱い） |
| リソースインジケーター（RFC 8707） | クライアントは認可・トークンの両リクエストに `resource`（MCP サーバーの正規 URI）を **MUST** 付ける |
| audience | MCP サーバーは自分宛てに発行されたトークンだけを受け付ける（**MUST**）。他のトークンの受け入れ・中継は禁止 |
| 発行者の識別（RFC 9207） | 認可応答に `iss` を **SHOULD** 付ける（将来 MUST 化の予告あり） |
| リフレッシュトークン | 公開クライアントでは回転または送信者制約が要る（OAuth 2.1） |

主要クライアントの対応:

| クライアント | 確認した内容 | 確度 |
| --- | --- | --- |
| Claude（claude.ai・Desktop・モバイル・Claude Code） | DCR・CIMD とも既定で対応。CIMD を使う条件は AS メタデータに `client_id_metadata_document_supported: true` と `token_endpoint_auth_methods_supported` の `none` の両方があること。無ければ DCR に落ちる。claude.ai 等のコールバックは `https://claude.ai/api/mcp/auth_callback`、Claude Code はループバック（ポート任意、`localhost` と `127.0.0.1`）。client_credentials は非対応 | 公式文書 |
| ChatGPT | CIMD を推奨の登録方式として対応。DCR にも対応。PKCE S256・`resource`・保護リソースメタデータに対応 | 公式文書（日付の記載なし） |
| VS Code | 2025-11 から安定版で CIMD に対応（`https://vscode.dev/oauth/client-metadata.json`）。事前登録の client ID も設定できる | 第三者の記事 |
| Cursor | DCR のみとする報告がある | 第三者の互換表（未検証） |

重要な帰結: **claude.ai（Web・Desktop・モバイル）の接続は Anthropic のクラウドから MCP サーバーと認可サーバーに到達する**（送信元 `160.79.104.0/21`）。社内網だけに置く構成では、使えるのはローカルで動くクライアント（Claude Code、VS Code 等）に限られる。

## 検討した選択肢

### トークン発行

| 案 | 概要 |
| --- | --- |
| A1. OpenIddict + EF Core ストア | 標準構成。EF Core と Npgsql の EF プロバイダを足す |
| A2. OpenIddict + Npgsql の自前ストア | EF Core を避け、4 つのストアを自前で実装する |
| **B. 自前の最小実装** | 必要なエンドポイントだけを ASP.NET Core の Minimal API で書く。トークンは参照トークン |
| C. Duende IdentityServer | 商用ライセンス。導入先ごとにライセンスが要る |
| （除外）外部の認可サーバー・Entra への事前登録 | 前提により除外（概要 §6.2・§6.3） |

### 比較

依存数は 2026-10-12 に、各案の最小構成を資格情報なしの SDK コンテナで `dotnet restore` し、lock ファイルの件数を数えた（最新の安定版。7 日ルールの確認は採用時に行う）。人のログインに要る OpenIdConnect + JwtBearer の 10 件はどの案でも共通なので、下表は**それに上乗せされる件数**。

| 観点 | A1. OpenIddict + EF | A2. OpenIddict + 自前ストア | **B. 自前** | C. Duende |
| --- | --- | --- | --- | --- |
| 上乗せの依存 | 15（OpenIddict 10、EF Core 4、Npgsql EF 1） | 8（OpenIddict のみ。単一の保守者） | **0** | 7（Duende 系 4、うち 1 つはライセンス検証。他に SAML 関連等） |
| CIMD | 無い（保守者は「時期尚早」と表明、2026-02）。自前のハンドラが要る | 同左 | 自前で実装 | 未確認 |
| DCR | 無い（8.0 の preview 予定で未実装） | 同左 | 実装しない（非推奨） | あり |
| 実装量（推測） | 認可・同意の画面と処理、CIMD ハンドラ | A1 + 4 ストア（概算 150 メソッド。`IQueryable` を受ける API は SQL に写せない） | 本体 1,500〜2,500 行 + テスト | 設定中心 |
| プロトコル実装の安全性 | 実績のある実装。設定誤りの余地は残る | ストアの誤りが直接の欠陥になる | **自前の欠陥の危険が最大**。範囲を絞り、RFC 9700 の項目をテストで固めて抑える | 実績あり |
| 保守性 | ライブラリの大版更新（7→8）への追従。EF の流儀が混ざる | ストアの API 変更に追従 | 全体が見通せる。仕様の改訂は自分で追う | ライセンス更新 |
| 仕様への追従 | ライブラリ待ち（CIMD・DCR は未対応） | 同左 | 自分で即時に追える | ベンダー次第 |
| ライセンス | Apache-2.0 | Apache-2.0 | — | 商用（年商 100 万ドル超の導入先は有償。推測を含む） |

補足（推測を含む）:

- OpenIddict を使っても、ログイン・同意・クライアント上限の判定・CIMD は jin-ji が書く。OpenIddict が肩代わりするのはパラメータ検証・トークン生成・メタデータで、ここは対象を絞れば数百行に収まる
- `OpenIddict.AspNetCore` のメタパッケージは OpenIddict のクライアント機能と Polly を連れてきて 39 件になる。採用するなら個別パッケージを指定する必要がある
- C は導入先（日本企業）ごとにライセンス契約が生じ、「1 コンテナ + PostgreSQL で動く」配布形態に合わない

### 人のログイン（Entra ID・汎用 OIDC）

| 案 | 依存（推移的を含む） | 評価 |
| --- | --- | --- |
| **ASP.NET Core OpenIdConnect + JwtBearer** | **10（すべて Microsoft）** | シングルテナントの Entra ID は `Authority = https://login.microsoftonline.com/{tenant}/v2.0` で足りる。汎用 OIDC と同じ実装で済む |
| Microsoft.Identity.Web | 30（MSAL・Azure.Identity・Key Vault 系を含む） | マルチテナントの発行者検証・トークンキャッシュ・下流 API 呼び出しが要る場合に価値がある。いずれも本件では不要 |

### ローカルアカウントのストア

| 案 | 依存 | 評価 |
| --- | --- | --- |
| **Identity + Npgsql の自前ストア** | **0**（Identity は共有フレームワーク） | 必要なインターフェースだけ実装すればよい（公式文書が Dapper 等の例を示している） |
| Identity + EF Core（認証専用スキーマ） | 7（EF Core 4、Npgsql EF 1、Identity.EF 1、Npgsql） | ストアを書かずに済むが、マイグレーション・項目カタログ・RLS・DB ロールの規則が二系統になる |

## 理由

**案 B（自前の最小実装・参照トークン）+ 標準 OIDC ハンドラ + Identity の自前ストア、EF Core なし**を採る。

1. **OpenIddict の価値が、jin-ji に要る部分と重ならない。** MCP で必須に近い CIMD は OpenIddict に無く、DCR も無い。EF Core を避けると 4 つのストアを自前で書くことになり、自前実装との差は「パラメータ検証とトークン生成」だけになる。ここは範囲を絞れば小さく、テストで固められる
2. **参照トークンで危険の大きい部分を消せる。** 署名鍵の生成・保管・ローテーション、JWKS の公開、`alg`・`kid` の検証が不要になる。退職・一時停止・クライアント停止を次のリクエストから即時に反映できる。1 万人規模なら 1 リクエスト 1 回の索引検索（数秒のメモリキャッシュ付き）で足りる
3. **依存が増えない。** 人のログインに要る 10 件（すべて Microsoft）以外に何も足さない。Microsoft.Identity.Web をやめることで 20 件減る
4. **仕様の改訂に自分で追従できる。** MCP の認可仕様は 2025-06・2025-11・2026-07 と改訂が続き、DCR の非推奨化・`iss` の MUST 化予告のように動いている。ライブラリ待ちにならない
5. **データ管理の規則が一系統のまま。** 認証テーブルも SQL マイグレーション・項目カタログのテスト・アプリ用 DB ロールの権限で守れる

自前実装の危険は「プロトコルの細部の誤り」に集中する。次の対策を必須とする。

- 実装範囲を固定する: `response_type=code` のみ、PKCE は S256 必須（`plain` 拒否）、implicit・ROPC・`id_token` 発行は持たない
- RFC 9700（OAuth 2.0 Security BCP）と MCP 仕様の Security Considerations を項目ごとにテスト化する（redirect_uri の完全一致、コードの単回使用と 60 秒以内の失効、コードと client・redirect_uri・`resource`・PKCE の束縛、`iss` の付与、リフレッシュトークンの再利用検知）
- 実クライアント（Claude Code、VS Code、MCP Inspector、claude.ai）での相互接続を CI 外の受け入れ試験に入れる
- トークン・コード・クライアントシークレットは 256 ビットの乱数、保存は SHA-256 のハッシュ、比較は定数時間

## 設計の要点

### 構成

| 役割 | 置き場所 | 内容 |
| --- | --- | --- |
| 認可サーバー | API ポート | `/.well-known/oauth-authorization-server`、`/oauth/authorize`（ログイン済みの Cookie セッションが前提、同意画面）、`/oauth/token`、`/oauth/revoke`。発行者（issuer）は API の公開 URL |
| MCP（リソースサーバー） | MCP ポート | 保護リソースメタデータ（`authorization_servers` は jin-ji の issuer 1 件のみ）。Bearer（jin-ji の参照トークン）だけを受け付け、Cookie は読まない。MCP SDK の認証ハンドラは使わない（ADR-0006） |
| REST API（リソースサーバー） | API ポート | Cookie（UI の BFF）と Bearer（jin-ji 発行・Entra 発行）を、`Authorization` ヘッダーの有無で振り分ける |

- `resource` は REST API と MCP で別の URI にし、トークンを発行時の `resource` に束縛する。MCP 用のトークンは REST API で使えず、逆も同じ
- `--role mcp` だけで動かす場合も、トークンは DB で検証できる（ログイン画面と発行は `api` の役割が受け持つ）

### クライアントの種類と項目上限

`api_client` 表で、すべてのクライアントを「既定で拒否」の登録制にし、クライアントごとに「渡してよい項目の上限」「スコープ」「MCP ツールの許可リスト」「レート制限」を持たせる（概要 §5.2・§7）。

| 種類 | 識別 | 認証 | 備考 |
| --- | --- | --- | --- |
| 社内システム（jin-ji 発行） | jin-ji が払い出す client_id | `private_key_jwt`（公開鍵を登録）。`client_secret` は移行用の例外として有効期限付きで許す | client assertion は `aud` = issuer、`exp` 5 分以内、`jti` の再利用を拒否 |
| 社内システム（Entra 発行） | Entra のアプリ（`azp`）またはマネージド ID | Entra のトークンを JwtBearer で検証 | `tid` の一致、`aud` = jin-ji の API、`idtyp=app`、アプリロール（`roles`）を確認し、`azp` を登録済みのクライアントに対応付ける。未登録なら有効な署名でも拒否。トークンの版（v1/v2）は jin-ji 側アプリ登録の `accessTokenAcceptedVersion` で決まるため、v2 に揃える設定を手順に含める。REST API のみで受け付ける |
| MCP クライアント（CIMD） | HTTPS の URL | 公開クライアント（`none`）。CIMD に `jwks_uri` があれば `private_key_jwt` も可 | **管理者が許可したドメインだけ**取得する（初期値は `claude.ai`・`chatgpt.com`・`vscode.dev`。それ以外は管理者の承認待ち）。https のみ、私設 IP 宛て禁止、リダイレクト不追従、サイズ・時間の上限、HTTP キャッシュに従う |
| MCP クライアント（事前登録） | jin-ji が払い出す client_id | 公開または機密 | CIMD に対応しないクライアント向け |

- ユーザー委任（MCP）の実効権限は「委任元ユーザーの権限 ∩ スコープ ∩ クライアントの上限」とし、監査ログには委任元・クライアント・エージェントを記録する（概要 §4.6・§7.1）
- スコープは最小限から始める: `jinji.read`（参照）、`jinji.propose`（作業領域への書き込みと変更提案）。承認や管理の操作はスコープを作らない（エージェントにはさせない）
- 同意画面にはクライアント名と redirect_uri のホストを表示し、ループバックしか持たないクライアントには注意書きを出す（MCP 仕様の要求）

### 外部 IdP のユーザーと person の紐付け

ログイン用の紐付けは取り込み用の `external_identity` と分け、`login_binding`（IdP の識別子、主体の識別子、person_id、紐付けの根拠、作成者）として持つ。1 つの主体は 1 人にだけ紐付き、1 人は複数の主体（Entra とローカル等）を持てる。

| 順位 | 照合のしかた | 自動で紐付けるか |
| --- | --- | --- |
| 1 | 既存の `login_binding`（Entra は `tid` + `oid`、汎用 OIDC は `iss` + `sub`） | — |
| 2 | Entra: `oid` が、Entra コネクタで取り込んだ `external_identity(entra_id)` と一致し、`tid` が設定のテナントと一致 | する |
| 3 | 社員番号のクレーム（Entra の `employeeId` を任意クレームで出す等、IdP ごとに設定）が person の社員番号と一致 | IdP ごとに「このクレームを信頼する」と設定した場合だけ |
| 4 | メールアドレス | **しない**（Microsoft も `email`・`preferred_username`・`upn` を認可に使うなと明記）。候補として提示するだけ |

- 紐付けできない場合はログインを拒否し、「未紐付けのログイン」として人事管理者に示す。jin-ji は正ではないので person を自動作成しない。手動の紐付けは監査し、hr_admin の操作とする
- 退職済み・一時停止中の person はログインできず、発行済みのトークン・セッションも次のリクエストから無効にする（参照トークンなので即時に効く）

### ローカルアカウント

- Identity のストアは `IUserStore`・`IUserPasswordStore`・`IUserSecurityStampStore`・`IUserLockoutStore`・`IUserTwoFactorStore`・`IUserAuthenticatorKeyStore`（TOTP）・`IUserTwoFactorRecoveryCodeStore`・`IUserPasskeyStore`（.NET 10）・`IUserEmailStore` を Npgsql で実装する（推測: 本体 600〜900 行）。ロールは Identity のものを使わず、jin-ji の既存のロール（`viewer_roles`）に寄せる
- パスキーは .NET 10 の Identity 標準を使う。`IdentityPasskeyOptions.ServerDomain` を明示する（既定は Host ヘッダーから推定されるため）。アテステーションは既定で検証されない
- 管理系ロールを持つローカルアカウントには、パスキーか TOTP を必須にする
- パスワードリセットは、管理者が発行する 1 回限り・短命のリセットリンクとする（jin-ji はメール送信を持たない）

### セキュリティの既定値（初期値。運用で調整）

| 項目 | 既定 |
| --- | --- |
| UI の Cookie | `__Host-` 接頭辞、`Secure`、`HttpOnly`、`SameSite=Lax`。ログインのたびに新しいセッションを発行（セッション固定対策）。無操作 8 時間・最長 12 時間 |
| CSRF | Cookie 認証の状態変更リクエストは独自ヘッダー + ASP.NET Core の Antiforgery を必須にする。Bearer と MCP ポートは Cookie を読まないので対象外 |
| アクセストークン | 10 分。参照トークン |
| リフレッシュトークン | MCP のみ発行。毎回回転し、使用済みのトークンの再利用を検知したら同じ系列を全失効し監査に残す。無操作 7 日・最長 30 日 |
| 認可コード | 60 秒・単回使用 |
| Data Protection の鍵 | PostgreSQL に保存（`IXmlRepository` を自前実装。2 メソッド）し、ファイルまたは環境変数で渡す X.509 証明書で暗号化。Azure Key Vault は追加の依存になるので任意の拡張とする |
| ブルートフォース | Identity のロックアウト（5 回で 15 分）+ RateLimiter（IP × アカウント、トークンエンドポイントは IP × client_id）。ロックアウトによるアカウントの妨害を抑えるため、IP 単位の制限を先に効かせる |
| 監査 | ログイン成功・失敗、紐付けの作成・変更、同意、トークン発行・失効、再利用検知、クライアント登録・変更、CIMD の取得失敗を `audit_event` に記録 |
| Webhook の署名の鍵 | 社内システムへの Webhook（概要 §9）の HMAC-SHA256 の鍵は 32 バイトの乱数で、上の Data Protection（目的を分ける）で暗号化した値だけを DB に持ち、平文は登録・ローテーションの応答で 1 回だけ返す。登録・変更・ローテーション・再送は人事管理者の人の操作として監査し、送信するワーカーにも同じ証明書を与える |
| 異常検知 | 概要 §7 の検知（自動スロットル → 一時停止 → 通知）に、トークンエンドポイントの失敗の急増と再利用検知を加える |

## 帰結

### 良い点

- 追加の依存は Microsoft 発行の 10 件だけで、EF Core も Azure SDK も入らない
- トークンの失効が即時で、退職・一時停止・クライアント停止が API・MCP に遅れなく効く
- MCP 仕様の改訂（CIMD・`iss`・DCR の扱い）に自分のペースで追従できる

### 失うもの・注意点

| 注意点 | 対処 |
| --- | --- |
| プロトコル実装の欠陥は自分の責任になる | 範囲の固定、RFC 9700 のテスト化、実クライアントでの受け入れ試験。外部のセキュリティレビューを本番運用前の完了条件にする |
| 参照トークンは第三者が自分で検証できない | 第三者に検証させる需要が出たら、RFC 9068 の JWT を追加する（Microsoft.IdentityModel.JsonWebTokens は既に推移的に入っている） |
| DCR しか持たないクライアントは使えない | 事前登録で対応する。需要が大きければ DCR を管理者承認付きで追加することを検討する |
| claude.ai などクラウド側のクライアントは、MCP と認可サーバーがインターネットから届くことを要する | 公開する場合はリバースプロキシで MCP と `/oauth/*`・`/.well-known/*` だけを出す。社内限定ならローカルのクライアントに限る |

## 参照した情報源（2026-10-12 確認）

- MCP Authorization（2026-07-28 版・draft）: https://modelcontextprotocol.io/specification/latest/basic/authorization
- MCP Client Registration（CIMD・DCR の非推奨化）: https://modelcontextprotocol.io/specification/draft/basic/authorization/client-registration
- Claude の接続の認証要件: https://claude.com/docs/connectors/building/authentication
- ChatGPT（Apps SDK）の認証: https://developers.openai.com/apps-sdk/build/auth
- VS Code の CIMD 対応（第三者）: https://den.dev/blog/cimd-vs-code-mcp/
- OpenIddict の DCR・CIMD に関する issue #2404: https://github.com/openiddict/openiddict-core/issues/2404
- ASP.NET Core のパスキー（.NET 10）: https://learn.microsoft.com/aspnet/core/security/authentication/passkeys/?view=aspnetcore-10.0
- Identity のカスタムストア: https://learn.microsoft.com/aspnet/core/security/authentication/identity-custom-storage-providers?view=aspnetcore-10.0
- Entra ID のクレーム検証: https://learn.microsoft.com/entra/identity-platform/claims-validation
- RFC 9700（OAuth 2.0 Security BCP）、RFC 9728、RFC 8707、RFC 9207、RFC 7591、RFC 7523、draft-ietf-oauth-v2-1、draft-ietf-oauth-client-id-metadata-document
- 依存数: 資格情報を持たない `mcr.microsoft.com/dotnet/sdk:10.0` コンテナでの `dotnet restore`（作業用ディレクトリで実施。リポジトリは変更していない）

未確認・推測として扱ったもの: Cursor の対応状況、Duende のライセンス条件の現行版、実装量の見積もり。
