# jin-ji

日本企業向けの人事情報基盤（プロジェクト名は仮）。既存の人事パッケージと社内システム群の中間に立ち、**全社公開レベルの人事情報だけ**を取り込み・整え、社内システムと AI エージェントへ配る。人事情報を MCP / AI エージェントから安全に扱える「AI Ready」にすることが目的で、ERP や人事情報の正（System of Record）ではない。

## 特徴

- **機微情報を持たない**: マイナンバー・生年月日・性別・住所・給与・評価などは取り込みの入口で捨て、DB に列も作らない。休職は理由を持たない「一時停止」として扱う（[ADR-0002](docs/adr/0002-data-scope.md)）
- **AI も人も同じ入口・同じ権限判定**: Web UI・REST API・MCP サーバーが同じドメイン層と認可エンジンを通る。公開範囲は全社公開を既定に、項目・組織・個人の単位で狭める。PostgreSQL の RLS を防波堤として重ねる（[ADR-0008](docs/adr/0008-authorization-model.md)）
- **公開に至る変更は人が承認する**: AI エージェントは作業領域（名寄せの候補・推定）に書けるが、公開される情報を変えるのは人の承認だけ（[ADR-0009](docs/adr/0009-agent-writes-hitl.md)）
- **二つの時間軸と内示の秘匿**: 有効時間と記録時間を持ち、「ある日時点の組織・所属・上長」を引ける。発令日前の異動は人事管理者以外に見せない（[ADR-0007](docs/adr/0007-bitemporal-model.md)）
- **整っていないデータの取り込み**: CSV / xlsx / Entra ID のユーザー一覧を、項目マッピング・日本語の正規化（異体字・かな・和暦）・決定的な名寄せで変更提案にする。残りは任意で LLM が候補を示す
- **社内システム連携**: 時点指定の REST API、変更フィード、ID だけを載せた Webhook、承認ルートの API（[ADR-0011](docs/adr/0011-webhook.md)）
- **Entra ID への反映はスクリプトが主**: 同期計画と PowerShell スクリプトを出し、管理者が実行する。直接反映は既定で無効（[ADR-0010](docs/adr/0010-entra-sync.md)）
- **認証は既存の IdP に委ねる**: Entra ID・汎用 OIDC・ローカルアカウント（パスキー・TOTP）。API・MCP 向けのトークンは自前の最小の認可サーバーが参照トークンで発行する（[ADR-0004](docs/adr/0004-authentication.md)）
- **小さく運用できる**: 1 つの実行ファイル + PostgreSQL。依存は最小限で、サプライチェーンの方針を決めている（[ADR-0001](docs/adr/0001-language-and-runtime.md)）

## 構成

| 部分 | 技術 | 場所 |
| --- | --- | --- |
| サーバー（REST API・MCP・ワーカー・管理 CLI） | C# / .NET 10（ASP.NET Core）。1 つの実行ファイルを `--role all\|api\|mcp\|worker` で切り替える | `src/` |
| DB | PostgreSQL 17 以上。SQL マイグレーション | `db/`、`deploy/compose/` |
| Web UI | React + Vite + TypeScript の SPA（組織図・3D 俯瞰・検索・修正依頼・認証の画面） | `web/` |
| サンプルデータ | 架空の「整っていない」会社の人事データと正解データ | `samples/messy-corp/` |

ビルド・テスト・依存のインストールはすべて Docker のコンテナ内で行い、ホストに .NET SDK や Node.js は要らない。

## まず試す: Web UI のデモ

架空のデータ（`samples/messy-corp` から生成）で動くデモ版の UI を、サーバーや DB なしで動かせる。必要なのは Docker だけ。

```sh
scripts/pnpm.sh install --frozen-lockfile
JINJI_PNPM_PORT=28000 JINJI_PNPM_NAME=jinji-pnpm-dev scripts/pnpm.sh dev
# → http://127.0.0.1:28000/ （組織図・3D 俯瞰・検索・修正依頼。ログインの画面のデモは demo / 任意 / 123456）
docker stop jinji-pnpm-dev   # 終わったら止める
```

デモのコードとデータは `--mode demo` のビルドと開発サーバーにだけ含まれ、本番ビルドには入らない（下の「Web UI」）。

## ドキュメント

- [アーキテクチャ概要](docs/architecture/overview.md): 設計の全体と、各機能の実装の規則。実装の状況は [§12](docs/architecture/overview.md#12-実装の状況)
- 決定記録（ADR）

  | ADR | 内容 |
  | --- | --- |
  | [0001](docs/adr/0001-language-and-runtime.md) | 実装言語とランタイム、サプライチェーン方針 |
  | [0002](docs/adr/0002-data-scope.md) | 扱うデータの範囲と、システムの位置づけ |
  | [0003](docs/adr/0003-ingest-dependencies.md) | 取り込みの依存パッケージ（ClosedXML） |
  | [0004](docs/adr/0004-authentication.md) | 認証とトークン発行の実現方式 |
  | [0005](docs/adr/0005-llm-dependency.md) | LLM の呼び出しの依存パッケージ（OpenAI 公式 .NET SDK） |
  | [0006](docs/adr/0006-mcp-sdk.md) | MCP サーバーの依存パッケージ（公式 C# SDK） |
  | [0007](docs/adr/0007-bitemporal-model.md) | 二つの時間軸と、発令を起点にした変更 |
  | [0008](docs/adr/0008-authorization-model.md) | 認可モデル |
  | [0009](docs/adr/0009-agent-writes-hitl.md) | AI エージェントによる書き込み |
  | [0010](docs/adr/0010-entra-sync.md) | Entra ID への反映 |
  | [0011](docs/adr/0011-webhook.md) | 社内システムへの変更の通知 |
  | [0012](docs/adr/0012-workplace-and-seat-data.md) | 建物・階・座席と、人の席・所在の扱い（提案） |

- [建物・フロアモードの設計（検討中）](docs/design/building-floor-mode.md)
- [Webhook の受け手の実装](docs/integration/webhook-receiver.md)（社内システムの開発者向け）
- 評価レポート: [取込の精度](docs/reports/ingest-accuracy.md)、[同期計画の評価](docs/reports/sync-plan.md)

以下は開発環境でサーバーを動かす手順と、機能ごとの使い方。

## 開発環境の前提

- Docker（Compose v2）。arm64 / amd64 のどちらでも動く
- ホストに .NET SDK は不要。ビルド・テストは `scripts/dotnet.sh` が `mcr.microsoft.com/dotnet/sdk:10.0` のコンテナ内で実行する（ADR-0001 のサプライチェーン方針）
  - マウントするのはリポジトリだけ。ホストのホームディレクトリや資格情報はコンテナに渡さない
  - HOME と NuGet のキャッシュは名前付きボリューム `jinji_dotnet_home` / `jinji_nuget` に置く
  - Compose のネットワーク `jinji_default` があれば参加し、PostgreSQL に `postgres:5432` で接続する
- 使うポート（すべて 127.0.0.1 のみ）

  | 用途 | ポート |
  | --- | --- |
  | PostgreSQL | 25432 |
  | API | 28080 |
  | MCP | 28090 |
  | メトリクス（Prometheus） | 28100 |

## サーバーを動かす

### 1. PostgreSQL を起動する

```sh
cp deploy/compose/.env.example deploy/compose/.env   # パスワードを書き換える（.env はコミットしない）
docker compose -f deploy/compose/compose.yaml up -d --wait
```

初回起動時に `deploy/compose/initdb/10-roles.sh` が次のロールとデータベース `jinji` を作る。

| ロール | 用途 |
| --- | --- |
| `jinji_owner` | マイグレーションの実行とテーブルの所有 |
| `jinji_app` | アプリの接続用。テーブル所有者にしない（所有者は RLS をすり抜けるため） |

パスワードを変えたい場合、初期化は空のボリュームでしか走らないので、`docker compose -f deploy/compose/compose.yaml down -v` でボリューム `jinji_pgdata` を消してから起動し直す（データは消える）。

### 2. マイグレーションを適用する

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- migrate
```

- 接続文字列は `ConnectionStrings__Owner`（jinji_owner）。未設定の場合は開発用に `deploy/compose/.env` の `JINJI_PG_OWNER_PASSWORD` などから組み立てる
- 二度目以降は未適用のファイルだけを適用する

### 3. ビルドとテスト

```sh
scripts/dotnet.sh build
scripts/dotnet.sh test
```

- ビルドは警告をエラーとして扱う
- テストは Compose の PostgreSQL に接続し、テストごとに一時データベース（`jinji_test_*`）を作って捨てる。接続情報は環境変数で渡す（`scripts/dotnet.sh` が `deploy/compose/.env` を渡す）

  | 環境変数 | 既定値 | 内容 |
  | --- | --- | --- |
  | `JINJI_PG_HOST` | `127.0.0.1`（Compose ネットワーク内では `postgres`） | 接続先 |
  | `JINJI_PG_PORT` | `25432`（Compose ネットワーク内では `5432`） | ポート |
  | `JINJI_PG_ADMIN_USER` | `postgres` | 一時データベースを作るスーパーユーザー |
  | `JINJI_PG_ADMIN_PASSWORD` | （必須） | 同パスワード |
  | `JINJI_PG_OWNER_PASSWORD` | （必須） | `jinji_owner` のパスワード |
  | `JINJI_PG_APP_PASSWORD` | （必須） | `jinji_app` のパスワード |

### 4. サーバーを起動する（任意）

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- --role all --JinJi:DataProtection:AllowUnprotectedKeys=true
```

- 役割 `all` / `api` は Data Protection の鍵（Cookie・CSRF・TOTP の鍵の保護）を DB に保存し、証明書（`JinJi:DataProtection:Certificate`）で暗号化する。証明書が無ければ起動しない。開発で証明書なしにするときだけ、上のように `AllowUnprotectedKeys=true` を明示する（起動時に警告する）

- 役割は `all`（既定）/ `api` / `mcp` / `worker`。環境変数 `JINJI_ROLE` でも指定できる
- DB はアプリ用ロールの接続文字列 `ConnectionStrings:App`（未設定なら開発用に `JINJI_PG_APP_PASSWORD` などから組み立てる）を、使うときに初めて解決する
- API は 28080、MCP は 28090、メトリクスは 28100 で待ち受ける。API のエンドポイントは下の「REST API」、MCP は下の「MCP サーバー」
- 役割 `all` / `api` では DB の接続文字列（`ConnectionStrings:App`、または開発用の `JINJI_PG_APP_PASSWORD`）が必須で、無ければ起動時に止まる
- 接続を受けたポートでエンドポイントを振り分けるので、MCP のエンドポイントに API のポートから到達することはない
- 待ち受けアドレスの既定は 127.0.0.1。コンテナから公開する場合は `JinJi__Listen__Address=0.0.0.0` を指定し、ポートの公開側で 127.0.0.1 に絞る
- メトリクスのポートは `JinJi:Listen:Address` に関わらず既定で 127.0.0.1 だけで待ち受ける（`JinJi:Listen:MetricsAddress` で変える）。API・MCP のポートから `/metrics` には到達できない
- `scripts/dotnet.sh` はリポジトリ直下の `.env`（外部 API キー）をコンテナに渡さない。必要なとき（実 LLM の評価）だけ `JINJI_DOTNET_PASS_ENV=<環境変数名>` で名前を指定して渡す（既定は何も渡さない。値はコマンドラインに現れない）
- `scripts/dotnet.sh` はポートを公開しないため、上のコマンドで起動したサーバーにはホストから到達できない。ポートの振り分けは `HostSurfaceTests` で確かめている。アプリのコンテナイメージと Compose のサービス定義はまだ無い

### 5. 取り込む（開発用の CLI、任意）

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- import /src/samples/messy-corp/mappings/employees_hr_package.json /src/samples/messy-corp/employees_hr_package.csv --executed-by <実行者の person.id> [--effective-date 2026-10-01] [--llm true]
```

- CSV（UTF-8 / UTF-8 BOM / Shift_JIS を自動判定）か xlsx を、項目マッピング（下記）で読み、作業領域（staging・正規化結果・名寄せ候補・変更提案・修正依頼）に保存する。公開側（人・組織・所属）は変えない
- 閲覧者は jin-ji 自身（system、経路 cli）。実行者は指定の人で、その取込の提案の起案者になり、承認できない
- 出力は取込単位の ID・行数・提案と要確認と修正依頼の件数・捨てた列の名前と件数（値は出さない）
- 提案は、実行者とは別の人事管理者が `ProposalReviewService` で承認したときだけ反映する（提案の承認の REST API・画面はまだ無く、今はサービス・テスト・評価から使う）
- `scripts/dotnet.sh` はリポジトリだけを `/src` にマウントし、`dotnet run` はプロジェクトのディレクトリで動くので、ファイルはリポジトリの中に置き `/src/...` の絶対パスで指定する
- `--llm true` で、要確認の提案に LLM による候補提示を行う（下の「LLM による候補提示」。設定 `JinJi:Llm` を有効にし、API キーの環境変数を渡したときだけ）。出力は送信先・回数・トークン数・料金の目安・送った項目名と件数（値は出さない）
- 拠点（[設計 §4.7](docs/architecture/overview.md#47-拠点とリモートワーク)）: 拠点マスタ（`target: sites`。例 `samples/messy-corp/mappings/sites.json`）→ 組織図（組織の拠点 `site_code` / `site_name`）→ 人（人事パッケージの「勤務地」を `site_name`、リモートワークの有無を `remote_work`）の順に取り込んで承認する。人の拠点は主務の組織の拠点と違う人だけが持ち、拠点マスタに無い勤務地は修正依頼（`unknown_site`）になる
- 拠点を導入する前の版で取り込んだ自由記述の勤務地（廃止）は、拠点マスタと組織の拠点を承認した後に拠点へ照合する。主務の組織と違う拠点は人ごとの拠点の変更提案に、拠点に当たらない値は修正依頼になる（承認は別の人事管理者）

  ```sh
  scripts/dotnet.sh run --project src/JinJi.Host -- reconcile-work-locations --executed-by <実行者の person.id>
  ```

### 6. 取込の精度を測る（任意）

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- evaluate-ingest /src/samples/messy-corp --output /src/docs/reports/ingest-accuracy.md
```

- 空の一時データベース（`jinji_eval_*`。終わったら消す。`--keep-database true` で残す）にサンプルの 5 ファイル（拠点マスタ・組織図・人事パッケージ・辞令・Entra ID）を順に取り込み、起案の提案を評価用の承認者が一括承認し、`ground_truth.json` と比べて [取込の精度](docs/reports/ingest-accuracy.md) を書き出す
- 接続情報はテストと同じ環境変数（`JINJI_PG_ADMIN_PASSWORD` など）。重いので通常のテストからは外し、テストでは項目マッピングでサンプルを読めることだけを確かめる（`SampleSmokeTests`）
- 最後に評価用の解決者が辞令の要確認を正解データに従って解決し（人の確認の代わり）、作業領域に戸籍名が残らないことを確かめる
- `--llm true` で、要確認の提案に実 LLM の候補提示を行い、その精度（第 1 候補が正解の割合・上位 3・誤った高確信度）と料金の目安も書き出す。**実 LLM を呼ぶのはこのときだけ**（通常のテストはフェイク）。`scripts/dotnet.sh` はリポジトリ直下の `.env` をコンテナに渡さないので、API キーは名前を指定して一時的に渡す

  ```sh
  set -a; . ./.env; set +a   # OPENAI_API_KEY（値は表示しない）
  JINJI_DOTNET_PASS_ENV=OPENAI_API_KEY scripts/dotnet.sh run --project src/JinJi.Host -- evaluate-ingest /src/samples/messy-corp \
    --output /src/docs/reports/ingest-accuracy.md --llm true --llm-model gpt-4.1-mini --llm-input-price 0.40 --llm-output-price 1.60
  ```

  - 接続先・許可リスト・API キーの環境変数名は `JinJi:Llm`（`src/JinJi.Host/appsettings.json` の既定は OpenAI）。`--llm-model` はモデル名、`--llm-input-price` / `--llm-output-price` は料金の目安の単価（米ドル / 100 万トークン）

### 7. Entra ID への同期（任意）

```sh
# 計画を作る（起案者は --executed-by の人。承認できない）
scripts/dotnet.sh run --project src/JinJi.Host -- sync-plan /src/samples/messy-corp/entra_users.json --executed-by <person.id> --settings /src/samples/messy-corp/mappings/entra_sync.json
# 別の人事管理者が REST API で承認する: POST /api/v1/sync/plans/{id}/approve
# 承認済みの計画のスクリプト一式を書き出す
scripts/dotnet.sh run --project src/JinJi.Host -- sync-script <plan-id> /src/out/sync
# 管理者がスクリプトを実行した後、実行ログ（JSON）を取り込んで照合する
scripts/dotnet.sh run --project src/JinJi.Host -- sync-result <plan-id> /src/out/sync/jinji-entra-sync-<plan-id>-apply-<日時>.log.json
```

- 詳細は下の「Entra ID への同期」。サンプルでの評価は `scripts/dotnet.sh run --project src/JinJi.Host -- evaluate-sync /src/samples/messy-corp --output /src/docs/reports/sync-plan.md`（[同期計画の評価](docs/reports/sync-plan.md)。取込の評価と同じ一時 DB を作り、終わったら消す。実テナントには接続しない）

## REST API

設計は [アーキテクチャ概要 §7](docs/architecture/overview.md#7-apimcp-のセキュリティ)・[§9](docs/architecture/overview.md#9-社内システム連携下流)（実装上の規則は §7.2 と §9 の末尾）。

| メソッドとパス | 内容 |
| --- | --- |
| `GET /api/v1/people/{id}?as_of=` | 人（見える項目だけ。`visible_fields`） |
| `GET /api/v1/people?query=&org=&include_descendants=&legal_entity=&level=&assignment_kind=&position=&employment_type=&job_type=&site=&site_type=&prefecture=&remote_work=&hired_from=&hired_to=&employment_status=&account_status=&sort=&facets=&as_of=&cursor=&limit=` | 検索（正規化表記と読みの部分一致・所属組織・拠点（拠点・種別・都道府県）・リモートワークの有無などの絞り込み。複数の値は同じ名前を繰り返す。`sort` は id / kana / org / hire_date / hire_date_desc / site / remote_work。`facets=true` で一致した人数と条件ごとの件数〔見える人の見える値だけ。最初のページだけ〕。limit は 1〜100。規則は設計 §7.2） |
| `GET /api/v1/people?ids=<id>,<id>,...&as_of=` | ID で一括取得（重複を除いて最大 100 件、指定の順。存在しない・見えない ID は単に含めない。返した人数は返却人数の制限に数える。他の条件とは併用できない） |
| `GET /api/v1/people/{id}/manager?as_of=` | 直属の上長 |
| `GET /api/v1/people/{id}/approval-route?org_unit_id=&vacancy=promote\|vacant&level=&position=&as_of=` | 承認ルート（申請を誰が承認するか。所属組織から上位の組織長を下の段から順に。空席の繰り上げ・本人が組織長の組織の飛ばし・兼務先起点・段〔`level`〕と役職〔`position`〕での引き当て。下の「社内システム向けの使い方」） |
| `GET /api/v1/orgs/{id}?as_of=` | 組織 |
| `GET /api/v1/orgs/{id}/members?include_descendants=&as_of=&cursor=&limit=` | 組織の所属者 |
| `GET /api/v1/orgs/tree?as_of=` | 組織ツリー（`parent_id` で木を組める平らな一覧） |
| `GET /api/v1/orgs?query=&as_of=&limit=` | 組織の検索（組織名・略称は組織名の正規化表記、組織コードは検索用の正規化表記の部分一致。完全一致・前方一致・部分一致の順。limit は 1〜100、打ち切ったら `has_more`） |
| `GET /api/v1/orgs/stats?as_of=` | 組織ごとの人数（`primary` 主務・`primary_total` 配下を含む主務・`concurrent` 兼務・`secondment` 出向）と、兼務・出向の組織間の集計（`links`: 主務の組織 → 兼務・出向の組織の組ごとの人数）。閲覧者に見える人だけを数える |
| `GET /api/v1/sites?query=&legal_entity=&site_type=&prefecture=&as_of=` | 拠点の一覧（コード・名称・種別・都道府県・所在地〔事業所の住所〕・拠点を持つ組織と、拠点の人数・リモートワークの対象の人数。人数は閲覧者に拠点が見える人だけ。人は返さない。設計 §4.7） |
| `GET /api/v1/sites/{id}?as_of=` | 拠点の詳細（一覧と同じ形） |
| `GET /api/v1/sites/{id}/buildings?as_of=` | 拠点の建物と階の一覧（階ごとの `seat_count`・`occupied_count`・`vacant_count`・`unavailable_count`。空席は割り当ての無い席。閲覧者に席が見えない人の席・保守中の席は使えない席。人は返さない。[建物・フロアモードの仕様](docs/design/building-floor-mode.md) §6） |
| `GET /api/v1/buildings/{id}?as_of=` | 建物と階の一覧（同上） |
| `GET /api/v1/floors/{id}/layout?as_of=` | その日の配置図（外形・固定の部分・ゾーン・席の置き場所と状態 `vacant` / `occupied` / `unavailable`。整数のミリメートル。人は返さない。ETag） |
| `GET /api/v1/floors/{id}/occupants?zone=&as_of=&cursor=&limit=` | 階の在籍者と席（決まった席・持ち場だけ。席の人は席が見える人だけ、ゾーンの持ち場の人は階が見える人だけ。返却人数の制限の対象） |
| `GET /api/v1/seats?floor=&building=&site=&zone=&kind=&feature=&vacant=true&as_of=&limit=` | 席の検索（空席など。`kind` は `fixed` / `station`。人は返さない） |
| `GET /api/v1/floors/{id}/presence` | 今日の階の所在（所在の提供を登録したときだけ。下の「当日の所在の提供」。提供が無ければ 404。`no-store`） |
| `GET /api/v1/meta` | メタ情報（業務日付・法人ごとの段の定義・時点の目盛り〔組織改編・一斉異動の日。人事管理者以外には `max_as_of` 以前だけ〕・未来の時点を指定できるか・指定できる最後の時点 `max_as_of`〔先行提供の社内システムは業務日付 + 日数〕） |
| `GET /api/v1/changes?since=&limit=` | 変更フィード（種別〔`person` / `org_unit` / `site`〕と ID だけ。拠点の変更は、その拠点を使う組織・人の変更としても届く。席の割り当て・配置図の改訂は人の変更として届く。当日の所在は流さない。limit は 1〜1000） |
| `GET /api/v1/admin/clients` | 自動スロットル・一時停止中のクライアント（人事管理者の人だけ） |
| `POST /api/v1/admin/clients/{clientId}/release` | 一時停止・自動スロットルの解除（人事管理者の人だけ） |
| `GET /api/v1/ingest/proposals/{id}` | 変更提案の根拠（名寄せ・組織の候補、LLM による推定）。値は返さない（人事管理者の人だけ） |
| `GET /api/v1/sync/plans?limit=` | Entra ID への同期計画の一覧（人事管理者の人だけ） |
| `GET /api/v1/sync/plans/{id}` | 同期計画の詳細（項目・Entra の現在値とあるべき値・根拠・最新の実行結果。人事管理者の人だけ） |
| `POST /api/v1/sync/plans/{id}/approve` / `reject` | 同期計画の承認・却下（人事管理者の人だけ、起案者は不可。jin-ji のデータが変わった計画は 409 で古い計画になる） |
| `GET /api/v1/sync/plans/{id}/script` | 承認済みの計画のスクリプト一式（ZIP: `.ps1`・`.data.json`・`.report.md`・`.report.csv`。人事管理者の人だけ） |
| `GET /api/v1/corrections?status=open\|closed\|all&person_id=&cursor=&limit=` | 修正依頼の一覧（閲覧者に見える範囲。対象の人の要約つき。下の「修正依頼」） |
| `GET /api/v1/corrections/{id}` | 修正依頼 |
| `POST /api/v1/corrections` | 修正依頼の起票（`{"person_id", "field", "reason", "suggested_value"}`。人・AI エージェント。同じ人・項目の未対応の依頼があれば賛同。jin-ji の公開データは変わらない） |
| `POST /api/v1/corrections/{id}/close` | 対応済み・対応不要にする（`{"status": "resolved" \| "dismissed", "note"}`。修正担当者・人事管理者の人だけ） |
| `GET /api/v1/corrections/summary` | 画面内の通知の件数（未対応・新しい依頼・長く放置された依頼。修正担当者・人事管理者だけ） |
| `GET /api/v1/corrections/report.csv?status=` | 修正依頼のレポート（CSV。UTF-8 BOM。修正担当者・人事管理者の人だけ） |
| `POST /api/v1/ingest/proposals/{id}/resolve` | 要確認の提案の解決（`{"resolution": "select_candidate" \| "register_new" \| "refer_to_correction" \| "reject", "person_id": ..., "org_unit_id": ...}`。人事管理者の人だけ、取込の実行者は不可） |
| `GET /openapi/v1.json` | OpenAPI 3.1 の文書（認証不要） |
| `GET /`（`/api`・`/openapi`・`/healthz` 以外） | Web UI（`JinJi:Web:RootPath` の SPA の成果物がある場合だけ。下の「Web UI」） |
| `GET /healthz` | ヘルスチェック（認証不要） |

- `/api/v1` は Bearer トークン（JWT）が必要。jin-ji は検証だけを行い、発行はしない（発行の方式は ADR-0004）。発行者とクライアントを設定に登録する

  ```json
  {
    "JinJi": {
      "Auth": {
        "Issuers": [
          {
            "Issuer": "https://idp.example.jp/",
            "Audiences": ["jinji-api"],
            "SigningKeys": [{ "KeyId": "2026-10", "PublicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----" }],
            "ActorTypes": ["user", "service", "agent"],
            "TrustRoles": true
          }
        ],
        "Clients": [
          { "ClientId": "portal", "ActorTypes": ["user"], "Fields": ["*"] },
          { "ClientId": "attendance", "ActorTypes": ["service"], "Fields": ["employee_number", "display_name", "assignments", "managers", "employment_status"] }
        ]
      },
      "Api": { "CursorKey": "<32 バイト以上の乱数の Base64>" }
    }
  }
  ```

  - 署名は RS / PS / ES の公開鍵（PEM）だけ。有効期限は必須
  - クライアントは `client_id`（無ければ `azp`・`appid`）で照合し、登録が無ければ 403。渡す項目の上限（`Fields`）は登録で決める
  - 人は `sub` が person ID、AI エージェントは `act` クレーム付きで `sub` が委任元の person ID、社内システムは `sub` がクライアント自身（Entra ID のアプリのトークンは `idtyp=app`）
  - ロール（`roles`）は `TrustRoles` が true の発行者のものだけを使う
- 応答の項目は、値が無いとき・見えないときも省略せず `null` を返す（見えないのか値が無いのかは `visible_fields` で区別する）。OpenAPI の文書では応答の型の全項目を required とし、null になり得る項目は型に null を含める。コード値（`type`・`secondment`・`*_status`・`action_type` など）には `enum` を宣言する。文書と実際の応答の一致は、主要なエンドポイントの応答を文書のスキーマで検証するテスト（`DirectoryApiTests`）で確かめる
- エラーは RFC 9457 Problem Details。存在しない ID と参照できない ID は同じ 404
- 429 には `Retry-After` を付ける（リクエスト数の上限と、返却人数の上限）
- 不正検知で一時停止になったクライアントは 403（`urn:jinji:problem:client-suspended`）。人事管理者が解除する
- メトリクスは `http://127.0.0.1:28100/metrics`（Prometheus のテキスト形式）: `jinji_requests_total`・`jinji_requests_rejected_total`（理由別）・`jinji_people_returned_total`・`jinji_request_duration_seconds`・`jinji_abuse_detections_total`、同じプロセスでワーカーが動くときは保持期限の消去の `jinji_retention_deleted_total`（表別）・`jinji_retention_runs_total` と修正依頼の通知の `jinji_correction_notices_total`（契機・結果別）・`jinji_correction_auto_resolved_total`

### 社内システム向けの使い方（勤怠・ワークフロー）

勤怠管理・承認ワークフローなどの社内システムは、client credentials（`private_key_jwt`）の社内システムとして登録し（下の「トークン発行」）、REST API を使う。渡す項目は登録の `--fields` で絞る（承認ルートには `assignments`・`managers` が要る。一時停止の状態を見るなら `account_status`）。設計の規則は [§9](docs/architecture/overview.md#9-社内システム連携下流)。

典型的な呼び出し順:

1. 初回の同期: `GET /api/v1/meta`（業務日付・段の定義・`max_as_of`）→ `GET /api/v1/orgs/tree`（組織の全体）→ `GET /api/v1/people?limit=100`（在籍者。`next_cursor` で続き）→ `GET /api/v1/changes`（`since` なしで今の位置を得る）
2. 差分の追従: `GET /api/v1/changes?since=<cursor>` を定期的に呼び、届いた ID を `GET /api/v1/people?ids=...`・`GET /api/v1/orgs/{id}` で取り直す（本文は種別と ID だけ。同じ ID が何度も届き得る）
3. 申請の承認者: 申請のたびに `GET /api/v1/people/{申請者}/approval-route` を呼ぶ
4. ポーリングの代わりに Webhook（任意）: 人事管理者が管理画面（`/admin?tab=webhooks`）で社内システムのクライアントに送信先（https）を登録すると、変わった人・組織・拠点の種別と ID が POST で届く（値は載せない。届いた ID を 2. と同じく取り直す）。署名（HMAC-SHA256・Standard Webhooks の形）の検証・重複の除き方・要約（大量の変更）の扱いは [Webhook の受け手の実装](docs/integration/webhook-receiver.md)、規則は [§9「Webhook の実装の規則」](docs/architecture/overview.md#webhook-の実装の規則)

```sh
# 申請者の承認ルート（主務から上位の組織長。空席は繰り上げ）
curl -H "Authorization: Bearer $TOKEN" "https://jinji.example.co.jp/api/v1/people/$PERSON/approval-route"
# 兼務先での申請（兼務の所属を起点に）・部長の承認だけ（段のキーは /meta の法人ごとの段の定義）
curl -H "Authorization: Bearer $TOKEN" "https://jinji.example.co.jp/api/v1/people/$PERSON/approval-route?org_unit_id=$ORG&level=department"
# 役職で引く・空席の段も返す
curl -H "Authorization: Bearer $TOKEN" "https://jinji.example.co.jp/api/v1/people/$PERSON/approval-route?position=部長&vacancy=vacant"
```

```json
{
  "person_id": "0199…", "as_of": "2026-10-12", "vacancy": "promote",
  "start": { "org_unit": { "id": "…", "code": "U2", "name": "第二係", "level_key": "unit", "level_name": "係", "level_rank": 4, "legal_entity_id": "…" },
             "assignment_type": "primary", "secondment": "none", "position_title": null },
  "steps": [
    { "order": 1, "org_unit": { "code": "S1", "name": "営業一課", "level_key": "section", "level_name": "課", "…": "…" }, "status": "assigned",
      "approver": { "id": "…", "display_name": "…", "account_status": "active", "…": "…" }, "approver_position": "課長",
      "skipped_units": [ { "org_unit": { "code": "U2", "…": "…" }, "reason": "vacant" } ] },
    { "order": 2, "org_unit": { "code": "DP1", "name": "営業部", "level_key": "department", "…": "…" }, "status": "assigned",
      "approver": { "…": "…" }, "approver_position": "部長", "skipped_units": [] }
  ],
  "unresolved_units": []
}
```

- `steps` は下の段から順。`steps` が空なら承認者を決められない（自動で承認しない）。`skipped_units` の `reason` は `vacant`（組織長が空席・退職済み・見えないので繰り上げた）か `self`（申請者自身が組織長）
- 一時停止中の承認者は承認者のまま返る（`approver.account_status` が `suspended`）。代理承認の情報は jin-ji に無いので、代わりの人の決め方はワークフロー製品の側で持つ
- 4/1 の組織改編などを事前に登録したい社内システムには、先行提供（有効日の N 日前から将来の予定を見せる。0〜90 日）を設定できる。人事管理者が起案し、別の人事管理者が承認すると、その日数だけ先の `as_of` を指定でき、変更フィードもその日数だけ早く届く（取り消された予定は変更フィードで届く）。人の委任（AI エージェント・UI）には効かない:

  ```sh
  # 起案（起案者は人事管理者の person。承認は UI でログインした別の人事管理者が「API クライアント」の画面で行う）
  scripts/dotnet.sh run --project src/JinJi.Host -- propose-disclosure-lead --client-id workflow --days 30 --proposed-by <person.id> --note "4/1 の組織改編の事前登録"
  scripts/dotnet.sh run --project src/JinJi.Host -- list-disclosure-leads --client-id workflow
  # 承認後: 30 日先までの時点を指定できる（/meta の max_as_of。例は業務日付が 2026-10-12 のとき）
  curl -H "Authorization: Bearer $TOKEN" "https://jinji.example.co.jp/api/v1/people/$PERSON/approval-route?as_of=2026-11-01"
  ```

## 人のログイン

ADR-0004 の段階 A（[アーキテクチャ概要 §6.4](docs/architecture/overview.md#64-人のログインの実装の規則adr-0004-の段階-a)）。画面は SPA が持ち、サーバーは `/auth` の JSON API と外部 IdP への転送を持つ（API のポートだけ。MCP のポートは Cookie を読まない）。

- 設定（`JinJi:Auth:Login`）: `PublicOrigin`（利用者から見た https の公開 URL。redirect_uri・パスキー・リンクの URL に使う）、`PasskeyServerDomain`（パスキーの RP ID。未設定ならパスキーを使わない）、`Providers`（外部 IdP。`Name`・`Type`〔`entra` / `oidc`〕・`Authority`・`TenantId`〔Entra〕・`ClientId`・`ClientSecret`・`EmployeeNumberClaim`・`TrustEmployeeNumberClaim`・`EmployeeNumberLegalEntity`）、`Session`（無操作 8 時間・最長 12 時間）、`Lockout`（5 回で 15 分）、`RateLimit`（接続元 → 接続元 × アカウント）
- 外部 IdP の主体は person に紐付ける（既存の紐付け → Entra の oid と取込の外部 ID の対応 → 信頼する社員番号のクレーム）。メールでは紐付けない。紐付けできないログインは拒否して記録し、人事管理者が手動で紐付ける
- 管理 CLI（接続文字列 `ConnectionStrings:App`。すべて監査ログに残る）:

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

ADR-0004 の段階 B（[アーキテクチャ概要 §6.5](docs/architecture/overview.md#65-トークン発行の実装の規則adr-0004-の段階-b)）。`JinJi:Auth:Server:Issuer` を設定したときだけ有効。

- 設定（`JinJi:Auth:Server`）: `Issuer`（API の公開 URL。人のログインの `PublicOrigin` と同じ）、`ApiResource`（既定 `{Issuer}/api/v1`）、`McpResource`（MCP サーバーの URI。例 `https://jinji.example.co.jp/mcp`）、寿命（アクセストークン 10 分・リフレッシュ 無操作 7 日 / 最長 30 日・認可コード 60 秒）、`TokenCacheDuration`（検証のキャッシュ、5 秒）、`Cimd`（`AllowedDomains` 既定 `claude.ai`・`chatgpt.com`・`vscode.dev`、大きさ 5 KiB・時間 5 秒、新しい CIMD のクライアントの既定のスコープ・項目）、`RateLimit`（接続元 → 接続元 × client_id）
- エンドポイント（API のポート）: `/.well-known/oauth-authorization-server`、`/oauth/authorize`（同意画面 `/consent?request=` へ転送）、`/oauth/requests/{id}`・`/oauth/requests/{id}/decision`（同意画面の JSON API）、`/oauth/token`、`/oauth/revoke`、`/oauth/delegations`・`/oauth/delegations/{id}/revoke`（本人による委任の一覧と取り消し。UI でログインした本人だけ）。MCP のポートは `/.well-known/oauth-protected-resource[/mcp]` と `POST /mcp`（下の「MCP サーバー」）
- クライアントの管理（管理 CLI か、UI でログインした人事管理者の `/auth/admin/oauth/*`）:

```sh
# MCP クライアントの事前登録（公開クライアント）
scripts/dotnet.sh run --project src/JinJi.Host -- register-client --client-id my-agent --name "社内エージェント" --auth none \
  --grant-types authorization_code,refresh_token --redirect-uris http://127.0.0.1/callback --resources mcp --scopes jinji.read --fields '*' --mcp-tools '*'
# 社内システム（client credentials・private_key_jwt）
scripts/dotnet.sh run --project src/JinJi.Host -- register-client --client-id attendance --name "勤怠" --auth private_key_jwt \
  --grant-types client_credentials --resources api --scopes jinji.read --fields employee_number,display_name,assignments,managers,employment_status
scripts/dotnet.sh run --project src/JinJi.Host -- add-client-key --client-id attendance --kid 2026-10 --key-file attendance.pub.pem
# CIMD: 承認待ちの一覧と承認・許可ドメインの追加
scripts/dotnet.sh run --project src/JinJi.Host -- list-clients --status pending_approval
scripts/dotnet.sh run --project src/JinJi.Host -- approve-client --client-id https://example.com/oauth/client.json
scripts/dotnet.sh run --project src/JinJi.Host -- allow-cimd-domain --domain example.com
# 社内システムの先行提供（有効日の N 日前から将来の予定を見せる。承認は UI でログインした別の人事管理者）
scripts/dotnet.sh run --project src/JinJi.Host -- propose-disclosure-lead --client-id attendance --days 30 --proposed-by <人事管理者の person.id>
scripts/dotnet.sh run --project src/JinJi.Host -- list-disclosure-leads [--client-id attendance]
# 停止・失効
scripts/dotnet.sh run --project src/JinJi.Host -- suspend-client --client-id my-agent
scripts/dotnet.sh run --project src/JinJi.Host -- revoke-tokens --client-id my-agent [--person <person.id>]
```

- client_secret（`issue-client-secret`）は機密クライアントの移行用の例外で、期限必須（既定 90 日）。値は出力にだけ現れる

## MCP サーバー

設計は [アーキテクチャ概要 §7.3](docs/architecture/overview.md#73-mcp-サーバーの実装の規則) と [ADR-0006](docs/adr/0006-mcp-sdk.md)。MCP のポート（既定 28090）の `POST /mcp`（Streamable HTTP。セッションなし）で、AI エージェントが委任元の利用者の権限で人事情報を参照する。使えるのは jin-ji のトークン発行（上の「トークン発行」）で MCP の resource（`McpResource`）宛てに発行した人の委任のトークンだけ。

| ツール | スコープ | 内容 |
| --- | --- | --- |
| `search_people` | `jinji.read` | 人の検索（氏名・社員番号・メールの部分一致と読みの照合）・組織・法人・段・所属の種類・役職・雇用区分・職種・拠点（拠点・種別・都道府県）・リモートワークの有無・入社年月日・状態の絞り込み・並べ替え（拠点ごと・リモートワークの対象者が先を含む）・条件ごとの件数（`include_facets`）。カーソルで続きを取る |
| `get_person` | `jinji.read` | 1 人の詳細（見える項目だけ）。`include_managers` で直属の上長も |
| `get_people` | `jinji.read` | 人を ID でまとめて（100 件まで） |
| `get_approval_route` | `jinji.read` | 承認ルート（申請を誰が承認するか。REST の `approval-route` と同じ規則・同じ項目名） |
| `get_org` | `jinji.read` | 組織と、上位組織・直下の組織・組織長 |
| `get_org_tree` | `jinji.read` | 組織図の部分木（根と深さを指定。最大 500 組織） |
| `search_orgs` | `jinji.read` | 組織の検索（名前・略称・コード） |
| `list_sites` | `jinji.read` | 拠点の一覧と人数（REST の `/sites` と同じ規則。人数は委任元に見える人だけ） |
| `list_floors` | `jinji.read` | 拠点・建物の階の一覧と席数・空席数（REST の `/sites/{id}/buildings`・`/buildings/{id}` と同じ） |
| `find_vacant_seats` | `jinji.read` | 空席（割り当ての無い席）を探す（人は返さない。最大 100 件） |
| `list_floor_occupants` | `jinji.read` | 階の在籍者と席（決まった席・持ち場だけ。当日の所在は含めない） |
| `get_changes` | `jinji.read` | 変更フィード（種別と ID だけ） |
| `get_directory_info` | `jinji.read` | 業務日付・法人ごとの組織の段の定義・時点の目盛り |
| `list_review_proposals` | `jinji.read` | 取込の要確認の提案の根拠（委任元が人事管理者か取込の実行者のときだけ） |
| `suggest_review_resolution` | `jinji.propose` | 要確認の提案に解決の推定を添える（作業領域だけに書く。公開側は変わらない） |
| `list_corrections` | `jinji.read` | 修正依頼の一覧・状況（委任元に見える範囲） |
| `report_correction` | `jinji.propose` | 修正依頼の起票（委任元の名義。人事パッケージで直せる人への情報提供で、jin-ji の公開データは変わらない） |

- 時点の指定（`as_of`）・返却人数の上限・ページング（署名付きのカーソル）・項目の上限・関係スコープは REST API と同じ（同じサービスを通る）。値の無い項目は省略する（REST は `null`）
- 項目の上限の `*` は当日の所在（`presence`）を含まない。`get_person` の `presence` は、所在の提供があり、項目の上限に `presence` を明示したクライアントにだけ返す
- ツールはクライアントごとの許可リスト（`--mcp-tools`。空は何も許さない、`*` はすべて）とトークンのスコープの両方を満たすものだけが見え、使える。提案のツールは `jinji.propose` が要り、無ければ 403（`insufficient_scope`）
- 呼び出しは監査ログ `mcp.tool.call`（ツール名・委任元・クライアント・対象の人）に残る
- 停止スイッチ: `JinJi:Mcp:Enabled=false` で MCP のポートを止める（`/healthz` 以外は 503。appsettings.json の変更は再起動なしで効く）。REST API・UI は動き続ける

クライアントの設定の例（`https://jinji.example.co.jp/mcp` は `JinJi:Auth:Server:McpResource` の URI）:

```sh
# Claude Code（初回の利用で認可の画面が開く）
claude mcp add --transport http jinji https://jinji.example.co.jp/mcp
```

```json
// VS Code（.vscode/mcp.json）
{
  "servers": {
    "jinji": { "type": "http", "url": "https://jinji.example.co.jp/mcp" }
  }
}
```

- Claude・VS Code は CIMD（Client ID Metadata Document）で自分を名乗る。許可ドメイン（既定 `claude.ai`・`vscode.dev`・`chatgpt.com`）のクライアントは最初の認可で `api_client` に登録され、スコープは `JinJi:Auth:Server:Cimd:DefaultScopes`（既定 `jinji.read`）、ツールは `DefaultMcpTools`（既定は空 = 何も使えない）になる。使わせるツールは管理者が決める:

  ```sh
  scripts/dotnet.sh run --project src/JinJi.Host -- list-clients
  scripts/dotnet.sh run --project src/JinJi.Host -- update-client --client-id https://vscode.dev/oauth/client-metadata.json --mcp-tools '*'
  ```

- CIMD に対応しないクライアントは事前登録（上の `register-client`。redirect_uri はクライアントのコールバック）で使う
- claude.ai（Web・Desktop）からの接続は Anthropic のクラウドから届く。社内網だけに置く構成では、ローカルで動くクライアント（Claude Code・VS Code など）に限られる（ADR-0004）

## Web UI

React + Vite + TypeScript の SPA（`web/`）。成果物は静的ファイルで、サーバーに Node.js は置かない（ADR-0001）。画面は組織図・3D 組織俯瞰・人の詳細・検索・修正依頼と、ログイン・同意・アカウント・認証の管理（下の「画面」）。

### コマンド（すべてコンテナ内で実行する）

npm 依存のインストール・ビルドはホストで行わない（ADR-0001）。`scripts/pnpm.sh` が公式イメージ `node:24.20.0-bookworm-slim`（ダイジェスト固定）の中で pnpm（`web/package.json` の `packageManager` で版とハッシュを固定し、corepack が取得）を実行する。マウントするのはリポジトリだけで、ホストのホームディレクトリ・資格情報（`~/.npmrc` 等）は渡さない。HOME と pnpm のストアは名前付きボリューム `jinji_pnpm_home` / `jinji_pnpm_store`、ホストと同じ UID/GID、`--cap-drop ALL`。

```sh
scripts/pnpm.sh install --frozen-lockfile   # lock ファイルどおりに入れる
scripts/pnpm.sh typecheck                   # tsc（strict）
scripts/pnpm.sh lint                        # oxlint（警告もエラー扱い）
scripts/pnpm.sh test                        # Vitest
scripts/pnpm.sh build                       # 本番ビルド（web/dist。デモのコード・データが入っていないことも検査する）
scripts/pnpm.sh build:demo                  # デモ用ビルド（web/dist-demo）

# 開発サーバー（ホストの 127.0.0.1:28000 だけに公開。既定はデモアダプタ）
JINJI_PNPM_PORT=28000 JINJI_PNPM_NAME=jinji-pnpm-dev scripts/pnpm.sh dev
# → http://127.0.0.1:28000/  （#/org-chart・#/overview-3d・#/search、/login・/consent・/account・/admin。?theme=dark で配色を固定）

# スクリーンショット（上の開発サーバーを起動したまま。公式イメージ mcr.microsoft.com/playwright をコンテナで使い、ホストにブラウザを入れない）
web/scripts/screenshots.sh                  # → web/screenshots/（gitignore 済み。JINJI_SHOTS=正規表現 で画面を絞れる）
docker stop jinji-pnpm-dev                  # 終わったら止める

# 配信時の CSP の下での確認: デモ用ビルドを preview で配信する（API と同じ CSP ヘッダーを付ける。vite.config.ts）
scripts/pnpm.sh build:demo
JINJI_PNPM_PORT=28000 JINJI_PNPM_NAME=jinji-pnpm-dev scripts/pnpm.sh preview:demo
web/scripts/screenshots.sh                  # 最後に「CSP 違反: 0 件」を表示する（違反・画面のエラーがあれば終了コード 1）
```

### 配信（API の役割）

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

### 依存の方針（npm）

- 設定は `web/pnpm-workspace.yaml` と `web/.npmrc`: 公開から 7 日未満の版を解決しない（`minimumReleaseAge: 10080`）、`trustPolicy: no-downgrade`、lifecycle script は既定で拒否（`onlyBuiltDependencies: []`、許可していないビルドの要求は `strictDepBuilds` で失敗）、レジストリ以外からの推移的依存を拒否（`blockExoticSubdeps`）、取得元は `registry.npmjs.org` のみ、`engine-strict`・`strict-peer-dependencies`
- 直接依存は版を完全に固定する（範囲指定しない）。依存を足すときは理由をレビューで記録し、標準機能や数十行の自前実装で済むものは入れない
- ネイティブバイナリ（rolldown・lightningcss・@tailwindcss/oxide・TypeScript 7・oxlint）は OS / CPU ごとの optionalDependencies で配られ、install script を要しない。許可している lifecycle script は無い
- 入れていないもの: TanStack Router / Query（画面が少なく、ハッシュのルーターと小さなキャッシュで足りる。2026-05 の TanStack の汚染事件もあり、必要になった時点で版を固定して検討する）、Radix（今はネイティブ要素で足りる）、openapi-typescript（TypeScript の JS API と多数の推移的依存を要するため、`web/scripts/gen-api-types.mjs` の小さな変換で型を生成）、ESLint（oxlint で代替）、drei（OrbitControls は three 本体のものを使う）

### API とデータの取得元

- API クライアントの型は OpenAPI の文書（`web/openapi/v1.json`、API の `/openapi/v1.json` を保存したもの）から `scripts/pnpm.sh gen:api` で `web/src/api/schema.gen.ts` に生成する。生成物が文書と一致することをテストで確かめる
  - 文書の更新: `scripts/dotnet.sh run --project src/JinJi.Host -- --role api` を起動し、別の端末で `docker exec <コンテナ名> curl -s http://127.0.0.1:28080/openapi/v1.json > web/openapi/v1.json`
- 画面は取得元（`DirectoryAdapter`）を通してだけデータを読む。実装は API アダプタ（`/api/v1` に同一オリジンで接続）とデモアダプタの 2 つで、同じ形を返す
- API はすべての項目を明示的に返す（見えない項目・値の無い項目は `null`。見えるかどうかは `visible_fields` で区別する）。UI は値を補ったり推測したりしない
- API アダプタが使うもの: 起動時に `GET /api/v1/meta`（業務日付・時点の目盛り・未来の時点を指定できるか。読めなければ端末の時計の日本時間の今日・目盛りなし）、組織図の人数と兼務・出向の線に `GET /api/v1/orgs/stats`（見える人だけの集計）、組織長の名前に `GET /api/v1/people?ids=`（100 件ずつの一括取得）、検索の組織に `GET /api/v1/orgs?query=`（API 側で正規化して照合）、人の検索に `GET /api/v1/people`（絞り込み・`sort`・最初のページだけ `facets=true`。複数の値は同じ名前を繰り返す）
- デモアダプタとデモデータ（`web/src/demo/demo-data.json`。`samples/messy-corp` の架空データから `scripts/pnpm.sh gen:demo` で生成）は、`--mode demo` のビルドと開発サーバーにだけ含める。本番ビルドでは定数 `__JINJI_DEMO__` が false になり、動的 import ごと取り除かれる（`build` の後に検査）
  - デモアダプタは一般社員から見た場合の API の規則（見えない項目は `null` にして `visible_fields` に入れない、将来の終了日・一時停止の終了予定日・退職前の退職年月日は返さない、未来の時点は 403、退職後 90 日を過ぎた人は 404）を簡略に真似る
  - `ground_truth.json` は現在の状態しか持たないため、2026-04-01 の一斉異動の前所属などは導出している（`meta.derivations`）。合成 1 万人のデータ（画面の「データ」で切り替え）は描画の負荷確認用
- SPA は API（api の役割）から配信され、応答ヘッダーで CSP（`default-src 'self'; script-src 'self'; style-src 'self'; ... frame-ancestors 'none'`）が付く。`style-src` に `'unsafe-inline'` は無いので、`<style>` を差し込むコードは入れない（React・React Flow・motion・3D のラベルが要素に付ける style は CSSOM 経由で対象外）。本番ビルドの `index.html` にも同等の CSP を meta で付ける。外部 CDN は使わない。フォント（BIZ UDPGothic、OFL）は `@fontsource/biz-udpgothic` の woff2 をセルフホストする
- 認証は BFF（HttpOnly Cookie のセッション）。API アダプタはトークンを付けず、Cookie で要求する。画面を開くと `GET /auth/me` で確かめ、ログインしていなければ `/login?return_url=<今の画面>` へ、緊急用アカウントは `/admin` へ移る。デモ（架空データ）ではログインを求めない
- 認証まわりの API（`/auth`・`/oauth`）は `web/src/auth/` の取得元（`AuthAdapter`）を通す。実装は API アダプタ（状態を変える要求に `X-JinJi-CSRF` を付ける。トークンは `GET /auth/csrf` とログインの応答。CSRF で拒否されたら 1 回だけ取り直す）とデモアダプタ（架空の利用者・クライアント。デモ用ビルドにだけ含める）。デモ用ビルドの認証の画面は `?source=api` で API に切り替わる

### 画面

| 画面 | 内容 |
| --- | --- |
| 組織図（`#/org-chart`） | React Flow。法人ごとの組織ツリーを左から右へ。開閉・ズーム・組織長・人数（主務・配下を含む人数・兼務と出向の受け入れ）。操作の帯は図の上（外）に置き、図の読み方の説明は帯の「図の見方」（?）の箱に入れる。兼務（橙・破線）・出向（緑・点線）の線は組織の組で集計し、閉じた組織は祖先に寄せる。線は既定では指した（ホバー・フォーカス）か選んだ組織の分だけを出し、人数のラベルは相手の組織の札の横に置く（右の列の札と重なるときは左。同じ相手との行き来は「兼務先 1名・兼務元 1名」と 1 つにまとめる。「すべて」「表示しない」に切り替え可）。組織の詳細パネルは兼務・出向を受け入れ・送り出し × 「この組織に直接」「配下を含む（外の組織との間）」の表で示し、閉じた組織の線・3D の弧の数は「配下を含む」と一致する。「一覧表」に切り替えると、組織ごとの人数と兼務・出向の相手先を表で読める。タイムマシンで日付を変えると配置と人数の増減を補間して遷移する |
| 3D 俯瞰（`#/overview-3d`） | React Three Fiber。組織の形（深さ＝段）と規模を俯瞰する画面。上の段ほど上位で、段の中の位置は d3-hierarchy の pack（子は親の真下にまとまる cone tree。1 万人・組織 1,000 超でも最下層が輪に密集しない）。組織は段ごとに底面の形を変えた工業製品風のモジュール（法人＝十二角の塔・1 段目＝八角・2 段目＝六角・3 段目＝円・4 段目以降＝角を落とした四角。白い外装・黒鉛色の台座と溝・上面の板にデータの色・黄の表示灯）で、どの形も底面の面積＝主務の人数（高さは一定）。空間は白基調（ダークはグラファイト）のワイヤーフレーム（格子の床・目盛り付きの段の輪・フォグ・CSS の走査線〔動きを減らす設定では止める〕）。黄（`--signal`）は選択・ホバー・表示灯・段の目印だけのアクセント。テクスチャ（`web/src/features/overview3d/textures/`）はグレースケールのタイル 3 枚で、読み込めなくても形と色だけで成り立つ。人は組織ごとの点（最大 120）で、近づいたときだけ描く。組織を選ぶと黄と黒の縞の輪で囲み、関係する組織（上位・配下・兼務出向の相手先）以外を灰色に退かせ、相手先を弧（兼務は破線・出向は点線）で結んでカメラを寄せる（配下の兼務・出向は選んだ組織に寄せて集計）。名前は HTML のラベルで、優先度の高い順に重ならないものだけを出す。段のラベルは「2段目 · 部 · 13組織」。組織は段ごとに部品（外装・板・表示灯）を 1 つずつの InstancedMesh（ジオメトリは段ごとに共有）、人の点は 1 つの InstancedMesh、`frameloop="demand"`。キーボードでは右の組織の一覧から選ぶ |
| 人の詳細 | 業務上の氏名・フリガナ・ローマ字、所属（主務・兼務・出向と役職）、上長、連絡先、基本情報。見えない項目（`visible_fields` に無い）は「非公開」、値が `null` の項目は「—」。一時停止中は状態（アイコンと文字）と開始日だけ |
| 修正依頼（`#/corrections`） | 修正担当者・人事管理者には全件（未対応・閉じたもの・すべて）、件数の帯（未対応・新しい依頼・長く未対応）、行ごとの「対応済み」「対応不要」（メモを入力して確定）、CSV のダウンロード。それ以外の人には自分が起票・賛同した依頼と自分についての依頼だけ（操作なし）。画面の切り替えのレールの「修正依頼」のアイコンに未対応の件数のバッジ（修正担当者・人事管理者だけ。長く未対応があれば ▲）。人の詳細の「値の誤りを報告」から起票する（見える項目だけ。値の提案は値が 1 つの項目だけ）。理由・メモはテキストとして表示し、リンクにしない。デモでは架空の依頼と「デモの閲覧者」の切り替えで一般の社員の見え方も確かめられる |
| 検索（`#/search`） | 人の多角的な検索。自由語（氏名・フリガナ・ローマ字・社員番号・社用メール。異体字・かな・ローマ字の綴り〔Sato / Satou / Satoh〕・長音の揺れを吸収）と、組織（配下を含むかの切り替え）・組織の段・主務兼務出向・役職・法人・雇用区分・職種・拠点（拠点・種別・都道府県）・リモートワークの有無・入社年月日（勤続年数の目安からも。和暦で入力可）・在籍状態・アカウント状態の絞り込み。条件ごとの件数（見える人の見える値だけを数えた値）を候補の横に出す。結果は名刺の比率（91:55）のカード（法人・組織・役職・氏名・フリガナ・ローマ字・社用メール・内線・社用携帯・拠点〔種別・都道府県〕、リモートワーク・兼務・出向・一時停止の印、メール・内線のコピー、顔写真の代わりに頭文字）か一覧（表）。並べ替え（フリガナ・組織・入社日）、選んだ条件のチップと解除、下端で続きを読む（1 ページ 48 人）。結果を拠点ごと・リモートワークの対象者ごとにまとめられる（カードの見出し・表の見出し行で区切る。API の `sort=site` / `remote_work` と組み合わせ、続きを読んでも区切りが続く。見えない値の人は最後の「表示なし」にまとめる）。条件・並び・まとめ方（`group`）・表示の形は URL に持つ（共有・戻る操作）。組織図・3D 俯瞰は「拠点で色分け」（`color=site`。人数の多い 3 拠点まで色、ほかは「その他」）。条件はデスクトップでは左の列、スマホではシート。自由語に当たる組織はその組織での絞り込み・組織図へのリンクとして出す |
| ログイン（`/login`） | 有効な方式（`GET /auth/providers`）だけを出す: 外部 IdP への遷移、パスキー（WebAuthn。ブラウザ標準の API だけ）、パスワード → 認証アプリの確認コード / リカバリコード。失敗の文はアカウントの有無・ロックを区別しない。`?error=unbound` などの外部 IdP の拒否も日本語で示す。デモは `demo` / 任意 / `123456` |
| 同意（`/consent?request=`） | クライアント名・登録の種類（事前登録 / CIMD の自己申告）・戻り先のホスト・ループバックだけのアプリの注意・スコープの意味・渡す範囲（項目の上限 ∩ 本人に見える範囲）・MCP ツール。許可・拒否は画面の下に固定 |
| アカウント（`/account`） | ログイン中の情報、ローカルアカウントのパスキーの登録・一覧・削除と認証アプリ（TOTP）の設定（QR は出さず、セットアップキーと otpauth の URI。有効にしたらリカバリコードを 1 回だけ表示）、AI エージェントへの許可（委任）の一覧と取り消し（トークン発行を使う環境だけ）、ログアウト |
| リンク（`/account-recovery`） | 管理者が発行したリンクでパスワードを設定・パスキーを登録する。鍵（フラグメント）は読んだらアドレス欄から消す |
| 認証の管理（`/admin`） | 人事管理者と緊急用アカウント。未紐付けのログインの紐付け・取り消し、ローカルアカウントの一覧（ユーザー名で絞る）とロックの解除・リンクの発行・無効化と再有効化、API クライアントの停止・再開・CIMD の承認と却下・MCP ツールの許可リスト、CIMD の許可ドメイン（後の 2 つは人事管理者だけ） |

- 色: データの色は「主務＝青・兼務＝橙・出向＝緑」の 3 つだけ（dataviz の参照パレットの 1〜3 枠。`web/src/index.css`）。ライト・ダークとも validate_palette.js（`--pairs all`）を通している。色は線・円などの印にだけ使い、文字は文字色にして線種の見本を添える（色だけに頼らない）。一時停止などの状態は状態の色に必ずアイコンと文字を付ける
- 日付: 内部は西暦の `yyyy-MM-dd`。表示は西暦（2026年4月1日）と和暦（令和8年4月1日。1 年は「元年」）をタイムマシンの箱の中で切り替える（端末に保存）。日付の入力はブラウザの `<input type="date">` の表示（ブラウザの言語設定で「10/01/2026」のようになる）を使わず、`2026/4/1`・`令和8年4月1日`・`R8.4.1`・全角の数字を受け付ける自前の欄にした（カレンダーのボタンでブラウザの日付選択も開ける）
- 画面の枠: 左にアイコンだけのレール（画面の切り替え・設定〔データの取得元・配色〕・利用者メニュー。名前はツールチップと読み上げ）、スマホ幅では下のタブバー。上の帯は画面の名前とタイムマシン（表示する日付）のボタンだけで、押すと日付の入力・目盛りのスライダー・今日に戻る・西暦 / 和暦の箱（`<dialog>`）を開く。今日以外を見ている間はボタンを警告の色にし、「今日に戻る」を箱の外にも出す。デモであることは「デモ」の札（押すと説明）で示す
- ライト / ダーク（OS の設定に従い、レールの「設定」で固定もできる。認証の画面は右上）。`prefers-reduced-motion` では遷移・カメラの移動を即時にする
- タイムマシンのスライダーの目盛りは取得元が持つ日付（API は `GET /api/v1/meta` の milestones、デモでは組織改編・一斉異動の日）。業務日付より後は、`meta.can_view_future` が true（人事管理者）のときだけ選べる

## 当日の所在の提供（拡張点）

設計は [建物・フロアモードの仕様](docs/design/building-floor-mode.md) §3.4。jin-ji は当日の所在を保存せず、座席予約も受けない（人事の範疇を超える施設管理で、連携先の範疇）。**所在は保持せず、外部の提供を注入して表示する**（リアルタイムの所在は連携先の範疇）。

- 拡張点は `JinJi.Application.Presence.IPresenceProvider`。今日（業務日付）・階または人を受け取り、人ごとの今日の階と、席（`seat.id`）またはエリア（配置図のゾーンの `zone_key`）を返す。型は時刻・入退館の記録を持たない
- 既定は `NoPresenceProvider`（提供なし）で、`GET /api/v1/floors/{id}/presence` は 404（`urn:jinji:problem:presence-unavailable`）、人の応答に `presence` は出ない
- 登録は、`JinJi.Host` を参照する小さな起動プロジェクトから差し替える（外部への問い合わせ・認証・失敗時の扱いは実装の責任。失敗したら空の結果を返すことを勧める）

  ```csharp
  using JinJi.Application.Presence;
  using JinJi.Host;
  using Microsoft.Extensions.DependencyInjection;
  using Microsoft.Extensions.DependencyInjection.Extensions;

  return await JinJiHost.RunAsync(args, services =>
      services.Replace(ServiceDescriptor.Singleton<IPresenceProvider, MyPresenceProvider>()));
  ```

- jin-ji は返った所在に必ず自分の認可を適用する（人事管理者も例外にしない。所在だけは全員を一覧できる立場を作らない）: 項目 `presence` の公開範囲（既定は本人と同じ部。`JinJi:Directory:PresenceVisibilityScope`）・ポリシー・個人単位の例外・クライアントの項目の上限（`*` は `presence` を含まないので、使うクライアントは `--fields '*,presence'` のように明示する）。席番号は項目 `seat` も見えるときだけ
- 今日だけ問い合わせ、`as_of`（過去・将来）では呼ばない。キャッシュ・永続化せず、変更フィード・Webhook・同期・監査ログに記録しない。応答は `Cache-Control: no-store`。所在は `GET /people/{id}`・MCP の `get_person`（1 人の参照）と `GET /floors/{id}/presence` だけに出し、人の一覧・検索には出さない

## 取り込み

設計は [アーキテクチャ概要 §8](docs/architecture/overview.md#8-取り込み名寄せ同期)（実装上の規則も §8 にある）。

- 項目マッピング（JSON）で取込元の列名を jin-ji の取込項目（`JinJi.Ingest.IngestFields`。DB の `jinji_ingest_field_keys()` と同じ許可リスト）に対応させる。マッピングにない列・保持禁止の列名（生年月日・性別・住所・給与など。マッピングに関わらず）は **staging に保存する前に捨てる**

  ```json
  {
    "name": "hr-export-people",
    "target": "people",
    "source": { "kind": "csv", "encoding": "auto", "headerRow": 1 },
    "legalEntityCode": "A",
    "employeeNumber": { "padLength": 6 },
    "stagingRetentionDays": 90,
    "fields": {
      "employee_number": ["社員番号", "社員No"],
      "full_name": ["氏名"],
      "full_name_kana": ["フリガナ"],
      "work_email": ["社用メール"],
      "hire_date": ["入社日"],
      "org_code": ["所属コード"],
      "position_title": ["役職"]
    },
    "ignore": ["備考"],
    "defaults": { "employment_type": "正社員" }
  }
  ```

  - `target`: `people`（在籍者名簿）、`org_units`（組織図。`org_code`・`org_name`・`org_level`・`parent_org_code`・`org_head_employee_number` / `org_head_name`・`org_remarks` など）、`personnel_actions`（辞令。`action_date`・`action_text` など）、`directory_users`（Entra ID のユーザー。`external_id`・`account_enabled` など）、`sites`（拠点マスタ）、`floor_layouts`（建物・階・配置図の改訂。配置図の JSON〔`source.kind: layout_json`、形式 `jinji-floor-layout/v1`〕か席の CSV。外形の行の無い CSV は今の配置図を引き継いで席だけを置き換える）、`workplace_assignments`（人の決まった席・持ち場。社員番号・席番号〔全角・ハイフンの揺れをそろえる〕かエリア・主副・開始日・最終日）
  - `source`: `kind`（`csv` / `xlsx` / `graph_json` / `layout_json`。省略時は拡張子）、`encoding`（`auto` / `utf-8` / `shift_jis`）、`delimiter`、`headerRow`（表題の行がある場合）、`sheet`、`fillMergedCells`（結合セルの値を補完する）、`hierarchy`（列が階層の組織図。`entityColumn`・`columns`・`noCarrySuffixes`）
  - 候補の列名は全角・空白のゆれを吸収して比べ、行ごとに先に書いた列の値を優先する（空なら次の候補）。候補・無視する列名に保持禁止の項目名は書けない
  - `legalEntityAliases`（法人のコード → 別名）、`employeeNumber.prefixedPadLength`（L45 → L0045）、`discardWhenPresent`（列に値がある行で捨てる項目）、`fullSnapshot`（Entra の全件の取込）
  - サンプルの設定は `samples/messy-corp/mappings/`
- 作業領域は hr_admin と取込の実行者本人だけが見られる（RLS。修正依頼は修正担当者・起票者・対象の本人にも見える。下の「修正依頼」）。実行者本人（AI エージェントは委任元）は承認なしで自分の取込の作業領域に書き込める（§7.1 の「公開されない作業領域」）
- 公開側への反映は変更提案の承認だけ。承認者 ≠ 取込の実行者（アプリと DB の CHECK 制約）。訂正は旧行の記録期間を閉じて新しい行を追加し、有効日を指定した取込は版を有効日で分ける
- 辞令は発令の提案にし、反映時に承認時の所属・一時停止から版を導出する。発令日前の発令（内示）は反映後も人事管理者以外には見えない
- 自由記述の項目に生年月日・住所・電話番号らしき値があれば、staging に保存せず修正依頼にする
- xlsx の読み込みに ClosedXML を使う。参照するのは `JinJi.Connectors` だけで、API / MCP からは推移的にも参照しない（`ProjectReferenceTests`。理由と推移的依存は ADR-0003）
- 列が階層の組織図で `source.hierarchy.skipCandidateColumns`（例: `["チーム"]`）を指定すると、結合セルの補いで上位組織を決めた新規の組織に、段飛ばしの上位組織の候補の要確認（`org_parent_ambiguous`）を作る

### 要確認の解決

設計は [アーキテクチャ概要 §8](docs/architecture/overview.md#8-取り込み名寄せ同期) の「要確認の解決」。

- 要確認（`needs_review`）の提案は、人事管理者の人が `ProposalResolutionService`（REST は上の `/api/v1/ingest/proposals/{id}/resolve`）で「候補から選ぶ」「新規として登録する」「修正依頼に回す」「却下」を選ぶ。取込の実行者・AI エージェント・ワーカーは解決できない
- 解決の結果は通常の提案（起案中）になり、既存の承認フローに乗る。**解決者はその提案を承認できない**（実行者・解決者・承認者の 3 人が要る。アプリと DB の CHECK 制約）
- 選べるのは候補（名寄せの候補・組織の候補・LLM が順位を付けた候補）だけ
- 名寄せの要確認を解決・却下したら、その行の氏名を作業領域（staging・正規化結果）から消す。人に対応付けられなかった行の staging は保持期限を短くする（項目マッピングの `unidentifiedStagingRetentionDays`、既定 14 日）。戸籍名（旧姓使用者の戸籍姓）を作業領域に残さないため（ADR-0002）

### LLM による候補提示

設計は [アーキテクチャ概要 §8](docs/architecture/overview.md#8-取り込み名寄せ同期) の「LLM による候補提示」、依存は [ADR-0005](docs/adr/0005-llm-dependency.md)。

- 取込ごとのオプトイン（既定はオフ）。要確認の提案のうち、ルールでは決まらない 4 種（社員番号の誤記・欠落、所属のあいまいな辞令、段飛ばしの組織の位置、Entra の旧組織名）に、候補の順位・確信度・理由を Structured Outputs で受け取り、提案の根拠（作業領域）に「LLM による推定」として付ける。提案の状態・値は変えず、公開側には人の解決と別の人の承認を経ないと出ない
- 送るのは判定に要る項目だけ（行の氏名・社員番号・発令の文面・組織名・役職、候補の業務上の氏名・社員番号・所属・役職、組織の名称・上位組織名・段・組織長の有無・配下の組織名）。送信先と送った項目名・件数（値は記録しない）を `llm_request_log` と監査ログ（`ingest.llm.send`）に残す
- 送信先は許可リスト（`JinJi:Llm:AllowedEndpoints`）だけ。候補に無い ID・スキーマに合わない応答は捨てる。行の中身は区切りの中にデータとして渡す
- 接続先は OpenAI 互換（OpenAI / Azure OpenAI の v1 API / LiteLLM など）をベース URL で切り替える。モデル名は設定で指定し、固定しない。API キーは環境変数（名前は設定）から読む

## Entra ID への同期

設計は [アーキテクチャ概要 §8](docs/architecture/overview.md#8-取り込み名寄せ同期) の「同期」と「同期の実装上の規則」。

- jin-ji の公開データ（業務日付時点。内示・将来の予定は含めない）と Entra のユーザー一覧（Graph の `GET /users` の JSON。今はファイル入力）の差分から**同期計画**を作り、作業領域（`sync_plan`・`sync_plan_item`）に保存する。対象は表示名・姓・名・部署・役職・会社名・勤務地（人の拠点の名前）・社員番号（employeeId）・上長（manager）・アカウントの有効/無効。アカウントの作成は推奨として出し、既定ではスクリプトに含めない。対応付けは外部 ID の対応（取込の Entra ID の承認で作るもの）だけで、人ではないアカウント・対応付けの無いアカウントは触らない
- **IT 全般統制の項目（退職者の無効化・一時停止者のサインイン停止）を最優先（critical）**にし、計画・差分レポートの先頭に件数と一覧を出す。除外の設定で無効化されない退職者・一時停止者も critical の警告にする
- 各項目に根拠（jin-ji のどのデータか）と、変える項目の Entra の現在値（スクリプトの事前条件）を付ける
- 承認（起案者 ≠ 承認者。人事管理者の人だけ）を経た計画だけがスクリプト出力・直接反映の対象。承認・出力・直接反映の時点で jin-ji のあるべき値を計算し直し、計画と違えば（期限切れも）**古い計画（stale）**にして拒否する。新しい計画を作ると前の起案中・承認済みの計画は置き換え済みになる
- 同期の設定（表示名の形式・部署名の段・役職・会社名・対象の法人・除外するユーザー・同期する項目）は JSON のファイル（例: `samples/messy-corp/mappings/entra_sync.json`）。計画に全文と SHA-256 を保存し、**計画の承認が設定の承認を兼ねる**（理由は §8）

  ```json
  {
    "name": "entra-sync",
    "legalEntities": ["P", "L"],
    "displayNameFormat": "{family} {given}（{department}）",
    "departmentLevel": "department",
    "departmentLevelByLegalEntity": { "L": null },
    "jobTitle": "primary",
    "clearJobTitleWhenEmpty": false,
    "companyName": "legal_entity_name",
    "secondmentUsesHostOrg": true,
    "fields": ["displayName", "givenName", "surname", "department", "jobTitle", "companyName", "officeLocation", "employeeId", "manager", "accountEnabled"],
    "exclude": [{ "userPrincipalName": "admin@example.co.jp", "note": "管理者アカウント" }],
    "reenableAccounts": false,
    "includeCreatesInScript": false,
    "planMaxAgeDays": 7
  }
  ```

  - 表示名の差し込みは `{family}`・`{given}`・`{family_kana}`・`{given_kana}`・`{family_latin}`・`{given_latin}`・`{display_name}`・`{department}`・`{company}`・`{employee_number}`
  - 部署名は勤務先の所属（主務、出向中は出向先）の組織から上にたどった、指定の段（キー）の組織の名前（null・見つからなければ勤務先の組織そのもの）
  - jin-ji に値の無い項目は消さない（役職だけ `clearJobTitleWhenEmpty` で消せる）。無効なアカウントの再有効化は `reenableAccounts` が true のときだけスクリプトに含める

- **スクリプト出力（主）**: `Microsoft.Graph` PowerShell 用（Windows PowerShell 5.1 / PowerShell 7。UTF-8 BOM・CRLF）
  - 値はスクリプト本文に埋め込まず、同梱の `.data.json`（ASCII だけの JSON）を読む。スクリプトはデータの SHA-256 を照合してから始める
  - 冪等: 各ユーザーの更新前に現在値を読み、計画時の値ともあるべき値とも違う項目があればスキップして記録する（`skipped_precondition`）。あるべき値なら何もしない（`already_applied`）
  - `-WhatIf` でドライラン（`User.Read.All` だけで接続）。本番は計画に要る最小のスコープで `Connect-MgGraph -Scopes`（無効化だけなら `User.EnableDisableAccount.All` + `User.Read.All`、属性・上長があれば `User.ReadWrite.All`）
  - エラーでも続行し、実行ログ（`.log.json`・`.log.csv`、UTF-8 BOM）を必ず書く。`sync-result` で取り込むと、計画 ID とデータの SHA-256 が一致するものだけを項目ごとに記録・照合する（`sync_run`・`sync_item_result`）
  - 差分レポート（`.report.md`・`.report.csv`）。値は Markdown・HTML の記号をエスケープし、CSV は数式として解釈される先頭の文字を無害化する
- **直接反映（任意・既定は無効）**: `JinJi.Host sync-apply <plan-id> [--what-if true]`。同じ承認済みの計画を Graph REST（`PATCH /users/{id}`・`PUT /users/{id}/manager/$ref`）で適用する最小実装（HttpClient。Graph SDK は入れない）。読み取りとは別の書き込み用のアプリの資格情報（証明書かワークロード ID）を `JinJi:Sync:DirectApply` で明示的に有効にしたときだけ。REST API からは起動しない
- 監査ログ: `sync.plan.create`・`sync.plan.read`・`sync.plan.approve`・`sync.plan.reject`・`sync.script.export`・`sync.result.import`・`sync.apply`（拒否・失敗も）

## 依存パッケージの方針

- バージョンは `Directory.Packages.props`（Central Package Management）で一元管理し、各プロジェクトの `packages.lock.json` をコミットする
- CI では `-p:ContinuousIntegrationBuild=true`（または `-p:JINJI_LOCKED_RESTORE=true`）で locked mode にし、lock ファイルと異なる復元を失敗させる
- 取得元は nuget.org のみ（`nuget.config` の packageSourceMapping）。署名の検証を必須にしている
- 依存の追加は ADR かレビューで理由を記録する。公開から 7 日以上経った版を使う
- LLM の呼び出しに OpenAI 公式 .NET SDK `OpenAI` 2.14.0（JinJi.Llm だけ。推移的に Microsoft 発行の `System.ClientModel` 1.15.0・`System.Memory.Data`・`Microsoft.Extensions.*.Abstractions` 10.0.9）。API / MCP からは推移的にも参照しない（`ProjectReferenceTests`。理由と一覧は ADR-0005）
- MCP サーバーに公式 C# SDK `ModelContextProtocol.AspNetCore` 2.2.0（JinJi.Mcp だけ。推移的に同じ発行元の `ModelContextProtocol`・`ModelContextProtocol.Core` 2.2.0 と Microsoft 発行の `Microsoft.Extensions.AI.Abstractions` 10.8.3）。使うのはトランスポートだけで、認証・ツールの判定・監査は jin-ji 側（理由と一覧は ADR-0006）
- その他の依存（どれも Microsoft 発行・MIT）: `Microsoft.AspNetCore.OpenApi` 10.0.12（JinJi.Api。推移的に `Microsoft.OpenApi` 2.12.0）、`Microsoft.AspNetCore.Authentication.JwtBearer`・`Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12（JinJi.Host。ADR-0004。推移的に `Microsoft.IdentityModel.*` / `System.IdentityModel.Tokens.Jwt` 8.19.2、`Microsoft.Bcl.Cryptography` 10.0.2）。メトリクスは標準の System.Diagnostics.Metrics と自前の Prometheus 出力で、OpenTelemetry は入れていない（理由は概要 §7.2）

## 修正依頼

設計は [アーキテクチャ概要 §8](docs/architecture/overview.md#8-取り込み名寄せ同期) の「修正依頼の実装の規則」。jin-ji の公開データの正は人事パッケージなので、jin-ji の値が誤っていても jin-ji では直さず、人事パッケージで直して取り込み直す。修正依頼はそのための人事パッケージの担当者への情報提供で、起票しても jin-ji の公開データは変わらない。

- 起票の経路: 取込の自動検出（社員番号の欠落・解釈できない値・重複行など）と要確認の解決の「修正依頼に回す」、人（UI の人の詳細・REST）、AI エージェント（MCP の `report_correction`・REST。委任元の名義、`jinji.propose` が要る）
- 人・エージェントの起票は、見える人の見える項目（jin-ji が保持する人の項目）だけ。理由（500 文字）・値の提案（200 文字。値が 1 つの項目だけ）に生年月日・住所・電話番号らしき記述があれば保存しない（ADR-0002）。同じ人・項目の未対応の依頼があれば新しく起票せず賛同として数える。1 人あたり 24 時間に 20 件まで（`JinJi:Corrections:MaxReportsPerDay`）
- ロール `hr_corrector`（修正担当者。人事パッケージで直せる人）と人事管理者は、すべての依頼を参照し、対応済み・対応不要にできる（人だけ。AI エージェントは委任元が修正担当者でも状態を変えられない）。それ以外の人は、自分が起票・賛同した依頼と自分についての依頼だけを見られる（起票者は返さない）
- 起票時の値の指紋（値そのものは持たない）を残し、取込の提案の承認・定期の判定で値が変わったら自動で「解消」にする。取込由来の依頼は、同じ項目マッピングの次の取込で「置き換え」になる（まだ直っていなければ新しい取込が改めて起票する）
- 通知: jin-ji はメールを送らない。画面内の通知（未対応・新しい依頼・長く放置された依頼の件数）と、任意の Webhook（Teams のワークフロー / Slack の Incoming Webhook。既定は無効）。Webhook の本文は件数と jin-ji へのリンクだけで、人事データの値は載せない。ワーカーが 1 日ごと（`JinJi:Corrections:Notice:Interval`）にまとめて送り、`correction_notice_run`・監査ログ（`correction.notify`）・メトリクスに残す
- レポート（人事パッケージの担当者の作業リスト。状態・経過日数・社員番号・氏名・法人・主務の組織・項目・理由・正しい値の提案・起票の経路・賛同数・取込のファイルと行番号…）は、REST の CSV（UTF-8 BOM）と管理 CLI（CSV / xlsx）。xlsx は ClosedXML を使うので CLI だけ（API のプロセスには入れない。ADR-0003）

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- correction-report --output /src/out/corrections.xlsx [--status open|closed|all] [--format csv|xlsx]
scripts/dotnet.sh run --project src/JinJi.Host -- notify-corrections   # 自動の解消の判定・件数の集計・Webhook を 1 回（ワーカーの定期実行と同じ）
```

Webhook の設定の例（URL は秘密を含むので環境変数で渡す。ホストは許可リストと完全一致、https だけ、私設のアドレスには接続しない）:

```sh
JinJi__Corrections__Notice__Webhook__Enabled=true
JinJi__Corrections__Notice__Webhook__Format=teams            # または slack
JinJi__Corrections__Notice__Webhook__Url=https://...（Teams のワークフローの URL / Slack の Incoming Webhook の URL）
JinJi__Corrections__Notice__Webhook__AllowedHosts__0=hooks.slack.com
```

## データの扱い

ADR-0002 により、**全社公開レベルの情報だけを保持する**。保持しない項目（生年月日、性別、住所、給与・等級、評価、休職理由など）の列は作らない。

- 全テーブルの全列を `db/field-catalog.json` に ADR-0002 の分類で登録する。DB とカタログが食い違うとテストが失敗する
- 保持しない項目を表す名前（`birth`、`gender`、`address`、`salary`、`grade`、`my_number` など）がテーブル名・列名に現れるとテストが失敗する
  - 例外（`ip_address` など、住所ではない語の並び）は `tests/JinJi.IntegrationTests/Support/ForbiddenNames.cs` の `Exceptions` に理由付きで列挙する。語の境界で完全に一致したときだけ外れ、他の禁止規則は外れない
- 列を追加するときは、マイグレーションとカタログを同じ変更で更新する

### 保持期限の消去

設計は [アーキテクチャ概要 §4.5](docs/architecture/overview.md#45-保存期間と消去)。ワーカー（役割 `worker` / `all`）が 1 時間ごとに、終わった行を保持期間の後に消す。管理 CLI でも 1 回実行できる。

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- purge-expired      # 表ごとの削除件数を出力し、retention_run と監査ログ（retention.purge）に残す
```

- 消すもの（既定の保持期間）: UI のセッション（終了から 30 日）、未紐付けのログイン（最後の試行から 90 日）、管理者が発行したリンク（期限から 30 日）、認可の要求・コード・アクセストークン・リフレッシュトークン（期限から 7 日）、トークンの系列と同意（終了か期限から 30 日）、client assertion の jti（期限）、承認されないままの CIMD のクライアント（30 日）、取込の staging（項目マッピングの保持期限）
- 消さないもの: 監査ログ、人事データ（退職者の匿名化は未実装。設計は §4.5）、紐付け・ロールの付与・ローカルアカウント・クライアントの登録と鍵、消去の記録（`retention_run`）
- DB の RLS でも「jin-ji 自身が、終わった行だけ」に絞っている。保持期間を短くしても、生きている系列のコードなど再利用の検知に要る行は消えない

## 認可と RLS

設計は [アーキテクチャ概要 §5](docs/architecture/overview.md#5-認可モデル公開範囲)（実装上の規則は §5.6）。

- 人の情報は `JinJi.Application` のサービス（`PersonDirectoryService`）を通し、`JinJi.Policy` の `PersonView`（見える項目だけを持つ投影）で返す。REST・MCP・UI はこれ以外の経路で人の情報を返さない
- DB アクセスは `JinJi.Infrastructure.Database.ViewerTransaction` でトランザクションを張り、閲覧者を `set_config(..., true)`（SET LOCAL 相当）で RLS に渡す。GUC は `jinji.viewer_kind` / `jinji.viewer_roles` / `jinji.viewer_person_id` / `jinji.viewer_api_client`（先行提供の日数を持つ社内システムの api_client.id。日数は DB が承認済みの行から読む）
- 閲覧者が未設定なら、どのテーブルも見えず書き込めない（fail closed。所有者 `jinji_owner` にも効く）。`psql` で中身を見るときは、同じトランザクションで先に設定する

  ```sql
  BEGIN;
  SELECT set_config('jinji.viewer_kind', 'system', true), set_config('jinji.viewer_roles', 'hr_admin', true);
  SELECT count(*) FROM person;
  COMMIT;
  ```

- RLS はアプリのバグに対する防波堤であり、GUC をアプリ自身が設定できる以上、侵害されたアプリへの防御ではない
- 組織の階層（段）は固定しない。「会社 > 事業部 > 部 > 課 > チーム」のような段の定義を法人ごと（無ければグループ共通の既定）に持ち、ポリシーでは課内・部内を `same_level(section)` のように段のキーで指定する。段の定義の変更も起案 → 別人の承認で反映する（`OrgLevelAdministrationService`。§5.6）
- 公開範囲は組織のルール（ポリシー）として決め、本人が自分の公開範囲を変える機能は持たない。個人単位の例外は人事管理者が起案し、別の人事管理者の承認で反映する（狭める方向だけ。対象者本人は起案・承認・廃止に関われない）
- ポリシーの追加も廃止も、起案 → 別人の承認で反映する（`PolicyAdministrationService`。DB の CHECK 制約・トリガー・RLS でも守る）
- 人事管理者以外への応答では、所属・一時停止の将来の終了日と、退職前の退職年月日を返さない（異動・復職・退職の予定を推測させない）
- 例外として、先行提供を承認された社内システム（client credentials）には、有効日の N 日前から将来の予定を見せる（`as_of` は業務日付 + N 日まで。その時点の人事管理者以外の閲覧者と同じに見せる）。線は RLS（`jinji_viewer_horizon()`）とアプリの両方で同じ日に引く。人の委任には効かない（§4・§9「先行提供」）
- 退職後の公開期間を過ぎた人は、人事管理者以外の一覧・参照に出さない（アプリだけで判定し、RLS では線を引かない。理由は §5.6）
- 今日の組織スナップショットはプロセス内にキャッシュする（閲覧者の区分ごと）。同一プロセスの書き込みは `IOrgDataChangeNotifier` で、他のプロセスの書き込みは DB の世代の定期確認で検知する
- テストでは、データの準備に限ってセッション単位の設定（`SampleData.ActAsHrAdminAsync`）を使う。アプリでは使わない（接続プールで次の利用者に引き継がれるため）

### 設定（appsettings.json / 環境変数）

| キー | 既定値 | 内容 |
| --- | --- | --- |
| `JinJi:Directory:RetiredPublicDays` | `90` | 退職者を人事管理者以外に公開する日数（退職年月日の翌日から数える） |
| `JinJi:Directory:MassActionThreshold` | `10` | 一斉異動とみなす発令の件数（同じ発令日の異動・役職の任免・兼務・出向）。メタ情報の時点の目盛りに使い、件数は返さない |
| `JinJi:Directory:PresenceVisibilityScope` | `same_level(department)` | 当日の所在（項目 `presence`。所在の提供を登録したときだけ）の公開範囲（関係スコープの表記。`none` は不可）。ポリシーでさらに狭められる |
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
| `JinJi:Webhooks:SubscriptionsPerRun` / `DeliveriesPerRun` / `Concurrency` / `MaxScanRows` / `CollectLease` / `DefaultRotationOverlap` | `20` / `50` / `4` / `100000` / `00:02:00` / `1.00:00:00` | 1 回の実行の量・同時に送る数・1 回の収集で読む行の上限・収集の貸し出し・鍵のローテーションで旧鍵でも署名する既定の期間。ワーカーを API と別のプロセスにするときは、ワーカーにも同じ `JinJi:DataProtection` の証明書と `JinJi:Api:CursorKey` を与える |
| `JinJi:Listen:MetricsPort` | `28100` | メトリクスのポート |
| `JinJi:Listen:MetricsAddress` | `127.0.0.1` | メトリクスの待ち受けアドレス |
| `JinJi:Auth:Issuers` / `Clients` / `ClockSkew` | 空 / 空 / `00:00:30` | 外部の発行者の JWT の発行者・クライアントの登録（上の「REST API」）。発行者が空なら JWT はすべて 401 |
| `JinJi:Auth:Server:Issuer` / `McpResource` | 未設定 / 未設定 | jin-ji のトークン発行（上の「トークン発行」）。Issuer が未設定なら無効 |
| `JinJi:Auth:Server:Cimd:DefaultMcpTools` | 空 | CIMD で登録したクライアントの MCP ツールの許可リスト（`*` はすべて） |
| `JinJi:Mcp:Enabled` | `true` | MCP の停止スイッチ（`false` で MCP のポートは `/healthz` 以外 503） |
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

環境変数では `JinJi__Directory__RetiredPublicDays=60` のように書く。不正な値（負の日数など）では起動しない。

## マイグレーション

- `db/migrations/NNNN_name.sql` を版番号順に適用する（`JinJi.Infrastructure` の `MigrationRunner`）。EF Core のマイグレーションは使わない
- 1 ファイル 1 トランザクション。ファイルの中に `BEGIN` / `COMMIT` を書かない
- 適用履歴は `schema_migration` に残り、適用済みのファイルはチェックサムで照合する。**適用済みのファイルは書き換えず**、新しいファイルを追加する
- SQL はアセンブリに埋め込まれるので、実行環境にファイルを置く必要はない

## ディレクトリ構成

```
jin-ji/
├── JinJi.slnx
├── Directory.Build.props        # net10.0・nullable・警告をエラー扱い・アナライザ
├── Directory.Packages.props     # Central Package Management・lock ファイル
├── global.json                  # SDK の版とテストランナー（Microsoft.Testing.Platform）
├── nuget.config                 # 取得元を nuget.org に固定・署名の検証
├── db/
│   ├── migrations/              # SQL マイグレーション（Infrastructure に埋め込む）
│   └── field-catalog.json       # 項目カタログ（全列の ADR-0002 分類）
├── deploy/compose/
│   ├── compose.yaml             # PostgreSQL（Compose プロジェクト名 jinji）
│   ├── .env.example             # パスワードの雛形（実体の .env はコミットしない）
│   └── initdb/                  # 初回起動時のロール作成
├── scripts/
│   ├── dotnet.sh                # .NET SDK をコンテナ内で実行するラッパー
│   └── pnpm.sh                  # pnpm（Node.js）をコンテナ内で実行するラッパー
├── src/
│   ├── JinJi.Domain/            # エンティティ・発令ロジック（外部依存なし）
│   ├── JinJi.Policy/            # 認可エンジン・マスキング
│   ├── JinJi.Application/       # ユースケース（API・MCP・ワーカーが共有）
│   ├── JinJi.Infrastructure/    # Npgsql・SQL マイグレーションのランナー
│   ├── JinJi.Api/               # REST API（クラスライブラリ。Host から登録）
│   ├── JinJi.Mcp/               # MCP サーバー（クラスライブラリ。Host から別ポートで登録）
│   ├── JinJi.Worker/            # 取込・同期・配信（ホスト型サービス。取込のジョブ）
│   ├── JinJi.Connectors/        # 取込コネクタ（CSV / xlsx・項目マッピング。ClosedXML を参照する唯一のプロジェクト）
│   ├── JinJi.Ingest/            # 許可リスト・正規化・名寄せ・提案の計画・LLM の問いの組み立てと検証（ワーカーだけが参照）
│   ├── JinJi.Llm/               # LLM の呼び出し（OpenAI 公式 SDK を参照する唯一のプロジェクト。ワーカーだけが参照）
│   ├── JinJi.Sync/              # Entra ID の同期計画の差分計算・PowerShell スクリプトと差分レポートの生成・実行ログの解釈・Graph REST の直接反映（既定は無効）
│   └── JinJi.Host/              # 唯一の実行可能プロジェクト（--role / migrate / import / reconcile-work-locations / evaluate-ingest / sync-plan / sync-script / sync-result / sync-apply / evaluate-sync）。Web/ に JWT の検証・入口の共通処理・レート制限・Prometheus の出力
├── tests/
│   └── JinJi.IntegrationTests/  # 実 PostgreSQL を使う結合テスト・構成のテスト
├── samples/
│   └── messy-corp/              # 整っていない人事データの架空サンプルと正解データ・項目マッピング
├── web/                         # Web UI（React SPA。デモデータは samples から生成）
└── docs/
    ├── adr/                     # 決定記録（ADR）
    ├── architecture/
    │   └── overview.md          # アーキテクチャ概要
    ├── integration/
    │   └── webhook-receiver.md  # 社内システム向け: Webhook の受け手の実装（署名の検証の例）
    └── reports/
        ├── ingest-accuracy.md   # 取込の精度（evaluate-ingest の出力）
        └── sync-plan.md         # 同期計画の評価（evaluate-sync の出力）
```

プロジェクト間の参照は設計どおりであることをテスト（`ProjectReferenceTests`）で確かめている。参照を増やすときは設計を見直し、テストの表も更新する。

## ライセンス

[Apache License 2.0](LICENSE)。脆弱性の報告は [SECURITY.md](SECURITY.md) を参照。
