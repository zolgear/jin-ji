# ADR-0005: LLM の呼び出しの依存パッケージ（OpenAI 公式 .NET SDK）

- ステータス: **承認**
- 日付: 2026-10-18
- 関連: [ADR-0001](0001-language-and-runtime.md)（サプライチェーン方針）、[ADR-0002](0002-data-scope.md)、[ADR-0003](0003-ingest-dependencies.md)、[アーキテクチャ概要 §3・§7.1・§8](../architecture/overview.md#8-取り込み名寄せ同期)

## 決定

要確認の提案への LLM による候補提示（§8「残りのみ LLM」）に、**OpenAI 公式 .NET SDK（`OpenAI` 2.14.0）** を使う。参照するのは新しいプロジェクト **JinJi.Llm（ワーカー側）だけ**とし、API・MCP のプロジェクトからは推移的にも参照させない。

- 接続先は OpenAI 互換の Chat Completions（ベース URL で切り替える）。OpenAI（`https://api.openai.com/v1`）・Azure OpenAI（v1 API、`https://<resource>.openai.azure.com/openai/v1/`）・LiteLLM などのプロキシを同じコードで扱う。モデル名は設定（`JinJi:Llm:Model`）で、コードに固定しない
- 応答は Structured Outputs（JSON Schema、strict）で受け、jin-ji の側でも検証する
- 呼び出しの口（`ILlmClient`）は JinJi.Ingest に置き、問いの組み立て・検証・送信の記録はすべて JinJi.Ingest（SDK に依存しない）で行う。JinJi.Llm は SDK で 1 回の呼び出しをするだけ

## 理由

- OpenAI 互換の Chat Completions は、OpenAI・Azure OpenAI・LiteLLM などのプロキシが共通に受け付ける口で、導入先の LLM の選び方（クラウドの契約・社内のプロキシ）に依らずに 1 つの実装で済む。ADR-0001 のサプライチェーンの実測（OpenAI 互換を含む構成）もこの SDK を前提にしている
- Structured Outputs（JSON Schema の strict）・トークン数の取得・エラーの状態コードを型付きで扱える。HTTP を自前で書くと、仕様の追従（応答形式・エラー）を自分で持つことになる
- 推移的依存がすべて Microsoft 発行（System.ClientModel・Microsoft.Extensions.*）で、第三者の発行元は OpenAI だけ
- 個人情報と DB 資格情報を持つ API / MCP のプロセスには入れない（ADR-0001「注意点と対処」と同じ扱い）。Host は単一の実行ファイルなので同じイメージには含まれるが、API・MCP のライブラリのコードからは型を参照できない

## 推移的依存（`src/JinJi.Llm/packages.lock.json`）

NuGet は範囲の下限の版を選ぶ。公開日はすべて 2026-10-12 時点で 7 日以上前（ADR-0001「公開直後の版を入れない」）。復元時の NuGetAudit で既知の脆弱性の警告は出ていない（警告はエラー扱い）。

| パッケージ | 版 | 公開日 | 発行元 / ライセンス | 用途 |
| --- | --- | --- | --- | --- |
| OpenAI | 2.14.0 | 2026-09-15 | OpenAI / MIT | Chat Completions・Structured Outputs |
| System.ClientModel | 1.15.0 | 2026-08-04 | Microsoft / MIT | SDK の HTTP パイプライン（リトライ・タイムアウト・エラー） |
| System.Memory.Data | 10.0.9 | 2026-06-09 | Microsoft / MIT | `BinaryData`（JSON Schema・要求の本文） |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.9 | 2026-06-09 | Microsoft / MIT | System.ClientModel の依存 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.9 | 2026-06-09 | Microsoft / MIT | 同上 |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.9 | 2026-06-09 | Microsoft / MIT | 同上 |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.9 | 2026-06-09 | Microsoft / MIT | 同上 |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.9 | 2026-06-09 | Microsoft / MIT | 同上 |
| Microsoft.Extensions.Logging.Abstractions | 10.0.9 | 2026-06-09 | Microsoft / MIT | 同上 |
| Microsoft.Extensions.Options | 10.0.9 | 2026-06-09 | Microsoft / MIT | 同上 |
| Microsoft.Extensions.Primitives | 10.0.9 | 2026-06-09 | Microsoft / MIT | 同上 |

Microsoft.Extensions.* は ASP.NET Core の共有フレームワークにも含まれるので、Host（Web SDK）の lock ファイルには現れない（共有フレームワークのものを使う）。Host の lock ファイルで増えたのは OpenAI・System.ClientModel・System.Memory.Data だけ。

第三者の発行元は 1 系統（OpenAI）。

## 守り方

- `Directory.Packages.props` で版を固定し、lock ファイルをコミットする（CI は locked mode）
- テスト（`ProjectReferenceTests`）で次を確かめる
  - OpenAI を直接参照するのは JinJi.Llm だけ
  - JinJi.Api・JinJi.Mcp のプロジェクト参照の推移的な閉包に JinJi.Llm が無く、lock ファイルにも OpenAI・System.ClientModel・System.Memory.Data が無い
  - JinJi.Llm の lock ファイルのパッケージが上表と一致する（増えたらこの ADR を更新する）
- 送信は許可リスト（`JinJi:Llm:AllowedEndpoints`）の接続先だけ。本番の外向き通信の許可リスト（ADR-0001）にも同じ接続先だけを入れる
- API キーは環境変数（名前は `JinJi:Llm:ApiKeyEnvironmentVariable`、既定 `OPENAI_API_KEY`）から読み、設定ファイル・ログ・例外のメッセージに出さない。`scripts/dotnet.sh` はリポジトリ直下の `.env` をコンテナに渡さず、`JINJI_DOTNET_PASS_ENV` で名前を明示したときだけ渡す（既定は何も渡さない）
- SDK の自動リトライは切り（`ClientRetryPolicy(0)`）、タイムアウト・リトライ・同時実行数・1 回の取込の上限は jin-ji の設定で持つ（回数・トークン数を数えて料金の目安を記録するため）
- 通常のテストは実 LLM を呼ばない（`ILlmClient` のフェイクで決定的にする）。実 LLM は `evaluate-ingest --llm true` でだけ使う

## 帰結

- OpenAI SDK の更新は Renovate の `minimumReleaseAge`（7 日）を経て、lock ファイルの差分と上表をレビューして取り込む
- SDK が使えなくなった・依存が増えすぎた場合は、JinJi.Llm の中だけを HTTP の自前実装（`HttpClient` + `System.Text.Json`）に置き換える。問いの組み立て・検証・記録は JinJi.Ingest にあり、`ILlmClient` は SDK の型を出さないので、差し替えは JinJi.Llm の中に閉じる
- Azure OpenAI の旧 API（`/openai/deployments/{name}/chat/completions?api-version=`）は扱わない。v1 API（ベース URL を `/openai/v1/` にする）を使う
