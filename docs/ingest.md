# 取り込みと修正依頼

人事パッケージ・組織図・辞令・Entra ID のユーザー一覧などの「整っていない」データを取り込み、変更提案にして、人の承認で公開側へ反映するまでの使い方。jin-ji の値の誤りを人事パッケージで直してもらうための修正依頼もここで扱う。

設計は [アーキテクチャ概要 §8](architecture/overview.md#8-取り込み名寄せ同期)（実装上の規則も §8 にある）。Entra ID への反映は [Entra ID への同期](entra-sync.md)。

## 目次

- [取り込む（開発用の CLI）](#取り込む開発用の-cli)
- [項目マッピング](#項目マッピング)
- [取り込みの規則](#取り込みの規則)
- [拠点の取り込み](#拠点の取り込み)
- [要確認の解決](#要確認の解決)
- [LLM による候補提示](#llm-による候補提示)
- [取込の精度を測る](#取込の精度を測る)
- [修正依頼](#修正依頼)

## 取り込む（開発用の CLI）

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- import /src/samples/messy-corp/mappings/employees_hr_package.json /src/samples/messy-corp/employees_hr_package.csv --executed-by <実行者の person.id> [--effective-date 2026-10-01] [--llm true]
```

- CSV（UTF-8 / UTF-8 BOM / Shift_JIS を自動判定）か xlsx を、項目マッピング（下記）で読み、作業領域（staging・正規化結果・名寄せ候補・変更提案・修正依頼）に保存する。公開側（人・組織・所属）は変えない
- 閲覧者は jin-ji 自身（system、経路 cli）。実行者は指定の人で、その取込の提案の起案者になり、承認できない
- 出力は取込単位の ID・行数・提案と要確認と修正依頼の件数・捨てた列の名前と件数（値は出さない）
- 提案は、実行者とは別の人事管理者が `ProposalReviewService` で承認したときだけ反映する（提案の承認の REST API・画面はまだ無く、今はサービス・テスト・評価から使う）
- `scripts/dotnet.sh` はリポジトリだけを `/src` にマウントし、`dotnet run` はプロジェクトのディレクトリで動くので、ファイルはリポジトリの中に置き `/src/...` の絶対パスで指定する
- `--llm true` で、要確認の提案に LLM による候補提示を行う（[下](#llm-による候補提示)。設定 `JinJi:Llm` を有効にし、API キーの環境変数を渡したときだけ）。出力は送信先・回数・トークン数・料金の目安・送った項目名と件数（値は出さない）

## 項目マッピング

項目マッピング（JSON）で取込元の列名を jin-ji の取込項目（`JinJi.Ingest.IngestFields`。DB の `jinji_ingest_field_keys()` と同じ許可リスト）に対応させる。マッピングにない列・保持禁止の列名（生年月日・性別・住所・給与など。マッピングに関わらず）は **staging に保存する前に捨てる**。

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

- `target`: `people`（在籍者名簿）、`org_units`（組織図。`org_code`・`org_name`・`org_level`・`parent_org_code`・`org_head_employee_number` / `org_head_name`・`org_remarks` など）、`personnel_actions`（辞令。`action_date`・`action_text` など）、`directory_users`（Entra ID のユーザー。`external_id`・`account_enabled` など）、`sites`（拠点マスタ）
- `source`: `kind`（`csv` / `xlsx` / `graph_json`。省略時は拡張子）、`encoding`（`auto` / `utf-8` / `shift_jis`）、`delimiter`、`headerRow`（表題の行がある場合）、`sheet`、`fillMergedCells`（結合セルの値を補完する）、`hierarchy`（列が階層の組織図。`entityColumn`・`columns`・`noCarrySuffixes`）
- 候補の列名は全角・空白のゆれを吸収して比べ、行ごとに先に書いた列の値を優先する（空なら次の候補）。候補・無視する列名に保持禁止の項目名は書けない
- `legalEntityAliases`（法人のコード → 別名）、`employeeNumber.prefixedPadLength`（L45 → L0045）、`discardWhenPresent`（列に値がある行で捨てる項目）、`fullSnapshot`（Entra の全件の取込）
- サンプルの設定は `samples/messy-corp/mappings/`

## 取り込みの規則

- 作業領域は hr_admin と取込の実行者本人だけが見られる（RLS。修正依頼は修正担当者・起票者・対象の本人にも見える。[下の「修正依頼」](#修正依頼)）。実行者本人（AI エージェントは委任元）は承認なしで自分の取込の作業領域に書き込める（[概要 §7.1](architecture/overview.md#71-ai-エージェントによる書き込み) の「公開されない作業領域」）
- 公開側への反映は変更提案の承認だけ。承認者 ≠ 取込の実行者（アプリと DB の CHECK 制約）。訂正は旧行の記録期間を閉じて新しい行を追加し、有効日を指定した取込は版を有効日で分ける
- 辞令は発令の提案にし、反映時に承認時の所属・一時停止から版を導出する。発令日前の発令（内示）は反映後も人事管理者以外には見えない
- 自由記述の項目に生年月日・住所・電話番号らしき値があれば、staging に保存せず修正依頼にする
- xlsx の読み込みに ClosedXML を使う。参照するのは `JinJi.Connectors` だけで、API / MCP からは推移的にも参照しない（`ProjectReferenceTests`。理由と推移的依存は [ADR-0003](adr/0003-ingest-dependencies.md)）
- 列が階層の組織図で `source.hierarchy.skipCandidateColumns`（例: `["チーム"]`）を指定すると、結合セルの補いで上位組織を決めた新規の組織に、段飛ばしの上位組織の候補の要確認（`org_parent_ambiguous`）を作る

## 拠点の取り込み

設計は [概要 §4.7](architecture/overview.md#47-拠点とリモートワーク)。

- 拠点マスタ（`target: sites`。例 `samples/messy-corp/mappings/sites.json`）→ 組織図（組織の拠点 `site_code` / `site_name`）→ 人（人事パッケージの「勤務地」を `site_name`、リモートワークの有無を `remote_work`）の順に取り込んで承認する。人の拠点は主務の組織の拠点と違う人だけが持ち、拠点マスタに無い勤務地は修正依頼（`unknown_site`）になる
- 拠点を導入する前の版で取り込んだ自由記述の勤務地（廃止）は、拠点マスタと組織の拠点を承認した後に拠点へ照合する。主務の組織と違う拠点は人ごとの拠点の変更提案に、拠点に当たらない値は修正依頼になる（承認は別の人事管理者）

  ```sh
  scripts/dotnet.sh run --project src/JinJi.Host -- reconcile-work-locations --executed-by <実行者の person.id>
  ```

## 要確認の解決

設計は [概要 §8](architecture/overview.md#8-取り込み名寄せ同期) の「要確認の解決」。

- 要確認（`needs_review`）の提案は、人事管理者の人が `ProposalResolutionService`（REST は `POST /api/v1/ingest/proposals/{id}/resolve`。[API 仕様書](api.md#取込変更提案)）で「候補から選ぶ」「新規として登録する」「修正依頼に回す」「却下」を選ぶ。取込の実行者・AI エージェント・ワーカーは解決できない
- 解決の結果は通常の提案（起案中）になり、既存の承認フローに乗る。**解決者はその提案を承認できない**（実行者・解決者・承認者の 3 人が要る。アプリと DB の CHECK 制約）
- 選べるのは候補（名寄せの候補・組織の候補・LLM が順位を付けた候補）だけ
- AI エージェントは MCP の `suggest_review_resolution` で解決の推定を添えられる（作業領域だけ。人が解決の参考にする）
- 名寄せの要確認を解決・却下したら、その行の氏名を作業領域（staging・正規化結果）から消す。人に対応付けられなかった行の staging は保持期限を短くする（項目マッピングの `unidentifiedStagingRetentionDays`、既定 14 日）。戸籍名（旧姓使用者の戸籍姓）を作業領域に残さないため（[ADR-0002](adr/0002-data-scope.md)）

## LLM による候補提示

設計は [概要 §8](architecture/overview.md#8-取り込み名寄せ同期) の「LLM による候補提示」、依存は [ADR-0005](adr/0005-llm-dependency.md)。設定（`JinJi:Llm`）は [運用ガイド](operations.md#設定appsettingsjson--環境変数)。

- 取込ごとのオプトイン（既定はオフ）。要確認の提案のうち、ルールでは決まらない 4 種（社員番号の誤記・欠落、所属のあいまいな辞令、段飛ばしの組織の位置、Entra の旧組織名）に、候補の順位・確信度・理由を Structured Outputs で受け取り、提案の根拠（作業領域）に「LLM による推定」として付ける。提案の状態・値は変えず、公開側には人の解決と別の人の承認を経ないと出ない
- 送るのは判定に要る項目だけ（行の氏名・社員番号・発令の文面・組織名・役職、候補の業務上の氏名・社員番号・所属・役職、組織の名称・上位組織名・段・組織長の有無・配下の組織名）。送信先と送った項目名・件数（値は記録しない）を `llm_request_log` と監査ログ（`ingest.llm.send`）に残す
- 送信先は許可リスト（`JinJi:Llm:AllowedEndpoints`）だけ。候補に無い ID・スキーマに合わない応答は捨てる。行の中身は区切りの中にデータとして渡す
- 接続先は OpenAI 互換（OpenAI / Azure OpenAI の v1 API / LiteLLM など）をベース URL で切り替える。モデル名は設定で指定し、固定しない。API キーは環境変数（名前は設定）から読む

## 取込の精度を測る

```sh
scripts/dotnet.sh run --project src/JinJi.Host -- evaluate-ingest /src/samples/messy-corp --output /src/docs/reports/ingest-accuracy.md
```

- 空の一時データベース（`jinji_eval_*`。終わったら消す。`--keep-database true` で残す）にサンプルの 5 ファイル（拠点マスタ・組織図・人事パッケージ・辞令・Entra ID）を順に取り込み、起案の提案を評価用の承認者が一括承認し、`ground_truth.json` と比べて [取込の精度](reports/ingest-accuracy.md) を書き出す
- 接続情報はテストと同じ環境変数（`JINJI_PG_ADMIN_PASSWORD` など。[はじめに](getting-started.md#3-ビルドとテスト)）。重いので通常のテストからは外し、テストでは項目マッピングでサンプルを読めることだけを確かめる（`SampleSmokeTests`）
- 最後に評価用の解決者が辞令の要確認を正解データに従って解決し（人の確認の代わり）、作業領域に戸籍名が残らないことを確かめる
- `--llm true` で、要確認の提案に実 LLM の候補提示を行い、その精度（第 1 候補が正解の割合・上位 3・誤った高確信度）と料金の目安も書き出す。**実 LLM を呼ぶのはこのときだけ**（通常のテストはフェイク）。`scripts/dotnet.sh` はリポジトリ直下の `.env` をコンテナに渡さないので、API キーは名前を指定して一時的に渡す

  ```sh
  set -a; . ./.env; set +a   # OPENAI_API_KEY（値は表示しない）
  JINJI_DOTNET_PASS_ENV=OPENAI_API_KEY scripts/dotnet.sh run --project src/JinJi.Host -- evaluate-ingest /src/samples/messy-corp \
    --output /src/docs/reports/ingest-accuracy.md --llm true --llm-model gpt-4.1-mini --llm-input-price 0.40 --llm-output-price 1.60
  ```

  - 接続先・許可リスト・API キーの環境変数名は `JinJi:Llm`（`src/JinJi.Host/appsettings.json` の既定は OpenAI）。`--llm-model` はモデル名、`--llm-input-price` / `--llm-output-price` は料金の目安の単価（米ドル / 100 万トークン）

## 修正依頼

設計は [概要 §8](architecture/overview.md#8-取り込み名寄せ同期) の「修正依頼の実装の規則」。jin-ji の公開データの正は人事パッケージなので、jin-ji の値が誤っていても jin-ji では直さず、人事パッケージで直して取り込み直す。修正依頼はそのための人事パッケージの担当者への情報提供で、起票しても jin-ji の公開データは変わらない。REST API は [API 仕様書](api.md#修正依頼)、画面は [Web UI](web-ui.md#画面)。

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

Webhook の設定の例（URL は秘密を含むので環境変数で渡す。ホストは許可リストと完全一致、https だけ、私設のアドレスには接続しない。設定の一覧は [運用ガイド](operations.md#設定appsettingsjson--環境変数)）:

```sh
JinJi__Corrections__Notice__Webhook__Enabled=true
JinJi__Corrections__Notice__Webhook__Format=teams            # または slack
JinJi__Corrections__Notice__Webhook__Url=https://...（Teams のワークフローの URL / Slack の Incoming Webhook の URL）
JinJi__Corrections__Notice__Webhook__AllowedHosts__0=hooks.slack.com
```
