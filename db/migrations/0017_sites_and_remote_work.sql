-- 0017: 拠点とリモートワークの有無（ADR-0002「拠点とリモートワーク」、アーキテクチャ概要 §4.2）
--
-- 内容
-- 1. 拠点マスタ（site / site_version）。組織と同じく不変 ID と版（二つの時間軸）に分ける
--    - 版はコード・名称・種別・所在地（都道府県のコードと、それより後の所在地）・有効期間を持つ。拠点は法人に属し、コードは法人の中で、
--      現在記録中・有効期間が重なる版で一意（組織コードと同じ排他制約）
--    - 所在地は事業所の住所（会社の情報）で、個人の住所ではない。列名に address を使わないのは、保持しない項目の名前の検査
--      （項目カタログのテスト・jinji_ingest_forbidden_key()）が address を個人の住所として扱うため
--    - 種別は固定の少数（本社・支社/支店・営業所・工場・研究所・物流拠点・店舗・その他）。段の定義（0004）のように設定にしないのは、
--      段は公開範囲（same_level）を決めるので法人ごとの違いと承認が要るのに対し、種別は表示とまとめ方（件数・グループ化）にしか使わず、
--      API の値（コード）を導入先ごとに変えない方が社内システムとの連携が安定するため。当てはまらないものは other にする
-- 2. 組織の版の拠点（org_unit_version.site_id）。組織に拠点が無ければ上位組織の拠点を使う（アプリが解決する）
-- 3. 人の版の拠点の上書き（person_version.site_id）とリモートワークの有無（person_version.remote_work）
--    - 人の拠点は「人の版の上書き → 主務の組織（無ければ上位組織）の拠点」の順に決める（アプリの OrgSnapshot.SiteOf）。上書きは異なる場合だけ持つ
--    - リモートワークは対象か否かだけ。理由・頻度・曜日・シフト・在宅の場所は持たない（休職を理由なしの一時停止で扱うのと同じ考え方）
-- 4. 自由記述の勤務地（person_version.work_location）の段階的な廃止
--    - 値は拠点に置き換える。取込済みの値は拠点マスタを入れた後に管理 CLI（reconcile-work-locations）で拠点に照合し、
--      主務の組織の拠点と違う拠点は人の拠点の上書きの変更提案に、拠点に当たらない値は修正依頼（unknown_site）にする（承認を経て反映する）
--    - 列は直ちには消さない。照合の入力であり、過去の版の値（記録）でもあるため。アプリは読み書きせず、新しい行には書けないよう
--      アプリ用ロールの INSERT を列単位の権限にして work_location を外す（記録期間を閉じる UPDATE は旧行のまま通る）。
--      照合を終えた後のマイグレーションで列を消す
--    - ポリシー・クライアントの項目上限・修正依頼の項目の work_location は site に置き換える（下の 6）
-- 5. 変更フィード: 拠点の版の追加・取り消しを、拠点（site）と、その拠点を使う組織・人の変更として記録する。組織の拠点が変わったら、その組織
--    （と拠点を引き継ぐ配下）の主務の所属者も人の変更として記録する（人の応答の拠点が変わるため）。見せてよい日・先行提供の上限（jinji_viewer_horizon()）は
--    他の表と同じ規則（0007・0016）
-- 6. 項目名の置き換え（work_location → site）
--    - ポリシー（policy_rule.fields）: site と remote_work に置き換える。勤務地を狭めていた規則は、拠点とリモートワークの有無の両方を同じ範囲に狭める
--      （新しい項目を規則の外に出して広げない。狭める側に倒す）
--    - クライアントの項目の上限（api_client.fields）: site に置き換える（remote_work は足さない。上限は渡す項目を最小にするためのもので、新しい項目を黙って渡さない）
--    - どちらも 0004 と同じく、中身の不変のトリガーと FORCE ROW LEVEL SECURITY をマイグレーションの中だけ外して書き換える（意味を保つ表現の移行）
--    - 修正依頼の項目（correction_request.person_field）の work_location は既存の行のために許可リストに残し、新しい起票は site / remote_work にする（アプリ）
--
-- 他の行を読むトリガーについて
-- - 変更フィードの記録のトリガーは、組織の版・所属・人の版を読んで影響する組織・人を求める。書き込むのは人事データを書ける hr_admin（change_event の
--   追加の RLS）で、hr_admin にはすべての行が見えるので、結果は閲覧者によって変わらない。整合性の検査ではないので §5.6 の規則に反しない

-- ---------------------------------------------------------------------------
-- 1. 拠点マスタ
-- ---------------------------------------------------------------------------

CREATE TABLE site (
    id               uuid PRIMARY KEY,
    legal_entity_id  uuid NOT NULL REFERENCES legal_entity (id),
    CONSTRAINT site_id_legal_entity_key UNIQUE (id, legal_entity_id)
);
COMMENT ON TABLE site IS '拠点の不変 ID。コード・名称・種別・所在地は site_version に版として持つ';

CREATE TABLE site_version (
    id               uuid      PRIMARY KEY,
    site_id          uuid      NOT NULL,
    legal_entity_id  uuid      NOT NULL,
    code             text      NOT NULL,
    name             text      NOT NULL,
    site_type        text      NOT NULL,
    prefecture_code  text,
    location         text,
    valid_period     daterange NOT NULL,
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT site_version_site_legal_entity_fkey FOREIGN KEY (site_id, legal_entity_id) REFERENCES site (id, legal_entity_id),
    CONSTRAINT site_version_code_check CHECK (btrim(code) <> '' AND char_length(code) <= 50 AND code !~ '[[:cntrl:]]'),
    CONSTRAINT site_version_name_check CHECK (btrim(name) <> '' AND char_length(name) <= 100 AND name !~ '[[:cntrl:]]'),
    -- 本社 / 支社・支店 / 営業所 / 工場 / 研究所 / 物流拠点・倉庫 / 店舗 / その他
    CONSTRAINT site_version_site_type_check CHECK (site_type IN (
        'head_office', 'branch', 'sales_office', 'factory', 'laboratory', 'logistics', 'store', 'other')),
    -- 都道府県のコード（JIS X 0401。01 北海道 〜 47 沖縄県）。国外の拠点は NULL
    CONSTRAINT site_version_prefecture_code_check CHECK (prefecture_code IS NULL OR prefecture_code ~ '^(0[1-9]|[1-3][0-9]|4[0-7])$'),
    CONSTRAINT site_version_location_check CHECK (
        location IS NULL OR (btrim(location) <> '' AND char_length(location) <= 200 AND location !~ '[[:cntrl:]]')),
    CONSTRAINT site_version_valid_period_check CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT site_version_recorded_period_check CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    -- 同一拠点の版の有効期間は重ならない（現在記録中の行に限る）
    CONSTRAINT site_version_no_overlap
        EXCLUDE USING gist (site_id WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period)),
    -- 同一法人で、現在記録中かつ有効期間が重なる版に同じ拠点コードを使わない
    CONSTRAINT site_version_code_no_overlap
        EXCLUDE USING gist (legal_entity_id WITH =, code WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period))
);
COMMENT ON TABLE site_version IS '拠点の版（bitemporal）。所在地は事業所の住所（会社の情報）で、個人の住所ではない';
COMMENT ON COLUMN site_version.legal_entity_id IS '拠点が属する法人（site.legal_entity_id と同じ値。拠点コードの一意性の排他制約に使う）';
COMMENT ON COLUMN site_version.site_type IS '拠点の種別（固定の少数。表示とまとめ方にだけ使う）';
COMMENT ON COLUMN site_version.prefecture_code IS '所在の都道府県のコード（JIS X 0401）。国外は NULL';
COMMENT ON COLUMN site_version.location IS '所在地（都道府県より後の部分。事業所の住所 = 会社の情報）';
CREATE INDEX site_version_site_idx ON site_version (site_id);

CREATE TRIGGER site_version_guard_recorded_period BEFORE UPDATE ON site_version
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();

-- ---------------------------------------------------------------------------
-- 2. 組織の版の拠点・3. 人の版の拠点の上書きとリモートワーク
-- ---------------------------------------------------------------------------

ALTER TABLE org_unit_version ADD COLUMN site_id uuid REFERENCES site (id);
COMMENT ON COLUMN org_unit_version.site_id IS '組織の拠点。NULL なら上位組織の拠点を使う（アプリが解決する）';
CREATE INDEX org_unit_version_site_idx ON org_unit_version (site_id) WHERE site_id IS NOT NULL;

ALTER TABLE person_version
    ADD COLUMN site_id uuid REFERENCES site (id),
    ADD COLUMN remote_work boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN person_version.site_id IS '人の拠点の上書き（主務の組織の拠点と異なる場合だけ持つ。NULL なら主務の組織の拠点）';
COMMENT ON COLUMN person_version.remote_work IS 'リモートワークの対象か（有無だけ。理由・頻度・曜日・シフト・在宅の場所は持たない。ADR-0002）';
COMMENT ON COLUMN person_version.work_location IS '廃止予定（0017）。自由記述の勤務地。拠点（site_id）に置き換え、アプリは読み書きしない。照合（reconcile-work-locations）の入力と過去の記録としてだけ残す';
CREATE INDEX person_version_site_idx ON person_version (site_id) WHERE site_id IS NOT NULL;

-- 新しい行に勤務地を書かせない（列単位の INSERT 権限から外す）。記録期間を閉じる UPDATE は従来どおり
REVOKE INSERT ON person_version FROM jinji_app;
GRANT INSERT (
    id, person_id, family_name, given_name, family_name_kana, given_name_kana, family_name_latin, given_name_latin,
    display_name, search_name, search_name_kana, work_email, extension_number, work_mobile_phone,
    employment_type, job_type, hire_date, termination_date, valid_period, recorded_period, site_id, remote_work)
    ON person_version TO jinji_app;

-- ---------------------------------------------------------------------------
-- 4. 行レベルセキュリティと権限
-- ---------------------------------------------------------------------------

ALTER TABLE site ENABLE ROW LEVEL SECURITY;
ALTER TABLE site FORCE ROW LEVEL SECURITY;
ALTER TABLE site_version ENABLE ROW LEVEL SECURITY;
ALTER TABLE site_version FORCE ROW LEVEL SECURITY;

-- 拠点の情報は組織と同じく全社公開。将来の版（開設・移転・名称変更の予定）は組織の版と同じ線（上限 = 業務日付 + 先行提供の日数）
CREATE POLICY site_select ON site FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY site_insert ON site FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY site_version_select ON site_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));
CREATE POLICY site_version_insert ON site_version FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY site_version_update ON site_version FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

GRANT SELECT, INSERT ON site, site_version TO jinji_app;
GRANT UPDATE (recorded_period) ON site_version TO jinji_app;

-- ---------------------------------------------------------------------------
-- 5. 変更フィード
-- ---------------------------------------------------------------------------

ALTER TABLE change_event DROP CONSTRAINT change_event_entity_type_check;
ALTER TABLE change_event ADD CONSTRAINT change_event_entity_type_check CHECK (entity_type IN ('person', 'org_unit', 'site'));

-- その日に拠点 p_site を使う組織（自分の拠点か、拠点を持たずに上位組織から引き継ぐ組織）
CREATE FUNCTION jinji_site_units(p_site uuid, p_date date) RETURNS SETOF uuid
LANGUAGE sql STABLE AS $$
    WITH RECURSIVE units AS (
        SELECT v.org_unit_id FROM org_unit_version v
        WHERE v.site_id = p_site AND upper_inf(v.recorded_period) AND v.valid_period @> p_date
        UNION
        SELECT c.org_unit_id FROM org_unit_version c JOIN units u ON c.parent_org_unit_id = u.org_unit_id
        WHERE c.site_id IS NULL AND upper_inf(c.recorded_period) AND c.valid_period @> p_date
    )
    SELECT org_unit_id FROM units
$$;
COMMENT ON FUNCTION jinji_site_units(uuid, date) IS '変更フィード: その日に拠点を使う組織（自分の拠点か、上位組織から引き継ぐ組織）';

-- その日に組織 p_unit の拠点を引き継ぐ組織（自分と、拠点を持たない配下）の主務の所属者
CREATE FUNCTION jinji_unit_site_members(p_unit uuid, p_date date) RETURNS SETOF uuid
LANGUAGE sql STABLE AS $$
    WITH RECURSIVE units AS (
        SELECT p_unit AS org_unit_id
        UNION
        SELECT c.org_unit_id FROM org_unit_version c JOIN units u ON c.parent_org_unit_id = u.org_unit_id
        WHERE c.site_id IS NULL AND upper_inf(c.recorded_period) AND c.valid_period @> p_date
    )
    SELECT DISTINCT a.person_id FROM assignment a JOIN units u ON a.org_unit_id = u.org_unit_id
    WHERE a.assignment_type = 'primary' AND upper_inf(a.recorded_period) AND a.valid_period @> p_date
$$;
COMMENT ON FUNCTION jinji_unit_site_members(uuid, date) IS '変更フィード: その日に組織の拠点を引き継ぐ組織（自分と拠点を持たない配下）の主務の所属者';

-- 拠点の版の変更を、拠点・その拠点を使う組織・その組織の主務の所属者と、拠点を上書きで持つ人の変更として記録する
CREATE FUNCTION jinji_record_site_dependents(p_site uuid, p_visible date, p_member_date date, p_withdrawn boolean) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO change_event (entity_type, entity_id, effective_date, withdrawn) VALUES ('site', p_site, p_visible, p_withdrawn);

    INSERT INTO change_event (entity_type, entity_id, effective_date, withdrawn)
    SELECT 'org_unit', u, p_visible, p_withdrawn FROM jinji_site_units(p_site, p_member_date) AS u;

    INSERT INTO change_event (entity_type, entity_id, effective_date, withdrawn)
    SELECT 'person', p, p_visible, p_withdrawn FROM (
        SELECT m AS p FROM jinji_site_units(p_site, p_member_date) AS u, jinji_unit_site_members(u, p_member_date) AS m
        UNION
        SELECT v.person_id FROM person_version v
        WHERE v.site_id = p_site AND upper_inf(v.recorded_period) AND v.valid_period @> p_member_date
    ) AS people;
END
$$;
COMMENT ON FUNCTION jinji_record_site_dependents(uuid, date, date, boolean) IS
    '変更フィード: 拠点の変更を、拠点・その拠点を使う組織・その組織の主務の所属者・拠点を上書きで持つ人の変更として記録する';

-- 組織の版の直前の拠点（同じ組織の、開始日を含むか開始日に終わる版のうち最も新しく記録したもの）。直前の版が無ければ found = false
CREATE FUNCTION jinji_org_unit_prior_site(p_unit uuid, p_from date, p_exclude uuid, OUT found boolean, OUT site_id uuid)
LANGUAGE sql STABLE AS $$
    SELECT true, v.site_id FROM org_unit_version v
    WHERE v.org_unit_id = p_unit AND v.id <> p_exclude
      AND (v.valid_period @> p_from OR (NOT upper_inf(v.valid_period) AND upper(v.valid_period) = p_from))
    ORDER BY lower(v.recorded_period) DESC, (v.valid_period @> p_from) DESC, v.id DESC
    LIMIT 1
$$;
COMMENT ON FUNCTION jinji_org_unit_prior_site(uuid, date, uuid) IS '変更フィード: 組織の版の直前の拠点（拠点が変わったかの判定に使う）';

-- 0007・0016 の記録のトリガー関数に、拠点の版と、組織の拠点の変更による所属者の記録を足す
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
        WHEN 'assignment', 'account_suspension' THEN
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
        WHEN 'assignment', 'account_suspension' THEN
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

CREATE TRIGGER site_version_record_change AFTER INSERT ON site_version
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();
CREATE TRIGGER site_version_record_withdrawal AFTER UPDATE OF recorded_period ON site_version
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();

-- ---------------------------------------------------------------------------
-- 6. 取込・提案・修正依頼の許可リスト
-- ---------------------------------------------------------------------------

-- 取込元に「取込済みの勤務地」（照合 reconcile-work-locations。値は person_version.work_location から読む）を足す
ALTER TABLE import_batch DROP CONSTRAINT import_batch_source_kind_check;
ALTER TABLE import_batch ADD CONSTRAINT import_batch_source_kind_check CHECK (source_kind IN ('csv', 'xlsx', 'graph_json', 'work_location'));
ALTER TABLE import_batch DROP CONSTRAINT import_batch_target_kind_check;
ALTER TABLE import_batch ADD CONSTRAINT import_batch_target_kind_check CHECK (
    target_kind IN ('people', 'org_units', 'personnel_actions', 'directory_users', 'sites'));

ALTER TABLE change_proposal DROP CONSTRAINT change_proposal_target_kind_check;
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_target_kind_check CHECK (target_kind IN (
    'person', 'person_version', 'org_unit', 'org_unit_version', 'assignment', 'personnel_action', 'account_suspension', 'external_identity',
    'site', 'site_version'));

ALTER TABLE correction_request DROP CONSTRAINT correction_request_issue_check;
ALTER TABLE correction_request ADD CONSTRAINT correction_request_issue_check CHECK (issue IN (
    'missing_employee_number', 'missing_required', 'invalid_date', 'invalid_value', 'duplicate_row',
    'unknown_legal_entity', 'unknown_org', 'unknown_org_level', 'unknown_head', 'conflicting_identity',
    'suspected_sensitive_value', 'abolished_org', 'stale_head', 'uninterpretable_action', 'suspected_employee_number_typo',
    'referred_from_review',
    'reported_value',
    -- 0017: 拠点マスタに当たらない拠点（取込の拠点コード・拠点名、勤務地の照合）
    'unknown_site'));

-- 取込項目（JinJi.Ingest の IngestFields.All と一致させる）。work_location は既存の staging の行のために残す（新しい項目マッピングには書けない）
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
        -- 正規化で作る検索用の表記
        'search_name', 'search_name_kana', 'org_search_name', 'parent_org_search_name', 'site_search_name'
    ]::text[]
$$;

-- 提案の列（JinJi.Application の ProposalFields.All と一致させる）。work_location は既存の提案の行のために残す（新しい提案には書かない）
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
        'site_id', 'remote_work', 'site_type', 'prefecture_code', 'location'
    ]::text[]
$$;

-- 修正依頼で報告できる人の項目（JinJi.Policy の PersonFields.PolicyControllable と、既存の行のための work_location）
CREATE OR REPLACE FUNCTION jinji_correction_person_fields() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'employee_number', 'legal_entity', 'display_name', 'family_name', 'given_name', 'family_name_kana', 'given_name_kana',
        'family_name_latin', 'given_name_latin', 'work_email', 'extension_number', 'work_mobile_phone',
        'employment_type', 'job_type', 'hire_date', 'termination_date', 'employment_status', 'account_status', 'account_suspension',
        'assignments', 'managers', 'personnel_actions',
        -- 0017
        'site', 'remote_work',
        -- 0017 より前の起票（廃止した項目。新しい起票には使えない）
        'work_location'
    ]::text[]
$$;

-- ---------------------------------------------------------------------------
-- 7. 項目名の置き換え（work_location → site）
-- ---------------------------------------------------------------------------

ALTER TABLE policy_rule NO FORCE ROW LEVEL SECURITY;
ALTER TABLE policy_rule DISABLE TRIGGER policy_rule_guard;
UPDATE policy_rule
SET fields = ARRAY(
    SELECT DISTINCT f FROM unnest(array_replace(fields, 'work_location', 'site') || ARRAY['remote_work']) AS f ORDER BY f)
WHERE 'work_location' = ANY (fields);
ALTER TABLE policy_rule ENABLE TRIGGER policy_rule_guard;
ALTER TABLE policy_rule FORCE ROW LEVEL SECURITY;

ALTER TABLE api_client NO FORCE ROW LEVEL SECURITY;
UPDATE api_client
SET fields = ARRAY(SELECT DISTINCT f FROM unnest(array_replace(fields, 'work_location', 'site')) AS f ORDER BY f)
WHERE 'work_location' = ANY (fields);
ALTER TABLE api_client FORCE ROW LEVEL SECURITY;
