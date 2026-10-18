# 建物・フロアモード（物理的な所在の把握）の仕様と実装方針

- ステータス: **検討中（提案）**
- 日付: 2026-10-18
- 関連: [ADR-0012（提案）](../adr/0012-workplace-and-seat-data.md)、[ADR-0002](../adr/0002-data-scope.md)（扱うデータの範囲）、[アーキテクチャ概要 §4.1・§4.7・§5・§7.3](../architecture/overview.md)
- モック: `assets/floor-*.png`（3D の表現 3 案と 2D の簡易フロアマップ。§5.1）

## 0. 目的と位置づけ

いまの 3D 表示は、組織の階層を段（レイヤー）として積み、拠点は組織の属性（色分け・拠点で分ける）として扱っている。新しい **建物・フロアモード** では、拠点を物理的な建物（本社ビル・工場・物流センター）として扱い、

```
拠点（site） → 建物 → 階（フロア） → エリア / ゾーン → 座席（または持ち場）
```

の空間の構造を 3D と 2D の簡易フロアマップで見せる。人が「どの階・どの席にいるか」を、**所属上の席（固定席・持ち場・ホームエリア）** と、将来の段階で **当日の所在（自己申告の座席予約）** の 2 つの意味で把握・管理できるようにする。

jin-ji の原則（概要 §1）との関係:

| 原則 | この機能での扱い |
| --- | --- |
| 正は人事パッケージ | 人事パッケージは座席を持たないことが多い。建物・階・座席のマスタと固定席の割り当ては、**jin-ji が正になり得る初めての情報**になる。外部の施設管理・座席予約システムがあればそちらを正にして取り込む（ADR-0012 の決定 2） |
| 全社公開レベルの情報しか持たない | 固定席は社内の座席表として掲示されるのと同じ水準の情報とみなし、保持してよい。**当日の所在は行動の記録に近く、全社公開レベルを超える**ため、MVP では持たない（§3） |
| AI も人も同じ入口・同じ権限判定 | 席・階は人の項目（ポリシーの `workplace`・`seat`）として PersonProjector を通す。MCP にだけ抜け道を作らない |
| 公開に至る変更は人が承認する | 配置図の改訂と席の割り当ては、起案 → 別の人の承認で公開側に出す（取込の提案と同じ枠組み） |
| 事実と記録の二つの時間を持つ | 配置図の改訂・席の割り当ては有効期間と記録期間を持ち、タイムマシンで過去・（権限があれば）将来の席の配置を見られる |

**対象外**（明記して範囲の膨張を防ぐ）: 会議室の予約、来客の管理、入退館・IC カードのログ、BLE / Wi-Fi などによる位置の測位、CAD・BIM・図面画像の取り込み、動線や滞在時間の分析、個人別の在席率のレポート、勤怠（労働時間の把握は勤怠システムの役目）。

## 1. ユースケースと利用者

| 利用者 | したいこと | 段階 |
| --- | --- | --- |
| 一般社員 | 同僚の席・階を知る（「鈴木さんはどの階？」）。初めて行く拠点で、相手の部署の島を探す。自分の席番号を確かめる | MVP |
| 総務・ファシリティ | 建物・階・座席のマスタを整える。固定席を割り当てる。空席と階の収容（席数・割り当て数）を把握する。レイアウト変更（改訂）を計画する | MVP（閲覧・取込）／ 次段階（編集 UI） |
| 人事 | 異動に伴う席の移動を計画する（4/1 の一斉発令に合わせた席替え）。将来の時点の席配置をタイムマシンで確かめる | 次段階 |
| AI エージェント / MCP | 「〇〇さんの席は？」「3 階の空席は？」「営業第一部はどの階？」に答える。委任元に見える範囲だけ | MVP |
| 社内システム | 内線表・受付の案内表示に、階（・席）を渡す（項目の上限で絞る） | MVP（API）／ Entra の officeLocation は次段階 |

### 働き方ごとの扱い

| 働き方 | 表現 | 備考 |
| --- | --- | --- |
| 固定席 | 人 → 座席（有効期間付き） | 1 人 1 席（主の席）。兼務先の席（副の席）は任意 |
| フリーアドレス | 組織 → ホームエリア（ゾーン）。人ごとの上書きも可 | 拠点の解決（組織の拠点を配下が引き継ぎ、人ごとに上書き。§4.7）と同じ形にする。当日どの席に座るかは次段階の座席予約 |
| 工場の持ち場・ライン | 人 → ゾーン（ライン）または座席（持ち場。`station`、定員 2 以上を許す） | **シフト・勤務帯は持たない**（ADR-0002）。交替で同じ持ち場を使う人は、定員 2 以上の持ち場に重ねて割り当てるだけにし、誰がいつ入るかは持たない |
| 在宅（リモートワーク） | 既存の `remote_work`（有無だけ）。席は持たなくてよい | 「今日は在宅」は記録しない。席が無いことを「不在」と表示しない |
| 出張・外出・休暇 | 持たない | 行動の記録になるため。座席予約が無いことは何も意味しない、と UI に明記する |
| 一時停止中（休職など） | 固定席の割り当ては残してよい（所属と同じ扱い。ADR-0002「一時停止中でも組織図上の所属は維持」） | 席の解放は総務の判断で割り当てを終える。理由は持たない |

## 2. データモデル

### 2.1 構造

```
site（拠点。既存）
 └─ building（建物）              1 拠点に N 棟（本社は 1 棟、工場は複数棟が普通）
     └─ floor（階）                B2・B1・1F・M2F（中二階）・2F … 屋上
         └─ floor_layout（配置図の改訂。有効期間付きの版。起案 → 承認）
             ├─ layout_zone（エリア / ゾーン。多角形）
             └─ layout_seat（席の置き場所。点 + 向き + 机の型）
 seat（座席の不変 ID と席番号。改訂をまたいで同じ席を指す）
 workplace_assignment（人 → 座席 または ゾーン。有効期間付き）
 org_home_zone（組織 → ホームエリア。有効期間付き。配下は引き継ぐ）
```

- **建物と拠点は 1:N**。拠点は「事業所（住所の単位）」で、工場・物流センターは同じ敷地に複数棟がある。本社ビルのような 1 棟の拠点は、建物を 1 つ作るだけで済む。建物の無い拠点（小さな営業所）は今のまま拠点だけで扱う
- **座席の同一性（`seat`）と置き場所（`layout_seat`）を分ける**。レイアウトの改訂で机を動かしても、席番号が同じなら同じ席として割り当てが続く。席番号を変える・席を廃止するのは改訂で表す
- **配置図は改訂（`floor_layout`）の単位で版を持つ**。席・ゾーンを 1 行ずつ版にすると、改訂 1 回で数百行の版が生まれ、承認の単位も分かりにくい。改訂を 1 つの起案として承認し、有効日から切り替える（一斉発令の改編プランと同じ考え方。概要 §4.3）

### 2.2 階の表し方

| 列 | 意味 |
| --- | --- |
| `label` | 表示名（`B1`・`1F`・`M2F`・`屋上` など。導入先の表記） |
| `level_order` | 並び順の整数。地上 n 階 = `n × 10`、地下 n 階 = `-n × 10`、中二階は間の値（`1F` と `2F` の間の `15`）。3D の積み上げの順もこれで決める |
| `kind` | `office`・`factory`（製造フロア）・`warehouse`・`lobby`・`service`（機械室・駐車場）・`other`。表示とまとめ方だけに使う固定の少数（拠点の種別と同じ理由。§4.7） |

### 2.3 簡易フロアマップの「簡易」の範囲

CAD や図面画像を取り込まず、**人が手で描ける・CSV / JSON で書ける最小の形** に限る。

| 要素 | 形 | 上限・規則 |
| --- | --- | --- |
| 座標系 | 階ごとのローカル座標。原点は図面の左下、x は右、y は奥。**整数のミリメートル** | 既定の格子は 100 mm。階の外形の外接矩形は 1 辺 500 m まで |
| 階の外形 | 多角形 1 つ（穴なし） | 頂点 3〜64、自己交差なし |
| 固定の部分（コア・柱・壁で囲った部屋） | 矩形または多角形 + 種類（`core`・`room`・`void`〔吹き抜け〕） | 1 階に 200 個まで。名前（「会議室 A」）は表示用の短い文字 |
| エリア / ゾーン | 多角形 + 種類（`office`・`free_address`・`production_line`・`storage`・`common`・`restricted`） | 1 階に 200 個まで |
| 座席 | 点（机の中心）+ 向き（0・90・180・270、または 15° 刻み）+ 机の型（`desk_1200`・`desk_1400`・`desk_1600`・`station`・`counter` などの既定の型から選ぶ） | 1 階に 3,000 席まで |
| 座席の属性 | 種類（`fixed`・`free`・`station`）・定員（既定 1。`station` だけ 2 以上可）・設備のタグ（許可リスト: `monitor`・`dual_monitor`・`standing`・`phone_booth` など） | **健康・障害に関わるタグ（車椅子対応など）は持たない**。人の割り当てと組み合わさると、ADR-0002 で保持しない障害の情報を推測させるため。必要なら総務の運用（jin-ji の外）で扱う |

保持の形式:

- DB は表と列で持つ（席の座標は `x_mm`・`y_mm` の整数列、多角形は `jsonb` の `[[x, y], …]`）。PostGIS は入れない（拡張を増やさない。必要な計算は外接矩形・点の多角形内判定程度でアプリで足りる）
- 入出力の交換形式は **JSON（jin-ji の配置図の形式 v1）** と、席だけの **CSV**（階・席番号・x・y・向き・種類）。形式はスキーマ（JSON Schema）で検証する
- **SVG は表示と書き出しにだけ使い、取り込まない**。SVG はスクリプトや外部参照を埋め込めるため、取り込むと保存型の XSS の入口になる。取り込みが必要になったら、要素と属性の許可リストで変換する別の手順にする

### 2.4 割り当てと時点

- `workplace_assignment`: 人 → 座席（`seat_id`）またはゾーン（`zone_id`。ライン・ホームエリアの人ごとの上書き）のどちらか一方。`role` は `primary`（主の席）・`secondary`（兼務先などの副の席）
- 二つの時間軸（§4.1）を持つ。過去・将来の席配置は `as_of` で引ける（タイムマシン）
  - 配置図: その日に有効な承認済みの改訂
  - 割り当て: その日に有効な割り当て。割り当て先の席がその日の改訂に無ければ「席なし」（閉じた席を黙って別の席に読み替えない。拠点の上書きの規則と同じ）
- 人のエリアの解決（拠点の解決と同じ形）: 固定席の割り当て → 人ごとのゾーンの割り当て → 主務の組織のホームエリア（無ければ上位組織へたどる。`inherited = true`）
- **将来の割り当て（席替えの予定）は、内示と同じ線で隠す**。異動に伴う席替えは、席が動くこと自体が内示の手がかりになるため、`lower(valid_period) <= jinji_viewer_horizon()` の線を RLS とアプリの両方で引く（§4.7 の将来の拠点の版と同じ）

### 2.5 PostgreSQL のスキーマ案（マイグレーション 0019 の骨子）

既存の表（0017 の `site_version` など）の作法に合わせる。記録期間の保護トリガー（`jinji_guard_recorded_period`）・RLS の FORCE・変更フィードのトリガー・項目カタログへの登録は、すべての表に付ける。

```sql
-- 建物（不変 ID と版）
CREATE TABLE building (
    id       uuid PRIMARY KEY,
    site_id  uuid NOT NULL REFERENCES site (id),
    CONSTRAINT building_id_site_key UNIQUE (id, site_id)
);
CREATE TABLE building_version (
    id               uuid PRIMARY KEY,
    building_id      uuid NOT NULL,
    site_id          uuid NOT NULL,
    code             text NOT NULL,          -- 拠点の中で一意（例: MAIN, B2）
    name             text NOT NULL,          -- 本館・第二工場棟
    sort_order       smallint NOT NULL DEFAULT 0,
    valid_period     daterange NOT NULL,
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    FOREIGN KEY (building_id, site_id) REFERENCES building (id, site_id),
    CONSTRAINT building_version_no_overlap EXCLUDE USING gist (building_id WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period)),
    CONSTRAINT building_version_code_no_overlap EXCLUDE USING gist (site_id WITH =, code WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period))
);

-- 階（不変 ID と版）
CREATE TABLE floor (
    id           uuid PRIMARY KEY,
    building_id  uuid NOT NULL REFERENCES building (id),
    CONSTRAINT floor_id_building_key UNIQUE (id, building_id)
);
CREATE TABLE floor_version (
    id               uuid PRIMARY KEY,
    floor_id         uuid NOT NULL,
    building_id      uuid NOT NULL,
    label            text NOT NULL,          -- B1・1F・M2F
    level_order      integer NOT NULL,       -- 地上 n 階 = n*10、地下 = 負、中二階 = 間の値
    kind             text NOT NULL CHECK (kind IN ('office','factory','warehouse','lobby','service','other')),
    valid_period     daterange NOT NULL,
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    FOREIGN KEY (floor_id, building_id) REFERENCES floor (id, building_id),
    CONSTRAINT floor_version_no_overlap EXCLUDE USING gist (floor_id WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period)),
    CONSTRAINT floor_version_label_no_overlap EXCLUDE USING gist (building_id WITH =, label WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period)),
    CONSTRAINT floor_version_order_no_overlap EXCLUDE USING gist (building_id WITH =, level_order WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period))
);

-- 座席の同一性（席番号は階の中で一意。廃止は改訂から外すことで表す）
CREATE TABLE seat (
    id        uuid PRIMARY KEY,
    floor_id  uuid NOT NULL REFERENCES floor (id),
    code      text NOT NULL CHECK (btrim(code) <> '' AND char_length(code) <= 30 AND code !~ '[[:cntrl:]]'),
    CONSTRAINT seat_floor_code_key UNIQUE (floor_id, code)
);

-- 配置図の改訂（起案 → 別人の承認で有効。承認済みの改訂は中身を変えない）
CREATE TABLE floor_layout (
    id               uuid PRIMARY KEY,
    floor_id         uuid NOT NULL REFERENCES floor (id),
    status           text NOT NULL CHECK (status IN ('draft','approved','rejected')),
    outline          jsonb NOT NULL,         -- 階の外形の多角形 [[x_mm, y_mm], …]
    fixtures         jsonb NOT NULL DEFAULT '[]',  -- コア・部屋・吹き抜け
    grid_mm          integer NOT NULL DEFAULT 100 CHECK (grid_mm BETWEEN 10 AND 1000),
    proposed_by      uuid NOT NULL REFERENCES person (id),
    approved_by      uuid REFERENCES person (id),
    valid_period     daterange NOT NULL,
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT floor_layout_two_person CHECK (approved_by IS NULL OR approved_by <> proposed_by),
    CONSTRAINT floor_layout_size CHECK (pg_column_size(outline) + pg_column_size(fixtures) <= 262144),
    CONSTRAINT floor_layout_no_overlap EXCLUDE USING gist (floor_id WITH =, valid_period WITH &&)
        WHERE (status = 'approved' AND upper_inf(recorded_period))
);
CREATE TABLE layout_zone (
    id            uuid PRIMARY KEY,
    layout_id     uuid NOT NULL REFERENCES floor_layout (id),
    zone_key      uuid NOT NULL,             -- 改訂をまたいで同じゾーンを指す ID（ホームエリア・割り当てが参照）
    name          text NOT NULL CHECK (char_length(name) <= 60 AND name !~ '[[:cntrl:]]'),
    kind          text NOT NULL CHECK (kind IN ('office','free_address','production_line','storage','common','restricted')),
    polygon       jsonb NOT NULL,
    CONSTRAINT layout_zone_key UNIQUE (layout_id, zone_key)
);
CREATE TABLE layout_seat (
    layout_id     uuid NOT NULL REFERENCES floor_layout (id),
    seat_id       uuid NOT NULL REFERENCES seat (id),
    zone_key      uuid,                      -- 席が属するゾーン（任意）
    x_mm          integer NOT NULL CHECK (x_mm BETWEEN 0 AND 500000),
    y_mm          integer NOT NULL CHECK (y_mm BETWEEN 0 AND 500000),
    rotation_deg  smallint NOT NULL DEFAULT 0 CHECK (rotation_deg BETWEEN 0 AND 359 AND rotation_deg % 15 = 0),
    desk_type     text NOT NULL,             -- 既定の型（許可リスト）
    seat_kind     text NOT NULL CHECK (seat_kind IN ('fixed','free','station')),
    capacity      smallint NOT NULL DEFAULT 1 CHECK (capacity BETWEEN 1 AND 20 AND (capacity = 1 OR seat_kind = 'station')),
    features      text[] NOT NULL DEFAULT '{}',  -- 設備のタグ（許可リスト。健康・障害に関わるタグは作らない）
    out_of_service boolean NOT NULL DEFAULT false,
    PRIMARY KEY (layout_id, seat_id)
);
CREATE INDEX layout_seat_seat_idx ON layout_seat (seat_id);

-- 人の席・持ち場（二つの時間軸）
CREATE TABLE workplace_assignment (
    id               uuid PRIMARY KEY,
    person_id        uuid NOT NULL REFERENCES person (id),
    seat_id          uuid REFERENCES seat (id),
    zone_key         uuid,                   -- ライン・人ごとのホームエリア
    floor_id         uuid NOT NULL REFERENCES floor (id),  -- 階で引くため（席・ゾーンの階と同じ値をアプリが入れる）
    role             text NOT NULL CHECK (role IN ('primary','secondary')),
    exclusive        boolean NOT NULL,       -- 定員 1 の席か（席の種類からアプリが決める。他の行を読むトリガーを書かない §5.6）
    valid_period     daterange NOT NULL,
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT workplace_assignment_target CHECK ((seat_id IS NULL) <> (zone_key IS NULL)),
    CONSTRAINT workplace_assignment_exclusive_seat CHECK (NOT exclusive OR seat_id IS NOT NULL),
    -- 1 人の主の席は同時に 1 つ
    CONSTRAINT workplace_assignment_primary_no_overlap EXCLUDE USING gist (person_id WITH =, valid_period WITH &&)
        WHERE (role = 'primary' AND upper_inf(recorded_period)),
    -- 定員 1 の席は同時に 1 人
    CONSTRAINT workplace_assignment_seat_no_overlap EXCLUDE USING gist (seat_id WITH =, valid_period WITH &&)
        WHERE (exclusive AND upper_inf(recorded_period))
);
CREATE INDEX workplace_assignment_floor_idx ON workplace_assignment USING gist (floor_id, valid_period) WHERE upper_inf(recorded_period);
CREATE INDEX workplace_assignment_person_idx ON workplace_assignment (person_id) WHERE upper_inf(recorded_period);

-- 組織のホームエリア（配下は引き継ぐ。アプリが解決）
CREATE TABLE org_home_zone (
    id               uuid PRIMARY KEY,
    org_unit_id      uuid NOT NULL REFERENCES org_unit (id),
    floor_id         uuid NOT NULL REFERENCES floor (id),
    zone_key         uuid NOT NULL,
    valid_period     daterange NOT NULL,
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT org_home_zone_no_overlap EXCLUDE USING gist (org_unit_id WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period))
);
```

DB で検査しないもの（§5.6 の規則: 他の行を読むトリガーを書かない）と、その代わりにアプリが承認時に検査するもの:

- 割り当て先の席・ゾーンが、その有効期間に有効な改訂にあること（改訂の切り替えで席が消えるなら、承認の画面に「割り当てが外れる人」を出す）
- 定員 2 以上の持ち場の割り当て数が定員を超えないこと
- 多角形の形（頂点数・自己交差・外形の内側にあること）、座標が外形の内側にあること
- 席の拠点（建物の拠点）と、人の拠点（§4.7 の解決）が食い違わないこと。食い違いは承認を止めず、要確認の一覧（修正依頼の `site_mismatch`）に出す。正は人事パッケージ側の拠点で、席の方を直すか拠点を直すかは人が判断する

RLS:

- 建物・階・配置図（承認済み）は組織・拠点と同じく全社公開。将来の版・将来に有効になる改訂は `jinji_viewer_horizon()` の線で人事管理者・ファシリティ管理者以外に見せない
- 起案中の改訂は、起案者・ファシリティ管理者・人事管理者だけ
- 割り当ては行としては閲覧者全員に見せ（今日・過去）、項目（`seat`・`workplace`）の判定はアプリ（PersonProjector）が行う（既存の所属と同じ役割分担。§5.4）。将来の割り当ては horizon の線で RLS が止める

## 3. プライバシー・権限

### 3.1 情報の機微さの段階

| 情報 | 機微さ | 扱い |
| --- | --- | --- |
| 建物・階・配置図（席の置き場所・ゾーン） | 会社の情報 | 全社公開。拠点の所在地と同じ |
| 組織のホームエリア（「営業第一部は 3F 西」） | 組織の情報 | 全社公開。組織の拠点と同じ |
| 人の階・エリア（`workplace`） | 社内の座席表と同じ水準 | 既定は全社公開。ポリシーで狭められる |
| 人の席（`seat`。席番号・位置） | 同上だが、粒度が細かい | 既定は全社公開（**未決: 部内を既定にするか**）。`workplace` とは別の項目にして、「階までは全社、席は部内」を設定 1 件で表せるようにする |
| 将来の席（席替えの予定） | 内示の手がかり | 人事管理者だけ（ファシリティ管理者には「誰か」を伏せた予定の枠だけ。§3.3） |
| 当日の所在（座席予約・チェックイン） | 行動の記録。全社公開レベルを超える | **MVP では持たない**。持つなら ADR-0002 を改め、自己申告の予約だけ・短い保持・狭い既定の公開範囲にする（§3.4） |

### 3.2 既存の権限モデルとの整合

- 項目 `workplace`（建物・階・エリア）と `seat`（席番号・座標）を `PersonFields` に足し、ポリシー（§5.2）とクライアントの項目上限の対象にする。上限は「新しい項目を黙って渡さない」原則（0017 の `work_location` の置き換えと同じ）で、既存のクライアントの上限には足さない
- 個人単位の例外（安全上の配慮など）で席を隠せる。ここで **隠した人の席を「割り当てあり（名前なし）」と表示すると、例外があること自体が目立つ**。そこで閲覧者に見せる席の状態を次の 3 つにし、隠した人の席は「使えない席」に含める（保守中・予備の席と区別しない）

  | 表示 | 中身 |
  | --- | --- |
  | 人（名前・組織の色） | 閲覧者に `seat` が見える人の席 |
  | 空席 | 割り当ての無い固定席・フリーアドレスの席 |
  | 使えない席（淡い斜線） | 保守中・予備（`out_of_service`）と、閲覧者に見えない人の席 |

- 件数（階の席数・割り当て数・空席数）は、件数の規則（§7.2）と同じく閲覧者に見える人だけで数える。「使えない席」は空席に数えない
- 空席の検索（「3 階の空席は？」）は人を返さないので種別は `org_stats`。ただし固定席の空席は全社公開してよいが、上の「使えない席」は空席として返さない
- タイムマシン: 過去の時点の席配置は、組織図の過去の時点と同じく誰でも見られる。関係（課内など）の判定は今日の組織で行う（§5.6 の既存の規則）。将来は人事管理者（とファシリティ管理者の伏せた枠）だけ

### 3.3 ロールと職務分掌

- 新しいロール `facility_admin`（ファシリティ管理者）: 建物・階・配置図の改訂の起案・承認、席の割り当ての起案。人の項目の公開範囲の制限は受ける（人事管理者ではない）
- 改訂と割り当ての承認は起案者と別の人（`facility_admin` か `hr_admin`）。§5.5 の職務分掌に足す
- **将来の割り当てと内示**: 4/1 の異動に合わせた席替えは、人事管理者が席の予定を起案し、ファシリティ管理者には「4/1 から 3F-B01 に割り当て予定あり」と **人を伏せた予定の枠** だけを見せる（机・電話の準備に要る情報だけ）。発令日（見せてよい日）が来たら通常の割り当てとして見える。ファシリティ管理者に内示を見せる運用にするかは未決（§8）

### 3.4 当日の所在（次段階）の設計の枠

当日の所在は、日本の職場では「監視されている」と受け取られやすく、労務上の問題（モニタリングの目的・範囲の不明確さ、事実上の勤怠管理への転用）を招きやすい。持つと決めた場合も、次の枠の中に限る。

- **自己申告の予約だけ**。本人が「明日 3F のフリーアドレスの B-05 に座る」と予約する。入退館・IC カード・PC のログオン・Wi-Fi / BLE の測位など、本人の操作によらない記録は取り込まない
- チェックイン（実際に座った時刻）は jin-ji では持たない。外部の座席予約システムがチェックインを持っていても、jin-ji には予約（日付・席）だけを取り込む
- **保持は短く**: 予約の日の翌日（設定、最長 7 日）に消す（保持期限の消去 §4.5 の対象の表に足す）。履歴として残さない・タイムマシンの対象にしない・変更フィード / Webhook に流さない（配信先に行動の記録を溜めさせない）
- **既定の公開範囲は狭く**: 項目 `presence` は既定で同じ部内（`same_level(department)`）など、全社公開より狭い規則を初期のポリシーとして置く（未決）。本人は常に見られる
- **集計の粒度**: 階・エリアの使用率は人数でのみ出し、個人別の在席率・出社日数のレポートは作らない（API も持たない）
- 上長・人事にも「誰が何日出社したか」を出す機能は持たない。勤怠の用途に使わないことを利用目的として明記する
- 監査: 参照は既存の参照ログ（リクエスト単位で person ID の集合）。予約の値そのものは監査ログに書かない
- 個人情報保護法の観点: 利用目的（座席の確保・同僚との合流）を特定して社内に公表し、目的外に使わない。個人情報保護委員会の Q&A が示す従業者のモニタリングの留意点（目的の特定と社内規程への明記・事前の周知・運用の責任者・定期的な確認）を導入時の手順書に書く。労働組合・従業員代表との協議を推奨する
- 在宅・出張・休暇は記録しない。「予約が無い」ことを「不在」「在宅」と表示しない（理由を推測させない。リモートワークを有無だけで持つのと同じ考え方）

### 3.5 MCP での提供範囲

- 人の席・階は `get_person`・`search_people` の人の項目として返す（REST と同じ `PersonView`）。項目の上限に `workplace`・`seat` が無いクライアントには返さない
- 新しいツールは人を返さないもの（階の一覧・空席）と、返却人数の制限を受けるもの（階の在籍者）に分ける（§6）
- 当日の所在（次段階）は、MCP では **本人の予約と、委任元に見える範囲の階ごとの人数** だけにし、「今日オフィスにいる人の一覧」を全社で引けるツールは作らない（AI エージェントによる大量の所在の抽出を防ぐ）

## 4. データの入手経路

| 経路 | 内容 | 段階 |
| --- | --- | --- |
| 配置図の JSON の取込 | 建物・階・外形・コア・ゾーン・席を 1 階分ずつ。改訂の起案になり、別の人の承認で有効 | MVP |
| 席の CSV | 階・席番号・x・y・向き・種類・ゾーン。既存の座席表（Excel）から起こす想定 | MVP |
| 割り当ての CSV | 社員番号・席番号（または階 + ゾーン）・開始日。照合は社員番号（名寄せ済みの人）・席番号の正規化（全角・ハイフンの揺れ。`３F-Ａ０１` → `3F-A01`） | MVP |
| 管理画面での配置図の編集 | SVG の上で外形・ゾーン・席を格子に吸着させて置く。改訂の起案 → 承認 | 次段階 |
| 管理画面での割り当て | 席をクリックして人を割り当てる。席替えの計画（将来の日付）| 次段階 |
| 発令との連動 | 異動の発令から「席替えが要る人」の一覧を作り、席の予定の起案を助ける（補完提案。作業領域） | 次段階 |
| Entra ID の officeLocation | 今は拠点名。設定で「拠点名 + 階」を出せるようにする（席までは出さない。テナント全体に見えるため） | 次段階 |
| 外部の座席予約システム | 予約（日付・席・本人）だけを取り込む。チェックインの時刻・入退館は取り込まない | 当日の所在の段階（要 ADR） |
| 入退館・IC カード | **取り込まない**（§3.4） | 対象外 |

取込は既存の枠組み（staging → 正規化 → 名寄せ → 提案 → 承認。§8）に乗せ、取込の対象に `floor_layouts`・`seats`・`workplace_assignments` を足す。混入の検査（`SensitiveValueDetector`）は席の名前・ゾーンの名前にもかける（ゾーンの名前に個人名や健康の情報を書かれることがあるため）。

## 5. 3D 表示の仕様

### 5.1 表現の案（静止画のモック）

モックは three を使った単体の HTML（web/src には組み込まない）で、同じデータ（6 階建ての本館、3F を選び、検索した人の席を示した状態）を 3 つの表現で描いた。

| 案 | 画像 | 表現 | 良い点 | 弱い点 |
| --- | --- | --- | --- | --- |
| A 建築模型 | `assets/floor-a-light.png` | 階のスラブを実際の階高で積む。選んだ階より上は輪郭だけ、下はごく薄いガラスの外皮と方立て | 建物としての実感が強い。描くのは選んだ階の中身だけなので軽い | 他の階の様子（どこが埋まっているか）が見えない。下の階のガラスが面として重い |
| **B 分解アクソメ（推奨）** | `assets/floor-b-light.png`・`assets/floor-b-dark.png` | 階を大きく離して積み、四隅を細い縦の破線で揃える。全階の机を見せ、選んだ階だけ組織の色と人の形、他の階は灰の点 | 全階の埋まり方と選んだ階の詳細を同時に読める。**組織モードの「段を積む」文法と同じ**で、モードを切り替えても空間の読み方が変わらない | 階が多い建物（20 階以上）は縦に長くなる（§5.3 の圧縮で対処） |
| C 製品モジュール | `assets/floor-c-light.png` | 今の 3D の外装の文法（白い外装・黒鉛色の台座と溝）を建物に当て、選んだ階を引き出しのように手前へ出す | 組織モードのモジュールと見た目の系列がそろう。選んだ階が明確 | 黒鉛色の帯と開口が重く、建物全体が「製品」に見えて所在の地図としては読みにくい。引き出しの動きが演出に寄る |

2D の簡易フロアマップ: `assets/floor-plan-light.png`・`assets/floor-plan-dark.png`（SVG。3F）。

**推奨は案 B**。理由:

1. 組織モード（段＝レイヤーを積む）と同じ「積層」の読み方で、建物・フロアモードに切り替えても利用者が空間を読み直さなくてよい
2. 全階の埋まり方（灰の点）と、選んだ階の詳細（組織の色・人の形・空席の輪）を同時に見せられる。階の一覧（左の在席 / 席数の棒）と 3D が同じことを違う粒度で示す
3. 線は細い縦の破線と輪郭だけで、直角の配線・発光・光のパルスを使わない。色はデータ（組織）と選択（黄 + 黒鉛色の細い輪）にしか使わない。抑制の効いた白基調の模型の質感を保てる
4. 低スペックの端末では、案 A の簡略化（選んだ階以外の机を描かず、スラブと人数の札だけにする）を LOD としてそのまま取り込める

案 C の外装の文法は、建物の一覧（拠点の敷地に建物を並べる俯瞰。§5.2 の段 1）で建物の外形を示すときにだけ、控えめに使う余地がある。

### 5.2 画面の流れ

| 段 | 見えるもの | 操作 |
| --- | --- | --- |
| 1 拠点 | 拠点の敷地に建物を外形の箱として並べる（建物の高さ＝階数。上面に人数の札） | 建物を選ぶ |
| 2 建物 | 案 B の分解した階の積層。非選択の階は灰の点、各階の札に在席 / 席数 | 階を選ぶ（左の階の一覧と同期） |
| 3 階 | 選んだ階に寄る。ゾーンの札（組織名と割り当て / 席数）・人の形・空席の輪 | 「平面図で開く」で 2D に切り替え。席を指すと人・席番号 |
| 4 人を探す | 検索・人の詳細の「席を表示」から、その建物・階へカメラが移り、席を黄 + 黒鉛色の細い輪と細い縦の引き出し線・札で示す | 2D の平面図でも同じ印 |

- 色: 既定は「組織で色分け」。その階で席の多い組織（段は設定。既定は 2 段目＝部）3 つまでを categorical の 3 枠（青・橙・緑。`lib/siteColors.ts` と同じ all-pairs の制約）にし、残りは灰の「その他」。色だけに頼らず、ゾーンの札に組織名を文字で出す
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
- 要素: 外形（実線）・コア（斜線）・部屋（細線）・ゾーン（組織の色の淡い面と細い枠。フリーアドレスは破線）・机（細線の矩形）・人（組織の色の点）・空席（輪）・使えない席（斜線）・探している人（黄 + 黒鉛色の輪と札）。1 m の格子をごく薄く
- 1 階 3,000 席まで SVG で描く（要素数は約 1 万）。超えたら Canvas に切り替える（同じ描画関数を 2 つの出力で持つ）
- アクセシビリティ: 平面図と同じ内容の表（席番号・人・組織・状態）を並べて置き、キーボードで席を順に辿れる。色だけに頼らない（状態は形で区別）
- 編集（次段階）: 同じ SVG の上で、外形・ゾーンの頂点と席を格子（既定 100 mm）に吸着させて動かす。編集は改訂の下書きに保存し、承認で有効になる。依存は追加しない

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

## 6. API / MCP

### 6.1 REST API の案

すべて `as_of` を受け、時点の規則は既存のまま（今日より後は人事管理者と、先行提供を承認された社内システムだけ）。

| メソッド・パス | 内容 | 種別（§7.2） |
| --- | --- | --- |
| `GET /api/v1/sites/{id}/buildings` | 拠点の建物と階の一覧（階ごとの席数・割り当て数・空席数。見える人だけで数える） | `org_stats` |
| `GET /api/v1/buildings/{id}` | 建物と階の一覧（同上） | `org_stats` |
| `GET /api/v1/floors/{id}/layout` | その日の配置図（外形・コア・ゾーン・席の置き場所と状態〔空席 / 使用中 / 使えない席〕）。人は返さない | `org_read` |
| `GET /api/v1/floors/{id}/occupants` | 階の人と席の対応（閲覧者に `seat` が見える人だけ。ページング） | `person_list`（返却人数の制限） |
| `GET /api/v1/seats?floor=&zone=&vacant=true&kind=` | 席の検索（空席など）。人は返さない | `org_stats` |
| `GET /api/v1/people/{id}`・`/people` | 人の応答に `workplace`（建物・階・エリア・`inherited`）と `seat`（席番号・座標・階）を足す。検索の条件に `building`・`floor`・`zone` を足す | 既存 |
| `POST /api/v1/floor-layouts`・`/{id}/approve`・`/{id}/reject` | 配置図の改訂の起案・承認（`jinji.propose`。ファシリティ管理者） | 既存の提案と同じ |

変更フィード・Webhook: 種別に `floor`（配置図の改訂）を足し、割り当ての変更は人（`person`）の変更として届ける（拠点の変更と同じ考え方。0017）。当日の所在は流さない。

### 6.2 MCP ツールの案

| ツール | 内容 | スコープ | 種別 |
| --- | --- | --- | --- |
| （既存）`get_person`・`search_people` | 人の項目に `workplace`・`seat`。`search_people` に `building_ids`・`floor_ids`・`zone_ids` | `jinji.read` | 既存 |
| `list_floors` | 拠点・建物の階の一覧と人数（席数・割り当て数・空席数） | `jinji.read` | `org_stats` |
| `find_vacant_seats` | 階・ゾーン・種類で空席を探す（席番号・ゾーン・設備のタグ。人は返さない。最大 100 件） | `jinji.read` | `org_stats` |
| `list_floor_occupants` | 階（・ゾーン）の在籍者と席（`search_people` の階の条件と同じ結果。返却人数の制限） | `jinji.read` | `person_list` |

- 「〇〇さんの席は？」は `search_people` → `get_person` の `seat` で答える（専用のツールを増やさない）
- 公開側を変えるツール（席の割り当て・配置図の変更）は持たない（§7.1 の決定どおり）。必要になったら「席替えの案」を作業領域に下書きするツールを、修正依頼と同じ扱いで検討する
- サーバーの指示に「`seat` が無いのは見えないか割り当てが無いかで、不在を意味しない」と書く

## 7. 段階的な実装計画

目安の日数は、AI エージェントが実装し人がレビューする前提の作業日（レビューの待ちは含めない）。

### 段階 0: 決定（数日）

- ADR-0012 の承認、§8 の未決事項への回答

### 段階 1: MVP（約 15〜20 日）

建物・階・座席の静的なマスタ + 固定席・持ち場・ホームエリアの割り当て + 3D / 2D の表示（閲覧のみ）+ デモデータ。

| # | 作業 | 目安 |
| --- | --- | --- |
| 1 | マイグレーション 0019（§2.5）・項目カタログ・RLS・統合テスト（排他制約・horizon の線・項目カタログの分類） | 3 日 |
| 2 | ドメインと解決（`WorkplaceSnapshot`: 時点の配置図・割り当て・ホームエリアの継承。スナップショットのキャッシュの世代に新しい表を足す）、ポリシーの項目 `workplace`・`seat`、`PersonView` の拡張 | 3 日 |
| 3 | 取込: 配置図の JSON・席の CSV・割り当ての CSV（staging → 照合 → 提案 → 承認）、席番号の正規化、混入の検査、拠点の食い違いの修正依頼 | 3 日 |
| 4 | REST（§6.1 の参照）・OpenAPI・MCP ツール 3 本・REST と MCP の突き合わせのテスト | 2〜3 日 |
| 5 | サンプル（`samples/messy-corp` に建物・階・配置図・割り当て。表記ゆれ・存在しない席番号・拠点の食い違いを含める）とデモデータの生成 | 1〜2 日 |
| 6 | Web: 2D の平面図（SVG・表の代替）、3D の建物・フロアモード（案 B・LOD・人の席へのカメラ移動）、人の詳細・検索から「席を表示」 | 5〜6 日 |
| 7 | スクリーンショット（ライト・ダーク・スマホ）、合成 5,000 席での性能確認 | 1 日 |

### 段階 2: 編集と計画（約 10〜15 日）

- 配置図の編集 UI（SVG・格子への吸着・改訂の下書き → 承認・改訂の差分の表示）
- 割り当ての編集 UI（席をクリックして割り当て、空席の検索）
- 席替えの計画: 異動の発令から席替えが要る人の一覧（補完提案）、将来の日付の割り当ての起案、ファシリティ管理者への伏せた予定の枠
- Entra ID の officeLocation に階を含める設定（任意）
- 工場の表現の磨き込み（ラインのゾーン・持ち場の定員の表示）

### 段階 3: 当日の所在（ADR-0002 の改定が前提。約 10 日）

- 自己申告の座席予約（本人の予約・取り消し・フリーアドレスの空き）、保持期限の消去、狭い既定のポリシー
- 外部の座席予約システムからの予約の取込（予約だけ）
- MCP: 本人の予約・見える範囲の階ごとの人数

### リスク

| リスク | 対処 |
| --- | --- |
| 席替えの予定から内示が漏れる | 将来の割り当てを horizon の線で隠す（RLS とアプリ）。ファシリティ管理者には人を伏せた枠だけ |
| 「監視」と受け取られ、導入先で反発される | 当日の所在を MVP で持たない。持つ場合も自己申告・短い保持・個人別レポートなし（§3.4）。導入時の手順書に利用目的の公表と周知を書く |
| 配置図の手入力が総務の負担になり、データが古くなる | CSV / JSON のひな形、既存の座席表（Excel）からの変換手順、改訂の差分の表示。古い配置図は階の札に最終改訂日を出す |
| 正の所在が曖昧（jin-ji と外部の座席予約システムの二重管理） | ADR-0012 で「どちらか一方を正にする」を導入先ごとの設定にし、外部を正にしたら jin-ji の編集 UI を閉じる |
| 低スペックの端末で重い | LOD・圧縮・2D への切り替え。合成 5,000 席で初回の描画とフレーム時間を測る |
| 範囲の膨張（会議室予約・FM システム化） | §0 の対象外を ADR に明記する |

## 8. 未決事項（優先度順）

1. **当日の所在を jin-ji で持つか**（ADR-0002 の範囲の変更）。持つなら「自己申告の予約だけ・チェックインなし・翌日に消去」の枠でよいか。持たないなら、外部の座席予約システムへのリンクだけにするか
2. **座席データの正をどこに置くか**: jin-ji を正にして総務が jin-ji で編集するか、外部の施設管理・座席予約システムを正にして取り込むだけにするか（導入先ごとの設定にするか）
3. **席の既定の公開範囲**: 階・エリア（`workplace`）は全社公開でよいか。席（`seat`）は全社公開か部内か
4. **将来の席替えを総務に誰の席か見せるか**: 推奨は「人を伏せた予定の枠だけ」。総務に内示を見せる運用（席の準備のため）を認めるか
5. **ファシリティ管理者のロールを新設するか**、それとも人事管理者が兼ねるか。承認の 2 人体制を総務の中で組めるか
6. **想定の規模**: 1 階の最大席数、1 拠点の建物数・最大階数。工場の持ち場の粒度（ライン単位で足りるか、持ち場の単位まで要るか）
7. **フリーアドレスの組織のホームエリア**を持つか（MVP に入れるか）
8. **3D の表現**: 推奨の案 B（分解アクソメ）でよいか。色の既定は「組織で色分け」か「単色 + 選んだものだけ色」か
9. **画面の位置**: 3D 表示の画面のモード（`mode=floor`）にするか、独立した「フロア」の画面にするか
10. **Entra ID の officeLocation** に階を含めるか（席は含めない前提）
