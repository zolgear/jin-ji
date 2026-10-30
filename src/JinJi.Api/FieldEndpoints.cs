using JinJi.Application;
using JinJi.Application.Access;
using JinJi.Application.FieldManagement;
using JinJi.Application.Ingest;
using JinJi.Domain;
using JinJi.Domain.Organization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>
/// 現場・IT 部門の直接登録（<c>/api/v1/field</c>。ADR-0013）: 領域の管理元・担当の指名・閲覧者の範囲・直接登録（登録・確定・同意・取り消し・破棄）。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>直接登録は承認を経ずに公開側へ反映する。範囲（組織長・指名した担当・組織管理者・人事管理者）と管理元はアプリと DB の両方で検査する</item>
/// <item>AI エージェントの登録は委任元の名義の下書きになる（確定は委任元の人だけ）。社内システムは使えない</item>
/// <item>管理元の起案・承認と担当の指名は人だけ。状態を変える要求（POST）は jin-ji が発行したトークンでは jinji.propose が要る</item>
/// </list>
/// </remarks>
internal static class FieldEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/field/modes", ModesAsync)
            .WithName("GetFieldModes")
            .WithSummary("領域ごとの管理元（hr / field / both）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<IReadOnlyList<FieldModeResponse>>();

        api.MapGet("/field/settings", SettingsAsync)
            .WithName("ListFieldSettings")
            .WithSummary("管理元の起案の一覧（新しい順）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<IReadOnlyList<FieldSettingResponse>>();

        api.MapPost("/field/settings", ProposeAsync)
            .WithName("ProposeFieldSetting")
            .WithSummary("管理元を起案する（人事管理者・組織管理者の人）")
            .WithDescription("同じ領域の起案中があれば 409。起案者とは別の人が承認すると有効になる。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<FieldSettingResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/field/settings/{settingId}/approve", (string settingId, HttpContext http, FieldManagementService service, CancellationToken ct) =>
                DecideAsync(settingId, true, http, service, ct))
            .WithName("ApproveFieldSetting")
            .WithSummary("管理元の起案を承認する（起案者とは別の人事管理者・組織管理者の人）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<FieldSettingResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/field/settings/{settingId}/reject", (string settingId, HttpContext http, FieldManagementService service, CancellationToken ct) =>
                DecideAsync(settingId, false, http, service, ct))
            .WithName("RejectFieldSetting")
            .WithSummary("管理元の起案を却下する（起案者とは別の人事管理者・組織管理者の人）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<FieldSettingResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/field/grants", GrantsAsync)
            .WithName("ListFieldEditorGrants")
            .WithSummary("担当の指名の一覧（取り消し済みを含む。新しい順）")
            .WithDescription("org_unit_id・person_id で絞れる。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<IReadOnlyList<FieldGrantResponse>>();

        api.MapPost("/field/grants", GrantAsync)
            .WithName("GrantFieldEditor")
            .WithSummary("組織の担当を指名する（組織長としての範囲を持つ人・組織管理者・人事管理者）")
            .WithDescription($"期間は今日以降に始まり {FieldManagementService.MaxGrantDays} 日まで。自分・担当による再指名はできない。同じ組織・同じ人の期間が重なれば 409。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<FieldGrantResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/field/grants/{grantId}/revoke", RevokeAsync)
            .WithName("RevokeFieldEditor")
            .WithSummary("担当の指名を取り消す")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<FieldGrantResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapGet("/field/scope", ScopeAsync)
            .WithName("GetFieldScope")
            .WithSummary("閲覧者の直接登録の範囲（今日。組織の ID の一覧）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<FieldScopeResponse>();

        api.MapGet("/field/registrations", ListAsync)
            .WithName("ListFieldRegistrations")
            .WithSummary("直接登録の一覧（自分の登録・同意した登録・自分の範囲の組織の登録。新しい順）")
            .WithDescription("mine=true で自分の登録だけ。status（カンマ区切り）で絞れる。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<IReadOnlyList<FieldRegistrationResponse>>();

        api.MapGet("/field/registrations/{registrationId}", GetAsync)
            .WithName("GetFieldRegistration")
            .WithSummary("直接登録を参照する（変更を含む）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<FieldRegistrationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/field/registrations", RegisterAsync)
            .WithName("CreateFieldRegistration")
            .WithSummary("直接登録する（範囲の中ならすぐに反映。AI エージェントは下書き）")
            .WithDescription(
                "changes の kind: assignment（異動・役職）/ person（拠点・リモートワーク・内線・社用携帯）/ org_update（名称・略称・段・上位組織・組織長・拠点）/ " +
                "org_create（新設）/ org_abolish（廃止）。values のキーは項目名（org_unit_id など）。有効日は今日以降（遡及は組織管理者・人事管理者）。" +
                $"相手側の組織の範囲が要れば pending_consent。変える人は {FieldRegistrationService.MaxPeople} 人まで。範囲の外は 403、値の誤りは 400、作成後に対象が変わっていれば 409。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<FieldRegistrationResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        MapTransition(api, "confirm", "ConfirmFieldRegistration", "AI エージェントの下書きを確定する（下書きの名義の人）", (s, v, id, ct) => s.ConfirmAsync(v, id, ct));
        MapTransition(api, "consent", "ConsentFieldRegistration", "同意待ちの登録に同意して反映する（相手側の組織の範囲を持つ、登録者とは別の人）", (s, v, id, ct) => s.ConsentAsync(v, id, ct));
        MapTransition(api, "withdraw", "WithdrawFieldRegistration", "反映済みの登録を有効日の前に取り消す（登録者か、登録の組織すべての範囲を持つ人）", (s, v, id, ct) => s.WithdrawAsync(v, id, ct));
        MapTransition(api, "discard", "DiscardFieldRegistration", "下書き・同意待ちの登録を破棄する（登録者か、組織管理者・人事管理者）", (s, v, id, ct) => s.DiscardAsync(v, id, ct));
    }

    private static void MapTransition(
        RouteGroupBuilder api,
        string action,
        string name,
        string summary,
        Func<FieldRegistrationService, Policy.ViewerContext, Guid, CancellationToken, Task<FieldRegistration>> transition)
    {
        api.MapPost($"/field/registrations/{{registrationId}}/{action}", async (string registrationId, HttpContext http, FieldRegistrationService service, CancellationToken ct) =>
            {
                if (!ApiRequest.TryParseId(registrationId, out var id))
                {
                    return Problems.NotFound(http);
                }

                return await Handle(http, async () => FieldRegistrationResponse.From(await transition(service, http.Viewer(), id, ct), withChanges: false));
            })
            .WithName(name)
            .WithSummary(summary)
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<FieldRegistrationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> ModesAsync(HttpContext http, FieldManagementService service, CancellationToken cancellationToken)
    {
        var modes = await service.ModesAsync(http.Viewer(), cancellationToken);
        return Results.Ok(modes.Select(m => new FieldModeResponse(FieldDomains.Key(m.Key), FieldDomains.Key(m.Value))).ToList());
    }

    private static async Task<IResult> SettingsAsync(HttpContext http, FieldManagementService service, CancellationToken cancellationToken) =>
        Results.Ok((await service.ListSettingsAsync(http.Viewer(), cancellationToken)).Select(FieldSettingResponse.From).ToList());

    private static async Task<IResult> ProposeAsync(ProposeFieldSettingRequest body, HttpContext http, FieldManagementService service, CancellationToken cancellationToken)
    {
        if (!FieldDomains.TryParseDomain(body?.Domain, out var domain) || !FieldDomains.TryParseMode(body?.Mode, out var mode))
        {
            return Problems.BadRequest(http, "domain（org_structure / org_site / assignment / person_site / person_contact）と mode（hr / field / both）を指定してください。");
        }

        return await Handle(http, async () => FieldSettingResponse.From(await service.ProposeModeAsync(http.Viewer(), domain, mode, body!.Note, cancellationToken)));
    }

    private static async Task<IResult> DecideAsync(string settingId, bool approve, HttpContext http, FieldManagementService service, CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(settingId, out var id))
        {
            return Problems.NotFound(http);
        }

        return await Handle(http, async () => FieldSettingResponse.From(await service.DecideModeAsync(http.Viewer(), id, approve, cancellationToken)));
    }

    private static async Task<IResult> GrantsAsync(
        HttpContext http,
        FieldManagementService service,
        [FromQuery(Name = "org_unit_id")] string? orgUnitId,
        [FromQuery(Name = "person_id")] string? personId,
        CancellationToken cancellationToken)
    {
        if (!TryParseOptionalId(orgUnitId, out var org) || !TryParseOptionalId(personId, out var person))
        {
            return Problems.BadRequest(http, "org_unit_id・person_id は UUID で指定してください。");
        }

        return Results.Ok((await service.ListGrantsAsync(http.Viewer(), org, person, cancellationToken)).Select(FieldGrantResponse.Of).ToList());
    }

    private static async Task<IResult> GrantAsync(GrantFieldEditorRequest body, HttpContext http, FieldManagementService service, CancellationToken cancellationToken)
    {
        if (body?.OrgUnitId is not { } org || body.PersonId is not { } person || body.From is not { } from || body.Until is not { } until)
        {
            return Problems.BadRequest(http, "org_unit_id・person_id・from・until を指定してください。");
        }

        return await Handle(http, async () => FieldGrantResponse.Of(await service.GrantEditorAsync(http.Viewer(), org, person, from, until, cancellationToken)));
    }

    private static async Task<IResult> RevokeAsync(string grantId, HttpContext http, FieldManagementService service, CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(grantId, out var id))
        {
            return Problems.NotFound(http);
        }

        return await Handle(http, async () => FieldGrantResponse.Of(await service.RevokeEditorAsync(http.Viewer(), id, cancellationToken)));
    }

    private static async Task<IResult> ScopeAsync(
        HttpContext http,
        FieldManagementService service,
        IOrgSnapshotProvider snapshots,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var viewer = http.Viewer();
        var today = BusinessDate.Today(timeProvider);
        var scope = await service.ScopeAsync(viewer, today, cancellationToken);
        var snapshot = await snapshots.LoadAsync(viewer, today, today, cancellationToken);
        var units = scope.ManagesAll ? [] : snapshot.Units.Where(u => scope.Covers(u.Id)).Select(u => u.Id).Order().ToList();
        var parents = scope.ManagesAll ? [] : snapshot.Units.Where(u => scope.CoversParentOf(u.Id)).Select(u => u.Id).Order().ToList();
        var grants = scope.ManagesAll ? [] : snapshot.Units.Where(u => scope.CanGrantEditors(u.Id)).Select(u => u.Id).Order().ToList();
        return Results.Ok(new FieldScopeResponse(scope.ManagesAll, units, parents, grants));
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        FieldRegistrationService service,
        [FromQuery(Name = "mine")] string? mine,
        [FromQuery(Name = "status")] string? status,
        CancellationToken cancellationToken)
    {
        var statuses = new List<FieldRegistrationStatus>();
        foreach (var code in (status ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!FieldRegistrationService.TryParseStatus(code, out var parsed))
            {
                return Problems.BadRequest(http, "status は draft / pending_consent / applied / withdrawn / discarded / expired のカンマ区切りで指定してください。");
            }

            statuses.Add(parsed);
        }

        if (mine is not (null or "" or "true" or "false"))
        {
            return Problems.BadRequest(http, "mine は true / false で指定してください。");
        }

        var registrations = await service.ListAsync(http.Viewer(), new FieldRegistrationQuery(mine == "true", statuses), cancellationToken);
        return Results.Ok(registrations.Select(r => FieldRegistrationResponse.From(r, withChanges: false)).ToList());
    }

    private static async Task<IResult> GetAsync(string registrationId, HttpContext http, FieldRegistrationService service, CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(registrationId, out var id))
        {
            return Problems.NotFound(http);
        }

        return await Handle(http, async () => FieldRegistrationResponse.From(await service.GetAsync(http.Viewer(), id, cancellationToken), withChanges: true));
    }

    private static async Task<IResult> RegisterAsync(CreateFieldRegistrationRequest body, HttpContext http, FieldRegistrationService service, CancellationToken cancellationToken)
    {
        if (body?.EffectiveDate is not { } date || body.Changes is not { Count: > 0 } changes)
        {
            return Problems.BadRequest(http, "effective_date と changes（1 件以上）を指定してください。");
        }

        var inputs = new List<FieldChangeInput>(changes.Count);
        foreach (var change in changes)
        {
            if (change is null || !TryParseKind(change.Kind, out var kind))
            {
                return Problems.BadRequest(http, "changes の kind は assignment / person / org_update / org_create / org_abolish で指定してください。");
            }

            inputs.Add(new FieldChangeInput(kind, change.TargetId, change.Values ?? new Dictionary<string, string?>()));
        }

        return await Handle(http, async () =>
            FieldRegistrationResponse.From(await service.RegisterAsync(http.Viewer(), new FieldRegistrationInput(date, body.Note, inputs), cancellationToken), withChanges: true));
    }

    /// <summary>操作の誤りを応答に変える（範囲の外は 403、見えない・無いは 404、状態の衝突は 409、値の誤りは 400）。</summary>
    private static async Task<IResult> Handle(HttpContext http, Func<Task<object>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (FieldManagementException ex)
        {
            return ex.Error switch
            {
                FieldManagementError.NotFound => Problems.NotFound(http),
                FieldManagementError.Forbidden => Problems.Forbidden(http, ex.Message),
                FieldManagementError.Conflict => Problems.Conflict(http, ex.Message),
                _ => Problems.BadRequest(http, ex.Message),
            };
        }
    }

    private static bool TryParseKind(string? code, out FieldChangeKind kind)
    {
        foreach (var value in Enum.GetValues<FieldChangeKind>())
        {
            if (Codes.Of(value) == code)
            {
                kind = value;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private static bool TryParseOptionalId(string? value, out Guid? id)
    {
        id = null;
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (!ApiRequest.TryParseId(value, out var parsed))
        {
            return false;
        }

        id = parsed;
        return true;
    }
}

/// <summary>領域の管理元。</summary>
public sealed record FieldModeResponse(
    [property: OpenApiEnum(typeof(FieldDomain))] string Domain,
    [property: OpenApiEnum(typeof(FieldManagementMode))] string Mode);

/// <summary>管理元の起案の要求。</summary>
public sealed record ProposeFieldSettingRequest
{
    [OpenApiEnum(typeof(FieldDomain))]
    public string? Domain { get; init; }

    [OpenApiEnum(typeof(FieldManagementMode))]
    public string? Mode { get; init; }

    /// <summary>起案の理由のメモ（任意。500 文字まで）。</summary>
    public string? Note { get; init; }
}

/// <summary>管理元の起案。</summary>
public sealed record FieldSettingResponse
{
    public required Guid Id { get; init; }

    [OpenApiEnum(typeof(FieldDomain))]
    public required string Domain { get; init; }

    [OpenApiEnum(typeof(FieldManagementMode))]
    public required string Mode { get; init; }

    [OpenApiEnum(typeof(FieldDomainSettingStatus))]
    public required string Status { get; init; }

    public string? Note { get; init; }

    public required Guid ProposedBy { get; init; }

    public required DateTimeOffset ProposedAt { get; init; }

    public Guid? DecidedBy { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    internal static FieldSettingResponse From(FieldDomainSetting setting) => new()
    {
        Id = setting.Id,
        Domain = FieldDomains.Key(setting.Domain),
        Mode = FieldDomains.Key(setting.Mode),
        Status = Codes.Of(setting.Status),
        Note = setting.Note,
        ProposedBy = setting.ProposedBy,
        ProposedAt = setting.ProposedAt,
        DecidedBy = setting.DecidedBy,
        DecidedAt = setting.DecidedAt,
    };
}

/// <summary>担当の指名の要求。</summary>
public sealed record GrantFieldEditorRequest
{
    public Guid? OrgUnitId { get; init; }

    public Guid? PersonId { get; init; }

    /// <summary>期間の始まり（今日以降）。</summary>
    public DateOnly? From { get; init; }

    /// <summary>期間の終わり（この日を含まない）。</summary>
    public DateOnly? Until { get; init; }
}

/// <summary>担当の指名。</summary>
public sealed record FieldGrantResponse
{
    public required Guid Id { get; init; }

    public required Guid OrgUnitId { get; init; }

    public required Guid PersonId { get; init; }

    public required DateOnly From { get; init; }

    /// <summary>期間の終わり（この日を含まない）。</summary>
    public required DateOnly Until { get; init; }

    public required Guid GrantedBy { get; init; }

    public required DateTimeOffset GrantedAt { get; init; }

    public Guid? RevokedBy { get; init; }

    public DateTimeOffset? RevokedAt { get; init; }

    internal static FieldGrantResponse Of(OrgEditorGrant grant) => new()
    {
        Id = grant.Id,
        OrgUnitId = grant.OrgUnitId,
        PersonId = grant.PersonId,
        From = grant.From,
        Until = grant.Until,
        GrantedBy = grant.GrantedBy,
        GrantedAt = grant.GrantedAt,
        RevokedBy = grant.RevokedBy,
        RevokedAt = grant.RevokedAt,
    };
}

/// <summary>閲覧者の直接登録の範囲（今日）。</summary>
/// <param name="ManagesAll">全社の範囲を持つ（組織管理者・人事管理者）。true なら一覧は空。</param>
/// <param name="OrgUnitIds">登録できる組織。</param>
/// <param name="ParentScopeOrgUnitIds">組織長の任免・上位組織の付け替えができる組織（一つ上の範囲を持つ）。</param>
/// <param name="GrantableOrgUnitIds">担当を指名できる組織（組織長としての範囲）。</param>
public sealed record FieldScopeResponse(
    bool ManagesAll,
    IReadOnlyList<Guid> OrgUnitIds,
    IReadOnlyList<Guid> ParentScopeOrgUnitIds,
    IReadOnlyList<Guid> GrantableOrgUnitIds);

/// <summary>直接登録の 1 つの変更（要求）。</summary>
public sealed record FieldChangeRequest
{
    [OpenApiEnum(typeof(FieldChangeKind))]
    public string? Kind { get; init; }

    /// <summary>対象の人・組織（新設では省略可。省略なら採番する）。</summary>
    public Guid? TargetId { get; init; }

    /// <summary>変える項目（キーは org_unit_id・position_title・site_id・remote_work・extension_number・work_mobile_phone・code・name・short_name・level_key・parent_org_unit_id・head_person_id）。</summary>
    public IReadOnlyDictionary<string, string?>? Values { get; init; }
}

/// <summary>直接登録の要求。</summary>
public sealed record CreateFieldRegistrationRequest
{
    /// <summary>有効日（今日以降。遡及は組織管理者・人事管理者）。</summary>
    public DateOnly? EffectiveDate { get; init; }

    /// <summary>理由のメモ（任意。500 文字まで）。</summary>
    public string? Note { get; init; }

    public IReadOnlyList<FieldChangeRequest>? Changes { get; init; }
}

/// <summary>直接登録の 1 つの変更（変更提案）。</summary>
public sealed record FieldRegistrationChangeResponse
{
    public required Guid Id { get; init; }

    [OpenApiEnum(typeof(ProposalTargetKind))]
    public required string TargetKind { get; init; }

    [OpenApiEnum(typeof(ProposalOperation))]
    public required string Operation { get; init; }

    public Guid? TargetId { get; init; }

    [OpenApiEnum(typeof(ProposalStatus))]
    public required string Status { get; init; }

    /// <summary>新設の値。</summary>
    public required IReadOnlyDictionary<string, string?> Payload { get; init; }

    /// <summary>旧値と新値。</summary>
    public required IReadOnlyDictionary<string, ProposalDiffResponse> Diff { get; init; }

    internal static FieldRegistrationChangeResponse From(ChangeProposalRecord proposal) => new()
    {
        Id = proposal.Id,
        TargetKind = Codes.Of(proposal.TargetKind),
        Operation = Codes.Of(proposal.Operation),
        TargetId = proposal.TargetId,
        Status = Codes.Of(proposal.Status),
        Payload = proposal.Payload,
        Diff = proposal.Diff.ToDictionary(d => d.Key, d => new ProposalDiffResponse(d.Value.Old, d.Value.New), StringComparer.Ordinal),
    };
}

/// <summary>項目の旧値・新値。</summary>
public sealed record ProposalDiffResponse(string? Old, string? New);

/// <summary>直接登録。</summary>
public sealed record FieldRegistrationResponse
{
    public required Guid Id { get; init; }

    [OpenApiEnum(typeof(FieldRegistrationStatus))]
    public required string Status { get; init; }

    public required DateOnly EffectiveDate { get; init; }

    public string? Note { get; init; }

    public required Guid RegisteredBy { get; init; }

    /// <summary>人（user）か、AI エージェントの下書き（agent）か。</summary>
    [OpenApiEnum("user", "agent")]
    public required string RegisteredVia { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>下書き・同意待ちの期限。</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    public required IReadOnlyList<Guid> OrgUnitIds { get; init; }

    public required IReadOnlyList<Guid> PersonIds { get; init; }

    /// <summary>相手側の同意が要る組織。</summary>
    public required IReadOnlyList<Guid> ConsentOrgUnitIds { get; init; }

    public Guid? ConsentedBy { get; init; }

    public DateTimeOffset? ConsentedAt { get; init; }

    public Guid? AppliedBy { get; init; }

    public DateTimeOffset? AppliedAt { get; init; }

    public Guid? WithdrawnBy { get; init; }

    public DateTimeOffset? WithdrawnAt { get; init; }

    /// <summary>変更（参照と登録の応答だけ。一覧では null）。</summary>
    public IReadOnlyList<FieldRegistrationChangeResponse>? Changes { get; init; }

    internal static FieldRegistrationResponse From(FieldRegistration registration, bool withChanges) => new()
    {
        Id = registration.Id,
        Status = FieldRegistrationService.Code(registration.Status),
        EffectiveDate = registration.EffectiveDate,
        Note = registration.Note,
        RegisteredBy = registration.RegisteredBy,
        RegisteredVia = registration.RegisteredVia == Policy.ActorType.Agent ? "agent" : "user",
        CreatedAt = registration.CreatedAt,
        ExpiresAt = registration.ExpiresAt,
        OrgUnitIds = registration.OrgUnitIds,
        PersonIds = registration.PersonIds,
        ConsentOrgUnitIds = registration.ConsentOrgUnitIds,
        ConsentedBy = registration.ConsentedBy,
        ConsentedAt = registration.ConsentedAt,
        AppliedBy = registration.AppliedBy,
        AppliedAt = registration.AppliedAt,
        WithdrawnBy = registration.WithdrawnBy,
        WithdrawnAt = registration.WithdrawnAt,
        Changes = withChanges ? [.. registration.Proposals.Select(FieldRegistrationChangeResponse.From)] : null,
    };
}
