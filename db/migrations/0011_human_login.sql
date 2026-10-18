-- 0011: 人のログイン（ADR-0004 の段階 A）
--
-- 内容
-- 1. ロールの付与（role_grant）。UI のセッションの閲覧者のロール（GUC jinji.viewer_roles）の出どころ。外部の IdP のロールは使わない
-- 2. ローカルアカウント（local_account・パスキー・リカバリコード）。ASP.NET Core Identity の自前ストアの保存先。
--    緊急用の管理者アカウントも同じ表に持つ（kind = 'break_glass'。person に紐付かず、パスキーだけでログインする）
-- 3. 外部 IdP の主体と person の紐付け（login_binding）と、紐付けできなかったログインの記録（unbound_login。人事管理者に示す）
-- 4. 管理者が発行する 1 回限り・短命のリンク（account_reset_link。パスワードの設定・再設定、パスキーの登録）。jin-ji はメールを送らない
-- 5. UI のセッション（ui_session）。サーバー側に保存し、Cookie には乱数の鍵だけを持たせる。退職・一時停止・紐付けの取り消しは次の要求から効く
-- 6. Data Protection の鍵（data_protection_key。IXmlRepository の保存先。鍵の XML は証明書で暗号化して入れる）
-- 7. 監査ログ: 緊急用アカウントの操作者（actor_type = 'break_glass'、actor_account_id）
--
-- 閲覧者（GUC）と RLS
-- - ログインの処理・セッションの検証・鍵の読み書き（jin-ji 自身）は jinji.viewer_kind = 'system'（ロールなし）で行う。
--   人事データは hr_admin を付けないので、今日時点の行（内示・氏名の履歴を除く）だけが見える
-- - 緊急用アカウントの操作は jinji.viewer_kind = 'break_glass'。jinji_viewer_kind() はこの値を「閲覧者なし」と扱うので、
--   人事データのテーブルは 1 行も見えず書き込めない（fail closed）。認証の表と監査ログの追記だけを、下の方針で個別に許す
-- - 人事管理者の人（kind = 'user' かつ hr_admin）は、紐付け・未紐付けのログイン・ローカルアカウントのロックの解除・リンクの発行・ロールの付与を扱える。
--   AI エージェント（kind = 'agent'）・社内システムは、委任元が人事管理者でも扱えない（認証の管理はエージェントにさせない）
-- - 資格情報（パスワードのハッシュ・TOTP の鍵・リカバリコード・リンクとセッションの鍵）は jin-ji 自身（system）と、
--   ローカルアカウントの管理（ロックの解除など）に限って緊急用・人事管理者の閲覧者だけが行を読める。値はハッシュか暗号化した値だけを入れる
-- - セッションと Data Protection の鍵は jin-ji 自身（system）だけ

-- ---------------------------------------------------------------------------
-- 1. 閲覧者の判定
-- ---------------------------------------------------------------------------

CREATE FUNCTION jinji_viewer_is_system() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce(public.jinji_viewer_kind() = 'system', false)
$$;

-- jinji_viewer_kind() は 'break_glass' を知らない（= 閲覧者なし）ので、生の値を見る
CREATE FUNCTION jinji_viewer_is_break_glass() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce(pg_catalog.current_setting('jinji.viewer_kind', true) = 'break_glass', false)
$$;

CREATE FUNCTION jinji_viewer_is_hr_admin_user() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce(public.jinji_viewer_kind() = 'user', false) AND public.jinji_viewer_is_hr_admin()
$$;

CREATE FUNCTION jinji_viewer_can_recover_auth() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT public.jinji_viewer_is_system() OR public.jinji_viewer_is_hr_admin_user() OR public.jinji_viewer_is_break_glass()
$$;

COMMENT ON FUNCTION jinji_viewer_is_system() IS 'RLS: 閲覧者が jin-ji 自身（GUC jinji.viewer_kind = system。ログインの処理・CLI・ワーカー）か';
COMMENT ON FUNCTION jinji_viewer_is_break_glass() IS 'RLS: 閲覧者が緊急用の管理者アカウント（jinji.viewer_kind = break_glass）か。人事データの表では「閲覧者なし」と同じに扱われる';
COMMENT ON FUNCTION jinji_viewer_is_hr_admin_user() IS 'RLS: 閲覧者が hr_admin のロールを持つ人（user）か。AI エージェント・社内システムは含めない';
COMMENT ON FUNCTION jinji_viewer_can_recover_auth() IS 'RLS: 認証の復旧（紐付け・ロックの解除・リンクの発行）を扱える閲覧者か（jin-ji 自身・人事管理者の人・緊急用アカウント）';

-- ---------------------------------------------------------------------------
-- 2. ロールの付与
-- ---------------------------------------------------------------------------

CREATE TABLE role_grant (
    id                    uuid        PRIMARY KEY,
    person_id             uuid        NOT NULL REFERENCES person (id),
    role                  text        NOT NULL,
    granted_by_kind       text        NOT NULL,
    granted_by_person_id  uuid        REFERENCES person (id),
    granted_at            timestamptz NOT NULL DEFAULT now(),
    revoked_by_kind       text,
    revoked_by_person_id  uuid        REFERENCES person (id),
    revoked_at            timestamptz,
    -- 既知のロール（JinJi.Policy の Roles.Known と一致させる）
    CONSTRAINT role_grant_role_check CHECK (role IN ('hr_admin')),
    -- 付与・取り消しの主体: 人事管理者の人（UI）/ 管理 CLI（jin-ji の運用者。人を記録しない）
    CONSTRAINT role_grant_granted_by_check CHECK (
        granted_by_kind IN ('person', 'cli') AND (granted_by_kind = 'person') = (granted_by_person_id IS NOT NULL)),
    CONSTRAINT role_grant_revoked_by_check CHECK (
        (revoked_at IS NULL) = (revoked_by_kind IS NULL)
        AND (revoked_by_kind IS NULL OR revoked_by_kind IN ('person', 'cli'))
        AND (revoked_by_kind IS NOT DISTINCT FROM 'person') = (revoked_by_person_id IS NOT NULL)),
    -- 職務分掌: 自分にロールを付与できない（§5.5）
    CONSTRAINT role_grant_not_self CHECK (granted_by_person_id IS DISTINCT FROM person_id)
);
COMMENT ON TABLE role_grant IS 'ロールの付与。UI のセッションの閲覧者のロール（GUC jinji.viewer_roles）はここから毎回読む（取り消しは次の要求から効く）。付与者は自分に付与できない';
-- 有効な付与は人 × ロールで 1 つ
CREATE UNIQUE INDEX role_grant_active_key ON role_grant (person_id, role) WHERE revoked_at IS NULL;

-- 取り消しだけを許す（中身は変えられず、取り消しは 1 回だけ）
CREATE FUNCTION jinji_guard_revocation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    revocation CONSTANT text[] := TG_ARGV::text[];
BEGIN
    IF (to_jsonb(NEW) - revocation) IS DISTINCT FROM (to_jsonb(OLD) - revocation) THEN
        RAISE EXCEPTION '%: 取り消し以外の変更はできません（id=%）', TG_TABLE_NAME, OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'revocation_only';
    END IF;

    IF (to_jsonb(OLD) ->> 'revoked_at') IS NOT NULL THEN
        RAISE EXCEPTION '%: 取り消し済みの行は変更できません（id=%）', TG_TABLE_NAME, OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'revocation_only';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_revocation() IS '取り消しの列（トリガーの引数）以外の変更と、取り消し済みの行の変更を拒否するトリガー関数';

CREATE TRIGGER role_grant_guard BEFORE UPDATE ON role_grant
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_revocation('revoked_by_kind', 'revoked_by_person_id', 'revoked_at');

-- ---------------------------------------------------------------------------
-- 3. ローカルアカウント（ASP.NET Core Identity の自前ストア）
-- ---------------------------------------------------------------------------

CREATE TABLE local_account (
    id                           uuid        PRIMARY KEY,
    kind                         text        NOT NULL,
    person_id                    uuid        REFERENCES person (id),
    user_name                    text        NOT NULL,
    normalized_user_name         text        NOT NULL,
    password_hash                text,
    security_stamp               text        NOT NULL,
    concurrency_stamp            text        NOT NULL,
    lockout_enabled              boolean     NOT NULL DEFAULT true,
    lockout_end                  timestamptz,
    access_failed_count          integer     NOT NULL DEFAULT 0,
    two_factor_enabled           boolean     NOT NULL DEFAULT false,
    authenticator_key_protected  text,
    created_by_kind              text        NOT NULL,
    created_at                   timestamptz NOT NULL DEFAULT now(),
    disabled_at                  timestamptz,
    -- 人のアカウント / 緊急用の管理者アカウント（person に紐付かない。権限は認証の復旧だけで、人事情報は見えない）
    CONSTRAINT local_account_kind_check CHECK (kind IN ('person', 'break_glass')),
    CONSTRAINT local_account_person_check CHECK ((kind = 'person') = (person_id IS NOT NULL)),
    -- 緊急用アカウントはパスキーだけでログインする（パスワード・TOTP を持たない）
    CONSTRAINT local_account_break_glass_passkey_only CHECK (
        kind <> 'break_glass' OR (password_hash IS NULL AND authenticator_key_protected IS NULL AND NOT two_factor_enabled)),
    CONSTRAINT local_account_user_name_check CHECK (btrim(user_name) <> '' AND char_length(user_name) <= 256),
    CONSTRAINT local_account_normalized_user_name_key UNIQUE (normalized_user_name),
    -- ロックアウトは常に有効（Identity の設定で外させない）
    CONSTRAINT local_account_lockout_enabled_check CHECK (lockout_enabled),
    CONSTRAINT local_account_access_failed_count_check CHECK (access_failed_count >= 0),
    -- 作成の主体: 管理 CLI だけ（Web からは作らない）
    CONSTRAINT local_account_created_by_kind_check CHECK (created_by_kind IN ('cli'))
);
COMMENT ON TABLE local_account IS 'ローカルアカウント（ASP.NET Core Identity のユーザー）。外部 IdP を持たない組織と緊急用の管理者アカウント用。パスワードはハッシュ、TOTP の鍵は Data Protection で暗号化した値だけを持つ';
COMMENT ON COLUMN local_account.password_hash IS 'Identity の PasswordHasher のハッシュ（PBKDF2）。未設定（リンクで設定する前・緊急用アカウント）は NULL';
COMMENT ON COLUMN local_account.authenticator_key_protected IS 'TOTP の共有鍵を Data Protection で暗号化した値（平文は持たない）';
-- 有効な人のアカウントは 1 人に 1 つ
CREATE UNIQUE INDEX local_account_person_key ON local_account (person_id) WHERE person_id IS NOT NULL AND disabled_at IS NULL;

CREATE TABLE local_account_passkey (
    credential_id       bytea       PRIMARY KEY,
    local_account_id    uuid        NOT NULL REFERENCES local_account (id),
    name                text,
    public_key          bytea       NOT NULL,
    sign_count          bigint      NOT NULL,
    transports          text[],
    is_user_verified    boolean     NOT NULL,
    is_backup_eligible  boolean     NOT NULL,
    is_backed_up        boolean     NOT NULL,
    attestation_object  bytea       NOT NULL,
    client_data_json    bytea       NOT NULL,
    created_at          timestamptz NOT NULL,
    -- WebAuthn の資格情報 ID は 16〜1023 バイト
    CONSTRAINT local_account_passkey_credential_id_check CHECK (octet_length(credential_id) BETWEEN 16 AND 1023),
    CONSTRAINT local_account_passkey_sign_count_check CHECK (sign_count BETWEEN 0 AND 4294967295),
    CONSTRAINT local_account_passkey_name_check CHECK (name IS NULL OR char_length(name) <= 200)
);
COMMENT ON TABLE local_account_passkey IS 'ローカルアカウントのパスキー（WebAuthn の公開鍵資格情報）。秘密鍵は持たない';
CREATE INDEX local_account_passkey_account_idx ON local_account_passkey (local_account_id);

CREATE TABLE local_account_recovery_code (
    id                uuid        PRIMARY KEY,
    local_account_id  uuid        NOT NULL REFERENCES local_account (id),
    code_sha256       text        NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    redeemed_at       timestamptz,
    CONSTRAINT local_account_recovery_code_sha256_format CHECK (code_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT local_account_recovery_code_key UNIQUE (local_account_id, code_sha256)
);
COMMENT ON TABLE local_account_recovery_code IS 'TOTP のリカバリコード（SHA-256 のハッシュだけ。使ったものは redeemed_at を付ける）';

-- ---------------------------------------------------------------------------
-- 4. 外部 IdP の主体と person の紐付け・紐付けできなかったログイン
-- ---------------------------------------------------------------------------

CREATE TABLE login_binding (
    id                     uuid        PRIMARY KEY,
    provider               text        NOT NULL,
    issuer                 text        NOT NULL,
    subject                text        NOT NULL,
    person_id              uuid        NOT NULL REFERENCES person (id),
    basis                  text        NOT NULL,
    created_by_kind        text        NOT NULL,
    created_by_person_id   uuid        REFERENCES person (id),
    created_by_account_id  uuid        REFERENCES local_account (id),
    created_at             timestamptz NOT NULL DEFAULT now(),
    revoked_by_kind        text,
    revoked_by_person_id   uuid        REFERENCES person (id),
    revoked_by_account_id  uuid        REFERENCES local_account (id),
    revoked_at             timestamptz,
    CONSTRAINT login_binding_provider_check CHECK (provider ~ '^[a-z][a-z0-9-]{0,31}$'),
    CONSTRAINT login_binding_issuer_check CHECK (btrim(issuer) <> '' AND char_length(issuer) <= 512),
    CONSTRAINT login_binding_subject_check CHECK (btrim(subject) <> '' AND char_length(subject) <= 256),
    -- 根拠: Entra の oid が取込の外部 ID の対応と一致 / 信頼する社員番号のクレームが一致 / 手動（人事管理者・緊急用アカウント・管理 CLI）
    CONSTRAINT login_binding_basis_check CHECK (basis IN ('entra_object_id', 'employee_number', 'manual')),
    -- 作成の主体: ログインの処理（自動）/ 人事管理者の人 / 緊急用アカウント / 管理 CLI。自動は手動以外の根拠、手動はそれ以外の主体
    CONSTRAINT login_binding_created_by_check CHECK (
        created_by_kind IN ('login', 'person', 'break_glass', 'cli')
        AND (created_by_kind = 'login') = (basis <> 'manual')
        AND (created_by_kind = 'person') = (created_by_person_id IS NOT NULL)
        AND (created_by_kind = 'break_glass') = (created_by_account_id IS NOT NULL)),
    CONSTRAINT login_binding_revoked_by_check CHECK (
        (revoked_at IS NULL) = (revoked_by_kind IS NULL)
        AND (revoked_by_kind IS NULL OR revoked_by_kind IN ('person', 'break_glass', 'cli'))
        AND (revoked_by_kind IS NOT DISTINCT FROM 'person') = (revoked_by_person_id IS NOT NULL)
        AND (revoked_by_kind IS NOT DISTINCT FROM 'break_glass') = (revoked_by_account_id IS NOT NULL))
);
COMMENT ON TABLE login_binding IS '外部 IdP の主体と person の紐付け（取込の external_identity とは別）。主体は発行者 + 識別子（Entra は tid を含む発行者 + oid、汎用 OIDC は iss + sub）。1 つの主体は 1 人だけ、1 人は複数の主体を持てる。変更は取り消して作り直す';
COMMENT ON COLUMN login_binding.provider IS '紐付けを作ったときのログイン方式の名前（設定 JinJi:Auth:Login:Providers の Name）。照合には使わない（発行者 + 主体で照合する）';
-- 有効な紐付けは主体ごとに 1 つ
CREATE UNIQUE INDEX login_binding_active_key ON login_binding (issuer, subject) WHERE revoked_at IS NULL;
CREATE INDEX login_binding_person_idx ON login_binding (person_id);

CREATE TRIGGER login_binding_guard BEFORE UPDATE ON login_binding
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_revocation('revoked_by_kind', 'revoked_by_person_id', 'revoked_by_account_id', 'revoked_at');

CREATE TABLE unbound_login (
    id                       uuid        PRIMARY KEY,
    provider                 text        NOT NULL,
    issuer                   text        NOT NULL,
    subject                  text        NOT NULL,
    tenant_id                text,
    claimed_employee_number  text,
    claimed_email            text,
    claimed_display_name     text,
    candidate_person_ids     uuid[]      NOT NULL DEFAULT '{}',
    attempt_count            integer     NOT NULL DEFAULT 1,
    first_attempted_at       timestamptz NOT NULL DEFAULT now(),
    last_attempted_at        timestamptz NOT NULL DEFAULT now(),
    resolved_at              timestamptz,
    resolved_binding_id      uuid        REFERENCES login_binding (id),
    CONSTRAINT unbound_login_provider_check CHECK (provider ~ '^[a-z][a-z0-9-]{0,31}$'),
    CONSTRAINT unbound_login_issuer_check CHECK (btrim(issuer) <> '' AND char_length(issuer) <= 512),
    CONSTRAINT unbound_login_subject_check CHECK (btrim(subject) <> '' AND char_length(subject) <= 256),
    -- IdP から来た値は長さを抑えて持つ（表示用）
    CONSTRAINT unbound_login_claims_length CHECK (
        char_length(coalesce(tenant_id, '')) <= 64
        AND char_length(coalesce(claimed_employee_number, '')) <= 64
        AND char_length(coalesce(claimed_email, '')) <= 256
        AND char_length(coalesce(claimed_display_name, '')) <= 256),
    CONSTRAINT unbound_login_candidates_check CHECK (array_position(candidate_person_ids, NULL) IS NULL AND cardinality(candidate_person_ids) <= 10),
    CONSTRAINT unbound_login_attempt_count_check CHECK (attempt_count >= 1),
    CONSTRAINT unbound_login_attempted_order CHECK (last_attempted_at >= first_attempted_at),
    CONSTRAINT unbound_login_resolved_check CHECK ((resolved_at IS NULL) = (resolved_binding_id IS NULL)),
    CONSTRAINT unbound_login_subject_key UNIQUE (issuer, subject)
);
COMMENT ON TABLE unbound_login IS '紐付けできずに拒否したログイン（主体ごとに 1 行。再試行で回数を数える）。人事管理者に示し、手動の紐付けで解決する。jin-ji は person を自動作成しない';
COMMENT ON COLUMN unbound_login.candidate_person_ids IS 'メールアドレスが社用メールと一致した人（候補として示すだけ。メールでは自動で紐付けない）';

-- ---------------------------------------------------------------------------
-- 5. 管理者が発行する 1 回限りのリンク
-- ---------------------------------------------------------------------------

CREATE TABLE account_reset_link (
    id                    uuid        PRIMARY KEY,
    local_account_id      uuid        NOT NULL REFERENCES local_account (id),
    purpose               text        NOT NULL,
    token_sha256          text        NOT NULL,
    issued_by_kind        text        NOT NULL,
    issued_by_person_id   uuid        REFERENCES person (id),
    issued_by_account_id  uuid        REFERENCES local_account (id),
    issued_at             timestamptz NOT NULL DEFAULT now(),
    expires_at            timestamptz NOT NULL,
    used_at               timestamptz,
    revoked_at            timestamptz,
    -- パスワードの設定・再設定 / パスキーの登録（緊急用アカウントの初期設定・再登録）
    CONSTRAINT account_reset_link_purpose_check CHECK (purpose IN ('password', 'passkey')),
    CONSTRAINT account_reset_link_token_format CHECK (token_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT account_reset_link_token_key UNIQUE (token_sha256),
    CONSTRAINT account_reset_link_issued_by_check CHECK (
        issued_by_kind IN ('cli', 'person', 'break_glass')
        AND (issued_by_kind = 'person') = (issued_by_person_id IS NOT NULL)
        AND (issued_by_kind = 'break_glass') = (issued_by_account_id IS NOT NULL)),
    -- 自分のリンクは発行できない（緊急用アカウントが自分の資格情報を足し続けられないようにする）
    CONSTRAINT account_reset_link_not_self CHECK (issued_by_account_id IS DISTINCT FROM local_account_id),
    -- 短命（最長 24 時間）
    CONSTRAINT account_reset_link_lifetime CHECK (expires_at > issued_at AND expires_at <= issued_at + interval '24 hours'),
    CONSTRAINT account_reset_link_used_check CHECK (used_at IS NULL OR (used_at >= issued_at AND used_at <= expires_at)),
    CONSTRAINT account_reset_link_single_end CHECK (used_at IS NULL OR revoked_at IS NULL)
);
COMMENT ON TABLE account_reset_link IS '管理者が発行する 1 回限り・短命のリンク（パスワードの設定・再設定、パスキーの登録）。鍵は SHA-256 のハッシュだけを持つ。新しいリンクを発行すると、同じアカウント・用途の未使用のリンクは取り消す';

CREATE FUNCTION jinji_guard_account_reset_link() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (to_jsonb(NEW) - ARRAY['used_at', 'revoked_at']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['used_at', 'revoked_at']) THEN
        RAISE EXCEPTION 'account_reset_link: 使用・取り消し以外の変更はできません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'account_reset_link_immutable';
    END IF;

    IF OLD.used_at IS NOT NULL OR OLD.revoked_at IS NOT NULL THEN
        RAISE EXCEPTION 'account_reset_link: 使用済み・取り消し済みのリンクは変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'account_reset_link_single_use';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_account_reset_link() IS 'account_reset_link の使用・取り消しを 1 回だけ許すトリガー関数（中身は変えられない）';

CREATE TRIGGER account_reset_link_guard BEFORE UPDATE ON account_reset_link
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_account_reset_link();

-- ---------------------------------------------------------------------------
-- 6. UI のセッション
-- ---------------------------------------------------------------------------

CREATE TABLE ui_session (
    id                uuid        PRIMARY KEY,
    key_sha256        text        NOT NULL,
    principal_kind    text        NOT NULL,
    person_id         uuid        REFERENCES person (id),
    local_account_id  uuid        REFERENCES local_account (id),
    login_binding_id  uuid        REFERENCES login_binding (id),
    provider          text        NOT NULL,
    methods           text[]      NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now(),
    last_seen_at      timestamptz NOT NULL DEFAULT now(),
    expires_at        timestamptz NOT NULL,
    ended_at          timestamptz,
    end_kind          text,
    CONSTRAINT ui_session_key_format CHECK (key_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ui_session_key_key UNIQUE (key_sha256),
    -- 人 / 緊急用アカウント（人に紐付かない）
    CONSTRAINT ui_session_principal_kind_check CHECK (principal_kind IN ('person', 'break_glass')),
    CONSTRAINT ui_session_person_check CHECK ((principal_kind = 'person') = (person_id IS NOT NULL)),
    -- ローカルアカウントのログインは provider = 'local'、外部 IdP のログインは紐付けを持つ
    CONSTRAINT ui_session_provider_check CHECK (provider ~ '^[a-z][a-z0-9-]{0,31}$'),
    CONSTRAINT ui_session_origin_check CHECK (
        (provider = 'local') = (local_account_id IS NOT NULL)
        AND (provider = 'local') = (login_binding_id IS NULL)
        AND (principal_kind <> 'break_glass' OR local_account_id IS NOT NULL)),
    -- 認証の方法（RFC 8176 の amr に倣う: pwd / otp / rc〔リカバリコード〕/ hwk〔パスキー〕/ fed〔外部 IdP〕）
    CONSTRAINT ui_session_methods_check CHECK (
        cardinality(methods) BETWEEN 1 AND 4 AND methods <@ ARRAY['pwd', 'otp', 'rc', 'hwk', 'fed']::text[]),
    CONSTRAINT ui_session_lifetime CHECK (expires_at > created_at AND expires_at <= created_at + interval '24 hours'),
    CONSTRAINT ui_session_seen_order CHECK (last_seen_at >= created_at),
    -- 終了: ログアウト / 無操作・最長の期限 / 人が退職・一時停止 / アカウントの無効化・資格情報の変更 / 紐付けの取り消し / 同じブラウザでの再ログイン
    CONSTRAINT ui_session_end_check CHECK (
        (ended_at IS NULL) = (end_kind IS NULL)
        AND (end_kind IS NULL OR end_kind IN (
            'logout', 'idle_timeout', 'expired', 'person_inactive', 'account_disabled', 'credential_changed', 'binding_revoked', 'replaced')))
);
COMMENT ON TABLE ui_session IS 'UI（BFF）のセッション。Cookie には乱数の鍵（Data Protection で保護）だけを持たせ、ここには鍵の SHA-256 を持つ。無操作・最長の期限、退職・一時停止、紐付けの取り消しは要求ごとに検査する';
CREATE INDEX ui_session_person_idx ON ui_session (person_id) WHERE ended_at IS NULL;
CREATE INDEX ui_session_account_idx ON ui_session (local_account_id) WHERE ended_at IS NULL;
CREATE INDEX ui_session_binding_idx ON ui_session (login_binding_id) WHERE ended_at IS NULL;

CREATE FUNCTION jinji_guard_ui_session() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (to_jsonb(NEW) - ARRAY['last_seen_at', 'ended_at', 'end_kind']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['last_seen_at', 'ended_at', 'end_kind']) THEN
        RAISE EXCEPTION 'ui_session: 最終利用日時・終了以外の変更はできません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'ui_session_immutable';
    END IF;

    IF OLD.ended_at IS NOT NULL THEN
        RAISE EXCEPTION 'ui_session: 終了したセッションは変更できません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'ui_session_ended';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_ui_session() IS 'ui_session の変更を最終利用日時の更新と 1 回の終了だけに限るトリガー関数（終了したセッションは生き返らない）';

CREATE TRIGGER ui_session_guard BEFORE UPDATE ON ui_session
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_ui_session();

-- ---------------------------------------------------------------------------
-- 7. Data Protection の鍵
-- ---------------------------------------------------------------------------

CREATE TABLE data_protection_key (
    id             uuid        PRIMARY KEY,
    friendly_name  text,
    xml            text        NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT data_protection_key_xml_length CHECK (char_length(xml) BETWEEN 1 AND 65536),
    CONSTRAINT data_protection_key_friendly_name_length CHECK (friendly_name IS NULL OR char_length(friendly_name) <= 256)
);
COMMENT ON TABLE data_protection_key IS 'ASP.NET Core Data Protection の鍵リング（IXmlRepository）。鍵は証明書で暗号化した XML で入れる（証明書なしは開発時の明示的な設定だけ）。追記だけ';

-- ---------------------------------------------------------------------------
-- 8. 監査ログ: 緊急用アカウントの操作者
-- ---------------------------------------------------------------------------

ALTER TABLE audit_event ADD COLUMN actor_account_id uuid;
ALTER TABLE audit_event DROP CONSTRAINT audit_event_actor_type_check;
ALTER TABLE audit_event ADD CONSTRAINT audit_event_actor_type_check CHECK (actor_type IN ('user', 'service', 'agent', 'system', 'break_glass'));
-- 緊急用アカウントの操作は、どのアカウントかを必ず持つ
ALTER TABLE audit_event ADD CONSTRAINT audit_event_break_glass_account CHECK (actor_type <> 'break_glass' OR actor_account_id IS NOT NULL);
COMMENT ON COLUMN audit_event.actor_account_id IS '操作者のローカルアカウント（緊急用の管理者アカウントの操作と、ローカルアカウントのログイン）。person への外部キーと同じ理由で外部キーは張らない';

-- ---------------------------------------------------------------------------
-- 9. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

ALTER TABLE role_grant ENABLE ROW LEVEL SECURITY;
ALTER TABLE role_grant FORCE ROW LEVEL SECURITY;
ALTER TABLE local_account ENABLE ROW LEVEL SECURITY;
ALTER TABLE local_account FORCE ROW LEVEL SECURITY;
ALTER TABLE local_account_passkey ENABLE ROW LEVEL SECURITY;
ALTER TABLE local_account_passkey FORCE ROW LEVEL SECURITY;
ALTER TABLE local_account_recovery_code ENABLE ROW LEVEL SECURITY;
ALTER TABLE local_account_recovery_code FORCE ROW LEVEL SECURITY;
ALTER TABLE login_binding ENABLE ROW LEVEL SECURITY;
ALTER TABLE login_binding FORCE ROW LEVEL SECURITY;
ALTER TABLE unbound_login ENABLE ROW LEVEL SECURITY;
ALTER TABLE unbound_login FORCE ROW LEVEL SECURITY;
ALTER TABLE account_reset_link ENABLE ROW LEVEL SECURITY;
ALTER TABLE account_reset_link FORCE ROW LEVEL SECURITY;
ALTER TABLE ui_session ENABLE ROW LEVEL SECURITY;
ALTER TABLE ui_session FORCE ROW LEVEL SECURITY;
ALTER TABLE data_protection_key ENABLE ROW LEVEL SECURITY;
ALTER TABLE data_protection_key FORCE ROW LEVEL SECURITY;

-- ロールの付与: 参照は jin-ji 自身（セッションの検証）と人事管理者の人。付与・取り消しは管理 CLI か、人事管理者の人（本人として記録）
CREATE POLICY role_grant_select ON role_grant FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()));
CREATE POLICY role_grant_insert ON role_grant FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_system()) AND granted_by_kind = 'cli')
    OR ((SELECT jinji_viewer_is_hr_admin_user()) AND granted_by_kind = 'person' AND granted_by_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY role_grant_update ON role_grant FOR UPDATE
    USING ((SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()))
    WITH CHECK (
        ((SELECT jinji_viewer_is_system()) AND revoked_by_kind = 'cli')
        OR ((SELECT jinji_viewer_is_hr_admin_user()) AND revoked_by_kind = 'person' AND revoked_by_person_id = (SELECT jinji_viewer_person_id())));

-- ローカルアカウント: 作成は jin-ji 自身（管理 CLI）だけ。参照と更新（ロックの解除・資格情報の変更）は認証の復旧を扱える閲覧者
CREATE POLICY local_account_select ON local_account FOR SELECT USING ((SELECT jinji_viewer_can_recover_auth()));
CREATE POLICY local_account_insert ON local_account FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY local_account_update ON local_account FOR UPDATE
    USING ((SELECT jinji_viewer_can_recover_auth())) WITH CHECK ((SELECT jinji_viewer_can_recover_auth()));

-- パスキー・リカバリコード: jin-ji 自身（ログイン・登録の処理）だけ
CREATE POLICY local_account_passkey_all ON local_account_passkey FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY local_account_recovery_code_all ON local_account_recovery_code FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));

-- 紐付け: 自動の紐付けと管理 CLI は jin-ji 自身、手動は人事管理者の人・緊急用アカウント（本人として記録）
CREATE POLICY login_binding_select ON login_binding FOR SELECT USING ((SELECT jinji_viewer_can_recover_auth()));
CREATE POLICY login_binding_insert ON login_binding FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_system()) AND created_by_kind IN ('login', 'cli'))
    OR ((SELECT jinji_viewer_is_hr_admin_user()) AND created_by_kind = 'person' AND created_by_person_id = (SELECT jinji_viewer_person_id()))
    OR ((SELECT jinji_viewer_is_break_glass()) AND created_by_kind = 'break_glass'));
CREATE POLICY login_binding_update ON login_binding FOR UPDATE
    USING ((SELECT jinji_viewer_can_recover_auth()))
    WITH CHECK (
        ((SELECT jinji_viewer_is_system()) AND revoked_by_kind = 'cli')
        OR ((SELECT jinji_viewer_is_hr_admin_user()) AND revoked_by_kind = 'person' AND revoked_by_person_id = (SELECT jinji_viewer_person_id()))
        OR ((SELECT jinji_viewer_is_break_glass()) AND revoked_by_kind = 'break_glass'));

-- 未紐付けのログイン: 記録は jin-ji 自身（ログインの処理）、参照と解決は認証の復旧を扱える閲覧者
CREATE POLICY unbound_login_select ON unbound_login FOR SELECT USING ((SELECT jinji_viewer_can_recover_auth()));
CREATE POLICY unbound_login_insert ON unbound_login FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY unbound_login_update ON unbound_login FOR UPDATE
    USING ((SELECT jinji_viewer_can_recover_auth())) WITH CHECK ((SELECT jinji_viewer_can_recover_auth()));

-- リンク: 発行は管理 CLI（jin-ji 自身）・人事管理者の人・緊急用アカウント（本人として記録）。使用は jin-ji 自身（未認証の要求の処理）
CREATE POLICY account_reset_link_select ON account_reset_link FOR SELECT USING ((SELECT jinji_viewer_can_recover_auth()));
CREATE POLICY account_reset_link_insert ON account_reset_link FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_system()) AND issued_by_kind = 'cli')
    OR ((SELECT jinji_viewer_is_hr_admin_user()) AND issued_by_kind = 'person' AND issued_by_person_id = (SELECT jinji_viewer_person_id()))
    OR ((SELECT jinji_viewer_is_break_glass()) AND issued_by_kind = 'break_glass'));
CREATE POLICY account_reset_link_update ON account_reset_link FOR UPDATE
    USING ((SELECT jinji_viewer_can_recover_auth())) WITH CHECK ((SELECT jinji_viewer_can_recover_auth()));

-- セッション・Data Protection の鍵: jin-ji 自身だけ
CREATE POLICY ui_session_all ON ui_session FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY data_protection_key_select ON data_protection_key FOR SELECT USING ((SELECT jinji_viewer_is_system()));
CREATE POLICY data_protection_key_insert ON data_protection_key FOR INSERT WITH CHECK ((SELECT jinji_viewer_is_system()));

-- 監査ログ: 緊急用アカウントは自分の操作だけを追記できる（参照はできない）
CREATE POLICY audit_event_insert_break_glass ON audit_event FOR INSERT WITH CHECK (
    (SELECT jinji_viewer_is_break_glass()) AND actor_type = 'break_glass');

-- ---------------------------------------------------------------------------
-- 10. アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT ON
    role_grant, local_account, local_account_passkey, local_account_recovery_code,
    login_binding, unbound_login, account_reset_link, ui_session, data_protection_key
    TO jinji_app;
GRANT UPDATE (revoked_by_kind, revoked_by_person_id, revoked_at) ON role_grant TO jinji_app;
GRANT UPDATE (password_hash, security_stamp, concurrency_stamp, lockout_end, access_failed_count, two_factor_enabled,
              authenticator_key_protected, disabled_at) ON local_account TO jinji_app;
GRANT UPDATE (name, sign_count, is_user_verified, is_backup_eligible, is_backed_up) ON local_account_passkey TO jinji_app;
GRANT UPDATE (redeemed_at) ON local_account_recovery_code TO jinji_app;
-- パスキーの削除とリカバリコードの作り直し（Identity のストアの操作）だけは行を消す
GRANT DELETE ON local_account_passkey, local_account_recovery_code TO jinji_app;
GRANT UPDATE (revoked_by_kind, revoked_by_person_id, revoked_by_account_id, revoked_at) ON login_binding TO jinji_app;
GRANT UPDATE (provider, tenant_id, claimed_employee_number, claimed_email, claimed_display_name, candidate_person_ids,
              attempt_count, last_attempted_at, resolved_at, resolved_binding_id) ON unbound_login TO jinji_app;
GRANT UPDATE (used_at, revoked_at) ON account_reset_link TO jinji_app;
GRANT UPDATE (last_seen_at, ended_at, end_kind) ON ui_session TO jinji_app;
