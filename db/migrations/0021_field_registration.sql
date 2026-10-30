-- 0021: 現場・IT 部門の直接登録（ADR-0013 の段 2。登録・同意・取り消し・公開側の表への書き込みの範囲）
--
-- 位置づけ
-- - 直接登録は「起案と反映を同じ人が同じトランザクションで行う変更提案」として記録する（ADR-0013 決定 5）。
--   登録の単位（field_registration）が変更提案（change_proposal。origin は field_registration_id の有無）をまとめ、
--   反映は取込の承認と同じ経路（二つの時間軸・旧値の照合・段の検査・発令・変更フィード）を通る
-- - 職務分掌の CHECK（反映者 ≠ 起案者）は直接登録の提案だけ外す。直接登録は承認を経ないため（決定 1）
--
-- 状態
-- - draft（AI エージェントの下書き・確定前）→ applied（反映済み）| pending_consent（相手側の同意待ち）| discarded | expired
-- - pending_consent → applied（同意で反映）| discarded | expired
-- - applied → withdrawn（有効日の前の取り消し）
--
-- 公開側の表への書き込み（RLS）
-- - 人事管理者は従来どおり（取込の承認）。それ以外は「人」が、管理元が hr でない領域に、範囲の中でだけ書ける
-- - 範囲の判定はトランザクションの開始時点の記録で、業務日付に組織長・担当である組織の配下（jinji_field_covers_path）。
--   登録が自分を組織長にする・自分の組織を別の組織の下に移すといった変更を、その変更自体で正当化できないようにするため
-- - 同意で反映するときは、同意者の範囲と登録者の範囲の和で判定する（GUC jinji.field_registration に、同意者が同意した登録を指す）
-- - 権限を生む変更（組織長の任免・上位組織の付け替え）は一つ上の組織の範囲を要する（トリガー jinji_guard_field_org_version）
-- - 人の版で変えてよい列は、管理元が hr でない領域の列だけ（拠点・リモートワーク・内線・社用携帯。トリガー jinji_guard_field_person_version）
-- - 有効日の規則（今日以降・遡及は org_admin と人事管理者）・人数の上限はアプリが検査する
--
-- 将来の行の秘匿（決定 6）
-- - 直接登録が書いた行は、反映のトランザクションの中でトリガーが field_registration_row に記録する（GUC jinji.field_registration）
-- - 未来日の行（内示と同じに人事管理者以外に見せない行）は、その登録の関係者（field_registration_viewer: 登録者・同意者・取り消した人）と
--   org_admin にだけ見せる。対象の本人には見せない
-- - field_registration_viewer の参照の方針は範囲の判定を使わない（公開側の表の参照の方針から読まれるので、範囲の判定 → 組織の版 → … の再帰を作らない）

-- ---------------------------------------------------------------------------
-- 1. 登録
-- ---------------------------------------------------------------------------

CREATE TABLE field_registration (
    id                        uuid        PRIMARY KEY,
    status                    text        NOT NULL DEFAULT 'draft',
    effective_date            date        NOT NULL,
    note                      text,
    registered_by_person_id   uuid        NOT NULL REFERENCES person (id),
    registered_via            text        NOT NULL,
    registered_via_client_id  text,
    created_at                timestamptz NOT NULL DEFAULT now(),
    expires_at                timestamptz NOT NULL,
    org_unit_ids              uuid[]      NOT NULL DEFAULT '{}',
    person_ids                uuid[]      NOT NULL DEFAULT '{}',
    consent_org_unit_ids      uuid[]      NOT NULL DEFAULT '{}',
    consented_by_person_id    uuid        REFERENCES person (id),
    consented_at              timestamptz,
    applied_by_person_id      uuid        REFERENCES person (id),
    applied_at                timestamptz,
    withdrawn_by_person_id    uuid        REFERENCES person (id),
    withdrawn_at              timestamptz,
    closed_by_person_id       uuid        REFERENCES person (id),
    closed_at                 timestamptz,
    -- 下書き / 同意待ち / 反映済み / 取り消し済み / 破棄 / 期限切れ
    CONSTRAINT field_registration_status_check CHECK (
        status IN ('draft', 'pending_consent', 'applied', 'withdrawn', 'discarded', 'expired')),
    -- 人（UI）/ AI エージェント（委任元の名義の下書き）
    CONSTRAINT field_registration_via_check CHECK (registered_via IN ('user', 'agent')),
    CONSTRAINT field_registration_client_check CHECK (
        registered_via_client_id IS NULL OR (btrim(registered_via_client_id) <> '' AND char_length(registered_via_client_id) <= 2048)),
    -- 理由のメモ（任意）。制御文字（改行を含む）は持たない
    CONSTRAINT field_registration_note_check CHECK (
        note IS NULL OR (btrim(note) <> '' AND char_length(note) <= 500 AND note !~ '[[:cntrl:]]')),
    CONSTRAINT field_registration_consent_check CHECK (
        (consented_by_person_id IS NULL) = (consented_at IS NULL)
        AND (consented_by_person_id IS NULL OR status IN ('pending_consent', 'applied', 'withdrawn'))),
    CONSTRAINT field_registration_applied_check CHECK (
        (applied_by_person_id IS NULL) = (applied_at IS NULL)
        AND (applied_at IS NOT NULL) = (status IN ('applied', 'withdrawn'))),
    CONSTRAINT field_registration_withdrawn_check CHECK (
        (withdrawn_by_person_id IS NULL) = (withdrawn_at IS NULL)
        AND (withdrawn_at IS NOT NULL) = (status = 'withdrawn')),
    CONSTRAINT field_registration_closed_check CHECK (
        (closed_at IS NOT NULL) = (status IN ('discarded', 'expired'))
        AND (closed_by_person_id IS NULL OR status = 'discarded'))
);
COMMENT ON TABLE field_registration IS
    '現場・IT 部門の直接登録（ADR-0013）。変更提案（change_proposal.field_registration_id）をまとめ、登録者（か同意者）が承認を経ずに反映する';
COMMENT ON COLUMN field_registration.effective_date IS '有効日（今日以降。遡及は org_admin・人事管理者だけ。アプリが検査する）';
COMMENT ON COLUMN field_registration.note IS '登録の理由のメモ（任意。人事データの値を書かない）';
COMMENT ON COLUMN field_registration.registered_via IS 'user（人）/ agent（AI エージェントが委任元の名義で作った下書き。反映は委任元の確定だけ）';
COMMENT ON COLUMN field_registration.expires_at IS '下書き・同意待ちの期限（過ぎたら期限切れにする）';
COMMENT ON COLUMN field_registration.org_unit_ids IS '登録が関係する組織（範囲を持つ人の一覧の判定に使う）';
COMMENT ON COLUMN field_registration.person_ids IS '登録で変える人（人数の上限・監査）';
COMMENT ON COLUMN field_registration.consent_org_unit_ids IS '登録者の範囲の外で、相手側の同意が要る組織';
CREATE INDEX field_registration_registered_by_idx ON field_registration (registered_by_person_id, created_at);
CREATE INDEX field_registration_org_unit_ids_idx ON field_registration USING gin (org_unit_ids);

CREATE FUNCTION jinji_guard_field_registration() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    workflow CONSTANT text[] := ARRAY[
        'status', 'consented_by_person_id', 'consented_at', 'applied_by_person_id', 'applied_at',
        'withdrawn_by_person_id', 'withdrawn_at', 'closed_by_person_id', 'closed_at'];
BEGIN
    IF (to_jsonb(NEW) - workflow) IS DISTINCT FROM (to_jsonb(OLD) - workflow) THEN
        RAISE EXCEPTION 'field_registration: 登録の中身は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'field_registration_immutable';
    END IF;

    -- 一度記録した同意者は変えない
    IF OLD.consented_by_person_id IS NOT NULL
       AND (NEW.consented_by_person_id, NEW.consented_at) IS DISTINCT FROM (OLD.consented_by_person_id, OLD.consented_at) THEN
        RAISE EXCEPTION 'field_registration: 同意者は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'field_registration_transition';
    END IF;

    IF (OLD.status = 'draft' AND NEW.status IN ('pending_consent', 'applied', 'discarded', 'expired'))
       OR (OLD.status = 'pending_consent' AND NEW.status IN ('pending_consent', 'applied', 'discarded', 'expired'))
       OR (OLD.status = 'applied' AND NEW.status = 'withdrawn') THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'field_registration: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'field_registration_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_field_registration() IS 'field_registration の状態遷移と中身の不変を守るトリガー関数';

CREATE TRIGGER field_registration_guard BEFORE UPDATE ON field_registration
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_field_registration();

-- 登録の関係者（登録者・同意者・取り消した人）。公開側の未来日の行を見せる相手
CREATE TABLE field_registration_viewer (
    registration_id  uuid NOT NULL REFERENCES field_registration (id),
    person_id        uuid NOT NULL REFERENCES person (id),
    PRIMARY KEY (registration_id, person_id)
);
COMMENT ON TABLE field_registration_viewer IS '直接登録の関係者（登録者・同意者・取り消した人）。登録が書いた未来日の行を、この人たちと org_admin にだけ見せる';
CREATE INDEX field_registration_viewer_person_idx ON field_registration_viewer (person_id);

-- 登録が公開側に書いた行（反映のトランザクションでトリガーが記録する）。取り消しと、未来日の行の秘匿の判定に使う
CREATE TABLE field_registration_row (
    registration_id  uuid NOT NULL REFERENCES field_registration (id),
    table_name       text NOT NULL,
    row_id           uuid NOT NULL,
    operation        text NOT NULL,
    PRIMARY KEY (registration_id, row_id, operation),
    CONSTRAINT field_registration_row_table_check CHECK (
        table_name IN ('org_unit', 'org_unit_version', 'person_version', 'assignment', 'personnel_action')),
    -- 追加した行 / 記録期間を閉じた行
    CONSTRAINT field_registration_row_operation_check CHECK (operation IN ('insert', 'close'))
);
COMMENT ON TABLE field_registration_row IS '直接登録が公開側に追加した行・記録期間を閉じた行の ID（値は持たない）。取り消しと未来日の行の秘匿に使う';
CREATE INDEX field_registration_row_row_idx ON field_registration_row (row_id);

-- ---------------------------------------------------------------------------
-- 2. 変更提案を直接登録にも使う
-- ---------------------------------------------------------------------------

ALTER TABLE change_proposal ADD COLUMN field_registration_id uuid REFERENCES field_registration (id);
ALTER TABLE change_proposal ALTER COLUMN import_batch_id DROP NOT NULL;
-- 取込の提案か直接登録の提案のどちらか（直接登録は取込単位を持たない）
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_origin_check CHECK (
    (import_batch_id IS NULL) <> (field_registration_id IS NULL));
-- 職務分掌（反映者 ≠ 起案者）は取込の提案だけ。直接登録は承認を経ない（ADR-0013 決定 1・5）
ALTER TABLE change_proposal DROP CONSTRAINT change_proposal_segregation_of_duties;
ALTER TABLE change_proposal ADD CONSTRAINT change_proposal_segregation_of_duties CHECK (
    status <> 'applied' OR decided_by_person_id IS DISTINCT FROM proposed_by_person_id OR field_registration_id IS NOT NULL);
COMMENT ON COLUMN change_proposal.field_registration_id IS '直接登録の提案なら、その登録（取込単位は持たない。ADR-0013）';
CREATE INDEX change_proposal_field_registration_idx ON change_proposal (field_registration_id) WHERE field_registration_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- 3. 範囲の判定（書き込みの方針で使う）
-- ---------------------------------------------------------------------------

-- 同意で反映中の登録の登録者（GUC jinji.field_registration が指す同意待ちの登録に、閲覧者が同意者として記録されているときだけ）
CREATE FUNCTION jinji_field_consent_registrant() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT r.registered_by_person_id FROM public.field_registration r
    WHERE r.id = NULLIF(pg_catalog.current_setting('jinji.field_registration', true), '')::uuid
      AND r.status = 'pending_consent'
      AND r.consented_by_person_id = public.jinji_viewer_person_id()
$$;
COMMENT ON FUNCTION jinji_field_consent_registrant() IS 'RLS: 同意で反映中の登録の登録者（GUC jinji.field_registration。閲覧者が同意者のときだけ）';

-- 直接登録が書いてよい領域か（「人」の閲覧者で、領域のいずれかの管理元が hr でない）
CREATE FUNCTION jinji_field_may_write(domains text[]) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce(public.jinji_viewer_kind() = 'user', false)
       AND EXISTS (SELECT 1 FROM pg_catalog.unnest(domains) d WHERE public.jinji_field_domain_mode(d) <> 'hr')
$$;
COMMENT ON FUNCTION jinji_field_may_write(text[]) IS 'RLS: 閲覧者が「人」で、領域のいずれかの管理元が hr でない（直接登録で書ける）か';

-- パスの組織が範囲に入るか。org_admin は全社。それ以外は、トランザクションの開始時点の記録で業務日付に有効な祖先（自分を含む）の
-- いずれかで、閲覧者（同意で反映中なら登録者も）が組織長か有効な担当であること
CREATE FUNCTION jinji_field_covers_path(target_path public.ltree) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_org_admin() OR EXISTS (
        SELECT 1 FROM public.org_unit_version s
        WHERE target_path OPERATOR(public.<@) s.path
          AND s.valid_period @> public.jinji_business_date()
          AND lower(s.recorded_period) < pg_catalog.now()
          AND (upper_inf(s.recorded_period) OR upper(s.recorded_period) = pg_catalog.now())
          AND (s.head_person_id IN (public.jinji_viewer_person_id(), public.jinji_field_consent_registrant())
               OR EXISTS (
                   SELECT 1 FROM public.org_editor_grant g
                   WHERE g.org_unit_id = s.org_unit_id
                     AND g.person_id IN (public.jinji_viewer_person_id(), public.jinji_field_consent_registrant())
                     AND g.revoked_at IS NULL
                     AND g.valid_period @> public.jinji_business_date())))
$$;
COMMENT ON FUNCTION jinji_field_covers_path(public.ltree) IS
    'RLS: パスの組織が直接登録の範囲に入るか（トランザクションの開始時点の記録・業務日付の組織長と担当。同意で反映中は登録者の範囲も）';

-- 組織がその日に範囲に入るか（その日の組織の版のパスで判定する。同じトランザクションで書いた版も含む）
CREATE FUNCTION jinji_field_covers_unit(target uuid, on_date date) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_org_admin() OR EXISTS (
        SELECT 1 FROM public.org_unit_version t
        WHERE t.org_unit_id = target
          AND t.valid_period @> on_date
          AND (upper_inf(t.recorded_period) OR upper(t.recorded_period) = pg_catalog.now())
          AND public.jinji_field_covers_path(t.path))
$$;
COMMENT ON FUNCTION jinji_field_covers_unit(uuid, date) IS 'RLS: 組織がその日に直接登録の範囲に入るか';

-- 人がその日に範囲に入るか（その日の主務の組織で判定する）
CREATE FUNCTION jinji_field_covers_person(target uuid, on_date date) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_org_admin() OR EXISTS (
        SELECT 1 FROM public.assignment a
        WHERE a.person_id = target
          AND a.assignment_type = 'primary'
          AND a.valid_period @> on_date
          AND (upper_inf(a.recorded_period) OR upper(a.recorded_period) = pg_catalog.now())
          AND public.jinji_field_covers_unit(a.org_unit_id, on_date))
$$;
COMMENT ON FUNCTION jinji_field_covers_person(uuid, date) IS 'RLS: 人がその日に直接登録の範囲に入るか（主務の組織）';

-- 閲覧者が、直接登録が書いた行（未来日の行を含む）を見てよいか（登録の関係者と org_admin）
CREATE FUNCTION jinji_viewer_sees_field_row(target uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT EXISTS (
        SELECT 1 FROM public.field_registration_row r
        WHERE r.row_id = target
          AND (public.jinji_viewer_is_org_admin()
               OR EXISTS (
                   SELECT 1 FROM public.field_registration_viewer v
                   WHERE v.registration_id = r.registration_id AND v.person_id = public.jinji_viewer_person_id())))
$$;
COMMENT ON FUNCTION jinji_viewer_sees_field_row(uuid) IS 'RLS: 直接登録が書いた行を、その登録の関係者（登録者・同意者・取り消した人）と org_admin に見せる';

-- ---------------------------------------------------------------------------
-- 4. 書いた行の記録・権限を生む変更と人の版の列の検査
-- ---------------------------------------------------------------------------

CREATE FUNCTION jinji_capture_field_registration_row() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    registration uuid := NULLIF(pg_catalog.current_setting('jinji.field_registration', true), '')::uuid;
BEGIN
    IF registration IS NOT NULL THEN
        INSERT INTO field_registration_row (registration_id, table_name, row_id, operation)
        VALUES (registration, TG_TABLE_NAME, NEW.id, CASE TG_OP WHEN 'INSERT' THEN 'insert' ELSE 'close' END)
        ON CONFLICT DO NOTHING;
    END IF;
    RETURN NULL;
END
$$;
COMMENT ON FUNCTION jinji_capture_field_registration_row() IS '直接登録の反映（GUC jinji.field_registration）で公開側に追加した行・記録期間を閉じた行を記録するトリガー関数';

CREATE TRIGGER org_unit_capture_field_registration AFTER INSERT ON org_unit
    FOR EACH ROW EXECUTE FUNCTION jinji_capture_field_registration_row();
CREATE TRIGGER org_unit_version_capture_field_registration AFTER INSERT OR UPDATE OF recorded_period ON org_unit_version
    FOR EACH ROW EXECUTE FUNCTION jinji_capture_field_registration_row();
CREATE TRIGGER person_version_capture_field_registration AFTER INSERT OR UPDATE OF recorded_period ON person_version
    FOR EACH ROW EXECUTE FUNCTION jinji_capture_field_registration_row();
CREATE TRIGGER assignment_capture_field_registration AFTER INSERT OR UPDATE OF recorded_period ON assignment
    FOR EACH ROW EXECUTE FUNCTION jinji_capture_field_registration_row();
CREATE TRIGGER personnel_action_capture_field_registration AFTER INSERT OR UPDATE OF recorded_period ON personnel_action
    FOR EACH ROW EXECUTE FUNCTION jinji_capture_field_registration_row();

-- 組織の版: 人事管理者・org_admin 以外の「人」が組織長か上位組織を変えるなら、一つ上の組織の範囲を要する（付け替えは元の上位組織も）。
-- 比べる相手は、同じトランザクションで記録期間を閉じた、開始時点の版
CREATE FUNCTION jinji_guard_field_org_version() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    prev record;
BEGIN
    IF public.jinji_viewer_is_hr_admin() OR public.jinji_viewer_is_org_admin() OR coalesce(public.jinji_viewer_kind() <> 'user', true) THEN
        RETURN NEW;
    END IF;

    FOR prev IN
        SELECT parent_org_unit_id, head_person_id, path FROM org_unit_version
        WHERE org_unit_id = NEW.org_unit_id
          AND lower(recorded_period) < pg_catalog.now() AND upper(recorded_period) = pg_catalog.now()
          AND valid_period && NEW.valid_period
    LOOP
        IF prev.head_person_id IS DISTINCT FROM NEW.head_person_id OR prev.parent_org_unit_id IS DISTINCT FROM NEW.parent_org_unit_id THEN
            IF public.nlevel(NEW.path) < 2
               OR NOT public.jinji_field_covers_path(public.subpath(NEW.path, 0, public.nlevel(NEW.path) - 1))
               OR (prev.parent_org_unit_id IS DISTINCT FROM NEW.parent_org_unit_id
                   AND (public.nlevel(prev.path) < 2
                        OR NOT public.jinji_field_covers_path(public.subpath(prev.path, 0, public.nlevel(prev.path) - 1)))) THEN
                RAISE EXCEPTION 'org_unit_version: 組織長・上位組織の変更は、一つ上の組織の範囲を要します（組織 %）', NEW.org_unit_id
                    USING ERRCODE = 'insufficient_privilege', CONSTRAINT = 'field_registration_parent_scope';
            END IF;
        END IF;
    END LOOP;
    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_field_org_version() IS '直接登録: 組織長・上位組織の変更に一つ上の組織の範囲を求めるトリガー関数（人事管理者・org_admin は対象外）';

CREATE TRIGGER org_unit_version_guard_field BEFORE INSERT ON org_unit_version
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_field_org_version();

-- 人の版: 人事管理者以外の「人」は、同じトランザクションで記録期間を閉じた版を写し、管理元が hr でない領域の列だけを変えられる
-- （氏名・在籍・雇用区分などは直接登録の対象外。ADR-0013 決定 2）
CREATE FUNCTION jinji_guard_field_person_version() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    fixed text[] := ARRAY['id', 'valid_period', 'recorded_period'];
    prev record;
    found boolean := false;
BEGIN
    IF public.jinji_viewer_is_hr_admin() OR coalesce(public.jinji_viewer_kind() <> 'user', true) THEN
        RETURN NEW;
    END IF;

    IF public.jinji_field_domain_mode('person_site') <> 'hr' THEN
        fixed := fixed || ARRAY['site_id', 'remote_work'];
    END IF;
    IF public.jinji_field_domain_mode('person_contact') <> 'hr' THEN
        fixed := fixed || ARRAY['extension_number', 'work_mobile_phone'];
    END IF;

    FOR prev IN
        SELECT to_jsonb(p) AS row FROM person_version p
        WHERE p.person_id = NEW.person_id
          AND lower(p.recorded_period) < pg_catalog.now() AND upper(p.recorded_period) = pg_catalog.now()
          AND p.valid_period && NEW.valid_period
    LOOP
        found := true;
        IF (prev.row - fixed) IS DISTINCT FROM (to_jsonb(NEW) - fixed) THEN
            RAISE EXCEPTION 'person_version: 直接登録で変えられない列を変えています（人 %）', NEW.person_id
                USING ERRCODE = 'insufficient_privilege', CONSTRAINT = 'field_registration_person_columns';
        END IF;
    END LOOP;

    IF NOT found THEN
        RAISE EXCEPTION 'person_version: 直接登録では人の版を新しく作れません（人 %）', NEW.person_id
            USING ERRCODE = 'insufficient_privilege', CONSTRAINT = 'field_registration_person_columns';
    END IF;
    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_field_person_version() IS '直接登録: 人の版で変えてよい列を、管理元が hr でない領域の列に限るトリガー関数（人事管理者は対象外）';

CREATE TRIGGER person_version_guard_field BEFORE INSERT ON person_version
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_field_person_version();

-- ---------------------------------------------------------------------------
-- 5. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

ALTER TABLE field_registration ENABLE ROW LEVEL SECURITY;
ALTER TABLE field_registration FORCE ROW LEVEL SECURITY;
ALTER TABLE field_registration_viewer ENABLE ROW LEVEL SECURITY;
ALTER TABLE field_registration_viewer FORCE ROW LEVEL SECURITY;
ALTER TABLE field_registration_row ENABLE ROW LEVEL SECURITY;
ALTER TABLE field_registration_row FORCE ROW LEVEL SECURITY;

-- 登録: 参照は人事管理者・org_admin・登録者・同意者と、関係する組織の範囲を持つ人（AI エージェントは委任元）。
-- 作成は人か AI エージェントが、本人（委任元）の名義の下書きとしてだけ
CREATE POLICY field_registration_select ON field_registration FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false)
        AND ((SELECT jinji_viewer_is_org_admin())
             OR registered_by_person_id = (SELECT jinji_viewer_person_id())
             OR consented_by_person_id = (SELECT jinji_viewer_person_id())
             OR EXISTS (SELECT 1 FROM pg_catalog.unnest(org_unit_ids) u
                        WHERE jinji_viewer_scope_covers(u, (SELECT jinji_business_date()), true)))));
CREATE POLICY field_registration_insert ON field_registration FOR INSERT WITH CHECK (
    status = 'draft'
    AND coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false)
    AND registered_via = (SELECT jinji_viewer_kind())
    AND registered_by_person_id = (SELECT jinji_viewer_person_id()));
-- 状態の更新は「人」だけ（期限切れは jin-ji 自身も）。誰が何をしたかは閲覧者本人としてしか記録できない
CREATE POLICY field_registration_update ON field_registration FOR UPDATE
    USING (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'system'), false))
    WITH CHECK (
        (coalesce((SELECT jinji_viewer_kind()) = 'user', false) AND (
            (status = 'pending_consent' AND consented_by_person_id IS NULL AND registered_by_person_id = (SELECT jinji_viewer_person_id()))
            OR (status = 'pending_consent' AND consented_by_person_id = (SELECT jinji_viewer_person_id()))
            OR (status = 'applied' AND applied_by_person_id = (SELECT jinji_viewer_person_id())
                AND (registered_by_person_id = applied_by_person_id OR consented_by_person_id = applied_by_person_id))
            OR (status = 'withdrawn' AND withdrawn_by_person_id = (SELECT jinji_viewer_person_id()))
            OR (status = 'discarded' AND closed_by_person_id = (SELECT jinji_viewer_person_id()))))
        OR (status = 'expired' AND closed_by_person_id IS NULL));

-- 関係者: 参照は本人の行と人事管理者・org_admin。追加は本人が、見えている登録にだけ
CREATE POLICY field_registration_viewer_select ON field_registration_viewer FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR (SELECT jinji_viewer_is_org_admin())
    OR person_id = (SELECT jinji_viewer_person_id()));
CREATE POLICY field_registration_viewer_insert ON field_registration_viewer FOR INSERT WITH CHECK (
    coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false)
    AND person_id = (SELECT jinji_viewer_person_id())
    AND EXISTS (SELECT 1 FROM field_registration r WHERE r.id = registration_id));

-- 書いた行: ID だけなので閲覧者なら参照できる（公開側の参照の方針から読む）。追加は反映する人（登録者・同意者）だけ
CREATE POLICY field_registration_row_select ON field_registration_row FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY field_registration_row_insert ON field_registration_row FOR INSERT WITH CHECK (
    coalesce((SELECT jinji_viewer_kind()) = 'user', false)
    AND EXISTS (
        SELECT 1 FROM field_registration r
        WHERE r.id = registration_id
          AND (r.registered_by_person_id = (SELECT jinji_viewer_person_id())
               OR r.consented_by_person_id = (SELECT jinji_viewer_person_id())
               OR r.withdrawn_by_person_id = (SELECT jinji_viewer_person_id()))));

-- 変更提案（直接登録の分）: 参照は登録が見える人。追加は登録者が下書きの登録にだけ（起案者は本人）。
-- 反映・却下は登録者か同意者の「人」（org_admin・人事管理者は下書きの破棄も）で、決めた人は本人
CREATE POLICY change_proposal_select_field ON change_proposal FOR SELECT USING (
    field_registration_id IS NOT NULL
    AND EXISTS (SELECT 1 FROM field_registration r WHERE r.id = field_registration_id));
CREATE POLICY change_proposal_insert_field ON change_proposal FOR INSERT WITH CHECK (
    field_registration_id IS NOT NULL
    AND status = 'proposed'
    AND proposed_by_person_id = (SELECT jinji_viewer_person_id())
    AND EXISTS (
        SELECT 1 FROM field_registration r
        WHERE r.id = field_registration_id AND r.status = 'draft' AND r.registered_by_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY change_proposal_update_field ON change_proposal FOR UPDATE
    USING (field_registration_id IS NOT NULL AND coalesce((SELECT jinji_viewer_kind()) = 'user', false))
    WITH CHECK (
        field_registration_id IS NOT NULL
        AND decided_by_person_id = (SELECT jinji_viewer_person_id())
        AND ((SELECT jinji_viewer_is_org_admin())
             OR EXISTS (
                 SELECT 1 FROM field_registration r
                 WHERE r.id = field_registration_id
                   AND (r.registered_by_person_id = (SELECT jinji_viewer_person_id())
                        OR r.consented_by_person_id = (SELECT jinji_viewer_person_id())))));

-- 公開側の表: 直接登録の書き込み（人事管理者の方針と OR で効く）
CREATE POLICY org_unit_insert_field ON org_unit FOR INSERT WITH CHECK (jinji_field_may_write(ARRAY['org_structure']));

CREATE POLICY org_unit_version_insert_field ON org_unit_version FOR INSERT WITH CHECK (
    jinji_field_may_write(ARRAY['org_structure', 'org_site']) AND jinji_field_covers_path(path));
CREATE POLICY org_unit_version_update_field ON org_unit_version FOR UPDATE
    USING (jinji_field_may_write(ARRAY['org_structure', 'org_site']) AND jinji_field_covers_path(path))
    WITH CHECK (jinji_field_may_write(ARRAY['org_structure', 'org_site']) AND jinji_field_covers_path(path));

CREATE POLICY person_version_insert_field ON person_version FOR INSERT WITH CHECK (
    jinji_field_may_write(ARRAY['person_site', 'person_contact'])
    AND jinji_field_covers_person(person_id, greatest(lower(valid_period), (SELECT jinji_business_date()))));
CREATE POLICY person_version_update_field ON person_version FOR UPDATE
    USING (jinji_field_may_write(ARRAY['person_site', 'person_contact'])
           AND jinji_field_covers_person(person_id, greatest(lower(valid_period), (SELECT jinji_business_date()))))
    WITH CHECK (jinji_field_may_write(ARRAY['person_site', 'person_contact'])
                AND jinji_field_covers_person(person_id, greatest(lower(valid_period), (SELECT jinji_business_date()))));

CREATE POLICY assignment_insert_field ON assignment FOR INSERT WITH CHECK (
    jinji_field_may_write(ARRAY['assignment'])
    AND jinji_field_covers_unit(org_unit_id, greatest(lower(valid_period), (SELECT jinji_business_date()))));
CREATE POLICY assignment_update_field ON assignment FOR UPDATE
    USING (jinji_field_may_write(ARRAY['assignment'])
           AND jinji_field_covers_unit(org_unit_id, greatest(lower(valid_period), (SELECT jinji_business_date()))))
    WITH CHECK (jinji_field_may_write(ARRAY['assignment'])
                AND jinji_field_covers_unit(org_unit_id, greatest(lower(valid_period), (SELECT jinji_business_date()))));

CREATE POLICY personnel_action_insert_field ON personnel_action FOR INSERT WITH CHECK (
    jinji_field_may_write(ARRAY['assignment'])
    AND CASE WHEN org_unit_id IS NULL
             THEN jinji_field_covers_person(person_id, greatest(effective_date, (SELECT jinji_business_date())))
             ELSE jinji_field_covers_unit(org_unit_id, greatest(effective_date, (SELECT jinji_business_date())))
        END);
CREATE POLICY personnel_action_update_field ON personnel_action FOR UPDATE
    USING (jinji_field_may_write(ARRAY['assignment'])
           AND CASE WHEN org_unit_id IS NULL
                    THEN jinji_field_covers_person(person_id, greatest(effective_date, (SELECT jinji_business_date())))
                    ELSE jinji_field_covers_unit(org_unit_id, greatest(effective_date, (SELECT jinji_business_date())))
               END)
    WITH CHECK (jinji_field_may_write(ARRAY['assignment']));

-- 変更フィード: 公開側の行の追加・記録期間を閉じたトリガーが、書き込んだ閲覧者で記録する
CREATE POLICY change_event_insert_field ON change_event FOR INSERT WITH CHECK (
    jinji_field_may_write(ARRAY['org_structure', 'org_site', 'assignment', 'person_site', 'person_contact']));

-- 公開側の表の参照: 直接登録が書いた行（未来日の行を含む）を、その登録の関係者と org_admin に見せる。
-- 既存の条件を先に評価させるため、方針を作り直して OR でつなぐ（既存の条件で見える行には関数を呼ばない）
DROP POLICY personnel_action_select ON personnel_action;
CREATE POLICY personnel_action_select ON personnel_action FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND effective_date <= (SELECT jinji_viewer_horizon()))
    OR (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false) AND jinji_viewer_sees_field_row(id)));

-- 人の版は、このトランザクションで記録期間を閉じた行も「人」に見せる（閉じる直前まで見えていた行。UPDATE の新しい行は参照の方針も満たす必要があるため）
DROP POLICY person_version_select ON person_version;
CREATE POLICY person_version_select ON person_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND upper_inf(recorded_period)
        AND valid_period && daterange((SELECT jinji_business_date()), (SELECT jinji_viewer_horizon()), '[]'))
    OR (coalesce((SELECT jinji_viewer_kind()) = 'user', false) AND upper(recorded_period) = pg_catalog.now())
    OR (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false) AND jinji_viewer_sees_field_row(id)));

DROP POLICY org_unit_version_select ON org_unit_version;
CREATE POLICY org_unit_version_select ON org_unit_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon()))
    OR (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false) AND jinji_viewer_sees_field_row(id)));

DROP POLICY assignment_select ON assignment;
CREATE POLICY assignment_select ON assignment FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon()))
    OR (coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent'), false) AND jinji_viewer_sees_field_row(id)));

-- ---------------------------------------------------------------------------
-- 6. アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT ON field_registration, field_registration_viewer, field_registration_row TO jinji_app;
GRANT UPDATE (
    status, consented_by_person_id, consented_at, applied_by_person_id, applied_at,
    withdrawn_by_person_id, withdrawn_at, closed_by_person_id, closed_at) ON field_registration TO jinji_app;
