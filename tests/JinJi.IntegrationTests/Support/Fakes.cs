using JinJi.Application;
using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Domain.Organization;
using JinJi.Policy;

namespace JinJi.IntegrationTests.Support;

/// <summary>業務日付を固定する TimeProvider（日本時間の正午）。</summary>
internal sealed class FixedTimeProvider(DateOnly today) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() =>
        new DateTimeOffset(today.ToDateTime(new TimeOnly(12, 0)), BusinessDate.UtcOffset).ToUniversalTime();
}

/// <summary>フィクスチャから全データ（RLS なし）のスナップショットを返す。呼び出しを記録する。</summary>
internal sealed class FixtureSnapshotProvider(OrgFixture fixture) : IOrgSnapshotProvider
{
    public List<(ViewerContext Viewer, DateOnly AsOf, DateOnly AttributesAsOf)> Calls { get; } = [];

    public Exception? Failure { get; set; }

    public Task<OrgSnapshot> LoadAsync(ViewerContext viewer, DateOnly asOf, DateOnly attributesAsOf, CancellationToken cancellationToken)
    {
        Calls.Add((viewer, asOf, attributesAsOf));
        return Failure is { } failure ? Task.FromException<OrgSnapshot>(failure) : Task.FromResult(fixture.BuildSnapshot(asOf, attributesAsOf));
    }
}

/// <summary>メモリ上のポリシーの保存先。</summary>
internal sealed class InMemoryPolicyRuleStore(IEnumerable<PolicyRule> active) : IPolicyRuleStore
{
    public Dictionary<Guid, StoredPolicyRule> Rules { get; } =
        active.ToDictionary(r => r.Id, r => new StoredPolicyRule(r, PolicyRuleStatus.Approved, Guid.Empty, Guid.Empty));

    public Task<PolicySet> LoadActiveAsync(ViewerContext viewer, CancellationToken cancellationToken) =>
        Task.FromResult(new PolicySet(Rules.Values.Where(r => r.Status == PolicyRuleStatus.Approved).Select(r => r.Rule)));

    public Task<StoredPolicyRule?> FindAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken) =>
        Task.FromResult(Rules.GetValueOrDefault(ruleId));

    public Task<IReadOnlyList<PolicyRule>> ListOpenAsync(ViewerContext viewer, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PolicyRule>>(
            [.. Rules.Values.Where(r => r.Status is PolicyRuleStatus.Proposed or PolicyRuleStatus.Approved).Select(r => r.Rule)]);

    public Task InsertProposalAsync(ViewerContext viewer, PolicyRule rule, CancellationToken cancellationToken)
    {
        Rules.Add(rule.Id, new StoredPolicyRule(rule, PolicyRuleStatus.Proposed, viewer.EffectivePersonId!.Value, null));
        return Task.CompletedTask;
    }

    public Task<bool> ApproveAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        if (!Rules.TryGetValue(ruleId, out var stored) || stored.Status != PolicyRuleStatus.Proposed)
        {
            return Task.FromResult(false);
        }

        Rules[ruleId] = stored with { Status = PolicyRuleStatus.Approved, DecidedByPersonId = viewer.EffectivePersonId };
        return Task.FromResult(true);
    }

    public Task<bool> ProposeRetirementAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        if (!Rules.TryGetValue(ruleId, out var stored) || stored.Status != PolicyRuleStatus.Approved || stored.IsRetirementProposed)
        {
            return Task.FromResult(false);
        }

        Rules[ruleId] = stored with { RetirementProposedByPersonId = viewer.EffectivePersonId };
        return Task.FromResult(true);
    }

    public Task<bool> ApproveRetirementAsync(ViewerContext viewer, Guid ruleId, CancellationToken cancellationToken)
    {
        if (!Rules.TryGetValue(ruleId, out var stored) || !stored.IsRetirementProposed)
        {
            return Task.FromResult(false);
        }

        Rules[ruleId] = stored with { Status = PolicyRuleStatus.Retired };
        return Task.FromResult(true);
    }
}

/// <summary>手で進める TimeProvider（キャッシュの時間切れのテスト用）。</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}

/// <summary>世代を手で決める <see cref="IOrgDataVersionSource"/>。呼び出し回数を数える。</summary>
internal sealed class ManualVersionSource : IOrgDataVersionSource
{
    public string Version { get; set; } = "v1";

    public int Calls { get; private set; }

    public Task<string> GetVersionAsync(ViewerContext viewer, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Version);
    }
}

/// <summary>監査ログを記録するだけの保存先。</summary>
internal sealed class RecordingAuditSink : IAuditSink
{
    public List<AuditRecord> Records { get; } = [];

    public Task WriteAsync(ViewerContext viewer, AuditRecord record, CancellationToken cancellationToken)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }
}

/// <summary>メモリ上の段の定義の保存先（起案・承認の状態だけを持つ。有効な定義はスナップショット側が持つ）。</summary>
internal sealed class InMemoryOrgLevelStore : IOrgLevelStore
{
    public Dictionary<Guid, StoredOrgLevelScheme> Schemes { get; } = [];

    /// <summary>組織が変わる日（今日より後）。</summary>
    public List<DateOnly> OrgChangeDates { get; } = [];

    public Task<StoredOrgLevelScheme?> FindAsync(ViewerContext viewer, Guid schemeId, CancellationToken cancellationToken) =>
        Task.FromResult(Schemes.GetValueOrDefault(schemeId));

    public Task InsertProposalAsync(ViewerContext viewer, Guid schemeId, OrgLevelScheme scheme, CancellationToken cancellationToken)
    {
        Schemes.Add(schemeId, new StoredOrgLevelScheme(schemeId, scheme, OrgLevelSchemeStatus.Proposed, viewer.EffectivePersonId, null));
        return Task.CompletedTask;
    }

    public Task<bool> ApproveAsync(ViewerContext viewer, Guid schemeId, CancellationToken cancellationToken)
    {
        if (!Schemes.TryGetValue(schemeId, out var stored) || stored.Status != OrgLevelSchemeStatus.Proposed)
        {
            return Task.FromResult(false);
        }

        Schemes[schemeId] = stored with { Status = OrgLevelSchemeStatus.Approved, DecidedByPersonId = viewer.EffectivePersonId };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<DateOnly>> GetOrgChangeDatesAfterAsync(ViewerContext viewer, DateOnly after, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DateOnly>>([.. OrgChangeDates.Where(d => d > after).Order()]);
}
