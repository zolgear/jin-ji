-- 0003: ポリシーの廃止の承認フローと、個人単位の例外への本人の関与の禁止
--
-- 内容
-- 1. 廃止も起案 → 別人の承認で反映する。廃止は公開範囲を広げ得る変更なので、追加と同じく一人では行えない（アーキテクチャ概要 §5.3、§5.5）
--    - 廃止の起案は承認済みの行に retirement_proposed_* を記録する。承認されるまで status は approved のまま（規則は有効）
--    - 廃止の承認で status = retired にし、retired_by_person_id（廃止を承認した人）を記録する
--    - 廃止の起案者 ≠ 廃止の承認者は CHECK 制約で守る（起案者 ≠ 承認者と同じ）
-- 2. 公開範囲は組織のルールとして決め、本人の裁量にしない（ADR-0002）。個人単位の例外（target_kind = 'person'）の対象者本人は、
--    その例外の起案者・承認者・廃止の起案者・廃止の承認者のいずれにもなれない（CHECK 制約）
--
-- 状態の遷移（トリガー jinji_guard_policy_rule を置き換える）
--   proposed                     → approved | rejected   （承認・却下。廃止の起案を同時に記録することはできない）
--   approved（廃止の起案なし）   → approved（廃止の起案あり）
--   approved（廃止の起案あり）   → retired                （廃止の承認。廃止の起案の記録は変えられない）
-- 廃止の起案の取り下げ・却下はまだ持たない（承認されなければ規則は有効のまま）。
--
-- 既存の行について
-- - 0002 の時点では廃止を一人で行えたため、廃止の起案の記録が無い retired の行や、対象者本人が起案した例外があり得る。
--   これらを拒否する制約は NOT VALID で付け、既存の行は検査しない（新しく書き込む行・更新する行には効く）

-- ---------------------------------------------------------------------------
-- 1. 廃止の起案の列
-- ---------------------------------------------------------------------------

ALTER TABLE policy_rule
    ADD COLUMN retirement_proposed_by_person_id  uuid REFERENCES person (id),
    ADD COLUMN retirement_proposed_via_client_id text,
    ADD COLUMN retirement_proposed_at            timestamptz;

COMMENT ON COLUMN policy_rule.retirement_proposed_by_person_id IS '廃止の起案者（AI エージェントの起案は委任元ユーザー）。廃止が承認されるまで規則は有効';
COMMENT ON COLUMN policy_rule.retirement_proposed_via_client_id IS '廃止の起案に使われたクライアント（AI エージェント等）';
COMMENT ON COLUMN policy_rule.retirement_proposed_at IS '廃止の起案日時';
COMMENT ON COLUMN policy_rule.retired_by_person_id IS '廃止を承認した人（廃止の起案者とは別人）';

ALTER TABLE policy_rule
    -- 廃止の起案は承認済み（または廃止済み）の行にだけ。起案者と日時は組で持つ
    ADD CONSTRAINT policy_rule_retirement_proposal_check CHECK (
        (retirement_proposed_by_person_id IS NULL) = (retirement_proposed_at IS NULL)
        AND (retirement_proposed_via_client_id IS NULL OR retirement_proposed_by_person_id IS NOT NULL)
        AND (retirement_proposed_by_person_id IS NULL OR status IN ('approved', 'retired'))),
    -- 職務分掌: 廃止の起案者は自分の廃止の起案を承認できない
    ADD CONSTRAINT policy_rule_retirement_segregation_of_duties CHECK (
        retired_by_person_id IS NULL OR retired_by_person_id IS DISTINCT FROM retirement_proposed_by_person_id);

-- 廃止には廃止の起案が要る（既存の行は検査しない）
ALTER TABLE policy_rule
    ADD CONSTRAINT policy_rule_retirement_requires_proposal CHECK (
        status <> 'retired' OR retirement_proposed_by_person_id IS NOT NULL) NOT VALID;

-- ---------------------------------------------------------------------------
-- 2. 個人単位の例外への本人の関与の禁止（既存の行は検査しない）
-- ---------------------------------------------------------------------------

ALTER TABLE policy_rule
    ADD CONSTRAINT policy_rule_person_exception_not_self CHECK (
        person_id IS NULL
        OR (person_id IS DISTINCT FROM proposed_by_person_id
            AND person_id IS DISTINCT FROM decided_by_person_id
            AND person_id IS DISTINCT FROM retirement_proposed_by_person_id
            AND person_id IS DISTINCT FROM retired_by_person_id)) NOT VALID;

COMMENT ON COLUMN policy_rule.person_id IS '個人単位の例外の対象者（人事管理者が起案し承認を経て設定する。対象者本人は起案・承認・廃止に関われない）';
COMMENT ON TABLE policy_rule IS '公開範囲のポリシーの規則（狭める方向だけ）。承認済み（status = approved）の行だけが判定に使われる。追加も廃止も起案 → 別人の承認で反映する';

-- ---------------------------------------------------------------------------
-- 3. 状態の遷移（0002 のトリガー関数を置き換える）
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION jinji_guard_policy_rule() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    workflow CONSTANT text[] := ARRAY[
        'status', 'decided_by_person_id', 'decided_at', 'retired_by_person_id', 'retired_at',
        'retirement_proposed_by_person_id', 'retirement_proposed_via_client_id', 'retirement_proposed_at'];
    retirement_proposal_changed boolean :=
        (NEW.retirement_proposed_by_person_id, NEW.retirement_proposed_via_client_id, NEW.retirement_proposed_at)
        IS DISTINCT FROM (OLD.retirement_proposed_by_person_id, OLD.retirement_proposed_via_client_id, OLD.retirement_proposed_at);
BEGIN
    IF (to_jsonb(NEW) - workflow) IS DISTINCT FROM (to_jsonb(OLD) - workflow) THEN
        RAISE EXCEPTION 'policy_rule: 規則の中身は変更できません（id=%）。新しく起案してください', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'policy_rule_immutable';
    END IF;

    IF OLD.status <> 'proposed' AND (NEW.decided_by_person_id IS DISTINCT FROM OLD.decided_by_person_id
                                     OR NEW.decided_at IS DISTINCT FROM OLD.decided_at) THEN
        RAISE EXCEPTION 'policy_rule: 承認・却下の記録は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'policy_rule_transition';
    END IF;

    -- 承認・却下（廃止の起案を同時に記録させない。RLS は廃止の起案者が閲覧者本人であることしか見ないため、
    -- 同時に許すと「他人の名前での承認 + 自分の廃止の起案」を一度に書けてしまう）
    IF OLD.status = 'proposed' AND NEW.status IN ('approved', 'rejected') AND NOT retirement_proposal_changed THEN
        RETURN NEW;
    END IF;

    -- 廃止の起案
    IF OLD.status = 'approved' AND NEW.status = 'approved'
       AND OLD.retirement_proposed_by_person_id IS NULL AND NEW.retirement_proposed_by_person_id IS NOT NULL THEN
        RETURN NEW;
    END IF;

    -- 廃止の承認（廃止の起案の記録は変えさせない。変えられると職務分掌の CHECK をすり抜けられる）
    IF OLD.status = 'approved' AND NEW.status = 'retired'
       AND OLD.retirement_proposed_by_person_id IS NOT NULL AND NOT retirement_proposal_changed THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'policy_rule: この状態の変更はできません（id=%、% → %、廃止の起案 %）',
        OLD.id, OLD.status, NEW.status,
        CASE WHEN OLD.retirement_proposed_by_person_id IS NULL THEN 'なし' ELSE 'あり' END
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'policy_rule_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_policy_rule() IS 'policy_rule の状態遷移（起案 → 承認・却下、承認済み → 廃止の起案 → 廃止）と中身の不変を守るトリガー関数';

-- ---------------------------------------------------------------------------
-- 4. RLS（更新の規則を置き換える）
-- ---------------------------------------------------------------------------

-- 記録する人は閲覧者本人だけ:
--   承認・却下    → decided_by_person_id
--   廃止の起案    → retirement_proposed_by_person_id
--   廃止の承認    → retired_by_person_id
DROP POLICY policy_rule_update ON policy_rule;
CREATE POLICY policy_rule_update ON policy_rule FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin()))
    WITH CHECK (
        (SELECT jinji_viewer_is_hr_admin())
        AND ((status IN ('approved', 'rejected') AND retirement_proposed_by_person_id IS NULL
              AND decided_by_person_id = (SELECT jinji_viewer_person_id()))
             OR (status = 'approved' AND retirement_proposed_by_person_id = (SELECT jinji_viewer_person_id()))
             OR (status = 'retired' AND retired_by_person_id = (SELECT jinji_viewer_person_id()))));

-- ---------------------------------------------------------------------------
-- アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT UPDATE (retirement_proposed_by_person_id, retirement_proposed_via_client_id, retirement_proposed_at) ON policy_rule TO jinji_app;
