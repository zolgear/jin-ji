# はじめに: 開発環境とデモ

jin-ji を手元で動かす手順。Web UI のデモ、開発環境でのサーバーの起動、ビルドとテスト、Web UI の開発のコマンドを扱う。

ビルド・テスト・依存のインストールはすべて Docker のコンテナ内で行い、ホストに .NET SDK や Node.js は要らない（[ADR-0001](adr/0001-language-and-runtime.md) のサプライチェーン方針）。

## 目次

- [Web UI のデモ](#web-ui-のデモ)
- [開発環境の前提](#開発環境の前提)
- [サーバーを動かす](#サーバーを動かす)
- [Web UI の開発](#web-ui-の開発)
- [次に読むもの](#次に読むもの)

## Web UI のデモ

架空のデータ（`samples/messy-corp` から生成）で動くデモ版の UI を、サーバーや DB なしで動かせる。必要なのは Docker だけ。

```sh
scripts/pnpm.sh install --frozen-lockfile
JINJI_PNPM_PORT=28000 JINJI_PNPM_NAME=jinji-pnpm-dev scripts/pnpm.sh dev
# → http://127.0.0.1:28000/ （組織図・3D 俯瞰・検索・修正依頼。ログインの画面のデモは demo / 任意 / 123456）
docker stop jinji-pnpm-dev   # 終わったら止める
```

デモのコードとデータは `--mode demo` のビルドと開発サーバーにだけ含まれ、本番ビルドには入らない（[Web UI](web-ui.md#api-とデータの取得元)）。

## 開発環境の前提

- Docker（Compose v2）。arm64 / amd64 のどちらでも動く
- ホストに .NET SDK は不要。ビルド・テストは `scripts/dotnet.sh` が `mcr.microsoft.com/dotnet/sdk:10.0` のコンテナ内で実行する
  - マウントするのはリポジトリだけ。ホストのホームディレクトリや資格情報はコンテナに渡さない
  - HOME と NuGet のキャッシュは名前付きボリューム `jinji_dotnet_home` / `jinji_nuget` に置く
  - Compose のネットワーク `jinji_default` があれば参加し、PostgreSQL に `postgres:5432` で接続する
  - リポジトリ直下の `.env`（外部 API キー）はコンテナに渡さない。必要なとき（実 LLM の評価）だけ `JINJI_DOTNET_PASS_ENV=<環境変数名>` で名前を指定して渡す（既定は何も渡さない。値はコマンドラインに現れない）
- 使うポート（すべて 127.0.0.1 のみ）

  | 用途 | ポート |
  | --- | --- |
  | PostgreSQL | 25432 |
  | API | 28080 |
  | MCP | 28090 |
  | メトリクス（Prometheus） | 28100 |
  | Web UI の開発サーバー | 28000 |

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
- 二度目以降は未適用のファイルだけを適用する。マイグレーションの書き方は [開発ガイド](development.md#マイグレーション)

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

- API を変えたら、OpenAPI の文書と UI の型を作り直す（[API 仕様書](api.md#再生成の手順)）

### 4. サーバーを起動する（任意）

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- --role all --JinJi:DataProtection:AllowUnprotectedKeys=true
```

- 役割は `all`（既定）/ `api` / `mcp` / `worker`。環境変数 `JINJI_ROLE` でも指定できる
- 役割 `all` / `api` は Data Protection の鍵（Cookie・CSRF・TOTP の鍵の保護）を DB に保存し、証明書（`JinJi:DataProtection:Certificate`）で暗号化する。証明書が無ければ起動しない。開発で証明書なしにするときだけ、上のように `AllowUnprotectedKeys=true` を明示する（起動時に警告する）
- 役割 `all` / `api` では DB の接続文字列（`ConnectionStrings:App`、未設定なら開発用に `JINJI_PG_APP_PASSWORD` などから組み立てる）が必須で、無ければ起動時に止まる
- API は 28080、MCP は 28090、メトリクスは 28100 で待ち受ける。エンドポイントは [API 仕様書](api.md)、待ち受けの設定は [運用ガイド](operations.md#ポートと待ち受け)
- `scripts/dotnet.sh` はポートを公開しないため、上のコマンドで起動したサーバーにはホストから到達できない。ポートの振り分けは `HostSurfaceTests` で確かめている。アプリのコンテナイメージと Compose のサービス定義はまだ無い

### 5. データを取り込む・同期を試す（任意）

- サンプルの取り込み・要確認の解決・取込の精度の評価: [取り込みと修正依頼](ingest.md)
- Entra ID への同期計画とスクリプト出力: [Entra ID への同期](entra-sync.md)
- 人事管理者のロールの付与・ローカルアカウントの作成などの管理 CLI: [運用ガイド](operations.md#人のログイン)

## Web UI の開発

React + Vite + TypeScript の SPA（`web/`）。画面と実装の規則は [Web UI](web-ui.md)。

npm 依存のインストール・ビルドはホストで行わない（ADR-0001）。`scripts/pnpm.sh` が公式イメージ `node:24.20.0-bookworm-slim`（ダイジェスト固定）の中で pnpm（`web/package.json` の `packageManager` で版とハッシュを固定し、corepack が取得）を実行する。マウントするのはリポジトリだけで、ホストのホームディレクトリ・資格情報（`~/.npmrc` 等）は渡さない。HOME と pnpm のストアは名前付きボリューム `jinji_pnpm_home` / `jinji_pnpm_store`、ホストと同じ UID/GID、`--cap-drop ALL`。依存の方針は [開発ガイド](development.md#npmweb)。

```sh
scripts/pnpm.sh install --frozen-lockfile   # lock ファイルどおりに入れる
scripts/pnpm.sh typecheck                   # tsc（strict）
scripts/pnpm.sh lint                        # oxlint（警告もエラー扱い）
scripts/pnpm.sh test                        # Vitest
scripts/pnpm.sh build                       # 本番ビルド（web/dist。デモのコード・データが入っていないことも検査する）
scripts/pnpm.sh build:demo                  # デモ用ビルド（web/dist-demo）
scripts/pnpm.sh gen:api                     # OpenAPI の文書から API クライアントの型を生成する
scripts/pnpm.sh gen:demo                    # samples/messy-corp からデモデータを生成する

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

本番ビルドの成果物は API のサーバーが配信する（[運用ガイド](operations.md#web-ui-の配信)）。

## 次に読むもの

- [ドキュメントの目次](README.md)
- [開発ガイド](development.md): ディレクトリ構成・依存の方針・コーディングの規則
- [アーキテクチャ概要](architecture/overview.md): 設計の全体
