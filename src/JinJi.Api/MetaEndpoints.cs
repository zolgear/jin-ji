using JinJi.Application.Access;
using JinJi.Application.Organization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace JinJi.Api;

/// <summary>メタ情報（<c>/api/v1/meta</c>）。業務日付・段の定義・時点の目盛り。人の情報は含まない。</summary>
internal static class MetaEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/meta", GetAsync)
            .WithName("GetMeta")
            .WithSummary("メタ情報（業務日付・段の定義・時点の目盛り）")
            .WithDescription(
                "業務日付（日本時間の今日。as_of の既定値）、法人ごとの段の定義、時点の目盛り（組織改編・一斉異動の日）を返す。"
                + "人事管理者以外には業務日付以前の目盛りだけを返す（組織改編・一斉異動の予定は内示と同じく秘匿する）。")
            .WithMetadata(new RequestKindMetadata(RequestKind.OrgRead, false))
            .Produces<MetaResponse>();
    }

    private static async Task<IResult> GetAsync(HttpContext http, DirectoryMetaService meta, CancellationToken cancellationToken) =>
        Results.Ok(MetaResponse.From(await meta.GetAsync(http.Viewer(), cancellationToken)));
}
