using JinJi.Application.Access;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>
/// 管理操作（<c>/api/v1/admin</c>）。不正検知で自動スロットル・一時停止になったクライアントの一覧と解除。
/// </summary>
/// <remarks>
/// 使えるのは人事管理者の人だけ（AI エージェント・社内システムは不可。一時停止はエージェント自身の暴走を止める手段なので、エージェントに解除させない）。
/// 解除した人事管理者のクライアントが一時停止中でも使える（入口の共通処理が管理操作だけは通す）。
/// </remarks>
internal static class AdminEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/admin/clients", ListAsync)
            .WithName("ListRestrictedClients")
            .WithSummary("自動スロットル・一時停止中のクライアントの一覧（人事管理者）")
            .WithMetadata(new RequestKindMetadata(RequestKind.Admin, false))
            .Produces<IReadOnlyList<ClientStatusResponse>>();

        api.MapPost("/admin/clients/{clientId}/release", ReleaseAsync)
            .WithName("ReleaseClient")
            .WithSummary("クライアントの一時停止・自動スロットルを解除する（人事管理者）")
            .WithDescription("通常の状態のクライアントなら 404。解除は監査ログに残る。")
            .WithMetadata(new RequestKindMetadata(RequestKind.Admin, false))
            .Produces<ClientStatusResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static IResult ListAsync(HttpContext http, AbuseDetector detector)
    {
        if (!AdminAccess.IsHrAdminUser(http.Viewer()))
        {
            return Problems.Forbidden(http, "人事管理者だけが使えます。");
        }

        return Results.Ok(detector.List().Select(ClientStatusResponse.From).ToList());
    }

    private static async Task<IResult> ReleaseAsync(
        string clientId,
        HttpContext http,
        AbuseDetector detector,
        IAbuseNotifier notifier,
        CancellationToken cancellationToken)
    {
        var viewer = http.Viewer();
        if (!AdminAccess.IsHrAdminUser(viewer))
        {
            return Problems.Forbidden(http, "人事管理者だけが使えます。");
        }

        if (detector.Release(clientId) is not { } transition)
        {
            return Problems.NotFound(http);
        }

        await notifier.NotifyAsync(transition, viewer, cancellationToken);
        return Results.Ok(new ClientStatusResponse(clientId, AbuseStates.ToCode(AbuseState.Normal), null, transition.At, null));
    }
}
