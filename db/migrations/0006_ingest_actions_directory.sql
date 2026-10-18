-- 0006: 取り込みの拡張（組織図の階層・辞令・Entra ID のユーザー一覧）
--
-- 内容
-- 1. 取込元の種類に Graph の GET /users 応答（graph_json）、取込の対象に辞令（personnel_actions）と Entra ID のユーザー（directory_users）を足す
-- 2. 変更提案の対象に発令（personnel_action）・一時停止（account_suspension）・外部 ID の対応（external_identity）を、操作に
--    組織の廃止（abolish）と所属・一時停止の終了（end）を足す
-- 3. 取込項目と提案の列の許可リストを広げる（在籍区分・兼務・出向・休職開始日・発令の日付と内容・組織図の備考・外部 ID・アカウントの有効/無効）
--    - 自由記述（発令内容・備考）は保持してよい情報の記述だが、生年月日・住所・電話番号らしき値が混ざり得る。取込時に検査し、
--      疑わしい値は staging に保存する前に除いて修正依頼（suspected_sensitive_value）に記録する（値は記録しない）
-- 4. 修正依頼の種別を足す（保持禁止の値の疑い・廃止済み組織の残存・退職者の責任者・解釈できない発令・社員番号の誤記の疑い）
--
-- 許可リストの関数は CHECK 制約から参照されている。CREATE OR REPLACE で中身だけを置き換える（既存の行は再検査しないが、広げるだけなので影響しない）

-- ---------------------------------------------------------------------------
-- 1. 取込単位
-- ---------------------------------------------------------------------------

ALTER TABLE import_batch DROP CONSTRAINT import_batch_source_kind_check;
ALTER TABLE import_batch ADD CONSTRAINT import_batch_source_kind_check CHECK (source_kind IN ('csv', 'xlsx', 'graph_json'));
ALTER TABLE import_batch DROP CONSTRAINT import_batch_target_kind_check;
ALTER TABLE import_batch ADD CONSTRAINT import_batch_target_kind_check CHECK (target_kind IN ('people', 'org_units', 'personnel_actions', 'directory_users'));

-- ---------------------------------------------------------------------------
-- 2. 変更提案
-- ---------------------------------------------------------------------------

ALTER TABLE change_proposal DROP CONSTRAINT change_proposal_target_kind_check;
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_target_kind_check CHECK (target_kind IN (
    'person', 'person_version', 'org_unit', 'org_unit_version', 'assignment', 'personnel_action', 'account_suspension', 'external_identity'));
ALTER TABLE change_proposal DROP CONSTRAINT change_proposal_operation_check;
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_operation_check CHECK (operation IN (
    'create', 'correct', 'change', 'review', 'supplement', 'abolish', 'end'));

-- ---------------------------------------------------------------------------
-- 3. 許可リスト
-- ---------------------------------------------------------------------------

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
        -- 兼務・出向
        'concurrent_org_name', 'concurrent_position_title',
        'secondment_legal_entity_code', 'secondment_org_name', 'secondment_start_date',
        -- 辞令
        'action_date', 'action_text', 'action_note',
        -- 外部のディレクトリ（Entra ID 等）
        'external_id', 'account_enabled',
        -- 正規化で作る検索用の表記
        'search_name', 'search_name_kana', 'org_search_name', 'parent_org_search_name'
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
        -- 0006
        'action_type', 'concurrent_org_unit_id', 'concurrent_position_title', 'secondment_org_unit_id', 'secondment_start_date',
        'suspension_start_date', 'source_system', 'external_id'
    ]::text[]
$$;

-- ---------------------------------------------------------------------------
-- 4. 修正依頼の種別
-- ---------------------------------------------------------------------------

ALTER TABLE correction_request DROP CONSTRAINT correction_request_issue_check;
ALTER TABLE correction_request ADD CONSTRAINT correction_request_issue_check CHECK (issue IN (
    'missing_employee_number', 'missing_required', 'invalid_date', 'invalid_value', 'duplicate_row',
    'unknown_legal_entity', 'unknown_org', 'unknown_org_level', 'unknown_head', 'conflicting_identity',
    'suspected_sensitive_value', 'abolished_org', 'stale_head', 'uninterpretable_action', 'suspected_employee_number_typo'));
