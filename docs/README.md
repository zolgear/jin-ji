# jin-ji のドキュメント

jin-ji のドキュメントの目次。プロジェクトの紹介は [リポジトリの README](../README.md)。

## 利用者向け

| 文書 | 内容 |
| --- | --- |
| [Web UI](web-ui.md) | 画面（組織図・3D 俯瞰・人の詳細・検索・修正依頼・ログイン・同意・アカウント・認証の管理）と表示の規則 |
| [Web UI のデモ](getting-started.md#web-ui-のデモ) | 架空のデータで動くデモを、サーバーや DB なしで試す |

## 開発者向け

| 文書 | 内容 |
| --- | --- |
| [はじめに](getting-started.md) | 開発環境の前提、PostgreSQL・マイグレーション・サーバーの起動、ビルドとテスト、Web UI の開発のコマンド |
| [開発ガイド](development.md) | ディレクトリ構成、プロジェクト間の参照、依存パッケージの方針（NuGet・npm）、データの扱い、マイグレーション、認可と RLS の実装の約束 |
| [Web UI](web-ui.md#api-とデータの取得元) | UI の取得元（API アダプタ・デモアダプタ）、型の生成、CSP、認証の扱い |

## 運用者向け

| 文書 | 内容 |
| --- | --- |
| [運用ガイド](operations.md) | 役割・ポート・待ち受け、設定の一覧（appsettings.json / 環境変数）、人のログインとトークン発行の管理 CLI、Web UI の配信とセキュリティヘッダー、メトリクス、保持期限の消去 |
| [取り込みと修正依頼](ingest.md) | 取込の CLI と項目マッピング、拠点の取り込み、要確認の解決、LLM による候補提示、取込の精度の評価、修正依頼 |
| [Entra ID への同期](entra-sync.md) | 同期計画・承認・スクリプト出力・実行ログの照合、同期の設定、直接反映 |

## API

| 文書 | 内容 |
| --- | --- |
| [API 仕様書](api.md) | REST API のエンドポイント・認証とスコープ・共通の規則（時点・ページング・エラー・キャッシュ・レート制限）、認証と管理の JSON API、Webhook、MCP のツール、当日の所在の提供（拡張点）。正は OpenAPI の文書（`web/openapi/v1.json`） |

## 連携

| 文書 | 内容 |
| --- | --- |
| [社内システムからの使い方](api.md#7-社内システムからの使い方) | 勤怠・ワークフローなどの社内システムの典型的な呼び出し順、承認ルート、先行提供 |
| [Webhook の受け手の実装](integration/webhook-receiver.md) | 社内システムの開発者向け。署名の検証・重複の除き方・要約の扱い |
| [当日の所在の提供（拡張点）](api.md#当日の所在の提供拡張点) | 席の予約・入退館を持たない jin-ji に、外部の所在の提供を注入する `IPresenceProvider` の登録と認可 |
| [MCP クライアントの接続](api.md#クライアントの接続) | Claude Code・VS Code などの AI エージェントからの接続 |

## 設計

| 文書 | 内容 |
| --- | --- |
| [アーキテクチャ概要](architecture/overview.md) | 設計の全体と、各機能の実装の規則。実装の状況は [§12](architecture/overview.md#12-実装の状況) |
| [建物・フロアモードの設計](design/building-floor-mode.md) | 建物・階・座席を扱う 3D 表示の設計（検討中） |

### 決定記録（ADR）

| ADR | タイトル | ステータス |
| --- | --- | --- |
| [0001](adr/0001-language-and-runtime.md) | 実装言語とランタイム | 承認 |
| [0002](adr/0002-data-scope.md) | 扱うデータの範囲と、システムの位置づけ | 承認 |
| [0003](adr/0003-ingest-dependencies.md) | 取り込みの依存パッケージ（ClosedXML） | 承認 |
| [0004](adr/0004-authentication.md) | 認証とトークン発行の実現方式 | 承認 |
| [0005](adr/0005-llm-dependency.md) | LLM の呼び出しの依存パッケージ（OpenAI 公式 .NET SDK） | 承認 |
| [0006](adr/0006-mcp-sdk.md) | MCP サーバーの依存パッケージ（公式 C# SDK） | 承認 |
| [0007](adr/0007-bitemporal-model.md) | 二つの時間軸（bitemporal）と、発令を起点にした変更 | 承認 |
| [0008](adr/0008-authorization-model.md) | 認可モデル（全社公開を既定に狭める、アプリと RLS の二重の線） | 承認 |
| [0009](adr/0009-agent-writes-hitl.md) | AI エージェントによる書き込み（公開に至る変更は人が承認する） | 承認 |
| [0010](adr/0010-entra-sync.md) | Entra ID への反映（スクリプト出力を主、直接反映は既定で無効） | 承認 |
| [0011](adr/0011-webhook.md) | 社内システムへの変更の通知（変更フィードと Webhook） | 承認 |
| [0012](adr/0012-workplace-and-seat-data.md) | 建物・階・座席と、人の決まった席・持ち場の扱い | 提案 |
| [0013](adr/0013-field-managed-changes.md) | 現場・IT 部門による組織と人の情報の直接登録と素案（シミュレーション） | 提案 |

ADR を足したら、この表に行を足す。

## レポート

| 文書 | 内容 |
| --- | --- |
| [取込の精度](reports/ingest-accuracy.md) | 架空のサンプルデータでの取込の精度（`evaluate-ingest` の出力） |
| [同期計画の評価](reports/sync-plan.md) | 架空のサンプルデータでの Entra ID の同期計画の評価（`evaluate-sync` の出力） |
