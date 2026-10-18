# ADR-0001: 実装言語とランタイム

- ステータス: **承認**
- 日付: 2026-10-18
- 関連: [ADR-0003](0003-ingest-dependencies.md)・[ADR-0004](0004-authentication.md)・[ADR-0005](0005-llm-dependency.md)・[ADR-0006](0006-mcp-sdk.md)（個別の依存）、[アーキテクチャ概要](../architecture/overview.md)

## 決定

**バックエンドは C# / .NET 10 LTS（ASP.NET Core）、フロントエンドは TypeScript（React）とする。**

- API・MCP サーバー・ワーカー・管理 CLI はすべて C# で、同じドメイン層と認可エンジンを共有する。実行ファイルは 1 つ（`JinJi.Host`）で、起動時の役割（`--role all|api|mcp|worker`）で切り替える
- フロントエンドの npm 依存はビルド時に限定し、成果物は静的ファイルとして API の役割から配信する。サーバーの実行プロセスに Node.js は置かない
- UI の型は OpenAPI 定義から生成する（手書きで二重定義しない）
- 依存は下記の「サプライチェーン方針」に従い、標準機能で済むものは入れない

## 背景

jin-ji は日本向けの人事情報基盤で、価値の中心は「人事情報を AI Ready にする」こと（MCP / AI エージェント経由で安全に操作・公開する）にある。全社員の個人情報を扱い、AI による API への自動攻撃も想定する。開発・保守の大半は AI コーディングエージェントが担う。

## 検討した選択肢

| 案 | 概要 |
| --- | --- |
| A. TypeScript 統一 | Node.js バックエンド + React。単一言語・単一ツールチェーン |
| **B. C# + TypeScript** | ASP.NET Core バックエンド + React |
| C. Python + TypeScript | FastAPI バックエンド + React |

### 評価

5 段階（5 が最良）。合計は 105 点満点。

| 観点 | 重み | C# | TS 統一 | Python |
| --- | --- | --- | --- | --- |
| コンパイラ・型による自己検証（AI 生成コードの誤りを機械的に弾く） | 3 | 5 | 4 | 2.5 |
| 認証・レート制限・計測等のファーストパーティ度 | 3 | 5 | 3.5 | 3 |
| サプライチェーン露出 | 3 | 4.5 | 2 | 2.5 |
| AI エージェントの開発速度（学習データ量、反復速度、単一ツールチェーン） | 3 | 3.5 | 4.5 | 4.5 |
| MCP SDK の成熟度・仕様追従 | 2 | 4 | 5 | 4 |
| 日本語正規化・Excel 取込・名寄せのライブラリ | 2 | 3 | 3 | 5 |
| フロントとの型共有・言語統一 | 2 | 2.5 | 5 | 2.5 |
| 長期安定性（LTS 期間、破壊的変更の頻度） | 2 | 5 | 3 | 3.5 |
| PostgreSQL 高度機能（RLS、範囲型、ltree）へのアクセス | 1 | 4 | 3.5 | 4 |
| **加重合計** | | **87** | **77.5** | **71.5** |

### サプライチェーンの実測

比較のため、同等機能（DB、OIDC、MCP、OpenAI 互換、OpenTelemetry、Microsoft Graph、パスキー、Excel 取込）を揃えたサーバー側構成を、依存解決のみで計測した（2026-10-11）。

| 構成 | 推移的依存を含む総数 | 備考 |
| --- | --- | --- |
| C# / NuGet | 91 | 62 が Microsoft・System・Azure・OpenTelemetry・Npgsql の発行元。第三者は 8 系統 29 件 |
| TS / npm | 475 | exceljs 173、MCP SDK 92、OpenTelemetry 62 |
| Python / PyPI | 102 | |

フロントエンド（npm、約 230）とそのビルドツール（約 250）は、どの案でも共通。

実際の構成では、この比較の構成からさらに減らしている。Microsoft.Identity.Web は使わず標準の OIDC ハンドラにし（ADR-0004）、Microsoft Graph は SDK を入れず HttpClient の最小実装にし（[概要 §8](../architecture/overview.md#8-取り込み名寄せ同期)）、メトリクスは OpenTelemetry を入れず標準の System.Diagnostics.Metrics と自前の Prometheus 出力にした（[概要 §7.2](../architecture/overview.md#72-rest-api-の実装の規則)）。

## 理由

1. **個人情報と DB 資格情報を持つプロセスの第三者依存を最小化できる。** npm では 2025〜2026 年に乗っ取り型の攻撃が繰り返され、正規の provenance 付きで悪性版が公開された例もある。C# 案では、実行時プロセスを npm エコシステムから切り離せる。
2. **セキュリティ要件の多くが標準機能で満たせる。** RateLimiter、Data Protection、OIDC / JWT Bearer、パスキー・TOTP（ASP.NET Core Identity）、System.Diagnostics.Metrics、OpenAPI 文書の生成が、共有フレームワークか Microsoft 発行の小さなパッケージで揃う。
3. **AI 開発の継続性。** nullable 参照型・Roslyn アナライザによるコンパイル時検証、LTS 36 か月、フレームワークの破壊的変更が少ない。モデルや担当者が交代しても、型とプロジェクト境界がドキュメントの代わりになる。
4. TS 統一案の主な利点（型共有）は、OpenAPI からの型生成でほぼ代替できる。API と MCP は同じ C# のドメイン層を通るため、「API では隠したが MCP では漏れた」という分断は起きない。

AI エージェントによる開発スピードと継続性を最重視した場合でも、順位は変わらなかった（C# と TS の差は縮まる）。スピードでは TS が、継続性では C# が勝る。本プロジェクトは長期運用と安全性を優先する。

## 帰結

### 良い点

- 実行時の依存が少なく、その大半が大手の発行元（下記「実行時の依存」）
- ASP.NET Core の標準機能で、レート制限・認証・計測を一貫した作法で書ける
- 日本の企業・SIer での受容性が高い

### 注意点と対処

| 注意点 | 対処 |
| --- | --- |
| 二言語・二ツールチェーン（dotnet / pnpm） | フロントは「OpenAPI の利用者」に徹し、ドメインロジックを持たせない。どちらもリポジトリの中の設定と lock ファイルで再現できるようにする |
| ビルドの反復が TS より遅い | `dotnet watch`、増分ビルドの維持 |
| 日本語の正規化・名寄せのライブラリが少ない | NFKC は標準機能。異体字・かな変換は小さな自前テーブルで実装し、テストで固める |
| Excel 取込ライブラリ（ClosedXML）が依存の大きな部分を占める | 取込処理（ワーカー側）にのみ参照させ、API / MCP のプロジェクトには入れない（ADR-0003） |
| npm 依存はフロントに残る | 下記のサプライチェーン方針で対処 |

## 実行時の依存

NuGet の実行時の依存は次だけ。版は `Directory.Packages.props` で固定する。

| パッケージ | 参照するプロジェクト | 決定 |
| --- | --- | --- |
| Npgsql | Infrastructure | 本 ADR（DB は PostgreSQL のみ。EF Core は使わない） |
| Microsoft.AspNetCore.OpenApi | Api | 本 ADR（OpenAPI 3.1 の文書の生成。Microsoft 発行） |
| Microsoft.AspNetCore.Authentication.OpenIdConnect・JwtBearer | Host | ADR-0004 |
| ClosedXML | Connectors（ワーカー側） | ADR-0003 |
| OpenAI | Llm（ワーカー側） | ADR-0005 |
| ModelContextProtocol.AspNetCore | Mcp | ADR-0006 |

依存を足すときは、ADR かレビューで理由と推移的依存の一覧を記録し、参照するプロジェクトをテスト（`ProjectReferenceTests`）で固定する。

## フロントエンドの構成

- React + Vite + TypeScript の SPA（`web/`）。ルーティングはハッシュの自前実装、データは取得元（`DirectoryAdapter`・`AuthAdapter`）を通して読む
- 直接依存は版を完全に固定する。標準機能や数十行の自前実装で済むものは入れない（UI 部品のライブラリ、ルーター・データ取得のライブラリ、OpenAPI の型生成ツールは入れず、型は `web/scripts/gen-api-types.mjs` で生成する）
- 認証は BFF（HttpOnly Cookie のセッション）で、ブラウザにアクセストークンを持たせない（ADR-0004）
- フォントはセルフホストし、外部 CDN は使わない。配信時の CSP は `default-src 'self'`・`style-src 'self'`（`'unsafe-inline'` なし）を前提にコードを書く
- 架空データで動くデモ（デモアダプタとデモデータ）は `--mode demo` のビルドと開発サーバーにだけ含める。本番ビルドからはビルド時の定数で取り除き、含まれていないことをビルドの後に検査する

## サプライチェーン方針

言語に関係なく適用する。

| 対策 | NuGet | npm（フロント） |
| --- | --- | --- |
| バージョン固定 | Central Package Management + `packages.lock.json` + locked mode | `pnpm-lock.yaml` + `--frozen-lockfile`、直接依存は完全な版で指定 |
| 公開直後の版を入れない | 公開から 7 日以上経った版を使う（更新は Renovate の `minimumReleaseAge` 7 日） | pnpm `minimumReleaseAge`（7 日）+ Renovate |
| 取得元の固定 | Package Source Mapping（nuget.org のみ）、署名の検証を必須にする | 公式レジストリのみ、`trustPolicy: no-downgrade`、レジストリ以外からの推移的依存を拒否 |
| インストール時のコード実行 | ビルドは開発コンテナ内に限定（`.targets` 対策） | lifecycle script は既定拒否、許可は明示列挙 |
| 脆弱性・悪性の検知 | NuGetAudit（警告はエラー扱い）、SBOM（CycloneDX） | OSV-Scanner、SBOM |
| 依存の追加 | ADR かレビューで理由を記録。標準機能で済むものは入れない | 同左 |

- provenance や署名の検証は**必要条件であって十分条件ではない**（正規の公開経路で悪性版が出た事例がある）
- 依存のインストール・ビルドは、ホストの資格情報を持たない開発コンテナ内で行う（`scripts/dotnet.sh`・`scripts/pnpm.sh`。マウントするのはリポジトリだけ）。AI エージェントも同じ環境で動かす
- CI ではフロントのビルドジョブとバックエンドのジョブを分け、フロントのジョブに DB・Graph・クラウドの資格情報を渡さない
- 本番コンテナは非 root・読み取り専用ファイルシステム・**外向き通信の許可リスト**で動かす。許可するのは DB、IdP、LLM の接続先（ADR-0005）、CIMD の取得先（管理者が許可したドメイン。ADR-0004）、Webhook・修正依頼の通知の送信先（社内システム。[概要 §9](../architecture/overview.md#9-社内システム連携下流)）、Microsoft Graph（直接反映を有効にした場合だけ）。送信は標準の HttpClient だけで行い、依存を追加しない
- 本番の SPA には厳格な CSP（`connect-src 'self'`、外部 CDN 不使用）を設定する
- 緊急の脆弱性修正は、人が差分を確認したうえで待機期間の例外とする
- CI・Renovate・SBOM の生成は、リポジトリのホスティングに合わせて設定する（lock ファイル・locked mode・pnpm の設定は、それに先立ってリポジトリに入れてある）
