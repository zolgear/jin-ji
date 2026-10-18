-- 0007: 変更フィード（アーキテクチャ概要 §9 の /changes?since=...）
--
-- 内容
-- 1. 変更の記録 change_event。二つの時間軸の各表に行が追加されたら、トリガーで「種別と ID」だけを 1 行追記する（値は持たない）
-- 2. 取りこぼしの無い読み出し: 行に書き込んだトランザクションの ID（xid8）を持たせ、読み出し側は
--    「今動いているどのトランザクションよりも古いトランザクション」の行だけを (txid, id) の順に返す（pg_snapshot_xmin）。
--    連番や記録時刻だけで位置を決めると、長いトランザクションが後からコミットしたときに、既に読んだ位置より前に行が現れて取りこぼす
-- 3. 将来の予定の秘匿: 行ごとに「人事管理者以外に変更として見せてよい日」（effective_date）を持ち、hr_admin 以外にはその日が来るまで見せない（RLS とアプリの両方）
--    - 有効期間の開始が今日より後（内示・組織改編の予定）: 開始日
--    - 有効期間の終了が今日より後（異動・復職の予定で今の行の終了日が決まった）: 終了日（終了日は人事管理者以外には返さないため。§5.6）
--    - 人の属性の版で退職年月日が今日以後（退職の予定）: 退職年月日の翌日（退職年月日は退職済みになるまで返さないため）
--    - 発令: 発令日
--    - それ以外: 有効期間の開始日（今日以前 = すぐ見せてよい）
--    見せてよい日が来たときに改めて配る処理はアプリ（ChangeFeedService）が行う（その日までに読み進めた位置より前の行も拾い直す）
--
-- 記録期間を閉じる更新（訂正の旧行）は記録しない。訂正は必ず新しい行の追加を伴い、その行で変更を知らせる。
-- 置き換えの無い取り消し（記録期間を閉じるだけ）は今の書き込み経路に無い。足すときは、ここに UPDATE のトリガーを足す

-- ---------------------------------------------------------------------------
-- 1. 変更の記録
-- ---------------------------------------------------------------------------

CREATE TABLE change_event (
    id              bigint      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    txid            xid8        NOT NULL DEFAULT pg_current_xact_id(),
    entity_type     text        NOT NULL,
    entity_id       uuid        NOT NULL,
    effective_date  date        NOT NULL,
    recorded_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT change_event_entity_type_check CHECK (entity_type IN ('person', 'org_unit'))
);
COMMENT ON TABLE change_event IS '変更フィード（§9）の記録。種別と ID だけを持ち、値は持たない（受け手は API で取り直し、受け手ごとの項目上限が効く）。追記専用';
COMMENT ON COLUMN change_event.txid IS '行を書き込んだトランザクションの ID。読み出しの位置（取りこぼしの無い順序）に使う';
COMMENT ON COLUMN change_event.effective_date IS '人事管理者以外に変更として見せてよい日（内示・将来の終了日・退職の予定の秘匿）';
CREATE INDEX change_event_txid_idx ON change_event (txid, id);
CREATE INDEX change_event_effective_date_idx ON change_event (effective_date, txid, id);

-- ---------------------------------------------------------------------------
-- 2. トリガー
-- ---------------------------------------------------------------------------

-- 今日より後に終わる・始まる期間は、その日まで人事管理者以外に見せない
CREATE FUNCTION jinji_change_visible_from(period daterange) RETURNS date
LANGUAGE sql STABLE AS $$
    SELECT CASE
        WHEN lower(period) > jinji_business_date() THEN lower(period)
        WHEN NOT upper_inf(period) AND upper(period) > jinji_business_date() THEN upper(period)
        ELSE lower(period)
    END
$$;
COMMENT ON FUNCTION jinji_change_visible_from(daterange) IS '変更フィード: 有効期間の行の変更を、人事管理者以外に見せてよい日';

CREATE FUNCTION jinji_record_change() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_type text;
    v_id uuid;
    v_date date;
BEGIN
    CASE TG_TABLE_NAME
        WHEN 'org_unit_version' THEN
            v_type := 'org_unit';
            v_id := NEW.org_unit_id;
            v_date := jinji_change_visible_from(NEW.valid_period);
        WHEN 'person_version' THEN
            v_type := 'person';
            v_id := NEW.person_id;
            v_date := jinji_change_visible_from(NEW.valid_period);
            IF NEW.termination_date IS NOT NULL AND NEW.termination_date >= jinji_business_date() THEN
                v_date := greatest(v_date, NEW.termination_date + 1);
            END IF;
        WHEN 'assignment', 'account_suspension' THEN
            v_type := 'person';
            v_id := NEW.person_id;
            v_date := jinji_change_visible_from(NEW.valid_period);
        WHEN 'personnel_action' THEN
            v_type := 'person';
            v_id := NEW.person_id;
            v_date := NEW.effective_date;
        ELSE
            RAISE EXCEPTION 'jinji_record_change: 未対応の表です: %', TG_TABLE_NAME;
    END CASE;

    INSERT INTO change_event (entity_type, entity_id, effective_date) VALUES (v_type, v_id, v_date);
    RETURN NULL;
END
$$;
COMMENT ON FUNCTION jinji_record_change() IS '変更フィード: 二つの時間軸の表への行の追加を change_event に記録するトリガー関数';

CREATE TRIGGER org_unit_version_record_change AFTER INSERT ON org_unit_version
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();
CREATE TRIGGER person_version_record_change AFTER INSERT ON person_version
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();
CREATE TRIGGER personnel_action_record_change AFTER INSERT ON personnel_action
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();
CREATE TRIGGER assignment_record_change AFTER INSERT ON assignment
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();
CREATE TRIGGER account_suspension_record_change AFTER INSERT ON account_suspension
    FOR EACH ROW EXECUTE FUNCTION jinji_record_change();

-- ---------------------------------------------------------------------------
-- 3. RLS と権限
-- ---------------------------------------------------------------------------

-- 参照: hr_admin はすべて、それ以外は見せてよい日が来た行だけ。追記: 人事データを書ける hr_admin だけ（トリガーは書き込んだ閲覧者で動く）
ALTER TABLE change_event ENABLE ROW LEVEL SECURITY;
ALTER TABLE change_event FORCE ROW LEVEL SECURITY;
CREATE POLICY change_event_select ON change_event FOR SELECT USING (
    (SELECT jinji_viewer_is_hr_admin())
    OR ((SELECT jinji_viewer_is_set()) AND effective_date <= (SELECT jinji_business_date())));
CREATE POLICY change_event_insert ON change_event FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_hr_admin()));

-- 追記専用（UPDATE・DELETE は与えない）
GRANT SELECT, INSERT ON change_event TO jinji_app;
