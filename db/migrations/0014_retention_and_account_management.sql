-- 0014: 保持期限の消去と、認証の管理の不足分（アーキテクチャ概要 §4.5・§6.4・§6.5）
--
-- 内容
-- 1. 保持期限を過ぎた行の消去。アプリ用ロールに、消してよい表だけ DELETE を与え、RLS で「jin-ji 自身（system）が、
--    終わった行（終了・期限切れ・解決済み・承認されないまま）だけ」に絞る（DB の下限）。どれだけ待ってから消すか（保持期間）は
--    設定（JinJi:Retention）でアプリが決める。人事データ・監査ログ・紐付け・ロールの付与・ローカルアカウント・クライアントの登録（承認待ちの CIMD を除く）は消さない
-- 2. 消去の実行の記録（retention_run）。表ごとの削除件数を持ち、監査ログ（retention.purge）から ID で引く。追記だけ
-- 3. TOTP の符号の再利用の検知（local_account.totp_last_step）。最後に受け付けた時間窓（30 秒の番号）を持ち、
--    それ以前の時間窓の符号は受け付けない（RFC 6238 §5.2。同じ符号を 2 度受け付けない）
-- 4. 本人による委任（トークンの系列）の取り消し（oauth_token_family.end_kind = 'user'）
--
-- 消去の下限（RLS。保持期間の設定がこれより短くても、これより先の行は消えない）
-- - ui_session: 終了したか、最長の期限を過ぎた
-- - unbound_login: 最後の試行（解決済みは解決）から 1 日を過ぎた
-- - account_reset_link: 期限を過ぎた（使用・取り消しの行も期限は 24 時間以内）
-- - oauth_authorization_request・oauth_access_token: 期限を過ぎた
-- - oauth_authorization_code: 期限を過ぎ、発行した系列が無いか、その系列が終わった（生きている系列のコードは、再利用の検知のため消さない）
-- - oauth_refresh_token: 期限を過ぎたか、系列が終わった（生きている系列の使用済みのトークンは、再利用の検知のため期限まで残す）
-- - oauth_token_family: 終了したか、最長の期限を過ぎた
-- - oauth_consent: 認可の要求の期限を過ぎ、その要求から生きている系列が無い
-- - api_client: 承認待ち（pending_approval）の CIMD のクライアントで、記録から 1 日を過ぎた（却下したものは拒否のまま残す）
-- - staging_row: 0005 のまま（hr_admin が、保持期限を過ぎた行だけ）
--
-- 外部キーの連鎖（ON DELETE CASCADE）は RLS を受けないので使わず、アプリが参照の順（トークン → コード → 系列 → 同意 → 要求）に消す

-- ---------------------------------------------------------------------------
-- 1. 消去（DELETE の権限と RLS の下限）
-- ---------------------------------------------------------------------------

-- FOR ALL（system）の方針がある表は、DELETE だけを絞る RESTRICTIVE の方針を足す（方針は AND で効く）
CREATE POLICY ui_session_delete_finished ON ui_session AS RESTRICTIVE FOR DELETE USING (
    ended_at IS NOT NULL OR expires_at <= now());

CREATE POLICY oauth_authorization_request_delete_expired ON oauth_authorization_request AS RESTRICTIVE FOR DELETE USING (
    expires_at <= now());

CREATE POLICY oauth_access_token_delete_expired ON oauth_access_token AS RESTRICTIVE FOR DELETE USING (
    expires_at <= now());

CREATE POLICY oauth_token_family_delete_finished ON oauth_token_family AS RESTRICTIVE FOR DELETE USING (
    ended_at IS NOT NULL OR expires_at <= now());

CREATE POLICY oauth_authorization_code_delete_finished ON oauth_authorization_code AS RESTRICTIVE FOR DELETE USING (
    expires_at <= now()
    AND (family_id IS NULL OR EXISTS (
        SELECT 1 FROM oauth_token_family f
        WHERE f.id = oauth_authorization_code.family_id AND (f.ended_at IS NOT NULL OR f.expires_at <= now()))));

CREATE POLICY oauth_refresh_token_delete_finished ON oauth_refresh_token AS RESTRICTIVE FOR DELETE USING (
    expires_at <= now()
    OR EXISTS (
        SELECT 1 FROM oauth_token_family f
        WHERE f.id = oauth_refresh_token.family_id AND (f.ended_at IS NOT NULL OR f.expires_at <= now())));

CREATE POLICY oauth_consent_delete_finished ON oauth_consent AS RESTRICTIVE FOR DELETE USING (
    EXISTS (
        SELECT 1 FROM oauth_authorization_request r
        WHERE r.id = oauth_consent.request_id AND r.expires_at <= now())
    AND NOT EXISTS (
        SELECT 1 FROM oauth_authorization_code c JOIN oauth_token_family f ON f.id = c.family_id
        WHERE c.request_id = oauth_consent.request_id AND f.ended_at IS NULL AND f.expires_at > now()));

-- FOR ALL の方針が無い表は、jin-ji 自身だけに DELETE の方針を足す
CREATE POLICY unbound_login_delete ON unbound_login FOR DELETE USING (
    (SELECT jinji_viewer_is_system())
    AND greatest(last_attempted_at, coalesce(resolved_at, last_attempted_at)) <= now() - interval '1 day');

CREATE POLICY account_reset_link_delete ON account_reset_link FOR DELETE USING (
    (SELECT jinji_viewer_is_system()) AND expires_at <= now());

CREATE POLICY api_client_delete_pending ON api_client FOR DELETE USING (
    (SELECT jinji_viewer_is_system()) AND status = 'pending_approval' AND registration = 'cimd' AND created_at <= now() - interval '1 day');

GRANT DELETE ON
    ui_session, unbound_login, account_reset_link,
    oauth_authorization_request, oauth_authorization_code, oauth_token_family, oauth_access_token, oauth_refresh_token, oauth_consent,
    api_client
    TO jinji_app;

-- ---------------------------------------------------------------------------
-- 2. 消去の実行の記録
-- ---------------------------------------------------------------------------

CREATE TABLE retention_run (
    id                           uuid        PRIMARY KEY,
    trigger                      text        NOT NULL,
    outcome                      text        NOT NULL,
    started_at                   timestamptz NOT NULL,
    finished_at                  timestamptz NOT NULL,
    ui_session                   integer     NOT NULL DEFAULT 0,
    unbound_login                integer     NOT NULL DEFAULT 0,
    account_reset_link           integer     NOT NULL DEFAULT 0,
    oauth_authorization_request  integer     NOT NULL DEFAULT 0,
    oauth_authorization_code     integer     NOT NULL DEFAULT 0,
    oauth_access_token           integer     NOT NULL DEFAULT 0,
    oauth_refresh_token          integer     NOT NULL DEFAULT 0,
    oauth_token_family           integer     NOT NULL DEFAULT 0,
    oauth_consent                integer     NOT NULL DEFAULT 0,
    oauth_client_assertion       integer     NOT NULL DEFAULT 0,
    pending_client               integer     NOT NULL DEFAULT 0,
    staging_row                  integer     NOT NULL DEFAULT 0,
    -- 定期実行（ワーカー）/ 管理 CLI
    CONSTRAINT retention_run_trigger_check CHECK (trigger IN ('worker', 'cli')),
    -- 失敗のときも、それまでに消した件数を残す
    CONSTRAINT retention_run_outcome_check CHECK (outcome IN ('success', 'failed')),
    CONSTRAINT retention_run_period_check CHECK (finished_at >= started_at),
    CONSTRAINT retention_run_counts_check CHECK (
        ui_session >= 0 AND unbound_login >= 0 AND account_reset_link >= 0 AND oauth_authorization_request >= 0
        AND oauth_authorization_code >= 0 AND oauth_access_token >= 0 AND oauth_refresh_token >= 0 AND oauth_token_family >= 0
        AND oauth_consent >= 0 AND oauth_client_assertion >= 0 AND pending_client >= 0 AND staging_row >= 0)
);
COMMENT ON TABLE retention_run IS '保持期限の消去の実行の記録（表ごとの削除件数）。監査ログ retention.purge の対象。追記だけで、消さない';
CREATE INDEX retention_run_started_at_idx ON retention_run (started_at);

ALTER TABLE retention_run ENABLE ROW LEVEL SECURITY;
ALTER TABLE retention_run FORCE ROW LEVEL SECURITY;

-- 記録は jin-ji 自身（ワーカー・管理 CLI）、参照は jin-ji 自身と人事管理者
CREATE POLICY retention_run_select ON retention_run FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin()));
CREATE POLICY retention_run_insert ON retention_run FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_system()));

GRANT SELECT, INSERT ON retention_run TO jinji_app;

-- ---------------------------------------------------------------------------
-- 3. TOTP の符号の再利用の検知
-- ---------------------------------------------------------------------------

ALTER TABLE local_account ADD COLUMN totp_last_step bigint;
ALTER TABLE local_account ADD CONSTRAINT local_account_totp_last_step_check CHECK (totp_last_step IS NULL OR totp_last_step >= 0);
COMMENT ON COLUMN local_account.totp_last_step IS '最後に受け付けた TOTP の時間窓（Unix 時刻 / 30 秒）。これ以前の時間窓の符号は受け付けない。共有鍵を作り直したら NULL に戻す';

GRANT UPDATE (totp_last_step) ON local_account TO jinji_app;

-- ---------------------------------------------------------------------------
-- 4. 本人による委任の取り消し
-- ---------------------------------------------------------------------------

ALTER TABLE oauth_token_family DROP CONSTRAINT oauth_token_family_end_check;
-- 終了: 失効の要求（RFC 7009）/ リフレッシュトークンの再利用の検知 / 認可コードの再利用 / 管理者の失効 / 本人の取り消し（アカウントの画面）
ALTER TABLE oauth_token_family ADD CONSTRAINT oauth_token_family_end_check CHECK (
    (ended_at IS NULL) = (end_kind IS NULL)
    AND (end_kind IS NULL OR end_kind IN ('revoked', 'reuse_detected', 'code_reuse', 'admin', 'user')));
