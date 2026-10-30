using JinJi.Application.Access;
using JinJi.Application.Groups;
using JinJi.Domain.Organization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>
/// グループ（<c>/api/v1/groups</c>。チーム・プロジェクトなど規程外の単位。ADR-0014）: 一覧・参照・作成・変更・終了・公開範囲と管理対象・メンバー。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>参照は閲覧者に見えるグループだけ（全社公開と、メンバーだけのグループのうち自分がメンバーのもの）。メンバーは項目 groups が見える人だけ</item>
/// <item>作成は人なら誰でも（作成者がオーナー）。変更はオーナー・所管組織の範囲を持つ人・組織管理者・人事管理者。AI エージェント・社内システムは参照だけ</item>
/// </list>
/// </remarks>
internal static class GroupEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/groups", ListAsync)
            .WithName("ListGroups")
            .WithSummary("グループの一覧（今日有効な、閲覧者に見えるもの。名前の順）")
            .WithDescription("kind・person_id（その人が今日のメンバーのグループ。その人の項目 groups が見えなければ空）・owner_org_unit_id・managed=true で絞れる。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgRead, false))
            .Produces<IReadOnlyList<GroupResponse>>();

        api.MapGet("/groups/{groupId}", GetAsync)
            .WithName("GetGroup")
            .WithSummary("グループと今日のメンバーを参照する")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgRead, false))
            .Produces<GroupDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/groups", CreateAsync)
            .WithName("CreateGroup")
            .WithSummary("グループを作る（人。作成者が最初のオーナー）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<GroupDetailResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPost("/groups/{groupId}/update", UpdateAsync)
            .WithName("UpdateGroup")
            .WithSummary("今日から名称・種別・所管組織を変える")
            .WithDescription("所管組織を変える・外す（clear_owner_org_unit）のは、所管組織の範囲を持つ人・組織管理者・人事管理者だけ。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<GroupDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/groups/{groupId}/close", CloseAsync)
            .WithName("CloseGroup")
            .WithSummary("その日（既定は今日）でグループを終える")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<GroupDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/groups/{groupId}/settings", SettingsAsync)
            .WithName("SetGroupSettings")
            .WithSummary("公開範囲（all / members）・管理対象を変える")
            .WithDescription("管理対象（関係スコープ same_group に使える）にする・外すのは組織管理者・人事管理者だけ。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<GroupDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/groups/{groupId}/members", AddMemberAsync)
            .WithName("AddGroupMember")
            .WithSummary("メンバーを加える")
            .WithDescription("期間は今日以降に始まる。管理対象のグループのオーナーの指名は、所管組織の範囲を持つ人・組織管理者・人事管理者だけ。期間が重なれば 409。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<GroupDetailResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/groups/{groupId}/members/{personId}/role", ChangeRoleAsync)
            .WithName("ChangeGroupMemberRole")
            .WithSummary("今日からメンバーの役割を変える")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<GroupDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        api.MapPost("/groups/{groupId}/members/{personId}/remove", RemoveMemberAsync)
            .WithName("RemoveGroupMember")
            .WithSummary("今日でメンバーから外す（本人は自分を外せる。管理対象のグループを除く）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Field, false))
            .Produces<GroupDetailResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        GroupService service,
        [FromQuery(Name = "kind")] string? kind,
        [FromQuery(Name = "person_id")] string? personId,
        [FromQuery(Name = "owner_org_unit_id")] string? ownerOrgUnitId,
        [FromQuery(Name = "managed")] string? managed,
        CancellationToken cancellationToken)
    {
        GroupKind? parsedKind = null;
        if (!string.IsNullOrEmpty(kind))
        {
            if (!GroupCodes.TryParseKind(kind, out var k))
            {
                return Problems.BadRequest(http, "kind は team / project / committee / other で指定してください。");
            }

            parsedKind = k;
        }

        if (!TryOptionalId(personId, out var person) || !TryOptionalId(ownerOrgUnitId, out var owner) || managed is not (null or "" or "true" or "false"))
        {
            return Problems.BadRequest(http, "person_id・owner_org_unit_id は UUID、managed は true / false で指定してください。");
        }

        var groups = await service.ListAsync(http.Viewer(), new GroupQuery(parsedKind, person, owner, managed == "true"), cancellationToken);
        return Results.Ok(groups.Select(GroupResponse.From).ToList());
    }

    private static Task<IResult> GetAsync(string groupId, HttpContext http, GroupService service, CancellationToken cancellationToken) =>
        WithId(http, groupId, id => service.GetAsync(http.Viewer(), id, cancellationToken));

    private static async Task<IResult> CreateAsync(CreateGroupRequest body, HttpContext http, GroupService service, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(body?.Name) || !GroupCodes.TryParseKind(body.Kind, out var kind))
        {
            return Problems.BadRequest(http, "name と kind（team / project / committee / other）を指定してください。");
        }

        if (!GroupCodes.TryParseVisibility(body.Visibility ?? "all", out var visibility))
        {
            return Problems.BadRequest(http, "visibility は all / members で指定してください。");
        }

        return await Handle(http, () => service.CreateAsync(http.Viewer(), new GroupCreateInput(body.Name, kind, body.OwnerOrgUnitId, visibility, body.ValidFrom), cancellationToken));
    }

    private static Task<IResult> UpdateAsync(string groupId, UpdateGroupRequest body, HttpContext http, GroupService service, CancellationToken cancellationToken)
    {
        GroupKind? kind = null;
        if (body?.Kind is { } code)
        {
            if (!GroupCodes.TryParseKind(code, out var parsed))
            {
                return Task.FromResult(Problems.BadRequest(http, "kind は team / project / committee / other で指定してください。"));
            }

            kind = parsed;
        }

        return WithId(http, groupId, id => service.UpdateAsync(
            http.Viewer(), id, new GroupUpdateInput(body?.Name, kind, body?.OwnerOrgUnitId, body?.ClearOwnerOrgUnit ?? false), cancellationToken));
    }

    private static Task<IResult> CloseAsync(string groupId, CloseGroupRequest? body, HttpContext http, GroupService service, CancellationToken cancellationToken) =>
        WithId(http, groupId, id => service.CloseAsync(http.Viewer(), id, body?.Date, cancellationToken));

    private static Task<IResult> SettingsAsync(string groupId, GroupSettingsRequest body, HttpContext http, GroupService service, CancellationToken cancellationToken)
    {
        GroupVisibility? visibility = null;
        if (body?.Visibility is { } code)
        {
            if (!GroupCodes.TryParseVisibility(code, out var parsed))
            {
                return Task.FromResult(Problems.BadRequest(http, "visibility は all / members で指定してください。"));
            }

            visibility = parsed;
        }

        return WithId(http, groupId, id => service.SetSettingsAsync(http.Viewer(), id, visibility, body?.Managed, cancellationToken));
    }

    private static Task<IResult> AddMemberAsync(string groupId, AddGroupMemberRequest body, HttpContext http, GroupService service, CancellationToken cancellationToken)
    {
        if (body?.PersonId is not { } person || !GroupCodes.TryParseRole(body.Role ?? "member", out var role))
        {
            return Task.FromResult(Problems.BadRequest(http, "person_id と role（owner / lead / member）を指定してください。"));
        }

        return WithId(http, groupId, id => service.AddMemberAsync(http.Viewer(), id, person, role, body.From, body.Until, cancellationToken));
    }

    private static Task<IResult> ChangeRoleAsync(string groupId, string personId, ChangeGroupRoleRequest body, HttpContext http, GroupService service, CancellationToken cancellationToken)
    {
        if (!ApiRequest.TryParseId(personId, out var person))
        {
            return Task.FromResult(Problems.NotFound(http));
        }

        if (!GroupCodes.TryParseRole(body?.Role, out var role))
        {
            return Task.FromResult(Problems.BadRequest(http, "role は owner / lead / member で指定してください。"));
        }

        return WithId(http, groupId, id => service.ChangeRoleAsync(http.Viewer(), id, person, role, cancellationToken));
    }

    private static Task<IResult> RemoveMemberAsync(string groupId, string personId, HttpContext http, GroupService service, CancellationToken cancellationToken) =>
        ApiRequest.TryParseId(personId, out var person)
            ? WithId(http, groupId, id => service.RemoveMemberAsync(http.Viewer(), id, person, cancellationToken))
            : Task.FromResult(Problems.NotFound(http));

    private static Task<IResult> WithId(HttpContext http, string groupId, Func<Guid, Task<GroupDetail>> action) =>
        ApiRequest.TryParseId(groupId, out var id) ? Handle(http, () => action(id)) : Task.FromResult(Problems.NotFound(http));

    private static async Task<IResult> Handle(HttpContext http, Func<Task<GroupDetail>> action)
    {
        try
        {
            return Results.Ok(GroupDetailResponse.From(await action()));
        }
        catch (GroupException ex)
        {
            return ex.Error switch
            {
                GroupError.NotFound => Problems.NotFound(http),
                GroupError.Forbidden => Problems.Forbidden(http, ex.Message),
                GroupError.Conflict => Problems.Conflict(http, ex.Message),
                _ => Problems.BadRequest(http, ex.Message),
            };
        }
    }

    private static bool TryOptionalId(string? value, out Guid? id)
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

/// <summary>グループ。</summary>
public sealed record GroupResponse
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    [OpenApiEnum(typeof(GroupKind))]
    public required string Kind { get; init; }

    /// <summary>所管組織。</summary>
    public Guid? OwnerOrgUnitId { get; init; }

    [OpenApiEnum(typeof(GroupVisibility))]
    public required string Visibility { get; init; }

    /// <summary>アクセス権の管理の対象か（関係スコープ same_group に使える）。</summary>
    public required bool Managed { get; init; }

    public required DateOnly ValidFrom { get; init; }

    /// <summary>終了日（この日を含まない）。</summary>
    public DateOnly? ValidUntil { get; init; }

    /// <summary>今日のメンバーの数（見えない人も数える）。</summary>
    public required int MemberCount { get; init; }

    internal static GroupResponse From(GroupRecord group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        Kind = GroupCodes.Of(group.Kind),
        OwnerOrgUnitId = group.OwnerOrgUnitId,
        Visibility = GroupCodes.Of(group.Visibility),
        Managed = group.Managed,
        ValidFrom = group.ValidFrom,
        ValidUntil = group.ValidUntil,
        MemberCount = group.MemberCount,
    };
}

/// <summary>グループのメンバー。</summary>
public sealed record GroupMemberResponse(
    Guid PersonId,
    [property: OpenApiEnum(typeof(GroupRole))] string Role,
    DateOnly From,
    DateOnly? Until);

/// <summary>グループと今日のメンバー（項目 groups が見える人だけ）。</summary>
public sealed record GroupDetailResponse
{
    public required GroupResponse Group { get; init; }

    public required IReadOnlyList<GroupMemberResponse> Members { get; init; }

    internal static GroupDetailResponse From(GroupDetail detail) => new()
    {
        Group = GroupResponse.From(detail.Group),
        Members = [.. detail.Members.Select(m => new GroupMemberResponse(m.PersonId, GroupCodes.Of(m.Role), m.From, m.Until))],
    };
}

/// <summary>グループの作成の要求。</summary>
public sealed record CreateGroupRequest
{
    public string? Name { get; init; }

    [OpenApiEnum(typeof(GroupKind))]
    public string? Kind { get; init; }

    public Guid? OwnerOrgUnitId { get; init; }

    /// <summary>公開範囲（既定 all）。</summary>
    [OpenApiEnum(typeof(GroupVisibility))]
    public string? Visibility { get; init; }

    /// <summary>開始日（既定は今日。今日以降）。</summary>
    public DateOnly? ValidFrom { get; init; }
}

/// <summary>グループの属性の変更の要求（省略した項目は変えない）。</summary>
public sealed record UpdateGroupRequest
{
    public string? Name { get; init; }

    [OpenApiEnum(typeof(GroupKind))]
    public string? Kind { get; init; }

    public Guid? OwnerOrgUnitId { get; init; }

    /// <summary>所管組織を外す。</summary>
    public bool? ClearOwnerOrgUnit { get; init; }
}

/// <summary>グループの終了の要求。</summary>
public sealed record CloseGroupRequest
{
    /// <summary>終了日（この日からグループが無くなる。既定は今日）。</summary>
    public DateOnly? Date { get; init; }
}

/// <summary>公開範囲・管理対象の変更の要求（省略した項目は変えない）。</summary>
public sealed record GroupSettingsRequest
{
    [OpenApiEnum(typeof(GroupVisibility))]
    public string? Visibility { get; init; }

    public bool? Managed { get; init; }
}

/// <summary>メンバーの追加の要求。</summary>
public sealed record AddGroupMemberRequest
{
    public Guid? PersonId { get; init; }

    /// <summary>役割（既定 member）。</summary>
    [OpenApiEnum(typeof(GroupRole))]
    public string? Role { get; init; }

    /// <summary>期間の始まり（既定は今日）。</summary>
    public DateOnly? From { get; init; }

    /// <summary>期間の終わり（この日を含まない。省略は終わりなし）。</summary>
    public DateOnly? Until { get; init; }
}

/// <summary>役割の変更の要求。</summary>
public sealed record ChangeGroupRoleRequest
{
    [OpenApiEnum(typeof(GroupRole))]
    public string? Role { get; init; }
}
