# Entra ID への同期

jin-ji の公開データを Entra ID のユーザーの属性・アカウントの有効 / 無効と、組織に対応するグループに反映する使い方。反映はスクリプト出力が主で、管理者が実行する。直接反映は既定で無効。

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

## 組織に対応するグループ

規程上の組織ごとに Entra ID のグループを作り、メンバーを jin-ji の所属に合わせる（決定は [ADR-0015](adr/0015-entra-org-groups.md)）。ユーザーの同期とは別の計画で、承認・古い計画（stale）の扱いは同じ。

```sh
# 計画を作る（入力は Entra のグループの一覧。ファイルか、直接反映の設定があれば graph）
scripts/dotnet.sh run --project src/JinJi.Host -- sync-group-plan /src/out/entra_groups.json --executed-by <person.id> --settings /src/out/entra_sync.json
scripts/dotnet.sh run --project src/JinJi.Host -- sync-group-plan graph --executed-by <person.id> --settings /src/out/entra_sync.json
# 別の人事管理者が REST API で承認する: POST /api/v1/sync/plans/{id}/approve
# 承認済みの計画を Graph API で反映する（先に --what-if true でドライラン）
scripts/dotnet.sh run --project src/JinJi.Host -- sync-apply <plan-id> --what-if true
scripts/dotnet.sh run --project src/JinJi.Host -- sync-apply <plan-id>
```

- 反映は Graph API での直接反映（`sync-apply`）。直接反映の設定（`JinJi:Sync:DirectApply`）を有効にしたときだけ動く。**グループの計画のスクリプト出力はまだ無い**（`sync-script`・`GET /sync/plans/{id}/script` は 409 で断る）
- 必要な Graph の権限（アプリケーション）: 読み取りに `Group.Read.All`・`GroupMember.Read.All`、反映に `Group.ReadWrite.All`・`User.Read.All`（チームを作るなら `Team.Create`）
- 実テナントでの検証は未実施（テストは Graph の代役で行っている）

### 設定（`orgGroups`）

同期の設定に `orgGroups` を足す。`legalEntities`（対象の法人）・`exclude`（除外するユーザー。`objectId` か `employeeNumber`）・`planMaxAgeDays` はユーザーの同期と共通。

```json
{
  "legalEntities": ["P"],
  "orgGroups": {
    "prefix": "JJ-",
    "displayNameFormat": "{name}",
    "mailNicknamePrefix": "jinji-",
    "groupType": "security",
    "createTeam": false,
    "members": "subtree",
    "includeConcurrent": true,
    "includeSecondment": true,
    "levels": [],
    "retiredPrefix": "【廃止】",
    "removeUnknownMembers": false
  }
}
```

| 項目 | 既定 | 内容 |
| --- | --- | --- |
| `prefix` | （必須） | 表示名の接頭辞。既存のグループと衝突しないように付ける |
| `displayNameFormat` | `{name}` | 接頭辞の後ろの形式。`{name}`（組織の名前）・`{code}`（組織コード）・`{company}`（法人名）・`{entity_code}`（法人コード） |
| `mailNicknamePrefix` | `jinji-` | `mailNickname` の接頭辞（ASCII）。後ろに「法人コード-組織コード」が付く |
| `groupType` | `security` | `security`（セキュリティグループ）か `microsoft365`（Microsoft 365 グループ）。作った後は変えられない |
| `createTeam` | `false` | Teams のチームも作る（`microsoft365` だけ。組織長をオーナーにする。組織長に Entra のアカウントの対応が無ければ作らない） |
| `members` | `subtree` | `subtree`（その組織と配下の組織で働く人）か `direct`（直属だけ） |
| `includeConcurrent` / `includeSecondment` | `true` | 兼務・出向の人を、兼務先・出向先の組織のグループに入れる |
| `levels` | `[]` | 対象の組織の段のキー（空ならすべての組織） |
| `retiredPrefix` | `【廃止】` | 廃止された組織のグループの表示名に付ける印 |
| `removeUnknownMembers` | `false` | jin-ji の人に対応付かないメンバー（手で加えたアカウント）も外す |

### 対応付けと衝突

- jin-ji が作るグループは、説明に印（`[jinji-org:<組織の ID>]`）を持つ。以後の計画は、この印でグループと組織を対応付ける（名前では対応付けない。組織の改称は表示名の更新になる）
- **印の無いグループは変更しない。** 同じ表示名・`mailNickname` の印の無いグループがあれば、作成を計画に入れず「衝突」（`group_conflict`）として出す。接頭辞を変えるか、管理者がそのグループの説明に印を書いて引き継ぐ
- グループは削除しない。廃止された組織のグループは、表示名に廃止の印を付けてメンバーを外す（`retire_group`）

### 計画の項目

| 操作 | 内容 | 実行 |
| --- | --- | --- |
| `create_group` / `update_group` / `retire_group` | グループの作成、表示名・説明の更新、廃止の印 | する |
| `add_group_member` / `remove_group_member` | メンバーの追加・削除（1 人 1 項目。外すのは jin-ji の人に対応付いたメンバー） | する |
| `add_group_owner` / `create_team` | 組織長をオーナーにする、チームを作る（`microsoft365` だけ） | する |
| `group_conflict` | 同じ名前の既存のグループ・種類の違い・印の重複 | しない |
| `group_note` | アカウントの対応が無くて加えられない人・jin-ji の人に対応付かないメンバー・対象外のグループ | しない |

- メンバーにできるのは、Entra のアカウントがちょうど 1 つ対応付いている人（ユーザーの同期と同じ外部 ID の対応）
- 直接反映は、変更の前に今の状態を読む。既にあるべき状態なら何もしない（`already_applied`）。計画の後に Entra 側の表示名・説明が変わっていればスキップする（`skipped_precondition`）。エラーでも続行し、項目ごとに記録する

### 入力のファイルの形

Graph の `GET /groups` の JSON に、グループごとの全員の `members`・`owners`（`id` だけでよい）を足したもの。`graph` を指定すれば jin-ji が Graph API から読む（印の付いたグループだけメンバーを読む）。

```json
{ "value": [ { "id": "…", "displayName": "JJ-営業一課", "description": "… [jinji-org:…]", "mailNickname": "jinji-p-a050",
               "groupTypes": [], "resourceProvisioningOptions": [], "members": [ { "id": "…" } ], "owners": [] } ] }
```

- `members` が無いグループは、メンバーが分からないものとして扱う（加える項目だけを作り、外す項目は作らない）
- Graph の `$expand=members` は 20 件までなので、`/groups/{id}/members` をすべてのページ読んで入れる

## 監査

監査ログ: `sync.plan.create`・`sync.plan.read`・`sync.plan.approve`・`sync.plan.reject`・`sync.script.export`・`sync.result.import`・`sync.apply`（拒否・失敗も）。
