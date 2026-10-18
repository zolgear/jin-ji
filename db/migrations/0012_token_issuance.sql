-- 0012: API・MCP 向けのトークン発行（ADR-0004 の段階 B。自前の最小の認可サーバー）
--
-- 内容
-- 1. クライアント（api_client）。既定で拒否の登録制。種類（事前登録 / CIMD）・認証の方式・許す付与・redirect_uri・
--    resource（REST API / MCP）・スコープ・項目の上限・MCP ツールの許可リスト・レート制限・状態を持つ。
--    設定ファイルのクライアント（JinJi:Auth:Clients）は外部の発行者（Entra ID など）の JWT 用として残し、jin-ji が発行するトークンのクライアントはこの表で持つ
-- 2. クライアントの公開鍵（api_client_key。private_key_jwt）と、移行用の期限付きの client_secret（api_client_secret。SHA-256 だけ）
-- 3. CIMD（Client ID Metadata Document）の取得を許すドメイン（cimd_allowed_domain）。設定の既定の許可リストに足すもの。
--    許可外の client_id は api_client に「管理者の承認待ち」（status = pending_approval）として記録し、承認されるまで取得も認可もしない
-- 4. 認可の要求（oauth_authorization_request）。/oauth/authorize の検査を通った要求をサーバー側に保存し、短い乱数の ID で同意画面・ログインに渡す
-- 5. 認可コード（oauth_authorization_code）。60 秒・単回。client・redirect_uri・resource・PKCE・スコープ・人に束縛する
-- 6. トークンの系列（oauth_token_family）・アクセストークン（oauth_access_token。参照トークン、10 分、resource に束縛）・
--    リフレッシュトークン（oauth_refresh_token。MCP だけ。毎回回転し、使用済みの再利用を検知したら系列ごと失効）
-- 7. 同意の記録（oauth_consent）と、client assertion の jti（oauth_client_assertion。再利用の拒否）
--
-- 閲覧者（GUC）と RLS
-- - トークンの発行・検証・CIMD の取得（jin-ji 自身）は jinji.viewer_kind = 'system'（ロールなし）で行う
-- - クライアント・鍵・シークレット・CIMD の許可ドメインの管理は、管理 CLI（system）か人事管理者の人（user + hr_admin。本人として記録）。
--   AI エージェント・社内システムは、委任元が人事管理者でも扱えない（クライアントの管理はエージェントにさせない）。緊急用アカウントも扱えない（認証の復旧ではない）
-- - コード・トークン・同意・jti は jin-ji 自身だけ。トークン・コード・シークレットは 256 ビットの乱数で、SHA-256 だけを保存する

-- ---------------------------------------------------------------------------
-- 1. 共通: 1 回だけ記録できる列
-- ---------------------------------------------------------------------------

-- jinji_guard_revocation（0011）は revoked_at の列で「取り消し済み」を見るので、名前の違う列（終了・使用）用に一般化したもの
CREATE FUNCTION jinji_guard_set_once() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    columns CONSTANT text[] := TG_ARGV::text[];
BEGIN
    IF (to_jsonb(NEW) - columns) IS DISTINCT FROM (to_jsonb(OLD) - columns) THEN
        RAISE EXCEPTION '%: % 以外の変更はできません（id=%）', TG_TABLE_NAME, array_to_string(columns, ', '), OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'set_once';
    END IF;

    -- 最初の列（終了・使用・取り消しの日時）が記録済みなら、もう変えられない（TG_ARGV の添字は 0 から）
    IF (to_jsonb(OLD) ->> columns[array_lower(columns, 1)]) IS NOT NULL THEN
        RAISE EXCEPTION '%: 記録済み（%）の行は変更できません（id=%）', TG_TABLE_NAME, columns[array_lower(columns, 1)], OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'set_once';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_set_once() IS '引数の列だけを 1 回だけ記録できるようにするトリガー関数（最初の引数の列が NULL でなくなったら行は不変）';

-- ---------------------------------------------------------------------------
-- 2. クライアント
-- ---------------------------------------------------------------------------

CREATE TABLE api_client (
    id                     uuid        PRIMARY KEY,
    client_id              text        NOT NULL,
    registration           text        NOT NULL,
    status                 text        NOT NULL,
    display_name           text,
    auth_method            text        NOT NULL,
    grant_types            text[]      NOT NULL,
    redirect_uris          text[]      NOT NULL DEFAULT '{}',
    resources              text[]      NOT NULL,
    scopes                 text[]      NOT NULL,
    fields                 text[]      NOT NULL,
    mcp_tools              text[]      NOT NULL DEFAULT '{}',
    rate_limit_per_client  integer,
    metadata_fetched_at    timestamptz,
    metadata_expires_at    timestamptz,
    created_by_kind        text        NOT NULL,
    created_by_person_id   uuid        REFERENCES person (id),
    created_at             timestamptz NOT NULL DEFAULT now(),
    updated_by_kind        text,
    updated_by_person_id   uuid        REFERENCES person (id),
    updated_at             timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT api_client_client_id_key UNIQUE (client_id),
    -- 事前登録（jin-ji が払い出す ID）/ CIMD（client_id がメタデータ文書の https の URL）
    CONSTRAINT api_client_registration_check CHECK (registration IN ('registered', 'cimd')),
    -- 事前登録の ID は英小文字・数字で始まる 3〜64 文字（jinji-ui は jin-ji 自身の UI の予約語）。CIMD はフラグメントを持たない https の URL
    CONSTRAINT api_client_client_id_check CHECK (
        CASE registration
            WHEN 'registered' THEN client_id ~ '^[a-z0-9][a-z0-9._-]{2,63}$' AND client_id <> 'jinji-ui'
            ELSE client_id ~ '^https://[^#[:space:]]+$' AND char_length(client_id) <= 2048
        END),
    -- 承認待ちは CIMD だけ（許可外のドメインの client_id）
    CONSTRAINT api_client_status_check CHECK (
        status IN ('active', 'pending_approval', 'suspended', 'rejected')
        AND (status <> 'pending_approval' OR registration = 'cimd')),
    CONSTRAINT api_client_display_name_check CHECK (display_name IS NULL OR (btrim(display_name) <> '' AND char_length(display_name) <= 200)),
    -- 公開クライアント（none）/ private_key_jwt。client_secret は機密クライアントの移行用の例外として api_client_secret に持つ
    CONSTRAINT api_client_auth_method_check CHECK (auth_method IN ('none', 'private_key_jwt')),
    CONSTRAINT api_client_grant_types_check CHECK (
        cardinality(grant_types) >= 1
        AND grant_types <@ ARRAY['authorization_code', 'refresh_token', 'client_credentials']::text[]
        AND array_position(grant_types, NULL) IS NULL
        -- リフレッシュトークンは認可コードの付与にだけ伴う。client credentials は機密クライアントだけ
        AND (NOT 'refresh_token' = ANY (grant_types) OR 'authorization_code' = ANY (grant_types))
        AND (NOT 'client_credentials' = ANY (grant_types) OR auth_method <> 'none')),
    -- CIMD は公開クライアントの認可コードだけ（文書の取得前の承認待ちは redirect_uri を持たない）
    CONSTRAINT api_client_cimd_check CHECK (
        registration <> 'cimd' OR (auth_method = 'none' AND NOT 'client_credentials' = ANY (grant_types))),
    CONSTRAINT api_client_redirect_uris_check CHECK (
        cardinality(redirect_uris) <= 20
        AND array_position(redirect_uris, NULL) IS NULL
        AND (registration = 'cimd' OR NOT 'authorization_code' = ANY (grant_types) OR cardinality(redirect_uris) >= 1)),
    CONSTRAINT api_client_resources_check CHECK (
        cardinality(resources) >= 1 AND resources <@ ARRAY['api', 'mcp']::text[] AND array_position(resources, NULL) IS NULL),
    CONSTRAINT api_client_scopes_check CHECK (
        cardinality(scopes) >= 1 AND scopes <@ ARRAY['jinji.read', 'jinji.propose']::text[] AND array_position(scopes, NULL) IS NULL),
    -- 項目名の検査はアプリ（PersonFields）。["*"] は上限なし
    CONSTRAINT api_client_fields_check CHECK (cardinality(fields) BETWEEN 1 AND 64 AND array_position(fields, NULL) IS NULL),
    CONSTRAINT api_client_mcp_tools_check CHECK (cardinality(mcp_tools) <= 200 AND array_position(mcp_tools, NULL) IS NULL),
    CONSTRAINT api_client_rate_limit_check CHECK (rate_limit_per_client IS NULL OR rate_limit_per_client BETWEEN 1 AND 1000000),
    CONSTRAINT api_client_metadata_check CHECK (
        (registration = 'cimd' OR (metadata_fetched_at IS NULL AND metadata_expires_at IS NULL))
        AND (metadata_fetched_at IS NULL) = (metadata_expires_at IS NULL)
        AND (metadata_expires_at IS NULL OR metadata_expires_at >= metadata_fetched_at)),
    -- 作成の主体: 管理 CLI / 人事管理者の人 / CIMD の取得（jin-ji 自身。許可したドメインの文書、または承認待ちの記録）
    CONSTRAINT api_client_created_by_check CHECK (
        created_by_kind IN ('cli', 'person', 'cimd')
        AND (created_by_kind = 'person') = (created_by_person_id IS NOT NULL)
        AND (created_by_kind <> 'cimd' OR registration = 'cimd')),
    CONSTRAINT api_client_updated_by_check CHECK (
        (updated_by_kind IS NULL OR updated_by_kind IN ('cli', 'person', 'cimd'))
        AND (updated_by_kind IS NOT DISTINCT FROM 'person') = (updated_by_person_id IS NOT NULL))
);
COMMENT ON TABLE api_client IS 'jin-ji が発行するトークンのクライアント（既定で拒否の登録制）。事前登録と CIMD。渡してよい項目の上限・スコープ・MCP ツールの許可リスト・レート制限はトークンではなくこの登録で決める';
COMMENT ON COLUMN api_client.client_id IS 'OAuth の client_id。事前登録は jin-ji が払い出す ID、CIMD はメタデータ文書の URL';
COMMENT ON COLUMN api_client.fields IS '渡してよい項目の上限（PersonFields の名前。["*"] は上限なし）。閲覧者の実効権限は「委任元の権限 ∩ スコープ ∩ この上限」';
COMMENT ON COLUMN api_client.metadata_expires_at IS 'CIMD の文書の HTTP キャッシュの期限。過ぎたら次の認可の要求で取り直す';

CREATE FUNCTION jinji_guard_api_client() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.id IS DISTINCT FROM OLD.id OR NEW.client_id IS DISTINCT FROM OLD.client_id OR NEW.registration IS DISTINCT FROM OLD.registration
       OR NEW.created_by_kind IS DISTINCT FROM OLD.created_by_kind OR NEW.created_by_person_id IS DISTINCT FROM OLD.created_by_person_id
       OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'api_client: ID・client_id・種類・作成の記録は変えられません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'api_client_immutable';
    END IF;

    -- 却下した CIMD のクライアントは承認に戻さない（同じ URL を承認するなら管理者が改めて許可する。rejected → active は承認の操作として許す）
    IF OLD.status = 'pending_approval' AND NEW.status NOT IN ('pending_approval', 'active', 'rejected') THEN
        RAISE EXCEPTION 'api_client: 承認待ちのクライアントは承認か却下しかできません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'api_client_transition';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_api_client() IS 'api_client の ID・client_id・種類・作成の記録を不変にし、承認待ちからの遷移を承認・却下に限るトリガー関数';

CREATE TRIGGER api_client_guard BEFORE UPDATE ON api_client
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_api_client();

-- ---------------------------------------------------------------------------
-- 3. クライアントの公開鍵・移行用の client_secret
-- ---------------------------------------------------------------------------

CREATE TABLE api_client_key (
    id                    uuid        PRIMARY KEY,
    client_ref            uuid        NOT NULL REFERENCES api_client (id),
    key_id                text        NOT NULL,
    public_key_pem        text        NOT NULL,
    created_by_kind       text        NOT NULL,
    created_by_person_id  uuid        REFERENCES person (id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    revoked_by_kind       text,
    revoked_by_person_id  uuid        REFERENCES person (id),
    revoked_at            timestamptz,
    CONSTRAINT api_client_key_key_id_check CHECK (key_id ~ '^[A-Za-z0-9._-]{1,128}$'),
    -- RSA か EC の SubjectPublicKeyInfo の PEM（秘密鍵は受け取らない。形の検査はアプリ）
    CONSTRAINT api_client_key_pem_check CHECK (
        public_key_pem LIKE '-----BEGIN PUBLIC KEY-----%' AND char_length(public_key_pem) <= 8192),
    CONSTRAINT api_client_key_created_by_check CHECK (
        created_by_kind IN ('cli', 'person') AND (created_by_kind = 'person') = (created_by_person_id IS NOT NULL)),
    CONSTRAINT api_client_key_revoked_by_check CHECK (
        (revoked_at IS NULL) = (revoked_by_kind IS NULL)
        AND (revoked_by_kind IS NULL OR revoked_by_kind IN ('cli', 'person'))
        AND (revoked_by_kind IS NOT DISTINCT FROM 'person') = (revoked_by_person_id IS NOT NULL))
);
COMMENT ON TABLE api_client_key IS 'クライアントの公開鍵（private_key_jwt の client assertion の検証に使う）。鍵の入れ替えのため複数持てる。取り消しは 1 回だけで中身は変えられない';
CREATE UNIQUE INDEX api_client_key_active_key ON api_client_key (client_ref, key_id) WHERE revoked_at IS NULL;

CREATE TRIGGER api_client_key_guard BEFORE UPDATE ON api_client_key
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_revocation('revoked_by_kind', 'revoked_by_person_id', 'revoked_at');

CREATE TABLE api_client_secret (
    id                    uuid        PRIMARY KEY,
    client_ref            uuid        NOT NULL REFERENCES api_client (id),
    secret_sha256         text        NOT NULL,
    created_by_kind       text        NOT NULL,
    created_by_person_id  uuid        REFERENCES person (id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    expires_at            timestamptz NOT NULL,
    revoked_by_kind       text,
    revoked_by_person_id  uuid        REFERENCES person (id),
    revoked_at            timestamptz,
    CONSTRAINT api_client_secret_format CHECK (secret_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT api_client_secret_key UNIQUE (secret_sha256),
    -- 移行用の例外なので有効期限は必須で、最長 1 年
    CONSTRAINT api_client_secret_lifetime CHECK (expires_at > created_at AND expires_at <= created_at + interval '366 days'),
    CONSTRAINT api_client_secret_created_by_check CHECK (
        created_by_kind IN ('cli', 'person') AND (created_by_kind = 'person') = (created_by_person_id IS NOT NULL)),
    CONSTRAINT api_client_secret_revoked_by_check CHECK (
        (revoked_at IS NULL) = (revoked_by_kind IS NULL)
        AND (revoked_by_kind IS NULL OR revoked_by_kind IN ('cli', 'person'))
        AND (revoked_by_kind IS NOT DISTINCT FROM 'person') = (revoked_by_person_id IS NOT NULL))
);
COMMENT ON TABLE api_client_secret IS '移行用の期限付き client_secret（private_key_jwt へ移るまでの例外。SHA-256 だけを持つ）。比較は定数時間';
CREATE INDEX api_client_secret_client_idx ON api_client_secret (client_ref) WHERE revoked_at IS NULL;

CREATE TRIGGER api_client_secret_guard BEFORE UPDATE ON api_client_secret
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_revocation('revoked_by_kind', 'revoked_by_person_id', 'revoked_at');

-- ---------------------------------------------------------------------------
-- 4. CIMD の取得を許すドメイン
-- ---------------------------------------------------------------------------

CREATE TABLE cimd_allowed_domain (
    id                    uuid        PRIMARY KEY,
    domain                text        NOT NULL,
    created_by_kind       text        NOT NULL,
    created_by_person_id  uuid        REFERENCES person (id),
    created_at            timestamptz NOT NULL DEFAULT now(),
    revoked_by_kind       text,
    revoked_by_person_id  uuid        REFERENCES person (id),
    revoked_at            timestamptz,
    -- ホスト名（小文字）。サブドメインは含めない（完全一致で照合する）
    CONSTRAINT cimd_allowed_domain_domain_check CHECK (
        domain ~ '^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+$' AND char_length(domain) <= 253),
    CONSTRAINT cimd_allowed_domain_created_by_check CHECK (
        created_by_kind IN ('cli', 'person') AND (created_by_kind = 'person') = (created_by_person_id IS NOT NULL)),
    CONSTRAINT cimd_allowed_domain_revoked_by_check CHECK (
        (revoked_at IS NULL) = (revoked_by_kind IS NULL)
        AND (revoked_by_kind IS NULL OR revoked_by_kind IN ('cli', 'person'))
        AND (revoked_by_kind IS NOT DISTINCT FROM 'person') = (revoked_by_person_id IS NOT NULL))
);
COMMENT ON TABLE cimd_allowed_domain IS 'CIMD の文書の取得を許すドメイン（設定 JinJi:Auth:Server:Cimd:AllowedDomains の既定に足すもの）。ホスト名の完全一致で照合する';
CREATE UNIQUE INDEX cimd_allowed_domain_active_key ON cimd_allowed_domain (domain) WHERE revoked_at IS NULL;

CREATE TRIGGER cimd_allowed_domain_guard BEFORE UPDATE ON cimd_allowed_domain
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_revocation('revoked_by_kind', 'revoked_by_person_id', 'revoked_at');

-- ---------------------------------------------------------------------------
-- 5. 認可の要求・認可コード
-- ---------------------------------------------------------------------------

CREATE TABLE oauth_authorization_request (
    id              uuid        PRIMARY KEY,
    handle_sha256   text        NOT NULL,
    client_ref      uuid        NOT NULL REFERENCES api_client (id),
    redirect_uri    text        NOT NULL,
    resource        text        NOT NULL,
    scopes          text[]      NOT NULL,
    state           text,
    code_challenge  text        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    decided_at      timestamptz,
    decision        text,
    person_id       uuid        REFERENCES person (id),
    CONSTRAINT oauth_authorization_request_handle_format CHECK (handle_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT oauth_authorization_request_handle_key UNIQUE (handle_sha256),
    CONSTRAINT oauth_authorization_request_redirect_uri_check CHECK (char_length(redirect_uri) BETWEEN 1 AND 2048),
    CONSTRAINT oauth_authorization_request_resource_check CHECK (resource IN ('api', 'mcp')),
    CONSTRAINT oauth_authorization_request_scopes_check CHECK (
        cardinality(scopes) >= 1 AND scopes <@ ARRAY['jinji.read', 'jinji.propose']::text[]),
    CONSTRAINT oauth_authorization_request_state_check CHECK (state IS NULL OR char_length(state) <= 1024),
    -- PKCE は S256 だけ（base64url の 43 文字）
    CONSTRAINT oauth_authorization_request_challenge_check CHECK (code_challenge ~ '^[A-Za-z0-9_-]{43}$'),
    CONSTRAINT oauth_authorization_request_lifetime CHECK (expires_at > created_at AND expires_at <= created_at + interval '1 hour'),
    CONSTRAINT oauth_authorization_request_decision_check CHECK (
        (decided_at IS NULL) = (decision IS NULL)
        AND (decision IS NULL OR decision IN ('approved', 'denied'))
        AND (decided_at IS NULL) = (person_id IS NULL))
);
COMMENT ON TABLE oauth_authorization_request IS '/oauth/authorize の検査を通った認可の要求。同意画面・ログインには短い乱数の ID（SHA-256 だけを保存）で渡す。同意・拒否は 1 回だけ';

CREATE TABLE oauth_authorization_code (
    id              uuid        PRIMARY KEY,
    code_sha256     text        NOT NULL,
    request_id      uuid        NOT NULL REFERENCES oauth_authorization_request (id),
    client_ref      uuid        NOT NULL REFERENCES api_client (id),
    person_id       uuid        NOT NULL REFERENCES person (id),
    redirect_uri    text        NOT NULL,
    resource        text        NOT NULL,
    scopes          text[]      NOT NULL,
    code_challenge  text        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,
    used_at         timestamptz,
    family_id       uuid,
    CONSTRAINT oauth_authorization_code_format CHECK (code_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT oauth_authorization_code_key UNIQUE (code_sha256),
    CONSTRAINT oauth_authorization_code_request_key UNIQUE (request_id),
    CONSTRAINT oauth_authorization_code_resource_check CHECK (resource IN ('api', 'mcp')),
    CONSTRAINT oauth_authorization_code_scopes_check CHECK (
        cardinality(scopes) >= 1 AND scopes <@ ARRAY['jinji.read', 'jinji.propose']::text[]),
    CONSTRAINT oauth_authorization_code_challenge_check CHECK (code_challenge ~ '^[A-Za-z0-9_-]{43}$'),
    -- 短命（既定 60 秒。最長 10 分）
    CONSTRAINT oauth_authorization_code_lifetime CHECK (expires_at > created_at AND expires_at <= created_at + interval '10 minutes'),
    CONSTRAINT oauth_authorization_code_family_check CHECK (family_id IS NULL OR used_at IS NOT NULL)
);
COMMENT ON TABLE oauth_authorization_code IS '認可コード（SHA-256 だけ）。単回使用で、client・redirect_uri・resource・PKCE・スコープ・人に束縛する。使用済みのコードが再び使われたら、そのコードから発行した系列を失効する';

-- 使用・発行した系列の記録だけを 1 回許す
CREATE FUNCTION jinji_guard_oauth_authorization_code() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (to_jsonb(NEW) - ARRAY['used_at', 'family_id']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['used_at', 'family_id'])
       OR (OLD.used_at IS NOT NULL AND NEW.used_at IS DISTINCT FROM OLD.used_at)
       OR (OLD.family_id IS NOT NULL AND NEW.family_id IS DISTINCT FROM OLD.family_id) THEN
        RAISE EXCEPTION 'oauth_authorization_code: 使用の記録以外の変更・使用済みの取り消しはできません（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'oauth_authorization_code_single_use';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_oauth_authorization_code() IS '認可コードの使用（used_at・family_id）を 1 回だけ記録でき、中身は変えられないようにするトリガー関数';

CREATE TRIGGER oauth_authorization_code_guard BEFORE UPDATE ON oauth_authorization_code
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_oauth_authorization_code();

CREATE FUNCTION jinji_guard_oauth_authorization_request() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (to_jsonb(NEW) - ARRAY['decided_at', 'decision', 'person_id']) IS DISTINCT FROM (to_jsonb(OLD) - ARRAY['decided_at', 'decision', 'person_id'])
       OR OLD.decided_at IS NOT NULL THEN
        RAISE EXCEPTION 'oauth_authorization_request: 同意・拒否の記録を 1 回だけ許します（id=%）', OLD.id
            USING ERRCODE = 'integrity_constraint_violation', CONSTRAINT = 'oauth_authorization_request_single_decision';
    END IF;

    RETURN NEW;
END
$$;
COMMENT ON FUNCTION jinji_guard_oauth_authorization_request() IS '認可の要求の同意・拒否を 1 回だけ記録でき、中身は変えられないようにするトリガー関数';

CREATE TRIGGER oauth_authorization_request_guard BEFORE UPDATE ON oauth_authorization_request
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_oauth_authorization_request();

-- ---------------------------------------------------------------------------
-- 6. トークン
-- ---------------------------------------------------------------------------

CREATE TABLE oauth_token_family (
    id           uuid        PRIMARY KEY,
    client_ref   uuid        NOT NULL REFERENCES api_client (id),
    person_id    uuid        NOT NULL REFERENCES person (id),
    resource     text        NOT NULL,
    scopes       text[]      NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    expires_at   timestamptz NOT NULL,
    ended_at     timestamptz,
    end_kind     text,
    CONSTRAINT oauth_token_family_resource_check CHECK (resource IN ('api', 'mcp')),
    CONSTRAINT oauth_token_family_scopes_check CHECK (
        cardinality(scopes) >= 1 AND scopes <@ ARRAY['jinji.read', 'jinji.propose']::text[]),
    -- 系列の最長の期限（既定 30 日。認可コードの付与から数える）
    CONSTRAINT oauth_token_family_lifetime CHECK (expires_at > created_at AND expires_at <= created_at + interval '366 days'),
    -- 終了: 失効の要求（RFC 7009）/ リフレッシュトークンの再利用の検知 / 認可コードの再利用 / 管理者の失効
    CONSTRAINT oauth_token_family_end_check CHECK (
        (ended_at IS NULL) = (end_kind IS NULL)
        AND (end_kind IS NULL OR end_kind IN ('revoked', 'reuse_detected', 'code_reuse', 'admin')))
);
COMMENT ON TABLE oauth_token_family IS 'トークンの系列（1 回の認可コードの付与から、回転したリフレッシュトークンとアクセストークンが連なる）。系列を終えると、その系列のトークンはすべて次の要求から使えない';
CREATE INDEX oauth_token_family_client_idx ON oauth_token_family (client_ref) WHERE ended_at IS NULL;
CREATE INDEX oauth_token_family_person_idx ON oauth_token_family (person_id) WHERE ended_at IS NULL;

CREATE TRIGGER oauth_token_family_guard BEFORE UPDATE ON oauth_token_family
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_set_once('ended_at', 'end_kind');

ALTER TABLE oauth_authorization_code
    ADD CONSTRAINT oauth_authorization_code_family_fkey FOREIGN KEY (family_id) REFERENCES oauth_token_family (id);

CREATE TABLE oauth_access_token (
    id            uuid        PRIMARY KEY,
    token_sha256  text        NOT NULL,
    family_id     uuid        REFERENCES oauth_token_family (id),
    client_ref    uuid        NOT NULL REFERENCES api_client (id),
    person_id     uuid        REFERENCES person (id),
    resource      text        NOT NULL,
    scopes        text[]      NOT NULL,
    issued_at     timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz NOT NULL,
    revoked_at    timestamptz,
    CONSTRAINT oauth_access_token_format CHECK (token_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT oauth_access_token_key UNIQUE (token_sha256),
    CONSTRAINT oauth_access_token_resource_check CHECK (resource IN ('api', 'mcp')),
    CONSTRAINT oauth_access_token_scopes_check CHECK (
        cardinality(scopes) >= 1 AND scopes <@ ARRAY['jinji.read', 'jinji.propose']::text[]),
    -- ユーザー委任（系列と人を持つ）/ client credentials（どちらも持たない。REST API だけ）
    CONSTRAINT oauth_access_token_delegation_check CHECK (
        (family_id IS NULL) = (person_id IS NULL) AND (family_id IS NOT NULL OR resource = 'api')),
    -- 短命（既定 10 分。最長 1 時間）
    CONSTRAINT oauth_access_token_lifetime CHECK (expires_at > issued_at AND expires_at <= issued_at + interval '1 hour')
);
COMMENT ON TABLE oauth_access_token IS 'jin-ji が発行するアクセストークン（参照トークン。SHA-256 だけ）。発行時の resource（REST API / MCP）に束縛し、検証は要求ごとの索引検索（数秒のメモリキャッシュ付き）';
CREATE INDEX oauth_access_token_family_idx ON oauth_access_token (family_id) WHERE revoked_at IS NULL;
CREATE INDEX oauth_access_token_client_idx ON oauth_access_token (client_ref) WHERE revoked_at IS NULL;

CREATE TRIGGER oauth_access_token_guard BEFORE UPDATE ON oauth_access_token
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_set_once('revoked_at');

CREATE TABLE oauth_refresh_token (
    id            uuid        PRIMARY KEY,
    token_sha256  text        NOT NULL,
    family_id     uuid        NOT NULL REFERENCES oauth_token_family (id),
    issued_at     timestamptz NOT NULL DEFAULT now(),
    expires_at    timestamptz NOT NULL,
    used_at       timestamptz,
    CONSTRAINT oauth_refresh_token_format CHECK (token_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT oauth_refresh_token_key UNIQUE (token_sha256),
    -- 無操作の期限（既定 7 日。系列の最長の期限を超えない）
    CONSTRAINT oauth_refresh_token_lifetime CHECK (expires_at > issued_at AND expires_at <= issued_at + interval '366 days')
);
COMMENT ON TABLE oauth_refresh_token IS 'リフレッシュトークン（MCP の委任だけ。SHA-256 だけ）。使うたびに回転し（used_at）、使用済みのトークンが再び使われたら系列ごと失効する（再利用の検知）';
CREATE INDEX oauth_refresh_token_family_idx ON oauth_refresh_token (family_id);

CREATE TRIGGER oauth_refresh_token_guard BEFORE UPDATE ON oauth_refresh_token
    FOR EACH ROW EXECUTE FUNCTION jinji_guard_set_once('used_at');

-- ---------------------------------------------------------------------------
-- 7. 同意・client assertion の jti
-- ---------------------------------------------------------------------------

CREATE TABLE oauth_consent (
    id            uuid        PRIMARY KEY,
    request_id    uuid        NOT NULL REFERENCES oauth_authorization_request (id),
    client_ref    uuid        NOT NULL REFERENCES api_client (id),
    person_id     uuid        NOT NULL REFERENCES person (id),
    redirect_uri  text        NOT NULL,
    resource      text        NOT NULL,
    scopes        text[]      NOT NULL,
    granted_at    timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT oauth_consent_request_key UNIQUE (request_id),
    CONSTRAINT oauth_consent_resource_check CHECK (resource IN ('api', 'mcp')),
    CONSTRAINT oauth_consent_scopes_check CHECK (
        cardinality(scopes) >= 1 AND scopes <@ ARRAY['jinji.read', 'jinji.propose']::text[])
);
COMMENT ON TABLE oauth_consent IS '利用者の同意の記録（追記だけ）。同意は認可の要求ごとに取り、過去の同意で省略しない';
CREATE INDEX oauth_consent_person_idx ON oauth_consent (person_id);

CREATE TABLE oauth_client_assertion (
    client_ref  uuid        NOT NULL REFERENCES api_client (id),
    jti         text        NOT NULL,
    expires_at  timestamptz NOT NULL,
    PRIMARY KEY (client_ref, jti),
    CONSTRAINT oauth_client_assertion_jti_check CHECK (char_length(jti) BETWEEN 1 AND 256)
);
COMMENT ON TABLE oauth_client_assertion IS 'private_key_jwt の client assertion の jti（同じ jti の再利用を拒否する）。assertion の期限を過ぎた行は消してよい';
CREATE INDEX oauth_client_assertion_expires_idx ON oauth_client_assertion (expires_at);

-- ---------------------------------------------------------------------------
-- 8. 行レベルセキュリティ
-- ---------------------------------------------------------------------------

ALTER TABLE api_client ENABLE ROW LEVEL SECURITY;
ALTER TABLE api_client FORCE ROW LEVEL SECURITY;
ALTER TABLE api_client_key ENABLE ROW LEVEL SECURITY;
ALTER TABLE api_client_key FORCE ROW LEVEL SECURITY;
ALTER TABLE api_client_secret ENABLE ROW LEVEL SECURITY;
ALTER TABLE api_client_secret FORCE ROW LEVEL SECURITY;
ALTER TABLE cimd_allowed_domain ENABLE ROW LEVEL SECURITY;
ALTER TABLE cimd_allowed_domain FORCE ROW LEVEL SECURITY;
ALTER TABLE oauth_authorization_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE oauth_authorization_request FORCE ROW LEVEL SECURITY;
ALTER TABLE oauth_authorization_code ENABLE ROW LEVEL SECURITY;
ALTER TABLE oauth_authorization_code FORCE ROW LEVEL SECURITY;
ALTER TABLE oauth_token_family ENABLE ROW LEVEL SECURITY;
ALTER TABLE oauth_token_family FORCE ROW LEVEL SECURITY;
ALTER TABLE oauth_access_token ENABLE ROW LEVEL SECURITY;
ALTER TABLE oauth_access_token FORCE ROW LEVEL SECURITY;
ALTER TABLE oauth_refresh_token ENABLE ROW LEVEL SECURITY;
ALTER TABLE oauth_refresh_token FORCE ROW LEVEL SECURITY;
ALTER TABLE oauth_consent ENABLE ROW LEVEL SECURITY;
ALTER TABLE oauth_consent FORCE ROW LEVEL SECURITY;
ALTER TABLE oauth_client_assertion ENABLE ROW LEVEL SECURITY;
ALTER TABLE oauth_client_assertion FORCE ROW LEVEL SECURITY;

-- クライアントの管理: 参照は jin-ji 自身（検証・発行）と人事管理者の人。作成・変更は管理 CLI・CIMD の取得（jin-ji 自身）か、人事管理者の人（本人として記録）
CREATE POLICY api_client_select ON api_client FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()));
CREATE POLICY api_client_insert ON api_client FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_system()) AND created_by_kind IN ('cli', 'cimd'))
    OR ((SELECT jinji_viewer_is_hr_admin_user()) AND created_by_kind = 'person' AND created_by_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY api_client_update ON api_client FOR UPDATE
    USING ((SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()))
    WITH CHECK (
        ((SELECT jinji_viewer_is_system()) AND updated_by_kind IN ('cli', 'cimd'))
        OR ((SELECT jinji_viewer_is_hr_admin_user()) AND updated_by_kind = 'person' AND updated_by_person_id = (SELECT jinji_viewer_person_id())));

-- 鍵・シークレット・CIMD の許可ドメイン: 同じ規則（取り消しも本人として記録）
CREATE POLICY api_client_key_select ON api_client_key FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()));
CREATE POLICY api_client_key_insert ON api_client_key FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_system()) AND created_by_kind = 'cli')
    OR ((SELECT jinji_viewer_is_hr_admin_user()) AND created_by_kind = 'person' AND created_by_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY api_client_key_update ON api_client_key FOR UPDATE
    USING ((SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()))
    WITH CHECK (
        ((SELECT jinji_viewer_is_system()) AND revoked_by_kind = 'cli')
        OR ((SELECT jinji_viewer_is_hr_admin_user()) AND revoked_by_kind = 'person' AND revoked_by_person_id = (SELECT jinji_viewer_person_id())));

CREATE POLICY api_client_secret_select ON api_client_secret FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()));
CREATE POLICY api_client_secret_insert ON api_client_secret FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_system()) AND created_by_kind = 'cli')
    OR ((SELECT jinji_viewer_is_hr_admin_user()) AND created_by_kind = 'person' AND created_by_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY api_client_secret_update ON api_client_secret FOR UPDATE
    USING ((SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()))
    WITH CHECK (
        ((SELECT jinji_viewer_is_system()) AND revoked_by_kind = 'cli')
        OR ((SELECT jinji_viewer_is_hr_admin_user()) AND revoked_by_kind = 'person' AND revoked_by_person_id = (SELECT jinji_viewer_person_id())));

CREATE POLICY cimd_allowed_domain_select ON cimd_allowed_domain FOR SELECT USING (
    (SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()));
CREATE POLICY cimd_allowed_domain_insert ON cimd_allowed_domain FOR INSERT WITH CHECK (
    ((SELECT jinji_viewer_is_system()) AND created_by_kind = 'cli')
    OR ((SELECT jinji_viewer_is_hr_admin_user()) AND created_by_kind = 'person' AND created_by_person_id = (SELECT jinji_viewer_person_id())));
CREATE POLICY cimd_allowed_domain_update ON cimd_allowed_domain FOR UPDATE
    USING ((SELECT jinji_viewer_is_system()) OR (SELECT jinji_viewer_is_hr_admin_user()))
    WITH CHECK (
        ((SELECT jinji_viewer_is_system()) AND revoked_by_kind = 'cli')
        OR ((SELECT jinji_viewer_is_hr_admin_user()) AND revoked_by_kind = 'person' AND revoked_by_person_id = (SELECT jinji_viewer_person_id())));

-- 要求・コード・トークン・同意・jti: jin-ji 自身だけ（管理者の失効も jin-ji 自身が行い、操作者は監査ログに残す）
CREATE POLICY oauth_authorization_request_all ON oauth_authorization_request FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY oauth_authorization_code_all ON oauth_authorization_code FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY oauth_token_family_all ON oauth_token_family FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY oauth_access_token_all ON oauth_access_token FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY oauth_refresh_token_all ON oauth_refresh_token FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY oauth_consent_all ON oauth_consent FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));
CREATE POLICY oauth_client_assertion_all ON oauth_client_assertion FOR ALL
    USING ((SELECT jinji_viewer_is_system())) WITH CHECK ((SELECT jinji_viewer_is_system()));

-- ---------------------------------------------------------------------------
-- 9. アプリ用ロールの権限
-- ---------------------------------------------------------------------------

GRANT SELECT, INSERT ON
    api_client, api_client_key, api_client_secret, cimd_allowed_domain,
    oauth_authorization_request, oauth_authorization_code, oauth_token_family, oauth_access_token, oauth_refresh_token,
    oauth_consent, oauth_client_assertion
    TO jinji_app;
GRANT UPDATE (status, display_name, grant_types, redirect_uris, resources, scopes, fields, mcp_tools, rate_limit_per_client,
              metadata_fetched_at, metadata_expires_at, updated_by_kind, updated_by_person_id, updated_at) ON api_client TO jinji_app;
GRANT UPDATE (revoked_by_kind, revoked_by_person_id, revoked_at) ON api_client_key, api_client_secret, cimd_allowed_domain TO jinji_app;
GRANT UPDATE (decided_at, decision, person_id) ON oauth_authorization_request TO jinji_app;
GRANT UPDATE (used_at, family_id) ON oauth_authorization_code TO jinji_app;
GRANT UPDATE (ended_at, end_kind) ON oauth_token_family TO jinji_app;
GRANT UPDATE (revoked_at) ON oauth_access_token TO jinji_app;
GRANT UPDATE (used_at) ON oauth_refresh_token TO jinji_app;
-- 期限を過ぎた jti だけは消す（再利用の拒否に要るのは assertion の期限までなので）
GRANT DELETE ON oauth_client_assertion TO jinji_app;
