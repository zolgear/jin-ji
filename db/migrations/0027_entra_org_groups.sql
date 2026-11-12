-- 0027: 組織に対応する Entra ID のグループの計画（ADR-0015）
--
-- 位置づけ
-- - 規程上の組織ごとに Entra ID のグループを作り、メンバーを jin-ji の所属に合わせる。反映はユーザーの同期（0009）と同じく
--   「計画 → 別の人事管理者の承認 → スクリプト出力・直接反映 → 実行結果の照合」で、作業領域の表（sync_plan ほか）を共有する
-- - 計画の種類は同期先（target_system）で分ける: entra_id（ユーザー）と entra_id_groups（組織のグループ）。
--   新しい計画が置き換えるのは同じ種類の計画だけなので、ユーザーの計画とグループの計画は別々に起案・承認できる
-- - グループと組織の対応付けは、グループの説明に書いた印で行う（jin-ji は Entra のオブジェクト ID を公開側に持たない）。
--   項目には、対象の組織・グループのオブジェクト ID（同じ計画で作るグループでは NULL）・表示用のグループの名前を持つ
--
-- 項目の対象（directory_object_id）
-- - グループそのものの項目（作成・更新・廃止の印・チーム・衝突）: グループのオブジェクト ID（作成では NULL。これから作るグループのチームも NULL）
-- - メンバー・オーナーの項目: ユーザーのオブジェクト ID（グループは group_object_id）
-- - 参考（group_note）: 対象が無いことがある（アカウントの対応の無い人など）

-- ---------------------------------------------------------------------------
-- 1. 項目のキー（changes・observed に現れてよいもの）
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION jinji_sync_field_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'displayName', 'givenName', 'surname', 'department', 'jobTitle', 'companyName', 'officeLocation', 'employeeId',
        'accountEnabled', 'manager', 'userPrincipalName', 'mailNickname',
        'group.displayName', 'group.description', 'group.mailNickname', 'group.type', 'group.member', 'group.owner', 'group.team'
    ]::text[]
$$;

-- ---------------------------------------------------------------------------
-- 2. 計画の種類
-- ---------------------------------------------------------------------------

ALTER TABLE sync_plan DROP CONSTRAINT sync_plan_target_system_check;
ALTER TABLE sync_plan ADD CONSTRAINT sync_plan_target_system_check CHECK (target_system IN ('entra_id', 'entra_id_groups'));
COMMENT ON COLUMN sync_plan.target_system IS '計画の種類（entra_id = ユーザーの属性とアカウント、entra_id_groups = 組織に対応するグループ）';
COMMENT ON COLUMN sync_plan.directory_user_count IS '入力の件数（ユーザーの計画ではユーザー、グループの計画ではグループ）';

-- ---------------------------------------------------------------------------
-- 3. 項目
-- ---------------------------------------------------------------------------

ALTER TABLE sync_plan_item ADD COLUMN org_unit_id uuid REFERENCES org_unit (id);
ALTER TABLE sync_plan_item ADD COLUMN group_object_id text;
ALTER TABLE sync_plan_item ADD COLUMN group_display_name text;
COMMENT ON COLUMN sync_plan_item.org_unit_id IS 'グループの計画: 対象の組織（廃止された組織のグループでは NULL）';
COMMENT ON COLUMN sync_plan_item.group_object_id IS 'グループの計画: グループのオブジェクト ID（同じ計画で作るグループでは NULL）';
COMMENT ON COLUMN sync_plan_item.group_display_name IS 'グループの計画: 表示用のグループの名前';

ALTER TABLE sync_plan_item DROP CONSTRAINT sync_plan_item_action_check;
ALTER TABLE sync_plan_item ADD CONSTRAINT sync_plan_item_action_check CHECK (action IN (
    'disable_account', 'block_sign_in', 'enable_account', 'update_attributes', 'set_manager', 'create_user',
    'unmatched_account', 'excluded_account',
    'create_group', 'update_group', 'retire_group', 'add_group_member', 'remove_group_member', 'add_group_owner', 'create_team',
    'group_conflict', 'group_note'));

-- 参考・衝突の項目は実行しない
ALTER TABLE sync_plan_item DROP CONSTRAINT sync_plan_item_in_script_check;
ALTER TABLE sync_plan_item ADD CONSTRAINT sync_plan_item_in_script_check CHECK (
    NOT in_script OR action NOT IN ('unmatched_account', 'excluded_account', 'group_conflict', 'group_note'));

ALTER TABLE sync_plan_item DROP CONSTRAINT sync_plan_item_object_id_check;
ALTER TABLE sync_plan_item ADD CONSTRAINT sync_plan_item_object_id_check CHECK (
    (action NOT IN ('create_user', 'create_group') OR directory_object_id IS NULL)
    AND (action IN ('create_user', 'create_group', 'create_team', 'group_note') OR directory_object_id IS NOT NULL)
    AND (directory_object_id IS NULL OR directory_object_id ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'));

-- グループの列は、グループの計画の項目だけが持つ
ALTER TABLE sync_plan_item ADD CONSTRAINT sync_plan_item_group_columns_check CHECK (
    (action IN ('create_group', 'update_group', 'retire_group', 'add_group_member', 'remove_group_member', 'add_group_owner', 'create_team',
                'group_conflict', 'group_note')
     OR (org_unit_id IS NULL AND group_object_id IS NULL AND group_display_name IS NULL))
    AND (group_object_id IS NULL OR group_object_id ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')
    AND (group_display_name IS NULL OR char_length(group_display_name) <= 300));
