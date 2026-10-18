# 運用ガイド

jin-ji を動かし続けるための設定と保守。ポート・待ち受け、設定の一覧、人のログインとトークン発行の管理、Web UI の配信、メトリクス、保持期限の消去を扱う。開発環境での起動は [はじめに](getting-started.md)、API の仕様は [API 仕様書](api.md)。

管理 CLI はすべて `scripts/dotnet.sh run --project src/JinJi.Host -- <コマンド>` の形で実行する（接続文字列は `ConnectionStrings:App`。操作はすべて監査ログに残る）。

## 目次

- [ポートと待ち受け](#ポートと待ち受け)
- [設定（appsettings.json / 環境変数）](#設定appsettingsjson--環境変数)
- [人のログイン](#人のログイン)
- [トークン発行（API・MCP）](#トークン発行apimcp)
- [Web UI の配信](#web-ui-の配信)
- [メトリクス](#メトリクス)
- [保持期限の消去](#保持期限の消去)
- [マイグレーションの適用](#マイグレーションの適用)

## ポートと待ち受け

1 つの実行ファイル（`JinJi.Host`）を役割で切り替える。

| 役割 | 内容 |
| --- | --- |
| `all`（既定） | API・MCP・ワーカーを 1 つのプロセスで動かす |
| `api` | REST API・認証・トークン発行・Web UI の配信 |
| `mcp` | MCP サーバー |
| `worker` | 保持期限の消去・修正依頼の通知・Webhook の送信などの定期実行 |

- 役割は `--role` か環境変数 `JINJI_ROLE` で指定する
- API は 28080、MCP は 28090、メトリクスは 28100 で待ち受ける。接続を受けたポートでエンドポイントを振り分けるので、MCP のエンドポイントに API のポートから到達することはない（公開面は [API 仕様書](api.md#2-ポートと公開面)）
- 待ち受けアドレスの既定は 127.0.0.1。コンテナから公開する場合は `JinJi__Listen__Address=0.0.0.0` を指定し、ポートの公開側で 127.0.0.1 に絞る
- メトリクスのポートは `JinJi:Listen:Address` に関わらず既定で 127.0.0.1 だけで待ち受ける（`JinJi:Listen:MetricsAddress` で変える）。API・MCP のポートから `/metrics` には到達できない
- 役割 `all` / `api` は Data Protection の鍵（Cookie・CSRF・TOTP・Webhook の署名の鍵の保護）を DB に保存し、証明書（`JinJi:DataProtection:Certificate`）で暗号化する。証明書が無ければ起動しない
- 役割 `all` / `api` では DB の接続文字列 `ConnectionStrings:App` が必須で、無ければ起動時に止まる。それ以外の役割は使うときに初めて解決する
- ワーカーを API と別のプロセスにするときは、ワーカーにも同じ `JinJi:DataProtection` の証明書と `JinJi:Api:CursorKey` を与える
- TLS はリバースプロキシで終端する前提で、jin-ji は HTTP で待ち受ける

## 設定（appsettings.json / 環境変数）

| キー | 既定値 | 内容 |
| --- | --- | --- |
| `JinJi:Directory:RetiredPublicDays` | `90` | 退職者を人事管理者以外に公開する日数（退職年月日の翌日から数える） |
| `JinJi:Directory:MassActionThreshold` | `10` | 一斉異動とみなす発令の件数（同じ発令日の異動・役職の任免・兼務・出向）。メタ情報の時点の目盛りに使い、件数は返さない |
| `JinJi:Web:RootPath` | `web/dist`（コンテンツルートからの相対） | Web UI の成果物の場所。`index.html` が無ければ SPA を配信せず API だけで動く |
| `JinJi:Web:Hsts:Enabled` / `MaxAge` / `IncludeSubDomains` | `false` / `365.00:00:00` / `false` | HSTS。TLS を終端するリバースプロキシの後ろに置くときだけ有効にする |
| `JinJi:OrgSnapshotCache:VersionCheckInterval` | `00:00:05` | 他のプロセスの書き込みを確かめる間隔（反映の最大の遅れ） |
| `JinJi:OrgSnapshotCache:MaxAge` | `00:10:00` | 世代が変わらなくても読み直すまでの最長時間 |
| `JinJi:Retention:Enabled` | `true` | 保持期限の消去の定期実行（ワーカー）。`false` でも `purge-expired` は使える |
| `JinJi:Retention:Interval` / `InitialDelay` | `01:00:00` / `00:05:00` | 定期実行の間隔（1 分〜1 日）と、起動から最初の実行までの待ち |
| `JinJi:Retention:UiSessions` / `UnboundLogins` / `ResetLinks` | `30.00:00:00` / `90.00:00:00` / `30.00:00:00` | セッション・未紐付けのログイン・リンクの保持期間（1 日〜3660 日。終了・最後の試行・期限から） |
| `JinJi:Retention:OAuthTokens` / `TokenFamilies` / `PendingClients` | `7.00:00:00` / `30.00:00:00` / `30.00:00:00` | 認可の要求・コード・トークン、系列と同意、承認されないままの CIMD のクライアントの保持期間 |
| `JinJi:Retention:BatchSize` | `5000` | 1 回の DELETE で消す行数の上限（100〜100,000） |
| `JinJi:Corrections:MaxReportsPerDay` | `20` | 修正依頼の起票・賛同の 1 人あたり 24 時間の上限（1〜1000） |
| `JinJi:Corrections:NewWithinDays` / `StaleAfterDays` | `7` / `14` | 画面内の通知の「新しい依頼」「長く放置された依頼」の日数 |
| `JinJi:Corrections:MaxExportRows` | `5000` | レポートの最大の行数（超えたら古い順に打ち切る。REST は `X-JinJi-Truncated: true`） |
| `JinJi:Corrections:Notice:Enabled` / `Interval` / `InitialDelay` | `true` / `1.00:00:00` / `00:10:00` | 通知の定期実行（ワーカー。自動の解消の判定・件数の集計・Webhook）。`false` でも `notify-corrections` は使える |
| `JinJi:Corrections:Notice:Webhook:Enabled` / `Format` / `Url` / `AllowedHosts` | `false` / `slack` / 未設定 / 空 | Webhook（`slack` / `teams`）。URL は https で、ホストは許可リストにあるものだけ（起動時に検査する） |
| `JinJi:Corrections:Notice:Webhook:LinkUrl` / `Timeout` | `JinJi:Auth:Login:PublicOrigin` + `/#/corrections` / `00:00:10` | 通知に載せる jin-ji のリンクと、送信の時間の上限 |
| `JinJi:Webhooks:Enabled` / `Interval` / `InitialDelay` | `true` / `00:00:10` / `00:00:30` | 社内システムへの Webhook の収集と送信（ワーカー）。登録は管理画面で行う |
| `JinJi:Webhooks:Timeout` / `MaxAttempts` / `RetryBaseDelay` / `RetryMaxDelay` | `00:00:10` / `8` / `00:00:30` / `06:00:00` | 送信の時間の上限、再送（指数バックオフ）の上限回数と待ち。上限で dead（管理画面で確かめて再送） |
| `JinJi:Webhooks:SubscriptionsPerRun` / `DeliveriesPerRun` / `Concurrency` / `MaxScanRows` / `CollectLease` / `DefaultRotationOverlap` | `20` / `50` / `4` / `100000` / `00:02:00` / `1.00:00:00` | 1 回の実行の量・同時に送る数・1 回の収集で読む行の上限・収集の貸し出し・鍵のローテーションで旧鍵でも署名する既定の期間 |
| `JinJi:Listen:MetricsPort` | `28100` | メトリクスのポート |
| `JinJi:Listen:MetricsAddress` | `127.0.0.1` | メトリクスの待ち受けアドレス |
| `JinJi:Auth:Issuers` / `Clients` / `ClockSkew` | 空 / 空 / `00:00:30` | 外部の発行者の JWT の発行者・クライアントの登録（[API 仕様書](api.md#34-外部の発行者の-jwt)）。発行者が空なら JWT はすべて 401 |
| `JinJi:Auth:Server:Issuer` / `McpResource` | 未設定 / 未設定 | jin-ji のトークン発行（[下](#トークン発行apimcp)）。Issuer が未設定なら無効 |
| `JinJi:Auth:Server:Cimd:DefaultMcpTools` | 空 | CIMD で登録したクライアントの MCP ツールの許可リスト（`*` はすべて） |
| `JinJi:Mcp:Enabled` | `true` | MCP の停止スイッチ（`false` で MCP のポートは `/healthz` 以外 503。appsettings.json の変更は再起動なしで効く） |
| `JinJi:Mcp:MaxRequestBodyBytes` | `65536` | MCP の要求の本文の上限（超えたら 413） |
| `JinJi:Api:CursorKey` | 未設定（起動ごとに乱数） | カーソルの署名鍵（Base64・32 バイト以上）。複数プロセス・再起動をまたぐなら設定する |
| `JinJi:RateLimit:Api:Window` | `00:01:00` | リクエスト数を数える窓（スライディングウィンドウ） |
| `JinJi:RateLimit:Api:PerClient` / `PerUser` | `1200` / `300` | クライアント・クライアント × 利用者あたりのリクエスト数 |
| `JinJi:RateLimit:Api:PerEndpoint:<種別>` | `person_read` 120、`person_list` 30、`org_read` 120、`org_stats` 30、`changes` 60、`admin` 30、`ingest` 60、`sync` 30、`correction` 30 | クライアント × 利用者 × エンドポイント種別あたりのリクエスト数 |
| `JinJi:RateLimit:Api:ThrottledPerClient` | `60` | 自動スロットル中のクライアントのリクエスト数 |
| `JinJi:RateLimit:Api:AnonymousPerAddress` | `120` | 未認証の要求の接続元アドレスあたりのリクエスト数 |
| `JinJi:RateLimit:Api:ReturnedPeopleWindow` | `01:00:00` | 返却人数を数える窓 |
| `JinJi:RateLimit:Api:ReturnedPeoplePerUser` / `PerClient` | `3000` / `20000` | 返却人数の上限（大量抽出対策） |
| `JinJi:RateLimit:Mcp:*` | API より厳しい既定値 | MCP の経路の制限（同じ形） |
| `JinJi:Abuse:Enabled` | `true` | 不正検知を有効にするか |
| `JinJi:Abuse:Window` | `00:05:00` | 不正検知の窓 |
| `JinJi:Abuse:DeniedThreshold` | `50` | 403/404 の数（列挙攻撃） |
| `JinJi:Abuse:PeopleReturnedThreshold` | `3000` | 返した人数（大量の個人参照） |
| `JinJi:Abuse:BroadListingThreshold` | `30` | 絞り込みの無い一覧の取得の数（全社横断の連続取得） |
| `JinJi:Abuse:OffHours:Start` / `End` / `IncludeWeekends` / `PeopleReturnedThreshold` | `20:00` / `07:00` / `true` / `500` | 業務時間外（日本時間）とそのときの返却人数のしきい値 |
| `JinJi:Abuse:ThrottleDuration` | `00:30:00` | 自動スロットルを続ける時間（この間の再発で一時停止） |
| `JinJi:Llm:Enabled` | `false` | LLM による候補提示を使えるようにするか（有効でも、取込ごとにオプトインしなければ使わない） |
| `JinJi:Llm:Provider` / `BaseUrl` / `Model` | `openai` / `https://api.openai.com/v1` / 未設定 | 接続先（OpenAI 互換。Azure は `https://<resource>.openai.azure.com/openai/v1/`）とモデル名（有効なら必須） |
| `JinJi:Llm:ApiKeyEnvironmentVariable` | `OPENAI_API_KEY` | API キーを読む環境変数の名前（キーの値は設定に書かない） |
| `JinJi:Llm:AllowedEndpoints` | `["https://api.openai.com/v1"]` | 送信してよい接続先（ここに無い接続先には何も送らない） |
| `JinJi:Llm:Timeout` / `MaxRetries` / `RetryDelay` | `00:00:30` / `2` / `00:00:01` | 1 回の呼び出しのタイムアウト・一時的な失敗のリトライ回数と間隔（指数で伸ばす） |
| `JinJi:Llm:MaxConcurrency` / `MaxCallsPerBatch` / `MaxCandidates` | `4` / `50` / `8` | 同時実行数・1 回の取込で LLM に回す上限・1 つの問いの人の候補の上限 |
| `JinJi:Llm:MaxOutputTokens` / `Temperature` | `800` / `0` | 応答の最大トークン数・温度（null なら送らない） |
| `JinJi:Llm:InputPricePerMillionTokens` / `OutputPricePerMillionTokens` | 未設定 | 料金の目安の単価（米ドル / 100 万トークン。ログと `llm_request_log` に出す） |
| `JinJi:Sync:DirectApply:Enabled` | `false` | Graph への直接反映を使えるようにするか（`sync-apply` だけが使う） |
| `JinJi:Sync:DirectApply:TenantId` / `ClientId` | 未設定 | 書き込み用のアプリ（読み取り用とは別のアプリ）。有効なら必須 |
| `JinJi:Sync:DirectApply:Credential` | `Certificate` | `Certificate`（PFX で署名したクライアント アサーション）か `WorkloadIdentity`（フェデレーションのトークンのファイル）。クライアントシークレットは扱わない |
| `JinJi:Sync:DirectApply:CertificatePath` / `CertificatePasswordEnvironmentVariable` / `FederatedTokenFile` | 未設定 | 証明書のパスとパスワードの環境変数の名前（値は設定に書かない）、フェデレーションのトークンのファイル（無ければ `AZURE_FEDERATED_TOKEN_FILE`） |
| `JinJi:Sync:DirectApply:AuthorityHost` / `GraphBaseUrl` / `AllowedHosts` | `https://login.microsoftonline.com` / `https://graph.microsoft.com/v1.0` / その 2 つのホスト | 接続先と送信してよいホスト |
| `JinJi:Sync:DirectApply:Timeout` / `MaxRetries` / `MaxRetryDelay` | `00:00:30` / `2` / `00:01:00` | タイムアウト・429 / 503 の再送の回数と Retry-After の上限 |

環境変数では `JinJi__Directory__RetiredPublicDays=60` のように書く。不正な値（負の日数など）では起動しない。人のログイン（`JinJi:Auth:Login`）とトークン発行（`JinJi:Auth:Server`）の設定は下の各節、Webhook の登録ごとの値は [概要 §9「Webhook の実装の規則」](architecture/overview.md#webhook-の実装の規則)。

## 人のログイン

ADR-0004 の段階 A（[概要 §6.4](architecture/overview.md#64-人のログインの実装の規則adr-0004-の段階-a)）。画面は SPA が持ち、サーバーは `/auth` の JSON API と外部 IdP への転送を持つ（API のポートだけ。MCP のポートは Cookie を読まない）。JSON API の一覧は [API 仕様書](api.md#6-認証管理の-json-api)。

- 設定（`JinJi:Auth:Login`）: `PublicOrigin`（利用者から見た https の公開 URL。redirect_uri・パスキー・リンクの URL に使う）、`PasskeyServerDomain`（パスキーの RP ID。未設定ならパスキーを使わない）、`Providers`（外部 IdP。`Name`・`Type`〔`entra` / `oidc`〕・`Authority`・`TenantId`〔Entra〕・`ClientId`・`ClientSecret`・`EmployeeNumberClaim`・`TrustEmployeeNumberClaim`・`EmployeeNumberLegalEntity`）、`Session`（無操作 8 時間・最長 12 時間）、`Lockout`（5 回で 15 分）、`RateLimit`（接続元 → 接続元 × アカウント）
- 外部 IdP の主体は person に紐付ける（既存の紐付け → Entra の oid と取込の外部 ID の対応 → 信頼する社員番号のクレーム）。メールでは紐付けない。紐付けできないログインは拒否して記録し、人事管理者が手動で紐付ける
- 管理 CLI:

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- grant-role --person <person.id> --role hr_admin       # 修正担当者は --role hr_corrector
scripts/dotnet.sh run --project src/JinJi.Host -- create-local-account --person <person.id> --user-name <名前>      # パスワードの設定リンクを出す
scripts/dotnet.sh run --project src/JinJi.Host -- create-break-glass-account --user-name <名前>                 # パスキーの登録リンクを出す
scripts/dotnet.sh run --project src/JinJi.Host -- issue-reset-link --user-name <名前> [--purpose password|passkey]
scripts/dotnet.sh run --project src/JinJi.Host -- unlock-account --user-name <名前>
scripts/dotnet.sh run --project src/JinJi.Host -- list-local-accounts [--query <ユーザー名の部分一致>]
scripts/dotnet.sh run --project src/JinJi.Host -- disable-account --user-name <名前>       # セッションを終え、その人の委任（トークン）も失効する
scripts/dotnet.sh run --project src/JinJi.Host -- enable-account --user-name <名前>
scripts/dotnet.sh run --project src/JinJi.Host -- list-unbound-logins
scripts/dotnet.sh run --project src/JinJi.Host -- bind-login --person <person.id> --unbound <未紐付けのログインの id>
```

- リンクは 1 回限り・既定 1 時間で、鍵は URL のフラグメントに入る（出力にだけ現れる。jin-ji はメールを送らないので、安全な経路で渡す）
- TOTP の符号は同じ時間窓（30 秒）のものを 2 度受け付けない。本人はアカウントの画面でパスキーを一覧・削除できる（最後の 1 つでログインの手段か管理系のロールの多要素が無くなる場合は 409）
- 状態を変える要求（POST）には `X-JinJi-CSRF` ヘッダーに `GET /auth/csrf` のトークンが要る

## トークン発行（API・MCP）

ADR-0004 の段階 B（[概要 §6.5](architecture/overview.md#65-トークン発行の実装の規則adr-0004-の段階-b)）。`JinJi:Auth:Server:Issuer` を設定したときだけ有効。エンドポイントとスコープは [API 仕様書](api.md#33-jin-ji-のトークン発行oauth)。

- 設定（`JinJi:Auth:Server`）: `Issuer`（API の公開 URL。人のログインの `PublicOrigin` と同じ）、`ApiResource`（既定 `{Issuer}/api/v1`）、`McpResource`（MCP サーバーの URI。例 `https://jinji.example.co.jp/mcp`）、寿命（アクセストークン 10 分・リフレッシュ 無操作 7 日 / 最長 30 日・認可コード 60 秒）、`TokenCacheDuration`（検証のキャッシュ、5 秒）、`Cimd`（`AllowedDomains` 既定 `claude.ai`・`chatgpt.com`・`vscode.dev`、大きさ 5 KiB・時間 5 秒、新しい CIMD のクライアントの既定のスコープ・項目・MCP ツール）、`RateLimit`（接続元 → 接続元 × client_id）
- クライアントの管理は、管理 CLI か、UI でログインした人事管理者の `/auth/admin/oauth/*`（管理画面の「API クライアント」）:

```sh
# MCP クライアントの事前登録（公開クライアント）
scripts/dotnet.sh run --project src/JinJi.Host -- register-client --client-id my-agent --name "社内エージェント" --auth none \
  --grant-types authorization_code,refresh_token --redirect-uris http://127.0.0.1/callback --resources mcp --scopes jinji.read --fields '*' --mcp-tools '*'
# 社内システム（client credentials・private_key_jwt）
scripts/dotnet.sh run --project src/JinJi.Host -- register-client --client-id attendance --name "勤怠" --auth private_key_jwt \
  --grant-types client_credentials --resources api --scopes jinji.read --fields employee_number,display_name,assignments,managers,employment_status
scripts/dotnet.sh run --project src/JinJi.Host -- add-client-key --client-id attendance --kid 2026-10 --key-file attendance.pub.pem
# CIMD: 承認待ちの一覧と承認・許可ドメインの追加・MCP ツールの許可
scripts/dotnet.sh run --project src/JinJi.Host -- list-clients --status pending_approval
scripts/dotnet.sh run --project src/JinJi.Host -- approve-client --client-id https://example.com/oauth/client.json
scripts/dotnet.sh run --project src/JinJi.Host -- allow-cimd-domain --domain example.com
scripts/dotnet.sh run --project src/JinJi.Host -- update-client --client-id https://vscode.dev/oauth/client-metadata.json --mcp-tools '*'
# 社内システムの先行提供（有効日の N 日前から将来の予定を見せる。承認は UI でログインした別の人事管理者が「API クライアント」の画面で行う）
scripts/dotnet.sh run --project src/JinJi.Host -- propose-disclosure-lead --client-id attendance --days 30 --proposed-by <人事管理者の person.id> --note "4/1 の組織改編の事前登録"
scripts/dotnet.sh run --project src/JinJi.Host -- list-disclosure-leads [--client-id attendance]
# 停止・失効
scripts/dotnet.sh run --project src/JinJi.Host -- suspend-client --client-id my-agent
scripts/dotnet.sh run --project src/JinJi.Host -- revoke-tokens --client-id my-agent [--person <person.id>]
```

- 管理 CLI のコマンドはほかに `activate-client`・`reject-client`・`revoke-client-key`・`revoke-client-secrets`・`disallow-cimd-domain`・`list-cimd-domains`
- client_secret（`issue-client-secret`）は機密クライアントの移行用の例外で、期限必須（既定 90 日）。値は出力にだけ現れる
- MCP の停止スイッチは `JinJi:Mcp:Enabled=false`。クライアント単位で止めるなら `suspend-client`（承認なしで次の要求から効く）
- 不正検知で一時停止になったクライアントの解除は人事管理者が `POST /api/v1/admin/clients/{clientId}/release` で行う（[API 仕様書](api.md#管理)）

## Web UI の配信

- 役割 `api` / `all` のサーバーが、API のポートで `JinJi:Web:RootPath`（既定 `web/dist`。コンテンツルートからの相対、絶対パスも可）の成果物を配信する。`index.html` が無ければ配信せず、API だけで起動する
  - 例: `scripts/dotnet.sh run --project src/JinJi.Host -- --role api --JinJi:Web:RootPath=/src/web/dist`
- `/api`・`/auth`・`/oauth`・`/.well-known`・`/openapi`・`/healthz` の下は配信もフォールバックもしない。それ以外の GET / HEAD で、ファイルが無く拡張子の無いパスは `index.html` を返す（クライアント側のルーティング）。拡張子のあるパスで無いファイルは 404
- 静的ファイルは認証・レート制限の前に返す（中身は画面の殻だけで、データは API から認証付きで取る）。MCP・メトリクスのポートからは見えない
- キャッシュ: `/assets/` の下（Vite がハッシュを名前に入れる）は `public, max-age=31536000, immutable`、`index.html` などは `no-cache`（ETag で再検証）、API の応答は `no-store`
- セキュリティヘッダー（API・MCP のポートのすべての応答。エラーを含む）
  - `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; worker-src 'self' blob:; manifest-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`
    - `style-src` に `'unsafe-inline'` は付けない。React・React Flow・motion が要素に付ける style は CSSOM 経由で CSP の対象外。three.js / WebGL に CSP の指定は要らず、テクスチャ・ワーカーの `data:` / `blob:` だけ許す
  - `X-Content-Type-Options: nosniff`、`Referrer-Policy: no-referrer`、`X-Frame-Options: DENY`、`Permissions-Policy`（カメラ・マイク・位置情報などをすべて止める）、`Cross-Origin-Opener-Policy` / `Cross-Origin-Resource-Policy: same-origin`
  - HSTS は既定で付けない（jin-ji は HTTP で待ち受け、TLS はリバースプロキシで終端する前提）。プロキシの後ろでは `JinJi:Web:Hsts:Enabled=true`

## メトリクス

`http://127.0.0.1:28100/metrics`（Prometheus のテキスト形式）。計装は標準の System.Diagnostics.Metrics と自前の Prometheus 出力で、OpenTelemetry は入れていない（理由は [概要 §7.2](architecture/overview.md#72-rest-api-の実装の規則)）。

| メトリクス | 内容 |
| --- | --- |
| `jinji_requests_total` | クライアント別のリクエスト数（経路・エンドポイント種別・状態コード） |
| `jinji_requests_rejected_total` | 拒否数（理由別） |
| `jinji_people_returned_total` | 返却人数 |
| `jinji_request_duration_seconds` | レイテンシ（ヒストグラム） |
| `jinji_abuse_detections_total` | 不正検知の発火数 |
| `jinji_retention_deleted_total`・`jinji_retention_runs_total` | 保持期限の消去（表別）と実行回数（同じプロセスでワーカーが動くとき） |
| `jinji_correction_notices_total`・`jinji_correction_auto_resolved_total` | 修正依頼の通知（契機・結果別）と自動の解消 |
| `jinji_webhook_deliveries_total`・`jinji_webhook_events_total`・`jinji_webhook_collections_total` | 社内システムへの Webhook の送信・イベント・収集 |

ラベルのクライアントは登録済みのクライアント ID だけ（未認証は `anonymous`、未登録は `unregistered`）。person ID・パス・クエリはラベルにしない。

## 保持期限の消去

設計は [概要 §4.5](architecture/overview.md#45-保存期間と消去)。ワーカー（役割 `worker` / `all`）が 1 時間ごとに、終わった行を保持期間の後に消す。管理 CLI でも 1 回実行できる。

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- purge-expired      # 表ごとの削除件数を出力し、retention_run と監査ログ（retention.purge）に残す
```

- 消すもの（既定の保持期間）: UI のセッション（終了から 30 日）、未紐付けのログイン（最後の試行から 90 日）、管理者が発行したリンク（期限から 30 日）、認可の要求・コード・アクセストークン・リフレッシュトークン（期限から 7 日）、トークンの系列と同意（終了か期限から 30 日）、client assertion の jti（期限）、承認されないままの CIMD のクライアント（30 日）、取込の staging（項目マッピングの保持期限）
- 消さないもの: 監査ログ、人事データ（退職者の匿名化は未実装。設計は §4.5）、紐付け・ロールの付与・ローカルアカウント・クライアントの登録と鍵、消去の記録（`retention_run`）、Webhook の送信の記録
- DB の RLS でも「jin-ji 自身が、終わった行だけ」に絞っている。保持期間を短くしても、生きている系列のコードなど再利用の検知に要る行は消えない

## マイグレーションの適用

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- migrate
```

- 接続文字列は `ConnectionStrings__Owner`（テーブルの所有者 `jinji_owner`）。アプリはテーブルの所有者でない `jinji_app` で接続する（所有者は RLS をすり抜けるため）
- 適用済みのファイルはチェックサムで照合し、未適用のファイルだけを適用する。SQL はアセンブリに埋め込まれるので、実行環境にファイルを置く必要はない。書き方の規則は [開発ガイド](development.md#マイグレーション)
