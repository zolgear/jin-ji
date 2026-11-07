-- 0025: 人の席・持ち場の直接登録（ADR-0012 の見直し・ADR-0013 決定 11）
--
-- 位置づけ
-- - 席の割り当て（workplace_assignment の主の席）を、組織と人の情報と同じく現場が直接登録できるようにする。
--   承認は経ず、有効日を決めて反映する。素案（計画）にも入れられる
-- - 管理元の領域に workplace（人の席・持ち場）を足す。既定は hr（取込だけ）で、設定しない導入先の動きは変わらない
-- - 範囲は人の主務の組織（人の拠点・連絡先と同じ）。総務・ファシリティ管理者のロールは作らない
-- - 配置図（建物・階・改訂・席の置き場所）は今のまま取込と人事管理者の承認だけで変える
--
-- 中身
-- - 領域の一覧に workplace を足す
-- - workplace_assignment に直接登録の書き込みの方針・書いた行の記録・関係者への参照を足す（0021 の所属と同じ形）
-- - 素案の変更の種類に workplace を足す（target_id は人、values の seat_id か floor_id と zone_key。どれも空なら席を外す）

-- ---------------------------------------------------------------------------
-- 1. 領域
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION jinji_field_domains() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY['org_structure', 'org_site', 'assignment', 'person_site', 'person_contact', 'workplace']
$$;
COMMENT ON FUNCTION jinji_field_domains() IS '管理元を設定できる領域（組織の構造・組織の拠点・所属・人の拠点・連絡先・人の席）';
COMMENT ON COLUMN field_domain_setting.domain IS '領域（org_structure・org_site・assignment・person_site・person_contact・workplace）';

-- ---------------------------------------------------------------------------
-- 2. 人の席・持ち場の直接登録
-- ---------------------------------------------------------------------------

ALTER TABLE field_registration_row DROP CONSTRAINT field_registration_row_table_check;
ALTER TABLE field_registration_row ADD CONSTRAINT field_registration_row_table_check CHECK (
    table_name IN ('org_unit', 'org_unit_version', 'person_version', 'assignment', 'personnel_action', 'workplace_assignment'));

CREATE TRIGGER workplace_assignment_capture_field_registration AFTER INSERT OR UPDATE OF recorded_period ON workplace_assignment
    FOR EACH ROW EXECUTE FUNCTION jinji_capture_field_registration_row();

-- 書き込み: 人の主務の組織の範囲（その日の所属。同じ登録で異動する人は異動先）
CREATE POLICY workplace_assignment_insert_field ON workplace_assignment FOR INSERT WITH CHECK (
    jinji_field_may_write(ARRAY['workplace'])
    AND jinji_field_covers_person(person_id, greatest(lower(valid_period), (SELECT jinji_business_date()))));
CREATE POLICY workplace_assignment_update_field ON workplace_assignment FOR UPDATE
    USING (jinji_field_may_write(ARRAY['workplace'])
           AND jinji_field_covers_person(person_id, greatest(lower(valid_period), (SELECT jinji_business_date()))))
    WITH CHECK (jinji_field_may_write(ARRAY['workplace'])
                AND jinji_field_covers_person(person_id, greatest(lower(valid_period), (SELECT jinji_business_date()))));

-- 参照: 直接登録が書いた行（未来日の席替えを含む）を、その登録の関係者と org_admin に見せる
DROP POLICY workplace_assignment_select ON workplace_assignment;
CREATE POLICY workplace_assignment_select ON workplace_assignment FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon()))
    OR (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false) AND jinji_viewer_sees_field_row(id)));

-- 変更フィード: 席の変更は人の変更として記録する（0019 のトリガー）
DROP POLICY change_event_insert_field ON change_event;
CREATE POLICY change_event_insert_field ON change_event FOR INSERT WITH CHECK (
    jinji_field_may_write(ARRAY['org_structure', 'org_site', 'assignment', 'person_site', 'person_contact', 'workplace']));

-- ---------------------------------------------------------------------------
-- 3. 素案の中身の検査
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION jinji_plan_value_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'org_unit_id', 'position_title', 'site_id', 'remote_work', 'extension_number', 'work_mobile_phone',
        'code', 'name', 'short_name', 'level_key', 'parent_org_unit_id', 'head_person_id',
        'person_id', 'role',
        -- 0024
        'person_ids',
        -- 0025
        'floor_id', 'seat_id', 'zone_key'
    ]::text[]
$$;

CREATE OR REPLACE FUNCTION jinji_plan_changes_allowed(changes jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT jsonb_typeof(changes) = 'array'
       AND jsonb_array_length(changes) <= 500
       AND NOT EXISTS (
           SELECT 1 FROM jsonb_array_elements(changes) AS c (item)
           WHERE jsonb_typeof(c.item) <> 'object'
              OR EXISTS (SELECT 1 FROM jsonb_object_keys(c.item) AS k (key) WHERE k.key NOT IN ('kind', 'target_id', 'values'))
              OR coalesce(c.item ->> 'kind', '') NOT IN
                  ('assignment', 'person', 'org_update', 'org_create', 'org_abolish', 'member_order', 'workplace', 'group_join', 'group_leave')
              OR (c.item ? 'target_id' AND jsonb_typeof(c.item -> 'target_id') NOT IN ('string', 'null'))
              OR (c.item ? 'values' AND (
                      jsonb_typeof(c.item -> 'values') <> 'object'
                      OR EXISTS (
                          SELECT 1 FROM jsonb_each(c.item -> 'values') AS v (key, value)
                          WHERE v.key <> ALL (public.jinji_plan_value_keys())
                             OR jsonb_typeof(v.value) NOT IN ('string', 'null')
                             -- 並び順の人の一覧は 500 人分（36 文字の ID とカンマ）まで
                             OR char_length(coalesce(v.value #>> '{}', '')) > CASE WHEN v.key = 'person_ids' THEN 18500 ELSE 200 END))))
$$;
