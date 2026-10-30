using System.Text;
using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Application.FieldManagement;
using JinJi.Application.Ingest;
using JinJi.Connectors;
using JinJi.Domain;
using JinJi.Infrastructure.Database;
using JinJi.Ingest;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;
using Npgsql;

namespace JinJi.IntegrationTests.Ingest;

/// <summary>同一プロセス内の書き込みの通知を数える。</summary>
internal sealed class CountingNotifier : IOrgDataChangeNotifier
{
    public int Count { get; private set; }

    public void NotifyChanged() => Count++;
}

/// <summary>
/// 取込のテスト用の一時データベース（テストごとに作る）。既定の段の定義（本部・部・課・係）で、小さな組織と人を登録する。
/// </summary>
/// <remarks>
/// <code>
/// 法人 A
///   D1  第一本部（division）       長: head
///     DP1 第一営業部（department） 長: head      head（E0001 山田 太郎）
///       S1 営業一課（section、長なし）           takahashi（E0002 髙橋 一郎、社用メールあり）
///       S2 営業二課（section）     長: head      member（E0005 鈴木 次郎）
///   D2  管理本部（division）       長: hr2
///     HR 人事部（department）      長: hr2       hr1（E9001。取込の実行者）, hr2（E9002。承認者）
/// </code>
/// </remarks>
internal sealed class IngestDatabase : IAsyncDisposable
{
    private readonly Dictionary<string, Guid> _ids = new(StringComparer.Ordinal);

    private IngestDatabase(TestDatabase database)
    {
        Database = database;
        DataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        Audit = new AuditWriter(new NpgsqlAuditSink(DataSource), TimeProvider.System);
        Snapshots = new NpgsqlOrgSnapshotProvider(DataSource);
        Store = new NpgsqlIngestStore(DataSource, TimeProvider.System);
        Importer = new ImportService(
            Store,
            Snapshots,
            Audit,
            TimeProvider.System,
            fieldManagement: new FieldManagementService(new NpgsqlFieldManagementStore(DataSource), Snapshots, Audit, TimeProvider.System),
            fieldRegistrations: new NpgsqlFieldRegistrationStore(DataSource, TimeProvider.System));
        Review = new ProposalReviewService(Store, Snapshots, Audit, TimeProvider.System, Notifier);
        Resolution = new ProposalResolutionService(Store, Snapshots, Audit, TimeProvider.System);
    }

    public static DateOnly Today => BusinessDate.Today(TimeProvider.System);

    public static DateOnly LongAgo => Today.AddYears(-10);

    public TestDatabase Database { get; }

    public NpgsqlDataSource DataSource { get; }

    public AuditWriter Audit { get; }

    public NpgsqlOrgSnapshotProvider Snapshots { get; }

    public NpgsqlIngestStore Store { get; }

    public ImportService Importer { get; }

    public CountingNotifier Notifier { get; } = new();

    public ProposalReviewService Review { get; }

    public ProposalResolutionService Resolution { get; }

    public Guid EntityA => Id("A");

    /// <summary>jin-ji 自身（ワーカー）。実行者は指定する。</summary>
    public static ViewerContext Worker => ViewerContext.ForSystem(Channel.Worker);

    public Guid Id(string key) => _ids[key];

    /// <summary>テストで足す人・組織の ID を採番して覚える。</summary>
    public Guid Register(string key) => _ids[key] = EntityId.New();

    /// <summary>人事管理者の人。</summary>
    public ViewerContext HrUser(string key) => ViewerContext.ForUser(Id(key), [Roles.HrAdmin], Channel.Ui, FieldLimit.Unrestricted);

    /// <summary>一般の人（ロールなし）。</summary>
    public ViewerContext User(string key) => ViewerContext.ForUser(Id(key), [], Channel.Ui, FieldLimit.Unrestricted);

    public static async Task<IngestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var database = await TestDatabase.CreateAsync(cancellationToken);
        await database.MigrateAsync(cancellationToken);
        var db = new IngestDatabase(database);
        await db.SeedAsync(cancellationToken);
        return db;
    }

    /// <summary>CSV を項目マッピングで読み、ワーカー（実行者 hr1）として取り込む。</summary>
    /// <param name="importer">取込の中核（LLM の候補提示を付けたもの。省略時は LLM なし）。</param>
    /// <param name="useLlm">LLM の候補提示にオプトインするか。</param>
    public Task<ImportResult> ImportCsvAsync(
        string csv,
        string mappingJson,
        CancellationToken cancellationToken,
        DateOnly? effectiveDate = null,
        ViewerContext? viewer = null,
        ImportService? importer = null,
        bool useLlm = false)
    {
        var mapping = ConnectorMapping.Parse(Encoding.UTF8.GetBytes(mappingJson));
        var bytes = Encoding.UTF8.GetBytes(csv);
        return (importer ?? Importer).ImportAsync(
            viewer ?? Worker,
            new ImportRequest
            {
                Table = SourceReader.Read(bytes, "import.csv", mapping),
                Profile = mapping.Profile,
                FileName = "import.csv",
                FileSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                MappingName = mapping.Name,
                MappingSha256 = mapping.Sha256,
                ExecutedByPersonId = viewer is null ? Id("hr1") : null,
                EffectiveDate = effectiveDate,
                UseLlm = useLlm,
            },
            cancellationToken);
    }

    /// <summary>hr_admin の閲覧者で 1 列目を読む（RLS を受ける）。</summary>
    public Task<List<T>> QueryAsync<T>(ViewerContext? viewer, string sql, CancellationToken cancellationToken, params object[] parameters) =>
        QueryWithAsync<T>(viewer ?? HrUser("hr2"), sql, cancellationToken, parameters);

    public async Task<List<T>> QueryWithAsync<T>(ViewerContext viewer, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var connection = await Database.OpenAppAsync(cancellationToken);
        return await FixtureDatabase.QueryAsync<T>(connection, viewer, sql, cancellationToken, parameters);
    }

    /// <summary>閲覧者を設定したトランザクションで 1 文を実行する（RLS を受ける）。</summary>
    public async Task<int> ExecuteAsync(ViewerContext viewer, string sql, CancellationToken cancellationToken, params object[] parameters)
    {
        await using var connection = await Database.OpenAppAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await ViewerTransaction.ApplyAsync(connection, transaction, viewer, cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected;
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        await Database.DisposeAsync();
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        foreach (var key in new[] { "A", "D1", "DP1", "S1", "S2", "D2", "HR", "head", "takahashi", "member", "hr1", "hr2" })
        {
            _ids[key] = EntityId.New();
        }

        await using var connection = await Database.OpenAppAsync(cancellationToken);
        await SampleData.ActAsHrAdminAsync(connection, cancellationToken);
        await SampleData.ExecuteAsync(connection, "INSERT INTO legal_entity (id, code, name) VALUES ($1, 'A', 'テスト株式会社')", cancellationToken, EntityA);

        var people = new (string Key, string Number, string Family, string Given, string? Email, string Unit)[]
        {
            ("head", "E0001", "山田", "太郎", "taro.yamada@example.co.jp", "DP1"),
            ("takahashi", "E0002", "髙橋", "一郎", "ichiro.takahashi@example.co.jp", "S1"),
            ("member", "E0005", "鈴木", "次郎", null, "S2"),
            ("hr1", "E9001", "人事", "一", null, "HR"),
            ("hr2", "E9002", "人事", "二", null, "HR"),
        };
        foreach (var p in people)
        {
            await SampleData.ExecuteAsync(connection, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, $3)", cancellationToken, Id(p.Key), EntityA, p.Number);
        }

        var units = new (string Key, string Code, string Name, string Level, string? Parent, string? Head)[]
        {
            ("D1", "D1", "第一本部", "division", null, "head"),
            ("DP1", "DP1", "第一営業部", "department", "D1", "head"),
            ("S1", "S1", "営業一課", "section", "DP1", null),
            ("S2", "S2", "営業二課", "section", "DP1", "head"),
            ("D2", "D2", "管理本部", "division", null, "hr2"),
            ("HR", "HR", "人事部", "department", "D2", "hr2"),
        };
        foreach (var u in units)
        {
            await SampleData.ExecuteAsync(connection, "INSERT INTO org_unit (id, legal_entity_id) VALUES ($1, $2)", cancellationToken, Id(u.Key), EntityA);
        }

        foreach (var u in units)
        {
            var path = string.Join('.', Ancestors(u.Key).Select(k => Id(k).ToString("N")));
            await SampleData.ExecuteAsync(
                connection,
                """
                INSERT INTO org_unit_version (id, org_unit_id, legal_entity_id, code, name, level_key, parent_org_unit_id, head_person_id, path, valid_period)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9::ltree, daterange($10, NULL))
                """,
                cancellationToken,
                EntityId.New(),
                Id(u.Key),
                EntityA,
                u.Code,
                u.Name,
                u.Level,
                u.Parent is null ? DBNull.Value : Id(u.Parent),
                u.Head is null ? DBNull.Value : Id(u.Head),
                path,
                LongAgo);
        }

        foreach (var p in people)
        {
            await SampleData.ExecuteAsync(
                connection,
                """
                INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name, work_email, employment_type, hire_date, valid_period)
                VALUES ($1, $2, $3, $4, $5, $6, $7, 'regular', $8, daterange($8, NULL))
                """,
                cancellationToken,
                EntityId.New(),
                Id(p.Key),
                p.Family,
                p.Given,
                $"{p.Family} {p.Given}",
                p.Family + p.Given,
                (object?)p.Email ?? DBNull.Value,
                LongAgo);
            await SampleData.ExecuteAsync(
                connection,
                "INSERT INTO assignment (id, person_id, org_unit_id, assignment_type, valid_period) VALUES ($1, $2, $3, 'primary', daterange($4, NULL))",
                cancellationToken,
                EntityId.New(),
                Id(p.Key),
                Id(p.Unit),
                LongAgo);
        }

        IEnumerable<string> Ancestors(string key)
        {
            var chain = new List<string>();
            for (string? k = key; k is not null; k = units.Single(x => x.Key == k).Parent)
            {
                chain.Insert(0, k);
            }

            return chain;
        }
    }
}
