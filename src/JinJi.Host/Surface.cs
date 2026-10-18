using Microsoft.AspNetCore.Routing.Matching;

namespace JinJi.Host;

/// <summary>エンドポイントの公開面。API・MCP・メトリクスはそれぞれ別ポートで待ち受ける。</summary>
internal enum Surface
{
    Api,
    Mcp,

    /// <summary>メトリクス（Prometheus のテキスト形式）。既定でループバックだけで待ち受け、API・MCP のポートからは見えない。</summary>
    Metrics,
}

/// <summary>エンドポイントがどの公開面に属するかを表すメタデータ。</summary>
internal sealed record SurfaceMetadata(Surface Surface);

/// <summary>待ち受けポートと公開面の対応。</summary>
internal sealed class SurfacePortMap(IReadOnlyDictionary<int, Surface> ports)
{
    public IReadOnlyDictionary<int, Surface> Ports { get; } = ports;

    public Surface? Resolve(int localPort) => Ports.TryGetValue(localPort, out var surface) ? surface : null;
}

/// <summary>
/// 接続を受けたローカルポートの公開面と一致するエンドポイントだけを候補に残すルーティング規則。
/// </summary>
/// <remarks>
/// Host ヘッダー（RequireHost）は利用者が偽装できるため使わず、実際に接続を受けたポートで判定する。
/// 公開面のメタデータを持たないエンドポイントはどのポートからも到達できない（既定で拒否）。
/// これにより、同じパス（例: /healthz）を API と MCP の両方に置いても衝突せず、
/// MCP のエンドポイントに API のポートから到達することもない。
/// </remarks>
internal sealed class SurfaceMatcherPolicy(SurfacePortMap portMap) : MatcherPolicy, IEndpointSelectorPolicy
{
    public override int Order => 0;

    public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints) => true;

    public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(candidates);

        var surface = portMap.Resolve(httpContext.Connection.LocalPort);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!candidates.IsValidCandidate(i))
            {
                continue;
            }

            var metadata = candidates[i].Endpoint.Metadata.GetMetadata<SurfaceMetadata>();
            if (surface is null || metadata is null || metadata.Surface != surface)
            {
                candidates.SetValidity(i, false);
            }
        }

        return Task.CompletedTask;
    }
}
