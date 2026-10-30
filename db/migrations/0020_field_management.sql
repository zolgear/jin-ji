-- 0020: 現場・IT 部門の直接登録の土台（ADR-0013 の段 1。管理元の設定・担当の指名・範囲の判定・組織管理者のロール）
--
-- 位置づけ
-- - 公開側（組織・拠点・所属・人の拠点・連絡先）を変える入口に、取込の承認のほか「現場・IT 部門の直接登録」を加える（ADR-0013）。
--   この段では、直接登録を許す領域（管理元）・登録できる人（組織長・指名した担当・org_admin）・範囲の判定だけを入れる。
--   公開側の表への書き込みの方針（RLS）は、直接登録の段で範囲の判定を使って広げる（この段では変えない）
--
-- 内容
-- 1. ロール org_admin（組織管理者。IT 部門を想定）。全社の組織と人の直接登録・担当の指名・管理元の起案ができる。
--    人事管理者と違い、氏名の履歴・取込の作業領域・監査ログ・内示は見えない（このロールで広がるのは、この段の表と範囲の判定だけ）
-- 2. 管理元の設定（field_domain_setting）。領域ごとに hr（既定。取込だけ）/ field（直接登録だけ）/ both（両方）。
--    書き込める人を変える設定なので、先行提供の日数と同じく起案 → 別人の承認で反映する（職務分掌。ADR-0008 決定 7）
-- 3. 担当の指名（org_editor_grant）。組織に結び付け、期間を持つ（指名者が交代しても残り、後任が取り消せる）。
--    指名できるのは、組織長として範囲を持つ人（担当としての範囲では指名できない＝再指名の禁止）・org_admin・人事管理者の「人」。自分は指名できない
-- 4. 範囲の判定 jinji_viewer_scope_covers(組織, 日付, 担当を含めるか)。
--    人事管理者・org_admin は全社。それ以外は、その日に自分が組織長である組織か、自分が担当に指名されている組織の、配下（自分を含む）
--
-- 他の行を読む RLS について
-- - jinji_viewer_scope_covers() は org_unit_version と org_editor_grant を閲覧者の権限で読む。org_unit_version は将来の版を人事管理者以外に
--   見せないので、将来の日付の範囲は（人事管理者・org_admin を除き）判定できず false になる（広げない側）
-- - org_editor_grant の方針は jinji_viewer_scope_covers() を書き込みの検査（INSERT / UPDATE の WITH CHECK）にだけ使い、参照の方針では使わない
--   （参照の方針から自分の表を読む関数を呼ぶと、PostgreSQL は方針の再帰として拒否する）

-- ---------------------------------------------------------------------------
-- 1. ロール
-- ---------------------------------------------------------------------------

ALTER TABLE role_grant DROP CONSTRAINT role_grant_role_check;
-- 既知のロール（JinJi.Policy の Roles.Known と一致させる）。org_admin = 組織管理者（IT 部門。ADR-0013）
ALTER TABLE role_grant ADD CONSTRAINT role_grant_role_check CHECK (role IN ('hr_admin', 'hr_corrector', 'org_admin'));

CREATE FUNCTION jinji_viewer_is_org_admin() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_set()
       AND 'org_admin' = ANY (pg_catalog.string_to_array(coalesce(pg_catalog.current_setting('jinji.viewer_roles', true), ''), ','))
$$;
COMMENT ON FUNCTION jinji_viewer_is_org_admin() IS 'RLS: 閲覧者が org_admin（組織管理者）ロールを持つか（GUC jinji.viewer_roles。AI エージェントは委任元のロール）';

-- 管理元の起案・承認、担当の指名ができる「人」（AI エージェントは不可。ADR-0013 決定 4）
CREATE FUNCTION jinji_viewer_is_field_admin_user() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce(public.jinji_viewer_kind() = 'user', false)
       AND (public.jinji_viewer_is_hr_admin() OR public.jinji_viewer_is_org_admin())
$$;
COMMENT ON FUNCTION jinji_viewer_is_field_admin_user() IS 'RLS: 人事管理者か org_admin の「人」（UI でログインした人。AI エージェント・社内システム・jin-ji 自身は含まない）';

-- ---------------------------------------------------------------------------
-- 2. 管理元の設定
-- ---------------------------------------------------------------------------

-- 領域（JinJi.Domain の FieldDomains.All と一致させる）
CREATE FUNCTION jinji_field_domains() RETURNS text[]
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT ARRAY['org_structure', 'org_site', 'assignment', 'person_site', 'person_contact']
$$;
COMMENT ON FUNCTION jinji_field_domains() IS '管理元を設定できる領域（組織の構造・組織の拠点・所属・人の拠点・連絡先）';

CREATE TABLE field_domain_setting (
    id                     uuid        PRIMARY KEY,
    domain                 text        NOT NULL,
    mode                   text        NOT NULL,
    status                 text        NOT NULL DEFAULT 'proposed',
    note                   text,
    proposed_by_person_id  uuid        NOT NULL REFERENCES person (id),
    proposed_at            timestamptz NOT NULL DEFAULT now(),
    decided_by_person_id   uuid        REFERENCES person (id),
    decided_at             timestamptz,
    superseded_at          timestamptz,
    CONSTRAINT field_domain_setting_domain_check CHECK (domain = ANY (public.jinji_field_domains())),
    -- 人事パッケージ（取込だけ）/ jin-ji（直接登録だけ）/ 両方
    CONSTRAINT field_domain_setting_mode_check CHECK (mode IN ('hr', 'field', 'both')),
    -- 起案中 / 承認済み（有効）/ 却下 / 置き換え済み（同じ領域の次の承認で）
    CONSTRAINT field_domain_setting_status_check CHECK (status IN ('proposed', 'approved', 'rejected', 'superseded')),
    -- 理由のメモ（任意）。制御文字（改行を含む）は持たない
    CONSTRAINT field_domain_setting_note_check CHECK (
        note IS NULL OR (btrim(note) <> '' AND char_length(note) <= 500 AND note !~ '[[:cntrl:]]')),
    CONSTRAINT field_domain_setting_decision_check CHECK (
        (status = 'proposed') = (decided_by_person_id IS NULL)
        AND (decided_by_person_id IS NULL) = (decided_at IS NULL)),
    CONSTRAINT field_domain_setting_superseded_check CHECK ((status = 'superseded') = (superseded_at IS NOT NULL)),
    -- 職務分掌: 起案者は自分の起案を承認・却下できない
    CONSTRAINT field_domain_setting_segregation_of_duties CHECK (
        decided_by_person_id IS NULL OR decided_by_person_id <> proposed_by_person_id)
);
COMMENT ON TABLE field_domain_setting IS
    '領域ごとの管理元（hr / field / both。ADR-0013）の起案と承認。起案 → 別人の承認で有効になり、同じ領域の承認済みの行を置き換える。承認済みの行が無い領域は hr';
COMMENT ON COLUMN field_domain_setting.domain IS '領域（org_structure・org_site・assignment・person_site・person_contact）';
COMMENT ON COLUMN field_domain_setting.mode IS '管理元（hr: 取込だけ / field: 直接登録だけ / both: 両方）';
COMMENT ON COLUMN field_domain_setting.note IS '起案の理由のメモ（任意。人事データの値を書かない）';
-- 領域ごとに、有効な行は高々 1 つ・起案中の行も高々 1 つ（承認は「旧行の置き換え → 新行の承認」の順に 1 トランザクションで行う）
CREATE UNIQUE INDEX field_domain_setting_one_active ON field_domain_setting (domain) WHERE status = 'approved';
CREATE UNIQUE INDEX field_domain_setting_one_pending ON field_domain_setting (domain) WHERE status = 'proposed';

-- 状態の遷移: proposed → approved | rejected（決めた人を 1 回だけ記録）、approved → superseded。中身は変えられない
CREATE FUNCTION jinji_guard_field_domain_setting() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    workflow CONSTANT text[] := ARRAY['status', 'decided_by_person_id', 'decided_at', 'superseded_at'];
BEGIN
    IF (to_jsonb(NEW) - workflow) IS DISTINCT FROM (to_jsonb(OLD) - workflow) THEN
        RAISE EXCEPTION 'field_domain_setting: 起案の中身は変更できません（id=%）。新しく起案してください', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'field_domain_setting_immutable';
    END IF;

    IF OLD.status = 'proposed' AND NEW.status IN ('approved', 'rejected') AND NEW.superseded_at IS NULL THEN
        RETURN NEW;
    END IF;

    IF OLD.status = 'approved' AND NEW.status = 'superseded'
       AND (NEW.decided_by_person_id, NEW.decided_at) IS NOT DISTINCT FROM (OLD.decided_by_person_id, OLD.decided_at) THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'field_domain_setting: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'field_domain_setting_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_field_domain_setting() IS 'field_domain_setting の状態遷移（起案 → 承認・却下、承認済み → 置き換え済み）と中身の不変を守るトリガー関数';

CREATE TRIGGER field_domain_setting_guard BEFORE UPDATE ON field_domain_setting
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_field_domain_setting();

-- 領域の今の管理元（承認済みの行。無ければ hr）
CREATE FUNCTION jinji_field_domain_mode(field_domain text) RETURNS text
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce((
        SELECT s.mode FROM public.field_domain_setting s
        WHERE s.domain = field_domain AND s.status = 'approved'), 'hr')
$$;
COMMENT ON FUNCTION jinji_field_domain_mode(text) IS '領域の今の管理元（承認済みの行。無ければ hr）';

-- ---------------------------------------------------------------------------
-- 3. 担当の指名
-- ---------------------------------------------------------------------------

CREATE TABLE org_editor_grant (
    id                    uuid        PRIMARY KEY,
    org_unit_id           uuid        NOT NULL REFERENCES org_unit (id),
    person_id             uuid        NOT NULL REFERENCES person (id),
    valid_period          daterange   NOT NULL,
    granted_by_person_id  uuid        NOT NULL REFERENCES person (id),
    granted_at            timestamptz NOT NULL DEFAULT now(),
    revoked_by_person_id  uuid        REFERENCES person (id),
    revoked_at            timestamptz,
    -- 期間は始まりと終わりを持つ（無期限の指名を作らない。上限の日数はアプリが検査する）
    CONSTRAINT org_editor_grant_valid_period_check CHECK (
        NOT isempty(valid_period) AND NOT lower_inf(valid_period) AND NOT upper_inf(valid_period)),
    CONSTRAINT org_editor_grant_revoked_check CHECK ((revoked_at IS NULL) = (revoked_by_person_id IS NULL)),
    -- 自分を指名できない
    CONSTRAINT org_editor_grant_not_self CHECK (granted_by_person_id <> person_id),
    -- 同じ組織・同じ人の有効な指名の期間は重ならない
    CONSTRAINT org_editor_grant_no_overlap
        EXCLUDE USING gist (org_unit_id WITH =, person_id WITH =, valid_period WITH &&) WHERE (revoked_at IS NULL)
);
COMMENT ON TABLE org_editor_grant IS
    '組織長などが指名した、組織とその配下の直接登録の担当（ADR-0013）。組織に結び付き、指名者が交代しても期間の終わりか取り消しまで残る';
COMMENT ON COLUMN org_editor_grant.valid_period IS '指名の期間（始まりと終わりを持つ）';

CREATE TRIGGER org_editor_grant_guard BEFORE UPDATE ON org_editor_grant
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_revocation('revoked_by_person_id', 'revoked_at');

-- ---------------------------------------------------------------------------
-- 4. 範囲の判定
-- ---------------------------------------------------------------------------

-- 閲覧者が、その日に組織 target の範囲を持つか。人事管理者・org_admin は全社。
-- それ以外は、その日の target の版の祖先（自分を含む）のいずれかで、組織長であるか、（with_grants なら）有効な担当の指名を持つこと。
-- 社内システム（人に紐付かない）と jin-ji 自身は、人事管理者・org_admin のロールが無ければ範囲を持たない
CREATE FUNCTION jinji_viewer_scope_covers(target uuid, on_date date, with_grants boolean) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_hr_admin()
        OR public.jinji_viewer_is_org_admin()
        OR (public.jinji_viewer_person_id() IS NOT NULL AND EXISTS (
            SELECT 1
            FROM public.org_unit_version t
            JOIN public.org_unit_version s
              ON t.path OPERATOR(public.<@) s.path
             AND s.valid_period @> on_date AND upper_inf(s.recorded_period)
            WHERE t.org_unit_id = target AND t.valid_period @> on_date AND upper_inf(t.recorded_period)
              AND (s.head_person_id = public.jinji_viewer_person_id()
                   OR (with_grants AND EXISTS (
                       SELECT 1 FROM public.org_editor_grant g
                       WHERE g.org_unit_id = s.org_unit_id
                         AND g.person_id = public.jinji_viewer_person_id()
                         AND g.revoked_at IS NULL
                         AND g.valid_period @> on_date)))))
$$;
COMMENT ON FUNCTION jinji_viewer_scope_covers(uuid, date, boolean) IS
    'RLS: 閲覧者がその日に組織の範囲（その組織か祖先の組織長、with_grants なら担当の指名）を持つか。人事管理者・org_admin は全社';

-- ---------------------------------------------------------------------------
-- 5. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

ALTER TABLE field_domain_setting ENABLE ROW LEVEL SECURITY;
ALTER TABLE field_domain_setting FORCE ROW LEVEL SECURITY;
ALTER TABLE org_editor_grant ENABLE ROW LEVEL SECURITY;
ALTER TABLE org_editor_grant FORCE ROW LEVEL SECURITY;

-- 管理元は設定データ（人事データの値ではない）なので、閲覧者なら誰でも参照できる。起案・承認は人事管理者か org_admin の「人」
CREATE POLICY field_domain_setting_select ON field_domain_setting FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY field_domain_setting_insert ON field_domain_setting FOR INSERT WITH CHECK (
    status = 'proposed'
    AND (SELECT jinji_viewer_is_field_admin_user())
    AND proposed_by_person_id = (SELECT jinji_viewer_person_id()));
CREATE POLICY field_domain_setting_update ON field_domain_setting FOR UPDATE
    USING ((SELECT jinji_viewer_is_field_admin_user()))
    WITH CHECK (
        (SELECT jinji_viewer_is_field_admin_user())
        AND ((status IN ('approved', 'rejected') AND decided_by_person_id = (SELECT jinji_viewer_person_id()))
             OR status = 'superseded'));

-- 担当の指名は、組織長と同じく誰が登録できるかを示す情報なので、人の閲覧者（AI エージェントは委任元）と jin-ji 自身に見せる。社内システムには見せない。
-- 指名・取り消しは「人」だけで、指名者・取り消した人は閲覧者本人。範囲は組織長としての範囲（担当としての範囲では指名できない）か、人事管理者・org_admin
CREATE POLICY org_editor_grant_select ON org_editor_grant FOR SELECT USING (
    coalesce((SELECT jinji_viewer_kind()) IN ('user', 'agent', 'system'), false));
CREATE POLICY org_editor_grant_insert ON org_editor_grant FOR INSERT WITH CHECK (
    coalesce((SELECT jinji_viewer_kind()) = 'user', false)
    AND revoked_at IS NULL
    AND granted_by_person_id = (SELECT jinji_viewer_person_id())
    AND jinji_viewer_scope_covers(org_unit_id, lower(valid_period), false));
CREATE POLICY org_editor_grant_update ON org_editor_grant FOR UPDATE
    USING (coalesce((SELECT jinji_viewer_kind()) = 'user', false))
    WITH CHECK (
        coalesce((SELECT jinji_viewer_kind()) = 'user', false)
        AND revoked_by_person_id = (SELECT jinji_viewer_person_id())
        AND jinji_viewer_scope_covers(org_unit_id, (SELECT jinji_business_date()), false));

-- ---------------------------------------------------------------------------
-- 6. アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT ON field_domain_setting, org_editor_grant TO jinji_app;
GRANT UPDATE (status, decided_by_person_id, decided_at, superseded_at) ON field_domain_setting TO jinji_app;
GRANT UPDATE (revoked_by_person_id, revoked_at) ON org_editor_grant TO jinji_app;
