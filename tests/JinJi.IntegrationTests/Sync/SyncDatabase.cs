using System.Security.Cryptography;
using System.Text.Json;
using JinJi.Application.Sync;
using JinJi.Domain;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Ingest;
using JinJi.Policy;
using JinJi.Sync;

namespace JinJi.IntegrationTests.Sync;

/// <summary>時刻を進められる TimeProvider（計画の期限のテスト用）。</summary>
internal sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>
/// 同期計画のテスト用の一時データベース。取込のテストの組織と人（<see cref="IngestDatabase"/>）に、退職者 retiree（E0009）と
/// Entra の外部 ID の対応（head・takahashi・member・retiree）を足す。Entra のユーザー一覧は jin-ji と同じ値から作り、上書きで差分を作る。
/// </summary>
internal sealed class SyncDatabase : IAsyncDisposable
{
    /// <summary>上長を除く同期の設定（上長の項目が増えないようにする）。</summary>
    public static readonly SyncSettings Settings = SyncSettings.Default with
    {
        Fields = ["displayName", "givenName", "surname", "department", "jobTitle", "companyName", "officeLocation", "employeeId", "accountEnabled"],
    };

    private readonly Dictionary<string, string> _objects = new(StringComparer.Ordinal);

    private SyncDatabase(IngestDatabase db)
    {
        Db = db;
        Time = new MutableTimeProvider(TimeProvider.System.GetUtcNow());
        Store = new NpgsqlSyncPlanStore(db.DataSource, Time);
        Service = new SyncPlanService(Store, db.Snapshots, new EntraSyncPlanner(), new EntraScriptRenderer(), new SyncLogParser(), db.Audit, Time);
    }

    public IngestDatabase Db { get; }

    public MutableTimeProvider Time { get; }

    public NpgsqlSyncPlanStore Store { get; }

    public SyncPlanService Service { get; }

    public static DateOnly Today => IngestDatabase.Today;

    public static async Task<SyncDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var sync = new SyncDatabase(await IngestDatabase.CreateAsync(cancellationToken));
        await sync.SeedAsync(cancellationToken);
        return sync;
    }

    public string Object(string key) => _objects[key];

    public Guid Id(string key) => Db.Id(key);

    public ViewerContext Hr(string key) => Db.HrUser(key);

    /// <summary>Entra のユーザー一覧（Graph の JSON）。値は jin-ji と同じ（退職者だけアカウントが有効のまま）。</summary>
    public byte[] UsersJson(Dictionary<string, Dictionary<string, object?>>? overrides = null)
    {
        var users = new List<Dictionary<string, object?>>
        {
            User("head", "E0001", "山田", "太郎", "第一営業部", true, "taro.yamada@example.co.jp"),
            User("takahashi", "E0002", "髙橋", "一郎", "営業一課", true, "ichiro.takahashi@example.co.jp"),
            User("member", "E0005", "鈴木", "次郎", "営業二課", true, "jiro.suzuki@example.co.jp"),
            User("retiree", "E0009", "退職", "三郎", null, true, "saburo@example.co.jp"),
            new() { ["id"] = Guid.NewGuid().ToString("D"), ["displayName"] = "会議室A", ["mail"] = "room-a@example.co.jp", ["accountEnabled"] = true },
        };
        foreach (var user in users)
        {
            var key = _objects.FirstOrDefault(kv => kv.Value == (string)user["id"]!).Key;
            if (key is not null && overrides?.GetValueOrDefault(key) is { } values)
            {
                foreach (var (field, value) in values)
                {
                    user[field] = value;
                }
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(new { value = users });
    }

    /// <summary>計画を作る（起案者は閲覧者の人。既定は hr1）。</summary>
    public Task<SyncPlanCreated> CreatePlanAsync(CancellationToken cancellationToken, byte[]? users = null, ViewerContext? viewer = null, SyncSettings? settings = null)
    {
        var content = users ?? UsersJson();
        return Service.CreateAsync(
            viewer ?? Hr("hr1"),
            new SyncPlanRequest(GraphDirectoryUsers.Parse(content), settings ?? Settings, "entra_users.json", Convert.ToHexStringLower(SHA256.HashData(content))),
            cancellationToken);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();

    private Dictionary<string, object?> User(string key, string number, string family, string given, string? department, bool enabled, string mail) => new()
    {
        ["id"] = _objects[key],
        ["displayName"] = $"{family} {given}",
        ["givenName"] = given,
        ["surname"] = family,
        ["department"] = department,
        ["jobTitle"] = null,
        ["companyName"] = department is null ? null : "テスト株式会社",
        ["officeLocation"] = null,
        ["employeeId"] = number,
        ["accountEnabled"] = enabled,
        ["userPrincipalName"] = mail,
        ["mail"] = mail,
    };

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var hr = Db.HrUser("hr2");
        var retiree = EntityId.New();
        await Db.ExecuteAsync(hr, "INSERT INTO person (id, legal_entity_id, employee_number) VALUES ($1, $2, 'E0009')", cancellationToken, retiree, Db.EntityA);
        await Db.ExecuteAsync(
            hr,
            """
            INSERT INTO person_version (id, person_id, family_name, given_name, display_name, search_name, employment_type, hire_date, termination_date, valid_period)
            VALUES ($1, $2, '退職', '三郎', '退職 三郎', '退職三郎', 'regular', $3, $4, daterange($3, NULL))
            """,
            cancellationToken,
            EntityId.New(),
            retiree,
            IngestDatabase.LongAgo,
            Today.AddDays(-10));
        _retiree = retiree;

        foreach (var key in new[] { "head", "takahashi", "member", "retiree" })
        {
            _objects[key] = Guid.NewGuid().ToString("D");
            await Db.ExecuteAsync(
                hr,
                "INSERT INTO external_identity (id, person_id, source_system, external_id) VALUES ($1, $2, 'entra_id', $3)",
                cancellationToken,
                EntityId.New(),
                key == "retiree" ? retiree : Db.Id(key),
                _objects[key]);
        }
    }

    private Guid _retiree;

    public Guid Retiree => _retiree;
}
