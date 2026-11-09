-- 0026: 施設管理者による配置図の直接登録（ADR-0012 の見直し・ADR-0013 決定 12）
--
-- 位置づけ
-- - 配置図（建物・階・配置図の改訂。机とエリアの置き場所）は、人事と違う部署（総務・ファシリティ）から提供されることが多い。
--   ロール facility_admin（施設管理者）を足し、配置図を直接登録（field_registration）として承認なしで反映できるようにする
-- - 承認を求めるかは導入先が選ぶ。管理元の設定の領域に floor_layout（配置図）を足し、値は field（承認なし。既定）か approval（承認あり）。
--   approval のときは登録を同意待ちにし、登録者と別の施設管理者か人事管理者の承認（同意の操作）で反映する
-- - 取込（人事管理者の起案 → 別の人事管理者の承認）は今のまま残す
--
-- 中身
-- 1. ロール facility_admin。全拠点の建物・階・配置図の改訂を登録できる。人の情報・取込の作業領域・監査ログは見えない（広がるのは配置図の表だけ）
-- 2. 管理元の領域 floor_layout と値 approval
-- 3. 登録の種類（field_registration.kind: org = 組織と人 / layout = 配置図）。承認ありの配置図の登録は、別の人の同意なしに反映できない
-- 4. 配置図の改訂を、どの直接登録が書いたか（floor_layout.field_registration_id）。承認なしの改訂は起案者 = 承認者になる
-- 5. 施設管理者の書き込み・参照の方針（RLS）と、取り消しのための書いた行の記録

-- ---------------------------------------------------------------------------
-- 1. ロール
-- ---------------------------------------------------------------------------

ALTER TABLE role_grant DROP CONSTRAINT role_grant_role_check;
-- 既知のロール（JinJi.Policy の Roles.Known と一致させる）。facility_admin = 施設管理者（総務・ファシリティ）
ALTER TABLE role_grant ADD CONSTRAINT role_grant_role_check CHECK (role IN ('hr_admin', 'hr_corrector', 'org_admin', 'facility_admin'));

-- 施設管理者の「人」（UI でログインした人。AI エージェント・社内システム・jin-ji 自身は含まない）
CREATE FUNCTION jinji_viewer_is_facility_admin_user() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_set()
       AND coalesce(public.jinji_viewer_kind() = 'user', false)
       AND 'facility_admin' = ANY (pg_catalog.string_to_array(coalesce(pg_catalog.current_setting('jinji.viewer_roles', true), ''), ','))
$$;
COMMENT ON FUNCTION jinji_viewer_is_facility_admin_user() IS 'RLS: 閲覧者が facility_admin（施設管理者）ロールを持つ「人」か（GUC jinji.viewer_roles）';

-- ---------------------------------------------------------------------------
-- 2. 管理元の領域 floor_layout
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION jinji_field_domains() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY['org_structure', 'org_site', 'assignment', 'person_site', 'person_contact', 'workplace', 'floor_layout']
$$;
COMMENT ON FUNCTION jinji_field_domains() IS '管理元を設定できる領域（組織の構造・組織の拠点・所属・人の拠点・連絡先・人の席・配置図）';

-- 承認済みの設定が無いときの管理元は、席・持ち場と配置図が field、ほかは hr（JinJi.Domain の FieldDomains.Default と同じ）
CREATE OR REPLACE FUNCTION jinji_field_domain_mode(field_domain text) RETURNS text
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce((
        SELECT s.mode FROM public.field_domain_setting s
        WHERE s.domain = field_domain AND s.status = 'approved'),
        CASE WHEN field_domain IN ('workplace', 'floor_layout') THEN 'field' ELSE 'hr' END)
$$;
COMMENT ON FUNCTION jinji_field_domain_mode(text) IS '領域の今の管理元（承認済みの行。無ければ既定: workplace・floor_layout は field、ほかは hr）';

-- 配置図の値は field（承認なし）か approval（承認あり）。ほかの領域は hr / field / both
ALTER TABLE field_domain_setting DROP CONSTRAINT field_domain_setting_mode_check;
ALTER TABLE field_domain_setting ADD CONSTRAINT field_domain_setting_mode_check CHECK (
    CASE WHEN domain = 'floor_layout' THEN mode IN ('field', 'approval') ELSE mode IN ('hr', 'field', 'both') END);
COMMENT ON COLUMN field_domain_setting.domain IS '領域（org_structure・org_site・assignment・person_site・person_contact・workplace・floor_layout）';
COMMENT ON COLUMN field_domain_setting.mode IS
    '管理元（hr: 取込だけ / field: 直接登録だけ / both: 両方。配置図は field: 承認なし / approval: 登録者と別の人の承認で反映）';

-- ---------------------------------------------------------------------------
-- 3. 登録の種類
-- ---------------------------------------------------------------------------

ALTER TABLE field_registration ADD COLUMN kind text NOT NULL DEFAULT 'org';
-- 組織と人（組織長・担当・組織管理者の範囲）/ 配置図（施設管理者・人事管理者）
ALTER TABLE field_registration ADD CONSTRAINT field_registration_kind_check CHECK (kind IN ('org', 'layout'));
-- 配置図の登録は人だけが作る（AI エージェントの下書きは無い）
ALTER TABLE field_registration ADD CONSTRAINT field_registration_layout_via_check CHECK (kind <> 'layout' OR registered_via = 'user');
COMMENT ON COLUMN field_registration.kind IS '登録の種類（org: 組織と人 / layout: 配置図）';

-- 承認ありの配置図の登録は、登録者と別の人の同意（承認）なしに反映しない
CREATE FUNCTION jinji_guard_layout_registration() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.kind = 'layout' AND NEW.status = 'applied' AND OLD.status <> 'applied'
       AND public.jinji_field_domain_mode('floor_layout') = 'approval'
       AND (NEW.consented_by_person_id IS NULL OR NEW.consented_by_person_id = NEW.registered_by_person_id) THEN
        RAISE EXCEPTION 'field_registration: 配置図の登録は、登録者と別の人の承認が要ります（id=%）', NEW.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'field_registration_layout_approval';
    END IF;
    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_layout_registration() IS '配置図の管理元が approval のとき、登録者と別の人の同意なしに配置図の登録を反映させないトリガー関数';

CREATE TRIGGER field_registration_guard_layout BEFORE UPDATE OF status ON field_registration
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_layout_registration();

-- 参照: 配置図の登録は施設管理者にも見せる（承認する人を決めないため）
DROP POLICY field_registration_select ON field_registration;
CREATE POLICY field_registration_select ON field_registration FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR (kind = 'layout' AND (SELECT jinji_viewer_is_facility_admin_user()))
    OR (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false)
        AND ((SELECT jinji_viewer_is_org_admin())
             OR registered_by_person_id = (SELECT jinji_viewer_person_id())
             OR consented_by_person_id = (SELECT jinji_viewer_person_id())
             OR EXISTS (SELECT 1 FROM pg_catalog.unnest(org_unit_ids) u
                        WHERE jinji_viewer_scope_covers(u, (SELECT jinji_business_date()), true)))));

-- ---------------------------------------------------------------------------
-- 4. 配置図の改訂を書いた直接登録
-- ---------------------------------------------------------------------------

ALTER TABLE floor_layout ADD COLUMN field_registration_id uuid REFERENCES field_registration (id)
    DEFAULT NULLIF(pg_catalog.current_setting('jinji.field_registration', true), '')::uuid;
COMMENT ON COLUMN floor_layout.field_registration_id IS
    '改訂を書いた直接登録（既定は反映中の登録 GUC jinji.field_registration。区切った写しは元の改訂の値）。取込の承認の改訂は NULL';
COMMENT ON COLUMN floor_layout.proposed_by_person_id IS '改訂の起案者（取込の実行者・直接登録の登録者）';
COMMENT ON COLUMN floor_layout.approved_by_person_id IS '改訂の承認者（取込は起案者と別の人事管理者。直接登録は反映した人で、承認なしなら起案者と同じ）';
-- 職務分掌は取込の改訂だけ。直接登録は管理元の設定に従う（承認ありは登録の状態の遷移で止める）
ALTER TABLE floor_layout DROP CONSTRAINT floor_layout_two_person;
ALTER TABLE floor_layout ADD CONSTRAINT floor_layout_two_person CHECK (
    approved_by_person_id <> proposed_by_person_id OR field_registration_id IS NOT NULL);
COMMENT ON TABLE floor_layout IS
    '配置図の改訂（bitemporal）。取込の承認か直接登録（施設管理者・人事管理者）で入る。中身（ゾーン・席の置き場所）は変えない';

-- ---------------------------------------------------------------------------
-- 5. 施設管理者の書き込み・参照と、取り消しのための記録
-- ---------------------------------------------------------------------------

ALTER TABLE field_registration_row DROP CONSTRAINT field_registration_row_table_check;
ALTER TABLE field_registration_row ADD CONSTRAINT field_registration_row_table_check CHECK (
    table_name IN ('org_unit', 'org_unit_version', 'person_version', 'assignment', 'personnel_action', 'workplace_assignment',
                   'building_version', 'floor_version', 'floor_layout'));

CREATE TRIGGER building_version_capture_field_registration AFTER INSERT OR UPDATE OF recorded_period ON building_version
    FOR EACH ROW EXECUTE FUNCTION jinji_capture_field_registration_row();
CREATE TRIGGER floor_version_capture_field_registration AFTER INSERT OR UPDATE OF recorded_period ON floor_version
    FOR EACH ROW EXECUTE FUNCTION jinji_capture_field_registration_row();
CREATE TRIGGER floor_layout_capture_field_registration AFTER INSERT OR UPDATE OF recorded_period ON floor_layout
    FOR EACH ROW EXECUTE FUNCTION jinji_capture_field_registration_row();

-- 書き込み: 施設管理者の人は全拠点の建物・階・配置図（人事管理者の方針と OR で効く）
CREATE POLICY building_insert_facility ON building FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY floor_insert_facility ON floor FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY seat_insert_facility ON seat FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY building_version_insert_facility ON building_version FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY building_version_update_facility ON building_version FOR UPDATE
    USING ((SELECT jinji_viewer_is_facility_admin_user())) WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY floor_version_insert_facility ON floor_version FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY floor_version_update_facility ON floor_version FOR UPDATE
    USING ((SELECT jinji_viewer_is_facility_admin_user())) WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY floor_layout_insert_facility ON floor_layout FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY floor_layout_update_facility ON floor_layout FOR UPDATE
    USING ((SELECT jinji_viewer_is_facility_admin_user())) WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY layout_zone_insert_facility ON layout_zone FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
CREATE POLICY layout_seat_insert_facility ON layout_seat FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));

-- 参照: 施設管理者には将来の建物・階・改訂（新設・改修の予定）も見せる（人の割り当ては今のまま）
DROP POLICY building_version_select ON building_version;
CREATE POLICY building_version_select ON building_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR (SELECT jinji_viewer_is_facility_admin_user())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));
DROP POLICY floor_version_select ON floor_version;
CREATE POLICY floor_version_select ON floor_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR (SELECT jinji_viewer_is_facility_admin_user())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));
DROP POLICY floor_layout_select ON floor_layout;
CREATE POLICY floor_layout_select ON floor_layout FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR (SELECT jinji_viewer_is_facility_admin_user())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));

-- 変更フィード: 配置図の行の追加・記録期間を閉じたトリガーが、書き込んだ施設管理者で記録する
CREATE POLICY change_event_insert_facility ON change_event FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_facility_admin_user()));
