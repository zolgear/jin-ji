using System.IO.Compression;
using JinJi.Application.Access;
using JinJi.Application.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>
/// Entra ID への同期計画（<c>/api/v1/sync/plans</c>）。一覧・詳細・承認・却下・スクリプト一式の取得（アーキテクチャ概要 §8「同期」）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>使えるのは人事管理者の人だけ（AI エージェント・社内システムは不可。§7.1「Entra ID への同期計画の確定」は人が承認する）</item>
/// <item>承認・却下は起案者以外（職務分掌）。承認・スクリプトの取得の時点で jin-ji の値が変わっていれば古い計画にして 409</item>
/// <item>スクリプトは承認済みの計画だけ。ZIP（スクリプト・データ・差分レポート）で返す</item>
/// <item>直接反映は API からは起動しない（書き込みの資格情報を API の役割に置かない。CLI <c>sync-apply</c> だけ）</item>
/// </list>
/// </remarks>
internal static class SyncEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/sync/plans", ListAsync)
            .WithName("ListSyncPlans")
            .WithSummary("同期計画の一覧（人事管理者）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Sync, false))
            .Produces<IReadOnlyList<SyncPlanResponse>>();

        api.MapGet("/sync/plans/{planId}", GetAsync)
            .WithName("GetSyncPlan")
            .WithSummary("同期計画の詳細（項目・根拠・実行結果の照合。人事管理者）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Sync, false))
            .Produces<SyncPlanDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/sync/plans/{planId}/approve", ApproveAsync)
            .WithName("ApproveSyncPlan")
            .WithSummary("同期計画を承認する（人事管理者。起案者は不可）")
            .WithDescription("起案中でなければ 409。承認の時点で jin-ji のデータが計画の作成後に変わっていれば、計画を古い計画（stale）にして 409。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Sync, false))
            .Produces<SyncPlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/sync/plans/{planId}/reject", RejectAsync)
            .WithName("RejectSyncPlan")
            .WithSummary("同期計画を却下する（人事管理者。起案者は不可）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Sync, false))
            .Produces<SyncPlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapGet("/sync/plans/{planId}/script", ScriptAsync)
            .WithName("GetSyncScript")
            .WithSummary("承認済みの同期計画のスクリプト一式（ZIP: .ps1・.data.json・.report.md・.report.csv。人事管理者）")
            .WithDescription("承認済みでなければ 409。jin-ji のデータが計画の作成後に変わっていれば、計画を古い計画（stale）にして 409。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Sync, false))
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ListAsync(string? limit, HttpContext http, SyncPlanService service, CancellationToken cancellationToken)
    {
        var viewer = http.Viewer();
        if (!AdminAccess.IsHrAdminUser(viewer))
        {
            return Problems.Forbidden(http, "人事管理者だけが使えます。");
        }

        if (!ApiRequest.TryParseLimit(limit, 20, 100, out var count))
        {
            return Problems.BadRequest(http, "limit は 1〜100 で指定してください。");
        }

        var plans = await service.ListAsync(viewer, count, cancellationToken);
        return Results.Ok(plans.Select(SyncPlanResponse.From).ToList());
    }

    private static Task<IResult> GetAsync(string planId, HttpContext http, SyncPlanService service, CancellationToken cancellationToken) =>
        RunAsync(http, planId, async (viewer, id) => Results.Ok(SyncPlanDetailResponse.From(await service.GetAsync(viewer, id, cancellationToken))));

    private static Task<IResult> ApproveAsync(string planId, HttpContext http, SyncPlanService service, CancellationToken cancellationToken) =>
        RunAsync(http, planId, async (viewer, id) => Results.Ok(SyncPlanResponse.From(await service.ApproveAsync(viewer, id, cancellationToken))));

    private static Task<IResult> RejectAsync(string planId, HttpContext http, SyncPlanService service, CancellationToken cancellationToken) =>
        RunAsync(http, planId, async (viewer, id) => Results.Ok(SyncPlanResponse.From(await service.RejectAsync(viewer, id, cancellationToken))));

    private static Task<IResult> ScriptAsync(string planId, HttpContext http, SyncPlanService service, CancellationToken cancellationToken) =>
        RunAsync(http, planId, async (viewer, id) =>
        {
            var bundle = await service.ExportScriptAsync(viewer, id, cancellationToken);
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, content) in bundle.Files)
                {
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    await using var stream = entry.Open();
                    await stream.WriteAsync(content, cancellationToken);
                }
            }

            http.Response.Headers.CacheControl = "no-store";
            return Results.File(buffer.ToArray(), "application/zip", $"jinji-entra-sync-{id:D}.zip");
        });

    private static async Task<IResult> RunAsync(HttpContext http, string planId, Func<Policy.ViewerContext, Guid, Task<IResult>> action)
    {
        var viewer = http.Viewer();
        if (!AdminAccess.IsHrAdminUser(viewer))
        {
            return Problems.Forbidden(http, "人事管理者だけが使えます。");
        }

        if (!ApiRequest.TryParseId(planId, out var id))
        {
            return Problems.NotFound(http);
        }

        try
        {
            return await action(viewer, id);
        }
        catch (SyncPlanNotFoundException)
        {
            return Problems.NotFound(http);
        }
        catch (SyncPlanStaleException ex)
        {
            return Problems.Conflict(http, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Problems.Conflict(http, ex.Message);
        }
    }
}

/// <summary>同期計画の要約（人の値は含まない）。</summary>
public sealed record SyncPlanResponse
{
    public required Guid Id { get; init; }

    public required string TargetSystem { get; init; }

    [OpenApiEnum(typeof(SyncPlanStatus))]
    public required string Status { get; init; }

    public required DateOnly BusinessDate { get; init; }

    public required Guid ProposedByPersonId { get; init; }

    public required DateTimeOffset ProposedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public Guid? DecidedByPersonId { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public DateTimeOffset? AppliedAt { get; init; }

    public required string SourceFileName { get; init; }

    public required int DirectoryUserCount { get; init; }

    public required string SettingsName { get; init; }

    public required string SettingsSha256 { get; init; }

    /// <summary>IT 全般統制の項目（退職者の無効化・一時停止者のサインイン停止・除外の警告）の件数。</summary>
    public required int CriticalCount { get; init; }

    /// <summary>種別ごとの件数（total・in_script・priority:*・action:*）。</summary>
    public required IReadOnlyDictionary<string, int> Summary { get; init; }

    public static SyncPlanResponse From(SyncPlanRecord plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new SyncPlanResponse
        {
            Id = plan.Id,
            TargetSystem = plan.TargetSystem,
            Status = SyncPlanService.Code(plan.Status),
            BusinessDate = plan.BusinessDate,
            ProposedByPersonId = plan.ProposedByPersonId,
            ProposedAt = plan.ProposedAt,
            ExpiresAt = plan.ExpiresAt,
            DecidedByPersonId = plan.DecidedByPersonId,
            DecidedAt = plan.DecidedAt,
            AppliedAt = plan.AppliedAt,
            SourceFileName = plan.SourceFileName,
            DirectoryUserCount = plan.DirectoryUserCount,
            SettingsName = plan.Settings.Name,
            SettingsSha256 = plan.SettingsSha256,
            CriticalCount = plan.Summary.GetValueOrDefault("priority:critical"),
            Summary = plan.Summary,
        };
    }
}

/// <summary>同期計画の詳細。</summary>
public sealed record SyncPlanDetailResponse(SyncPlanResponse Plan, IReadOnlyList<SyncPlanItemResponse> Items)
{
    public static SyncPlanDetailResponse From(SyncPlanDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var results = detail.Reconciliation.ToDictionary(r => r.ItemId);
        return new SyncPlanDetailResponse(
            SyncPlanResponse.From(detail.Plan),
            [.. detail.Items.Select(i => SyncPlanItemResponse.From(i, results.GetValueOrDefault(i.Id)))]);
    }
}

/// <summary>同期計画の項目（変更・根拠・最新の実行結果）。</summary>
public sealed record SyncPlanItemResponse
{
    public required Guid Id { get; init; }

    public required int Sequence { get; init; }

    [OpenApiEnum(typeof(SyncPriority))]
    public required string Priority { get; init; }

    [OpenApiEnum(typeof(SyncAction))]
    public required string Action { get; init; }

    public required bool InScript { get; init; }

    public Guid? PersonId { get; init; }

    public string? EmployeeNumber { get; init; }

    public string? ObjectId { get; init; }

    public string? UserPrincipalName { get; init; }

    public required IReadOnlyList<SyncChangeResponse> Changes { get; init; }

    public string? Code { get; init; }

    public required IReadOnlyList<SyncEvidenceResponse> Basis { get; init; }

    public string? Note { get; init; }

    /// <summary>最新の実行結果（succeeded / already_applied / skipped_precondition / failed / would_apply。未実行なら null）。</summary>
    [OpenApiEnum(typeof(SyncOutcome))]
    public string? Outcome { get; init; }

    public static SyncPlanItemResponse From(SyncPlanItem item, SyncItemReconciliation? result)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new SyncPlanItemResponse
        {
            Id = item.Id,
            Sequence = item.Sequence,
            Priority = SyncPlanService.Code(item.Priority),
            Action = SyncPlanService.Code(item.Action),
            InScript = item.InScript,
            PersonId = item.PersonId,
            EmployeeNumber = item.EmployeeNumber,
            ObjectId = item.ObjectId,
            UserPrincipalName = item.UserPrincipalName,
            Changes = [.. item.Changes.Select(c => new SyncChangeResponse(c.Field, c.Current, c.Desired, c.CurrentKnown))],
            Code = item.Basis.Code,
            Basis = [.. item.Basis.Jinji.Select(e => new SyncEvidenceResponse(e.Field, e.Source, e.Reference, e.Value))],
            Note = item.Note,
            Outcome = result?.Outcome is { } outcome ? SyncPlanService.Code(outcome) : null,
        };
    }
}

/// <summary>項目の変更（Entra の現在値とあるべき値）。</summary>
public sealed record SyncChangeResponse(string Field, string? Current, string? Desired, bool CurrentKnown);

/// <summary>根拠（jin-ji のどのデータか）。</summary>
public sealed record SyncEvidenceResponse(string Field, string Source, string? Reference, string? Value);
