using JinJi.Domain;
using JinJi.IntegrationTests.Support;
using Npgsql;

namespace JinJi.IntegrationTests.Auth;

/// <summary>
/// トークン発行の表（0012）の RLS・制約・トリガー・アプリ用ロールの権限。アプリ用ロールで接続し、閲覧者（GUC）ごとに確かめる。
/// </summary>
public sealed class OAuthSchemaTests(AuthFixtureDatabase db) : IClassFixture<AuthFixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    [Theory(DisplayName = "コード・トークン・同意・jti・認可の要求は jin-ji 自身（system）だけが読める（人事管理者・エージェント・緊急用アカウントは 0 行）")]
    [InlineData("oauth_authorization_request")]
    [InlineData("oauth_authorization_code")]
    [InlineData("oauth_token_family")]
    [InlineData("oauth_access_token")]
    [InlineData("oauth_refresh_token")]
    [InlineData("oauth_consent")]
    [InlineData("oauth_client_assertion")]
    public async Task TokenTables_AreSystemOnly(string table)
    {
        await SeedTokenAsync();
        Assert.True(await CountAsync("system", string.Empty, null, $"SELECT count(*) FROM {table}") > 0);
        Assert.Equal(0, await CountAsync("user", "hr_admin", F.Person("hr_admin"), $"SELECT count(*) FROM {table}"));
        Assert.Equal(0, await CountAsync("agent", "hr_admin", F.Person("hr_admin"), $"SELECT count(*) FROM {table}"));
        Assert.Equal(0, await CountAsync("break_glass", string.Empty, null, $"SELECT count(*) FROM {table}"));
        Assert.Equal(0, await CountAsync(string.Empty, string.Empty, null, $"SELECT count(*) FROM {table}"));
    }

    [Fact(DisplayName = "クライアントの管理: 人事管理者の人は本人として記録するときだけ登録でき、エージェント・一般の人・緊急用アカウントは見られない")]
    public async Task Clients_AreManagedBySystemOrHrAdmin()
    {
        var id = await InsertClientAsync("schema-a");
        Assert.Equal(1, await CountAsync("user", "hr_admin", F.Person("hr_admin"), $"SELECT count(*) FROM api_client WHERE id = '{id}'"));
        Assert.Equal(0, await CountAsync("user", string.Empty, F.Person("u1_member"), $"SELECT count(*) FROM api_client WHERE id = '{id}'"));
        Assert.Equal(0, await CountAsync("agent", "hr_admin", F.Person("hr_admin"), $"SELECT count(*) FROM api_client WHERE id = '{id}'"));
        Assert.Equal(0, await CountAsync("break_glass", string.Empty, null, $"SELECT count(*) FROM api_client WHERE id = '{id}'"));

        // 人事管理者の人は本人としてしか記録できない
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "user", "hr_admin", F.Person("hr_admin"),
            ClientInsert("schema-b", "'person'", "$2"),
            EntityId.New(), F.Person("hr_head")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        await ExecuteAsync("user", "hr_admin", F.Person("hr_admin"), ClientInsert("schema-c", "'person'", "$2"), EntityId.New(), F.Person("hr_admin"));

        // エージェントは委任元が人事管理者でも登録できない
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "agent", "hr_admin", F.Person("hr_admin"), ClientInsert("schema-d", "'person'", "$2"), EntityId.New(), F.Person("hr_admin")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "制約: client_id の形・jinji-ui の予約・公開クライアントの client credentials・CIMD の承認待ち・トークンの寿命の上限")]
    public async Task Constraints_RejectInvalidRows()
    {
        async Task<string> FailAsync(string sql, params object[] parameters) =>
            (await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("system", string.Empty, null, sql, parameters))).ConstraintName!;

        Assert.Equal("api_client_client_id_check", await FailAsync(ClientInsert("jinji-ui", "'cli'", "NULL"), EntityId.New()));
        Assert.Equal("api_client_client_id_check", await FailAsync(ClientInsert("Bad ID", "'cli'", "NULL"), EntityId.New()));
        Assert.Equal("api_client_grant_types_check", await FailAsync(
            """
            INSERT INTO api_client (id, client_id, registration, status, auth_method, grant_types, resources, scopes, fields, created_by_kind)
            VALUES ($1, 'svc-none', 'registered', 'active', 'none', '{client_credentials}', '{api}', '{jinji.read}', '{*}', 'cli')
            """,
            EntityId.New()));
        Assert.Equal("api_client_status_check", await FailAsync(
            """
            INSERT INTO api_client (id, client_id, registration, status, auth_method, grant_types, redirect_uris, resources, scopes, fields, created_by_kind)
            VALUES ($1, 'pending-reg', 'registered', 'pending_approval', 'none', '{authorization_code}', '{http://localhost/cb}', '{mcp}', '{jinji.read}', '{*}', 'cli')
            """,
            EntityId.New()));
        Assert.Equal("api_client_scopes_check", await FailAsync(
            """
            INSERT INTO api_client (id, client_id, registration, status, auth_method, grant_types, redirect_uris, resources, scopes, fields, created_by_kind)
            VALUES ($1, 'admin-scope', 'registered', 'active', 'none', '{authorization_code}', '{http://localhost/cb}', '{mcp}', '{jinji.admin}', '{*}', 'cli')
            """,
            EntityId.New()));

        var (client, family, _) = await SeedTokenAsync();
        Assert.Equal("oauth_access_token_lifetime", await FailAsync(
            """
            INSERT INTO oauth_access_token (id, token_sha256, family_id, client_ref, person_id, resource, scopes, issued_at, expires_at)
            VALUES ($1, repeat('b', 64), $2, $3, $4, 'mcp', '{jinji.read}', now(), now() + interval '2 hours')
            """,
            EntityId.New(), family, client, F.Person("u1_member")));
        // client credentials（人なし）のトークンは REST API だけ
        Assert.Equal("oauth_access_token_delegation_check", await FailAsync(
            """
            INSERT INTO oauth_access_token (id, token_sha256, client_ref, resource, scopes, issued_at, expires_at)
            VALUES ($1, repeat('c', 64), $2, 'mcp', '{jinji.read}', now(), now() + interval '10 minutes')
            """,
            EntityId.New(), client));
    }

    [Fact(DisplayName = "トリガー: 系列の終了・トークンの失効・リフレッシュの使用・コードの使用は 1 回だけで、中身は変えられない")]
    public async Task Guards_AllowSingleTransitions()
    {
        var (_, family, code) = await SeedTokenAsync();

        await ExecuteAsync("system", string.Empty, null, "UPDATE oauth_token_family SET ended_at = now(), end_kind = 'revoked' WHERE id = $1", family);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, "UPDATE oauth_token_family SET ended_at = NULL, end_kind = NULL WHERE id = $1", family));
        Assert.Equal("set_once", ex.ConstraintName);

        await ExecuteAsync("system", string.Empty, null, "UPDATE oauth_access_token SET revoked_at = now() WHERE family_id = $1", family);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, "UPDATE oauth_access_token SET revoked_at = NULL WHERE family_id = $1", family));
        Assert.Equal("set_once", ex.ConstraintName);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, "UPDATE oauth_access_token SET expires_at = expires_at + interval '1 minute' WHERE family_id = $1", family));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        await ExecuteAsync("system", string.Empty, null, "UPDATE oauth_refresh_token SET used_at = now() WHERE family_id = $1", family);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, "UPDATE oauth_refresh_token SET used_at = NULL WHERE family_id = $1", family));
        Assert.Equal("set_once", ex.ConstraintName);

        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, "UPDATE oauth_authorization_code SET used_at = NULL WHERE id = $1", code));
        Assert.Equal("oauth_authorization_code_single_use", ex.ConstraintName);
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "system", string.Empty, null, "UPDATE oauth_authorization_code SET redirect_uri = 'https://evil.example/' WHERE id = $1", code));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "アプリ用ロールは鍵・シークレット・許可ドメインを消せない。トークン・コード・系列・同意・クライアントは、生きている行を消せない（0014 の下限）")]
    public async Task AppRole_CannotDeleteLiveTokens()
    {
        var (client, family, _) = await SeedTokenAsync();
        foreach (var table in new[] { "api_client_key", "api_client_secret", "cimd_allowed_domain" })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync("system", string.Empty, null, $"DELETE FROM {table}"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        }

        // 生きている系列の行（期限内のトークン・コード・要求、終わっていない系列、その要求の同意）と、有効なクライアントは消えない
        foreach (var sql in new[]
                 {
                     "DELETE FROM oauth_access_token WHERE family_id = $1", "DELETE FROM oauth_refresh_token WHERE family_id = $1",
                     "DELETE FROM oauth_authorization_code WHERE family_id = $1", "DELETE FROM oauth_token_family WHERE id = $1",
                     "DELETE FROM oauth_consent WHERE request_id IN (SELECT request_id FROM oauth_authorization_code WHERE family_id = $1)",
                     "DELETE FROM oauth_authorization_request WHERE id IN (SELECT request_id FROM oauth_authorization_code WHERE family_id = $1)",
                 })
        {
            Assert.Equal(0, await ExecuteAsync("system", string.Empty, null, sql, family));
        }

        Assert.Equal(0, await ExecuteAsync("system", string.Empty, null, "DELETE FROM api_client WHERE id = $1", client));

        // 系列を終えても、期限内のアクセストークン・期限内の要求は消えない。リフレッシュトークンと（期限を過ぎた）コードの扱いは系列の終了に従う
        await ExecuteAsync("system", string.Empty, null, "UPDATE oauth_token_family SET ended_at = now(), end_kind = 'revoked' WHERE id = $1", family);
        Assert.Equal(0, await ExecuteAsync("system", string.Empty, null, "DELETE FROM oauth_access_token WHERE family_id = $1", family));
        Assert.Equal(1, await ExecuteAsync("system", string.Empty, null, "DELETE FROM oauth_refresh_token WHERE family_id = $1", family));

        // 人事管理者・エージェントには見えないので消せない
        Assert.Equal(0, await ExecuteAsync("user", "hr_admin", F.Person("hr_admin"), "DELETE FROM oauth_token_family WHERE id = $1", family));
        await ExecuteAsync("system", string.Empty, null, "DELETE FROM oauth_client_assertion WHERE expires_at < now() - interval '1 day'");
    }

    // ---- 補助 ----------------------------------------------------------------------------------------------------

    private static string ClientInsert(string clientId, string createdByKind, string createdByPerson) =>
        $$"""
        INSERT INTO api_client (id, client_id, registration, status, auth_method, grant_types, redirect_uris, resources, scopes, fields, created_by_kind, created_by_person_id)
        VALUES ($1, '{{clientId}}', 'registered', 'active', 'none', '{authorization_code,refresh_token}', '{http://127.0.0.1/cb}', '{mcp}', '{jinji.read}', '{*}', {{createdByKind}}, {{createdByPerson}})
        """;

    private async Task<Guid> InsertClientAsync(string clientId)
    {
        var id = EntityId.New();
        await ExecuteAsync("system", string.Empty, null, ClientInsert(clientId, "'cli'", "NULL"), id);
        return id;
    }

    /// <summary>クライアント・要求・コード・系列・アクセストークン・リフレッシュトークン・同意・jti を 1 組作る（jin-ji 自身で）。</summary>
    private async Task<(Guid Client, Guid Family, Guid Code)> SeedTokenAsync()
    {
        var client = await InsertClientAsync("seed-" + Guid.NewGuid().ToString("N")[..12]);
        var (request, family, code) = (EntityId.New(), EntityId.New(), EntityId.New());
        var person = F.Person("u1_member");
        string Hash() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await ExecuteAsync(
            "system", string.Empty, null,
            """
            INSERT INTO oauth_authorization_request (id, handle_sha256, client_ref, redirect_uri, resource, scopes, code_challenge, expires_at, decided_at, decision, person_id)
            VALUES ($1, $2, $3, 'http://127.0.0.1/cb', 'mcp', '{jinji.read}', repeat('A', 43), now() + interval '10 minutes', now(), 'approved', $4)
            """,
            request, Hash(), client, person);
        await ExecuteAsync(
            "system", string.Empty, null,
            "INSERT INTO oauth_consent (id, request_id, client_ref, person_id, redirect_uri, resource, scopes) VALUES ($1, $2, $3, $4, 'http://127.0.0.1/cb', 'mcp', '{jinji.read}')",
            EntityId.New(), request, client, person);
        await ExecuteAsync(
            "system", string.Empty, null,
            "INSERT INTO oauth_token_family (id, client_ref, person_id, resource, scopes, expires_at) VALUES ($1, $2, $3, 'mcp', '{jinji.read}', now() + interval '30 days')",
            family, client, person);
        await ExecuteAsync(
            "system", string.Empty, null,
            """
            INSERT INTO oauth_authorization_code (id, code_sha256, request_id, client_ref, person_id, redirect_uri, resource, scopes, code_challenge, expires_at, used_at, family_id)
            VALUES ($1, $2, $3, $4, $5, 'http://127.0.0.1/cb', 'mcp', '{jinji.read}', repeat('A', 43), now() + interval '60 seconds', now(), $6)
            """,
            code, Hash(), request, client, person, family);
        await ExecuteAsync(
            "system", string.Empty, null,
            """
            INSERT INTO oauth_access_token (id, token_sha256, family_id, client_ref, person_id, resource, scopes, expires_at)
            VALUES ($1, $2, $3, $4, $5, 'mcp', '{jinji.read}', now() + interval '10 minutes')
            """,
            EntityId.New(), Hash(), family, client, person);
        await ExecuteAsync(
            "system", string.Empty, null,
            "INSERT INTO oauth_refresh_token (id, token_sha256, family_id, expires_at) VALUES ($1, $2, $3, now() + interval '7 days')",
            EntityId.New(), Hash(), family);
        await ExecuteAsync(
            "system", string.Empty, null,
            "INSERT INTO oauth_client_assertion (client_ref, jti, expires_at) VALUES ($1, $2, now() + interval '5 minutes')",
            client, Guid.NewGuid().ToString("N"));
        return (client, family, code);
    }

    private async Task<long> CountAsync(string kind, string roles, Guid? person, string sql)
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.SetSessionViewerAsync(connection, kind, roles, person, Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task<int> ExecuteAsync(string kind, string roles, Guid? person, string sql, params object[] parameters)
    {
        await using var connection = await db.Database.OpenAppAsync(Ct);
        await SampleData.SetSessionViewerAsync(connection, kind, roles, person, Ct);
        return await SampleData.ExecuteAsync(connection, sql, Ct, parameters);
    }
}
