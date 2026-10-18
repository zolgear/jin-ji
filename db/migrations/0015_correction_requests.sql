-- 0015: 修正依頼の仕上げ（アーキテクチャ概要 §8「修正依頼の実装の規則」）
--
-- 位置づけ（2026-10-12 決定）
-- - jin-ji の公開データの正は人事パッケージ。jin-ji の値が誤っていても jin-ji では直さず、人事パッケージで直して取り込み直す。
--   修正依頼は「人事パッケージの情報を直せる人への情報提供」で、起票しても jin-ji の公開データは変わらない（公開側への入口は取込の変更提案の承認だけ）
-- - 起票の経路は 3 つ: 取込の自動検出（0005 以来。origin = ingest）、人（UI・REST。origin = user）、AI エージェント（MCP・REST。origin = agent。委任元の名義）
--
-- 内容
-- 1. ロール hr_corrector（修正担当者。人事パッケージで直せる人）。修正依頼の参照・状態の更新（対応済み・対応不要）ができる。人事管理者も同じことができる
-- 2. correction_request に、人・項目の報告（対象の人・項目・理由・値の提案・起票者）と、閉じ方（人 / 自動）・対応のメモを足す
--    （理由の列名は explanation。reason は休職の理由などを表す語として ADR-0002 の禁止語の検査に当たるため、API・MCP の名前だけ reason にする）
--    - 同じ人・項目の未対応の報告は 1 つだけ（部分一意索引）。重ねて起票されたら賛同（correction_endorsement）として数える
--    - 起票時の値の指紋（value_fingerprint。依頼ごとの乱数と値の SHA-256。値そのものは持たない）を持ち、値が変わったら自動で「解消」（auto_resolved）にする
--    - 取込由来の依頼は、同じ項目マッピングの次の取込で「置き換え」（superseded）にする（まだ直っていなければ新しい取込が改めて起票する）
-- 3. 賛同（correction_endorsement）。追記だけ
-- 4. 通知の実行の記録（correction_notice_run）。件数と送信先のホスト・結果だけで、人事データの値は持たない。追記だけ
--
-- 可視範囲（RLS。アプリはさらに列を伏せる）
-- - 人事管理者・修正担当者・jin-ji 自身: すべて
-- - 取込の実行者本人: その取込の依頼（0005 のまま）
-- - それ以外の人（AI エージェントは委任元）: 自分が起票した依頼、自分が賛同した依頼、自分自身についての依頼
-- - 社内システム（人に紐付かない）: なし
--
-- 書き込み
-- - 起票（人・項目の報告）は人か AI エージェントの閲覧者だけで、起票者は閲覧者本人（委任元）、origin は閲覧者の種別と一致すること
-- - 状態の更新は、人事管理者か修正担当者の「人」と jin-ji 自身だけ（AI エージェントは委任元が修正担当者でも状態を変えられない）。
--   閉じた人は閲覧者本人としてしか記録できない。状態の遷移（未対応 → 対応済み・対応不要・解消・置き換え）と中身の不変はトリガーで守る
-- - 賛同の記録も起票と同じく人か AI エージェントの本人だけ。起票者本人の賛同はアプリが拒否する（他の行を読む検査はトリガーに書かない。§5.6）
--
-- 循環を作らない: correction_request の参照の方針は correction_endorsement を読むので、correction_endorsement の方針は correction_request を読まない
-- （PostgreSQL は方針の相互参照を無限の再帰として拒否する）。賛同の件数は jin-ji 自身の閲覧者で数える（件数は人事データの値ではない）

-- ---------------------------------------------------------------------------
-- 1. ロール
-- ---------------------------------------------------------------------------

ALTER TABLE role_grant DROP CONSTRAINT role_grant_role_check;
-- 既知のロール（JinJi.Policy の Roles.Known と一致させる）。hr_corrector = 修正担当者（人事パッケージで直せる人）
ALTER TABLE role_grant ADD CONSTRAINT role_grant_role_check CHECK (role IN ('hr_admin', 'hr_corrector'));

CREATE FUNCTION jinji_viewer_is_hr_corrector() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_set()
       AND 'hr_corrector' = ANY (pg_catalog.string_to_array(coalesce(pg_catalog.current_setting('jinji.viewer_roles', true), ''), ','))
$$;
COMMENT ON FUNCTION jinji_viewer_is_hr_corrector() IS 'RLS: 閲覧者が hr_corrector（修正担当者）ロールを持つか（GUC jinji.viewer_roles）';

-- 修正依頼のすべてを参照できる閲覧者（人事管理者・修正担当者。AI エージェントは委任元のロール）
CREATE FUNCTION jinji_viewer_handles_corrections() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_hr_admin() OR public.jinji_viewer_is_hr_corrector()
$$;
COMMENT ON FUNCTION jinji_viewer_handles_corrections() IS 'RLS: 修正依頼のすべてを参照できる閲覧者か（hr_admin か hr_corrector。AI エージェントは委任元のロール）';

-- 修正依頼の状態を変えられる閲覧者（人事管理者・修正担当者の人と、jin-ji 自身。AI エージェントは不可）
CREATE FUNCTION jinji_viewer_can_close_corrections() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce(public.jinji_viewer_kind() IN ('user', 'system'), false) AND public.jinji_viewer_handles_corrections()
$$;
COMMENT ON FUNCTION jinji_viewer_can_close_corrections() IS 'RLS: 修正依頼の状態を変えられる閲覧者か（hr_admin・hr_corrector の人と、hr_admin を付けた jin-ji 自身。AI エージェントは不可）';

-- ---------------------------------------------------------------------------
-- 2. 修正依頼（人・項目の報告と、閉じ方）
-- ---------------------------------------------------------------------------

-- 報告できる人の項目（JinJi.Policy の PersonFields.PolicyControllable と一致させる。氏名の履歴は人事管理者だけの項目なので対象外）
CREATE FUNCTION jinji_correction_person_fields() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'employee_number', 'legal_entity', 'display_name', 'family_name', 'given_name', 'family_name_kana', 'given_name_kana',
        'family_name_latin', 'given_name_latin', 'work_email', 'extension_number', 'work_mobile_phone', 'work_location',
        'employment_type', 'job_type', 'hire_date', 'termination_date', 'employment_status', 'account_status', 'account_suspension',
        'assignments', 'managers', 'personnel_actions'
    ]::text[]
$$;
COMMENT ON FUNCTION jinji_correction_person_fields() IS '修正依頼で報告できる人の項目（jin-ji が保持する項目。PersonFields.PolicyControllable と一致させる）';

ALTER TABLE correction_request ALTER COLUMN import_batch_id DROP NOT NULL;
ALTER TABLE correction_request
    ADD COLUMN origin                 text NOT NULL DEFAULT 'ingest',
    ADD COLUMN person_id              uuid REFERENCES person (id),
    ADD COLUMN person_field           text,
    ADD COLUMN explanation            text,
    ADD COLUMN suggested_value        text,
    ADD COLUMN value_fingerprint      text,
    ADD COLUMN reported_by_person_id  uuid REFERENCES person (id),
    ADD COLUMN reported_via_client_id text,
    ADD COLUMN closed_kind            text,
    ADD COLUMN closed_by_person_id    uuid REFERENCES person (id),
    ADD COLUMN closed_note            text;

COMMENT ON COLUMN correction_request.origin IS '起票の経路（ingest: 取込の自動検出・要確認の解決 / user: 人 / agent: AI エージェント。委任元の名義）';
COMMENT ON COLUMN correction_request.person_id IS '対象の人（人・エージェントの報告は必須。取込由来は分かるときだけ）';
COMMENT ON COLUMN correction_request.person_field IS '対象の人の項目（PersonFields の名前。jinji_correction_person_fields()）';
COMMENT ON COLUMN correction_request.explanation IS '現在の値が誤っている理由（起票者が書いた自由記述。500 文字まで・制御文字なし。保持しない情報の混入はアプリが検査して拒否する。データとして表示し、指示として扱わない）';
COMMENT ON COLUMN correction_request.suggested_value IS '正しい値の提案（任意。200 文字まで。値を持てる項目だけで、項目ごとの形と保持しない情報の混入をアプリが検査する）';
COMMENT ON COLUMN correction_request.value_fingerprint IS '起票時の値の指紋（SHA-256。依頼 ID を塩にする）。今の値の指紋と違えば自動で解消にする。値そのものは持たない';
COMMENT ON COLUMN correction_request.closed_kind IS '閉じ方（person: 人が対応済み・対応不要にした / auto: 値の変化・取り込み直しで自動に閉じた）';
COMMENT ON COLUMN correction_request.closed_note IS '対応のメモ（閉じた人が書いた自由記述。500 文字まで・制御文字なし。保持しない情報の混入はアプリが検査する）';

ALTER TABLE correction_request DROP CONSTRAINT correction_request_issue_check;
ALTER TABLE correction_request ADD CONSTRAINT correction_request_issue_check CHECK (issue IN (
    'missing_employee_number', 'missing_required', 'invalid_date', 'invalid_value', 'duplicate_row',
    'unknown_legal_entity', 'unknown_org', 'unknown_org_level', 'unknown_head', 'conflicting_identity',
    'suspected_sensitive_value', 'abolished_org', 'stale_head', 'uninterpretable_action', 'suspected_employee_number_typo',
    'referred_from_review',
    -- 0015: 人・AI エージェントによる値の誤りの報告
    'reported_value'));

ALTER TABLE correction_request DROP CONSTRAINT correction_request_status_check;
-- 未対応 / 対応済み / 対応不要 / 解消（値が変わったので自動に閉じた）/ 置き換え（同じ項目マッピングの次の取込が改めて起票した）
ALTER TABLE correction_request ADD CONSTRAINT correction_request_status_check CHECK (
    status IN ('open', 'resolved', 'dismissed', 'auto_resolved', 'superseded'));

ALTER TABLE correction_request
    ADD CONSTRAINT correction_request_origin_check CHECK (origin IN ('ingest', 'user', 'agent')),
    -- 取込由来は取込単位を持ち、人・エージェントの報告は取込単位・行を持たず、対象の人・項目・理由・起票者を必ず持つ
    ADD CONSTRAINT correction_request_origin_shape CHECK (
        CASE origin
            WHEN 'ingest' THEN import_batch_id IS NOT NULL AND reported_by_person_id IS NULL AND explanation IS NULL AND suggested_value IS NULL
            ELSE import_batch_id IS NULL AND staging_row_id IS NULL AND source_row_number IS NULL AND field_key IS NULL
                 AND issue = 'reported_value' AND person_id IS NOT NULL AND person_field IS NOT NULL AND explanation IS NOT NULL
                 AND reported_by_person_id IS NOT NULL AND value_fingerprint IS NOT NULL
                 AND (origin = 'user' OR reported_via_client_id IS NOT NULL)
        END),
    ADD CONSTRAINT correction_request_person_field_allowed CHECK (
        person_field IS NULL OR (person_field = ANY (public.jinji_correction_person_fields()) AND NOT public.jinji_ingest_forbidden_key(person_field))),
    ADD CONSTRAINT correction_request_explanation_check CHECK (
        explanation IS NULL OR (btrim(explanation) <> '' AND char_length(explanation) <= 500 AND explanation !~ '[[:cntrl:]]')),
    ADD CONSTRAINT correction_request_suggested_value_check CHECK (
        suggested_value IS NULL OR (person_field IS NOT NULL AND btrim(suggested_value) <> '' AND char_length(suggested_value) <= 200 AND suggested_value !~ '[[:cntrl:]]')),
    ADD CONSTRAINT correction_request_fingerprint_format CHECK (value_fingerprint IS NULL OR value_fingerprint ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT correction_request_client_check CHECK (
        reported_via_client_id IS NULL OR (btrim(reported_via_client_id) <> '' AND char_length(reported_via_client_id) <= 2048)),
    ADD CONSTRAINT correction_request_closed_note_check CHECK (
        closed_note IS NULL OR (btrim(closed_note) <> '' AND char_length(closed_note) <= 500 AND closed_note !~ '[[:cntrl:]]')),
    ADD CONSTRAINT correction_request_closed_kind_check CHECK (
        closed_kind IS NULL OR closed_kind IN ('person', 'auto'));

-- 閉じ方の整合。0015 より前に閉じた行（閉じ方の記録が無い）があり得るので、新しく書き込む行・更新する行にだけ効かせる
ALTER TABLE correction_request ADD CONSTRAINT correction_request_closed_shape CHECK (
    CASE status
        WHEN 'open' THEN closed_kind IS NULL AND closed_by_person_id IS NULL AND closed_note IS NULL
        WHEN 'auto_resolved' THEN closed_kind = 'auto' AND closed_by_person_id IS NULL
        WHEN 'superseded' THEN closed_kind = 'auto' AND closed_by_person_id IS NULL
        ELSE closed_kind = 'person' AND closed_by_person_id IS NOT NULL
    END) NOT VALID;

-- 同じ人・項目の未対応の報告は 1 つだけ（重ねての起票は賛同になる）
CREATE UNIQUE INDEX correction_request_open_report_key ON correction_request (person_id, person_field)
    WHERE status = 'open' AND origin IN ('user', 'agent');
CREATE INDEX correction_request_status_created_idx ON correction_request (status, created_at);
CREATE INDEX correction_request_person_idx ON correction_request (person_id) WHERE person_id IS NOT NULL;
CREATE INDEX correction_request_reporter_idx ON correction_request (reported_by_person_id) WHERE reported_by_person_id IS NOT NULL;

-- 中身は変えられず、状態は未対応から閉じる方向にだけ 1 回変えられる。
-- 例外: staging の行の保持期限の消去で、外部キーの ON DELETE SET NULL が staging_row_id を NULL にする（0005。行番号は残る）
CREATE FUNCTION jinji_guard_correction_request() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    workflow CONSTANT text[] := ARRAY['status', 'resolved_at', 'closed_kind', 'closed_by_person_id', 'closed_note', 'staging_row_id'];
BEGIN
    IF (to_jsonb(NEW) - workflow) IS DISTINCT FROM (to_jsonb(OLD) - workflow)
       OR (NEW.staging_row_id IS NOT NULL AND NEW.staging_row_id IS DISTINCT FROM OLD.staging_row_id) THEN
        RAISE EXCEPTION 'correction_request: 修正依頼の中身は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'correction_request_immutable';
    END IF;

    IF (NEW.status, NEW.resolved_at, NEW.closed_kind, NEW.closed_by_person_id, NEW.closed_note)
       IS NOT DISTINCT FROM (OLD.status, OLD.resolved_at, OLD.closed_kind, OLD.closed_by_person_id, OLD.closed_note) THEN
        RETURN NEW;
    END IF;

    IF OLD.status = 'open' AND NEW.status IN ('resolved', 'dismissed', 'auto_resolved', 'superseded') THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'correction_request: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'correction_request_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_correction_request() IS 'correction_request の状態遷移（未対応 → 対応済み・対応不要・解消・置き換え）と中身の不変を守るトリガー関数';

CREATE TRIGGER correction_request_guard BEFORE UPDATE ON correction_request
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_correction_request();

-- ---------------------------------------------------------------------------
-- 3. 賛同
-- ---------------------------------------------------------------------------

CREATE TABLE correction_endorsement (
    id                      uuid        PRIMARY KEY,
    correction_request_id   uuid        NOT NULL REFERENCES correction_request (id),
    endorsed_by_person_id   uuid        NOT NULL REFERENCES person (id),
    endorsed_via_actor      text        NOT NULL,
    endorsed_via_client_id  text,
    created_at              timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT correction_endorsement_actor_check CHECK (endorsed_via_actor IN ('user', 'agent')),
    CONSTRAINT correction_endorsement_client_check CHECK (
        (endorsed_via_actor = 'user' OR endorsed_via_client_id IS NOT NULL)
        AND (endorsed_via_client_id IS NULL OR (btrim(endorsed_via_client_id) <> '' AND char_length(endorsed_via_client_id) <= 2048))),
    -- 1 人 1 回
    CONSTRAINT correction_endorsement_once UNIQUE (correction_request_id, endorsed_by_person_id)
);
COMMENT ON TABLE correction_endorsement IS '修正依頼への賛同（同じ人・項目の未対応の依頼に重ねて起票された記録）。追記だけ。参照は人事管理者・修正担当者・jin-ji 自身と賛同した本人';
CREATE INDEX correction_endorsement_person_idx ON correction_endorsement (endorsed_by_person_id, created_at);

-- ---------------------------------------------------------------------------
-- 4. 通知の実行の記録
-- ---------------------------------------------------------------------------

CREATE TABLE correction_notice_run (
    id                  uuid        PRIMARY KEY,
    trigger             text        NOT NULL,
    outcome             text        NOT NULL,
    started_at          timestamptz NOT NULL,
    finished_at         timestamptz NOT NULL,
    open_count          integer     NOT NULL,
    new_count           integer     NOT NULL,
    stale_count         integer     NOT NULL,
    auto_resolved_count integer     NOT NULL DEFAULT 0,
    destination_host    text,
    http_status         integer,
    -- 定期実行（ワーカー）/ 管理 CLI
    CONSTRAINT correction_notice_run_trigger_check CHECK (trigger IN ('worker', 'cli')),
    -- 送った / 送る件数が無い / Webhook が無効 / 失敗
    CONSTRAINT correction_notice_run_outcome_check CHECK (outcome IN ('sent', 'nothing_to_send', 'disabled', 'failed')),
    CONSTRAINT correction_notice_run_period_check CHECK (finished_at >= started_at),
    CONSTRAINT correction_notice_run_counts_check CHECK (open_count >= 0 AND new_count >= 0 AND stale_count >= 0 AND auto_resolved_count >= 0),
    CONSTRAINT correction_notice_run_host_check CHECK (destination_host IS NULL OR (btrim(destination_host) <> '' AND char_length(destination_host) <= 253)),
    CONSTRAINT correction_notice_run_status_check CHECK (http_status IS NULL OR http_status BETWEEN 100 AND 599)
);
COMMENT ON TABLE correction_notice_run IS '修正依頼の通知の実行の記録（件数・送信先のホスト・結果だけ。人事データの値と Webhook の URL は持たない）。監査ログ correction.notify の対象。追記だけ';
CREATE INDEX correction_notice_run_started_at_idx ON correction_notice_run (started_at);

-- ---------------------------------------------------------------------------
-- 5. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

ALTER TABLE correction_endorsement ENABLE ROW LEVEL SECURITY;
ALTER TABLE correction_endorsement FORCE ROW LEVEL SECURITY;
ALTER TABLE correction_notice_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE correction_notice_run FORCE ROW LEVEL SECURITY;

DROP POLICY correction_request_select ON correction_request;
DROP POLICY correction_request_insert ON correction_request;
DROP POLICY correction_request_update ON correction_request;

CREATE POLICY correction_request_select ON correction_request FOR SELECT USING (
    (SELECT jinji_viewer_handles_corrections())
    OR (SELECT jinji_viewer_is_system())
    OR (import_batch_id IS NOT NULL AND jinji_viewer_is_batch_executor(import_batch_id))
    OR ((SELECT jinji_viewer_is_set()) AND (
        person_id = (SELECT jinji_viewer_person_id())
        OR reported_by_person_id = (SELECT jinji_viewer_person_id())
        OR EXISTS (
            SELECT 1 FROM correction_endorsement e
            WHERE e.correction_request_id = correction_request.id AND e.endorsed_by_person_id = (SELECT jinji_viewer_person_id())))));

CREATE POLICY correction_request_insert ON correction_request FOR INSERT WITH CHECK (
    (origin = 'ingest' AND ((SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id)))
    OR (origin IN ('user', 'agent')
        AND origin = (SELECT jinji_viewer_kind())
        AND reported_by_person_id = (SELECT jinji_viewer_person_id())
        AND status = 'open'));

CREATE POLICY correction_request_update ON correction_request FOR UPDATE
    USING ((SELECT jinji_viewer_can_close_corrections()))
    WITH CHECK ((SELECT jinji_viewer_can_close_corrections())
                AND (closed_by_person_id IS NULL OR closed_by_person_id = (SELECT jinji_viewer_person_id())));

CREATE POLICY correction_endorsement_select ON correction_endorsement FOR SELECT USING (
    (SELECT jinji_viewer_handles_corrections())
    OR (SELECT jinji_viewer_is_system())
    OR ((SELECT jinji_viewer_is_set()) AND endorsed_by_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY correction_endorsement_insert ON correction_endorsement FOR INSERT WITH CHECK (
    endorsed_via_actor = (SELECT jinji_viewer_kind())
    AND endorsed_by_person_id = (SELECT jinji_viewer_person_id()));

-- 取込単位（ファイル名・行数などの取込の記録。値は持たない）は、修正依頼の根拠として修正担当者にも見せる（参照だけ）
CREATE POLICY import_batch_select_correction_handler ON import_batch FOR SELECT USING ((SELECT jinji_viewer_handles_corrections()));

-- 記録は jin-ji 自身（ワーカー・管理 CLI）、参照は jin-ji 自身・人事管理者・修正担当者
CREATE POLICY correction_notice_run_select ON correction_notice_run FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_handles_corrections()));
CREATE POLICY correction_notice_run_insert ON correction_notice_run FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_system()));

-- ---------------------------------------------------------------------------
-- 6. アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT UPDATE (closed_kind, closed_by_person_id, closed_note) ON correction_request TO jinji_app;
GRANT SELECT, INSERT ON correction_endorsement, correction_notice_run TO jinji_app;
