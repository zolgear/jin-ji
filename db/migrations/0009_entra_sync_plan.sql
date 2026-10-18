-- 0009: Entra ID への同期計画（作業領域）・承認・実行結果の照合
--
-- 位置づけ（アーキテクチャ概要 §1・§7.1・§8「同期」）
-- - jin-ji の公開データ（業務日付時点。内示は含めない）と Entra ID のユーザー一覧の差分から同期計画を作り、ここに保存する。
--   計画は「公開されない作業領域」で、承認（起案者 ≠ 承認者。§5.5）を経たものだけがスクリプト出力・直接反映の対象になる
--   （「Entra ID への同期計画の確定」は §7.1 の表で公開される情報の側。AI エージェントは起案まで）
-- - 計画の各項目には、変える項目の Entra の現在値（スクリプトの事前条件）と jin-ji の値・その根拠（どのデータか）を持つ
-- - 計画の作成後に jin-ji 側が変わった（承認・出力・直接反映の時点で jin-ji の値を計算し直すと一致しない）・期限を過ぎた計画は古い計画（stale）にする。
--   Entra 側の変化は、スクリプト・直接反映が各ユーザーの更新前に現在値を読み、計画の値と比べて検出する（違えばスキップして記録）
-- - 新しい計画を作ると、同じ同期先の起案中・承認済み（未実行）の計画は置き換え済み（superseded）になる
-- - 実行結果（スクリプトの実行ログ・直接反映の結果）を取り込み、項目ごとに記録して照合する（§4.6 の配信ログ）
--
-- 守り
-- - 参照・追加は hr_admin だけ（RLS）。値は全社公開レベルの項目（氏名・部署・役職・会社名・勤務地・社員番号・上長・アカウントの有効/無効）だけで、
--   changes / observed のキーは Graph のプロパティ名の許可リスト（jinji_sync_field_keys()）に限る（CHECK 制約。所有者にも効く）
-- - 起案者 ≠ 承認者は CHECK 制約。承認者は閲覧者本人としてしか記録できない（RLS）。状態の遷移と中身の不変はトリガー
-- - 計画の項目・実行の記録・項目ごとの結果は追記だけ（アプリ用ロールに UPDATE / DELETE を与えない）

-- ---------------------------------------------------------------------------
-- 1. 許可リスト
-- ---------------------------------------------------------------------------

-- 同期する項目（Microsoft Graph の user のプロパティ名 + 上長）。JinJi.Application の EntraFields.All と同じ集合（テストで突き合わせる）
CREATE FUNCTION jinji_sync_field_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'displayName', 'givenName', 'surname', 'department', 'jobTitle', 'companyName', 'officeLocation', 'employeeId',
        'accountEnabled', 'manager', 'userPrincipalName', 'mailNickname'
    ]::text[]
$$;
COMMENT ON FUNCTION jinji_sync_field_keys() IS '同期計画の changes / 実行結果の observed に入れてよい項目（Graph の user のプロパティ名と manager）。JinJi.Application の EntraFields.All と一致させる';

-- 計画の差分: [{"field": 項目, "current": 文字列|null, "desired": 文字列|null, "current_known": 真偽}]
CREATE FUNCTION jinji_sync_changes_allowed(changes jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT jsonb_typeof(changes) = 'array'
       AND coalesce((
           SELECT bool_and(
               jsonb_typeof(c) = 'object'
               AND c ?& ARRAY['field', 'current', 'desired', 'current_known']
               AND (SELECT count(*) FROM jsonb_object_keys(c)) = 4
               AND jsonb_typeof(c -> 'field') = 'string'
               AND (c ->> 'field') = ANY (public.jinji_sync_field_keys())
               AND NOT public.jinji_ingest_forbidden_key(c ->> 'field')
               AND jsonb_typeof(c -> 'current') IN ('string', 'null')
               AND jsonb_typeof(c -> 'desired') IN ('string', 'null')
               AND jsonb_typeof(c -> 'current_known') = 'boolean')
           FROM jsonb_array_elements(changes) c), true)
$$;
COMMENT ON FUNCTION jinji_sync_changes_allowed(jsonb) IS '同期計画の項目の changes が「許可リストの項目 → 現在値・あるべき値（文字列か null）」だけか';

-- 計画の要約: {種別: 0 以上の整数}
CREATE FUNCTION jinji_sync_summary_allowed(summary jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT jsonb_typeof(summary) = 'object'
       AND coalesce((
           SELECT bool_and(jsonb_typeof(e.value) = 'number' AND (e.value)::numeric >= 0 AND (e.value)::numeric = trunc((e.value)::numeric))
           FROM jsonb_each(summary) e), true)
$$;
COMMENT ON FUNCTION jinji_sync_summary_allowed(jsonb) IS 'sync_plan.summary が「種別 → 件数（0 以上の整数）」だけか';

-- ---------------------------------------------------------------------------
-- 2. 同期計画
-- ---------------------------------------------------------------------------

CREATE TABLE sync_plan (
    id                      uuid        PRIMARY KEY,
    target_system           text        NOT NULL,
    status                  text        NOT NULL,
    settings                jsonb       NOT NULL,
    settings_sha256         text        NOT NULL,
    source_file_name        text        NOT NULL,
    source_sha256           text        NOT NULL,
    directory_user_count    integer     NOT NULL,
    jinji_fingerprint       text        NOT NULL,
    business_date           date        NOT NULL,
    summary                 jsonb       NOT NULL DEFAULT '{}',
    proposed_by_person_id   uuid        NOT NULL REFERENCES person (id),
    proposed_via_actor      text        NOT NULL,
    proposed_via_client_id  text,
    proposed_at             timestamptz NOT NULL DEFAULT now(),
    expires_at              timestamptz NOT NULL,
    decided_by_person_id    uuid        REFERENCES person (id),
    decided_at              timestamptz,
    closed_at               timestamptz,
    applied_at              timestamptz,
    -- 同期先（今は Entra ID だけ。Google Workspace などは後で足す）
    CONSTRAINT sync_plan_target_system_check CHECK (target_system IN ('entra_id')),
    -- 起案中 / 承認済み / 却下 / 置き換え済み（新しい計画）/ 古い（jin-ji 側の変化・期限切れ）/ 実行済み（実行結果を取り込んだ）
    CONSTRAINT sync_plan_status_check CHECK (status IN ('proposed', 'approved', 'rejected', 'superseded', 'stale', 'applied')),
    CONSTRAINT sync_plan_settings_check CHECK (jsonb_typeof(settings) = 'object'),
    CONSTRAINT sync_plan_settings_sha256_format CHECK (settings_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_plan_source_file_name_not_blank CHECK (btrim(source_file_name) <> ''),
    CONSTRAINT sync_plan_source_sha256_format CHECK (source_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_plan_jinji_fingerprint_format CHECK (jinji_fingerprint ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_plan_directory_user_count_check CHECK (directory_user_count >= 0),
    -- 要約は {種別: 件数} だけ
    CONSTRAINT sync_plan_summary_check CHECK (public.jinji_sync_summary_allowed(summary)),
    CONSTRAINT sync_plan_proposed_via_actor_check CHECK (proposed_via_actor IN ('user', 'agent', 'system')),
    CONSTRAINT sync_plan_expires_after_proposed CHECK (expires_at > proposed_at),
    -- 承認・却下・実行済みは決定者を持つ。決定者と日時は対
    CONSTRAINT sync_plan_decision_check CHECK (
        (status NOT IN ('approved', 'rejected', 'applied') OR decided_by_person_id IS NOT NULL)
        AND (decided_by_person_id IS NULL) = (decided_at IS NULL)
        AND (status = 'applied') = (applied_at IS NOT NULL)
        AND (status IN ('superseded', 'stale')) = (closed_at IS NOT NULL)),
    -- 職務分掌: 起案者は承認・却下できない（§5.5）
    CONSTRAINT sync_plan_segregation_of_duties CHECK (decided_by_person_id IS DISTINCT FROM proposed_by_person_id)
);
COMMENT ON TABLE sync_plan IS 'Entra ID への同期計画（作業領域）。jin-ji の公開データと Entra のユーザー一覧の差分。起案者とは別の hr_admin が承認したものだけをスクリプト出力・直接反映する';
COMMENT ON COLUMN sync_plan.settings IS '計画に使った同期の設定（表示名の形式・部署名の段・対象の法人・除外するユーザーなど）の全文。計画の承認がこの設定の承認を兼ねる';
COMMENT ON COLUMN sync_plan.jinji_fingerprint IS 'jin-ji 側のあるべき値（対象の人・外部 ID の対応・設定）の SHA-256。承認・出力の時点で計算し直して比べ、違えば古い計画にする';
CREATE INDEX sync_plan_proposed_at_idx ON sync_plan (proposed_at DESC);

CREATE FUNCTION jinji_guard_sync_plan() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    workflow CONSTANT text[] := ARRAY['status', 'decided_by_person_id', 'decided_at', 'closed_at', 'applied_at'];
BEGIN
    IF (to_jsonb(NEW) - workflow) IS DISTINCT FROM (to_jsonb(OLD) - workflow) THEN
        RAISE EXCEPTION 'sync_plan: 計画の中身は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'sync_plan_immutable';
    END IF;

    -- 決定者は起案中からの承認・却下のときだけ記録でき、その後は変えられない
    IF OLD.decided_by_person_id IS NOT NULL AND NEW.decided_by_person_id IS DISTINCT FROM OLD.decided_by_person_id THEN
        RAISE EXCEPTION 'sync_plan: 決定者は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'sync_plan_immutable';
    END IF;

    IF OLD.decided_by_person_id IS NULL AND NEW.decided_by_person_id IS NOT NULL AND NOT (OLD.status = 'proposed' AND NEW.status IN ('approved', 'rejected')) THEN
        RAISE EXCEPTION 'sync_plan: 決定者は承認・却下のときだけ記録できます（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'sync_plan_transition';
    END IF;

    IF (OLD.status = 'proposed' AND NEW.status IN ('approved', 'rejected', 'superseded', 'stale'))
       OR (OLD.status = 'approved' AND NEW.status IN ('superseded', 'stale', 'applied')) THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'sync_plan: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'sync_plan_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_sync_plan() IS 'sync_plan の状態遷移（起案 → 承認・却下・置き換え・古い、承認済み → 置き換え・古い・実行済み）と中身の不変を守るトリガー関数';

CREATE TRIGGER sync_plan_guard BEFORE UPDATE ON sync_plan
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_sync_plan();

CREATE TABLE sync_plan_item (
    id                   uuid    PRIMARY KEY,
    sync_plan_id         uuid    NOT NULL REFERENCES sync_plan (id),
    sequence             integer NOT NULL,
    priority             text    NOT NULL,
    action               text    NOT NULL,
    person_id            uuid    REFERENCES person (id),
    employee_number      text,
    directory_object_id  text,
    user_principal_name  text,
    in_script            boolean NOT NULL,
    changes              jsonb   NOT NULL DEFAULT '[]',
    basis                jsonb   NOT NULL DEFAULT '{}',
    note                 text,
    -- IT 全般統制（退職者の無効化・一時停止者のサインイン停止）/ 要対応 / 通常 / 参考
    CONSTRAINT sync_plan_item_priority_check CHECK (priority IN ('critical', 'high', 'normal', 'info')),
    CONSTRAINT sync_plan_item_action_check CHECK (action IN (
        'disable_account', 'block_sign_in', 'enable_account', 'update_attributes', 'set_manager', 'create_user',
        'unmatched_account', 'excluded_account')),
    CONSTRAINT sync_plan_item_sequence_check CHECK (sequence >= 1),
    CONSTRAINT sync_plan_item_changes_allowed CHECK (public.jinji_sync_changes_allowed(changes)),
    CONSTRAINT sync_plan_item_basis_check CHECK (jsonb_typeof(basis) = 'object'),
    -- 参考の項目（対応付けの無いアカウント・除外したアカウント）はスクリプトに含めない
    CONSTRAINT sync_plan_item_in_script_check CHECK (NOT in_script OR action NOT IN ('unmatched_account', 'excluded_account')),
    -- 既存のアカウントへの操作は Entra のオブジェクト ID（UUID）を持つ。作成はまだ持たない
    CONSTRAINT sync_plan_item_object_id_check CHECK (
        (action = 'create_user') = (directory_object_id IS NULL)
        AND (directory_object_id IS NULL OR directory_object_id ~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')),
    CONSTRAINT sync_plan_item_plan_sequence_key UNIQUE (sync_plan_id, sequence),
    -- 実行結果を項目に縛る複合外部キーの参照先
    CONSTRAINT sync_plan_item_id_plan_key UNIQUE (id, sync_plan_id)
);
COMMENT ON TABLE sync_plan_item IS '同期計画の項目（作業領域）。変える項目の Entra の現在値（スクリプトの事前条件）・jin-ji のあるべき値・根拠。追記だけ';
COMMENT ON COLUMN sync_plan_item.changes IS '[{field, current, desired, current_known}]。current は計画を作ったときの Entra の値で、スクリプト・直接反映はこれと今の値が違えばスキップする';
COMMENT ON COLUMN sync_plan_item.basis IS '根拠（jin-ji のどのデータか: 人・属性の版・所属・組織・法人・一時停止・外部 ID の対応・設定）';

-- ---------------------------------------------------------------------------
-- 3. 実行結果（スクリプトの実行ログ・直接反映）
-- ---------------------------------------------------------------------------

CREATE TABLE sync_run (
    id                     uuid        PRIMARY KEY,
    sync_plan_id           uuid        NOT NULL REFERENCES sync_plan (id),
    mode                   text        NOT NULL,
    what_if                boolean     NOT NULL,
    log_sha256             text,
    data_sha256            text        NOT NULL,
    started_at             timestamptz,
    finished_at            timestamptz,
    recorded_by_person_id  uuid        REFERENCES person (id),
    recorded_via_actor     text        NOT NULL,
    recorded_at            timestamptz NOT NULL DEFAULT now(),
    entry_count            integer     NOT NULL,
    unknown_entry_count    integer     NOT NULL,
    -- 生成したスクリプト（管理者が実行し、ログを取り込む）/ jin-ji が Graph に直接反映した
    CONSTRAINT sync_run_mode_check CHECK (mode IN ('script', 'direct')),
    CONSTRAINT sync_run_log_sha256_format CHECK (log_sha256 IS NULL OR log_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_run_data_sha256_format CHECK (data_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT sync_run_script_has_log CHECK ((mode = 'script') = (log_sha256 IS NOT NULL)),
    CONSTRAINT sync_run_recorded_via_actor_check CHECK (recorded_via_actor IN ('user', 'agent', 'system')),
    CONSTRAINT sync_run_counts_check CHECK (entry_count >= 0 AND unknown_entry_count >= 0)
);
COMMENT ON TABLE sync_run IS '同期計画の実行の記録（スクリプトの実行ログの取込、または直接反映）。追記だけ';

CREATE TABLE sync_item_result (
    id                 uuid        PRIMARY KEY,
    sync_run_id        uuid        NOT NULL REFERENCES sync_run (id),
    sync_plan_id       uuid        NOT NULL,
    sync_plan_item_id  uuid        NOT NULL,
    outcome            text        NOT NULL,
    observed           jsonb       NOT NULL DEFAULT '{}',
    message            text,
    executed_at        timestamptz,
    -- 項目は実行の記録と同じ計画のもの
    CONSTRAINT sync_item_result_item_fkey FOREIGN KEY (sync_plan_item_id, sync_plan_id) REFERENCES sync_plan_item (id, sync_plan_id),
    -- 反映した / 既に反映済み（あるべき値と同じ）/ 事前条件の不一致でスキップ（Entra 側が計画の後に変わった）/ 失敗 / ドライラン（反映する予定）
    CONSTRAINT sync_item_result_outcome_check CHECK (outcome IN ('succeeded', 'already_applied', 'skipped_precondition', 'failed', 'would_apply')),
    CONSTRAINT sync_item_result_observed_allowed CHECK (public.jinji_jsonb_keys_allowed(observed, public.jinji_sync_field_keys())),
    CONSTRAINT sync_item_result_message_length CHECK (message IS NULL OR char_length(message) <= 1000),
    CONSTRAINT sync_item_result_run_item_key UNIQUE (sync_run_id, sync_plan_item_id)
);
COMMENT ON TABLE sync_item_result IS '同期計画の項目ごとの実行結果（成功・既に反映済み・スキップ・失敗・ドライラン）。observed は事前条件を確かめたときの Entra の値。追記だけ';
CREATE INDEX sync_item_result_plan_idx ON sync_item_result (sync_plan_id);

-- 実行の記録の計画と、結果の計画が一致すること（結果を別の計画の実行に付けさせない）
CREATE FUNCTION jinji_guard_sync_item_result() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM public.sync_run r WHERE r.id = NEW.sync_run_id AND r.sync_plan_id = NEW.sync_plan_id) THEN
        RAISE EXCEPTION 'sync_item_result: 実行の記録と計画が一致しません（id=%）', NEW.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'sync_item_result_run_plan';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_sync_item_result() IS 'sync_item_result の計画が実行の記録（sync_run）の計画と一致することを守るトリガー関数';

CREATE TRIGGER sync_item_result_guard BEFORE INSERT ON sync_item_result
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_sync_item_result();

-- ---------------------------------------------------------------------------
-- 4. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

ALTER TABLE sync_plan ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_plan FORCE ROW LEVEL SECURITY;
ALTER TABLE sync_plan_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_plan_item FORCE ROW LEVEL SECURITY;
ALTER TABLE sync_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_run FORCE ROW LEVEL SECURITY;
ALTER TABLE sync_item_result ENABLE ROW LEVEL SECURITY;
ALTER TABLE sync_item_result FORCE ROW LEVEL SECURITY;

-- 計画: 参照・起案は hr_admin だけ。起案は起案中の状態で、決定者なし。
-- 承認・却下は hr_admin だけで、決定者は閲覧者本人としてしか記録できない（人に紐付かない system の閲覧者は承認できない）
CREATE POLICY sync_plan_select ON sync_plan FOR SELECT USING ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY sync_plan_insert ON sync_plan FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin()) AND status = 'proposed' AND decided_by_person_id IS NULL);
CREATE POLICY sync_plan_update ON sync_plan FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin()))
    WITH CHECK (
        (SELECT jinji_viewer_is_hr_admin())
        AND (status NOT IN ('approved', 'rejected') OR decided_by_person_id = (SELECT jinji_viewer_person_id())));

CREATE POLICY sync_plan_item_select ON sync_plan_item FOR SELECT USING ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY sync_plan_item_insert ON sync_plan_item FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- 実行の記録: 記録者は閲覧者本人（system の閲覧者は人なし）
CREATE POLICY sync_run_select ON sync_run FOR SELECT USING ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY sync_run_insert ON sync_run FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin()) AND recorded_by_person_id IS NOT DISTINCT FROM (SELECT jinji_viewer_person_id()));

CREATE POLICY sync_item_result_select ON sync_item_result FOR SELECT USING ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY sync_item_result_insert ON sync_item_result FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- ---------------------------------------------------------------------------
-- 5. アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT ON sync_plan, sync_plan_item, sync_run, sync_item_result TO jinji_app;
GRANT UPDATE (status, decided_by_person_id, decided_at, closed_at, applied_at) ON sync_plan TO jinji_app;
