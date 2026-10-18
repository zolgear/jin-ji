# 同期計画の評価（messy-corp サンプル）

`JinJi.Host evaluate-sync samples/messy-corp` の出力（手で編集しない）。サンプルの 5 ファイルを取り込み・承認した一時 DB から、`entra_users.json` に対する Entra ID の同期計画を作り、正解データと比べた。実テナント・Graph には接続しない（反映はメモリ上の Entra で確かめる）。

| 項目 | 値 |
| --- | --- |
| 実行日時 | 2026-10-12 21:54 (JST) |
| 基準日（業務日付） | 2026-10-01 |
| 入力のユーザー | 347 |
| 設定 | `messy-corp-entra-sync`（`samples/messy-corp/mappings/entra_sync.json` + 評価用の人の除外） |
| 項目（スクリプトに含める） | 544（533） |

## 計画の項目

| 優先度 | 操作 | 件数 | スクリプトに含める |
| --- | --- | --- | --- |
| critical | disable_account | 5 | 5 |
| critical | block_sign_in | 5 | 5 |
| normal | update_attributes | 203 | 203 |
| normal | set_manager | 320 | 320 |
| normal | create_user | 3 | 0 |
| info | excluded_account | 8 | 0 |

属性の更新の内訳（項目ごとの件数）:

| 項目 | 件数 |
| --- | --- |
| companyName | 19 |
| department | 87 |
| displayName | 115 |
| employeeId | 62 |
| givenName | 11 |
| surname | 66 |

参考の項目の内訳: non_person 8

## 正解データとの比較

| 検査 | 正解データ | 結果 | 誤検出 | 定義 |
| --- | --- | --- | --- | --- |
| 退職者のアカウントの無効化（critical） | ENTRA_RETIRED_ENABLED | 100.0%（5/5） | 0 | 無効化の項目（スクリプトに含める） |
| 一時停止者のサインイン停止（critical） | 休職中（status = leave）でアカウントが有効 | 100.0%（5/5） | 0 | サインイン停止の項目（スクリプトに含める） |
| 旧組織名の部署の更新 | ENTRA_STALE_DEPT | 100.0%（15/15） | 0 | あるべき値が正解の勤務先の組織名と一致したもの。部署の更新は全体で 87 件（旧組織名以外は表記の違い: 半角カナ・末尾の空白・略記など） |
| 空の employeeId の設定 | ENTRA_EMPLOYEEID_EMPTY | 100.0%（25/25） | 0 | 現在値が空で、あるべき値が正解の社員番号のもの |
| 人ではないアカウントの除外 | ENTRA_NON_PERSON | 100.0%（8/8） | 0 | 参考の項目（スクリプトに含めない） |
| アカウントの作成の推奨 | ENTRA_MISSING_ACCOUNT | 100.0%（3/3） | 0 | 作成の推奨（スクリプトに含めない） |
| 表示名の更新 | ENTRA_NAME_REVERSED | 100.0%（8/8） | 0 | 表示名（姓 名）の更新があるもの（誤検出は数えない: 表記の違いの更新もあるため） |
| 表示名の更新 | ENTRA_NAME_ROMAJI | 100.0%（10/10） | 0 | 表示名（姓 名）の更新があるもの（誤検出は数えない: 表記の違いの更新もあるため） |
| 内示（未来日付の辞令）を反映しない | pending_orders | OK | 0 | 内示の対象者の Entra のアカウント 13 件で、部署・役職のあるべき値が発令後の値になっていない |

## スクリプト一式

| ファイル | バイト |
| --- | --- |
| `jinji-entra-sync-<計画 ID>.data.json` | 295046 |
| `jinji-entra-sync-<計画 ID>.ps1` | 16641 |
| `jinji-entra-sync-<計画 ID>.report.csv` | 196631 |
| `jinji-entra-sync-<計画 ID>.report.md` | 150926 |

必要な最小権限（本番の実行）: User.ReadWrite.All。ドライランは User.Read.All。

## メモリ上の Entra への反映と収束

承認済みの計画をメモリ上の Entra（入力の一覧から作る）に直接反映の経路で適用し（作成の推奨は除く）、反映後の一覧から計画を作り直した。反映の前に 1 人の部署を Entra 側で変え、事前条件の不一致でスキップされることも確かめた。

| 実行 | succeeded | already_applied | skipped_precondition | failed | would_apply |
| --- | --- | --- | --- | --- | --- |
| ドライラン | 0 | 0 | 0 | 0 | 533 |
| 反映 | 532 | 0 | 1 | 0 | 0 |

- 書き込み（PATCH・PUT）: 532 回
- 作り直した計画のスクリプトに含める項目: 1 件（update_attributes 1）。事前条件の不一致でスキップした 1 件だけが残れば収束

定義:

- 正解データの Entra の不整合（`anomalies` の `entra_users.json`）の行を、正解の対応（`mappings`）で Entra のオブジェクト ID にして、計画の項目と突き合わせる
- 誤検出: 計画に出たが正解に無いもの（表示名・部署は表記の違いの更新も出るので、部署は正解の組織名との一致だけを数える）
- 対応付けは取込の Entra ID の外部 ID の対応（承認済み）だけで行う。人ではないアカウント・対応付けの無いアカウントは触らない
