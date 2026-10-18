-- 0005: 取り込みの作業領域（取込単位・staging・正規化結果・名寄せ候補・変更提案・修正依頼リスト）
--
-- 位置づけ（アーキテクチャ概要 §7.1・§8）
-- - ここに作るテーブルは「公開されない作業領域」。内容は承認の関門（change_proposal の承認）を通らない限り公開側
--   （org_unit_version・person_version・assignment など）へ出ない。エージェントが作業領域で誤っても、公開される情報は汚れない
-- - そのため、取込の実行主体（人、またはその人から委任された AI エージェント）は、自分のバッチの作業領域に承認なしで書き込んでよい
--   （RLS の INSERT は hr_admin か、バッチの実行者本人）。公開側への反映は、変更提案を別人が承認したときだけ（§5.5 職務分掌）
-- - 閲覧は hr_admin と、そのバッチの実行者本人だけ（取込元の生データ・名寄せの提案は管理系の情報。§5 の表）
--
-- 保持しない項目を残さない（ADR-0002「生データ（staging）にも許可リスト外の項目は残さない」）
-- - コネクタは、項目マッピングにない列・保持禁止の列を staging に保存する前に捨てる。取込結果には捨てた列名と件数だけを残し、値は残さない
-- - staging_row.data / normalized_row.data の jsonb のキーは、取込項目の許可リスト（jinji_ingest_field_keys()）にあるものだけ。
--   値は文字列か null だけ（入れ子にしてキーを隠せないようにする）。さらに保持禁止の項目を表す名前は、許可リストに誤って足されても拒否する
--   （jinji_ingest_forbidden_key()）。CHECK 制約なので所有者にも効く
-- - change_proposal.payload / diff のキーも、反映先の列の許可リスト（jinji_proposal_field_keys()）で同様に検査する
-- - 元の行のハッシュ（staging_row.row_sha256）は、保持する列の値だけから作る。捨てた列の値を含めると、生年月日のように
--   取り得る値の少ない項目は、ハッシュから総当たりで復元できてしまうため
--
-- 保持期限（アーキテクチャ概要 §4.5）
-- - staging_row.expires_at を過ぎた行は削除してよい。アプリ用ロールに staging_row の DELETE だけを与え、RLS で
--   「hr_admin が、保持期限を過ぎた行だけ」に絞る。正規化結果・名寄せ候補は外部キーの ON DELETE CASCADE で一緒に消える
--   （参照整合性の連鎖は RLS を受けない）。変更提案・修正依頼は根拠として行番号を持ち、staging の行を外部キーで縛らない
--
-- 職務分掌（§5.5）
-- - 変更提案の起案者は取込の実行者（複合外部キーで import_batch.executed_by_person_id と一致させる）
-- - 承認者 ≠ 起案者（= 取込の実行者）は CHECK 制約。承認者は閲覧者本人としてしか記録できない（RLS）
-- - 状態の遷移（起案 → 反映・却下、要確認 → 却下）と中身の不変はトリガーで守る

-- ---------------------------------------------------------------------------
-- 1. 許可リスト
-- ---------------------------------------------------------------------------

-- 取込項目（staging / 正規化結果の jsonb のキー）。JinJi.Ingest の IngestFields.All と同じ集合（テストで突き合わせる）
CREATE FUNCTION jinji_ingest_field_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        -- 人
        'employee_number', 'legal_entity_code',
        'full_name', 'family_name', 'given_name',
        'full_name_kana', 'family_name_kana', 'given_name_kana',
        'family_name_latin', 'given_name_latin', 'display_name',
        'work_email', 'extension_number', 'work_mobile_phone', 'work_location',
        'employment_type', 'job_type', 'hire_date', 'termination_date',
        -- 所属（人の取込）・組織（組織の取込）
        'org_code', 'org_name', 'position_title', 'assignment_type',
        'org_short_name', 'org_level', 'parent_org_code', 'parent_org_name', 'org_head_employee_number', 'org_valid_from',
        -- 正規化で作る検索用の表記
        'search_name', 'search_name_kana', 'org_search_name', 'parent_org_search_name'
    ]::text[]
$$;
COMMENT ON FUNCTION jinji_ingest_field_keys() IS '取込項目の許可リスト（staging_row.data / normalized_row.data に入れてよいキー）。JinJi.Ingest の IngestFields.All と一致させる';

-- 変更提案の payload / diff のキー（反映先の列名）。JinJi.Application の ProposalFields.All と同じ集合
CREATE FUNCTION jinji_proposal_field_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'person_id', 'legal_entity_id', 'employee_number',
        'family_name', 'given_name', 'family_name_kana', 'given_name_kana', 'family_name_latin', 'given_name_latin',
        'display_name', 'search_name', 'search_name_kana',
        'work_email', 'extension_number', 'work_mobile_phone', 'work_location',
        'employment_type', 'job_type', 'hire_date', 'termination_date',
        'org_unit_id', 'assignment_type', 'position_title', 'secondment_type',
        'code', 'name', 'short_name', 'level_key', 'parent_org_unit_id', 'head_person_id', 'valid_from'
    ]::text[]
$$;
COMMENT ON FUNCTION jinji_proposal_field_keys() IS '変更提案の payload / diff に入れてよいキー（反映先の列名）。JinJi.Application の ProposalFields.All と一致させる';

-- 保持しない項目（ADR-0002）を表す名前か。許可リストへの誤った追加に対する二重の守り。
-- 下線で区切った語（age, sex など他の語の一部になりやすいもの）と、部分文字列（birth, address など）で判定する
CREATE FUNCTION jinji_ingest_forbidden_key(key text) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT lower(key) ~ '(birth|gender|address|salary|payroll|grade|bonus|allowance|mynumber|my_number|individual_number|nationalit|citizenship|passport|spouse|dependent|marital|insurance|legal_name|registered_name|maiden|religion|diagnos|disabilit|evaluation|appraisal)'
        OR string_to_array(lower(key), '_') && ARRAY['age', 'dob', 'sex', 'home', 'private', 'personal', 'zip', 'postal', 'pay', 'wage', 'tax', 'bank',
                                                     'pension', 'rating', 'performance', 'health', 'medical', 'illness', 'reason', 'education', 'school', 'resume', 'koseki']
$$;
COMMENT ON FUNCTION jinji_ingest_forbidden_key(text) IS '保持しない項目（ADR-0002）を表すキー名か（許可リストに誤って足された場合の二重の守り）';

-- jsonb が「許可リストのキー → 文字列か null」だけのオブジェクトか
CREATE FUNCTION jinji_jsonb_keys_allowed(data jsonb, allowed text[]) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT jsonb_typeof(data) = 'object'
       AND coalesce((
           SELECT bool_and(e.key = ANY (allowed) AND NOT public.jinji_ingest_forbidden_key(e.key) AND jsonb_typeof(e.value) IN ('string', 'null'))
           FROM jsonb_each(data) e), true)
$$;
COMMENT ON FUNCTION jinji_jsonb_keys_allowed(jsonb, text[]) IS 'jsonb が「許可リストのキー（保持禁止の名前を除く）→ 文字列か null」だけのオブジェクトか';

-- 変更提案の diff: { キー: { "old": 文字列|null, "new": 文字列|null } }
CREATE FUNCTION jinji_proposal_diff_allowed(diff jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT jsonb_typeof(diff) = 'object'
       AND coalesce((
           SELECT bool_and(
               e.key = ANY (public.jinji_proposal_field_keys())
               AND NOT public.jinji_ingest_forbidden_key(e.key)
               AND jsonb_typeof(e.value) = 'object'
               AND public.jinji_jsonb_keys_allowed(e.value, ARRAY['old', 'new']))
           FROM jsonb_each(diff) e), true)
$$;
COMMENT ON FUNCTION jinji_proposal_diff_allowed(jsonb) IS '変更提案の diff が「許可リストのキー → {old, new}（文字列か null）」だけか';

-- ---------------------------------------------------------------------------
-- 2. 取込単位
-- ---------------------------------------------------------------------------

CREATE TABLE import_batch (
    id                      uuid        PRIMARY KEY,
    source_kind             text        NOT NULL,
    target_kind             text        NOT NULL,
    file_name               text        NOT NULL,
    file_sha256             text        NOT NULL,
    mapping_name            text        NOT NULL,
    mapping_sha256          text        NOT NULL,
    legal_entity_id         uuid        REFERENCES legal_entity (id),
    effective_date          date,
    executed_by_person_id   uuid        NOT NULL REFERENCES person (id),
    executed_via_actor      text        NOT NULL,
    executed_via_client_id  text,
    status                  text        NOT NULL,
    row_count               integer     NOT NULL DEFAULT 0,
    dropped_columns         jsonb       NOT NULL DEFAULT '[]',
    started_at              timestamptz NOT NULL DEFAULT now(),
    finished_at             timestamptz,
    -- CSV / Excel（Entra ID・人事パッケージ API のコネクタは後で足す）
    CONSTRAINT import_batch_source_kind_check CHECK (source_kind IN ('csv', 'xlsx')),
    -- 人（在籍者名簿）/ 組織（組織図）
    CONSTRAINT import_batch_target_kind_check CHECK (target_kind IN ('people', 'org_units')),
    CONSTRAINT import_batch_file_name_not_blank CHECK (btrim(file_name) <> ''),
    CONSTRAINT import_batch_file_sha256_format CHECK (file_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT import_batch_mapping_sha256_format CHECK (mapping_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT import_batch_mapping_name_not_blank CHECK (btrim(mapping_name) <> ''),
    CONSTRAINT import_batch_executed_via_actor_check CHECK (executed_via_actor IN ('user', 'agent', 'system')),
    -- 実行中 / 完了（作業領域に保存済み）/ 失敗
    CONSTRAINT import_batch_status_check CHECK (status IN ('running', 'completed', 'failed')),
    CONSTRAINT import_batch_row_count_check CHECK (row_count >= 0),
    -- 捨てた列は [{"name": 列名, "count": 値のあった件数, "kind": "unmapped" | "ignored" | "forbidden"}] だけ（値は持たない）
    CONSTRAINT import_batch_dropped_columns_check CHECK (jsonb_typeof(dropped_columns) = 'array'),
    -- 変更提案の起案者を実行者に縛る複合外部キーの参照先
    CONSTRAINT import_batch_id_executor_key UNIQUE (id, executed_by_person_id)
);
COMMENT ON TABLE import_batch IS '取込単位（作業領域）。取込元のファイル 1 つ。閲覧は hr_admin と実行者本人だけ。実行者（人。AI エージェント・ワーカーはその委任元・指定された人）は変更提案の起案者になり、承認できない';

-- ---------------------------------------------------------------------------
-- 3. staging（許可リストを通った項目だけ）と正規化結果
-- ---------------------------------------------------------------------------

CREATE TABLE staging_row (
    id                 uuid        PRIMARY KEY,
    import_batch_id    uuid        NOT NULL REFERENCES import_batch (id),
    source_row_number  integer     NOT NULL,
    row_sha256         text        NOT NULL,
    data               jsonb       NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    expires_at         timestamptz NOT NULL,
    CONSTRAINT staging_row_source_row_number_check CHECK (source_row_number >= 1),
    CONSTRAINT staging_row_row_sha256_format CHECK (row_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT staging_row_data_allowed CHECK (public.jinji_jsonb_keys_allowed(data, public.jinji_ingest_field_keys())),
    CONSTRAINT staging_row_expires_after_created CHECK (expires_at > created_at),
    CONSTRAINT staging_row_batch_row_key UNIQUE (import_batch_id, source_row_number)
);
COMMENT ON TABLE staging_row IS '取込元の行（作業領域）。項目マッピングと許可リストを通った項目だけを持つ。保持期限（expires_at）を過ぎたら削除する';
CREATE INDEX staging_row_expires_at_idx ON staging_row (expires_at);

CREATE TABLE normalized_row (
    staging_row_id   uuid  PRIMARY KEY REFERENCES staging_row (id) ON DELETE CASCADE,
    import_batch_id  uuid  NOT NULL REFERENCES import_batch (id),
    data             jsonb NOT NULL,
    issues           jsonb NOT NULL DEFAULT '[]',
    CONSTRAINT normalized_row_data_allowed CHECK (public.jinji_jsonb_keys_allowed(data, public.jinji_ingest_field_keys())),
    -- 問題は [{"field": 取込項目, "code": 種別}] だけ（値は持たない）
    CONSTRAINT normalized_row_issues_check CHECK (jsonb_typeof(issues) = 'array')
);
COMMENT ON TABLE normalized_row IS '正規化結果（作業領域）。表示用の表記と検索用の正規化表記を分けて持つ。エージェントが承認なしで書き込んでよい';

-- ---------------------------------------------------------------------------
-- 4. 名寄せ候補
-- ---------------------------------------------------------------------------

CREATE TABLE match_candidate (
    id                   uuid         PRIMARY KEY,
    import_batch_id      uuid         NOT NULL REFERENCES import_batch (id),
    staging_row_id       uuid         NOT NULL REFERENCES staging_row (id) ON DELETE CASCADE,
    outcome              text         NOT NULL,
    match_rule           text,
    candidate_person_id  uuid         REFERENCES person (id),
    confidence           numeric(4,3) NOT NULL,
    -- 一意に決まった / 複数候補（要確認）/ 候補なし
    CONSTRAINT match_candidate_outcome_check CHECK (outcome IN ('unique', 'ambiguous', 'none')),
    -- 社員番号 / 社用メール / 正規化氏名 + 所属（決定的な突合。LLM による候補は後の手順）
    CONSTRAINT match_candidate_match_rule_check CHECK (match_rule IS NULL OR match_rule IN ('employee_number', 'work_email', 'name_and_org')),
    CONSTRAINT match_candidate_confidence_check CHECK (confidence >= 0 AND confidence <= 1),
    CONSTRAINT match_candidate_none_check CHECK ((outcome = 'none') = (candidate_person_id IS NULL))
);
COMMENT ON TABLE match_candidate IS '名寄せ候補（作業領域）。staging の行ごとの突合の結果と候補の人。エージェントが承認なしで書き込んでよい';
CREATE INDEX match_candidate_staging_row_idx ON match_candidate (staging_row_id);

-- ---------------------------------------------------------------------------
-- 5. 変更提案（公開側への唯一の入口。別人の承認で反映する）
-- ---------------------------------------------------------------------------

CREATE TABLE change_proposal (
    id                      uuid         PRIMARY KEY,
    import_batch_id         uuid         NOT NULL REFERENCES import_batch (id),
    target_kind             text         NOT NULL,
    operation               text         NOT NULL,
    target_id               uuid,
    effective_date          date,
    payload                 jsonb        NOT NULL DEFAULT '{}',
    diff                    jsonb        NOT NULL DEFAULT '{}',
    basis                   jsonb        NOT NULL DEFAULT '{}',
    staging_row_ids         uuid[]       NOT NULL DEFAULT '{}',
    confidence              numeric(4,3) NOT NULL,
    status                  text         NOT NULL,
    proposed_by_person_id   uuid         NOT NULL REFERENCES person (id),
    proposed_via_client_id  text,
    proposed_at             timestamptz  NOT NULL DEFAULT now(),
    decided_by_person_id    uuid         REFERENCES person (id),
    decided_at              timestamptz,
    applied_at              timestamptz,
    -- 人 / 人の版 / 組織 / 組織の版 / 所属
    CONSTRAINT change_proposal_target_kind_check CHECK (target_kind IN ('person', 'person_version', 'org_unit', 'org_unit_version', 'assignment')),
    -- 新規 / 訂正（同じ有効期間の行を置き換える）/ 変更（有効日で分ける）/ 名寄せの要確認 / 補完の提案
    CONSTRAINT change_proposal_operation_check CHECK (operation IN ('create', 'correct', 'change', 'review', 'supplement')),
    CONSTRAINT change_proposal_payload_allowed CHECK (public.jinji_jsonb_keys_allowed(payload, public.jinji_proposal_field_keys())),
    CONSTRAINT change_proposal_diff_allowed CHECK (public.jinji_proposal_diff_allowed(diff)),
    CONSTRAINT change_proposal_basis_check CHECK (jsonb_typeof(basis) = 'object'),
    CONSTRAINT change_proposal_confidence_check CHECK (confidence >= 0 AND confidence <= 1),
    -- 起案 / 要確認（そのままでは承認できない）/ 反映済み（承認と反映は同じトランザクション）/ 却下
    CONSTRAINT change_proposal_status_check CHECK (status IN ('proposed', 'needs_review', 'applied', 'rejected')),
    CONSTRAINT change_proposal_review_status_check CHECK (operation NOT IN ('review', 'supplement') OR status IN ('needs_review', 'rejected')),
    CONSTRAINT change_proposal_decision_check CHECK (
        (status IN ('proposed', 'needs_review')) = (decided_by_person_id IS NULL)
        AND (decided_by_person_id IS NULL) = (decided_at IS NULL)
        AND (status = 'applied') = (applied_at IS NOT NULL)),
    -- 起案者は取込の実行者
    CONSTRAINT change_proposal_proposer_is_executor_fkey
        FOREIGN KEY (import_batch_id, proposed_by_person_id) REFERENCES import_batch (id, executed_by_person_id),
    -- 職務分掌: 取込の実行者（= 起案者）は承認・却下できない（§5.5）
    CONSTRAINT change_proposal_segregation_of_duties CHECK (decided_by_person_id IS DISTINCT FROM proposed_by_person_id)
);
COMMENT ON TABLE change_proposal IS '変更提案。取込結果と現在のデータの差分・根拠（取込元の行）・確信度を持つ。取込の実行者とは別の hr_admin が承認したときだけ公開側に反映する';
CREATE INDEX change_proposal_batch_idx ON change_proposal (import_batch_id);

CREATE FUNCTION jinji_guard_change_proposal() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    workflow CONSTANT text[] := ARRAY['status', 'decided_by_person_id', 'decided_at', 'applied_at'];
BEGIN
    IF (to_jsonb(NEW) - workflow) IS DISTINCT FROM (to_jsonb(OLD) - workflow) THEN
        RAISE EXCEPTION 'change_proposal: 提案の中身は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'change_proposal_immutable';
    END IF;

    IF (OLD.status = 'proposed' AND NEW.status IN ('applied', 'rejected'))
       OR (OLD.status = 'needs_review' AND NEW.status = 'rejected') THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'change_proposal: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'change_proposal_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_change_proposal() IS 'change_proposal の状態遷移（起案 → 反映・却下、要確認 → 却下）と中身の不変を守るトリガー関数';

CREATE TRIGGER change_proposal_guard BEFORE UPDATE ON change_proposal
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_change_proposal();

-- ---------------------------------------------------------------------------
-- 6. 修正依頼リスト（人事パッケージ側で直すべき項目）
-- ---------------------------------------------------------------------------

CREATE TABLE correction_request (
    id                 uuid        PRIMARY KEY,
    import_batch_id    uuid        NOT NULL REFERENCES import_batch (id),
    staging_row_id     uuid        REFERENCES staging_row (id) ON DELETE SET NULL,
    source_row_number  integer,
    field_key          text,
    issue              text        NOT NULL,
    message            text        NOT NULL,
    status             text        NOT NULL DEFAULT 'open',
    created_at         timestamptz NOT NULL DEFAULT now(),
    resolved_at        timestamptz,
    CONSTRAINT correction_request_field_key_allowed CHECK (
        field_key IS NULL OR (field_key = ANY (public.jinji_ingest_field_keys()) AND NOT public.jinji_ingest_forbidden_key(field_key))),
    CONSTRAINT correction_request_issue_check CHECK (issue IN (
        'missing_employee_number', 'missing_required', 'invalid_date', 'invalid_value', 'duplicate_row',
        'unknown_legal_entity', 'unknown_org', 'unknown_org_level', 'unknown_head', 'conflicting_identity')),
    CONSTRAINT correction_request_message_not_blank CHECK (btrim(message) <> ''),
    -- 未対応 / 対応済み / 対応不要
    CONSTRAINT correction_request_status_check CHECK (status IN ('open', 'resolved', 'dismissed')),
    CONSTRAINT correction_request_resolved_check CHECK ((status = 'open') = (resolved_at IS NULL))
);
COMMENT ON TABLE correction_request IS '修正依頼リスト（作業領域）。jin-ji で直さず、正である人事パッケージ側で直すべき項目。メッセージに取込元の値は書かない';
CREATE INDEX correction_request_batch_idx ON correction_request (import_batch_id);

-- ---------------------------------------------------------------------------
-- 7. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

-- 閲覧者がバッチの実行者本人か（import_batch の RLS を受ける。実行者本人には自分のバッチが見える）
CREATE FUNCTION jinji_viewer_is_batch_executor(batch_id uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT EXISTS (
        SELECT 1 FROM public.import_batch b
        WHERE b.id = batch_id AND b.executed_by_person_id = public.jinji_viewer_person_id())
$$;
COMMENT ON FUNCTION jinji_viewer_is_batch_executor(uuid) IS 'RLS: 閲覧者が取込バッチの実行者本人（AI エージェントは委任元）か';

ALTER TABLE import_batch ENABLE ROW LEVEL SECURITY;
ALTER TABLE import_batch FORCE ROW LEVEL SECURITY;
ALTER TABLE staging_row ENABLE ROW LEVEL SECURITY;
ALTER TABLE staging_row FORCE ROW LEVEL SECURITY;
ALTER TABLE normalized_row ENABLE ROW LEVEL SECURITY;
ALTER TABLE normalized_row FORCE ROW LEVEL SECURITY;
ALTER TABLE match_candidate ENABLE ROW LEVEL SECURITY;
ALTER TABLE match_candidate FORCE ROW LEVEL SECURITY;
ALTER TABLE change_proposal ENABLE ROW LEVEL SECURITY;
ALTER TABLE change_proposal FORCE ROW LEVEL SECURITY;
ALTER TABLE correction_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE correction_request FORCE ROW LEVEL SECURITY;

-- 取込単位: 参照・作成は hr_admin か実行者本人（実行者本人は hr_admin でなくても、自分のバッチを作業領域に作れる）
CREATE POLICY import_batch_select ON import_batch FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND executed_by_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY import_batch_insert ON import_batch FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND executed_by_person_id = (SELECT jinji_viewer_person_id())));

-- 作業領域の行: 参照・追加は hr_admin か、そのバッチの実行者本人（エージェントの自動の書き込みを許す区分。§7.1）
CREATE POLICY staging_row_select ON staging_row FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));
CREATE POLICY staging_row_insert ON staging_row FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));
-- 削除は保持期限を過ぎた行だけ、hr_admin だけ
CREATE POLICY staging_row_delete ON staging_row FOR DELETE USING (
    (SELECT jinji_viewer_is_hr_admin()) AND expires_at <= now());

CREATE POLICY normalized_row_select ON normalized_row FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));
CREATE POLICY normalized_row_insert ON normalized_row FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));

CREATE POLICY match_candidate_select ON match_candidate FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));
CREATE POLICY match_candidate_insert ON match_candidate FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));

CREATE POLICY correction_request_select ON correction_request FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));
CREATE POLICY correction_request_insert ON correction_request FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));
CREATE POLICY correction_request_update ON correction_request FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- 変更提案: 起案（追加）は hr_admin かバッチの実行者本人で、起案中・要確認の状態に限る。
-- 承認・却下は hr_admin だけで、承認者は閲覧者本人としてしか記録できない（人に紐付かない system の閲覧者は承認できない）
CREATE POLICY change_proposal_select ON change_proposal FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id));
CREATE POLICY change_proposal_insert ON change_proposal FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_hr_admin()) OR jinji_viewer_is_batch_executor(import_batch_id))
    AND status IN ('proposed', 'needs_review'));
CREATE POLICY change_proposal_update ON change_proposal FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin()))
    WITH CHECK ((SELECT jinji_viewer_is_hr_admin()) AND decided_by_person_id = (SELECT jinji_viewer_person_id()));

-- ---------------------------------------------------------------------------
-- 8. アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT ON import_batch, staging_row, normalized_row, match_candidate, change_proposal, correction_request TO jinji_app;
GRANT DELETE ON staging_row TO jinji_app;
GRANT UPDATE (status, decided_by_person_id, decided_at, applied_at) ON change_proposal TO jinji_app;
GRANT UPDATE (status, resolved_at) ON correction_request TO jinji_app;
