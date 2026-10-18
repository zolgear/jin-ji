# ADR-0006: MCP サーバーの依存パッケージ（公式 C# SDK）

- ステータス: **承認**
- 日付: 2026-10-18
- 関連: [ADR-0001](0001-language-and-runtime.md)（サプライチェーン方針）、[ADR-0003](0003-ingest-dependencies.md)、[ADR-0004](0004-authentication.md)、[ADR-0005](0005-llm-dependency.md)、[アーキテクチャ概要 §2・§3・§6.5・§7](../architecture/overview.md#7-apimcp-のセキュリティ)

## 決定

MCP サーバーのトランスポートに、**公式 C# SDK `ModelContextProtocol.AspNetCore` 2.2.0** を使う。参照するのは **JinJi.Mcp だけ**とし、JinJi.Api・ワーカー側のプロジェクトからは参照させない。

SDK に任せるのはプロトコルの層だけにし、認証・認可・ツールの判定・監査は jin-ji の既存の仕組みで持つ。

| 層 | 担当 | 備考 |
| --- | --- | --- |
| Streamable HTTP・JSON-RPC 2.0・プロトコルの版の取り決め（initialize / 2026-07-28 版の要求ごとのメタデータ）・`MCP-Protocol-Version` などのヘッダーの検査 | **SDK** | セッションを持たない（`HttpServerSessionMode.Stateless`） |
| 認証（参照トークン）・401 の challenge・保護リソースメタデータ | jin-ji（ADR-0004 の参照トークンの `jinji.token` スキーム） | SDK の認証ハンドラ（`McpAuthenticationHandler`）は使わない |
| 閲覧者・一時停止・スコープ（ツールごと）・返却人数・レート制限・不正検知・メトリクス | jin-ji（入口の共通処理） | REST API と同じ仕組み。ツールの種別は JSON-RPC の本文から決める |
| ツールの一覧・呼び出し・許可リスト・監査 | jin-ji（`McpToolDispatcher`） | SDK のツールの属性・反射による登録（`[McpServerTool]`・`WithTools`）は使わず、`ListToolsHandler`・`CallToolHandler` だけを差し込む |
| ツールの入力スキーマ | jin-ji（手書きの JSON Schema） | 上限・形式・ページングを明示し、知らない引数は誤りにする |

## 背景

- MCP サーバーは API とは別のポートで待ち受け、認証は jin-ji が発行する参照トークン（ADR-0004）だけを受け付ける。保護リソースメタデータも jin-ji が出す。ツールの判定・監査は REST API と同じ入口の共通処理を通す（概要 §1「MCP だけ特別な抜け道を作らない」）
- ADR-0001 のサプライチェーンの実測は、MCP を含む構成で行っている（npm の MCP SDK は推移的依存を含めて 92 件）
- MCP 仕様は 2025-06・2025-11・2026-07 と改訂が続き、2026-07-28 版では initialize の手順とセッション（`Mcp-Session-Id`）が無くなり、要求ごとのメタデータ（`_meta`）と `Mcp-Method`・`Mcp-Name` ヘッダーが要る。主要なクライアント（Claude・VS Code・ChatGPT）は当面、旧版（initialize の手順）と新版が混在する

## 検討した選択肢

| 案 | 概要 |
| --- | --- |
| **A. 公式 C# SDK（トランスポートだけを使う）** | `ModelContextProtocol.AspNetCore` の Streamable HTTP の受け口に、ツールの一覧・呼び出しの処理だけを差し込む |
| B. 公式 C# SDK（ツールの属性・認証ハンドラも使う） | `[McpServerTool]` の反射による登録、SDK の認証ハンドラ・`[Authorize]` のフィルター |
| C. 自前の最小実装 | Streamable HTTP（POST の JSON-RPC 2.0、SSE か JSON の応答）を Minimal API で書く |

### 依存の実測（2026-10-12）

資格情報を持たない SDK コンテナ（`mcr.microsoft.com/dotnet/sdk:10.0`）で、リポジトリの外の一時プロジェクトを `dotnet restore` し、lock ファイルを数えた（署名の検証と取得元の固定はリポジトリの `nuget.config` と同じ）。2.2.0 は 2026-08-13 公開の最新の安定版で、7 日以上経っている（2.0.0 の前はプレリリース。1.x は旧版の仕様まで）。

| 構成 | 推移的依存を含む件数 | 内訳 |
| --- | --- | --- |
| **JinJi.Mcp（ASP.NET Core の共有フレームワークを参照）** | **4** | ModelContextProtocol 発行 3、Microsoft 発行 1 |
| 共有フレームワークなし（参考: `ModelContextProtocol.Core` + `ModelContextProtocol`） | 12 | ModelContextProtocol 発行 2、Microsoft 発行 10（Microsoft.Extensions.*。ASP.NET Core では共有フレームワークのものを使う） |

復元時の NuGetAudit で既知の脆弱性の警告は出ていない（警告はエラー扱い）。

### 比較

| 観点 | **A. SDK（トランスポートだけ）** | B. SDK（全部） | C. 自前 |
| --- | --- | --- | --- |
| 上乗せの依存 | 4（第三者の発行元は ModelContextProtocol の 1 系統） | 4 | 0 |
| 仕様への追従（版の取り決め・2026-07-28 版の要求ごとのメタデータ・ヘッダー・誤りの対応） | SDK の更新で追える | 同左 | 自分で追う。改訂が速く、主要クライアントが旧版と新版を混在させる間は両方を実装する必要がある |
| 認証・認可の一貫性 | 既存の入口の共通処理（参照トークン・スコープ・返却人数・レート制限・監査）のまま | SDK の認証ハンドラと `[Authorize]` が既存の仕組みと二重になる | 既存のまま |
| ツールの入力スキーマ | 手書き（上限・形式・ページングを明示できる） | 反射で生成（`[Description]` で説明は付くが、上限などは足しにくい） | 手書き |
| SDK の変更の影響 | ハンドラの差し込み口（`ConfigureSessionOptions`・`ListToolsHandler`・`CallToolHandler`）と結果の型だけ | 属性・フィルター・反射の流儀にも追従が要る | 無い |

## 推奨と理由

**案 A** を推奨する。

1. **依存が小さく、発行元が少ない。** 上乗せは 4 件で、第三者の発行元は ModelContextProtocol の 1 系統（GitHub の `modelcontextprotocol/csharp-sdk`。nuget.org の予約済み接頭辞・検証済みの発行者）。残りは Microsoft.Extensions.AI.Abstractions（Microsoft）だけ。JinJi.Mcp は ASP.NET Core の共有フレームワークを参照するので、Microsoft.Extensions.* は増えない
2. **仕様の改訂を自分で追わずに済む。** 2026-07-28 版でトランスポートの取り決め（initialize の廃止・要求ごとのメタデータ・ヘッダー）が大きく変わり、旧版のクライアントも当面残る。この部分は jin-ji の価値（人事情報の認可）と関係が薄く、自前で持つと不具合と保守の負担が大きい
3. **認可の線を一系統に保てる。** SDK の認証ハンドラ・`[Authorize]` を使わず、ADR-0004 の参照トークンのスキームと、REST API と同じ入口の共通処理（閲覧者・スコープ・返却人数・レート制限・不正検知・監査）をそのまま通す。「MCP だけの抜け道を作らない」（概要 §1）を、型と経路の両方で守れる
4. **SDK への依存を薄くできる。** 使うのは受け口（`MapMcp`）・セッションの設定（`ConfigureSessionOptions`）・ツールの一覧と呼び出しのハンドラ・結果の型だけ。ツールの定義と判定は jin-ji の型（`McpTool`）で、SDK の型を外に出さない

案 C（自前）は依存を増やさないが、上の 2 の負担が大きい。案 A で問題が出たときの退路として残す（帰結を参照）。

## 推移的依存（`src/JinJi.Mcp/packages.lock.json`）

NuGet は範囲の下限の版を選ぶ。公開日はすべて 2026-10-12 時点で 7 日以上前（ADR-0001「公開直後の版を入れない」）。

| パッケージ | 版 | 公開日 | 発行元 / ライセンス | 用途 |
| --- | --- | --- | --- | --- |
| ModelContextProtocol.AspNetCore | 2.2.0 | 2026-08-13 | ModelContextProtocol / Apache-2.0 | Streamable HTTP の受け口（`MapMcp`） |
| ModelContextProtocol | 2.2.0 | 2026-08-13 | ModelContextProtocol / Apache-2.0 | サーバーの登録（`AddMcpServer`） |
| ModelContextProtocol.Core | 2.2.0 | 2026-08-13 | ModelContextProtocol / Apache-2.0 | JSON-RPC・プロトコルの型・サーバーの本体 |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | 2026-07-27 | Microsoft / MIT | SDK の依存（ツールの関数の抽象。jin-ji は使わない） |

Host（Web SDK）の lock ファイルで増えたのも同じ 4 件。第三者の発行元は 1 系統（ModelContextProtocol）。

## 守り方

- `Directory.Packages.props` で版を固定し、lock ファイルをコミットする（CI は locked mode）
- テスト（`ProjectReferenceTests`）で次を確かめる
  - `ModelContextProtocol.AspNetCore` を直接参照するのは JinJi.Mcp だけで、JinJi.Api の lock ファイルには上表のパッケージが無い
  - JinJi.Mcp の lock ファイルのパッケージが上表と一致する（増えたらこの ADR を更新する）
- SDK の機能のうち、セッションを持つ受け口（GET・DELETE・旧 SSE の `/sse`）・SDK の認証ハンドラ・ツールの属性による登録・サンプリング・誘導（elicitation）は使わない。stateless なので、サーバーからクライアントへの要求はそもそも送れない
- ツールの呼び出しの前に、入口の共通処理が JSON-RPC の本文を上限（既定 64 KiB）まで読み、ツールの種別・スコープを決める（SDK が読む前に拒否できるように）
- SDK の更新は Renovate の `minimumReleaseAge`（7 日）を経て、lock ファイルの差分・上表・通しのテスト（initialize → tools/list → tools/call）を確かめて取り込む

## 帰結

- 2026-07-28 版のクライアント（要求ごとのメタデータ）と、それより前の版のクライアント（initialize の手順）の両方を、同じ受け口で受けられる（SDK が版を見て振り分ける）
- SDK が使えなくなった・依存が増えすぎた場合は、JinJi.Mcp の中だけを自前の Streamable HTTP（POST の JSON-RPC 2.0）に置き換える。ツールの定義・判定・監査は jin-ji の型（`McpTool`・`McpToolDispatcher`）で SDK に依存しないので、差し替えは受け口と結果の型の写しに閉じる
- SDK は誤りを Error のログに出す（知らないツールの呼び出しなど）。ログの量は運用で見て、必要ならカテゴリ `ModelContextProtocol` の水準を設定で下げる

## 参照した情報源（2026-10-12 確認）

- MCP Transports（Streamable HTTP）・Authorization（2026-07-28 版）: https://modelcontextprotocol.io/specification/latest
- 公式 C# SDK: https://github.com/modelcontextprotocol/csharp-sdk （2.2.0 のタグのソース。`HttpServerTransportOptions`・`StreamableHttpHandler`・`McpServerImpl` を読んで、stateless の振る舞いとハンドラの差し込み口を確かめた）、https://csharp.sdk.modelcontextprotocol.io/
- nuget.org の公開日・発行者・nuspec の依存（`api.nuget.org` の登録情報）
- 依存数: 資格情報を持たない `mcr.microsoft.com/dotnet/sdk:10.0` コンテナでの `dotnet restore`（作業用ディレクトリで実施し、終わったら消した。リポジトリは変更していない）
