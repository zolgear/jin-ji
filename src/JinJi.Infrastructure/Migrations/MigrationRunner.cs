using Npgsql;

namespace JinJi.Infrastructure.Migrations;

/// <summary>
/// SQL ファイルのマイグレーションを版番号順に適用する最小のランナー。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>適用履歴は <c>schema_migration</c> テーブルに記録する（無ければ作る）</item>
/// <item>適用済みのファイルはチェックサムを照合し、書き換えられていれば失敗する</item>
/// <item>1 ファイルを 1 トランザクションで適用する。途中で失敗したファイルは何も残さない</item>
/// <item>適用済みの最新版より古い未適用ファイル（順序違反）があれば失敗する</item>
/// <item>同時実行はアドバイザリロックで直列化する</item>
/// <item>テーブル所有者ロール（jinji_owner）で実行する。スーパーユーザーでの実行は拒否する</item>
/// </list>
/// SQL ファイルの中に BEGIN / COMMIT を書かないこと（ランナーがトランザクションを張る）。
/// </remarks>
public sealed class MigrationRunner
{
    /// <summary>マイグレーション用アドバイザリロックのキー（"JINJI" の ASCII）。</summary>
    private const long AdvisoryLockKey = 0x4A494E4A49;

    private const string HistoryTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migration (
            version     text        PRIMARY KEY,
            file_name   text        NOT NULL,
            checksum    text        NOT NULL,
            applied_at  timestamptz NOT NULL DEFAULT now(),
            applied_by  text        NOT NULL DEFAULT current_user
        );
        COMMENT ON TABLE schema_migration IS 'マイグレーションの適用履歴（MigrationRunner が管理する）';
        """;

    private readonly string _connectionString;

    public MigrationRunner(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>未適用のマイグレーションを適用する。</summary>
    /// <returns>今回適用したものと、適用済みだったものの数。</returns>
    public async Task<MigrationResult> ApplyAsync(IReadOnlyList<MigrationScript> scripts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        var ordered = Validate(scripts);

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await RejectSuperuserAsync(connection, cancellationToken);
        await ExecuteAsync(connection, null, $"SELECT pg_advisory_lock({AdvisoryLockKey})", cancellationToken);
        try
        {
            await ExecuteAsync(connection, null, HistoryTableSql, cancellationToken);

            var applied = await ReadHistoryAsync(connection, cancellationToken);
            var pending = SelectPending(ordered, applied);

            foreach (var script in pending)
            {
                await ApplyOneAsync(connection, script, cancellationToken);
            }

            return new MigrationResult(pending, applied.Count);
        }
        finally
        {
            // 接続を閉じればロックは解放される。ここでの失敗で元の例外を隠さないよう、解放の失敗は無視する
            try
            {
                await ExecuteAsync(connection, null, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
            }
            catch (NpgsqlException)
            {
            }
        }
    }

    private static List<MigrationScript> Validate(IReadOnlyList<MigrationScript> scripts)
    {
        var duplicated = scripts
            .GroupBy(s => s.Version, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicated.Count > 0)
        {
            throw new MigrationException($"同じ版番号のマイグレーションが複数あります: {string.Join(", ", duplicated)}");
        }

        return [.. scripts.OrderBy(s => s.Version, StringComparer.Ordinal)];
    }

    private static List<MigrationScript> SelectPending(
        List<MigrationScript> ordered,
        Dictionary<string, AppliedMigration> applied)
    {
        var byVersion = ordered.ToDictionary(s => s.Version, StringComparer.Ordinal);

        foreach (var (version, record) in applied)
        {
            if (!byVersion.TryGetValue(version, out var script))
            {
                throw new MigrationException(
                    $"適用済みのマイグレーション {record.FileName} に対応するファイルがありません。");
            }

            if (!string.Equals(script.Checksum, record.Checksum, StringComparison.Ordinal))
            {
                throw new MigrationException(
                    $"適用済みのマイグレーション {script.FileName} の内容が変更されています（チェックサム不一致）。"
                    + "適用済みのファイルは書き換えず、新しいファイルを追加してください。");
            }
        }

        var pending = ordered.Where(s => !applied.ContainsKey(s.Version)).ToList();
        var latestApplied = applied.Keys.Max(StringComparer.Ordinal);
        if (latestApplied is not null)
        {
            var outOfOrder = pending.Where(s => string.CompareOrdinal(s.Version, latestApplied) < 0).ToList();
            if (outOfOrder.Count > 0)
            {
                throw new MigrationException(
                    $"適用済みの最新版 {latestApplied} より古い未適用のマイグレーションがあります: "
                    + string.Join(", ", outOfOrder.Select(s => s.FileName)));
            }
        }

        return pending;
    }

    private static async Task ApplyOneAsync(NpgsqlConnection connection, MigrationScript script, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, transaction, script.Sql, cancellationToken);

            await using var record = new NpgsqlCommand(
                "INSERT INTO schema_migration (version, file_name, checksum) VALUES ($1, $2, $3)",
                connection,
                transaction);
            record.Parameters.Add(new NpgsqlParameter { Value = script.Version });
            record.Parameters.Add(new NpgsqlParameter { Value = script.FileName });
            record.Parameters.Add(new NpgsqlParameter { Value = script.Checksum });
            await record.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new MigrationException($"マイグレーション {script.FileName} の適用に失敗しました: {ex.Message}", ex);
        }
    }

    private static async Task RejectSuperuserAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT rolsuper FROM pg_roles WHERE rolname = current_user",
            connection);
        var isSuperuser = await command.ExecuteScalarAsync(cancellationToken) is true;
        if (isSuperuser)
        {
            throw new MigrationException(
                "スーパーユーザーではマイグレーションを実行しません。テーブル所有者ロール（jinji_owner）で接続してください。");
        }
    }

    private static async Task<Dictionary<string, AppliedMigration>> ReadHistoryAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT version, file_name, checksum FROM schema_migration",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var applied = new Dictionary<string, AppliedMigration>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(reader.GetString(0), new AppliedMigration(reader.GetString(1), reader.GetString(2)));
        }

        return applied;
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            // スキーマ変更は時間がかかり得るので、コマンドのタイムアウトは設けない
            CommandTimeout = 0,
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record AppliedMigration(string FileName, string Checksum);
}

/// <summary>マイグレーションの適用結果。</summary>
/// <param name="Applied">今回適用したマイグレーション（適用順）。</param>
/// <param name="PreviouslyApplied">実行前に適用済みだった数。</param>
public sealed record MigrationResult(IReadOnlyList<MigrationScript> Applied, int PreviouslyApplied);
