# 開発ガイド

jin-ji のコードに手を入れる人向けの規則。ディレクトリ構成とプロジェクト間の参照、依存の方針、データの扱い、マイグレーション、認可と RLS の実装の約束を扱う。環境の準備とビルド・テストの動かし方は [はじめに](getting-started.md)、設計の全体は [アーキテクチャ概要](architecture/overview.md)。

## 目次

- [ディレクトリ構成](#ディレクトリ構成)
- [プロジェクト間の参照](#プロジェクト間の参照)
- [依存パッケージの方針](#依存パッケージの方針)
- [データの扱い](#データの扱い)
- [マイグレーション](#マイグレーション)
- [認可と RLS](#認可と-rls)
- [API の変更](#api-の変更)

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
│   └── JinJi.Host/              # 唯一の実行可能プロジェクト（--role / migrate / import / reconcile-work-locations / evaluate-ingest / sync-plan / sync-script / sync-result / sync-apply / evaluate-sync と管理 CLI）。Web/ に JWT の検証・入口の共通処理・レート制限・Prometheus の出力
├── tests/
│   └── JinJi.IntegrationTests/  # 実 PostgreSQL を使う結合テスト・構成のテスト
├── samples/
│   └── messy-corp/              # 整っていない人事データの架空サンプルと正解データ・項目マッピング
├── web/                         # Web UI（React SPA。デモデータは samples から生成）
│   └── openapi/v1.json          # 保存した OpenAPI の文書（UI の型の元。API 仕様書の正）
└── docs/                        # ドキュメント（目次は docs/README.md）
```

## プロジェクト間の参照

プロジェクト間の参照は設計どおりであることをテスト（`ProjectReferenceTests`）で確かめている。参照を増やすときは設計を見直し、テストの表も更新する。

- 人の情報を返すのは `JinJi.Application` のサービスだけ（[認可と RLS](#認可と-rls)）。REST・MCP・UI はこれ以外の経路で人の情報を返さない
- ClosedXML は `JinJi.Connectors` だけ、OpenAI 公式 SDK は `JinJi.Llm` だけが参照する。API / MCP からは推移的にも参照しない（理由は [ADR-0003](adr/0003-ingest-dependencies.md)・[ADR-0005](adr/0005-llm-dependency.md)）

## 依存パッケージの方針

依存の追加は [ADR-0001 のサプライチェーン方針](adr/0001-language-and-runtime.md#サプライチェーン方針) に従う。標準機能で済むものは入れない。

### NuGet（サーバー）

- バージョンは `Directory.Packages.props`（Central Package Management）で一元管理し、各プロジェクトの `packages.lock.json` をコミットする
- CI では `-p:ContinuousIntegrationBuild=true`（または `-p:JINJI_LOCKED_RESTORE=true`）で locked mode にし、lock ファイルと異なる復元を失敗させる
- 取得元は nuget.org のみ（`nuget.config` の packageSourceMapping）。署名の検証を必須にしている
- 依存の追加は ADR かレビューで理由を記録する。公開から 7 日以上経った版を使う
- 取込の xlsx の読み込みに ClosedXML（`JinJi.Connectors` だけ。理由と推移的依存は ADR-0003）
- LLM の呼び出しに OpenAI 公式 .NET SDK `OpenAI` 2.14.0（JinJi.Llm だけ。推移的に Microsoft 発行の `System.ClientModel` 1.15.0・`System.Memory.Data`・`Microsoft.Extensions.*.Abstractions` 10.0.9）。API / MCP からは推移的にも参照しない（`ProjectReferenceTests`。理由と一覧は ADR-0005）
- MCP サーバーに公式 C# SDK `ModelContextProtocol.AspNetCore` 2.2.0（JinJi.Mcp だけ。推移的に同じ発行元の `ModelContextProtocol`・`ModelContextProtocol.Core` 2.2.0 と Microsoft 発行の `Microsoft.Extensions.AI.Abstractions` 10.8.3）。使うのはトランスポートだけで、認証・ツールの判定・監査は jin-ji 側（理由と一覧は [ADR-0006](adr/0006-mcp-sdk.md)）
- その他の依存（どれも Microsoft 発行・MIT）: `Microsoft.AspNetCore.OpenApi` 10.0.12（JinJi.Api。推移的に `Microsoft.OpenApi` 2.12.0）、`Microsoft.AspNetCore.Authentication.JwtBearer`・`Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.12（JinJi.Host。ADR-0004。推移的に `Microsoft.IdentityModel.*` / `System.IdentityModel.Tokens.Jwt` 8.19.2、`Microsoft.Bcl.Cryptography` 10.0.2）。メトリクスは標準の System.Diagnostics.Metrics と自前の Prometheus 出力で、OpenTelemetry は入れていない（理由は [概要 §7.2](architecture/overview.md#72-rest-api-の実装の規則)）

### npm（web/）

- 設定は `web/pnpm-workspace.yaml` と `web/.npmrc`: 公開から 7 日未満の版を解決しない（`minimumReleaseAge: 10080`）、`trustPolicy: no-downgrade`、lifecycle script は既定で拒否（`onlyBuiltDependencies: []`、許可していないビルドの要求は `strictDepBuilds` で失敗）、レジストリ以外からの推移的依存を拒否（`blockExoticSubdeps`）、取得元は `registry.npmjs.org` のみ、`engine-strict`・`strict-peer-dependencies`
- 直接依存は版を完全に固定する（範囲指定しない）。依存を足すときは理由をレビューで記録し、標準機能や数十行の自前実装で済むものは入れない
- ネイティブバイナリ（rolldown・lightningcss・@tailwindcss/oxide・TypeScript 7・oxlint）は OS / CPU ごとの optionalDependencies で配られ、install script を要しない。許可している lifecycle script は無い
- 入れていないもの: TanStack Router / Query（画面が少なく、ハッシュのルーターと小さなキャッシュで足りる。2026-05 の TanStack の汚染事件もあり、必要になった時点で版を固定して検討する）、Radix（今はネイティブ要素で足りる）、openapi-typescript（TypeScript の JS API と多数の推移的依存を要するため、`web/scripts/gen-api-types.mjs` の小さな変換で型を生成）、ESLint（oxlint で代替）、drei（OrbitControls は three 本体のものを使う）

## データの扱い

[ADR-0002](adr/0002-data-scope.md) により、**全社公開レベルの情報だけを保持する**。保持しない項目（生年月日、性別、住所、給与・等級、評価、休職理由など）の列は作らない。

- 全テーブルの全列を `db/field-catalog.json` に ADR-0002 の分類で登録する。DB とカタログが食い違うとテストが失敗する
- 保持しない項目を表す名前（`birth`、`gender`、`address`、`salary`、`grade`、`my_number` など）がテーブル名・列名に現れるとテストが失敗する
  - 例外（`ip_address` など、住所ではない語の並び）は `tests/JinJi.IntegrationTests/Support/ForbiddenNames.cs` の `Exceptions` に理由付きで列挙する。語の境界で完全に一致したときだけ外れ、他の禁止規則は外れない
- 列を追加するときは、マイグレーションとカタログを同じ変更で更新する

## マイグレーション

- `db/migrations/NNNN_name.sql` を版番号順に適用する（`JinJi.Infrastructure` の `MigrationRunner`）。EF Core のマイグレーションは使わない
- 1 ファイル 1 トランザクション。ファイルの中に `BEGIN` / `COMMIT` を書かない
- 適用履歴は `schema_migration` に残り、適用済みのファイルはチェックサムで照合する。**適用済みのファイルは書き換えず**、新しいファイルを追加する
- SQL はアセンブリに埋め込まれるので、実行環境にファイルを置く必要はない
- 適用の手順は [運用ガイド](operations.md#マイグレーションの適用)

## 認可と RLS

設計は [概要 §5](architecture/overview.md#5-認可モデル公開範囲)（実装上の規則は §5.6）、決定は [ADR-0008](adr/0008-authorization-model.md)。

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

## API の変更

- REST API・MCP のツールの仕様は [API 仕様書](api.md)。正は OpenAPI の文書（`web/openapi/v1.json`）
- エンドポイントや応答の型を変えたら、OpenAPI の文書と UI の型を作り直す（[API 仕様書の再生成の手順](api.md#再生成の手順)）
- 応答の型の規則（全項目 required・null の扱い・enum の宣言）は `JinJi.Api.OpenApiConventions` が担い、文書と応答の一致はテストで確かめる
