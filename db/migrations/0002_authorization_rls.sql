-- 0002: 認可（ポリシーの保存・RLS）と、骨格レビューで残った課題
--
-- 内容
-- 1. 組織コードの時点での一意性（同一法人・現在記録中・有効期間の重複で同じ code を禁止）
-- 2. 記録期間（recorded_period）は「開いている期間を閉じる」更新だけを許す（閉じた期間の開き直し・書き換えを禁止）
-- 3. 公開範囲のポリシー（policy_rule）。変更は起案 → 承認で反映し、起案者と承認者は別人（アーキテクチャ概要 §5.5）
-- 4. 行レベルセキュリティ（RLS）。アーキテクチャ概要 §5.4 の「粗い防波堤」
--
-- RLS の考え方と限界
-- - 細かい判定（項目のマスキング・関係スコープ・クライアントの項目上限）はアプリのポリシーエンジンだけが行う。
--   RLS は「アプリにバグがあっても越えられない線」だけを引く: 内示の行、過去の氏名の版、監査ログ、ポリシーの変更
-- - 閲覧者の情報は、アプリがトランザクションごとに set_config(名前, 値, true)（SET LOCAL 相当）で渡す。
--   トランザクションが終われば消えるので、接続プールで次の利用者に引き継がれない
--     jinji.viewer_kind       'user' | 'service' | 'agent' | 'system'（未設定・それ以外の値は「閲覧者なし」）
--     jinji.viewer_roles      ロール名のカンマ区切り（例: 'hr_admin'）。空なら一般の閲覧者
--     jinji.viewer_person_id  閲覧者の person.id（AI エージェントは委任元。社内システムは空）
-- - 閲覧者が未設定なら、どのテーブルも 1 行も見えず、書き込めない（fail closed）
-- - 限界: これらの GUC はアプリ用ロール（jinji_app）自身が自由に設定できる。したがって RLS は
--   「アプリのバグ（設定し忘れ・判定の誤り・抜け道の問い合わせ）に対する防波堤」であって、
--   侵害されたアプリ（任意の SQL を実行できる攻撃者）への防御ではない。後者は DB 資格情報の保護、
--   ネットワークの分離、監査ログ、異常検知で守る
--
-- FORCE ROW LEVEL SECURITY を付ける理由
-- - アプリは jinji_app（テーブル所有者ではない）で接続するので、アプリに対しては FORCE が無くても RLS が効く
-- - それでも FORCE を付け、所有者（jinji_owner）にも RLS を効かせる。誤ってアプリを所有者の接続文字列で動かしても、
--   閲覧者が未設定なら何も見えない（fail closed）ようにするため
-- - 所有者は ALTER TABLE で RLS を外せるので、これは事故の防止であって所有者への防御ではない
-- - 副作用: 所有者で行うデータの修正（今後のマイグレーション等）は、先に set_config で hr_admin の閲覧者を設定する必要がある。
--   外部キー・一意・排他制約の検査は RLS を受けないので、整合性の検査には影響しない。
--   整合性の検査を他の行を読むトリガー関数で書くと RLS を受けるので、制約で書くこと

-- ---------------------------------------------------------------------------
-- 1. 組織コードの時点での一意性
-- ---------------------------------------------------------------------------

-- 排他制約に法人を含めるため、版に法人を持たせる。不変 ID（org_unit）の法人と食い違わないよう複合外部キーで縛る
ALTER TABLE org_unit ADD CONSTRAINT org_unit_id_legal_entity_key UNIQUE (id, legal_entity_id);

ALTER TABLE org_unit_version ADD COLUMN legal_entity_id uuid;
UPDATE org_unit_version v SET legal_entity_id = o.legal_entity_id FROM org_unit o WHERE o.id = v.org_unit_id;
ALTER TABLE org_unit_version
    ALTER COLUMN legal_entity_id SET NOT NULL,
    ADD CONSTRAINT org_unit_version_org_unit_legal_entity_fkey
        FOREIGN KEY (org_unit_id, legal_entity_id) REFERENCES org_unit (id, legal_entity_id),
    -- 上位組織は同じ法人の組織に限る（組織ツリーは法人ごと。法人をまたぐ関係は出向・兼務の所属で表す）
    ADD CONSTRAINT org_unit_version_parent_legal_entity_fkey
        FOREIGN KEY (parent_org_unit_id, legal_entity_id) REFERENCES org_unit (id, legal_entity_id),
    -- 同一法人で、現在記録中かつ有効期間が重なる版に同じ組織コードを使わない
    ADD CONSTRAINT org_unit_version_code_no_overlap
        EXCLUDE USING gist (legal_entity_id WITH =, code WITH =, valid_period WITH &&)
        WHERE (upper_inf(recorded_period));

COMMENT ON COLUMN org_unit_version.legal_entity_id IS '組織が属する法人（org_unit.legal_entity_id と同じ値。組織コードの一意性の排他制約に使う）';

-- ---------------------------------------------------------------------------
-- 2. 記録期間の保護
-- ---------------------------------------------------------------------------

-- 許す更新は「現在記録中（upper が無限）の行の recorded_period を、開始はそのままに閉じる」ことだけ。
-- - 閉じた期間を開き直す・閉じた行を書き換える・開始を動かす・他の列を変える、はすべて拒否する
-- - jinji_app には列単位の権限で recorded_period の UPDATE しか与えていないが、値の中身まではそれで縛れないためトリガーで検査する
-- - 所有者にも効く（誤りの訂正も上書きではなく、新しい行の追加で行う）
CREATE FUNCTION jinji_guard_recorded_period() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT upper_inf(OLD.recorded_period) THEN
        RAISE EXCEPTION '%: 記録期間が閉じた行は変更できません（id=%）', TG_TABLE_NAME, OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'recorded_period_closed';
    END IF;

    IF lower(NEW.recorded_period) IS DISTINCT FROM lower(OLD.recorded_period)
       OR lower_inc(NEW.recorded_period) IS DISTINCT FROM lower_inc(OLD.recorded_period)
       OR upper_inf(NEW.recorded_period) THEN
        RAISE EXCEPTION '%: 記録期間は開始を変えずに閉じる更新だけができます（id=%）', TG_TABLE_NAME, OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'recorded_period_close_only';
    END IF;

    IF (to_jsonb(NEW) - 'recorded_period') IS DISTINCT FROM (to_jsonb(OLD) - 'recorded_period') THEN
        RAISE EXCEPTION '%: 記録期間以外の列は変更できません（id=%）', TG_TABLE_NAME, OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'recorded_period_close_only';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_recorded_period() IS '二つの時間軸のテーブルで、記録期間を閉じる更新だけを許すトリガー関数';

CREATE TRIGGER org_unit_version_guard_recorded_period BEFORE UPDATE ON org_unit_version
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();
CREATE TRIGGER person_version_guard_recorded_period BEFORE UPDATE ON person_version
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();
CREATE TRIGGER personnel_action_guard_recorded_period BEFORE UPDATE ON personnel_action
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();
CREATE TRIGGER assignment_guard_recorded_period BEFORE UPDATE ON assignment
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();
CREATE TRIGGER account_suspension_guard_recorded_period BEFORE UPDATE ON account_suspension
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_recorded_period();

-- ---------------------------------------------------------------------------
-- 3. 公開範囲のポリシー
-- ---------------------------------------------------------------------------

-- 既定（全項目をグループ全体に公開）は行として持たない。行は公開範囲を狭める規則だけ。
-- 合成は「最も狭いものが勝つ」（当てはまるすべての規則を満たしたときだけ見える）。判定はアプリ（JinJi.Policy）が行う。
CREATE TABLE policy_rule (
    id                      uuid        PRIMARY KEY,
    target_kind             text        NOT NULL,
    org_unit_id             uuid        REFERENCES org_unit (id),
    include_descendants     boolean     NOT NULL DEFAULT true,
    person_id               uuid        REFERENCES person (id),
    fields                  text[]      NOT NULL,
    allow_scope             text        NOT NULL,
    status                  text        NOT NULL DEFAULT 'proposed',
    proposed_by_person_id   uuid        NOT NULL REFERENCES person (id),
    proposed_via_client_id  text,
    proposed_at             timestamptz NOT NULL DEFAULT now(),
    decided_by_person_id    uuid        REFERENCES person (id),
    decided_at              timestamptz,
    retired_by_person_id    uuid        REFERENCES person (id),
    retired_at              timestamptz,
    -- 全員 / 組織単位 / 本人による制限
    CONSTRAINT policy_rule_target_kind_check CHECK (target_kind IN ('all', 'org_unit', 'person')),
    CONSTRAINT policy_rule_target_check CHECK (
        (target_kind = 'all' AND org_unit_id IS NULL AND person_id IS NULL)
        OR (target_kind = 'org_unit' AND org_unit_id IS NOT NULL AND person_id IS NULL)
        OR (target_kind = 'person' AND person_id IS NOT NULL AND org_unit_id IS NULL)),
    CONSTRAINT policy_rule_fields_check CHECK (cardinality(fields) > 0 AND array_position(fields, NULL) IS NULL),
    CONSTRAINT policy_rule_allow_scope_check CHECK (allow_scope IN (
        'none', 'self', 'same_team', 'same_department', 'same_division', 'legal_entity', 'group',
        'direct_reports', 'subordinates', 'managers_chain')),
    -- 起案中 / 承認済み（有効）/ 却下 / 廃止
    CONSTRAINT policy_rule_status_check CHECK (status IN ('proposed', 'approved', 'rejected', 'retired')),
    CONSTRAINT policy_rule_decision_check CHECK (
        (status = 'proposed') = (decided_by_person_id IS NULL)
        AND (decided_by_person_id IS NULL) = (decided_at IS NULL)),
    CONSTRAINT policy_rule_retirement_check CHECK (
        (status = 'retired') = (retired_at IS NOT NULL)
        AND (retired_by_person_id IS NULL) = (retired_at IS NULL)),
    -- 職務分掌: 起案者は自分の起案を承認・却下できない（AI エージェントの起案は委任元ユーザーの起案として記録する）
    CONSTRAINT policy_rule_segregation_of_duties CHECK (decided_by_person_id IS DISTINCT FROM proposed_by_person_id)
);
COMMENT ON TABLE policy_rule IS '公開範囲のポリシーの規則（狭める方向だけ）。承認済み（status = approved）の行だけが判定に使われる。変更は起案 → 承認で反映する';
CREATE INDEX policy_rule_status_idx ON policy_rule (status);

-- 状態の遷移は proposed → approved | rejected、approved → retired だけ。規則の中身は起案後に変えない（変えるなら新しく起案する）
CREATE FUNCTION jinji_guard_policy_rule() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (to_jsonb(NEW) - ARRAY['status', 'decided_by_person_id', 'decided_at', 'retired_by_person_id', 'retired_at'])
       IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['status', 'decided_by_person_id', 'decided_at', 'retired_by_person_id', 'retired_at']) THEN
        RAISE EXCEPTION 'policy_rule: 規則の中身は変更できません（id=%）。新しく起案してください', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'policy_rule_immutable';
    END IF;

    IF NOT ((OLD.status = 'proposed' AND NEW.status IN ('approved', 'rejected'))
            OR (OLD.status = 'approved' AND NEW.status = 'retired')) THEN
        RAISE EXCEPTION 'policy_rule: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'policy_rule_transition';
    END IF;

    IF OLD.status <> 'proposed' AND (NEW.decided_by_person_id IS DISTINCT FROM OLD.decided_by_person_id
                                     OR NEW.decided_at IS DISTINCT FROM OLD.decided_at) THEN
        RAISE EXCEPTION 'policy_rule: 承認・却下の記録は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'policy_rule_transition';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_policy_rule() IS 'policy_rule の状態遷移と中身の不変を守るトリガー関数';

CREATE TRIGGER policy_rule_guard BEFORE UPDATE ON policy_rule
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_policy_rule();

-- ---------------------------------------------------------------------------
-- 4. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

-- 閲覧者の情報を読む関数。不正な値（未知の種別）は「未設定」と同じに扱う。person_id が UUID でなければエラー（fail closed）。
-- 方針で使うときは (SELECT 関数()) と書き、行ごとではなく問い合わせごとに 1 回だけ評価させる。
CREATE FUNCTION jinji_viewer_kind() RETURNS text
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT CASE
        WHEN pg_catalog.current_setting('jinji.viewer_kind', true) IN ('user', 'service', 'agent', 'system')
        THEN pg_catalog.current_setting('jinji.viewer_kind', true)
    END
$$;

CREATE FUNCTION jinji_viewer_is_set() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_kind() IS NOT NULL
$$;

CREATE FUNCTION jinji_viewer_is_hr_admin() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_set()
       AND 'hr_admin' = ANY (pg_catalog.string_to_array(coalesce(pg_catalog.current_setting('jinji.viewer_roles', true), ''), ','))
$$;

CREATE FUNCTION jinji_viewer_person_id() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT CASE
        WHEN public.jinji_viewer_is_set()
        THEN NULLIF(pg_catalog.current_setting('jinji.viewer_person_id', true), '')::uuid
    END
$$;

-- 業務日付（日本時間の日付）。current_date はセッションのタイムゾーンに依存するので使わない。
-- アプリ側の JinJi.Domain.BusinessDate と同じ規則（両者が食い違うと内示の判定がずれる）
CREATE FUNCTION jinji_business_date() RETURNS date
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT (pg_catalog.now() AT TIME ZONE 'Asia/Tokyo')::date
$$;

COMMENT ON FUNCTION jinji_viewer_kind() IS 'RLS: 閲覧者の種別（GUC jinji.viewer_kind）。未設定・不正なら NULL';
COMMENT ON FUNCTION jinji_viewer_is_set() IS 'RLS: 閲覧者が設定されているか（未設定なら何も見せない）';
COMMENT ON FUNCTION jinji_viewer_is_hr_admin() IS 'RLS: 閲覧者が hr_admin ロールを持つか（GUC jinji.viewer_roles）';
COMMENT ON FUNCTION jinji_viewer_person_id() IS 'RLS: 閲覧者の person.id（GUC jinji.viewer_person_id）';
COMMENT ON FUNCTION jinji_business_date() IS 'RLS: 業務日付（日本時間の日付）。内示の判定に使う';

-- 有効化（所有者にも効かせる。理由は冒頭）
ALTER TABLE legal_entity ENABLE ROW LEVEL SECURITY;
ALTER TABLE legal_entity FORCE ROW LEVEL SECURITY;
ALTER TABLE org_unit ENABLE ROW LEVEL SECURITY;
ALTER TABLE org_unit FORCE ROW LEVEL SECURITY;
ALTER TABLE person ENABLE ROW LEVEL SECURITY;
ALTER TABLE person FORCE ROW LEVEL SECURITY;
ALTER TABLE org_unit_version ENABLE ROW LEVEL SECURITY;
ALTER TABLE org_unit_version FORCE ROW LEVEL SECURITY;
ALTER TABLE person_version ENABLE ROW LEVEL SECURITY;
ALTER TABLE person_version FORCE ROW LEVEL SECURITY;
ALTER TABLE personnel_action ENABLE ROW LEVEL SECURITY;
ALTER TABLE personnel_action FORCE ROW LEVEL SECURITY;
ALTER TABLE assignment ENABLE ROW LEVEL SECURITY;
ALTER TABLE assignment FORCE ROW LEVEL SECURITY;
ALTER TABLE account_suspension ENABLE ROW LEVEL SECURITY;
ALTER TABLE account_suspension FORCE ROW LEVEL SECURITY;
ALTER TABLE external_identity ENABLE ROW LEVEL SECURITY;
ALTER TABLE external_identity FORCE ROW LEVEL SECURITY;
ALTER TABLE audit_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE audit_event FORCE ROW LEVEL SECURITY;
ALTER TABLE policy_rule ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy_rule FORCE ROW LEVEL SECURITY;

-- 時間軸を持たないテーブル: 閲覧者が設定されていれば参照でき、書き込みは hr_admin だけ
-- （取込ワーカーは system の閲覧者に hr_admin ロールを付けて書き込む）
CREATE POLICY legal_entity_select ON legal_entity FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY legal_entity_insert ON legal_entity FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY org_unit_select ON org_unit FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY org_unit_insert ON org_unit FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY person_select ON person FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY person_insert ON person FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY external_identity_select ON external_identity FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY external_identity_insert ON external_identity FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- 発令: 発令日が業務日付より後の行（内示）は hr_admin だけ
CREATE POLICY personnel_action_select ON personnel_action FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND effective_date <= (SELECT jinji_business_date())));
CREATE POLICY personnel_action_insert ON personnel_action FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY personnel_action_update ON personnel_action FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- 人の属性の版: hr_admin 以外は「現在記録中かつ今日有効」の版だけ（氏名の履歴は婚姻・離婚を推測させるため。ADR-0002）
CREATE POLICY person_version_select ON person_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND upper_inf(recorded_period) AND valid_period @> (SELECT jinji_business_date())));
CREATE POLICY person_version_insert ON person_version FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY person_version_update ON person_version FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- 組織の版・所属・一時停止: 有効期間の開始が業務日付より後の行（組織改編・異動・一時停止の予定 = 内示）は hr_admin だけ
CREATE POLICY org_unit_version_select ON org_unit_version FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_business_date())));
CREATE POLICY org_unit_version_insert ON org_unit_version FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY org_unit_version_update ON org_unit_version FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

CREATE POLICY assignment_select ON assignment FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_business_date())));
CREATE POLICY assignment_insert ON assignment FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY assignment_update ON assignment FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

CREATE POLICY account_suspension_select ON account_suspension FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND lower(valid_period) <= (SELECT jinji_business_date())));
CREATE POLICY account_suspension_insert ON account_suspension FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY account_suspension_update ON account_suspension FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin())) WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- 監査ログ: 追記は閲覧者が設定されていれば誰でも（拒否の記録も残すため）。参照は hr_admin だけ
CREATE POLICY audit_event_select ON audit_event FOR SELECT USING ((SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY audit_event_insert ON audit_event FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_set()));

-- ポリシー: 参照は閲覧者が設定されていれば誰でも（判定に使うため）。変更系は hr_admin だけで、
-- 起案者・承認者・廃止者は閲覧者本人として記録させる（他人の名前で承認できない）
CREATE POLICY policy_rule_select ON policy_rule FOR SELECT USING ((SELECT jinji_viewer_is_set()));
CREATE POLICY policy_rule_insert ON policy_rule FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_hr_admin())
    AND status = 'proposed'
    AND proposed_by_person_id = (SELECT jinji_viewer_person_id()));
CREATE POLICY policy_rule_update ON policy_rule FOR UPDATE
    USING ((SELECT jinji_viewer_is_hr_admin()))
    WITH CHECK (
        (SELECT jinji_viewer_is_hr_admin())
        AND ((status IN ('approved', 'rejected') AND decided_by_person_id = (SELECT jinji_viewer_person_id()))
             OR (status = 'retired' AND retired_by_person_id = (SELECT jinji_viewer_person_id()))));

-- ---------------------------------------------------------------------------
-- アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT ON policy_rule TO jinji_app;
GRANT UPDATE (status, decided_by_person_id, decided_at, retired_by_person_id, retired_at) ON policy_rule TO jinji_app;
