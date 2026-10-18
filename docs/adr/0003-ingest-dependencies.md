# ADR-0003: 取り込みの依存パッケージ（ClosedXML）

- ステータス: **承認**
- 日付: 2026-10-18
- 関連: [ADR-0001](0001-language-and-runtime.md)（サプライチェーン方針）、[ADR-0002](0002-data-scope.md)、[アーキテクチャ概要 §8](../architecture/overview.md#8-取り込み名寄せ同期)

## 決定

xlsx の読み込みに **ClosedXML 0.105.1** を使う。参照するのは **JinJi.Connectors（ワーカー側）だけ**とし、API・MCP のプロジェクトからは推移的にも参照させない。

それ以外の依存は足さない。

| 用途 | 使うもの | 追加の依存 |
| --- | --- | --- |
| CSV の読み込み（RFC 4180 + 実務のゆれ） | 自前の小さなパーサ（`CsvTableReader`） | なし |
| 文字コード（UTF-8 / UTF-8 BOM / UTF-16 BOM / Shift_JIS(CP932)） | .NET の `CodePagesEncodingProvider`（共有フレームワークに含まれる） | なし |
| Unicode 正規化（NFKC） | `string.Normalize` | なし |
| 異体字・かな・和暦・漢数字 | 自前のテーブルと正規表現（テーブル駆動のテストで固める） | なし |
| JSON（項目マッピング・jsonb） | `System.Text.Json` | なし |
| xlsx | **ClosedXML** | 下表 |

## 理由

- 導入先では組織図・辞令・名簿が Excel で管理されていることが多く（概要 §1）、xlsx を CSV に変換してもらう手順は初期導入の障壁になる
- xlsx は ZIP + OpenXML で、結合セル・共有文字列・日付の表示形式・数式のキャッシュ値を正しく読むには相応の実装が要る。自作は不具合と保守の負担が大きい
- ClosedXML は .NET で最も使われている xlsx ライブラリの一つで、Microsoft の DocumentFormat.OpenXml の上に作られている（推移的依存の多くが Microsoft 発行）。ADR-0001 の検討でも Excel 取込はこれを前提にしていた
- 個人情報と DB 資格情報を持つ API / MCP のプロセスには入れない（ADR-0001「注意点と対処」）。Host は単一の実行ファイルなので同じイメージには含まれるが、API・MCP のライブラリのコードからは型を参照できず、`--role api|mcp` で ClosedXML のコードが読み込まれることはない

## 推移的依存（`src/JinJi.Connectors/packages.lock.json`）

NuGet は範囲の下限の版を選ぶ。公開日はすべて 2026-10-12 時点で 7 日以上前（ADR-0001「公開直後の版を入れない」）。復元時の NuGetAudit で既知の脆弱性の警告は出ていない（警告はエラー扱い）。

| パッケージ | 版 | 公開日 | 発行元 / ライセンス | 用途 |
| --- | --- | --- | --- | --- |
| ClosedXML | 0.105.1 | 2026-07-25 | ClosedXML / MIT | xlsx の読み書き |
| ClosedXML.Parser | 2.0.0 | 2025-04-03 | Jan Havlíček / MIT | 数式の構文解析 |
| DocumentFormat.OpenXml | 3.1.1 | 2024-10-16 | Microsoft / MIT | OpenXML の読み書き |
| DocumentFormat.OpenXml.Framework | 3.1.1 | 2024-10-16 | Microsoft / MIT | 同上の基盤 |
| ExcelNumberFormat | 1.1.0 | 2020-10-02 | ExcelNumberFormat developers / MIT | 表示形式の解釈（日付の判定） |
| RBush.Signed | 4.0.0 | 2024-11-18 | viceroypenguin / MIT | セル範囲の空間索引 |
| SixLabors.Fonts | 1.0.0 | 2023-08-11 | Six Labors / Apache-2.0 | 列幅の計算（読み込みでは使わない） |
| System.IO.Packaging | 8.0.1 | 2024-10-08 | Microsoft / MIT | ZIP パッケージ |

第三者の発行元は 5 系統（ClosedXML、ClosedXML.Parser、ExcelNumberFormat、RBush、SixLabors）。

## 守り方

- `Directory.Packages.props` で版を固定し、lock ファイルをコミットする（CI は locked mode）
- テスト（`ProjectReferenceTests`）で次を確かめる
  - ClosedXML を直接参照するのは JinJi.Connectors だけ
  - JinJi.Api・JinJi.Mcp のプロジェクト参照の推移的な閉包に Connectors・Ingest・Worker・Infrastructure が無く、lock ファイルにも上表のパッケージが無い
  - JinJi.Connectors の lock ファイルのパッケージが上表と一致する（増えたらこの ADR を更新する）
- 取込元のファイルは信頼しない入力として扱う。読むのはセルの値（数式はキャッシュされた結果）だけで、マクロ付きのブック（xlsm）・旧形式（xls）・外部参照は扱わない

## 帰結

- ClosedXML の更新は Renovate の `minimumReleaseAge`（7 日）を経て、lock ファイルの差分と上表をレビューして取り込む
- ClosedXML が保守されなくなった場合は、読み込みだけを DocumentFormat.OpenXml（Microsoft）で書き直す。コネクタの出力（`RawTable`）は ClosedXML に依存しないので、差し替えはコネクタの中に閉じる
