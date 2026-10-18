-- 0008: 要確認の提案の解決（人の操作）・LLM による候補提示の記録・作業領域の氏名の消去
--
-- 内容
-- 1. 要確認（needs_review）の提案を人が解決できるようにする（§7.1 の HITL）。解決は「候補から選ぶ」「新規として登録する」「修正依頼に回す」の 3 種
--    （却下は既存の rejected）。解決した提案は resolved になり、解決の結果は通常の提案（proposed。resolved_from_proposal_id で元を指す）として
--    同じ取込単位に追加し、既存の承認フローに乗せる
--    - 解決できるのは hr_admin の人だけで、取込の実行者（= 起案者）は解決できない（resolved_by ≠ proposed_by。CHECK 制約）
--    - 解決者が内容を決めた提案を、解決者自身が承認することもできない（decided_by ≠ resolved_by。CHECK 制約）。
--      取込の実行者・解決者・承認者の 3 者が揃わないと、要確認だった変更は公開側に出ない
--    - 解決の結果の提案は、resolved 状態の元の提案から、同じ人（元の提案の decided_by）が、同じ取込単位に作ったものだけ（トリガー）
-- 2. 修正依頼の種別に「要確認から回した」（referred_from_review）を足す
-- 3. 作業領域（staging_row / normalized_row）から項目を消せるようにする（氏名の消去。ADR-0002「戸籍上の氏名は保持しない」）
--    - 名寄せで人に対応付けられなかった行の氏名は、戸籍名（旧姓使用者の戸籍姓）である可能性がある。提案の解決・却下の後は不要なので消す
--    - 更新できるのは data だけ、hr_admin だけ、キーを減らす方向だけ（残すキーの値は変えられない。トリガー）
-- 4. 取込項目の許可リストに parent_org_alt_name（列が階層の組織図で、結合セルの補いで決めた上位組織の代わりの候補 = 段飛ばしの場合の上位組織）を足す
-- 5. LLM への送信の記録（llm_request_log）。送信先・モデル・送った項目名と件数・呼び出し回数・トークン数・料金の目安だけで、値は持たない
--    （§8「送信内容を監査ログに残す」。audit_event には詳細の列が無いので、取込作業領域の表として持ち、audit_event にも ingest.llm.send を残す）

-- ---------------------------------------------------------------------------
-- 1. 変更提案の解決
-- ---------------------------------------------------------------------------

ALTER TABLE change_proposal ADD COLUMN resolved_from_proposal_id uuid REFERENCES change_proposal (id);
ALTER TABLE change_proposal ADD COLUMN resolved_by_person_id uuid REFERENCES person (id);
ALTER TABLE change_proposal ADD COLUMN resolution text;

ALTER TABLE change_proposal DROP CONSTRAINT change_proposal_status_check;
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_status_check CHECK (status IN ('proposed', 'needs_review', 'applied', 'rejected', 'resolved'));
ALTER TABLE change_proposal DROP CONSTRAINT change_proposal_review_status_check;
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_review_status_check CHECK (
    operation NOT IN ('review', 'supplement') OR status IN ('needs_review', 'rejected', 'resolved'));
-- 解決の種別: 候補から選んだ / 新規として登録した / 修正依頼に回した（resolved のときだけ持つ）
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_resolution_check CHECK (
    (status = 'resolved') = (resolution IS NOT NULL)
    AND (resolution IS NULL OR resolution IN ('selected_candidate', 'registered_new', 'referred_to_correction')));
-- 解決の結果の提案: 元の提案と解決者は対で持つ。解決者 ≠ 起案者（取込の実行者）、承認者 ≠ 解決者（職務分掌 §5.5）
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_resolved_pair_check CHECK (
    (resolved_from_proposal_id IS NULL) = (resolved_by_person_id IS NULL));
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_resolver_not_proposer CHECK (
    resolved_by_person_id IS DISTINCT FROM proposed_by_person_id);
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_approver_not_resolver CHECK (
    resolved_by_person_id IS NULL OR decided_by_person_id IS DISTINCT FROM resolved_by_person_id);
CREATE INDEX change_proposal_resolved_from_idx ON change_proposal (resolved_from_proposal_id) WHERE resolved_from_proposal_id IS NOT NULL;

COMMENT ON COLUMN change_proposal.resolved_from_proposal_id IS '解決の結果の提案なら、元の要確認の提案';
COMMENT ON COLUMN change_proposal.resolved_by_person_id IS '解決の結果の提案なら、解決した人（hr_admin。取込の実行者・承認者とは別の人）';
COMMENT ON COLUMN change_proposal.resolution IS '要確認の提案の解決の種別（selected_candidate / registered_new / referred_to_correction）';

-- 状態の遷移（起案 → 反映・却下、要確認 → 却下・解決）と中身の不変
CREATE OR REPLACE FUNCTION jinji_guard_change_proposal() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    workflow CONSTANT text[] := ARRAY['status', 'decided_by_person_id', 'decided_at', 'applied_at', 'resolution'];
BEGIN
    IF (to_jsonb(NEW) - workflow) IS DISTINCT FROM (to_jsonb(OLD) - workflow) THEN
        RAISE EXCEPTION 'change_proposal: 提案の中身は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'change_proposal_immutable';
    END IF;

    IF (OLD.status = 'proposed' AND NEW.status IN ('applied', 'rejected'))
       OR (OLD.status = 'needs_review' AND NEW.status IN ('rejected', 'resolved')) THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'change_proposal: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'change_proposal_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_change_proposal() IS 'change_proposal の状態遷移（起案 → 反映・却下、要確認 → 却下・解決）と中身の不変を守るトリガー関数';

-- 解決の結果の提案は、resolved の元の提案から、その解決者が、同じ取込単位に、起案中（proposed）で作ったものだけ
CREATE FUNCTION jinji_guard_resolved_proposal() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    source record;
BEGIN
    IF NEW.resolved_from_proposal_id IS NULL THEN
        RETURN NEW;
    END IF;

    SELECT import_batch_id, status, decided_by_person_id INTO source
    FROM public.change_proposal WHERE id = NEW.resolved_from_proposal_id;

    IF NOT FOUND
       OR source.status <> 'resolved'
       OR source.import_batch_id <> NEW.import_batch_id
       OR source.decided_by_person_id IS DISTINCT FROM NEW.resolved_by_person_id
       OR NEW.status <> 'proposed' THEN
        RAISE EXCEPTION 'change_proposal: 解決の結果の提案は、解決済みの要確認の提案から、その解決者が同じ取込単位に起案中で作ったものだけです（元=%）', NEW.resolved_from_proposal_id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'change_proposal_resolved_source';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_resolved_proposal() IS '解決の結果の提案（resolved_from_proposal_id あり）が、解決済みの元の提案と解決者・取込単位で対応していることを守るトリガー関数';

CREATE TRIGGER change_proposal_resolved_guard BEFORE INSERT ON change_proposal
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_resolved_proposal();

-- 起案（追加）: 解決の結果の提案は hr_admin が、解決者 = 閲覧者本人としてだけ追加できる
DROP POLICY change_proposal_insert ON change_proposal;
CREATE POLICY change_proposal_insert ON change_proposal FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id))
    AND status IN ('proposed', 'needs_review')
    AND (resolved_by_person_id IS NULL
         OR ((SELECT jinji_viewer_is_hr_admin()) AND resolved_by_person_id = (SELECT jinji_viewer_person_id()))));

GRANT UPDATE (resolution) ON change_proposal TO jinji_app;

-- ---------------------------------------------------------------------------
-- 2. 修正依頼の種別
-- ---------------------------------------------------------------------------

ALTER TABLE correction_request DROP CONSTRAINT correction_request_issue_check;
ALTER TABLE correction_request ADD CONSTRAINT correction_request_issue_check CHECK (issue IN (
    'missing_employee_number', 'missing_required', 'invalid_date', 'invalid_value', 'duplicate_row',
    'unknown_legal_entity', 'unknown_org', 'unknown_org_level', 'unknown_head', 'conflicting_identity',
    'suspected_sensitive_value', 'abolished_org', 'stale_head', 'uninterpretable_action', 'suspected_employee_number_typo',
    'referred_from_review'));

-- ---------------------------------------------------------------------------
-- 3. 作業領域の項目の消去（氏名）
-- ---------------------------------------------------------------------------

-- 更新は data だけ、キーを減らす方向だけ（残るキーの値は変えない）。ほかの列は変えられない
CREATE FUNCTION jinji_guard_workspace_scrub() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT (OLD.data @> NEW.data)
       OR (to_jsonb(NEW) - 'data') IS DISTINCT FROM (to_jsonb(OLD) - 'data') THEN
        RAISE EXCEPTION '%: 作業領域の行は項目を消すことしかできません（id=%）', TG_TABLE_NAME, to_jsonb(OLD) ->> (CASE WHEN TG_TABLE_NAME = 'staging_row' THEN 'id' ELSE 'staging_row_id' END)
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = TG_TABLE_NAME || '_scrub_only';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_workspace_scrub() IS 'staging_row / normalized_row の更新を「data のキーを減らすだけ」に限るトリガー関数（氏名の消去。ADR-0002）';

CREATE TRIGGER staging_row_scrub_guard BEFORE UPDATE ON staging_row
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_workspace_scrub();
CREATE TRIGGER normalized_row_scrub_guard BEFORE UPDATE ON normalized_row
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_workspace_scrub();

CREATE POLICY staging_row_update ON staging_row FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY normalized_row_update ON normalized_row FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

GRANT UPDATE (data) ON staging_row, normalized_row TO jinji_app;

-- ---------------------------------------------------------------------------
-- 4. 取込項目の許可リスト
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
        -- 0008: 結合セルの補いで決めた上位組織の代わりの候補（段飛ばしの場合の上位組織）
        'parent_org_alt_name',
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

-- ---------------------------------------------------------------------------
-- 5. LLM への送信の記録
-- ---------------------------------------------------------------------------

-- LLM に送ってよい項目（送信内容の要約のキー）。JinJi.Ingest の LlmFields.All と同じ集合（テストで突き合わせる）
CREATE FUNCTION jinji_llm_field_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        -- 取込元の行
        'row.name', 'row.employee_number', 'row.action_text', 'row.org_name', 'row.position_title',
        -- 行の人（名寄せで決まっている場合）の現在の所属
        'subject.orgs', 'subject.title',
        -- 候補の人（業務上の氏名・社員番号・所属・役職）
        'person.name', 'person.employee_number', 'person.orgs', 'person.title',
        -- 候補の組織（組織名・上位組織名・段・組織長の有無・配下の組織名）
        'org.name', 'org.parent_name', 'org.level', 'org.has_head', 'org.children',
        -- 判定の対象の組織（段飛ばしの組織）
        'target.org_name', 'target.org_level'
    ]::text[]
$$;
COMMENT ON FUNCTION jinji_llm_field_keys() IS 'LLM に送ってよい項目（llm_request_log.sent_fields のキー）。JinJi.Ingest の LlmFields.All と一致させる';

-- 送った項目の要約が {許可リストの項目名: 0 以上の整数} だけか（値は入れられない）
CREATE FUNCTION jinji_llm_sent_fields_allowed(sent jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT jsonb_typeof(sent) = 'object'
       AND coalesce((
           SELECT bool_and(e.key = ANY (public.jinji_llm_field_keys()) AND jsonb_typeof(e.value) = 'number' AND (e.value)::numeric >= 0
                           AND (e.value)::numeric = trunc((e.value)::numeric))
           FROM jsonb_each(sent) e), true)
$$;
COMMENT ON FUNCTION jinji_llm_sent_fields_allowed(jsonb) IS 'llm_request_log.sent_fields が「LLM に送ってよい項目名 → 件数」だけか';

CREATE TABLE llm_request_log (
    id                      uuid          PRIMARY KEY,
    import_batch_id         uuid          NOT NULL REFERENCES import_batch (id),
    endpoint_host           text          NOT NULL,
    model                   text          NOT NULL,
    sent_fields             jsonb         NOT NULL,
    question_count          integer       NOT NULL,
    call_count              integer       NOT NULL,
    discarded_id_count      integer       NOT NULL,
    schema_violation_count  integer       NOT NULL,
    failed_count            integer       NOT NULL,
    skipped_count           integer       NOT NULL,
    input_tokens            integer       NOT NULL,
    output_tokens           integer       NOT NULL,
    estimated_cost_usd      numeric(12,6),
    created_at              timestamptz   NOT NULL DEFAULT now(),
    CONSTRAINT llm_request_log_endpoint_host_not_blank CHECK (btrim(endpoint_host) <> ''),
    CONSTRAINT llm_request_log_model_not_blank CHECK (btrim(model) <> ''),
    -- 送った項目は {項目名: 件数} だけ（値は持たない）。項目名は許可リスト、件数は 0 以上の整数
    CONSTRAINT llm_request_log_sent_fields_check CHECK (public.jinji_llm_sent_fields_allowed(sent_fields)),
    CONSTRAINT llm_request_log_counts_check CHECK (
        question_count >= 0 AND call_count >= 0 AND discarded_id_count >= 0 AND schema_violation_count >= 0
        AND failed_count >= 0 AND skipped_count >= 0 AND input_tokens >= 0 AND output_tokens >= 0),
    CONSTRAINT llm_request_log_cost_check CHECK (estimated_cost_usd IS NULL OR estimated_cost_usd >= 0)
);
COMMENT ON TABLE llm_request_log IS 'LLM への送信の記録（取込単位ごと。作業領域）。送信先・モデル・送った項目名と件数・呼び出し回数・トークン数・料金の目安だけで、送った値は持たない';
CREATE INDEX llm_request_log_batch_idx ON llm_request_log (import_batch_id);

ALTER TABLE llm_request_log ENABLE ROW LEVEL SECURITY;
ALTER TABLE llm_request_log FORCE ROW LEVEL SECURITY;
CREATE POLICY llm_request_log_select ON llm_request_log FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));
CREATE POLICY llm_request_log_insert ON llm_request_log FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));

GRANT SELECT, INSERT ON llm_request_log TO jinji_app;
