using JinJi.Infrastructure.Database;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Support;

/// <summary>
/// <see cref="OrgFixture"/>（段の定義を含む）と <see cref="PolicyEvaluationTests.Policies"/> を登録した一時データベース（テストクラス単位で共有する）。
/// </summary>
/// <remarks>
/// データはアプリ用ロール（jinji_app）に hr_admin の閲覧者を設定して登録する（RLS の書き込みの規則も通す）。
/// ポリシーは hr_admin が起案し、hr_head が承認する（起案者 ≠ 承認者）。
/// </remarks>
public class FixtureDatabase : IAsyncLifetime
{
    /// <summary>初期化時に 1 件だけ登録する監査ログの action（RLS の件数のテストで使う）。</summary>
    internal const string FixtureAuditAction = "fixture.setup";

    private TestDatabase? _database;
    private NpgsqlDataSource? _appDataSource;

    internal OrgFixture Fixture => OrgFixture.Shared;

    internal TestDatabase Database => _database ?? throw new InvalidOperationException("初期化前です。");

    /// <summary>アプリ用ロールのデータソース。</summary>
    internal NpgsqlDataSource AppDataSource => _appDataSource ?? throw new InvalidOperationException("初期化前です。");

    /// <summary>ポリシーの承認者（hr_admin とは別の人事管理者）。</summary>
    internal ViewerContext Approver => ViewerContext.ForUser(Fixture.Person("hr_head"), [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);

    public async ValueTask InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(CancellationToken.None);
        await _database.MigrateAsync(CancellationToken.None);
        _appDataSource = NpgsqlDataSource.Create(_database.AppConnectionString);

        await using (var app = await _database.OpenAppAsync(CancellationToken.None))
        {
            await SampleData.ActAsHrAdminAsync(app, CancellationToken.None);
            await Fixture.InsertAsync(app, CancellationToken.None);
            await SampleData.ExecuteAsync(
                app,
                "INSERT INTO audit_event (id, actor_type, channel, action, outcome) VALUES ($1, 'system', 'cli', $2, 'success')",
                CancellationToken.None,
                Domain.EntityId.New(),
                FixtureAuditAction);
        }

        // 法人 C・D の段の定義（既定の定義は 0004 のマイグレーションが作る）。hr_admin が起案し、hr_head が承認する
        var levels = new NpgsqlOrgLevelStore(_appDataSource, TimeProvider.System);
        foreach (var scheme in Fixture.LevelSchemes.Where(s => s.LegalEntityId is not null))
        {
            var schemeId = Domain.EntityId.New();
            await levels.InsertProposalAsync(Fixture.HrAdmin(), schemeId, scheme, CancellationToken.None);
            if (!await levels.ApproveAsync(Approver, schemeId, CancellationToken.None))
            {
                throw new InvalidOperationException($"フィクスチャの段の定義 {schemeId} を承認できませんでした。");
            }
        }

        var store = new NpgsqlPolicyRuleStore(_appDataSource, TimeProvider.System);
        foreach (var rule in PolicyEvaluationTests.Policies.Rules)
        {
            await store.InsertProposalAsync(Fixture.HrAdmin(), rule, CancellationToken.None);
            if (!await store.ApproveAsync(Approver, rule.Id, CancellationToken.None))
            {
                throw new InvalidOperationException($"フィクスチャのポリシー {rule.Id} を承認できませんでした。");
            }
        }

        await SeedExtraAsync(CancellationToken.None);
    }

    /// <summary>派生クラスが共通のフィクスチャに足すデータ（既定は何もしない）。</summary>
    protected virtual Task SeedExtraAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_appDataSource is not null)
        {
            await _appDataSource.DisposeAsync();
        }

        if (_database is not null)
        {
            await _database.DisposeAsync();
        }
    }

    /// <summary>アプリ用ロールで、閲覧者を設定した（SET LOCAL 相当）トランザクションの中で問い合わせ、1 列目を読む。</summary>
    internal async Task<List<T>> QueryAsAppAsync<T>(ViewerContext? viewer, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var connection = await Database.OpenAppAsync(cancellationToken);
        return await QueryAsync<T>(connection, viewer, sql, cancellationToken, parameters);
    }

    /// <summary>指定した接続で、閲覧者を設定したトランザクションの中で問い合わせ、1 列目を読む。viewer が null なら閲覧者を設定しない。</summary>
    internal static async Task<List<T>> QueryAsync<T>(
        NpgsqlConnection connection,
        ViewerContext? viewer,
        string sql,
        CancellationToken cancellationToken,
        params object[] parameters)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (viewer is not null)
        {
            await ViewerTransaction.ApplyAsync(connection, transaction, viewer, cancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var rows = new List<T>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(reader.GetFieldValue<T>(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return rows;
    }
}
