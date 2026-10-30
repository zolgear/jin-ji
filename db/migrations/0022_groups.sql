-- 0022: 規程外の単位（チーム・プロジェクト）のグループ（ADR-0014）
--
-- 位置づけ
-- - 社内規程に載らず人事システムでも扱わないチーム・プロジェクト・委員会を、組織（org_unit）とは別の「グループ」として持つ。
--   組織のツリー・上長・承認ルート・same_level・人の拠点の既定・Entra ID の部署には影響させない
-- - 管理元は常に jin-ji。作成と変更は承認なしで、すぐに反映する（ADR-0013 の直接登録と同じ扱い）
-- - グループの属性（名称・種別・所管組織）とメンバーは二つの時間軸で持つ（アクセス権の根拠を後から再現できるように）。
--   公開範囲（visibility）と管理対象（managed）は今の設定で、変更は監査ログに残す
--
-- 誰が変えられるか（アプリと RLS）
-- - 作成は「人」なら誰でも。作成者が最初のオーナーになる（作成のトランザクションの中だけ、作成者が編集者として扱われる）
-- - 変更は、オーナー・所管組織の範囲を持つ人（ADR-0013 決定 3）・org_admin・人事管理者（jinji_group_may_edit）
-- - メンバーは自分を外せる（管理対象のグループを除く）
-- - 管理対象（managed）にするのは org_admin・人事管理者だけ。管理対象のグループのオーナーの指名は、org_admin・人事管理者・所管組織の範囲を持つ人だけ
--   （オーナーが別の人をオーナーにして権限を広げられないようにする）
-- - AI エージェントは書けない（グループの変更はアクセス権に効く公開情報なので、エージェントは素案の下書きまで。ADR-0014 決定 7）
--
-- 公開範囲
-- - visibility = all（既定）は閲覧者なら誰でも見える。members は、メンバー（今日以降に有効なメンバー）・org_admin・人事管理者だけが存在を見られる
-- - 判定に使うメンバーの一覧は org_group.member_ids に持ち、メンバーの書き込みでトリガーが作り直す。期間が終わったメンバーは、
--   次にそのグループのメンバーを書き込むまで一覧に残る（存在が見える期間が少し延びる側に倒れる）
-- - メンバーの表の参照の方針はグループの表を、グループの表の参照の方針は自分の列だけを読む（方針の再帰を作らない）

-- ---------------------------------------------------------------------------
-- 1. グループ
-- ---------------------------------------------------------------------------

CREATE TABLE org_group (
    id                    uuid        PRIMARY KEY,
    visibility            text        NOT NULL DEFAULT 'all',
    managed               boolean     NOT NULL DEFAULT false,
    member_ids            uuid[]      NOT NULL DEFAULT '{}',
    created_by_person_id  uuid        NOT NULL REFERENCES person (id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    changed_at            timestamptz NOT NULL DEFAULT now(),
    -- 全社公開 / メンバーだけ
    CONSTRAINT org_group_visibility_check CHECK (visibility IN ('all', 'members'))
);
COMMENT ON TABLE org_group IS 'グループ（チーム・プロジェクトなど規程外の単位。ADR-0014）の不変 ID と今の設定。名称などは org_group_version に版として持つ';
COMMENT ON COLUMN org_group.visibility IS '公開範囲（all: 全社 / members: メンバー・org_admin・人事管理者だけ）';
COMMENT ON COLUMN org_group.managed IS 'アクセス権の管理の対象か（関係スコープ same_group に使える。org_admin・人事管理者だけが設定する）';
COMMENT ON COLUMN org_group.changed_at IS '設定（公開範囲・管理対象・メンバーの一覧）を最後に変えた日時（スナップショットの世代の判定に使う）';
COMMENT ON COLUMN org_group.member_ids IS '今日以降に有効なメンバー（公開範囲 members の判定に使う。メンバーの書き込みでトリガーが作り直す）';
CREATE INDEX org_group_member_ids_idx ON org_group USING gin (member_ids);

CREATE TABLE org_group_version (
    id                 uuid      PRIMARY KEY,
    group_id           uuid      NOT NULL REFERENCES org_group (id),
    name               text      NOT NULL,
    kind               text      NOT NULL,
    owner_org_unit_id  uuid      REFERENCES org_unit (id),
    valid_period       daterange NOT NULL,
    recorded_period    tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    CONSTRAINT org_group_version_name_check CHECK (btrim(name) <> '' AND char_length(name) <= 200 AND name !~ '[[:cntrl:]]'),
    -- チーム / プロジェクト / 委員会 / その他
    CONSTRAINT org_group_version_kind_check CHECK (kind IN ('team', 'project', 'committee', 'other')),
    CONSTRAINT org_group_version_valid_period_check CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT org_group_version_recorded_period_check CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    CONSTRAINT org_group_version_no_overlap
        EXCLUDE USING gist (group_id WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period))
);
COMMENT ON TABLE org_group_version IS 'グループの版（bitemporal）。名称・種別・所管組織。有効期間の終わりがグループの終了';
COMMENT ON COLUMN org_group_version.owner_org_unit_id IS '所管組織（任意。組織図で添えて表示し、範囲を持つ人が変更できる）';

CREATE TABLE org_group_member (
    id                  uuid      PRIMARY KEY,
    group_id            uuid      NOT NULL REFERENCES org_group (id),
    person_id           uuid      NOT NULL REFERENCES person (id),
    role                text      NOT NULL,
    added_by_person_id  uuid      NOT NULL REFERENCES person (id),
    valid_period        daterange NOT NULL,
    recorded_period     tstzrange NOT NULL DEFAULT tstzrange(now(), NULL),
    -- オーナー / リーダー / メンバー
    CONSTRAINT org_group_member_role_check CHECK (role IN ('owner', 'lead', 'member')),
    CONSTRAINT org_group_member_valid_period_check CHECK (NOT isempty(valid_period) AND NOT lower_inf(valid_period)),
    CONSTRAINT org_group_member_recorded_period_check CHECK (NOT isempty(recorded_period) AND NOT lower_inf(recorded_period)),
    CONSTRAINT org_group_member_no_overlap
        EXCLUDE USING gist (group_id WITH =, person_id WITH =, valid_period WITH &&) WHERE (upper_inf(recorded_period))
);
COMMENT ON TABLE org_group_member IS 'グループのメンバー（bitemporal）。いつ誰が誰を加えたか・いつからいつまでメンバーだったかを残す（アクセス権の根拠）';
CREATE INDEX org_group_member_group_idx ON org_group_member (group_id);
CREATE INDEX org_group_member_person_idx ON org_group_member (person_id);

CREATE TRIGGER org_group_version_guard_recorded_period BEFORE UPDATE ON org_group_version
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();
CREATE TRIGGER org_group_member_guard_recorded_period BEFORE UPDATE ON org_group_member
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();

-- ---------------------------------------------------------------------------
-- 2. 編集できる人の判定
-- ---------------------------------------------------------------------------

-- 閲覧者がグループのオーナーか（今日有効な、現在記録中のオーナーの行）
CREATE FUNCTION jinji_viewer_is_group_owner(target uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT EXISTS (
        SELECT 1 FROM public.org_group_member m
        WHERE m.group_id = target AND m.person_id = public.jinji_viewer_person_id() AND m.role = 'owner'
          AND m.valid_period @> public.jinji_business_date()
          AND (upper_inf(m.recorded_period) OR (upper(m.recorded_period) = pg_catalog.now() AND lower(m.recorded_period) < pg_catalog.now())))
$$;
COMMENT ON FUNCTION jinji_viewer_is_group_owner(uuid) IS 'RLS: 閲覧者がグループの今日のオーナーか（同じトランザクションで閉じた行も含める）';

-- 閲覧者がグループの管理者か（人事管理者・org_admin・今日の所管組織の範囲を持つ人）。管理対象のグループのオーナーの指名に使う
CREATE FUNCTION jinji_viewer_is_group_admin(target uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce(public.jinji_viewer_kind() = 'user', false)
       AND (public.jinji_viewer_is_hr_admin()
            OR public.jinji_viewer_is_org_admin()
            OR EXISTS (
                -- トランザクションの開始時点の版で判定する（同じトランザクションで閉じた版も含める。変更で自分の権限を作らない）
                SELECT 1 FROM public.org_group_version v
                WHERE v.group_id = target AND v.valid_period @> public.jinji_business_date()
                  AND lower(v.recorded_period) < pg_catalog.now()
                  AND (upper_inf(v.recorded_period) OR upper(v.recorded_period) = pg_catalog.now())
                  AND v.owner_org_unit_id IS NOT NULL
                  AND public.jinji_viewer_scope_covers(v.owner_org_unit_id, public.jinji_business_date(), true)))
$$;
COMMENT ON FUNCTION jinji_viewer_is_group_admin(uuid) IS 'RLS: 閲覧者がグループの管理者（人事管理者・org_admin・所管組織の範囲を持つ人）か';

-- 閲覧者がグループを編集できるか（管理者・オーナー。作成のトランザクションの中では作成者も）
CREATE FUNCTION jinji_viewer_may_edit_group(target uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce(public.jinji_viewer_kind() = 'user', false)
       AND (public.jinji_viewer_is_group_admin(target)
            OR public.jinji_viewer_is_group_owner(target)
            OR EXISTS (
                SELECT 1 FROM public.org_group g
                WHERE g.id = target AND g.created_by_person_id = public.jinji_viewer_person_id() AND g.created_at = pg_catalog.now()))
$$;
COMMENT ON FUNCTION jinji_viewer_may_edit_group(uuid) IS 'RLS: 閲覧者がグループを編集できるか（管理者・オーナー。作成のトランザクションの中では作成者）';

-- 同じトランザクションで記録期間を閉じた、同じ内容（グループ・人・役割・加えた人）のメンバーの行があるか（写した行の判定）。
-- メンバーの表の方針から自分の表を読むので plpgsql にする（方針の再帰として拒否されないように、問い合わせを関数の中に閉じ込める）
CREATE FUNCTION jinji_group_member_closed_now(p_group uuid, p_person uuid, p_role text, p_added_by uuid) RETURNS boolean
LANGUAGE plpgsql STABLE AS $$
BEGIN
    RETURN EXISTS (
        SELECT 1 FROM org_group_member m
        WHERE m.group_id = p_group AND m.person_id = p_person AND m.role = p_role AND m.added_by_person_id = p_added_by
          AND lower(m.recorded_period) < pg_catalog.now() AND upper(m.recorded_period) = pg_catalog.now());
END
$$;
COMMENT ON FUNCTION jinji_group_member_closed_now(uuid, uuid, text, uuid) IS 'RLS: 同じトランザクションで記録期間を閉じた同じ内容のメンバーの行があるか（写した行の判定）';

-- ---------------------------------------------------------------------------
-- 3. トリガー（設定の変更の検査・メンバーの一覧の作り直し）
-- ---------------------------------------------------------------------------

-- 設定の変更: ID・作成者・作成日時は変えない（変えた日時は記録する）。管理対象は人事管理者・org_admin だけ、公開範囲は編集できる人だけが変えられる
CREATE FUNCTION jinji_guard_org_group() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.id, NEW.created_by_person_id, NEW.created_at) IS DISTINCT FROM (OLD.id, OLD.created_by_person_id, OLD.created_at) THEN
        RAISE EXCEPTION 'org_group: ID・作成者・作成日時は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'org_group_immutable';
    END IF;

    IF NEW.managed IS DISTINCT FROM OLD.managed
       AND NOT (coalesce(public.jinji_viewer_kind() = 'user', false) AND (public.jinji_viewer_is_hr_admin() OR public.jinji_viewer_is_org_admin())) THEN
        RAISE EXCEPTION 'org_group: 管理対象にする・外すのは人事管理者・組織管理者だけです（id=%）', OLD.id
            USING ERRCODE = 'insufficient_privilege', CONSTRAINT = 'org_group_managed';
    END IF;

    IF NEW.visibility IS DISTINCT FROM OLD.visibility AND NOT public.jinji_viewer_may_edit_group(OLD.id) THEN
        RAISE EXCEPTION 'org_group: 公開範囲はグループを編集できる人だけが変えられます（id=%）', OLD.id
            USING ERRCODE = 'insufficient_privilege', CONSTRAINT = 'org_group_visibility';
    END IF;
    NEW.changed_at := pg_catalog.now();
    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_org_group() IS 'org_group の不変の列・管理対象・公開範囲の変更を守るトリガー関数';

CREATE TRIGGER org_group_guard BEFORE UPDATE ON org_group
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_org_group();

-- メンバーの書き込みのたびに、グループの今日以降に有効なメンバーの一覧を作り直す
CREATE FUNCTION jinji_refresh_group_members() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    UPDATE org_group SET member_ids = coalesce((
        SELECT array_agg(DISTINCT m.person_id ORDER BY m.person_id) FROM org_group_member m
        WHERE m.group_id = NEW.group_id AND upper_inf(m.recorded_period)
          AND (upper_inf(m.valid_period) OR upper(m.valid_period) > public.jinji_business_date())), '{}')
    WHERE id = NEW.group_id;
    RETURN NULL;
END
$$;
COMMENT ON FUNCTION jinji_refresh_group_members() IS 'グループのメンバーの書き込みで org_group.member_ids を作り直すトリガー関数';

CREATE TRIGGER org_group_member_refresh AFTER INSERT OR UPDATE OF recorded_period ON org_group_member
    FOR EACH ROW EXECUTE FUNCTION jinji_refresh_group_members();

-- ---------------------------------------------------------------------------
-- 4. 変更フィード（種別 group。公開範囲 all のグループだけ記録する）
-- ---------------------------------------------------------------------------

ALTER TABLE change_event DROP CONSTRAINT change_event_entity_type_check;
ALTER TABLE change_event ADD CONSTRAINT change_event_entity_type_check CHECK (entity_type IN ('person', 'org_unit', 'site', 'group'));
ALTER TABLE webhook_notified_entity DROP CONSTRAINT webhook_notified_entity_type_check;
ALTER TABLE webhook_notified_entity ADD CONSTRAINT webhook_notified_entity_type_check CHECK (entity_type IN ('person', 'org_unit', 'site', 'group'));

CREATE FUNCTION jinji_record_group_change() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_withdrawn boolean := TG_OP = 'UPDATE';
    v_date date;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM org_group g WHERE g.id = NEW.group_id AND g.visibility = 'all') THEN
        RETURN NULL;
    END IF;

    v_date := jinji_change_visible_from(NEW.valid_period);
    -- 記録期間を閉じた行は、将来の予定の取り消しのときだけ記録する（置き換えの行が追加の記録として届く）
    IF v_withdrawn AND v_date <= jinji_business_date() THEN
        RETURN NULL;
    END IF;

    INSERT INTO change_event (entity_type, entity_id, effective_date, withdrawn) VALUES ('group', NEW.group_id, v_date, v_withdrawn);
    RETURN NULL;
END
$$;
COMMENT ON FUNCTION jinji_record_group_change() IS '変更フィード: グループの版・メンバーの追加と将来の予定の取り消しを change_event に記録するトリガー関数（公開範囲 all だけ）';

CREATE TRIGGER org_group_version_record_change AFTER INSERT ON org_group_version
    FOR EACH ROW EXECUTE FUNCTION jinji_record_group_change();
CREATE TRIGGER org_group_member_record_change AFTER INSERT ON org_group_member
    FOR EACH ROW EXECUTE FUNCTION jinji_record_group_change();
CREATE TRIGGER org_group_version_record_withdrawal AFTER UPDATE OF recorded_period ON org_group_version
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_group_change();
CREATE TRIGGER org_group_member_record_withdrawal AFTER UPDATE OF recorded_period ON org_group_member
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_group_change();

-- ---------------------------------------------------------------------------
-- 5. ポリシーの関係スコープ same_group（管理対象のグループの同じメンバー）
-- ---------------------------------------------------------------------------

ALTER TABLE policy_rule NO FORCE ROW LEVEL SECURITY;
ALTER TABLE policy_rule DISABLE TRIGGER policy_rule_guard;
ALTER TABLE policy_rule DROP CONSTRAINT policy_rule_allow_scope_check;
ALTER TABLE policy_rule ADD CONSTRAINT policy_rule_allow_scope_check CHECK (allow_scope IN (
    'none', 'self', 'same_level', 'legal_entity', 'group', 'direct_reports', 'subordinates', 'managers_chain', 'same_group'));
ALTER TABLE policy_rule ENABLE TRIGGER policy_rule_guard;
ALTER TABLE policy_rule FORCE ROW LEVEL SECURITY;

-- 修正依頼で報告できる人の項目に、所属するグループ（groups）を足す
CREATE OR REPLACE FUNCTION jinji_correction_person_fields() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY[
        'employee_number', 'legal_entity', 'display_name', 'family_name', 'given_name', 'family_name_kana', 'given_name_kana',
        'family_name_latin', 'given_name_latin', 'work_email', 'extension_number', 'work_mobile_phone',
        'employment_type', 'job_type', 'hire_date', 'termination_date', 'employment_status', 'account_status', 'account_suspension',
        'assignments', 'managers', 'personnel_actions',
        -- 0017
        'site', 'remote_work',
        -- 0019
        'workplace', 'seat',
        -- 0022
        'groups',
        -- 0017 より前の起票（廃止した項目。新しい起票には使えない）
        'work_location'
    ]::text[]
$$;

-- ---------------------------------------------------------------------------
-- 6. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

ALTER TABLE org_group ENABLE ROW LEVEL SECURITY;
ALTER TABLE org_group FORCE ROW LEVEL SECURITY;
ALTER TABLE org_group_version ENABLE ROW LEVEL SECURITY;
ALTER TABLE org_group_version FORCE ROW LEVEL SECURITY;
ALTER TABLE org_group_member ENABLE ROW LEVEL SECURITY;
ALTER TABLE org_group_member FORCE ROW LEVEL SECURITY;

-- グループ: 全社公開は閲覧者なら誰でも、メンバーだけのものはメンバー・org_admin・人事管理者。参照の方針は自分の列だけを読む
CREATE POLICY org_group_select ON org_group FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR (SELECT jinji_viewer_is_org_admin())
    OR ((SELECT jinji_viewer_is_set()) AND visibility = 'all')
    OR (SELECT jinji_viewer_person_id()) = ANY (member_ids)
    -- 作成のトランザクションの中の作成者（メンバーだけのグループでも、最初のオーナーを加えるまでに見えるように）
    OR (created_by_person_id = (SELECT jinji_viewer_person_id()) AND created_at = pg_catalog.now()));
-- 作成は「人」が本人の名義で（管理対象は作成のときには付けない）
CREATE POLICY org_group_insert ON org_group FOR INSERT WITH CHECK (
    coalesce((SELECT jinji_viewer_kind()) = 'user', false)
    AND created_by_person_id = (SELECT jinji_viewer_person_id())
    AND NOT managed);
-- 更新は編集できる人と、メンバーの一覧の作り直し（自分を外したメンバー）。設定の列の検査はトリガー
CREATE POLICY org_group_update ON org_group FOR UPDATE
    USING (coalesce((SELECT jinji_viewer_kind()) = 'user', false)
           AND (jinji_viewer_may_edit_group(id) OR (SELECT jinji_viewer_person_id()) = ANY (member_ids)))
    WITH CHECK (coalesce((SELECT jinji_viewer_kind()) = 'user', false));

-- グループの版: 参照はグループが見える人。追加・記録期間を閉じるのは編集できる人
CREATE POLICY org_group_version_select ON org_group_version FOR SELECT USING (
    EXISTS (SELECT 1 FROM org_group g WHERE g.id = group_id));
CREATE POLICY org_group_version_insert ON org_group_version FOR INSERT WITH CHECK (jinji_viewer_may_edit_group(group_id));
CREATE POLICY org_group_version_update ON org_group_version FOR UPDATE
    USING (jinji_viewer_may_edit_group(group_id)) WITH CHECK (jinji_viewer_may_edit_group(group_id));

-- メンバー: 参照は本人の行とグループが見える人。追加は編集できる人が本人の名義で（管理対象のグループのオーナーの指名は管理者だけ）。
-- 記録期間を閉じるのは編集できる人と、管理対象でないグループの本人（自分を外す）
CREATE POLICY org_group_member_select ON org_group_member FOR SELECT USING (
    person_id = (SELECT jinji_viewer_person_id())
    OR EXISTS (SELECT 1 FROM org_group g WHERE g.id = group_id));
CREATE POLICY org_group_member_insert ON org_group_member FOR INSERT WITH CHECK (
    -- 加えた人は本人。役割の変更・期間の短縮で写す行は、元の行の加えた人のまま
    (added_by_person_id = (SELECT jinji_viewer_person_id())
     OR jinji_group_member_closed_now(group_id, person_id, role, added_by_person_id))
    AND (jinji_viewer_may_edit_group(group_id)
         -- 自分を外すときの、期間を縮めた行（管理対象でないグループの本人）
         OR (person_id = (SELECT jinji_viewer_person_id())
             AND EXISTS (SELECT 1 FROM org_group g WHERE g.id = group_id AND NOT g.managed)
             AND jinji_group_member_closed_now(group_id, person_id, role, added_by_person_id)))
    AND (role <> 'owner'
         OR jinji_viewer_is_group_admin(group_id)
         OR jinji_group_member_closed_now(group_id, person_id, role, added_by_person_id)
         OR EXISTS (SELECT 1 FROM org_group g WHERE g.id = group_id AND (NOT g.managed OR g.created_at = pg_catalog.now()))));
CREATE POLICY org_group_member_update ON org_group_member FOR UPDATE
    USING (jinji_viewer_may_edit_group(group_id)
           OR (person_id = (SELECT jinji_viewer_person_id()) AND EXISTS (SELECT 1 FROM org_group g WHERE g.id = group_id AND NOT g.managed)))
    WITH CHECK (true);

-- 変更フィード: グループの変更のトリガーが、書き込んだ閲覧者で記録する
CREATE POLICY change_event_insert_group ON change_event FOR INSERT WITH CHECK (
    entity_type = 'group' AND coalesce((SELECT jinji_viewer_kind()) = 'user', false));

-- ---------------------------------------------------------------------------
-- 7. アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT ON org_group, org_group_version, org_group_member TO jinji_app;
GRANT UPDATE (visibility, managed, member_ids) ON org_group TO jinji_app;
GRANT UPDATE (recorded_period) ON org_group_version, org_group_member TO jinji_app;
