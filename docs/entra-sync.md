# Entra ID への同期

jin-ji の公開データを Entra ID のユーザーの属性・アカウントの有効 / 無効に反映する使い方。反映はスクリプト出力が主で、管理者が実行する。直接反映は既定で無効。

設計は [アーキテクチャ概要 §8](architecture/overview.md#8-取り込み名寄せ同期) の「同期」と「同期の実装上の規則」、決定は [ADR-0010](adr/0010-entra-sync.md)。

## 手順

```sh
# 計画を作る（起案者は --executed-by の人。承認できない）
scripts/dotnet.sh run --project src/JinJi.Host -- sync-plan /src/samples/messy-corp/entra_users.json --executed-by <person.id> --settings /src/samples/messy-corp/mappings/entra_sync.json
# 別の人事管理者が REST API で承認する: POST /api/v1/sync/plans/{id}/approve
# 承認済みの計画のスクリプト一式を書き出す
scripts/dotnet.sh run --project src/JinJi.Host -- sync-script <plan-id> /src/out/sync
# 管理者がスクリプトを実行した後、実行ログ（JSON）を取り込んで照合する
scripts/dotnet.sh run --project src/JinJi.Host -- sync-result <plan-id> /src/out/sync/jinji-entra-sync-<plan-id>-apply-<日時>.log.json
```

- 計画の一覧・詳細・承認・却下・スクリプトの取得の REST API は [API 仕様書](api.md#entra-id-への同期計画)
- サンプルでの評価: `scripts/dotnet.sh run --project src/JinJi.Host -- evaluate-sync /src/samples/messy-corp --output /src/docs/reports/sync-plan.md`（[同期計画の評価](reports/sync-plan.md)。取込の評価と同じ一時 DB を作り、終わったら消す。実テナントには接続しない）

## 同期計画

- jin-ji の公開データ（業務日付時点。内示・将来の予定は含めない）と Entra のユーザー一覧（Graph の `GET /users` の JSON。今はファイル入力）の差分から**同期計画**を作り、作業領域（`sync_plan`・`sync_plan_item`）に保存する。対象は表示名・姓・名・部署・役職・会社名・勤務地（人の拠点の名前）・社員番号（employeeId）・上長（manager）・アカウントの有効/無効。アカウントの作成は推奨として出し、既定ではスクリプトに含めない。対応付けは外部 ID の対応（取込の Entra ID の承認で作るもの）だけで、人ではないアカウント・対応付けの無いアカウントは触らない
- **IT 全般統制の項目（退職者の無効化・一時停止者のサインイン停止）を最優先（critical）**にし、計画・差分レポートの先頭に件数と一覧を出す。除外の設定で無効化されない退職者・一時停止者も critical の警告にする
- 各項目に根拠（jin-ji のどのデータか）と、変える項目の Entra の現在値（スクリプトの事前条件）を付ける
- 承認（起案者 ≠ 承認者。人事管理者の人だけ）を経た計画だけがスクリプト出力・直接反映の対象。承認・出力・直接反映の時点で jin-ji のあるべき値を計算し直し、計画と違えば（期限切れも）**古い計画（stale）**にして拒否する。新しい計画を作ると前の起案中・承認済みの計画は置き換え済みになる

## 同期の設定

同期の設定（表示名の形式・部署名の段・役職・会社名・対象の法人・除外するユーザー・同期する項目）は JSON のファイル（例: `samples/messy-corp/mappings/entra_sync.json`）。計画に全文と SHA-256 を保存し、**計画の承認が設定の承認を兼ねる**（理由は概要 §8）。

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

## スクリプト出力（主）

`Microsoft.Graph` PowerShell 用（Windows PowerShell 5.1 / PowerShell 7。UTF-8 BOM・CRLF）。

- 値はスクリプト本文に埋め込まず、同梱の `.data.json`（ASCII だけの JSON）を読む。スクリプトはデータの SHA-256 を照合してから始める
- 冪等: 各ユーザーの更新前に現在値を読み、計画時の値ともあるべき値とも違う項目があればスキップして記録する（`skipped_precondition`）。あるべき値なら何もしない（`already_applied`）
- `-WhatIf` でドライラン（`User.Read.All` だけで接続）。本番は計画に要る最小のスコープで `Connect-MgGraph -Scopes`（無効化だけなら `User.EnableDisableAccount.All` + `User.Read.All`、属性・上長があれば `User.ReadWrite.All`）
- エラーでも続行し、実行ログ（`.log.json`・`.log.csv`、UTF-8 BOM）を必ず書く。`sync-result` で取り込むと、計画 ID とデータの SHA-256 が一致するものだけを項目ごとに記録・照合する（`sync_run`・`sync_item_result`）
- 差分レポート（`.report.md`・`.report.csv`）。値は Markdown・HTML の記号をエスケープし、CSV は数式として解釈される先頭の文字を無害化する

## 直接反映（任意・既定は無効）

- `JinJi.Host sync-apply <plan-id> [--what-if true]`。同じ承認済みの計画を Graph REST（`PATCH /users/{id}`・`PUT /users/{id}/manager/$ref`）で適用する最小実装（HttpClient。Graph SDK は入れない）
- 読み取りとは別の書き込み用のアプリの資格情報（証明書かワークロード ID）を `JinJi:Sync:DirectApply` で明示的に有効にしたときだけ（設定は [運用ガイド](operations.md#設定appsettingsjson--環境変数)）。REST API からは起動しない

## 監査

監査ログ: `sync.plan.create`・`sync.plan.read`・`sync.plan.approve`・`sync.plan.reject`・`sync.script.export`・`sync.result.import`・`sync.apply`（拒否・失敗も）。
