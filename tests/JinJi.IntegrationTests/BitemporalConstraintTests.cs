using JinJi.IntegrationTests.Support;
using Npgsql;

namespace JinJi.IntegrationTests;

/// <summary>
/// 二つの時間軸と排他制約。データ操作はアプリ用ロール（jinji_app）で行い、権限が足りることも確かめる。
/// </summary>
public sealed class BitemporalConstraintTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private NpgsqlConnection _app = null!;
    private SampleData.Ids _ids = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _database = await TestDatabase.CreateAsync(Ct);
        await _database.MigrateAsync(Ct);
        _app = await _database.OpenAppAsync(Ct);
        await SampleData.ActAsHrAdminAsync(_app, Ct);
        _ids = await SampleData.InsertBasicsAsync(_app, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact(DisplayName = "同一人物の主務の有効期間の重複は排他制約で拒否される")]
    public async Task OverlappingPrimaryAssignments_AreRejectedByExclusionConstraint()
    {
        await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "[2026-04-01,)", Ct);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitB, "primary", "[2026-10-01,2027-04-01)", Ct));

        Assert.Equal(PostgresErrorCodes.ExclusionViolation, ex.SqlState);
        Assert.Equal("assignment_primary_no_overlap", ex.ConstraintName);
    }

    [Fact(DisplayName = "主務の期間が接していれば登録できる")]
    public async Task AdjacentPrimaryAssignments_AreAccepted()
    {
        await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "[2026-04-01,2026-10-01)", Ct);
        await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitB, "primary", "[2026-10-01,)", Ct);
    }

    [Fact(DisplayName = "兼務は主務と期間が重なってよい")]
    public async Task ConcurrentAssignment_MayOverlapPrimary()
    {
        await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "[2026-04-01,)", Ct);
        await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitB, "concurrent", "[2026-04-01,)", Ct);
    }

    [Fact(DisplayName = "旧行の記録時間を閉じれば同じ期間の主務を訂正として追加できる")]
    public async Task ClosingRecordedPeriod_AllowsCorrectionRowForSameValidPeriod()
    {
        var original = await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "[2026-04-01,)", Ct);

        // 訂正: 旧行の recorded_period を閉じ（上書きはしない）、新しい行を追加する。
        // 旧行の記録開始と同じ時刻で閉じると空の範囲になるため、別トランザクションの時刻で閉じる
        var closed = await SampleData.ExecuteAsync(
            _app,
            "UPDATE assignment SET recorded_period = tstzrange(lower(recorded_period), clock_timestamp()) WHERE id = $1",
            Ct,
            original);
        Assert.Equal(1, closed);

        await SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitB, "primary", "[2026-04-01,)", Ct);
    }

    [Fact(DisplayName = "有効期間の開始日がない行は拒否される")]
    public async Task ValidPeriodWithoutLowerBound_IsRejected()
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            SampleData.InsertAssignmentAsync(_app, _ids.Person, _ids.OrgUnitA, "primary", "(,2026-04-01)", Ct));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }
}
