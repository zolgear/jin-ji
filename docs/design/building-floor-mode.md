# 建物・フロアモード（人の決まった席・持ち場）の仕様と実装方針

- ステータス: **検討中（提案）**。MVP（段階 1）のうち Web 以外を実装した（マイグレーション 0019。§9）
- 日付: 2026-10-18
- 関連: [ADR-0012（提案）](../adr/0012-workplace-and-seat-data.md)、[ADR-0002](../adr/0002-data-scope.md)（扱うデータの範囲。決定 7「席の割り当て」）、[ADR-0008](../adr/0008-authorization-model.md)（認可）、[ADR-0009](../adr/0009-agent-writes-hitl.md)（エージェントの書き込み）、[アーキテクチャ概要 §4.1・§4.7・§4.8・§5・§7.3](../architecture/overview.md)
- モック: `assets/floor-*.png`（3D の表現 3 案と 2D の簡易フロアマップ。§5.1）

## 0. 目的と位置づけ

いまの 3D 表示は、組織の階層を段（レイヤー）として積み、拠点は組織の属性（色分け・拠点で分ける）として扱っている。新しい **建物・フロアモード** では、拠点を物理的な建物（本社ビル・工場・物流センター）として扱い、

```
拠点（site） → 建物 → 階（フロア） → エリア / ゾーン（工場のライン・倉庫のエリアを含む） → 座席（または持ち場）
```

の空間の構造を 3D と 2D の簡易フロアマップで見せる。人が「どの階・どの席・どの持ち場の人か」を、**人事が持つ決まった席・持ち場（固定席・持ち場・副の席。有効期間付き）** として把握できるようにする。

扱うのは人事の範疇の情報だけにする。**当日の所在（今日どこにいるか）・座席予約・フリーアドレス・会議室などの施設の予約と管理は扱わない**。いずれも人事の範疇を超える施設管理で、連携先（施設管理・座席予約・入退館のシステム）の範疇である。

ただし、当日の所在を同じ画面で見たい需要は残る。そこで **所在は保持せず、外部の提供を注入して表示する**（§3.4。リアルタイムの所在は連携先の範疇）。jin-ji は所在を保存せず、予約も受けない。導入先が自前の実装（`IPresenceProvider`）を登録したときだけ、今日の所在に jin-ji の認可を適用して返す。

jin-ji の原則（概要 §1）との関係:

| 原則 | この機能での扱い |
| --- | --- |
| 正は人事パッケージ | 人事パッケージは座席を持たないことが多い。建物・階・座席のマスタと決まった席の割り当ては、**jin-ji が正になり得る初めての情報**になる。外部の施設管理システムがあればそちらを正にして取り込む（ADR-0012 の決定 2） |
| 全社公開レベルの情報しか持たない | 決まった席は社内の座席表として掲示されるのと同じ水準の情報とみなし、保持してよい。当日の所在は行動の記録に近く全社公開レベルを超えるので、**保持しない**（注入した提供の表示だけ。§3.4） |
| AI も人も同じ入口・同じ権限判定 | 席・階・所在は人の項目（ポリシーの `workplace`・`seat`・`presence`）として PersonProjector を通す。MCP にだけ抜け道を作らない |
| 公開に至る変更は人が承認する | 配置図の改訂と席の割り当ては、起案 → 別の人の承認で公開側に出す（取込の提案と同じ枠組み） |
| 事実と記録の二つの時間を持つ | 配置図の改訂・席の割り当ては有効期間と記録期間を持ち、タイムマシンで過去・（権限があれば）将来の席の配置を見られる |

**対象外**（明記して範囲の膨張を防ぐ。人事の範疇を超える施設管理で、連携先の範疇）: 当日の所在の保存、座席予約・会議室の予約、フリーアドレス（組織のホームエリア・自由席の種類）、来客の管理、チェックイン（実際に座った時刻）、入退館・IC カードのログ、PC のログオン、BLE / Wi-Fi などによる位置の測位、在宅・出張・外出・休暇などの場所や理由の区分、CAD・BIM・図面画像の取り込み、動線や滞在時間の分析、個人別の在席率・出社日数のレポート、勤怠（労働時間・就業場所の把握は勤怠システムの役目）。会議室・コアなどは配置図の図形（固定の部分）としてだけ持つ。

## 1. ユースケースと利用者

| 利用者 | したいこと | 段階 |
| --- | --- | --- |
| 一般社員 | 同僚の席・階を知る（「鈴木さんはどの階？」）。初めて行く拠点で、相手の部署の島を探す。自分の席番号を確かめる | MVP |
| 総務・ファシリティ | 建物・階・座席のマスタを整える。決まった席・持ち場を割り当てる。空席（割り当ての無い席）と階の収容（席数・割り当て数）を把握する。レイアウト変更（改訂）を計画する | MVP（閲覧・取込）／ 段階 3（編集 UI） |
| 人事 | 異動に伴う席の移動を計画する（4/1 の一斉発令に合わせた席替え）。将来の時点の席配置をタイムマシンで確かめる | 段階 3 |
| AI エージェント / MCP | 「〇〇さんの席は？」「3 階の空席は？」「営業第一部はどの階？」に答える。委任元に見える範囲だけ | MVP |
| 社内システム | 内線表・受付の案内表示に、階（・席）を渡す（項目の上限で絞る） | MVP（API）／ Entra の officeLocation は段階 3 |
| 導入先（所在の提供を持つ） | 入館・座席予約などの連携先の今日の所在を、jin-ji の権限判定を通してフロアの画面に重ねる（`IPresenceProvider` の登録） | MVP（拡張点）。jin-ji は提供の実装を持たない |

### 働き方ごとの扱い

| 働き方 | 表現 | 備考 |
| --- | --- | --- |
| 固定席 | 人 → 座席（有効期間付き） | 1 人 1 席（主の席）。兼務先の席（副の席）は任意 |
| 工場の持ち場・ライン、倉庫のエリア | 人 → ゾーン（ライン・エリア）または座席（持ち場。`station`、定員 2 以上を許す） | **シフト・勤務帯は持たない**（ADR-0002）。交替で同じ持ち場を使う人は、定員 2 以上の持ち場に重ねて割り当てるだけにし、誰がいつ入るかは持たない |
| フリーアドレス | 持たない（席・ゾーンの割り当てが無い人として扱う） | 日々の座席の確保は座席予約のシステムの範疇。jin-ji は「自由席」の種類も組織のホームエリアも持たない |
| 在宅（リモートワーク） | 既存の `remote_work`（有無だけ）。席は持たなくてよい | 席が無いことを「不在」と表示しない |
| 出張・外出・休暇 | 持たない | 行動の記録・事情の推測につながるため |
| 一時停止中（休職など） | 決まった席の割り当ては残してよい（所属と同じ扱い。ADR-0002「一時停止中でも組織図上の所属は維持」） | 席の解放は総務の判断で割り当てを終える。理由は持たない |

## 2. データモデル

### 2.1 構造

```
site（拠点。既存）
 └─ building（建物）              1 拠点に N 棟（本社は 1 棟、工場は複数棟が普通）
     └─ floor（階）                B2・B1・1F・M2F（中二階）・2F … 屋上
         └─ floor_layout（配置図の改訂。有効期間付き。承認済みだけ）
             ├─ layout_zone（エリア / ゾーン。多角形）
             └─ layout_seat（席の置き場所。点 + 向き + 机の型）
 seat（座席の不変 ID と席番号。改訂をまたいで同じ席を指す）
 workplace_assignment（人 → 座席 または ゾーン。主・副。有効期間付き）
```

- **建物と拠点は 1:N**。拠点は「事業所（住所の単位）」で、工場・物流センターは同じ敷地に複数棟がある。本社ビルのような 1 棟の拠点は、建物を 1 つ作るだけで済む。建物の無い拠点（小さな営業所）は今のまま拠点だけで扱う
- **座席の同一性（`seat`）と置き場所（`layout_seat`）を分ける**。レイアウトの改訂で机を動かしても、席番号が同じなら同じ席として割り当てが続く。席番号を変える・席を廃止するのは改訂で表す
- **配置図は改訂（`floor_layout`）の単位で版を持つ**。席・ゾーンを 1 行ずつ版にすると、改訂 1 回で数百行の版が生まれ、承認の単位も分かりにくい。改訂を 1 つの起案として承認し、有効日から切り替える（一斉発令の改編プランと同じ考え方。概要 §4.3）
- **起案中の改訂は取込の作業領域（変更提案）が持つ**。改訂 1 つ分を提案の payload に持ち、別の人事管理者の承認で `floor_layout` に入る。`floor_layout` には承認済みの改訂だけを置き、中身は変えない（有効期間を区切るときは記録期間を閉じて写しの改訂を足す）。起案 → 別人の承認の仕組みを取込と 1 つにする

### 2.2 階の表し方

| 列 | 意味 |
| --- | --- |
| `label` | 表示名（`B1`・`1F`・`M2F`・`屋上` など。導入先の表記。全角・小文字の揺れはそろえる） |
| `level_order` | 並び順の整数。地上 n 階 = `n × 10`、地下 n 階 = `-n × 10`、中二階は間の値（`1F` と `2F` の間の `15`）。3D の積み上げの順もこれで決める |
| `floor_kind` | `office`・`factory`（製造フロア）・`warehouse`・`lobby`・`service`（機械室・駐車場）・`other`。表示とまとめ方だけに使う固定の少数（拠点の種別と同じ理由。§4.7） |

### 2.3 簡易フロアマップの「簡易」の範囲

CAD や図面画像を取り込まず、**人が手で描ける・CSV / JSON で書ける最小の形** に限る。

| 要素 | 形 | 上限・規則 |
| --- | --- | --- |
| 座標系 | 階ごとのローカル座標。原点は図面の左下、x は右、y は奥。**整数のミリメートル** | 既定の格子は 100 mm。階の外形の外接矩形は 1 辺 500 m まで |
| 階の外形 | 多角形 1 つ（穴なし） | 頂点 3〜64、自己交差なし |
| 固定の部分（コア・柱・壁で囲った部屋） | 矩形または多角形 + 種類（`core`・`room`・`void`〔吹き抜け〕など） | 1 階に 200 個まで。名前（「会議室 A」）は表示用の短い文字。**会議室は図形だけで、予約・利用状況は持たない** |
| エリア / ゾーン | 多角形 + 種類（`office`・`production_line`・`storage`・`common`・`restricted`） | 1 階に 200 個まで |
| 座席 | 点（机の中心）+ 向き（15° 刻み）+ 机の型（`desk_1200`・`desk_1400`・`desk_1600`・`station`・`counter` などの既定の型から選ぶ） | 1 階に 3,000 席まで |
| 座席の属性 | 種類（`fixed` 固定席・`station` 持ち場）・定員（既定 1。`station` だけ 2 以上可）・設備のタグ（許可リスト: `monitor`・`dual_monitor`・`standing`・`phone_booth` など）・保守中 | **健康・障害に関わるタグ（車椅子対応など）は持たない**。人の割り当てと組み合わさると、ADR-0002 で保持しない障害の情報を推測させるため。必要なら総務の運用（jin-ji の外）で扱う |

保持の形式:

- DB は表と列で持つ（席の座標は `x_mm`・`y_mm` の整数列、多角形は `jsonb` の `[[x, y], …]`）。PostGIS は入れない（拡張を増やさない。必要な計算は外接矩形・点の多角形内判定程度でアプリで足りる）
- 入出力の交換形式は **JSON（jin-ji の配置図の形式 `jinji-floor-layout/v1`）** と、席だけの **CSV**（階・席番号・x・y・向き・種類・ゾーン）
- **SVG は表示と書き出しにだけ使い、取り込まない**。SVG はスクリプトや外部参照を埋め込めるため、取り込むと保存型の XSS の入口になる

### 2.4 割り当てと時点

- `workplace_assignment`: 人 → 座席（`seat_id`）またはゾーン（`zone_key`。工場のライン・倉庫のエリア）のどちらか一方。`role` は `primary`（主の席）・`secondary`（兼務先などの副の席）
- 二つの時間軸（§4.1）を持つ。過去・将来の席配置は `as_of` で引ける（タイムマシン）
  - 配置図: その日に有効な承認済みの改訂
  - 割り当て: その日に有効な割り当て。割り当て先の席がその日の改訂に無ければ「席なし」（閉じた席を黙って別の席に読み替えない。拠点の上書きの規則と同じ）
- 人の階・エリア（`workplace`）の解決: 主の席の割り当て → その席の階・ゾーン、無ければ主のゾーンの割り当て → その階・ゾーン、どちらも無ければ無し。**組織から引き継がない**（組織単位のエリアはフリーアドレスの表現で、持たない）
- **将来の割り当て（席替えの予定）は、内示と同じ線で隠す**。異動に伴う席替えは、席が動くこと自体が内示の手がかりになるため、`lower(valid_period) <= jinji_viewer_horizon()` の線を RLS とアプリの両方で引く（§4.7 の将来の拠点の版と同じ）

### 2.5 PostgreSQL のスキーマ（マイグレーション 0019）

既存の表（0017 の `site_version` など）の作法に合わせる。記録期間の保護トリガー（`jinji_guard_recorded_period`）・RLS の FORCE・変更フィードのトリガー・項目カタログへの登録は、すべての表に付ける。**時刻の列（作成・更新の日時など）は持たない**（記録期間の `tstzrange` を除く）。

```sql
-- 建物（不変 ID と版）
CREATE TABLE building (id uuid PRIMARY KEY, site_id uuid NOT NULL REFERENCES site (id), UNIQUE (id, site_id));
CREATE TABLE building_version (
    id uuid PRIMARY KEY, building_id uuid NOT NULL, site_id uuid NOT NULL,
    code text NOT NULL,                 -- 拠点の中で一意（例: MAIN, F1）
    name text NOT NULL, sort_order smallint NOT NULL DEFAULT 0,
    valid_period daterange NOT NULL, recorded_period tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    FOREIGN KEY (building_id, site_id) REFERENCES building (id, site_id),
    EXCLUDE USING gist (building_id WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period)),
    EXCLUDE USING gist (site_id WITH =, code WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period))
);

-- 階（不変 ID と版。label・level_order は建物の中で重ならない）
CREATE TABLE floor (id uuid PRIMARY KEY, building_id uuid NOT NULL REFERENCES building (id), UNIQUE (id, building_id));
CREATE TABLE floor_version (
    id uuid PRIMARY KEY, floor_id uuid NOT NULL, building_id uuid NOT NULL,
    label text NOT NULL, level_order integer NOT NULL,
    floor_kind text NOT NULL CHECK (floor_kind IN ('office','factory','warehouse','lobby','service','other')),
    valid_period daterange NOT NULL, recorded_period tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    FOREIGN KEY (floor_id, building_id) REFERENCES floor (id, building_id)
    -- floor_id・(building_id, label)・(building_id, level_order) の重なりの排他制約
);

-- 座席の同一性（席番号は階の中で一意）
CREATE TABLE seat (id uuid PRIMARY KEY, floor_id uuid NOT NULL REFERENCES floor (id), code text NOT NULL, UNIQUE (floor_id, code), UNIQUE (id, floor_id));

-- 配置図の改訂（承認済みだけ。起案者 ≠ 承認者）
CREATE TABLE floor_layout (
    id uuid PRIMARY KEY, floor_id uuid NOT NULL REFERENCES floor (id),
    outline jsonb NOT NULL, fixtures jsonb NOT NULL DEFAULT '[]', grid_mm integer NOT NULL DEFAULT 100,
    proposed_by_person_id uuid NOT NULL, approved_by_person_id uuid NOT NULL,
    valid_period daterange NOT NULL, recorded_period tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT floor_layout_two_person CHECK (approved_by_person_id <> proposed_by_person_id),
    CONSTRAINT floor_layout_no_overlap EXCLUDE USING gist (floor_id WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period))
);
CREATE TABLE layout_zone (
    layout_id uuid NOT NULL REFERENCES floor_layout (id), zone_key uuid NOT NULL,   -- zone_key は改訂をまたいで同じゾーンを指す
    name text NOT NULL,
    zone_kind text NOT NULL CHECK (zone_kind IN ('office','production_line','storage','common','restricted')),
    polygon jsonb NOT NULL,
    PRIMARY KEY (layout_id, zone_key)
);
CREATE TABLE layout_seat (
    layout_id uuid NOT NULL, floor_id uuid NOT NULL, seat_id uuid NOT NULL, zone_key uuid,
    x_mm integer NOT NULL, y_mm integer NOT NULL, rotation_deg smallint NOT NULL DEFAULT 0,
    desk_type text NOT NULL,
    seat_kind text NOT NULL CHECK (seat_kind IN ('fixed','station')),
    capacity smallint NOT NULL DEFAULT 1 CHECK (capacity = 1 OR seat_kind = 'station'),
    features text[] NOT NULL DEFAULT '{}',          -- 許可リスト。健康・障害に関わるタグは作らない
    out_of_service boolean NOT NULL DEFAULT false,
    PRIMARY KEY (layout_id, seat_id),
    FOREIGN KEY (layout_id, floor_id) REFERENCES floor_layout (id, floor_id),   -- 改訂の階
    FOREIGN KEY (seat_id, floor_id) REFERENCES seat (id, floor_id),             -- 席の階
    FOREIGN KEY (layout_id, zone_key) REFERENCES layout_zone (layout_id, zone_key)  -- 同じ改訂のゾーン
);

-- 人の決まった席・持ち場（二つの時間軸）
CREATE TABLE workplace_assignment (
    id uuid PRIMARY KEY, person_id uuid NOT NULL REFERENCES person (id),
    floor_id uuid NOT NULL REFERENCES floor (id), seat_id uuid REFERENCES seat (id), zone_key uuid,
    role text NOT NULL CHECK (role IN ('primary','secondary')),
    exclusive boolean NOT NULL,                     -- 定員 1 の席か（席の種類からアプリが決める。他の行を読むトリガーを書かない）
    valid_period daterange NOT NULL, recorded_period tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT workplace_assignment_target CHECK ((seat_id IS NULL) <> (zone_key IS NULL)),
    CONSTRAINT workplace_assignment_primary_no_overlap EXCLUDE USING gist (person_id WITH =, valid_period WITH &&)
        WHERE (role = 'primary' AND upper_inf(recorded_period)),          -- 1 人の主の席は同時に 1 つ
    CONSTRAINT workplace_assignment_seat_no_overlap EXCLUDE USING gist (seat_id WITH =, valid_period WITH &&)
        WHERE (exclusive AND upper_inf(recorded_period))                  -- 定員 1 の席は同時に 1 人
);
```

（列の CHECK 制約〔多角形の形・座標の範囲・格子・机の型と設備のタグの許可リスト・文字数〕は 0019 を参照。）

DB で検査しないもの（§5.6 の規則: 他の行を読むトリガーを書かない）と、その代わりにアプリが起案・承認時に検査するもの:

- 割り当て先の席・ゾーンが、その日の改訂にあること
- 定員 2 以上の持ち場の割り当て数が定員を超えないこと（超える行は修正依頼）
- 多角形の自己交差・外形の内側にあること、座標が外形の内側にあること
- 席の拠点（建物の拠点）と、人の拠点（§4.7 の解決）が食い違わないこと。食い違いは承認を止めず、修正依頼（`site_mismatch`）に出す。正は人事パッケージ側の拠点で、席の方を直すか拠点を直すかは人が判断する

RLS:

- 建物・階・配置図（承認済み）は組織・拠点と同じく全社公開。将来の版・将来に有効になる改訂（とその席の置き場所・ゾーン）は `jinji_viewer_horizon()` の線で人事管理者以外に見せない
- 割り当ては行としては閲覧者全員に見せ（今日・過去）、項目（`seat`・`workplace`）の判定はアプリ（PersonProjector）が行う（既存の所属と同じ役割分担。§5.4）。将来の割り当ては horizon の線で RLS が止める
- 書けるのは人事管理者（取込の承認）だけ

変更フィード: 割り当ての変更は人（`person`）の変更として記録する。建物・階の版・配置図の改訂は、その階に席・持ち場を持つ人の変更として記録する（人の応答の `workplace`・`seat` が変わるため）。配置図そのものの種別（`floor`）は編集（段階 3）とあわせて決める。

## 3. プライバシー・権限

### 3.1 情報の機微さの段階

| 情報 | 機微さ | 扱い |
| --- | --- | --- |
| 建物・階・配置図（席の置き場所・ゾーン） | 会社の情報 | 全社公開。拠点の所在地と同じ |
| 人の階・エリア（`workplace`） | 社内の座席表と同じ水準 | 既定は全社公開。ポリシーで狭められる |
| 人の席（`seat`。席番号・位置） | 同上だが、粒度が細かい | 既定は全社公開（**未決: 部内を既定にするか**）。`workplace` とは別の項目にして、「階までは全社、席は部内」を設定 1 件で表せるようにする |
| 将来の席（席替えの予定） | 内示の手がかり | 人事管理者だけ |
| 当日の所在（`presence`。注入した提供の今日の分） | 行動の記録に近い。全社公開レベルを超える | **保持しない**。提供があるときだけ表示し、既定の公開範囲は本人と同じ部（§3.4） |
| チェックイン・入退館・測位・座席予約 | 施設管理・行動の記録 | 持たない（連携先の範疇） |

### 3.2 既存の権限モデルとの整合

- 項目 `workplace`（建物・階・エリア）と `seat`（席番号・座標）を `PersonFields` に足し、ポリシー（§5.2）とクライアントの項目上限の対象にする。上限は「新しい項目を黙って渡さない」原則（0017 の `work_location` の置き換えと同じ）で、既存のクライアントの上限には足さない
- 個人単位の例外（安全上の配慮など）で席を隠せる。ここで **隠した人の席を「割り当てあり（名前なし）」と表示すると、例外があること自体が目立つ**。そこで閲覧者に見せる席の状態を次の 3 つにし、隠した人の席は「使えない席」に含める（保守中・予備の席と区別しない）

  | 表示 | 中身 |
  | --- | --- |
  | 人（名前・組織の色） / `occupied` | 閲覧者に `seat` が見える人の席 |
  | 空席 / `vacant` | 割り当ての無い席（**予約の空きではない**） |
  | 使えない席（淡い斜線） / `unavailable` | 保守中・予備（`out_of_service`）と、閲覧者に見えない人の席 |

- 件数（階の席数・使用中・空席・使えない席）は、件数の規則（§7.2）と同じく閲覧者に見える人だけで数える。「使えない席」は空席に数えない
- 空席の検索（「3 階の空席は？」）は人を返さないので種別は `org_stats`。上の「使えない席」は空席として返さない
- タイムマシン: 過去の時点の席配置は、組織図の過去の時点と同じく誰でも見られる。関係（課内など）の判定は今日の組織で行う（§5.6 の既存の規則）。将来は人事管理者だけ

### 3.3 ロールと職務分掌

- 改訂と割り当ての起案は取込の実行者（人事管理者）、承認は別の人事管理者（§5.5 の職務分掌と同じ）。ファシリティ管理者のロールは作らない（未決 §8）
- **将来の割り当てと内示**: 4/1 の異動に合わせた席替えは、人事管理者が将来の日付の割り当てを起案する。発令日（見せてよい日）が来たら通常の割り当てとして見える

### 3.4 当日の所在の拡張点（`IPresenceProvider`）

**所在は保持せず、外部の提供を注入して表示する。リアルタイムの所在は連携先の範疇**。jin-ji は所在の表・予約の受付・取込を持たない。導入先が入館・座席予約などの連携先から今日の所在を引く実装を書き、DI で登録したときだけ表示する。

```csharp
// JinJi.Application.Presence
public sealed record PresenceQuery(DateOnly Date, Guid? FloorId, IReadOnlyCollection<Guid> PersonIds);
public sealed record PresenceEntry(Guid PersonId, Guid FloorId, Guid? SeatId, Guid? ZoneKey);
public interface IPresenceProvider
{
    bool IsAvailable { get; }
    Task<IReadOnlyList<PresenceEntry>> GetAsync(PresenceQuery query, CancellationToken cancellationToken);
}
```

- **問い合わせ**: 階で引く（`FloorId` あり・`PersonIds` は空）か、人で引く（`PersonIds` あり・`FloorId` は null）。`Date` は業務日付（日本時間の今日）だけ
- **返す値**: 人ごとに今日の階と、席（`seat.id`）またはエリア（配置図のゾーンの `zone_key`）。**型は時刻・入退館・チェックインなどの生の記録を持たない**。jin-ji はフリーアドレスのマスタを持たないので、エリアは配置図のゾーンで表す。提供する側は閲覧者を知らない（閲覧者に合わせて絞らなくてよい）
- **既定の実装は「提供なし」**（`NoPresenceProvider`。`IsAvailable = false`）。このとき API は所在を返さず、`GET /floors/{id}/presence` は 404（`type` は `urn:jinji:problem:presence-unavailable`）
- **登録**: ホストは `TryAddSingleton<IPresenceProvider, NoPresenceProvider>()` で登録する。導入先は `JinJi.Host` を参照する小さな起動プロジェクトを作り、`JinJiHost.RunAsync(args, services => services.Replace(ServiceDescriptor.Singleton<IPresenceProvider, 自前の提供>()))` で起動する（Web の役割のサービスの登録の後に差し替わる）。外部への問い合わせ（認証・タイムアウト・失敗時の扱い）は実装の側の責任で、jin-ji は提供の実装を持たない。失敗したら例外ではなく空の結果を返すことを勧める

jin-ji の側で必ず守ること（固定。提供の実装・設定で変えられない）:

| 項目 | 内容 |
| --- | --- |
| 認可を必ず適用する | 返った所在には、項目 `presence` の公開範囲（組み込みの規則。既定は本人と同じ部 `same_level(department)`。設定 `JinJi:Directory:PresenceVisibilityScope`、`none` は不可）・ポリシー（積で狭まる）・個人単位の例外・クライアントの項目の上限を適用する。席番号は項目 `seat` も見えるときだけ返し、それ以外はエリアまで。見えない人の所在の席は `unavailable`（隠した人の席と同じ扱い） |
| `*` は含めない | クライアントの項目の上限の `*` は `presence` を含まない。`presence` を明示したクライアントだけに返す |
| 今日だけ | 時点指定（`as_of` の過去・将来）では提供を呼ばない。人の参照で `as_of` を省くか今日のときだけ問い合わせる |
| 保持しない | キャッシュ・永続化せず、変更フィード・Webhook・同期・監査ログに所在を記録しない。HTTP の応答は `Cache-Control: no-store` |
| 配置図に無いものは捨てる | jin-ji の配置図に無い階・席・ゾーンの所在、席とエリアのどちらも無い所在は捨てる。同じ人が複数あれば最初の 1 件 |
| 人の一覧の条件にしない | 人の検索（`search_people`・`GET /people`）・一括取得・階の在籍者には所在を出さない。所在は 1 人の参照と階の所在の画面だけ |
| 意味づけない | 所在が無いことを「不在」「在宅」「休み」と表示しない・返さない |

人事管理者（`hr_admin`）は、他の項目と同じく公開範囲の規則にかかわらず `presence` が見える（全項目の閲覧。ADR-0008）。ただし項目の上限は受けるので、人事管理者の委任でも `presence` を明示していないクライアントには返らない。

### 3.5 MCP での提供範囲

- 人の席・階は `get_person`・`search_people` の人の項目として返す（REST と同じ `PersonView`）。項目の上限に `workplace`・`seat` が無いクライアントには返さない
- 新しいツールは人を返さないもの（階の一覧・空席）と、返却人数の制限を受けるもの（階の在籍者）に分ける（§6）
- 当日の所在は `get_person` の `presence` だけで、項目の上限に `presence` を明示したクライアントにだけ返す。予約・所在のツールは持たない。`list_floor_occupants` は決まった席だけを返す（「今日その階にいる人の一覧」は引けない）
- サーバーの指示に「`seat` が無いのは見えないか割り当てが無いかで、不在を意味しない」「空席は割り当ての無い席で、予約の空きではない」「`presence` は jin-ji が保持する情報ではなく、無いことは不在を意味しない。勤怠・評価の判断に使わない」と書く

## 4. データの入手経路

| 経路 | 内容 | 段階 |
| --- | --- | --- |
| 配置図の JSON の取込 | 建物・階・外形・固定の部分・ゾーン・席。建物・階・改訂の起案になり、別の人の承認で有効 | MVP |
| 席の CSV | 階・席番号・x・y・向き・種類・ゾーン。今の配置図の外形・固定の部分・ゾーンを引き継いで席だけを置き換える改訂。既存の座席表（Excel）から起こす想定 | MVP |
| 割り当ての CSV | 社員番号・席番号（または階 + ゾーン）・主副・開始日・最終日。照合は社員番号（名寄せ済みの人）・席番号の正規化（全角・ハイフンの揺れ。`３Ｆ－Ａ０１` → `3F-A01`）。社員番号の無い行は修正依頼 | MVP |
| 管理画面での配置図の編集 | SVG の上で外形・ゾーン・席を格子に吸着させて置く。改訂の起案 → 承認 | 段階 3 |
| 管理画面での割り当て | 席をクリックして人を割り当てる。席替えの計画（将来の日付） | 段階 3 |
| 発令との連動 | 異動の発令から「席替えが要る人」の一覧を作り、席の予定の起案を助ける（補完提案。作業領域） | 段階 3 |
| Entra ID の officeLocation | 今は拠点名。設定で「拠点名 + 階」を出せるようにする（席までは出さない。テナント全体に見えるため） | 段階 3 |
| 当日の所在 | **取り込まない**。`IPresenceProvider` の注入で表示だけ（§3.4） | 拡張点 |

取込は既存の枠組み（staging → 正規化 → 名寄せ → 提案 → 承認。§8）に乗せ、取込の対象に `floor_layouts`（配置図の JSON・席の CSV）・`workplace_assignments` を足す。取込の対象ごとに書ける項目を分ける（配置図の取込に人の属性は書けず、人の取込に席は書けない）。混入の検査（`SensitiveValueDetector`）は席の名前・ゾーンの名前にもかける。

## 5. 3D 表示の仕様

### 5.1 表現の案（静止画のモック）

モックは three を使った単体の HTML（web/src には組み込まない）で、同じデータ（6 階建ての本館、3F を選び、検索した人の席を示した状態）を 3 つの表現で描いた。

| 案 | 画像 | 表現 | 良い点 | 弱い点 |
| --- | --- | --- | --- | --- |
| A 建築模型 | `assets/floor-a-light.png` | 階のスラブを実際の階高で積む。選んだ階より上は輪郭だけ、下はごく薄いガラスの外皮と方立て | 建物としての実感が強い。描くのは選んだ階の中身だけなので軽い | 他の階の様子（どこが埋まっているか）が見えない。下の階のガラスが面として重い |
| **B 分解アクソメ（推奨）** | `assets/floor-b-light.png`・`assets/floor-b-dark.png` | 階を大きく離して積み、四隅を細い縦の破線で揃える。全階の机を見せ、選んだ階だけ組織の色と人の形、他の階は灰の点 | 全階の埋まり方と選んだ階の詳細を同時に読める。**組織モードの「段を積む」文法と同じ**で、モードを切り替えても空間の読み方が変わらない | 階が多い建物（20 階以上）は縦に長くなる（§5.3 の圧縮で対処） |
| C 製品モジュール | `assets/floor-c-light.png` | 今の 3D の外装の文法（白い外装・黒鉛色の台座と溝）を建物に当て、選んだ階を引き出しのように手前へ出す | 組織モードのモジュールと見た目の系列がそろう。選んだ階が明確 | 黒鉛色の帯と開口が重く、建物全体が「製品」に見えて地図としては読みにくい。引き出しの動きが演出に寄る |

2D の簡易フロアマップ: `assets/floor-plan-light.png`・`assets/floor-plan-dark.png`（SVG。3F）。

**推奨は案 B**。理由:

1. 組織モード（段＝レイヤーを積む）と同じ「積層」の読み方で、建物・フロアモードに切り替えても利用者が空間を読み直さなくてよい
2. 全階の埋まり方（灰の点）と、選んだ階の詳細（組織の色・人の形・空席の輪）を同時に見せられる。階の一覧（左の割り当て / 席数の棒）と 3D が同じことを違う粒度で示す
3. 線は細い縦の破線と輪郭だけで、直角の配線・発光・光のパルスを使わない。色はデータ（組織）と選択（黄 + 黒鉛色の細い輪）にしか使わない。抑制の効いた白基調の模型の質感を保てる
4. 低スペックの端末では、案 A の簡略化（選んだ階以外の机を描かず、スラブと人数の札だけにする）を LOD としてそのまま取り込める

案 C の外装の文法は、建物の一覧（拠点の敷地に建物を並べる俯瞰。§5.2 の段 1）で建物の外形を示すときにだけ、控えめに使う余地がある。

### 5.2 画面の流れ

| 段 | 見えるもの | 操作 |
| --- | --- | --- |
| 1 拠点 | 拠点の敷地に建物を外形の箱として並べる（建物の高さ＝階数。上面に人数の札） | 建物を選ぶ |
| 2 建物 | 案 B の分解した階の積層。非選択の階は灰の点、各階の札に割り当て / 席数 | 階を選ぶ（左の階の一覧と同期） |
| 3 階 | 選んだ階に寄る。ゾーンの札（ゾーン名と割り当て / 席数）・人の形・空席の輪 | 「平面図で開く」で 2D に切り替え。席を指すと人・席番号 |
| 4 人を探す | 検索・人の詳細の「席を表示」から、その建物・階へカメラが移り、席を黄 + 黒鉛色の細い輪と細い縦の引き出し線・札で示す | 2D の平面図でも同じ印 |

- 色: 既定は「組織で色分け」。その階で席の多い組織（段は設定。既定は 2 段目＝部）3 つまでを categorical の 3 枠（青・橙・緑。`lib/siteColors.ts` と同じ all-pairs の制約）にし、残りは灰の「その他」。色だけに頼らず、凡例に組織名を文字で出す
- 空席は塗らない輪（黒鉛色の細線）、使えない席は淡い斜線。人は「組織の色の小さな人の形」（遠景では点）
- 選択・ホバーの黄（`--signal`）は必ず黒鉛色と組にし、文字には使わない（今の 3D と同じ規則）
- 動き: 階を選んだときのカメラの移動と、非選択の階が淡くなる遷移だけ。`prefers-reduced-motion` では瞬時に切り替える。発光・脈動・粒子は使わない

### 5.3 規模と性能（数千席、低スペックの端末）

- 階ごとに机・人・空席の輪をそれぞれ 1 つの InstancedMesh にする（1 階 3 描画呼び出し + スラブ・輪郭）。ジオメトリは全階で共有
- LOD: 建物の段では、非選択の階は「スラブ + 灰の点」（点は 1 つの InstancedMesh）。端末の性能が低い（`devicePixelRatio` の上限・`hardwareConcurrency`・初回のフレーム時間で判定）ときは非選択の階の点も描かず、札の人数だけにする
- 階が多い建物（目安 12 階超）: 選んだ階の前後 2 階だけを分解して離し、それ以外は詰めて積む（圧縮）
- 札は今の 3D と同じく HTML の重ね・優先度順・衝突で間引く。席番号の札は 3 の段で、カメラに近い席だけ
- `frameloop="demand"`・ポストプロセスなし・影はテクスチャ 1 枚の接地影だけ（モックと同じ）
- データ: 配置図は階ごとに取得（席 3,000 で JSON 約 150 KB、ETag で使い回す）。人の割り当ては階ごとに分けて取り、建物の段では階ごとの件数だけを取る
- WebGL が使えない端末: 3D の代わりに 2D の平面図（SVG）を出す（今の `WebGlBoundary` の代替表示を平面図にする）

### 5.4 2D の簡易フロアマップ

- **表示は SVG**（React で直接描く）。xyflow（React Flow）はノードと辺のグラフのためのもので、座標が固定の平面図には合わない（ズーム・パンは SVG の viewBox で足りる）。依存を増やさない
- 要素: 外形（実線）・コア（斜線）・部屋（細線。会議室は図形と名前だけ）・ゾーン（淡い面と細い枠。立入制限は破線）・机（細線の矩形）・人（組織の色の点）・空席（輪）・使えない席（斜線）・探している人（黄 + 黒鉛色の輪と札）。1 m の格子をごく薄く
- 1 階 3,000 席まで SVG で描く（要素数は約 1 万）。超えたら Canvas に切り替える（同じ描画関数を 2 つの出力で持つ）
- アクセシビリティ: 平面図と同じ内容の表（席番号・人・組織・状態）を並べて置き、キーボードで席を順に辿れる。色だけに頼らない（状態は形で区別）
- 編集（段階 3）: 同じ SVG の上で、外形・ゾーンの頂点と席を格子（既定 100 mm）に吸着させて動かす。編集は改訂の下書きに保存し、承認で有効になる。依存は追加しない

### 5.5 画面の状態（URL）

既存のハッシュのルーター（`lib/router.ts`）の流儀で、3D 表示の画面に `mode` を足す。

```
#/overview-3d?mode=floor&site=<id>&building=<id>&floor=<id>&plan=1&person=<id>&as_of=2026-04-01
```

| 引数 | 意味 |
| --- | --- |
| `mode` | `org`（既定。今の組織モード）/ `floor`（建物・フロアモード） |
| `site`・`building`・`floor` | 段の選択。省略すると上の段 |
| `plan` | `1` で 2D の平面図を開く（3D の右に並べるか、狭い画面では切り替え） |
| `person` | 既存の引数。建物・フロアモードではその人の席へ移る |
| `as_of` | 既存のタイムマシン。その日の配置図と割り当て |
| `color` | `org`（既定）/ `none`（単色。選んだ人・組織だけ色） |

今の「拠点で色分け」（`color=site`）・「拠点で分ける」は組織モードの引数として残し、`mode=floor` では使わない。人の詳細パネル・検索の結果に「席を表示」のリンクを置き、このハッシュへ移る。

### 5.6 フロントの構成

- `web/src/features/floor/`（新規）: `floorModel.ts`（API の応答から階・席・状態を組む。three に依存しない）、`FloorScene3D.tsx`、`FloorPlan2D.tsx`（SVG）、`floorLayout3d.ts`（積層・圧縮・LOD の配置）
- 今の `overview3d` の色（PALETTE）・札の重ね・カメラの寄せ・床の格子のシェーダーは、組織モードとフロアモードで共有する部品に切り出す（切り出しは組織モードの作業が落ち着いてから行う）
- デモ（`web/src/demo`）: 東京本社（本館 6 階）・横浜工場（2 棟。製造フロアのライン）・千葉物流センター（倉庫の持ち場）の配置図と割り当てを `samples/messy-corp` から生成する。合成の大規模データ（`largeDataset.ts`）に 5,000 席を足して性能を確かめる

### 5.7 当日の所在の表示（提供があるときだけ）

- 所在の提供が登録されていて、今日を表示しているときだけ、`GET /floors/{id}/presence` の層を重ねる。タイムマシンの過去・将来の日には出さない
- 席の状態は §3.2 の 3 つのまま増やさない。見える人の所在の席は人（小さな「今日」の印。形の違いで、色だけに頼らない）、見えない人の所在の席は使えない席
- 決まった席の人は、所在が無くても所属上の席として人を出す。「来ていない」「不在」の印は付けない
- 凡例に「当日の所在は連携先の情報です。所在が無いことは不在・在宅・休みを意味しません」と出す。取得は階ごと・今日だけで、`no-store`（ETag で使い回さない）

## 6. API / MCP

### 6.1 REST API

決まった席の参照はすべて `as_of` を受け、時点の規則は既存のまま（今日より後は人事管理者と、先行提供を承認された社内システムだけ）。

| メソッド・パス | 内容 | 種別（§7.2） |
| --- | --- | --- |
| `GET /api/v1/sites/{id}/buildings` | 拠点の建物と階の一覧（階ごとの `seat_count`・`occupied_count`・`vacant_count`・`unavailable_count`。見える人だけで数える。人は返さない） | `org_stats` |
| `GET /api/v1/buildings/{id}` | 建物と階の一覧（同上） | `org_stats` |
| `GET /api/v1/floors/{id}/layout` | その日の配置図（外形・固定の部分・ゾーン・席の置き場所と状態〔`vacant` / `occupied` / `unavailable`〕）。人は返さない。改訂の終了日は返さない（次の改訂の予定を推測させない）。ETag と `Cache-Control: private, no-cache` | `org_read` |
| `GET /api/v1/floors/{id}/occupants` | 階の在籍者と席（`person`・`seat_id`・`seat_code`・`zone_key`・`zone_name`・`role`〔primary / secondary〕・`source`〔seat / zone〕）。席の人は `seat` が見える人だけ、席の無い持ち場の人は `workplace` が見える人だけ。`zone` で絞る。cursor | `person_list`（返却人数の制限） |
| `GET /api/v1/seats` | 席の検索（`floor`〔繰り返し可〕・`building`・`site` のいずれかで範囲、`zone`・`kind`〔fixed / station〕・`feature`・`vacant=true`）。人は返さない | `org_stats` |
| `GET /api/v1/floors/{id}/presence` | 今日の階の所在（提供があるときだけ。§3.4）。`date`・`floor`・`seats`（所在のある席の `status`: `occupied` / `unavailable`）・`items`（見える人の `person`・`seat_id`・`seat_code`・`zone_key`・`zone_name`）。`as_of` を受けない。提供が無ければ 404（`presence-unavailable`）。`Cache-Control: no-store`。監査ログ・変更フィードに残さない | `person_list`（返却人数の制限） |
| `GET /api/v1/people/{id}`・`/people` | 人の応答に `workplace`（建物・階・エリア・`source`）と `seat`（主の席の席番号・座標・階・種類）。`GET /people/{id}` だけ、提供があり・今日で・見えるとき `presence`（今日の階・エリア・席）を足し、その応答は `no-store` | 既存 |

`GET /people` の検索の条件・件数には当日の所在を足さない。予約・所在の書き込みの API は持たない。

### 6.2 MCP ツール

| ツール | 内容 | スコープ | 種別 |
| --- | --- | --- | --- |
| （既存）`get_person`・`search_people` | 人の項目に `workplace`・`seat`。`get_person` だけ、項目の上限に `presence` を明示したクライアントに `presence` | `jinji.read` | 既存 |
| `list_floors` | 拠点（`site_id`）か建物（`building_id`）の階の一覧と席数（席数・使用中・空席・使えない席） | `jinji.read` | `org_stats` |
| `find_vacant_seats` | 階・建物・拠点の範囲で空席（割り当ての無い席）を探す。`zone_key`・`kinds`・`features`・`as_of`。席番号・ゾーン・種類・定員・設備のタグ。人は返さない。最大 100 件 | `jinji.read` | `org_stats` |
| `list_floor_occupants` | 階（・ゾーン）の在籍者と席（REST の `occupants` と同じ）。決まった席だけで、当日の所在は含めない | `jinji.read` | `person_list` |

- 「〇〇さんの席は？」は `search_people` → `get_person` の `seat` で答える（専用のツールを増やさない）
- 公開側を変えるツール（席の割り当て・配置図の変更）・予約のツール・所在の一覧のツールは持たない

## 7. 段階的な実装計画

目安の日数は、AI エージェントが実装し人がレビューする前提の作業日（レビューの待ちは含めない）。

### 段階 0: 決定（数日）

- ADR-0012 の承認、§8 の未決事項への回答

### 段階 1: MVP（約 15〜20 日）

建物・階・座席の静的なマスタ + 決まった席・持ち場の割り当て + 3D / 2D の表示（閲覧のみ）+ デモデータ + 当日の所在の拡張点。

| # | 作業 | 目安 |
| --- | --- | --- |
| 1 | マイグレーション 0019（§2.5）・項目カタログ・RLS・統合テスト（排他制約・horizon の線・所在の表を持たないこと） | 3 日 |
| 2 | ドメインと解決（`WorkplaceMap`: 時点の配置図・割り当て。スナップショットのキャッシュの世代に新しい表を足す）、ポリシーの項目 `workplace`・`seat`・`presence`、`PersonView` の拡張 | 3 日 |
| 3 | 取込: 配置図の JSON・席の CSV・割り当ての CSV（staging → 照合 → 提案 → 承認）、席番号の正規化、混入の検査、拠点の食い違いの修正依頼 | 3 日 |
| 4 | REST（§6.1）・OpenAPI・MCP ツール 3 本・`IPresenceProvider` と所在の認可（テストは固定の値の提供）・REST と MCP の突き合わせのテスト | 2〜3 日 |
| 5 | サンプル（`samples/messy-corp` に建物・階・配置図・割り当て。表記ゆれ・存在しない席番号・拠点の食い違いを含める）とデモデータの生成 | 1〜2 日 |
| 6 | Web: 2D の平面図（SVG・表の代替）、3D の建物・フロアモード（案 B・LOD・人の席へのカメラ移動）、人の詳細・検索から「席を表示」 | 5〜6 日 |
| 7 | スクリーンショット（ライト・ダーク・スマホ）、合成 5,000 席での性能確認 | 1 日 |

### 段階 3: 編集と計画（約 12〜18 日）

- 配置図の編集 UI（SVG・格子への吸着・改訂の下書き → 承認・改訂の差分の表示）
- 割り当ての編集 UI（席をクリックして割り当て、空席の検索）
- 席替えの計画: 異動の発令から席替えが要る人の一覧（補完提案）、将来の日付の割り当ての起案
- Entra ID の officeLocation に階を含める設定（任意）
- 工場の表現の磨き込み（ラインのゾーン・持ち場の定員の表示）

（段階 2 は欠番。当日の所在の保存・座席予約は扱わない。）

### リスク

| リスク | 対処 |
| --- | --- |
| 席替えの予定から内示が漏れる | 将来の割り当てを horizon の線で隠す（RLS とアプリ） |
| 「監視」と受け取られる | jin-ji は所在を保持せず、時刻・入退館の記録を型として受け取らない。所在は提供があるときだけ、今日だけ、狭い既定の公開範囲で表示する。個人別の集計・所在の一覧・配信の経路を作らない |
| 範囲の膨張（座席予約・会議室予約・FM システム化） | §0 の対象外を ADR に明記する。施設の予約と管理は連携先の範疇 |
| 配置図の手入力が総務の負担になり、データが古くなる | CSV / JSON のひな形、既存の座席表（Excel）からの変換手順、改訂の差分の表示。古い配置図は階の札に最終改訂日を出す |
| 正の席が曖昧（jin-ji と外部の施設管理システムの二重管理） | ADR-0012 で「どちらか一方を正にする」を導入先ごとの設定にし、外部を正にしたら jin-ji の編集 UI を閉じる |
| 低スペックの端末で重い | LOD・圧縮・2D への切り替え。合成 5,000 席で初回の描画とフレーム時間を測る |

## 8. 未決事項（優先度順）

1. **座席データの正をどこに置くか**: jin-ji を正にして総務が jin-ji で編集するか、外部の施設管理システムを正にして取り込むだけにするか（導入先ごとの設定にするか）
2. **席の既定の公開範囲**: 階・エリア（`workplace`）は全社公開でよいか。席（`seat`）は全社公開か部内か
3. **当日の所在（`presence`）の既定の公開範囲**: 既定は「本人と同じ部」。課・チームに狭めるか、全社まで広げてよいとするか
4. **将来の席替えを総務に誰の席か見せるか**: 人を伏せた予定の枠を作るか、人事管理者だけにするか
5. **ファシリティ管理者のロールを新設するか**、それとも人事管理者が兼ねるか。承認の 2 人体制を総務の中で組めるか
6. **想定の規模**: 1 階の最大席数、1 拠点の建物数・最大階数。工場の持ち場の粒度（ライン単位で足りるか、持ち場の単位まで要るか）
7. **3D の表現**: 推奨の案 B（分解アクソメ）でよいか。色の既定は「組織で色分け」か「単色 + 選んだものだけ色」か
8. **画面の位置**: 3D 表示の画面のモード（`mode=floor`）にするか、独立した「フロア」の画面にするか
9. **Entra ID の officeLocation** に階を含めるか（席は含めない前提）

## 9. 実装の状況（0019）

MVP（段階 1）のうち Web 以外（スキーマ・解決・取込・REST・MCP・所在の拡張点・テスト・サンプル）を実装した。Web（3D・2D）とデモデータは別の作業。

- コード: `JinJi.Domain.Workplace`（`WorkplaceMap`・`FloorGeometry`・`LayoutJson`・`WorkplaceCodes`、`OrgSnapshot.WorkplaceOf` / `SeatsOf`）、`JinJi.Policy`（項目 `workplace`・`seat`・`presence`、`PersonFields.ExplicitOnly`、`PresencePolicy`、`FieldLimit.ForClient`）、`JinJi.Application.Workplace.WorkplaceDirectoryService`、`JinJi.Application.Presence`（`IPresenceProvider`・`NoPresenceProvider`・`PresenceService`）、`JinJi.Ingest.ProposalPlanner.Workplace`・`JinJi.Connectors.FloorLayoutJson`、`JinJi.Api.WorkplaceEndpoints`、`JinJi.Mcp.Tools.WorkplaceTools`、`db/migrations/0019_buildings_floors_and_seats.sql`
- 採用した既定: 座席データの正は jin-ji（取込で入れ、別の人事管理者が承認）。`workplace`・`seat` は全社公開（部内にするなら `fields: [seat]` の全員の規則を 1 件承認する）。`presence` は本人と同じ部。将来の席替えは人事管理者だけ（ファシリティ管理者のロールは作らない）。Entra ID の officeLocation は変えない
- 人の応答の `seat` は主の席だけ。副の席は階の在籍者（`role = secondary`）で返す
- 見送ったもの: 人の検索の条件 `building`・`floor`・`zone`（階の人の一覧は `occupants`・`list_floor_occupants` で引く）、配置図の起案・承認の専用の API と編集 UI・席替えの計画（段階 3）、変更フィードの種別 `floor`
