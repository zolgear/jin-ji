-- 0023: 素案（シミュレーション）・申請版・素案からの登録の記録（ADR-0013 決定 8・9）
--
-- 位置づけ
-- - 素案は、来期の組織・異動の計画のように、決める前に試す案。公開されない作業領域（ADR-0009）に置き、公開側を変えない。
--   変更フィード・Webhook・同期計画・承認ルート・時点指定の参照には出さない
-- - 範囲や管理元にかかわらず誰でも作れる。AI エージェントも委任元の名義で承認なしに書ける
-- - 中身は作成者の私物。見えるのは作成者と、作成者が共有した人だけ（plan.shared_with）。人事管理者・org_admin にも見せない
-- - 申請版（plan_version）は素案を固定したもの（版番号を持ち、中身を変えない）。承認の結果（承認日・ワークフロー側の申請番号・記録した人）を
--   1 回だけ記録できる。記録できる人はアプリが決める（範囲を持つ人か、設定で指定した社内システム）
-- - 素案・申請版から直接登録にしたら、どの変更（添字）がどの登録になったかを plan_registration に残す
-- - 素案は最後の更新から保持期限（既定 1 年）を過ぎたら消す（申請版・登録の記録も一緒に消える。登録そのものは残る）
--
-- 中身（changes）の形
-- - 直接登録と同じ変更の配列: [{"kind": ..., "target_id": ..., "values": {...}}]。kind は直接登録の 5 つと、グループの加入・脱退
--   （group_join・group_leave。target_id はグループ、values に person_id と role）
-- - キー・値は許可リストの項目だけで、値は文字列か null（入れ子にして項目を隠せないようにする）。CHECK 制約なので所有者にも効く

-- ---------------------------------------------------------------------------
-- 1. 中身の検査
-- ---------------------------------------------------------------------------

CREATE FUNCTION jinji_plan_value_keys() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'org_unit_id', 'position_title', 'site_id', 'remote_work', 'extension_number', 'work_mobile_phone',
        'code', 'name', 'short_name', 'level_key', 'parent_org_unit_id', 'head_person_id',
        'person_id', 'role'
    ]::text[]
$$;
COMMENT ON FUNCTION jinji_plan_value_keys() IS '素案の変更の values に入れてよいキー（JinJi.Application の PlanChanges.ValueKeys と一致させる）';

CREATE FUNCTION jinji_plan_changes_allowed(changes jsonb) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT jsonb_typeof(changes) = 'array'
       AND jsonb_array_length(changes) <= 500
       AND NOT EXISTS (
           SELECT 1 FROM jsonb_array_elements(changes) AS c (item)
           WHERE jsonb_typeof(c.item) <> 'object'
              OR EXISTS (SELECT 1 FROM jsonb_object_keys(c.item) AS k (key) WHERE k.key NOT IN ('kind', 'target_id', 'values'))
              OR coalesce(c.item ->> 'kind', '') NOT IN ('assignment', 'person', 'org_update', 'org_create', 'org_abolish', 'group_join', 'group_leave')
              OR (c.item ? 'target_id' AND jsonb_typeof(c.item -> 'target_id') NOT IN ('string', 'null'))
              OR (c.item ? 'values' AND (
                      jsonb_typeof(c.item -> 'values') <> 'object'
                      OR EXISTS (
                          SELECT 1 FROM jsonb_each(c.item -> 'values') AS v (key, value)
                          WHERE v.key <> ALL (public.jinji_plan_value_keys())
                             OR jsonb_typeof(v.value) NOT IN ('string', 'null')
                             OR char_length(coalesce(v.value #>> '{}', '')) > 200))))
$$;
COMMENT ON FUNCTION jinji_plan_changes_allowed(jsonb) IS '素案・申請版の changes が、許可リストのキーと文字列（か null）の値だけの変更の配列か';

-- ---------------------------------------------------------------------------
-- 2. 素案・申請版・登録の記録
-- ---------------------------------------------------------------------------

CREATE TABLE plan (
    id               uuid        PRIMARY KEY,
    title            text        NOT NULL,
    note             text,
    base_date        date        NOT NULL,
    effective_date   date        NOT NULL,
    changes          jsonb       NOT NULL DEFAULT '[]',
    owner_person_id  uuid        NOT NULL REFERENCES person (id),
    created_via      text        NOT NULL,
    shared_with      uuid[]      NOT NULL DEFAULT '{}',
    created_at       timestamptz NOT NULL DEFAULT now(),
    updated_at       timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT plan_title_check CHECK (btrim(title) <> '' AND char_length(title) <= 200 AND title !~ '[[:cntrl:]]'),
    CONSTRAINT plan_note_check CHECK (note IS NULL OR (btrim(note) <> '' AND char_length(note) <= 2000)),
    CONSTRAINT plan_changes_allowed CHECK (jinji_plan_changes_allowed(changes)),
    -- 人（UI）/ AI エージェント（委任元の名義）
    CONSTRAINT plan_created_via_check CHECK (created_via IN ('user', 'agent')),
    CONSTRAINT plan_shared_with_check CHECK (cardinality(shared_with) <= 100 AND NOT (owner_person_id = ANY (shared_with)))
);
COMMENT ON TABLE plan IS '素案（ADR-0013 決定 8）。公開されない作業領域で、公開側を変えない。見えるのは作成者と共有した人だけ。最後の更新から保持期限で消す';
COMMENT ON COLUMN plan.base_date IS '基準日（この日の公開側の上に変更を重ねて見る）';
COMMENT ON COLUMN plan.effective_date IS '予定の有効日（直接登録にするときの有効日）';
COMMENT ON COLUMN plan.changes IS '変更の配列（直接登録と同じ形とグループの加入・脱退。許可リストのキーと文字列の値だけ）';
COMMENT ON COLUMN plan.shared_with IS '共有した人（人の項目は、共有された人自身の公開範囲で見せる）';
CREATE INDEX plan_owner_idx ON plan (owner_person_id, updated_at);
CREATE INDEX plan_shared_with_idx ON plan USING gin (shared_with);

CREATE TABLE plan_version (
    id                               uuid        PRIMARY KEY,
    plan_id                          uuid        NOT NULL REFERENCES plan (id) ON DELETE CASCADE,
    version_no                       integer     NOT NULL,
    title                            text        NOT NULL,
    note                             text,
    base_date                        date        NOT NULL,
    effective_date                   date        NOT NULL,
    changes                          jsonb       NOT NULL,
    created_by_person_id             uuid        NOT NULL REFERENCES person (id),
    created_at                       timestamptz NOT NULL DEFAULT now(),
    approved_on                      date,
    approval_reference               text,
    approval_recorded_by_person_id   uuid        REFERENCES person (id),
    approval_recorded_by_client      text,
    approval_recorded_at             timestamptz,
    CONSTRAINT plan_version_no_key UNIQUE (plan_id, version_no),
    CONSTRAINT plan_version_no_check CHECK (version_no >= 1),
    CONSTRAINT plan_version_changes_allowed CHECK (jinji_plan_changes_allowed(changes)),
    CONSTRAINT plan_version_reference_check CHECK (
        approval_reference IS NULL OR (btrim(approval_reference) <> '' AND char_length(approval_reference) <= 100 AND approval_reference !~ '[[:cntrl:]]')),
    -- 承認の記録は、日・記録した日時・記録した人か社内システムのどちらかが揃う
    CONSTRAINT plan_version_approval_check CHECK (
        (approved_on IS NULL) = (approval_recorded_at IS NULL)
        AND (approved_on IS NULL OR ((approval_recorded_by_person_id IS NULL) <> (approval_recorded_by_client IS NULL)))
        AND (approved_on IS NOT NULL OR (approval_reference IS NULL AND approval_recorded_by_person_id IS NULL AND approval_recorded_by_client IS NULL)))
);
COMMENT ON TABLE plan_version IS '申請版（ADR-0013 決定 9）。素案を固定したもの。中身は変えず、承認の結果を 1 回だけ記録する';
COMMENT ON COLUMN plan_version.approval_reference IS 'ワークフロー側の申請番号（任意）';
COMMENT ON COLUMN plan_version.approval_recorded_by_client IS '承認を記録した社内システム（ワークフロー製品）のクライアント ID';

CREATE FUNCTION jinji_guard_plan_version() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    approval CONSTANT text[] := ARRAY['approved_on', 'approval_reference', 'approval_recorded_by_person_id', 'approval_recorded_by_client', 'approval_recorded_at'];
BEGIN
    IF (to_jsonb(NEW) - approval) IS DISTINCT FROM (to_jsonb(OLD) - approval) THEN
        RAISE EXCEPTION 'plan_version: 申請版の中身は変更できません（id=%）。次の版を作ってください', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'plan_version_immutable';
    END IF;

    IF OLD.approved_on IS NOT NULL THEN
        RAISE EXCEPTION 'plan_version: 承認の記録は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'plan_version_approval_once';
    END IF;
    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_plan_version() IS 'plan_version の中身の不変と、承認の記録を 1 回だけにするトリガー関数';

CREATE TRIGGER plan_version_guard BEFORE UPDATE ON plan_version
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_plan_version();

CREATE TABLE plan_registration (
    plan_id                uuid    NOT NULL REFERENCES plan (id) ON DELETE CASCADE,
    plan_version_id        uuid    REFERENCES plan_version (id) ON DELETE CASCADE,
    change_index           integer NOT NULL,
    field_registration_id  uuid    NOT NULL REFERENCES field_registration (id),
    PRIMARY KEY (field_registration_id, change_index),
    CONSTRAINT plan_registration_index_check CHECK (change_index >= 0)
);
COMMENT ON TABLE plan_registration IS '素案・申請版のどの変更（changes の添字）がどの直接登録になったか';
CREATE INDEX plan_registration_plan_idx ON plan_registration (plan_id);

-- ---------------------------------------------------------------------------
-- 3. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

ALTER TABLE plan ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan FORCE ROW LEVEL SECURITY;
ALTER TABLE plan_version ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_version FORCE ROW LEVEL SECURITY;
ALTER TABLE plan_registration ENABLE ROW LEVEL SECURITY;
ALTER TABLE plan_registration FORCE ROW LEVEL SECURITY;

-- 素案: 参照は作成者と共有した人（人・AI エージェントは委任元）。人事管理者・org_admin にも見せない（作成者の私物）。
-- 作成・更新・削除は作成者（AI エージェントは委任元の名義）。jin-ji 自身は保持期限を過ぎたものだけ消せる
CREATE POLICY plan_select ON plan FOR SELECT USING (
    coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false)
    AND (owner_person_id = (SELECT jinji_viewer_person_id()) OR (SELECT jinji_viewer_person_id()) = ANY (shared_with)));
CREATE POLICY plan_select_retention ON plan FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) AND updated_at < now() - interval '30 days');
CREATE POLICY plan_insert ON plan FOR INSERT WITH CHECK (
    coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false)
    AND created_via = (SELECT jinji_viewer_kind())
    AND owner_person_id = (SELECT jinji_viewer_person_id()));
CREATE POLICY plan_update ON plan FOR UPDATE
    USING (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false) AND owner_person_id = (SELECT jinji_viewer_person_id()))
    WITH CHECK (owner_person_id = (SELECT jinji_viewer_person_id()));
CREATE POLICY plan_delete ON plan FOR DELETE USING (
    (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false) AND owner_person_id = (SELECT jinji_viewer_person_id()))
    OR ((SELECT jinji_viewer_is_system()) AND updated_at < now() - interval '30 days'));

-- 申請版: 参照は素案が見える人と、社内システム（ワークフロー製品が承認を記録するため。中身を返すかはアプリが決める）。
-- 作成は素案の作成者。承認の記録は人か社内システム（記録した人・クライアントは閲覧者本人。誰が記録できるかはアプリが決める）
CREATE POLICY plan_version_select ON plan_version FOR SELECT USING (
    EXISTS (SELECT 1 FROM plan p WHERE p.id = plan_id)
    OR coalesce((SELECT jinji_viewer_kind()) = 'service', false));
CREATE POLICY plan_version_insert ON plan_version FOR INSERT WITH CHECK (
    approved_on IS NULL
    AND created_by_person_id = (SELECT jinji_viewer_person_id())
    AND EXISTS (SELECT 1 FROM plan p WHERE p.id = plan_id AND p.owner_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY plan_version_update ON plan_version FOR UPDATE
    USING (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'service'), false))
    WITH CHECK (
        (coalesce((SELECT jinji_viewer_kind()) = 'user', false)
         AND approval_recorded_by_person_id = (SELECT jinji_viewer_person_id()) AND approval_recorded_by_client IS NULL)
        OR (coalesce((SELECT jinji_viewer_kind()) = 'service', false)
            AND approval_recorded_by_person_id IS NULL AND approval_recorded_by_client IS NOT NULL));

-- 登録の記録: 参照は素案が見える人。追加は、その直接登録の登録者本人
CREATE POLICY plan_registration_select ON plan_registration FOR SELECT USING (
    EXISTS (SELECT 1 FROM plan p WHERE p.id = plan_id));
CREATE POLICY plan_registration_insert ON plan_registration FOR INSERT WITH CHECK (
    coalesce((SELECT jinji_viewer_kind()) = 'user', false)
    AND EXISTS (SELECT 1 FROM field_registration r
                WHERE r.id = field_registration_id AND r.registered_by_person_id = (SELECT jinji_viewer_person_id())));

-- ---------------------------------------------------------------------------
-- 4. 保持期限の消去の記録・アプリ用ロールの権限
-- ---------------------------------------------------------------------------

ALTER TABLE retention_run ADD COLUMN plan integer NOT NULL DEFAULT 0;
COMMENT ON COLUMN retention_run.plan IS '消した素案の数（申請版・登録の記録も一緒に消える）';

GRANT SELECT, INSERT, DELETE ON plan TO jinji_app;
GRANT UPDATE (title, note, base_date, effective_date, changes, shared_with, updated_at) ON plan TO jinji_app;
GRANT SELECT, INSERT ON plan_version, plan_registration TO jinji_app;
GRANT UPDATE (approved_on, approval_reference, approval_recorded_by_person_id, approval_recorded_by_client, approval_recorded_at) ON plan_version TO jinji_app;
