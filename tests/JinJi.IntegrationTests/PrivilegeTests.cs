using JinJi.Domain;
using JinJi.IntegrationTests.Support;
using Npgsql;

namespace JinJi.IntegrationTests;

/// <summary>
/// 所有者ロール（jinji_owner）とアプリ用ロール（jinji_app）の分離。
/// </summary>
public sealed class PrivilegeTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private NpgsqlConnection _app = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(Ct);
        await _database.MigrateAsync(Ct);
        _app = await _database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(_app, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact(DisplayName = "アプリ用ロールは監査ログを追記・参照できる")]
    public async Task AppRole_CanInsertAndSelectAuditEvents()
    {
        var id = await InsertAuditEventAsync();

        await using var command = new NpgsqlCommand("SELECT count(*) FROM audit_event WHERE id = $1", _app);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact(DisplayName = "アプリ用ロールは監査ログを更新できない")]
    public async Task AppRole_CannotUpdateAuditEvents()
    {
        var id = await InsertAuditEventAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            SampleData.ExecuteAsync(_app, "UPDATE audit_event SET outcome = 'failed' WHERE id = $1", Ct, id));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "アプリ用ロールは監査ログを削除できない")]
    public async Task AppRole_CannotDeleteAuditEvents()
    {
        var id = await InsertAuditEventAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            SampleData.ExecuteAsync(_app, "DELETE FROM audit_event WHERE id = $1", Ct, id));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "アプリ用ロールは所属の記録時間以外を更新できない")]
    public async Task AppRole_CanUpdateOnlyRecordedPeriodOfAssignment()
    {
        var ids = await SampleData.InsertBasicsAsync(_app, Ct);
        var assignment = await SampleData.InsertAssignmentAsync(_app, ids.Person, ids.OrgUnitA, "primary", "[2026-04-01,)", Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            SampleData.ExecuteAsync(_app, "UPDATE assignment SET org_unit_id = $1 WHERE id = $2", Ct, ids.OrgUnitB, assignment));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Theory(DisplayName = "アプリ用ロールに許していない操作は拒否される")]
    [InlineData("DELETE FROM person")]
    [InlineData("SELECT * FROM schema_migration")]
    [InlineData("CREATE TABLE app_owned (id int)")]
    [InlineData("TRUNCATE audit_event")]
    public async Task AppRole_IsDeniedUngrantedOperations(string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => SampleData.ExecuteAsync(_app, sql, Ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact(DisplayName = "すべてのテーブルの所有者は所有者ロールでありアプリ用ロールではない")]
    public async Task AllTables_AreOwnedByOwnerRole()
    {
        await using var connection = await _database.OpenOwnerAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT c.relname, pg_get_userbyid(c.relowner)
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S')
            """,
            connection);
        var owners = new List<(string Table, string Owner)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            owners.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.NotEmpty(owners);
        Assert.All(owners, o => Assert.Equal(TestSettings.OwnerRole, o.Owner));
    }

    private async Task<Guid> InsertAuditEventAsync()
    {
        var id = EntityId.New();
        await SampleData.ExecuteAsync(
            _app,
            "INSERT INTO audit_event (id, actor_type, channel, action, outcome) VALUES ($1, 'system', 'cli', 'test', 'success')",
            Ct,
            id);
        return id;
    }
}
