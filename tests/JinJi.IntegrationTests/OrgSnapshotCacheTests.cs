using JinJi.Application;
using JinJi.Application.Organization;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Infrastructure.Database;
using JinJi.IntegrationTests.Support;
using JinJi.Policy;

namespace JinJi.IntegrationTests;

/// <summary>組織スナップショットのキャッシュ（業務日付ごと・閲覧者の区分ごと・世代の確認・同一プロセスの書き込みでの無効化）。DB を使わない。</summary>
public sealed class OrgSnapshotCacheTests : IDisposable
{
    private static readonly OrgFixture F = OrgFixture.Shared;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    private readonly FixtureSnapshotProvider _inner = new(F);
    private readonly ManualVersionSource _versions = new();
    private readonly ManualTimeProvider _time = new(new DateTimeOffset(F.Today.ToDateTime(new TimeOnly(10, 0)), BusinessDate.UtcOffset));
    private readonly OrgSnapshotCache _cache;

    public OrgSnapshotCacheTests()
    {
        _cache = new OrgSnapshotCache(_inner, _versions, _time, new OrgSnapshotCacheOptions { VersionCheckInterval = Interval, MaxAge = TimeSpan.FromMinutes(10) });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _cache.Dispose();

    [Fact(DisplayName = "確認の間隔内は DB の世代も確かめずに同じスナップショットを返す")]
    public async Task WithinInterval_ReturnsCachedWithoutCheck()
    {
        var first = await LoadTodayAsync(F.User("u1_member"));
        _time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        var second = await LoadTodayAsync(F.User("u1_member"));

        Assert.Same(first, second);
        Assert.Single(_inner.Calls);
        Assert.Equal(1, _versions.Calls);
    }

    [Fact(DisplayName = "間隔を過ぎたら世代を確かめ、変わっていなければ読み直さない")]
    public async Task AfterInterval_SameVersion_KeepsSnapshot()
    {
        var first = await LoadTodayAsync(F.User("u1_member"));
        _time.Advance(Interval);
        var second = await LoadTodayAsync(F.User("u1_member"));

        Assert.Same(first, second);
        Assert.Single(_inner.Calls);
        Assert.Equal(2, _versions.Calls);
    }

    [Fact(DisplayName = "他のプロセスの書き込み（世代の変化）は、確認の間隔を過ぎた後の参照で読み直す")]
    public async Task VersionChange_Reloads()
    {
        var first = await LoadTodayAsync(F.User("u1_member"));
        _versions.Version = "v2";

        Assert.Same(first, await LoadTodayAsync(F.User("u1_member")));
        _time.Advance(Interval);
        Assert.NotSame(first, await LoadTodayAsync(F.User("u1_member")));
        Assert.Equal(2, _inner.Calls.Count);
    }

    [Fact(DisplayName = "同一プロセスの書き込みの通知で、確認の間隔内でもすぐに読み直す")]
    public async Task NotifyChanged_ReloadsImmediately()
    {
        var first = await LoadTodayAsync(F.User("u1_member"));
        var admin = await LoadTodayAsync(F.HrAdmin());

        _cache.NotifyChanged();

        Assert.NotSame(first, await LoadTodayAsync(F.User("u1_member")));
        Assert.NotSame(admin, await LoadTodayAsync(F.HrAdmin()));
        Assert.Equal(4, _inner.Calls.Count);
    }

    [Fact(DisplayName = "閲覧者の区分（人事管理者か）ごとに持ち、区分内（利用者・エージェント・社内システム）では共有する")]
    public async Task Slots_ArePerViewerClass()
    {
        var general = await LoadTodayAsync(F.User("u1_member"));
        var admin = await LoadTodayAsync(F.HrAdmin());

        Assert.NotSame(general, admin);
        Assert.Same(general, await LoadTodayAsync(F.User("sec1_head")));
        Assert.Same(general, await LoadTodayAsync(ViewerContext.ForAgent("assistant", F.Person("u2_member"), [], Channel.Mcp, FieldLimit.Unrestricted)));
        Assert.Same(general, await LoadTodayAsync(ViewerContext.ForService("attendance", FieldLimit.Unrestricted)));
        Assert.Same(admin, await LoadTodayAsync(ViewerContext.ForSystem(Channel.Worker)));
        Assert.Equal([false, true], _inner.Calls.Select(c => c.Viewer.IsHrAdmin));
    }

    [Fact(DisplayName = "時点指定（今日以外）はキャッシュせず毎回読む")]
    public async Task OtherDates_AreNotCached()
    {
        var past = F.Today.AddDays(-10);

        await _cache.LoadAsync(F.HrAdmin(), past, past, Ct);
        await _cache.LoadAsync(F.HrAdmin(), past, past, Ct);
        await _cache.LoadAsync(F.User("u1_member"), past, F.Today, Ct);

        Assert.Equal(3, _inner.Calls.Count);
        Assert.Equal(0, _versions.Calls);
    }

    [Fact(DisplayName = "業務日付が変わったら前日の分は使わない（日本時間の 0 時で切り替わる）")]
    public async Task BusinessDateChange_Reloads()
    {
        var first = await LoadTodayAsync(F.User("u1_member"));

        _time.Advance(TimeSpan.FromHours(14));
        var next = await _cache.LoadAsync(F.User("u1_member"), F.Today.AddDays(1), F.Today.AddDays(1), Ct);

        Assert.NotSame(first, next);
        Assert.Equal(F.Today.AddDays(1), next.AsOf);
        // 前日の日付は「今日以外」になるのでキャッシュを経由しない
        await LoadTodayAsync(F.User("u1_member"));
        Assert.Equal(3, _inner.Calls.Count);
    }

    [Fact(DisplayName = "世代が変わらなくても最長保持時間を過ぎたら読み直す")]
    public async Task MaxAge_ForcesReload()
    {
        var first = await LoadTodayAsync(F.User("u1_member"));

        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.NotSame(first, await LoadTodayAsync(F.User("u1_member")));
    }

    [Fact(DisplayName = "同時の参照でも読み込みは区分ごとに 1 回")]
    public async Task ConcurrentLoads_AreCoalesced()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => LoadTodayAsync(F.User("u1_member")), Ct)));

        Assert.Single(_inner.Calls);
        Assert.All(results, r => Assert.Same(results[0], r));
    }

    [Fact(DisplayName = "読み込み中に書き込みの通知があったら、その結果はキャッシュしない")]
    public async Task NotifyDuringLoad_DoesNotCacheStaleResult()
    {
        var calls = 0;
        var gate = new DelegateSnapshotProvider((viewer, asOf, attributesAsOf) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                // 読み込み中に同一プロセスで書き込みがあった
                _cacheForGate!.NotifyChanged();
            }

            return F.BuildSnapshot(asOf, attributesAsOf);
        });
        using var cache = new OrgSnapshotCache(gate, _versions, _time);
        _cacheForGate = cache;

        var first = await cache.LoadAsync(F.User("u1_member"), F.Today, F.Today, Ct);
        var second = await cache.LoadAsync(F.User("u1_member"), F.Today, F.Today, Ct);
        var third = await cache.LoadAsync(F.User("u1_member"), F.Today, F.Today, Ct);

        Assert.NotSame(first, second);
        Assert.Same(second, third);
        Assert.Equal(2, calls);
    }

    [Theory(DisplayName = "設定値の検査")]
    [InlineData(-1, 600, false)]
    [InlineData(0, 600, true)]
    [InlineData(5, 0, false)]
    [InlineData(60, 30, false)]
    [InlineData(5, 600, true)]
    public void Options_AreValidated(int intervalSeconds, int maxAgeSeconds, bool valid)
    {
        var options = new OrgSnapshotCacheOptions { VersionCheckInterval = TimeSpan.FromSeconds(intervalSeconds), MaxAge = TimeSpan.FromSeconds(maxAgeSeconds) };

        if (valid)
        {
            options.Validate();
        }
        else
        {
            Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
        }
    }

    private OrgSnapshotCache? _cacheForGate;

    private Task<OrgSnapshot> LoadTodayAsync(ViewerContext viewer) => _cache.LoadAsync(viewer, F.Today, F.Today, Ct);

    private sealed class DelegateSnapshotProvider(Func<ViewerContext, DateOnly, DateOnly, OrgSnapshot> load) : IOrgSnapshotProvider
    {
        public Task<OrgSnapshot> LoadAsync(ViewerContext viewer, DateOnly asOf, DateOnly attributesAsOf, CancellationToken cancellationToken) =>
            Task.FromResult(load(viewer, asOf, attributesAsOf));
    }
}

/// <summary>DB の世代（<see cref="NpgsqlOrgDataVersionSource"/>）と、それを使ったキャッシュ。他のプロセスの書き込みを別の接続で模す。</summary>
public sealed class OrgDataVersionTests(FixtureDatabase db) : IClassFixture<FixtureDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrgFixture F => db.Fixture;

    private NpgsqlOrgDataVersionSource Versions => new(db.AppDataSource);

    [Fact(DisplayName = "世代: 書き込みが無ければ同じ。行の追加・記録期間を閉じる更新・古い記録時刻の行の追加で変わり、見えない行の追加では一般の閲覧者の世代は変わらない")]
    public async Task Version_TracksChanges()
    {
        var general = F.User("u1_member");
        var (g1, h1) = (await Versions.GetVersionAsync(general, Ct), await Versions.GetVersionAsync(F.HrAdmin(), Ct));
        Assert.Equal(g1, await Versions.GetVersionAsync(general, Ct));
        Assert.NotEqual(g1, h1);

        await using var writer = await db.Database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(writer, Ct);

        // 将来の所属（内示）: 人事管理者の世代だけが変わる
        var future = await SampleData.InsertAssignmentAsync(writer, F.Person("s2_member"), F.Unit("U3"), "concurrent", $"[{F.Today.AddDays(30):yyyy-MM-dd},)", Ct);
        var (g2, h2) = (await Versions.GetVersionAsync(general, Ct), await Versions.GetVersionAsync(F.HrAdmin(), Ct));
        Assert.Equal(g1, g2);
        Assert.NotEqual(h1, h2);

        // 記録期間を閉じるだけの更新（行数は変わらない）
        await SampleData.ExecuteAsync(writer, "UPDATE assignment SET recorded_period = tstzrange(lower(recorded_period), clock_timestamp()) WHERE id = $1", Ct, future);
        Assert.NotEqual(h2, await Versions.GetVersionAsync(F.HrAdmin(), Ct));

        // 既に見た最大値より前の記録時刻の行（長いトランザクションが後からコミットされた場合）: 行数で検知する
        var h3 = await Versions.GetVersionAsync(F.HrAdmin(), Ct);
        await SampleData.ExecuteAsync(
            writer,
            "INSERT INTO account_suspension (id, person_id, valid_period, recorded_period) VALUES ($1, $2, daterange($3, NULL), tstzrange(now() - interval '1 day', NULL))",
            Ct,
            EntityId.New(),
            F.Person("dp2_member"),
            F.Today.AddDays(-1));
        Assert.NotEqual(h3, await Versions.GetVersionAsync(F.HrAdmin(), Ct));
        Assert.NotEqual(g2, await Versions.GetVersionAsync(general, Ct));
    }

    [Fact(DisplayName = "キャッシュ: 他のプロセスの書き込みを世代で検知して読み直す")]
    public async Task Cache_ReloadsAfterWriteByAnotherProcess()
    {
        using var cache = new OrgSnapshotCache(
            new NpgsqlOrgSnapshotProvider(db.AppDataSource),
            Versions,
            TimeProvider.System,
            new OrgSnapshotCacheOptions { VersionCheckInterval = TimeSpan.Zero });
        var viewer = F.User("u2_member");

        var first = await cache.LoadAsync(viewer, F.Today, F.Today, Ct);
        Assert.Same(first, await cache.LoadAsync(viewer, F.Today, F.Today, Ct));
        Assert.Null(first.FindPerson(F.Person("r1_member"))!.Suspension);

        // 別の接続（他のプロセス）で一時停止を登録する
        await using (var writer = await db.Database.OpenAppAsync(Ct))
        {
            await SampleData.ActAsHrAdminAsync(writer, Ct);
            await SampleData.ExecuteAsync(
                writer,
                "INSERT INTO account_suspension (id, person_id, valid_period) VALUES ($1, $2, daterange($3, $4))",
                Ct,
                EntityId.New(),
                F.Person("r1_member"),
                F.Today,
                F.Today.AddDays(30));
        }

        var reloaded = await cache.LoadAsync(viewer, F.Today, F.Today, Ct);
        Assert.NotSame(first, reloaded);
        Assert.True(reloaded.FindPerson(F.Person("r1_member"))!.IsSuspended);
    }
}
