-- 0004: 組織の階層（段）を固定せず設定できるようにする
--
-- 内容
-- 1. 段の定義（org_level_scheme / org_level）。法人ごとに持ち、法人に定義が無ければグループ共通の既定（legal_entity_id IS NULL）を使う。
--    段はキー（英小文字・数字・_。ポリシーから参照する安定 ID）、表示名、順位（上から 1, 2, ...）を持つ
--    - 定義の変更は公開範囲（same_level の範囲）を変えるので、範囲ごとに定義を丸ごと起案し、別人の承認で有効な定義を置き換える
--    - 段（org_level）は起案中の定義に、その起案者だけが追加できる。追加後は変更・削除できない（UPDATE / DELETE の権限を与えない）
--    - 範囲ごとに有効（approved）な定義は 1 つ（排他制約。承認と置き換えを 1 トランザクションで行うため、コミット時に検査する）
-- 2. 組織の版は種別（unit_type）の代わりに段のキー（level_key）を持つ。NULL は段を持たない組織（室・プロジェクト等）
--    - 既存の unit_type は、既定の定義（division 本部 1 / department 部 2 / section 課 3 / unit 係 4）の段に写す。group / other は段を持たない組織にする
--      （従来の「group / other は上位組織の一つ下の階層とみなす」規則は、「段を持たない組織は祖先の段で判定する」規則に置き換える。overview §5.6）
-- 3. ポリシーの課内・部内・本部内（same_team / same_department / same_division）を「同じ〈段〉内」（allow_scope = 'same_level' と allow_level_key）に一般化する
--    - 既存の行は same_team → same_level(section)、same_department → same_level(department)、same_division → same_level(division) に写す
--      （従来の same_team の基準は「課」の階層だったので section に写す）
--
-- DB で検査しないこと（アプリと JinJi.IntegrationTests で守る）
-- - 組織の版の level_key が、その法人に効く段の定義にあること、子の段の順位が親（段を持つ最も近い祖先）より下であること:
--   段の定義は範囲ごとに置き換わり、組織の版と親子関係は二つの時間軸を持つので、外部キー・排他制約では表せない。
--   他の行を読むトリガーで書くと、FORCE ROW LEVEL SECURITY の下では閲覧者によって見える行が変わり（将来の組織の版は hr_admin にしか見えない）、
--   検査の結果が閲覧者に依存してしまう。段の定義の変更の承認時（OrgLevelAdministrationService）と、組織の書き込み時（取込で実装する）に
--   アプリが OrgSnapshot.LevelOrderViolations で検査する。定義に無い段のキーを持つ組織は、判定では段を持たない組織として扱う
-- - ポリシーから参照されている段の削除の禁止: policy_rule の allow_level_key は定義の行を指さない（定義は置き換わるため）ので、アプリが承認時に検査する
--
-- 既存の行の書き換え（2・3）について
-- - 組織の版は記録期間の保護（0002）、ポリシーは中身の不変（0002・0003）のトリガーで書き換えを禁じている。ここでの書き換えは表現の移行で、
--   判定の意味は変わらない（同じ人に同じ範囲が当てはまる）ので、マイグレーションの中だけトリガーを止めて書き換える。
--   FORCE ROW LEVEL SECURITY も同じ間だけ外す（所有者として RLS を受けずに全行を書き換えるため）

-- ---------------------------------------------------------------------------
-- 1. 段の定義
-- ---------------------------------------------------------------------------

CREATE TABLE org_level_scheme (
    id                       uuid        PRIMARY KEY,
    legal_entity_id          uuid        REFERENCES legal_entity (id),
    status                   text        NOT NULL DEFAULT 'proposed',
    seeded                   boolean     NOT NULL DEFAULT false,
    proposed_by_person_id    uuid        REFERENCES person (id),
    proposed_via_client_id   text,
    proposed_at              timestamptz NOT NULL DEFAULT now(),
    decided_by_person_id     uuid        REFERENCES person (id),
    decided_at               timestamptz,
    superseded_by_scheme_id  uuid        REFERENCES org_level_scheme (id),
    superseded_at            timestamptz,
    -- 起案中 / 承認済み（有効）/ 却下 / 置き換え済み
    CONSTRAINT org_level_scheme_status_check CHECK (status IN ('proposed', 'approved', 'rejected', 'superseded')),
    -- マイグレーションで作った初期の定義（seeded）だけが起案者・承認者を持たない
    CONSTRAINT org_level_scheme_proposer_check CHECK (seeded = (proposed_by_person_id IS NULL)),
    CONSTRAINT org_level_scheme_decision_check CHECK (
        seeded
        OR ((status = 'proposed') = (decided_by_person_id IS NULL)
            AND (decided_by_person_id IS NULL) = (decided_at IS NULL))),
    CONSTRAINT org_level_scheme_superseded_check CHECK (
        (status = 'superseded') = (superseded_by_scheme_id IS NOT NULL)
        AND (superseded_by_scheme_id IS NULL) = (superseded_at IS NULL)
        AND superseded_by_scheme_id IS DISTINCT FROM id),
    -- 職務分掌: 起案者は自分の起案を承認・却下できない
    CONSTRAINT org_level_scheme_segregation_of_duties CHECK (
        decided_by_person_id IS NULL OR decided_by_person_id IS DISTINCT FROM proposed_by_person_id),
    -- 範囲（法人、または既定）ごとに有効な定義は 1 つ。承認と置き換えを同じトランザクションで行うため、コミット時に検査する
    CONSTRAINT org_level_scheme_one_active EXCLUDE USING gist (
        (coalesce(legal_entity_id, '00000000-0000-0000-0000-000000000000'::uuid)) WITH =)
        WHERE (status = 'approved') DEFERRABLE INITIALLY DEFERRED
);
COMMENT ON TABLE org_level_scheme IS '組織の階層の定義（段の集合）。法人ごと、または legal_entity_id が NULL のグループ共通の既定。変更は範囲ごとに丸ごと起案し、別人の承認で有効な定義を置き換える';

CREATE TABLE org_level (
    id         uuid    PRIMARY KEY,
    scheme_id  uuid    NOT NULL REFERENCES org_level_scheme (id),
    level_key  text    NOT NULL,
    name       text    NOT NULL,
    rank       integer NOT NULL,
    CONSTRAINT org_level_level_key_format CHECK (level_key ~ '^[a-z][a-z0-9_]{0,62}$'),
    CONSTRAINT org_level_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT org_level_rank_positive CHECK (rank >= 1),
    CONSTRAINT org_level_scheme_key UNIQUE (scheme_id, level_key),
    CONSTRAINT org_level_scheme_rank UNIQUE (scheme_id, rank)
);
COMMENT ON TABLE org_level IS '組織の階層の段（例: 会社・事業部・部・課・チーム）。順位は上から 1, 2, ...（連番はアプリが検査する）。起案後は変更しない';

-- 既定の定義（従来の固定の階層を写したもの）
DO $$
DECLARE
    scheme uuid := gen_random_uuid();
BEGIN
    INSERT INTO org_level_scheme (id, legal_entity_id, status, seeded) VALUES (scheme, NULL, 'approved', true);
    INSERT INTO org_level (id, scheme_id, level_key, name, rank) VALUES
        (gen_random_uuid(), scheme, 'division', '本部', 1),
        (gen_random_uuid(), scheme, 'department', '部', 2),
        (gen_random_uuid(), scheme, 'section', '課', 3),
        (gen_random_uuid(), scheme, 'unit', '係', 4);
END
$$;

-- 状態の遷移: proposed → approved | rejected、approved → superseded（同じ範囲の承認済みの別の定義に置き換えられたときだけ）
CREATE FUNCTION jinji_guard_org_level_scheme() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    workflow CONSTANT text[] := ARRAY['status', 'decided_by_person_id', 'decided_at', 'superseded_by_scheme_id', 'superseded_at'];
    replacement record;
BEGIN
    IF (to_jsonb(NEW) - workflow) IS DISTINCT FROM (to_jsonb(OLD) - workflow) THEN
        RAISE EXCEPTION 'org_level_scheme: 定義の中身は変更できません（id=%）。新しく起案してください', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'org_level_scheme_immutable';
    END IF;

    IF OLD.status = 'proposed' AND NEW.status IN ('approved', 'rejected') AND NEW.superseded_by_scheme_id IS NULL THEN
        RETURN NEW;
    END IF;

    IF OLD.status = 'approved' AND NEW.status = 'superseded'
       AND (NEW.decided_by_person_id, NEW.decided_at) IS NOT DISTINCT FROM (OLD.decided_by_person_id, OLD.decided_at) THEN
        -- 置き換え先は同じ範囲の承認済みの定義（定義の表は閲覧者が設定されていれば誰でも読めるので、RLS で結果が変わらない）
        SELECT s.legal_entity_id, s.status INTO replacement FROM public.org_level_scheme s WHERE s.id = NEW.superseded_by_scheme_id;
        IF FOUND AND replacement.status = 'approved' AND replacement.legal_entity_id IS NOT DISTINCT FROM OLD.legal_entity_id THEN
            RETURN NEW;
        END IF;

        RAISE EXCEPTION 'org_level_scheme: 置き換え先は同じ範囲の承認済みの定義でなければなりません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'org_level_scheme_transition';
    END IF;

    RAISE EXCEPTION 'org_level_scheme: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'org_level_scheme_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_org_level_scheme() IS 'org_level_scheme の状態遷移（起案 → 承認・却下、承認済み → 置き換え済み）と中身の不変を守るトリガー関数';

CREATE TRIGGER org_level_scheme_guard BEFORE UPDATE ON org_level_scheme
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_org_level_scheme();

-- ---------------------------------------------------------------------------
-- 2. 組織の版: 種別 → 段のキー
-- ---------------------------------------------------------------------------

ALTER TABLE org_unit_version ADD COLUMN level_key text;
ALTER TABLE org_unit_version ADD CONSTRAINT org_unit_version_level_key_format CHECK (level_key IS NULL OR level_key ~ '^[a-z][a-z0-9_]{0,62}$');
COMMENT ON COLUMN org_unit_version.level_key IS '段のキー（法人に効く段の定義で解決する）。NULL は段を持たない組織（室・プロジェクト等。所属者は祖先の段で判定する）';

ALTER TABLE org_unit_version NO FORCE ROW LEVEL SECURITY;
ALTER TABLE org_unit_version DISABLE TRIGGER org_unit_version_guard_recorded_period;
UPDATE org_unit_version SET level_key = CASE WHEN unit_type IN ('division', 'department', 'section', 'unit') THEN unit_type END;
ALTER TABLE org_unit_version ENABLE TRIGGER org_unit_version_guard_recorded_period;
ALTER TABLE org_unit_version FORCE ROW LEVEL SECURITY;

ALTER TABLE org_unit_version DROP COLUMN unit_type;

-- ---------------------------------------------------------------------------
-- 3. ポリシー: same_team / same_department / same_division → same_level(段)
-- ---------------------------------------------------------------------------

ALTER TABLE policy_rule ADD COLUMN allow_level_key text;
COMMENT ON COLUMN policy_rule.allow_level_key IS 'allow_scope = same_level の段のキー（同じ〈段〉内。段は対象者・閲覧者の所属組織の法人の定義で解決する）';
ALTER TABLE policy_rule DROP CONSTRAINT policy_rule_allow_scope_check;

ALTER TABLE policy_rule NO FORCE ROW LEVEL SECURITY;
ALTER TABLE policy_rule DISABLE TRIGGER policy_rule_guard;
UPDATE policy_rule
SET allow_level_key = CASE allow_scope
        WHEN 'same_team' THEN 'section'
        WHEN 'same_department' THEN 'department'
        WHEN 'same_division' THEN 'division'
    END,
    allow_scope = 'same_level'
WHERE allow_scope IN ('same_team', 'same_department', 'same_division');
ALTER TABLE policy_rule ENABLE TRIGGER policy_rule_guard;
ALTER TABLE policy_rule FORCE ROW LEVEL SECURITY;

ALTER TABLE policy_rule
    ADD CONSTRAINT policy_rule_allow_scope_check CHECK (allow_scope IN (
        'none', 'self', 'same_level', 'legal_entity', 'group', 'direct_reports', 'subordinates', 'managers_chain')),
    ADD CONSTRAINT policy_rule_allow_level_key_check CHECK (
        (allow_scope = 'same_level') = (allow_level_key IS NOT NULL)
        AND (allow_level_key IS NULL OR allow_level_key ~ '^[a-z][a-z0-9_]{0,62}$'));

-- ---------------------------------------------------------------------------
-- 4. 行レベルセキュリティと権限
-- ---------------------------------------------------------------------------

ALTER TABLE org_level_scheme ENABLE ROW LEVEL SECURITY;
ALTER TABLE org_level_scheme FORCE ROW LEVEL SECURITY;
ALTER TABLE org_level ENABLE ROW LEVEL SECURITY;
ALTER TABLE org_level FORCE ROW LEVEL SECURITY;

-- 参照は閲覧者が設定されていれば誰でも（判定に使うため）。変更系は hr_admin だけで、起案者・承認者は閲覧者本人として記録させる
CREATE POLICY org_level_scheme_select ON org_level_scheme FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY org_level_scheme_insert ON org_level_scheme FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin())
    AND status = 'proposed'
    AND NOT seeded
    AND proposed_by_person_id = (SELECT jinji_viewer_person_id()));
CREATE POLICY org_level_scheme_update ON org_level_scheme FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin()))
    WITH CHECK (
        (SELECT jinji_viewer_is_hr_admin())
        AND ((status IN ('approved', 'rejected') AND superseded_by_scheme_id IS NULL
              AND decided_by_person_id = (SELECT jinji_viewer_person_id()))
             OR status = 'superseded'));

-- 段は、起案中の定義に、その起案者だけが追加できる
CREATE POLICY org_level_select ON org_level FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY org_level_insert ON org_level FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin())
    AND EXISTS (
        SELECT 1 FROM org_level_scheme s
        WHERE s.id = scheme_id AND s.status = 'proposed' AND s.proposed_by_person_id = (SELECT jinji_viewer_person_id())));

GRANT SELECT, INSERT ON org_level_scheme, org_level TO jinji_app;
GRANT UPDATE (status, decided_by_person_id, decided_at, superseded_by_scheme_id, superseded_at) ON org_level_scheme TO jinji_app;
