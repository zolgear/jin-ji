-- 0016: 未来日付の変更の先行提供（社内システムのクライアントごとの例外）と、変更フィードの取り消しの記録（アーキテクチャ概要 §4「将来の予定の秘匿」・§9）
--
-- 位置づけ
-- - 将来の予定（内示・組織改編・異動・退職の予定）は、発令日（有効日）まで人事管理者以外に見せない（0002・0007）。
--   勤怠・ワークフローなどの社内システムが 4/1 の組織改編を事前に登録できるよう、client credentials のクライアント（社内システム）ごとに
--   「有効日の N 日前から見せてよい」（先行提供の日数。0〜90 日、既定 0 = 例外なし）を設定できるようにする
-- - 人の委任（MCP・UI・認可コードのトークン）には効かない。効くのは閲覧者の種別が service で、jin-ji が発行したトークンのクライアント（api_client）だけ
--   （外部の発行者の JWT のクライアントは設定ファイルのクライアントで、この表の対象にならない）
-- - 設定の変更は内示の秘匿を緩める操作なので、段の定義・ポリシーと同じく起案 → 別人の承認で反映する（職務分掌。§5.5）
--
-- 内容
-- 1. 先行提供の日数の起案と承認（api_client_disclosure_lead）。クライアントごとに有効（approved）な行は高々 1 つ、起案中も高々 1 つ
-- 2. 閲覧者の GUC jinji.viewer_api_client（先行提供の日数を持つ社内システムの api_client.id。アプリは日数が 0 なら設定しない）と、
--    閲覧者の「見せてよい日の上限」jinji_viewer_horizon()（業務日付 + 承認済みの日数。人事管理者以外の線をこの日に引く）
-- 3. 将来の予定の行の RLS（0002・0007）を、業務日付の代わりに jinji_viewer_horizon() で引き直す。日数が 0 の閲覧者には従来と同じ線になる
--    - 発令: 発令日 <= 上限
--    - 組織の版・所属・一時停止: 有効期間の開始 <= 上限
--    - 人の属性の版: 現在記録中で、有効期間が [業務日付, 上限] と重なる版（日数が 0 なら「今日有効な版」= 従来と同じ）。
--      先行提供のクライアントには、入社予定の人・属性の変更の予定（上限までに有効になる版）も見える。過去の版（氏名の履歴）は引き続き人事管理者だけ
--    - 変更フィード: 見せてよい日 <= 上限（その日数だけ早く届く）
-- 4. 変更フィードの取り消しの記録: 将来の予定の行（見せてよい日が業務日付より後の行）の記録期間を、置き換えの有無にかかわらず閉じたら、
--    change_event に withdrawn = true の行を「その行の見せてよい日」で追記する。先行提供で既にその予定を受け取ったクライアントに、
--    予定が消えた（変わった）ことを届けるため。0007 の「置き換えの無い取り消しは今の書き込み経路に無い」は、取込の EndRowsAsync
--    （有効日以後に始まる行の取り消し）が足されたので当たらなくなっていた
--    - 取り消しの記録は、先行提供の日数を持つ閲覧者（と人事管理者）にだけ見せる。日数 0 の閲覧者はその予定を一度も見ていないので、
--      見せてよい日が来ても何も変わっていない ID が届くだけになり、予定があったことの手がかりを与えてしまうため
--
-- 他の行を読む RLS について
-- - jinji_viewer_horizon() は api_client_disclosure_lead を読む。この表の参照は「その社内システム自身の行」を許すので、閲覧者によって結果が変わるが、
--   それは意図した違い（日数はクライアントごと）。逆向きの参照（この表の方針が人事データの表を読む）は無いので循環しない
-- - アプリ（ViewerContext.DisclosureLeadDays・PersonProjector・ChangeFeedService）も同じ日数で同じ線を引く（片方のバグでは広がらない）。
--   日数はトークンの検証（AccessTokenValidator。キャッシュは既定 5 秒）で同じ表から読む

-- ---------------------------------------------------------------------------
-- 1. 先行提供の日数の起案と承認
-- ---------------------------------------------------------------------------

CREATE TABLE api_client_disclosure_lead (
    id                     uuid        PRIMARY KEY,
    api_client_id          uuid        NOT NULL REFERENCES api_client (id),
    lead_days              integer     NOT NULL,
    status                 text        NOT NULL DEFAULT 'proposed',
    note                   text,
    proposed_by_kind       text        NOT NULL,
    proposed_by_person_id  uuid        NOT NULL REFERENCES person (id),
    proposed_at            timestamptz NOT NULL DEFAULT now(),
    decided_by_person_id   uuid        REFERENCES person (id),
    decided_at             timestamptz,
    superseded_at          timestamptz,
    CONSTRAINT api_client_disclosure_lead_days_check CHECK (lead_days BETWEEN 0 AND 90),
    -- 起案中 / 承認済み（有効）/ 却下 / 置き換え済み（同じクライアントの次の承認で）
    CONSTRAINT api_client_disclosure_lead_status_check CHECK (status IN ('proposed', 'approved', 'rejected', 'superseded')),
    -- 起案は人事管理者の人（UI）か、管理 CLI（起案者として人事管理者の person を指定する。同期計画の --executed-by と同じ）
    CONSTRAINT api_client_disclosure_lead_proposed_by_kind_check CHECK (proposed_by_kind IN ('person', 'cli')),
    -- 理由・用途のメモ（任意）。制御文字（改行を含む）は持たない
    CONSTRAINT api_client_disclosure_lead_note_check CHECK (
        note IS NULL OR (btrim(note) <> '' AND char_length(note) <= 500 AND note !~ '[[:cntrl:]]')),
    CONSTRAINT api_client_disclosure_lead_decision_check CHECK (
        (status = 'proposed') = (decided_by_person_id IS NULL)
        AND (decided_by_person_id IS NULL) = (decided_at IS NULL)),
    CONSTRAINT api_client_disclosure_lead_superseded_check CHECK ((status = 'superseded') = (superseded_at IS NOT NULL)),
    -- 職務分掌: 起案者は自分の起案を承認・却下できない
    CONSTRAINT api_client_disclosure_lead_segregation_of_duties CHECK (
        decided_by_person_id IS NULL OR decided_by_person_id <> proposed_by_person_id)
);
COMMENT ON TABLE api_client_disclosure_lead IS
    '社内システム（client credentials の api_client）ごとの先行提供の日数（有効日の N 日前から将来の予定を見せる）。起案 → 別人の承認で有効になり、同じクライアントの承認済みの行を置き換える';
COMMENT ON COLUMN api_client_disclosure_lead.lead_days IS '有効日の何日前から将来の予定を見せるか（0〜90。0 は例外なし）';
COMMENT ON COLUMN api_client_disclosure_lead.note IS '起案の理由・用途のメモ（任意。人事データの値を書かない）';
-- クライアントごとに、有効な行は高々 1 つ・起案中の行も高々 1 つ（承認は「旧行の置き換え → 新行の承認」の順に 1 トランザクションで行う）
CREATE UNIQUE INDEX api_client_disclosure_lead_one_active ON api_client_disclosure_lead (api_client_id) WHERE status = 'approved';
CREATE UNIQUE INDEX api_client_disclosure_lead_one_pending ON api_client_disclosure_lead (api_client_id) WHERE status = 'proposed';

-- 状態の遷移: proposed → approved | rejected（決めた人を 1 回だけ記録）、approved → superseded。中身は変えられない
CREATE FUNCTION jinji_guard_api_client_disclosure_lead() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    workflow CONSTANT text[] := ARRAY['status', 'decided_by_person_id', 'decided_at', 'superseded_at'];
BEGIN
    IF (to_jsonb(NEW) - workflow) IS DISTINCT FROM (to_jsonb(OLD) - workflow) THEN
        RAISE EXCEPTION 'api_client_disclosure_lead: 起案の中身は変更できません（id=%）。新しく起案してください', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'api_client_disclosure_lead_immutable';
    END IF;

    IF OLD.status = 'proposed' AND NEW.status IN ('approved', 'rejected') AND NEW.superseded_at IS NULL THEN
        RETURN NEW;
    END IF;

    IF OLD.status = 'approved' AND NEW.status = 'superseded'
       AND (NEW.decided_by_person_id, NEW.decided_at) IS NOT DISTINCT FROM (OLD.decided_by_person_id, OLD.decided_at) THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'api_client_disclosure_lead: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'api_client_disclosure_lead_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_api_client_disclosure_lead() IS 'api_client_disclosure_lead の状態遷移（起案 → 承認・却下、承認済み → 置き換え済み）と中身の不変を守るトリガー関数';

CREATE TRIGGER api_client_disclosure_lead_guard BEFORE UPDATE ON api_client_disclosure_lead
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_api_client_disclosure_lead();

-- ---------------------------------------------------------------------------
-- 2. 閲覧者の先行提供の日数と、見せてよい日の上限
-- ---------------------------------------------------------------------------

-- 先行提供の日数を持つ社内システムの api_client.id（GUC jinji.viewer_api_client）。社内システム以外・未設定・空は NULL。UUID でなければエラー（fail closed）
CREATE FUNCTION jinji_viewer_api_client() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT CASE
        WHEN public.jinji_viewer_kind() = 'service'
        THEN NULLIF(pg_catalog.current_setting('jinji.viewer_api_client', true), '')::uuid
    END
$$;

-- 閲覧者の先行提供の日数（承認済みの行。無ければ 0）。社内システム以外は常に 0（人の委任・人・jin-ji 自身には効かない）
CREATE FUNCTION jinji_viewer_disclosure_lead() RETURNS integer
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce((
        SELECT l.lead_days FROM public.api_client_disclosure_lead l
        WHERE l.api_client_id = public.jinji_viewer_api_client() AND l.status = 'approved'), 0)
$$;

-- 人事管理者以外に将来の予定を見せてよい日の上限（業務日付 + 先行提供の日数）。日数 0 なら業務日付（従来の線）
CREATE FUNCTION jinji_viewer_horizon() RETURNS date
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_business_date() + public.jinji_viewer_disclosure_lead()
$$;

COMMENT ON FUNCTION jinji_viewer_api_client() IS 'RLS: 先行提供の日数を持つ社内システムの api_client.id（GUC jinji.viewer_api_client）。社内システム以外は NULL';
COMMENT ON FUNCTION jinji_viewer_disclosure_lead() IS 'RLS: 閲覧者の先行提供の日数（承認済み。無ければ 0。社内システム以外は 0）';
COMMENT ON FUNCTION jinji_viewer_horizon() IS 'RLS: 人事管理者以外に将来の予定を見せてよい日の上限（業務日付 + 先行提供の日数）';

-- ---------------------------------------------------------------------------
-- 3. 将来の予定の行の RLS を上限で引き直す
-- ---------------------------------------------------------------------------

DROP POLICY personnel_action_select ON personnel_action;
CREATE POLICY personnel_action_select ON personnel_action FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND effective_date <= (SELECT jinji_viewer_horizon())));

-- 人の属性の版: 過去の版（氏名の履歴）は従来どおり hr_admin だけ。先行提供の閲覧者には上限までに有効になる版も見せる
DROP POLICY person_version_select ON person_version;
CREATE POLICY person_version_select ON person_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND upper_inf(recorded_period)
        AND valid_period && daterange((SELECT jinji_business_date()), (SELECT jinji_viewer_horizon()), '[]')));

DROP POLICY org_unit_version_select ON org_unit_version;
CREATE POLICY org_unit_version_select ON org_unit_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));

DROP POLICY assignment_select ON assignment;
CREATE POLICY assignment_select ON assignment FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));

DROP POLICY account_suspension_select ON account_suspension;
CREATE POLICY account_suspension_select ON account_suspension FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_viewer_horizon())));

-- ---------------------------------------------------------------------------
-- 4. 変更フィード: 上限と、将来の予定の取り消しの記録
-- ---------------------------------------------------------------------------

ALTER TABLE change_event ADD COLUMN withdrawn boolean NOT NULL DEFAULT false;
COMMENT ON COLUMN change_event.withdrawn IS
    '将来の予定の行の記録期間を閉じた（取り消し・訂正）記録か。先行提供の日数を持つ閲覧者と人事管理者にだけ見せる';

-- 記録期間を閉じた将来の予定の行（見せてよい日が業務日付より後）を、その見せてよい日で記録する（取り消しの記録）
CREATE FUNCTION jinji_record_withdrawal() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_type text;
    v_id uuid;
    v_date date;
BEGIN
    CASE TG_TABLE_NAME
        WHEN 'org_unit_version' THEN
            v_type := 'org_unit';
            v_id := OLD.org_unit_id;
            v_date := jinji_change_visible_from(OLD.valid_period);
        WHEN 'person_version' THEN
            v_type := 'person';
            v_id := OLD.person_id;
            v_date := jinji_change_visible_from(OLD.valid_period);
            IF OLD.termination_date IS NOT NULL AND OLD.termination_date >= jinji_business_date() THEN
                v_date := greatest(v_date, OLD.termination_date + 1);
            END IF;
        WHEN 'assignment', 'account_suspension' THEN
            v_type := 'person';
            v_id := OLD.person_id;
            v_date := jinji_change_visible_from(OLD.valid_period);
        WHEN 'personnel_action' THEN
            v_type := 'person';
            v_id := OLD.person_id;
            v_date := OLD.effective_date;
        ELSE
            RAISE EXCEPTION 'jinji_record_withdrawal: 未対応の表です: %', TG_TABLE_NAME;
    END CASE;

    -- 既に見せてよい日が来ている行は全員が見ている。その取り消しは必ず置き換えの行（新しい記録）を伴うので、そちらで届く
    IF v_date > jinji_business_date() THEN
        INSERT INTO change_event (entity_type, entity_id, effective_date, withdrawn) VALUES (v_type, v_id, v_date, true);
    END IF;

    RETURN NULL;
END
$$;
COMMENT ON FUNCTION jinji_record_withdrawal() IS '変更フィード: 将来の予定の行の記録期間を閉じたこと（取り消し・訂正）を change_event に記録するトリガー関数';

CREATE TRIGGER org_unit_version_record_withdrawal AFTER UPDATE OF recorded_period ON org_unit_version
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();
CREATE TRIGGER person_version_record_withdrawal AFTER UPDATE OF recorded_period ON person_version
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();
CREATE TRIGGER personnel_action_record_withdrawal AFTER UPDATE OF recorded_period ON personnel_action
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();
CREATE TRIGGER assignment_record_withdrawal AFTER UPDATE OF recorded_period ON assignment
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();
CREATE TRIGGER account_suspension_record_withdrawal AFTER UPDATE OF recorded_period ON account_suspension
    FOR EACH ROW WHEN (upper_inf(OLD.recorded_period) AND NOT upper_inf(NEW.recorded_period)) EXECUTE FUNCTION jinji_record_withdrawal();

-- 参照: hr_admin はすべて、それ以外は見せてよい日が上限以前の行。取り消しの記録は先行提供の日数を持つ閲覧者だけ
DROP POLICY change_event_select ON change_event;
CREATE POLICY change_event_select ON change_event FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set())
        AND effective_date <= (SELECT jinji_viewer_horizon())
        AND (NOT withdrawn OR (SELECT jinji_viewer_disclosure_lead()) > 0)));

-- ---------------------------------------------------------------------------
-- 5. 先行提供の表の RLS と権限
-- ---------------------------------------------------------------------------

ALTER TABLE api_client_disclosure_lead ENABLE ROW LEVEL SECURITY;
ALTER TABLE api_client_disclosure_lead FORCE ROW LEVEL SECURITY;

-- 参照: jin-ji 自身（トークンの検証・管理 CLI）と人事管理者の人。社内システムは自分の行だけ（RLS の上限の計算に使う）
CREATE POLICY api_client_disclosure_lead_select ON api_client_disclosure_lead FOR SELECT USING (
    (SELECT jinji_viewer_is_system())
    OR (SELECT jinji_viewer_is_hr_admin_user())
    OR api_client_id = (SELECT jinji_viewer_api_client()));
-- 起案: 人事管理者の人（本人として記録）か、管理 CLI（jin-ji 自身。起案者の人はアプリが人事管理者であることを確かめる）
CREATE POLICY api_client_disclosure_lead_insert ON api_client_disclosure_lead FOR INSERT WITH CHECK (
    status = 'proposed'
    AND (((SELECT jinji_viewer_is_system()) AND proposed_by_kind = 'cli')
         OR ((SELECT jinji_viewer_is_hr_admin_user()) AND proposed_by_kind = 'person' AND proposed_by_person_id = (SELECT jinji_viewer_person_id()))));
-- 承認・却下・置き換え: 人事管理者の人だけ（jin-ji 自身・管理 CLI は承認できない。決めた人は閲覧者本人としてしか記録できない）
CREATE POLICY api_client_disclosure_lead_update ON api_client_disclosure_lead FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin_user()))
    WITH CHECK (
        (SELECT jinji_viewer_is_hr_admin_user())
        AND ((status IN ('approved', 'rejected') AND decided_by_person_id = (SELECT jinji_viewer_person_id()))
             OR status = 'superseded'));

GRANT SELECT, INSERT ON api_client_disclosure_lead TO jinji_app;
GRANT UPDATE (status, decided_by_person_id, decided_at, superseded_at) ON api_client_disclosure_lead TO jinji_app;
