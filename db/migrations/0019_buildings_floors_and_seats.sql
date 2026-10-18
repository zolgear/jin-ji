-- 0019: 建物・階・簡易フロアマップ（配置図の改訂・ゾーン・席）と、人の席・持ち場
-- （ADR-0012、建物・フロアモードの仕様 §2・§3、アーキテクチャ概要 §4.8）
--
-- 内容
-- 1. 建物（building / building_version）と階（floor / floor_version）。拠点（0017）と同じく不変 ID と版（二つの時間軸）に分ける
--    - 建物は拠点に属する（1 拠点に N 棟）。コードは拠点の中で、階の表示名・並び順は建物の中で、現在記録中・有効期間が重なる版で一意
--    - 階の並び順（level_order）は地上 n 階 = n × 10、地下 n 階 = -n × 10、中二階は間の値（1F と 2F の間の 15）
-- 2. 座席の同一性（seat）と置き場所（layout_seat）を分ける。席番号は階の中で一意で、改訂をまたいで同じ席を指す
-- 3. 配置図の改訂（floor_layout）と、改訂ごとのゾーン（layout_zone）・席の置き場所（layout_seat）
--    - floor_layout には承認済みの改訂だけを置く。起案（下書き）は取込の作業領域の変更提案（change_proposal）が持ち、
--      別の人事管理者の承認で公開側に出る（起案者 ≠ 承認者は CHECK 制約でも守る）。承認済みの改訂の中身は変えない
--      （ゾーン・席の表には UPDATE を与えない。改訂の有効期間を区切るときは、記録期間を閉じて写しの改訂を足す）
--    - 座標は階ごとのローカル座標（整数のミリメートル。原点は左下、x は右、y は奥）。外形・ゾーンは多角形（頂点 3〜64）、
--      席は点 + 向き（15° 刻み）+ 机の型（許可リスト）。自己交差・外形の内側にあることはアプリ（取込の照合）が検査する
--    - 設備のタグは許可リストに限り、健康・障害に関わるタグ（車椅子対応など）は作らない（人の割り当てと組み合わさると障害の情報を推測させるため）
--    - 席の置き場所の階と席の階、席のゾーンと改訂のゾーンは、複合外部キーで同じ階・同じ改訂に縛る（他の行を読むトリガーは書かない）
-- 4. 人の席・持ち場（workplace_assignment。二つの時間軸）: 人 → 座席かゾーン（工場のライン・倉庫のエリアなど席の無い持ち場）のどちらか一方
--    - 1 人の主の席は同時に 1 つ、定員 1 の席（exclusive）は同時に 1 人（排他制約）。exclusive は席の種類からアプリが決める
--    - 定員 2 以上の持ち場の割り当て数・割り当て先の席がその期間の改訂にあることはアプリが検査する（他の行を読むトリガーを書かない §5.6）
--    - シフト・勤務帯は持たない（ADR-0002）。交替で同じ持ち場を使う人は、定員 2 以上の持ち場に重ねて割り当てるだけ
-- 5. 行レベルセキュリティ: 建物・階・配置図は組織・拠点と同じく全社公開。将来の版・将来に有効になる改訂・将来の割り当て（席替えの予定）は
--    内示と同じ線（lower(valid_period) <= jinji_viewer_horizon()）で人事管理者以外に見せない。割り当ては行としては閲覧者全員に見せ（今日・過去）、
--    項目（workplace・seat）の判定はアプリ（PersonProjector）が行う（所属と同じ役割分担。§5.4）
-- 6. 変更フィード: 割り当ての変更は人の変更として記録する（人の応答の workplace・seat が変わるため）。
--    建物・階の版、配置図の改訂の追加は、その階に席・持ち場を持つ人の変更として記録する（階・建物の名前、席の位置が変わるため）。
--    変更フィードの種別は増やさない（floor の種別は配置図の編集とあわせて段階 3 で検討する）
-- 7. 取込の許可リスト: 配置図（JSON・席の CSV）と割り当て（CSV）の取込の対象・項目・提案の種別・修正依頼の種別
--
-- 範囲: 人事として持つ「人の決まった席・持ち場」だけ。フリーアドレス・会議室などの施設の予約と管理、当日の所在（予約・入退館・測位）は
-- 人事の範疇を超える施設管理で、連携先の範疇なので持たない（配置図の会議室などは表示用の図形 fixtures だけ。予約・状態は持たない）。
-- 当日の所在は保存せず、外部の提供（JinJi.Application.Presence.IPresenceProvider）を注入して表示する
--
-- 他の行を読むトリガー・RLS について
-- - 変更フィードの記録のトリガーは、割り当てを読んで影響する人を求める（0017 と同じく書き込むのは hr_admin で、結果は閲覧者によって変わらない）
-- - ゾーン・席の置き場所の RLS は、改訂（floor_layout）が見えるかを副問い合わせで読む（改訂の RLS がそのまま効く）

-- ---------------------------------------------------------------------------
-- 1. 建物と階
-- ---------------------------------------------------------------------------

CREATE TABLE building (
    id       uuid PRIMARY KEY,
    site_id  uuid NOT NULL REFERENCES site (id),
    CONSTRAINT building_id_site_key UNIQUE (id, site_id)
);
COMMENT ON TABLE building IS '建物の不変 ID。拠点に属する（1 拠点に N 棟）。コード・名称・並び順は building_version に版として持つ';

CREATE TABLE building_version (
    id               uuid      PRIMARY KEY,
    building_id      uuid      NOT NULL,
    site_id          uuid      NOT NULL,
    code             text      NOT NULL,
    name             text      NOT NULL,
    sort_order       smallint  NOT NULL DEFAULT 0,
    valid_period     daterange NOT NULL,
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT building_version_building_site_fkey FOREIGN KEY (building_id, site_id) REFERENCES building (id, site_id),
    CONSTRAINT building_version_code_check CHECK (btrim(code) <> '' AND char_length(code) <= 30 AND code !~ '[[:cntrl:]]'),
    CONSTRAINT building_version_name_check CHECK (btrim(name) <> '' AND char_length(name) <= 100 AND name !~ '[[:cntrl:]]'),
    CONSTRAINT building_version_sort_order_check CHECK (sort_order BETWEEN -999 AND 999),
    CONSTRAINT building_version_valid_period_check CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT building_version_recorded_period_check CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    CONSTRAINT building_version_no_overlap
        EXCLUDE USING gist (building_id WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period)),
    -- 同じ拠点で、現在記録中かつ有効期間が重なる版に同じ建物コードを使わない
    CONSTRAINT building_version_code_no_overlap
        EXCLUDE USING gist (site_id WITH =, code WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period))
);
COMMENT ON TABLE building_version IS '建物の版（bitemporal）';
COMMENT ON COLUMN building_version.site_id IS '建物の拠点（building.site_id と同じ値。建物コードの一意性の排他制約に使う）';
COMMENT ON COLUMN building_version.sort_order IS '拠点の中の建物の並び順（小さいほど先）';
CREATE INDEX building_version_building_idx ON building_version (building_id);

CREATE TABLE floor (
    id           uuid PRIMARY KEY,
    building_id  uuid NOT NULL REFERENCES building (id),
    CONSTRAINT floor_id_building_key UNIQUE (id, building_id)
);
COMMENT ON TABLE floor IS '階の不変 ID。建物に属する。表示名・並び順・種類は floor_version に版として持つ';

CREATE TABLE floor_version (
    id               uuid      PRIMARY KEY,
    floor_id         uuid      NOT NULL,
    building_id      uuid      NOT NULL,
    label            text      NOT NULL,
    level_order      integer   NOT NULL,
    floor_kind       text      NOT NULL,
    valid_period     daterange NOT NULL,
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT floor_version_floor_building_fkey FOREIGN KEY (floor_id, building_id) REFERENCES floor (id, building_id),
    CONSTRAINT floor_version_label_check CHECK (btrim(label) <> '' AND char_length(label) <= 20 AND label !~ '[[:cntrl:]]'),
    CONSTRAINT floor_version_level_order_check CHECK (level_order BETWEEN -1000 AND 2000),
    -- 事務所 / 製造フロア / 倉庫 / ロビー / 機械室・駐車場 / その他（表示とまとめ方にだけ使う固定の少数。拠点の種別と同じ理由）
    CONSTRAINT floor_version_floor_kind_check CHECK (floor_kind IN ('office', 'factory', 'warehouse', 'lobby', 'service', 'other')),
    CONSTRAINT floor_version_valid_period_check CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT floor_version_recorded_period_check CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    CONSTRAINT floor_version_no_overlap
        EXCLUDE USING gist (floor_id WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period)),
    CONSTRAINT floor_version_label_no_overlap
        EXCLUDE USING gist (building_id WITH =, label WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period)),
    CONSTRAINT floor_version_order_no_overlap
        EXCLUDE USING gist (building_id WITH =, level_order WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period))
);
COMMENT ON TABLE floor_version IS '階の版（bitemporal）';
COMMENT ON COLUMN floor_version.label IS '階の表示名（B1・1F・M2F・屋上など導入先の表記）。建物の中で一意';
COMMENT ON COLUMN floor_version.level_order IS '並び順（地上 n 階 = n × 10、地下 n 階 = -n × 10、中二階は間の値）。3D の積み上げの順。建物の中で一意';
COMMENT ON COLUMN floor_version.floor_kind IS '階の種類（固定の少数。表示とまとめ方にだけ使う）';
CREATE INDEX floor_version_floor_idx ON floor_version (floor_id);

-- ---------------------------------------------------------------------------
-- 2. 座席の同一性
-- ---------------------------------------------------------------------------

CREATE TABLE seat (
    id        uuid PRIMARY KEY,
    floor_id  uuid NOT NULL REFERENCES floor (id),
    code      text NOT NULL,
    CONSTRAINT seat_code_check CHECK (btrim(code) <> '' AND char_length(code) <= 30 AND code !~ '[[:cntrl:]]'),
    CONSTRAINT seat_floor_code_key UNIQUE (floor_id, code),
    CONSTRAINT seat_id_floor_key UNIQUE (id, floor_id)
);
COMMENT ON TABLE seat IS '座席の不変 ID と席番号（階の中で一意）。置き場所は改訂ごとの layout_seat。廃止は改訂から外すことで表す';
COMMENT ON COLUMN seat.code IS '席番号（正規化した表記。例: 3F-A01）';

-- ---------------------------------------------------------------------------
-- 3. 配置図の改訂
-- ---------------------------------------------------------------------------

-- 多角形 [[x_mm, y_mm], …]（頂点 3〜64、座標は 0〜500,000 の整数）。行の値だけを見る（他の行を読まない）
CREATE FUNCTION jinji_polygon_valid(p jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE
        WHEN p IS NULL OR jsonb_typeof(p) <> 'array' THEN false
        WHEN jsonb_array_length(p) NOT BETWEEN 3 AND 64 THEN false
        ELSE NOT EXISTS (
            SELECT 1 FROM jsonb_array_elements(p) AS v(pt)
            WHERE CASE
                WHEN jsonb_typeof(v.pt) <> 'array' THEN true
                WHEN jsonb_array_length(v.pt) <> 2 THEN true
                WHEN jsonb_typeof(v.pt -> 0) <> 'number' OR jsonb_typeof(v.pt -> 1) <> 'number' THEN true
                ELSE (v.pt ->> 0)::numeric NOT BETWEEN 0 AND 500000
                  OR (v.pt ->> 1)::numeric NOT BETWEEN 0 AND 500000
                  OR (v.pt ->> 0)::numeric <> trunc((v.pt ->> 0)::numeric)
                  OR (v.pt ->> 1)::numeric <> trunc((v.pt ->> 1)::numeric)
            END)
    END
$$;
COMMENT ON FUNCTION jinji_polygon_valid(jsonb) IS '配置図の多角形の形（頂点 3〜64、整数のミリメートル 0〜500,000）';

-- 固定の部分（コア・部屋・吹き抜け）[{kind, name?, polygon}]（200 個まで）
CREATE FUNCTION jinji_layout_fixtures_valid(f jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE
        WHEN f IS NULL OR jsonb_typeof(f) <> 'array' THEN false
        WHEN jsonb_array_length(f) > 200 THEN false
        ELSE NOT EXISTS (
            SELECT 1 FROM jsonb_array_elements(f) AS e(item)
            WHERE CASE
                WHEN jsonb_typeof(e.item) <> 'object' THEN true
                WHEN EXISTS (SELECT 1 FROM jsonb_object_keys(e.item) AS k WHERE k NOT IN ('kind', 'name', 'polygon')) THEN true
                WHEN NOT coalesce(e.item ->> 'kind' IN ('core', 'room', 'void'), false) THEN true
                WHEN e.item ? 'name' AND (jsonb_typeof(e.item -> 'name') <> 'string'
                    OR char_length(e.item ->> 'name') > 60 OR (e.item ->> 'name') ~ '[[:cntrl:]]') THEN true
                ELSE NOT public.jinji_polygon_valid(e.item -> 'polygon')
            END)
    END
$$;
COMMENT ON FUNCTION jinji_layout_fixtures_valid(jsonb) IS '配置図の固定の部分（core・room・void、名前は 60 文字まで、多角形）の形';

CREATE TABLE floor_layout (
    id                     uuid      PRIMARY KEY,
    floor_id               uuid      NOT NULL REFERENCES floor (id),
    outline                jsonb     NOT NULL,
    fixtures               jsonb     NOT NULL DEFAULT '[]',
    grid_mm                integer   NOT NULL DEFAULT 100,
    proposed_by_person_id  uuid      NOT NULL REFERENCES person (id),
    approved_by_person_id  uuid      NOT NULL REFERENCES person (id),
    valid_period           daterange NOT NULL,
    recorded_period        tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT floor_layout_outline_check CHECK (public.jinji_polygon_valid(outline)),
    CONSTRAINT floor_layout_fixtures_check CHECK (public.jinji_layout_fixtures_valid(fixtures)),
    CONSTRAINT floor_layout_grid_mm_check CHECK (grid_mm BETWEEN 10 AND 1000),
    CONSTRAINT floor_layout_size_check CHECK (pg_column_size(outline) + pg_column_size(fixtures) <= 262144),
    -- 職務分掌: 改訂の起案者（取込の実行者）は承認できない
    CONSTRAINT floor_layout_two_person CHECK (approved_by_person_id <> proposed_by_person_id),
    CONSTRAINT floor_layout_valid_period_check CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT floor_layout_recorded_period_check CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    CONSTRAINT floor_layout_no_overlap
        EXCLUDE USING gist (floor_id WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period)),
    CONSTRAINT floor_layout_id_floor_key UNIQUE (id, floor_id)
);
COMMENT ON TABLE floor_layout IS '配置図の承認済みの改訂（bitemporal）。起案は取込の作業領域（change_proposal）が持つ。中身（ゾーン・席の置き場所）は変えない';
COMMENT ON COLUMN floor_layout.outline IS '階の外形の多角形 [[x_mm, y_mm], …]（階ごとのローカル座標。原点は左下）';
COMMENT ON COLUMN floor_layout.fixtures IS '固定の部分（コア・壁で囲った部屋・吹き抜け）[{kind, name, polygon}]。名前は表示用の短い文字';
COMMENT ON COLUMN floor_layout.grid_mm IS '編集の格子の間隔（ミリメートル）';
COMMENT ON COLUMN floor_layout.proposed_by_person_id IS '改訂の起案者（取込の実行者）';
COMMENT ON COLUMN floor_layout.approved_by_person_id IS '改訂の承認者（起案者と別の人事管理者）';
CREATE INDEX floor_layout_floor_idx ON floor_layout (floor_id);

CREATE TABLE layout_zone (
    layout_id  uuid  NOT NULL REFERENCES floor_layout (id),
    zone_key   uuid  NOT NULL,
    name       text  NOT NULL,
    zone_kind  text  NOT NULL,
    polygon    jsonb NOT NULL,
    PRIMARY KEY (layout_id, zone_key),
    CONSTRAINT layout_zone_name_check CHECK (btrim(name) <> '' AND char_length(name) <= 60 AND name !~ '[[:cntrl:]]'),
    -- 事務所 / 製造ライン / 保管 / 共用 / 立入制限
    CONSTRAINT layout_zone_zone_kind_check CHECK (zone_kind IN ('office', 'production_line', 'storage', 'common', 'restricted')),
    CONSTRAINT layout_zone_polygon_check CHECK (public.jinji_polygon_valid(polygon))
);
COMMENT ON TABLE layout_zone IS '改訂ごとのエリア / ゾーン（多角形）。zone_key は改訂をまたいで同じゾーンを指す（割り当てが参照する）';
COMMENT ON COLUMN layout_zone.name IS 'ゾーンの名前（表示用。取込で保持しない情報の混入を検査する）';

CREATE TABLE layout_seat (
    layout_id       uuid     NOT NULL,
    floor_id        uuid     NOT NULL,
    seat_id         uuid     NOT NULL,
    zone_key        uuid,
    x_mm            integer  NOT NULL,
    y_mm            integer  NOT NULL,
    rotation_deg    smallint NOT NULL DEFAULT 0,
    desk_type       text     NOT NULL,
    seat_kind       text     NOT NULL,
    capacity        smallint NOT NULL DEFAULT 1,
    features        text[]   NOT NULL DEFAULT '{}',
    out_of_service  boolean  NOT NULL DEFAULT false,
    PRIMARY KEY (layout_id, seat_id),
    -- 席の置き場所の階 = 改訂の階 = 席の階（複合外部キー）。席のゾーンは同じ改訂のゾーン
    CONSTRAINT layout_seat_layout_floor_fkey FOREIGN KEY (layout_id, floor_id) REFERENCES floor_layout (id, floor_id),
    CONSTRAINT layout_seat_seat_floor_fkey FOREIGN KEY (seat_id, floor_id) REFERENCES seat (id, floor_id),
    CONSTRAINT layout_seat_zone_fkey FOREIGN KEY (layout_id, zone_key) REFERENCES layout_zone (layout_id, zone_key),
    CONSTRAINT layout_seat_position_check CHECK (x_mm BETWEEN 0 AND 500000 AND y_mm BETWEEN 0 AND 500000),
    CONSTRAINT layout_seat_rotation_check CHECK (rotation_deg BETWEEN 0 AND 359 AND rotation_deg % 15 = 0),
    CONSTRAINT layout_seat_desk_type_check CHECK (desk_type IN (
        'desk_1000', 'desk_1200', 'desk_1400', 'desk_1600', 'desk_1800', 'station', 'counter', 'booth')),
    -- 固定席 / 持ち場。定員 2 以上は持ち場だけ
    CONSTRAINT layout_seat_seat_kind_check CHECK (seat_kind IN ('fixed', 'station')),
    CONSTRAINT layout_seat_capacity_check CHECK (capacity BETWEEN 1 AND 20 AND (capacity = 1 OR seat_kind = 'station')),
    -- 設備のタグ（許可リスト）。健康・障害に関わるタグは作らない
    CONSTRAINT layout_seat_features_check CHECK (
        cardinality(features) <= 8
        AND features <@ ARRAY['monitor', 'dual_monitor', 'standing', 'phone_booth', 'docking_station', 'locker', 'quiet']::text[])
);
COMMENT ON TABLE layout_seat IS '改訂ごとの席の置き場所（点 + 向き + 机の型）と属性。席番号が同じなら改訂をまたいで同じ席（seat）';
COMMENT ON COLUMN layout_seat.floor_id IS '階（改訂・席と同じ値。複合外部キーで同じ階に縛る）';
COMMENT ON COLUMN layout_seat.zone_key IS '席が属するゾーン（任意。同じ改訂のゾーン）';
COMMENT ON COLUMN layout_seat.desk_type IS '机の型（既定の型の許可リスト）';
COMMENT ON COLUMN layout_seat.seat_kind IS '席の種類（fixed 固定席 / station 持ち場）';
COMMENT ON COLUMN layout_seat.capacity IS '定員（既定 1。持ち場だけ 2 以上を許す）';
COMMENT ON COLUMN layout_seat.features IS '設備のタグ（許可リスト。健康・障害に関わるタグは作らない）';
COMMENT ON COLUMN layout_seat.out_of_service IS '使えない席（保守中・予備）';
CREATE INDEX layout_seat_seat_idx ON layout_seat (seat_id);

-- ---------------------------------------------------------------------------
-- 4. 人の席・持ち場
-- ---------------------------------------------------------------------------

CREATE TABLE workplace_assignment (
    id               uuid      PRIMARY KEY,
    person_id        uuid      NOT NULL REFERENCES person (id),
    floor_id         uuid      NOT NULL REFERENCES floor (id),
    seat_id          uuid,
    zone_key         uuid,
    role             text      NOT NULL,
    exclusive        boolean   NOT NULL,
    valid_period     daterange NOT NULL,
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT workplace_assignment_seat_floor_fkey FOREIGN KEY (seat_id, floor_id) REFERENCES seat (id, floor_id),
    -- 主の席 / 副の席（兼務先など）
    CONSTRAINT workplace_assignment_role_check CHECK (role IN ('primary', 'secondary')),
    -- 座席かゾーンのどちらか一方
    CONSTRAINT workplace_assignment_target CHECK ((seat_id IS NULL) <> (zone_key IS NULL)),
    CONSTRAINT workplace_assignment_exclusive_seat CHECK (NOT exclusive OR seat_id IS NOT NULL),
    CONSTRAINT workplace_assignment_valid_period_check CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT workplace_assignment_recorded_period_check CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    -- 1 人の主の席は同時に 1 つ
    CONSTRAINT workplace_assignment_primary_no_overlap
        EXCLUDE USING gist (person_id WITH =, valid_period WITH &&)
        WHERE (role = 'primary' AND upper_inf(recorded_period)),
    -- 定員 1 の席は同時に 1 人
    CONSTRAINT workplace_assignment_seat_no_overlap
        EXCLUDE USING gist (seat_id WITH =, valid_period WITH &&)
        WHERE (exclusive AND upper_inf(recorded_period))
);
COMMENT ON TABLE workplace_assignment IS '人の席・持ち場（bitemporal）。座席か、ゾーン（工場のライン・倉庫のエリアなど席の無い持ち場）のどちらか一方。シフト・勤務帯は持たない';
COMMENT ON COLUMN workplace_assignment.floor_id IS '階（席・ゾーンの階と同じ値をアプリが入れる。階で引くため）';
COMMENT ON COLUMN workplace_assignment.zone_key IS 'ゾーン（工場のライン・倉庫のエリアなど席の無い持ち場。改訂をまたぐゾーンの ID）';
COMMENT ON COLUMN workplace_assignment.role IS 'primary 主の席 / secondary 副の席（兼務先など）';
COMMENT ON COLUMN workplace_assignment.exclusive IS '定員 1 の席か（席の種類からアプリが決める。排他制約に使う）';
CREATE INDEX workplace_assignment_floor_idx ON workplace_assignment USING gist (floor_id, valid_period) WHERE upper_inf(recorded_period);
CREATE INDEX workplace_assignment_person_idx ON workplace_assignment (person_id) WHERE upper_inf(recorded_period);

-- ---------------------------------------------------------------------------
-- 記録期間の保護
-- ---------------------------------------------------------------------------

CREATE TRIGGER building_version_guard_recorded_period BEFORE UPDATE ON building_version
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();
CREATE TRIGGER floor_version_guard_recorded_period BEFORE UPDATE ON floor_version
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();
CREATE TRIGGER floor_layout_guard_recorded_period BEFORE UPDATE ON floor_layout
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();
CREATE TRIGGER workplace_assignment_guard_recorded_period BEFORE UPDATE ON workplace_assignment
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();

-- ---------------------------------------------------------------------------
-- 6. 行レベルセキュリティと権限
-- ---------------------------------------------------------------------------

ALTER TABLE building ENABLE ROW LEVEL SECURITY;
ALTER TABLE building FORCE ROW LEVEL SECURITY;
ALTER TABLE building_version ENABLE ROW LEVEL SECURITY;
ALTER TABLE building_version FORCE ROW LEVEL SECURITY;
ALTER TABLE floor ENABLE ROW LEVEL SECURITY;
ALTER TABLE floor FORCE ROW LEVEL SECURITY;
ALTER TABLE floor_version ENABLE ROW LEVEL SECURITY;
ALTER TABLE floor_version FORCE ROW LEVEL SECURITY;
ALTER TABLE seat ENABLE ROW LEVEL SECURITY;
ALTER TABLE seat FORCE ROW LEVEL SECURITY;
ALTER TABLE floor_layout ENABLE ROW LEVEL SECURITY;
ALTER TABLE floor_layout FORCE ROW LEVEL SECURITY;
ALTER TABLE layout_zone ENABLE ROW LEVEL SECURITY;
ALTER TABLE layout_zone FORCE ROW LEVEL SECURITY;
ALTER TABLE layout_seat ENABLE ROW LEVEL SECURITY;
ALTER TABLE layout_seat FORCE ROW LEVEL SECURITY;
ALTER TABLE workplace_assignment ENABLE ROW LEVEL SECURITY;
ALTER TABLE workplace_assignment FORCE ROW LEVEL SECURITY;

-- 不変 ID: 閲覧者が設定されていれば参照でき、追加は hr_admin だけ
CREATE POLICY building_select ON building FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY building_insert ON building FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY floor_select ON floor FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY floor_insert ON floor FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY seat_select ON seat FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY seat_insert ON seat FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- 版・改訂・割り当て: 将来の行（新設・改修・席替えの予定）は内示と同じ線（上限 = 業務日付 + 先行提供の日数）
CREATE POLICY building_version_select ON building_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));
CREATE POLICY building_version_insert ON building_version FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY building_version_update ON building_version FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

CREATE POLICY floor_version_select ON floor_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));
CREATE POLICY floor_version_insert ON floor_version FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY floor_version_update ON floor_version FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- 改訂の承認者は、変更提案を承認した閲覧者（アプリが記録する。提案の承認者は閲覧者本人としてしか記録できない 0005）。
-- 有効期間を区切る写しの改訂は元の起案者・承認者のまま足す（中身は同じ改訂）ので、承認者 = 閲覧者は RLS では縛らない（起案者 ≠ 承認者は CHECK 制約）
CREATE POLICY floor_layout_select ON floor_layout FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));
CREATE POLICY floor_layout_insert ON floor_layout FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY floor_layout_update ON floor_layout FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- ゾーン・席の置き場所: 改訂が見えるときだけ（改訂の RLS がそのまま効く）
CREATE POLICY layout_zone_select ON layout_zone FOR SELECT USING (
    (SELECT jinji_viewer_is_set()) AND EXISTS (SELECT 1 FROM floor_layout l WHERE l.id = layout_zone.layout_id));
CREATE POLICY layout_zone_insert ON layout_zone FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY layout_seat_select ON layout_seat FOR SELECT USING (
    (SELECT jinji_viewer_is_set()) AND EXISTS (SELECT 1 FROM floor_layout l WHERE l.id = layout_seat.layout_id));
CREATE POLICY layout_seat_insert ON layout_seat FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

CREATE POLICY workplace_assignment_select ON workplace_assignment FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));
CREATE POLICY workplace_assignment_insert ON workplace_assignment FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY workplace_assignment_update ON workplace_assignment FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

GRANT SELECT, INSERT ON building, building_version, floor, floor_version, seat, floor_layout, layout_zone, layout_seat,
    workplace_assignment TO jinji_app;
GRANT UPDATE (recorded_period) ON building_version, floor_version, floor_layout, workplace_assignment TO jinji_app;

-- ---------------------------------------------------------------------------
-- 6. 変更フィード
-- ---------------------------------------------------------------------------

-- その日に階 p_floor に席・持ち場を持つ人
CREATE FUNCTION jinji_floor_people(p_floor uuid, p_date date) RETURNS SETOF uuid
LANGUAGE sql STABLE AS $$
    SELECT DISTINCT w.person_id FROM workplace_assignment w
    WHERE w.floor_id = p_floor AND upper_inf(w.recorded_period) AND w.valid_period @> p_date
$$;
COMMENT ON FUNCTION jinji_floor_people(uuid, date) IS '変更フィード: その日に階に席・持ち場を持つ人';

-- 階（複数）の変更を、その階に席・エリアを持つ人の変更として記録する
CREATE FUNCTION jinji_record_floor_dependents(p_floors uuid[], p_visible date, p_member_date date, p_withdrawn boolean) RETURNS void
LANGUAGE sql AS $$
    INSERT INTO change_event (entity_type, entity_id, effective_date, withdrawn)
    SELECT DISTINCT 'person', p, p_visible, p_withdrawn FROM unnest(p_floors) AS f, jinji_floor_people(f, p_member_date) AS p
$$;
COMMENT ON FUNCTION jinji_record_floor_dependents(uuid[], date, date, boolean) IS
    '変更フィード: 建物・階の版、配置図の改訂の変更を、その階に席・持ち場を持つ人の変更として記録する';

-- 0017 の記録のトリガー関数に、建物・階の版・配置図の改訂・割り当てを足す
CREATE OR REPLACE FUNCTION jinji_record_change() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_type text;
    v_id uuid;
    v_date date;
    v_prior record;
BEGIN
    CASE TG_TABLE_NAME
        WHEN 'org_unit_version' THEN
            v_type := 'org_unit';
            v_id := NEW.org_unit_id;
            v_date := jinji_change_visible_from(NEW.valid_period);
            -- 組織の拠点が変わったら、拠点を引き継ぐ主務の所属者の拠点も変わる（新しい組織は所属者がまだいないので記録しない）
            SELECT p.found, p.site_id INTO v_prior FROM jinji_org_unit_prior_site(NEW.org_unit_id, lower(NEW.valid_period), NEW.id) AS p;
            IF v_prior.found AND v_prior.site_id IS DISTINCT FROM NEW.site_id THEN
                INSERT INTO change_event (entity_type, entity_id, effective_date)
                SELECT 'person', m, v_date
                FROM jinji_unit_site_members(NEW.org_unit_id, greatest(lower(NEW.valid_period), jinji_business_date())) AS m;
            END IF;
        WHEN 'person_version' THEN
            v_type := 'person';
            v_id := NEW.person_id;
            v_date := jinji_change_visible_from(NEW.valid_period);
            IF NEW.termination_date IS NOT NULL AND NEW.termination_date >= jinji_business_date() THEN
                v_date := greatest(v_date, NEW.termination_date + 1);
            END IF;
        WHEN 'assignment', 'account_suspension', 'workplace_assignment' THEN
            v_type := 'person';
            v_id := NEW.person_id;
            v_date := jinji_change_visible_from(NEW.valid_period);
        WHEN 'personnel_action' THEN
            v_type := 'person';
            v_id := NEW.person_id;
            v_date := NEW.effective_date;
        WHEN 'site_version' THEN
            PERFORM jinji_record_site_dependents(
                NEW.site_id, jinji_change_visible_from(NEW.valid_period), greatest(lower(NEW.valid_period), jinji_business_date()), false);
            RETURN NULL;
        WHEN 'building_version' THEN
            PERFORM jinji_record_floor_dependents(
                ARRAY(SELECT f.id FROM floor f WHERE f.building_id = NEW.building_id),
                jinji_change_visible_from(NEW.valid_period), greatest(lower(NEW.valid_period), jinji_business_date()), false);
            RETURN NULL;
        WHEN 'floor_version', 'floor_layout' THEN
            PERFORM jinji_record_floor_dependents(
                ARRAY[NEW.floor_id], jinji_change_visible_from(NEW.valid_period), greatest(lower(NEW.valid_period), jinji_business_date()), false);
            RETURN NULL;
        ELSE
            RAISE EXCEPTION 'jinji_record_change: 未対応の表です: %', TG_TABLE_NAME;
    END CASE;

    INSERT INTO change_event (entity_type, entity_id, effective_date) VALUES (v_type, v_id, v_date);
    RETURN NULL;
END
$$;

CREATE OR REPLACE FUNCTION jinji_record_withdrawal() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_type text;
    v_id uuid;
    v_date date;
    v_prior record;
BEGIN
    CASE TG_TABLE_NAME
        WHEN 'org_unit_version' THEN
            v_type := 'org_unit';
            v_id := OLD.org_unit_id;
            v_date := jinji_change_visible_from(OLD.valid_period);
        WHEN 'person_version' THEN
            v_type := 'person';
            v_id := OLD.person_id;
            v_date := jinji_change_visible_from(OLD.valid_period);
            IF OLD.termination_date IS NOT NULL AND OLD.termination_date >= jinji_business_date() THEN
                v_date := greatest(v_date, OLD.termination_date + 1);
            END IF;
        WHEN 'assignment', 'account_suspension', 'workplace_assignment' THEN
            v_type := 'person';
            v_id := OLD.person_id;
            v_date := jinji_change_visible_from(OLD.valid_period);
        WHEN 'personnel_action' THEN
            v_type := 'person';
            v_id := OLD.person_id;
            v_date := OLD.effective_date;
        WHEN 'site_version' THEN
            v_date := jinji_change_visible_from(OLD.valid_period);
            IF v_date > jinji_business_date() THEN
                PERFORM jinji_record_site_dependents(OLD.site_id, v_date, greatest(lower(OLD.valid_period), jinji_business_date()), true);
            END IF;
            RETURN NULL;
        WHEN 'building_version' THEN
            v_date := jinji_change_visible_from(OLD.valid_period);
            IF v_date > jinji_business_date() THEN
                PERFORM jinji_record_floor_dependents(
                    ARRAY(SELECT f.id FROM floor f WHERE f.building_id = OLD.building_id),
                    v_date, greatest(lower(OLD.valid_period), jinji_business_date()), true);
            END IF;
            RETURN NULL;
        WHEN 'floor_version', 'floor_layout' THEN
            v_date := jinji_change_visible_from(OLD.valid_period);
            IF v_date > jinji_business_date() THEN
                PERFORM jinji_record_floor_dependents(ARRAY[OLD.floor_id], v_date, greatest(lower(OLD.valid_period), jinji_business_date()), true);
            END IF;
            RETURN NULL;
        ELSE
            RAISE EXCEPTION 'jinji_record_withdrawal: 未対応の表です: %', TG_TABLE_NAME;
    END CASE;

    -- 既に見せてよい日が来ている行は全員が見ている。その取り消しは必ず置き換えの行（新しい記録）を伴うので、そちらで届く
    IF v_date > jinji_business_date() THEN
        INSERT INTO change_event (entity_type, entity_id, effective_date, withdrawn) VALUES (v_type, v_id, v_date, true);
        -- 拠点を変える将来の組織の版の取り消しは、拠点を引き継ぐ主務の所属者にも届ける
        IF TG_TABLE_NAME = 'org_unit_version' THEN
            SELECT p.found, p.site_id INTO v_prior FROM jinji_org_unit_prior_site(OLD.org_unit_id, lower(OLD.valid_period), OLD.id) AS p;
            IF v_prior.found AND v_prior.site_id IS DISTINCT FROM OLD.site_id THEN
                INSERT INTO change_event (entity_type, entity_id, effective_date, withdrawn)
                SELECT 'person', m, v_date, true
                FROM jinji_unit_site_members(OLD.org_unit_id, greatest(lower(OLD.valid_period), jinji_business_date())) AS m;
            END IF;
        END IF;
    END IF;

    RETURN NULL;
END
$$;

CREATE TRIGGER building_version_record_change AFTER INSERT ON building_version
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();
CREATE TRIGGER building_version_record_withdrawal AFTER UPDATE OF recorded_period ON building_version
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();
CREATE TRIGGER floor_version_record_change AFTER INSERT ON floor_version
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();
CREATE TRIGGER floor_version_record_withdrawal AFTER UPDATE OF recorded_period ON floor_version
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();
CREATE TRIGGER floor_layout_record_change AFTER INSERT ON floor_layout
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();
CREATE TRIGGER floor_layout_record_withdrawal AFTER UPDATE OF recorded_period ON floor_layout
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();
CREATE TRIGGER workplace_assignment_record_change AFTER INSERT ON workplace_assignment
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();
CREATE TRIGGER workplace_assignment_record_withdrawal AFTER UPDATE OF recorded_period ON workplace_assignment
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();

-- ---------------------------------------------------------------------------
-- 8. 取込・提案・修正依頼の許可リスト
-- ---------------------------------------------------------------------------

-- 取込元に配置図の JSON（jin-ji の配置図の形式 v1。要素ごとの行にして読む）を足す
ALTER TABLE import_batch DROP CONSTRAINT import_batch_source_kind_check;
ALTER TABLE import_batch ADD CONSTRAINT import_batch_source_kind_check CHECK (source_kind IN ('csv', 'xlsx', 'graph_json', 'work_location', 'layout_json'));
ALTER TABLE import_batch DROP CONSTRAINT import_batch_target_kind_check;
ALTER TABLE import_batch ADD CONSTRAINT import_batch_target_kind_check CHECK (
    target_kind IN ('people', 'org_units', 'personnel_actions', 'directory_users', 'sites', 'floor_layouts', 'workplace_assignments'));

ALTER TABLE change_proposal DROP CONSTRAINT change_proposal_target_kind_check;
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_target_kind_check CHECK (target_kind IN (
    'person', 'person_version', 'org_unit', 'org_unit_version', 'assignment', 'personnel_action', 'account_suspension', 'external_identity',
    'site', 'site_version',
    -- 0019
    'building', 'building_version', 'floor', 'floor_version', 'floor_layout', 'workplace_assignment'));

ALTER TABLE correction_request DROP CONSTRAINT correction_request_issue_check;
ALTER TABLE correction_request ADD CONSTRAINT correction_request_issue_check CHECK (issue IN (
    'missing_employee_number', 'missing_required', 'invalid_date', 'invalid_value', 'duplicate_row',
    'unknown_legal_entity', 'unknown_org', 'unknown_org_level', 'unknown_head', 'conflicting_identity',
    'suspected_sensitive_value', 'abolished_org', 'stale_head', 'uninterpretable_action', 'suspected_employee_number_typo',
    'referred_from_review',
    'reported_value',
    'unknown_site',
    -- 0019: 建物・階・席・ゾーンが jin-ji に無い、席の拠点と人の拠点の食い違い（承認は止めない）
    'unknown_building', 'unknown_floor', 'unknown_seat', 'unknown_zone', 'site_mismatch'));

CREATE OR REPLACE FUNCTION jinji_ingest_field_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        -- 人
        'employee_number', 'legal_entity_code',
        'full_name', 'family_name', 'given_name',
        'full_name_kana', 'family_name_kana', 'given_name_kana',
        'family_name_latin', 'given_name_latin', 'display_name',
        'work_email', 'extension_number', 'work_mobile_phone', 'work_location',
        'employment_type', 'job_type', 'hire_date', 'termination_date',
        'employment_status', 'suspension_start_date',
        -- 所属（人の取込）・組織（組織の取込）
        'org_code', 'org_name', 'position_title', 'assignment_type',
        'org_short_name', 'org_level', 'parent_org_code', 'parent_org_name', 'org_head_employee_number', 'org_valid_from',
        'org_head_name', 'org_remarks',
        'parent_org_alt_name',
        -- 兼務・出向
        'concurrent_org_name', 'concurrent_position_title',
        'secondment_legal_entity_code', 'secondment_org_name', 'secondment_start_date',
        -- 辞令
        'action_date', 'action_text', 'action_note',
        -- 外部のディレクトリ（Entra ID 等）
        'external_id', 'account_enabled',
        -- 0017: 拠点（人・組織の拠点、拠点マスタ）とリモートワークの有無
        'site_code', 'site_name', 'site_type', 'site_prefecture', 'site_location', 'site_valid_from', 'site_closed_date', 'remote_work',
        -- 0019: 建物・階・配置図の改訂（外形・固定の部分・ゾーン・席）
        'building_code', 'building_name', 'building_sort_order', 'floor_label', 'floor_level_order', 'floor_kind',
        'layout_valid_from', 'layout_element', 'layout_name', 'layout_kind', 'layout_polygon', 'layout_grid_mm',
        'seat_code', 'seat_x_mm', 'seat_y_mm', 'seat_rotation', 'seat_desk_type', 'seat_kind', 'seat_capacity', 'seat_features',
        'seat_zone', 'seat_out_of_service',
        -- 0019: 人の席・持ち場
        'zone_name', 'workplace_role', 'workplace_valid_from', 'workplace_end_date',
        -- 正規化で作る検索用の表記
        'search_name', 'search_name_kana', 'org_search_name', 'parent_org_search_name', 'site_search_name'
    ]::text[]
$$;

CREATE OR REPLACE FUNCTION jinji_proposal_field_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'person_id', 'legal_entity_id', 'employee_number',
        'family_name', 'given_name', 'family_name_kana', 'given_name_kana', 'family_name_latin', 'given_name_latin',
        'display_name', 'search_name', 'search_name_kana',
        'work_email', 'extension_number', 'work_mobile_phone', 'work_location',
        'employment_type', 'job_type', 'hire_date', 'termination_date',
        'org_unit_id', 'assignment_type', 'position_title', 'secondment_type',
        'code', 'name', 'short_name', 'level_key', 'parent_org_unit_id', 'head_person_id', 'valid_from',
        'action_type', 'concurrent_org_unit_id', 'concurrent_position_title', 'secondment_org_unit_id', 'secondment_start_date',
        'suspension_start_date', 'source_system', 'external_id',
        -- 0017
        'site_id', 'remote_work', 'site_type', 'prefecture_code', 'location',
        -- 0019: 建物・階・配置図の改訂（layout は改訂 1 つ分の JSON）・割り当て
        'building_id', 'sort_order', 'floor_id', 'label', 'level_order', 'floor_kind', 'layout',
        'seat_id', 'zone_key', 'role', 'exclusive'
    ]::text[]
$$;

-- 修正依頼で報告できる人の項目に、階・エリア（workplace）と席（seat）を足す
CREATE OR REPLACE FUNCTION jinji_correction_person_fields() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'employee_number', 'legal_entity', 'display_name', 'family_name', 'given_name', 'family_name_kana', 'given_name_kana',
        'family_name_latin', 'given_name_latin', 'work_email', 'extension_number', 'work_mobile_phone',
        'employment_type', 'job_type', 'hire_date', 'termination_date', 'employment_status', 'account_status', 'account_suspension',
        'assignments', 'managers', 'personnel_actions',
        -- 0017
        'site', 'remote_work',
        -- 0019
        'workplace', 'seat',
        -- 0017 より前の起票（廃止した項目。新しい起票には使えない）
        'work_location'
    ]::text[]
$$;
