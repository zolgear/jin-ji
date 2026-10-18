using System.Net;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.Server.Kestrel.Core;

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

/// <summary>公開面ごとの待ち受け（設定したアドレスとポート。ポート 0 は OS が空いているポートを割り当てる）。</summary>
internal sealed record SurfaceListener(Surface Surface, IPAddress Address, int Port);

/// <summary>待ち受けポートと公開面の対応。</summary>
/// <remarks>
/// 対応は Kestrel の待ち受け（<see cref="ListenOptions"/>）から引く。ポート 0 を指定したときは、待ち受けを始めた後に
/// OS が割り当てたポートで判定する（<see cref="ListenOptions.IPEndPoint"/> は待ち受けの開始で実際のポートに置き換わる）。
/// </remarks>
internal sealed class SurfacePortMap(IReadOnlyList<SurfaceListener> listeners)
{
    private readonly List<(Surface Surface, ListenOptions Listen)> _listens = [];

    public IReadOnlyList<SurfaceListener> Listeners { get; } = listeners;

    /// <summary>Kestrel の待ち受けを公開面に結び付ける（Kestrel の設定のときに呼ぶ）。</summary>
    public void Attach(Surface surface, ListenOptions listen)
    {
        lock (_listens)
        {
            _listens.Add((surface, listen));
        }
    }

    public Surface? Resolve(int localPort)
    {
        lock (_listens)
        {
            foreach (var (surface, listen) in _listens)
            {
                if (listen.IPEndPoint?.Port == localPort)
                {
                    return surface;
                }
            }
        }

        return null;
    }

    /// <summary>公開面が待ち受けているポート（ポート 0 を指定した待ち受けは開始後に割り当てたポート。待ち受けていない公開面・開始前のポート 0 は null）。</summary>
    public int? PortOf(Surface surface)
    {
        lock (_listens)
        {
            foreach (var (s, listen) in _listens)
            {
                if (s == surface && listen.IPEndPoint is { Port: not 0 } endpoint)
                {
                    return endpoint.Port;
                }
            }
        }

        return null;
    }
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
