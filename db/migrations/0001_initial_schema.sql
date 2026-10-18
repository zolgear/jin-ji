-- 0001: 骨格の最小スキーマ
--
-- 前提
-- - テーブル所有者ロール（jinji_owner）で実行する。アプリ用ロール（jinji_app）はテーブル所有者にしない
-- - 保持する項目は ADR-0002 の「保持する項目」に限る。保持しない項目の列は作らない
--   列を追加したら db/field-catalog.json にも分類を登録すること（テストで検査する）
-- - ID は UUIDv7 をアプリ側で生成する（PostgreSQL 17 に uuidv7() が無いため、DB 側の既定値は付けない）
--
-- 二つの時間軸（bitemporal、アーキテクチャ概要 §4.1）
-- - valid_period    daterange : 事実として有効な期間（有効時間）
-- - recorded_period tstzrange : jin-ji がその内容を記録していた期間（記録時間）
-- - 訂正は行を上書きしない。旧行の recorded_period を閉じ、新しい行を追加する
-- - 「現在記録中の行」は upper_inf(recorded_period) の行
--
-- RLS は次の手順（認可エンジン + RLS + 監査ログ）で有効化する。

CREATE EXTENSION IF NOT EXISTS ltree;
CREATE EXTENSION IF NOT EXISTS btree_gist;

-- ---------------------------------------------------------------------------
-- データベース・スキーマの権限
-- ---------------------------------------------------------------------------

DO $$
BEGIN
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO jinji_app', current_database());
END
$$;

REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO jinji_app;

-- ---------------------------------------------------------------------------
-- 法人
-- ---------------------------------------------------------------------------

CREATE TABLE legal_entity (
    id    uuid PRIMARY KEY,
    code  text NOT NULL,
    name  text NOT NULL,
    CONSTRAINT legal_entity_code_key UNIQUE (code),
    CONSTRAINT legal_entity_code_not_blank CHECK (btrim(code) <> ''),
    CONSTRAINT legal_entity_name_not_blank CHECK (btrim(name) <> '')
);
COMMENT ON TABLE legal_entity IS '法人。出向・出向受入やグループ会社を表すため、最初から複数法人を前提にする';

-- ---------------------------------------------------------------------------
-- 組織（不変の ID）と人（不変の ID）
-- ---------------------------------------------------------------------------

CREATE TABLE org_unit (
    id               uuid PRIMARY KEY,
    legal_entity_id  uuid NOT NULL REFERENCES legal_entity (id)
);
COMMENT ON TABLE org_unit IS '組織の不変 ID。名称・コード・上位組織などは org_unit_version に版として持つ';

CREATE TABLE person (
    id               uuid PRIMARY KEY,
    legal_entity_id  uuid NOT NULL REFERENCES legal_entity (id),
    employee_number  text NOT NULL,
    CONSTRAINT person_employee_number_key UNIQUE (legal_entity_id, employee_number),
    CONSTRAINT person_employee_number_not_blank CHECK (btrim(employee_number) <> '')
);
COMMENT ON TABLE person IS '人の不変 ID。氏名・連絡先・在籍情報は person_version に版として持つ';

-- ---------------------------------------------------------------------------
-- 組織の版
-- ---------------------------------------------------------------------------

CREATE TABLE org_unit_version (
    id                  uuid      PRIMARY KEY,
    org_unit_id         uuid      NOT NULL REFERENCES org_unit (id),
    code                text      NOT NULL,
    name                text      NOT NULL,
    short_name          text,
    unit_type           text      NOT NULL,
    parent_org_unit_id  uuid      REFERENCES org_unit (id),
    head_person_id      uuid      REFERENCES person (id),
    path                ltree     NOT NULL,
    valid_period        daterange NOT NULL,
    recorded_period     tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT org_unit_version_code_not_blank CHECK (btrim(code) <> ''),
    CONSTRAINT org_unit_version_name_not_blank CHECK (btrim(name) <> ''),
    -- 本部 / 部 / 課 / 係 / グループ / その他
    CONSTRAINT org_unit_version_unit_type_check
        CHECK (unit_type IN ('division', 'department', 'section', 'unit', 'group', 'other')),
    CONSTRAINT org_unit_version_not_own_parent CHECK (parent_org_unit_id IS DISTINCT FROM org_unit_id),
    CONSTRAINT org_unit_version_valid_period_check
        CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT org_unit_version_recorded_period_check
        CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    -- 同一組織の版の有効期間は重ならない（現在記録中の行に限る）
    CONSTRAINT org_unit_version_no_overlap
        EXCLUDE USING gist (org_unit_id WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period))
);
COMMENT ON TABLE org_unit_version IS '組織の版（bitemporal）。path は最上位から自組織までの org_unit.id（ハイフンなし）を並べた ltree';
CREATE INDEX org_unit_version_path_idx ON org_unit_version USING gist (path);
CREATE INDEX org_unit_version_parent_idx ON org_unit_version (parent_org_unit_id);

-- ---------------------------------------------------------------------------
-- 人の属性の版
-- ---------------------------------------------------------------------------

CREATE TABLE person_version (
    id                 uuid      PRIMARY KEY,
    person_id          uuid      NOT NULL REFERENCES person (id),
    -- 氏名（業務上の氏名。通称・旧姓使用を含む。戸籍名は持たない）
    family_name        text      NOT NULL,
    given_name         text,
    family_name_kana   text,
    given_name_kana    text,
    family_name_latin  text,
    given_name_latin   text,
    display_name       text      NOT NULL,
    search_name        text      NOT NULL,
    search_name_kana   text,
    -- 業務連絡先
    work_email         text,
    extension_number   text,
    work_mobile_phone  text,
    work_location      text,
    -- 在籍・その他
    employment_type    text      NOT NULL,
    job_type           text,
    hire_date          date      NOT NULL,
    termination_date   date,
    valid_period       daterange NOT NULL,
    recorded_period    tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT person_version_family_name_not_blank CHECK (btrim(family_name) <> ''),
    CONSTRAINT person_version_display_name_not_blank CHECK (btrim(display_name) <> ''),
    -- 正社員 / 契約 / 嘱託 / 派遣 / 出向受入 / パート・アルバイト / その他
    CONSTRAINT person_version_employment_type_check
        CHECK (employment_type IN ('regular', 'contract', 'entrusted', 'dispatched', 'seconded_in', 'part_time', 'other')),
    CONSTRAINT person_version_termination_after_hire
        CHECK (termination_date IS NULL OR termination_date >= hire_date),
    CONSTRAINT person_version_valid_period_check
        CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT person_version_recorded_period_check
        CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    -- 同一人物の版の有効期間は重ならない（現在記録中の行に限る）
    CONSTRAINT person_version_no_overlap
        EXCLUDE USING gist (person_id WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period))
);
COMMENT ON TABLE person_version IS '人の属性の版（bitemporal）。氏名の履歴は婚姻・離婚を推測させるため、参照は人事管理者に限る（RLS は次の手順で付ける）';
CREATE INDEX person_version_search_name_idx ON person_version (search_name);

-- ---------------------------------------------------------------------------
-- 発令
-- ---------------------------------------------------------------------------

CREATE TABLE personnel_action (
    id               uuid      PRIMARY KEY,
    person_id        uuid      NOT NULL REFERENCES person (id),
    action_type      text      NOT NULL,
    effective_date   date      NOT NULL,
    org_unit_id      uuid      REFERENCES org_unit (id),
    assignment_type  text,
    position_title   text,
    document_number  text,
    supersedes_id    uuid      REFERENCES personnel_action (id),
    recorded_period  tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    -- 異動・役職の任免・入退社・兼務・出向・一時停止（給与等級・給与に関わる発令は取り込まない）
    CONSTRAINT personnel_action_action_type_check
        CHECK (action_type IN (
            'hire', 'termination', 'transfer',
            'position_appointment', 'position_dismissal',
            'concurrent_start', 'concurrent_end',
            'secondment_start', 'secondment_end',
            'suspension_start', 'suspension_end')),
    CONSTRAINT personnel_action_assignment_type_check
        CHECK (assignment_type IS NULL OR assignment_type IN ('primary', 'concurrent')),
    CONSTRAINT personnel_action_recorded_period_check
        CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period))
);
COMMENT ON TABLE personnel_action IS '発令（bitemporal）。有効時間は発令日 effective_date。所属・役職の版は発令から導出する。発令日前の発令（内示）は人事管理者以外に見せない';
CREATE INDEX personnel_action_person_idx ON personnel_action (person_id, effective_date);

-- ---------------------------------------------------------------------------
-- 所属（主務・兼務）
-- ---------------------------------------------------------------------------

CREATE TABLE assignment (
    id                   uuid      PRIMARY KEY,
    person_id            uuid      NOT NULL REFERENCES person (id),
    org_unit_id          uuid      NOT NULL REFERENCES org_unit (id),
    assignment_type      text      NOT NULL,
    position_title       text,
    secondment_type      text      NOT NULL DEFAULT 'none',
    personnel_action_id  uuid      REFERENCES personnel_action (id),
    supersedes_id        uuid      REFERENCES assignment (id),
    valid_period         daterange NOT NULL,
    recorded_period      tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    -- 主務 / 兼務
    CONSTRAINT assignment_assignment_type_check CHECK (assignment_type IN ('primary', 'concurrent')),
    -- 出向なし / 出向（送り出し）/ 出向受入
    CONSTRAINT assignment_secondment_type_check CHECK (secondment_type IN ('none', 'outbound', 'inbound')),
    CONSTRAINT assignment_valid_period_check
        CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT assignment_recorded_period_check
        CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    -- 同一人物の主務の有効期間は重ならない（現在記録中の行に限る）。兼務は重なってよい
    CONSTRAINT assignment_primary_no_overlap
        EXCLUDE USING gist (person_id WITH =, valid_period WITH &&)
        WHERE (assignment_type = 'primary' AND upper_inf(recorded_period))
);
COMMENT ON TABLE assignment IS '所属・役職（bitemporal）。1 人が同時に複数持てる（兼務・出向）。personnel_action_id は導出元の発令の行';
CREATE INDEX assignment_person_idx ON assignment (person_id);
CREATE INDEX assignment_org_unit_idx ON assignment (org_unit_id);

-- ---------------------------------------------------------------------------
-- アカウントの一時停止（休職・休業などを理由を持たずに表す。ADR-0002）
-- ---------------------------------------------------------------------------

CREATE TABLE account_suspension (
    id                   uuid      PRIMARY KEY,
    person_id            uuid      NOT NULL REFERENCES person (id),
    personnel_action_id  uuid      REFERENCES personnel_action (id),
    supersedes_id        uuid      REFERENCES account_suspension (id),
    valid_period         daterange NOT NULL,
    recorded_period      tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT account_suspension_valid_period_check
        CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT account_suspension_recorded_period_check
        CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    CONSTRAINT account_suspension_no_overlap
        EXCLUDE USING gist (person_id WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period))
);
COMMENT ON TABLE account_suspension IS 'アカウントの一時停止（bitemporal）。理由は保持しない。valid_period は開始日〜終了予定日';

-- ---------------------------------------------------------------------------
-- 外部 ID の対応
-- ---------------------------------------------------------------------------

CREATE TABLE external_identity (
    id             uuid        PRIMARY KEY,
    person_id      uuid        NOT NULL REFERENCES person (id),
    source_system  text        NOT NULL,
    external_id    text        NOT NULL,
    recorded_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT external_identity_source_key UNIQUE (source_system, external_id),
    CONSTRAINT external_identity_source_system_not_blank CHECK (btrim(source_system) <> ''),
    CONSTRAINT external_identity_external_id_not_blank CHECK (btrim(external_id) <> '')
);
COMMENT ON TABLE external_identity IS '外部 ID の対応（人事パッケージの社員 ID、Entra ID の objectId 等）';
CREATE INDEX external_identity_person_idx ON external_identity (person_id);

-- ---------------------------------------------------------------------------
-- 監査ログ（追記専用）
-- ---------------------------------------------------------------------------

CREATE TABLE audit_event (
    id                      uuid        PRIMARY KEY,
    occurred_at             timestamptz NOT NULL DEFAULT now(),
    actor_type              text        NOT NULL,
    actor_person_id         uuid,
    actor_client_id         text,
    on_behalf_of_person_id  uuid,
    channel                 text        NOT NULL,
    action                  text        NOT NULL,
    target_type             text,
    target_id               uuid,
    subject_person_ids      uuid[]      NOT NULL DEFAULT '{}',
    outcome                 text        NOT NULL,
    request_id              text,
    CONSTRAINT audit_event_actor_type_check CHECK (actor_type IN ('user', 'service', 'agent', 'system')),
    CONSTRAINT audit_event_channel_check CHECK (channel IN ('ui', 'api', 'mcp', 'connector', 'worker', 'cli')),
    CONSTRAINT audit_event_outcome_check CHECK (outcome IN ('success', 'denied', 'failed')),
    CONSTRAINT audit_event_action_not_blank CHECK (btrim(action) <> '')
);
COMMENT ON TABLE audit_event IS '監査ログ。誰が・何を・どの経路で参照/変更/配信したか。アプリ用ロールには INSERT/SELECT のみ許可する。人の削除・匿名化後も残すため person への外部キーは張らない';
CREATE INDEX audit_event_occurred_at_idx ON audit_event (occurred_at);
CREATE INDEX audit_event_subject_person_ids_idx ON audit_event USING gin (subject_person_ids);

-- ---------------------------------------------------------------------------
-- アプリ用ロール（jinji_app）の権限。必要最小限に絞る
-- - DELETE はどのテーブルにも与えない（匿名化・削除は承認付きの別手順で行う）
-- - bitemporal のテーブルは、訂正のために recorded_period の列だけ UPDATE を許す
-- - audit_event は INSERT と SELECT のみ（追記専用）
-- - schema_migration には権限を与えない
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT ON
    legal_entity, org_unit, person, external_identity,
    org_unit_version, person_version, personnel_action, assignment, account_suspension
    TO jinji_app;

GRANT UPDATE (recorded_period) ON
    org_unit_version, person_version, personnel_action, assignment, account_suspension
    TO jinji_app;

GRANT SELECT, INSERT ON audit_event TO jinji_app;
