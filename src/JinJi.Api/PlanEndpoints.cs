using JinJi.Application.Access;
using JinJi.Application.FieldManagement;
using JinJi.Application.Plans;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>
/// 素案・申請版・提案資料（<c>/api/v1/plans</c>。ADR-0013 決定 8・9）。素案は公開されない作業領域で、公開側を変えない。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>見えるのは作成者と共有した人だけ。書けるのは作成者（人・AI エージェントの委任元）だけ</item>
/// <item>シミュレーションの人は ID だけで、閲覧者に見える項目だけを並べる（氏名は人の API で閲覧者の権限で引く）</item>
/// <item>承認の結果の記録は、申請版の組織すべての範囲を持つ人か、設定 <c>JinJi:Plans:ApprovalClients</c> の社内システムだけ</item>
/// </list>
/// </remarks>
internal static class PlanEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/plans", ListAsync)
            .WithName("ListPlans")
            .WithSummary("素案の一覧（自分の素案と共有された素案。更新の新しい順）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<IReadOnlyList<PlanResponse>>();

        api.MapPost("/plans", CreateAsync)
            .WithName("CreatePlan")
            .WithSummary("素案を作る（人・AI エージェント。公開側は変わらない）")
            .WithDescription($"changes は直接登録と同じ形（kind: assignment / person / org_update / org_create / org_abolish）とグループの加入・脱退（group_join / group_leave。target_id はグループ、values に person_id・role）。{PlanChanges.MaxChanges} 件まで。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanResponse>();

        api.MapGet("/plans/{planId}", GetAsync)
            .WithName("GetPlan")
            .WithSummary("素案を参照する")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/plans/{planId}/update", UpdateAsync)
            .WithName("UpdatePlan")
            .WithSummary("素案の中身を置き換える（作成者）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/plans/{planId}/share", ShareAsync)
            .WithName("SharePlan")
            .WithSummary("素案を共有する人を置き換える（作成者の人。100 人まで）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/plans/{planId}/delete", DeleteAsync)
            .WithName("DeletePlan")
            .WithSummary("素案を消す（作成者。申請版も消える）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/plans/{planId}/simulation", (string planId, HttpContext http, PlanService service, CancellationToken ct) => SimulateAsync(planId, null, http, service, ct))
            .WithName("SimulatePlan")
            .WithSummary("素案を基準日の組織に重ねた姿（今の姿との比較・異動・人数・警告）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanSimulationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/plans/{planId}/changes.csv", (string planId, HttpContext http, PlanService service, CancellationToken ct) => CsvAsync(planId, null, http, service, ct))
            .WithName("ExportPlanChanges")
            .WithSummary("人事へ渡す変更の一覧（CSV。UTF-8 BOM）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, true))
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        api.MapPost("/plans/{planId}/versions", FreezeAsync)
            .WithName("CreatePlanVersion")
            .WithSummary("素案を固定して次の版番号の申請版にする（作成者）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanVersionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/plans/{planId}/versions", ListVersionsAsync)
            .WithName("ListPlanVersions")
            .WithSummary("申請版の一覧（版番号の順）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<IReadOnlyList<PlanVersionResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/plans/{planId}/versions/{versionNo:int}", GetVersionAsync)
            .WithName("GetPlanVersion")
            .WithSummary("申請版を参照する")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanVersionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/plans/{planId}/versions/{versionNo:int}/simulation", (string planId, int versionNo, HttpContext http, PlanService service, CancellationToken ct) =>
                SimulateAsync(planId, versionNo, http, service, ct))
            .WithName("SimulatePlanVersion")
            .WithSummary("申請版を基準日の組織に重ねた姿（提案資料の元）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanSimulationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/plans/{planId}/versions/{versionNo:int}/changes.csv", (string planId, int versionNo, HttpContext http, PlanService service, CancellationToken ct) =>
                CsvAsync(planId, versionNo, http, service, ct))
            .WithName("ExportPlanVersionChanges")
            .WithSummary("申請版の変更の一覧（CSV。UTF-8 BOM）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, true))
            .Produces(StatusCodes.Status200OK, contentType: "text/csv");

        api.MapPost("/plans/{planId}/versions/{versionNo:int}/approval", ApprovalAsync)
            .WithName("RecordPlanVersionApproval")
            .WithSummary("申請版の承認の結果を記録する（1 回だけ）")
            .WithDescription("記録できるのは、申請版の組織すべての範囲を持つ人か、設定で指定した社内システム（ワークフロー製品。client credentials・jinji.propose）。社内システムには申請版の中身を返さない。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanApprovalResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/plans/{planId}/register", RegisterAsync)
            .WithName("RegisterPlan")
            .WithSummary("素案（か承認済みの申請版）の変更を、操作した人の範囲で直接登録にする")
            .WithDescription("version_no を指定したら、その承認済みの申請版から。indexes で変更を選べる（省略はすべて）。範囲の外は 403。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<PlanRegistrationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ListAsync(HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        Results.Ok((await service.ListAsync(http.Viewer(), cancellationToken)).Select(PlanResponse.From).ToList());

    private static Task<IResult> CreateAsync(PlanRequest body, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        TryInput(body, out var input, out var error)
            ? Handle(http, async () => PlanResponse.From(await service.CreateAsync(http.Viewer(), input!, cancellationToken)))
            : Task.FromResult(Problems.BadRequest(http, error!));

    private static Task<IResult> GetAsync(string planId, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        WithId(http, planId, async id => PlanResponse.From(await service.GetAsync(http.Viewer(), id, cancellationToken)));

    private static Task<IResult> UpdateAsync(string planId, PlanRequest body, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        TryInput(body, out var input, out var error)
            ? WithId(http, planId, async id => PlanResponse.From(await service.UpdateAsync(http.Viewer(), id, input!, cancellationToken)))
            : Task.FromResult(Problems.BadRequest(http, error!));

    private static Task<IResult> ShareAsync(string planId, SharePlanRequest body, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        body?.PersonIds is { } people
            ? WithId(http, planId, async id => PlanResponse.From(await service.ShareAsync(http.Viewer(), id, people, cancellationToken)))
            : Task.FromResult(Problems.BadRequest(http, "person_ids を指定してください。"));

    private static async Task<IResult> DeleteAsync(string planId, HttpContext http, PlanService service, CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(planId, out var id))
        {
            return Problems.NotFound(http);
        }

        var result = await Handle(http, async () =>
        {
            await service.DeleteAsync(http.Viewer(), id, cancellationToken);
            return true;
        });
        return result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK } ? Results.NoContent() : result;
    }

    private static Task<IResult> SimulateAsync(string planId, int? versionNo, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        WithId(http, planId, async id => PlanSimulationResponse.From(await service.SimulateAsync(http.Viewer(), id, versionNo, cancellationToken)));

    private static async Task<IResult> CsvAsync(string planId, int? versionNo, HttpContext http, PlanService service, CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(planId, out var id))
        {
            return Problems.NotFound(http);
        }

        try
        {
            var bytes = await service.ExportCsvAsync(http.Viewer(), id, versionNo, cancellationToken);
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(bytes, "text/csv; charset=utf-8", $"jinji-plan-{id:N}{(versionNo is { } no ? $"-v{no}" : string.Empty)}.csv");
        }
        catch (PlanException ex)
        {
            return Problem(http, ex);
        }
    }

    private static Task<IResult> FreezeAsync(string planId, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        WithId(http, planId, async id => PlanVersionResponse.From(await service.FreezeAsync(http.Viewer(), id, cancellationToken)));

    private static Task<IResult> ListVersionsAsync(string planId, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        WithId(http, planId, async id => (await service.ListVersionsAsync(http.Viewer(), id, cancellationToken)).Select(PlanVersionResponse.From).ToList());

    private static Task<IResult> GetVersionAsync(string planId, int versionNo, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        WithId(http, planId, async id => PlanVersionResponse.From(await service.GetVersionAsync(http.Viewer(), id, versionNo, cancellationToken)));

    private static Task<IResult> ApprovalAsync(string planId, int versionNo, PlanApprovalRequest body, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        body?.ApprovedOn is { } approvedOn
            ? WithId(http, planId, async id =>
            {
                var version = await service.RecordApprovalAsync(http.Viewer(), id, versionNo, approvedOn, body.Reference, cancellationToken);
                return new PlanApprovalResponse(version.Id, version.VersionNo, version.ApprovedOn!.Value, version.ApprovalReference, version.ApprovalRecordedAt!.Value);
            })
            : Task.FromResult(Problems.BadRequest(http, "approved_on を指定してください。"));

    private static Task<IResult> RegisterAsync(string planId, RegisterPlanRequest? body, HttpContext http, PlanService service, CancellationToken cancellationToken) =>
        WithId(http, planId, async id =>
        {
            var result = await service.RegisterAsync(http.Viewer(), id, body?.VersionNo, body?.Indexes, cancellationToken);
            return new PlanRegistrationResponse(
                result.Registration is { } r ? FieldRegistrationResponse.From(r, withChanges: true) : null,
                result.GroupChanges);
        });

    private static bool TryInput(PlanRequest? body, out PlanInput? input, out string? error)
    {
        input = null;
        error = null;
        if (body?.Title is not { } title || body.BaseDate is not { } baseDate || body.EffectiveDate is not { } effective)
        {
            error = "title・base_date・effective_date を指定してください。";
            return false;
        }

        var changes = new List<PlanChange>();
        foreach (var change in body.Changes ?? [])
        {
            if (change is null || !PlanChanges.TryParse(change.Kind, out var kind))
            {
                error = "changes の kind は assignment / person / org_update / org_create / org_abolish / group_join / group_leave で指定してください。";
                return false;
            }

            changes.Add(new PlanChange(kind, change.TargetId, change.Values ?? new Dictionary<string, string?>()));
        }

        input = new PlanInput(title, body.Note, baseDate, effective, changes);
        return true;
    }

    private static Task<IResult> WithId<T>(HttpContext http, string planId, Func<Guid, Task<T>> action) =>
        ApiRequest.TryParseId(planId, out var id) ? Handle(http, () => action(id)) : Task.FromResult(Problems.NotFound(http));

    private static async Task<IResult> Handle<T>(HttpContext http, Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (PlanException ex)
        {
            return Problem(http, ex);
        }
    }

    private static IResult Problem(HttpContext http, PlanException ex) => ex.Error switch
    {
        PlanError.NotFound => Problems.NotFound(http),
        PlanError.Forbidden => Problems.Forbidden(http, ex.Message),
        PlanError.Conflict => Problems.Conflict(http, ex.Message),
        _ => Problems.BadRequest(http, ex.Message),
    };
}

/// <summary>素案の変更（要求・応答）。</summary>
public sealed record PlanChangeDto
{
    [OpenApiEnum(typeof(PlanChangeKind))]
    public string? Kind { get; init; }

    public Guid? TargetId { get; init; }

    public IReadOnlyDictionary<string, string?>? Values { get; init; }

    internal static PlanChangeDto From(PlanChange change) => new() { Kind = PlanChanges.Code(change.Kind), TargetId = change.TargetId, Values = change.Values };
}

/// <summary>素案の作成・置き換えの要求。</summary>
public sealed record PlanRequest
{
    public string? Title { get; init; }

    public string? Note { get; init; }

    /// <summary>基準日（この日の公開側の上に変更を重ねて見る）。</summary>
    public DateOnly? BaseDate { get; init; }

    /// <summary>予定の有効日（基準日以降）。</summary>
    public DateOnly? EffectiveDate { get; init; }

    public IReadOnlyList<PlanChangeDto>? Changes { get; init; }
}

/// <summary>素案の共有の要求。</summary>
public sealed record SharePlanRequest
{
    public IReadOnlyList<Guid>? PersonIds { get; init; }
}

/// <summary>素案。</summary>
public sealed record PlanResponse
{
    public required Guid Id { get; init; }

    public required string Title { get; init; }

    public string? Note { get; init; }

    public required DateOnly BaseDate { get; init; }

    public required DateOnly EffectiveDate { get; init; }

    public required IReadOnlyList<PlanChangeDto> Changes { get; init; }

    public required Guid Owner { get; init; }

    public required IReadOnlyList<Guid> SharedWith { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    internal static PlanResponse From(Plan plan) => new()
    {
        Id = plan.Id,
        Title = plan.Title,
        Note = plan.Note,
        BaseDate = plan.BaseDate,
        EffectiveDate = plan.EffectiveDate,
        Changes = [.. plan.Changes.Select(PlanChangeDto.From)],
        Owner = plan.Owner,
        SharedWith = plan.SharedWith,
        CreatedAt = plan.CreatedAt,
        UpdatedAt = plan.UpdatedAt,
    };
}

/// <summary>申請版。</summary>
public sealed record PlanVersionResponse
{
    public required Guid Id { get; init; }

    public required Guid PlanId { get; init; }

    public required int VersionNo { get; init; }

    public required string Title { get; init; }

    public string? Note { get; init; }

    public required DateOnly BaseDate { get; init; }

    public required DateOnly EffectiveDate { get; init; }

    public required IReadOnlyList<PlanChangeDto> Changes { get; init; }

    public required Guid CreatedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public DateOnly? ApprovedOn { get; init; }

    /// <summary>ワークフロー側の申請番号。</summary>
    public string? ApprovalReference { get; init; }

    public Guid? ApprovalRecordedBy { get; init; }

    public string? ApprovalRecordedByClient { get; init; }

    internal static PlanVersionResponse From(PlanVersion version) => new()
    {
        Id = version.Id,
        PlanId = version.PlanId,
        VersionNo = version.VersionNo,
        Title = version.Title,
        Note = version.Note,
        BaseDate = version.BaseDate,
        EffectiveDate = version.EffectiveDate,
        Changes = [.. version.Changes.Select(PlanChangeDto.From)],
        CreatedBy = version.CreatedBy,
        CreatedAt = version.CreatedAt,
        ApprovedOn = version.ApprovedOn,
        ApprovalReference = version.ApprovalReference,
        ApprovalRecordedBy = version.ApprovalRecordedBy,
        ApprovalRecordedByClient = version.ApprovalRecordedByClient,
    };
}

/// <summary>承認の結果の記録の要求。</summary>
public sealed record PlanApprovalRequest
{
    /// <summary>承認日（今日以前）。</summary>
    public DateOnly? ApprovedOn { get; init; }

    /// <summary>ワークフロー側の申請番号（任意。100 文字まで）。</summary>
    public string? Reference { get; init; }
}

/// <summary>承認の結果の記録（中身は返さない）。</summary>
public sealed record PlanApprovalResponse(Guid Id, int VersionNo, DateOnly ApprovedOn, string? Reference, DateTimeOffset RecordedAt);

/// <summary>素案からの登録の要求。</summary>
public sealed record RegisterPlanRequest
{
    /// <summary>承認済みの申請版から登録するなら版番号。</summary>
    public int? VersionNo { get; init; }

    /// <summary>登録する変更の番号（0 始まり。省略はすべて）。</summary>
    public IReadOnlyList<int>? Indexes { get; init; }
}

/// <summary>素案からの登録の結果。</summary>
public sealed record PlanRegistrationResponse(FieldRegistrationResponse? Registration, int GroupChanges);

/// <summary>素案を適用した姿。</summary>
public sealed record PlanSimulationResponse
{
    public required DateOnly BaseDate { get; init; }

    public required IReadOnlyList<PlanUnitResponse> Units { get; init; }

    public required IReadOnlyList<PlanMoveResponse> Moves { get; init; }

    public required IReadOnlyList<PlanFieldChangeResponse> PersonChanges { get; init; }

    public required IReadOnlyList<PlanFieldChangeResponse> OrgChanges { get; init; }

    public required IReadOnlyList<PlanGroupChangeResponse> GroupChanges { get; init; }

    public required IReadOnlyList<PlanWarningResponse> Warnings { get; init; }

    internal static PlanSimulationResponse From(PlanSimulation s) => new()
    {
        BaseDate = s.BaseDate,
        Units = [.. s.Units.Select(u => new PlanUnitResponse(u.Id, u.Code, u.Name, u.ShortName, u.LevelKey, u.ParentId, u.HeadPersonId, u.SiteId, Codes.Of(u.State), u.Before, u.After))],
        Moves = [.. s.Moves.Select(m => new PlanMoveResponse(m.Index, m.PersonId, m.FromUnitId, m.ToUnitId, m.FromTitle, m.ToTitle))],
        PersonChanges = [.. s.PersonChanges.Select(PlanFieldChangeResponse.From)],
        OrgChanges = [.. s.OrgChanges.Select(PlanFieldChangeResponse.From)],
        GroupChanges = [.. s.GroupChanges.Select(g => new PlanGroupChangeResponse(g.Index, g.GroupId, g.PersonId, g.Join, g.Role is { } r ? Domain.Organization.GroupCodes.Of(r) : null))],
        Warnings = [.. s.Warnings.Select(w => new PlanWarningResponse(w.Index, w.Message))],
    };
}

/// <summary>素案を適用した組織（before は今の主務の所属者の数、after は適用後）。</summary>
public sealed record PlanUnitResponse(
    Guid Id,
    string Code,
    string Name,
    string? ShortName,
    string? LevelKey,
    Guid? ParentId,
    Guid? HeadPersonId,
    Guid? SiteId,
    [property: OpenApiEnum(typeof(PlanUnitState))] string State,
    int Before,
    int After);

/// <summary>異動。</summary>
public sealed record PlanMoveResponse(int Index, Guid PersonId, Guid? FromUnitId, Guid ToUnitId, string? FromTitle, string? ToTitle);

/// <summary>項目の変更。</summary>
public sealed record PlanFieldChangeResponse(int Index, Guid TargetId, string Field, string? Old, string? New)
{
    internal static PlanFieldChangeResponse From(PlanFieldChange c) => new(c.Index, c.TargetId, c.Field, c.Old, c.New);
}

/// <summary>グループの加入・脱退。</summary>
public sealed record PlanGroupChangeResponse(
    int Index,
    Guid GroupId,
    Guid PersonId,
    bool Join,
    [property: OpenApiEnum(typeof(Domain.Organization.GroupRole))] string? Role);

/// <summary>適用できなかった変更（index は変更の番号。-1 は素案全体）。</summary>
public sealed record PlanWarningResponse(int Index, string Message);
