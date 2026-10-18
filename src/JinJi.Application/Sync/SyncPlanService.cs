using System.Text.Json;
using JinJi.Application.Audit;
using JinJi.Domain;
using JinJi.Policy;

namespace JinJi.Application.Sync;

/// <summary>計画の作成の要求。</summary>
/// <param name="Users">Entra のユーザー一覧（今はファイル入力。Graph の読み取りは同じ形の別の実装で足す）。</param>
/// <param name="SourceFileName">入力のファイル名（記録用）。</param>
/// <param name="SourceSha256">入力の SHA-256。</param>
/// <param name="ProposedByPersonId">起案者（閲覧者が jin-ji 自身のときに必須。人・エージェントでは閲覧者本人・委任元）。</param>
public sealed record SyncPlanRequest(
    IReadOnlyList<DirectoryUser> Users,
    SyncSettings Settings,
    string SourceFileName,
    string SourceSha256,
    Guid? ProposedByPersonId = null);

/// <summary>計画の作成の結果。</summary>
public sealed record SyncPlanCreated(SyncPlanRecord Plan, IReadOnlyList<SyncPlanItem> Items, IReadOnlyList<Guid> Superseded, IReadOnlyList<string> Warnings);

/// <summary>直接反映の準備（承認済みで新しい計画と、実行する項目）。</summary>
public sealed record SyncApplyTarget(SyncPlanRecord Plan, IReadOnlyList<SyncPlanItem> Items, string DataSha256);

/// <summary>
/// Entra ID への同期計画の作成・承認・スクリプト出力・実行結果の取込（アーキテクチャ概要 §8「同期」）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>計画は jin-ji の公開データ（業務日付時点。内示・将来の予定は含めない）と Entra のユーザー一覧の差分。作業領域に保存する</item>
/// <item>使えるのは人事管理者（人、またはその委任を受けたエージェント・jin-ji 自身）。承認・却下は人事管理者の人だけで、起案者は承認できない（§5.5・§7.1）</item>
/// <item>スクリプト出力・直接反映は承認済みの計画だけ。その時点で jin-ji の値を計算し直し、計画と違えば（または期限切れなら）古い計画にして拒否する</item>
/// <item>Entra 側の変化はスクリプト・直接反映が各ユーザーの更新前に現在値を読んで検出する（計画の現在値 = 事前条件）</item>
/// <item>実行ログは、計画の ID とデータの SHA-256 が一致するものだけを取り込む（別の計画・改ざんしたデータのログを拒否する）</item>
/// <item>すべて監査ログに残す（拒否・失敗も）</item>
/// </list>
/// </remarks>
public sealed class SyncPlanService
{
    public const string TargetSystemEntra = "entra_id";
    public const string CreateAction = "sync.plan.create";
    public const string ReadAction = "sync.plan.read";
    public const string ApproveAction = "sync.plan.approve";
    public const string RejectAction = "sync.plan.reject";
    public const string ExportAction = "sync.script.export";
    public const string ResultAction = "sync.result.import";
    public const string ApplyAction = "sync.apply";
    public const string TargetType = "sync_plan";

    private readonly ISyncPlanStore _store;
    private readonly IOrgSnapshotProvider _snapshots;
    private readonly ISyncPlanner _planner;
    private readonly ISyncScriptRenderer _renderer;
    private readonly ISyncLogParser _logParser;
    private readonly AuditWriter _audit;
    private readonly TimeProvider _timeProvider;

    public SyncPlanService(
        ISyncPlanStore store,
        IOrgSnapshotProvider snapshots,
        ISyncPlanner planner,
        ISyncScriptRenderer renderer,
        ISyncLogParser logParser,
        AuditWriter audit,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(logParser);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _snapshots = snapshots;
        _planner = planner;
        _renderer = renderer;
        _logParser = logParser;
        _audit = audit;
        _timeProvider = timeProvider;
    }

    /// <summary>計画を作り、作業領域に保存する（同じ同期先の起案中・承認済みの計画は置き換え済みになる）。</summary>
    public async Task<SyncPlanCreated> CreateAsync(ViewerContext viewer, SyncPlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(request);
        if (!viewer.IsHrAdmin || viewer.Actor == ActorType.Service)
        {
            await _audit.RecordAsync(viewer, CreateAction, AuditOutcome.Denied, [], cancellationToken, TargetType);
            throw new AccessDeniedException("同期計画の作成は人事管理者だけが行えます。");
        }

        var proposer = viewer.Actor == ActorType.System ? request.ProposedByPersonId : viewer.EffectivePersonId;
        if (proposer is null || (viewer.Actor != ActorType.System && request.ProposedByPersonId is { } given && given != proposer))
        {
            throw new ArgumentException("起案者（人）を指定してください（jin-ji 自身の閲覧者では必須。人・エージェントでは閲覧者本人・委任元）。", nameof(request));
        }

        request.Settings.Validate();
        var now = _timeProvider.GetUtcNow();
        var today = BusinessDate.Today(_timeProvider);
        try
        {
            var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
            var identities = await _store.LoadIdentitiesAsync(viewer, request.Settings.SourceSystem, cancellationToken);
            var draft = _planner.Plan(new SyncPlanInput(snapshot, identities, request.Users, request.Settings, today));
            var plan = new SyncPlanRecord
            {
                Id = EntityId.New(),
                Status = SyncPlanStatus.Proposed,
                Settings = request.Settings,
                SettingsSha256 = request.Settings.Sha256(),
                SourceFileName = request.SourceFileName,
                SourceSha256 = request.SourceSha256,
                DirectoryUserCount = request.Users.Count,
                JinjiFingerprint = draft.JinjiFingerprint,
                BusinessDate = today,
                Summary = Summarize(draft.Items),
                ProposedByPersonId = proposer.Value,
                ProposedViaActor = viewer.Actor,
                ProposedViaClientId = viewer.ClientId,
                ProposedAt = now,
                ExpiresAt = now.AddDays(request.Settings.PlanMaxAgeDays),
            };
            var superseded = await _store.InsertPlanAsync(viewer, plan, draft.Items, cancellationToken);
            await _audit.RecordAsync(viewer, CreateAction, AuditOutcome.Success, Subjects(draft.Items), cancellationToken, TargetType, plan.Id);
            return new SyncPlanCreated(plan, draft.Items, superseded, draft.Warnings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            await _audit.RecordAsync(viewer, CreateAction, AuditOutcome.Failed, [], cancellationToken, TargetType);
            throw;
        }
    }

    /// <summary>計画の一覧（作成の新しい順。人の値は含まない）。</summary>
    public async Task<IReadOnlyList<SyncPlanRecord>> ListAsync(ViewerContext viewer, int limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireOperatorAsync(viewer, ReadAction, null, cancellationToken);
        var plans = await _store.ListPlansAsync(viewer, Math.Clamp(limit, 1, 100), cancellationToken);
        await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, [], cancellationToken, TargetType);
        return plans;
    }

    /// <summary>計画の詳細（項目と、項目ごとの実行結果の照合）。</summary>
    public async Task<SyncPlanDetail> GetAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        await RequireOperatorAsync(viewer, ReadAction, planId, cancellationToken);
        var plan = await FindAsync(viewer, ReadAction, planId, cancellationToken);
        var items = await _store.ListItemsAsync(viewer, planId, cancellationToken);
        var runs = await _store.ListRunsAsync(viewer, planId, cancellationToken);
        await _audit.RecordAsync(viewer, ReadAction, AuditOutcome.Success, Subjects(items), cancellationToken, TargetType, planId);
        return new SyncPlanDetail(plan, items, Reconcile(items, runs));
    }

    /// <summary>起案中の計画を承認する（人事管理者の人。起案者は不可）。承認の時点で jin-ji の値が変わっていれば古い計画にして拒否する。</summary>
    public async Task<SyncPlanRecord> ApproveAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var (plan, items) = await PrepareDecisionAsync(viewer, ApproveAction, planId, cancellationToken);
        await EnsureFreshAsync(viewer, ApproveAction, plan, items, cancellationToken);
        if (!await _store.DecideAsync(viewer, planId, approve: true, cancellationToken))
        {
            await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Failed, Subjects(items), cancellationToken, TargetType, planId);
            throw new InvalidOperationException($"計画 {planId} を承認できませんでした（状態が変わった可能性があります）。");
        }

        await _audit.RecordAsync(viewer, ApproveAction, AuditOutcome.Success, Subjects(items), cancellationToken, TargetType, planId);
        return (await _store.FindPlanAsync(viewer, planId, cancellationToken))!;
    }

    /// <summary>起案中の計画を却下する（人事管理者の人。起案者は不可）。</summary>
    public async Task<SyncPlanRecord> RejectAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var (_, items) = await PrepareDecisionAsync(viewer, RejectAction, planId, cancellationToken);
        if (!await _store.DecideAsync(viewer, planId, approve: false, cancellationToken))
        {
            await _audit.RecordAsync(viewer, RejectAction, AuditOutcome.Failed, Subjects(items), cancellationToken, TargetType, planId);
            throw new InvalidOperationException($"計画 {planId} を却下できませんでした（状態が変わった可能性があります）。");
        }

        await _audit.RecordAsync(viewer, RejectAction, AuditOutcome.Success, Subjects(items), cancellationToken, TargetType, planId);
        return (await _store.FindPlanAsync(viewer, planId, cancellationToken))!;
    }

    /// <summary>
    /// 承認済みの計画から、スクリプト・データ・差分レポートを作る（人事管理者の人か jin-ji 自身。エージェントは不可）。
    /// jin-ji の値が変わっていれば（期限切れなら）古い計画にして拒否する。
    /// </summary>
    public async Task<SyncScriptBundle> ExportScriptAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var target = await PrepareExecutionAsync(viewer, ExportAction, planId, cancellationToken);
        var bundle = _renderer.Render(target.Plan, target.Items);
        await _audit.RecordAsync(viewer, ExportAction, AuditOutcome.Success, Subjects(target.Items), cancellationToken, TargetType, planId);
        return bundle;
    }

    /// <summary>直接反映の準備（承認済みで新しい計画の、実行する項目）。直接反映は JinJi.Sync の DirectApplier が行い、結果を <see cref="RecordDirectRunAsync"/> で記録する。</summary>
    public async Task<SyncApplyTarget> PrepareApplyAsync(ViewerContext viewer, Guid planId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var target = await PrepareExecutionAsync(viewer, ApplyAction, planId, cancellationToken);
        return target with { Items = [.. target.Items.Where(i => i.InScript)] };
    }

    /// <summary>直接反映の結果を記録する。</summary>
    public async Task<SyncResultImport> RecordDirectRunAsync(
        ViewerContext viewer,
        SyncApplyTarget target,
        bool whatIf,
        IReadOnlyList<SyncLogEntry> results,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(results);
        var run = new SyncRunRecord
        {
            Id = EntityId.New(),
            PlanId = target.Plan.Id,
            Mode = SyncRunMode.Direct,
            WhatIf = whatIf,
            DataSha256 = target.DataSha256,
            StartedAt = startedAt,
            FinishedAt = _timeProvider.GetUtcNow(),
            RecordedByPersonId = viewer.PersonId,
            RecordedViaActor = viewer.Actor,
            RecordedAt = _timeProvider.GetUtcNow(),
            Results = results,
        };
        await _store.InsertRunAsync(viewer, run, cancellationToken);
        var items = await _store.ListItemsAsync(viewer, target.Plan.Id, cancellationToken);
        await _audit.RecordAsync(viewer, ApplyAction, AuditOutcome.Success, Subjects(items.Where(i => i.InScript)), cancellationToken, TargetType, target.Plan.Id);
        return Summarize(run, items, await _store.ListRunsAsync(viewer, target.Plan.Id, cancellationToken));
    }

    /// <summary>
    /// スクリプトの実行ログを取り込み、計画の項目ごとの結果を記録して照合する（人事管理者の人か jin-ji 自身）。
    /// 取り込めるのは承認された計画（実行後に古くなった計画も含む）のログで、計画の ID とデータの SHA-256 が一致するものだけ。
    /// </summary>
    public async Task<SyncResultImport> ImportResultsAsync(ViewerContext viewer, Guid planId, byte[] log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(log);
        await RequireOperatorAsync(viewer, ResultAction, planId, cancellationToken);
        var plan = await FindAsync(viewer, ResultAction, planId, cancellationToken);
        var items = await _store.ListItemsAsync(viewer, planId, cancellationToken);
        if (plan.DecidedByPersonId is null || plan.Status == SyncPlanStatus.Rejected)
        {
            await _audit.RecordAsync(viewer, ResultAction, AuditOutcome.Failed, [], cancellationToken, TargetType, planId);
            throw new InvalidOperationException($"計画 {planId} は承認されていないので、実行結果を取り込めません。");
        }

        SyncExecutionLog parsed;
        try
        {
            parsed = _logParser.Parse(log);
            if (parsed.PlanId != planId)
            {
                throw new SyncLogFormatException("実行ログの計画 ID が一致しません（別の計画のログです）。");
            }

            if (!string.Equals(parsed.DataSha256, _renderer.DataSha256(plan, items), StringComparison.Ordinal))
            {
                throw new SyncLogFormatException("実行ログのデータの SHA-256 が計画と一致しません（データが改ざんされたか、別の出力のログです）。");
            }
        }
        catch (SyncLogFormatException)
        {
            await _audit.RecordAsync(viewer, ResultAction, AuditOutcome.Failed, [], cancellationToken, TargetType, planId);
            throw;
        }

        var scriptItems = items.Where(i => i.InScript).Select(i => i.Id).ToHashSet();
        var entries = parsed.Entries.Where(e => scriptItems.Contains(e.ItemId))
            .GroupBy(e => e.ItemId)
            .Select(g => g.Last())
            .ToList();
        var run = new SyncRunRecord
        {
            Id = EntityId.New(),
            PlanId = planId,
            Mode = SyncRunMode.Script,
            WhatIf = parsed.WhatIf,
            LogSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(log)),
            DataSha256 = parsed.DataSha256,
            StartedAt = parsed.StartedAt,
            FinishedAt = parsed.FinishedAt,
            RecordedByPersonId = viewer.PersonId,
            RecordedViaActor = viewer.Actor,
            RecordedAt = _timeProvider.GetUtcNow(),
            Results = entries,
            UnknownEntryCount = parsed.Entries.Count(e => !scriptItems.Contains(e.ItemId)),
        };
        await _store.InsertRunAsync(viewer, run, cancellationToken);
        var subjects = items.Where(i => entries.Any(e => e.ItemId == i.Id));
        await _audit.RecordAsync(viewer, ResultAction, AuditOutcome.Success, Subjects(subjects), cancellationToken, TargetType, planId);
        return Summarize(run, items, await _store.ListRunsAsync(viewer, planId, cancellationToken));
    }

    /// <summary>項目ごとの最新の結果（本番の実行を優先し、無ければドライラン）。スクリプトに含めた項目だけ。</summary>
    public static IReadOnlyList<SyncItemReconciliation> Reconcile(IReadOnlyList<SyncPlanItem> items, IReadOnlyList<SyncRunRecord> runs)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(runs);
        var ordered = runs.OrderBy(r => r.WhatIf ? 0 : 1).ThenBy(r => r.RecordedAt).ToList();
        var latest = new Dictionary<Guid, (SyncLogEntry Entry, Guid RunId)>();
        foreach (var run in ordered)
        {
            foreach (var entry in run.Results)
            {
                latest[entry.ItemId] = (entry, run.Id);
            }
        }

        return [.. items.Where(i => i.InScript).OrderBy(i => i.Sequence).Select(i => latest.TryGetValue(i.Id, out var r)
            ? new SyncItemReconciliation(i.Id, i.Sequence, i.Action, i.Priority, r.Entry.Outcome, r.RunId, r.Entry.Message)
            : new SyncItemReconciliation(i.Id, i.Sequence, i.Action, i.Priority, null, null, null))];
    }

    /// <summary>種別ごとの件数（total・in_script・priority:* ・action:*）。</summary>
    public static IReadOnlyDictionary<string, int> Summarize(IReadOnlyList<SyncPlanItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var summary = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["total"] = items.Count,
            ["in_script"] = items.Count(i => i.InScript),
        };
        foreach (var group in items.GroupBy(i => i.Priority))
        {
            summary["priority:" + Code(group.Key)] = group.Count();
        }

        foreach (var group in items.GroupBy(i => i.Action))
        {
            summary["action:" + Code(group.Key)] = group.Count();
        }

        return summary;
    }

    /// <summary>列挙値のコード（snake_case）。</summary>
    public static string Code<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static SyncResultImport Summarize(SyncRunRecord run, IReadOnlyList<SyncPlanItem> items, IReadOnlyList<SyncRunRecord> runs)
    {
        var counts = run.Results.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());
        var reconciliation = Reconcile(items, runs);
        var inRun = run.Results.Select(r => r.ItemId).ToHashSet();
        return new SyncResultImport(run.Id, run.WhatIf, counts, items.Count(i => i.InScript && !inRun.Contains(i.Id)), run.UnknownEntryCount, reconciliation);
    }

    private async Task<(SyncPlanRecord Plan, IReadOnlyList<SyncPlanItem> Items)> PrepareDecisionAsync(
        ViewerContext viewer,
        string action,
        Guid planId,
        CancellationToken cancellationToken)
    {
        if (viewer.Actor != ActorType.User || !viewer.IsHrAdmin || viewer.PersonId is null)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, planId);
            throw new AccessDeniedException("同期計画の承認・却下は人事管理者（人）だけが行えます。");
        }

        var plan = await FindAsync(viewer, action, planId, cancellationToken);
        var items = await _store.ListItemsAsync(viewer, planId, cancellationToken);
        if (plan.ProposedByPersonId == viewer.PersonId)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, Subjects(items), cancellationToken, TargetType, planId);
            throw new AccessDeniedException("計画の起案者は、その計画を承認・却下できません（職務分掌）。");
        }

        if (plan.Status != SyncPlanStatus.Proposed)
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, Subjects(items), cancellationToken, TargetType, planId);
            throw new InvalidOperationException($"計画 {planId} は起案中ではありません（{Code(plan.Status)}）。");
        }

        return (plan, items);
    }

    private async Task<SyncApplyTarget> PrepareExecutionAsync(ViewerContext viewer, string action, Guid planId, CancellationToken cancellationToken)
    {
        await RequireOperatorAsync(viewer, action, planId, cancellationToken);
        var plan = await FindAsync(viewer, action, planId, cancellationToken);
        var items = await _store.ListItemsAsync(viewer, planId, cancellationToken);
        if (plan.Status is not (SyncPlanStatus.Approved or SyncPlanStatus.Applied))
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, Subjects(items), cancellationToken, TargetType, planId);
            throw new InvalidOperationException($"計画 {planId} は承認済みではありません（{Code(plan.Status)}）。承認済みの計画だけをスクリプト出力・直接反映できます。");
        }

        await EnsureFreshAsync(viewer, action, plan, items, cancellationToken);
        return new SyncApplyTarget(plan, items, _renderer.DataSha256(plan, items));
    }

    /// <summary>計画が新しいか（期限内で、jin-ji 側のあるべき値が計画と同じ）。古ければ stale にして拒否する。</summary>
    private async Task EnsureFreshAsync(ViewerContext viewer, string action, SyncPlanRecord plan, IReadOnlyList<SyncPlanItem> items, CancellationToken cancellationToken)
    {
        string? reason = null;
        if (plan.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            reason = $"期限（{plan.ExpiresAt:yyyy-MM-dd HH:mm} UTC）を過ぎています";
        }
        else
        {
            var today = BusinessDate.Today(_timeProvider);
            var snapshot = await _snapshots.LoadAsync(viewer, today, today, cancellationToken);
            var identities = await _store.LoadIdentitiesAsync(viewer, plan.Settings.SourceSystem, cancellationToken);
            if (!string.Equals(_planner.Fingerprint(snapshot, identities, plan.Settings, today), plan.JinjiFingerprint, StringComparison.Ordinal))
            {
                reason = "計画の作成後に jin-ji のデータ（人・所属・組織・一時停止・退職・外部 ID の対応）が変わりました";
            }
        }

        if (reason is null)
        {
            return;
        }

        if (plan.Status is SyncPlanStatus.Proposed or SyncPlanStatus.Approved)
        {
            await _store.MarkStaleAsync(viewer, plan.Id, cancellationToken);
        }

        await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, Subjects(items), cancellationToken, TargetType, plan.Id);
        throw new SyncPlanStaleException($"計画 {plan.Id} は古い計画です（{reason}）。計画を作り直してください。");
    }

    /// <summary>計画の参照・出力・結果の取込は、人事管理者の人か jin-ji 自身（エージェント・社内システムは不可）。</summary>
    private async Task RequireOperatorAsync(ViewerContext viewer, string action, Guid? planId, CancellationToken cancellationToken)
    {
        if (!viewer.IsHrAdmin || viewer.Actor is not (ActorType.User or ActorType.System) || (viewer.Actor == ActorType.User && viewer.PersonId is null))
        {
            await _audit.RecordAsync(viewer, action, AuditOutcome.Denied, [], cancellationToken, TargetType, planId);
            throw new AccessDeniedException("同期計画は人事管理者だけが扱えます。");
        }
    }

    private async Task<SyncPlanRecord> FindAsync(ViewerContext viewer, string action, Guid planId, CancellationToken cancellationToken)
    {
        if (await _store.FindPlanAsync(viewer, planId, cancellationToken) is { } plan)
        {
            return plan;
        }

        await _audit.RecordAsync(viewer, action, AuditOutcome.Failed, [], cancellationToken, TargetType, planId);
        throw new SyncPlanNotFoundException($"計画 {planId} がありません。");
    }

    private static IEnumerable<Guid> Subjects(IEnumerable<SyncPlanItem> items) => items.Select(i => i.PersonId).OfType<Guid>();
}
