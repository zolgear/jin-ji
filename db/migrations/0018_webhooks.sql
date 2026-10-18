-- 0018: 社内システムへの Webhook（変更の通知。アーキテクチャ概要 §9「Webhook の実装の規則」）
--
-- 位置づけ
-- - 勤怠・ワークフローなどの社内システム（client credentials の api_client）が、変更フィードをポーリングしなくても「変わった」ことを知れるようにする。
--   Webhook は通知であり、本文は種別と ID だけ（氏名などの値は載せない）。受け手は自分のトークンで REST・変更フィードから取り直す（pull 前提）
-- - 送る内容は、受け手のクライアントを閲覧者として変更フィード（0007・0016）を読んだ結果そのもの。将来の予定の線（jinji_viewer_horizon()）・
--   取り消しの記録（withdrawn）の宛先は RLS がそのまま決める（受け手ごとに送る日が違う）。読み進めた位置は受け手ごとにこの表に持つ
-- - 登録・変更・鍵のローテーション・再送は人事管理者の人（UI）だけ。起案と別人の承認は持たない（下の「承認を持たない理由」）
--
-- 内容
-- 1. 受け手の登録 webhook_subscription（api_client ごとに 1 つ）。送信先の URL（https）・1 回に送る件数の上限・要約に切り替える件数・送信の間隔と、
--    変更フィードの位置（受け手ごとの読み進めた位置・見せてよい日が来た行を配った日・拾い直しの途中の位置）、ワーカーの取り出しの貸し出し（lease）
-- 2. 署名の鍵 webhook_secret。HMAC-SHA256 の鍵を Data Protection で暗号化した値だけを持つ（平文は作成・ローテーションの応答で 1 回だけ返す）。
--    ローテーションでは旧鍵に期限（重ねて署名する期間の終わり）を 1 回だけ記録し、期限までは新旧両方で署名する
-- 3. 送信の待ち行列 webhook_delivery（1 行 = 1 回の POST = イベント ID）。本文（種別と ID の JSON）・状態（pending / sending / succeeded / dead）・
--    試行回数・次の試行の日時・貸し出しの期限。複数のワーカーでも FOR UPDATE SKIP LOCKED と貸し出しで二重に送らない
-- 4. 通知済みの対象 webhook_notified_entity（受け手ごとに、変更として通知した人・組織・拠点の ID）。取り消し（withdrawn）は、
--    受け手に今見える対象か、以前に通知した対象にだけ送る（取り消された入社予定の人は今は見えないが、先行提供で通知済みなら届ける）
--
-- 承認を持たない理由（職務分掌）
-- - 本文は種別・ID・日付だけで値を持たず、受け手は自分のトークンでしか値を取り直せない。登録しても受け手が読める範囲（項目の上限・スコープ・
--   先行提供の日数。どれも承認・登録の手順を経る）は広がらない。クライアントの登録・項目の上限の変更（0012）と同じく、人事管理者 1 人の操作とし、
--   すべてを監査ログに残す
-- - 送信先を外部に向ける誤り・悪用（ID と日付が漏れる）は、https だけ・公開のアドレスだけ（SSRF の対策。アプリ）・監査ログで抑える
--
-- 他の行を読む RLS は作らない（このマイグレーションの方針はすべて閲覧者の種別とロールだけで決まる）

-- ---------------------------------------------------------------------------
-- 1. 受け手の登録
-- ---------------------------------------------------------------------------

CREATE TABLE webhook_subscription (
    id                       uuid        PRIMARY KEY,
    api_client_id            uuid        NOT NULL REFERENCES api_client (id),
    url                      text        NOT NULL,
    status                   text        NOT NULL DEFAULT 'active',
    max_events_per_delivery  integer     NOT NULL DEFAULT 100,
    summary_threshold        integer     NOT NULL DEFAULT 1000,
    min_interval_seconds     integer     NOT NULL DEFAULT 10,
    -- 変更フィードの位置（ChangeFeedPosition。受け手のクライアントを閲覧者として読み進めた位置）
    position_xid             bigint      NOT NULL,
    position_id              bigint      NOT NULL,
    activated_through        date        NOT NULL,
    deferred_target          date,
    deferred_xid             bigint,
    deferred_id              bigint,
    next_collect_at          timestamptz NOT NULL DEFAULT now(),
    next_send_at             timestamptz NOT NULL DEFAULT now(),
    lease_owner              uuid,
    lease_until              timestamptz,
    last_success_at          timestamptz,
    last_failure_at          timestamptz,
    last_failure             text,
    created_by_kind          text        NOT NULL,
    created_by_person_id     uuid        REFERENCES person (id),
    created_at               timestamptz NOT NULL DEFAULT now(),
    updated_by_kind          text,
    updated_by_person_id     uuid        REFERENCES person (id),
    updated_at               timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT webhook_subscription_client_key UNIQUE (api_client_id),
    -- 送信先は https だけ（利用者情報・フラグメント・空白・制御文字なし。公開のアドレスかは送信の直前にアプリが DNS の解決の後に確かめる）
    CONSTRAINT webhook_subscription_url_check CHECK (
        url ~ '^https://[^/@#?[:space:][:cntrl:]]+' AND char_length(url) <= 2048 AND url !~ '[#[:space:][:cntrl:]]'),
    CONSTRAINT webhook_subscription_status_check CHECK (status IN ('active', 'disabled')),
    CONSTRAINT webhook_subscription_limits_check CHECK (
        max_events_per_delivery BETWEEN 1 AND 1000
        AND summary_threshold BETWEEN max_events_per_delivery AND 100000
        AND min_interval_seconds BETWEEN 1 AND 3600),
    CONSTRAINT webhook_subscription_deferred_check CHECK (
        (deferred_target IS NULL) = (deferred_xid IS NULL) AND (deferred_xid IS NULL) = (deferred_id IS NULL)),
    CONSTRAINT webhook_subscription_lease_check CHECK ((lease_owner IS NULL) = (lease_until IS NULL)),
    CONSTRAINT webhook_subscription_last_failure_check CHECK (
        last_failure IS NULL OR (char_length(last_failure) <= 500 AND last_failure !~ '[[:cntrl:]]')),
    CONSTRAINT webhook_subscription_created_by_check CHECK (
        (created_by_kind = 'person' AND created_by_person_id IS NOT NULL) OR (created_by_kind = 'cli' AND created_by_person_id IS NULL)),
    CONSTRAINT webhook_subscription_updated_by_check CHECK (
        updated_by_kind IS NULL OR (updated_by_kind = 'person' AND updated_by_person_id IS NOT NULL)
        OR (updated_by_kind = 'cli' AND updated_by_person_id IS NULL))
);
COMMENT ON TABLE webhook_subscription IS
    '社内システム（api_client）への Webhook の登録（クライアントごとに 1 つ）。本文は種別と ID だけで、受け手は自分のトークンで取り直す。変更フィードの位置を受け手ごとに持つ';
COMMENT ON COLUMN webhook_subscription.url IS '送信先（https だけ。公開のアドレスかは送信の直前に確かめる。秘密を含めない。署名の鍵は webhook_secret）';
COMMENT ON COLUMN webhook_subscription.max_events_per_delivery IS '1 回の POST に載せる変更の件数の上限（1〜1000）';
COMMENT ON COLUMN webhook_subscription.summary_threshold IS '1 回の収集で通知する変更がこれを超えたら、1 件ずつ送らずに要約（changes.summary。件数と変更フィードの位置だけ）に切り替える';
COMMENT ON COLUMN webhook_subscription.min_interval_seconds IS '同じ受け手への POST の最小の間隔（秒）';
COMMENT ON COLUMN webhook_subscription.position_xid IS '変更フィードの読み進めた位置（トランザクション ID）。受け手のクライアントを閲覧者として読む';
COMMENT ON COLUMN webhook_subscription.activated_through IS '見せてよい日が来た行をこの日まで配った（受け手の先行提供の上限 = 業務日付 + 日数まで進む）';
COMMENT ON COLUMN webhook_subscription.lease_owner IS '変更を収集中のワーカー（貸し出し。期限を過ぎたら他のワーカーが引き継ぐ）';
COMMENT ON COLUMN webhook_subscription.last_failure IS '最後の送信の失敗の理由（状態コード・接続できない等。URL・本文・応答の本文は含めない）';

-- ID・クライアント・作成の記録は不変
CREATE FUNCTION jinji_guard_webhook_subscription() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.id, NEW.api_client_id, NEW.created_by_kind, NEW.created_by_person_id, NEW.created_at)
       IS DISTINCT FROM (OLD.id, OLD.api_client_id, OLD.created_by_kind, OLD.created_by_person_id, OLD.created_at) THEN
        RAISE EXCEPTION 'webhook_subscription: ID・クライアント・作成の記録は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'webhook_subscription_immutable';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_webhook_subscription() IS 'webhook_subscription の ID・クライアント・作成の記録の不変を守るトリガー関数';

CREATE TRIGGER webhook_subscription_guard BEFORE UPDATE ON webhook_subscription
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_webhook_subscription();

-- ---------------------------------------------------------------------------
-- 2. 署名の鍵
-- ---------------------------------------------------------------------------

CREATE TABLE webhook_secret (
    id                    uuid        PRIMARY KEY,
    subscription_id       uuid        NOT NULL REFERENCES webhook_subscription (id),
    protected_secret      text        NOT NULL,
    created_by_kind       text        NOT NULL,
    created_by_person_id  uuid        REFERENCES person (id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    expires_at            timestamptz,
    CONSTRAINT webhook_secret_protected_check CHECK (char_length(protected_secret) BETWEEN 16 AND 4096),
    CONSTRAINT webhook_secret_created_by_check CHECK (
        (created_by_kind = 'person' AND created_by_person_id IS NOT NULL) OR (created_by_kind = 'cli' AND created_by_person_id IS NULL))
);
COMMENT ON TABLE webhook_secret IS
    'Webhook の署名の鍵（HMAC-SHA256）。値は Data Protection で暗号化したものだけを持ち、平文は作成・ローテーションの応答で 1 回だけ返す。期限の無い鍵が今の鍵';
COMMENT ON COLUMN webhook_secret.protected_secret IS 'Data Protection（目的 JinJi.Webhooks.SigningSecret）で暗号化した鍵';
COMMENT ON COLUMN webhook_secret.expires_at IS 'ローテーションで置き換えた旧鍵の期限（重ねる期間は 0〜7 日。アプリが確かめる）。期限までは新旧両方で署名する（1 回だけ記録できる）';
-- 今の鍵（期限なし）は受け手ごとに 1 つ
CREATE UNIQUE INDEX webhook_secret_one_current ON webhook_secret (subscription_id) WHERE expires_at IS NULL;

-- 期限を 1 回だけ記録できる（中身は不変）
CREATE TRIGGER webhook_secret_set_once BEFORE UPDATE ON webhook_secret
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_set_once('expires_at');

-- ---------------------------------------------------------------------------
-- 3. 送信の待ち行列
-- ---------------------------------------------------------------------------

CREATE TABLE webhook_delivery (
    id                        uuid        PRIMARY KEY,
    subscription_id           uuid        NOT NULL REFERENCES webhook_subscription (id),
    kind                      text        NOT NULL,
    body                      text        NOT NULL,
    event_count               integer     NOT NULL,
    status                    text        NOT NULL DEFAULT 'pending',
    attempts                  integer     NOT NULL DEFAULT 0,
    next_attempt_at           timestamptz NOT NULL,
    lease_until               timestamptz,
    last_attempt_at           timestamptz,
    last_http_status          integer,
    last_error                text,
    created_at                timestamptz NOT NULL DEFAULT now(),
    succeeded_at              timestamptz,
    dead_at                   timestamptz,
    redelivered_by_person_id  uuid        REFERENCES person (id),
    redelivered_at            timestamptz,
    -- changes: 変更の通知（種別と ID）/ summary: 大量の変更の要約（件数と変更フィードの位置だけ）/ ping: 送信の確かめ（人事管理者の操作）
    CONSTRAINT webhook_delivery_kind_check CHECK (kind IN ('changes', 'summary', 'ping')),
    CONSTRAINT webhook_delivery_status_check CHECK (status IN ('pending', 'sending', 'succeeded', 'dead')),
    CONSTRAINT webhook_delivery_body_check CHECK (char_length(body) <= 262144),
    CONSTRAINT webhook_delivery_counts_check CHECK (event_count >= 0 AND attempts >= 0),
    CONSTRAINT webhook_delivery_lease_check CHECK ((status = 'sending') = (lease_until IS NOT NULL)),
    CONSTRAINT webhook_delivery_succeeded_check CHECK ((status = 'succeeded') = (succeeded_at IS NOT NULL)),
    CONSTRAINT webhook_delivery_dead_check CHECK ((status = 'dead') = (dead_at IS NOT NULL)),
    CONSTRAINT webhook_delivery_last_error_check CHECK (
        last_error IS NULL OR (char_length(last_error) <= 500 AND last_error !~ '[[:cntrl:]]')),
    CONSTRAINT webhook_delivery_redelivered_check CHECK ((redelivered_by_person_id IS NULL) = (redelivered_at IS NULL))
);
COMMENT ON TABLE webhook_delivery IS
    'Webhook の送信の待ち行列と記録（1 行 = 1 回の POST。id がイベント ID = 冪等キー・nonce）。本文は種別と ID だけで、氏名などの値を持たない';
COMMENT ON COLUMN webhook_delivery.body IS '送る JSON（イベント ID・種別・対象の ID・見せてよい日・取り消しか・変更フィードの位置）。再送でも同じ本文を送る';
COMMENT ON COLUMN webhook_delivery.next_attempt_at IS '次の試行の日時（失敗したら指数バックオフで延ばす）';
COMMENT ON COLUMN webhook_delivery.lease_until IS '送信中（sending）の貸し出しの期限。期限を過ぎたら他のワーカーが送り直す（受け手はイベント ID で重複を除く）';
COMMENT ON COLUMN webhook_delivery.last_error IS '最後の失敗の理由（状態コード・接続できない等。URL・応答の本文は含めない）';
COMMENT ON COLUMN webhook_delivery.redelivered_by_person_id IS '最後に dead からの再送を指示した人事管理者';
CREATE INDEX webhook_delivery_due_idx ON webhook_delivery (subscription_id, status, next_attempt_at);
CREATE INDEX webhook_delivery_subscription_idx ON webhook_delivery (subscription_id, created_at DESC);

-- 状態の遷移と中身の不変:
--   pending → sending（取り出し）、sending → sending（貸し出しの期限切れの引き継ぎ）・succeeded・pending（再試行）・dead（上限回数）、
--   dead → pending（人事管理者の再送。試行回数を 0 に戻し、再送した人を記録する）。succeeded は終わり
CREATE FUNCTION jinji_guard_webhook_delivery() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.id, NEW.subscription_id, NEW.kind, NEW.body, NEW.event_count, NEW.created_at)
       IS DISTINCT FROM (OLD.id, OLD.subscription_id, OLD.kind, OLD.body, OLD.event_count, OLD.created_at) THEN
        RAISE EXCEPTION 'webhook_delivery: 本文・宛先は変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'webhook_delivery_immutable';
    END IF;

    IF (OLD.status = 'pending' AND NEW.status = 'sending')
       OR (OLD.status = 'sending' AND NEW.status IN ('sending', 'succeeded', 'pending', 'dead')) THEN
        IF (NEW.redelivered_by_person_id, NEW.redelivered_at) IS DISTINCT FROM (OLD.redelivered_by_person_id, OLD.redelivered_at) THEN
            RAISE EXCEPTION 'webhook_delivery: 再送の記録は dead からの再送でだけ変えられます（id=%）', OLD.id
                USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'webhook_delivery_transition';
        END IF;
        RETURN NEW;
    END IF;

    IF OLD.status = 'dead' AND NEW.status = 'pending' AND NEW.attempts = 0 AND NEW.redelivered_by_person_id IS NOT NULL
       AND NEW.redelivered_at IS NOT NULL AND NEW.redelivered_at IS DISTINCT FROM OLD.redelivered_at THEN
        RETURN NEW;
    END IF;

    RAISE EXCEPTION 'webhook_delivery: 状態を % から % に変えられません（id=%）', OLD.status, NEW.status, OLD.id
        USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'webhook_delivery_transition';
END
$$;
COMMENT ON FUNCTION jinji_guard_webhook_delivery() IS 'webhook_delivery の状態の遷移と本文・宛先の不変を守るトリガー関数';

CREATE TRIGGER webhook_delivery_guard BEFORE UPDATE ON webhook_delivery
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_webhook_delivery();

-- ---------------------------------------------------------------------------
-- 4. 通知済みの対象
-- ---------------------------------------------------------------------------

CREATE TABLE webhook_notified_entity (
    subscription_id  uuid        NOT NULL REFERENCES webhook_subscription (id),
    entity_type      text        NOT NULL,
    entity_id        uuid        NOT NULL,
    notified_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (subscription_id, entity_type, entity_id),
    CONSTRAINT webhook_notified_entity_type_check CHECK (entity_type IN ('person', 'org_unit', 'site'))
);
COMMENT ON TABLE webhook_notified_entity IS
    '受け手ごとに、変更として通知した人・組織・拠点の ID。取り消し（withdrawn）を、今は見えないが以前に通知した対象（取り消された入社予定など）にも届けるために使う';

-- ---------------------------------------------------------------------------
-- 5. RLS と権限
-- ---------------------------------------------------------------------------

-- 参照・変更は jin-ji 自身（ワーカー）と人事管理者の人だけ。社内システム・AI エージェント・一般の人・緊急用アカウントには見えない
ALTER TABLE webhook_subscription ENABLE ROW LEVEL SECURITY;
ALTER TABLE webhook_subscription FORCE ROW LEVEL SECURITY;
CREATE POLICY webhook_subscription_select ON webhook_subscription FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()));
CREATE POLICY webhook_subscription_insert ON webhook_subscription FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_system()) AND created_by_kind = 'cli')
    OR ((SELECT jinji_viewer_is_hr_admin_user()) AND created_by_kind = 'person' AND created_by_person_id = (SELECT jinji_viewer_person_id())));
-- 位置・貸し出し・結果はワーカー（jin-ji 自身）、登録の変更は人事管理者の人（変更した人は本人としてしか記録できない）
CREATE POLICY webhook_subscription_update ON webhook_subscription FOR UPDATE
    USING ((SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()))
    WITH CHECK (
        (SELECT jinji_viewer_is_system())
        OR ((SELECT jinji_viewer_is_hr_admin_user()) AND updated_by_kind = 'person' AND updated_by_person_id = (SELECT jinji_viewer_person_id())));

ALTER TABLE webhook_secret ENABLE ROW LEVEL SECURITY;
ALTER TABLE webhook_secret FORCE ROW LEVEL SECURITY;
CREATE POLICY webhook_secret_select ON webhook_secret FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()));
CREATE POLICY webhook_secret_insert ON webhook_secret FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_system()) AND created_by_kind = 'cli')
    OR ((SELECT jinji_viewer_is_hr_admin_user()) AND created_by_kind = 'person' AND created_by_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY webhook_secret_update ON webhook_secret FOR UPDATE
    USING ((SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()))
    WITH CHECK ((SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()));

ALTER TABLE webhook_delivery ENABLE ROW LEVEL SECURITY;
ALTER TABLE webhook_delivery FORCE ROW LEVEL SECURITY;
CREATE POLICY webhook_delivery_select ON webhook_delivery FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()));
-- 変更の通知・要約はワーカーだけが積む。人事管理者の人が積めるのは送信の確かめ（ping。本文に人事データを持たない）だけ
CREATE POLICY webhook_delivery_insert ON webhook_delivery FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_system()) OR ((SELECT jinji_viewer_is_hr_admin_user()) AND kind = 'ping' AND event_count = 0));
-- 送信の結果はワーカー、dead からの再送は人事管理者の人（再送した人は本人としてしか記録できない）
CREATE POLICY webhook_delivery_update ON webhook_delivery FOR UPDATE
    USING ((SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()))
    WITH CHECK (
        (SELECT jinji_viewer_is_system())
        OR ((SELECT jinji_viewer_is_hr_admin_user()) AND status = 'pending' AND redelivered_by_person_id = (SELECT jinji_viewer_person_id())));

ALTER TABLE webhook_notified_entity ENABLE ROW LEVEL SECURITY;
ALTER TABLE webhook_notified_entity FORCE ROW LEVEL SECURITY;
CREATE POLICY webhook_notified_entity_all ON webhook_notified_entity FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));

-- DELETE は与えない（送信の記録は配信の証跡。§4.6）
GRANT SELECT, INSERT ON webhook_subscription, webhook_secret, webhook_delivery, webhook_notified_entity TO jinji_app;
GRANT UPDATE (url, status, max_events_per_delivery, summary_threshold, min_interval_seconds,
              position_xid, position_id, activated_through, deferred_target, deferred_xid, deferred_id,
              next_collect_at, next_send_at, lease_owner, lease_until, last_success_at, last_failure_at, last_failure,
              updated_by_kind, updated_by_person_id, updated_at) ON webhook_subscription TO jinji_app;
GRANT UPDATE (expires_at) ON webhook_secret TO jinji_app;
GRANT UPDATE (status, attempts, next_attempt_at, lease_until, last_attempt_at, last_http_status, last_error,
              succeeded_at, dead_at, redelivered_by_person_id, redelivered_at) ON webhook_delivery TO jinji_app;
GRANT UPDATE (notified_at) ON webhook_notified_entity TO jinji_app;
