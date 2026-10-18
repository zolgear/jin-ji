using System.Data;
using JinJi.Policy;
using Npgsql;

namespace JinJi.Infrastructure.Database;

/// <summary>
/// 閲覧者の情報を RLS に渡したトランザクション。アプリの DB アクセスはすべてこれを通す。
/// </summary>
/// <remarks>
/// <para>
/// トランザクションの開始直後に <c>set_config(名前, 値, true)</c>（SET LOCAL 相当）で GUC を設定する。
/// トランザクションが終われば値は消えるので、接続プールで次の利用者に閲覧者が引き継がれることはない。
/// </para>
/// <list type="table">
/// <item><term>jinji.viewer_kind</term><description>user / service / agent / system</description></item>
/// <item><term>jinji.viewer_roles</term><description>ロール名のカンマ区切り（例: hr_admin）</description></item>
/// <item><term>jinji.viewer_person_id</term><description>閲覧者の person.id（AI エージェントは委任元、社内システムは空）</description></item>
/// <item><term>jinji.viewer_api_client</term><description>先行提供の日数を持つ社内システムの api_client.id（それ以外は空。日数は DB が承認済みの行から読む。0016）</description></item>
/// </list>
/// <para>
/// RLS は粗い防波堤で、アプリ自身が GUC を設定できる以上、侵害されたアプリへの防御にはならない（0002 マイグレーションの冒頭を参照）。
/// </para>
/// </remarks>
public sealed class ViewerTransaction : IAsyncDisposable, IDisposable
{
    public const string KindSetting = "jinji.viewer_kind";
    public const string RolesSetting = "jinji.viewer_roles";
    public const string PersonIdSetting = "jinji.viewer_person_id";
    public const string ApiClientSetting = "jinji.viewer_api_client";

    private ViewerTransaction(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        Connection = connection;
        Transaction = transaction;
    }

    public NpgsqlConnection Connection { get; }

    public NpgsqlTransaction Transaction { get; }

    /// <summary>接続を開き、トランザクションを始め、閲覧者を設定する。</summary>
    public static async Task<ViewerTransaction> BeginAsync(
        NpgsqlDataSource dataSource,
        ViewerContext viewer,
        CancellationToken cancellationToken,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(viewer);

        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
            var scope = new ViewerTransaction(connection, transaction);
            if (readOnly)
            {
                await using var command = scope.CreateCommand("SET TRANSACTION READ ONLY");
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await ApplyAsync(connection, transaction, viewer, cancellationToken);
            return scope;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// 接続を開き、トランザクションを始め、GUC の値を直接設定する（認証の表の閲覧者用。<see cref="NpgsqlAuthStore"/>）。
    /// 人事データの閲覧者は <see cref="ViewerContext"/> から作ること。
    /// </summary>
    internal static async Task<ViewerTransaction> BeginRawAsync(
        NpgsqlDataSource dataSource,
        string kind,
        string roles,
        Guid? personId,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var scope = new ViewerTransaction(connection, transaction);
            await using var command = scope.CreateCommand(SetSql);
            AddSettings(command, kind, roles, personId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return scope;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>同期版（Data Protection の IXmlRepository が同期の API のため）。</summary>
    internal static ViewerTransaction BeginRaw(NpgsqlDataSource dataSource, string kind, string roles, Guid? personId)
    {
        var connection = dataSource.OpenConnection();
        try
        {
            var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
            var scope = new ViewerTransaction(connection, transaction);
            using var command = scope.CreateCommand(SetSql);
            AddSettings(command, kind, roles, personId);
            command.ExecuteNonQuery();
            return scope;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private const string SetSql =
        $"""
        SELECT set_config('{KindSetting}', $1, true),
               set_config('{RolesSetting}', $2, true),
               set_config('{PersonIdSetting}', $3, true),
               set_config('{ApiClientSetting}', $4, true)
        """;

    private static void AddSettings(NpgsqlCommand command, string kind, string roles, Guid? personId, Guid? apiClient = null)
    {
        command.Parameters.Add(new NpgsqlParameter { Value = kind });
        command.Parameters.Add(new NpgsqlParameter { Value = roles });
        command.Parameters.Add(new NpgsqlParameter { Value = personId?.ToString() ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter { Value = apiClient?.ToString() ?? string.Empty });
    }

    /// <summary>既に始めたトランザクションに閲覧者を設定する（SET LOCAL 相当）。</summary>
    public static async Task ApplyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ViewerContext viewer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(viewer);

        await using var command = new NpgsqlCommand(SetSql, connection, transaction);
        // 先行提供の日数が 0 の閲覧者（先行提供を外した閲覧者を含む）には api_client を渡さない（RLS の線を従来のまま業務日付に引く）
        AddSettings(
            command,
            DbCodes.ToCode(viewer.Actor),
            string.Join(',', viewer.Roles.Order(StringComparer.Ordinal)),
            viewer.EffectivePersonId,
            viewer.DisclosureLeadDays > 0 ? viewer.ApiClientRef : null);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public NpgsqlCommand CreateCommand(string sql) => new(sql, Connection, Transaction);

    public Task CommitAsync(CancellationToken cancellationToken) => Transaction.CommitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync();
        await Connection.DisposeAsync();
    }

    public void Dispose()
    {
        Transaction.Dispose();
        Connection.Dispose();
    }
}
