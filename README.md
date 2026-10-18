# jin-ji

日本企業向けの人事情報基盤。既存の人事パッケージと社内システム群の中間に立ち、**全社公開レベルの人事情報だけ**を取り込み・整え、社内システムと AI エージェントへ配る。

人事情報を MCP / AI エージェントから安全に扱える「AI Ready」にすることが目的で、ERP や人事情報の正（System of Record）ではない。

## 特徴

- **機微情報を持たない**: マイナンバー・生年月日・性別・住所・給与・評価などは取り込みの入口で捨て、DB に列も作らない。休職は理由を持たない「一時停止」として扱う
- **AI も人も同じ入口・同じ権限判定**: Web UI・REST API・MCP サーバーが同じドメイン層と認可エンジンを通る。公開範囲は全社公開を既定に、項目・組織・個人の単位で狭める。PostgreSQL の行単位セキュリティ（RLS）を防波堤として重ねる
- **公開に至る変更は人が承認する**: AI エージェントは作業領域（名寄せの候補・推定）に書けるが、公開される情報を変えるのは人の承認だけ
- **二つの時間軸と内示の秘匿**: 「ある日時点の組織・所属・上長」を引ける。発令日前の異動は人事管理者以外に見せない
- **整っていないデータの取り込み**: CSV / xlsx / Entra ID のユーザー一覧を、項目マッピング・日本語の正規化（異体字・かな・和暦）・決定的な名寄せで変更提案にする。残りは任意で LLM が候補を示す
- **社内システム連携**: 時点指定の REST API、変更フィード、ID だけを載せた Webhook、承認ルートの API
- **Entra ID への反映はスクリプトが主**: 同期計画と PowerShell スクリプトを出し、管理者が実行する。直接反映は既定で無効
- **認証は既存の IdP に委ねる**: Entra ID・汎用 OIDC・ローカルアカウント（パスキー・TOTP）。API・MCP 向けのトークンは最小の認可サーバーが発行する
- **小さく運用できる**: 1 つの実行ファイル + PostgreSQL。依存は最小限

## 使われ方

| だれが | 何をする |
| --- | --- |
| 社員 | Web UI で組織図・3D 俯瞰・人の検索を見る。値の誤りに気づいたら修正依頼を出す |
| 人事管理者 | 人事パッケージなどのデータを取り込み、変更提案と要確認を確かめて承認する。公開範囲のポリシーを決める |
| AI エージェント | MCP サーバーを通して、委任元の社員と同じ権限で人事情報を参照する。修正依頼の起票や要確認への推定はできるが、公開される情報は変えない |
| 社内システム | 勤怠・承認ワークフローなどが REST API・変更フィード・Webhook で組織・所属・上長・承認ルートを引く |
| 情報システム部門 | 同期計画から出た PowerShell スクリプトで Entra ID のユーザーを更新する |

## 構成

| 部分 | 技術 |
| --- | --- |
| サーバー（REST API・MCP・ワーカー・管理 CLI） | C# / .NET 10（ASP.NET Core）。1 つの実行ファイルを役割（`all` / `api` / `mcp` / `worker`）で切り替える |
| DB | PostgreSQL 17 以上 |
| Web UI | React + Vite + TypeScript の SPA（組織図・3D 俯瞰・検索・修正依頼・認証の画面） |
| サンプルデータ | 架空の「整っていない」会社の人事データと正解データ（`samples/messy-corp/`） |

ビルド・テスト・依存のインストールはすべて Docker のコンテナ内で行い、ホストに .NET SDK や Node.js は要らない。

## すぐ試す: Web UI のデモ

架空のデータで動くデモ版の UI を、サーバーや DB なしで動かせる。必要なのは Docker だけ。

```sh
scripts/pnpm.sh install --frozen-lockfile
JINJI_PNPM_PORT=28000 JINJI_PNPM_NAME=jinji-pnpm-dev scripts/pnpm.sh dev
# → http://127.0.0.1:28000/ （組織図・3D 俯瞰・検索・修正依頼。ログインの画面のデモは demo / 任意 / 123456）
docker stop jinji-pnpm-dev   # 終わったら止める
```

サーバーと PostgreSQL を動かす手順は [はじめに](docs/getting-started.md)。

## ドキュメント

ドキュメントの目次は **[docs/README.md](docs/README.md)**。

- [はじめに](docs/getting-started.md): 開発環境・サーバーの起動・テスト
- [API 仕様書](docs/api.md): REST API・Webhook・MCP のツール
- [運用ガイド](docs/operations.md): 設定・ポート・管理 CLI・保守
- [アーキテクチャ概要](docs/architecture/overview.md): 設計の全体

## 状況

開発中。サーバー（取り込み・認可・REST API・MCP・Webhook・Entra ID の同期計画）と Web UI の主な画面は動くが、本番での運用の前に残っている作業がある。実装の状況は [アーキテクチャ概要 §12](docs/architecture/overview.md#12-実装の状況)。

## ライセンス

[Apache License 2.0](LICENSE)。脆弱性の報告は [SECURITY.md](SECURITY.md) を参照。
