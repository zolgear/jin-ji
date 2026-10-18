using JinJi.Application.Sync;
using JinJi.Policy;

namespace JinJi.Sync.DirectApply;

/// <summary>
/// 承認済みの同期計画を Graph に直接反映する（既定は無効。<see cref="DirectApplyOptions"/>）。スクリプトと同じ計画・同じ規則で動く。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>承認済みで新しい計画だけ（<see cref="SyncPlanService.PrepareApplyAsync"/>。jin-ji 側が変わっていれば古い計画にして拒否する）</item>
/// <item>各ユーザーの更新前に現在値を読み、項目ごとに「計画時の値」か「あるべき値」かを確かめる（スクリプトと同じ事前条件。冪等）</item>
/// <item>エラーは記録して次の項目へ進む。結果は実行の記録（mode = direct）として保存し、項目ごとに照合できる</item>
/// <item>アカウントの作成は直接反映しない（スクリプトで、管理者の判断で行う）</item>
/// </list>
/// </remarks>
public sealed class DirectApplier
{
    private readonly SyncPlanService _service;
    private readonly IDirectoryWriteClient _client;
    private readonly TimeProvider _timeProvider;

    public DirectApplier(SyncPlanService service, IDirectoryWriteClient client, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _service = service;
        _client = client;
        _timeProvider = timeProvider;
    }

    /// <param name="whatIf">true なら現在値を読んで事前条件を確かめるだけで、書き込まない。</param>
    public async Task<SyncResultImport> ApplyAsync(ViewerContext viewer, Guid planId, bool whatIf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        var target = await _service.PrepareApplyAsync(viewer, planId, cancellationToken);
        var startedAt = _timeProvider.GetUtcNow();
        var results = new List<SyncLogEntry>();
        foreach (var item in target.Items.Where(i => i.Action != SyncAction.CreateUser).OrderBy(i => i.Sequence))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ApplyItemAsync(item, whatIf, cancellationToken));
        }

        return await _service.RecordDirectRunAsync(viewer, target, whatIf, results, startedAt, cancellationToken);
    }

    private async Task<SyncLogEntry> ApplyItemAsync(SyncPlanItem item, bool whatIf, CancellationToken cancellationToken)
    {
        var observed = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            var objectId = item.ObjectId ?? throw new InvalidOperationException("オブジェクト ID がありません。");
            var properties = item.Changes.Select(c => c.Field).Where(f => f != EntraFields.Manager).Distinct(StringComparer.Ordinal).ToList();
            if (properties.Count > 0)
            {
                foreach (var (key, value) in await _client.GetUserAsync(objectId, properties, cancellationToken))
                {
                    observed[key] = value;
                }
            }

            if (item.Changes.Any(c => c.Field == EntraFields.Manager))
            {
                observed[EntraFields.Manager] = (await _client.GetManagerIdAsync(objectId, cancellationToken))?.ToLowerInvariant();
            }

            var pending = new List<SyncChange>();
            var conflicts = new List<string>();
            foreach (var change in item.Changes)
            {
                var now = observed.GetValueOrDefault(change.Field);
                if (Equal(now, change.Desired, change.Field))
                {
                    continue;
                }

                if (change.CurrentKnown && !Equal(now, change.Current, change.Field))
                {
                    conflicts.Add(change.Field);
                }
                else
                {
                    pending.Add(change);
                }
            }

            if (conflicts.Count > 0)
            {
                return Entry(SyncOutcome.SkippedPrecondition, "計画の後に Entra 側の値が変わったためスキップしました: " + string.Join(", ", conflicts));
            }

            if (pending.Count == 0)
            {
                return Entry(SyncOutcome.AlreadyApplied, null);
            }

            if (whatIf)
            {
                return Entry(SyncOutcome.WouldApply, null);
            }

            if (item.Action == SyncAction.SetManager)
            {
                await _client.SetManagerAsync(objectId, pending[0].Desired ?? throw new InvalidOperationException("上長がありません。"), cancellationToken);
            }
            else
            {
                var body = pending.ToDictionary(
                    c => c.Field,
                    c => c.Field == EntraFields.AccountEnabled ? (object?)(c.Desired == "true") : c.Desired,
                    StringComparer.Ordinal);
                await _client.UpdateUserAsync(objectId, body, cancellationToken);
            }

            return Entry(SyncOutcome.Succeeded, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return Entry(SyncOutcome.Failed, ex.Message);
        }

        SyncLogEntry Entry(SyncOutcome outcome, string? message) =>
            new(item.Id, outcome, new Dictionary<string, string?>(observed, StringComparer.Ordinal), message is null ? null : message[..Math.Min(message.Length, 1000)], _timeProvider.GetUtcNow());
    }

    private static bool Equal(string? left, string? right, string field) =>
        string.Equals(Normalize(left), Normalize(right), field == EntraFields.Manager ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalize(string? value) => value ?? string.Empty;
}
