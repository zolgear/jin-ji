using JinJi.IntegrationTests.Support;
using Npgsql;

namespace JinJi.IntegrationTests;

/// <summary>
/// 項目カタログと実際のスキーマの突き合わせ（ADR-0002「項目カタログをテストで守る」）。
/// </summary>
public sealed class FieldCatalogTests : IAsyncLifetime
{
    private TestDatabase _database = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(Ct);
        await _database.MigrateAsync(Ct);
    }

    public ValueTask DisposeAsync() => _database.DisposeAsync();

    [Fact(DisplayName = "DB の全列が項目カタログに登録されている")]
    public async Task EveryDatabaseColumn_IsInCatalog()
    {
        var catalog = FieldCatalog.Load().Columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var columns = await ReadDatabaseColumnsAsync();

        var missing = columns.Where(c => !catalog.Contains(c)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            "項目カタログ（db/field-catalog.json）に分類のない列があります。ADR-0002 の分類を登録してください: "
            + string.Join(", ", missing));
    }

    [Fact(DisplayName = "項目カタログの全列が DB に存在する")]
    public async Task EveryCatalogColumn_ExistsInDatabase()
    {
        var columns = (await ReadDatabaseColumnsAsync()).ToHashSet(StringComparer.Ordinal);

        var stale = FieldCatalog.Load().Columns.Select(c => c.Key).Where(k => !columns.Contains(k)).ToList();

        Assert.True(stale.Count == 0, "DB に存在しない列がカタログにあります: " + string.Join(", ", stale));
    }

    [Fact(DisplayName = "DB に保持しない項目を表すテーブル・列がない")]
    public async Task Database_HasNoForbiddenTableOrColumnNames()
    {
        var columns = await ReadDatabaseColumnsAsync();
        var names = columns.Select(c => c.Split('.')[0]).Distinct().Concat(columns);

        var hits = names
            .Select(n => (Name: n, Hits: ForbiddenNames.Find(n.Replace('.', '_'))))
            .Where(x => x.Hits.Count > 0)
            .Select(x => $"{x.Name}: {string.Join(", ", x.Hits)}")
            .ToList();

        Assert.True(hits.Count == 0, "ADR-0002 で保持しない項目を表す名前があります: " + string.Join("; ", hits));
    }

    [Fact(DisplayName = "利用者のテーブルはすべて public スキーマにあり実体化ビューはない")]
    public async Task AllRelations_AreInPublicSchema_WithoutMaterializedViews()
    {
        // カタログは public スキーマのテーブル名で引く。information_schema に現れない実体化ビューも作らない前提
        await using var connection = await _database.OpenOwnerAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname || '.' || c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg_toast%'
              AND (n.nspname <> 'public' OR c.relkind = 'm')
            """,
            connection);
        var offending = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            offending.Add(reader.GetString(0));
        }

        Assert.True(offending.Count == 0, "想定外のリレーション: " + string.Join(", ", offending));
    }

    /// <summary>information_schema から「テーブル.列」の一覧を読む（所有者ロールなので全列が見える）。</summary>
    private async Task<List<string>> ReadDatabaseColumnsAsync()
    {
        await using var connection = await _database.OpenOwnerAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT table_name, column_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
            ORDER BY table_name, ordinal_position
            """,
            connection);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            columns.Add($"{reader.GetString(0)}.{reader.GetString(1)}");
        }

        Assert.NotEmpty(columns);
        return columns;
    }
}
